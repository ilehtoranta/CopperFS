using Amiga;

namespace CopperStart.Dos;

/// <summary>Read-only MorphOS link-target packets over classic OFS/FFS media.</summary>
public static class AmigaFfsReadLinkCore
{
    // Link layouts: Linux fs/affs/amigaffs.h affs_tail and slink_front.
    // Keep this reader separate from the classic handler's admitted header types.
    private static bool Header<T>(ref T p, in AmigaFfsGeometry g, uint key, APTR block) where T : struct, IAmigaFileSystemPlatform
        => AmigaDiskFileSystemCore.Header(ref p, in g, key, block, true);

    private static byte Upper(byte c, uint dosType) => c is >= 97 and <= 122 ||
        (dosType >= 0x444F5302 && c >= 224 && c != 247) ? unchecked((byte)(c - 32)) : c;

    public static int Read<T>(ref T p, uint directory, APTR path, APTR output, int capacity,
        uint dosType, APTR metadata, APTR data, out DOS.Error error) where T : struct, IAmigaFileSystemPlatform
    {
        // Mount geometry is immutable during a serialized packet. Reuse this
        // snapshot through the parent and target walks instead of repeatedly
        // copying the partition geometry in each helper.
        var geometry = AmigaFfsGeometry.Read(ref p);
        error = DOS.Error.None;
        uint length = 0, offset = 0;
        while (true)
        {
            if (path.IsNull || length > int.MaxValue || path.Raw > uint.MaxValue - length ||
                !p.IsMapped(APTR.FromPointer(path.Raw + length), 1))
            { error = DOS.Error.BadNumber; return -1; }
            var c = p.ReadUInt8(path, (int)length);
            if (c == 0) break;
            if (c == ':') { directory = geometry.RootBlock; offset = length + 1; }
            length++;
        }
        while (offset < length)
        {
            if (!Header(ref p, in geometry, directory, metadata)) { error = DOS.Error.DiskNotValidated; return -1; }
            if (unchecked((int)p.ReadUInt32(metadata, geometry.Tail + 508)) is not (1 or 2))
            { error = DOS.Error.ObjectWrongType; return -1; }
            if (p.ReadUInt8(path, (int)offset) == '/')
            {
                directory = directory == geometry.RootBlock ? directory : p.ReadUInt32(metadata, geometry.Tail + 500);
                offset++; continue;
            }
            var start = offset;
            while (offset < length && p.ReadUInt8(path, (int)offset) != '/') offset++;
            var count = offset - start;
            if (count > 30) { error = DOS.Error.InvalidComponentName; return -1; }
            uint hash = count;
            for (uint i = 0; i < count; i++) hash = (hash * 13 + Upper(p.ReadUInt8(path, (int)(start + i)), dosType)) & 0x7FF;
            var key = p.ReadUInt32(metadata, 24 + (int)AmigaGeometryMath.Remainder(hash, geometry.TableEntries) * 4);
            var matched = false;
            for (uint visited = 0; key != 0 && visited < geometry.BlockCount; visited++)
            {
                if (!Header(ref p, in geometry, key, metadata) || p.ReadUInt32(metadata, geometry.Tail + 500) != directory)
                { error = DOS.Error.DiskNotValidated; return -1; }
                matched = p.ReadUInt8(metadata, geometry.Tail + 432) == count;
                for (uint i = 0; matched && i < count; i++) matched =
                    Upper(p.ReadUInt8(metadata, geometry.Tail + 433 + (int)i), dosType) == Upper(p.ReadUInt8(path, (int)(start + i)), dosType);
                if (matched) break;
                key = p.ReadUInt32(metadata, geometry.Tail + 496);
            }
            if (!matched) { error = key == 0 ? DOS.Error.ObjectNotFound : DOS.Error.DiskNotValidated; return -1; }
            var type = unchecked((int)p.ReadUInt32(metadata, geometry.Tail + 508));
            if (type is 3 or 4 or -4)
                return Target(ref p, in geometry, key, directory, path, offset, length - offset, output, capacity, metadata, data, out error);
            directory = key;
            if (offset < length) offset++;
        }
        error = DOS.Error.ObjectWrongType;
        return -1;
    }

    // Measure and emit a canonical volume-qualified path without a stack-sized
    // path buffer. Parent walks are bounded by actual disk blocks, not guest text.
    private static bool Path<T>(ref T p, in AmigaFfsGeometry geometry, uint node, APTR block, APTR output, out uint length)
        where T : struct, IAmigaFileSystemPlatform
    {
        length = 0;
        var key = node;
        for (uint visited = 0; visited < geometry.BlockCount; visited++)
        {
            if (!Header(ref p, in geometry, key, block)) return false;
            var size = p.ReadUInt8(block, geometry.Tail + 432);
            if (size == 0 || size > 30 || length > int.MaxValue - size - 1) return false;
            length += (uint)size + 1;
            if (key == geometry.RootBlock) break;
            key = p.ReadUInt32(block, geometry.Tail + 500);
        }
        if (key != geometry.RootBlock) return false;
        // No slash after the final component; the root itself retains its colon.
        if (node != geometry.RootBlock) length--;
        if (output.IsNull) return true;
        var position = length;
        key = node;
        while (true)
        {
            if (!Header(ref p, in geometry, key, block)) return false;
            var size = p.ReadUInt8(block, geometry.Tail + 432);
            if (key == geometry.RootBlock) p.WriteUInt8(output, (int)--position, (byte)':');
            else if (key != node) p.WriteUInt8(output, (int)--position, (byte)'/');
            position -= size;
            for (var i = 0; i < size; i++) p.WriteUInt8(output, (int)position + i, p.ReadUInt8(block, geometry.Tail + 433 + i));
            if (key == geometry.RootBlock) return position == 0;
            key = p.ReadUInt32(block, geometry.Tail + 500);
        }
    }

