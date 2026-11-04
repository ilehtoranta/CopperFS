using Amiga;

namespace CopperStart.Dos;

/// <summary>
/// Read-only OFS/FFS metadata and file traversal. All buffers belong
/// to the caller; no managed objects, host media access, or persistent globals.
/// </summary>
public static class AmigaDiskFileSystemCore
{

    public static bool Checksum<T>(ref T p, APTR buffer) where T : struct, IAmigaFileSystemPlatform
    {
        var blockBytes = p.BlockBytes;
        uint sum = 0;
        for (var i = 0; i < blockBytes; i += 4) sum = unchecked(sum + p.ReadUInt32(buffer, i));
        return sum == 0;
    }

    public static bool Header<T>(ref T p, uint block, APTR buffer) where T : struct, IAmigaFileSystemPlatform
    {
        var geometry = p.Geometry;
        return Header(ref p, ref geometry, block, buffer, false);
    }

    internal static bool Header<T>(ref T p, ref AmigaVolumeGeometry geometry, uint block, APTR buffer, bool links)
        where T : struct, IAmigaFileSystemPlatform
    {
        if (block < geometry.ReservedBlocks || block >= geometry.BlockCount || !p.ReadBlock(block, buffer) ||
            !Checksum(ref p, buffer) || p.ReadUInt32(buffer) != 2) return false;
        var type = unchecked((int)p.ReadUInt32(buffer, geometry.Tail + 508));
        return block == geometry.RootBlock ? type == 1 && p.ReadUInt32(buffer, 12) == geometry.TableEntries :
            p.ReadUInt32(buffer, 4) == block && (type == 2 || type == -3 || (links && type is 3 or 4 or -4));
    }

    public static bool Mount<T>(ref T p, APTR buffer, out uint dosType) where T : struct, IAmigaFileSystemPlatform
    {
        var geometry = p.Geometry;
        dosType = 0;
        if (!p.ReadBlock(0, buffer)) return false;
        dosType = p.ReadUInt32(buffer);
        // Directory-cache disks use the ordinary hash table for read access.
        if (dosType < 0x444F5300 || dosType > 0x444F5305) return false;
        return Header(ref p, geometry.RootBlock, buffer) && p.ReadUInt8(buffer, geometry.Tail + 432) is > 0 and <= 30;
    }

    private static byte Upper(byte c, uint type) => c is >= 97 and <= 122 ||
        (type >= 0x444F5302 && c >= 224 && c != 247) ? unchecked((byte)(c - 32)) : c;

    public static uint Resolve<T>(ref T p, uint directory, APTR name, uint length,
        uint dosType, APTR buffer, out DOS.Error error) where T : struct, IAmigaFileSystemPlatform
    {
        var geometry = p.Geometry;
        error = DOS.Error.None;
        if (length > 255) { error = DOS.Error.InvalidComponentName; return 0; }
        var tableEntries = geometry.TableEntries;
        if (tableEntries == 0) { error = DOS.Error.DiskNotValidated; return 0; }
        var offset = 0u;
        for (var i = 0u; i < length; i++)
            if (p.ReadUInt8(name, (int)i) == ':') { directory = geometry.RootBlock; offset = i + 1; }
        for (var depth = 0; depth <= 255; depth++)
        {
            if (!Header(ref p, directory, buffer)) { error = DOS.Error.DiskNotValidated; return 0; }
            if (offset == length) return directory;
            if (unchecked((int)p.ReadUInt32(buffer, geometry.Tail + 508)) < 0) { error = DOS.Error.ObjectWrongType; return 0; }
            if (p.ReadUInt8(name, (int)offset) == '/')
            {
                directory = directory == geometry.RootBlock ? geometry.RootBlock : p.ReadUInt32(buffer, geometry.Tail + 500);
                offset++;
                continue;
            }
            var start = offset;
            while (offset < length && p.ReadUInt8(name, (int)offset) != '/') offset++;
            var count = offset - start;
            if (count == 0 || count > 30) { error = DOS.Error.InvalidComponentName; return 0; }
            var hash = count;
            for (var i = 0u; i < count; i++) hash = (hash * 13 + Upper(p.ReadUInt8(name, (int)(start + i)), dosType)) & 0x7FF;
            var entry = p.ReadUInt32(buffer, 24 + (int)(AmigaGeometryMath.Remainder(hash, tableEntries)) * 4);
            var found = false;
            for (var guard = 0u; entry != 0 && guard < geometry.BlockCount; guard++)
            {
                if (!Header(ref p, entry, buffer) || p.ReadUInt32(buffer, geometry.Tail + 500) != directory)
                { error = DOS.Error.DiskNotValidated; return 0; }
                var matches = p.ReadUInt8(buffer, geometry.Tail + 432) == count;
                for (var i = 0u; matches && i < count; i++) matches =
                    Upper(p.ReadUInt8(buffer, geometry.Tail + 433 + (int)i), dosType) == Upper(p.ReadUInt8(name, (int)(start + i)), dosType);
                if (matches) { found = true; break; }
                entry = p.ReadUInt32(buffer, geometry.Tail + 496);
            }
            if (!found) { error = entry != 0 ? DOS.Error.DiskNotValidated : DOS.Error.ObjectNotFound; return 0; }
            directory = entry;
            if (offset < length) offset++; // separator; a second slash means parent
        }
        error = DOS.Error.DiskNotValidated;
        return 0;
    }

