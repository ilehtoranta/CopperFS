using Amiga;

#if COPPERFS_NO_FFS_MOUNT_INDEX && COPPERFS_FFS_HANDLER
#error The native indexed FFS handler requires its mount index.
#endif

namespace CopperStart.Dos;

// Private RAM owned by one mount. Zero status is the stateless path. During a
// mutation the old free count is unpublished until the valid root write succeeds.
public static class AmigaFfsMountIndex
{
    // A scratch-only mount never takes ownership of an index. Expose that
    // choice to the compiler so it can discard private indexed paths.
#if COPPERFS_NO_FFS_MOUNT_INDEX
    internal const bool Enabled = false;
#else
    internal const bool Enabled = true;
#endif

    internal const int Status = 0, Bytes = 4, Used = 8, Maps = 12, Keys = 16,
        Sorted = 20, Chain = 24, Capacity = 28, Header = 32, FileBytes = 36,
        FileBlocks = 40, Tables = 44, Stage = 48, StageBytes = 52, Type = 56, RootImage = 60;
    internal const uint Clean = 1, Updating = 2, GroupOpen = 3, GroupFailed = 4, MetadataOpen = 5;
    // A volume too large for the index keeps a free-count summary instead.
    // Summarized: Used and NextCandidate describe the published bitmap, and
    // SummaryMap holds the root's 25 bitmap pointers, the extension count and
    // the extension keys. SummaryBusy: a mutation owns them until its valid
    // root write. Forget (any reset, inhibit or I/O failure) discards both.
    internal const uint Summarized = 6, SummaryBusy = 7;
    internal const uint SummaryRootPointers = 25;
    internal const int PendingHeader = 64, HeaderImage = 68, PendingBitmap = 72, BitmapImage = 76;
    // The clean mount can reuse the pending-bitmap sector for one verified OFS
    // payload. Begin expires its identity before the first grouped allocation.
    internal const int ReadHeader = 80, ReadStart = 84, ReadBytes = 88;
    internal const int PendingTable = 92, TableImage = 96;
    internal const int PendingParent = 100, ParentImage = 104;
    internal const int AppendImage = 108, AppendCapacity = 112, PendingAppend = 116;
    internal const int NextCandidate = 120;
    internal const int PreviousHeader = 124, PreviousHeaderImage = 128, PreviousTable = 132, PreviousTableImage = 136;
    internal const int Companion = 140, ParkedHeader = 144;
    private const int ParkedSize = 148, ParkedBlocks = 152, ParkedTables = 156,
        SpareData = 160, SpareTables = 164, ParkedAscending = 216,
        TableStorage = 220, SeenStorage = 224;
    internal const int PublishedHeader = 228, PublishedTable = 232;
    internal const int FreshHeaderImage = 236, FreshHeaderKey = 240, FreshTableImage = 244, FreshTableKey = 248;
    private const int ParkedRunLogical = 252, ParkedRunPhysical = 256, ParkedRunLength = 260;
    internal const int FreshHeaderPointerSum = 264, FreshTablePointerSum = 268;
    internal const int PrefixDirectory = 272, PrefixLength = 276, PrefixName = 280;
    internal const uint PrefixCapacity = 32;
    internal const int FirstExtension = PrefixName + (int)PrefixCapacity, FirstExtensionImage = FirstExtension + 4;
    internal const int PublishedParent = FirstExtensionImage + 4;
    internal const int SealedAttributeHeader = PublishedParent + 4;
    private const int RootChildren = SealedAttributeHeader + 4, Directory = RootChildren + 4;
    private const uint RootChildBytes = 64 * 8;
    internal const int AppendAdmitted = 168, BitmapPages = 172, PendingBitmapImage = 176;
    internal const int RunHeader = 180, RunLogical = 184, RunPhysical = 188, RunLength = 192;
    internal const int PublishedPreviousHeader = 196, PublishedPreviousTable = 200;
    internal const int AppendStorage = 204, AppendStorageBytes = 208;
    internal const int AscendingBlocks = 212;

    // Read-only root for the single mount owner between namespace packets.
    public static APTR NamespaceRoot<T>(ref T p, APTR index) where T : struct, IAmigaFileSystemPlatform
    {
        if (index.IsNull || p.ReadUInt32(index, Status) != MetadataOpen) return APTR.Null;
        return APTR.FromPointer(p.ReadUInt32(index, RootImage));
    }

