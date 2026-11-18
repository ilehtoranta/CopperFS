using Amiga;

namespace CopperStart.Dos;

public static partial class AmigaFfsWriteCore
{
    // Validate pointer order and forward chains, including FFS 40.1's separate
    // append runs (old EOF link zero, new sequence starting at one).
    // The bounded workspace also records the checked order for later packets.
    private static bool OfsChain<T>(ref T p, ref Work w, uint header, bool release,
        out uint lastData) where T : struct, IAmigaWritableFileSystemPlatform
    {
        lastData = 0;
        var g = AmigaFfsGeometry.Read(ref p);
        var headerImage = InspectHeader(ref p, ref w, in g, header);
        if (headerImage.IsNull || p.ReadUInt32(headerImage, g.Tail + 508) != 0xFFFFFFFD)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        return OfsChain(ref p, ref w, in g, header, p.ReadUInt32(headerImage, g.Tail + 324),
            p.ReadUInt32(headerImage, 16), release, out lastData);
    }

    private static bool OfsChain<T>(ref T p, ref Work w, in AmigaFfsGeometry g, uint header,
        uint size, uint firstData, bool release, out uint lastData) where T : struct, IAmigaWritableFileSystemPlatform
    {
        lastData = 0;
        var remaining = size;
        var payload = g.BlockBytes - 24;
        if (MountIndexEnabled && !release && w.LayoutKnown && AmigaFfsMountIndex.Matches(ref p, w.Index, header, size))
        {
            var length = p.ReadUInt32(w.Index, AmigaFfsMountIndex.FileBlocks);
            if (length != 0) lastData = p.ReadUInt32(AmigaFfsMountIndex.DataKeys(ref p, w.Index), (int)(length - 1) * 4);
            return true;
        }
        var blocks = remaining == 0 ? 0 : AmigaGeometryMath.Divide(remaining - 1, payload) + 1;
        if (blocks > g.BlockCount - g.ReservedBlocks) return Fail(ref w, DOS.Error.DiskNotValidated);
        var indexed = MountIndexEnabled && !release && w.LayoutKnown && AmigaFfsMountIndex.FitsFile(ref p, w.Index, blocks, g.TableEntries);
        var dataKeys = APTR.Null; var tables = APTR.Null;
        var stage = APTR.Null;
        var stageBlocks = 0u;
        if (MountIndexEnabled && indexed)
        {
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.Header, 0);
            dataKeys = AmigaFfsMountIndex.DataKeys(ref p, w.Index);
            tables = AmigaFfsMountIndex.TableKeys(ref p, w.Index);
            stage = APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.Stage));
            stageBlocks = p.ReadUInt32(w.Index, AmigaFfsMountIndex.StageBytes) / g.BlockBytes;
        }
        // FFS 40.1 can set the file header's firstData to the first sector
        // of a later append. Require it to name an actual run head reached
        // through the pointer tables and the checked forward links.
        var headFound = blocks == 0 && firstData == 0;
        if (blocks != 0 && firstData == 0) return Fail(ref w, DOS.Error.DiskNotValidated);
        uint expected = 0; var table = header; var index = 0u;
        var sequenceBase = 0u;
        for (var guard = 0u; guard < g.BlockCount; guard++)
        {
            if (!Allocated(ref p, ref w, in g, table) || !p.ReadBlock(table, w.Meta) ||
                !AmigaDiskFileSystemCore.Checksum(ref p, w.Meta) || p.ReadUInt32(w.Meta, 4) != table ||
                p.ReadUInt32(w.Meta) != (guard == 0 ? 2u : 16u) ||
                p.ReadUInt32(w.Meta, g.Tail + 508) != 0xFFFFFFFD ||
                (guard != 0 && p.ReadUInt32(w.Meta, g.Tail + 500) != header))
                return Fail(ref w, DOS.Error.DiskNotValidated);
            if (MountIndexEnabled && indexed) p.WriteUInt32(tables, (int)guard * 4, table);
            var count = blocks - index; if (count > g.TableEntries) count = g.TableEntries;
            if (p.ReadUInt32(w.Meta, 8) != count) return Fail(ref w, DOS.Error.DiskNotValidated);
            var staged = 0u;
            var image = w.Data;
            for (var i = 0u; i < count; i++)
            {
                var key = p.ReadUInt32(w.Meta, g.Tail + 308 - (int)i * 4);
                var bytes = remaining > payload ? payload : remaining;
                if ((expected != 0 && key != expected) || key == header || key == table)
                    return Fail(ref w, DOS.Error.DiskNotValidated);
                if (staged == 0)
                {
                    var run = 1u;
                    while (MountIndexEnabled && run < stageBlocks && run < count - i &&
                        p.ReadUInt32(w.Meta, g.Tail + 308 - (int)(i + run) * 4) == key + run) run++;
                    if (MountIndexEnabled && run > 1)
                    {
                        if (!AllocatedRun(ref p, ref w, in g, key, run) || p.ReadDataBlocks(key, run, stage, false) != run)
                            return Fail(ref w, DOS.Error.DiskNotValidated);
                        image = stage;
                        staged = run;
                    }
                    else
                    {
                        if (!Allocated(ref p, ref w, in g, key) || !p.ReadBlock(key, w.Data))
                            return Fail(ref w, DOS.Error.DiskNotValidated);
                        image = w.Data;
                    }
                }
                if (!AmigaDiskFileSystemCore.Checksum(ref p, image) || p.ReadUInt32(image) != 8 ||
                    p.ReadUInt32(image, 4) != header || p.ReadUInt32(image, 12) != bytes)
                    return Fail(ref w, DOS.Error.DiskNotValidated);
                var sequence = p.ReadUInt32(image, 8);
                if (expected == 0 && key == firstData) headFound = true;
                if (expected == 0 && index != 0)
                {
                    if (sequence != 1) return Fail(ref w, DOS.Error.DiskNotValidated);
                    sequenceBase = index;
                }
                if (sequence != index + 1 && (sequenceBase == 0 || sequence != index + 1 - sequenceBase))
                    return Fail(ref w, DOS.Error.DiskNotValidated);
                // The absolute sequence previously proved uniqueness. For a
                // restarted sequence, check earlier pointers explicitly, using
                // only the metadata scratch and restoring its current table.
                if (MountIndexEnabled && indexed) p.WriteUInt32(dataKeys, (int)index * 4, key);
                else if (sequence != index + 1 && !OfsUniquePrefix(ref p, ref w, header, table, index, key)) return false;
                expected = p.ReadUInt32(image, 16); lastData = key;
                if (MountIndexEnabled && staged != 0)
                {
                    staged--;
                    image = APTR.FromPointer(image.Raw + g.BlockBytes);
                }
                index++; remaining -= bytes;
                if (release) Bit(ref p, ref w, key, true);
                if (w.Error != DOS.Error.None) return false;
            }
            var next = p.ReadUInt32(w.Meta, g.Tail + 504);
            if (guard != 0 && release) Bit(ref p, ref w, table, true);
            if (w.Error != DOS.Error.None) return false;
            if (index == blocks)
                return (headFound && next == 0 && expected == 0 && (!MountIndexEnabled || !indexed ||
                    AmigaFfsMountIndex.SealFile(ref p, w.Index, header, size, blocks, guard + 1))) || Fail(ref w, DOS.Error.DiskNotValidated);
            if (next == 0 || next == header) return Fail(ref w, DOS.Error.DiskNotValidated);
            table = next;
        }
        return Fail(ref w, DOS.Error.DiskNotValidated);
    }

    private static bool OfsUniquePrefix<T>(ref T p, ref Work w, uint header,
        uint currentTable, uint index, uint key) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var g = AmigaFfsGeometry.Read(ref p); var table = header;
        for (var seen = 0u; seen < index;)
        {
            if (!p.ReadBlock(table, w.Meta)) return Fail(ref w, DOS.Error.DiskNotValidated);
            var count = index - seen; if (count > g.TableEntries) count = g.TableEntries;
            for (var slot = 0u; slot < count; slot++)
                if (p.ReadUInt32(w.Meta, g.Tail + 308 - (int)slot * 4) == key)
                    return Fail(ref w, DOS.Error.DiskNotValidated);
            seen += count; table = p.ReadUInt32(w.Meta, g.Tail + 504);
        }
        return p.ReadBlock(currentTable, w.Meta) || Fail(ref w, DOS.Error.DiskNotValidated);
    }

    // The owning group already checked every OFS header. A contained write
    // across consecutive keys needs only the data images, even at a table boundary.
    private static bool TryOverwriteOfsRun<T>(ref T p, APTR index, ref Request r, uint bytes, uint size,
        bool deviceChecksProtection, out DOS.Error error) where T : struct, IAmigaWritableFileSystemPlatform
    {
        error = DOS.Error.None;
        if (r.Length <= 2 * bytes || r.Position >= size || r.Length > size - r.Position ||
            r.Length == size || p.ReadUInt32(index, AmigaFfsMountIndex.PendingAppend) != 0) return false;
        var stageBytes = p.ReadUInt32(index, AmigaFfsMountIndex.StageBytes);
        if (r.Length > stageBytes) return false;
        var payload = bytes - 24;
        var first = AmigaGeometryMath.DivRem(r.Position, payload, out var within);
        var count = AmigaGeometryMath.Divide(within + r.Length - 1, payload) + 1;
        var span = count * bytes;
        var stage = APTR.FromPointer(p.ReadUInt32(index, AmigaFfsMountIndex.Stage));
        if (stage.IsNull || span > stageBytes || stage.Raw - r.Buffer.Raw < r.Length || r.Buffer.Raw - stage.Raw < span)
            return false;
        var key = VerifiedRun(ref p, index, r.Header, AmigaFfsMountIndex.DataKeys(ref p, index), first, count);
        if (key == 0) return false;
        if (!p.IsMapped(r.Buffer, r.Length)) { error = DOS.Error.BadNumber; return true; }
        if (!deviceChecksProtection && p.WriteProtected()) { error = DOS.Error.DiskWriteProtected; return true; }
        p.WriteUInt32(index, AmigaFfsMountIndex.Status, AmigaFfsMountIndex.Updating);
        if (PatchGroupRun(ref p, ref r, stage, key, count, bytes, within, 24, 0))
            p.WriteUInt32(index, AmigaFfsMountIndex.Status, AmigaFfsMountIndex.GroupOpen);
        else
        {
            p.WriteUInt32(index, AmigaFfsMountIndex.Status, AmigaFfsMountIndex.GroupFailed);
            error = deviceChecksProtection && p.WriteProtected() ? DOS.Error.DiskWriteProtected : DOS.Error.DiskNotValidated;
        }
        return true;
    }

    // The open group verified the chain, including every sector checksum.
    // Small edge patches only change the longwords touched by the copy.
    private static void PatchOfsPayload<T>(ref T p, APTR source, APTR image, uint offset, uint length, uint blockBytes)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var target = APTR.FromPointer(image.Raw + offset);
        if (length > blockBytes / 8)
        {
            p.Copy(source, target, length);
            Seal(ref p, image, 20);
            return;
        }
        var first = (int)(offset & ~3u);
        var end = (int)(offset + length);
        var checksum = p.ReadUInt32(image, 20);
        for (var at = first; at < end; at += 4) checksum = unchecked(checksum + p.ReadUInt32(image, at));
        p.Copy(source, target, length);
        for (var at = first; at < end; at += 4) checksum = unchecked(checksum - p.ReadUInt32(image, at));
        p.WriteUInt32(image, 20, checksum);
    }

    // OFS checksums cover the payload. For full aligned sectors sum the
    // words as they are copied into the outgoing image.
    private static void CopyAndSealOfs<T>(ref T p, APTR source, APTR image, uint bytes)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var destination = APTR.FromPointer(image.Raw + 24);
        var distance = source.Raw > destination.Raw ? source.Raw - destination.Raw : destination.Raw - source.Raw;
        if (bytes != p.BlockBytes - 24 || (bytes & 3) != 0 || ((source.Raw | destination.Raw) & 1) != 0 || distance < bytes)
        {
            p.Copy(source, destination, bytes);
            Seal(ref p, image, 20);
            return;
        }
        var sum = unchecked(8u + p.ReadUInt32(image, 4) + p.ReadUInt32(image, 8) + bytes + p.ReadUInt32(image, 16));
        var input = source;
        var remaining = bytes;
        while (remaining >= 16)
        {
            var a = p.ReadUInt32(input);
            var b = p.ReadUInt32(input, 4);
            var c = p.ReadUInt32(input, 8);
            var d = p.ReadUInt32(input, 12);
            p.WriteUInt32(destination, 0, a); p.WriteUInt32(destination, 4, b);
            p.WriteUInt32(destination, 8, c); p.WriteUInt32(destination, 12, d);
            sum = unchecked(sum + a + b + c + d);
            input = APTR.FromPointer(input.Raw + 16);
            destination = APTR.FromPointer(destination.Raw + 16);
            remaining -= 16;
        }
        while (remaining != 0)
        {
            var word = p.ReadUInt32(input);
            p.WriteUInt32(destination, 0, word);
            sum = unchecked(sum + word);
            input = APTR.FromPointer(input.Raw + 4);
            destination = APTR.FromPointer(destination.Raw + 4);
            remaining -= 4;
        }
        p.WriteUInt32(image, 20, unchecked(0u - sum));
    }

    private static bool SetOfsForwardLink<T>(ref T p, ref Work w, uint previous, uint next)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