    private static int Target<T>(ref T p, in AmigaFfsGeometry geometry, uint key, uint parent, APTR suffix, uint suffixOffset, uint suffixLength,
        APTR output, int capacity, APTR metadata, APTR data, out DOS.Error error) where T : struct, IAmigaFileSystemPlatform
    {
        error = DOS.Error.None;
        if (!Header(ref p, in geometry, key, data)) { error = DOS.Error.DiskNotValidated; return -1; }
        var soft = p.ReadUInt32(data, geometry.Tail + 508) == 3;
        uint prefix = 0, targetLength = 0;
        var relative = true;
        var volumeRelative = soft && p.ReadUInt8(data, 24) == ':';
        if (soft)
        {
            var end = geometry.BlockBytes - 200;
            while (24 + targetLength < end && p.ReadUInt8(data, 24 + (int)targetLength) != 0)
            {
                if (p.ReadUInt8(data, 24 + (int)targetLength) == ':') relative = false;
                targetLength++;
            }
            if (24 + targetLength == end) { error = DOS.Error.DiskNotValidated; return -1; }
            if (relative || volumeRelative)
            {
                if (!relative) parent = geometry.RootBlock;
                if (!Path(ref p, in geometry, parent, metadata, APTR.Null, out prefix)) { error = DOS.Error.DiskNotValidated; return -1; }
                if (relative)
                {
                    if (parent != geometry.RootBlock && targetLength != 0) prefix++;
                }
                else prefix--; // the stored target already supplies the colon
            }
        }
        else
        {
            for (uint visited = 0; visited < geometry.BlockCount; visited++)
            {
                key = p.ReadUInt32(data, geometry.Tail + 468); // original, longword -11
                if (!Header(ref p, in geometry, key, data)) { error = DOS.Error.DiskNotValidated; return -1; }
                var type = unchecked((int)p.ReadUInt32(data, geometry.Tail + 508));
                if (type is not (4 or -4)) break;
            }
            var targetType = unchecked((int)p.ReadUInt32(data, geometry.Tail + 508));
            if (targetType is not (1 or 2 or -3) || !Path(ref p, in geometry, key, metadata, APTR.Null, out targetLength))
            { error = DOS.Error.DiskNotValidated; return -1; }
        }
        // The separator after the link's input component is a join boundary.
        // Preserve additional slashes (Amiga parent components), not a duplicate
        // boundary slash after an existing target separator or volume colon.
        var last = soft && targetLength != 0 ? p.ReadUInt8(data, 23 + (int)targetLength) :
            (soft ? parent : key) == geometry.RootBlock ? (byte)':' : (byte)0;
        if (suffixLength != 0 && last is (byte)'/' or (byte)':') { suffixOffset++; suffixLength--; }
        var total = (ulong)prefix + targetLength + suffixLength;
        if (total >= int.MaxValue) { error = DOS.Error.LineTooLong; return -2; }
        var required = (uint)total + 1;
        if (output.IsNull) return (int)required;
        if (capacity < 0 || (uint)capacity < required) { error = DOS.Error.LineTooLong; return -2; }
        if (!p.IsMapped(output, required)) { error = DOS.Error.BadNumber; return -1; }
        // Move the suffix first so a caller may reuse its input path buffer.
        var suffixDestination = prefix + targetLength;
        if (output.Raw + suffixDestination > suffix.Raw + suffixOffset)
        {
            for (var i = suffixLength; i != 0; i--)
                p.WriteUInt8(output, (int)(suffixDestination + i - 1), p.ReadUInt8(suffix, (int)(suffixOffset + i - 1)));
        }
        else
        {
            for (uint i = 0; i < suffixLength; i++)
                p.WriteUInt8(output, (int)(suffixDestination + i), p.ReadUInt8(suffix, (int)(suffixOffset + i)));
        }
        if (soft)
        {
            if (relative || volumeRelative)
            {
                if (!Path(ref p, in geometry, parent, metadata, output, out _)) { error = DOS.Error.DiskNotValidated; return -1; }
                if (relative && parent != geometry.RootBlock && targetLength != 0)
                    p.WriteUInt8(output, (int)prefix - 1, (byte)'/');
            }
            for (uint i = 0; i < targetLength; i++) p.WriteUInt8(output, (int)(prefix + i), p.ReadUInt8(data, 24 + (int)i));
        }
        else if (!Path(ref p, in geometry, key, metadata, output, out _)) { error = DOS.Error.DiskNotValidated; return -1; }
        p.WriteUInt8(output, (int)total, 0);
        return (int)total;
    }
}
