using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Copper68k;
using CopperDisk;
using CopperMod.Amiga.Lightweight;
using CopperNova.Reference;
using CopperSharp.Compiler;
using CopperSharp.Compiler.BootAdf;
using CopperSharp.Targets.Amiga;

if(args.Length!=3) throw new ArgumentException("Usage: CopperNova.Qualification <handler> <ROM> <output directory>");
string handler=Path.GetFullPath(args[0]),rom=Path.GetFullPath(args[1]),root=Path.GetFullPath(args[2]);
Directory.CreateDirectory(Path.Combine(root,"boot","S"));
string repo=Directory.GetCurrentDirectory();
var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(handler)!,"source-manifest.json"))).RootElement;
string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
if(Hash(File.ReadAllBytes(handler))!=manifest.GetProperty("HunkSha256").GetString()) throw new InvalidDataException("Handler hash mismatch");
foreach(var source in manifest.GetProperty("Sources").EnumerateArray())
    if(Hash(File.ReadAllBytes(Path.Combine(repo,source.GetProperty("Path").GetString()!)))!=source.GetProperty("Sha256").GetString()) throw new InvalidDataException("Source mismatch");
var compiled=AmigaM68kCompiler.Compile(new M68kCompilationRequest {
    AssemblyPath=typeof(NovaGateProbe).Assembly.Location,EntryPoint="NovaGateProbe::Entry",Cpu=M68kCpuTarget.M68000,
    OutputFormat=M68kOutputFormat.Hunk,RuntimeProfile=M68kRuntimeProfile.Application,ExceptionMode=M68kExceptionMode.Full,IncludedExportNames=[] });
