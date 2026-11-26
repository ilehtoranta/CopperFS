using Amiga;

namespace CopperStart.Dos;

/// <summary>Quick OFS/FFS format, including chained bitmap pointer blocks.</summary>
public static class AmigaDiskFileSystemFormatCore
{
    public static bool Format<T>(ref T p, uint dosType, APTR name, uint length,
        uint days, uint minutes, uint ticks, APTR scratch, out DOS.Error error)
        where T : struct, IAmigaFormatFileSystemPlatform
    {
        error = DOS.Error.BadNumber;
        var g = p.Geometry;
        if (!CanFormat(g, dosType) || !p.IsMapped(scratch, g.BlockBytes)) return false;
        if (!AmigaSmartFileSystemFormatCore.ValidName(ref p, name, length))
        { error = DOS.Error.InvalidComponentName; return false; }
        if (p.WriteProtected()) { error = DOS.Error.DiskWriteProtected; return false; }
        var maps = (g.BlockCount - g.ReservedBlocks - 1) / g.BitmapBits + 1;
        var entries = g.BlockBytes / 4 - 1;
        var extensions = maps <= 25 ? 0 : (maps - 26) / entries + 1;
        // Kickstart 3.1 Format's layout (measured by the Format reference
        // test): a DOS4/5 directory cache directly after the root, then the
        // bitmap pages, then their extension blocks.
        var cache = dosType >= 0x444F5304 ? g.RootBlock + 1 : 0;
        var first = g.RootBlock + 1 + (cache != 0 ? 1u : 0u);
        var end = first + maps + extensions;
        error = DOS.Error.DiskNotValidated;
        // Withdraw the old boot block first, so an interrupted format never
        // pairs the old volume with new allocation pages. Like Kickstart,
        // leave the other reserved blocks and the last block untouched.
        p.Clear(scratch, g.BlockBytes);
        if (!p.WriteFormatBlock(0, scratch) || !p.Flush()) return false;
        for (var map = 0u; map < maps; map++)
        {
            p.Clear(scratch, g.BlockBytes);
            for (var at = 4u; at < g.BlockBytes; at += 4)
            {
                var key = (ulong)g.ReservedBlocks + (ulong)map * g.BitmapBits + (at - 4) * 8;
                uint bits = 0;
                for (var bit = 0; bit < 32; bit++)
                {
                    var block = key + (uint)bit;
                    if (block < g.BlockCount && (block < g.RootBlock || block >= end)) bits |= 1u << bit;
                }
                p.WriteUInt32(scratch, (int)at, bits);
            }
            Seal(ref p, scratch, 0);
            if (!p.WriteFormatBlock(first + map, scratch)) return false;
        }
        for (var extension = 0u; extension < extensions; extension++)
        {
            p.Clear(scratch, g.BlockBytes);
            for (var i = 0u; i < entries && 25 + extension * entries + i < maps; i++)
                p.WriteUInt32(scratch, (int)i * 4, first + 25 + extension * entries + i);
            if (extension + 1 < extensions) p.WriteUInt32(scratch, (int)g.BlockBytes - 4, first + maps + extension + 1);
            if (!p.WriteFormatBlock(first + maps + extension, scratch)) return false;
        }
        if (cache != 0)
        {
            p.Clear(scratch, g.BlockBytes); p.WriteUInt32(scratch, 0, 33);
            p.WriteUInt32(scratch, 4, cache); p.WriteUInt32(scratch, 8, g.RootBlock); Seal(ref p, scratch, 20);
            if (!p.WriteFormatBlock(cache, scratch)) return false;
        }
        p.Clear(scratch, g.BlockBytes); p.WriteUInt32(scratch, 0, 2); p.WriteUInt32(scratch, 12, g.TableEntries);
        // Kickstart stores the block after the root here on every variant;
        // only DOS4/5 use it (as the directory cache).
        p.WriteUInt32(scratch, g.Tail + 504, g.RootBlock + 1);
        p.WriteUInt32(scratch, g.Tail + 312, uint.MaxValue);
        for (var i = 0u; i < maps && i < 25; i++) p.WriteUInt32(scratch, g.Tail + 316 + (int)i * 4, first + i);
        if (extensions != 0) p.WriteUInt32(scratch, g.Tail + 416, first + maps);
        p.WriteUInt8(scratch, g.Tail + 432, (byte)length);
        for (var i = 0u; i < length; i++) p.WriteUInt8(scratch, g.Tail + 433 + (int)i, p.ReadUInt8(name, (int)i));
        for (var stamp = 0; stamp < 3; stamp++)
        {
            var at = g.Tail + (stamp == 0 ? 420 : stamp == 1 ? 472 : 484);
            p.WriteUInt32(scratch, at, days); p.WriteUInt32(scratch, at + 4, minutes); p.WriteUInt32(scratch, at + 8, ticks);
        }
        p.WriteUInt32(scratch, (int)g.BlockBytes - 4, 1); Seal(ref p, scratch, 20);
        if (!p.WriteFormatBlock(g.RootBlock, scratch) || !p.Flush()) return false;
        // Kickstart's Format writes only the DOS type into the boot block.
        p.Clear(scratch, g.BlockBytes); p.WriteUInt32(scratch, 0, dosType);
        if (!p.WriteFormatBlock(0, scratch) || !p.Flush()) return false;
        error = DOS.Error.None; return true;
    }
    public static bool CanFormat(AmigaVolumeGeometry g, uint dosType)
    {
        if (dosType is < 0x444F5300 or > 0x444F5305 ||
            !AmigaVolumeGeometry.Create(g.BlockCount, g.BlockBytes, g.ReservedBlocks, out _)) return false;
        var maps = (g.BlockCount - g.ReservedBlocks - 1) / g.BitmapBits + 1;
        var extensions = maps <= 25 ? 0 : (maps - 26) / (g.BlockBytes / 4 - 1) + 1;
        var first = g.RootBlock + 1;
        var extra = dosType >= 0x444F5304 ? 1u : 0u;
        return first < g.BlockCount && maps < g.BlockCount - first && extensions + extra < g.BlockCount - first - maps;
    }

    private static void Seal<T>(ref T p, APTR scratch, int checksum) where T : struct, IAmigaFileSystemPlatform
    {
        uint sum = 0;
        for (var at = 0u; at < p.BlockBytes; at += 4) sum = unchecked(sum + p.ReadUInt32(scratch, (int)at));
        p.WriteUInt32(scratch, checksum, unchecked(0u - sum));
    }
}
