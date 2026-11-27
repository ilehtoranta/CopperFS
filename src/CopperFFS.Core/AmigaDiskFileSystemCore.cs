using Amiga;

namespace CopperStart.Dos;

/// <summary>
/// Read-only OFS/FFS metadata and file traversal. All buffers belong
/// to the caller; no managed objects, host media access, or persistent globals.
/// </summary>
public static class AmigaDiskFileSystemCore
{

    public static bool Checksum<T>(ref T p, APTR buffer) where T : struct, IAmigaFileSystemPlatform
    {
        var remaining = p.BlockBytes;
        uint sum = 0;
        var cursor = buffer;
        // Keep the eight loads in one expression so the 68000 can add them
        // directly from memory without spilling temporary words.
        var end = APTR.FromPointer(cursor.Raw + (remaining & ~31u));
        while (cursor != end)
        {
            sum = unchecked(sum + p.ReadUInt32(cursor) + p.ReadUInt32(cursor, 4) +
                p.ReadUInt32(cursor, 8) + p.ReadUInt32(cursor, 12) +
                p.ReadUInt32(cursor, 16) + p.ReadUInt32(cursor, 20) +
                p.ReadUInt32(cursor, 24) + p.ReadUInt32(cursor, 28));
            cursor = APTR.FromPointer(cursor.Raw + 32);
        }
        remaining &= 31;
        for (; remaining >= 4; remaining -= 4)
        {
            sum = unchecked(sum + p.ReadUInt32(cursor));
            cursor = APTR.FromPointer(cursor.Raw + 4);
        }
        return sum == 0;
    }

    public static bool Header<T>(ref T p, uint block, APTR buffer) where T : struct, IAmigaFileSystemPlatform
    {
        var geometry = AmigaFfsGeometry.Read(ref p);
        return Header(ref p, in geometry, block, buffer, false);
    }

    internal static bool Header<T>(ref T p, in AmigaFfsGeometry geometry, uint block, APTR buffer, bool links)
        where T : struct, IAmigaFileSystemPlatform
    {
        var image = HeaderView(ref p, in geometry, block, buffer, APTR.Null, 0, links);
        if (image.IsNull) return false;
        if (image != buffer) p.Copy(image, buffer, geometry.BlockBytes);
        return true;
    }

    internal static APTR HeaderView<T>(ref T p, in AmigaFfsGeometry g, uint block, APTR scratch,
        APTR output, uint length, bool links) where T : struct, IAmigaFileSystemPlatform
    {
        if (block < g.ReservedBlocks || block >= g.BlockCount) return APTR.Null;
        APTR view;
#if COPPERFS_FFS_HANDLER
        // Only the standalone classic platform maintains this checksum proof.
        if (!p.TryBorrowCheckedClassicBlock(block, scratch, output, length, out view))
#endif
        {
            if (!p.ReadBlock(block, scratch) || !Checksum(ref p, scratch)) return APTR.Null;
            view = scratch;
        }
        if (view.IsNull || p.ReadUInt32(view) != 2) return APTR.Null;
        var type = unchecked((int)p.ReadUInt32(view, g.Tail + 508));
        var valid = block == g.RootBlock ? type == 1 && p.ReadUInt32(view, 12) == g.TableEntries :
            p.ReadUInt32(view, 4) == block && (type == 2 || type == -3 || (links && type is 3 or 4 or -4));
        return valid ? view : APTR.Null;
    }

    private static APTR CheckedFfsMetadataView<T>(ref T p, uint block, APTR scratch, APTR output, uint outputBytes)
        where T : struct, IAmigaFileSystemPlatform
    {
        APTR view;
#if COPPERFS_FFS_HANDLER
        if (p.TryBorrowCheckedClassicBlock(block, scratch, output, outputBytes, out view)) return view;
#endif
        if (!p.TryBorrowFfsMetadataBlock(block, scratch, output, outputBytes, out view))
        {
            if (!p.ReadBlock(block, scratch)) return APTR.Null;
            view = scratch;
        }
        return view.IsNotNull && Checksum(ref p, view) ? view : APTR.Null;
    }

    private static APTR FileHeaderView<T>(ref T p, uint block, APTR scratch, APTR output, uint outputBytes)
        where T : struct, IAmigaFileSystemPlatform
    {
        var count = p.BlockCount; var bytes = p.BlockBytes; var reserved = p.ReservedBlocks;
        if (block < reserved || block >= count) return APTR.Null;
        var view = CheckedFfsMetadataView(ref p, block, scratch, output, outputBytes);
        if (view.IsNull || p.ReadUInt32(view) != 2) return APTR.Null;
        var type = unchecked((int)p.ReadUInt32(view, (int)bytes - 4));
        var root = AmigaFfsGeometry.RootFor(count, reserved);
        return block == root
            ? type == 1 && p.ReadUInt32(view, 12) == bytes / 4 - 56 ? view : APTR.Null
            : p.ReadUInt32(view, 4) == block && (type == 2 || type == -3) ? view : APTR.Null;
    }

    public static bool Mount<T>(ref T p, APTR buffer, out uint dosType) where T : struct, IAmigaFileSystemPlatform
        => Mount(ref p, buffer, out dosType, false);

    // longNames admits read-only DOS\6/7. Such a caller must pass the mounted
    // dosType to Examine, ExamineNext, Resolve and ReadLink: their dosType-less
    // overloads assume the classic header layout.
    public static bool Mount<T>(ref T p, APTR buffer, out uint dosType, bool longNames) where T : struct, IAmigaFileSystemPlatform
    {
        var geometry = AmigaFfsGeometry.Read(ref p);
        dosType = 0;
        if (!p.ReadBlock(0, buffer)) return false;
        dosType = p.ReadUInt32(buffer);
        // Directory-cache disks use the ordinary hash table for read access.
        var lnfs = LongNames(dosType);
        if (dosType < 0x444F5300 || dosType > 0x444F5307 || (lnfs && !longNames)) return false;
        // The LNFS root repeats the boot block's signature; classic roots keep 0 there.
        return Header(ref p, geometry.RootBlock, buffer) && p.ReadUInt8(buffer, geometry.Tail + 432) is > 0 and <= 30 &&
            (!lnfs || p.ReadUInt32(buffer, geometry.Tail + 496) == dosType);
    }

    // DOS\6 (OFS) and DOS\7 (FFS) long-name mode (LNFS), read-only. Layout from
    // Olaf Barthel, "DCFS and LNFS Low Level Data Structures" (AmigaOS wiki):
    // entry headers merge the 30-character name and 80-byte comment into one
    // 112-byte name-and-comment field at Tail+328 (two BCPL strings), followed
    // by a comment block key at Tail+440 and the date at Tail+452. A comment that
    // does not fit beside the name lives in a TYPE_COMMENT (64) block. The root
    // block keeps the classic name and date positions. Names always use the
    // international hash. No CopperFFS write path accepts these variants.
    internal static bool LongNames(uint dosType) => dosType is 0x444F5306 or 0x444F5307;
    internal const int LongNameBytes = 112, LongCommentKey = 440, LongDate = 452, CommentBlockType = 64;
    // FIB and ExAll names are limited to 107 characters by fib_FileName[108].
    internal const uint LongNameLimit = 107;
    internal static int NameOffset(bool lnfs) => lnfs ? 328 : 432;
    internal static uint NameLimit(bool lnfs) => lnfs ? LongNameLimit : 30;

