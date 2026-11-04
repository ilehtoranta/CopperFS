using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CopperSFS.Check;

public sealed record ImageEntry(string Path, uint Node, bool Directory, ulong Size,
    uint Protection, uint Modified, ushort OwnerUid, ushort OwnerGid, byte Flags,
    string Comment, string? Sha256);

public sealed record ImageReport(uint Blocks, int BlockBytes, uint FreeBlocks, IReadOnlyList<ImageEntry> Entries);

/// <summary>
/// Read-only, separately implemented checker for clean SFS v3 and SFS2 v4 partition images.
/// No CopperSFS traversal, checksum, allocation or recovery routines are used here.
/// Unsupported features are rejected, never reported as checked.
/// </summary>
public sealed class SfsImageChecker
{
    private readonly Stream input;
    private readonly int bytes;
    private readonly uint blocks, reserved;
    private readonly bool sfs2;
    private readonly Dictionary<uint, string> owners = new();
    private readonly HashSet<uint> metadata = new(), adminAllocated = new(), adminReserved = new();
    private readonly Dictionary<uint, Node> nodes = new();
    private readonly Dictionary<uint, Extent> extents = new();
    private readonly HashSet<uint> objects = new(), usedExtents = new();
    private readonly List<ImageEntry> entries = new();
    private readonly List<(uint Block, uint Parent, List<(uint Node, string Name)> Children)> hashes = new();
    private bool caseSensitive;
    private uint rootContainer;
    private sealed record Node(uint Container, uint Next, ushort Hash);
    private sealed record Extent(uint Next, uint Previous, uint Count);
    private sealed record Obj(uint Node, uint Container, uint Value, ulong Size, uint Protection,
        uint Modified, ushort Uid, ushort Gid, byte Flags, string Name, string Comment);

    private SfsImageChecker(Stream input, int bytes, uint reserved)
    {
        Require(input.CanRead && input.CanSeek, "A readable, seekable partition image is required.");
        Require(bytes >= 512 && bytes <= 65536 && (bytes & (bytes - 1)) == 0, "Invalid block size.");
        Require(input.Length % bytes == 0 && input.Length / bytes is >= 4 and <= uint.MaxValue,
            "Image length must be a whole number of blocks, between 4 and 2^32-1 blocks.");
        this.input = input; this.bytes = bytes; this.reserved = reserved;
        blocks = (uint)(input.Length / bytes);
        Require(reserved > 0 && reserved < blocks - 1, "Invalid reserved block count.");
        var magic = U32(Read(0), 0);
        Require(magic is 0x53465300 or 0x53465302, "Unsupported SFS identifier.");
        sfs2 = magic == 0x53465302;
    }

