using Amiga;

namespace CopperStart.Dos;

public static partial class AmigaFfsWriteCore
{
    /// <summary>Manual DOS0/DOS1 maintenance. The caller holds exclusive mount ownership
    /// until every synchronous discard completes. No free-space list survives a call.
    /// FFS has no journal replay here: an invalid volume must be validated separately.</summary>
    public static bool Trim<T>(ref T p, ref Work w, APTR control, uint flags,
        out AmigaSmartFileSystemWriteCore.TrimResult result) where T : struct, IAmigaDiscardFileSystemPlatform
    {
        result = default; w.Error = DOS.Error.None;
        if (flags > AmigaSmartFileSystemTrimPacket.QueryOnly) return Fail(ref w, DOS.Error.BadNumber);
        if (p.Geometry.BitmapBits == 0 || p.Geometry.BlockCount <= p.Geometry.ReservedBlocks)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if (!p.ReadBlock(0, w.Meta)) return Fail(ref w, DOS.Error.DiskNotValidated);
        if (p.ReadUInt32(w.Meta) is not (0x444F5300 or 0x444F5301)) return Fail(ref w, DOS.Error.ActionNotKnown);
        if (!p.QueryDiscard(control, out result.Limits)) return Fail(ref w, DOS.Error.ActionNotKnown);
        if (!result.Limits.Valid) return Fail(ref w, DOS.Error.BadNumber);
        if (flags == AmigaSmartFileSystemTrimPacket.QueryOnly) return true;
        if (p.WriteProtected()) return Fail(ref w, DOS.Error.DiskWriteProtected);

        // FFS mutations finish their bitmap writes before returning to dispatch.
        // Flush all acknowledged writes, including the valid root, before trusting
        // allocation. A failed barrier or preflight authorizes no discard.
        if (!p.Flush()) return Fail(ref w, DOS.Error.DiskNotValidated);
        if (!TrimLayout(ref p, ref w) || !Prepare(ref p, ref w, 0)) return false;
        var g = p.Geometry;
        var cursor = new AmigaDiskFileSystemCore.BitmapCursor();
        uint first = g.ReservedBlocks, index = 0, run = 0, length = 0;
        while (first < g.BlockCount)
        {
            if (!AmigaDiskFileSystemCore.BitmapBlock(ref p, ref cursor, index++, w.Meta, out var map) ||
                !p.ReadBlock(map, w.Bitmap) || !AmigaDiskFileSystemCore.Checksum(ref p, w.Bitmap))
                return Fail(ref w, DOS.Error.DiskNotValidated);
            var count = g.BlockCount - first;
            if (count > g.BitmapBits) count = g.BitmapBits;
            for (var bit = 0u; bit < count; bit++)
            {
                var free = (p.ReadUInt32(w.Bitmap, 4 + (int)(bit / 32) * 4) & (1u << (int)(bit % 32))) != 0;
                if (free) { if (length == 0) run = first + bit; length++; }
                else if (length != 0)
                {
                    if (!TrimRange(ref p, ref w, control, run, length, flags, ref result)) return false;
                    length = 0;
                }
            }
            first += count;
        }
        return length == 0 || TrimRange(ref p, ref w, control, run, length, flags, ref result);
    }

    private static bool TrimLayout<T>(ref T p, ref Work w) where T : struct, IAmigaDiscardFileSystemPlatform
    {
        var g = p.Geometry;
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
        uint flags, ref AmigaSmartFileSystemWriteCore.TrimResult result) where T : struct, IAmigaDiscardFileSystemPlatform
    {
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