    // Stored name length, or a value above the variant's limit when the
    // header's name field is malformed. The root always uses classic fields.
    internal static uint StoredNameLength<T>(ref T p, APTR header, int tail, bool lnfs) where T : struct, IAmigaFileSystemPlatform
    {
        var length = (uint)p.ReadUInt8(header, tail + NameOffset(lnfs));
        if (!lnfs) return length;
        // Name and comment are both BCPL strings inside the 112-byte field.
        return length + 2 > LongNameBytes ? LongNameLimit + 1 : length;
    }



    internal static byte Upper(byte c, uint type) => c is >= 97 and <= 122 ||
        (type >= 0x444F5302 && c is >= 224 and <= 254 && c != 247) ? unchecked((byte)(c - 32)) : c;

    internal static uint NameBucket<T>(ref T p, APTR name, uint length, uint dosType, uint buckets)
        where T : struct, IAmigaFileSystemPlatform
    {
        // Only the low eleven bits contribute to the DOS hash. Discarding
        // the high bits once gives the same residue for every component.
        var hash = length;
        var cursor = name;
        var end = name.Raw + length;
        while (cursor.Raw != end)
        {
            hash = unchecked(hash * 13 + Upper(p.ReadUInt8(cursor), dosType));
            cursor = APTR.FromPointer(cursor.Raw + 1);
        }
        return AmigaGeometryMath.Remainder(hash & 2047, buckets);
    }

    public static uint Resolve<T>(ref T p, uint directory, APTR name, uint length,
        uint dosType, APTR buffer, out DOS.Error error) where T : struct, IAmigaFileSystemPlatform
    {
        var key = ResolveView(ref p, directory, name, length, dosType, buffer, out error, out var image);
        if (key != 0 && image != buffer) p.Copy(image, buffer, p.BlockBytes);
        return key;
    }

    // Consume the returned header before another block access. Resolve exposes
    // the same traversal to callers that need a stable copy in their scratch.
    internal static uint ResolveView<T>(ref T p, uint directory, APTR name, uint length,
        uint dosType, APTR buffer, out DOS.Error error, out APTR image, APTR mountIndex = default, bool followFinal = true)
        where T : struct, IAmigaFileSystemPlatform
        => ResolveForOpen(ref p, directory, name, length, dosType, buffer, out error, out image, out _, mountIndex, followFinal);

    internal struct MissingComponent
    {
        internal uint Parent, Start;
        internal int HashSlot;
    }

    internal static uint ResolveForOpen<T>(ref T p, uint directory, APTR name, uint length,
        uint dosType, APTR buffer, out DOS.Error error, out APTR image, out MissingComponent missing,
        APTR mountIndex = default, bool followFinal = true)
        where T : struct, IAmigaFileSystemPlatform
    {
        var geometry = AmigaFfsGeometry.Read(ref p);
        missing = default;
        image = APTR.Null;
        error = DOS.Error.None;
        if (length > 255) { error = DOS.Error.InvalidComponentName; return 0; }
        var tableEntries = geometry.TableEntries;
        if (tableEntries == 0) { error = DOS.Error.DiskNotValidated; return 0; }
        var lnfs = LongNames(dosType);
        var nameAt = geometry.Tail + NameOffset(lnfs);
        var offset = 0u;
        for (var i = 0u; i < length; i++)
            if (p.ReadUInt8(name, (int)i) == ':') { directory = geometry.RootBlock; offset = i + 1; }
        var rootStart = offset;
        var prefixIndex = APTR.Null;
        var view = APTR.Null;
        if (AmigaFfsMountIndex.Enabled && directory == geometry.RootBlock && mountIndex.IsNotNull &&
            p.ReadUInt32(mountIndex, AmigaFfsMountIndex.Status) is AmigaFfsMountIndex.Clean or AmigaFfsMountIndex.MetadataOpen &&
            p.ReadUInt32(mountIndex, AmigaFfsMountIndex.Type) == dosType)
        {
            // Build verified this root's identity, checksum and table size.
            // Grouped namespace operations update the same private hash table.
            var root = APTR.FromPointer(p.ReadUInt32(mountIndex, AmigaFfsMountIndex.RootImage));
            if (root.IsNotNull && !Overlaps(name, length, root, geometry.BlockBytes)) view = root;
            if (view.IsNotNull && length > offset &&
                !Overlaps(name, length, APTR.FromPointer(mountIndex.Raw + AmigaFfsMountIndex.PrefixName), AmigaFfsMountIndex.PrefixCapacity))
            {
                prefixIndex = mountIndex;
                var child = p.ReadUInt32(prefixIndex, AmigaFfsMountIndex.PrefixDirectory);
                if (child != 0)
                {
                    var bytes = p.ReadUInt32(prefixIndex, AmigaFfsMountIndex.PrefixLength);
                    // The saved prefix ends just after a checked directory's
                    // slash. A parent lookup may request the same name alone.
                    if (bytes == length - offset + 1) bytes--;
                    if (bytes <= length - offset)
                    {
                        uint i = 0;
                        while (i < bytes && p.ReadUInt8(prefixIndex, AmigaFfsMountIndex.PrefixName + (int)i) ==
                            p.ReadUInt8(name, (int)(offset + i))) i++;
                        if (i == bytes)
                        {
                            directory = child;
                            offset += bytes;
                            view = APTR.Null;
                        }
                    }
                }
            }
        }
        for (var depth = 0; depth <= 255; depth++)
        {
            if (view.IsNull) view = HeaderView(ref p, in geometry, directory, buffer, name, length, false);
            if (view.IsNull) { error = DOS.Error.DiskNotValidated; return 0; }
            if (offset == length)
            {
                image = view;
                return directory;
            }
            if (unchecked((int)p.ReadUInt32(view, geometry.Tail + 508)) < 0) { error = DOS.Error.ObjectWrongType; return 0; }
            if (p.ReadUInt8(name, (int)offset) == '/')
            {
                directory = directory == geometry.RootBlock ? geometry.RootBlock : p.ReadUInt32(view, geometry.Tail + 500);
                view = APTR.Null;
                offset++;
                continue;
            }
            var start = offset;
            while (offset < length && p.ReadUInt8(name, (int)offset) != '/') offset++;
            var count = offset - start;
            if (count == 0 || count > NameLimit(lnfs)) { error = DOS.Error.InvalidComponentName; return 0; }
            var slot = 24 + (int)NameBucket(ref p, APTR.FromPointer(name.Raw + start), count, dosType, tableEntries) * 4;
            var entry = p.ReadUInt32(view, slot);
            if (AmigaFfsMountIndex.Enabled && entry != 0 && directory == geometry.RootBlock && prefixIndex.IsNotNull &&
                AmigaFfsMountIndex.SoleRootChildDiffers(ref p, prefixIndex, slot, entry, count))
                entry = 0;
            var found = false;
            // Brent's cycle check: a damaged chain that loops back is refused
            // after about twice its length, not after BlockCount reads.
            uint mark = 0, power = 1, run = 0;
            for (var guard = 0u; entry != 0 && guard < geometry.BlockCount; guard++)
            {
                if (entry == mark) { error = DOS.Error.DiskNotValidated; return 0; }
                if (++run == power) { mark = entry; power <<= 1; run = 0; }
                // Hard and soft link headers share the hash chains of ordinary entries.
                view = HeaderView(ref p, in geometry, entry, buffer, name, length, true);
                if (view.IsNull || p.ReadUInt32(view, geometry.Tail + 500) != directory)
                { error = DOS.Error.DiskNotValidated; return 0; }
                var matches = p.ReadUInt8(view, nameAt) == count;
                for (var i = 0u; matches && i < count; i++)
                {
                    var stored = p.ReadUInt8(view, nameAt + 1 + (int)i);
                    var requested = p.ReadUInt8(name, (int)(start + i));
                    matches = stored == requested || Upper(stored, dosType) == Upper(requested, dosType);
                }
                if (matches) { found = true; break; }
                entry = p.ReadUInt32(view, geometry.Tail + 496);
            }
            if (!found)
            {
                error = entry != 0 ? DOS.Error.DiskNotValidated : DOS.Error.ObjectNotFound;
                if (entry == 0 && offset == length)
                {
                    missing.Parent = directory;
                    missing.Start = start;
                    missing.HashSlot = slot;
                }
                return 0;
            }
            // Kickstart FFS follows hard links to their object and reports a
            // soft link to dos.library, which reads it and retries the path.
            // A caller acting on the named entry itself keeps the final link.
            var linkType = unchecked((int)p.ReadUInt32(view, geometry.Tail + 508));
            if (linkType is 3 or 4 or -4 && (offset < length || followFinal))
            {
                if (linkType == 3) { error = DOS.Error.IsSoftLink; return 0; }
                var target = p.ReadUInt32(view, geometry.Tail + 468);
                view = HeaderView(ref p, in geometry, target, buffer, name, length, false);
                if (view.IsNull || unchecked((int)p.ReadUInt32(view, geometry.Tail + 508)) != (linkType == 4 ? 2 : -3))
                { error = DOS.Error.DiskNotValidated; return 0; }
                entry = target;
            }
            directory = entry;
            if (offset < length)
            {
                offset++; // separator; a second slash means parent
                var prefix = offset - rootStart;
                // A checked directory survives file writes and sibling creates.
                // Rename and directory removal expire this root-relative path.
                if (AmigaFfsMountIndex.Enabled && prefixIndex.IsNotNull && prefix <= AmigaFfsMountIndex.PrefixCapacity &&
                    p.ReadUInt32(view, geometry.Tail + 508) == 2)
                {
                    p.Copy(APTR.FromPointer(name.Raw + rootStart),
                        APTR.FromPointer(prefixIndex.Raw + AmigaFfsMountIndex.PrefixName), prefix);
                    p.WriteUInt32(prefixIndex, AmigaFfsMountIndex.PrefixLength, prefix);
                    p.WriteUInt32(prefixIndex, AmigaFfsMountIndex.PrefixDirectory, entry);
                }
            }
        }
        error = DOS.Error.DiskNotValidated;
        return 0;
    }

