using Amiga;

namespace CopperStart.Dos;

public static partial class AmigaFfsWriteCore
{
    /// <summary>Manual DOS0/DOS1 maintenance. The caller holds exclusive mount ownership
    /// until every synchronous discard completes. No free-space list survives a call.
    /// FFS has no journal replay here: an invalid volume must be validated separately.</summary>
    public static bool Trim<T>(ref T p, ref Work w, APTR control, uint flags, out AmigaSmartFileSystemWriteCore.TrimResult result)
        where T : struct, IAmigaDiscardFileSystemPlatform =>
        TrimWindow(ref p, ref w, control, flags, out result, 0, uint.MaxValue);

    // Revalidate durable allocation each turn. Window bounds discard, not preflight.
    public static bool TrimWindow<T>(ref T p, ref Work w, APTR control, uint flags,
        out AmigaSmartFileSystemWriteCore.TrimResult result, uint startBlock, uint blockCount, APTR validation = default) where T : struct, IAmigaDiscardFileSystemPlatform
    => TrimWindowCore(ref p, ref w, control, flags, out result, startBlock, blockCount, validation, false);

    /// <summary>Only for hints emitted by validated allocation mutations. Holds
    /// mount ownership through recovery, durable commit, current-bitmap validation
    /// and discard. Hints may be stale or rolled back; allocated blocks are skipped.</summary>
    public static bool TrimFreedRange<T>(ref T p, ref Work w, APTR control,
        uint startBlock, uint blockCount, out AmigaSmartFileSystemWriteCore.TrimResult result)
        where T : struct, IAmigaDiscardFileSystemPlatform =>
        TrimWindowCore(ref p, ref w, control, 0, out result, startBlock, blockCount, APTR.Null, true);

