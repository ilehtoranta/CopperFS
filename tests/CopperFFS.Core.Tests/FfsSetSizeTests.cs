using System.Text;
using Amiga;
using CopperSFS;
using CopperStart.Dos;

namespace CopperStart.FileSystem.Tests;

public sealed partial class VolumeGeometryTests
{
    private static uint ExpectedFileBlocks(AmigaVolumeGeometry g, int dos, uint size)
    {
        var payload = (dos & 1) == 0 ? g.BlockBytes - 24 : g.BlockBytes;
        var blocks = size == 0 ? 0 : (size - 1) / payload + 1;
        return blocks + (blocks == 0 ? 0 : (blocks - 1) / g.TableEntries);
    }

    // Shrinking and growing across pointer-table boundaries keeps a valid
    // chain on every variant: the kept prefix reads back unchanged, the
    // block count matches the size, and DOS4/5 caches record the new size.
    [Theory]
    [InlineData(0, 512u)] [InlineData(1, 512u)] [InlineData(4, 512u)] [InlineData(5, 512u)]
    [InlineData(0, 1024u)] [InlineData(1, 1024u)] [InlineData(4, 1024u)]
    public void SetSizeShrinksAndGrowsAcrossPointerTables(int dos, uint bytes)
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(12000, bytes, 2, out var g));
        var p = new Memory(storage, g); var type = 0x444F5300u + (uint)dos; VariantFormat(ref p, storage, type);
        var file = VariantCreate(ref p, storage, g.RootBlock, "Sized");
        var baseline = Used(ref p);
        var payload = (dos & 1) == 0 ? (int)bytes - 24 : (int)bytes;
        var data = Enumerable.Range(0, payload * (int)(g.TableEntries * 2 + 9)).Select(i => (byte)(i * 7 + 3)).ToArray();
        OfsWrite(ref p, storage, ref file, 0, data);
        var sizes = new uint[]
        {
            (uint)(payload * (int)g.TableEntries + 1), (uint)(payload * (int)g.TableEntries), (uint)(payload * 3 - 5), 1,
            (uint)(payload * (int)(g.TableEntries * 3) + 17), (uint)(payload * 2), 0, (uint)payload + 100,
        };
        foreach (var size in sizes)
        {
            var w = Work(); Assert.True(AmigaFfsWriteCore.SetSize(ref p, ref w, file.Header, size), $"{size}: {w.Error}");
            Assert.Equal(baseline + ExpectedFileBlocks(g, dos, size), Used(ref p));
            Assert.True(p.ReadBlock(file.Header, Meta)); Assert.Equal(size, p.ReadUInt32(Meta, g.Tail + 324));
            Assert.Equal(uint.MaxValue, p.ReadBlock(g.RootBlock, Meta) ? p.ReadUInt32(Meta, g.Tail + 312) : 0);
            var kept = (int)Math.Min(size, (uint)data.Length);
            if (size != 0)
            {
                Assert.Equal((int)size, AmigaDiskFileSystemCore.Read(ref p, file.Header, type, 0, Output, size, Meta, Data, out var error));
                Assert.Equal(DOS.Error.None, error);
                Assert.Equal(data.AsSpan(0, kept).ToArray(), storage.Ram.AsSpan((int)Output.Raw, kept).ToArray());
            }
            // Later growth reads whatever its blocks held; only the shrink
            // point is still covered by the original data.
            data = data.AsSpan(0, kept).ToArray();
            if ((dos & 1) == 0)
            {
                var expected = new byte[size];
                Assert.True(p.ReadBlock(file.Header, Meta));
                Assert.Equal(size, (uint)AmigaDiskFileSystemCore.Read(ref p, file.Header, type, 0, Output, size, Meta, Data, out _));
                storage.Ram.AsSpan((int)Output.Raw, (int)size).CopyTo(expected);
                // OFS growth zero-fills the new region.
                Assert.All(expected.Skip(kept), b => Assert.Equal(0, b));
                InspectOfs(storage, g, file.Header, expected);
            }
            if (dos >= 4) InspectDirectoryCache(storage, g, g.RootBlock, 1);
        }
    }

    // FFS growth links data blocks without writing them: only pointer
    // tables, the header, the bitmap and the two root validity writes.
    // The grown region reads back the blocks' previous contents, as with
    // Kickstart 3.1's SetFileSize.
    [Theory]
    [InlineData(1)] [InlineData(5)]
    public void FfsSetSizeGrowthWritesNoDataBlocks(int dos)
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(12000, 512, 2, out var g));
        var p = new Memory(storage, g); var type = 0x444F5300u + (uint)dos; VariantFormat(ref p, storage, type);
        var fill = VariantCreate(ref p, storage, g.RootBlock, "Fill");
        OfsWrite(ref p, storage, ref fill, 0, Enumerable.Repeat((byte)0xA5, 200 * 512).ToArray());
        var w = Work(); Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, fill.Header, false), w.Error.ToString());
        var file = VariantCreate(ref p, storage, g.RootBlock, "Grown");
        storage.Transfers.Clear();
        w = Work(); Assert.True(AmigaFfsWriteCore.SetSize(ref p, ref w, file.Header, 150 * 512), w.Error.ToString());
        var writes = storage.Transfers.Where(t => t.Command is 3 or 25).ToArray();
        Assert.True(writes.Length <= 14, $"{writes.Length} writes to grow by 150 blocks");
        Assert.Equal(150 * 512, AmigaDiskFileSystemCore.Read(ref p, file.Header, type, 0, Output, 150 * 512, Meta, Data, out var error));
        Assert.Equal(DOS.Error.None, error);
        Assert.All(storage.Ram.AsSpan((int)Output.Raw, 150 * 512).ToArray(), b => Assert.Equal(0xA5, b));
        if (dos >= 4) InspectDirectoryCache(storage, g, g.RootBlock, 1);
    }

    // A growth that does not fit is refused before the root is invalidated.
    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void SetSizeGrowthBeyondFreeSpaceLeavesTheVolumeUntouched(int dos)
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(1760, 512, 2, out var g));
        var p = new Memory(storage, g); VariantFormat(ref p, storage, 0x444F5300u + (uint)dos);
        var file = VariantCreate(ref p, storage, g.RootBlock, "Huge");
        var before = storage.Sectors.ToDictionary(x => x.Key, x => x.Value.ToArray());
        var w = Work();
        Assert.False(AmigaFfsWriteCore.SetSize(ref p, ref w, file.Header, 2000 * 512));
        Assert.Equal(DOS.Error.DiskFull, w.Error);
        foreach (var (key, sector) in before) Assert.Equal(sector, storage.Sectors[key]);
        Assert.Equal(before.Keys.Order(), storage.Sectors.Keys.Order());
    }
}