    public static int Read<T>(ref T p, uint header, uint dosType, uint position,
        APTR destination, uint length, APTR metadata, APTR data, out DOS.Error error)
        where T : struct, IAmigaFileSystemPlatform =>
        Read(ref p, header, dosType, position, destination, length, metadata, data, out error, APTR.Null);

    public static int Read<T>(ref T p, uint header, uint dosType, uint position,
        APTR destination, uint length, APTR metadata, APTR data, out DOS.Error error, APTR mountIndex)
        where T : struct, IAmigaFileSystemPlatform
    {
        var cursor = new ReadCursor();
        return Read(ref p, ref cursor, header, dosType, position, destination, length, metadata, data, out error, out _, mountIndex);
    }

    /// <summary>Caller-owned FFS traversal state. Clear Header after any mutation,
    /// recovery or media change. No pointer into shared scratch survives a call.</summary>
    public struct ReadCursor
    {
        public uint Header, Size, Table, TableIndex, RunStart, RunBlock, RunCount, DosType, NextTable;
    }

    public static int Read<T>(ref T p, ref ReadCursor cursor, uint header, uint dosType, uint position,
        APTR destination, uint length, APTR metadata, APTR data, out DOS.Error error)
        where T : struct, IAmigaFileSystemPlatform =>
        Read(ref p, ref cursor, header, dosType, position, destination, length, metadata, data, out error, out _);

    /// <summary>Reports whether traversal state was changed or invalidated, so
    /// native adapters can leave an unchanged caller-owned record in place.</summary>
    public static int Read<T>(ref T p, ref ReadCursor cursor, uint header, uint dosType, uint position,
        APTR destination, uint length, APTR metadata, APTR data, out DOS.Error error, out bool cursorChanged)
        where T : struct, IAmigaFileSystemPlatform =>
        Read(ref p, ref cursor, header, dosType, position, destination, length, metadata, data, out error, out cursorChanged, APTR.Null);

