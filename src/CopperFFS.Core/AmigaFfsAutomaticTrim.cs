using Amiga;

namespace CopperStart.Dos;

public static partial class AmigaFfsWriteCore
{
    /// <summary>One bounded automatic-maintenance step. The owner invalidates the
    /// batch before any intervening mount operation. The final allocation scan
    /// and device completion must remain within the same exclusive turn.</summary>
    public static bool CollectAutomaticTrim<T>(ref T p, ref Work w, APTR batch,
        uint mount, uint generation, out bool complete) where T : struct, IAmigaWritableFileSystemPlatform
    {
        complete = false; w.Error = DOS.Error.None;
        if (!AmigaAutomaticTrimBatch.Current(ref p, batch, mount, generation) ||
            p.ReadUInt32(batch, AmigaAutomaticTrimBatch.Inputs) > AmigaAutomaticTrimBatch.Capacity)
            return Fail(ref w, DOS.Error.BadNumber);
        var g = AmigaFfsGeometry.Read(ref p);
        if (g.BitmapBits == 0 || g.BlockCount <= g.ReservedBlocks)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if (!AmigaAutomaticTrimBatch.ValidInputs(ref p, batch, g.ReservedBlocks, g.BlockCount)) return Fail(ref w, DOS.Error.BadNumber);
        var maps = (g.BlockCount - g.ReservedBlocks - 1) / g.BitmapBits + 1;
        if (p.ReadUInt32(batch, AmigaAutomaticTrimBatch.Stage) == 0)
        {
            // The mutation Prepare() also resolves the root's allocation map.
            // Defer that lookup to the budgeted shared walk below as well.
            if (p.WriteProtected()) return Fail(ref w, DOS.Error.DiskWriteProtected);
            if (!p.ReadBlock(0, w.Meta) || p.ReadUInt32(w.Meta) is not (0x444F5300 or 0x444F5301) ||
                !Header(ref p, ref w, g.RootBlock) || p.ReadUInt32(w.Meta, g.Tail + 312) != uint.MaxValue || !p.Flush())
            {
#if !COPPERFS_NO_FFS_MOUNT_INDEX
                AmigaFfsMountIndex.Forget(ref p, w.Index);
#endif
                return Fail(ref w, DOS.Error.DiskNotValidated);
            }
            uint used = 0, previous = uint.MaxValue, total = 0;
            for (var i = 0u; i < p.ReadUInt32(batch, AmigaAutomaticTrimBatch.Inputs); i++)
            {
                var first = p.ReadUInt32(batch, AmigaAutomaticTrimBatch.Input + (int)i * 8);
                var count = p.ReadUInt32(batch, AmigaAutomaticTrimBatch.Input + (int)i * 8 + 4);
                if (count == 0 || first < g.ReservedBlocks || first >= g.BlockCount ||
                    count > g.BlockCount - first || count > 4096 - total ||
                    (g.RootBlock >= first && g.RootBlock - first < count)) return Fail(ref w, DOS.Error.BadNumber);
                total += count;
                var end = (first + count - 1 - g.ReservedBlocks) / g.BitmapBits;
                for (var index = (first - g.ReservedBlocks) / g.BitmapBits; index <= end; index++)
                {
                    if (index == previous) continue;
                    if (used == AmigaAutomaticTrimBatch.MapCapacity)
                        return Fail(ref w, DOS.Error.DiskNotValidated);
                    var at = AmigaAutomaticTrimBatch.Maps + (int)used * 12;
                    p.WriteUInt32(batch, at, index); p.WriteUInt32(batch, at + 4, 0);
                    p.WriteUInt32(batch, at + 8, 0); used++; previous = index;
                }
            }
            p.WriteUInt32(batch, AmigaAutomaticTrimBatch.MapCount, used);
            p.WriteUInt32(batch, AmigaAutomaticTrimBatch.WalkBlock, g.RootBlock);
            var directory = w.Index;
            if (MountIndexEnabled && directory.IsNotNull &&
                p.ReadUInt32(directory, AmigaFfsMountIndex.Status) == AmigaFfsMountIndex.Clean)
            {
                for (var i = 0u; i < p.ReadUInt32(batch, AmigaAutomaticTrimBatch.Inputs); i++)
                {
                    var at = AmigaAutomaticTrimBatch.Input + (int)i * 8;
                    if (AmigaFfsMountIndex.Intersects(ref p, directory, p.ReadUInt32(batch, at), p.ReadUInt32(batch, at + 4)))
                        return Fail(ref w, DOS.Error.DiskNotValidated);
                }
                for (var i = 0u; i < used; i++)
                {
                    var at = AmigaAutomaticTrimBatch.Maps + (int)i * 12;
                    p.WriteUInt32(batch, at + 4, AmigaFfsMountIndex.Map(ref p, directory, p.ReadUInt32(batch, at)));
                }
                p.WriteUInt32(batch, AmigaAutomaticTrimBatch.Stage, 2);
            }
            else p.WriteUInt32(batch, AmigaAutomaticTrimBatch.Stage, 3);
        }
        uint budget = 128;
        if (p.ReadUInt32(batch, AmigaAutomaticTrimBatch.Stage) == 3 &&
            (!AutomaticLocateMapsStep(ref p, ref w, batch, in g, maps, ref budget, out complete) || !complete))
            return w.Error == DOS.Error.None;
        var prepared = p.ReadUInt32(batch, AmigaAutomaticTrimBatch.Stage) == 2;
        if (!prepared && (!AutomaticLayoutStep(ref p, ref w, batch, in g, maps, ref budget, out complete) || !complete)) return w.Error == DOS.Error.None;
        complete = true;
        // The layout walk protects every permanent pointer key and proves each
        // touched bitmap has exactly one identity. Its allocation is checked too.
        uint allocationMap = 0;
        if (!prepared)
        {
            allocationMap = p.ReadUInt32(batch, AmigaAutomaticTrimBatch.FfsRootMap);
            if (allocationMap == 0 || !p.ReadBlock(allocationMap, w.Bitmap) || !AmigaDiskFileSystemCore.Checksum(ref p, w.Bitmap))
                return Fail(ref w, DOS.Error.DiskNotValidated);
            var bit = (g.RootBlock - g.ReservedBlocks) % g.BitmapBits;
            if ((p.ReadUInt32(w.Bitmap, 4 + (int)(bit >> 5) * 4) & 1u << (int)(bit & 31)) != 0)
                return Fail(ref w, DOS.Error.DiskNotValidated);
        }
        for (var i = 0u; !prepared && i < p.ReadUInt32(batch, AmigaAutomaticTrimBatch.MapCount); i++)
        {
            var at = AmigaAutomaticTrimBatch.Maps + (int)i * 12;
            var key = p.ReadUInt32(batch, at + 4);
            var map = p.ReadUInt32(batch, at + 8);
            if ((p.ReadUInt32(batch, AmigaAutomaticTrimBatch.MapSeen) & 1u << (int)i) == 0 || map == 0)
                return Fail(ref w, DOS.Error.DiskNotValidated);
            if (allocationMap != map)
            {
                if (!p.ReadBlock(map, w.Bitmap) || !AmigaDiskFileSystemCore.Checksum(ref p, w.Bitmap))
                    return Fail(ref w, DOS.Error.DiskNotValidated);
                allocationMap = map;
            }
            var bit = (key - g.ReservedBlocks) % g.BitmapBits;
            if ((p.ReadUInt32(w.Bitmap, 4 + (int)(bit >> 5) * 4) & 1u << (int)(bit & 31)) != 0)
                return Fail(ref w, DOS.Error.DiskNotValidated);
        }
        uint loaded = uint.MaxValue;
        p.WriteUInt32(batch, AmigaAutomaticTrimBatch.Outputs, 0);
        p.WriteUInt32(batch, AmigaAutomaticTrimBatch.OutputBlocks, 0);
        for (var i = 0u; i < p.ReadUInt32(batch, AmigaAutomaticTrimBatch.Inputs); i++)
        {
            var first = p.ReadUInt32(batch, AmigaAutomaticTrimBatch.Input + (int)i * 8);
            var end = first + p.ReadUInt32(batch, AmigaAutomaticTrimBatch.Input + (int)i * 8 + 4);
            uint run = 0, length = 0;
            while (first < end)
            {
                var index = (first - g.ReservedBlocks) / g.BitmapBits;
                var pageFirst = g.ReservedBlocks + index * g.BitmapBits;
                var pageCount = g.BlockCount - pageFirst < g.BitmapBits ? g.BlockCount - pageFirst : g.BitmapBits;
                if (index != loaded)
                {
                    uint key = 0;
                    for (var m = 0u; m < p.ReadUInt32(batch, AmigaAutomaticTrimBatch.MapCount); m++)
                    {
                        var at = AmigaAutomaticTrimBatch.Maps + (int)m * 12;
                        if (p.ReadUInt32(batch, at) == index) { key = p.ReadUInt32(batch, at + 4); break; }
                    }
                    if (key == 0 || !p.ReadBlock(key, w.Bitmap) || !AmigaDiskFileSystemCore.Checksum(ref p, w.Bitmap))
                        return Fail(ref w, DOS.Error.DiskNotValidated);
                    w.BitmapBlock = key; w.BitmapFirst = pageFirst; w.BitmapCount = pageCount; w.BitmapDirty = 0;
                    loaded = index;
                }
                var take = pageFirst + pageCount - first;
                if (take > end - first) take = end - first;
                AmigaAutomaticTrimBatch.ScanBitmap(ref p, batch, w.Bitmap, 4, pageFirst, first, take, false, ref run, ref length);
                first += take;
            }
            if (length != 0) AmigaAutomaticTrimBatch.FreeRun(ref p, batch, run, length);
        }
        p.WriteUInt32(batch, AmigaAutomaticTrimBatch.Stage, 2);
        return true;
    }

