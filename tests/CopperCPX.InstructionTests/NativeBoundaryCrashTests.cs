using Copper68k;
using CopperCPX.Reference.Tests;
using CopperFS.Cpx4.Reference;
using CopperSharp.Compiler;
using CopperSharp.Targets.Amiga;
using System.Text;

namespace CopperCPX.InstructionTests;

public static class BoundaryProbe
{
    private static CpxError Drive(ref CpxEngine<GuestMemory> engine)
    {
        var m=new GuestMemory();
        for(uint i=0;i<40000;i++)
        {
            var step=engine.Step();
            if(step==StepResult.Complete||step==StepResult.Failed) return engine.Error;
            if(step!=StepResult.NeedIo) continue;
            var io=engine.Request;
            m.Write32(0x8004,io.Sector.High); m.Write32(0x8008,io.Sector.Low);
            m.Write32(0x800c,io.Buffer); m.Write32(0x8010,io.Bytes);
            m.Write32(0x8000,(uint)io.Kind+1); engine.CompleteIo(io.Token,io.Bytes,true);
        }
        return CpxError.Busy;
    }
    public static uint Entry()
    {
        var m=new GuestMemory(); var e=new CpxEngine<GuestMemory>(m,0x70000);
        if(e.Initialize(512,512,Wide.From(8192),1)!=CpxError.None||
            e.BeginOperation(CpxOperation.Mount)!=CpxError.None||Drive(ref e)!=CpxError.None) return 1;
        uint count=m.Read32(0x8200);
        if(count==0) return 0;
        if(count==1)
        {
            m.Write8(0x8300,120);
            if(e.BeginCreate(Wide.From(1),0x8300,1)!=CpxError.None||Drive(ref e)!=CpxError.ReserveExhausted) return 2;
        }
        else
        {
            if(e.BeginOperation(CpxOperation.BeginTransaction)!=CpxError.None||Drive(ref e)!=CpxError.None) return 3;
            for(uint i=0;i<count;i++)
            {
                Wide at=Wide.From(m.Read32(0x8400+4*i));
                if(e.BeginOperation(CpxOperation.ReadMetadata,at)!=CpxError.None||Drive(ref e)!=CpxError.None) return 4;
                // Change the protection word in each object leaf so prefix publication is observable.
                if(m.Read8(e.ReadBuffer+5)==11) m.Write32(e.ReadBuffer+64+72,0x1234);
                if(e.BeginOperation(CpxOperation.StageMetadata,at,e.ReadBuffer)!=CpxError.None) return 5;
                var status=Drive(ref e);
                if(i==42)
                {
                    if(status!=CpxError.TransactionLimit||e.TransactionImageCount!=42||
                        e.BeginOperation(CpxOperation.CommitTransaction)!=CpxError.TransactionLimit) return 6;
                }
                else if(status!=CpxError.None) return 7;
            }
            if(count==42&&(e.BeginOperation(CpxOperation.CommitTransaction)!=CpxError.None||Drive(ref e)!=CpxError.None)) return 8;
        }
        return 0;
    }
}