    public static int Read<T>(ref T p, ref ReadCursor cursor, uint header, uint dosType, uint position,
        APTR destination, uint length, APTR metadata, APTR data, out DOS.Error error, out bool cursorChanged, APTR mountIndex)
        where T : struct, IAmigaFileSystemPlatform
    {
        cursorChanged = false;
        // A retained run needs only the stable block-size scalar. Construct
        // device geometry only when pointer-table traversal is necessary.
        var blockBytes = p.BlockBytes;
        var tableEntries = blockBytes / 4 - 56;
        // Preserve OFS checksums and the legacy forward-copy semantics when
        // caller memory overlaps either scratch block.
        if ((dosType & 1) == 0 || Overlaps(destination, length, metadata, blockBytes) ||
            Overlaps(destination, length, data, blockBytes))
        {
            cursorChanged = true;
            cursor.Header = 0;
            return ReadSingleBlocks(ref p, header, dosType, position, destination, length, metadata, data, out error, mountIndex);
        }
        error = DOS.Error.None;
        var copied = 0u;
        // A pointer-table boundary can split a large caller request into a
        // short run. It is still streaming I/O, not a small read-ahead packet.
        var readAhead = length <= 4096;
        var loaded = false;
        var tableView = metadata;
        var tail = (int)blockBytes - 512;
        if (length > int.MaxValue || !p.IsMapped(destination, length) || tableEntries == 0) goto failed;
        if (cursor.Header != header || header == 0 || cursor.DosType != dosType)
        {
            cursorChanged = true;
            cursor.Header = 0;
            tableView = FileHeaderView(ref p, header, metadata, destination, length);
            if (tableView.IsNull ||
                unchecked((int)p.ReadUInt32(tableView, tail + 508)) != -3) goto failed;
            cursor.Size = p.ReadUInt32(tableView, tail + 324);
            cursor.Table = header; cursor.TableIndex = 0; cursor.RunCount = 0;
            cursor.NextTable = p.ReadUInt32(tableView, tail + 504);
            cursor.DosType = dosType; cursor.Header = header;
            loaded = true;
        }
        if (position > cursor.Size) { error = DOS.Error.SeekError; cursorChanged = true; cursor.Header = 0; return -1; }
        if (length > cursor.Size - position) length = cursor.Size - position;
        while (copied < length)
        {
            var index = blockBytes == 512 ? position >> 9 : AmigaGeometryMath.Divide(position, blockBytes);
            if (cursor.RunCount == 0 || index < cursor.RunStart || index - cursor.RunStart >= cursor.RunCount)
            {
                cursorChanged = true;
                var volumeBlocks = p.BlockCount;
                var reserved = p.ReservedBlocks;
                var wantedTable = AmigaGeometryMath.Divide(index, tableEntries);
                if (wantedTable >= volumeBlocks) goto failed;
                if (wantedTable < cursor.TableIndex)
                {
                    cursor.Table = header; cursor.TableIndex = 0;
                    tableView = FileHeaderView(ref p, header, metadata, destination, length);
                    if (tableView.IsNull ||
                        unchecked((int)p.ReadUInt32(tableView, tail + 508)) != -3) goto failed;
                    cursor.NextTable = p.ReadUInt32(tableView, tail + 504);
                    loaded = true;
                }
                if (!loaded && cursor.TableIndex == wantedTable)
                {
                    if (cursor.TableIndex == 0)
                    {
                        tableView = FileHeaderView(ref p, header, metadata, destination, length);
                        if (tableView.IsNull ||
                            unchecked((int)p.ReadUInt32(tableView, tail + 508)) != -3) goto failed;
                    }
                    else
                    {
                        tableView = ReadExtensionView(ref p, volumeBlocks, reserved, tail, header, cursor.Table, metadata, destination, length);
                        if (tableView.IsNull) goto failed;
                    }
                    cursor.NextTable = p.ReadUInt32(tableView, tail + 504);
                    loaded = true;
                }
                while (cursor.TableIndex < wantedTable)
                {
                    var table = cursor.NextTable;
                    tableView = ReadExtensionView(ref p, volumeBlocks, reserved, tail, header, table, metadata, destination, length);
                    if (tableView.IsNull) goto failed;
                    cursor.Table = table; cursor.TableIndex++;
                    cursor.NextTable = p.ReadUInt32(tableView, tail + 504);
                    loaded = true;
                }
                var used = p.ReadUInt32(tableView, 8);
                var slot = index - wantedTable * tableEntries;
                if (used > tableEntries || slot >= used) goto failed;
                var pointer = tail + 308 - (int)slot * 4;
                var block = p.ReadUInt32(tableView, pointer);
                if (block < reserved || block >= volumeBlocks) goto failed;
                // Stop before a bad or fragmented pointer. Its validation still
                // happens only after the preceding data has been delivered.
                var limit = used - slot;
                if (limit > volumeBlocks - block) limit = volumeBlocks - block;
                cursor.RunStart = index; cursor.RunBlock = block;
                cursor.RunCount = ContiguousPointers(ref p, tableView, pointer, block, limit);
                // Scratch survives a data transfer; a borrowed cache image
                // may expire before the next table lookup.
                loaded = tableView == metadata;
            }
            var physical = cursor.RunBlock + index - cursor.RunStart;
            var within = blockBytes == 512 ? position & 511 : AmigaGeometryMath.Remainder(position, blockBytes);
            var target = APTR.FromPointer(destination.Raw + copied);
            var remaining = length - copied;
            if (within == 0 && remaining >= blockBytes && ((target.Raw & 3) == 0 || p.SupportsUnalignedDataReads))
            {
                var blocks = blockBytes == 512 ? remaining >> 9 : AmigaGeometryMath.Divide(remaining, blockBytes);
                var available = cursor.RunCount - (index - cursor.RunStart);
                if (blocks > available) blocks = available;
                var read = readAhead ? p.ReadDataBlocks(physical, blocks, target)
                    : p.ReadDataBlocks(physical, blocks, target, false);
                if (read > blocks) goto failed;
                var bytes = read * blockBytes;
                copied += bytes; position += bytes;
                if (read != blocks) goto failed;
            }
            else
            {
                var count = blockBytes - within;
                if (count > remaining) count = remaining;
                var image = p.ReadRetainedBlock(physical);
                if (image.IsNull || Overlaps(target, count, image, blockBytes))
                {
                    if (!p.ReadBlock(physical, data)) goto failed;
                    image = data;
                }
                p.Copy(APTR.FromPointer(image.Raw + within), target, count);
                copied += count; position += count;
            }
        }
        return (int)copied;
    failed:
        cursorChanged = true;
        cursor.Header = 0;
        error = DOS.Error.DiskNotValidated;
        return copied == 0 ? -1 : (int)copied;
    }

    private static bool Overlaps(APTR a, uint length, APTR b, uint bytes) =>
        length != 0 && bytes != 0 && (a.Raw <= b.Raw ? b.Raw - a.Raw < length : a.Raw - b.Raw < bytes);

    // FFS stores its data pointers in reverse order. The first was validated
    // by the caller, and limit bounds both the remaining table and the disk.
    private static uint ContiguousPointers<T>(ref T p, APTR table, int pointer, uint first, uint limit)
        where T : struct, IAmigaFileSystemPlatform
    {
        var expected = first;
        // Keep the table address in one register. Constant displacements let
        // the native compiler read adjacent entries without rebuilding a
        // dynamic byte offset for every pointer.
        var cursor = APTR.FromPointer(table.Raw + (uint)pointer);
        while (limit > 4)
        {
            if (p.ReadUInt32(cursor, -4) != expected + 1) return expected - first + 1;
            if (p.ReadUInt32(cursor, -8) != expected + 2) return expected - first + 2;
            if (p.ReadUInt32(cursor, -12) != expected + 3) return expected - first + 3;
            if (p.ReadUInt32(cursor, -16) != expected + 4) return expected - first + 4;
            cursor = APTR.FromPointer(cursor.Raw - 16);
            expected += 4; limit -= 4;
        }
        while (limit > 1 && p.ReadUInt32(cursor, -4) == expected + 1)
        {
            cursor = APTR.FromPointer(cursor.Raw - 4);
            expected++; limit--;
        }
        return expected - first + 1;
    }

    private static APTR ReadExtensionView<T>(ref T p, uint blockCount, uint reserved, int tail, uint header, uint block,
        APTR metadata, APTR output, uint outputBytes)
        where T : struct, IAmigaFileSystemPlatform
    {
        if (block < reserved || block >= blockCount) return APTR.Null;
        var view = CheckedFfsMetadataView(ref p, block, metadata, output, outputBytes);
        return view.IsNotNull && p.ReadUInt32(view) == 16 &&
            p.ReadUInt32(view, 4) == block && p.ReadUInt32(view, tail + 500) == header &&
            unchecked((int)p.ReadUInt32(view, tail + 508)) == -3 ? view : APTR.Null;
    }

