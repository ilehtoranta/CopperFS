using CopperCPX.Qualification;
using CopperCPX.Reference.Tests;

namespace CopperCPX.Tests;

public class InhibitLifecycleTests
{
    private static readonly (uint,uint) Ok=(uint.MaxValue,0);
    private const uint Pending=120000;
    internal static void Begin(DispatcherDriver d,uint action,uint a=0,uint b=0,uint c=0,uint e=0)
    {
        d.Ram.Write32(Pending+8,action); d.Ram.Write32(Pending+20,a); d.Ram.Write32(Pending+24,b);
        d.Ram.Write32(Pending+28,c); d.Ram.Write32(Pending+32,0); d.Ram.Write32(Pending+36,e);
        Assert.Equal(CpxError.None,d.Dispatcher.BeginPacket(Pending));
    }
    internal static CpxIo NextIo(DispatcherDriver d)
    {
        for(int n=0;n<100000;n++)
        {
            d.Dispatcher=new(d.Ram,32); var result=d.Dispatcher.Step();
            if(result==StepResult.NeedIo) return d.Dispatcher.Request;
            Assert.Equal(StepResult.Pending,result);
        }
        throw new InvalidDataException("No bounded I/O request");
    }
    internal static (uint Result,uint Error) Finish(DispatcherDriver d)
    {
        for(int n=0;n<100000;n++)
        {
            d.Dispatcher=new(d.Ram,32); var result=d.Dispatcher.Step();
            if(result==StepResult.Complete) { Assert.False(d.Dispatcher.Busy); return(d.Dispatcher.Result,d.Dispatcher.DosError); }
            Assert.NotEqual(StepResult.Failed,result);
            if(result!=StepResult.NeedIo) continue;
            Service(d,d.Dispatcher.Request);
        }
        throw new InvalidDataException("Packet did not terminate");
    }
    internal static void Service(DispatcherDriver d,CpxIo io)
    {
        EngineDriver.Service(io,d.Ram,new Disk{Bytes=d.Image});
        Assert.Equal(CpxError.None,d.Dispatcher.CompleteIo(io.Token,io.Bytes,true));
    }

