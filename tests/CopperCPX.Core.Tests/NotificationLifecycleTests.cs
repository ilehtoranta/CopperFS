using System.Text;
using CopperCPX.Qualification;

namespace CopperCPX.Tests;

public class NotificationLifecycleTests
{
    private static readonly (uint, uint) Ok = (uint.MaxValue, 0);
    internal static uint Request(DispatcherDriver d, string name, uint flags = 1, uint index = 0, byte signal = 5)
    {
        uint request = 90000 + index * 1024, text = request + 64;
        Array.Clear(d.Ram.Bytes, (int)request, 1024);
        byte[] bytes = Encoding.Latin1.GetBytes(name); bytes.CopyTo(d.Ram.Bytes, (int)text);
        d.Ram.Write32(request, text); d.Ram.Write32(request + 4, text); d.Ram.Write32(request + 12, flags);
        d.Ram.Write32(request + 16, 140000 + index * 64); d.Ram.Write8(request + 20, signal);
        return request;
    }
    internal static void Refresh(DispatcherDriver d)
    {
        for (int watch = 0; watch < 32; watch++)
        {
            d.Dispatcher = new(d.Ram, 32);
            if (!d.Dispatcher.TryBeginNotificationRefresh()) return;
            bool done = false;
            for (int n = 0; n < 100000; n++)
            {
                d.Dispatcher = new(d.Ram, 32); var step = d.Dispatcher.Step();
                if (step == StepResult.Complete) { done = true; break; }
                Assert.NotEqual(StepResult.Failed, step); if (step != StepResult.NeedIo) continue;
                var io = d.Dispatcher.Request;
                Assert.Equal(StepResult.NeedIo, d.Dispatcher.Step()); Assert.Equal(io, d.Dispatcher.Request);
                EngineDriver.Service(io, d.Ram, new Disk { Bytes = d.Image });
                Assert.Equal(CpxError.None, d.Dispatcher.CompleteIo(io.Token, io.Bytes, true));
            }
            Assert.True(done); Assert.False(d.Dispatcher.Busy);
        }
        throw new InvalidDataException("Notification refresh failed to exhaust its bounded watch set.");
    }
    private static CpxNotification Take(DispatcherDriver d, uint request, bool signal = false)
    {
        Refresh(d); Assert.True(d.Dispatcher.TryTakeNotification(out var delivery));
        Assert.Equal(d.Ram.Read32(request + 16), delivery.Target);
        if (signal) { Assert.Equal(0u, delivery.Message); Assert.Equal(1u << d.Ram.Read8(request + 20), delivery.SignalMask); }
        else
        {
            Assert.NotEqual(0u, delivery.Message); Assert.Equal(0u, delivery.SignalMask);
            Assert.Equal(0x40000000u, d.Ram.Read32(delivery.Message + 20));
            Assert.Equal(0x12, d.Ram.Read8(delivery.Message + 24)); Assert.Equal(0x34, d.Ram.Read8(delivery.Message + 25));
            Assert.Equal(request, d.Ram.Read32(delivery.Message + 26));
            Assert.Equal(0, d.Ram.Read8(delivery.Message + 18)); Assert.Equal(38, d.Ram.Read8(delivery.Message + 19));
            Assert.Equal(0x1234u, d.Ram.Read32(delivery.Message + 14));
        }
        return delivery;
    }
    private static void None(DispatcherDriver d)
    { Refresh(d); Assert.False(d.Dispatcher.TryTakeNotification(out _)); }
    private static void Change(DispatcherDriver d, string name = "file", uint protection = 1)
    { Assert.Equal(Ok, d.Packet(21, 0, 0, d.Bstr(name), protection)); Refresh(d); }
    private static void Remove(DispatcherDriver d, uint request) => Assert.Equal(Ok, d.Packet(4098, request));

    [Theory] [InlineData(17u)] [InlineData(18u)]
    public void InitialDeliveryRequiresAnExistingObjectAndUsesTheRequestedMethod(uint flags)
    {
        using var d = new DispatcherDriver(512); d.Execute(new(ActionKind.Create, "file"));
        uint existing = Request(d, "CopperCPX4:file", flags), missing = Request(d, "CopperCPX4:missing", flags, 1);
        Assert.Equal(Ok, d.Packet(4097, existing)); var first = Take(d, existing, flags == 18);
        Assert.Equal(Ok, d.Packet(4097, missing)); None(d);
        if (first.Message != 0) Assert.True(d.Dispatcher.NotificationReply(first.Message));
        d.Execute(new(ActionKind.Create, "missing")); var appeared = Take(d, missing, flags == 18);
        if (appeared.Message != 0) Assert.True(d.Dispatcher.NotificationReply(appeared.Message));
        Remove(d, existing); Remove(d, missing); None(d);
    }

