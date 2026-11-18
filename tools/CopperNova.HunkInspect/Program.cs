using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

if(args.Length!=3) throw new ArgumentException("Usage: CopperNova.HunkInspect <HUNK> <map> <output directory>");
byte[] hunk=File.ReadAllBytes(args[0]);
uint Word(int n)=>BinaryPrimitives.ReadUInt32BigEndian(hunk.AsSpan(n*4,4));
// Fail closed on unsupported HUNK arrangements instead of assuming a code offset.
if(Word(0)!=0x3f3||Word(1)!=0||Word(2)!=1||Word(3)!=0||Word(4)!=0||Word(6)!=0x3e9||Word(7)>int.MaxValue/4)
    throw new InvalidDataException("Expected one code HUNK without resident names");
int codeBytes=checked((int)Word(7)*4);
if(codeBytes>hunk.Length-32) throw new InvalidDataException("Truncated code HUNK");
var symbols=File.ReadLines(args[1]).Select(line=>Regex.Match(line,@"^([0-9A-F]{8}) +(\d+) (.+)$"))
    .Where(m=>m.Success).Select(m=>new { Offset=Convert.ToInt32(m.Groups[1].Value,16),Bytes=int.Parse(m.Groups[2].Value),Name=m.Groups[3].Value }).ToArray();
const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
var assemblerType=Assembly.Load("CopperSharp.Compiler").GetType("CopperSharp.Compiler.Backend.M68kAssembler",true)!;
var emit=assemblerType.GetMethod("EmitWord",Flags,null,new[]{typeof(ushort)},null)!;
var render=assemblerType.GetMethod("TryRenderInstruction",Flags)!;
var reports=new List<object>();
Directory.CreateDirectory(args[2]);
// Inspect actual CRC/common-helper ranges, not pre-optimization ASM exports.
// General dispatch methods also contain embedded jump-table data and are omitted.
foreach(var symbol in symbols.Where(s=>s.Name.Contains("NovaEngine")&&new[]{"Crc","CrcFour","SectorCrc","PositionCrc","PatchedCrc","EncodeResidentLeaf","WalkPath","Find","Load","Issue","Finish","BeginPacket","CompleteIo"}.Any(n=>s.Name.EndsWith("::"+n))))
{
    if(symbol.Offset<0||symbol.Bytes<=0||symbol.Offset+symbol.Bytes>codeBytes||(symbol.Bytes&1)!=0) throw new InvalidDataException("Invalid map range");
    byte[] bytes=hunk.AsSpan(32+symbol.Offset,symbol.Bytes).ToArray();
    var assembler=Activator.CreateInstance(assemblerType,true)!;
    for(int i=0;i<codeBytes;i+=2) emit.Invoke(assembler,new object[]{BinaryPrimitives.ReadUInt16BigEndian(hunk.AsSpan(32+i,2))});
    var listing=new List<string>(); var calls=new List<object>(); var branches=new List<int>(); int decodedBytes=symbol.Offset;
    while(decodedBytes<symbol.Offset+symbol.Bytes)
    {
        int offset=decodedBytes;
        ushort opcode=BinaryPrimitives.ReadUInt16BigEndian(hunk.AsSpan(32+offset,2));
        object?[] arguments=new object?[7]; arguments[0]=offset;
        var parameters=render.GetParameters();
        for(int i=1;i<=4;i++) arguments[i]=Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(parameters[i].ParameterType.GetGenericArguments()));
        int? target=null;
        bool branch=(opcode&0xf000)==0x6000,dbra=(opcode&0xfff8)==0x51c8;
        if(branch||dbra)
        {
            // Reconstruct symbolic branch context from the actual encoded
            // displacement. The compiler renderer requires these fixups.
            bool word=dbra||(opcode&255)==0;
            int displacement=word?BinaryPrimitives.ReadInt16BigEndian(hunk.AsSpan(34+offset,2)):unchecked((sbyte)opcode);
            target=offset+2+displacement;
            if(target<0||target>=codeBytes) throw new InvalidDataException("Branch target outside code");
            if((opcode&0xff00)!=0x6100) branches.Add(target.Value);
            var fixupType=parameters[3].ParameterType.GetGenericArguments()[1];
            object fixup=Activator.CreateInstance(fixupType,new object[]{offset,"raw_"+target.Value.ToString("X8"),false,false})!;
            ((IDictionary)arguments[3]!).Add(offset,fixup);
        }
        arguments[6]=0;
        if(!(bool)render.Invoke(assembler,arguments)!||arguments[6] is not int length||length<2) throw new InvalidDataException("Undecoded instruction in "+symbol.Name+" at "+offset);
        decodedBytes+=length;
        listing.Add($"{offset:X8} {Convert.ToHexString(bytes.AsSpan(offset-symbol.Offset,length)),-16} {arguments[5]}");
        if((opcode&0xff00)==0x6100||(opcode&0xffc0)==0x4e80)
        {
            int? absolute=target;
            calls.Add(new {Offset=offset,Target=absolute,Name=symbols.FirstOrDefault(s=>s.Offset==absolute)?.Name,Instruction=arguments[5]});
        }
    }
    if(decodedBytes!=symbol.Offset+bytes.Length) throw new InvalidDataException("Decoder did not cover method");
    string name=symbol.Name.Split("::")[^1];
    File.WriteAllLines(Path.Combine(args[2],name+".asm"),listing);
    reports.Add(new {symbol.Name,symbol.Offset,symbol.Bytes,CodeSha256=Convert.ToHexString(SHA256.HashData(bytes)),CallSites=calls,
        FirstBytes=Convert.ToHexString(bytes.AsSpan(0,Math.Min(12,bytes.Length))),DecodedInstructions=listing.Count,
        BranchTargets=branches,AllNonCallBranchesWithinMethod=branches.All(at=>at>=symbol.Offset&&at<symbol.Offset+symbol.Bytes)});
}
File.WriteAllText(Path.Combine(args[2],"inspection.json"),JsonSerializer.Serialize(new {
    HunkSha256=Convert.ToHexString(SHA256.HashData(hunk)),MapSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[1]))),
    CodeOffset=32,CodeBytes=codeBytes,Methods=reports },new JsonSerializerOptions { WriteIndented=true }));
Console.WriteLine($"Decoded actual HUNK bytes for {reports.Count} native methods.");
