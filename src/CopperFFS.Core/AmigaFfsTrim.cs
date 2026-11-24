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

    private static bool TrimWindowCore<T>(ref T p, ref Work w, APTR control, uint flags,
        out AmigaSmartFileSystemWriteCore.TrimResult result, uint startBlock, uint blockCount, APTR validation, bool tracked, bool committed = false) where T : struct, IAmigaDiscardFileSystemPlatform
    {
        result = default; w.Error = DOS.Error.None;
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
            if (p.ReadUInt32(w.Meta) is not (0x444F5300 or 0x444F5301)) return Fail(ref w, DOS.Error.ActionNotKnown);
        }
        if (!p.QueryDiscard(control, out result.Limits)) return Fail(ref w, DOS.Error.ActionNotKnown);
        if (!result.Limits.Valid) return Fail(ref w, DOS.Error.BadNumber);
        if (validation.IsNotNull || tracked)
        {
            if (!p.ReadBlock(0, w.Meta)) return Fail(ref w, DOS.Error.DiskNotValidated);
            if (p.ReadUInt32(w.Meta) is not (0x444F5300 or 0x444F5301)) return Fail(ref w, DOS.Error.ActionNotKnown);
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
        var cursor = new AmigaDiskFileSystemCore.BitmapCursor();
        uint index = startBlock > g.ReservedBlocks ? (startBlock - g.ReservedBlocks) / g.BitmapBits : 0;
        if (startBlock >= g.BlockCount) return true;
        uint first = g.ReservedBlocks + index * g.BitmapBits, run = 0, length = 0;
        while (first < g.BlockCount)
        {
            if (first >= startBlock && first - startBlock >= blockCount) break;
            if (!AmigaDiskFileSystemCore.BitmapBlock(ref p, ref cursor, index++, w.Meta, out var map) ||
                !p.ReadBlock(map, w.Bitmap) || !AmigaDiskFileSystemCore.Checksum(ref p, w.Bitmap))
                return Fail(ref w, DOS.Error.DiskNotValidated);
            var count = g.BlockCount - first;
            if (count > g.BitmapBits) count = g.BitmapBits;
            // The next tracked range may use IsFree/LoadBitmap. Keep the cached
            // identity in sync with the page we just loaded into that scratch.
            w.BitmapBlock = map; w.BitmapFirst = first; w.BitmapCount = count; w.BitmapDirty = 0;
            w.ActiveBitmap = w.Bitmap;
            for (var bit = startBlock > first ? startBlock - first : 0; bit < count; bit++)
            {
                if (first + bit >= startBlock && first + bit - startBlock >= blockCount) break;
                var free = (p.ReadUInt32(w.Bitmap, 4 + (int)(bit / 32) * 4) & (1u << (int)(bit % 32))) != 0;
                if (free) { if (length == 0) run = first + bit; length++; }
                else if (length != 0)
                {
                    if (!TrimRange(ref p, ref w, control, run, length, flags, ref result, startBlock, blockCount)) return false;
                    length = 0;
                }
            }
            first += count;
        }
        return length == 0 || TrimRange(ref p, ref w, control, run, length, flags, ref result, startBlock, blockCount);
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

    private static bool TrimLayout<T>(ref T p, ref Work w) where T : struct, IAmigaDiscardFileSystemPlatform
    {
        var g = AmigaFfsGeometry.Read(ref p);
        var maps = (g.BlockCount - g.ReservedBlocks - 1) / g.BitmapBits + 1;
        var cursor = new AmigaDiskFileSystemCore.BitmapCursor();
        // Bitmap pages have no own-block field. Reject aliases explicitly rather
        // than applying one checksum-valid page to two different allocation ranges.
        // This bounded pairwise check uses existing scratch, only during maintenance.
        for (var i = 0u; i < maps; i++)
        {
            if (!AmigaDiskFileSystemCore.BitmapBlock(ref p, ref cursor, i, w.Meta, out var map))
                return Fail(ref w, DOS.Error.DiskNotValidated);
            var previous = new AmigaDiskFileSystemCore.BitmapCursor();
            for (var j = 0u; j < i; j++)
                if (!AmigaDiskFileSystemCore.BitmapBlock(ref p, ref previous, j, w.Data, out var other) || other == map)
                    return Fail(ref w, DOS.Error.DiskNotValidated);
        }
        if (!Header(ref p, ref w, g.RootBlock)) return false;
        var extension = p.ReadUInt32(w.Meta, g.Tail + 416);
        var needed = maps <= 25 ? 0 : (maps - 26) / (g.BlockBytes / 4 - 1) + 1;
        for (var i = 0u; i < needed; i++)
        {
            if (extension < g.ReservedBlocks || extension >= g.BlockCount || extension == g.RootBlock)
                return Fail(ref w, DOS.Error.DiskNotValidated);
            cursor = default;
            for (var j = 0u; j < maps; j++)
                if (!AmigaDiskFileSystemCore.BitmapBlock(ref p, ref cursor, j, w.Meta, out var map) || map == extension)
                    return Fail(ref w, DOS.Error.DiskNotValidated);
            if (!p.ReadBlock(extension, w.Data)) return Fail(ref w, DOS.Error.DiskNotValidated);
            extension = p.ReadUInt32(w.Data, (int)g.BlockBytes - 4);
        }
        return extension == 0 || Fail(ref w, DOS.Error.DiskNotValidated);
    }

    private static bool TrimRange<T>(ref T p, ref Work w, APTR control, uint block, uint count,
        uint flags, ref AmigaSmartFileSystemWriteCore.TrimResult result, uint startBlock, uint blockCount) where T : struct, IAmigaDiscardFileSystemPlatform
    {
        if (block < startBlock)
        {
            var skipWindow = startBlock - block;
            if (skipWindow >= count) return true;
            block = startBlock; count -= skipWindow;
        }
        var offset = block - startBlock;
        if (offset >= blockCount) return true;
        if (count > blockCount - offset) count = blockCount - offset;

        var granularity = result.Limits.Granularity;
        var remainder = block % granularity;
        var alignment = result.Limits.Alignment;
        var skip = remainder <= alignment ? alignment - remainder : granularity - (remainder - alignment);
        if (skip >= count) return true;
        block += skip; count -= skip; count -= count % granularity;
        var maximum = result.Limits.MaximumBlocks - result.Limits.MaximumBlocks % granularity;
        while (count != 0)
        {
            var take = count < maximum ? count : maximum;
            if (flags != AmigaSmartFileSystemTrimPacket.DryRun && !p.DiscardBlocks(control, block, take))
                return Fail(ref w, DOS.Error.SeekError);
            // A failed request can already have changed free blocks. Its entire
            // range stays out of completed counters; a retry starts a fresh scan.
            result.Blocks += take; result.Ranges++;
            block += take; count -= take;
        }
        return true;
    }
}
