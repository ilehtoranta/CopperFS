using System.Buffers.Binary;
using Amiga;
using CopperStart.Dos;

namespace CopperStart.Exec.Tests;

public sealed class NativeRecordLockCoreTests
{
    private const uint State = 0x1000, A = 0x2000, B = 0x2100, C = 0x2200, D = 0x2300;

    private sealed class Memory
    {
        internal readonly byte[] Bytes = new byte[65536];
        internal readonly HashSet<uint> Records = [];
        internal readonly HashSet<uint> Timers = [];
        internal readonly List<(uint Packet, uint Port, uint Message, int Result, DOS.Error Error)> Replies = [];
        internal readonly List<(uint Timer, bool Completed)> Stops = [];
        internal uint Next = 0x4000, NextTimer = 0x9000, NextPacket = 0x3000;
        internal bool FailAllocation, FailTimer;
        internal int Starts;
    }

    private struct Platform(Memory m) : INativeRecordLockPlatform
    {
        public bool IsMapped(APTR a, uint n) => a.Raw <= m.Bytes.Length && n <= m.Bytes.Length - a.Raw;
        public byte ReadUInt8(APTR a, int o = 0) => m.Bytes[a.Raw + o];
        public ushort ReadUInt16(APTR a, int o = 0) => BinaryPrimitives.ReadUInt16BigEndian(m.Bytes.AsSpan((int)a.Raw + o));
        public uint ReadUInt32(APTR a, int o = 0) => BinaryPrimitives.ReadUInt32BigEndian(m.Bytes.AsSpan((int)a.Raw + o));
        public void WriteUInt8(APTR a, int o, byte v) => m.Bytes[a.Raw + o] = v;
        public void WriteUInt16(APTR a, int o, ushort v) => BinaryPrimitives.WriteUInt16BigEndian(m.Bytes.AsSpan((int)a.Raw + o), v);
        public void WriteUInt32(APTR a, int o, uint v) => BinaryPrimitives.WriteUInt32BigEndian(m.Bytes.AsSpan((int)a.Raw + o), v);
        public void Clear(APTR a, uint n) => m.Bytes.AsSpan((int)a.Raw, (int)n).Clear();
        public void Copy(APTR a, APTR b, uint n) => m.Bytes.AsSpan((int)a.Raw, (int)n).CopyTo(m.Bytes.AsSpan((int)b.Raw));
        public APTR AllocateRecord(uint n)
        {
            Assert.Equal(NativeRecordLockCore.RecordBytes, n);
            if (m.FailAllocation) return APTR.Null;
            var r = m.Next; m.Next += n; Assert.True(m.Records.Add(r)); return APTR.FromPointer(r);
        }
        public void FreeRecord(APTR a, uint n)
        { Assert.Equal(NativeRecordLockCore.RecordBytes, n); Assert.True(m.Records.Remove(a.Raw)); Clear(a, n); }
        public bool StartRecordTimer(APTR state, uint ticks, out APTR timer)
        {
            Assert.Equal(State, state.Raw); Assert.NotEqual(0u, ticks);
            timer = APTR.Null; if (m.FailTimer) return false;
            timer = APTR.FromPointer(m.NextTimer++); Assert.True(m.Timers.Add(timer.Raw)); m.Starts++; return true;
        }
        public void StopRecordTimer(APTR timer, bool completed)
        { Assert.True(m.Timers.Remove(timer.Raw)); m.Stops.Add((timer.Raw, completed)); }
        public void ReplyRecordPacket(APTR state, APTR r, int result, DOS.Error error) => m.Replies.Add((
            ReadUInt32(r, NativeRecordLockCore.PacketOffset), ReadUInt32(r, NativeRecordLockCore.ReplyPortOffset),
            ReadUInt32(r, NativeRecordLockCore.MessageOffset), result, error));
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Memory M = new();
        internal Platform P;
        internal APTR LastPacket;
        internal Fixture() => P = new Platform(M);
        internal (int Result, DOS.Error Error) Lock(uint owner, ulong offset = 0x100000000, ulong length = 16,
            uint mode = 1, uint timeout = 0, uint node = 7)
        {
            LastPacket = APTR.FromPointer(M.NextPacket); M.NextPacket += 64;
            P.WriteUInt32(LastPacket, 0, LastPacket.Raw + 0x800);
            P.WriteUInt32(LastPacket, 4, LastPacket.Raw + 0x900);
            var result = NativeRecordLockCore.Lock(ref P, APTR.FromPointer(State), APTR.FromPointer(owner), node,
                offset, length, mode, timeout, LastPacket, out var error);
            return (result, error);
        }
        internal (int Result, DOS.Error Error) Unlock(uint owner, ulong offset = 0x100000000, ulong length = 16)
        {
            var result = NativeRecordLockCore.Unlock(ref P, APTR.FromPointer(State), APTR.FromPointer(owner), offset, length, out var error);
            return (result, error);
        }
        internal bool Pending(APTR packet) => NativeRecordLockCore.IsPending(ref P, APTR.FromPointer(State), packet);
        internal void Close(uint owner) => NativeRecordLockCore.Close(ref P, APTR.FromPointer(State), APTR.FromPointer(owner));
        public void Dispose()
        {
            NativeRecordLockCore.Destroy(ref P, APTR.FromPointer(State));
            Assert.Empty(M.Records); Assert.Empty(M.Timers);
            Assert.Equal(0u, P.ReadUInt32(APTR.FromPointer(State), NativeRecordLockCore.LocksOffset));
            Assert.Equal(0u, P.ReadUInt32(APTR.FromPointer(State), NativeRecordLockCore.WaitersOffset));
        }
    }

