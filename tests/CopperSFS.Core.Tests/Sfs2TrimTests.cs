using Amiga;
using CopperSFS.Bench;
using static CopperSFS.Tests.Sfs2WriteTests;

namespace CopperSFS.Tests;

public sealed class Sfs2TrimTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData(8 * 1024 * 1024)] [InlineData(128 * 1024 * 1024)]
    public void AutomaticSfs2ReadsOnlyIntersectingMapsOnSmallAndLargeVolumes(int capacity)
    {
        var disk = new StudyDisk(Empty(bytes: 1024, capacity: capacity), 1024); var work = WorkFor(disk);
        var batch = APTR.FromPointer((uint)disk.Ram.Length - 2048);
        AmigaAutomaticTrimBatch.Begin(ref disk, batch, 11, 7,
            new AmigaFileSystemDiscardLimits { MaximumBlocks = 4096, Granularity = 1 }, 8);
        disk.WriteUInt32(batch, AmigaAutomaticTrimBatch.Inputs, 2);
        disk.WriteUInt32(batch, AmigaAutomaticTrimBatch.Input, 1000);
        disk.WriteUInt32(batch, AmigaAutomaticTrimBatch.Input + 4, 16);
        disk.WriteUInt32(batch, AmigaAutomaticTrimBatch.Input + 8, disk.Geometry.BlockCount - 100);
        disk.WriteUInt32(batch, AmigaAutomaticTrimBatch.Input + 12, 16);
        Assert.True(AmigaSmartFileSystem2WriteCore.CollectAutomaticTrim(ref disk, ref work, batch, 11, 7), work.Error.ToString());
        Assert.Equal(32u, disk.ReadUInt32(batch, AmigaAutomaticTrimBatch.OutputBlocks));
        Assert.Equal(1, disk.Counts.Flushes);
        Assert.InRange(disk.Counts.BlockReads, 2, 8);
        Assert.Equal(0, disk.Counts.BlockWrites);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AutomaticSfs2BatchPreservesReusedDataAfterEitherCommitPolicy(bool redo)
    {
        var disk = new StudyDisk(Empty(), 512, crashModel: true);
        var deleted = CreateFile(ref disk, "Deleted"); PutFile(ref disk, deleted, 0, StudyFixture.Pattern(32768, 22));
        disk.FreedQueue = APTR.FromPointer((uint)disk.Ram.Length - 4096);
        var work = WorkFor(disk); var r = new AmigaSmartFileSystem2WriteCore.Request { Node = deleted };
        Assert.True(redo ? AmigaSmartFileSystem2WriteCore.ExecuteTrst(ref disk, ref work, ref r,
            AmigaSmartFileSystem2WriteCore.TrstOperation.Remove, disk.Payload, 192 * 1024, out _)
            : AmigaSmartFileSystem2WriteCore.Remove(ref disk, ref work, deleted));
        Assert.InRange(AmigaFreedRangeQueue.Count(ref disk, disk.FreedQueue), 1u, 8u);
        var kept = CreateFile(ref disk, "Reused"); PutFile(ref disk, kept, 0, StudyFixture.Pattern(4096, 23));
        var expected = Manifest(disk.Snapshot());
        var batch = APTR.FromPointer((uint)disk.Ram.Length - 2048);
        AmigaAutomaticTrimBatch.Begin(ref disk, batch, 11, 7,
            new AmigaFileSystemDiscardLimits { MaximumBlocks = 4096, Granularity = 1 }, 8);
        uint used = 0;
        while (AmigaFreedRangeQueue.Take(ref disk, disk.FreedQueue, 512, 0, out var first, out var count))
        {
            Assert.True(used < 8);
            disk.WriteUInt32(batch, AmigaAutomaticTrimBatch.Input + (int)used * 8, first);
            disk.WriteUInt32(batch, AmigaAutomaticTrimBatch.Input + (int)used * 8 + 4, count); used++;
        }
        disk.WriteUInt32(batch, AmigaAutomaticTrimBatch.Inputs, used); AmigaAutomaticTrimBatch.Sort(ref disk, batch);
        var writes = disk.Counts.BlockWrites;
        Assert.True(AmigaSmartFileSystem2WriteCore.CollectAutomaticTrim(ref disk, ref work, batch, 11, 7), work.Error.ToString());
        Assert.Equal(writes, disk.Counts.BlockWrites);
        Assert.True(disk.ReadUInt32(batch, AmigaAutomaticTrimBatch.OutputBlocks) > 0);
        disk.DiscardLimits = new() { MaximumBlocks = 4096, Granularity = 1 };
        for (var i = 0u; i < disk.ReadUInt32(batch, AmigaAutomaticTrimBatch.Outputs); i++)
            Assert.True(disk.DiscardBlocks(APTR.Null, disk.ReadUInt32(batch, AmigaAutomaticTrimBatch.Output + (int)i * 8),
                disk.ReadUInt32(batch, AmigaAutomaticTrimBatch.Output + (int)i * 8 + 4)));
        Assert.Equal(expected, Manifest(disk.Snapshot())); Inspect(disk.Snapshot());
    }

    [Theory]
    [InlineData(0x54524641u)] [InlineData(0x54525354u)]
    public void AutomaticSfs2RefusesDirtyMarkerWithoutRecoveryWrites(uint id)
    {
        var image = Empty(); var marker = (int)(U32(image, 104) + 2) * 512;
        U32(image, marker, id); Seal(image.AsSpan(marker, 512));
        var disk = new StudyDisk(image, 512, crashModel: true); var work = WorkFor(disk);
        var batch = APTR.FromPointer((uint)disk.Ram.Length - 2048);
        AmigaAutomaticTrimBatch.Begin(ref disk, batch, 11, 7,
            new AmigaFileSystemDiscardLimits { MaximumBlocks = 4096, Granularity = 1 }, 8);
        disk.WriteUInt32(batch, AmigaAutomaticTrimBatch.Inputs, 1);
        disk.WriteUInt32(batch, AmigaAutomaticTrimBatch.Input, 100);
        disk.WriteUInt32(batch, AmigaAutomaticTrimBatch.Input + 4, 32);
        Assert.False(AmigaSmartFileSystem2WriteCore.CollectAutomaticTrim(ref disk, ref work, batch, 11, 7));
        Assert.Equal(0, disk.Counts.BlockWrites); Assert.Empty(disk.DiscardRequests);
        Assert.Equal(0u, disk.ReadUInt32(batch, AmigaAutomaticTrimBatch.Outputs));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TrackedBatchUsesOneBarrierAndPartialFailureTouchesOnlyFreeBlocks(bool fail)
    {
        var disk = new StudyDisk(Empty(capacity: 8 * 1024 * 1024), 512) { FailDiscardAt = fail ? 1 : 0 };
        var batch = APTR.FromPointer((uint)disk.Ram.Length - 2048);
        disk.WriteUInt32(batch, 0, 100); disk.WriteUInt32(batch, 4, 16);
        disk.WriteUInt32(batch, 8, 300); disk.WriteUInt32(batch, 12, 16);
        var work = WorkFor(disk);
        Assert.Equal(!fail, AmigaSmartFileSystem2WriteCore.TrimFreedBatch(ref disk, ref work, APTR.Null, batch, 2, out var result));
        Assert.Equal(1, disk.Counts.Flushes);
        Assert.Equal(fail ? 0u : 32u, result.Blocks);
        foreach (var (first, count) in disk.DiscardRequests)
            Assert.True(first >= 100 && first + count <= 116 || first >= 300 && first + count <= 316);
        Inspect(disk.Snapshot());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TrackedSfs2RemovalAndReuseNeverDiscardAllocatedBlocks(bool redo)
    {
        var disk = new StudyDisk(Empty(), 512, crashModel: true);
        var deleted = CreateFile(ref disk, "Deleted"); PutFile(ref disk, deleted, 0, StudyFixture.Pattern(8192, 22));
        disk.FreedQueue = APTR.FromPointer((uint)disk.Ram.Length - 4096);
        var work = WorkFor(disk);
        if (redo)
        {
            var r = new AmigaSmartFileSystem2WriteCore.Request { Node = deleted };
            Assert.True(AmigaSmartFileSystem2WriteCore.ExecuteTrst(ref disk, ref work, ref r,
                AmigaSmartFileSystem2WriteCore.TrstOperation.Remove, disk.Payload, 192 * 1024, out _));
        }
        else Assert.True(AmigaSmartFileSystem2WriteCore.Remove(ref disk, ref work, deleted));
        Assert.True(AmigaFreedRangeQueue.Count(ref disk, disk.FreedQueue) > 0);
        var kept = CreateFile(ref disk, "Reused"); PutFile(ref disk, kept, 0, StudyFixture.Pattern(16384, 23));
        var expected = Manifest(disk.Snapshot());
        for (var guard = 0; AmigaFreedRangeQueue.Count(ref disk, disk.FreedQueue) != 0; guard++)
        {
            Assert.True(guard < 128);
            Assert.True(AmigaFreedRangeQueue.Take(ref disk, disk.FreedQueue, 64, 0, out var first, out var count));
            var before = disk.DiscardRequests.Count;
            Assert.True(AmigaSmartFileSystem2WriteCore.TrimFreedRange(ref disk, ref work, APTR.Null,
                first, count, out _), work.Error.ToString());
            foreach (var (block, length) in disk.DiscardRequests.Skip(before))
                Assert.True(block >= first && length <= count && block - first <= count - length);
            Assert.Equal(expected, Manifest(disk.Snapshot()));
        }
        Inspect(disk.Snapshot());
    }

    [Fact]
    public void TrackedRangeIoDoesNotReadUnrelatedAllocationPages()
    {
        var reads = new List<long>();
        foreach (var mib in new[] { 8, 32 })
        {
            var disk = new StudyDisk(Empty(capacity: mib * 1024 * 1024), 512);
            var work = WorkFor(disk); var before = disk.Counts.BlockReads;
            Assert.True(AmigaSmartFileSystem2WriteCore.TrimFreedRange(ref disk, ref work, APTR.Null, 100, 16, out var result));
            Assert.Equal(16u, result.Blocks);
            reads.Add(disk.Counts.BlockReads - before);
            output.WriteLine($"{mib} MiB tracked 16-block hint: {disk.Counts}");
        }
        Assert.Equal(reads[0], reads[1]);
    }

    [Theory]
    [InlineData(8, 512)] [InlineData(32, 512)] [InlineData(128, 4096)]
    public void CachedWindowValidationIsLinearAndWritesInvalidateIt(int mib, int bytes)
    {
        var image = Empty(bytes, capacity: mib * 1024 * 1024);
        var disk = new StudyDisk(image, bytes);
        var certificate = APTR.FromPointer((uint)disk.Ram.Length - 4);
        disk.TrimCertificate = certificate;
        var work = WorkFor(disk);
        for (uint cursor = 0; cursor < disk.Geometry.BlockCount; cursor += 4096)
            Assert.True(AmigaSmartFileSystem2WriteCore.TrimWindow(ref disk, ref work, APTR.Null,
                0, out _, cursor, 4096, certificate));
        var maps = (disk.BlockCount - 1) / ((uint)(bytes - 12) * 8) + 1;
        var windows = (disk.BlockCount + 4095) / 4096;
        output.WriteLine($"{mib} MiB cached windows: {disk.Counts}; maps={maps}");
        // Full preflight once, bounded bitmap work per subsequent window.
        Assert.True(disk.Counts.BlockReads < maps + windows * 20);
        Assert.Equal(1, disk.Counts.Flushes);
        Assert.Equal(1u, disk.ReadUInt32(certificate));
        var before = disk.DiscardRequests.Count;
        var lastMap = U32(image, 96) + maps - 1;
        Assert.True(disk.ReadBlock(lastMap, work.Meta));
        disk.WriteUInt32(work.Meta, 4, disk.ReadUInt32(work.Meta, 4) ^ 1);
        Assert.True(disk.WriteBlock(lastMap, work.Meta));
        Assert.Equal(0u, disk.ReadUInt32(certificate));
        Assert.False(AmigaSmartFileSystem2WriteCore.TrimWindow(ref disk, ref work, APTR.Null,
            0, out _, 0, 4096, certificate));
        Assert.Equal(before, disk.DiscardRequests.Count);
    }

    [Fact]
    public void DeferredWindowIoCostIsMeasuredAgainstOneManualPass()
    {
        var image = Empty(capacity: 8 * 1024 * 1024);
        var manual = new StudyDisk(image.ToArray(), 512); var work = WorkFor(manual);
        Assert.True(AmigaSmartFileSystem2WriteCore.Trim(ref manual, ref work, APTR.Null, 0, out _));
        var delayed = new StudyDisk(image.ToArray(), 512); work = WorkFor(delayed);
        for (uint cursor = 0; cursor < delayed.Geometry.BlockCount; cursor += 4096)
            Assert.True(AmigaSmartFileSystem2WriteCore.TrimWindow(ref delayed, ref work, APTR.Null,
                0, out _, cursor, 4096));
        output.WriteLine($"Manual: {manual.Counts}; deferred windows: {delayed.Counts}");
        Assert.Equal(manual.Discarded, delayed.Discarded);
        // This is a measured qualification limitation: preflight repeats per window.
        Assert.True(delayed.Counts.BlockReads > manual.Counts.BlockReads);
        Assert.True(delayed.Counts.Flushes > manual.Counts.Flushes);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DeferredWindowsRevalidateReusedBlocksAndRecoverBeforeDiscard(bool redo)
    {
        var disk = new StudyDisk(Empty(), 512, crashModel: true);
        var deleted = CreateFile(ref disk, "Deleted");
        PutFile(ref disk, deleted, 0, StudyFixture.Pattern(8192, 22));
        var work = WorkFor(disk);
        if (redo)
        {
            var request = new AmigaSmartFileSystem2WriteCore.Request { Node = deleted };
            Assert.True(AmigaSmartFileSystem2WriteCore.ExecuteTrst(ref disk, ref work, ref request,
                AmigaSmartFileSystem2WriteCore.TrstOperation.Remove, disk.Payload, 192 * 1024, out _));
        }
        else Assert.True(AmigaSmartFileSystem2WriteCore.Remove(ref disk, ref work, deleted));
        // An allocation happens after scheduling and before the delayed scan.
        var keep = CreateFile(ref disk, "Reused");
        PutFile(ref disk, keep, 0, StudyFixture.Pattern(16384, 23));
        var expected = Manifest(disk.Snapshot());
        const uint window = 67;
        for (uint cursor = 0; cursor < disk.Geometry.BlockCount; cursor += window)
        {
            var requests = disk.DiscardRequests.Count;
            Assert.True(AmigaSmartFileSystem2WriteCore.TrimWindow(ref disk, ref work, APTR.Null, 0,
                out _, cursor, window), work.Error.ToString());
            foreach (var (block, count) in disk.DiscardRequests.Skip(requests))
            {
                Assert.True(block >= cursor);
                Assert.True(count <= window && block - cursor <= window - count);
            }
            Assert.Equal(expected, Manifest(disk.Snapshot()));
        }
        Inspect(disk.Snapshot());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void EveryBitmapAndPermanentMetadataMustValidateBeforeAnyDiscard(int corruption)
    {
        var image = Empty(capacity: 8 * 1024 * 1024);
        var bitmap = (int)U32(image, 96);
        if (corruption == 0) image[(bitmap + 4) * 512 + 4] ^= 1; // Last map: earlier free runs must not be submitted.
        else
        {
            var block = corruption == 1 ? 2 : corruption == 2 ? bitmap + 4 : 5;
            var map = image.AsSpan(bitmap * 512, 512);
            var at = 12 + block / 32 * 4;
            U32(map, at, U32(map, at) | (1u << (31 - block % 32))); Seal(map);
        }
        var disk = new StudyDisk(image, 512); var work = WorkFor(disk);
        Assert.False(AmigaSmartFileSystem2WriteCore.Trim(ref disk, ref work, APTR.Null, 0, out _));
        Assert.Empty(disk.DiscardRequests);
    }

    [Theory]
    [InlineData(1u, 0u, 6000u)] [InlineData(8u, 3u, 31u)] [InlineData(128u, 127u, 512u)]
    public void RangeLimitsAlignmentAndPartitionEndsAreRespected(uint granularity, uint alignment, uint maximum)
    {
        var disk = new StudyDisk(Empty(capacity: 8 * 1024 * 1024), 512)
        { ScalarRanges = true, DiscardLimits = new() { Granularity = granularity, Alignment = alignment, MaximumBlocks = maximum } };
        var work = WorkFor(disk);
        Assert.True(AmigaSmartFileSystem2WriteCore.Trim(ref disk, ref work, APTR.Null, 0, out var result));
        Assert.Equal((uint)disk.Discarded, result.Blocks);
        foreach (var (block, count) in disk.DiscardRequests)
        {
            Assert.Equal(alignment, block % granularity); Assert.Equal(0u, count % granularity);
            Assert.InRange(count, 1u, maximum); Assert.True(block + count < disk.Geometry.BlockCount);
        }
        Inspect(disk.Snapshot());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void InterruptedJournalMustRecoverDurablyBeforeDiscard(bool redo)
    {
        var (image, node) = Seed();
        var mutation = new StudyDisk(image, 512, crashModel: true);
        Assert.True(Mutate(ref mutation, node, "overwrite", redo));
        var interrupted = new StudyDisk(image, 512, crashModel: true) { CutAfterEvent = mutation.Events - 1 };
        Assert.Throws<StudyPowerCut>(() => Mutate(ref interrupted, node, "overwrite", redo));
        var dirty = interrupted.Snapshot();
        var recovered = new StudyDisk(dirty, 512, crashModel: true); var work = WorkFor(recovered);
        Assert.True(AmigaSmartFileSystem2WriteCore.Recover(ref recovered, ref work));
        var expected = Manifest(recovered.Snapshot());
        var checkpointEvents = recovered.Events + 1; // Trim also flushes the clean allocation state.
        for (var point = 1; point <= checkpointEvents; point++)
        {
            var failed = new StudyDisk(dirty, 512, crashModel: true) { FailAtEvent = point, PersistentFailure = true };
            work = WorkFor(failed);
            Assert.False(AmigaSmartFileSystem2WriteCore.Trim(ref failed, ref work, APTR.Null, 0, out _));
            Assert.Equal(0, failed.Discarded);
            var disk = new StudyDisk(dirty, 512, crashModel: true) { CutAfterEvent = point }; work = WorkFor(disk);
            Assert.Throws<StudyPowerCut>(() => AmigaSmartFileSystem2WriteCore.Trim(ref disk, ref work, APTR.Null, 0, out _));
            Assert.Equal(0, disk.Discarded);
            foreach (var saved in disk.CrashImages())
            {
                var reopened = new StudyDisk(saved, 512, crashModel: true); work = WorkFor(reopened);
                Assert.True(AmigaSmartFileSystem2WriteCore.Trim(ref reopened, ref work, APTR.Null, 0, out _));
                Assert.Equal(expected, Manifest(reopened.Snapshot()));
            }
        }
    }

    [Theory]
    [InlineData(512, false)] [InlineData(1024, false)] [InlineData(4096, false)]
    [InlineData(512, true)] [InlineData(1024, true)] [InlineData(4096, true)]
    public void DeleteTrimRewritePreservesFilesAndAllocation(int bytes, bool redo)
    {
        var disk = new StudyDisk(Empty(bytes), bytes, crashModel: true);
        var keep = CreateFile(ref disk, "Keep"); PutFile(ref disk, keep, 0, StudyFixture.Pattern(4099, 21));
        var deleted = CreateFile(ref disk, "Deleted"); PutFile(ref disk, deleted, 0, StudyFixture.Pattern(8192, 22));
        var work = WorkFor(disk);
        if (redo)
        {
            var r = new AmigaSmartFileSystem2WriteCore.Request { Node = deleted };
            Assert.True(AmigaSmartFileSystem2WriteCore.ExecuteTrst(ref disk, ref work, ref r,
                AmigaSmartFileSystem2WriteCore.TrstOperation.Remove, disk.Payload, 192 * 1024, out _));
        }
        else Assert.True(AmigaSmartFileSystem2WriteCore.Remove(ref disk, ref work, deleted));
        var expected = Manifest(disk.Snapshot(), bytes);
        Assert.True(AmigaSmartFileSystem2WriteCore.Trim(ref disk, ref work, APTR.Null, 1, out var proposed));
        Assert.Equal(0, disk.Discarded);
        Assert.True(AmigaSmartFileSystem2WriteCore.Trim(ref disk, ref work, APTR.Null, 0, out var actual));
        Assert.Equal(proposed.Blocks, actual.Blocks); Assert.True(actual.Blocks > 0);
        Assert.Equal(expected, Manifest(disk.Snapshot(), bytes));
        var reused = CreateFile(ref disk, "Reused"); PutFile(ref disk, reused, 0, StudyFixture.Pattern(16384, 23));
        Inspect(disk.Snapshot(), bytes);
    }

    [Fact]
    public void QueryUnsupportedReadOnlyAndBadBitmapCannotDiscard()
    {
        var disk = new StudyDisk(Empty(), 512, crashModel: true); var work = WorkFor(disk);
        Assert.True(AmigaSmartFileSystem2WriteCore.Trim(ref disk, ref work, APTR.Null, 2, out _));
        Assert.Equal(0, disk.Events);
        disk.DiscardSupported = false;
        Assert.False(AmigaSmartFileSystem2WriteCore.Trim(ref disk, ref work, APTR.Null, 0, out _));
        Assert.Equal(0, disk.Events);
        disk.DiscardSupported = true; disk.ReadOnly = true;
        Assert.False(AmigaSmartFileSystem2WriteCore.Trim(ref disk, ref work, APTR.Null, 0, out _));
        Assert.Equal(0, disk.Discarded);
        var image = Empty(); var map = U32(image, 96); image[map * 512 + 4] ^= 1;
        disk = new StudyDisk(image, 512); work = WorkFor(disk);
        Assert.False(AmigaSmartFileSystem2WriteCore.Trim(ref disk, ref work, APTR.Null, 0, out _));
        Assert.Equal(0, disk.Discarded);
    }

    [Fact]
    public void EveryDiscardFailureAndCheckpointCutCanRecoverAndRetry()
    {
        var seed = new StudyDisk(Empty(), 512, crashModel: true);
        var keep = CreateFile(ref seed, "Keep"); PutFile(ref seed, keep, 0, StudyFixture.Pattern(1027, 31));
        var image = seed.Snapshot(); var expected = Manifest(image);
        var work = WorkFor(seed);
        Assert.True(AmigaSmartFileSystem2WriteCore.Trim(ref seed, ref work, APTR.Null, 0, out _));
        for (var cut = 1; cut <= seed.Discarded; cut++)
        {
            var disk = new StudyDisk(image, 512, crashModel: true) { FailDiscardAt = cut }; work = WorkFor(disk);
            Assert.False(AmigaSmartFileSystem2WriteCore.Trim(ref disk, ref work, APTR.Null, 0, out var failed));
            Assert.Equal((uint)(disk.DiscardRequests.Count - 1), failed.Ranges);
            Assert.Equal((uint)disk.DiscardRequests.SkipLast(1).Sum(r => (long)r.Count), failed.Blocks);
            Assert.Equal(expected, Manifest(disk.Snapshot()));
            disk.FailDiscardAt = 0;
            Assert.True(AmigaSmartFileSystem2WriteCore.Trim(ref disk, ref work, APTR.Null, 0, out _));
            Assert.Equal(expected, Manifest(disk.Snapshot()));
        }
        var probe = new StudyDisk(image, 512, crashModel: true); work = WorkFor(probe);
        Assert.True(AmigaSmartFileSystem2WriteCore.Trim(ref probe, ref work, APTR.Null, 0, out _));
        for (var cut = 1; cut <= probe.Events; cut++)
        {
            var disk = new StudyDisk(image, 512, crashModel: true) { CutAfterEvent = cut }; work = WorkFor(disk);
            Assert.Throws<StudyPowerCut>(() => AmigaSmartFileSystem2WriteCore.Trim(ref disk, ref work, APTR.Null, 0, out _));
            var reopened = new StudyDisk(disk.Snapshot(), 512); work = WorkFor(reopened);
            Assert.True(AmigaSmartFileSystem2WriteCore.Trim(ref reopened, ref work, APTR.Null, 0, out _));
            Assert.Equal(expected, Manifest(reopened.Snapshot()));
        }
    }
}