    /// <summary>Batch of trusted unallocation hints (pairs of first/count).
    /// One durable gate precedes all discards. The owner must exclude every other
    /// mount operation for the complete call; batch storage must not alias scratch.</summary>
    public static bool TrimFreedBatch<T>(ref T p, ref Work w, APTR control,
        APTR batch, uint ranges, out AmigaSmartFileSystemWriteCore.TrimResult result) where T : struct, IAmigaDiscardFileSystemPlatform
    {
        result = default;
        if (ranges == 0) return true;
        if (ranges > AmigaFreedRangeQueue.Capacity || !p.IsMapped(batch, ranges * 8))
            return Fail(ref w, DOS.Error.BadNumber);
        for (var i = 0u; i < ranges; i++)
        {
            var first = p.ReadUInt32(batch, (int)i * 8);
            var count = p.ReadUInt32(batch, (int)i * 8 + 4);
            var ok = TrimWindowCore(ref p, ref w, control, 0, out var part,
                first, count, APTR.Null, true, i != 0);
            result.Limits = part.Limits; result.Blocks += part.Blocks; result.Ranges += part.Ranges;
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>Manual TRIM that packs up to <see cref="IAmigaBatchDiscardFileSystemPlatform.DiscardDescriptors"/>
    /// free runs (at most eight, and at most Limits.MaximumBlocks blocks) into
    /// one DiscardRanges command. Ranges is caller-owned storage for eight
    /// first/count pairs that must not alias control or the Work scratch.
    /// Descriptor splitting, alignment and Result.Ranges match TrimWindow; a
    /// failed command leaves its whole vector out of the completed counters.</summary>
    public static bool TrimWindowBatched<T>(ref T p, ref Work w, APTR control, APTR ranges, uint flags,
        out AmigaSmartFileSystemWriteCore.TrimResult result, uint startBlock, uint blockCount, APTR validation = default)
        where T : struct, IAmigaBatchDiscardFileSystemPlatform
    {
        if (!TrimPrepare(ref p, ref w, control, flags, out result, startBlock, blockCount, validation, false, false, out var s))
            return false;
        if (s.Finished) return true;
        var v = new TrimVector();
        var descriptors = flags == AmigaSmartFileSystemTrimPacket.DryRun ? 0 : p.DiscardDescriptors(control);
        if (descriptors > TrimVectorCapacity) descriptors = TrimVectorCapacity;
        // One descriptor per command is exactly what DiscardBlocks issues.
        if (descriptors >= 2)
        {
            if (ranges.IsNull || !p.IsMapped(ranges, descriptors * 8)) return Fail(ref w, DOS.Error.BadNumber);
            v.Ranges = ranges; v.Capacity = descriptors;
        }
        while (true)
        {
            var status = TrimScanBitmap(ref p, ref w, control, flags, ref result, startBlock, blockCount, ref s, ref v);
            if (status == TrimFailed) return false;
            if (v.Count != 0)
            {
                if (!p.DiscardRanges(control, v.Ranges, v.Count)) return Fail(ref w, DOS.Error.SeekError);
                result.Blocks += v.Blocks; result.Ranges += v.Count;
                v.Count = 0; v.Blocks = 0;
            }
            if (status == TrimDone) return true;
        }
    }

    private const uint TrimVectorCapacity = 8;
    private const int TrimDone = 0, TrimVectorFull = 1, TrimFailed = 2;

    // Capacity 0 issues every descriptor directly with DiscardBlocks. Otherwise
    // descriptors collect in Ranges and the scan pauses when the next one does
    // not fit; Pending holds the aligned remainder of the run being split.
    private struct TrimVector
    {
        internal APTR Ranges;
        internal uint Capacity, Count, Blocks, PendingBlock, PendingCount;
    }

    // Resumable bitmap position. The page in Work.Bitmap stays loaded across a
    // pause: discard touches only free blocks and caller control/range storage.
    private struct TrimScan
    {
        internal AmigaDiskFileSystemCore.BitmapCursor Cursor;
        internal uint Index, First, Map, Bit, Run, Length;
        internal bool InPage, Finished;
    }

    private static bool TrimWindowCore<T>(ref T p, ref Work w, APTR control, uint flags,
        out AmigaSmartFileSystemWriteCore.TrimResult result, uint startBlock, uint blockCount, APTR validation, bool tracked, bool committed = false) where T : struct, IAmigaDiscardFileSystemPlatform
    {
        if (!TrimPrepare(ref p, ref w, control, flags, out result, startBlock, blockCount, validation, tracked, committed, out var s))
            return false;
        if (s.Finished) return true;
        var v = new TrimVector();
        return TrimScanBitmap(ref p, ref w, control, flags, ref result, startBlock, blockCount, ref s, ref v) == TrimDone;
    }

    private static bool TrimPrepare<T>(ref T p, ref Work w, APTR control, uint flags,
        out AmigaSmartFileSystemWriteCore.TrimResult result, uint startBlock, uint blockCount, APTR validation, bool tracked, bool committed,
        out TrimScan scan) where T : struct, IAmigaDiscardFileSystemPlatform
    {
        result = default; w.Error = DOS.Error.None;
        scan = new TrimScan { Finished = true };
        // Discard preflight always starts with an uncached allocation page,
        // including when the caller reuses a completed mutation's work record.
        w.LayoutKnown = false; w.BitmapBlock = 0; w.BitmapDirty = 0;
        if (flags > AmigaSmartFileSystemTrimPacket.QueryOnly) return Fail(ref w, DOS.Error.BadNumber);
        if (p.Geometry.BitmapBits == 0 || p.Geometry.BlockCount <= AmigaFfsGeometry.Read(ref p).ReservedBlocks)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        // Automatic discovery on an unsupported device must not evict a useful
        // cache page just to read the boot block. Keep manual packet ordering.
        if (validation.IsNull && !tracked)
        {
            if (!p.ReadBlock(0, w.Meta)) return Fail(ref w, DOS.Error.DiskNotValidated);
            if (p.ReadUInt32(w.Meta) is not (>= 0x444F5300 and <= 0x444F5305)) return Fail(ref w, DOS.Error.ActionNotKnown);
        }
        if (!p.QueryDiscard(control, out result.Limits)) return Fail(ref w, DOS.Error.ActionNotKnown);
        if (!result.Limits.Valid) return Fail(ref w, DOS.Error.BadNumber);
        if (validation.IsNotNull || tracked)
        {
            if (!p.ReadBlock(0, w.Meta)) return Fail(ref w, DOS.Error.DiskNotValidated);
            if (p.ReadUInt32(w.Meta) is not (>= 0x444F5300 and <= 0x444F5305)) return Fail(ref w, DOS.Error.ActionNotKnown);
        }
        if (flags == AmigaSmartFileSystemTrimPacket.QueryOnly) return true;
        if (p.WriteProtected()) return Fail(ref w, DOS.Error.DiskWriteProtected);
        if (MountIndexEnabled && w.Index.IsNotNull &&
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.Status) is AmigaFfsMountIndex.GroupOpen or AmigaFfsMountIndex.MetadataOpen &&
            !FinishFileWriteGroup(ref p, ref w)) return false;

        // FFS mutations finish their bitmap writes before returning to dispatch.
        // Flush all acknowledged writes, including the valid root, before trusting
        // allocation. A failed barrier or preflight authorizes no discard.
        var validated = AmigaTrimValidation.Current(ref p, validation);
        if (!committed && !validated && !p.Flush())
        {
#if !COPPERFS_NO_FFS_MOUNT_INDEX
            AmigaFfsMountIndex.Forget(ref p, w.Index);
#endif
            return Fail(ref w, DOS.Error.DiskNotValidated);
        }
#if !COPPERFS_NO_FFS_MOUNT_INDEX
        var directory = w.Index;
        if (!validated && !tracked && directory.IsNotNull)
        {
            var geometry = AmigaFfsGeometry.Read(ref p);
            AmigaFfsMountIndex.Forget(ref p, directory);
            if (!LoadDosType(ref p, ref w) || !Header(ref p, ref w, geometry.RootBlock) || p.ReadUInt32(w.Meta, geometry.Tail + 312) != uint.MaxValue ||
                !AmigaFfsMountIndex.Build(ref p, directory, w.Meta, w.Bitmap, in geometry, w.DosType))
                return Fail(ref w, DOS.Error.DiskNotValidated);
            validated = p.ReadUInt32(directory, AmigaFfsMountIndex.Status) == AmigaFfsMountIndex.Clean;
        }
#endif
        if (!validated && !tracked && !TrimLayout(ref p, ref w)) return false;
        if (tracked && !TrimTrackedLayout(ref p, ref w, startBlock, blockCount)) return false;
        var g = AmigaFfsGeometry.Read(ref p);
        if (!Prepare(ref p, ref w, in g, 0, scan: !validated && !tracked)) return false;
        AmigaTrimValidation.Complete(ref p, validation);
        if (startBlock >= g.BlockCount) return true;
        scan.Index = startBlock > g.ReservedBlocks ? (startBlock - g.ReservedBlocks) / g.BitmapBits : 0;
        scan.First = g.ReservedBlocks + scan.Index * g.BitmapBits;
        scan.Finished = false;
        return true;
    }

    // Scan free runs from the saved position. Returns TrimVectorFull when the
    // caller must issue the collected vector before the scan continues.
    private static int TrimScanBitmap<T>(ref T p, ref Work w, APTR control, uint flags,
        ref AmigaSmartFileSystemWriteCore.TrimResult result, uint startBlock, uint blockCount,
        ref TrimScan s, ref TrimVector v) where T : struct, IAmigaDiscardFileSystemPlatform
    {
        if (v.PendingCount != 0)
        {
            var pending = v.PendingCount; v.PendingCount = 0;
            var resumed = TrimEmit(ref p, ref w, control, flags, ref result, ref v, v.PendingBlock, pending);
            if (resumed != TrimDone) return resumed;
        }
        if (s.Finished) return TrimDone;
        var g = AmigaFfsGeometry.Read(ref p);
        // One past the window's last block, clamped to the volume.
        var windowEnd = blockCount > g.BlockCount - startBlock ? g.BlockCount : startBlock + blockCount;
        while (s.First < g.BlockCount)
        {
            if (s.First >= startBlock && s.First - startBlock >= blockCount) break;
            var count = g.BlockCount - s.First;
            if (count > g.BitmapBits) count = g.BitmapBits;
            if (!s.InPage)
            {
                if (!AmigaDiskFileSystemCore.BitmapBlock(ref p, ref s.Cursor, s.Index, w.Meta, out s.Map) ||
                    !p.ReadBlock(s.Map, w.Bitmap) || !AmigaDiskFileSystemCore.Checksum(ref p, w.Bitmap))
                { Fail(ref w, DOS.Error.DiskNotValidated); return TrimFailed; }
                // The next tracked range may use IsFree/LoadBitmap. Keep the cached
                // identity in sync with the page we just loaded into that scratch.
                w.BitmapBlock = s.Map; w.BitmapFirst = s.First; w.BitmapCount = count; w.BitmapDirty = 0;
                w.ActiveBitmap = w.Bitmap;
                s.Bit = startBlock > s.First ? startBlock - s.First : 0;
                s.InPage = true;
            }
            // Scan whole words where the window allows: an all-free word extends
            // the run by 32 blocks and an all-used word ends it.
            var limit = count;
            if (windowEnd - s.First < limit) limit = windowEnd - s.First;
            while (s.Bit < limit)
            {
                var word = p.ReadUInt32(w.Bitmap, 4 + (int)(s.Bit / 32) * 4);
                var whole = (s.Bit & 31) == 0 && limit - s.Bit >= 32 && (word == 0 || word == uint.MaxValue);
                var free = whole ? word != 0 : (word & (1u << (int)(s.Bit % 32))) != 0;
                var step = whole ? 32u : 1u;
                if (free) { if (s.Length == 0) s.Run = s.First + s.Bit; s.Length += step; s.Bit += step; continue; }
                s.Bit += step;
                if (s.Length == 0) continue;
                var length = s.Length; s.Length = 0;
                var status = TrimRange(ref p, ref w, control, s.Run, length, flags, ref result, ref v, startBlock, blockCount);
                if (status != TrimDone) return status;
            }
            s.First += count; s.Index++; s.InPage = false;
        }
        s.Finished = true;
        if (s.Length == 0) return TrimDone;
        var tail = s.Length; s.Length = 0;
        return TrimRange(ref p, ref w, control, s.Run, tail, flags, ref result, ref v, startBlock, blockCount);
    }

    // Validate pointer layout relevant to a tracked range without reading the
    // allocation contents of unrelated bitmap pages. Check aliases of touched
    // pages and protect every permanent bitmap/extension key from stale hints.
    private static bool TrimTrackedLayout<T>(ref T p, ref Work w, uint first, uint count)
        where T : struct, IAmigaDiscardFileSystemPlatform
    {
        var g = AmigaFfsGeometry.Read(ref p);
        if (count == 0 || first < g.ReservedBlocks || first >= g.BlockCount || count > g.BlockCount - first)
            return Fail(ref w, DOS.Error.BadNumber);
        if (g.RootBlock >= first && g.RootBlock - first < count &&
            (IsFree(ref p, ref w, g.RootBlock) || w.Error != DOS.Error.None)) return Fail(ref w, DOS.Error.DiskNotValidated);
        var maps = (g.BlockCount - g.ReservedBlocks - 1) / g.BitmapBits + 1;
        var begin = (first - g.ReservedBlocks) / g.BitmapBits;
        var end = (first + count - 1 - g.ReservedBlocks) / g.BitmapBits;
        for (var touched = begin; touched <= end; touched++)
        {
            var lookup = new AmigaDiskFileSystemCore.BitmapCursor();
            if (!AmigaDiskFileSystemCore.BitmapBlock(ref p, ref lookup, touched, w.Meta, out var page))
                return Fail(ref w, DOS.Error.DiskNotValidated);
            if (IsFree(ref p, ref w, page) || w.Error != DOS.Error.None)
                return Fail(ref w, DOS.Error.DiskNotValidated);
            var cursor = new AmigaDiskFileSystemCore.BitmapCursor();
            for (var index = 0u; index < maps; index++)
            {
                if (!AmigaDiskFileSystemCore.BitmapBlock(ref p, ref cursor, index, w.Meta, out var other) ||
                    (index != touched && other == page) ||
                    (other >= first && other - first < count && (IsFree(ref p, ref w, other) || w.Error != DOS.Error.None)))
                    return Fail(ref w, DOS.Error.DiskNotValidated);
            }
        }
        if (!Header(ref p, ref w, g.RootBlock)) return false;
        var extension = p.ReadUInt32(w.Meta, g.Tail + 416);
        for (var guard = 0u; extension != 0; guard++)
        {
            if (guard >= maps || extension < g.ReservedBlocks || extension >= g.BlockCount ||
                extension == g.RootBlock || (extension >= first && extension - first < count &&
                    (IsFree(ref p, ref w, extension) || w.Error != DOS.Error.None)) ||
                !p.ReadBlock(extension, w.Data)) return Fail(ref w, DOS.Error.DiskNotValidated);
            extension = p.ReadUInt32(w.Data, (int)g.BlockBytes - 4);
        }
        return true;
    }

    // Bitmap pages have no own-block field. Reject aliases explicitly rather
    // than applying one checksum-valid page to two different allocation ranges:
    // every bitmap and extension key must be distinct. Sort the keys in chunks
    // the buffer can hold and binary-search the later keys against each chunk.
    // A forgotten (declined) mount index lends its workspace, so one chunk
    // covers the volume; otherwise the Bitmap scratch holds one block of keys.
    private static bool TrimLayout<T>(ref T p, ref Work w) where T : struct, IAmigaDiscardFileSystemPlatform
    {
        var g = AmigaFfsGeometry.Read(ref p);
        var maps = (g.BlockCount - g.ReservedBlocks - 1) / g.BitmapBits + 1;
        var needed = maps <= 25 ? 0 : (maps - 26) / (g.BlockBytes / 4 - 1) + 1;
        if (!Header(ref p, ref w, g.RootBlock)) return false;
        var firstExtension = p.ReadUInt32(w.Meta, g.Tail + 416);
        var extension = firstExtension;
        for (var i = 0u; i < needed; i++)
        {
            if (extension < g.ReservedBlocks || extension >= g.BlockCount || extension == g.RootBlock ||
                !p.ReadBlock(extension, w.Data)) return Fail(ref w, DOS.Error.DiskNotValidated);
            extension = p.ReadUInt32(w.Data, (int)g.BlockBytes - 4);
        }
        if (extension != 0) return Fail(ref w, DOS.Error.DiskNotValidated);
        var total = maps + needed;
        var buffer = w.Bitmap; var capacity = g.BlockBytes / 4;
        if (MountIndexEnabled && w.Index.IsNotNull && p.ReadUInt32(w.Index, AmigaFfsMountIndex.Status) == 0 &&
            AmigaFfsMountIndex.ScratchWords(ref p, w.Index) > capacity)
        {
            buffer = AmigaFfsMountIndex.SummaryMap(w.Index);
            capacity = AmigaFfsMountIndex.ScratchWords(ref p, w.Index);
        }
        for (var start = 0u; start < total; start += capacity)
        {
            var count = total - start < capacity ? total - start : capacity;
            var walk = new LayoutWalk();
            for (var k = 0u; k < start + count; k++)
            {
                if (!NextLayoutKey(ref p, ref w, in g, maps, firstExtension, ref walk, out var key))
                    return Fail(ref w, DOS.Error.DiskNotValidated);
                if (k >= start) p.WriteUInt32(buffer, (int)(k - start) * 4, key);
            }
            if (!AmigaFfsMountIndex.Unique(ref p, buffer, count)) return Fail(ref w, DOS.Error.DiskNotValidated);
            for (var k = start + count; k < total; k++)
            {
                if (!NextLayoutKey(ref p, ref w, in g, maps, firstExtension, ref walk, out var key) ||
                    AmigaFfsMountIndex.SortedContains(ref p, buffer, count, key))
                    return Fail(ref w, DOS.Error.DiskNotValidated);
            }
        }
        return true;
    }

    private struct LayoutWalk
    {
        internal AmigaDiskFileSystemCore.BitmapCursor Cursor;
        internal uint Ordinal, Extension;
    }

    // The layout's keys in order: each bitmap page, then each extension block
    // (already range-checked). Meta is the cursor's scratch, Data the chain's.
    private static bool NextLayoutKey<T>(ref T p, ref Work w, in AmigaFfsGeometry g, uint maps, uint firstExtension,
        ref LayoutWalk walk, out uint key) where T : struct, IAmigaDiscardFileSystemPlatform
    {
        if (walk.Ordinal < maps)
        {
            if (!AmigaDiskFileSystemCore.BitmapBlock(ref p, ref walk.Cursor, walk.Ordinal, w.Meta, out key)) return false;
        }
        else
        {
            key = walk.Ordinal == maps ? firstExtension : walk.Extension;
            if (!p.ReadBlock(key, w.Data)) return false;
            walk.Extension = p.ReadUInt32(w.Data, (int)g.BlockBytes - 4);
        }
        walk.Ordinal++;
        return true;
    }

    private static int TrimRange<T>(ref T p, ref Work w, APTR control, uint block, uint count,
        uint flags, ref AmigaSmartFileSystemWriteCore.TrimResult result, ref TrimVector v,
        uint startBlock, uint blockCount) where T : struct, IAmigaDiscardFileSystemPlatform
    {
        if (block < startBlock)
        {
            var skipWindow = startBlock - block;
            if (skipWindow >= count) return TrimDone;
            block = startBlock; count -= skipWindow;
        }
        var offset = block - startBlock;
        if (offset >= blockCount) return TrimDone;
        if (count > blockCount - offset) count = blockCount - offset;

        var granularity = result.Limits.Granularity;
        var remainder = block % granularity;
        var alignment = result.Limits.Alignment;
        var skip = remainder <= alignment ? alignment - remainder : granularity - (remainder - alignment);
        if (skip >= count) return TrimDone;
        block += skip; count -= skip; count -= count % granularity;
        return TrimEmit(ref p, ref w, control, flags, ref result, ref v, block, count);
    }

    // Split an aligned run into descriptors of at most MaximumBlocks. With a
    // vector, a descriptor that would overflow its slots or block budget is
    // kept pending; descriptors are never cut to fill a vector, so their
    // number and sizes match the direct path and a dry run.
    private static int TrimEmit<T>(ref T p, ref Work w, APTR control, uint flags,
        ref AmigaSmartFileSystemWriteCore.TrimResult result, ref TrimVector v, uint block, uint count)
        where T : struct, IAmigaDiscardFileSystemPlatform
    {
        var granularity = result.Limits.Granularity;
        var maximum = result.Limits.MaximumBlocks - result.Limits.MaximumBlocks % granularity;
        while (count != 0)
        {
            var take = count < maximum ? count : maximum;
            if (v.Capacity != 0)
            {
                if (v.Count == v.Capacity || take > maximum - v.Blocks)
                { v.PendingBlock = block; v.PendingCount = count; return TrimVectorFull; }
                p.WriteUInt32(v.Ranges, (int)v.Count * 8, block);
                p.WriteUInt32(v.Ranges, (int)v.Count * 8 + 4, take);
                v.Count++; v.Blocks += take;
            }
            else
            {
                if (flags != AmigaSmartFileSystemTrimPacket.DryRun && !p.DiscardBlocks(control, block, take))
                { Fail(ref w, DOS.Error.SeekError); return TrimFailed; }
                // A failed request can already have changed free blocks. Its entire
                // range stays out of completed counters; a retry starts a fresh scan.
                result.Blocks += take; result.Ranges++;
            }
            block += take; count -= take;
        }
        return TrimDone;
    }
}
