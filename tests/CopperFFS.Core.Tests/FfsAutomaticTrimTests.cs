using Amiga;
using CopperSFS;
using CopperStart.Dos;

namespace CopperStart.FileSystem.Tests;

public sealed partial class VolumeGeometryTests
{
    private static APTR AutomaticBatch(ref Memory p, Storage storage, params (uint First, uint Count)[] ranges)
    {
        var batch = APTR.FromPointer((uint)storage.Ram.Length - 2048);
        AmigaAutomaticTrimBatch.Begin(ref p, batch, 11, 7, storage.Limits, 8);
        p.WriteUInt32(batch, AmigaAutomaticTrimBatch.Inputs, (uint)ranges.Length);
        for (var i = 0; i < ranges.Length; i++)
        { p.WriteUInt32(batch, AmigaAutomaticTrimBatch.Input + i * 8, ranges[i].First); p.WriteUInt32(batch, AmigaAutomaticTrimBatch.Input + i * 8 + 4, ranges[i].Count); }
        AmigaAutomaticTrimBatch.Sort(ref p, batch);
        return batch;
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AutomaticFfsLayoutYieldsAt128PointersAndKeepsOneBarrier(bool ofs)
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(630000, 512, 2, out var g));
        var p = new Memory(storage, g); Format(ref p, g); if (ofs) SetDosType(ref p, 0x444F5300);
        var batch = AutomaticBatch(ref p, storage, (100, 16), (600000, 16)); var work = Work();
        Assert.True(AmigaFfsWriteCore.CollectAutomaticTrim(ref p, ref work, batch, 11, 7, out var done), work.Error.ToString());
        Assert.False(done); Assert.InRange(p.ReadUInt32(batch, AmigaAutomaticTrimBatch.WalkOrdinal), 1u, 128u);
        Assert.Equal(0u, p.ReadUInt32(batch, AmigaAutomaticTrimBatch.Outputs)); Assert.Empty(storage.Discards);
        work = Work(); // Native turns reconstruct scratch descriptors.
        Assert.True(AmigaFfsWriteCore.CollectAutomaticTrim(ref p, ref work, batch, 11, 7, out done), work.Error.ToString());
        Assert.True(done); Assert.Equal(1, storage.Flushes);
        Assert.Equal(32u, p.ReadUInt32(batch, AmigaAutomaticTrimBatch.OutputBlocks));
        for (var i = 0u; i < p.ReadUInt32(batch, AmigaAutomaticTrimBatch.Outputs); i++)
            Assert.True(p.DiscardBlocks(APTR.Null, p.ReadUInt32(batch, AmigaAutomaticTrimBatch.Output + (int)i * 8),
                p.ReadUInt32(batch, AmigaAutomaticTrimBatch.Output + (int)i * 8 + 4)));
        // A one-descriptor device leaves hints for another idle turn. Reuse
        // only the layout proof and rescan allocation without another barrier.
        p.WriteUInt32(batch, AmigaAutomaticTrimBatch.Inputs, 1);
        p.WriteUInt32(batch, AmigaAutomaticTrimBatch.Input, 600000);
        p.WriteUInt32(batch, AmigaAutomaticTrimBatch.Input + 4, 16);
        work = Work();
        Assert.True(AmigaFfsWriteCore.CollectAutomaticTrim(ref p, ref work, batch, 11, 7, out done));
        Assert.True(done); Assert.Equal(1, storage.Flushes);
        Assert.Equal(16u, p.ReadUInt32(batch, AmigaAutomaticTrimBatch.OutputBlocks));
    }

    [Fact]
    public void AutomaticFfsLocatingDistantMapAlsoYieldsAndRejectsInvalidatedContext()
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(100000000, 512, 2, out var g));
        var p = new Memory(storage, g); Format(ref p, g);
        var batch = AutomaticBatch(ref p, storage, (g.BlockCount - 100, 16)); var work = Work();
        storage.Transfers.Clear();
        Assert.True(AmigaFfsWriteCore.CollectAutomaticTrim(ref p, ref work, batch, 11, 7, out var done));
        Assert.False(done); Assert.Equal(3u, p.ReadUInt32(batch, AmigaAutomaticTrimBatch.Stage));
        Assert.InRange(storage.Transfers.Count, 128, 132);
        Assert.Equal(1, storage.Flushes); Assert.Equal(0u, p.ReadUInt32(batch, AmigaAutomaticTrimBatch.Outputs));
        var reads = storage.Transfers.Count;
        Assert.False(AmigaFfsWriteCore.CollectAutomaticTrim(ref p, ref work, batch, 11, 8, out _));
        Assert.Equal(reads, storage.Transfers.Count); Assert.Empty(storage.Discards);
    }

    [Fact]
    public void AutomaticFfsRejectsStaleGenerationAndAliasedTouchedBitmap()
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(9003, 512, 2, out var g));
        var p = new Memory(storage, g); Format(ref p, g);
        var batch = AutomaticBatch(ref p, storage, (100, 16)); var w = Work();
        Assert.False(AmigaFfsWriteCore.CollectAutomaticTrim(ref p, ref w, batch, 11, 8, out _));
        Assert.Equal(0, storage.Flushes);
        Assert.True(p.ReadBlock(g.RootBlock, Meta));
        p.WriteUInt32(Meta, 320, g.RootBlock + 1); Checksum(ref p, Meta, 512, 20);
        Assert.True(p.WriteBlock(g.RootBlock, Meta));
        Assert.False(AmigaFfsWriteCore.CollectAutomaticTrim(ref p, ref w, batch, 11, 7, out _));
        Assert.Empty(storage.Discards);
    }

    [Fact]
    public void AutomaticFfsFailedBarrierProducesNoAuthorization()
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(9003, 512, 2, out var g));
        var p = new Memory(storage, g); Format(ref p, g); var batch = AutomaticBatch(ref p, storage, (100, 16));
        storage.FailFlush = true; var w = Work();
        Assert.False(AmigaFfsWriteCore.CollectAutomaticTrim(ref p, ref w, batch, 11, 7, out _));
        Assert.Equal(0u, p.ReadUInt32(batch, AmigaAutomaticTrimBatch.Outputs)); Assert.Empty(storage.Discards);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AutomaticFfsSharedWalkStillRejectsUnallocatedRootOrTouchedBitmap(bool root)
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(630000, 512, 2, out var g));
        var p = new Memory(storage, g); Format(ref p, g);
        var key = root ? g.RootBlock : g.RootBlock + 1;
        var map = (key - g.ReservedBlocks) / g.BitmapBits;
        var bit = (key - g.ReservedBlocks) % g.BitmapBits;
        Assert.True(p.ReadBlock(g.RootBlock + 1 + map, Bitmap));
        var at = 4 + (int)(bit >> 5) * 4;
        p.WriteUInt32(Bitmap, at, p.ReadUInt32(Bitmap, at) | 1u << (int)(bit & 31));
        Checksum(ref p, Bitmap, 512, 0); Assert.True(p.WriteBlock(g.RootBlock + 1 + map, Bitmap));
        var batch = AutomaticBatch(ref p, storage, (100, 16)); var work = Work();
        var ok = true; var done = false;
        for (var turn = 0; ok && !done && turn < 8; turn++)
            ok = AmigaFfsWriteCore.CollectAutomaticTrim(ref p, ref work, batch, 11, 7, out done);
        Assert.False(ok); Assert.Equal(0u, p.ReadUInt32(batch, AmigaAutomaticTrimBatch.Outputs));
        Assert.Empty(storage.Discards);
    }
}
