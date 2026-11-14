using CopperCPX;
using CopperFS.Cpx4.Reference;
using CopperCPX.Reference.Tests;
namespace CopperCPX.Tests;

public class FileMutationTests
{
    internal static CpxEngine<Ram> Mount(IBlockDevice disk,Ram ram,int unit=512)
    {
        var e=new CpxEngine<Ram>(ram,32); Assert.Equal(CpxError.None,e.Initialize(512,(uint)unit,new((uint)(disk.SectorCount>>32),(uint)disk.SectorCount),1));
        Assert.Equal(CpxError.None,e.BeginOperation(CpxOperation.Mount)); EngineTests.Drive(ref e,ram,disk); return e;
    }
    [Theory][InlineData(512)][InlineData(1024)][InlineData(2048)][InlineData(4096)]
    public void InlineMutationsRetireBeforeReportingProgressAndClearExposedBytes(int unit)
    {
        Disk disk=EngineTests.Fresh(unit); var reference=new Cpx4Volume<Disk>(disk,new byte[36864]); reference.Create("file"u8,out ulong id);
        var ram=new Ram(new byte[100000]); var e=Mount(disk,ram,unit); "hello"u8.CopyTo(ram.Bytes.AsSpan(90000));
        Assert.Equal(CpxError.None,e.BeginInlineWrite(Wide.From((uint)id),Wide.From(3),90000,5));
        ram.Bytes.AsSpan(90000,5).Fill(0xee); // The operation must already own its input.
        int barriers=0;
        while(true)
        {
            StepResult step=e.Step(); if(step==StepResult.Complete) break; Assert.NotEqual(StepResult.Failed,step);
            Assert.Equal(0u,e.CommittedBytes);
            Assert.Equal(CpxError.Busy,e.BeginOperation(CpxOperation.Flush));
            if(step!=StepResult.NeedIo) continue;
            CpxIo io=e.Request; Assert.Equal(StepResult.NeedIo,e.Step());
            if(io.Kind==IoKind.Read) disk.Read(io.Sector.Low,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes));
            else if(io.Kind==IoKind.Write) disk.Write(io.Sector.Low,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes));
            else { disk.Flush(); barriers++; }
            Assert.Equal(CpxError.None,e.CompleteIo(io.Token,io.Bytes,true));
        }
        Assert.Equal(6,barriers); Assert.Equal(5u,e.CommittedBytes);
        e.BeginInlineResize(Wide.From((uint)id),4); EngineTests.Drive(ref e,ram,disk);
        e.BeginInlineResize(Wide.From((uint)id),8); EngineTests.Drive(ref e,ram,disk);
        var reopened=new Cpx4Volume<Disk>(disk,new byte[36864]); byte[] bytes=new byte[8]; Assert.Equal(8,reopened.Read(id,0,bytes));
        Assert.Equal(new byte[]{0,0,0,(byte)'h',0,0,0,0},bytes); reopened.Check(); new IndependentImage(disk.Bytes);
        e.BeginProtection(Wide.From((uint)id),4); EngineTests.Drive(ref e,ram,disk);
        Assert.Equal(CpxError.None,e.BeginInlineResize(Wide.From((uint)id),0));
        DriveFailure(ref e,ram,disk,CpxError.Protected); Assert.True(e.IsMounted);
        e.BeginProtection(Wide.From((uint)id),0); EngineTests.Drive(ref e,ram,disk);
        e.BeginDate(Wide.From((uint)id),uint.MaxValue,1439,2999); EngineTests.Drive(ref e,ram,disk);
        e.BeginOperation(CpxOperation.ReadObject,Wide.From((uint)id)); EngineTests.Drive(ref e,ram,disk);
        Assert.Equal(uint.MaxValue,ram.Read32(e.ObjectRecord+56));
        Assert.Equal(0x059f0bb7u,ram.Read32(e.ObjectRecord+60));
    }
    internal static void DriveFailure(ref CpxEngine<Ram> e,Ram ram,IBlockDevice disk,CpxError error)
    {
        for(int n=0;n<2000;n++)
        {
            var step=e.Step(); if(step==StepResult.Failed) { Assert.Equal(error,e.Error); return; }
            Assert.NotEqual(StepResult.Complete,step); if(step!=StepResult.NeedIo) continue;
            var io=e.Request; Assert.Equal(IoKind.Read,io.Kind); disk.Read(io.Sector.Low,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes)); e.CompleteIo(io.Token,io.Bytes,true);
        }
        Assert.Fail("Operation did not fail.");
    }
    private static void Write(IBlockDevice disk,uint id)
    {
        var ram=new Ram(new byte[100000]); var e=Mount(disk,ram); "new content"u8.CopyTo(ram.Bytes.AsSpan(90000));
        Assert.Equal(CpxError.None,e.BeginInlineWrite(Wide.From(id),default,90000,11)); EngineTests.Drive(ref e,ram,disk);
    }
    [Fact] public void NativeInlineWriteRecoversAtEverySectorAndBarrier()
    {
        var disk=EngineTests.Fresh(512); var reference=new Cpx4Volume<Disk>(disk,new byte[36864]); reference.Create("file"u8,out ulong id);
        reference.Write(id,0,"old content"u8,out _); var trace=new CrashDevice(disk.Bytes); Write(trace,(uint)id);
        for(int cut=1;cut<=trace.Events.Count;cut++) for(int mode=0;mode<5;mode++)
        {
            var fault=new CrashDevice(disk.Bytes){Cut=cut,Persistence=mode}; Assert.Throws<PowerLoss>(()=>Write(fault,(uint)id));
            var final=new MemoryDevice(fault.Durable); var ram=new Ram(new byte[100000]); Mount(final,ram);
            new IndependentImage(final.Bytes); var reopened=new Cpx4Volume<MemoryDevice>(final,new byte[36864]); reopened.Check();
            byte[] content=new byte[11]; Assert.Equal(11,reopened.Read(id,0,content));
            Assert.True(content.AsSpan().SequenceEqual("old content"u8)||content.AsSpan().SequenceEqual("new content"u8));
        }
    }
    [Fact] public void NativeAllocationAndPromotionRecoverAtEverySectorAndBarrier()
    {
        var disk=EngineTests.Fresh(512); var reference=new Cpx4Volume<Disk>(disk,new byte[36864]); reference.Create("file"u8,out ulong id);
        reference.Write(id,0,"old"u8,out _);
        void Grow(IBlockDevice device)
        {
            var ram=new Ram(new byte[100000]); var e=Mount(device,ram); ram.Bytes.AsSpan(90000,700).Fill(0x5a);
            Assert.Equal(CpxError.None,e.BeginMappedWrite(Wide.From((uint)id),Wide.From(3),90000,700)); EngineTests.Drive(ref e,ram,device);
        }
        var trace=new CrashDevice(disk.Bytes); Grow(trace);
        for(int cut=1;cut<=trace.Events.Count;cut++) for(int mode=0;mode<5;mode++)
        {
            var fault=new CrashDevice(disk.Bytes){Cut=cut,Persistence=mode}; Assert.Throws<PowerLoss>(()=>Grow(fault));
            var final=new MemoryDevice(fault.Durable); Mount(final,new Ram(new byte[100000]));
            new IndependentImage(final.Bytes); var reopened=new Cpx4Volume<MemoryDevice>(final,new byte[36864]); reopened.Check();
            byte[] content=new byte[703]; int length=reopened.Read(id,0,content); Assert.Contains(length,new[]{3,512,703});
            Assert.Equal("old"u8.ToArray(),content[..3]); Assert.All(content[3..length],b=>Assert.Equal(0x5a,b));
        }
    }
}
