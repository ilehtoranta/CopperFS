using Amiga;

namespace CopperStart.Dos;

// Private RAM owned by one mount. Zero status is the stateless path. During a
// mutation the old free count is unpublished until the valid root write succeeds.
public static class AmigaFfsMountIndex
{
    internal const int Status = 0, Bytes = 4, Used = 8, Maps = 12, Keys = 16,
        Sorted = 20, Chain = 24, Capacity = 28, Header = 32, FileBytes = 36,
        FileBlocks = 40, Tables = 44, Stage = 48, StageBytes = 52, Type = 56, RootImage = 60;
    internal const uint Clean = 1, Updating = 2, GroupOpen = 3;
    internal const int PendingHeader = 64, HeaderImage = 68;
    private const int Directory = 72;

    internal static APTR DataKeys<T>(ref T p, APTR memory) where T : struct, IAmigaFileSystemPlatform
        => APTR.FromPointer(p.ReadUInt32(memory, Chain));

    internal static APTR TableKeys<T>(ref T p, APTR memory) where T : struct, IAmigaFileSystemPlatform
        => APTR.FromPointer(p.ReadUInt32(memory, Chain) + p.ReadUInt32(memory, Capacity) * 4);

    internal static bool FitsFile<T>(ref T p, APTR memory, uint blocks, uint entries)
        where T : struct, IAmigaFileSystemPlatform
    {
        var tables = blocks == 0 ? 1 : (blocks - 1) / entries + 1;
        var capacity = p.ReadUInt32(memory, Capacity);
        return blocks <= capacity && tables <= capacity - blocks;
    }

    internal static bool Matches<T>(ref T p, APTR memory, uint header, uint size)
        where T : struct, IAmigaFileSystemPlatform => memory.IsNotNull &&
        p.ReadUInt32(memory, Header) == header && p.ReadUInt32(memory, FileBytes) == size &&
        (p.ReadUInt32(memory, Status) == Clean || p.ReadUInt32(memory, Status) == GroupOpen);

    internal static bool SealFile<T>(ref T p, APTR memory, uint header, uint size, uint blocks, uint tables)
        where T : struct, IAmigaFileSystemPlatform
    {
        // Dense volumes can check identity in linear time. The allocated
        // scratch also covers sparse layouts through the sorted fallback.
        var tableKeys = TableKeys(ref p, memory);
        var seen = APTR.FromPointer(tableKeys.Raw + tables * 4);
        var dataKeys = DataKeys(ref p, memory);
        var words = (p.BlockCount - 1) / 32 + 1;
        var scratchWords = p.ReadUInt32(memory, Capacity) - tables;
        if (words <= scratchWords && words / 16 <= blocks + tables)
        {
            p.Clear(seen, words * 4);
            if (!MarkDistinct(ref p, seen, dataKeys, blocks) || !MarkDistinct(ref p, seen, tableKeys, tables)) return false;
        }
        else
        {
            p.Copy(dataKeys, seen, blocks * 4);
            p.Copy(tableKeys, APTR.FromPointer(seen.Raw + blocks * 4), tables * 4);
            if (!Unique(ref p, seen, blocks + tables)) return false;
        }
        p.WriteUInt32(memory, FileBytes, size); p.WriteUInt32(memory, FileBlocks, blocks);
        p.WriteUInt32(memory, Tables, tables); p.WriteUInt32(memory, Header, header);
        return true;
    }

    private static bool MarkDistinct<T>(ref T p, APTR seen, APTR keys, uint count) where T : struct, IAmigaFileSystemPlatform
    {
        for (var i = 0u; i < count; i++)
        {
            var key = p.ReadUInt32(keys, (int)i * 4);
            var offset = (int)(key >> 5) * 4;
            var mask = 1u << (int)(key & 31);
            var word = p.ReadUInt32(seen, offset);
            if ((word & mask) != 0) return false;
            p.WriteUInt32(seen, offset, word | mask);
        }
        return true;
    }

