using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystem2WriteCore
{
    // New contiguous data can bypass redo only if the durable allocation map
    // proves it unreachable before this request. Otherwise retain full-data
    // staging (for example, reuse of a block retired by this transaction).
    private static bool WriteNewDataRun<T>(ref T p, ref Work w, uint first, uint count, APTR source)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var overlay = p.RedoOverlay;
        if (!w.RedoStaging || overlay.IsNull)
            return p.WriteDataBlocks(first, count, source) || Fail(ref w, DOS.Error.DiskNotValidated);
        SetTrstOverlay(ref p, APTR.Null);
        var checkedRange = TrstRangeOriginallyFree(ref p, ref w, first, count, out var free);
        if (checkedRange && free && w.GroupStaging && w.GroupBase.IsNotNull)
        {
            // Durable-free blocks can still hold data accepted by earlier packets.
            SetTrstOverlay(ref p, w.GroupBase);
            checkedRange = TrstRangeOriginallyFree(ref p, ref w, first, count, out free);
            SetTrstOverlay(ref p, APTR.Null);
        }
        // One newly allocated partial block can join the physical write queue
        // and the first journal barrier. A direct command loses that joining
        // opportunity. Bulk runs retain the established direct transfer path.
        var success = checkedRange && (!free || (p.SupportsUnpublishedDataWrites && count != 1
            ? p.WriteUnpublishedDataBlocks(first, count, source) : p.WriteDataBlocks(first, count, source)));
        SetTrstOverlay(ref p, overlay);
        if (!checkedRange || !success) return Fail(ref w, DOS.Error.DiskNotValidated);
        if (free) return true;
        return p.WriteDataBlocks(first, count, source) || Fail(ref w, DOS.Error.NoFreeStore);
    }

    private static bool TrstRangeOriginallyFree<T>(ref T p, ref Work w, uint first, uint count, out bool free)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        free = false;
        if (count == 0 || first < p.ReservedBlocks || first >= p.BlockCount - 1 ||
            count > p.BlockCount - 1 - first) return Fail(ref w, DOS.Error.DiskNotValidated);
        while (count != 0)
        {
            if (!BitmapPosition(ref p, ref w, first, out var map, out var offset, out _)) return false;
            var image = Sfs2Layout.View(ref p, map, BitmapId, w.Journal);
            if (image.IsNull) return Fail(ref w, DOS.Error.DiskNotValidated);
            var within = first & 31;
            var remaining = (p.BlockBytes - (uint)offset) * 8 - within;
            if (remaining > count) remaining = count;
            first += remaining; count -= remaining;
            while (remaining != 0)
            {
                var bits = 32u - within;
                if (bits > remaining) bits = remaining;
                var mask = (uint.MaxValue >> (int)within) & (uint.MaxValue << (int)(32 - within - bits));
                if ((p.ReadUInt32(image, offset) & mask) != mask) return true;
                remaining -= bits; offset += 4; within = 0;
            }
        }
        free = true;
        return true;
    }
}