    public static int Read<T>(ref T p, uint header, uint dosType, uint position,
        APTR destination, uint length, APTR metadata, APTR data, out DOS.Error error)
        where T : struct, IAmigaFileSystemPlatform
    {
        var geometry = p.Geometry;
        error = DOS.Error.None;
        if (length > int.MaxValue || !p.IsMapped(destination, length) ||
            !Header(ref p, header, metadata) || unchecked((int)p.ReadUInt32(metadata, geometry.Tail + 508)) != -3)
        { error = DOS.Error.DiskNotValidated; return -1; }
        var size = p.ReadUInt32(metadata, geometry.Tail + 324);
        if (position > size) { error = DOS.Error.SeekError; return -1; }
        if (length > size - position) length = size - position;
        var payload = (dosType & 1) != 0 ? geometry.BlockBytes : geometry.BlockBytes - 24;
        var tableEntries = geometry.TableEntries;
        if (payload == 0 || tableEntries == 0) { error = DOS.Error.DiskNotValidated; return -1; }
        var copied = 0u;
        var table = header;
        var tableIndex = 0u;
        while (copied < length)
        {
            var index = AmigaGeometryMath.Divide(position, payload);
            var extensions = AmigaGeometryMath.Divide(index, tableEntries);
            if (extensions >= geometry.BlockCount) break;
            var valid = true;
            // metadata retains the current pointer table while data sectors
            // use the separate data buffer. Restarting at the file header on
            // every sector makes a large sequential read seek back and forth
            // through every preceding extension table.
            for (; tableIndex < extensions; tableIndex++)
            {
                table = p.ReadUInt32(metadata, geometry.Tail + 504);
                if (table < geometry.ReservedBlocks || table >= geometry.BlockCount || !p.ReadBlock(table, metadata) ||
                    !Checksum(ref p, metadata) || p.ReadUInt32(metadata) != 16 ||
                    p.ReadUInt32(metadata, 4) != table || p.ReadUInt32(metadata, geometry.Tail + 500) != header ||
                    unchecked((int)p.ReadUInt32(metadata, geometry.Tail + 508)) != -3) { valid = false; break; }
            }
            if (!valid || p.ReadUInt32(metadata, 8) > tableEntries || AmigaGeometryMath.Remainder(index, tableEntries) >= p.ReadUInt32(metadata, 8)) break;
            var block = p.ReadUInt32(metadata, geometry.Tail + 308 - (int)(AmigaGeometryMath.Remainder(index, tableEntries)) * 4);
            if (block < geometry.ReservedBlocks || block >= geometry.BlockCount || !p.ReadBlock(block, data)) break;
            var within = AmigaGeometryMath.Remainder(position, payload);
            var count = payload - within;
            if (count > length - copied) count = length - copied;
            var dataOffset = 0u;
            if ((dosType & 1) == 0)
            {
                if (!Checksum(ref p, data) || p.ReadUInt32(data) != 8 ||
                    p.ReadUInt32(data, 4) != header || p.ReadUInt32(data, 8) != index + 1 ||
                    p.ReadUInt32(data, 12) > geometry.BlockBytes - 24 || p.ReadUInt32(data, 12) < within + count) break;
                dataOffset = 24;
            }
            var source = APTR.FromPointer(data.Raw + dataOffset + within);
            var target = APTR.FromPointer(destination.Raw + copied);
            var distance = source.Raw > target.Raw ? source.Raw - target.Raw : target.Raw - source.Raw;
            if (distance >= count && count <= uint.MaxValue - source.Raw && count <= uint.MaxValue - target.Raw)
                p.Copy(source, target, count);
            else
                for (var i = 0u; i < count; i++) p.WriteUInt8(destination, (int)(copied + i), p.ReadUInt8(data, (int)(dataOffset + within + i)));
            copied += count;
            position += count;
        }
        if (copied != length) { error = DOS.Error.DiskNotValidated; return copied == 0 ? -1 : (int)copied; }
        return (int)copied;
    }

