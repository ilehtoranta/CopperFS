using Amiga;

namespace CopperSFS;

/// <summary>Explicit reserved-block writes, available only to volume initialization.</summary>
public interface IAmigaFormatFileSystemPlatform : IAmigaWritableFileSystemPlatform
{
    bool WriteFormatBlock(uint block, APTR source);
}

/// <summary>Quick format of classic SFS and SFS2 using one caller-owned block.</summary>
public static class AmigaSmartFileSystemFormatCore
{
    public static bool Format<T>(ref T p, uint dosType, APTR name, uint length,
        uint date, APTR scratch, out DOS.Error error, uint flags = uint.MaxValue) where T : struct, IAmigaFormatFileSystemPlatform
    {
        error = DOS.Error.BadNumber;
        var g = p.Geometry;
        var sfs2 = dosType == 0x53465302;
        if (flags == uint.MaxValue) flags = sfs2 ? 0u : 64u;
        if ((flags & ~193u) != 0 || (sfs2 && (flags & 64) != 0)) return false;
        var recycled = (flags & 64) != 0;
        ushort recycleHash = 9;
        for (var i = 0; i < 9; i++)
        {
            var letter = ".recycled"[i]; if ((flags & 128) == 0 && letter >= 'a' && letter <= 'z') letter = (char)(letter - 32);
            recycleHash = unchecked((ushort)(recycleHash * 13 + letter));
        }
        if (!CanFormat(g, dosType) || !p.IsMapped(scratch, g.BlockBytes)) return false;
        if (!ValidName(ref p, name, length)) { error = DOS.Error.InvalidComponentName; return false; }
        if (p.WriteProtected()) { error = DOS.Error.DiskWriteProtected; return false; }
        var mapBits = (g.BlockBytes - 12) * 8;
        var maps = (g.BlockCount - 1) / mapBits + 1;
        var admin = g.ReservedBlocks;
        var root = admin + 1;
        var bitmap = admin + 32;
        error = DOS.Error.DiskNotValidated;
        // Remove both old roots before publishing a new filesystem generation.
        p.Clear(scratch, g.BlockBytes);
        if (!p.WriteFormatBlock(0, scratch) || !p.WriteFormatBlock(g.BlockCount - 1, scratch) || !p.Flush()) return false;
        Header(ref p, scratch, admin, 0x41444D43);
        p.WriteUInt8(scratch, 20, 32); p.WriteUInt32(scratch, 24, admin);
        p.WriteUInt32(scratch, 28, recycled ? 0xFE000000u : 0xFC000000u); // admin, root, hash, marker, extent, nodes
        if (!Put(ref p, scratch, admin, sfs2)) return false;
        Header(ref p, scratch, root, 0x4F424A43);
        p.WriteUInt32(scratch, 28, 1); p.WriteUInt32(scratch, 32, 15);
        p.WriteUInt32(scratch, 36, root + 1);
        if (recycled) p.WriteUInt32(scratch, 40, root + 5);
        var extra = sfs2 ? 2 : 0;
        p.WriteUInt32(scratch, 44 + extra, date); p.WriteUInt8(scratch, 48 + extra, 128);
        for (var i = 0u; i < length; i++) p.WriteUInt8(scratch, 49 + extra + (int)i, p.ReadUInt8(name, (int)i));
        p.WriteUInt32(scratch, (int)g.BlockBytes - 28, g.BlockCount - bitmap - maps - 1);
        p.WriteUInt32(scratch, (int)g.BlockBytes - 24, date);
        if (!Put(ref p, scratch, root, sfs2)) return false;
        Header(ref p, scratch, root + 1, 0x48544142); p.WriteUInt32(scratch, 12, 1);
        if (recycled) p.WriteUInt32(scratch, 16 + (int)(recycleHash % ((g.BlockBytes - 16) / 4)) * 4, 2);
        if (!Put(ref p, scratch, root + 1, sfs2)) return false;
        Header(ref p, scratch, root + 2, 0x54524F4B);
        if (!Put(ref p, scratch, root + 2, sfs2)) return false;
        Header(ref p, scratch, root + 3, 0x424E4443);
        p.WriteUInt8(scratch, 14, 1); p.WriteUInt8(scratch, 15, (byte)(sfs2 ? 16 : 14));
        if (!Put(ref p, scratch, root + 3, sfs2)) return false;
        Header(ref p, scratch, root + 4, 0x4E444320);
        p.WriteUInt32(scratch, 12, 1); p.WriteUInt32(scratch, 16, 1); p.WriteUInt32(scratch, 20, root);
        for (var n = 1; n < 6; n++) p.WriteUInt32(scratch, 20 + n * 10, uint.MaxValue);
        if (recycled) { p.WriteUInt32(scratch, 30, root + 5); p.WriteUInt16(scratch, 38, recycleHash); }
        if (!Put(ref p, scratch, root + 4, sfs2)) return false;
        if (recycled)
        {
            Header(ref p, scratch, root + 5, 0x4F424A43); p.WriteUInt32(scratch, 12, 1);
            p.WriteUInt32(scratch, 28, 2); p.WriteUInt32(scratch, 32, 10);
            p.WriteUInt32(scratch, 44, date); p.WriteUInt8(scratch, 48, (byte)(134 | ((flags & 1) == 0 ? 1 : 0)));
            for (var i = 0; i < 9; i++) p.WriteUInt8(scratch, 49 + i, (byte)".recycled"[i]);
            if (!Put(ref p, scratch, root + 5, false)) return false;
        }
        for (var map = 0u; map < maps; map++)
        {
            Header(ref p, scratch, bitmap + map, 0x42544D50);
            for (var at = 12u; at < g.BlockBytes; at += 4)
            {
                var first = (ulong)map * mapBits + (at - 12) * 8;
                uint bits = 0;
                for (var bit = 0; bit < 32; bit++)
                {
                    var key = first + (uint)bit;
                    if (key >= bitmap + maps && key < g.BlockCount - 1) bits |= 0x80000000u >> bit;
                }
                p.WriteUInt32(scratch, (int)at, bits);
            }
            if (!Put(ref p, scratch, bitmap + map, sfs2)) return false;
        }
        if (!p.Flush()) return false;
        // Publish the secondary root first, then the primary. Neither can point
        // at metadata which has not crossed the device's flush boundary.
        for (var copy = 0; copy < 2; copy++)
        {
            var block = copy == 0 ? g.BlockCount - 1 : 0;
            Header(ref p, scratch, block, dosType);
            p.WriteUInt16(scratch, 12, (ushort)(sfs2 ? 4 : 3)); p.WriteUInt32(scratch, 16, date);
            p.WriteUInt8(scratch, 20, (byte)(flags & 192));
            p.WriteUInt32(scratch, 32, (uint)(g.StartByte >> 32)); p.WriteUInt32(scratch, 36, (uint)g.StartByte);
            var end = g.StartByte + (ulong)g.BlockCount * g.BlockBytes;
            p.WriteUInt32(scratch, 40, (uint)(end >> 32)); p.WriteUInt32(scratch, 44, (uint)end);
            p.WriteUInt32(scratch, 48, g.BlockCount); p.WriteUInt32(scratch, 52, g.BlockBytes);
            p.WriteUInt32(scratch, 96, bitmap); p.WriteUInt32(scratch, 100, admin);
            p.WriteUInt32(scratch, 104, root); p.WriteUInt32(scratch, 108, root + 3); p.WriteUInt32(scratch, 112, root + 4);
            if (!Put(ref p, scratch, block, sfs2) || !p.Flush()) return false;
        }
        error = DOS.Error.None; return true;
    }

