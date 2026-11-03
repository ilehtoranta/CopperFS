using Amiga;
using CopperSFS.Bench;
using static CopperSFS.Tests.Sfs2WriteTests;

namespace CopperSFS.Tests;

public sealed class Sfs2TrimTests
{
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
            Assert.False(AmigaSmartFileSystem2WriteCore.Trim(ref disk, ref work, APTR.Null, 0, out _));
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
