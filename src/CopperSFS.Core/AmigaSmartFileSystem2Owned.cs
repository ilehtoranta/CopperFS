using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystem2WriteCore
{
    // A private trial can edit each intersecting bitmap word once. The checked
    // legacy path keeps its existing per-block journal ordering.
    private static bool ChangeOwnedAllocationRun<T>(ref T p, ref Work w, uint first, uint count, bool free)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (count == 0 || first < p.ReservedBlocks || first >= p.BlockCount - 1 || count > p.BlockCount - 1 - first)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        var start = first; var total = count; var bits = (p.BlockBytes - 12) * 8;
        while (count != 0)
        {
            if (!BitmapPosition(ref p, ref w, first, out var map, out var offset, out _) ||
                !EditMetadata(ref p, ref w, map, BitmapId, w.Bitmap, out var edit)) return false;
            var take = bits - first % bits;
            if (take > count) take = count;
            var remaining = take; var within = first & 31;
            while (remaining != 0)
            {
                var span = 32 - within; if (span > remaining) span = remaining;
                var mask = (uint.MaxValue >> (int)within) & (uint.MaxValue << (int)(32 - within - span));
                var word = p.ReadUInt32(edit.Image, offset);
                if ((word & mask) != (free ? 0 : mask)) return RejectMetadata(ref p, ref w, ref edit);
                p.WriteUInt32(edit.Image, offset, free ? word | mask : word & ~mask);
                remaining -= span; offset += 4; within = 0;
            }
            if (!PublishMetadata(ref p, ref w, map, ref edit)) return false;
            w.FreeDelta += free ? (long)take : -(long)take;
            first += take; count -= take;
        }
        if (free && w.TrackFrees)
        {
            if (w.FreedCount != 0 && start == w.FreedFirst + w.FreedCount) w.FreedCount += total;
            else
            {
                if (w.FreedCount != 0) p.ObserveFreedRange(w.FreedFirst, w.FreedCount);
                w.FreedFirst = start; w.FreedCount = total;
            }
        }
        return true;
    }

    private static bool FreeDataRun<T>(ref T p, ref Work w, uint first, uint count)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (w.RedoStaging && p.Sfs2Context.IsNotNull) return ChangeOwnedAllocationRun(ref p, ref w, first, count, true);
        for (var i = 0u; i < count; i++) if (!Mark(ref p, ref w, first + i, true)) return false;
        return true;
    }

    private struct FileInformation
    {
        internal ulong Size;
        internal uint First;
    }

    // Packet-local scalar state replaces a full block copy used only to keep a
    // file record alive while the allocator and extent tree use shared scratch.
    private static bool FileInformationForWrite<T>(ref T p, ref Work w, uint node, out FileInformation file)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        file = default; var handle = default(Sfs2MetadataStore.Handle); int at;
        if (p.Sfs2Context.IsNotNull)
        {
            if (!AmigaSmartFileSystemReadOnlyCore.OwnedObject(ref p, node, w.Meta, false, out handle, out at))
                return Fail(ref w, DOS.Error.DiskNotValidated);
        }
        else
        {
            if (!Sfs2Layout.Object(ref p, node, w.Meta, out at)) return Fail(ref w, DOS.Error.DiskNotValidated);
            handle.Image = w.Meta;
        }
        var image = handle.Image;
        var flags = p.ReadUInt8(image, at + 26); var protection = p.ReadUInt32(image, at + 8);
        file.Size = FileSize(ref p, image, at); file.First = p.ReadUInt32(image, at + 12);
        Sfs2MetadataStore.Release(ref p, ref handle);
        if ((flags & (128 | 64)) != 0) return Fail(ref w, DOS.Error.ObjectWrongType);
        return ((protection ^ 15) & 4) == 0 || Fail(ref w, DOS.Error.WriteProtected);
    }

    private static bool UpdateFileInformation<T>(ref T p, ref Work w, uint node, uint first, ulong size)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var handle = default(Sfs2MetadataStore.Handle); int at;
        if (w.RedoStaging && p.Sfs2Context.IsNotNull)
        {
            if (!AmigaSmartFileSystemReadOnlyCore.OwnedObject(ref p, node, w.Meta, true, out handle, out at))
                return Fail(ref w, p.ReadUInt32(p.RedoOverlay, 12) != 0 ? DOS.Error.NoFreeStore : DOS.Error.DiskNotValidated);
        }
        else
        {
            if (!Sfs2Layout.Object(ref p, node, w.Meta, out at)) return Fail(ref w, DOS.Error.DiskNotValidated);
            handle.Image = w.Meta;
        }
        var image = handle.Image; var block = p.ReadUInt32(image, 8);
        p.WriteUInt32(image, at + 12, first); SetFileSize(ref p, image, at, size);
        p.WriteUInt32(image, at + 8, p.ReadUInt32(image, at + 8) & ~16u);
        return PublishMetadata(ref p, ref w, block, ref handle);
    }
}
