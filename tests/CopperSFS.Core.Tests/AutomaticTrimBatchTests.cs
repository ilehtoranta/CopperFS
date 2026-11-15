using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    private static APTR AutomaticBatch(ref Disk disk)
    {
        var batch = APTR.FromPointer((uint)disk.Ram.Length - 2048);
        AmigaAutomaticTrimBatch.Begin(ref disk, batch, 11, 7,
            new AmigaFileSystemDiscardLimits { MaximumBlocks = 4096, Granularity = 1 }, 8);
        disk.WriteUInt32(batch, AmigaAutomaticTrimBatch.Inputs, 2);
        disk.WriteUInt32(batch, AmigaAutomaticTrimBatch.Input, 32);
        disk.WriteUInt32(batch, AmigaAutomaticTrimBatch.Input + 4, 32);
        disk.WriteUInt32(batch, AmigaAutomaticTrimBatch.Input + 8, 80);
        disk.WriteUInt32(batch, AmigaAutomaticTrimBatch.Input + 12, 16);
        return batch;
    }

    [Fact]
    public void AutomaticSfsChecksCleanAllocationOnceWithoutRecoveryWrites()
    {
        var disk = new Disk(Image()) { Buffered = true, DiscardPattern = 2 };
        var w = BatchWork(ref disk); var batch = AutomaticBatch(ref disk);
        var expected = AllocatedState(disk.Snapshot());
        Assert.True(AmigaSmartFileSystemWriteCore.CollectAutomaticTrim(ref disk, ref w, batch, 11, 7), w.Error.ToString());
        Assert.Equal(0, disk.Writes); Assert.Equal(1, disk.Flushes);
        Assert.True(disk.ReadUInt32(batch, AmigaAutomaticTrimBatch.OutputBlocks) > 0);
        for (var i = 0u; i < disk.ReadUInt32(batch, AmigaAutomaticTrimBatch.Outputs); i++)
            Assert.True(disk.DiscardBlocks(APTR.Null, disk.ReadUInt32(batch, AmigaAutomaticTrimBatch.Output + (int)i * 8),
                disk.ReadUInt32(batch, AmigaAutomaticTrimBatch.Output + (int)i * 8 + 4)));
        Assert.Equal(expected, AllocatedState(disk.Snapshot()));
    }

    [Fact]
    public void AutomaticSfsNeverReplaysPublishedTrstOrCommitsPendingBuffer()
    {
        var image = Image(); var marker = Block(image, 4);
        Header(image, 4, 0x54525354);
        U16(marker, 16, 7); U32(marker, 18, 20); marker[22] = 0; marker[23] = 0xC0;
        marker[24] = (byte)'T'; marker[25] = (byte)'E'; marker[26] = (byte)'S'; marker[27] = (byte)'T';
        marker[28] = 0x7F; marker[29] = 0x7E; Sum(marker);
        var disk = new Disk(image) { Buffered = true }; var w = BatchWork(ref disk);
        var batch = AutomaticBatch(ref disk);
        Assert.False(AmigaSmartFileSystemWriteCore.CollectAutomaticTrim(ref disk, ref w, batch, 11, 7));
        Assert.Equal(0, disk.Writes); Assert.Empty(disk.DiscardRequests);
        Assert.Equal(0u, disk.ReadUInt32(batch, AmigaAutomaticTrimBatch.Outputs));
        disk = new Disk(Image()) { Buffered = true }; w = TrimWork(ref disk, true, 128);
        Assert.True(AmigaSmartFileSystemWriteCore.Truncate(ref disk, ref w, 3));
        batch = AutomaticBatch(ref disk); var writes = disk.Writes; var flushes = disk.Flushes;
        Assert.False(AmigaSmartFileSystemWriteCore.CollectAutomaticTrim(ref disk, ref w, batch, 11, 7));
        Assert.Equal(writes, disk.Writes); Assert.Equal(flushes, disk.Flushes);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AutomaticBitmapWordScanMatchesIndividualBitsAndExactBounds(bool msb)
    {
        var disk = new Disk(Image()); var batch = AutomaticBatch(ref disk); var bitmap = APTR.FromPointer(4096);
        var random = new Random(5473);
        for (var trial = 0; trial < 100; trial++)
        {
            AmigaAutomaticTrimBatch.Begin(ref disk, batch, 11, 7,
                new AmigaFileSystemDiscardLimits { MaximumBlocks = 4096, Granularity = 1 }, 8);
            var word = trial == 0 ? uint.MaxValue : (uint)random.NextInt64(0, 0x100000000L);
            disk.WriteUInt32(bitmap, 0, word);
            var begin = (uint)random.Next(0, 16); var count = (uint)random.Next(1, 33 - (int)begin);
            uint run = 0, length = 0;
            AmigaAutomaticTrimBatch.ScanBitmap(ref disk, batch, bitmap, 0, 100, 100 + begin, count, msb, ref run, ref length);
            if (length != 0) AmigaAutomaticTrimBatch.FreeRun(ref disk, batch, run, length);
            var actual = new HashSet<uint>();
            foreach (var offset in new[] { AmigaAutomaticTrimBatch.Output, AmigaAutomaticTrimBatch.Remainders })
            {
                var used = disk.ReadUInt32(batch, offset == AmigaAutomaticTrimBatch.Output ? AmigaAutomaticTrimBatch.Outputs : AmigaAutomaticTrimBatch.RemainderCount);
                for (var i = 0u; i < used; i++)
                {
                    var first = disk.ReadUInt32(batch, offset + (int)i * 8);
                    for (var n = 0u; n < disk.ReadUInt32(batch, offset + (int)i * 8 + 4); n++) actual.Add(first + n);
                }
            }
            var expected = Enumerable.Range((int)begin, (int)count).Where(i => (word & 1u << (msb ? 31 - i : i)) != 0).Select(i => (uint)i + 100);
            Assert.Equal(expected, actual.Order());
        }
    }

    [Fact]
    public void AutomaticBatchAlignsInwardAndRetainsDescriptorAndAggregateRemaindersAsHints()
    {
        var disk = new Disk(Image()); var batch = AutomaticBatch(ref disk);
        AmigaAutomaticTrimBatch.Begin(ref disk, batch, 11, 7,
            new AmigaFileSystemDiscardLimits { MaximumBlocks = 17, Granularity = 8, Alignment = 3 }, 1);
        AmigaAutomaticTrimBatch.FreeRun(ref disk, batch, 100, 40);
        AmigaAutomaticTrimBatch.FreeRun(ref disk, batch, 200, 20);
        Assert.Equal(1u, disk.ReadUInt32(batch, AmigaAutomaticTrimBatch.Outputs));
        Assert.Equal(107u, disk.ReadUInt32(batch, AmigaAutomaticTrimBatch.Output));
        Assert.Equal(16u, disk.ReadUInt32(batch, AmigaAutomaticTrimBatch.Output + 4));
        Assert.Equal(2u, disk.ReadUInt32(batch, AmigaAutomaticTrimBatch.RemainderCount));
        Assert.Equal(123u, disk.ReadUInt32(batch, AmigaAutomaticTrimBatch.Remainders));
        Assert.Equal(16u, disk.ReadUInt32(batch, AmigaAutomaticTrimBatch.Remainders + 4));
        Assert.Equal(203u, disk.ReadUInt32(batch, AmigaAutomaticTrimBatch.Remainders + 8));
        Assert.False(AmigaAutomaticTrimBatch.Current(ref disk, batch, 11, 8));
        Assert.False(AmigaAutomaticTrimBatch.Current(ref disk, batch, 12, 7));
    }
}
