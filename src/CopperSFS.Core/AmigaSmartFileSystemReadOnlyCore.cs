using Amiga;

namespace CopperSFS;

public enum AmigaSmartFileSystemFormat : uint
{
    Classic = 0x53465300,
    Sfs2 = 0x53465302
}

/// <summary>
/// Allocation-free traversal of clean classic SFS version 3 and SFS2 version 4 volumes.
/// This API never writes or recovers a volume. Existing classic APIs remain unchanged.
/// The caller supplies distinct block-sized metadata/data buffers and mapped guest memory.
/// </summary>
public static partial class AmigaSmartFileSystemReadOnlyCore
{
    private const uint Objects = 0x4F424A43, Nodes = 0x4E444320, Tree = 0x424E4443, Clean = 0x54524F4B;

    /// <summary>Name and Comment point into the metadata buffer, until it is reused.</summary>
    public struct ReadEntry
    {
        public uint Node, Parent, Protection, ModifiedSeconds;
        public ulong Size;
        public ushort OwnerUid, OwnerGid;
        public byte Flags;
        public APTR Name, Comment;
        public uint NameLength, CommentLength;
        public readonly bool Directory => (Flags & 128) != 0;
        public readonly bool SoftLink => (Flags & 64) != 0;
    }

    internal struct Volume
    {
        public AmigaSmartFileSystemFormat Format;
        public uint Root, NodeTree, ExtentTree;
        public bool CaseSensitive;
        public readonly int Extra => Format == AmigaSmartFileSystemFormat.Sfs2 ? 2 : 0;
    }

    /// <summary>Scalar results of a clean SFS2 mount for pathname lookup.
    /// Discard on mutation, recovery, media change or a change in the logical read view.</summary>
    public readonly struct LookupRoot(uint rootBlock, uint nodeTree, uint firstChild, bool caseSensitive)
    {
        public readonly uint RootBlock = rootBlock, NodeTree = nodeTree, FirstChild = firstChild;
        public readonly bool CaseSensitive = caseSensitive;
    }

    public static bool MountSfs2ForLookup<T>(ref T p, APTR metadata, out LookupRoot lookup)
        where T : struct, IAmigaFileSystemPlatform => MountSfs2ForLookup(ref p, metadata, false, out lookup);

    /// <summary>A remount that needs only lookup scalars can borrow the checked root object.</summary>
    public static bool MountSfs2ForLookup<T>(ref T p, APTR metadata, bool borrowRoot, out LookupRoot lookup)
        where T : struct, IAmigaFileSystemPlatform =>
        MountSfs2ForLookup(ref p, metadata, borrowRoot, out lookup, out _);

    /// <summary>Also return the selected root's extent-tree key for the lifetime of this mount result.</summary>
    public static bool MountSfs2ForLookup<T>(ref T p, APTR metadata, bool borrowRoot, out LookupRoot lookup, out uint extentTree)
        where T : struct, IAmigaFileSystemPlatform
    {
        lookup = default;
        extentTree = 0;
        if (!Open(ref p, metadata, borrowRoot, out var volume, out var root, out var at) ||
            volume.Format != AmigaSmartFileSystemFormat.Sfs2) return false;
        lookup = new LookupRoot(volume.Root, volume.NodeTree, p.ReadUInt32(root, at + 16), volume.CaseSensitive);
        extentTree = volume.ExtentTree;
        return true;
    }

    internal static bool Header<T>(ref T p, uint number, uint id, APTR buffer, AmigaSmartFileSystemFormat format)
        where T : struct, IAmigaFileSystemPlatform =>
        number < p.BlockCount && (format == AmigaSmartFileSystemFormat.Sfs2 && p.TryReadVerifiedSfs2Block(number, id, buffer) ||
        ReadHeader(ref p, number, id, buffer, format));

    private static bool ReadHeader<T>(ref T p, uint number, uint id, APTR buffer, AmigaSmartFileSystemFormat format)
        where T : struct, IAmigaFileSystemPlatform =>
        p.ReadBlock(number, buffer) && p.ReadUInt32(buffer) == id && p.ReadUInt32(buffer, 8) == number && Checksum(ref p, buffer, format);

    private static APTR HeaderView<T>(ref T p, uint number, uint id, APTR scratch, AmigaSmartFileSystemFormat format)
        where T : struct, IAmigaFileSystemPlatform
    {
        if (number >= p.BlockCount) return APTR.Null;
        if (format == AmigaSmartFileSystemFormat.Sfs2)
        {
            if (p.TryReadSfs2MetadataView(number, id, scratch, out var image)) return image;
            image = p.BorrowVerifiedSfs2Block(number, id, scratch);
            if (image.IsNotNull) return image;
        }
        return ReadHeader(ref p, number, id, scratch, format) ? scratch : APTR.Null;
    }