    public static APTR NamespaceDirectory<T>(ref T p, APTR index, uint key) where T : struct, IAmigaFileSystemPlatform
    {
        if (key == 0 || index.IsNull || p.ReadUInt32(index, Status) != MetadataOpen ||
            p.ReadUInt32(index, PendingParent) != key) return APTR.Null;
        return APTR.FromPointer(p.ReadUInt32(index, ParentImage));
    }

    // Borrow a sealed sector until the next mutation on this mount.
    public static APTR PublishedMetadataImage<T>(ref T p, APTR index, uint key) where T : struct, IAmigaFileSystemPlatform
    {
        if (index.IsNull || key == 0) return APTR.Null;
        var status = p.ReadUInt32(index, Status);
        if (status == MetadataOpen && p.ReadUInt32(index, SealedAttributeHeader) == key &&
            p.ReadUInt32(index, PendingHeader) == key)
            return APTR.FromPointer(p.ReadUInt32(index, HeaderImage));
        if (status is not (Clean or MetadataOpen)) return APTR.Null;
        if (p.ReadUInt32(index, PublishedHeader) == key) return APTR.FromPointer(p.ReadUInt32(index, HeaderImage));
        if (p.ReadUInt32(index, PublishedTable) == key) return APTR.FromPointer(p.ReadUInt32(index, TableImage));
        if (p.ReadUInt32(index, PublishedParent) == key) return APTR.FromPointer(p.ReadUInt32(index, ParentImage));
        if (p.ReadUInt32(index, ParkedHeader) == key && p.ReadUInt32(index, PublishedPreviousHeader) == key)
            return APTR.FromPointer(p.ReadUInt32(index, PreviousHeaderImage));
        return APTR.Null;
    }

    // A file inserted into an empty root bucket has no hash successor. Its
    // checked name length can rule out a later collision without a disk read.
    // Different buckets sharing a cache slot are distinguished by the key.
    internal static bool SoleRootChildDiffers<T>(ref T p, APTR index, int slot, uint key, uint length)
        where T : struct, IAmigaFileSystemPlatform
    {
        var entries = APTR.FromPointer(p.ReadUInt32(index, RootChildren));
        var at = ((slot - 24) & 252) * 2;
        return entries.IsNotNull && p.ReadUInt32(entries, at) == key && p.ReadUInt32(entries, at + 4) != length;
    }

    internal static void RecordRootChild<T>(ref T p, APTR index, int slot, uint key, uint length)
        where T : struct, IAmigaFileSystemPlatform
    {
        var entries = APTR.FromPointer(p.ReadUInt32(index, RootChildren));
        if (entries.IsNull) return;
        var at = ((slot - 24) & 252) * 2;
        p.WriteUInt32(entries, at, key);
        p.WriteUInt32(entries, at + 4, length);
    }

    internal static void RememberRun<T>(ref T p, APTR memory, uint header, uint logical, uint physical, uint length)
        where T : struct, IAmigaFileSystemPlatform
    {
        if (p.ReadUInt32(memory, RunHeader) == header)
        {
            var ascending = p.ReadUInt32(memory, AscendingBlocks);
            if (logical == ascending && (logical == 0 ||
                physical > p.ReadUInt32(DataKeys(ref p, memory), (int)(logical - 1) * 4)))
                p.WriteUInt32(memory, AscendingBlocks, ascending + length);
            var previous = p.ReadUInt32(memory, RunLength);
            if (logical - p.ReadUInt32(memory, RunLogical) == previous &&
                physical - p.ReadUInt32(memory, RunPhysical) == previous)
            {
                p.WriteUInt32(memory, RunLength, previous + length);
                return;
            }
            if (previous >= length) return;
        }
        else p.WriteUInt32(memory, AscendingBlocks, logical == 0 ? length : 0);
        p.WriteUInt32(memory, RunHeader, header);
        p.WriteUInt32(memory, RunLogical, logical);
        p.WriteUInt32(memory, RunPhysical, physical);
        p.WriteUInt32(memory, RunLength, length);
    }

    internal static APTR DataKeys<T>(ref T p, APTR memory) where T : struct, IAmigaFileSystemPlatform
        => APTR.FromPointer(p.ReadUInt32(memory, Chain));

    internal static APTR TableKeys<T>(ref T p, APTR memory) where T : struct, IAmigaFileSystemPlatform
        => APTR.FromPointer(p.ReadUInt32(memory, TableStorage));

