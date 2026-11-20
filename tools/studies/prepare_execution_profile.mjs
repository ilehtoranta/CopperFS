// Derive a host-only scalar observer from our native qualification runner.
// It executes the same correctness guest and HUNK, without inserting guest code.
import fs from 'node:fs';
const out='artifacts/execution-core/profile'; fs.mkdirSync(out,{recursive:true});
let s=fs.readFileSync('tools/CopperNova.Qualification/Program.cs','utf8');
const replace=(from,to)=>{if(!s.includes(from))throw Error('Qualification source changed: '+from);s=s.replace(from,to);};
replace('string repo=Directory.GetCurrentDirectory();',`string repo=Directory.GetCurrentDirectory();
string sourceRoot=Environment.GetEnvironmentVariable("NOVA_PROFILE_SOURCE_ROOT")??throw new ArgumentException("Set frozen source root");
string phase=Environment.GetEnvironmentVariable("NOVA_PROFILE_PHASE")??"flush";
int start=phase=="startup"?224:phase=="mutation"?220:phase=="remount"?228:phase=="read"?222:226;
int end=phase=="startup"?225:phase=="mutation"?221:phase=="remount"?229:phase=="read"?223:227;
string SourcePath(string p)=>
    (p.EndsWith("NativeNovaHandler.cs")&&File.Exists(Path.Combine(sourceRoot,Path.GetFileName(p))))||p.Replace('\\\\','/').StartsWith("prototypes/CopperNova.Prototype/")?Path.Combine(sourceRoot,Path.GetFileName(p)):Path.Combine(repo,p);`);
replace('Path.Combine(repo,source.GetProperty("Path").GetString()!)','SourcePath(source.GetProperty("Path").GetString()!)');
replace('new Sampler(machine)','new Sampler(machine,handler)');
replace('if(value==220) sampler.Operations=true;',`if(value==220) sampler.Operations=true;
        if(value==start) sampler.Profile=true;
        if(value==end) sampler.Profile=false;`);
replace('Console.WriteLine(JsonSerializer.Serialize(receipt));',`Console.WriteLine(JsonSerializer.Serialize(receipt));
    File.WriteAllText(Path.Combine(root,"profile.json"),JsonSerializer.Serialize(new {
        Phase=phase,Unit="Scalar CPU instruction cycles attributed to the MC68000 handler task; not benchmark bus cycles",
        receipt.HandlerSha256,receipt.ProbeSha256,sampler.TaskCycles,sampler.TaskInstructions,Methods=sampler.Results()
    },new JsonSerializerOptions { WriteIndented=true }));`);
replace('public bool Startup,Operations;','public bool Startup,Operations,Profile;');
replace('public Sampler(LightweightA500Machine machine)','public Sampler(LightweightA500Machine machine,string handler)');
replace('this.machine=machine; var flags=',`this.machine=machine;
        methods=File.ReadLines(Path.ChangeExtension(handler,"map")).Select(line=>System.Text.RegularExpressions.Regex.Match(line,@"^([0-9A-F]{8}) +(\\d+) (.+)$"))
            .Where(m=>m.Success&&uint.Parse(m.Groups[2].Value)>0).Select(m=>(Convert.ToUInt32(m.Groups[1].Value,16),uint.Parse(m.Groups[2].Value),m.Groups[3].Value)).OrderBy(m=>m.Item1).ToArray();
        cycles=new long[methods.Length]; calls=new long[methods.Length];
        var flags=`);
replace('private uint task;',`private uint task;
    private readonly (uint At,uint Bytes,string Name)[] methods;
    private readonly long[] cycles,calls;
    public long TaskCycles,TaskInstructions;
    public object Results()=>methods.Select((m,i)=>new {m.Name,Cycles=cycles[i],Calls=calls[i]}).Where(m=>m.Cycles!=0||m.Calls!=0).OrderByDescending(m=>m.Cycles).ToArray();
    private int Find(uint pc)
    {
        if(pc<Code) return -1; uint at=pc-Code; int lo=0,hi=methods.Length-1;
        while(lo<=hi) { int mid=(lo+hi)>>1; if(at<methods[mid].At)hi=mid-1;else if(at-methods[mid].At>=methods[mid].Bytes)lo=mid+1;else return mid; }
        return -1;
    }`);
replace('if(Code!=0)\n', 'bool measured=false; int method=-1;\n                if(Code!=0)\n');
replace('if(task!=0&&current==task)\n                    {',`if(task!=0&&current==task)
                    {
                        measured=Profile;
                        if(measured) { method=Find(cpu.ProgramCounter); if(method>=0&&cpu.ProgramCounter==Code+methods[method].At) calls[method]++; }`);
replace('core.ExecuteInstruction(); if(cpu.Cycles<=before)',`core.ExecuteInstruction();
                if(measured) { long elapsed=cpu.Cycles-before; TaskCycles+=elapsed; TaskInstructions++; if(method>=0) cycles[method]+=elapsed; }
                if(cpu.Cycles<=before)`);
fs.writeFileSync(out+'/Program.cs',s);
fs.writeFileSync(out+'/Profile.csproj',`<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include="../../../tools/CopperNova.Qualification/CopperNova.Qualification.csproj" /></ItemGroup></Project>`);