    [Fact]
    public void WaitReplyCoalescesChangesUntilAcknowledgement()
    {
        using var d = new DispatcherDriver(512); d.Execute(new(ActionKind.Create, "file"));
        uint request = Request(d, "file", 25); Assert.Equal(Ok, d.Packet(4097, request));
        var first = Take(d, request); Assert.Equal(1u, d.Ram.Read32(request + 40));
        Change(d, protection: 1); Change(d, protection: 2); Change(d, protection: 0); None(d);
        Assert.True(d.Dispatcher.NotificationReply(first.Message)); var second = Take(d, request);
        Assert.Equal(1u, d.Ram.Read32(request + 40)); Assert.True(d.Dispatcher.NotificationReply(second.Message));
        Assert.Equal(0u, d.Ram.Read32(request + 40)); None(d); Remove(d, request);
    }

    [Fact]
    public void NonWaitingMessagesUseBoundedCapacityAndCoalesceOverflowWithoutLosingTheWatch()
    {
        using var d = new DispatcherDriver(512); d.Execute(new(ActionKind.Create, "file"));
        uint request = Request(d, "file"); Assert.Equal(Ok, d.Packet(4097, request)); None(d);
        var held = new List<uint>();
        for (uint n = 0; n < 8; n++) { Change(d, protection: n & 1); held.Add(Take(d, request).Message); }
        Assert.Equal(8, held.Distinct().Count()); Assert.Equal(8u, d.Ram.Read32(request + 40));
        Change(d, protection: 2); Change(d, protection: 0); None(d);
        Assert.True(d.Dispatcher.NotificationReply(held[0])); held.RemoveAt(0);
        held.Add(Take(d, request).Message); Assert.Equal(8u, d.Ram.Read32(request + 40));
        foreach (uint message in held) Assert.True(d.Dispatcher.NotificationReply(message));
        Assert.Equal(0u, d.Ram.Read32(request + 40)); None(d); Remove(d, request);
    }

    [Fact]
    public void LastWatchSlotAndExhaustionAreRecoverableAfterRemoval()
    {
        using var d = new DispatcherDriver(512); uint[] requests = new uint[9];
        for (uint n = 0; n < 9; n++) requests[n] = Request(d, "missing" + n, index: n);
        for (int n = 0; n < 8; n++) Assert.Equal(Ok, d.Packet(4097, requests[n]));
        Assert.Equal((0u, 103u), d.Packet(4097, requests[8]));
        Assert.Equal(Ok, d.Packet(25, 88000 >> 2)); None(d);
        Remove(d, requests[7]); Assert.Equal(Ok, d.Packet(4097, requests[8]));
        for (int n = 0; n < 7; n++) Remove(d, requests[n]); Remove(d, requests[8]); None(d);
    }

    [Fact]
    public void RemovalDetachesCallerMemoryButRetainsHeldMessageStorageUntilReply()
    {
        using var d = new DispatcherDriver(512); d.Execute(new(ActionKind.Create, "file"));
        uint[] requests = new uint[9];
        for (uint n = 0; n < 9; n++) requests[n] = Request(d, "file", n == 0 ? 25u : 1u, n);
        Assert.Equal(Ok, d.Packet(4097, requests[0])); var held = Take(d, requests[0]);
        for (int n = 1; n < 8; n++) Assert.Equal(Ok, d.Packet(4097, requests[n]));
        Remove(d, requests[0]); Assert.Equal(0u, d.Ram.Read32(requests[0] + 40));
        Array.Fill(d.Ram.Bytes, (byte)0xa5, (int)requests[0], 48); // caller may free/reuse it now
        Assert.Equal((0u, 103u), d.Packet(4097, requests[8]));
        Assert.True(d.Dispatcher.NotificationReply(held.Message));
        Assert.All(d.Ram.Bytes.AsSpan((int)requests[0], 48).ToArray(), b => Assert.Equal(0xa5, b));
        Assert.False(d.Dispatcher.NotificationReply(held.Message));
        Assert.Equal(Ok, d.Packet(4097, requests[8]));
        for (int n = 1; n < 9; n++) Remove(d, requests[n]); None(d);
    }

    [Fact]
    public void FileWritesNotifyAfterCloseAndUnrelatedFilesDoNotTriggerTheWatch()
    {
        using var d = new DispatcherDriver(512); d.Execute(new(ActionKind.Create, "file"));
        uint request = Request(d, "file"); Assert.Equal(Ok, d.Packet(4097, request)); None(d);
        uint h = RecordLockPacketTests.Open(d); d.Ram.Write8(100000, 42);
        Assert.Equal((1u, 0u), d.Packet(87, h, 100000, 1)); None(d);
        d.Execute(new(ActionKind.Create, "other")); None(d);
        RecordLockPacketTests.Close(d, h); var changed = Take(d, request);
        Assert.True(d.Dispatcher.NotificationReply(changed.Message)); None(d); Remove(d, request);
    }

