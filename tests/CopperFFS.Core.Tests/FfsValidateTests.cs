using System.Text;
using Amiga;
using CopperSFS;
using CopperStart.Dos;

namespace CopperStart.FileSystem.Tests;

public sealed partial class VolumeGeometryTests
{
    private static readonly APTR ValidateWorkspace = APTR.FromPointer(0x180000);

    private static void SetRootValid(ref Memory p, AmigaVolumeGeometry g, bool valid)
    {
        Assert.True(p.ReadBlock(g.RootBlock, Meta)); p.WriteUInt32(Meta, g.Tail + 312, valid ? uint.MaxValue : 0);
        Reseal(ref p, g.RootBlock);
    }

    // Snapshot of every bitmap page's allocation words.
    private static uint[] BitmapWords(ref Memory p, AmigaVolumeGeometry g)
    {
        var maps = (g.BlockCount - g.ReservedBlocks - 1) / g.BitmapBits + 1;
        var words = new List<uint>();
        for (var i = 0u; i < maps; i++)
        {
            Assert.True(AmigaDiskFileSystemCore.BitmapBlock(ref p, i, Meta, out var key)); Assert.True(p.ReadBlock(key, Bitmap));
            for (var at = 4; at < g.BlockBytes; at += 4) words.Add(p.ReadUInt32(Bitmap, at));
        }
        return words.ToArray();
    }

    private static void FlipBit(ref Memory p, AmigaVolumeGeometry g, uint key)
    {
        var bit = key - g.ReservedBlocks;
        Assert.True(AmigaDiskFileSystemCore.BitmapBlock(ref p, bit / g.BitmapBits, Meta, out var map)); Assert.True(p.ReadBlock(map, Bitmap));
        var at = 4 + (int)(bit % g.BitmapBits / 32) * 4;
        p.WriteUInt32(Bitmap, at, p.ReadUInt32(Bitmap, at) ^ (1u << (int)(bit % 32)));
        uint sum = 0; for (var i = 4; i < g.BlockBytes; i += 4) sum = unchecked(sum + p.ReadUInt32(Bitmap, i));
        p.WriteUInt32(Bitmap, 0, unchecked(0u - sum)); Assert.True(p.WriteBlock(map, Bitmap));
    }

    // A tree with nested directories, OFS/FFS files spanning extension
    // tables and a hard link, on a volume with bitmap extension blocks.
    private static (Storage, Memory, AmigaVolumeGeometry, uint[] Keys) ValidationVolume(int dos, uint bytes = 512, uint blocks = 200000)
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(blocks, bytes, 2, out var g));
        var p = new Memory(storage, g); var type = 0x444F5300u + (uint)dos; VariantFormat(ref p, storage, type);
        var a = VariantCreate(ref p, storage, g.RootBlock, "A", true);
        var b = VariantCreate(ref p, storage, a.Header, "B", true);
        var keys = new List<uint> { a.Header, b.Header };
        for (var i = 0; i < 6; i++)
        {
            var file = VariantCreate(ref p, storage, i % 2 == 0 ? b.Header : g.RootBlock, $"File{i}");
            OfsWrite(ref p, storage, ref file, 0, Enumerable.Range(0, 3000 + i * (int)bytes * 40).Select(n => (byte)(n + i)).ToArray());
            keys.Add(file.Header);
        }
        Encoding.Latin1.GetBytes("Link").CopyTo(storage.Ram, (int)Name.Raw);
        var link = new AmigaFfsWriteCore.Request { Directory = a.Header, Name = Name, Length = 4 }; var w = Work();
        Assert.True(AmigaFfsWriteCore.MakeLink(ref p, ref w, ref link, keys[2], APTR.Null, 0, false), w.Error.ToString());
        keys.Add(link.Header);
        return (storage, p, g, keys.ToArray());
    }

    // The validator rebuilds exactly the allocation the writer produced, from
    // a bitmap with leaked and missing bits, in one pass or several.
    [Theory]
    [InlineData(0, 262144u)] [InlineData(1, 262144u)] [InlineData(3, 262144u)] [InlineData(5, 262144u)]
    [InlineData(1, 508u)] [InlineData(5, 1500u)]
    public void ValidateRebuildsTheBitmapFromTheTree(int dos, uint workspaceBytes)
    {
        var (storage, p, g, keys) = ValidationVolume(dos);
        var expected = BitmapWords(ref p, g);
        Assert.True(AmigaDiskFileSystemCore.CountUsed(ref p, Meta, Bitmap, out var used));
        // Leak two free blocks, lose a header's bit and a data block's bit.
        FlipBit(ref p, g, g.BlockCount - 7); FlipBit(ref p, g, 150000);
        FlipBit(ref p, g, keys[3]);
        Assert.True(p.ReadBlock(keys[4], Meta)); FlipBit(ref p, g, p.ReadUInt32(Meta, g.Tail + 308));
        SetRootValid(ref p, g, false);
        var w = Work();
        Assert.True(AmigaFfsWriteCore.Validate(ref p, ref w, ValidateWorkspace, workspaceBytes), w.Error.ToString());
        Assert.Equal(expected, BitmapWords(ref p, g));
        Assert.True(AmigaDiskFileSystemCore.CountUsed(ref p, Meta, Bitmap, out var after)); Assert.Equal(used, after);
        Assert.True(p.ReadBlock(g.RootBlock, Meta)); Assert.Equal(uint.MaxValue, p.ReadUInt32(Meta, g.Tail + 312));
        // The repaired volume accepts writes again.
        var again = VariantCreate(ref p, storage, g.RootBlock, "Again");
        OfsWrite(ref p, storage, ref again, 0, new byte[5000]);
    }

    // A block reachable twice, or a damaged header, is refused: the volume
    // keeps its invalid root and no bitmap page changes.
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ValidateRefusesCrossLinksAndDamagedHeaders(bool crossLink)
    {
        var (storage, p, g, keys) = ValidationVolume(1, blocks: 20000);
        Assert.True(p.ReadBlock(keys[3], Meta));
        if (crossLink)
        {
            // Point a data slot of one file at another file's header.
            p.WriteUInt32(Meta, g.Tail + 308, keys[4]);
            Reseal(ref p, keys[3]);
        }
        else
        {
            p.WriteUInt32(Meta, 20, p.ReadUInt32(Meta, 20) + 1);
            Assert.True(p.WriteBlock(keys[3], Meta));
        }
        SetRootValid(ref p, g, false);
        var before = BitmapWords(ref p, g);
        var w = Work();
        Assert.False(AmigaFfsWriteCore.Validate(ref p, ref w, ValidateWorkspace, 262144));
        Assert.Equal(DOS.Error.DiskNotValidated, w.Error);
        Assert.Equal(before, BitmapWords(ref p, g));
        Assert.True(p.ReadBlock(g.RootBlock, Meta)); Assert.Equal(0u, p.ReadUInt32(Meta, g.Tail + 312));
    }
}