    [Theory][InlineData(512)][InlineData(4096)]
    public void InhibitPublishesOnlyAfterBarrierAndUninhibitReadsFreshMedia(int unit)
    {
        using var d=new DispatcherDriver(unit); d.Execute(new(ActionKind.Create,"old"));
        Begin(d,31,uint.MaxValue); var io=NextIo(d);
        for(int n=0;io.Kind!=IoKind.Flush;n++) { Assert.InRange(n,0,10000); Service(d,io); io=NextIo(d); }
        for(int n=0;n<12;n++)
        {
            Assert.False(d.Dispatcher.IsInhibited); Assert.True(d.Dispatcher.Busy);
            Assert.Equal(StepResult.NeedIo,d.Dispatcher.Step()); Assert.Equal(io,d.Dispatcher.Request);
            Assert.Equal(CpxError.Busy,d.Dispatcher.BeginPacket(DispatcherDriver.PacketAddress));
        }
        Service(d,io); Assert.Equal(Ok,Finish(d)); Assert.True(d.Dispatcher.IsInhibited); Assert.False(d.Dispatcher.MediaAvailable);
        Assert.Equal(Ok,d.Packet(31,1)); Assert.Equal(Ok,d.Packet(31,7));
        Assert.Equal((0u,226u),d.Packet(8,0,d.Bstr("old"),0xfffffffe));
        Assert.Equal(Ok,d.Packet(1027)); Assert.Equal((0u,0u),d.Packet(7));
        Assert.Equal(Ok,d.Packet(25,86000>>2)); Assert.Equal(uint.MaxValue,d.Ram.Read32(86024)); Assert.Equal(0u,d.Ram.Read32(86012));
        using(var replacement=new DispatcherDriver(unit)) { replacement.Execute(new(ActionKind.Create,"new")); replacement.Image.CopyTo(d.Image,0); }
        Begin(d,31); io=NextIo(d); Assert.Equal(IoKind.Read,io.Kind); Assert.True(d.Dispatcher.IsInhibited);
        Service(d,io); Assert.Equal(Ok,Finish(d)); Assert.False(d.Dispatcher.IsInhibited); Assert.True(d.Dispatcher.MediaAvailable);
        Assert.Equal((0u,205u),d.Packet(8,0,d.Bstr("old"),0xfffffffe));
        var locked=d.Packet(8,0,d.Bstr("new"),0xfffffffe); Assert.Equal(0u,locked.Error); d.Packet(15,locked.Result);
        Assert.Equal(Ok,d.Packet(31)); new IndependentImage(d.Image);
    }
    [Fact] public void FailedBarrierAndFailedRemountNeverReenableAccess()
    {
        using var d=new DispatcherDriver(512); Begin(d,31,1); var io=NextIo(d);
        while(io.Kind!=IoKind.Flush) { Service(d,io); io=NextIo(d); }
        d.Dispatcher.CompleteIo(io.Token,0,false); Assert.Equal((0u,213u),Finish(d));
        Assert.False(d.Dispatcher.IsInhibited); Assert.False(d.Dispatcher.MediaAvailable);
        Assert.Equal((0u,226u),d.Packet(22,0,d.Bstr("forbidden")));
        Assert.Equal(Ok,d.Packet(31,1)); Assert.True(d.Dispatcher.IsInhibited);
        Begin(d,31); io=NextIo(d); d.Dispatcher.CompleteIo(io.Token,0,false); Assert.Equal((0u,213u),Finish(d));
        Assert.True(d.Dispatcher.IsInhibited); Assert.False(d.Dispatcher.MediaAvailable);
        Assert.Equal(Ok,d.Packet(31)); Assert.True(d.Dispatcher.MediaAvailable);
    }
    [Fact] public void FullHandleAndLockPoolsRefuseInhibitWithoutConsumingResources()
    {
        using var d=new DispatcherDriver(512); var handles=new uint[32]; var locks=new uint[32];
        for(int n=0;n<32;n++) { handles[n]=RecordLockPacketTests.Open(d); locks[n]=d.Packet(19).Result; Assert.NotEqual(0u,locks[n]); }
        Assert.Equal((0u,202u),d.Packet(31,1)); Assert.True(d.Dispatcher.MediaAvailable);
        foreach(uint h in handles) RecordLockPacketTests.Close(d,h);
        Assert.Equal((0u,202u),d.Packet(31,1));
        foreach(uint l in locks) Assert.Equal(Ok,d.Packet(15,l));
        Assert.Equal(Ok,d.Packet(31,1)); Assert.Equal(Ok,d.Packet(31)); Assert.False(d.Dispatcher.HasOpenResources);
    }
    [Fact] public void AllEnumerationSlotsRetireAcrossInhibitAndRejectOldKeysAfterRemount()
    {
        using var d=new DispatcherDriver(512); for(int n=0;n<3;n++) d.Execute(new(ActionKind.Create,$"file{n:D2}"));
        uint[] keys=new uint[16];
        for(uint n=0;n<16;n++) { Assert.Equal(Ok,ExAllPacketTests.All(d,16,1,85000+n*20)); keys[n]=d.Ram.Read32(85004+n*20); }
        Assert.Equal(Ok,d.Packet(31,1)); Assert.Equal(Ok,d.Packet(31));
        for(uint n=0;n<16;n++)
        {
            Assert.Equal((0u,211u),ExAllPacketTests.All(d,16,1,85000+n*20));
            d.Ram.Write32(85004+n*20,0); Assert.Equal(Ok,ExAllPacketTests.All(d,16,1,85000+n*20)); Assert.NotEqual(keys[n],d.Ram.Read32(85004+n*20));
        }
        for(uint n=0;n<16;n++) Assert.Equal(Ok,ExAllPacketTests.End(d,85000+n*20));
    }
    [Fact] public void SixteenRetainedTimerRequestsBlockRemountUntilRepliesAreRetired()
    {
        using var d=new DispatcherDriver(512); uint a=RecordLockPacketTests.Open(d),b=RecordLockPacketTests.Open(d);
        Assert.Equal(Ok,RecordLockPacketTests.Lock(d,a,0,10)); var timers=new CpxRecordTimer[16];
        for(uint n=0;n<16;n++) { RecordWaitTests.Park(d,b,n); timers[n]=RecordWaitTests.StartTimer(d); }
        Assert.Equal((0u,202u),d.Packet(31,1)); RecordLockPacketTests.Close(d,a); RecordLockPacketTests.Close(d,b);
        Assert.Equal((0u,202u),d.Packet(31,1));
        foreach(var timer in timers) Assert.Equal(CpxError.None,d.Dispatcher.CompleteRecordTimer(timer.Slot,timer.Token));
        Assert.Equal((0u,202u),d.Packet(31,1));
        for(int n=0;n<16;n++) { Assert.True(d.Dispatcher.TryTakeRecordReply(out var reply)); Assert.Equal(211u,reply.Error); }
        Assert.Equal(Ok,d.Packet(31,1)); Assert.Equal(Ok,d.Packet(31));
        Assert.False(d.Dispatcher.HasAsyncResources);
    }
    [Fact] public void WatchesAndHeldMessagesKeepOwnershipWhileRefreshIsSuspended()
    {
        using var d=new DispatcherDriver(512); d.Execute(new(ActionKind.Create,"file")); var held=new uint[8]; var watches=new uint[8];
        for(uint n=0;n<8;n++)
        {
            watches[n]=NotificationLifecycleTests.Request(d,"file",25,n); Assert.Equal(Ok,d.Packet(4097,watches[n]));
            NotificationLifecycleTests.Refresh(d); Assert.True(d.Dispatcher.TryTakeNotification(out var notification)); held[n]=notification.Message;
        }
        Assert.Equal(Ok,d.Packet(31,1)); Assert.False(d.Dispatcher.TryBeginNotificationRefresh());
        foreach(uint watch in watches) Assert.Equal(Ok,d.Packet(4098,watch));
        Assert.True(d.Dispatcher.HasAsyncResources);
        foreach(uint message in held) Assert.True(d.Dispatcher.NotificationReply(message));
        Assert.False(d.Dispatcher.HasAsyncResources); Assert.Equal(Ok,d.Packet(31));
        for(uint n=0;n<8;n++) Assert.Equal(Ok,d.Packet(4097,NotificationLifecycleTests.Request(d,"file",1,n)));
        for(uint n=0;n<8;n++) Assert.Equal(Ok,d.Packet(4098,watches[n]));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void CancellationOrRemovalRetainsOutstandingReadAndBlocksReuseUntilCompletion(bool removal)
    {
        using var d=new DispatcherDriver(512); d.Execute(new(ActionKind.Create,"file")); d.Execute(new(ActionKind.Write,"file",Data:new byte[2000]));
        uint h=RecordLockPacketTests.Open(d); Begin(d,82,h,100000,2000); var io=NextIo(d);
        if(removal) d.Dispatcher.MediaRemoved(); else d.Dispatcher.RequestStorageCancellation();
        for(int n=0;n<12;n++)
        {
            Assert.True(d.Dispatcher.Busy); Assert.True(d.Dispatcher.StorageStopPending);
            Assert.Equal(StepResult.NeedIo,d.Dispatcher.Step()); Assert.Equal(io,d.Dispatcher.Request);
            Assert.Equal(CpxError.Busy,d.Dispatcher.BeginPacket(DispatcherDriver.PacketAddress));
        }
        d.Dispatcher.CompleteIo(io.Token,0,false);
        Assert.Equal((uint.MaxValue,removal?226u:304u),Finish(d)); Assert.False(d.Dispatcher.StorageStopPending);
        Assert.Equal(0u,d.Ram.Read32(h+8)); Assert.Equal(0u,d.Ram.Read32(h+12));
        Assert.Equal((0u,202u),d.Packet(31)); Assert.Equal(Ok,d.Packet(1007,h));
        Assert.Equal(Ok,d.Packet(31)); Assert.True(d.Dispatcher.MediaAvailable);
        Assert.NotEqual(CpxError.None,d.Dispatcher.CompleteIo(io.Token,io.Bytes,true)); new IndependentImage(d.Image);
    }
    [Fact] public void SoftwareProtectionSurvivesInhibitAndUsesOnlyPersistenceBarrier()
    {
        using var d=new DispatcherDriver(512); Assert.Equal(Ok,d.Packet(1023,1,123));
        Begin(d,31,1); var io=NextIo(d); Assert.Equal(IoKind.Flush,io.Kind);
        Service(d,io); Assert.Equal(Ok,Finish(d)); Assert.True(d.Dispatcher.IsWriteProtected);
        Assert.Equal(Ok,d.Packet(31)); Assert.True(d.Dispatcher.IsWriteProtected);
        Assert.Equal((0u,214u),d.Packet(22,0,d.Bstr("protected")));
        Assert.Equal((0u,210u),d.Packet(1023,0,124)); Assert.Equal(Ok,d.Packet(1023,0,123));
        Assert.False(d.Dispatcher.IsWriteProtected);
    }
}
