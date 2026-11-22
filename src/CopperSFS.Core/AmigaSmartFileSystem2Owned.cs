using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystem2WriteCore
{
    private static bool InsertOriginalExtentRun<T>(ref T p, ref Work w, uint key, uint previous, uint length)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if ((previous & 0x80000000) != 0)
        {
            var view = Sfs2Layout.View(ref p, w.ExtentRoot, TreeId, w.Meta);
            if (view.IsNull) return Fail(ref w, DOS.Error.DiskNotValidated);
            var count = p.ReadUInt16(view, 12); var terminal = p.ReadUInt8(view, 14) != 0;
            var stride = p.ReadUInt8(view, 15);
            if ((!terminal && count == 0) || stride != (terminal ? 16 : 8) ||
                16u + (uint)count * stride > p.BlockBytes) return Fail(ref w, DOS.Error.DiskNotValidated);
            var fits = terminal && 16u + (uint)(count + 1) * 16 <= p.BlockBytes;
            var index = (int)count;
            for (var i = 0; fits && i < count; i++)
            {
                var existing = p.ReadUInt32(view, 16 + i * 16);
                if (existing == key) return Fail(ref w, DOS.Error.DiskNotValidated);
                if (index == count && existing > key) index = i;
            }
            if (fits)
            {
                if (!EditScalarMetadata(ref p, ref w, w.ExtentRoot, TreeId, w.Meta, out var edit)) return false;
                for (var record = (int)count; record > index; record--)
                    for (var part = 12; part >= 0; part -= 4)
                        Scalar32(ref p, ref edit, 16 + record * 16 + part,
                            p.ReadUInt32(edit.Image, 16 + (record - 1) * 16 + part));
                var at = 16 + index * 16;
                Scalar32(ref p, ref edit, at, key); Scalar32(ref p, ref edit, at + 4, 0);
                Scalar32(ref p, ref edit, at + 8, previous); Scalar32(ref p, ref edit, at + 12, length);
                Scalar16(ref p, ref edit, 12, (ushort)(count + 1));
                return PublishMetadata(ref p, ref w, w.ExtentRoot, ref edit);
            }
        }
        // Linked, full and branch nodes retain the checked inserter.
        return InsertExtent(ref p, ref w, key, previous) &&
            (length == 1 || GrowExtentRun(ref p, ref w, key, key + 1, length - 1));
    }

    private static bool GrowOriginalExtent<T>(ref T p, ref Work w, uint key, uint nextBlock, uint added)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var leaf = w.ExtentRoot;
        for (var depth = 0u; depth < p.BlockCount; depth++)
        {
            var view = Sfs2Layout.View(ref p, leaf, TreeId, w.Meta);
            if (view.IsNull) return Fail(ref w, DOS.Error.DiskNotValidated);
            var count = p.ReadUInt16(view, 12); var terminal = p.ReadUInt8(view, 14) != 0;
            var stride = p.ReadUInt8(view, 15);
            if ((!terminal && count == 0) || stride != (terminal ? 16 : 8) ||
                16u + (uint)count * stride > p.BlockBytes) return Fail(ref w, DOS.Error.DiskNotValidated);
            if (!terminal)
            {
                var selected = 16;
                for (var i = 1; i < count && p.ReadUInt32(view, 16 + i * stride) <= key; i++) selected = 16 + i * stride;
                leaf = p.ReadUInt32(view, selected + 4);
                if (leaf == 0 || leaf >= p.BlockCount) return Fail(ref w, DOS.Error.DiskNotValidated);
                continue;
            }
            var at = 16;
            while (at < 16 + count * stride && p.ReadUInt32(view, at) != key) at += stride;
            if (at == 16 + count * stride || !EditScalarMetadata(ref p, ref w, leaf, TreeId, w.Meta, out var edit))
                return Fail(ref w, w.Error != DOS.Error.None ? w.Error : DOS.Error.DiskNotValidated);
            // The temporary view expires at EditMetadata; inspect only the
            // pinned private image from this point onward.
            var length = p.ReadUInt32(edit.Image, at + 12);
            if (p.ReadUInt32(edit.Image, at) != key || added == 0 || added > uint.MaxValue - length ||
                (ulong)key + length != nextBlock) return RejectMetadata(ref p, ref w, ref edit);
            Scalar32(ref p, ref edit, at + 12, length + added);
            return PublishMetadata(ref p, ref w, leaf, ref edit);
        }
        return Fail(ref w, DOS.Error.DiskNotValidated);
    }

    // A complete existing-data packet in one checked extent has no metadata
    // delta. The original-data policy can retain the acknowledged arena instead
    // of cloning/publishing it. All other packets use the ordinary private trial.
    private static bool TryWriteUnchangedOriginalPacket<T>(ref T p, ref Work w, ref Request r,
        APTR group, APTR active, uint span, uint state, out uint actual, out bool prepared, out FileInformation info,
        APTR originalCursor, out uint appendExtentOffset)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        actual = 0; prepared = false; info = default; appendExtentOffset = 0;
        var bytes = p.BlockBytes;
        if (r.Length == 0 || r.Length > int.MaxValue || !p.IsMapped(r.Buffer, r.Length)) return false;
        if (!Prepare(ref p, ref w) ||
            (!OriginalFileInformation(ref p, originalCursor, group, active, r.Node, out info, out appendExtentOffset) &&
                !FileInformationForWrite(ref p, ref w, r.Node, out info)))
            return Fail(ref w, w.Error != DOS.Error.None ? w.Error : DOS.Error.DiskNotValidated);
        prepared = true;
        var index = AmigaSfsGeometryMath.DivRem(r.Position, bytes, out var within);
        if (within != 0 || r.Length % bytes != 0) return false;
        if ((info.Protection & 16) != 0 || r.Position > info.Size || r.Length > info.Size - r.Position) return false;
        if (!FileBlock(ref p, ref w, info.First, index, out var block, out var key, out var size)) return false;
        var count = r.Length / bytes;
        if (block == 0 || count > size - (block - key)) return false;
        var cache = Sfs2MountContext.Cache(ref p, p.Sfs2Context);
        if ((cache.IsNotNull && !Sfs2MetadataStore.Unpinned(ref p, cache)) ||
            (state == 1 && !Sfs2MetadataStore.Unpinned(ref p, active))) return Fail(ref w, DOS.Error.DiskNotValidated);
        if (state == 0)
        {
            var epoch = Sfs2MountContext.NextAttempt(ref p, p.Sfs2Context);
            if (epoch == 0 || !Sfs2MetadataStore.InitializeEmpty(ref p, active, bytes, epoch))
                return Fail(ref w, DOS.Error.NoFreeStore);
            p.WriteUInt32(active, 24, Sfs2MetadataStore.Trial);
            SetTrstOverlay(ref p, active);
        }
        w.RedoStaging = true;
        var ok = TryWriteOriginalFileData(ref p, ref w, block, count, r.Buffer, out var written);
        w.RedoStaging = false;
        if (!ok || !written)
        {
            if (state == 0) SetTrstOverlay(ref p, APTR.Null);
            return false;
        }
        if (!(state == 0 ? Sfs2MetadataStore.Publish(ref p, active) : Sfs2MetadataStore.Unpinned(ref p, active)) ||
            !Sfs2MountContext.Accept(ref p, active))
        {
            if (state == 0) SetTrstOverlay(ref p, APTR.Null);
            return Fail(ref w, DOS.Error.DiskNotValidated);
        }
        if (state == 0) p.WriteUInt32(group, 28, 0);
        p.WriteUInt32(group, 4, 1);
        if (originalCursor.IsNotNull)
        {
            p.WriteUInt32(originalCursor, 0, 0);
            if (index <= uint.MaxValue && OriginalDataRangeAvailable(ref p, active, key, size))
                RememberOriginalDataCursor(ref p, originalCursor, group, active, r.Node, info.Size,
                    (uint)index - (block - key), key, size, info.First, info.ObjectBlock, info.ObjectOffset);
        }
        actual = r.Length; return true;
    }

    private static bool TryWriteOriginalFileData<T>(ref T p, ref Work w, uint first, uint count, APTR source, out bool written)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        written = false;
        if (!w.RedoStaging || p.Sfs2Context.IsNull || !p.UsesOriginalSfs2DataWrites) return true;
        // A replacement may still belong to a different durable owner. Such
        // data already has a private image and must retain full-data staging.
        // The trial includes every accepted image, including earlier packets.
        var arena = p.RedoOverlay;
        if (!OriginalDataRangeAvailable(ref p, arena, first, count)) return true;
        if (!p.WriteExistingFileDataBlocks(first, count, source)) return Fail(ref w, DOS.Error.DiskNotValidated);
        written = true; return true;
    }

    private static bool OriginalDataRangeAvailable<T>(ref T p, APTR arena, uint first, uint count)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!Sfs2MetadataStore.IsStore(ref p, arena)) return false;
        var images = p.ReadUInt32(arena);
        if (count < images)
        {
            for (var i = 0u; i < count; i++)
                if (Sfs2MetadataStore.Find(ref p, arena, first + i).IsNotNull) return false;
        }
        else
        {
            // A bulk overwrite usually has only a few metadata images, often
            // none. Check those keys once instead of probing every data sector.
            var slot = arena.Raw + p.ReadUInt32(arena, 20);
            var stride = p.BlockBytes + Sfs2MetadataStore.SlotBytes;
            for (var i = 0u; i < images; i++, slot += stride)
            {
                var key = p.ReadUInt32(APTR.FromPointer(slot)) & 0x7FFFFFFF;
                if (key >= first && key - first < count) return false;
            }
        }
        return true;
    }

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
                !EditScalarMetadata(ref p, ref w, map, BitmapId, w.Bitmap, out var edit)) return false;
            var take = bits - first % bits;
            if (take > count) take = count;
            var remaining = take; var within = first & 31;
            while (remaining != 0)
            {
                var span = 32 - within; if (span > remaining) span = remaining;
                var mask = (uint.MaxValue >> (int)within) & (uint.MaxValue << (int)(32 - within - span));
                var word = p.ReadUInt32(edit.Image, offset);
                if ((word & mask) != (free ? 0 : mask)) return RejectMetadata(ref p, ref w, ref edit);
                Scalar32(ref p, ref edit, offset, free ? word | mask : word & ~mask);
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
        internal uint First, Protection, ObjectBlock, ObjectOffset;
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
        file.Protection = protection;
        file.Size = FileSize(ref p, image, at); file.First = p.ReadUInt32(image, at + 12);
        file.ObjectBlock = p.ReadUInt32(image, 8); file.ObjectOffset = (uint)at;
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

    // Allocation and extent edits cannot move this packet's object record.
    // Reuse its checked location rather than walking the node index again.
    private static bool UpdateWriteInformation<T>(ref T p, ref Work w, uint node,
        ref FileInformation info, uint first, ulong size) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!w.RedoStaging || p.Sfs2Context.IsNull || !p.UsesOriginalSfs2DataWrites || info.ObjectBlock == 0)
            return UpdateFileInformation(ref p, ref w, node, first, size);
        if (info.ObjectOffset < 24 || info.ObjectOffset > p.BlockBytes - 27 ||
            !EditScalarMetadata(ref p, ref w, info.ObjectBlock, ObjectId, w.Meta, out var edit))
            return Fail(ref w, w.Error != DOS.Error.None ? w.Error : DOS.Error.DiskNotValidated);
        var at = (int)info.ObjectOffset; var image = edit.Image;
        if (p.ReadUInt32(image, at + 4) != node || (p.ReadUInt8(image, at + 26) & (128 | 64)) != 0)
            return RejectMetadata(ref p, ref w, ref edit);
        Scalar32(ref p, ref edit, at + 12, first);
        Scalar32(ref p, ref edit, at + 16, (uint)(size >> 16));
        Scalar16(ref p, ref edit, at + 20, (ushort)size);
        Scalar32(ref p, ref edit, at + 8, p.ReadUInt32(image, at + 8) & ~16u);
        return PublishMetadata(ref p, ref w, info.ObjectBlock, ref edit);
    }
}