    [Theory]
    [InlineData(0u, 1u, false)]
    [InlineData(0u, 3u, false)]
    [InlineData(2u, 1u, false)]
    [InlineData(2u, 3u, true)]
    public void OverlappingRangesAcrossHandlesFollowSharedAndExclusiveModes(uint first, uint second, bool success)
    {
        using var f = new Fixture();
        Assert.Equal((-1, DOS.Error.None), f.Lock(A, mode: first));
        Assert.Equal(success ? (-1, DOS.Error.None) : (0, DOS.Error.LockCollision), f.Lock(B, mode: second, timeout: uint.MaxValue));
        Assert.Equal(0, f.M.Starts);
    }

    [Theory]
    [InlineData(0x100000010UL, 4UL)]
    [InlineData(0xFFFFFFFFFFFFFFF0UL, 8UL)]
    [InlineData(0x8000000000000000UL, 16UL)]
    public void UnsignedWideOffsetsAndLengthsDoNotNarrow(ulong offset, ulong length)
    {
        using var f = new Fixture();
        Assert.Equal((-1, DOS.Error.None), f.Lock(A, offset, length));
        Assert.Equal((0, DOS.Error.LockCollision), f.Lock(B, offset, length));
        Assert.Equal((-1, DOS.Error.None), f.Unlock(A, offset, length));
        Assert.Equal((-1, DOS.Error.None), f.Lock(B, offset, length));
        Assert.Equal((-1, DOS.Error.None), f.Unlock(B, offset, length));
    }

    [Fact]
    public void AdjacentAndHighBitRangesAndOtherNodesAreIndependent()
    {
        using var f = new Fixture();
        Assert.Equal((-1, DOS.Error.None), f.Lock(A));
        Assert.Equal((-1, DOS.Error.None), f.Lock(B, offset: 0x100000010));
        Assert.Equal((-1, DOS.Error.None), f.Lock(B, offset: 0xFFFFFFFFFFFFFFF0, length: 8));
        Assert.Equal((0, DOS.Error.LockCollision), f.Lock(C, offset: 0xFFFFFFFFFFFFFFF4, length: 1));
        Assert.Equal((-1, DOS.Error.None), f.Lock(C, node: 8));
    }

    [Fact]
    public void SameHandleCanAcquireRepeatedExclusiveLocksAndMustBalanceEachOne()
    {
        using var f = new Fixture();
        Assert.Equal((-1, DOS.Error.None), f.Lock(A)); Assert.Equal((-1, DOS.Error.None), f.Lock(A));
        Assert.Equal((-1, DOS.Error.None), f.Unlock(A));
        Assert.Equal((0, DOS.Error.LockCollision), f.Lock(B));
        Assert.Equal((-1, DOS.Error.None), f.Unlock(A));
        Assert.Equal((0, DOS.Error.RecordNotLocked), f.Unlock(A));
        Assert.Equal((-1, DOS.Error.None), f.Lock(B));
    }

    [Fact]
    public void UnlockRequiresTheSameHandleAndExactRange()
    {
        using var f = new Fixture(); Assert.Equal((-1, DOS.Error.None), f.Lock(A));
        Assert.Equal((0, DOS.Error.RecordNotLocked), f.Unlock(B));
        Assert.Equal((0, DOS.Error.RecordNotLocked), f.Unlock(A, length: 15));
        Assert.Equal((0, DOS.Error.LockCollision), f.Lock(B));
    }