    internal static bool Checksum<T>(ref T p, APTR buffer, AmigaSmartFileSystemFormat format)
        where T : struct, IAmigaFileSystemPlatform
    {
        var bytes = p.BlockBytes;
        if (format == AmigaSmartFileSystemFormat.Sfs2 && (bytes & 3) == 0)
            return AmigaFileSystemBlockIo.ValidSfs2Sum(ref p, buffer, bytes);
        uint sum = 0;
        var i = 0;
        for (; bytes - (uint)i >= 32; i += 32)
        {
            sum = unchecked(sum + p.ReadUInt32(buffer, i) + p.ReadUInt32(buffer, i + 4) +
                p.ReadUInt32(buffer, i + 8) + p.ReadUInt32(buffer, i + 12) +
                p.ReadUInt32(buffer, i + 16) + p.ReadUInt32(buffer, i + 20) +
                p.ReadUInt32(buffer, i + 24) + p.ReadUInt32(buffer, i + 28));
        }
        for (; i < bytes; i += 4) sum = unchecked(sum + p.ReadUInt32(buffer, i));
        return sum == (format == AmigaSmartFileSystemFormat.Sfs2 ? 0xFFFFFFFEu : uint.MaxValue);
    }

    private static bool RootAt<T>(ref T p, uint number, APTR scratch, bool borrow, out APTR buffer, out AmigaSmartFileSystemFormat format,
        out ushort sequence) where T : struct, IAmigaFileSystemPlatform
    {
        format = default; sequence = 0;
        const uint sfs2 = (uint)AmigaSmartFileSystemFormat.Sfs2;
        buffer = borrow ? p.BorrowVerifiedSfs2Block(number, sfs2, scratch) : APTR.Null;
        var verified = buffer.IsNotNull;
        if (!verified)
        {
            buffer = scratch;
            verified = p.TryReadVerifiedSfs2Block(number, sfs2, scratch);
            if (!verified && !p.ReadBlock(number, scratch)) return false;
        }
        var id = p.ReadUInt32(buffer);
        if (id != (uint)AmigaSmartFileSystemFormat.Classic && id != (uint)AmigaSmartFileSystemFormat.Sfs2) return false;
        format = (AmigaSmartFileSystemFormat)id;
        var version = format == AmigaSmartFileSystemFormat.Sfs2 ? 4 : 3;
        if (p.ReadUInt32(buffer, 8) != number || p.ReadUInt16(buffer, 12) != version ||
            p.ReadUInt32(buffer, 48) != p.BlockCount || p.ReadUInt32(buffer, 52) != p.BlockBytes ||
            (!verified && !Checksum(ref p, buffer, format))) return false;
        sequence = p.ReadUInt16(buffer, 14);
        return true;
    }

    /// <summary>Probe both root copies without modifying media. Does not establish a clean mount.</summary>
    public static bool Root<T>(ref T p, APTR metadata, out AmigaSmartFileSystemFormat format)
        where T : struct, IAmigaFileSystemPlatform
    {
        var valid = SelectRoot(ref p, metadata, true, out var volume);
        format = volume.Format;
        return valid;
    }

    private static void DecodeRoot<T>(ref T p, APTR metadata, AmigaSmartFileSystemFormat format, out Volume volume)
        where T : struct, IAmigaFileSystemPlatform
    {
        volume = new Volume { Format = format, Root = p.ReadUInt32(metadata, 104),
            NodeTree = p.ReadUInt32(metadata, 112), ExtentTree = p.ReadUInt32(metadata, 108),
            CaseSensitive = (p.ReadUInt8(metadata, 20) & 128) != 0 };
    }

    private static bool SelectRoot<T>(ref T p, APTR metadata, bool returnImage, out Volume volume)
        where T : struct, IAmigaFileSystemPlatform
    {
        volume = default;
        var g = p.Geometry;
        if (g.BlockCount < 4 || g.ReservedBlocks == 0 || g.ReservedBlocks >= g.BlockCount - 1 ||
            g.BlockBytes < 512 || g.BlockBytes > 32768 || (g.BlockBytes & (g.BlockBytes - 1)) != 0 ||
            !p.IsMapped(metadata, g.BlockBytes)) return false;
        var first = RootAt(ref p, 0, metadata, true, out var image, out var firstFormat, out var firstSequence);
        if (first && !returnImage) DecodeRoot(ref p, image, firstFormat, out volume);
        var last = RootAt(ref p, g.BlockCount - 1, metadata, true, out image, out var lastFormat, out var lastSequence);
        if (first && last && firstFormat != lastFormat) return false;
        if (last && (!first || (short)(lastSequence - firstSequence) > 0))
        {
            if (returnImage)
            {
                volume.Format = lastFormat;
                if (image != metadata) p.Copy(image, metadata, g.BlockBytes);
            }
            else DecodeRoot(ref p, image, lastFormat, out volume);
            return true;
        }
        // Open consumes only the validated scalars. Root's public caller also
        // consumes the selected block image in its own scratch.
        return first && (!returnImage || RootAt(ref p, 0, metadata, false, out _, out volume.Format, out _));
    }

    private static bool Open<T>(ref T p, APTR metadata, out Volume volume)
        where T : struct, IAmigaFileSystemPlatform => Open(ref p, metadata, false, out volume, out _, out _);