    private static bool TryReadOfsPayload<T>(ref T p, APTR directory, uint header, uint dosType, uint position,
        APTR destination, uint length, APTR metadata, APTR data) where T : struct, IAmigaFileSystemPlatform
    {
        if (directory.IsNull || length == 0 || header == 0 ||
            p.ReadUInt32(directory, AmigaFfsMountIndex.Status) != AmigaFfsMountIndex.Clean ||
            p.ReadUInt32(directory, AmigaFfsMountIndex.ReadHeader) != header ||
            p.ReadUInt32(directory, AmigaFfsMountIndex.Type) != dosType) return false;
        var offset = unchecked(position - p.ReadUInt32(directory, AmigaFfsMountIndex.ReadStart));
        var bytes = p.ReadUInt32(directory, AmigaFfsMountIndex.ReadBytes);
        if (offset >= bytes || length > bytes - offset || !p.IsMapped(destination, length) ||
            Overlaps(destination, length, metadata, p.BlockBytes) || Overlaps(destination, length, data, p.BlockBytes) ||
            Overlaps(destination, length, directory, p.ReadUInt32(directory, AmigaFfsMountIndex.Bytes))) return false;
        p.Copy(APTR.FromPointer(p.ReadUInt32(directory, AmigaFfsMountIndex.BitmapImage) + offset), destination, length);
        return true;
    }

    internal static int ReadSingleBlocks<T>(ref T p, uint header, uint dosType, uint position,
        APTR destination, uint length, APTR metadata, APTR data, out DOS.Error error, APTR directory)
        where T : struct, IAmigaFileSystemPlatform
    {
        error = DOS.Error.None;
        if (AmigaFfsMountIndex.Enabled && TryReadOfsPayload(ref p, directory, header, dosType, position, destination, length, metadata, data))
            return (int)length;
        var geometry = AmigaFfsGeometry.Read(ref p);
        if (length > int.MaxValue || !p.IsMapped(destination, length))
        { error = DOS.Error.DiskNotValidated; return -1; }
        // The single-owner mount verified the file type, length and complete
        // pointer chain together. Its clean proof expires on every untracked write.
        var indexed = AmigaFfsMountIndex.Enabled && directory.IsNotNull && (dosType & 1) == 0 && header != 0 &&
            p.ReadUInt32(directory, AmigaFfsMountIndex.Status) == AmigaFfsMountIndex.Clean &&
            p.ReadUInt32(directory, AmigaFfsMountIndex.Type) == dosType &&
            p.ReadUInt32(directory, AmigaFfsMountIndex.Header) == header &&
            !Overlaps(destination, length, metadata, geometry.BlockBytes) &&
            !Overlaps(destination, length, data, geometry.BlockBytes) &&
            !Overlaps(destination, length, directory, p.ReadUInt32(directory, AmigaFfsMountIndex.Bytes));
        if ((!AmigaFfsMountIndex.Enabled || !indexed) && (!Header(ref p, in geometry, header, metadata, false) ||
            unchecked((int)p.ReadUInt32(metadata, geometry.Tail + 508)) != -3))
        { error = DOS.Error.DiskNotValidated; return -1; }
        var size = AmigaFfsMountIndex.Enabled && indexed ? p.ReadUInt32(directory, AmigaFfsMountIndex.FileBytes) :
            p.ReadUInt32(metadata, geometry.Tail + 324);
        if (position > size) { error = DOS.Error.SeekError; return -1; }
        if (length > size - position) length = size - position;
        var payload = (dosType & 1) != 0 ? geometry.BlockBytes : geometry.BlockBytes - 24;
        var tableEntries = geometry.TableEntries;
        if (payload == 0 || tableEntries == 0) { error = DOS.Error.DiskNotValidated; return -1; }
        if (length == 0) return 0;
        var staged = AmigaFfsMountIndex.Enabled && directory.IsNotNull && p.ReadUInt32(directory, AmigaFfsMountIndex.Status) == AmigaFfsMountIndex.Clean &&
            (dosType & 1) == 0 && !Overlaps(destination, length, metadata, geometry.BlockBytes) &&
            !Overlaps(destination, length, data, geometry.BlockBytes);
        var fileKeys = AmigaFfsMountIndex.Enabled && indexed ? AmigaFfsMountIndex.DataKeys(ref p, directory) : APTR.Null;
        var window = AmigaFfsMountIndex.Enabled && staged ? APTR.FromPointer(p.ReadUInt32(directory, AmigaFfsMountIndex.Stage)) : APTR.Null;
        var capacity = AmigaFfsMountIndex.Enabled && staged ? p.ReadUInt32(directory, AmigaFfsMountIndex.StageBytes) / geometry.BlockBytes : 0;
        uint windowBlock = 0, windowCount = 0, failedBlock = uint.MaxValue;
        var copied = 0u;
        var table = header;
        var tableIndex = 0u;
        var ofsSequenceBase = 0u;
        while (copied < length)
        {
            var index = AmigaGeometryMath.DivRem(position, payload, out var withinRemainder);
            var extensions = AmigaGeometryMath.DivRem(index, tableEntries, out var slotRemainder);
            var slot = slotRemainder;
            if (extensions >= geometry.BlockCount) break;
            var valid = true;
            // metadata retains the current pointer table while data sectors
            // use the separate data buffer. Restarting at the file header on
            // every sector makes a large sequential read seek back and forth
            // through every preceding extension table.
            for (; (!AmigaFfsMountIndex.Enabled || fileKeys.IsNull) && tableIndex < extensions; tableIndex++)
            {
                table = p.ReadUInt32(metadata, geometry.Tail + 504);
                if (table < geometry.ReservedBlocks || table >= geometry.BlockCount || !p.ReadBlock(table, metadata) ||
                    !Checksum(ref p, metadata) || p.ReadUInt32(metadata) != 16 ||
                    p.ReadUInt32(metadata, 4) != table || p.ReadUInt32(metadata, geometry.Tail + 500) != header ||
                    unchecked((int)p.ReadUInt32(metadata, geometry.Tail + 508)) != -3) { valid = false; break; }
            }
            if ((!AmigaFfsMountIndex.Enabled || fileKeys.IsNull) &&
                (!valid || p.ReadUInt32(metadata, 8) > tableEntries || slot >= p.ReadUInt32(metadata, 8))) break;
            var block = AmigaFfsMountIndex.Enabled && fileKeys.IsNotNull ? p.ReadUInt32(fileKeys, (int)index * 4) :
                p.ReadUInt32(metadata, geometry.Tail + 308 - (int)slot * 4);
            if (block < geometry.ReservedBlocks || block >= geometry.BlockCount ||
                (AmigaFfsMountIndex.Enabled && block == failedBlock)) break;
            var image = data;
            if (AmigaFfsMountIndex.Enabled && block - windowBlock >= windowCount && capacity >= 2 && length - copied >= payload * 2)
            {
                var wanted = (length - copied - 1) / payload + 1;
                if (wanted > capacity) wanted = capacity;
                var entries = fileKeys.IsNotNull ? p.ReadUInt32(directory, AmigaFfsMountIndex.FileBlocks) - index :
                    p.ReadUInt32(metadata, 8) - slot;
                if (wanted > entries) wanted = entries;
                uint run = 1;
                while (run < wanted && run < geometry.BlockCount - block &&
                    (fileKeys.IsNotNull ? p.ReadUInt32(fileKeys, (int)(index + run) * 4) :
                    p.ReadUInt32(metadata, geometry.Tail + 308 - (int)(slot + run) * 4)) == block + run) run++;
                windowBlock = block;
                windowCount = run >= 2 ? p.ReadDataBlocks(block, run, window, false) : 0;
                if (run >= 2 && windowCount < run)
                {
                    if (windowCount == 0) break;
                    failedBlock = block + windowCount;
                }
            }
            if (AmigaFfsMountIndex.Enabled && block - windowBlock < windowCount)
                image = APTR.FromPointer(window.Raw + (block - windowBlock) * geometry.BlockBytes);
            else
            {
                var retained = AmigaFfsMountIndex.Enabled && indexed ? p.ReadRetainedBlock(block) : APTR.Null;
                if (retained.IsNotNull && !Overlaps(destination, length, retained, geometry.BlockBytes)) image = retained;
                else if (!p.ReadBlock(block, data)) break;
            }
            var within = withinRemainder;
            var count = payload - within;
            if (count > length - copied) count = length - copied;
            var dataOffset = 0u;
            if ((dosType & 1) == 0)
            {
                var sequence = p.ReadUInt32(image, 8);
                if (!Checksum(ref p, image) || p.ReadUInt32(image) != 8 ||
                    p.ReadUInt32(image, 4) != header ||
                    p.ReadUInt32(image, 12) > geometry.BlockBytes - 24 || p.ReadUInt32(image, 12) < within + count) break;
                if ((!AmigaFfsMountIndex.Enabled || fileKeys.IsNull) && sequence != index + 1 &&
                    (ofsSequenceBase == 0 || sequence != index + 1 - ofsSequenceBase))
                {
                    if (sequence == 0 || sequence > index ||
                        !OfsAppendSequence(ref p, header, index + 1 - sequence, table, block, metadata, data)) break;
                    ofsSequenceBase = index + 1 - sequence;
                }
                dataOffset = 24;
                if (AmigaFfsMountIndex.Enabled && copied == 0 && count == length && staged &&
                    dosType == p.ReadUInt32(directory, AmigaFfsMountIndex.Type) &&
                    !Overlaps(destination, length, directory, p.ReadUInt32(directory, AmigaFfsMountIndex.Bytes)))
                {
                    var saved = APTR.FromPointer(p.ReadUInt32(directory, AmigaFfsMountIndex.BitmapImage));
                    if (saved.IsNotNull)
                    {
                        var start = position - within;
                        var validBytes = p.ReadUInt32(image, 12);
                        if (validBytes > size - start) validBytes = size - start;
                        p.Copy(APTR.FromPointer(image.Raw + dataOffset), saved, validBytes);
                        p.WriteUInt32(directory, AmigaFfsMountIndex.ReadStart, start);
                        p.WriteUInt32(directory, AmigaFfsMountIndex.ReadBytes, validBytes);
                        p.WriteUInt32(directory, AmigaFfsMountIndex.ReadHeader, header);
                    }
                }
            }
            var source = APTR.FromPointer(image.Raw + dataOffset + within);
            var target = APTR.FromPointer(destination.Raw + copied);
            var distance = source.Raw > target.Raw ? source.Raw - target.Raw : target.Raw - source.Raw;
            if (distance >= count && count <= uint.MaxValue - source.Raw && count <= uint.MaxValue - target.Raw)
                p.Copy(source, target, count);
            else
                for (var i = 0u; i < count; i++) p.WriteUInt8(destination, (int)(copied + i), p.ReadUInt8(image, (int)(dataOffset + within + i)));
            copied += count;
            position += count;
        }
        if (copied != length) { error = DOS.Error.DiskNotValidated; return copied == 0 ? -1 : (int)copied; }
        return (int)copied;
    }