if(!compiled.FrameworkAnalysis.IsCompatible||compiled.FrameworkAnalysis.ManagedAllocationSites.Count!=0) throw new InvalidOperationException("Incompatible gate probe");
File.WriteAllBytes(Path.Combine(root,"boot","PROBE"),compiled.Image); File.Copy(handler,Path.Combine(root,"boot","HANDLER"),true);
File.WriteAllText(Path.Combine(root,"boot","S","startup-sequence"),"PROBE\n");
byte[] boot=await FileSystemAdf.CreateAsync(Path.Combine(root,"boot"),"NovaPrototypeGate");
bool packed=Environment.GetEnvironmentVariable("NOVA_PACKED")=="1";
string media=Path.Combine(root,"disk.hdf"); File.WriteAllBytes(media,packed?NovaImage.FormatPacked():NovaImage.Format());
var hook=new Storage();
bool done=false,failed=false; uint control=0,pointer=0; int octets=0,shutdownBefore=-1,shutdownAfter=-1;
using(var machine=new LightweightA500Machine(new() {
    CpuModel=M68kCpuModel.M68000,ChipRamBytes=512*1024,FastRamBytes=0,SlowRamBytes=0,
    FloppyDriveCount=1,Hardfiles=[new(0,media,readOnly:false) { ExposeBootNode=false,IoHook=hook }] }))
{
    machine.LoadKickstart(File.ReadAllBytes(rom)); machine.MountAdf(0,boot); machine.SetDriveWriteProtected(0,true); machine.Reset();
    var sampler=new Sampler(machine);
    machine.ParallelDataWritten+=(value,_,_)=>
    {
        if(octets!=0) { pointer=(pointer<<8)|value; if(--octets==0) control=pointer; return; }
        if(value==200) { octets=4; pointer=0; }
        if(value==224) { sampler.Code=Read(machine,control+32); sampler.Startup=true; }
        if(value==225) sampler.Startup=false;
        if(value==220) sampler.Operations=true;
        if(value==232) shutdownBefore=hook.Flushes;
        if(value==233) shutdownAfter=hook.Flushes;
        if(value==210) done=true;
        if(value==250) failed=true;
    };
    var watch=Stopwatch.StartNew();
    for(int frame=0;frame<20000&&!done&&!failed&&watch.Elapsed<TimeSpan.FromMinutes(8);frame++) sampler.Frame();
    var receipt=new {
        Passed=done&&!failed&&sampler.GuardIntact&&sampler.Peak is >0 and <=4096&&sampler.OperationAllocations==0&&sampler.StartupAllocations>0&&shutdownAfter-shutdownBefore==(packed?2:4),
        PartialPacketCoverage=true,Cpu="MC68000",ChipRamBytes=524288,FastRamBytes=0,HandlerTaskStackBytes=4096,
        sampler.Peak,sampler.Lower,sampler.Upper,sampler.GuardIntact,sampler.StartupAllocations,sampler.OperationAllocations,
        sampler.LargestStartupAllocMemRequest,ShutdownBarriers=shutdownAfter-shutdownBefore,
        Stage=control==0?0:Read(machine,control+20),DosError=control==0?0:Read(machine,control+24),
        Frames=machine.CompletedFrames,HostSeconds=watch.Elapsed.TotalSeconds,HandlerSha256=Hash(File.ReadAllBytes(handler)),
        ProbeSha256=Hash(compiled.Image),SourceInputsMatched=true,ManagedAllocationSites=manifest.GetProperty("ManagedAllocationSites").GetInt32()
    };
    File.WriteAllText(Path.Combine(root,"execution.json"),JsonSerializer.Serialize(receipt,new JsonSerializerOptions { WriteIndented=true }));
    Console.WriteLine(JsonSerializer.Serialize(receipt));
    if(!receipt.Passed) throw new InvalidOperationException("Actual-HUNK gate failed");
}
byte[] finished=File.ReadAllBytes(media); var check=packed?NovaImage.CheckPacked(finished):NovaImage.Check(finished);
var file=check.Entries.Single(e=>e.Path=="/Bench/Renamed.bin");
byte[] expected=new byte[7027];
for(int i=0;i<6003;i++) expected[i]=i>=4093&&i<4862?(byte)((i-4093)*13+5):i>=1023&&i<2047?(byte)((i-1023)*23+9):(byte)(i*17+(i>>9)*31+3);
Array.Fill(expected,(byte)0x5B,6003,1024);
if(file.Size!=7027||file.Sha256!=Hash(expected)) throw new InvalidDataException("Shutdown did not persist the unflushed tail");
File.WriteAllText(Path.Combine(root,"independent-check.json"),JsonSerializer.Serialize(check,new JsonSerializerOptions { WriteIndented=true }));
#if PACKED_TREE_GATE
if(!packed||file.Comment?.Length!=79||file.Comment[^1]!='!'||check.Entries.Single(e=>e.Path=="/Bench/Fragment").Size!=4096) throw new InvalidDataException("Packed attribute/overflow verification failed");
#endif
var finalReceipt=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(root,"execution.json")))!;
finalReceipt["IndependentOwnershipAndShutdownPayloadVerified"]=true;
File.WriteAllText(Path.Combine(root,"qualification.json"),finalReceipt.ToJsonString(new JsonSerializerOptions { WriteIndented=true }));
Console.WriteLine("Independent ownership and post-shutdown payload checks passed.");

static uint Read(LightweightA500Machine machine,uint address)=>BinaryPrimitives.ReadUInt32BigEndian(machine.ChipRam.Span.Slice((int)address,4));

sealed class Storage : IAmigaHardfileIoHook
{
    public int Flushes;
    public bool FlushOnDispose=>false;
    public void ObserveOpen(int unit) { }
    public void ObserveCommand(ushort command) { }
    public void Read(FileStream backing,long offset,Span<byte> bytes) { backing.Position=offset; backing.ReadExactly(bytes); }
    public void Write(FileStream backing,long offset,ReadOnlySpan<byte> bytes) { backing.Position=offset; backing.Write(bytes); }
    public void Flush(FileStream backing) { Flushes++; backing.Flush(true); }
}