    private static bool Open<T>(ref T p, APTR metadata, bool borrowRoot, out Volume volume, out APTR root, out int rootAt)
        where T : struct, IAmigaFileSystemPlatform
    {
        rootAt = 0; root = metadata;
        if (!SelectRoot(ref p, metadata, false, out volume)) return false;
        if (volume.Root < p.ReservedBlocks || volume.Root >= p.BlockCount - 3) return false;
        var clean = HeaderView(ref p, volume.Root + 2, Clean, metadata, volume.Format);
        if (clean.IsNull || p.ReadUInt32(clean, 12) != 0) return false;
        if (borrowRoot) root = HeaderView(ref p, volume.Root, Objects, metadata, volume.Format);
        else if (!Header(ref p, volume.Root, Objects, metadata, volume.Format)) return false;
        return root.IsNotNull && p.ReadUInt32(root, 12) == 0 &&
            Scan(ref p, volume, root, 1, out rootAt, out _) &&
            (p.ReadUInt8(root, rootAt + 24 + volume.Extra) & 128) != 0;
    }

    /// <summary>Dirty volumes are refused; even a writable provider receives no writes or flushes.</summary>
    public static bool Mount<T>(ref T p, APTR metadata, out AmigaSmartFileSystemFormat format)
        where T : struct, IAmigaFileSystemPlatform
    {
        var ok = Open(ref p, metadata, out var volume); format = volume.Format; return ok;
    }

    private static bool Strings<T>(ref T p, Volume v, APTR buffer, int at, out int next,
        out uint nameLength, out uint commentLength) where T : struct, IAmigaFileSystemPlatform
    {
        var end = (int)p.BlockBytes - (p.ReadUInt32(buffer, 8) == v.Root ? 36 : 0);
        return RecordStrings(ref p, buffer, at, at + 25 + v.Extra, end, out next, out nameLength, out commentLength);
    }

    private static bool RecordStrings<T>(ref T p, APTR buffer, int at, int name, int end,
        out int next, out uint nameLength, out uint commentLength) where T : struct, IAmigaFileSystemPlatform
    {
        next = 0; nameLength = commentLength = 0;
        if (at < 24 || name >= end) return false;
        var pos = name;
        while (pos < end && p.ReadUInt8(buffer, pos) != 0) pos++;
        nameLength = (uint)(pos - name);
        if (pos >= end || nameLength == 0 || nameLength > 107) return false;
        return RecordComment(ref p, buffer, at, pos + 1, end, out next, out commentLength);
    }

    private static bool RecordComment<T>(ref T p, APTR buffer, int at, int comment, int end,
        out int next, out uint commentLength) where T : struct, IAmigaFileSystemPlatform
    {
        next = 0;
        var pos = comment;
        while (pos < end && p.ReadUInt8(buffer, pos) != 0) pos++;
        commentLength = (uint)(pos - comment);
        if (pos >= end || commentLength > 79) return false;
        next = (pos + 2) & ~1;
        return next > at && next <= end;
    }

    private static bool Scan<T>(ref T p, Volume v, APTR buffer, uint node, out int at, out int next)
        where T : struct, IAmigaFileSystemPlatform
    {
        at = 24; next = 0;
        var end = (int)p.BlockBytes - (p.ReadUInt32(buffer, 8) == v.Root ? 36 : 0);
        var nameOffset = 25 + v.Extra;
        while (at + nameOffset + 2 <= end)
        {
            if (!RecordStrings(ref p, buffer, at, at + nameOffset, end, out next, out _, out _)) return false;
            if (p.ReadUInt32(buffer, at + 4) == node) return true;
            at = next;
        }
        return false;
    }

    internal static bool Object<T>(ref T p, Volume v, uint node, APTR buffer, out int at)
        where T : struct, IAmigaFileSystemPlatform => Object(ref p, v, node, buffer, false, out _, out at);

    private static bool Object<T>(ref T p, Volume v, uint node, APTR scratch, bool borrowRecord, out APTR record, out int at)
        where T : struct, IAmigaFileSystemPlatform
    {
        var container = ObjectContainer(ref p, v, node, scratch);
        return ReadObjectRecord(ref p, v, container, node, scratch, borrowRecord, out record, out at);
    }

    private static uint ObjectContainer<T>(ref T p, Volume v, uint node, APTR scratch)
        where T : struct, IAmigaFileSystemPlatform
    {
        if (node == 0) return uint.MaxValue;
        var container = v.Root;
        if (node != 1)
        {
            var index = v.NodeTree; var bytes = p.BlockBytes;
            // Branch pointers reserve one low bit in v4, or log2(block bytes / 32) bits in v3.
            var pointerShift = 1;
            if (v.Format != AmigaSmartFileSystemFormat.Sfs2)
            {
                pointerShift = 0;
                for (var width = bytes; width > 32; width >>= 1) pointerShift++;
            }
            uint precedingStride = uint.MaxValue;
            container = uint.MaxValue;
            for (var depth = 0; depth < 32; depth++)
            {
                var buffer = HeaderView(ref p, index, Nodes, scratch, v.Format);
                if (buffer.IsNull) return uint.MaxValue;
                var first = p.ReadUInt32(buffer, 12); var stride = p.ReadUInt32(buffer, 16);
                if (stride == 0 || stride >= precedingStride || node < first) return uint.MaxValue;
                var delta = node - first;
                if (stride == 1)
                {
                    // Each leaf entry occupies ten bytes following its 20-byte header.
                    if (delta >= (bytes == 512 ? (512u - 20) / 10 : (bytes - 20) / 10)) return uint.MaxValue;
                    container = p.ReadUInt32(buffer, 20 + (int)delta * 10); break;
                }
                var entry = delta / stride;
                if (entry >= (bytes - 20) / 4) return uint.MaxValue;
                var pointer = p.ReadUInt32(buffer, 20 + (int)entry * 4);
                index = pointer >> pointerShift;
                precedingStride = stride;
            }
        }
        return container >= p.ReservedBlocks && container < p.BlockCount - 1 ? container : uint.MaxValue;
    }

