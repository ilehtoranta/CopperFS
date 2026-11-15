using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TrackedBatchCheckpointCutsPreserveOldOrNewAllocation(bool buffered)
    {
        var initial = Image(); var old = AllocatedState(initial);
        var probe = new Disk(initial.ToArray()) { Buffered = true, DiscardPattern = 2 };
        probe.FreedQueue = APTR.FromPointer((uint)probe.Ram.Length - 4096);
        var work = TrimWork(ref probe, buffered, 128);
        Assert.True(AmigaSmartFileSystemWriteCore.Truncate(ref probe, ref work, 3));
        Assert.True(AmigaFreedRangeQueue.Take(ref probe, probe.FreedQueue, 64, 0, out var first, out var count));
        var batch = APTR.FromPointer((uint)probe.Ram.Length - 2048);
        probe.WriteUInt32(batch, 0, first); probe.WriteUInt32(batch, 4, count);
        probe.IoEvents = 0;
        Assert.True(AmigaSmartFileSystemWriteCore.TrimFreedBatch(ref probe, ref work, APTR.Null, batch, 1, out _));
        var expected = AllocatedState(probe.Snapshot());
        for (var cut = 1; cut <= probe.IoEvents; cut++)
        {
            var interrupted = new Disk(initial.ToArray()) { Buffered = true, DiscardPattern = 2 };
            interrupted.FreedQueue = probe.FreedQueue;
            var pending = TrimWork(ref interrupted, buffered, 128);
            Assert.True(AmigaSmartFileSystemWriteCore.Truncate(ref interrupted, ref pending, 3));
            interrupted.WriteUInt32(batch, 0, first); interrupted.WriteUInt32(batch, 4, count);
            interrupted.IoEvents = 0; interrupted.CutAfterIo = cut;
            Assert.Throws<PowerCut>(() => AmigaSmartFileSystemWriteCore.TrimFreedBatch(ref interrupted,
                ref pending, APTR.Null, batch, 1, out _));
            foreach (var image in interrupted.CrashImages())
            {
                var reopened = new Disk(image); var recovery = BatchWork(ref reopened);
                Assert.True(AmigaSmartFileSystemWriteCore.TrimFreedRange(ref reopened, ref recovery,
                    APTR.Null, first, count, out _));
                var actual = AllocatedState(reopened.Snapshot());
                Assert.True(actual.SequenceEqual(expected) || buffered && actual.SequenceEqual(old));
                if (interrupted.DiscardSectors != 0) Assert.Equal(expected, actual);
            }
        }
    }

    [Fact]
    public void FreedQueueCoalescesBoundsMemoryAndNeverForcesIo()
    {
        var disk = new Disk(Image()); var queue = APTR.FromPointer((uint)disk.Ram.Length - 4096);
        for (uint i = 0; i < AmigaFreedRangeQueue.Capacity; i++)
            Assert.True(AmigaFreedRangeQueue.Add(ref disk, queue, 100 + i * 3, 1));
        Assert.False(AmigaFreedRangeQueue.Add(ref disk, queue, 1000, 1));
        Assert.Equal(AmigaFreedRangeQueue.Capacity, AmigaFreedRangeQueue.Count(ref disk, queue));
        Assert.False(AmigaFreedRangeQueue.Add(ref disk, queue, 99, 200));
        Assert.True(AmigaFreedRangeQueue.Add(ref disk, queue, 100 + (AmigaFreedRangeQueue.Capacity - 1) * 3 + 1, 2));
        Assert.Equal(AmigaFreedRangeQueue.Capacity, AmigaFreedRangeQueue.Count(ref disk, queue));
        AmigaFreedRangeQueue.Clear(ref disk, queue);
        Assert.True(AmigaFreedRangeQueue.Add(ref disk, queue, 99, 100));
        Assert.True(AmigaFreedRangeQueue.Add(ref disk, queue, 199, 100));
        Assert.Equal(1u, AmigaFreedRangeQueue.Count(ref disk, queue));
        Assert.Equal(99u, disk.ReadUInt32(queue, 4)); Assert.Equal(200u, disk.ReadUInt32(queue, 8));
        var seen = new HashSet<uint>();
        while (AmigaFreedRangeQueue.Take(ref disk, queue, 17, 3, out var first, out var count))
        {
            Assert.InRange(count, 1u, 17u);
            for (var key = first; key < first + count; key++) seen.Add(key);
        }
        Assert.Equal(Enumerable.Range(99, 200).Select(n => (uint)n), seen.Order());
        Assert.False(AmigaFreedRangeQueue.Add(ref disk, queue, uint.MaxValue - 1, 3));
        Assert.False(AmigaFreedRangeQueue.Add(ref disk, queue, 1, 0));
        Assert.Equal(0, disk.Writes); Assert.Equal(0, disk.Flushes);
    }

    [Fact]
    public void FreedQueueRandomMergesAndChunkingPreserveExactlyAcceptedHints()
    {
        var disk = new Disk(Image()); var queue = APTR.FromPointer((uint)disk.Ram.Length - 4096);
        var expected = new HashSet<uint>(); var random = new Random(8234);
        for (var i = 0; i < 1000; i++)
        {
            var first = (uint)random.Next(1, 1000); var count = (uint)random.Next(1, 18);
            if (AmigaFreedRangeQueue.Add(ref disk, queue, first, count))
                for (var key = first; key < first + count; key++) expected.Add(key);
            Assert.InRange(AmigaFreedRangeQueue.Count(ref disk, queue), 0u, AmigaFreedRangeQueue.Capacity);
        }
        var seen = new HashSet<uint>();
        while (AmigaFreedRangeQueue.Take(ref disk, queue, 31, 7, out var first, out var count))
            for (var key = first; key < first + count; key++) seen.Add(key);
        Assert.Equal(expected.Order(), seen.Order());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TrackedSfsFreesCommitBeforeDiscardAndSkipReallocatedBlocks(bool buffered)
    {
        var disk = new Disk(Image()) { Buffered = true };
        disk.FreedQueue = APTR.FromPointer((uint)disk.Ram.Length - 4096);
        var work = TrimWork(ref disk, buffered, 128);
        Assert.True(AmigaSmartFileSystemWriteCore.Truncate(ref disk, ref work, 3));
        Assert.True(AmigaFreedRangeQueue.Count(ref disk, disk.FreedQueue) > 0);
        var request = new AmigaSmartFileSystemWriteCore.Request
        { Node = 3, Buffer = disk.Name("Reused"), Length = 6 };
        Assert.True(AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref request, out _));
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work));
        var expected = AllocatedState(disk.Snapshot());
        for (var guard = 0; AmigaFreedRangeQueue.Count(ref disk, disk.FreedQueue) != 0; guard++)
        {
            Assert.True(guard < 128);
            Assert.True(AmigaFreedRangeQueue.Take(ref disk, disk.FreedQueue, 64, 0, out var first, out var count));
            var requests = disk.DiscardRequests.Count;
            Assert.True(AmigaSmartFileSystemWriteCore.TrimFreedRange(ref disk, ref work, APTR.Null,
                first, count, out _), work.Error.ToString());
            foreach (var (block, length) in disk.DiscardRequests.Skip(requests))
                Assert.True(block >= first && length <= count && block - first <= count - length);
            Assert.Equal(expected, AllocatedState(disk.Snapshot()));
        }
    }
}
