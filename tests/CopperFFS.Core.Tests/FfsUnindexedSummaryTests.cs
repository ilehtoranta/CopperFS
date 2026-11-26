using System.Text;
using Amiga;
using CopperSFS;
using CopperStart.Dos;

namespace CopperStart.FileSystem.Tests;

public sealed partial class VolumeGeometryTests
{
    // A volume whose bitmap keys do not fit the mount index used to reread
    // every bitmap page (and rewalk the extension chain) on every metadata
    // packet and every Info poll. The first packet now checks the bitmap once
    // and keeps a free-count summary; later creates and deletes read a bounded
    // number of bitmap pages and Info needs no I/O.
    [Theory]
    [InlineData(1)] [InlineData(5)]
    public void UnindexedLargeVolumeReadsBoundedBitmapPagesPerMutation(int dos)
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(1_000_000, 512, 2, out var g)); var p = new Memory(storage, g);
        var type = 0x444F5300u + (uint)dos; VariantFormat(ref p, storage, type);
        // 2 KB holds the summary but not the 246 bitmap and extension keys.
        var index = APTR.FromPointer(0x180000); AmigaFfsMountIndex.Initialize(ref p, index, 2048);
        var maps = (g.BlockCount - g.ReservedBlocks - 1) / g.BitmapBits + 1;
        Assert.True(maps > 25 + 127, "the volume needs two bitmap extension blocks");
        var pages = new HashSet<ulong>();
        for (var i = 0u; i < maps; i++)
        {
            Assert.True(AmigaDiskFileSystemCore.BitmapBlock(ref p, i, Meta, out var key));
            pages.Add(g.StartByte + (ulong)key * g.BlockBytes);
        }
        int BitmapReads() => storage.Transfers.Count(t => t.Command is 2 or 24 && pages.Contains(t.Offset));
        AmigaFfsWriteCore.Work Indexed() { var w = Work(); w.Index = index; return w; }
        AmigaFfsWriteCore.Request Create(string name)
        {
            Encoding.Latin1.GetBytes(name).CopyTo(storage.Ram, (int)Name.Raw);
            var r = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = (uint)name.Length }; var w = Indexed();
            Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref r, false), w.Error.ToString()); return r;
        }
        storage.Transfers.Clear(); Create("First");
        Assert.True(BitmapReads() >= maps, $"the first packet checks all {maps} pages, read {BitmapReads()}");
        var made = new List<uint>();
        for (var i = 0; i < 10; i++)
        {
            storage.Transfers.Clear(); made.Add(Create($"File{i}").Header);
            Assert.True(BitmapReads() <= 4, $"create {i} read {BitmapReads()} bitmap pages");
        }
        foreach (var key in made)
        {
            storage.Transfers.Clear(); var w = Indexed();
            Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, key, false), w.Error.ToString());
            Assert.True(BitmapReads() <= 4, $"delete read {BitmapReads()} bitmap pages");
        }
        // Info answers from the summary, which matches an independent count.
        Assert.True(AmigaDiskFileSystemCore.CountUsed(ref p, Meta, Bitmap, out var expected));
        storage.Transfers.Clear(); var info = Indexed();
        Assert.True(AmigaFfsWriteCore.CountUsed(ref p, ref info, out var used));
        Assert.Empty(storage.Transfers); Assert.Equal(expected, used);
        // A reset discards the summary; the next packet checks the bitmap again.
        AmigaFfsMountIndex.Forget(ref p, index);
        storage.Transfers.Clear(); Create("Again");
        Assert.True(BitmapReads() >= maps);
        info = Indexed(); Assert.True(AmigaFfsWriteCore.CountUsed(ref p, ref info, out used)); Assert.Equal(expected + 1, used);
    }

    // Info polls on a large volume nobody has written yet establish the
    // summary once; a failed mutation discards it so the next packet rescans.
    [Fact]
    public void UnindexedSummaryIsEstablishedByInfoAndDroppedByAFailedMutation()
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(1_000_000, 512, 2, out var g)); var p = new Memory(storage, g);
        VariantFormat(ref p, storage, 0x444F5301u);
        var index = APTR.FromPointer(0x180000); AmigaFfsMountIndex.Initialize(ref p, index, 2048);
        Assert.True(AmigaDiskFileSystemCore.CountUsed(ref p, Meta, Bitmap, out var expected));
        var w = Work(); w.Index = index;
        Assert.True(AmigaFfsWriteCore.CountUsed(ref p, ref w, out var first)); Assert.Equal(expected, first);
        storage.Transfers.Clear(); w = Work(); w.Index = index;
        Assert.True(AmigaFfsWriteCore.CountUsed(ref p, ref w, out var second)); Assert.Equal(expected, second);
        Assert.Empty(storage.Transfers);
        // The create's header write fails after Begin invalidated the root.
        storage.FailWriteCall = storage.WriteCalls + 2;
        Encoding.Latin1.GetBytes("Broken").CopyTo(storage.Ram, (int)Name.Raw);
        var r = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 6 }; w = Work(); w.Index = index;
        Assert.False(AmigaFfsWriteCore.Create(ref p, ref w, ref r, false));
        // The volume now needs validation: Info falls back to reading the bitmap.
        storage.Transfers.Clear(); w = Work(); w.Index = index;
        Assert.True(AmigaFfsWriteCore.CountUsed(ref p, ref w, out _)); Assert.NotEmpty(storage.Transfers);
    }
}