    private static bool ReadObjectRecord<T>(ref T p, Volume v, uint container, uint node, APTR scratch,
        bool borrowRecord, out APTR record, out int at) where T : struct, IAmigaFileSystemPlatform
    {
        at = 0; record = scratch;
        if (container == uint.MaxValue) return false;
        if (borrowRecord) record = HeaderView(ref p, container, Objects, scratch, v.Format);
        else if (!Header(ref p, container, Objects, scratch, v.Format)) return false;
        return record.IsNotNull && Scan(ref p, v, record, node, out at, out _);
    }

    private static bool Describe<T>(ref T p, Volume v, uint node, APTR metadata, out ReadEntry entry)
        where T : struct, IAmigaFileSystemPlatform
    {
        entry = default;
        return Object(ref p, v, node, metadata, out var at) && DescribeAt(ref p, v, node, metadata, at, out entry);
    }

    private static bool DescribeAt<T>(ref T p, Volume v, uint node, APTR metadata, int at, out ReadEntry entry)
        where T : struct, IAmigaFileSystemPlatform
    {
        entry = default;
        if (!Strings(ref p, v, metadata, at, out _, out var nameLength, out var commentLength)) return false;
        var flags = p.ReadUInt8(metadata, at + 24 + v.Extra);
        if (!ObjectSize(ref p, v, metadata, at, flags, out var size)) return false;
        entry = new ReadEntry
        {
            Node = node, Parent = p.ReadUInt32(metadata, 12), Flags = flags, Size = size,
            Protection = p.ReadUInt32(metadata, at + 8) ^ 15,
            ModifiedSeconds = p.ReadUInt32(metadata, at + 20 + v.Extra),
            OwnerUid = p.ReadUInt16(metadata, at), OwnerGid = p.ReadUInt16(metadata, at + 2),
            Name = APTR.FromPointer(metadata.Raw + (uint)(at + 25 + v.Extra)), NameLength = nameLength,
            Comment = APTR.FromPointer(metadata.Raw + (uint)(at + 26 + v.Extra) + nameLength), CommentLength = commentLength
        };
        return true;
    }

    public static bool Describe<T>(ref T p, uint node, APTR metadata, out ReadEntry entry)
        where T : struct, IAmigaFileSystemPlatform
    {
        entry = default;
        return Open(ref p, metadata, out var v) && Describe(ref p, v, node, metadata, out entry);
    }

    private static bool ObjectSize<T>(ref T p, Volume v, APTR metadata, int at, byte flags, out ulong size)
        where T : struct, IAmigaFileSystemPlatform
    {
        size = 0;
        if ((flags & 128) != 0) return true;
        size = v.Extra == 0 ? p.ReadUInt32(metadata, at + 16) :
            ((ulong)p.ReadUInt32(metadata, at + 16) << 16) | p.ReadUInt16(metadata, at + 20);
        var blocks = p.BlockCount - p.ReservedBlocks - 1;
        var bytes = p.BlockBytes;
        return size <= (bytes == 512 ? (ulong)blocks << 9 : (ulong)blocks * bytes);
    }

    private static byte Upper(byte c) => c is >= 97 and <= 122 or >= 224 and <= 246 or >= 248 and <= 254 ? (byte)(c - 32) : c;

    private static bool SameComponent<T>(ref T p, APTR path, int start, APTR record, int name, uint bytes, bool caseSensitive)
        where T : struct, IAmigaFileSystemPlatform
    {
        var left = path.Raw + (uint)start;
        var right = record.Raw + (uint)name;
        var stop = left + bytes;
        while (left != stop)
        {
            var a = p.ReadUInt8(APTR.FromPointer(left++));
            var b = p.ReadUInt8(APTR.FromPointer(right++));
            if (b == 0 || (a != b && (caseSensitive || Upper(a) != Upper(b)))) return false;
        }
        return true;
    }

    private static bool MatchRecord<T>(ref T p, APTR path, int start, uint length, APTR image, int at,
        int name, int end, bool caseSensitive, out bool matched, out int next)
        where T : struct, IAmigaFileSystemPlatform
    {
        // For a match, comparison proves both the byte length and absence of an
        // embedded NUL. Nonmatches still need full validation before advancing.
        var terminator = name + (int)length;
        matched = length != 0 && terminator < end && p.ReadUInt8(image, terminator) == 0 &&
            SameComponent(ref p, path, start, image, name, length, caseSensitive);
        return matched ? RecordComment(ref p, image, at, terminator + 1, end, out next, out _) :
            RecordStrings(ref p, image, at, name, end, out next, out _, out _);
    }

    public static uint Resolve<T>(ref T p, uint directory, APTR name, uint length, APTR metadata, out DOS.Error error)
        where T : struct, IAmigaFileSystemPlatform =>
        ResolveRecord(ref p, directory, name, length, metadata, false, default, out _, out error);