    // A checked file has distinct data, extension and header keys. If its
    // ascending data and all extensions exactly fill their physical span,
    // the entire body can be freed as one range. The header stays allocated.
    internal static uint ContiguousBody<T>(ref T p, APTR memory, uint header, out uint count)
        where T : struct, IAmigaFileSystemPlatform
    {
        count = 0;
        var tables = p.ReadUInt32(memory, Tables);
        if (tables < 2 || p.ReadUInt32(memory, RunHeader) != header) return 0;
        var blocks = p.ReadUInt32(memory, FileBlocks);
        if (blocks == 0 || p.ReadUInt32(memory, AscendingBlocks) != blocks) return 0;
        var data = DataKeys(ref p, memory);
        var first = p.ReadUInt32(data);
        var last = p.ReadUInt32(data, (int)(blocks - 1) * 4);
        var total = blocks + tables - 1;
        if (last - first >= total) return 0;
        var extensions = TableKeys(ref p, memory);
        for (var i = 1u; i < tables; i++)
        {
            var key = p.ReadUInt32(extensions, (int)i * 4);
            if (key < first) first = key;
            if (key > last) last = key;
        }
        if (last - first != total - 1) return 0;
        count = total;
        return first;
    }

    internal static bool FitsFile<T>(ref T p, APTR memory, uint blocks, uint entries)
        where T : struct, IAmigaFileSystemPlatform
    {
        var tables = blocks == 0 ? 1 : (blocks - 1) / entries + 1;
        var capacity = p.ReadUInt32(memory, Capacity);
        return blocks <= capacity && tables <= capacity - blocks;
    }

    internal static bool Matches<T>(ref T p, APTR memory, uint header, uint size)
        where T : struct, IAmigaFileSystemPlatform => memory.IsNotNull &&
        (p.ReadUInt32(memory, Status) == Clean || p.ReadUInt32(memory, Status) is GroupOpen or MetadataOpen) &&
        p.ReadUInt32(memory, Header) == header && p.ReadUInt32(memory, FileBytes) == size;

    // Only a new, disjoint file may write while this copy is parked. Begin
    // expires it before any other mutation, including delete and key reuse.
    internal static uint ParkFile<T>(ref T p, APTR index) where T : struct, IAmigaFileSystemPlatform
    {
        p.WriteUInt32(index, ParkedHeader, 0);
        p.WriteUInt32(index, PublishedPreviousHeader, 0);
        p.WriteUInt32(index, PublishedPreviousTable, 0);
        var header = p.ReadUInt32(index, Header);
        var blocks = p.ReadUInt32(index, FileBlocks);
        var tables = p.ReadUInt32(index, Tables);
        if (header == 0 || blocks == 0) return 0;
        if (p.ReadUInt32(index, Status) == Clean && p.ReadUInt32(index, PublishedHeader) == header)
        {
            var previous = APTR.FromPointer(p.ReadUInt32(index, PreviousHeaderImage));
            if (previous.IsNotNull)
            {
                // The new sibling owns HeaderImage. Preserve the old file's
                // sealed sector with its map so later lookup can use both.
                p.Copy(APTR.FromPointer(p.ReadUInt32(index, HeaderImage)), previous, p.BlockBytes);
                p.WriteUInt32(index, PublishedPreviousHeader, header);
            }
        }
        // Creating the empty sibling writes its first table key. Keep these
        // few table identities and switch the large data array after success.
        p.Copy(TableKeys(ref p, index), APTR.FromPointer(p.ReadUInt32(index, SpareTables)), tables * 4);
        p.WriteUInt32(index, ParkedSize, p.ReadUInt32(index, FileBytes));
        p.WriteUInt32(index, ParkedBlocks, blocks);
        p.WriteUInt32(index, ParkedTables, tables);
        var validRun = p.ReadUInt32(index, RunHeader) == header;
        p.WriteUInt32(index, ParkedAscending, validRun ? p.ReadUInt32(index, AscendingBlocks) : 0);
        p.WriteUInt32(index, ParkedRunLength, validRun ? p.ReadUInt32(index, RunLength) : 0);
        p.WriteUInt32(index, ParkedRunLogical, p.ReadUInt32(index, RunLogical));
        p.WriteUInt32(index, ParkedRunPhysical, p.ReadUInt32(index, RunPhysical));
        return header;
    }

    internal static void SwapDataKeys<T>(ref T p, APTR index) where T : struct, IAmigaFileSystemPlatform
    {
        var active = p.ReadUInt32(index, Chain);
        p.WriteUInt32(index, Chain, p.ReadUInt32(index, SpareData));
        p.WriteUInt32(index, SpareData, active);
    }

