using Amiga;

namespace CopperStart.Dos;

public static partial class AmigaFfsWriteCore
{
    // New FFS headers and extensions are cleared before their first pointer
    // is installed. During an append group only the fields below can change.
    // A generic header update or a table read withdraws that proof. Existing
    // filesystem images can contain nonzero unused words and need a full sum.
    private static void SealFileImage<T>(ref T p, APTR index, uint key, APTR image, bool header)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var identity = header ? AmigaFfsMountIndex.FreshHeaderImage : AmigaFfsMountIndex.FreshTableImage;
        if (p.ReadUInt32(index, identity) != image.Raw || p.ReadUInt32(index, identity + 4) != key)
        {
            Seal(ref p, image, 20);
            return;
        }
        var bytes = p.BlockBytes;
        var tail = (int)bytes - 512;
        var slots = p.ReadUInt32(image, 8);
        var sum = unchecked(p.ReadUInt32(image) + key + slots + p.ReadUInt32(image, 12) +
            p.ReadUInt32(image, tail + 500) + p.ReadUInt32(image, tail + 504) + p.ReadUInt32(image, tail + 508));
        if (header)
        {
            sum = unchecked(sum + p.ReadUInt32(image, 16) + p.ReadUInt32(image, tail + 324) + p.ReadUInt32(image, tail + 496));
            var nameBytes = (p.ReadUInt8(image, tail + 432) + 4) & ~3;
            for (var offset = 0; offset < nameBytes; offset += 4)
                sum = unchecked(sum + p.ReadUInt32(image, tail + 432 + offset));
        }
        sum = unchecked(sum + p.ReadUInt32(index, header ? AmigaFfsMountIndex.FreshHeaderPointerSum : AmigaFfsMountIndex.FreshTablePointerSum));
        p.WriteUInt32(image, 20, unchecked(0u - sum));
    }

    internal static bool TryBufferAppend<T>(ref T p, APTR index, ref Request r, out DOS.Error error, bool protectionChecked = false)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        error = DOS.Error.None;
        var blockBytes = p.BlockBytes;
        var type = p.ReadUInt32(index, AmigaFfsMountIndex.Type);
        if (r.Length == 0 || r.Length > blockBytes * 2 || type >= 0x444F5304 ||
            r.Header != p.ReadUInt32(index, AmigaFfsMountIndex.Header)) return false;
        var size = p.ReadUInt32(index, AmigaFfsMountIndex.FileBytes);
        var pending = p.ReadUInt32(index, AmigaFfsMountIndex.PendingAppend);
        if (r.Position != size + pending || r.Length > uint.MaxValue - r.Position) return false;
        if (pending == 0)
        {
            // Draining a partial file sector consumes a prefix of the buffer.
            // Align the first whole FFS sector for DMA or the first whole OFS
            // payload for the wordwise copy and checksum. OFS payload sizes
            // are multiples of four, including the 488-byte floppy payload.
            var storage = p.ReadUInt32(index, AmigaFfsMountIndex.AppendStorage);
            var bytes = p.ReadUInt32(index, AmigaFfsMountIndex.AppendStorageBytes);
            var pad = (size - storage) & ((type & 1) != 0 ? blockBytes - 1 : 3u);
            if (pad >= bytes || r.Length > bytes - pad) pad = 0;
            p.WriteUInt32(index, AmigaFfsMountIndex.AppendImage, storage + pad);
            p.WriteUInt32(index, AmigaFfsMountIndex.AppendCapacity, bytes - pad);
        }
        var capacity = p.ReadUInt32(index, AmigaFfsMountIndex.AppendCapacity);
        if (r.Length > capacity - pending) return false;
        // No competing allocator can run until the buffer drains. If the
        // whole buffer fits, subsequent packets already have space for
        // their data and pointer tables, including a partial last sector.
        if (pending == 0 || pending + r.Length > p.ReadUInt32(index, AmigaFfsMountIndex.AppendAdmitted))
        {
            var payload = (type & 1) == 0 ? blockBytes - 24 : blockBytes;
            var entries = blockBytes / 4 - 56;
            var max = capacity > uint.MaxValue - size ? uint.MaxValue - size : capacity;
            var admitted = 0u;
            if (AppendFits(ref p, index, size + max, payload, entries, out error) && error == DOS.Error.None)
                admitted = max;
            else if (!AppendFits(ref p, index, r.Position + r.Length, payload, entries, out error)) return false;
            else if (error != DOS.Error.None) return true;
            p.WriteUInt32(index, AmigaFfsMountIndex.AppendAdmitted, admitted);
        }
        if (!p.IsMapped(r.Buffer, r.Length)) error = DOS.Error.BadNumber;
        else if (!protectionChecked && p.WriteProtected()) error = DOS.Error.DiskWriteProtected;
        else
        {
            var target = APTR.FromPointer(p.ReadUInt32(index, AmigaFfsMountIndex.AppendImage) + pending);
            p.Copy(r.Buffer, target, r.Length);
            p.WriteUInt32(index, AmigaFfsMountIndex.PendingAppend, pending + r.Length);
        }
        return true;
    }

    // The first short append to an existing file has just passed the same
    // ownership, space and protection checks as a write-through packet.
    // Keep it with subsequent appends instead of rewriting the old tail twice.
    private static bool TryStartBufferedAppend<T>(ref T p, ref Work w, ref Request r, in AmigaFfsGeometry g,
        uint size, out uint actual) where T : struct, IAmigaWritableFileSystemPlatform
    {
        actual = 0;
        if (w.GroupHeader == 0 || r.Position != size || r.Length > g.BlockBytes * 2 ||
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.Status) != AmigaFfsMountIndex.Clean ||
            !TryBufferAppend(ref p, w.Index, ref r, out w.Error, protectionChecked: true)) return false;
        if (w.Error != DOS.Error.None) return true;
        // FileBytes describes the materialized chain. The mount adds
        // PendingAppend when it serves the open handle's logical length.
        w.RestoreSize = size;
        if (!Begin(ref p, ref w, in g, keepRuns: true) || !Finish(ref p, ref w, in g))
        {
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.Status, AmigaFfsMountIndex.GroupFailed);
            return true;
        }
        actual = r.Length;
        return true;
    }

    private static bool AppendFits<T>(ref T p, APTR index, uint end, uint payload, uint entries, out DOS.Error error)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var blocks = AmigaGeometryMath.Divide(end - 1, payload) + 1;
        var tables = AmigaGeometryMath.Divide(blocks - 1, entries) + 1;
        var capacity = p.ReadUInt32(index, AmigaFfsMountIndex.Capacity);
        var needed = blocks - p.ReadUInt32(index, AmigaFfsMountIndex.FileBlocks) +
            tables - p.ReadUInt32(index, AmigaFfsMountIndex.Tables);
        error = needed > p.BlockCount - p.ReadUInt32(index, AmigaFfsMountIndex.Used) ? DOS.Error.DiskFull : DOS.Error.None;
        return blocks <= capacity && tables <= capacity - blocks;
    }

    // Finish the last buffered FFS sector with the start of the next packet.
    // The device can then consume the sector-aligned remainder directly.
    private static bool CompleteBufferedSector<T>(ref T p, ref Work w, ref Request r, uint pending, out uint taken)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        taken = 0;
        var index = w.Index;
        var bytes = p.BlockBytes;
        var fill = unchecked(0u - r.Position) & (bytes - 1);
        if (fill == 0 || r.Length < bytes * 2 || r.Length > int.MaxValue ||
            r.Length > uint.MaxValue - r.Position || r.Header != p.ReadUInt32(index, AmigaFfsMountIndex.Header) ||
            p.ReadUInt32(index, AmigaFfsMountIndex.Type) is not (0x444F5301 or 0x444F5303) ||
            r.Position != p.ReadUInt32(index, AmigaFfsMountIndex.FileBytes) + pending ||
            fill > p.ReadUInt32(index, AmigaFfsMountIndex.AppendCapacity) - pending) return true;
        // The caller can supply guest memory near the mount's workspace.
        // Keep the ordinary drain order when it could change the input.
        if (r.Buffer.Raw < index.Raw ? index.Raw - r.Buffer.Raw < r.Length :
            r.Buffer.Raw - index.Raw < p.ReadUInt32(index, AmigaFfsMountIndex.Bytes)) return true;
        if (!AppendFits(ref p, index, r.Position + r.Length, bytes, bytes / 4 - 56, out var error)) return true;
        if (error != DOS.Error.None) return Fail(ref w, error);
        if (!p.IsMapped(r.Buffer, r.Length)) return Fail(ref w, DOS.Error.BadNumber);
        if (p.WriteProtected()) return Fail(ref w, DOS.Error.DiskWriteProtected);
        var target = APTR.FromPointer(p.ReadUInt32(index, AmigaFfsMountIndex.AppendImage) + pending);
        p.Copy(r.Buffer, target, fill);
        p.WriteUInt32(index, AmigaFfsMountIndex.PendingAppend, pending + fill);
        taken = fill;
        return true;
    }

    private static bool DrainBufferedAppend<T>(ref T p, ref Work w)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var index = w.Index;
        var bytes = p.ReadUInt32(index, AmigaFfsMountIndex.PendingAppend);
        if (bytes == 0) return true;
        var request = new Request
        {
            Header = p.ReadUInt32(index, AmigaFfsMountIndex.Header),
            Position = p.ReadUInt32(index, AmigaFfsMountIndex.FileBytes),
            Buffer = APTR.FromPointer(p.ReadUInt32(index, AmigaFfsMountIndex.AppendImage)),
            Length = bytes
        };
        var grouped = w.GroupFileWrites;
        w.GroupFileWrites = true;
        p.WriteUInt32(index, AmigaFfsMountIndex.PendingAppend, 0);
        var ok = WriteThrough(ref p, ref w, ref request, out var written, true);
        w.GroupFileWrites = grouped;
        if (ok && written == bytes) return true;
        p.WriteUInt32(index, AmigaFfsMountIndex.Status, AmigaFfsMountIndex.GroupFailed);
        if (w.Error == DOS.Error.None) w.Error = DOS.Error.DiskNotValidated;
        return false;
    }

    // The verified map supplies the whole file order. Allocate and write a
    // physical run even when it crosses an FFS pointer-table boundary, then
    // publish the newly populated tables from the end back to the header.
    private static bool AppendFfsBlocks<T>(ref T p, ref Work w, ref Request r, in AmigaFfsGeometry g,
        APTR keys, APTR tables, uint oldBlocks, uint newBlocks, uint oldTail, uint tail, uint lastData,
        out uint actual, bool resumed = false)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        actual = 0;
        // Admission already covers the full request, including its final data
        // block and extension table. Keep a partial last sector until the next
        // packet supplies its suffix or an observer publishes the group.
        var buffered = w.RestoreSize & (g.BlockBytes - 1);
        if (buffered != 0)
        {
            if (!w.BufferSmallAppends || !w.GroupFileWrites || w.GroupHeader == 0 || w.RootImage.IsNull ||
                !w.WritingIndexedFile || w.RestoreFile == 0 || w.DosType >= 0x444F5304 ||
                newBlocks - oldBlocks < 2 || p.ReadUInt32(w.Index, AmigaFfsMountIndex.AppendStorageBytes) < 2 * g.BlockBytes ||
                (r.Buffer.Raw < w.Index.Raw ? w.Index.Raw - r.Buffer.Raw < r.Length :
                    r.Buffer.Raw - w.Index.Raw < p.ReadUInt32(w.Index, AmigaFfsMountIndex.Bytes)))
                buffered = 0;
            else
            {
                newBlocks--;
                // The deferred block may have been the only one in its table.
                if (tail != 0 && newBlocks == tail * g.TableEntries) tail--;
                w.RestoreSize -= buffered;
            }
        }
        var committedHeader = p.ReadUInt32(w.Index, AmigaFfsMountIndex.PublishedHeader);
        var committedTable = p.ReadUInt32(w.Index, AmigaFfsMountIndex.PublishedTable);
        if (!Begin(ref p, ref w, in g, keepRuns: true)) return false;
        if (w.GroupHeader == r.Header && committedHeader == r.Header)
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.PendingHeader, r.Header);
        if (w.GroupHeader != 0 && newBlocks != oldBlocks && oldTail != 0 && committedTable != 0 &&
            newBlocks <= (oldTail + 1) * g.TableEntries &&
            committedTable == p.ReadUInt32(tables, (int)oldTail * 4))
        {
            if (oldTail == 1 && p.ReadUInt32(w.Index, AmigaFfsMountIndex.FirstExtensionImage) != 0)
                MoveGroupImage(ref p, w.Index, AmigaFfsMountIndex.PendingTable, AmigaFfsMountIndex.FirstExtension, committedTable);
            else p.WriteUInt32(w.Index, AmigaFfsMountIndex.PendingTable, committedTable);
        }
        var publishedTail = 0u;
        if (resumed && w.GroupHeader == r.Header)
        {
            // Both images were published at the sibling's close. The next
            // append can edit them in place after invalidating the root.
            if (p.ReadUInt32(w.Index, AmigaFfsMountIndex.PublishedPreviousHeader) == r.Header)
                MoveGroupImage(ref p, w.Index, AmigaFfsMountIndex.PreviousHeader, AmigaFfsMountIndex.PendingHeader, r.Header);
            publishedTail = p.ReadUInt32(w.Index, AmigaFfsMountIndex.PublishedPreviousTable);
        }
        var logical = oldBlocks;
        var tableTail = g.Tail;
        var preferred = (logical == 0 ? r.Header : lastData) + 1;
        var source = r.Buffer.Raw;
        var remaining = r.Length - buffered;
        var runStart = newBlocks;
        uint runPhysical = 0;
        var within = r.Position & (g.BlockBytes - 1);
        if (within != 0)
        {
            // A short append can end in the same sector. Only its newly
            // admitted bytes change; keep the checked logical mapping.
            var take = g.BlockBytes - within;
            if (take > remaining) take = remaining;
#if COPPERFS_FFS_HANDLER
            if (p.TryPatchClassicSector(lastData, within, r.Buffer, take, out var error))
            {
                if (error != DOS.Error.None) return Fail(ref w, error);
            }
            else
#endif
            {
                if (!p.ReadBlock(lastData, w.Data)) return Fail(ref w, DOS.Error.DiskNotValidated);
                p.Copy(r.Buffer, APTR.FromPointer(w.Data.Raw + within), take);
                if (!p.WriteDataBlocks(lastData, 1, w.Data)) return Fail(ref w, DOS.Error.DiskNotValidated);
            }
            source += take;
            remaining -= take;
        }
        while (logical < newBlocks)
        {
            var first = AllocateFfsRun(ref p, ref w, in g, preferred, newBlocks - logical, out var count);
            if (first == 0) return false;
            var full = count;
            if (remaining < count * g.BlockBytes) full--;
            if (full != 0 && !p.WriteDataBlocks(first, full, APTR.FromPointer(source))) return Fail(ref w, DOS.Error.DiskNotValidated);
            source += full * g.BlockBytes;
            remaining -= full * g.BlockBytes;
            if (full != count)
            {
                p.Copy(APTR.FromPointer(source), w.Data, remaining);
                p.Clear(APTR.FromPointer(w.Data.Raw + remaining), g.BlockBytes - remaining);
                if (!p.WriteDataBlocks(first + full, 1, w.Data)) return Fail(ref w, DOS.Error.DiskNotValidated);
            }
            // The last run can populate the mount index and the on-disk
            // reversed table in one pass. Earlier runs must be materialized
            // before another allocation can interrupt the physical sequence.
            if (count == newBlocks - logical) { runStart = logical; runPhysical = first; }
            else WriteRunKeys(ref p, APTR.FromPointer(keys.Raw + logical * 4), first, count);
            AmigaFfsMountIndex.RememberRun(ref p, w.Index, r.Header, logical, first, count);
            logical += count;
            preferred = first + count;
        }
        // Only the first two extensions can share the two retained sectors
        // at publication. Allocate that pair child-first when both are new.
        // Keep later extension allocations in their ordinary physical order.
        var adjacentPair = oldTail == 0 && tail >= 2 && w.GroupHeader != 0 &&
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.FirstExtensionImage) != 0;
        for (var table = oldTail + 1; table <= tail; table++)
        {
            var key = Allocate(ref p, ref w, in g);
            if (key == 0) return false;
            var ordinal = adjacentPair && table <= 2 ? 3 - table : table;
            p.WriteUInt32(tables, (int)ordinal * 4, key);
        }

        uint successor = 0;
        for (var table = tail; newBlocks != oldBlocks || table == 0; table--)
        {
            var key = p.ReadUInt32(tables, (int)table * 4);
            var image = w.Meta;
            var pending = AmigaFfsMountIndex.PendingHeader;
            var cached = false;
            if (w.GroupHeader != 0)
            {
                if (key != w.GroupHeader)
                {
                    pending = table == 1 && p.ReadUInt32(w.Index, AmigaFfsMountIndex.FirstExtensionImage) != 0
                        ? AmigaFfsMountIndex.FirstExtension : AmigaFfsMountIndex.PendingTable;
                    // A generic partial write can already own this extension.
                    if (pending == AmigaFfsMountIndex.FirstExtension && p.ReadUInt32(w.Index, AmigaFfsMountIndex.PendingTable) == key)
                        SaveGroupImage(ref p, w.Index, AmigaFfsMountIndex.PendingTable, pending);
                }
                if (publishedTail == key && table == oldTail && pending != AmigaFfsMountIndex.PendingHeader)
                {
                    // A higher new extension must be published before changing
                    // its predecessor. The sealed predecessor stayed clean
                    // until this iteration and needs just its final write.
                    if (pending == AmigaFfsMountIndex.PendingTable && !PublishGroupTable(ref p, ref w)) return false;
                    MoveGroupImage(ref p, w.Index, AmigaFfsMountIndex.PreviousTable, pending, key);
                }
                var retained = APTR.FromPointer(p.ReadUInt32(w.Index, pending + 4));
                if (retained.IsNotNull)
                {
                    cached = p.ReadUInt32(w.Index, pending) == key;
                    if (!cached && pending == AmigaFfsMountIndex.PendingTable && !PublishGroupTable(ref p, ref w)) return false;
                    image = retained;
                }
            }
            if (table == oldTail)
            {
                if (!cached)
                {
                    if (image != w.Meta)
                        p.WriteUInt32(w.Index, table == 0 ? AmigaFfsMountIndex.FreshHeaderImage : AmigaFfsMountIndex.FreshTableImage, 0);
                    if (!p.ReadBlock(key, image)) return Fail(ref w, DOS.Error.DiskNotValidated);
                }
            }
            else
            {
                p.Clear(image, g.BlockBytes);
                p.WriteUInt32(image, 0, 16);
                p.WriteUInt32(image, 4, key);
                p.WriteUInt32(image, 12, g.TableEntries);
                p.WriteUInt32(image, tableTail + 500, r.Header);
                p.WriteUInt32(image, tableTail + 508, 0xFFFFFFFD);
                if (image != w.Meta)
                {
                    p.WriteUInt32(w.Index, AmigaFfsMountIndex.FreshTableImage, image.Raw);
                    p.WriteUInt32(w.Index, AmigaFfsMountIndex.FreshTableKey, key);
                    p.WriteUInt32(w.Index, AmigaFfsMountIndex.FreshTablePointerSum, 0);
                }
            }
            var start = table * g.TableEntries;
            var slots = newBlocks - start;
            if (slots > g.TableEntries) slots = g.TableEntries;
            var slot = oldBlocks > start ? oldBlocks - start : 0;
            var target = APTR.FromPointer(image.Raw + (uint)(tableTail + 308) - slot * 4);
            var firstLogical = start + slot;
            var keyAddress = APTR.FromPointer(keys.Raw + firstLogical * 4);
            var count = slots - slot;
            uint added = 0;
            if (firstLogical < runStart)
            {
                var prefix = runStart - firstLogical;
                if (prefix > count) prefix = count;
                added = CopyTableKeys(ref p, keyAddress, target, prefix);
                count -= prefix;
                firstLogical += prefix;
                keyAddress = APTR.FromPointer(keyAddress.Raw + prefix * 4);
                target = APTR.FromPointer(target.Raw - prefix * 4);
            }
            if (count != 0)
                added = unchecked(added + WriteTableRun(ref p, keyAddress, target, runPhysical + (firstLogical - runStart), count));
            var identity = table == 0 ? AmigaFfsMountIndex.FreshHeaderImage : AmigaFfsMountIndex.FreshTableImage;
            if (p.ReadUInt32(w.Index, identity) == image.Raw && p.ReadUInt32(w.Index, identity + 4) == key)
            {
                var sumField = table == 0 ? AmigaFfsMountIndex.FreshHeaderPointerSum : AmigaFfsMountIndex.FreshTablePointerSum;
                p.WriteUInt32(w.Index, sumField, unchecked(p.ReadUInt32(w.Index, sumField) + added));
            }
            p.WriteUInt32(image, 8, slots);
            p.WriteUInt32(image, tableTail + 504, successor);
            if (table == 0)
            {
                if (oldBlocks == 0) p.WriteUInt32(image, 16, p.ReadUInt32(keys));
                w.CacheParent = p.ReadUInt32(image, tableTail + 500);
                p.WriteUInt32(image, tableTail + 324, w.RestoreSize);
            }
            if (image != w.Meta) p.WriteUInt32(w.Index, pending, key);
            else if (!Put(ref p, ref w, key, image, 20)) return false;
            successor = key;
            if (table == oldTail) break;
        }
        if (oldTail != 0)
        {
            if (w.GroupHeader == r.Header && p.ReadUInt32(w.Index, AmigaFfsMountIndex.PendingHeader) == r.Header)
            {
                // Growth beyond the first table only changes the file size
                // in this private header. Close seals it after its extensions.
                var header = APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.HeaderImage));
                w.CacheParent = p.ReadUInt32(header, tableTail + 500);
                p.WriteUInt32(header, tableTail + 324, w.RestoreSize);
            }
            else
            {
                if (!Header(ref p, ref w, r.Header)) return false;
                SetFinalSize(ref p, ref w, tableTail, w.RestoreSize);
                if (!Put(ref p, ref w, r.Header, w.Meta, 20)) return false;
            }
        }
        p.WriteUInt32(w.Index, AmigaFfsMountIndex.FileBlocks, newBlocks);
        p.WriteUInt32(w.Index, AmigaFfsMountIndex.Tables, tail + 1);
        actual = r.Length - buffered;
        if (!Finish(ref p, ref w, in g)) return false;
        if (buffered != 0)
        {
            var storage = p.ReadUInt32(w.Index, AmigaFfsMountIndex.AppendStorage);
            var pad = unchecked(0u - storage) & (g.BlockBytes - 1);
            p.Copy(APTR.FromPointer(r.Buffer.Raw + r.Length - buffered), APTR.FromPointer(storage + pad), buffered);
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.AppendImage, storage + pad);
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.AppendCapacity, p.ReadUInt32(w.Index, AmigaFfsMountIndex.AppendStorageBytes) - pad);
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.AppendAdmitted, buffered);
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.PendingAppend, buffered);
        }
        actual = r.Length;
        return true;
    }

    // Small extensions can find their last pointer table without a native
    // software division. Larger files retain the general calculation.
    private static uint AppendTailTable(uint blocks, uint entries)
    {
        if (blocks <= entries) return 0;
        var beyondFirst = blocks - entries;
        if (beyondFirst <= entries) return 1;
        if (beyondFirst - entries <= entries) return 2;
        return AmigaGeometryMath.Divide(blocks - 1, entries);
    }

    private static uint AllocateFfsRun<T>(ref T p, ref Work w, in AmigaFfsGeometry g,
        uint preferred, uint wanted, out uint count) where T : struct, IAmigaWritableFileSystemPlatform
    {
        count = 0;
        if (preferred >= g.ReservedBlocks && preferred < g.BlockCount)
        {
            if (!LoadBitmap(ref p, ref w, preferred)) return 0;
            count = TakeFreePrefix(ref p, ref w, preferred, wanted, g.RootBlock);
            if (count != 0) return preferred;
        }
        uint next = 0, remaining = 0;
        var first = AllocateData(ref p, ref w, in g, preferred, wanted, ref next, ref remaining);
        if (first != 0) count = 1 + TakeFreePrefix(ref p, ref w, first + 1, wanted - 1, g.RootBlock);
        return first;
    }

    // Consume adjacent free bits directly from their verified page. Each word
    // supplies both the run length and the exact checksum and free-count delta.
    private static uint TakeFreePrefix<T>(ref T p, ref Work w, uint first, uint wanted, uint root)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var bit = first - w.BitmapFirst;
        if (bit >= w.BitmapCount) return 0;
        if (wanted > w.BitmapCount - bit) wanted = w.BitmapCount - bit;
        if (root - first < wanted) wanted = root - first;
        var taken = 0u;
        uint delta = 0;
        while (taken < wanted)
        {
            var shift = (int)(bit & 31);
            var offset = 4 + (int)(bit >> 5) * 4;
            var word = p.ReadUInt32(w.ActiveBitmap, offset);
            var take = 32u - (uint)shift;
            if (wanted - taken < take) take = wanted - taken;
            var mask = (uint.MaxValue >> (int)(32 - take)) << shift;
            var occupied = ~word & mask;
            if (occupied != 0)
            {
                take = AmigaBitmapWordCore.FirstSet(occupied) - (uint)shift;
                if (take == 0) break;
                mask = (uint.MaxValue >> (int)(32 - take)) << shift;
            }
            p.WriteUInt32(w.ActiveBitmap, offset, word & ~mask);
            delta = unchecked(delta + mask);
            taken += take;
            bit += take;
            if (occupied != 0) break;
        }
        if (taken != 0)
        {
            w.BitmapChecksumDelta = unchecked(w.BitmapChecksumDelta + delta);
            w.IndexedUsed += taken;
            if (w.NextKey - first < taken) w.NextKey = first + taken;
            w.BitmapDirty = 1;
        }
        return taken;
    }

    private static void WriteRunKeys<T>(ref T p, APTR destination, uint first, uint count)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var target = destination;
        while (count >= 4)
        {
            p.WriteUInt32(target, 0, first);
            p.WriteUInt32(target, 4, first + 1);
            p.WriteUInt32(target, 8, first + 2);
            p.WriteUInt32(target, 12, first + 3);
            target = APTR.FromPointer(target.Raw + 16);
            first += 4;
            count -= 4;
        }
        while (count != 0)
        {
            p.WriteUInt32(target, 0, first++);
            target = APTR.FromPointer(target.Raw + 4);
            count--;
        }
    }

    // The mount's logical order and the disk's reversed table use the same
    // keys. Generate each key once and accumulate its checksum contribution.
    private static uint WriteTableRun<T>(ref T p, APTR keys, APTR destination, uint first, uint count)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var target = destination;
        var ascending = keys;
        uint sum = 0;
        while (count >= 4)
        {
            p.WriteUInt32(target, 0, first);
            p.WriteUInt32(ascending, 0, first);
            var b = first + 1; p.WriteUInt32(target, -4, b); p.WriteUInt32(ascending, 4, b);
            var c = first + 2; p.WriteUInt32(target, -8, c); p.WriteUInt32(ascending, 8, c);
            var d = first + 3; p.WriteUInt32(target, -12, d); p.WriteUInt32(ascending, 12, d);
            sum = unchecked(sum + (first << 2) + 6);
            target = APTR.FromPointer(target.Raw - 16);
            ascending = APTR.FromPointer(ascending.Raw + 16);
            first += 4; count -= 4;
        }
        while (count != 0)
        {
            p.WriteUInt32(target, 0, first);
            p.WriteUInt32(ascending, 0, first);
            sum = unchecked(sum + first);
            first++; count--;
            target = APTR.FromPointer(target.Raw - 4);
            ascending = APTR.FromPointer(ascending.Raw + 4);
        }
        return sum;
    }

    private static uint CopyTableKeys<T>(ref T p, APTR from, APTR to, uint count)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var source = from;
        var target = to;
        uint sum = 0;
        while (count >= 4)
        {
            var a = p.ReadUInt32(source); p.WriteUInt32(target, 0, a);
            var b = p.ReadUInt32(source, 4); p.WriteUInt32(target, -4, b);
            var c = p.ReadUInt32(source, 8); p.WriteUInt32(target, -8, c);
            var d = p.ReadUInt32(source, 12); p.WriteUInt32(target, -12, d);
            sum = unchecked(sum + a + b + c + d);
            source = APTR.FromPointer(source.Raw + 16);
            target = APTR.FromPointer(target.Raw - 16);
            count -= 4;
        }
        while (count != 0)
        {
            var key = p.ReadUInt32(source);
            p.WriteUInt32(target, 0, key);
            sum = unchecked(sum + key);
            source = APTR.FromPointer(source.Raw + 4);
            target = APTR.FromPointer(target.Raw - 4);
            count--;
        }
        return sum;
    }
}