    // Consume the checked record before the next cache access. Only scalar
    // fields escape this operation.
    public static uint ResolveInformation<T>(ref T p, uint directory, APTR name, uint length, APTR metadata,
        out ulong size, out bool isDirectory, out uint parent, out DOS.Error error)
        where T : struct, IAmigaFileSystemPlatform =>
        ResolveInformation(ref p, directory, name, length, metadata, default, out size, out isDirectory, out parent, out error);

    /// <summary>Use a still-current MountSfs2ForLookup result, or default for full validation.</summary>
    public static uint ResolveInformation<T>(ref T p, uint directory, APTR name, uint length, APTR metadata,
        LookupRoot lookup, out ulong size, out bool isDirectory, out uint parent, out DOS.Error error)
        where T : struct, IAmigaFileSystemPlatform =>
        ResolveInformation(ref p, directory, name, length, metadata, lookup, out size, out isDirectory, out parent, out _, out error);

    /// <summary>Also return a regular file's first extent key, or zero. Its tree record is validated when read.</summary>
    public static uint ResolveInformation<T>(ref T p, uint directory, APTR name, uint length, APTR metadata,
        LookupRoot lookup, out ulong size, out bool isDirectory, out uint parent, out uint firstExtent, out DOS.Error error)
        where T : struct, IAmigaFileSystemPlatform
    {
        var node = ResolveRecord(ref p, directory, name, length, metadata, true, lookup, out var info, out error);
        size = info.Size; isDirectory = info.Directory; parent = info.Parent; firstExtent = info.FirstExtent;
        return node;
    }

    private struct ObjectInformation
    {
        public ulong Size;
        public bool Directory;
        public uint Parent, FirstExtent;
    }

    private static bool ReadInformation<T>(ref T p, Volume v, APTR record, int at, out ObjectInformation info)
        where T : struct, IAmigaFileSystemPlatform
    {
        info = default;
        var flags = p.ReadUInt8(record, at + 24 + v.Extra);
        if (!ObjectSize(ref p, v, record, at, flags, out var size)) return false;
        info.Size = size; info.Directory = (flags & 128) != 0; info.Parent = p.ReadUInt32(record, 12);
        if ((flags & (128 | 64)) == 0) info.FirstExtent = p.ReadUInt32(record, at + 12);
        return true;
    }

    private static uint ResolveRecord<T>(ref T p, uint directory, APTR name, uint length, APTR scratch, bool borrowRecord, LookupRoot lookup,
        out ObjectInformation info, out DOS.Error error) where T : struct, IAmigaFileSystemPlatform
    {
        Volume v; var record = APTR.Null; var at = 0; info = default; error = DOS.Error.None;
        if (length > 255 || !p.IsMapped(name, length)) { error = DOS.Error.InvalidComponentName; return 0; }
        if (lookup.RootBlock != 0)
        {
            if (!p.IsMapped(scratch, p.BlockBytes)) { error = DOS.Error.DiskNotValidated; return 0; }
            v = new Volume { Format = AmigaSmartFileSystemFormat.Sfs2, Root = lookup.RootBlock,
                NodeTree = lookup.NodeTree, CaseSensitive = lookup.CaseSensitive };
        }
        else if (!Open(ref p, scratch, borrowRecord, out v, out record, out at)) { error = DOS.Error.DiskNotValidated; return 0; }
        var start = 0u;
        for (var i = 0u; i < length; i++) if (p.ReadUInt8(name, (int)i) == ':') { directory = 1; start = i + 1; }
        // Consume the verified root record before looking up another block.
        var haveRecord = directory == 1 && lookup.RootBlock == 0;
        while (start < length)
        {
            uint next;
            if (directory == 1 && lookup.RootBlock != 0)
            {
                // The mount already checked the root's directory flag and zero parent.
                if (p.ReadUInt8(name, (int)start) == '/') { start++; continue; }
                next = lookup.FirstChild;
            }
            else
            {
                if (!haveRecord && !Object(ref p, v, directory, scratch, true, out record, out at)) goto corrupt;
                haveRecord = false;
                var flags = p.ReadUInt8(record, at + 24 + v.Extra);
                if ((flags & 128) == 0)
                {
                    if (!ObjectSize(ref p, v, record, at, flags, out _)) goto corrupt;
                    error = DOS.Error.ObjectWrongType; return 0;
                }
                if (p.ReadUInt8(name, (int)start) == '/')
                {
                    var parent = p.ReadUInt32(record, 12);
                    directory = parent == 0 ? 1 : parent; start++; continue;
                }
                next = p.ReadUInt32(record, at + 16);
            }
            var end = start; while (end < length && p.ReadUInt8(name, (int)end) != '/') end++;
            if (end - start > 107) { error = DOS.Error.InvalidComponentName; return 0; }
            var matched = 0u; uint previous = 0;
            for (var visited = 0u; next != 0 && visited < p.BlockCount; visited++)
            {
                var metadata = HeaderView(ref p, next, Objects, scratch, v.Format);
                if (metadata.IsNull || p.ReadUInt32(metadata, 12) != directory ||
                    p.ReadUInt32(metadata, 20) != previous) goto corrupt;
                var bytes = (int)p.BlockBytes;
                var stringsEnd = bytes - (next == v.Root ? 36 : 0);
                var nameOffset = 25 + v.Extra;
                for (var pos = 24; pos + nameOffset + 2 <= bytes && p.ReadUInt8(metadata, pos + nameOffset) != 0;)
                {
                    var storedName = pos + nameOffset;
                    if (!MatchRecord(ref p, name, (int)start, end - start, metadata, pos, storedName, stringsEnd,
                        v.CaseSensitive, out var equal, out var following)) goto corrupt;
                    if (equal)
                    {
                        matched = p.ReadUInt32(metadata, pos + 4);
                        if (pos == 24 && matched > 1 && borrowRecord && lookup.RootBlock != 0 && end + 1 >= length)
                        {
                            if (ReadIndexedFirstRecord(ref p, v, next, matched, metadata, scratch, out info)) return matched;
                            goto corrupt;
                        }
                        break;
                    }
                    pos = following;
                }
                if (matched != 0) break;
                previous = next; next = p.ReadUInt32(metadata, 16);
            }
            if (matched == 0) { error = next == 0 ? DOS.Error.ObjectNotFound : DOS.Error.DiskNotValidated; return 0; }
            directory = matched; start = end < length ? end + 1 : end;
        }
        if ((haveRecord || Object(ref p, v, directory, scratch, borrowRecord, out record, out at)) &&
            (!borrowRecord || ReadInformation(ref p, v, record, at, out info))) return directory;
    corrupt:
        error = DOS.Error.DiskNotValidated; return 0;
    }