    // FFS 40.1's OFS append leaves the previous EOF link zero and numbers newly
    // allocated data from one. Recognize that boundary, including random reads
    // into the appended run; ordinary canonical reads do no additional I/O.
    private static bool OfsAppendSequence<T>(ref T p, uint header, uint start,
        uint table, uint block, APTR metadata, APTR data) where T : struct, IAmigaFileSystemPlatform
    {
        if (start == 0 || !OfsPointer(ref p, header, start - 1, metadata, out var previous) ||
            !p.ReadBlock(previous, data) || !Checksum(ref p, data) || p.ReadUInt32(data) != 8 ||
            p.ReadUInt32(data, 4) != header || p.ReadUInt32(data, 16) != 0 ||
            !OfsPointer(ref p, header, start, metadata, out var first) || first == previous ||
            !p.ReadBlock(first, data) || !Checksum(ref p, data) || p.ReadUInt32(data) != 8 ||
            p.ReadUInt32(data, 4) != header || p.ReadUInt32(data, 8) != 1) return false;
        return p.ReadBlock(table, metadata) && p.ReadBlock(block, data);
    }

    private static bool OfsPointer<T>(ref T p, uint header, uint index, APTR metadata,
        out uint block) where T : struct, IAmigaFileSystemPlatform
    {
        var g = AmigaFfsGeometry.Read(ref p); block = 0;
        if (!Header(ref p, header, metadata)) return false;
        var table = header;
        var pages = AmigaGeometryMath.DivRem(index, g.TableEntries, out var slotRemainder);
        var slot = slotRemainder;
        for (var page = 0u; page < pages; page++)
        {
            if (page >= g.BlockCount) return false;
            table = p.ReadUInt32(metadata, g.Tail + 504);
            if (table < g.ReservedBlocks || table >= g.BlockCount || !p.ReadBlock(table, metadata) ||
                !Checksum(ref p, metadata) || p.ReadUInt32(metadata) != 16 || p.ReadUInt32(metadata, 4) != table ||
                p.ReadUInt32(metadata, g.Tail + 500) != header || p.ReadUInt32(metadata, g.Tail + 508) != 0xFFFFFFFD) return false;
        }
        if (p.ReadUInt32(metadata, 8) > g.TableEntries || slot >= p.ReadUInt32(metadata, 8)) return false;
        block = p.ReadUInt32(metadata, g.Tail + 308 - (int)slot * 4);
        return block >= g.ReservedBlocks && block < g.BlockCount && block != header && block != table;
    }

    public static bool Examine<T>(ref T p, uint key, APTR fib, APTR buffer) where T : struct, IAmigaFileSystemPlatform
        => Examine(ref p, key, fib, buffer, 0);

    // dosType selects the classic or DOS\6/7 long-name header layout; 0 is classic.
    public static bool Examine<T>(ref T p, uint key, APTR fib, APTR buffer, uint dosType) where T : struct, IAmigaFileSystemPlatform
    {
        if (!p.IsMapped(fib, FileInfoBlock.SizeInBytes) || !Header(ref p, key, buffer)) return false;
        return ExamineHeader(ref p, key, fib, buffer, dosType);
    }

