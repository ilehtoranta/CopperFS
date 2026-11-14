using System.Buffers.Binary;
using CopperCPX.Qualification;

namespace CopperCPX.Tests;

public class AsyncSchedulingTests
{
    private const uint Pool = 32 + CpxArena.Scratch + 6 * 1024 + CpxArena.IndexBytes;
    private sealed class Meter(byte[] bytes)
    {
        internal readonly byte[] Bytes = bytes;
        internal uint Records;
        internal uint Read(uint p)
        {
            if (p >= Pool + 11776 && p < Pool + 14336 && (p - Pool - 11776) % 80 == 0) Records++;
            else if (p >= Pool + 14336 && p < Pool + 15360 && (p - Pool - 14336) % 64 == 0) Records++;
            else if (p >= Pool + 4608 && p < Pool + 11776)
            {
                uint offset = (p - Pool - 4608) % 896;
                if (offset == 0 || offset >= 328 && offset < 840 && (offset - 328) % 64 == 0) Records++;
            }
            return BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan((int)p));
        }
    }
    private struct Memory(Meter meter) : ICpxMemory
    {
        public byte Read8(uint p) => meter.Bytes[p];
        public uint Read32(uint p) => meter.Read(p);
        public void Write8(uint p, byte b) => meter.Bytes[p] = b;
        public void Write32(uint p, uint value) => BinaryPrimitives.WriteUInt32BigEndian(meter.Bytes.AsSpan((int)p), value);
    }

    [Fact]
    public void FullGrantWaitAndNotificationPoolsStayBoundedAndInsideArena()
    {
        using var d = new DispatcherDriver(512);
        uint a = RecordLockPacketTests.Open(d), b = RecordLockPacketTests.Open(d);
        for (uint n = 0; n < 32; n++) Assert.Equal((uint.MaxValue, 0u), RecordLockPacketTests.Lock(d, a, n * 20, 10));
        var timers = new CpxRecordTimer[16];
        for (uint n = 0; n < 16; n++) { RecordWaitTests.Park(d, b, n); timers[n] = RecordWaitTests.StartTimer(d); }
        uint[] watches = new uint[8];
        for (uint n = 0; n < 8; n++)
        { watches[n] = NotificationLifecycleTests.Request(d, "file", 17, n); Assert.Equal((uint.MaxValue, 0u), d.Packet(4097, watches[n])); }
        Array.Fill(d.Ram.Bytes, (byte)0x5a, 0, 32);
        uint end = 32 + CpxArena.Bytes(1024); Array.Fill(d.Ram.Bytes, (byte)0xa5, (int)end, 32);
        var meter = new Meter(d.Ram.Bytes); var memory = new Memory(meter);
        // Force waiter rescanning while keeping the first range conflicting.
        RecordLockPacketTests.Unlock(d, a, 620, 10);
        var messages = new List<uint>();
        for (int n = 0; n < 64; n++)
        {
            var dispatcher = new CpxDosDispatcher<Memory>(memory, 32); meter.Records = 0;
            dispatcher.StepRecordWaits();
            Assert.InRange(meter.Records, 0u, 64u);
            meter.Records = 0;
            bool sent = dispatcher.TryTakeNotification(out var delivery);
            Assert.InRange(meter.Records, 0u, 64u);
            if (sent) messages.Add(delivery.Message);
        }
        Assert.Equal(8, messages.Count);
        var final = new CpxDosDispatcher<Memory>(memory, 32);
        meter.Records = 0; final.CancelRecordWaits(304);
        Assert.InRange(meter.Records, 0u, 64u);
        for (int n = 0; n < 16; n++)
        {
            meter.Records = 0;
            bool cancel = final.TryTakeRecordTimer(out var timer);
            Assert.True(cancel); Assert.True(timer.Cancel); Assert.InRange(meter.Records, 0u, 64u);
            Assert.Equal(CpxError.None, final.CompleteRecordTimer(timer.Slot, timer.Token));
            Assert.True(final.TryTakeRecordReply(out _));
        }
        foreach (uint message in messages) Assert.True(final.NotificationReply(message));
        foreach (uint watch in watches) Assert.Equal((uint.MaxValue, 0u), d.Packet(4098, watch));
        RecordLockPacketTests.Close(d, a); RecordLockPacketTests.Close(d, b);
        Assert.False(d.Dispatcher.HasAsyncResources); Assert.False(d.Dispatcher.HasOpenResources);
        Assert.All(d.Ram.Bytes[..32], b => Assert.Equal(0x5a, b));
        Assert.All(d.Ram.Bytes.AsSpan((int)end, 32).ToArray(), b => Assert.Equal(0xa5, b));
    }
}