    public static ImageReport Check(Stream input, int blockBytes = 512, uint reservedBlocks = 1) =>
        new SfsImageChecker(input, blockBytes, reservedBlocks).Run();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
    private static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
    private static uint Id(string value) => BinaryPrimitives.ReadUInt32BigEndian(Encoding.Latin1.GetBytes(value));
    private byte[] Read(uint number)
    {
        Require(number < blocks, $"Block {number}: outside partition.");
        var data = new byte[bytes];
        input.Position = (long)number * bytes;
        input.ReadExactly(data);
        return data;
    }
    private byte[] Header(uint number, string type)
    {
        var data = Read(number);
        uint sum = 0;
        for (var offset = 0; offset < bytes; offset += 4) sum = unchecked(sum + U32(data, offset));
        Require(U32(data, 0) == Id(type) && U32(data, 8) == number && sum == (sfs2 ? 0xFFFFFFFE : uint.MaxValue),
            $"Block {number}: invalid {type} header, self pointer or checksum.");
        return data;
    }
    private void Claim(uint number, string owner)
    {
        Require(number < blocks, $"Block {number}: {owner} outside partition.");
        Require(owners.TryAdd(number, owner), $"Block {number}: overlapping ownership ({owner}).");
    }
    private byte[] Meta(uint number, string type)
    {
        Claim(number, type);
        metadata.Add(number);
        return Header(number, type);
    }
    private byte[] Root(uint number)
    {
        var data = Header(number, sfs2 ? "SFS\u0002" : "SFS\0");
        Require(U16(data, 12) == (sfs2 ? 4 : 3) && U32(data, 48) == blocks && U32(data, 52) == bytes,
            $"Block {number}: root geometry/version mismatch.");
        return data;
    }
    private ImageReport Run()
    {
        // Both copies must validate for qualification. A degraded root is an error here.
        var first = Root(0); var last = Root(blocks - 1);
        var root = unchecked((short)(U16(last, 14) - U16(first, 14))) > 0 ? last : first;
        caseSensitive = (root[20] & 128) != 0;
        for (uint i = 0; i < reserved; i++) Claim(i, "reserved");
        Claim(blocks - 1, "secondary root");
        rootContainer = U32(root, 104);
        Require(rootContainer < blocks - 2, "Invalid root object container.");
        var marker = Meta(rootContainer + 2, "TROK");
        Require(U32(marker, 12) == 0, "Clean transaction marker has a journal pointer.");
        ReadAdmin(U32(root, 100));
        ReadNodes(U32(root, 112), 0, null, null);
        ReadExtents(U32(root, 108), 0, 0, (ulong)uint.MaxValue + 1);
        var rootData = Meta(rootContainer, "OBJC");
        Require(U32(rootData, 12) == 0 && U32(rootData, 16) == 0 && U32(rootData, 20) == 0,
            "Root object container has unexpected links.");
        Require(U32(rootData, bytes - 36) == 0 && U32(rootData, bytes - 32) == 0,
            "Recycled/deleted-file accounting is not supported by this checker.");
        var rootObjects = ParseObjects(rootData, rootContainer, bytes - 36);
        Require(rootObjects.Count == 1 && rootObjects[0].Node == 1 && (rootObjects[0].Flags & 128) != 0,
            "Expected exactly one root directory object.");
        Walk(rootObjects[0], "/", 0);
        Require(objects.SetEquals(nodes.Keys), "Object node table contains orphan or missing objects.");
        Require(usedExtents.SetEquals(extents.Keys), "Extent tree contains orphan extents.");
        CheckHashes();

        var bitsPerMap = (uint)(bytes - 12) * 8;
        var bitmapCount = ((ulong)blocks + bitsPerMap - 1) / bitsPerMap;
        var maps = new List<byte[]>();
        var bitmapBlocks = new HashSet<uint>();
        for (ulong i = 0; i < bitmapCount; i++)
        {
            var number = (ulong)U32(root, 96) + i;
            Require(number < blocks, "Bitmap extends outside partition.");
            maps.Add(Meta((uint)number, "BTMP"));
            bitmapBlocks.Add((uint)number);
        }
        // Admin ranges reserve all their blocks globally, but only set admin bits may hold metadata.
        // Native formatters place the fixed bitmap region outside admin space;
        // synthetic/older CopperSFS volumes can place it inside an admin range.
        var adminMetadata = metadata.Where(n => !bitmapBlocks.Contains(n) || adminReserved.Contains(n)).ToHashSet();
        foreach (var number in adminMetadata)
            Require(adminAllocated.Contains(number), $"Block {number}: live metadata not allocated in admin map.");
        Require(adminAllocated.SetEquals(adminMetadata), "Admin map contains unreachable allocated metadata.");
        foreach (var number in adminReserved)
            if (!adminAllocated.Contains(number)) Claim(number, "unused admin slot");

        uint free = 0;
        for (uint number = 0; number < blocks; number++)
        {
            var index = number % bitsPerMap;
            var isFree = (maps[(int)(number / bitsPerMap)][12 + index / 8] & (0x80 >> (int)(index % 8))) != 0;
            Require(isFree != owners.ContainsKey(number), $"Block {number}: bitmap disagrees with ownership.");
            if (isFree) free++;
        }
        Require(U32(rootData, bytes - 28) == free, "Cached free-block count disagrees with bitmap.");
        return new(blocks, bytes, free, entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToArray());
    }