    internal static bool ResumeFile<T>(ref T p, APTR index, uint header, uint size) where T : struct, IAmigaFileSystemPlatform
    {
        if (p.ReadUInt32(index, ParkedHeader) != header || p.ReadUInt32(index, Status) != Clean ||
            p.ReadUInt32(index, ParkedSize) != size) return false;
        var blocks = p.ReadUInt32(index, ParkedBlocks);
        var tables = p.ReadUInt32(index, ParkedTables);
        SwapDataKeys(ref p, index);
        p.Copy(APTR.FromPointer(p.ReadUInt32(index, SpareTables)), TableKeys(ref p, index), tables * 4);
        p.WriteUInt32(index, FileBlocks, blocks); p.WriteUInt32(index, Tables, tables);
        p.WriteUInt32(index, Header, header); p.WriteUInt32(index, FileBytes, size);
        p.WriteUInt32(index, RunHeader, header);
        p.WriteUInt32(index, RunLength, p.ReadUInt32(index, ParkedRunLength));
        p.WriteUInt32(index, RunLogical, p.ReadUInt32(index, ParkedRunLogical));
        p.WriteUInt32(index, RunPhysical, p.ReadUInt32(index, ParkedRunPhysical));
        p.WriteUInt32(index, AscendingBlocks, p.ReadUInt32(index, ParkedAscending));
        p.WriteUInt32(index, ParkedHeader, 0);
        return true;
    }

