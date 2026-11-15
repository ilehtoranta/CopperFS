using CopperCPX;
using CopperCPX.Reference.Tests;
using CopperFS.Cpx4.Reference;

namespace CopperCPX.Tests;

public class BatchedWriteTests
{
    [Theory][InlineData(512)][InlineData(1024)][InlineData(2048)][InlineData(4096)]
    public void ContiguousWriteCommitsOneRunAndReadsItDirectly(int unit)
    {
        var disk=EngineTests.Fresh(unit); var volume=new Cpx4Volume<Disk>(disk,new byte[36864]);
        volume.Create("file"u8,out ulong id);
        var ram=new Ram(new byte[250000]); var engine=FileMutationTests.Mount(disk,ram,unit);
        int length=unit*8; byte[] payload=Enumerable.Range(0,length).Select(n=>(byte)(n*31+7)).ToArray(); payload.CopyTo(ram.Bytes,100000);
        Wide before=engine.Transaction;
        Assert.Equal(CpxError.None,engine.BeginMappedWrite(Wide.From((uint)id),default,100000,(uint)length));
        var writes=Drive(ref engine,ram,disk);
        Assert.Equal((uint)length,engine.TransferredBytes);
        Assert.Equal(before.Low+1,engine.Transaction.Low);
        Assert.Contains(writes,io=>io.Kind==IoKind.Write&&io.Buffer==100000&&io.Bytes==length);
        Assert.Equal(6,writes.Count(io=>io.Kind==IoKind.Flush));
        volume=new(disk,new byte[36864]); volume.Check(); new IndependentImage(disk.Bytes);
        byte[] actual=new byte[length]; Assert.Equal(length,volume.Read(id,0,actual)); Assert.Equal(payload,actual);
        Assert.Equal(CpxError.None,engine.BeginFileRead(Wide.From((uint)id),default,150000,(uint)length));
        var reads=Drive(ref engine,ram,disk);
        Assert.Contains(reads,io=>io.Kind==IoKind.Read&&io.Buffer==150000&&io.Bytes==length);
        Assert.Equal(payload,ram.Bytes.AsSpan(150000,length).ToArray());
        ram.Bytes.AsSpan(100000,length).Fill(0x6a);
        engine.BeginMappedWrite(Wide.From((uint)id),default,100000,(uint)length);
        var overwrite=Drive(ref engine,ram,disk);
        Assert.Single(overwrite,io=>io.Kind==IoKind.Write);
        Assert.Single(overwrite,io=>io.Kind==IoKind.Flush);
        volume=new(disk,new byte[36864]); Assert.Equal(length,volume.Read(id,0,actual)); Assert.All(actual,b=>Assert.Equal(0x6a,b)); volume.Check();
    }

    [Fact]
    public void RunStopsAtFutureSparseExtentAndPreservesPartialTails()
    {
        var disk=EngineTests.Fresh(512); var volume=new Cpx4Volume<Disk>(disk,new byte[36864]); volume.Create("file"u8,out ulong id);
        byte[] old=Enumerable.Repeat((byte)0xd3,2048).ToArray(); volume.Write(id,4096,old,out _);
        var ram=new Ram(new byte[120000]); var engine=FileMutationTests.Mount(disk,ram);
        byte[] payload=Enumerable.Range(0,5003).Select(n=>(byte)(n*7)).ToArray(); payload.CopyTo(ram.Bytes,90000);
        engine.BeginMappedWrite(Wide.From((uint)id),Wide.From(13),90000,(uint)payload.Length); Drive(ref engine,ram,disk);
        volume=new(disk,new byte[36864]); byte[] actual=new byte[6144]; volume.Read(id,0,actual);
        byte[] expected=new byte[6144]; old.CopyTo(expected,4096); payload.CopyTo(expected,13);
        Assert.Equal(expected,actual); volume.Check(); new IndependentImage(disk.Bytes);
    }

    [Theory][InlineData(512)][InlineData(4096)]
    public void EntireNewRunRecoversBeforeOrAfterPublicationAtEverySectorAndBarrier(int unit)
    {
        var disk=EngineTests.Fresh(unit); var volume=new Cpx4Volume<Disk>(disk,new byte[36864]); volume.Create("file"u8,out ulong id);
        byte[] payload=Enumerable.Range(0,unit*3).Select(n=>(byte)(n*19+3)).ToArray();
        void Write(IBlockDevice device)
        {
            var ram=new Ram(new byte[120000]); var engine=FileMutationTests.Mount(device,ram,unit); payload.CopyTo(ram.Bytes,90000);
            engine.BeginMappedWrite(Wide.From((uint)id),default,90000,(uint)payload.Length); EngineTests.Drive(ref engine,ram,device);
        }
        var trace=new CrashDevice(disk.Bytes); Write(trace);
        for(int cut=1;cut<=trace.Events.Count;cut++) for(int persistence=0;persistence<5;persistence++)
        {
            var fault=new CrashDevice(disk.Bytes){Cut=cut,Persistence=persistence}; Assert.Throws<PowerLoss>(()=>Write(fault));
            var recovered=new MemoryDevice(fault.Durable); FileMutationTests.Mount(recovered,new Ram(new byte[120000]),unit);
            var check=new Cpx4Volume<MemoryDevice>(recovered,new byte[36864]); check.Check(); new IndependentImage(recovered.Bytes);
            byte[] actual=new byte[payload.Length]; int length=check.Read(id,0,actual);
            Assert.True(length==0||length==payload.Length); if(length!=0) Assert.Equal(payload,actual);
        }
    }

    private static List<CpxIo> Drive(ref CpxEngine<Ram> engine,Ram ram,IBlockDevice disk)
    {
        var requests=new List<CpxIo>();
        for(int step=0;step<20000;step++)
        {
            var result=engine.Step(); if(result==StepResult.Complete) return requests;
            Assert.True(result!=StepResult.Failed,$"Engine failed: {engine.Error}"); if(result!=StepResult.NeedIo) continue;
            var io=engine.Request; requests.Add(io); ulong sector=((ulong)io.Sector.High<<32)|io.Sector.Low;
            if(io.Kind==IoKind.Read) disk.Read(sector,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes));
            else if(io.Kind==IoKind.Write) disk.Write(sector,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes));
            else disk.Flush();
            Assert.Equal(CpxError.None,engine.CompleteIo(io.Token,io.Bytes,true));
        }
        Assert.Fail("Continuation did not terminate."); return requests;
    }
}