    private void ReadAdmin(uint number)
    {
        uint previous = 0;
        while (number != 0)
        {
            var data = Meta(number, "ADMC");
            Require(U32(data, 16) == previous, $"Block {number}: broken admin previous link.");
            // Reference SFS writes 32; earlier CopperSFS builds used the header
            // comment's log2(byte-count) interpretation, 2, for the same layout.
            Require(data[20] is 32 or 2, "Only 32-block admin maps are supported.");
            for (int offset = 24; offset + 8 <= bytes; offset += 8)
            {
                var start = U32(data, offset); var used = U32(data, offset + 4);
                if (start == 0) { Require(used == 0, "Admin bits without a range."); continue; }
                Require(start >= reserved && (ulong)start + 32 <= blocks - 1, "Admin range outside writable partition.");
                for (uint bit = 0; bit < 32; bit++)
                {
                    Require(adminReserved.Add(start + bit), "Overlapping admin ranges.");
                    if ((used & (0x80000000u >> (int)bit)) != 0) adminAllocated.Add(start + bit);
                }
            }
            previous = number; number = U32(data, 12);
        }
    }
    private bool ReadNodes(uint number, int depth, uint? expectedFirst, uint? expectedStride)
    {
        Require(depth < 32, "Object node tree is too deep.");
        var data = Meta(number, "NDC ");
        var first = U32(data, 12); var stride = U32(data, 16);
        Require(stride > 0 && (!expectedFirst.HasValue || first == expectedFirst) &&
            (!expectedStride.HasValue || stride == expectedStride), "Invalid object node range.");
        if (stride == 1)
        {
            var full = true;
            for (int offset = 20; offset + 10 <= bytes; offset += 10)
            {
                var container = U32(data, offset);
                if (container == 0) { full = false; continue; }
                // Original SFS reserves nodes 3..6 with sentinel pointers.
                if (container == uint.MaxValue)
                {
                    var reservedNode = (ulong)first + (uint)((offset - 20) / 10);
                    Require(reservedNode is >= 3 and <= 6 && U32(data, offset + 4) == 0 && U16(data, offset + 8) == 0,
                        "Invalid reserved object-node sentinel.");
                    continue;
                }
                var node = (ulong)first + (uint)((offset - 20) / 10);
                Require(node is > 0 and <= uint.MaxValue && nodes.TryAdd((uint)node,
                    new(container, U32(data, offset + 4), U16(data, offset + 8))), "Duplicate/invalid object node number.");
            }
            return full;
        }
        var capacity = (uint)(bytes - 20) / 4;
        var leafCapacity = (uint)(bytes - 20) / 10;
        Require(stride == leafCapacity || stride % capacity == 0, "Invalid node index stride.");
        var shift = sfs2 ? 1 : System.Numerics.BitOperations.Log2((uint)bytes) - 5;
        var allFull = true;
        for (uint slot = 0; slot < capacity; slot++)
        {
            var pointer = U32(data, 20 + (int)slot * 4);
            if (pointer == 0) { allFull = false; continue; }
            var childFirst = (ulong)first + slot * (ulong)stride;
            Require(childFirst <= uint.MaxValue, "Object node range overflow.");
            var childFull = ReadNodes(pointer >> shift, depth + 1, (uint)childFirst, stride == leafCapacity ? 1 : stride / capacity);
            Require(((pointer & 1) != 0) == childFull, $"Block {number}: incorrect node fullness flag.");
            allFull &= childFull;
        }
        return allFull;
    }
    private void ReadExtents(uint number, int depth, ulong lower, ulong upper)
    {
        Require(depth < 32, "Extent tree is too deep.");
        var data = Meta(number, "BNDC");
        var count = U16(data, 12); var leaf = data[14] == 1; var stride = data[15];
        Require(data[14] <= 1 && stride >= (leaf ? (sfs2 ? 16 : 14) : 8) && stride % 2 == 0 &&
            16 + count * stride <= bytes && (leaf || count > 0), "Invalid extent tree layout.");
        ulong previous = 0;
        for (var i = 0; i < count; i++)
        {
            var offset = 16 + i * stride; var key = U32(data, offset);
            Require(key >= lower && key < upper && (i == 0 || key > previous), "Unsorted/out-of-range extent tree keys.");
            previous = key;
            if (leaf)
            {
                uint length = sfs2 ? U32(data, offset + 12) : U16(data, offset + 12);
                Require(key >= reserved && length > 0 && (ulong)key + length <= blocks - 1 &&
                    extents.TryAdd(key, new(U32(data, offset + 4), U32(data, offset + 8), length)), "Invalid/duplicate extent.");
            }
            else ReadExtents(U32(data, offset + 4), depth + 1, key,
                i + 1 < count ? U32(data, offset + stride) : upper);
        }
    }
    private List<Obj> ParseObjects(byte[] data, uint container, int limit)
    {
        var result = new List<Obj>();
        var extra = sfs2 ? 2 : 0;
        for (var offset = 24; offset + 27 + extra <= limit;)
        {
            var node = U32(data, offset + 4);
            if (node == 0) break;
            var nameAt = offset + 25 + extra;
            var endName = Array.IndexOf(data, (byte)0, nameAt, limit - nameAt);
            Require(endName > nameAt && endName - nameAt <= (sfs2 ? 107 : 100), "Invalid object name.");
            var endComment = Array.IndexOf(data, (byte)0, endName + 1, limit - endName - 1);
            Require(endComment >= 0 && endComment - endName - 1 <= 79, "Invalid object comment.");
            var name = Encoding.Latin1.GetString(data, nameAt, endName - nameAt);
            Require(!name.Any(c => c < 32 || c is ':' or '/'), "Invalid name character.");
            var flags = data[offset + 24 + extra];
            Require((flags & (8 | 32 | 64)) == 0, "Ring lists and links are not supported by this checker.");
            var size = sfs2 && (flags & 128) == 0 ? ((ulong)U32(data, offset + 16) << 16) | U16(data, offset + 20) : U32(data, offset + 16);
            result.Add(new(node, container, U32(data, offset + 12), size,
                U32(data, offset + 8), U32(data, offset + 20 + extra), U16(data, offset), U16(data, offset + 2),
                flags, name, Encoding.Latin1.GetString(data, endName + 1, endComment - endName - 1)));
            offset = (endComment + 2) & ~1;
        }
        return result;
    }
    private void Walk(Obj obj, string path, int depth)
    {
        Require(depth < 256, "Directory nesting exceeds checker limit.");
        Require(objects.Add(obj.Node) && nodes.TryGetValue(obj.Node, out var node) && node.Container == obj.Container,
            $"{path}: duplicate object or incorrect node mapping.");
        var directory = (obj.Flags & 128) != 0;
        string? digest = null;
        if (directory)
        {
            var children = new List<Obj>();
            Require(obj.Size <= uint.MaxValue, "Directory pointer exceeds block address width.");
            var number = (uint)obj.Size; uint previous = 0;
            while (number != 0)
            {
                var data = Meta(number, "OBJC");
                Require(U32(data, 12) == obj.Node && U32(data, 20) == previous, $"{path}: broken directory links.");
                children.AddRange(ParseObjects(data, number, bytes));
                previous = number; number = U32(data, 16);
            }
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var child in children)
            {
                Require(names.Add(Fold(child.Name)), $"{path}: duplicate directory name.");
                Walk(child, path == "/" ? "/" + child.Name : path + "/" + child.Name, depth + 1);
            }
            if (obj.Value != 0) hashes.Add((obj.Value, obj.Node, children.Select(c => (c.Node, c.Name)).ToList()));
        }
        else
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var number = obj.Value; uint previous = obj.Node | 0x80000000; ulong allocated = 0, remaining = obj.Size;
            while (number != 0)
            {
                Require(usedExtents.Add(number) && extents.ContainsKey(number), $"{path}: shared, missing or cyclic extent.");
                var extent = extents[number];
                Require(extent.Previous == previous, $"{path}: broken extent previous link.");
                for (uint i = 0; i < extent.Count; i++)
                {
                    Claim(number + i, path);
                    var data = Read(number + i);
                    var take = (int)Math.Min((ulong)bytes, remaining);
                    hash.AppendData(data, 0, take); remaining -= (uint)take;
                }
                allocated += extent.Count;
                previous = number; number = extent.Next;
            }
            Require(remaining == 0 && allocated == ((ulong)obj.Size + (uint)bytes - 1) / (uint)bytes,
                $"{path}: file size and extent allocation disagree.");
            digest = Convert.ToHexString(hash.GetHashAndReset());
        }
        entries.Add(new(path, obj.Node, directory, directory ? 0 : obj.Size, obj.Protection,
            obj.Modified, obj.Uid, obj.Gid, obj.Flags, obj.Comment, digest));
    }
    private string Fold(string name) => caseSensitive ? name : new string(name.Select(c =>
        c is >= 'a' and <= 'z' or >= '\u00e0' and <= '\u00f6' or >= '\u00f8' and <= '\u00fe' ? (char)(c - 32) : c).ToArray());
    private void CheckHashes()
    {
        foreach (var (number, parent, children) in hashes)
        {
            var data = Meta(number, "HTAB");
            Require(U32(data, 12) == parent, "Hash table has wrong parent.");
            var expected = children.ToDictionary(c => c.Node, c => c.Name);
            var seen = new HashSet<uint>();
            var buckets = (bytes - 16) / 4;
            for (var bucket = 0; bucket < buckets; bucket++)
            {
                var current = U32(data, 16 + bucket * 4);
                while (current != 0)
                {
                    Require(seen.Add(current) && expected.ContainsKey(current) && nodes.ContainsKey(current),
                        "Hash chain has cyclic, foreign or missing object.");
                    var name = Fold(expected[current]);
                    ushort hash = (ushort)name.Length;
                    foreach (var c in name) hash = unchecked((ushort)(hash * 13 + c));
                    var node = nodes[current];
                    Require(node.Hash == hash && hash % buckets == bucket, "Incorrect object hash/bucket.");
                    current = node.Next;
                }
            }
            Require(seen.SetEquals(expected.Keys), "Hash table omits directory objects.");
        }
    }
}
