using CopperCPX.Qualification;
using CopperCPX.Reference.Tests;

namespace CopperCPX.Tests;

public class StorageStopRecoveryTests
{
    public static IEnumerable<object[]> Barriers()
    { foreach(bool removed in new[]{false,true}) for(int barrier=1;barrier<=6;barrier++) yield return [removed,barrier]; }
    [Theory][MemberData(nameof(Barriers))]
    public void StopAtEveryPublicationBarrierRetainsRecoverableOwnership(bool removed,int stopBarrier)
    {
        using var d=new DispatcherDriver(512); byte[] old=Enumerable.Repeat((byte)0x5a,64).ToArray(),updated=Enumerable.Repeat((byte)0xa5,64).ToArray();
        d.Execute(new(ActionKind.Create,"file")); d.Execute(new(ActionKind.Write,"file",Data:old));
        uint h=RecordLockPacketTests.Open(d); updated.CopyTo(d.Ram.Bytes,100000);
        InhibitLifecycleTests.Begin(d,87,h,100000,(uint)updated.Length); int barriers=0;
        for(int n=0;n<100000;n++)
        {
            var io=InhibitLifecycleTests.NextIo(d);
            if(io.Kind==IoKind.Flush&&++barriers==stopBarrier)
            {
                if(removed) d.Dispatcher.MediaRemoved(); else d.Dispatcher.RequestStorageCancellation();
                Assert.Equal(StepResult.NeedIo,d.Dispatcher.Step()); Assert.Equal(io,d.Dispatcher.Request);
                // The device completes the already submitted barrier successfully
                // after cancellation. No further original-operation I/O is issued.
                EngineDriver.Service(io,d.Ram,new Disk{Bytes=d.Image}); d.Dispatcher.CompleteIo(io.Token,io.Bytes,true);
                Assert.Equal(StepResult.Complete,d.Dispatcher.Step());
                Assert.Equal(removed?226u:304u,d.Dispatcher.DosError); break;
            }
            InhibitLifecycleTests.Service(d,io);
        }
        Assert.Equal(stopBarrier,barriers); Assert.False(d.Dispatcher.Busy);
        Assert.Equal(0u,d.Ram.Read32(h+12));
        Assert.Equal((uint.MaxValue,0u),d.Packet(1007,h)); Assert.Equal((uint.MaxValue,0u),d.Packet(31));
        var image=new IndependentImage(d.Image); ulong id=image.Names[(1,"file")]; byte[] actual=image.Read(id);
        Assert.Equal(stopBarrier<3?old:updated,actual);
        new CopperFS.Cpx4.Reference.Cpx4Volume<Disk>(new Disk{Bytes=d.Image},new byte[36864]).Check();
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void MultiChunkStopReportsOnlyRetiredPrefixAndPreservesItAfterRecovery(bool removed)
    {
        using var d=new DispatcherDriver(512); uint h=RecordLockPacketTests.Open(d);
        byte[] input=Enumerable.Range(0,1200).Select(n=>(byte)(n*13)).ToArray(); input.CopyTo(d.Ram.Bytes,100000);
        InhibitLifecycleTests.Begin(d,87,h,100000,(uint)input.Length); bool stopped=false;
        for(int n=0;n<100000;n++)
        {
            d.Dispatcher=new(d.Ram,32); var step=d.Dispatcher.Step();
            var engine=new CpxEngine<Ram>(d.Ram,32);
            if(engine.TransferredBytes==512)
            {
                if(removed) d.Dispatcher.MediaRemoved(); else d.Dispatcher.RequestStorageCancellation();
                if(step==StepResult.NeedIo)
                { var io=d.Dispatcher.Request; Assert.Equal(StepResult.NeedIo,d.Dispatcher.Step()); d.Dispatcher.CompleteIo(io.Token,0,false); }
                Assert.Equal(StepResult.Complete,d.Dispatcher.Step()); Assert.Equal(512u,d.Dispatcher.Result);
                Assert.Equal(removed?226u:304u,d.Dispatcher.DosError); stopped=true; break;
            }
            Assert.NotEqual(StepResult.Complete,step); Assert.NotEqual(StepResult.Failed,step);
            if(step==StepResult.NeedIo) InhibitLifecycleTests.Service(d,d.Dispatcher.Request);
        }
        Assert.True(stopped); Assert.Equal(0u,d.Ram.Read32(h+8)); Assert.Equal(512u,d.Ram.Read32(h+12));
        Assert.Equal((uint.MaxValue,0u),d.Packet(1007,h)); Assert.Equal((uint.MaxValue,0u),d.Packet(31));
        var image=new IndependentImage(d.Image); Assert.Equal(input[..512],image.Read(image.Names[(1,"file")]));
        new CopperFS.Cpx4.Reference.Cpx4Volume<Disk>(new Disk{Bytes=d.Image},new byte[36864]).Check();
    }
}
