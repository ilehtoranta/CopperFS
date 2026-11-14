using CopperCPX.Qualification;

namespace CopperCPX.Tests;

/// <summary>Events are deliberately delivered separately from DOS packets, as by Exec/timer.device.</summary>
public class RecordWaitTests
{
    private static readonly (uint, uint) Ok = (uint.MaxValue, 0);
    private const uint FirstPacket = 110000;
    private static uint Open(DispatcherDriver d) => RecordLockPacketTests.Open(d);
    private static void Pump(DispatcherDriver d, int count = 32)
    { for (int n = 0; n < count; n++) { d.Dispatcher = new(d.Ram, 32); d.Dispatcher.StepRecordWaits(); } }
    internal static uint Park(DispatcherDriver d, uint owner, uint index = 0, uint ticks = 50, uint mode = 0)
    {
        uint p = FirstPacket + index * 64;
        d.Ram.Write32(p, 130000 + index * 32); d.Ram.Write32(p + 4, 140000 + index * 32);
        d.Ram.Write32(p + 8, 2008); d.Ram.Write32(p + 12, 0xaabbccdd); d.Ram.Write32(p + 16, 0x11223344);
        d.Ram.Write32(p + 20, owner); d.Ram.Write32(p + 24, 0); d.Ram.Write32(p + 28, 10);
        d.Ram.Write32(p + 32, mode); d.Ram.Write32(p + 36, ticks);
        Assert.Equal(CpxError.None, d.Dispatcher.BeginPacket(p));
        bool complete = false;
        for (int n = 0; n < 100; n++)
        {
            d.Dispatcher = new(d.Ram, 32); var step = d.Dispatcher.Step();
            Assert.NotEqual(StepResult.NeedIo, step); Assert.NotEqual(StepResult.Failed, step);
            if (step == StepResult.Complete) { complete = true; break; }
        }
        Assert.True(complete); Assert.True(d.Dispatcher.PacketDeferred); Assert.False(d.Dispatcher.Busy);
        return p;
    }
    internal static CpxRecordTimer StartTimer(DispatcherDriver d)
    {
        for (int n = 0; n < 32; n++)
        {
            Pump(d, 1);
            if (!d.Dispatcher.TryTakeRecordTimer(out var timer)) continue;
            Assert.False(timer.Cancel); return timer;
        }
        throw new InvalidDataException("Parked record did not request a timer.");
    }
    private static CpxRecordTimer CancelTimer(DispatcherDriver d)
    {
        for (int n = 0; n < 64; n++)
        {
            Pump(d, 1);
            if (!d.Dispatcher.TryTakeRecordTimer(out var timer)) continue;
            Assert.True(timer.Cancel); return timer;
        }
        throw new InvalidDataException("Resolved record did not request timer cancellation.");
    }
    private static CpxPacketReply Reply(DispatcherDriver d, uint packet, uint error)
    {
        Pump(d);
        Assert.True(d.Dispatcher.TryTakeRecordReply(out var reply));
        Assert.Equal(packet, reply.Packet); Assert.Equal(d.Ram.Read32(packet), reply.Message);
        Assert.Equal(d.Ram.Read32(packet + 4), reply.ReplyPort);
        Assert.Equal(error == 0 ? uint.MaxValue : 0u, reply.Result); Assert.Equal(error, reply.Error);
        Assert.False(d.Dispatcher.TryTakeRecordReply(out _)); return reply;
    }