    internal static bool SealFile<T>(ref T p, APTR memory, uint header, uint size, uint blocks, uint tables)
        where T : struct, IAmigaFileSystemPlatform
    {
        // Dense volumes can check identity in linear time. The allocated
        // scratch also covers sparse layouts through the sorted fallback.
        var tableKeys = TableKeys(ref p, memory);
        var seen = APTR.FromPointer(p.ReadUInt32(memory, SeenStorage));
        var dataKeys = DataKeys(ref p, memory);
        var words = (p.BlockCount - 1) / 32 + 1;
        var scratchWords = p.ReadUInt32(memory, Capacity);
        uint ascending = 0, previous = 0;
        while (ascending < blocks)
        {
            var key = p.ReadUInt32(dataKeys, (int)ascending * 4);
            if (key <= previous) break;
            previous = key;
            ascending++;
        }
        if (ascending == blocks)
        {
            // The file order already proves its data keys distinct. Only
            // table/table and table/data aliases remain to be ruled out.
            if (tables > 1)
            {
                p.Copy(tableKeys, seen, tables * 4);
                if (!Unique(ref p, seen, tables)) return false;
            }
            for (var i = 0u; i < tables; i++)
            {
                var key = p.ReadUInt32(tableKeys, (int)i * 4);
                if (key <= previous && SortedIntersects(ref p, dataKeys, blocks, key, 1)) return false;
            }
        }
        else if (words <= scratchWords && words / 16 <= blocks + tables)
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
        p.WriteUInt32(memory, RunHeader, header);
        p.WriteUInt32(memory, AscendingBlocks, ascending);
        p.WriteUInt32(memory, RunLength, 0);
        if (ascending != 0)
        {
            var first = p.ReadUInt32(dataKeys);
            if (previous - first == ascending - 1)
            {
                p.WriteUInt32(memory, RunLogical, 0);
                p.WriteUInt32(memory, RunPhysical, first);
                p.WriteUInt32(memory, RunLength, ascending);
            }
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

    internal struct SavedFile
    {
        internal uint Key, Size, Blocks, TableCount, Ascending, Logical, Physical, Length;
        internal uint SealedHeader, SealedTable;
        internal APTR Data, Tables;
        internal bool DisjointSibling;
    }

    // The spare file bank can validate a deletion while preserving a live
    // writer's map. Once the target is sealed its duplicate-check scratch
    // can be reused to compare the two files, including all pointer tables.
    internal static SavedFile BorrowRemovalSpace<T>(ref T p, APTR memory, uint target, uint size, uint targetBlocks, uint entries)
        where T : struct, IAmigaFileSystemPlatform
    {
        var saved = new SavedFile();
        var key = p.ReadUInt32(memory, Header);
        if (key == 0 || key == target || p.ReadUInt32(memory, Status) != Clean) return saved;
        var parked = p.ReadUInt32(memory, ParkedHeader);
        if (parked != 0)
        {
            if (parked != target || p.ReadUInt32(memory, Companion) != key ||
                p.ReadUInt32(memory, ParkedSize) != size || p.ReadUInt32(memory, ParkedBlocks) != targetBlocks) return saved;
            // Only this newly allocated companion could grow after parking.
            // Its successful allocations consumed bits that were still free
            // while every block of the parked file was known allocated.
            saved.DisjointSibling = true;
        }
        var capacity = p.ReadUInt32(memory, Capacity);
        var words = (p.BlockCount - 1) / 32 + 1;
        if (!saved.DisjointSibling && (words > capacity || !FitsFile(ref p, memory, targetBlocks, entries))) return saved;
        saved.Key = key; saved.Size = p.ReadUInt32(memory, FileBytes);
        saved.Blocks = p.ReadUInt32(memory, FileBlocks); saved.TableCount = p.ReadUInt32(memory, Tables);
        saved.Data = DataKeys(ref p, memory); saved.Tables = TableKeys(ref p, memory);
        if (p.ReadUInt32(memory, PublishedHeader) == key) saved.SealedHeader = key;
        if (saved.TableCount > 1)
        {
            var tail = p.ReadUInt32(saved.Tables, (int)(saved.TableCount - 1) * 4);
            if (p.ReadUInt32(memory, PublishedTable) == tail) saved.SealedTable = tail;
        }
        if (p.ReadUInt32(memory, RunHeader) == key)
        {
            saved.Ascending = p.ReadUInt32(memory, AscendingBlocks);
            saved.Logical = p.ReadUInt32(memory, RunLogical);
            saved.Physical = p.ReadUInt32(memory, RunPhysical);
            saved.Length = p.ReadUInt32(memory, RunLength);
        }
        p.WriteUInt32(memory, Chain, p.ReadUInt32(memory, SpareData));
        p.WriteUInt32(memory, TableStorage, p.ReadUInt32(memory, SpareTables));
        p.WriteUInt32(memory, Header, parked);
        if (saved.DisjointSibling)
        {
            p.WriteUInt32(memory, FileBytes, size);
            p.WriteUInt32(memory, FileBlocks, targetBlocks);
            p.WriteUInt32(memory, Tables, p.ReadUInt32(memory, ParkedTables));
            p.WriteUInt32(memory, RunHeader, parked);
            p.WriteUInt32(memory, RunLength, p.ReadUInt32(memory, ParkedRunLength));
            p.WriteUInt32(memory, RunLogical, p.ReadUInt32(memory, ParkedRunLogical));
            p.WriteUInt32(memory, RunPhysical, p.ReadUInt32(memory, ParkedRunPhysical));
            p.WriteUInt32(memory, AscendingBlocks, p.ReadUInt32(memory, ParkedAscending));
        }
        return saved;
    }

    internal static bool RemovalIsDisjoint<T>(ref T p, APTR memory, in SavedFile saved)
        where T : struct, IAmigaFileSystemPlatform
    {
        if (saved.Key == 0 || saved.DisjointSibling) return true;
        var blocks = p.ReadUInt32(memory, FileBlocks);
        var tables = p.ReadUInt32(memory, Tables);
        var data = DataKeys(ref p, memory);
        var keys = TableKeys(ref p, memory);
        if (saved.Ascending == saved.Blocks && saved.TableCount <= 8 && tables <= 8 &&
            p.ReadUInt32(memory, RunHeader) == p.ReadUInt32(memory, Header) &&
            p.ReadUInt32(memory, RunLength) == blocks && (blocks == 0 || p.ReadUInt32(memory, RunLogical) == 0))
        {
            var first = p.ReadUInt32(memory, RunPhysical);
            if (blocks != 0 && SortedIntersects(ref p, saved.Data, saved.Blocks, first, blocks)) return false;
            for (var i = 0u; i < saved.TableCount; i++)
            {
                var key = p.ReadUInt32(saved.Tables, (int)i * 4);
                if (key - first < blocks) return false;
                for (var j = 0u; j < tables; j++)
                    if (key == p.ReadUInt32(keys, (int)j * 4)) return false;
            }
            for (var i = 0u; i < tables; i++)
                if (SortedIntersects(ref p, saved.Data, saved.Blocks, p.ReadUInt32(keys, (int)i * 4), 1)) return false;
            return true;
        }
        var seen = APTR.FromPointer(p.ReadUInt32(memory, SeenStorage));
        p.Clear(seen, ((p.BlockCount - 1) / 32 + 1) * 4);
        return MarkDistinct(ref p, seen, saved.Data, saved.Blocks) && MarkDistinct(ref p, seen, saved.Tables, saved.TableCount) &&
            MarkDistinct(ref p, seen, data, blocks) && MarkDistinct(ref p, seen, keys, tables);
    }

    internal static void RestoreRemovalSpace<T>(ref T p, APTR memory, in SavedFile saved, bool committed)
        where T : struct, IAmigaFileSystemPlatform
    {
        if (saved.Key == 0) return;
        p.WriteUInt32(memory, Chain, saved.Data.Raw);
        p.WriteUInt32(memory, TableStorage, saved.Tables.Raw);
        if (!committed) { Forget(ref p, memory); return; }
        p.WriteUInt32(memory, FileBytes, saved.Size); p.WriteUInt32(memory, FileBlocks, saved.Blocks);
        p.WriteUInt32(memory, Tables, saved.TableCount); p.WriteUInt32(memory, Header, saved.Key);
        p.WriteUInt32(memory, RunHeader, saved.Key); p.WriteUInt32(memory, AscendingBlocks, saved.Ascending);
        p.WriteUInt32(memory, RunLogical, saved.Logical); p.WriteUInt32(memory, RunPhysical, saved.Physical);
        p.WriteUInt32(memory, RunLength, saved.Length);
    }

    public static void Initialize<T>(ref T p, APTR memory, uint bytes) where T : struct, IAmigaFileSystemPlatform
    {
        if (memory.IsNull || bytes < 8 || bytes > int.MaxValue || !p.IsMapped(memory, bytes)) return;
        p.Clear(memory, bytes < Directory ? bytes : Directory);
        p.WriteUInt32(memory, Bytes, bytes);
    }

    public static void Forget<T>(ref T p, APTR memory) where T : struct, IAmigaFileSystemPlatform
    {
        if (memory.IsNull) return;
        p.WriteUInt32(memory, Status, 0);
    }

    internal static uint Map<T>(ref T p, APTR memory, uint index) where T : struct, IAmigaFileSystemPlatform
        => p.ReadUInt32(memory, Directory + (int)index * 4);

    // The volume's bitmap keys do not fit this workspace, so Build leaves it unindexed.
    internal static bool Declines<T>(ref T p, APTR memory, in AmigaFfsGeometry g) where T : struct, IAmigaFileSystemPlatform
    {
        var bytes = p.ReadUInt32(memory, Bytes);
        var maps = (g.BlockCount - g.ReservedBlocks - 1) / g.BitmapBits + 1;
        var extensionSlots = g.BlockBytes / 4 - 1;
        var extensions = maps <= 25 ? 0 : (maps - 26) / extensionSlots + 1;
        var keys = maps + extensions + 1;
        return bytes < Directory || keys > (bytes - Directory) / 8;
    }

    // Words a forgotten index can lend to maintenance as a sort buffer.
    internal static uint ScratchWords<T>(ref T p, APTR memory) where T : struct, IAmigaFileSystemPlatform
    {
        var bytes = p.ReadUInt32(memory, Bytes);
        return bytes < Directory ? 0 : (bytes - (uint)Directory) / 4;
    }

    internal static bool SortedContains<T>(ref T p, APTR start, uint entries, uint key) where T : struct, IAmigaFileSystemPlatform
        => SortedIntersects(ref p, start, entries, key, 1);

    internal static APTR SummaryMap(APTR memory) => APTR.FromPointer(memory.Raw + (uint)Directory);

    // Extension keys the summary map can hold after the root pointers and count.
    internal static uint SummaryCapacity<T>(ref T p, APTR memory) where T : struct, IAmigaFileSystemPlatform
    {
        var bytes = p.ReadUInt32(memory, Bytes);
        var fixedBytes = (uint)Directory + (SummaryRootPointers + 1) * 4;
        return bytes < fixedBytes ? 0 : (bytes - fixedBytes) / 4;
    }

    // The sorted copy supports both exact duplicate detection and intersection
    // tests for arbitrary hints without scanning the pointer chain again.
    internal static bool Intersects<T>(ref T p, APTR memory, uint first, uint count) where T : struct, IAmigaFileSystemPlatform
    {
        var entries = p.ReadUInt32(memory, Keys);
        var start = APTR.FromPointer(p.ReadUInt32(memory, Sorted));
        return SortedIntersects(ref p, start, entries, first, count);
    }

    private static bool SortedIntersects<T>(ref T p, APTR start, uint entries, uint first, uint count)
        where T : struct, IAmigaFileSystemPlatform
    {
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
        if (Declines(ref p, memory, in g)) return true;
        var bytes = p.ReadUInt32(memory, Bytes);
        var maps = (g.BlockCount - g.ReservedBlocks - 1) / g.BitmapBits + 1;
        var extensionSlots = g.BlockBytes / 4 - 1;
        var extensions = maps <= 25 ? 0 : (maps - 26) / extensionSlots + 1;
        var keys = maps + extensions + 1;
        p.WriteUInt32(memory, PrefixDirectory, 0);
        p.WriteUInt32(memory, RunHeader, 0);
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
        var bitmapImage = APTR.Null;
        if (available >= g.BlockBytes)
        {
            available -= g.BlockBytes;
            bitmapImage = APTR.FromPointer(headerImage.Raw - g.BlockBytes);
        }
        p.WriteUInt32(memory, BitmapImage, bitmapImage.Raw);
        p.WriteUInt32(memory, PendingBitmap, 0);
        p.WriteUInt32(memory, ReadHeader, 0);
        p.WriteUInt32(memory, HeaderImage, headerImage.Raw);
        p.WriteUInt32(memory, PendingHeader, 0);
        p.WriteUInt32(memory, SealedAttributeHeader, 0);
        var tableImage = APTR.Null;
        if (available >= g.BlockBytes)
        {
            available -= g.BlockBytes;
            tableImage = APTR.FromPointer(bitmapImage.Raw - g.BlockBytes);
        }
        p.WriteUInt32(memory, PendingTable, 0);
        p.WriteUInt32(memory, PublishedHeader, 0);
        p.WriteUInt32(memory, PublishedParent, 0);
        p.WriteUInt32(memory, PublishedTable, 0);
        p.WriteUInt32(memory, FreshHeaderImage, 0);
        p.WriteUInt32(memory, FreshTableImage, 0);
        var parentImage = APTR.Null;
        if (available >= g.BlockBytes)
        {
            available -= g.BlockBytes;
            parentImage = APTR.FromPointer(tableImage.Raw - g.BlockBytes);
        }
        p.WriteUInt32(memory, PendingParent, 0);
        var previousHeader = APTR.Null;
        var previousTable = APTR.Null;
        if (available >= 2 * g.BlockBytes)
        {
            available -= 2 * g.BlockBytes;
            previousHeader = APTR.FromPointer(parentImage.Raw - g.BlockBytes);
            previousTable = APTR.FromPointer(previousHeader.Raw - g.BlockBytes);
            // Parking the first file swaps its images with the spare slots.
            // Place its table immediately before the sibling's header for DMA.
            var spare = previousHeader;
            previousHeader = tableImage;
            tableImage = parentImage;
            parentImage = spare;
        }
        var firstExtension = APTR.Null;
        if (previousTable.IsNotNull && available >= g.BlockBytes)
        {
            available -= g.BlockBytes;
            // The deeper extension precedes its parent in memory, so that a
            // child-first physical allocation can publish both in one request.
            firstExtension = tableImage;
            tableImage = parentImage;
            parentImage = APTR.FromPointer(memory.Raw + Directory + keys * 8 + available);
        }
        p.WriteUInt32(memory, TableImage, tableImage.Raw);
        p.WriteUInt32(memory, FirstExtension, 0);
        p.WriteUInt32(memory, FirstExtensionImage, firstExtension.Raw);
        p.WriteUInt32(memory, ParentImage, parentImage.Raw);
        p.WriteUInt32(memory, PreviousHeaderImage, previousHeader.Raw);
        p.WriteUInt32(memory, PreviousTableImage, previousTable.Raw);
        p.WriteUInt32(memory, PreviousHeader, 0);
        p.WriteUInt32(memory, PreviousTable, 0);
        var children = APTR.Null;
        if (available / 16 >= RootChildBytes)
        {
            available -= RootChildBytes;
            children = APTR.FromPointer(memory.Raw + Directory + keys * 8 + available);
            p.Clear(children, RootChildBytes);
        }
        p.WriteUInt32(memory, RootChildren, children.Raw);
        // Keep most small workspaces for file identities and OFS transfers.
        var appendBytes = available / 16 & ~3u;
        if (appendBytes > 16384) appendBytes = 16384;
        available -= appendBytes;
        var appendStorage = memory.Raw + Directory + keys * 8 + available;
        p.WriteUInt32(memory, AppendStorage, appendStorage);
        p.WriteUInt32(memory, AppendStorageBytes, appendBytes);
        p.WriteUInt32(memory, AppendImage, appendStorage);
        p.WriteUInt32(memory, AppendCapacity, appendBytes);
        p.WriteUInt32(memory, PendingAppend, 0);
        p.WriteUInt32(memory, ParkedHeader, 0);
        p.WriteUInt32(memory, Companion, 0);
        // Keep all allocation pages when they fit a quarter of the remaining
        // workspace. The file index and bounded staging area share the rest.
        var pages = APTR.Null;
        if (maps <= (available / 4) / g.BlockBytes)
        {
            available -= maps * g.BlockBytes;
            pages = APTR.FromPointer(memory.Raw + Directory + keys * 8 + available);
        }
        p.WriteUInt32(memory, BitmapPages, pages.Raw);
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
            if (map < g.ReservedBlocks || map >= g.BlockCount) return false;
            p.WriteUInt32(memory, Directory + (int)i * 4, map);
            p.WriteUInt32(sorted, (int)i * 4, map);
        }
        if (next != 0 || !Unique(ref p, sorted, keys)) return false;
        // The file index is empty during mount validation. Use its storage to
        // fetch adjacent maps together and check permanent ownership in the
        // same pass that counts the allocated bits.
        var window = APTR.FromPointer(sorted.Raw + keys * 4);
        var windowBlocks = available / g.BlockBytes;
        if (windowBlocks > 32) windowBlocks = 32;
        uint firstBit = g.ReservedBlocks, permanent = 0;
        for (var i = 0u; i < maps;)
        {
            var first = Map(ref p, memory, i);
            var run = 1u;
            while (run < windowBlocks && run < maps - i && Map(ref p, memory, i + run) == first + run) run++;
            var image = bitmap;
            if (pages.IsNotNull) image = APTR.FromPointer(pages.Raw + i * g.BlockBytes);
            if (run > 1)
            {
                if (pages.IsNull) image = window;
                if (p.ReadDataBlocks(first, run, image, false) != run) return false;
            }
            else if (!p.ReadBlock(first, image)) return false;
            for (var n = 0u; n < run; n++)
            {
                var bits = g.BlockCount - firstBit;
                if (bits > g.BitmapBits) bits = g.BitmapBits;
                if (!AmigaDiskFileSystemCore.CountCheckedBitmap(ref p, image, bits, out var allocated)) return false;
                used += allocated;
                while (permanent < keys)
                {
                    var bit = p.ReadUInt32(sorted, (int)permanent * 4) - firstBit;
                    if (bit >= bits) break;
                    if ((p.ReadUInt32(image, 4 + (int)(bit >> 5) * 4) & 1u << (int)(bit & 31)) != 0) return false;
                    permanent++;
                }
                firstBit += bits;
                image = APTR.FromPointer(image.Raw + g.BlockBytes);
            }
            i += run;
        }
        if (permanent != keys) return false;
        p.WriteUInt32(memory, RootImage, rootImage.Raw);
        var staging = available / 4;
        // An OFS table can describe 72 sectors on 512-byte media. A full
        // table in one stage avoids an extra tail relink and table update.
        var tableBytes = (type & 1) == 0 ? g.BlockBytes * g.TableEntries : 0u;
        var stageLimit = tableBytes < 32768 ? 32768u : tableBytes > 65536 ? 65536u : tableBytes;
        if (staging > stageLimit) staging = stageLimit;
        staging &= ~3u;
        p.WriteUInt32(memory, Type, type);
        p.WriteUInt32(memory, Used, used);
        p.WriteUInt32(memory, NextCandidate, g.ReservedBlocks);
        p.WriteUInt32(memory, Maps, maps);
        p.WriteUInt32(memory, Keys, keys);
        p.WriteUInt32(memory, Sorted, sorted.Raw);
        // Each pointer table describes up to TableEntries data blocks. Both
        // file banks have the same maximum size and share the validation set.
        var data = sorted.Raw + keys * 4;
        var words = (available - staging) / 4;
        var maximum = words / 3;
        var tableWords = maximum == 0 ? 0 : (maximum - 1) / g.TableEntries + 1;
        var capacity = (words - 2 * tableWords) / 3;
        p.WriteUInt32(memory, Chain, data);
        p.WriteUInt32(memory, SpareData, data + capacity * 4);
        p.WriteUInt32(memory, SeenStorage, data + capacity * 8);
        p.WriteUInt32(memory, TableStorage, data + capacity * 12);
        p.WriteUInt32(memory, SpareTables, data + capacity * 12 + tableWords * 4);
        p.WriteUInt32(memory, Capacity, capacity);
        p.WriteUInt32(memory, Stage, memory.Raw + Directory + keys * 8 + available - staging);
        p.WriteUInt32(memory, StageBytes, staging);
        p.WriteUInt32(memory, Header, 0);
        p.WriteUInt32(memory, Status, Clean);
        return true;
    }
}
