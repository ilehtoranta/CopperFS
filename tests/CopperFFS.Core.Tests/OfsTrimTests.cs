using Amiga;
using CopperSFS;
using CopperStart.Dos;
namespace CopperStart.FileSystem.Tests;
public sealed partial class VolumeGeometryTests
{
    [Theory]
    [InlineData(512u)] [InlineData(1024u)] [InlineData(4096u)]
    public void OfsTrimPreservesAllMetadataWithNonDefaultBlocksAndWideOffsets(uint bytes)
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(9003, bytes, 2, out var g));
        g.StartByte = 5UL * 1024 * 1024 * 1024 + 512;
        var p = new Memory(storage, g); Format(ref p, g); SetDosType(ref p, 0x444F5300);
        var before = storage.Sectors.ToDictionary(x => x.Key, x => x.Value.ToArray()); var w = Work();
        Assert.True(AmigaFfsWriteCore.Trim(ref p, ref w, APTR.Null, 1, out var dry));
        Assert.Empty(storage.Discards);
        Assert.True(AmigaFfsWriteCore.Trim(ref p, ref w, APTR.Null, 0, out var real));
        Assert.Equal(dry.Blocks, real.Blocks); Assert.Equal(dry.Ranges, real.Ranges);
        Assert.Equal(g.BlockCount - Used(ref p), real.Blocks);
        foreach (var sector in before) Assert.Equal(sector.Value, storage.Sectors[sector.Key]);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(128)] [InlineData(129)] [InlineData(258)]
    public void OfsPartialDiscardRetriesFreshBitmapAfterNewWrites(int fail)
    {
        var storage = new Storage { FailDiscardAt = fail };
        Assert.True(AmigaVolumeGeometry.Create(3520, 512, 2, out var g));
        g.StartByte = 5UL * 1024 * 1024 * 1024 + 512;
        var p = new Memory(storage, g); Format(ref p, g); SetDosType(ref p, 0x444F5300); var w = Work();
        Assert.True(AmigaFfsWriteCore.Trim(ref p, ref w, APTR.Null, 2, out _));
        Assert.Equal(0, storage.Flushes);
        Assert.True(AmigaFfsWriteCore.Trim(ref p, ref w, APTR.Null, 1, out _));
        Assert.Empty(storage.Discards);
        Assert.False(AmigaFfsWriteCore.Trim(ref p, ref w, APTR.Null, 0, out var result));
        Assert.Equal(DOS.Error.SeekError, w.Error);
        Assert.Equal((uint)(storage.Discards.Count - 1), result.Ranges);
        Assert.Equal((uint)storage.Discards.SkipLast(1).Sum(x => (long)x.Count), result.Blocks);
        var request = OfsFile(ref p, storage, g, "NewFile");
        var expected = new byte[] { 1, 2, 3, 4 };
        OfsWrite(ref p, storage, ref request, 0, expected);
        var owned = InspectOfs(storage, g, request.Header, expected);
        storage.FailDiscardAt = 0; storage.Discards.Clear();
        Assert.True(AmigaFfsWriteCore.Trim(ref p, ref w, APTR.Null, 0, out _));
        InspectOfs(storage, g, request.Header, expected);
        foreach (var key in owned)
            Assert.DoesNotContain(storage.Discards, range => key >= range.Block && key - range.Block < range.Count);
    }
}