    [Fact]
    public void WaitingPacketReleasesOperationOwnerAndGrantWaitsForTimerReap()
    {
        using var d = new DispatcherDriver(512); uint a = Open(d), b = Open(d);
        Assert.Equal(Ok, RecordLockPacketTests.Lock(d, a, 0, 10)); uint p = Park(d, b);
        var timer = StartTimer(d); Assert.Equal(50u, timer.Ticks);
        Assert.True(d.Dispatcher.HasRecordWaits); Assert.False(d.Dispatcher.TryTakeRecordReply(out _));
        foreach (uint action in new uint[] { 20, 30, 33 })
        {
            Assert.Equal((0u, 209u), d.Packet(action, b, timer.Token, timer.Slot));
            Pump(d);
            Assert.True(d.Dispatcher.MediaAvailable); Assert.True(d.Dispatcher.HasRecordWaits);
            Assert.False(d.Dispatcher.TryTakeRecordTimer(out _)); Assert.False(d.Dispatcher.TryTakeRecordReply(out _));
        }
        // Both metadata I/O and another filesystem mutation can run while the lock waits.
        Assert.Equal(Ok, d.Packet(25, 88000 >> 2));
        uint unrelated = RecordLockPacketTests.Open(d, "unrelated");
        d.Ram.Write8(90000, 42); Assert.Equal((1u, 0u), d.Packet(87, unrelated, 90000, 1));
        RecordLockPacketTests.Close(d, unrelated);
        RecordLockPacketTests.Unlock(d, a, 0, 10); var cancel = CancelTimer(d);
        Assert.Equal(timer.Slot, cancel.Slot); Assert.Equal(timer.Token, cancel.Token);
        Assert.False(d.Dispatcher.TryTakeRecordReply(out _));
        d.Dispatcher.CompleteRecordTimer(timer.Slot, timer.Token);
        Reply(d, p, 0); Assert.False(d.Dispatcher.HasRecordWaits);
        Assert.Equal((0u, 241u), RecordLockPacketTests.Lock(d, a, 0, 1));
        RecordLockPacketTests.Unlock(d, b, 0, 10);
        RecordLockPacketTests.Close(d, a); RecordLockPacketTests.Close(d, b);
    }