    [Fact]
    public void AbsentWatchTracksCreationDeletionAndRecreationAtTheSameName()
    {
        using var d = new DispatcherDriver(512); uint request = Request(d, "newfile");
        Assert.Equal(Ok, d.Packet(4097, request)); None(d);
        foreach (ActionKind action in new[] { ActionKind.Create, ActionKind.Delete, ActionKind.Create })
        {
            Assert.Equal(Outcome.Success, d.Execute(new(action, "newfile")).Outcome);
            var changed = Take(d, request); Assert.True(d.Dispatcher.NotificationReply(changed.Message)); None(d);
        }
        Remove(d, request);
    }

    [Fact]
    public void AFullMessagePoolCannotStarveAnotherWatch()
    {
        using var d = new DispatcherDriver(512); d.Execute(new(ActionKind.Create, "file"));
        d.Execute(new(ActionKind.Create, "other"));
        uint slow = Request(d, "file"), fast = Request(d, "other", 2, 1);
        Assert.Equal(Ok, d.Packet(4097, slow)); Assert.Equal(Ok, d.Packet(4097, fast));
        var held = new List<uint>();
        for (uint n = 0; n < 8; n++) { Change(d, protection: n & 1); held.Add(Take(d, slow).Message); }
        Change(d, protection: 2); Change(d, "other", 2);
        bool delivered = false;
        for (int step = 0; step < 8; step++)
        {
            d.Dispatcher = new(d.Ram, 32);
            if (!d.Dispatcher.TryTakeNotification(out var delivery)) continue;
            Assert.Equal(0u, delivery.Message); Assert.Equal(d.Ram.Read32(fast + 16), delivery.Target);
            delivered = true; break;
        }
        Assert.True(delivered, "An application holding all its messages must not starve another watch.");
        Remove(d, slow); Remove(d, fast);
        foreach (uint message in held) Assert.True(d.Dispatcher.NotificationReply(message));
    }

    [Theory] [InlineData(0u, 5)] [InlineData(3u, 5)] [InlineData(4u, 5)]
    [InlineData(2u, 32)] [InlineData(0x80000001u, 5)]
    public void InvalidNotificationArgumentsCannotConsumeWatchCapacity(uint flags, byte signal)
    {
        using var d = new DispatcherDriver(512); uint invalid = Request(d, "missing", flags, signal: signal);
        Assert.Equal((0u, 115u), d.Packet(4097, invalid)); Assert.False(d.Dispatcher.HasAsyncResources);
        uint[] requests = new uint[8];
        for (uint n = 0; n < 8; n++)
        { requests[n] = Request(d, "missing", index: n); Assert.Equal(Ok, d.Packet(4097, requests[n])); }
        foreach (uint request in requests) Remove(d, request); Assert.False(d.Dispatcher.HasAsyncResources);
    }

    [Fact]
    public void MessageAcknowledgementUsesOwnedAddressesAndIgnoresTamperedPrivateLinks()
    {
        using var d = new DispatcherDriver(512); d.Execute(new(ActionKind.Create, "file"));
        uint request = Request(d, "file", 17); Assert.Equal(Ok, d.Packet(4097, request)); var delivery = Take(d, request);
        d.Ram.Write32(delivery.Message + 30, uint.MaxValue); d.Ram.Write32(delivery.Message + 34, uint.MaxValue);
        Assert.False(d.Dispatcher.NotificationReply(0)); Assert.False(d.Dispatcher.NotificationReply(delivery.Message + 2));
        Assert.Equal(1u, d.Ram.Read32(request + 40)); Assert.True(d.Dispatcher.NotificationReply(delivery.Message));
        Assert.Equal(0u, d.Ram.Read32(request + 40)); Assert.False(d.Dispatcher.NotificationReply(delivery.Message));
        Remove(d, request); Assert.False(d.Dispatcher.HasAsyncResources);
    }