    public static bool Examine<T>(ref T p, uint key, APTR fib, APTR buffer) where T : struct, IAmigaFileSystemPlatform
    {
        if (!p.IsMapped(fib, FileInfoBlock.SizeInBytes) || !Header(ref p, key, buffer)) return false;
        return ExamineHeader(ref p, key, fib, buffer);
    }

    // The serial handler has already validated this header. Do not read and
    // checksum the same block again merely to project it into a FIB.
    private static bool ExamineHeader<T>(ref T p, uint key, APTR fib, APTR buffer) where T : struct, IAmigaFileSystemPlatform
    {
        var geometry = p.Geometry;
        var blockBytes = geometry.BlockBytes;
        if (blockBytes == 0) return false;
        p.Clear(fib, FileInfoBlock.SizeInBytes);
        var type = p.ReadUInt32(buffer, geometry.Tail + 508);
        p.WriteUInt32(fib, 4, type);
        p.WriteUInt32(fib, 120, type);
        var nameLength = p.ReadUInt8(buffer, geometry.Tail + 432);
        if (nameLength > 30) return false;
        // Packet FIB names use the SDK's length-prefixed representation.
        // Native DOS name/path reconstruction consumes this packet record.
        p.WriteUInt8(fib, 8, nameLength);
        for (var i = 0; i < nameLength; i++) p.WriteUInt8(fib, 9 + i, p.ReadUInt8(buffer, geometry.Tail + 433 + i));
        p.WriteUInt32(fib, 116, key == geometry.RootBlock ? 0 : p.ReadUInt32(buffer, geometry.Tail + 320));
        var size = unchecked((int)type) < 0 ? p.ReadUInt32(buffer, geometry.Tail + 324) : 0;
        p.WriteUInt32(fib, 124, size);
        p.WriteUInt32(fib, 128, AmigaGeometryMath.Divide(size, blockBytes) + (AmigaGeometryMath.Remainder(size, blockBytes) == 0 ? 0u : 1u));
        for (var i = 0; i < 12; i += 4) p.WriteUInt32(fib, 132 + i, p.ReadUInt32(buffer, geometry.Tail + 420 + i));
        if (key != geometry.RootBlock)
        {
            var commentLength = p.ReadUInt8(buffer, geometry.Tail + 328);
            if (commentLength > 79) return false;
            p.WriteUInt8(fib, 144, commentLength);
            for (var i = 0; i < commentLength; i++) p.WriteUInt8(fib, 145 + i, p.ReadUInt8(buffer, geometry.Tail + 329 + i));
        }
        return true;
    }

    // Private acceleration only: the public disk key remains the ordinal.
    // A different/interleaved cursor or an invalidated mount record can always
    // rebuild the position from that ordinal without extra caller requirements.
    public struct DirectoryCursor
    {
        public uint Directory, Ordinal, Slot, Entry, Visited;
    }

    public static DOS.Error ExamineNext<T>(ref T p, uint directory, APTR fib, APTR metadata, APTR table)
        where T : struct, IAmigaFileSystemPlatform
    {
        var cursor = new DirectoryCursor();
        return ExamineNext(ref p, ref cursor, directory, fib, metadata, table);
    }