    public static void Initialize<T>(ref T p, APTR memory, uint bytes) where T : struct, IAmigaFileSystemPlatform
    {
        if (memory.IsNull || bytes < Directory || bytes > int.MaxValue || !p.IsMapped(memory, bytes)) return;
        p.Clear(memory, Directory);
        p.WriteUInt32(memory, Bytes, bytes);
    }

    public static void Forget<T>(ref T p, APTR memory) where T : struct, IAmigaFileSystemPlatform
    {
        if (memory.IsNull) return;
        p.WriteUInt32(memory, Status, 0);
        p.WriteUInt32(memory, Header, 0);
    }

    internal static uint Map<T>(ref T p, APTR memory, uint index) where T : struct, IAmigaFileSystemPlatform
        => p.ReadUInt32(memory, Directory + (int)index * 4);

    // The sorted copy supports both exact duplicate detection and intersection
    // tests for arbitrary hints without scanning the pointer chain again.
    internal static bool Intersects<T>(ref T p, APTR memory, uint first, uint count) where T : struct, IAmigaFileSystemPlatform
    {
        var entries = p.ReadUInt32(memory, Keys);
        var start = APTR.FromPointer(p.ReadUInt32(memory, Sorted));
        uint low = 0, high = entries;
        while (low < high)
        {
            var mid = low + ((high - low) >> 1);
            if (p.ReadUInt32(start, (int)mid * 4) < first) low = mid + 1;
            else high = mid;
        }
        return low < entries && p.ReadUInt32(start, (int)low * 4) - first < count;
    }

    // In-place heapsort bounds cold preflight even for hostile pointer order.
    private static void Sift<T>(ref T p, APTR list, uint root, uint count) where T : struct, IAmigaFileSystemPlatform
    {
        var value = p.ReadUInt32(list, (int)root * 4);
        while (root < count / 2)
        {
            var child = root * 2 + 1;
            var largest = p.ReadUInt32(list, (int)child * 4);
            if (child + 1 < count && p.ReadUInt32(list, (int)(child + 1) * 4) > largest)
                largest = p.ReadUInt32(list, (int)++child * 4);
            if (value >= largest) break;
            p.WriteUInt32(list, (int)root * 4, largest);
            root = child;
        }
        p.WriteUInt32(list, (int)root * 4, value);
    }

    internal static bool Unique<T>(ref T p, APTR list, uint count) where T : struct, IAmigaFileSystemPlatform
    {
        for (var n = count / 2; n != 0;) Sift(ref p, list, --n, count);
        for (var end = count; end > 1;)
        {
            var last = p.ReadUInt32(list, (int)--end * 4);
            p.WriteUInt32(list, (int)end * 4, p.ReadUInt32(list));
            p.WriteUInt32(list, 0, last);
            Sift(ref p, list, 0, end);
        }
        for (var n = 1u; n < count; n++)
            if (p.ReadUInt32(list, (int)n * 4) == p.ReadUInt32(list, (int)(n - 1) * 4)) return false;
        return true;
    }