    public static bool CanFormat(AmigaVolumeGeometry g, uint dosType)
    {
        if (dosType is not (0x53465300 or 0x53465302) ||
            !AmigaVolumeGeometry.Create(g.BlockCount, g.BlockBytes, g.ReservedBlocks, out _) ||
            g.BlockCount > 0x80000000 || g.ReservedBlocks > uint.MaxValue - 32) return false;
        var maps = (g.BlockCount - 1) / ((g.BlockBytes - 12) * 8) + 1;
        var bitmap = g.ReservedBlocks + 32;
        return maps < g.BlockCount && bitmap < g.BlockCount - maps - 1;
    }

    public static bool ValidName<T>(ref T p, APTR name, uint length) where T : struct, IAmigaGuestMemory
    {
        if (length == 0 || length > 30 || !p.IsMapped(name, length)) return false;
        for (var i = 0u; i < length; i++)
            if (p.ReadUInt8(name, (int)i) is 0 or (byte)':' or (byte)'/') return false;
        return true;
    }
    private static void Header<T>(ref T p, APTR scratch, uint block, uint id) where T : struct, IAmigaFileSystemPlatform
    { p.Clear(scratch, p.BlockBytes); p.WriteUInt32(scratch, 0, id); p.WriteUInt32(scratch, 8, block); }
    private static bool Put<T>(ref T p, APTR scratch, uint block, bool sfs2) where T : struct, IAmigaFormatFileSystemPlatform
    {
        uint sum = sfs2 ? 2u : 1u;
        for (var at = 0u; at < p.BlockBytes; at += 4) sum = unchecked(sum + p.ReadUInt32(scratch, (int)at));
        p.WriteUInt32(scratch, 4, unchecked(0u - sum));
        return p.WriteFormatBlock(block, scratch);
    }
}