    // Locate the selected identities with one forward cursor, charging both
    // extension links and selected pointers to the same per-turn budget as
    // the full alias/exclusion walk. No unbounded BitmapBlock prewalk remains.
    private static bool AutomaticLocateMapsStep<T>(ref T p, ref Work w, APTR batch,
        in AmigaFfsGeometry g, uint maps, ref uint budget, out bool complete) where T : struct, IAmigaWritableFileSystemPlatform
    {
        complete = false;
        var block = p.ReadUInt32(batch, AmigaAutomaticTrimBatch.WalkBlock);
        var selected = p.ReadUInt32(batch, AmigaAutomaticTrimBatch.WalkIndex);
        var first = p.ReadUInt32(batch, AmigaAutomaticTrimBatch.WalkOrdinal);
        var guard = p.ReadUInt32(batch, AmigaAutomaticTrimBatch.WalkGuard);
        uint loaded = uint.MaxValue;
        while (budget != 0 && selected < p.ReadUInt32(batch, AmigaAutomaticTrimBatch.MapCount))
        {
            var root = block == g.RootBlock;
            if (block != loaded)
            {
                if (block < g.ReservedBlocks || block >= g.BlockCount ||
                    AmigaAutomaticTrimBatch.Contains(ref p, batch, block) ||
                    (root ? !Header(ref p, ref w, block) : !p.ReadBlock(block, w.Meta)))
                    return Fail(ref w, DOS.Error.DiskNotValidated);
                loaded = block;
            }
            var capacity = root ? 25u : g.BlockBytes / 4 - 1;
            var at = AmigaAutomaticTrimBatch.Maps + (int)selected * 12;
            var index = p.ReadUInt32(batch, at);
            budget--;
            if (index - first >= capacity)
            {
                var next = p.ReadUInt32(w.Meta, root ? g.Tail + 416 : (int)g.BlockBytes - 4);
                if (next == 0 || next == g.RootBlock || ++guard > maps)
                    return Fail(ref w, DOS.Error.DiskNotValidated);
                block = next; first += capacity;
            }
            else
            {
                var key = p.ReadUInt32(w.Meta, (root ? g.Tail + 316 : 0) + (int)(index - first) * 4);
                if (key < g.ReservedBlocks || key >= g.BlockCount || key == g.RootBlock)
                    return Fail(ref w, DOS.Error.DiskNotValidated);
                p.WriteUInt32(batch, at + 4, key); selected++;
            }
        }
        complete = selected == p.ReadUInt32(batch, AmigaAutomaticTrimBatch.MapCount);
        p.WriteUInt32(batch, AmigaAutomaticTrimBatch.WalkBlock, complete ? g.RootBlock : block);
        p.WriteUInt32(batch, AmigaAutomaticTrimBatch.WalkIndex, complete ? 0 : selected);
        p.WriteUInt32(batch, AmigaAutomaticTrimBatch.WalkOrdinal, complete ? 0 : first);
        p.WriteUInt32(batch, AmigaAutomaticTrimBatch.WalkGuard, complete ? 0 : guard);
        if (complete) p.WriteUInt32(batch, AmigaAutomaticTrimBatch.Stage, 1);
        return true;
    }