    [Fact]
    public void TimeoutRepliesOnceAndDoesNotConsumeGrantOrReusedWaitSlot()
    {
        using var d = new DispatcherDriver(512); uint a = Open(d), b = Open(d);
        Assert.Equal(Ok, RecordLockPacketTests.Lock(d, a, 0, 10));
        uint p = Park(d, b, ticks: 1); var first = StartTimer(d); Assert.Equal(1u, first.Ticks);
        d.Dispatcher.CompleteRecordTimer(first.Slot, first.Token); Reply(d, p, 242);
        uint next = Park(d, b, 1); var second = StartTimer(d);
        Assert.Equal(first.Slot, second.Slot); Assert.NotEqual(first.Token, second.Token);
        d.Dispatcher.CompleteRecordTimer(first.Slot, first.Token); Pump(d);
        Assert.False(d.Dispatcher.TryTakeRecordReply(out _)); Assert.True(d.Dispatcher.HasRecordWaits);
        d.Dispatcher.CompleteRecordTimer(second.Slot, second.Token); Reply(d, next, 242);
        d.Dispatcher.CompleteRecordTimer(second.Slot, second.Token); Pump(d);
        Assert.False(d.Dispatcher.TryTakeRecordReply(out _));
        RecordLockPacketTests.Close(d, a); RecordLockPacketTests.Close(d, b);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void CancellationQuiescesTimerBeforeReplyAndOwnerCloseUsesInvalidLock(bool close)
    {
        using var d = new DispatcherDriver(512); uint a = Open(d), b = Open(d);
        Assert.Equal(Ok, RecordLockPacketTests.Lock(d, a, 0, 10)); uint p = Park(d, b); var timer = StartTimer(d);
        if (close) RecordLockPacketTests.Close(d, b); else d.Dispatcher.CancelRecordWait(p);
        var cancel = CancelTimer(d); Assert.Equal(timer.Slot, cancel.Slot);
        Assert.False(d.Dispatcher.TryTakeRecordReply(out _));
        d.Dispatcher.CompleteRecordTimer(timer.Slot, timer.Token);
        Reply(d, p, close ? 211u : 304u);
        Assert.False(d.Dispatcher.HasRecordWaits);
        if (!close) RecordLockPacketTests.Close(d, b);
        RecordLockPacketTests.Close(d, a);
    }

    [Fact]
    public void EveryWaitSlotIsUsableAndExhaustionDoesNotStealAnExistingPacket()
    {
        using var d = new DispatcherDriver(512); uint a = Open(d), b = Open(d);
        Assert.Equal(Ok, RecordLockPacketTests.Lock(d, a, 0, 10));
        var timers = new CpxRecordTimer[16]; var packets = new uint[16];
        for (uint n = 0; n < 16; n++) { packets[n] = Park(d, b, n); timers[n] = StartTimer(d); }
        Assert.Equal(16, timers.Select(x => x.Slot).Distinct().Count());
        Assert.Equal((0u, 103u), d.Packet(2008, b, 0, 10, 0, 50)); Assert.False(d.Dispatcher.PacketDeferred);
        Assert.Equal(Ok, d.Packet(25, 88000 >> 2));
        // Deliberately expire out of order and free exactly one slot at a time.
        for (int n = 15; n >= 0; n--)
        { d.Dispatcher.CompleteRecordTimer(timers[n].Slot, timers[n].Token); Reply(d, packets[n], 242); }
        Assert.False(d.Dispatcher.HasRecordWaits);
        uint reused = Park(d, b); var again = StartTimer(d);
        d.Dispatcher.CompleteRecordTimer(again.Slot, again.Token); Reply(d, reused, 242);
        RecordLockPacketTests.Close(d, a); RecordLockPacketTests.Close(d, b);
    }

    [Fact]
    public void MassCancellationKeepsAllWaitBuffersUntilTheirOwnTimerCompletes()
    {
        using var d = new DispatcherDriver(512); uint a = Open(d), b = Open(d);
        Assert.Equal(Ok, RecordLockPacketTests.Lock(d, a, 0, 10));
        var timers = new CpxRecordTimer[16]; var packets = new uint[16];
        for (uint n = 0; n < 16; n++) { packets[n] = Park(d, b, n); timers[n] = StartTimer(d); }
        d.Dispatcher.CancelRecordWaits(226);
        var cancelSlots = new HashSet<uint>();
        for (int n = 0; n < 16; n++) cancelSlots.Add(CancelTimer(d).Slot);
        Assert.Equal(16, cancelSlots.Count); Assert.False(d.Dispatcher.TryTakeRecordReply(out _));
        for (int n = 0; n < 16; n++)
        { d.Dispatcher.CompleteRecordTimer(timers[n].Slot, timers[n].Token); Reply(d, packets[n], 226); }
        Assert.False(d.Dispatcher.HasRecordWaits);
        RecordLockPacketTests.Close(d, a); RecordLockPacketTests.Close(d, b);
    }

    [Fact]
    public void CancellationBeforeTimerSubmissionNeedsNoAbortAndLeavesNoOutstandingRequest()
    {
        using var d = new DispatcherDriver(512); uint a = Open(d), b = Open(d);
        Assert.Equal(Ok, RecordLockPacketTests.Lock(d, a, 0, 10)); uint p = Park(d, b);
        Assert.True(d.Dispatcher.CancelRecordWait(p));
        Assert.False(d.Dispatcher.TryTakeRecordTimer(out _)); Reply(d, p, 304);
        Assert.False(d.Dispatcher.CancelRecordWait(p)); Assert.False(d.Dispatcher.HasRecordWaits);
        RecordLockPacketTests.Close(d, a); RecordLockPacketTests.Close(d, b);
    }

    [Fact]
    public void ClosingOwnerAfterProvisionalGrantReleasesGrantWithoutReusingLiveTimer()
    {
        using var d = new DispatcherDriver(512); uint a = Open(d), b = Open(d);
        Assert.Equal(Ok, RecordLockPacketTests.Lock(d, a, 0, 10)); uint p = Park(d, b); var timer = StartTimer(d);
        RecordLockPacketTests.Unlock(d, a, 0, 10); var cancel = CancelTimer(d);
        Assert.Equal((0u, 241u), RecordLockPacketTests.Lock(d, a, 0, 10));
        RecordLockPacketTests.Close(d, b); uint reused = Open(d); Assert.Equal(b, reused);
        Assert.Equal(Ok, RecordLockPacketTests.Lock(d, a, 0, 10));
        Assert.False(d.Dispatcher.TryTakeRecordReply(out _));
        Assert.Equal(CpxError.None, d.Dispatcher.CompleteRecordTimer(cancel.Slot, cancel.Token)); Reply(d, p, 211);
        Assert.Equal(CpxError.StaleCompletion, d.Dispatcher.CompleteRecordTimer(timer.Slot, timer.Token));
        Assert.Equal((0u, 240u), d.Packet(2009, reused, 0, 10));
        RecordLockPacketTests.Close(d, a); RecordLockPacketTests.Close(d, reused);
    }

    [Fact]
    public void TimerCompletionDuringUnrelatedStorageIoCannotReplaceItsRequestOrOperationState()
    {
        using var d = new DispatcherDriver(512); uint a = Open(d), b = Open(d);
        Assert.Equal(Ok, RecordLockPacketTests.Lock(d, a, 0, 10)); uint p = Park(d, b); var timer = StartTimer(d);
        uint packet = DispatcherDriver.PacketAddress;
        d.Ram.Write32(packet + 8, 25); d.Ram.Write32(packet + 20, 88000 >> 2);
        Assert.Equal(CpxError.None, d.Dispatcher.BeginPacket(packet)); bool injected = false, finished = false;
        for (int n = 0; n < 10000; n++)
        {
            d.Dispatcher = new(d.Ram, 32); var step = d.Dispatcher.Step();
            if (step == StepResult.Complete) { finished = true; break; }
            Assert.NotEqual(StepResult.Failed, step); if (step != StepResult.NeedIo) continue;
            var io = d.Dispatcher.Request;
            if (!injected)
            {
                Assert.Equal(CpxError.None, d.Dispatcher.CompleteRecordTimer(timer.Slot, timer.Token));
                Reply(d, p, 242); Assert.True(d.Dispatcher.Busy); Assert.Equal(io, d.Dispatcher.Request);
                Assert.Equal(StepResult.NeedIo, d.Dispatcher.Step()); injected = true;
            }
            EngineDriver.Service(io, d.Ram, new Disk { Bytes = d.Image });
            Assert.Equal(CpxError.None, d.Dispatcher.CompleteIo(io.Token, io.Bytes, true));
        }
        Assert.True(injected); Assert.True(finished); Assert.Equal(0u, d.Dispatcher.DosError);
        RecordLockPacketTests.Close(d, a); RecordLockPacketTests.Close(d, b);
    }

    [Fact]
    public void GrantCapacityLostWhileWaitingReturnsExhaustionWithoutInventingOwnership()
    {
        using var d = new DispatcherDriver(512); uint a = Open(d), b = Open(d);
        Assert.Equal(Ok, RecordLockPacketTests.Lock(d, a, 0, 10));
        for (uint n = 1; n < 32; n++) Assert.Equal(Ok, RecordLockPacketTests.Lock(d, a, n * 20, 1));
        uint p = Park(d, b); var timer = StartTimer(d);
        RecordLockPacketTests.Unlock(d, a, 0, 10);
        Assert.Equal(Ok, RecordLockPacketTests.Lock(d, a, 1000, 1)); // consume the available slot before servicing waiters
        var cancel = CancelTimer(d); Assert.Equal(timer.Token, cancel.Token);
        d.Dispatcher.CompleteRecordTimer(cancel.Slot, cancel.Token); Reply(d, p, 103);
        Assert.Equal((0u, 240u), d.Packet(2009, b, 0, 10));
        RecordLockPacketTests.Close(d, a); RecordLockPacketTests.Close(d, b);
    }

    [Fact]
    public void FailedTimerReturnsAnExplicitErrorAndDoesNotLeaveAnIdleWorkSpin()
    {
        using var d = new DispatcherDriver(512); uint a = Open(d), b = Open(d);
        Assert.Equal(Ok, RecordLockPacketTests.Lock(d, a, 0, 10)); uint p = Park(d, b); var timer = StartTimer(d);
        Assert.Equal(CpxError.None, d.Dispatcher.CompleteRecordTimer(timer.Slot, timer.Token, 213)); Reply(d, p, 213);
        Pump(d, 64); Assert.False(d.Dispatcher.HasRecordWork); Assert.False(d.Dispatcher.HasAsyncResources);
        RecordLockPacketTests.Close(d, a); RecordLockPacketTests.Close(d, b);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void CompletionBeforeAbortSubmissionRetiresWorkExactlyOnce(bool grant)
    {
        using var d = new DispatcherDriver(512); uint a = Open(d), b = Open(d);
        Assert.Equal(Ok, RecordLockPacketTests.Lock(d, a, 0, 10)); uint p = Park(d, b); var timer = StartTimer(d);
        if (grant) { RecordLockPacketTests.Unlock(d, a, 0, 10); Pump(d); }
        else Assert.True(d.Dispatcher.CancelRecordWait(p));
        // Timer has completed before the adapter submits its queued AbortIO.
        Assert.Equal(CpxError.None, d.Dispatcher.CompleteRecordTimer(timer.Slot, timer.Token));
        Assert.False(d.Dispatcher.TryTakeRecordTimer(out _)); Reply(d, p, grant ? 0u : 304u);
        Pump(d, 64); Assert.False(d.Dispatcher.HasRecordWork); Assert.False(d.Dispatcher.HasAsyncResources);
        if (grant) RecordLockPacketTests.Unlock(d, b, 0, 10);
        RecordLockPacketTests.Close(d, a); RecordLockPacketTests.Close(d, b);
    }

    [Fact]
    public void GrantBeforeTimerSubmissionRepliesWithoutCreatingAnOrphanTimer()
    {
        using var d = new DispatcherDriver(512); uint a = Open(d), b = Open(d);
        Assert.Equal(Ok, RecordLockPacketTests.Lock(d, a, 0, 10)); uint p = Park(d, b);
        RecordLockPacketTests.Unlock(d, a, 0, 10); Pump(d);
        Assert.False(d.Dispatcher.TryTakeRecordTimer(out _)); Reply(d, p, 0);
        Pump(d, 64); Assert.False(d.Dispatcher.HasRecordWork); Assert.False(d.Dispatcher.HasAsyncResources);
        Assert.Equal((0u, 241u), RecordLockPacketTests.Lock(d, a, 0, 1));
        RecordLockPacketTests.Close(d, a); RecordLockPacketTests.Close(d, b);
    }

    [Fact]
    public void LastReplyMayLeaveRescanWorkWhichTheAdapterMustDrainBeforeSleeping()
    {
        using var d = new DispatcherDriver(512); uint a = Open(d), b = Open(d);
        Assert.Equal(Ok, RecordLockPacketTests.Lock(d, a, 0, 10)); uint p = Park(d, b); var timer = StartTimer(d);
        RecordLockPacketTests.Unlock(d, a, 0, 10);
        var cancel = CancelTimer(d);
        Assert.Equal(CpxError.None, d.Dispatcher.CompleteRecordTimer(timer.Slot, timer.Token));
        // Deliberately consume the reply immediately, as the native service
        // wheel can. Do not use Reply(), which drains remaining scan work first.
        Assert.True(d.Dispatcher.TryTakeRecordReply(out var reply)); Assert.Equal(p, reply.Packet); Assert.Equal(0u, reply.Error);
        Assert.False(d.Dispatcher.HasAsyncResources); Assert.True(d.Dispatcher.HasRecordWork);
        int phases = 0;
        while (d.Dispatcher.HasRecordWork && phases < 16)
        { d.Dispatcher = new(d.Ram, 32); Assert.True(d.Dispatcher.StepRecordWaits()); phases++; }
        Assert.InRange(phases, 1, 16); Assert.False(d.Dispatcher.HasRecordWork);
        Assert.False(d.Dispatcher.TryTakeRecordTimer(out _)); Assert.False(d.Dispatcher.TryTakeRecordReply(out _));
        Assert.Equal(Ok, d.Packet(1027)); RecordLockPacketTests.Close(d, a); RecordLockPacketTests.Close(d, b);
    }
}