    // The serial handler has already validated this header. Do not read and
    // checksum the same block again merely to project it into a FIB. A
    // long-name comment block is read into buffer, then the header is restored.
    private static bool ExamineHeader<T>(ref T p, uint key, APTR fib, APTR buffer, uint dosType) where T : struct, IAmigaFileSystemPlatform
    {
        var geometry = AmigaFfsGeometry.Read(ref p);
        var blockBytes = geometry.BlockBytes;
        if (blockBytes == 0) return false;
        var root = key == geometry.RootBlock;
        var lnfs = LongNames(dosType) && !root;
        p.Clear(fib, FileInfoBlock.SizeInBytes);
        // fib_DiskKey is the header key, as in Kickstart FFS.
        p.WriteUInt32(fib, 0, key);
        var type = p.ReadUInt32(buffer, geometry.Tail + 508);
        p.WriteUInt32(fib, 4, type);
        p.WriteUInt32(fib, 120, type);
        var nameAt = geometry.Tail + NameOffset(lnfs);
        var nameLength = StoredNameLength(ref p, buffer, geometry.Tail, lnfs);
        if (nameLength > NameLimit(lnfs)) return false;
        // Packet FIB names use the SDK's length-prefixed representation.
        // Native DOS name/path reconstruction consumes this packet record.
        p.WriteUInt8(fib, 8, (byte)nameLength);
        for (var i = 0; i < nameLength; i++) p.WriteUInt8(fib, 9 + i, p.ReadUInt8(buffer, nameAt + 1 + i));
        p.WriteUInt32(fib, 116, key == geometry.RootBlock ? 0 : p.ReadUInt32(buffer, geometry.Tail + 320));
        var file = unchecked((int)type) < 0;
        var size = file ? p.ReadUInt32(buffer, geometry.Tail + 324) : 0;
        p.WriteUInt32(fib, 124, size);
        // Kickstart 3.1 FFS reports size / block size + 1 for OFS and FFS files
        // alike (0 bytes -> 1, 512 -> 2), not the blocks actually used.
        p.WriteUInt32(fib, 128, file ? AmigaGeometryMath.Divide(size, blockBytes) + 1 : 0);
        // fib_OwnerUID and fib_OwnerGID from the header's owner longword.
        if (key != geometry.RootBlock)
        {
            var owner = p.ReadUInt32(buffer, geometry.Tail + 316);
            p.WriteUInt16(fib, 224, (ushort)(owner >> 16));
            p.WriteUInt16(fib, 226, (ushort)owner);
        }
        var dateAt = geometry.Tail + (lnfs ? LongDate : 420);
        for (var i = 0; i < 12; i += 4) p.WriteUInt32(fib, 132 + i, p.ReadUInt32(buffer, dateAt + i));
        if (root) return true;
        // Classic headers keep the comment at Tail+328; LNFS stores it right
        // after the name, or in a separate comment block when both do not fit.
        var commentAt = lnfs ? nameAt + 1 + (int)nameLength : geometry.Tail + 328;
        var commentLength = p.ReadUInt8(buffer, commentAt);
        if (commentLength > 79 || (lnfs && nameLength + commentLength + 2 > LongNameBytes)) return false;
        p.WriteUInt8(fib, 144, commentLength);
        for (var i = 0; i < commentLength; i++) p.WriteUInt8(fib, 145 + i, p.ReadUInt8(buffer, commentAt + 1 + i));
        if (!lnfs) return true;
        var commentBlock = p.ReadUInt32(buffer, geometry.Tail + LongCommentKey);
        if (commentBlock == 0) return true;
        // An entry keeps either an inline comment or a comment block.
        if (commentLength != 0 || commentBlock < geometry.ReservedBlocks || commentBlock >= geometry.BlockCount ||
            commentBlock == key) return false;
        if (!p.ReadBlock(commentBlock, buffer) || !Checksum(ref p, buffer) || p.ReadUInt32(buffer) != CommentBlockType ||
            p.ReadUInt32(buffer, 4) != commentBlock || p.ReadUInt32(buffer, 8) != key) return false;
        commentLength = p.ReadUInt8(buffer, 24);
        if (commentLength > 79) return false;
        p.WriteUInt8(fib, 144, commentLength);
        for (var i = 0; i < commentLength; i++) p.WriteUInt8(fib, 145 + i, p.ReadUInt8(buffer, 25 + i));
        // Callers may still read the entry header from buffer.
        return Header(ref p, in geometry, key, buffer, true);
    }

    // Private acceleration only: fib_DiskKey (the last returned header key)
    // is authoritative. A different, interleaved or invalidated cursor resumes
    // from that key. Ordinal holds the key this cursor last returned.
    public struct DirectoryCursor
    {
        public uint Directory, Ordinal, Slot, Entry, Visited;
    }

    public static DOS.Error ExamineNext<T>(ref T p, uint directory, APTR fib, APTR metadata, APTR table)
        where T : struct, IAmigaFileSystemPlatform
    {
        var cursor = new DirectoryCursor();
        return ExamineNext(ref p, ref cursor, directory, fib, metadata, table);
    }

    // dosType selects the name hash used to resume; 0 reads it from the boot block.
    public static DOS.Error ExamineNext<T>(ref T p, ref DirectoryCursor cursor,
        uint directory, APTR fib, APTR metadata, APTR table, uint dosType = 0)
        where T : struct, IAmigaFileSystemPlatform
        // Kickstart FFS lists link entries with their own secondary types.
        => ExamineNextCore(ref p, ref cursor, directory, fib, metadata, table, true, dosType);

    /// <summary>Inspect link entries without resolving them; retained packet enumeration semantics are unchanged.</summary>
    public static DOS.Error ExamineNextIncludingLinks<T>(ref T p, ref DirectoryCursor cursor,
        uint directory, APTR fib, APTR metadata, APTR table)
        where T : struct, IAmigaFileSystemPlatform
        => ExamineNextCore(ref p, ref cursor, directory, fib, metadata, table, true);

