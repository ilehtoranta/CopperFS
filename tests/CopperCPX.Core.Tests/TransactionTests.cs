using CopperCPX;
using CopperFS.Cpx4.Reference;
using CopperCPX.Reference.Tests;
namespace CopperCPX.Tests;

public class TransactionTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static CpxEngine<Ram> Mount(IBlockDevice d,Ram ram)
    {
        var e=new CpxEngine<Ram>(ram,32); Assert.Equal(CpxError.None,e.Initialize(512,512,Wide.From(8192),1));
        Assert.Equal(CpxError.None,e.BeginOperation(CpxOperation.Mount)); EngineTests.Drive(ref e,ram,d); return e;
    }
    private static void Restage(IBlockDevice disk,ulong[] targets)
    {
        var ram=new Ram(new byte[100000]); var e=Mount(disk,ram);
        Assert.Equal(CpxError.None,e.BeginOperation(CpxOperation.BeginTransaction)); EngineTests.Drive(ref e,ram,disk);
        foreach(ulong target in targets)
        {
            var at=new Wide((uint)(target>>32),(uint)target);
            Assert.Equal(CpxError.None,e.BeginOperation(CpxOperation.ReadMetadata,at)); EngineTests.Drive(ref e,ram,disk);
            Assert.Equal(CpxError.None,e.BeginOperation(CpxOperation.StageMetadata,at,e.ReadBuffer)); EngineTests.Drive(ref e,ram,disk);
        }
        Assert.Equal((uint)targets.Length,e.TransactionImageCount);
        Assert.Equal(CpxError.None,e.BeginOperation(CpxOperation.CommitTransaction)); EngineTests.Drive(ref e,ram,disk);
    }
    private static (byte[] Image,ulong[] Targets) Fixture()
    {
        var d=EngineTests.Fresh(512); var v=new Cpx4Volume<Disk>(d,new byte[36864]);
        for(int n=0;n<90;n++) Assert.Equal(MutationResult.Success,v.Create(System.Text.Encoding.ASCII.GetBytes($"name{n}"),out _));
        var oracle=new IndependentImage(d.Bytes); ulong[] targets=oracle.Metadata.Take(43).ToArray(); Assert.Equal(43,targets.Length); return(d.Bytes,targets);
    }
    [Fact] public void NativeEngine42ImageTransactionRecoversAfterEverySectorAndBarrier()
    {
        var (image,all)=Fixture(); var targets=all[..42]; var trace=new CrashDevice(image); Restage(trace,targets);
        Assert.Equal(6,trace.Events.Count(e=>e=="after flush"));
        for(int cut=1;cut<=trace.Events.Count;cut++) for(int mode=0;mode<5;mode++)
        {
            var fault=new CrashDevice(image) { Cut=cut, Persistence=mode }; Assert.Throws<PowerLoss>(()=>Restage(fault,targets));
            var final=new MemoryDevice(fault.Durable); var ram=new Ram(new byte[100000]); var e=Mount(final,ram);
            Assert.True(e.IsMounted); new IndependentImage(final.Bytes);
            var oracle=new Cpx4Volume<MemoryDevice>(final,new byte[36864]); oracle.Check(); Assert.Equal(90UL,oracle.FileCount);
        }
        output.WriteLine($"{trace.Events.Count} cuts x 5 persistence schedules = {trace.Events.Count*5} resumable-engine crash recoveries.");
    }
    [Fact] public void FortyThirdTargetRequiresAbortAndCannotPublishFortyTwoTargetPrefix()
    {
        var (image,targets)=Fixture(); var d=new MemoryDevice(image); var ram=new Ram(new byte[100000]); var e=Mount(d,ram); Wide generation=e.Transaction;
        e.BeginOperation(CpxOperation.BeginTransaction); EngineTests.Drive(ref e,ram,d);
        for(int n=0;n<43;n++)
        {
            Wide at=new(0,(uint)targets[n]); e.BeginOperation(CpxOperation.ReadMetadata,at); EngineTests.Drive(ref e,ram,d);
            e.BeginOperation(CpxOperation.StageMetadata,at,e.ReadBuffer);
            if(n<42) EngineTests.Drive(ref e,ram,d);
            else { Assert.Equal(StepResult.Pending,e.Step()); Assert.Equal(StepResult.Failed,e.Step()); }
        }
        Assert.Equal(CpxError.TransactionLimit,e.BeginOperation(CpxOperation.CommitTransaction)); Assert.True(Wide.Equal(generation,e.Transaction));
        Assert.Equal(CpxError.None,e.BeginOperation(CpxOperation.AbortTransaction)); Assert.Equal(StepResult.Complete,e.Step()); new IndependentImage(d.Bytes);
    }
    [Fact] public void CorruptLastCommittedImageIsRejectedBeforeAnyHomeWrite()
    {
        var (image,targets)=Fixture(); var trace=new CrashDevice(image); Restage(trace,targets[..42]);
        int barriers=0,cut=0;
        for(int n=0;n<trace.Events.Count;n++) if(trace.Events[n]=="after flush"&&++barriers==3) { cut=n+1; break; }
        Assert.True(cut>0); var fault=new CrashDevice(image){Cut=cut}; Assert.Throws<PowerLoss>(()=>Restage(fault,targets[..42]));
        ulong next=IndependentImage.L(fault.Durable,65536+1024+80)+1;
        int log=(next&1)==1?1048576:1572864; fault.Durable[log+(5+41)*1024+100]^=0x80;
        var final=new MemoryDevice(fault.Durable); var ram=new Ram(new byte[100000]); var e=new CpxEngine<Ram>(ram,32);
        e.Initialize(512,512,Wide.From(8192),1); e.BeginOperation(CpxOperation.Mount);
        StepResult result=StepResult.Pending;
        for(int n=0;n<2000;n++)
        {
            result=e.Step(); if(result is StepResult.Complete or StepResult.Failed) break;
            if(result!=StepResult.NeedIo) continue; var io=e.Request; Assert.Equal(IoKind.Read,io.Kind);
            final.Read(io.Sector.Low,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes)); e.CompleteIo(io.Token,io.Bytes,true);
        }
        Assert.Equal(StepResult.Failed,result); Assert.Equal(CpxError.Corrupt,e.Error); Assert.Equal(0,final.Writes);
    }
}