    private static bool ReadIndexedFirstRecord<T>(ref T p, Volume v, uint block, uint node, APTR image,
        APTR scratch, out ObjectInformation info) where T : struct, IAmigaFileSystemPlatform
    {
        // The node-tree read can evict this image. Capture only scalars, and
        // use them when the index confirms this is the same container. Its
        // first record is also the first record a canonical scan would find.
        var valid = ReadInformation(ref p, v, image, 24, out var candidate);
        var container = ObjectContainer(ref p, v, node, scratch);
        if (container == block) { info = candidate; return valid; }
        info = default;
        return ReadObjectRecord(ref p, v, container, node, scratch, true, out var record, out var at) &&
            ReadInformation(ref p, v, record, at, out info);
    }

    internal static bool Extent<T>(ref T p, Volume v, uint key, APTR scratch, out uint count, out uint next,
        out uint previous, bool borrowImage = false)
        where T : struct, IAmigaFileSystemPlatform
    {
        count = next = previous = 0;
        var block = v.ExtentTree;
        for (var depth = 0; depth < 32; depth++)
        {
            var image = scratch;
            if (borrowImage && v.Format == AmigaSmartFileSystemFormat.Sfs2)
            {
                image = HeaderView(ref p, block, Tree, scratch, v.Format);
                if (image.IsNull) return false;
            }
            else if (!Header(ref p, block, Tree, scratch, v.Format)) return false;
            var entries = p.ReadUInt16(image, 12); var leaf = p.ReadUInt8(image, 14) != 0;
            var stride = p.ReadUInt8(image, 15);
            if (entries == 0 || stride != (leaf ? 14 + v.Extra : 8) ||
                (uint)entries * stride > p.BlockBytes - 16) return false;
            var selected = 16;
            var selectedKey = p.ReadUInt32(image, selected);
            if (selectedKey > key) return false;
            var lastKey = selectedKey;
            for (var i = 1; i < entries; i++)
            {
                var at = 16 + i * stride; var candidate = p.ReadUInt32(image, at);
                if (candidate <= lastKey) return false;
                if (candidate <= key) { selected = at; selectedKey = candidate; }
                lastKey = candidate;
            }
            if (!leaf) { block = p.ReadUInt32(image, selected + 4); continue; }
            if (selectedKey != key) return false;
            next = p.ReadUInt32(image, selected + 4); previous = p.ReadUInt32(image, selected + 8);
            count = v.Extra == 0 ? p.ReadUInt16(image, selected + 12) : p.ReadUInt32(image, selected + 12);
            return key >= p.ReservedBlocks && key < p.BlockCount - 1 && count != 0 &&
                count <= p.BlockCount - 1 - key &&
                (next == 0 || next >= p.ReservedBlocks && next < p.BlockCount - 1);
        }
        return false;
    }

    /// <summary>64-bit byte position; each transfer remains bounded by the mapped buffer and Int32.MaxValue.</summary>
    public static int Read<T>(ref T p, uint node, ulong position, APTR destination, uint length,
        APTR metadata, APTR data, out DOS.Error error) where T : struct, IAmigaFileSystemPlatform
    {
        var cursor = new ReadCursor();
        return Read(ref p, ref cursor, node, position, destination, length, metadata, data, out error);
    }

    /// <summary>Caller-owned traversal state. Clear Node after mutation,
    /// recovery or media change. Shared scratch need not survive between calls.</summary>
    public struct ReadCursor
    {
        public uint Node;
        public AmigaSmartFileSystemFormat Format;
        public ulong Size;
        public uint First, Tree, Extent, Start, Count, Next, Previous;
    }