#if COPPERFS_FFS_HANDLER
        if (p.TryPatchClassicOfsForwardLink(previous, next, out var written))
            return written || Fail(ref w, DOS.Error.DiskNotValidated);
#endif
        if (!p.ReadBlock(previous, w.Data)) return Fail(ref w, DOS.Error.DiskNotValidated);
        var old = p.ReadUInt32(w.Data, 16);
        p.WriteUInt32(w.Data, 20, unchecked(p.ReadUInt32(w.Data, 20) + old - next));
        p.WriteUInt32(w.Data, 16, next);
        return Put(ref p, ref w, previous, w.Data, -1);
    }

    // Called after Prepare has selected DOS0. FFS retains its existing writer.
    // A complete single-table file has one publication point for both the
    // pointer table and the head of the OFS forward chain.
    private static bool WriteReplacementOfsData<T>(ref T p, ref Work w, ref Request r, uint first, uint count)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var payload = p.BlockBytes - 24;
        for (var i = 0u; i < count; i++)
        {
            p.WriteUInt32(w.Data, 0, 8); p.WriteUInt32(w.Data, 4, r.Header);
            p.WriteUInt32(w.Data, 8, i + 1); p.WriteUInt32(w.Data, 12, payload);
            p.WriteUInt32(w.Data, 16, i + 1 == count ? 0 : first + i + 1);
            CopyAndSealOfs(ref p, APTR.FromPointer(r.Buffer.Raw + i * payload), w.Data, payload);
            if (!Put(ref p, ref w, first + i, w.Data, -1)) return false;
        }
        return true;
    }

    private static bool WriteOfs<T>(ref T p, ref Work w, ref Request r, in AmigaFfsGeometry g, out uint actual, bool fixedHardDisk)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        actual = 0; var payload = g.BlockBytes - 24;
        var header = InspectHeader(ref p, ref w, in g, r.Header);
        if (header.IsNull) return false;
        if (p.ReadUInt32(header, g.Tail + 508) != 0xFFFFFFFD) return Fail(ref w, DOS.Error.ObjectWrongType);
        if ((p.ReadUInt32(header, g.Tail + 320) & 4) != 0) return Fail(ref w, DOS.Error.WriteProtected);
        var size = p.ReadUInt32(header, g.Tail + 324);
        var firstData = p.ReadUInt32(header, 16);
        if (r.Position > size) return Fail(ref w, DOS.Error.SeekError);
        if (r.Length > uint.MaxValue - r.Position) return Fail(ref w, DOS.Error.DiskFull);
        if (r.Length == 0) return true;
        var end = r.Position + r.Length; var newSize = end > size ? end : size;
        var oldBlocks = size == 0 ? 0 : AmigaGeometryMath.Divide(size - 1, payload) + 1;
        if (end > size && !PrepareGrowthIndex(ref p, ref w, in g)) return false;
        if (MountIndexEnabled && w.LayoutKnown) AmigaFfsMountIndex.ResumeFile(ref p, w.Index, r.Header, size);
        if (!OfsChain(ref p, ref w, in g, r.Header, size, firstData, false, out var previousData)) return false;
        var newBlocks = oldBlocks;
        if (end > size)
        {
            newBlocks = AmigaGeometryMath.Divide(end - 1, payload) + 1;
            var oldExt = oldBlocks == 0 ? 0 : AmigaGeometryMath.Divide(oldBlocks - 1, g.TableEntries);
            var newExt = AmigaGeometryMath.Divide(newBlocks - 1, g.TableEntries);
            if (!CanGrow(ref p, ref w, in g, newBlocks - oldBlocks + newExt - oldExt)) return false;
        }
        var stage = APTR.Null;
        uint stageBlocks = 0;
        if (MountIndexEnabled && fixedHardDisk && w.LayoutKnown)
        {
            stage = APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.Stage));
            stageBlocks = p.ReadUInt32(w.Index, AmigaFfsMountIndex.StageBytes) / g.BlockBytes;
        }
        var fileKeys = APTR.Null;
        var fileTables = APTR.Null;
        if (MountIndexEnabled && w.LayoutKnown && AmigaFfsMountIndex.FitsFile(ref p, w.Index, newBlocks, g.TableEntries) &&
            (oldBlocks == 0 || AmigaFfsMountIndex.Matches(ref p, w.Index, r.Header, size)))
        {
            fileKeys = AmigaFfsMountIndex.DataKeys(ref p, w.Index);
            fileTables = AmigaFfsMountIndex.TableKeys(ref p, w.Index);
            p.WriteUInt32(fileTables, 0, r.Header);
            w.RestoreFile = r.Header; w.RestoreSize = newSize; w.WritingIndexedFile = true;
            if (w.GroupFileWrites && w.DosType < 0x444F5304 && p.ReadUInt32(w.Index, AmigaFfsMountIndex.HeaderImage) != 0)
                w.GroupHeader = r.Header;
        }
        if (MountIndexEnabled && w.BufferSmallAppends && TryStartBufferedAppend(ref p, ref w, ref r, in g, size, out actual))
            return w.Error == DOS.Error.None;
        if (!Begin(ref p, ref w, in g)) return false;
        if (fixedHardDisk && g.BlockBytes == 512 && r.Position == 0 && r.Length == size &&
            oldBlocks >= 64 && oldBlocks <= g.TableEntries && size == oldBlocks * payload)
        {
            if (!Header(ref p, ref w, r.Header) ||
                !RelocateOverwrite(ref p, ref w, in g, ref r, r.Header, 0, oldBlocks, ref actual, out var moved)) return false;
            if (moved) { w.RestoreFile = 0; return Finish(ref p, ref w, in g); }
        }
        var table = r.Header; var tableIndex = 0u;
        var sizePublished = false;
        uint runFirst = 0, runCount = 0;
        while (actual < r.Length)
        {
            var position = r.Position + actual; var index = AmigaGeometryMath.DivRem(position, payload, out var withinRemainder);
            var targetTable = AmigaGeometryMath.DivRem(index, g.TableEntries, out var slotRemainder);
            var slot = slotRemainder;
            if (MountIndexEnabled && fileTables.IsNotNull && targetTable != tableIndex && oldBlocks != 0 &&
                targetTable <= (oldBlocks - 1) / g.TableEntries)
            {
                table = p.ReadUInt32(fileTables, (int)targetTable * 4);
                tableIndex = targetTable; runCount = 0;
            }
            while (tableIndex < targetTable)
            {
                runCount = 0;
                if (!ReadWriteTable(ref p, ref w, table)) return Fail(ref w, DOS.Error.DiskNotValidated);
                var next = p.ReadUInt32(w.Meta, g.Tail + 504);
                if (next == 0)
                {
                    next = Allocate(ref p, ref w, in g); if (next == 0) return false;
                    p.Clear(w.Meta, g.BlockBytes); p.WriteUInt32(w.Meta, 0, 16); p.WriteUInt32(w.Meta, 4, next);
                    p.WriteUInt32(w.Meta, g.Tail + 500, r.Header); p.WriteUInt32(w.Meta, g.Tail + 508, 0xFFFFFFFD);
                    if (!Put(ref p, ref w, next, w.Meta, 20) || !ReadWriteTable(ref p, ref w, table))
                        return Fail(ref w, DOS.Error.DiskNotValidated);
                    p.WriteUInt32(w.Meta, g.Tail + 504, next);
                    if (!Put(ref p, ref w, table, w.Meta, 20)) return false;
                }
                table = next; tableIndex++;
                if (MountIndexEnabled && fileTables.IsNotNull) p.WriteUInt32(fileTables, (int)tableIndex * 4, table);
            }
            if (!ReadWriteTable(ref p, ref w, table)) return Fail(ref w, DOS.Error.DiskNotValidated);
            var at = g.Tail + 308 - (int)slot * 4;
            var key = p.ReadUInt32(w.Meta, at); var allocate = index >= oldBlocks;
            if (allocate)
            {
                var wanted = newBlocks - index;
                var slots = g.TableEntries - slot;
                if (wanted > slots) wanted = slots;
                key = AllocateData(ref p, ref w, in g, (previousData == 0 ? r.Header : previousData) + 1,
                    wanted, ref runFirst, ref runCount);
                if (key == 0) return false;
                if (MountIndexEnabled && fileKeys.IsNotNull) p.WriteUInt32(fileKeys, (int)index * 4, key);
                if (withinRemainder == 0 && runCount != 0)
                {
                    // Allocate a verified contiguous run before constructing its
                    // forward chain. Each new data block is sealed once. Publish
                    // its pointer prefix only after every data write succeeds;
                    // every failure after Begin still leaves the root invalid.
                    var blocks = runCount + 1;
                    var staged = stageBlocks >= 2;
                    if (staged && blocks > stageBlocks) blocks = stageBlocks;
                    if (!ClaimRun(ref p, ref w, key + 1, blocks - 1)) return false;
                    var remaining = r.Length - actual;
                    var input = APTR.FromPointer(r.Buffer.Raw + actual);
                    var image = staged ? stage : w.Data;
                    for (var n = 0u; n < blocks; n++)
                    {
                        var bytes = remaining > payload ? payload : remaining;
                        if (bytes != payload) p.Clear(image, g.BlockBytes);
                        p.WriteUInt32(image, 0, 8); p.WriteUInt32(image, 4, r.Header);
                        p.WriteUInt32(image, 8, index + n + 1); p.WriteUInt32(image, 12, bytes);
                        p.WriteUInt32(image, 16, n + 1 == blocks ? 0 : key + n + 1);
                        CopyAndSealOfs(ref p, input, image, bytes);
                        if (!staged && !Put(ref p, ref w, key + n, image, -1)) return false;
                        p.WriteUInt32(w.Meta, at - (int)n * 4, key + n);
                        if (MountIndexEnabled && fileKeys.IsNotNull) p.WriteUInt32(fileKeys, (int)(index + n) * 4, key + n);
                        remaining -= bytes;
                        input = APTR.FromPointer(input.Raw + bytes);
                        if (staged) image = APTR.FromPointer(image.Raw + g.BlockBytes);
                        else actual += bytes;
                    }
                    if (staged)
                    {
                        if (!p.WriteDataBlocksRetainingLast(key, blocks, stage)) return Fail(ref w, DOS.Error.DiskNotValidated);
                        actual = r.Length - remaining;
                    }
                    if (previousData != 0 && !SetOfsForwardLink(ref p, ref w, previousData, key)) return false;
                    previousData = key + blocks - 1;
                    p.WriteUInt32(w.Meta, 8, slot + blocks);
                    if (index == 0) p.WriteUInt32(w.Meta, 16, key);
                    if (table == r.Header && actual == r.Length)
                        sizePublished = SetFinalSize(ref p, ref w, g.Tail, newSize);
                    if (!Put(ref p, ref w, table, w.Meta, 20)) return false;
                    runFirst += blocks - 1; runCount = 0;
                    continue;
                }
                p.Clear(w.Data, g.BlockBytes); p.WriteUInt32(w.Data, 0, 8);
                p.WriteUInt32(w.Data, 4, r.Header); p.WriteUInt32(w.Data, 8, index + 1);
            }
            else
            {
                var left = r.Length - actual;
                if (MountIndexEnabled && fileKeys.IsNotNull && stageBlocks >= 2 && withinRemainder == 0 && left >= payload * 2)
                {
                    var blocks = left / payload;
                    if (blocks > stageBlocks) blocks = stageBlocks;
                    if (blocks > oldBlocks - index) blocks = oldBlocks - index;
                    if (blocks > g.TableEntries - slot) blocks = g.TableEntries - slot;
                    var run = 1u;
                    while (run < blocks && p.ReadUInt32(fileKeys, (int)(index + run) * 4) == key + run) run++;
                    if (run >= 2)
                    {
                        if (p.ReadDataBlocks(key, run, stage) != run) return Fail(ref w, DOS.Error.DiskNotValidated);
                        for (var n = 0u; n < run; n++)
                        {
                            var image = APTR.FromPointer(stage.Raw + n * g.BlockBytes);
                            p.WriteUInt32(image, 12, payload);
                            CopyAndSealOfs(ref p, APTR.FromPointer(r.Buffer.Raw + actual + n * payload), image, payload);
                        }
                        if (!p.WriteDataBlocks(key, run, stage)) return Fail(ref w, DOS.Error.DiskNotValidated);
                        actual += run * payload;
                        continue;
                    }
                }
                if (!p.ReadBlock(key, w.Data)) return Fail(ref w, DOS.Error.DiskNotValidated);
            }
            var within = withinRemainder;
            var count = payload - within;
            if (count > r.Length - actual) count = r.Length - actual;
            p.Copy(APTR.FromPointer(r.Buffer.Raw + actual), APTR.FromPointer(w.Data.Raw + 24 + within), count);
            var dataBytes = newSize - index * payload; if (dataBytes > payload) dataBytes = payload;
            p.WriteUInt32(w.Data, 12, dataBytes);
            if (!Put(ref p, ref w, key, w.Data, 20)) return false;
            if (allocate)
            {
                // Publish the new data only after its header, payload and checksum
                // have been written. Any later failure leaves the root invalid.
                if (previousData != 0 && !SetOfsForwardLink(ref p, ref w, previousData, key)) return false;
                previousData = key;
                p.WriteUInt32(w.Meta, at, key); p.WriteUInt32(w.Meta, 8, slot + 1);
                if (index == 0) p.WriteUInt32(w.Meta, 16, key);
                if (table == r.Header && count == r.Length - actual)
                    sizePublished = SetFinalSize(ref p, ref w, g.Tail, newSize);
                if (!Put(ref p, ref w, table, w.Meta, 20)) return false;
            }
            actual += count;
        }
        if (MountIndexEnabled && w.RestoreFile != 0)
        {
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.FileBlocks, newBlocks);
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.Tables, (newBlocks - 1) / g.TableEntries + 1);
        }
        if (newSize == size || sizePublished) return Finish(ref p, ref w, in g);
        if (!Header(ref p, ref w, r.Header)) return false;
        w.CacheParent = p.ReadUInt32(w.Meta, g.Tail + 500);
        p.WriteUInt32(w.Meta, g.Tail + 324, newSize);
        return Put(ref p, ref w, r.Header, w.Meta, 20) && Finish(ref p, ref w, in g);
    }
}