public class NativeBoundaryCrashTests
{
    private static readonly Lazy<M68kCompilationResult> Code=new(()=>AmigaM68kCompiler.Compile(new(){
        AssemblyPath=typeof(BoundaryProbe).Assembly.Location,EntryPoint=typeof(BoundaryProbe).FullName+"::Entry",
        ManagedAssemblyPaths=[typeof(CpxArena).Assembly.Location,typeof(global::Amiga.APTR).Assembly.Location],IncludedExportNames=[],
        Cpu=M68kCpuTarget.M68000,OutputFormat=M68kOutputFormat.Assembly,RuntimeProfile=M68kRuntimeProfile.Freestanding,
        MemoryManagement=M68kMemoryManagement.None,ExceptionMode=M68kExceptionMode.Yolo}));
    private sealed class Cut : Exception;
    internal static byte[] Run(byte[] image,uint count,ulong[] targets,int cut)
    {
        var b=new EngineInstructions.Bus{Disk=(byte[])image.Clone()}; var code=Code.Value;
        code.Code.CopyTo(b.Memory,0x10000);
        foreach(var r in code.Relocations){uint p=0x10000+(uint)r.Offset;b.Put(p,b.Get(p)+0x10000);}
        b.Put(0x8200,count);for(int i=0;i<targets.Length;i++) b.Put(0x8400+(uint)i*4,(uint)targets[i]);
        int barriers=0;
        byte[] durable=(byte[])image.Clone();
        b.AfterRequest=kind=>{
            // Preserve even unflushed staging writes for the rejected transaction.
            if(kind==2&&count==43)
            {
                int offset=checked((int)b.Get(0x8008)*512),length=checked((int)b.Get(0x8010));
                b.Disk.AsSpan(offset,length).CopyTo(durable.AsSpan(offset));
            }
            if(kind!=3)return;
            b.Disk.CopyTo(durable,0);
            if(++barriers==cut)throw new Cut();
        };
        b.Put(0xf0000,0xf1000);
        using var cpu=M68kCoreFactory.Default.Create(M68kCpuModel.M68000,b);
        cpu.Reset(0x10000+code.EntryPoint,0xf0000);
        try{
            for(int n=0;n<150000000&&cpu.State.ProgramCounter!=0xf1000;n++) cpu.ExecuteInstruction();
        }catch(Cut){return durable;}
        Assert.Equal(0,cut);Assert.Equal(0xf1000u,cpu.State.ProgramCounter);Assert.Equal(0u,cpu.State.D[0]);
        if(count is 1 or 43)Assert.Equal(1,barriers);
        return durable;
    }
    internal static (EngineInstructions.Bus,ulong[]) Fixture(int count)
    {
        var d=new EngineInstructions.Bus();
        if(count==1)d.Disk=ExhaustedVolume.Create();
        else{
            Cpx4Volume<EngineInstructions.Bus>.Format(d,512,new byte[36864],12345);
            var v=new Cpx4Volume<EngineInstructions.Bus>(d,new byte[36864]);
            for(int i=0;i<90;i++){var name=Encoding.ASCII.GetBytes($"file{i}");Assert.Equal(MutationResult.Success,v.Create(name,out ulong id));v.Write(id,0,name,out _);}
        }
        var before=new IndependentImage(d.Disk);var targets=before.Metadata.Take(43).ToArray();
        if(count!=1)Assert.Equal(43,targets.Length);
        return (d,targets);
    }
    internal static void VerifyLeaves(byte[] original,byte[] recovered,ulong[] targets,bool published)
    {
        int checkedLeaves=0;
        foreach(ulong target in targets.Take(42))
        {
            int at=checked((int)target*512);
            if(IndependentImage.S(original,at+4)!=11) continue;
            uint old=IndependentImage.I(original,at+136);
            Assert.NotEqual(0x1234u,old);
            Assert.Equal(published?0x1234u:old,IndependentImage.I(recovered,at+136));
            checkedLeaves++;
        }
        Assert.True(checkedLeaves>0);
    }
    [Theory][InlineData(42)][InlineData(43)][InlineData(1)]
    public void FreshCompiledMountVerifiesBoundary(int count)
    {
        var (d,targets)=Fixture(count);
        var before=new IndependentImage(d.Disk);
        ulong gen=IndependentImage.L(d.Disk,66560+80);
        foreach(int cut in count==42?Enumerable.Range(1,6):new[]{0}){
            var crashed=Run(d.Disk,(uint)count,targets,cut);
            var recovered=Run(crashed,0,[],0);
            var after=new IndependentImage(recovered);
            if(count!=1) VerifyLeaves(d.Disk,recovered,targets,cut>=3);
            Assert.Equal(before.Names.Count,after.Names.Count);
            foreach(var (key,id) in before.Names){Assert.Equal(id,after.Names[key]);Assert.Equal(before.Read(id),after.Read(id));}
            Assert.Equal(gen+(cut>=3?1UL:0UL),IndependentImage.L(recovered,66560+80));
        }
    }
}