    /// <summary>Scalar fields from a verified regular-file record and its clean SFS2 root.
    /// Supply only while both records remain current. Extents are checked by Read.</summary>
    public readonly struct ReadStart(uint node, ulong size, uint firstExtent, uint extentTree)
    {
        public readonly uint Node = node, FirstExtent = firstExtent, ExtentTree = extentTree;
        public readonly ulong Size = size;
    }

    public static int Read<T>(ref T p, ref ReadCursor cursor, uint node, ulong position,
        APTR destination, uint length, APTR metadata, APTR data, out DOS.Error error)
        where T : struct, IAmigaFileSystemPlatform =>
        Read(ref p, ref cursor, node, position, destination, length, metadata, data, default, out error);

    public static int Read<T>(ref T p, ref ReadCursor cursor, uint node, ulong position,
        APTR destination, uint length, APTR metadata, APTR data, ReadStart start, out DOS.Error error)
        where T : struct, IAmigaFileSystemPlatform
    {
        var bytes = p.BlockBytes;
        if (ReadOverlaps(destination, length, metadata, bytes) || ReadOverlaps(destination, length, data, bytes))
        {
            cursor.Node = 0;
            return ReadSingleBlocks(ref p, node, position, destination, length, metadata, data, out error);
        }
        error = DOS.Error.None;
        var copied = 0u;
        if (length > int.MaxValue || !p.IsMapped(destination, length) || !p.IsMapped(data, bytes)) goto failed;
        if (cursor.Node != node || node == 0)
        {
            cursor.Node = 0;
            if (start.FirstExtent != 0 && start.Node == node && node != 0)
            {
                cursor.Format = AmigaSmartFileSystemFormat.Sfs2;
                cursor.Size = start.Size; cursor.First = start.FirstExtent; cursor.Tree = start.ExtentTree;
            }
            else
            {
                if (!Open(ref p, metadata, true, out var volume, out _, out _) ||
                    !Object(ref p, volume, node, metadata, true, out var record, out var at)) goto failed;
                // Scan has checked the strings. Consume the record before the next I/O.
                var flags = p.ReadUInt8(record, at + 24 + volume.Extra);
                if (!ObjectSize(ref p, volume, record, at, flags, out var size)) goto failed;
                if ((flags & (128 | 64)) != 0) { error = DOS.Error.ObjectWrongType; return -1; }
                cursor.Format = volume.Format;
                cursor.Size = size; cursor.First = p.ReadUInt32(record, at + 12); cursor.Tree = volume.ExtentTree;
            }
            cursor.Extent = cursor.First; cursor.Start = 0; cursor.Count = 0; cursor.Next = 0;
            cursor.Previous = node | 0x80000000; cursor.Node = node;
        }
        if (position > cursor.Size) { error = DOS.Error.SeekError; cursor.Node = 0; return -1; }
        if (length > cursor.Size - position) length = (uint)(cursor.Size - position);
        var v = new Volume { Format = cursor.Format, ExtentTree = cursor.Tree };
        var fileBlock = bytes == 512 ? position >> 9 : position / bytes;
        if (fileBlock < cursor.Start)
        {
            cursor.Extent = cursor.First; cursor.Start = 0; cursor.Count = 0; cursor.Next = 0;
            cursor.Previous = node | 0x80000000;
        }
        while (copied < length)
        {
            while (cursor.Count == 0 || fileBlock >= (ulong)cursor.Start + cursor.Count)
            {
                if (cursor.Count != 0)
                {
                    cursor.Start += cursor.Count; cursor.Previous = cursor.Extent; cursor.Extent = cursor.Next;
                }
                if (!Extent(ref p, v, cursor.Extent, metadata, out var count, out var next, out var back, borrowImage: true) ||
                    (back != cursor.Previous && !(v.Extra == 0 && cursor.Start == 0 && back == 0)) ||
                    (ulong)cursor.Start + count > p.BlockCount) goto failed;
                cursor.Count = count; cursor.Next = next;
            }
            var physical = (ulong)cursor.Extent + fileBlock - cursor.Start;
            if (physical >= p.BlockCount - 1) goto failed;
            var within = bytes == 512 ? (uint)position & 511 : (uint)(position % bytes);
            var target = APTR.FromPointer(destination.Raw + copied);
            var remaining = length - copied;
            if (within == 0 && remaining >= bytes && (target.Raw & 3) == 0)
            {
                var blocks = bytes == 512 ? remaining >> 9 : remaining / bytes;
                var available = cursor.Count - (uint)(fileBlock - cursor.Start);
                if (blocks > available) blocks = available;
                var read = bytes == 512 && blocks > 8 && v.Format == AmigaSmartFileSystemFormat.Sfs2
                    ? p.ReadDataBlocks((uint)physical, blocks, target, false)
                    : p.ReadDataBlocks((uint)physical, blocks, target);
                if (read > blocks) goto failed;
                var taken = read * bytes;
                copied += taken; position += taken; fileBlock += read;
                if (read != blocks) goto failed;
            }
            else
            {
                var take = bytes - within;
                if (take > remaining) take = remaining;
                var image = p.ReadRetainedBlock((uint)physical);
                if (image.IsNull || ReadOverlaps(target, take, image, bytes))
                {
                    if (!p.ReadBlock((uint)physical, data)) goto failed;
                    image = data;
                }
                p.Copy(APTR.FromPointer(image.Raw + within), target, take);
                copied += take; position += take; fileBlock++;
            }
        }
        return (int)copied;
    failed:
        cursor.Node = 0;
        error = DOS.Error.DiskNotValidated;
        return copied == 0 ? -1 : (int)copied;
    }