    private static DOS.Error ExamineNextCore<T>(ref T p, ref DirectoryCursor cursor,
        uint directory, APTR fib, APTR metadata, APTR table, bool includeLinks, uint dosType = 0)
        where T : struct, IAmigaFileSystemPlatform
    {
        var geometry = AmigaFfsGeometry.Read(ref p);
        if (!p.IsMapped(fib, FileInfoBlock.SizeInBytes) || !Header(ref p, directory, table)) return DOS.Error.InvalidLock;
        if (unchecked((int)p.ReadUInt32(table, geometry.Tail + 508)) < 0) return DOS.Error.ObjectWrongType;
        // Like Kickstart FFS, fib_DiskKey holds the last returned header key
        // (the directory's own key after Examine). The scan continues from
        // that header's hash bucket and successor, so deleting the returned
        // entry between calls skips nothing.
        var wanted = p.ReadUInt32(fib);
        if (wanted >= geometry.BlockCount) return DOS.Error.NoMoreEntries;
        var next = cursor;
        // 0 selects the classic header layout (see the long-name Mount).
        var lnfs = LongNames(dosType);
        if (wanted == 0 || wanted == directory) next = new DirectoryCursor { Directory = directory };
        else if (next.Directory != directory || next.Ordinal != wanted)
        {
            // A deleted entry's header keeps its name and old successor on disk.
            if (dosType == 0)
            {
                if (!p.ReadBlock(0, metadata)) return DOS.Error.DiskNotValidated;
                dosType = p.ReadUInt32(metadata);
                // A legacy caller never mounted a long-name volume.
                if (LongNames(dosType)) return DOS.Error.DiskNotValidated;
            }
            var nameLength = 0u;
            if (Header(ref p, in geometry, wanted, metadata, true) && p.ReadUInt32(metadata, geometry.Tail + 500) == directory)
            {
                nameLength = StoredNameLength(ref p, metadata, geometry.Tail, lnfs);
                if (nameLength == 0 || nameLength > NameLimit(lnfs)) return DOS.Error.DiskNotValidated;
            }
            if (nameLength != 0)
            {
                next = new DirectoryCursor
                {
                    Directory = directory,
                    Slot = NameBucket(ref p, APTR.FromPointer(metadata.Raw + (uint)(geometry.Tail + NameOffset(lnfs) + 1)), nameLength, dosType, geometry.TableEntries),
                    Entry = p.ReadUInt32(metadata, geometry.Tail + 496)
                };
                if (next.Entry == 0) next.Slot++;
            }
            // The freed header was already reused. The invalidated private
            // cursor still knows the bucket: restart at its head.
            else if (next.Directory == directory && next.Slot < geometry.TableEntries)
                next = new DirectoryCursor { Directory = directory, Slot = next.Slot, Visited = next.Visited };
            else return DOS.Error.NoMoreEntries;
        }
        for (; next.Slot < geometry.TableEntries; next.Slot++)
        {
            var entry = next.Entry;
            if (entry == 0) entry = p.ReadUInt32(table, 24 + (int)next.Slot * 4);
            if (entry != 0)
            {
                if (next.Visited >= geometry.BlockCount || !Header(ref p, in geometry, entry, metadata, includeLinks) ||
                    p.ReadUInt32(metadata, geometry.Tail + 500) != directory) return DOS.Error.DiskNotValidated;
                next.Visited++;
                var successor = p.ReadUInt32(metadata, geometry.Tail + 496);
                if (!ExamineHeader(ref p, entry, fib, metadata, dosType)) return DOS.Error.DiskNotValidated;
                // Ordinal carries the last returned key for the private fast path.
                next.Ordinal = entry;
                if (successor == 0) { next.Slot++; next.Entry = 0; }
                else next.Entry = successor;
                cursor = next;
                return DOS.Error.None;
            }
            next.Entry = 0;
        }
        return DOS.Error.NoMoreEntries;
    }
    public static bool BitmapBlock<T>(ref T p, uint index, APTR scratch, out uint block)
        where T : struct, IAmigaFileSystemPlatform
    {
        var cursor = new BitmapCursor();
        return BitmapBlock(ref p, ref cursor, index, scratch, out block);
    }

    // The caller retains scratch between steps. Only the root and each extension
    // are read once, irrespective of how many bitmap pointers they contain.
    public struct BitmapCursor
    {
        public uint Next, Visited, First, Count;
        public int Offset;
    }
    public static bool BitmapBlock<T>(ref T p, ref BitmapCursor cursor, uint index, APTR scratch, out uint block)
        where T : struct, IAmigaFileSystemPlatform
    {
        var geometry = AmigaFfsGeometry.Read(ref p);
        block = 0;
        if (cursor.Count == 0 || index < cursor.First)
        {
            if (!Header(ref p, geometry.RootBlock, scratch)) return false;
            cursor.Offset = geometry.Tail + 316;
            cursor.First = 0; cursor.Count = 25; cursor.Visited = 0;
            cursor.Next = p.ReadUInt32(scratch, geometry.Tail + 416);
        }
        while (index - cursor.First >= cursor.Count)
        {
            if (cursor.Visited++ >= geometry.BlockCount || cursor.Next < geometry.ReservedBlocks ||
                cursor.Next >= geometry.BlockCount || !p.ReadBlock(cursor.Next, scratch)) return false;
            cursor.Offset = 0;
            cursor.First += cursor.Count;
            cursor.Count = geometry.BlockBytes / 4 - 1;
            cursor.Next = p.ReadUInt32(scratch, (int)geometry.BlockBytes - 4);
        }
        block = p.ReadUInt32(scratch, cursor.Offset + (int)(index - cursor.First) * 4);
        return block >= geometry.ReservedBlocks && block < geometry.BlockCount && block != geometry.RootBlock;
    }

    internal static bool CountCheckedBitmap<T>(ref T p, APTR bitmap, uint count, out uint allocated)
        where T : struct, IAmigaFileSystemPlatform
    {
        uint used = 0;
        var sum = p.ReadUInt32(bitmap);
        var remaining = count;
        var word = APTR.FromPointer(bitmap.Raw + 4);
        while (remaining >= 32)
        {
            // Most words on a newly formatted volume are entirely free.
            // Skip eight together without repeating per-word index arithmetic.
            if (remaining >= 256 && (p.ReadUInt32(word) & p.ReadUInt32(word, 4) &
                p.ReadUInt32(word, 8) & p.ReadUInt32(word, 12) & p.ReadUInt32(word, 16) &
                p.ReadUInt32(word, 20) & p.ReadUInt32(word, 24) & p.ReadUInt32(word, 28)) == uint.MaxValue)
            {
                sum = unchecked(sum - 8);
                word = APTR.FromPointer(word.Raw + 32); remaining -= 256; continue;
            }
            var words = remaining >= 256 ? 8u : remaining / 32;
            remaining -= words * 32;
            do
            {
                var free = p.ReadUInt32(word);
                sum = unchecked(sum + free);
                word = APTR.FromPointer(word.Raw + 4);
                if (free == 0) used += 32;
                else if (free != uint.MaxValue) used += 32 - AmigaBitmapWordCore.Population(free);
            } while (--words != 0);
        }
        // The checksum includes every padding bit and word in the last page.
        // Only the in-range low bits of its last logical word describe blocks.
        if (remaining != 0)
        {
            var last = p.ReadUInt32(word);
            used += remaining - AmigaBitmapWordCore.Population(last & ((1u << (int)remaining) - 1));
            sum = unchecked(sum + last);
            word = APTR.FromPointer(word.Raw + 4);
        }
        var end = bitmap.Raw + p.BlockBytes;
        while (word.Raw != end)
        {
            sum = unchecked(sum + p.ReadUInt32(word));
            word = APTR.FromPointer(word.Raw + 4);
        }
        allocated = used;
        return sum == 0;
    }

    public static bool CountUsed<T>(ref T p, APTR scratch, APTR bitmap, out uint used)
        where T : struct, IAmigaFileSystemPlatform =>
        CountUsed(ref p, scratch, bitmap, out used, APTR.Null);

    public static bool CountUsed<T>(ref T p, APTR scratch, APTR bitmap, out uint used, APTR indexMemory)
        where T : struct, IAmigaFileSystemPlatform
    {
        var geometry = AmigaFfsGeometry.Read(ref p);
        if (indexMemory.IsNotNull && p.ReadUInt32(indexMemory, AmigaFfsMountIndex.Status) is AmigaFfsMountIndex.Clean or AmigaFfsMountIndex.Summarized)
        {
            used = p.ReadUInt32(indexMemory, AmigaFfsMountIndex.Used);
            return true;
        }
        used = geometry.ReservedBlocks;
        var key = geometry.ReservedBlocks;
        var cursor = new BitmapCursor();
        for (var index = 0u; key < geometry.BlockCount; index++)
        {
            if (!BitmapBlock(ref p, ref cursor, index, scratch, out var block) || !p.ReadBlock(block, bitmap)) return false;
            var count = geometry.BlockCount - key;
            if (count > geometry.BitmapBits) count = geometry.BitmapBits;
            if (!CountCheckedBitmap(ref p, bitmap, count, out var allocated)) return false;
            used += allocated;
            key += count;
        }
        return true;
    }

}