    [Theory][InlineData(21u)][InlineData(1036u)]
    public void PublicationWaitsForAllJournalBarriersAndAnUncertainWriteNeverPublishesAnEvent(uint action)
    {
        foreach (bool fail in new[] { false, true })
        {
            using var d = new DispatcherDriver(512); d.Execute(new(ActionKind.Create, "file"));
            uint request = Request(d, "file"); Assert.Equal(Ok, d.Packet(4097, request));
            uint packet = DispatcherDriver.PacketAddress;
            d.Ram.Write32(packet + 8, action); d.Ram.Write32(packet + 20, 0); d.Ram.Write32(packet + 24, 0);
            d.Ram.Write32(packet + 28, d.Bstr("file")); d.Ram.Write32(packet + 32, 1);
            Assert.Equal(CpxError.None, d.Dispatcher.BeginPacket(packet)); bool injected = false, finished = false; int barriers = 0;
            for (int n = 0; n < 100000; n++)
            {
                d.Dispatcher = new(d.Ram, 32); var step = d.Dispatcher.Step();
                if (step == StepResult.Complete) { finished = true; break; }
                Assert.False(d.Dispatcher.TryBeginNotificationRefresh()); Assert.False(d.Dispatcher.TryTakeNotification(out _));
                Assert.NotEqual(StepResult.Failed, step); if (step != StepResult.NeedIo) continue;
                var io = d.Dispatcher.Request;
                if (fail && io.Kind == IoKind.Write && !injected)
                { Assert.Equal(CpxError.Io, d.Dispatcher.CompleteIo(io.Token, 0, false)); injected = true; }
                else
                {
                    if (io.Kind == IoKind.Flush) barriers++;
                    EngineDriver.Service(io, d.Ram, new Disk { Bytes = d.Image });
                    Assert.Equal(CpxError.None, d.Dispatcher.CompleteIo(io.Token, io.Bytes, true));
                }
            }
            Assert.True(finished);
            if (fail) { Assert.True(injected); Assert.Equal(213u, d.Dispatcher.DosError); None(d); }
            else
            {
                Assert.Equal(6, barriers); Assert.Equal(0u, d.Dispatcher.DosError);
                var delivery = Take(d, request); Assert.True(d.Dispatcher.NotificationReply(delivery.Message));
            }
            Remove(d, request);
        }
    }

    [Fact]
    public void RenameNotifiesOldAndNewNamesAndBothParentDirectories()
    {
        using var d = new DispatcherDriver(512);
        foreach (string directory in new[] { "one", "two" })
        { uint l = d.Packet(22, 0, d.Bstr(directory)).Result; Assert.NotEqual(0u, l); Assert.Equal(Ok, d.Packet(15, l)); }
        d.Execute(new(ActionKind.Create, "one/file"));
        string[] names = ["one", "two", "one/file", "two/file"]; uint[] requests = new uint[4];
        for (uint n = 0; n < 4; n++)
        { requests[n] = Request(d, names[n], index: n); Assert.Equal(Ok, d.Packet(4097, requests[n])); }
        Assert.Equal(Outcome.Success, d.Execute(new(ActionKind.Rename, "one/file", "two/file")).Outcome);
        Refresh(d); var delivered = new HashSet<uint>();
        for (int n = 0; n < 4; n++)
        {
            Assert.True(d.Dispatcher.TryTakeNotification(out var delivery));
            delivered.Add(d.Ram.Read32(delivery.Message + 26)); Assert.True(d.Dispatcher.NotificationReply(delivery.Message));
        }
        Assert.Equal(requests.Order(), delivered.Order()); None(d);
        foreach (uint request in requests) Remove(d, request);
    }

    [Fact]
    public void WritingThroughAHardLinkNotifiesTheAliasByObjectIdentityAfterClose()
    {
        using var d = new DispatcherDriver(512); d.Execute(new(ActionKind.Create, "file"));
        Assert.Equal(Outcome.Success, d.Execute(new(ActionKind.HardLink, "file", "alias")).Outcome);
        uint request = Request(d, "alias"); Assert.Equal(Ok, d.Packet(4097, request));
        uint h = RecordLockPacketTests.Open(d); d.Ram.Write8(100000, 42);
        Assert.Equal((1u, 0u), d.Packet(87, h, 100000, 1)); None(d);
        RecordLockPacketTests.Close(d, h); var changed = Take(d, request);
        Assert.True(d.Dispatcher.NotificationReply(changed.Message)); Remove(d, request);
    }

    [Fact]
    public void DuplicateRegistrationAndOverlongPathLeaveOtherWatchesAndCallerDataUntouched()
    {
        using var d = new DispatcherDriver(512);
        uint request = Request(d, "missing/" + new string('x', 247));
        Assert.Equal(Ok, d.Packet(4097, request));
        Assert.Equal((0u, 202u), d.Packet(4097, request));
        uint tooLong = Request(d, "missing/" + new string('x', 248), index: 1);
        d.Ram.Write32(tooLong + 40, 0x12345678);
        Assert.Equal((0u, 120u), d.Packet(4097, tooLong)); Assert.Equal(0x12345678u, d.Ram.Read32(tooLong + 40));
        for (uint n = 1; n < 8; n++) Assert.Equal(Ok, d.Packet(4097, Request(d, "missing", index: n)));
        for (uint n = 0; n < 8; n++) Remove(d, 90000 + n * 1024);
        Assert.False(d.Dispatcher.HasAsyncResources);
    }
}