    private static bool ReadOverlaps(APTR a, uint length, APTR b, uint bytes) =>
        length != 0 && bytes != 0 && (a.Raw <= b.Raw ? b.Raw - a.Raw < length : a.Raw - b.Raw < bytes);

    private static int ReadSingleBlocks<T>(ref T p, uint node, ulong position, APTR destination, uint length,
        APTR metadata, APTR data, out DOS.Error error) where T : struct, IAmigaFileSystemPlatform
    {
        error = DOS.Error.None;
        if (length > int.MaxValue || !p.IsMapped(destination, length) || !p.IsMapped(data, p.BlockBytes) ||
            !Open(ref p, metadata, out var v) || !Describe(ref p, v, node, metadata, out var entry))
        { error = DOS.Error.DiskNotValidated; return -1; }
        if (entry.Directory || entry.SoftLink) { error = DOS.Error.ObjectWrongType; return -1; }
        if (position > entry.Size) { error = DOS.Error.SeekError; return -1; }
        if (length > entry.Size - position) length = (uint)(entry.Size - position);
        if (length == 0) return 0;
        if (!Object(ref p, v, node, metadata, out var at)) { error = DOS.Error.DiskNotValidated; return -1; }
        var extent = p.ReadUInt32(metadata, at + 12); var bytes = p.BlockBytes;
        ulong extentStart = 0; uint extentSize = 0, next = 0, copied = 0, previous = node | 0x80000000;
        while (copied < length)
        {
            var fileBlock = position / bytes;
            while (extentSize == 0 || fileBlock >= extentStart + extentSize)
            {
                if (extentSize != 0) { extentStart += extentSize; previous = extent; extent = next; }
                if (!Extent(ref p, v, extent, metadata, out extentSize, out next, out var back) ||
                    (back != previous && !(v.Extra == 0 && extentStart == 0 && back == 0)) ||
                    extentStart + extentSize > p.BlockCount) goto failed;
            }
            var physical = (ulong)extent + fileBlock - extentStart;
            if (physical >= p.BlockCount - 1 || !p.ReadBlock((uint)physical, data)) goto failed;
            var within = (uint)(position % bytes); var take = bytes - within;
            if (take > length - copied) take = length - copied;
            for (var i = 0u; i < take; i++) p.WriteUInt8(destination, (int)(copied + i), p.ReadUInt8(data, (int)(within + i)));
            copied += take; position += take;
        }
        return (int)copied;
    failed:
        error = DOS.Error.DiskNotValidated; return copied == 0 ? -1 : (int)copied;
    }

    /// <summary>Initialize cursor to the directory node. Hidden entries are skipped. ReadEntry strings use metadata.</summary>
    public static DOS.Error ExamineNext<T>(ref T p, uint directory, ref uint cursor, APTR metadata, out ReadEntry entry)
        where T : struct, IAmigaFileSystemPlatform
    {
        entry = default;
        if (!Open(ref p, metadata, out var v)) return DOS.Error.DiskNotValidated;
        if (!Describe(ref p, v, directory, metadata, out var parent)) return DOS.Error.InvalidLock;
        if (!parent.Directory) return DOS.Error.ObjectWrongType;
        if (!Object(ref p, v, directory, metadata, out var at)) return DOS.Error.DiskNotValidated;
        var block = p.ReadUInt32(metadata, at + 16); uint previous = 0;
        var seen = cursor == directory;
        for (var visited = 0u; block != 0 && visited < p.BlockCount; visited++)
        {
            if (!Header(ref p, block, Objects, metadata, v.Format) || p.ReadUInt32(metadata, 12) != directory ||
                p.ReadUInt32(metadata, 20) != previous) return DOS.Error.DiskNotValidated;
            for (var pos = 24; pos + 27 + v.Extra <= p.BlockBytes && p.ReadUInt8(metadata, pos + 25 + v.Extra) != 0;)
            {
                if (!Strings(ref p, v, metadata, pos, out var following, out _, out _)) return DOS.Error.DiskNotValidated;
                var node = p.ReadUInt32(metadata, pos + 4); var flags = p.ReadUInt8(metadata, pos + 24 + v.Extra);
                if (seen && (flags & 1) == 0)
                {
                    if (!Describe(ref p, v, node, metadata, out entry) || entry.Parent != directory) return DOS.Error.DiskNotValidated;
                    cursor = node; return DOS.Error.None;
                }
                if (node == cursor) seen = true;
                pos = following;
            }
            previous = block; block = p.ReadUInt32(metadata, 16);
        }
        return block == 0 && seen ? DOS.Error.NoMoreEntries : DOS.Error.DiskNotValidated;
    }

    public static bool CountUsed<T>(ref T p, APTR metadata, out uint used)
        where T : struct, IAmigaFileSystemPlatform
    {
        used = 0;
        if (!Open(ref p, metadata, out _)) return false;
        var free = p.ReadUInt32(metadata, (int)p.BlockBytes - 28);
        var available = p.BlockCount - p.ReservedBlocks - 1;
        if (free > available) return false;
        used = available - free; return true;
    }
}