    [Fact]
    public void BlockingPacketKeepsItsMessageAndReplyPortUntilAnUnlockGrantsIt()
    {
        using var f = new Fixture(); f.Lock(A);
        Assert.Equal((0, DOS.Error.None), f.Lock(B, mode: 0, timeout: 25));
        var packet = f.LastPacket; Assert.True(f.Pending(packet)); Assert.Empty(f.M.Replies);
        Assert.Equal((-1, DOS.Error.None), f.Unlock(A));
        Assert.False(f.Pending(packet));
        Assert.Equal((packet.Raw, packet.Raw + 0x900, packet.Raw + 0x800, -1, DOS.Error.None), Assert.Single(f.M.Replies));
        Assert.False(Assert.Single(f.M.Stops).Completed);
        Assert.Equal((-1, DOS.Error.None), f.Unlock(B));
    }

    [Fact]
    public void TimeoutRepliesOnceAndDoesNotResetWhenAnUnrelatedHandleCloses()
    {
        using var f = new Fixture(); f.Lock(A); f.Lock(B, mode: 0, timeout: 25);
        var packet = f.LastPacket; var timer = Assert.Single(f.M.Timers);
        f.Close(C); Assert.Equal(1, f.M.Starts); Assert.True(f.Pending(packet));
        Assert.True(NativeRecordLockCore.TimerReply(ref f.P, APTR.FromPointer(State), APTR.FromPointer(timer)));
        Assert.False(NativeRecordLockCore.TimerReply(ref f.P, APTR.FromPointer(State), APTR.FromPointer(timer)));
        var reply = Assert.Single(f.M.Replies); Assert.Equal(0, reply.Result); Assert.Equal(DOS.Error.LockTimeout, reply.Error);
        Assert.True(Assert.Single(f.M.Stops).Completed); Assert.False(f.Pending(packet));
        Assert.Equal((0, DOS.Error.LockCollision), f.Lock(B));
    }

    [Fact]
    public void CloseReleasesLocksAndCancelsTheClosedHandlesPendingPackets()
    {
        using var f = new Fixture(); f.Lock(A); f.Lock(B, mode: 0, timeout: 25);
        f.Close(B); Assert.Equal(DOS.Error.InvalidLock, Assert.Single(f.M.Replies).Error);
        f.Lock(C, mode: 0, timeout: 25); var packet = f.LastPacket;
        f.Close(A); Assert.False(f.Pending(packet)); Assert.Equal(-1, f.M.Replies[^1].Result);
    }

    [Fact]
    public void SharedWaitersCanWakeTogetherWhileAnExclusiveWaiterRetainsItsOriginalTimer()
    {
        using var f = new Fixture(); f.Lock(A);
        f.Lock(B, mode: 2, timeout: 25); f.Lock(C, mode: 2, timeout: 25); f.Lock(D, mode: 0, timeout: 25);
        var packet = f.LastPacket; Assert.Equal((-1, DOS.Error.None), f.Unlock(A));
        Assert.Equal(2, f.M.Replies.Count); Assert.True(f.Pending(packet)); Assert.Equal(3, f.M.Starts);
        f.Close(B); Assert.True(f.Pending(packet)); f.Close(C); Assert.False(f.Pending(packet));
        Assert.Equal(3, f.M.Replies.Count); Assert.All(f.M.Replies, r => Assert.Equal(-1, r.Result));
    }

    [Fact]
    public void AllocationAndTimerFailureLeaveNoPendingRecordOrSuccessfulLock()
    {
        using var f = new Fixture(); f.M.FailAllocation = true;
        Assert.Equal((0, DOS.Error.NoFreeStore), f.Lock(A)); Assert.Empty(f.M.Records);
        f.M.FailAllocation = false; f.Lock(A); f.M.FailTimer = true;
        Assert.Equal((0, DOS.Error.NoFreeStore), f.Lock(B, mode: 0, timeout: 25));
        Assert.Single(f.M.Records); Assert.False(f.Pending(f.LastPacket));
        Assert.Equal((0, DOS.Error.LockCollision), f.Lock(B));
    }

    [Fact]
    public void ImmediateAndZeroTimeoutRequestsNeverAllocateWaiters()
    {
        using var f = new Fixture(); f.Lock(A);
        Assert.Equal((0, DOS.Error.LockCollision), f.Lock(B, timeout: uint.MaxValue));
        Assert.Equal((0, DOS.Error.LockTimeout), f.Lock(B, mode: 0));
        Assert.Equal(0, f.M.Starts); Assert.Single(f.M.Records);
    }

    [Fact]
    public void DestroyCancelsEveryTimerRepliesNoDiskAndFreesPrivateRecords()
    {
        using var f = new Fixture(); f.Lock(A); f.Lock(B, mode: 0, timeout: 25);
        NativeRecordLockCore.Destroy(ref f.P, APTR.FromPointer(State));
        Assert.Equal(DOS.Error.NoDisk, Assert.Single(f.M.Replies).Error);
        Assert.Empty(f.M.Records); Assert.Empty(f.M.Timers);
    }
}
