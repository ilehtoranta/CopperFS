using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystemWriteCore
{
    // The overwrite continuation and the append route are mutually exclusive.
    // A normal trial, a commit, or a rejected append invalidates the route.
    private static bool TryAppendTrstGroup<T>(ref T p, ref Work w, ref Request r, APTR group,
        APTR active, out bool handled) where T : struct, IAmigaWritableFileSystemPlatform
    {
        handled = false;
        if (w.BlockBytes != 512 || r.Node == 0 || r.Node != p.ReadUInt32(group, TrstGroupObjectHint) ||
            r.Length == 0 || r.Length > 65536 || r.Length > uint.MaxValue - r.Position ||
            ((r.Position | r.Length) & 511) != 0 || (r.Buffer.Raw & 3) != 0 ||
            r.Position == 0 || HasOverwriteReservations(ref p, ref w) ||
            !p.IsMapped(r.Buffer, r.Length) || Overlap(r.Buffer, r.Length, w.Meta, 512) ||
            Overlap(r.Buffer, r.Length, w.Journal, 512)) return true;
        if (!SfsSharedImagePool.ValidLayout(ref p, group, active, p.ReadUInt32(group, 8), 512))
            return Fail(ref w, DOS.Error.DiskNotValidated);

        var objectBlock = p.ReadUInt32(group, TrstGroupObjectHint + 4);
        var at = (int)p.ReadUInt32(group, TrstGroupObjectHint + 8);
        var obj = AmigaSmartFileSystemRedoArena.MetadataView(ref p, active, objectBlock, ObjectId, false, out _);
        if (obj.IsNull || at < 24 || at > 512 - 27 || (at & 1) != 0 ||
            (objectBlock == w.RootContainer && at > 512 - 28 - 27) ||
            p.ReadUInt32(obj, at + 4) != r.Node || p.ReadUInt32(obj, at + 16) != r.Position ||
            p.ReadUInt32(obj, 12) == RecycledNode || (p.ReadUInt8(obj, at + 24) & (128 | 64)) != 0 ||
            (((p.ReadUInt32(obj, at + 8) ^ 15) & (4 | 16)) != 0)) return true;
        var first = p.ReadUInt32(obj, at + 12);
        if (first == 0) return true;

        var route = APTR.FromPointer(group.Raw + TrstGroupProof);
        var retained = p.ReadUInt32(route) == (active.Raw | 1);
        uint key, blocks, leafBlock;
        int extentAt;
        if (retained)
        {
            key = p.ReadUInt32(route, 4); blocks = p.ReadUInt32(route, 8);
            leafBlock = p.ReadUInt32(route, 12); extentAt = (int)p.ReadUInt32(route, 16);
        }
        else
        {
            var eof = r.Position >> 9;
            if (!FileBlock(ref p, ref w, first, eof - 1, out var last, out key, out blocks,
                out var start, out leafBlock, out extentAt)) return false;
            if (last == 0 || blocks != eof - start) return true;
        }
        var length = r.Length >> 9;
        var destination = key + blocks;
        var limit = w.BlockCount - 1;
        var marker = w.RootContainer + 2;
        if (key < w.ReservedBlocks || blocks == 0 || length > ushort.MaxValue - blocks ||
            destination < key || destination >= limit || length > limit - destination ||
            (marker >= destination && marker - destination < length)) return true;

        var leaf = AmigaSmartFileSystemRedoArena.MetadataView(ref p, active, leafBlock, TreeId, false, out _);
        if (leaf.IsNull || extentAt < 16 || extentAt > 512 - 14 || (extentAt & 1) != 0 ||
            p.ReadUInt32(leaf, extentAt) != key || p.ReadUInt32(leaf, extentAt + 4) != 0 ||
            p.ReadUInt16(leaf, extentAt + 12) != blocks) return true;
        if (!BitmapPosition(ref w, destination, out var map, out var offset, out _)) return false;
        var bit = destination & 31;
        if (length > (512u - (uint)offset) * 8 - bit) return true;
        var bitmap = AmigaSmartFileSystemRedoArena.MetadataView(ref p, active, map, BitmapId, false, out _);
        var root = objectBlock == w.RootContainer ? obj :
            AmigaSmartFileSystemRedoArena.MetadataView(ref p, active, w.RootContainer, ObjectId, false, out _);
        if (bitmap.IsNull || root.IsNull || !TrstBitmapRange(ref p, bitmap, offset, bit, length, true)) return true;
        var free = p.ReadUInt32(root, 512 - 28);
        if (free < length || free > w.BlockCount) return true;
        // This admission charges every originally free sector in the touched
        // maps. It survives further edits to this unchanged set of images.
        if (!retained && !TrstGroupHasJournalSpace(ref p, ref w, group, active, out _, allowSearch: false))
            return w.Error == DOS.Error.None;

        p.RedoOverlay = APTR.Null;
        var checkedRange = TrstRangeOriginallyFree(ref p, ref w, destination, length, out var originallyFree);
        var written = checkedRange && originallyFree && (p.SupportsUnpublishedDataWrites
            ? p.WriteUnpublishedDataBlocks(destination, length, r.Buffer)
            : p.WriteDataBlocks(destination, length, r.Buffer));
        p.RedoOverlay = active;
        if (!checkedRange) return false;
        if (!originallyFree) return true;
        if (!written) return Fail(ref w, DOS.Error.DiskNotValidated);

        // These are owned images. No fallible operations, cache lookups or
        // calls into the device follow the first edit. All four remain sealed.
        UpdateTrstBitmapRange(ref p, bitmap, offset, bit, length, false);
        ReplaceHalf(ref p, leaf, extentAt + 12, (ushort)(blocks + length));
        ReplaceWord(ref p, obj, at + 16, r.Position + r.Length);
        ReplaceWord(ref p, root, 512 - 28, free - length);
        p.WriteUInt32(route, 4, key); p.WriteUInt32(route, 8, blocks + length);
        p.WriteUInt32(route, 12, leafBlock); p.WriteUInt32(route, 16, (uint)extentAt);
        p.WriteUInt32(route, 0, active.Raw | 1);
        w.FreeDelta = w.RecycledBlocksDelta = w.RecycledFilesDelta = 0;
        handled = true;
        return true;
    }
}
