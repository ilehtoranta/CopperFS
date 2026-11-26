using Amiga;

namespace CopperStart.Dos;

public static partial class AmigaFfsWriteCore
{
    // DOS4/5 mirror canonical directory headers into chained T_DIRC blocks.
    // Each mutation edits only the records it changed, before publishing the
    // valid root: new records go to the head block (a full head gets a new
    // head block next to the directory), changed records stay in their block
    // when they fit, and a block emptied by a removal leaves the chain. A
    // growing directory therefore touches a bounded number of cache blocks
    // per entry. Reuse the three caller-owned blocks; no persistent index.
    private static bool CacheBlock<T>(ref T p, ref Work w, uint key, uint directory)
        where T : struct, IAmigaWritableFileSystemPlatform => CacheBlockAt(ref p, ref w, key, directory, w.Meta, out _);

    // Read and check one cache block into `block`; `end` is the offset just
    // past its last record. Checking allocation can use Data as bitmap scratch
    // before the read, so `block` may be Data.
    private static bool CacheBlockAt<T>(ref T p, ref Work w, uint key, uint directory, APTR block, out uint end)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        end = 0;
        var g = AmigaFfsGeometry.Read(ref p);
        if (!Allocated(ref p, ref w, in g, key) || !p.ReadBlock(key, block) ||
            !AmigaDiskFileSystemCore.Checksum(ref p, block) || p.ReadUInt32(block) != 33 ||
            p.ReadUInt32(block, 4) != key || p.ReadUInt32(block, 8) != directory || p.ReadUInt32(block, 16) == key)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        var records = p.ReadUInt32(block, 12); var at = 24u;
        if (records > (g.BlockBytes - 24) / 26) return Fail(ref w, DOS.Error.DiskNotValidated);
        for (var i = 0u; i < records; i++)
        {
            if (at > g.BlockBytes - 26) return Fail(ref w, DOS.Error.DiskNotValidated);
            var header = p.ReadUInt32(block, (int)at);
            var name = p.ReadUInt8(block, (int)at + 23);
            if (header < g.ReservedBlocks || header >= g.BlockCount || name == 0 || name > 30 ||
                at + 25 + name > g.BlockBytes) return Fail(ref w, DOS.Error.DiskNotValidated);
            var comment = p.ReadUInt8(block, (int)at + 24 + name);
            if (comment > 79 || at + 25 + name + comment > g.BlockBytes) return Fail(ref w, DOS.Error.DiskNotValidated);
            at += CacheRecordBytes(name, comment);
        }
        if (at > g.BlockBytes) return Fail(ref w, DOS.Error.DiskNotValidated);
        end = at;
        return true;
    }

    private static bool ReleaseCache<T>(ref T p, ref Work w, uint directory)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var tail = (int)p.BlockBytes - 512;
        if (!Header(ref p, ref w, directory)) return false;
        var first = p.ReadUInt32(w.Meta, tail + 504); var key = first;
        // Validate the complete old chain before changing allocation bits.
        for (var visited = 0u; key != 0; visited++)
        {
            if (visited >= p.BlockCount || !CacheBlock(ref p, ref w, key, directory)) return Fail(ref w, DOS.Error.DiskNotValidated);
            key = p.ReadUInt32(w.Meta, 16);
        }
        for (key = first; key != 0;)
        {
            if (!CacheBlock(ref p, ref w, key, directory)) return false;
            var next = p.ReadUInt32(w.Meta, 16); Bit(ref p, ref w, key, true); key = next;
            if (w.Error != DOS.Error.None) return false;
        }
        if (first == 0) return true;
        if (!Header(ref p, ref w, directory)) return false;
        p.WriteUInt32(w.Meta, tail + 504, 0);
        return Put(ref p, ref w, directory, w.Meta, 20);
    }

    private static uint CacheRecordBytes(uint name, uint comment) => (26u + name + comment) & ~1u;

    // A DOS4/5 mutation edits its directories' caches only after Begin marks
    // the root invalid, so the edit must never run out of blocks. Refuse the
    // operation now unless `reserved` blocks plus any cache block the edit
    // needs are free. The change adds a record of `insertBytes` (0: none) and
    // resizes `replaceKey`'s record to `replaceBytes` (0 drops it); a same-
    // directory rename passes both. `insertSlot` names the new record's hash
    // slot for a directory that has no cache chain yet. Reads only; Meta and
    // Data are scratch on return.
    private static bool CacheBudgetFits<T>(ref T p, ref Work w, uint directory, uint insertSlot, uint insertBytes,
        uint replaceKey, uint replaceBytes, uint reserved) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var g = AmigaFfsGeometry.Read(ref p);
        if (!(MountIndexEnabled && w.LayoutKnown) && !w.FreeCountKnown) return true;
        var free = g.BlockCount - w.IndexedUsed;
        if (free < reserved) return Fail(ref w, DOS.Error.DiskFull);
        if (!Header(ref p, ref w, directory)) return false;
        var head = p.ReadUInt32(w.Meta, g.Tail + 504);
        if (head == 0) return RebuildBudgetFits(ref p, ref w, directory, insertSlot, insertBytes, replaceKey, replaceBytes, free - reserved);
        // An edit places the record in its own block when it still fits there,
        // else in the head block, else in one new head block.
        if (!CacheBlockAt(ref p, ref w, head, directory, w.Data, out var end)) return false;
        var bytes = insertBytes != 0 ? insertBytes : replaceBytes;
        if (bytes == 0 || bytes <= g.BlockBytes - end || free - reserved != 0) return true;
        if (replaceKey != 0)
        {
            var key = head;
            for (var visited = 0u; key != 0; visited++)
            {
                if (visited >= g.BlockCount || !CacheBlockAt(ref p, ref w, key, directory, w.Data, out end))
                    return Fail(ref w, DOS.Error.DiskNotValidated);
                var at = 24u; var records = p.ReadUInt32(w.Data, 12);
                for (var i = 0u; i < records; i++)
                {
                    var name = p.ReadUInt8(w.Data, (int)at + 23);
                    var size = CacheRecordBytes(name, p.ReadUInt8(w.Data, (int)at + 24 + name));
                    if (p.ReadUInt32(w.Data, (int)at) == replaceKey) return bytes <= g.BlockBytes - end + size || Fail(ref w, DOS.Error.DiskFull);
                    at += size;
                }
                key = p.ReadUInt32(w.Data, 16);
            }
        }
        return Fail(ref w, DOS.Error.DiskFull);
    }

    // A directory without a cache chain gets a full rebuild: count its blocks
    // with RebuildCache's next-fit placement. Meta holds the directory header.
    private static bool RebuildBudgetFits<T>(ref T p, ref Work w, uint directory, uint insertSlot, uint insertBytes,
        uint replaceKey, uint replaceBytes, uint spare) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var g = AmigaFfsGeometry.Read(ref p);
        p.Copy(w.Meta, w.Data, g.BlockBytes);
        uint needed = 1, at = 24, visited = 0;
        for (var slot = 0u; slot < g.TableEntries; slot++)
        {
            if (slot == insertSlot && insertBytes != 0) CachePlace(insertBytes, g.BlockBytes, ref needed, ref at);
            var entry = p.ReadUInt32(w.Data, 24 + (int)slot * 4);
            while (entry != 0)
            {
                if (visited++ >= g.BlockCount || !AmigaDiskFileSystemCore.Header(ref p, in g, entry, w.Meta, true) ||
                    p.ReadUInt32(w.Meta, g.Tail + 500) != directory) return Fail(ref w, DOS.Error.DiskNotValidated);
                var bytes = entry == replaceKey ? replaceBytes :
                    CacheRecordBytes(p.ReadUInt8(w.Meta, g.Tail + 432), p.ReadUInt8(w.Meta, g.Tail + 328));
                if (bytes != 0) CachePlace(bytes, g.BlockBytes, ref needed, ref at);
                entry = p.ReadUInt32(w.Meta, g.Tail + 496);
            }
        }
        return spare >= needed || Fail(ref w, DOS.Error.DiskFull);
    }

    // Mirror RebuildCache's next-fit placement.
    private static void CachePlace(uint bytes, uint blockBytes, ref uint blocks, ref uint at)
    {
        if (bytes > blockBytes - at) { blocks++; at = 24; }
        at += bytes;
    }

    private static void EmptyCache<T>(ref T p, ref Work w, uint key, uint directory)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        p.Clear(w.Data, p.BlockBytes); p.WriteUInt32(w.Data, 0, 33);
        p.WriteUInt32(w.Data, 4, key); p.WriteUInt32(w.Data, 8, directory);
    }

    // Prefer the first free block after the directory, so its cache stays
    // near its header; wrap to the partition start when none is left. The
    // operation's own allocation cursor is kept.
    private static uint AllocateNear<T>(ref T p, ref Work w, in AmigaFfsGeometry g, uint near)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var saved = w.NextKey;
        w.NextKey = near;
        var key = Allocate(ref p, ref w, in g);
        if (key == 0 && w.Error == DOS.Error.DiskFull)
        {
            w.Error = DOS.Error.None; w.NextKey = g.ReservedBlocks;
            key = Allocate(ref p, ref w, in g);
        }
        w.NextKey = saved;
        return key;
    }

    // Write the record mirroring `header` (key `entry`) at `at` in `block`.
    private static void PutCacheRecord<T>(ref T p, APTR header, APTR block, uint at, uint entry, int tail)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var offset = (int)at; var type = unchecked((int)p.ReadUInt32(header, tail + 508));
        var name = p.ReadUInt8(header, tail + 432); var comment = p.ReadUInt8(header, tail + 328);
        p.WriteUInt32(block, offset, entry);
        p.WriteUInt32(block, offset + 4, type == -3 ? p.ReadUInt32(header, tail + 324) : 0);
        p.WriteUInt32(block, offset + 8, p.ReadUInt32(header, tail + 320));
        p.WriteUInt32(block, offset + 12, p.ReadUInt32(header, tail + 316));
        p.WriteUInt16(block, offset + 16, (ushort)p.ReadUInt32(header, tail + 420));
        p.WriteUInt16(block, offset + 18, (ushort)p.ReadUInt32(header, tail + 424));
        p.WriteUInt16(block, offset + 20, (ushort)p.ReadUInt32(header, tail + 428));
        p.WriteUInt8(block, offset + 22, unchecked((byte)type)); p.WriteUInt8(block, offset + 23, name);
        p.Copy(APTR.FromPointer(header.Raw + (uint)tail + 433), APTR.FromPointer(block.Raw + at + 24), name);
        p.WriteUInt8(block, offset + 24 + name, comment);
        p.Copy(APTR.FromPointer(header.Raw + (uint)tail + 329), APTR.FromPointer(block.Raw + at + 25 + name), comment);
    }

    private static bool SyncCaches<T>(ref T p, ref Work w) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!NewCache(ref p, ref w, w.CacheNew)) return false;
        var parent = w.CacheParent; var other = w.CacheOther == parent ? 0 : w.CacheOther;
        if (w.CacheKey == 0 && w.CacheDrop == 0)
            return RebuildCache(ref p, ref w, parent) && RebuildCache(ref p, ref w, other);
        // Drop first: a freed link header may be reused for a new cache block.
        if (w.CacheDrop != 0 &&
            (!SyncCache(ref p, ref w, parent, w.CacheDrop, true, false) || !SyncCache(ref p, ref w, other, w.CacheDrop, true, false)))
            return false;
        // A key that moved into `other` has no record there yet.
        return w.CacheKey == 0 ||
            (SyncCache(ref p, ref w, parent, w.CacheKey, false, w.CacheFresh) && SyncCache(ref p, ref w, other, w.CacheKey, false, true));
    }

    // A new, empty directory gets one empty cache block.
    private static bool NewCache<T>(ref T p, ref Work w, uint directory) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (directory == 0) return true;
        var g = AmigaFfsGeometry.Read(ref p);
        if (!Header(ref p, ref w, directory) || p.ReadUInt32(w.Meta, g.Tail + 508) != 2) return Fail(ref w, DOS.Error.DiskNotValidated);
        if (p.ReadUInt32(w.Meta, g.Tail + 504) != 0) return RebuildCache(ref p, ref w, directory);
        var key = AllocateNear(ref p, ref w, in g, directory); if (key == 0) return false;
        EmptyCache(ref p, ref w, key, directory);
        if (!Put(ref p, ref w, key, w.Data, 20) || !Header(ref p, ref w, directory)) return false;
        p.WriteUInt32(w.Meta, g.Tail + 504, key);
        return Put(ref p, ref w, directory, w.Meta, 20);
    }

    // Bring `key`'s record in `directory`'s cache in line with its header. The
    // record goes when `gone` or when the header now names another parent;
    // otherwise it is rewritten, in place when it still fits its block. With
    // `absent` the caller knows no record exists yet, so no block is searched.
    private static bool SyncCache<T>(ref T p, ref Work w, uint directory, uint key, bool gone, bool absent)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (directory == 0) return true;
        var g = AmigaFfsGeometry.Read(ref p);
        if (!Header(ref p, ref w, directory) || unchecked((int)p.ReadUInt32(w.Meta, g.Tail + 508)) <= 0)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        var head = p.ReadUInt32(w.Meta, g.Tail + 504);
        if (head == 0) return RebuildCache(ref p, ref w, directory);
        uint bytes = 0;
        if (!gone)
        {
            if (!Header(ref p, ref w, key)) return false;
            if (p.ReadUInt32(w.Meta, g.Tail + 500) == directory)
            {
                var name = p.ReadUInt8(w.Meta, g.Tail + 432); var comment = p.ReadUInt8(w.Meta, g.Tail + 328);
                if (name == 0 || name > 30 || comment > 79) return Fail(ref w, DOS.Error.DiskNotValidated);
                bytes = CacheRecordBytes(name, comment);
            }
        }
        // From here Meta holds the header the record mirrors when bytes != 0.
        if (bytes == 0 || !absent)
        {
            uint previous = 0, block = head;
            for (var visited = 0u; block != 0; visited++)
            {
                if (visited >= g.BlockCount || !CacheBlockAt(ref p, ref w, block, directory, w.Data, out var end))
                    return Fail(ref w, DOS.Error.DiskNotValidated);
                var next = p.ReadUInt32(w.Data, 16);
                var records = p.ReadUInt32(w.Data, 12);
                uint at = 24, size = 0, index = 0;
                for (; index < records; index++)
                {
                    var name = p.ReadUInt8(w.Data, (int)at + 23);
                    size = CacheRecordBytes(name, p.ReadUInt8(w.Data, (int)at + 24 + name));
                    if (p.ReadUInt32(w.Data, (int)at) == key) break;
                    at += size;
                }
                if (index < records)
                {
                    // Close the gap, then append the new record if it fits here.
                    for (var from = at + size; from < end; from++)
                        p.WriteUInt8(w.Data, (int)(from - size), p.ReadUInt8(w.Data, (int)from));
                    end -= size; records--;
                    p.Clear(APTR.FromPointer(w.Data.Raw + end), size);
                    if (bytes != 0 && bytes <= g.BlockBytes - end)
                    {
                        PutCacheRecord(ref p, w.Meta, w.Data, end, key, g.Tail);
                        records++; bytes = 0;
                    }
                    p.WriteUInt32(w.Data, 12, records);
                    if (records == 0 && (previous != 0 || next != 0))
                        return DropCacheBlock(ref p, ref w, in g, directory, previous, block, next);
                    if (!Put(ref p, ref w, block, w.Data, 20)) return false;
                    break;
                }
                previous = block; block = next;
            }
            if (bytes == 0) return true;
        }
        // Meta still holds the header: add its record to the head block.
        if (!CacheBlockAt(ref p, ref w, head, directory, w.Data, out var room)) return false;
        if (bytes <= g.BlockBytes - room)
        {
            PutCacheRecord(ref p, w.Meta, w.Data, room, key, g.Tail);
            p.WriteUInt32(w.Data, 12, p.ReadUInt32(w.Data, 12) + 1);
            return Put(ref p, ref w, head, w.Data, 20);
        }
        var fresh = AllocateNear(ref p, ref w, in g, directory); if (fresh == 0) return false;
        // Allocation can use Data as bitmap-pointer scratch. Meta still owns the header.
        EmptyCache(ref p, ref w, fresh, directory);
        p.WriteUInt32(w.Data, 12, 1); p.WriteUInt32(w.Data, 16, head);
        PutCacheRecord(ref p, w.Meta, w.Data, 24, key, g.Tail);
        if (!Put(ref p, ref w, fresh, w.Data, 20) || !Header(ref p, ref w, directory)) return false;
        p.WriteUInt32(w.Meta, g.Tail + 504, fresh);
        return Put(ref p, ref w, directory, w.Meta, 20);
    }

    // Unchain and free an emptied block that is not the directory's last.
    private static bool DropCacheBlock<T>(ref T p, ref Work w, in AmigaFfsGeometry g, uint directory, uint previous, uint block, uint next)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (previous == 0)
        {
            if (!Header(ref p, ref w, directory)) return false;
            p.WriteUInt32(w.Meta, g.Tail + 504, next);
            if (!Put(ref p, ref w, directory, w.Meta, 20)) return false;
        }
        else
        {
            if (!CacheBlockAt(ref p, ref w, previous, directory, w.Data, out _)) return false;
            p.WriteUInt32(w.Data, 16, next);
            if (!Put(ref p, ref w, previous, w.Data, 20)) return false;
        }
        Bit(ref p, ref w, block, true);
        return w.Error == DOS.Error.None;
    }

    private static bool RebuildCache<T>(ref T p, ref Work w, uint directory)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (directory == 0) return true;
        var g = AmigaFfsGeometry.Read(ref p);
        if (!Header(ref p, ref w, directory) || unchecked((int)p.ReadUInt32(w.Meta, g.Tail + 508)) <= 0)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if (!ReleaseCache(ref p, ref w, directory)) return false;
        var first = AllocateNear(ref p, ref w, in g, directory); if (first == 0) return false;
        var current = first; EmptyCache(ref p, ref w, current, directory);
        var at = 24u; var records = 0u; var visited = 0u;
        for (var slot = 0u; slot < g.TableEntries; slot++)
        {
            if (!Header(ref p, ref w, directory)) return false;
            var entry = p.ReadUInt32(w.Meta, 24 + (int)slot * 4);
            while (entry != 0)
            {
                if (visited++ >= g.BlockCount || !AmigaDiskFileSystemCore.Header(ref p, in g, entry, w.Meta, true) ||
                    p.ReadUInt32(w.Meta, g.Tail + 500) != directory) return Fail(ref w, DOS.Error.DiskNotValidated);
                var next = p.ReadUInt32(w.Meta, g.Tail + 496);
                var name = p.ReadUInt8(w.Meta, g.Tail + 432); var comment = p.ReadUInt8(w.Meta, g.Tail + 328);
                if (name == 0 || name > 30 || comment > 79) return Fail(ref w, DOS.Error.DiskNotValidated);
                var bytes = CacheRecordBytes(name, comment);
                if (bytes > g.BlockBytes - at)
                {
                    p.WriteUInt32(w.Data, 12, records);
                    if (!Put(ref p, ref w, current, w.Data, 20)) return false;
                    var following = AllocateNear(ref p, ref w, in g, directory); if (following == 0) return false;
                    // Allocation can use Data as bitmap-pointer scratch. Meta still owns the entry.
                    if (!p.ReadBlock(current, w.Data)) return Fail(ref w, DOS.Error.DiskNotValidated);
                    p.WriteUInt32(w.Data, 16, following);
                    if (!Put(ref p, ref w, current, w.Data, 20)) return false;
                    current = following; EmptyCache(ref p, ref w, current, directory); at = 24; records = 0;
                }
                PutCacheRecord(ref p, w.Meta, w.Data, at, entry, g.Tail);
                records++; at += bytes; entry = next;
            }
        }
        p.WriteUInt32(w.Data, 12, records);
        if (!Put(ref p, ref w, current, w.Data, 20) || !Header(ref p, ref w, directory)) return false;
        p.WriteUInt32(w.Meta, g.Tail + 504, first);
        return Put(ref p, ref w, directory, w.Meta, 20);
    }
}