// Scalar scheduler adapted from this repository's CpxBootTests.StackSampler.
// It is a correctness diagnostic, never a benchmark timing source.
sealed class Sampler
{
    private readonly LightweightA500Machine machine;
    private readonly IM68kCore core;
    private readonly Action<long> advance;
    private readonly Func<long,long> clamp;
    private readonly Func<bool> dispatch;
    private readonly Func<long> boundary;
    private uint task;
    private readonly byte[] guard=new byte[32];
    public uint Code,Peak,Lower,Upper,StartupAllocations,OperationAllocations,LargestStartupAllocMemRequest;
    public bool Startup,Operations;
    public bool GuardIntact=>Lower!=0&&guard.All(b=>b==0xa5);
    public Sampler(LightweightA500Machine machine)
    {
        this.machine=machine; var flags=BindingFlags.Instance|BindingFlags.NonPublic; var type=machine.GetType();
        core=(IM68kCore)type.GetField("_cpu",flags)!.GetValue(machine)!;
        advance=type.GetMethod("AdvanceHardwareTo",flags)!.CreateDelegate<Action<long>>(machine);
        clamp=type.GetMethod("ClampCpuBatchTarget",flags)!.CreateDelegate<Func<long,long>>(machine);
        dispatch=type.GetMethod("DispatchPendingCpuInterrupt",flags)!.CreateDelegate<Func<bool>>(machine);
        boundary=type.GetProperty("NextFrameCycle",flags)!.GetMethod!.CreateDelegate<Func<long>>(machine);
    }
    private uint Get(uint at)=>BinaryPrimitives.ReadUInt32BigEndian(machine.ChipRam.Span.Slice((int)at,4));
    public void Frame()
    {
        long target=machine.CompletedFrames+1; var cpu=machine.Cpu;
        while(machine.CompletedFrames<target)
        {
            long end=boundary();
            if(cpu.Halted) { advance(end); cpu.Cycles=Math.Max(cpu.Cycles,end); continue; }
            if(cpu.Stopped) { long wake=((cpu.StatusRegister>>8)&7)>=6?end:clamp(end); advance(wake); cpu.Cycles=Math.Max(cpu.Cycles,wake); dispatch(); continue; }
            if(dispatch()) continue;
            while(cpu.Cycles<end)
            {
                if(dispatch()) continue; if(cpu.Halted||cpu.Stopped) break;
                if(Code!=0)
                {
                    uint exec=Get(4),current=Get(exec+276);
                    if(task==0&&cpu.ProgramCounter==Code)
                    {
                        task=current; Lower=Get(task+58); Upper=Get(task+62);
                        if(Upper-Lower!=4096) throw new InvalidOperationException("Incorrect handler stack");
                        System.Runtime.InteropServices.MemoryMarshal.AsMemory(machine.ChipRam).Span.Slice((int)Lower,32).Fill(0xa5);
                    }
                    if(task!=0&&current==task)
                    {
                        uint lvo=unchecked(exec-cpu.ProgramCounter);
                        if(Startup&&lvo==198) LargestStartupAllocMemRequest=Math.Max(LargestStartupAllocMemRequest,core.State.D[0]);
                        if(lvo is 198 or 204 or 222 or 654 or 666 or 684 or 696 or 708 or 870 or 894 or 936 or 942 or 972 or 984)
                        { if(Startup) StartupAllocations++; else if(Operations) OperationAllocations++; }
                        uint sp=core.State.A[7];
                        if(sp<=Upper&&sp>=Lower-4096) Peak=Math.Max(Peak,Upper-sp);
                        machine.ChipRam.Span.Slice((int)Lower,32).CopyTo(guard);
                    }
                }
                long before=cpu.Cycles; core.ExecuteInstruction(); if(cpu.Cycles<=before) throw new InvalidOperationException("CPU stopped advancing"); advance(cpu.Cycles);
            }
        }
    }
}