    // Called with a verified, clean root in meta. A volume too large for this
    // workspace keeps its original scratch-based validation and allocation.
    internal static bool Build<T>(ref T p, APTR memory, APTR meta, APTR bitmap,
        in AmigaFfsGeometry g, uint type) where T : struct, IAmigaFileSystemPlatform
    {
        Forget(ref p, memory);
        var bytes = p.ReadUInt32(memory, Bytes);
        var maps = (g.BlockCount - g.ReservedBlocks - 1) / g.BitmapBits + 1;
        var extensionSlots = g.BlockBytes / 4 - 1;
        var extensions = maps <= 25 ? 0 : (maps - 26) / extensionSlots + 1;
        var keys = maps + extensions + 1;
        if (bytes < Directory || keys > (bytes - Directory) / 8) return true;
        var available = bytes - Directory - keys * 8;
        var rootImage = APTR.Null;
        if (available >= g.BlockBytes)
        {
            available -= g.BlockBytes;
            rootImage = APTR.FromPointer(memory.Raw + bytes - g.BlockBytes);
            p.Copy(meta, rootImage, g.BlockBytes);
        }
        var headerImage = APTR.Null;
        if (available >= g.BlockBytes)
        {
            available -= g.BlockBytes;
            headerImage = APTR.FromPointer(rootImage.Raw - g.BlockBytes);
        }
        p.WriteUInt32(memory, HeaderImage, headerImage.Raw);
        p.WriteUInt32(memory, PendingHeader, 0);
        var sorted = APTR.FromPointer(memory.Raw + Directory + keys * 4);
        p.WriteUInt32(sorted, (int)(keys - 1) * 4, g.RootBlock);
        uint slot = 0, slots = 25, used = g.ReservedBlocks, extension = 0;
        var offset = g.Tail + 316;
        var next = p.ReadUInt32(meta, g.Tail + 416);
        for (var i = 0u; i < maps; i++)
        {
            if (slot == slots)
            {
                if (next < g.ReservedBlocks || next >= g.BlockCount || !p.ReadBlock(next, meta)) return false;
                p.WriteUInt32(sorted, (int)(maps + extension++) * 4, next);
                next = p.ReadUInt32(meta, (int)g.BlockBytes - 4);
                slot = 0; slots = extensionSlots; offset = 0;
            }
            var map = p.ReadUInt32(meta, offset + (int)slot++ * 4);
            if (map < g.ReservedBlocks || map >= g.BlockCount || !p.ReadBlock(map, bitmap) ||
                !AmigaDiskFileSystemCore.Checksum(ref p, bitmap)) return false;
            p.WriteUInt32(memory, Directory + (int)i * 4, map);
            p.WriteUInt32(sorted, (int)i * 4, map);
            var remaining = g.BlockCount - g.ReservedBlocks - i * g.BitmapBits;
            used += AmigaDiskFileSystemCore.CountAllocated(ref p, bitmap, remaining < g.BitmapBits ? remaining : g.BitmapBits);
        }
        if (next != 0 || !Unique(ref p, sorted, keys)) return false;
        uint loaded = uint.MaxValue;
        for (var i = 0u; i < keys; i++)
        {
            var relative = p.ReadUInt32(sorted, (int)i * 4) - g.ReservedBlocks;
            var index = AmigaGeometryMath.DivRem(relative, g.BitmapBits, out var bit);
            if (index != loaded)
            {
                if (!p.ReadBlock(Map(ref p, memory, index), bitmap) || !AmigaDiskFileSystemCore.Checksum(ref p, bitmap)) return false;
                loaded = index;
            }
            if ((p.ReadUInt32(bitmap, 4 + (int)(bit >> 5) * 4) & 1u << (int)(bit & 31)) != 0) return false;
        }
        p.WriteUInt32(memory, RootImage, rootImage.Raw);
        var staging = available / 4;
        if (staging > 32768) staging = 32768;
        staging &= ~3u;
        p.WriteUInt32(memory, Type, type);
        p.WriteUInt32(memory, Used, used);
        p.WriteUInt32(memory, Maps, maps);
        p.WriteUInt32(memory, Keys, keys);
        p.WriteUInt32(memory, Sorted, sorted.Raw);
        p.WriteUInt32(memory, Chain, sorted.Raw + keys * 4);
        // Three equally bounded regions hold data keys, table keys and the
        // temporary exact uniqueness set. The last two share their boundary.
        p.WriteUInt32(memory, Capacity, (available - staging) / 12);
        p.WriteUInt32(memory, Stage, memory.Raw + Directory + keys * 8 + available - staging);
        p.WriteUInt32(memory, StageBytes, staging);
        p.WriteUInt32(memory, Header, 0);
        p.WriteUInt32(memory, Status, Clean);
        return true;
    }
}