    private static bool AutomaticLayoutStep<T>(ref T p, ref Work w, APTR batch,
        in AmigaFfsGeometry g, uint maps, ref uint budget, out bool complete) where T : struct, IAmigaWritableFileSystemPlatform
    {
        complete = false;
        var block = p.ReadUInt32(batch, AmigaAutomaticTrimBatch.WalkBlock);
        var entry = p.ReadUInt32(batch, AmigaAutomaticTrimBatch.WalkIndex);
        var ordinal = p.ReadUInt32(batch, AmigaAutomaticTrimBatch.WalkOrdinal);
        var guard = p.ReadUInt32(batch, AmigaAutomaticTrimBatch.WalkGuard);
        while (budget != 0)
        {
            var root = block == g.RootBlock;
            if (block < g.ReservedBlocks || block >= g.BlockCount ||
                AmigaAutomaticTrimBatch.Contains(ref p, batch, block) ||
                (root ? !Header(ref p, ref w, block) : !p.ReadBlock(block, w.Meta)))
                return Fail(ref w, DOS.Error.DiskNotValidated);
            var capacity = root ? 25u : g.BlockBytes / 4 - 1;
            var offset = root ? g.Tail + 316 : 0;
            while (entry < capacity && ordinal < maps && budget != 0)
            {
                var key = p.ReadUInt32(w.Meta, offset + (int)entry * 4);
                if (key < g.ReservedBlocks || key >= g.BlockCount || key == g.RootBlock ||
                    AmigaAutomaticTrimBatch.Contains(ref p, batch, key)) return Fail(ref w, DOS.Error.DiskNotValidated);
                if ((g.RootBlock - g.ReservedBlocks) / g.BitmapBits == ordinal)
                    p.WriteUInt32(batch, AmigaAutomaticTrimBatch.FfsRootMap, key);
                for (var m = 0u; m < p.ReadUInt32(batch, AmigaAutomaticTrimBatch.MapCount); m++)
                {
                    var at = AmigaAutomaticTrimBatch.Maps + (int)m * 12;
                    var selected = p.ReadUInt32(batch, at + 4);
                    // Save this identity too, so checking that each selected
                    // bitmap is allocated needs no later pointer-chain walk.
                    if ((selected - g.ReservedBlocks) / g.BitmapBits == ordinal)
                        p.WriteUInt32(batch, at + 8, key);
                    if (selected == key)
                    {
                        var seen = p.ReadUInt32(batch, AmigaAutomaticTrimBatch.MapSeen);
                        var mask = 1u << (int)m;
                        if (p.ReadUInt32(batch, at) != ordinal || (seen & mask) != 0)
                            return Fail(ref w, DOS.Error.DiskNotValidated);
                        p.WriteUInt32(batch, AmigaAutomaticTrimBatch.MapSeen, seen | mask);
                    }
                }
                entry++; ordinal++; budget--;
            }
            if (entry == capacity || ordinal == maps)
            {
                // Charge extension links too. If this turn used its budget,
                // resume at this same page before following the next link.
                if (budget == 0) break;
                budget--;
                var next = p.ReadUInt32(w.Meta, root ? g.Tail + 416 : (int)g.BlockBytes - 4);
                if (ordinal == maps && next == 0) { complete = true; return true; }
                if (next == 0 || next == g.RootBlock || ++guard > maps)
                    return Fail(ref w, DOS.Error.DiskNotValidated);
                block = next; entry = 0;
            }
        }
        p.WriteUInt32(batch, AmigaAutomaticTrimBatch.WalkBlock, block);
        p.WriteUInt32(batch, AmigaAutomaticTrimBatch.WalkIndex, entry);
        p.WriteUInt32(batch, AmigaAutomaticTrimBatch.WalkOrdinal, ordinal);
        p.WriteUInt32(batch, AmigaAutomaticTrimBatch.WalkGuard, guard);
        return true;
    }
}
