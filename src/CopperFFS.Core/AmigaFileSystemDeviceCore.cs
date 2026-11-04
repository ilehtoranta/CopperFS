using Amiga;

namespace CopperStart.Dos;

public interface IAmigaFileSystemDevicePlatform
{
    bool Transfer(ushort command, ulong byteOffset, uint bytes, APTR buffer);
}

/// <summary>Translate partition keys to device offsets; split transfers without wrapping at 4 GiB.</summary>
public static class AmigaFileSystemDeviceCore
{
    public static bool Transfer<T>(ref T p, AmigaVolumeGeometry geometry, bool write,
        uint block, uint blocks, APTR buffer) where T : struct, IAmigaFileSystemDevicePlatform
    {
        // The common single-sector request needs neither a 64-bit multiply nor
        // MaxTransfer division. Other block sizes, split requests and TD64
        // boundaries retain the general path below.
        if (blocks == 1 && geometry.BlockBytes == 512 && geometry.SectorBytes == 512 &&
            block < geometry.BlockCount && geometry.MaxTransfer >= 512 &&
            geometry.StartByte <= uint.MaxValue - 511u &&
            block <= (uint.MaxValue - 511u - (uint)geometry.StartByte) >> 9 &&
            buffer.Raw <= uint.MaxValue - 512u)
        {
            var sectorOffset = (uint)geometry.StartByte + (block << 9);
            return p.Transfer((ushort)(write ? 3 : 2), sectorOffset, 512, buffer);
        }
        if (blocks == 0 || blocks > geometry.BlockCount || block > geometry.BlockCount - blocks ||
            !geometry.DeviceRange(block, out var offset)) return false;
        var remaining = (ulong)blocks * geometry.BlockBytes;
        var sectorBytes = geometry.SectorBytes;
        if (sectorBytes == 0) return false;
        var maximum = geometry.MaxTransfer - AmigaGeometryMath.Remainder(geometry.MaxTransfer, sectorBytes);
        if (maximum == 0 || remaining > uint.MaxValue || buffer.Raw > uint.MaxValue - remaining ||
            remaining > ulong.MaxValue - offset) return false;
        var cursor = buffer;
        while (remaining != 0)
        {
            var count = remaining > maximum ? maximum : (uint)remaining;
            // An ordinary TD request cannot straddle the 32-bit offset boundary.
            var extended = offset > uint.MaxValue || count > 0x100000000UL - offset;
            if (!p.Transfer((ushort)(extended ? write ? 25 : 24 : write ? 3 : 2), offset, count, cursor)) return false;
            offset += count; remaining -= count; cursor = APTR.FromPointer(cursor.Raw + count);
        }
        return true;
    }
}
