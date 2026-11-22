using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystem2WriteCore
{
    // An aligned continuation of one existing extent needs only scalar changes.
    // Reserve existing accepted images, prove both allocation views, and finish
    // all fallible work before changing metadata. Single-owner execution and
    // exclusive pins make the final memory-only publication indivisible to
    // callers. Unsupported cases retain the ordinary disposable trial.
    private static bool TryAppendOriginalPacket<T>(ref T p, ref Work w, ref Request r,
        APTR group, APTR active, uint state, ref FileInformation info, APTR cursor, uint appendExtentOffset, out uint actual)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        actual = 0;
        var tree = default(Sfs2MetadataStore.Handle); var bitmap = tree; var obj = tree; var root = tree;
        var treeBucket = APTR.Null; var bitmapBucket = treeBucket; var objectBucket = treeBucket; var rootBucket = treeBucket;
        var context = p.Sfs2Context;
        var firstExtent = info.Size == 0 && info.First == 0;
        if (state != 1 || p.BlockBytes != 512 || r.Length == 0 || r.Length > 65536 ||
            r.Position != info.Size || (!firstExtent && (info.Size == 0 || info.First == 0)) || ((info.Size | r.Length) & 511) != 0 ||
            info.ObjectBlock == 0 || info.ObjectOffset < 24 ||
            info.ObjectOffset > 512 - 27 || (info.ObjectOffset & 1) != 0 ||
            info.Size >> 9 > uint.MaxValue || (info.Protection & 16) != 0 ||
            Overlap(context, p.ReadUInt32(context, 44), r.Buffer, r.Length) ||
            Overlap(context, p.ReadUInt32(context, 44), group, p.ReadUInt32(group, 8)) ||
            p.ReadUInt32(active, 24) != 0 || !Sfs2MetadataStore.Unpinned(ref p, active)) return false;
        var cache = Sfs2MountContext.Cache(ref p, context);
        if (cache.IsNotNull && !Sfs2MetadataStore.Unpinned(ref p, cache)) return false;
        // The prepared acceptance deliberately leaves both disk roots alone.
        if (appendExtentOffset == 0 && (Sfs2MetadataStore.Find(ref p, active, 0).IsNotNull ||
            Sfs2MetadataStore.Find(ref p, active, p.BlockCount - 1).IsNotNull)) return false;
        var count = r.Length >> 9; var length = (uint)(info.Size >> 9);
        var first = info.First + length;
        if (firstExtent)
        {
            var staging = w.RedoStaging; w.RedoStaging = true;
            var found = FindFreeRun(ref p, ref w, 0, count, count, true, out first, out var available);
            w.RedoStaging = staging;
            if (!found || available != count) return false;
        }
        var limit = p.BlockCount - 1;
        if (length > uint.MaxValue - count || first < info.First || first < p.ReservedBlocks ||
            first >= limit || count > limit - first || !OriginalDataRangeAvailable(ref p, active, first, count)) return false;
        var bits = (512u - 12) * 8;
        if (count > bits - first % bits ||
            (w.RootContainer + 2 >= first && w.RootContainer + 2 - first < count) || w.AdminReservation != 0) return false;
        if (!BitmapPosition(ref p, ref w, first, out var map, out var mapOffset, out _)) return false;
        var oldCount = p.ReadUInt32(active);
        if (!Sfs2MetadataStore.ReserveAcceptedScalar(ref p, active, w.ExtentRoot, TreeId, w.Meta, firstExtent, out tree, out treeBucket) ||
            !Sfs2MetadataStore.ReserveAcceptedScalar(ref p, active, map, BitmapId, w.Bitmap, firstExtent, out bitmap, out bitmapBucket) ||
            !Sfs2MetadataStore.ReserveAcceptedScalar(ref p, active, info.ObjectBlock, ObjectId, w.Meta, firstExtent, out obj, out objectBucket)) goto Decline;
        if (info.ObjectBlock != w.RootContainer && !Sfs2MetadataStore.ReserveAcceptedScalar(ref p, active, w.RootContainer,
            ObjectId, w.Meta, firstExtent, out root, out rootBucket)) goto Decline;
        if (firstExtent && !OriginalDataRangeAvailable(ref p, active, first, count)) goto Decline;
        var treeCount = p.ReadUInt16(tree.Image, 12);
        if (p.ReadUInt8(tree.Image, 14) == 0 || p.ReadUInt8(tree.Image, 15) != 16 ||
            16u + (uint)treeCount * 16 > 512) goto Decline;
        var extentOffset = 16;
        if (firstExtent)
        {
            if (16u + (uint)(treeCount + 1) * 16 > 512) goto Decline;
            extentOffset = 16 + treeCount * 16;
            for (var i = 0; i < treeCount; i++)
            {
                var key = p.ReadUInt32(tree.Image, 16 + i * 16);
                if (key == first) { Fail(ref w, DOS.Error.DiskNotValidated); goto Decline; }
                if (extentOffset == 16 + treeCount * 16 && key > first) extentOffset = 16 + i * 16;
            }
        }
        else
        {
            if (appendExtentOffset != 0) extentOffset = (int)appendExtentOffset;
            else while (extentOffset < 16 + treeCount * 16 && p.ReadUInt32(tree.Image, extentOffset) != info.First) extentOffset += 16;
            if (extentOffset < 16 || extentOffset >= 16 + treeCount * 16 || (extentOffset & 15) != 0 ||
                p.ReadUInt32(tree.Image, extentOffset) != info.First || p.ReadUInt32(tree.Image, extentOffset + 4) != 0 ||
                p.ReadUInt32(tree.Image, extentOffset + 8) != (r.Node | 0x80000000) ||
                p.ReadUInt32(tree.Image, extentOffset + 12) != length) goto Decline;
        }
        var at = (int)info.ObjectOffset; var protection = p.ReadUInt32(obj.Image, at + 8);
        if (p.ReadUInt32(obj.Image, at + 4) != r.Node || (p.ReadUInt8(obj.Image, at + 26) & (128 | 64)) != 0 ||
            ((protection ^ 15) & 4) != 0 || (protection & 16) != 0 ||
            p.ReadUInt32(obj.Image, at + 12) != info.First || FileSize(ref p, obj.Image, at) != info.Size ||
            (root.Arena.IsNull && at + 27 > 512 - 28)) goto Decline;
        var rootImage = root.Arena.IsNull ? obj.Image : root.Image;
        var freeBlocks = p.ReadUInt32(rootImage, 512 - 28);
        if (freeBlocks < count || freeBlocks - count > p.BlockCount ||
            !OriginalAppendBitsFree(ref p, bitmap.Image, mapOffset, first & 31, count)) goto Decline;

        // Reading the physical bitmap may replace clean-cache views, but every
        // accepted image above is pinned and lives in a separate arena.
        SetTrstOverlay(ref p, APTR.Null);
        var checkedRange = TrstRangeOriginallyFree(ref p, ref w, first, count, out var free);
        SetTrstOverlay(ref p, active);
        if (!checkedRange || !free) goto Decline;
        if (Sfs2MountContext.NextAttempt(ref p, context) == 0)
        { Fail(ref w, DOS.Error.NoFreeStore); goto Decline; }
        var mountEpoch = p.ReadUInt32(context, 8); var view = p.ReadUInt32(context, 12);
        tree.MountEpoch = bitmap.MountEpoch = obj.MountEpoch = root.MountEpoch = mountEpoch;
        if (!Sfs2MountContext.PrepareScalarAcceptance(ref p, out var generation, out var epoch))
        { Fail(ref w, DOS.Error.DiskNotValidated); goto Decline; }
        p.WriteUInt32(group, 28, 0); // No longer a clone of its inactive predecessor.
        SetTrstOverlay(ref p, APTR.Null);
        var written = p.SupportsUnpublishedDataWrites && count != 1
            ? p.WriteUnpublishedDataBlocks(first, count, r.Buffer) : p.WriteDataBlocks(first, count, r.Buffer);
        SetTrstOverlay(ref p, active);
        if (!written || p.ReadUInt32(context, 8) != mountEpoch || p.ReadUInt32(context, 12) != view ||
            p.RedoOverlay != active || p.ReadUInt32(group, 4) != 1 || p.ReadUInt32(group, 16) != active.Raw ||
            !ReservedOriginalAppendCurrent(ref p, tree) || !ReservedOriginalAppendCurrent(ref p, bitmap) ||
            !ReservedOriginalAppendCurrent(ref p, obj) ||
            (root.Arena.IsNotNull && !ReservedOriginalAppendCurrent(ref p, root)) ||
            !Sfs2MountContext.PrepareScalarAcceptance(ref p, out generation, out epoch))
        { Fail(ref w, DOS.Error.DiskNotValidated); goto Decline; }

        // All reservations, counters, ranges and I/O have succeeded. From here
        // to acceptance there are only mapped memory accesses, with no lookup,
        // allocation, device call, rejection or callback into the filesystem.
        Sfs2MetadataStore.BeginReservedScalar(ref p, ref tree);
        Sfs2MetadataStore.BeginReservedScalar(ref p, ref bitmap);
        Sfs2MetadataStore.BeginReservedScalar(ref p, ref obj);
        if (root.Arena.IsNotNull) Sfs2MetadataStore.BeginReservedScalar(ref p, ref root);
        var remaining = count; var within = first & 31; var offset = mapOffset;
        // Up to five aligned words, spanning at most two 16-byte journal
        // regions. Keep the checksum and dirty-region bookkeeping in locals
        // while the bitmap image is exclusively owned.
        var bitmapWords = (within + count + 31) >> 5;
        TrackScalar(ref p, ref bitmap, offset, (int)bitmapWords * 4);
        var checksum = bitmap.Checksum;
        while (remaining != 0)
        {
            var take = 32 - within; if (take > remaining) take = remaining;
            var mask = (uint.MaxValue >> (int)within) & (uint.MaxValue << (int)(32 - within - take));
            // The preflight checked every bit in the range was set.
            p.WriteUInt32(bitmap.Image, offset, p.ReadUInt32(bitmap.Image, offset) & ~mask);
            checksum = unchecked(checksum + mask);
            remaining -= take; offset += 4; within = 0;
        }
        bitmap.Checksum = checksum;
        var size = info.Size + r.Length;
        if (firstExtent)
        {
            for (var record = 16 + treeCount * 16; record > extentOffset; record -= 16)
                for (var part = 12; part >= 0; part -= 4)
                    Scalar32(ref p, ref tree, record + part, p.ReadUInt32(tree.Image, record - 16 + part));
            Scalar32(ref p, ref tree, extentOffset, first); Scalar32(ref p, ref tree, extentOffset + 4, 0);
            Scalar32(ref p, ref tree, extentOffset + 8, r.Node | 0x80000000);
            Scalar32(ref p, ref tree, extentOffset + 12, count); Scalar16(ref p, ref tree, 12, (ushort)(treeCount + 1));
            Scalar32(ref p, ref obj, at + 12, first);
        }
        else Scalar32(ref p, ref tree, extentOffset + 12, length + count);
        Scalar32(ref p, ref obj, at + 16, (uint)(size >> 16)); Scalar16(ref p, ref obj, at + 20, (ushort)size);
        if (root.Arena.IsNull) Scalar32(ref p, ref obj, 512 - 28, freeBlocks - count);
        else Scalar32(ref p, ref root, 512 - 28, freeBlocks - count);
        Sfs2MetadataStore.CompleteReservedScalar(ref p, ref tree);
        Sfs2MetadataStore.CompleteReservedScalar(ref p, ref bitmap);
        Sfs2MetadataStore.CompleteReservedScalar(ref p, ref obj);
        if (root.Arena.IsNotNull) Sfs2MetadataStore.CompleteReservedScalar(ref p, ref root);
        Sfs2MountContext.AcceptPreparedScalars(ref p, generation, epoch);
        info.Size = size; if (firstExtent) info.First = first;
        if (cursor.IsNotNull)
        {
            // The old extent can contain private redo images. A proof for the
            // new allocation alone must never allow an in-place overwrite of them.
            var extentCount = length + count;
            if (!firstExtent && !(appendExtentOffset != 0 && p.ReadUInt32(cursor, 40) == length) &&
                !OriginalDataRangeAvailable(ref p, active, info.First, length)) extentCount = 0;
            RememberOriginalDataCursor(ref p, cursor, group, active, r.Node, size,
                0, info.First, extentCount, info.First, info.ObjectBlock, info.ObjectOffset, (uint)extentOffset);
        }
        actual = r.Length; return true;

    Decline:
        Sfs2MetadataStore.CancelAcceptedScalar(ref p, ref tree);
        Sfs2MetadataStore.CancelAcceptedScalar(ref p, ref bitmap);
        Sfs2MetadataStore.CancelAcceptedScalar(ref p, ref obj);
        Sfs2MetadataStore.CancelAcceptedScalar(ref p, ref root);
        if (rootBucket.IsNotNull) p.WriteUInt32(rootBucket, 0, 0);
        if (objectBucket.IsNotNull) p.WriteUInt32(objectBucket, 0, 0);
        if (bitmapBucket.IsNotNull) p.WriteUInt32(bitmapBucket, 0, 0);
        if (treeBucket.IsNotNull) p.WriteUInt32(treeBucket, 0, 0);
        p.WriteUInt32(active, 0, oldCount);
        return false;
    }

    private static bool ReservedOriginalAppendCurrent<T>(ref T p, in Sfs2MetadataStore.Handle handle)
        where T : struct, IAmigaGuestMemory =>
        Sfs2MetadataStore.Current(ref p, handle) && p.ReadUInt32(handle.Slot, 12) == 1;

    private static bool OriginalAppendBitsFree<T>(ref T p, APTR bitmap, int offset, uint within, uint count)
        where T : struct, IAmigaGuestMemory
    {
        while (count != 0)
        {
            var take = 32 - within; if (take > count) take = count;
            var mask = (uint.MaxValue >> (int)within) & (uint.MaxValue << (int)(32 - within - take));
            if ((p.ReadUInt32(bitmap, offset) & mask) != mask) return false;
            count -= take; offset += 4; within = 0;
        }
        return true;
    }
}
