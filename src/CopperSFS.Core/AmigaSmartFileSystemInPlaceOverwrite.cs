using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystemWriteCore
{
    /// <summary>Experimental SFS payload policy for a serialized, clean mount.
    /// A handled device failure can have persisted a prefix. Metadata is never
    /// changed here. Unhandled packets use the complete transaction protocol.</summary>
    public static bool TryInPlaceOverwrite<T>(ref T p, ref Work w, ref Request r, APTR group, out bool handled)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        handled = false;
        if (p.RedoOverlay.IsNotNull || w.Buffered.IsNotNull || w.Batch.IsNotNull || r.Length == 0 ||
            r.Length > 65536 || !p.IsMapped(r.Buffer, r.Length)) return true;
        var bytes = p.BlockBytes;
        if (bytes < 512 || bytes > 32768 || (bytes & (bytes - 1)) != 0 ||
            !TrstWorkspaceFits(ref p, ref w, APTR.Null, 0, bytes) ||
            Overlap(r.Buffer, r.Length, w.Meta, bytes) || Overlap(r.Buffer, r.Length, w.Data, bytes) ||
            Overlap(r.Buffer, r.Length, w.Bitmap, bytes) || Overlap(r.Buffer, r.Length, w.Journal, bytes) ||
            group.IsNotNull && (!TrstGroupValid(ref p, group) || p.ReadUInt32(group, 4) != 0 ||
                Overlap(r.Buffer, r.Length, group, p.ReadUInt32(group, 8)))) return true;
        if (!Prepare(ref p, ref w)) { handled = true; return false; }
        var obj = AmigaSmartFileSystemCore.ObjectViewFromRoots(ref p, r.Node, w.RootContainer, w.NodeRoot,
            w.BlockCount, w.BlockBytes, w.Meta, out var at);
        if (obj.IsNull) { handled = true; return Fail(ref w, DOS.Error.DiskNotValidated); }
        var size = p.ReadUInt32(obj, at + 16);
        var protection = p.ReadUInt32(obj, at + 8);
        if ((p.ReadUInt8(obj, at + 24) & 192) != 0 || ((protection ^ 15u) & 4) != 0 ||
            (protection & 16) != 0 || r.Position > size || r.Length > size - r.Position) return true;

        var key = p.ReadUInt32(obj, at + 12);
        var owner = r.Node | 0x80000000u;
        var index = r.Position / w.BlockBytes;
        var within = r.Position % w.BlockBytes;
        var sectors = (within + r.Length + w.BlockBytes - 1) / w.BlockBytes;
        while (key != 0)
        {
            if (!ExtentRecord(ref p, ref w, key, out var length, out var next, out var previous) || previous != owner)
                return true;
            if (index < length)
            {
                if (sectors > length - index) return true;
                break;
            }
            owner = key; key = next; index -= length;
        }
        if (key == 0) return true;
        var first = key + index;
        var end = first + sectors;
        var bits = (w.BlockBytes - 12) * 8;
        var maps = (w.BlockCount - 1) / bits + 1;
        // Exclude complete admin areas, including their as-yet unused slots.
        var admin = w.AdminContainer;
        for (var visits = 0u; admin != 0 && visits < w.BlockCount; visits++)
        {
            var page = View(ref p, ref w, admin, AdminId, w.Meta);
            if (page.IsNull) { handled = true; return false; }
            for (var pos = 24; pos + 8 <= w.BlockBytes; pos += 8)
            {
                var area = p.ReadUInt32(page, pos);
                if (area != 0 && area < end && (first <= area || first - area < 32)) return true;
            }
            admin = p.ReadUInt32(page, 12);
        }
        if (admin != 0) { handled = true; return Fail(ref w, DOS.Error.DiskNotValidated); }
        for (var block = first; block < end;)
        {
            var map = block / bits;
            var bitmap = View(ref p, ref w, w.BitmapBase + map, BitmapId, w.Bitmap);
            if (bitmap.IsNull) { handled = true; return false; }
            var bit = block % bits;
            do
            {
                if (TrimPermanent(ref w, block, maps) || TrimFree(ref p, bitmap, bit)) return true;
                block++; bit++;
            } while (block < end && bit < bits);
        }
        // All checks precede the first write. The lower layer observes queued
        // images on partial reads and marks the device dirty before submission.
        handled = true;
        var copied = 0u;
        while (copied < r.Length)
        {
            var remaining = r.Length - copied;
            var source = APTR.FromPointer(r.Buffer.Raw + copied);
            if (within == 0 && remaining >= w.BlockBytes && (source.Raw & 3) == 0)
            {
                var blocks = remaining / w.BlockBytes;
                if (!p.WriteDataBlocks(first, blocks, source)) return Fail(ref w, DOS.Error.DiskNotValidated);
                first += blocks; copied += blocks * w.BlockBytes;
            }
            else
            {
                var take = w.BlockBytes - within;
                if (take > remaining) take = remaining;
                if (take != w.BlockBytes && !p.ReadBlock(first, w.Data)) return Fail(ref w, DOS.Error.DiskNotValidated);
                p.Copy(source, APTR.FromPointer(w.Data.Raw + within), take);
                if (!p.WriteBlock(first, w.Data)) return Fail(ref w, DOS.Error.DiskNotValidated);
                first++; copied += take; within = 0;
            }
        }
        return true;
    }
}