    public static DOS.Error ExamineNext<T>(ref T p, ref DirectoryCursor cursor,
        uint directory, APTR fib, APTR metadata, APTR table)
        where T : struct, IAmigaFileSystemPlatform
    {
        var geometry = p.Geometry;
        if (!p.IsMapped(fib, FileInfoBlock.SizeInBytes) || !Header(ref p, directory, table)) return DOS.Error.InvalidLock;
        if (unchecked((int)p.ReadUInt32(table, geometry.Tail + 508)) < 0) return DOS.Error.ObjectWrongType;
        var wanted = p.ReadUInt32(fib);
        if (wanted >= geometry.BlockCount) return DOS.Error.NoMoreEntries;
        var next = cursor;
        if (wanted == 0 || next.Directory != directory || next.Ordinal != wanted)
            next = new DirectoryCursor { Directory = directory };
        for (; next.Slot < geometry.TableEntries; next.Slot++)
        {
            var entry = next.Entry;
            if (entry == 0) entry = p.ReadUInt32(table, 24 + (int)next.Slot * 4);
            while (entry != 0)
            {
                if (next.Visited >= geometry.BlockCount || !Header(ref p, entry, metadata) ||
                    p.ReadUInt32(metadata, geometry.Tail + 500) != directory) return DOS.Error.DiskNotValidated;
                next.Visited++;
                var successor = p.ReadUInt32(metadata, geometry.Tail + 496);
                if (next.Ordinal == wanted)
                {
                    if (!ExamineHeader(ref p, entry, fib, metadata)) return DOS.Error.DiskNotValidated;
                    next.Ordinal++;
                    if (successor == 0) { next.Slot++; next.Entry = 0; }
                    else next.Entry = successor;
                    p.WriteUInt32(fib, 0, next.Ordinal);
                    cursor = next;
                    return DOS.Error.None;
                }
                next.Ordinal++; entry = successor;
            }
            next.Entry = 0;
        }
        return DOS.Error.NoMoreEntries;
    }
    public static bool BitmapBlock<T>(ref T p, uint index, APTR scratch, out uint block)
        where T : struct, IAmigaFileSystemPlatform
    {
        var cursor = new BitmapCursor();
        return BitmapBlock(ref p, ref cursor, index, scratch, out block);
    }

    // The caller retains scratch between steps. Only the root and each extension
    // are read once, irrespective of how many bitmap pointers they contain.
    public struct BitmapCursor
    {
        public uint Next, Visited, First, Count;
        public int Offset;
    }
    public static bool BitmapBlock<T>(ref T p, ref BitmapCursor cursor, uint index, APTR scratch, out uint block)
        where T : struct, IAmigaFileSystemPlatform
    {
        var geometry = p.Geometry;
        block = 0;
        if (cursor.Count == 0 || index < cursor.First)
        {
            if (!Header(ref p, geometry.RootBlock, scratch)) return false;
            cursor.Offset = geometry.Tail + 316;
            cursor.First = 0; cursor.Count = 25; cursor.Visited = 0;
            cursor.Next = p.ReadUInt32(scratch, geometry.Tail + 416);
        }
        while (index - cursor.First >= cursor.Count)
        {
            if (cursor.Visited++ >= geometry.BlockCount || cursor.Next < geometry.ReservedBlocks ||
                cursor.Next >= geometry.BlockCount || !p.ReadBlock(cursor.Next, scratch)) return false;
            cursor.Offset = 0;
            cursor.First += cursor.Count;
            cursor.Count = geometry.BlockBytes / 4 - 1;
            cursor.Next = p.ReadUInt32(scratch, (int)geometry.BlockBytes - 4);
        }
        block = p.ReadUInt32(scratch, cursor.Offset + (int)(index - cursor.First) * 4);
        return block >= geometry.ReservedBlocks && block < geometry.BlockCount && block != geometry.RootBlock;
    }

    public static bool CountUsed<T>(ref T p, APTR scratch, APTR bitmap, out uint used)
        where T : struct, IAmigaFileSystemPlatform
    {
        var geometry = p.Geometry;
        used = geometry.ReservedBlocks;
        var key = geometry.ReservedBlocks;
        var cursor = new BitmapCursor();
        for (var index = 0u; key < geometry.BlockCount; index++)
        {
            if (!BitmapBlock(ref p, ref cursor, index, scratch, out var block) || !p.ReadBlock(block, bitmap) || !Checksum(ref p, bitmap)) return false;
            var count = geometry.BlockCount - key;
            if (count > geometry.BitmapBits) count = geometry.BitmapBits;
            for (var bit = 0u; bit < count; bit += 32)
            {
                var bits = count - bit;
                if (bits > 32) bits = 32;
                var free = p.ReadUInt32(bitmap, 4 + (int)(bit / 32) * 4);
                if (free == uint.MaxValue) continue;
                if (free == 0) { used += bits; continue; }
                if (bits != 32) free &= (1u << (int)bits) - 1;
                // Count each bitmap word once, including only valid bits in the final map.
                used += bits - AmigaBitmapWordCore.Population(free);
            }
            key += count;
        }
        return true;
    }

}
