using System.Buffers.Binary;
using System.Text;
using Amiga;

namespace CopperSFS.Tests;

public sealed class Sfs2ReferenceFactAttribute : FactAttribute
{
    public Sfs2ReferenceFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COPPERSFS_SFS2_REFERENCE_IMAGE")))
            Skip = "Set COPPERSFS_SFS2_REFERENCE_IMAGE to an original-handler image from CopperSFS.Sfs2Reference.";
    }
}

public sealed class Sfs2ReadOnlyTests
{
    private const uint Objects = 0x4F424A43, Nodes = 0x4E444320, Tree = 0x424E4443;
    private static readonly APTR Meta = APTR.FromPointer(512), Data = APTR.FromPointer(1024), Target = APTR.FromPointer(4096);

    private struct Disk(Dictionary<uint, byte[]> blocks, uint blockCount = 128, uint blockBytes = 512,
        uint reservedBlocks = 1) : IAmigaBlockRangeFileSystemPlatform
    {
        public readonly byte[] Ram = new byte[32768];
        public int Writes, Flushes, Reads;
        public uint PatternStart, PatternEnd;
        public AmigaVolumeGeometry Geometry { get { AmigaVolumeGeometry.Create(blockCount, blockBytes, reservedBlocks, out var g); return g; } }
        public bool ReadBlock(uint block, APTR destination)
        {
            Reads++;
            if (Reads > 10000) throw new InvalidDataException("Traversal did not terminate.");
            if (block >= blockCount || !IsMapped(destination, blockBytes)) return false;
            var target = Ram.AsSpan((int)destination.Raw, (int)blockBytes);
            if (blocks.TryGetValue(block, out var value)) value.CopyTo(target);
            else if (block >= PatternStart && block < PatternEnd)
                for (var i = 0; i < blockBytes; i++) target[i] = (byte)(i * 17 + 3);
            else target.Clear();
            return true;
        }
        public bool WriteProtected() => false; // Catch accidental writes even on a writable provider.
        public bool WriteBlock(uint block, APTR source) { Writes++; return false; }
        public bool Flush() { Flushes++; return false; }
        public bool ReadBlocks(uint first, uint count, APTR destination) => throw new NotSupportedException();
        public bool WriteBlocks(uint first, uint count, APTR source) { Writes++; return false; }
        public byte ReadUInt8(APTR address, int offset = 0) => Ram[(int)address.Raw + offset];
        public ushort ReadUInt16(APTR address, int offset = 0) => BinaryPrimitives.ReadUInt16BigEndian(Ram.AsSpan((int)address.Raw + offset));
        public uint ReadUInt32(APTR address, int offset = 0) => BinaryPrimitives.ReadUInt32BigEndian(Ram.AsSpan((int)address.Raw + offset));
        public void WriteUInt8(APTR address, int offset, byte value) => Ram[(int)address.Raw + offset] = value;
        public void WriteUInt16(APTR address, int offset, ushort value) => U16(Ram, (int)address.Raw + offset, value);
        public void WriteUInt32(APTR address, int offset, uint value) => U32(Ram, (int)address.Raw + offset, value);
        public bool IsMapped(APTR address, uint length) => address.Raw <= Ram.Length && length <= Ram.Length - address.Raw;
        public void Clear(APTR address, uint length) => Ram.AsSpan((int)address.Raw, (int)length).Clear();
        public void Copy(APTR source, APTR destination, uint length) => Ram.AsSpan((int)source.Raw, (int)length).CopyTo(Ram.AsSpan((int)destination.Raw));
        public APTR Name(string value) { Encoding.Latin1.GetBytes(value).CopyTo(Ram, 3000); return APTR.FromPointer(3000); }
        public string Text(APTR pointer, uint length) => Encoding.Latin1.GetString(Ram, (int)pointer.Raw, (int)length);
    }

    private static void U16(byte[] b, int at, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at), value);
    private static void U32(byte[] b, int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at), value);
    private static void Seal(byte[] b, bool sfs2)
    {
        U32(b, 4, 0); uint sum = sfs2 ? 2u : 1u;
        for (var at = 0; at < b.Length; at += 4) sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at)));
        U32(b, 4, unchecked(0u - sum));
    }
    private static int AddObject(byte[] b, int at, uint node, uint value, ulong size, byte flags, string name, bool sfs2,
        string comment = "")
    {
        U32(b, at + 4, node); U32(b, at + 8, 0x8F); U32(b, at + 12, value);
        var extra = sfs2 ? 2 : 0;
        if ((flags & 128) != 0 || !sfs2) U32(b, at + 16, (uint)size);
        else { U32(b, at + 16, (uint)(size >> 16)); U16(b, at + 20, (ushort)size); }
        U32(b, at + 20 + extra, 123456789); b[at + 24 + extra] = flags;
        Encoding.Latin1.GetBytes(name).CopyTo(b, at + 25 + extra);
        Encoding.Latin1.GetBytes(comment).CopyTo(b, at + 26 + extra + name.Length);
        return (at + 28 + extra + name.Length + comment.Length) & ~1;
    }

    // Original synthetic test data using the layout verified by the external formatter;
    // no reference disk image or third-party source is embedded in the test assembly.
    private static Dictionary<uint, byte[]> Image(bool sfs2 = true, uint count = 128, int bytes = 512)
    {
        var blocks = new Dictionary<uint, byte[]>();
        byte[] Header(uint n, uint id) { var b = new byte[bytes]; U32(b, 0, id); U32(b, 8, n); blocks[n] = b; return b; }
        foreach (var n in new[] { 0u, count - 1 })
        {
            var b = Header(n, sfs2 ? 0x53465302u : 0x53465300u);
            U16(b, 12, (ushort)(sfs2 ? 4 : 3)); U32(b, 48, count); U32(b, 52, (uint)bytes);
            U32(b, 104, 3); U32(b, 108, 6); U32(b, 112, 7);
        }
        var root = Header(3, Objects); AddObject(root, 24, 1, 4, 8, 128, "Synthetic", sfs2); U32(root, bytes - 28, 90);
        Header(5, 0x54524F4B);
        var children = Header(8, Objects); U32(children, 12, 1);
        var at = AddObject(children, 24, 2, 0, 0, 129, ".recycled", sfs2);
        at = AddObject(children, at, 7, 9, 10, 128, "Docs", sfs2);
        AddObject(children, at, 9, 50, 513, 0, "LengthProbe", sfs2);
        var payload = Header(10, Objects); U32(payload, 12, 7);
        AddObject(payload, 24, 8, 40, (uint)(bytes * 2 + 277), 0, "Payload", sfs2, "Test comment");
        var node = Header(7, Nodes); U32(node, 12, 1); U32(node, 16, 1);
        U32(node, 20, 3); U32(node, 30, 8); U32(node, 80, 8); U32(node, 90, 10); U32(node, 100, 8);
        var tree = Header(6, Tree); U16(tree, 12, 2); tree[14] = 1; tree[15] = (byte)(sfs2 ? 16 : 14);
        U32(tree, 16, 40); U32(tree, 24, 0x80000008);
        if (sfs2) U32(tree, 28, 3); else U16(tree, 28, 3);
        at = 16 + tree[15]; U32(tree, at, 50); U32(tree, at + 8, 0x80000009);
        if (sfs2) U32(tree, at + 12, 2); else U16(tree, at + 12, 2);
        foreach (var b in blocks.Values) Seal(b, sfs2);
        for (uint n = 40; n < 43; n++) blocks[n] = Enumerable.Range(0, bytes).Select(i => (byte)(i * 17 + 3)).ToArray();
        return blocks;
    }

    [Theory]
    [InlineData(false, 1024)]
    [InlineData(false, 2048)]
    [InlineData(false, 4096)]
    [InlineData(true, 1024)]
    [InlineData(true, 2048)]
    [InlineData(true, 4096)]
    public void ReadOnlyTraversalHonorsFilesystemBlockSize(bool sfs2, int bytes)
    {
        var disk = new Disk(Image(sfs2, bytes: bytes), blockBytes: (uint)bytes);
        var metadata = APTR.FromPointer(4096); var data = APTR.FromPointer(8192); var target = APTR.FromPointer(16384);
        Assert.True(AmigaSmartFileSystemReadOnlyCore.Mount(ref disk, metadata, out _));
        var name = "Docs/Payload";
        Assert.Equal(8u, AmigaSmartFileSystemReadOnlyCore.Resolve(ref disk, 1, disk.Name(name), (uint)name.Length, metadata, out _));
        var size = bytes * 2 + 277;
        Assert.Equal(size - 3, AmigaSmartFileSystemReadOnlyCore.Read(ref disk, 8, 3, target, (uint)size, metadata, data, out var error));
        Assert.Equal(DOS.Error.None, error);
        Assert.Equal(Enumerable.Range(3, size - 3).Select(i => (byte)(i * 17 + 3)), disk.Ram.Skip(16384).Take(size - 3));
        Assert.Equal(0, disk.Writes); Assert.Equal(0, disk.Flushes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongNamesLatin1AndCaseSensitiveVolumesRespectTheirFormat(bool sfs2)
    {
        var image = Image(sfs2); var file = image[10]; Array.Clear(file, 24, file.Length - 24);
        var name = "ä" + new string('x', 106);
        AddObject(file, 24, 8, 40, 1301, 0, name, sfs2); Seal(file, sfs2);
        var disk = new Disk(image);
        Assert.Equal(8u, AmigaSmartFileSystemReadOnlyCore.Resolve(ref disk, 7, disk.Name("Ä" + new string('X', 106)), 107, Meta, out _));
        foreach (var n in new[] { 0u, 127u }) { image[n][20] = 128; Seal(image[n], sfs2); }
        Assert.Equal(0u, AmigaSmartFileSystemReadOnlyCore.Resolve(ref disk, 7, disk.Name("Ä" + new string('X', 106)), 107, Meta, out var error));
        Assert.Equal(DOS.Error.ObjectNotFound, error);
        Assert.Equal(8u, AmigaSmartFileSystemReadOnlyCore.Resolve(ref disk, 7, disk.Name(name), 107, Meta, out _));
        Assert.Equal(1u, AmigaSmartFileSystemReadOnlyCore.Resolve(ref disk, 7, disk.Name("/"), 1, Meta, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothFormatsMountResolveDescribeReadAndEnumerateWithoutWrites(bool sfs2)
    {
        var image = Image(sfs2); var before = image.ToDictionary(x => x.Key, x => x.Value.ToArray()); var disk = new Disk(image);
        Assert.True(AmigaSmartFileSystemReadOnlyCore.Mount(ref disk, Meta, out var format));
        Assert.Equal(sfs2 ? AmigaSmartFileSystemFormat.Sfs2 : AmigaSmartFileSystemFormat.Classic, format);
        Assert.Equal(8u, AmigaSmartFileSystemReadOnlyCore.Resolve(ref disk, 1, disk.Name("SFS:docs/pAyLoAd"), 16, Meta, out var error));
        Assert.Equal(DOS.Error.None, error);
        Assert.True(AmigaSmartFileSystemReadOnlyCore.Describe(ref disk, 8, Meta, out var entry));
        Assert.Equal(1301ul, entry.Size); Assert.Equal(7u, entry.Parent); Assert.Equal(128u, entry.Protection);
        Assert.Equal(123456789u, entry.ModifiedSeconds); Assert.Equal("Payload", disk.Text(entry.Name, entry.NameLength));
        Assert.Equal("Test comment", disk.Text(entry.Comment, entry.CommentLength));
        Assert.Equal(1301, AmigaSmartFileSystemReadOnlyCore.Read(ref disk, 8, 0, Target, 2000, Meta, Data, out error));
        Assert.Equal(DOS.Error.None, error);
        Assert.Equal(Enumerable.Range(0, 1301).Select(i => (byte)(i * 17 + 3)), disk.Ram.Skip(4096).Take(1301));
        uint cursor = 1; var names = new List<string>();
        while ((error = AmigaSmartFileSystemReadOnlyCore.ExamineNext(ref disk, 1, ref cursor, Meta, out entry)) == DOS.Error.None)
            names.Add(disk.Text(entry.Name, entry.NameLength));
        Assert.Equal(DOS.Error.NoMoreEntries, error); Assert.Equal(new[] { "Docs", "LengthProbe" }, names);
        Assert.True(AmigaSmartFileSystemReadOnlyCore.CountUsed(ref disk, Meta, out var used)); Assert.Equal(36u, used);
        Assert.Equal(0, disk.Writes); Assert.Equal(0, disk.Flushes);
        foreach (var (n, bytes) in before) Assert.Equal(bytes, image[n]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SparseLargeFileReadsAcrossFourGiBAndAcrossWideExtents(bool indexed)
    {
        const uint count = 0x11000000, first = 0x10000000, extentBlocks = 0x800001;
        var image = Image(count: count); var file = image[10]; var size = (ulong)extentBlocks * 512 + 513;
        U32(file, 36, first); U32(file, 40, (uint)(size >> 16)); U16(file, 44, (ushort)size); Seal(file, true);
        var tree = image[6]; Array.Clear(tree, 16, 496); U16(tree, 12, 3);
        U32(tree, 16, 50); U32(tree, 24, 0x80000009); U32(tree, 28, 2);
        U32(tree, 32, first); U32(tree, 36, first + extentBlocks + 16); U32(tree, 40, 0x80000008); U32(tree, 44, extentBlocks);
        U32(tree, 48, first + extentBlocks + 16); U32(tree, 56, first); U32(tree, 60, 2); Seal(tree, true);
        if (indexed)
        {
            image[11] = tree.ToArray(); U32(image[11], 8, 11); Seal(image[11], true);
            Array.Clear(tree, 16, 496); U16(tree, 12, 1); tree[14] = 0; tree[15] = 8;
            U32(tree, 16, 50); U32(tree, 20, 11); Seal(tree, true);
            image[12] = image[7].ToArray(); U32(image[12], 8, 12); Seal(image[12], true);
            var node = image[7]; Array.Clear(node, 20, 492); U32(node, 16, 49); U32(node, 20, (12u << 1) | 1); Seal(node, true);
        }
        var disk = new Disk(image, count) { PatternStart = first, PatternEnd = first + extentBlocks + 18 };
        Assert.True(AmigaSmartFileSystemReadOnlyCore.Describe(ref disk, 8, Meta, out var entry)); Assert.Equal(size, entry.Size);
        foreach (var offset in new[] { 0xFFFFFFFDul, 0x100000000ul, (ulong)extentBlocks * 512 - 3, size - 3 })
        {
            var length = (uint)Math.Min(1030ul, size - offset);
            Assert.Equal((int)length, AmigaSmartFileSystemReadOnlyCore.Read(ref disk, 8, offset, Target, 1030, Meta, Data, out var error));
            Assert.Equal(DOS.Error.None, error);
            Assert.Equal(Enumerable.Range(0, (int)length).Select(i => (byte)((offset + (uint)i) * 17 + 3)), disk.Ram.Skip(4096).Take((int)length));
        }
        Assert.Equal(0, AmigaSmartFileSystemReadOnlyCore.Read(ref disk, 8, size, Target, 10, Meta, Data, out _));
        Assert.Equal(-1, AmigaSmartFileSystemReadOnlyCore.Read(ref disk, 8, ulong.MaxValue, Target, 1, Meta, Data, out var seek));
        Assert.Equal(DOS.Error.SeekError, seek); Assert.Equal(0, disk.Writes); Assert.Equal(0, disk.Flushes);
    }

    [Theory]
    [InlineData(false, 512)]
    [InlineData(false, 1024)]
    [InlineData(false, 4096)]
    [InlineData(true, 512)]
    [InlineData(true, 1024)]
    [InlineData(true, 4096)]
    public void IndexedObjectPointersDiscardTheFormatsFullnessBits(bool sfs2, int bytes)
    {
        var image = Image(sfs2, bytes: bytes); var leaf = image[7].ToArray(); image[12] = leaf;
        U32(leaf, 8, 12); Seal(leaf, sfs2);
        var branch = image[7]; Array.Clear(branch, 20, bytes - 20); U32(branch, 16, (uint)(bytes - 20) / 10);
        var shift = sfs2 ? 1 : System.Numerics.BitOperations.Log2((uint)bytes) - 5;
        U32(branch, 20, (12u << shift) | ((1u << shift) - 1)); Seal(branch, sfs2);
        var disk = new Disk(image, blockBytes: (uint)bytes); var metadata = APTR.FromPointer(4096);
        Assert.True(AmigaSmartFileSystemReadOnlyCore.Describe(ref disk, 8, metadata, out var entry));
        Assert.Equal((ulong)(bytes * 2 + 277), entry.Size);
        var path = "Docs/Payload";
        Assert.Equal(8u, AmigaSmartFileSystemReadOnlyCore.Resolve(ref disk, 1, disk.Name(path), (uint)path.Length, metadata, out var error));
        Assert.Equal(DOS.Error.None, error);
    }

    [Fact]
    public void EveryExistingMutationAndRecoveryEntryRejectsSfs2WithoutWriting()
    {
        var disk = new Disk(Image()); var w = new AmigaSmartFileSystemWriteCore.Work
        { Meta = Meta, Data = Data, Bitmap = APTR.FromPointer(1536), Journal = APTR.FromPointer(2048) };
        var r = new AmigaSmartFileSystemWriteCore.Request { Node = 8, Directory = 1, Name = disk.Name("New"), Length = 3, Buffer = Target };
        Assert.False(AmigaSmartFileSystemCore.Mount(ref disk, Meta));
        Assert.False(AmigaSmartFileSystemWriteCore.Recover(ref disk, ref w));
        Assert.False(AmigaSmartFileSystemWriteCore.Create(ref disk, ref w, ref r, false));
        Assert.False(AmigaSmartFileSystemWriteCore.Write(ref disk, ref w, ref r, out var actual)); Assert.Equal(0u, actual);
        Assert.False(AmigaSmartFileSystemWriteCore.WriteBatched(ref disk, ref w, ref r, APTR.FromPointer(6144), 512, out actual)); Assert.Equal(0u, actual);
        Assert.False(AmigaSmartFileSystemWriteCore.Resize(ref disk, ref w, 8, 100));
        Assert.False(AmigaSmartFileSystemWriteCore.Truncate(ref disk, ref w, 8));
        Assert.False(AmigaSmartFileSystemWriteCore.Remove(ref disk, ref w, 8));
        Assert.False(AmigaSmartFileSystemWriteCore.Rename(ref disk, ref w, ref r));
        Assert.False(AmigaSmartFileSystemWriteCore.SetMetadata(ref disk, ref w, ref r, DosPacketAction.SetComment));
        Assert.Equal(0, disk.Writes); Assert.Equal(0, disk.Flushes);
    }

    [Theory]
    [InlineData("dirty")]
    [InlineData("root-version")]
    [InlineData("root-checksum")]
    [InlineData("object-checksum")]
    [InlineData("comment")]
    [InlineData("extent-width")]
    [InlineData("extent-range")]
    [InlineData("extent-cycle")]
    [InlineData("node-cycle")]
    public void MalformedOrDirtySfs2IsRefusedWithoutWrites(string corruption)
    {
        var image = Image();
        switch (corruption)
        {
            case "dirty": U32(image[5], 0, 0x54524641); Seal(image[5], true); break;
            case "root-version": foreach (var n in new[] { 0u, 127u }) { U16(image[n], 12, 3); Seal(image[n], true); } break;
            case "root-checksum": foreach (var n in new[] { 0u, 127u }) Seal(image[n], false); break;
            case "object-checksum": Seal(image[10], false); break;
            case "comment": Array.Fill(image[10], (byte)'x', 59, 453); Seal(image[10], true); break;
            case "extent-width": image[6][15] = 14; Seal(image[6], true); break;
            case "extent-range": U32(image[6], 28, uint.MaxValue); Seal(image[6], true); break;
            case "extent-cycle": U32(image[6], 20, 40); U32(image[6], 28, 1); Seal(image[6], true); break;
            case "node-cycle": U32(image[7], 16, 49); U32(image[7], 20, 7u << 1); Seal(image[7], true); break;
        }
        var disk = new Disk(image);
        var result = AmigaSmartFileSystemReadOnlyCore.Read(ref disk, 8, corruption == "extent-cycle" ? 512ul : 0, Target, 10, Meta, Data, out var error);
        Assert.Equal(-1, result); Assert.Equal(DOS.Error.DiskNotValidated, error);
        Assert.Equal(0, disk.Writes); Assert.Equal(0, disk.Flushes);
    }

    [Fact]
    public void RootCopiesSelectNewestAndRejectMixedFormats()
    {
        var image = Image(); U16(image[0], 14, 65535); Seal(image[0], true);
        U16(image[127], 14, 0); Seal(image[127], true); var disk = new Disk(image);
        Assert.True(AmigaSmartFileSystemReadOnlyCore.Root(ref disk, Meta, out _)); Assert.Equal(127u, disk.ReadUInt32(Meta, 8));
        U32(image[0], 0, 0x53465300); U16(image[0], 12, 3); Seal(image[0], false);
        Assert.False(AmigaSmartFileSystemReadOnlyCore.Root(ref disk, Meta, out _));
        image[0][0] = 0;
        Assert.True(AmigaSmartFileSystemReadOnlyCore.Mount(ref disk, Meta, out var format)); Assert.Equal(AmigaSmartFileSystemFormat.Sfs2, format);
    }

    [Sfs2ReferenceFact]
    public void ReadsOriginalHandlerImageAndPreservesEveryByte()
    {
        var path = Environment.GetEnvironmentVariable("COPPERSFS_SFS2_REFERENCE_IMAGE")!;
        var bytes = File.ReadAllBytes(path); var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        var blocks = new Dictionary<uint, byte[]>();
        for (uint n = 0; n < bytes.Length / 512; n++) blocks[n] = bytes.AsSpan((int)n * 512, 512).ToArray();
        var disk = new Disk(blocks, (uint)(bytes.Length / 512), reservedBlocks: 2);
        Assert.True(AmigaSmartFileSystemReadOnlyCore.Mount(ref disk, Meta, out var format)); Assert.Equal(AmigaSmartFileSystemFormat.Sfs2, format);
        foreach (var (name, size) in new[] { ("Docs/Payload", 1301ul), ("LengthProbe", 513ul) })
        {
            var node = AmigaSmartFileSystemReadOnlyCore.Resolve(ref disk, 1, disk.Name(name), (uint)name.Length, Meta, out var error);
            Assert.Equal(DOS.Error.None, error); Assert.NotEqual(0u, node);
            Assert.True(AmigaSmartFileSystemReadOnlyCore.Describe(ref disk, node, Meta, out var entry)); Assert.Equal(size, entry.Size);
            if (name.StartsWith("Docs"))
            {
                Assert.Equal("Original SFS 1.279", disk.Text(entry.Comment, entry.CommentLength));
                Assert.Equal(128u, entry.Protection);
                Assert.Equal(1234u * 86400 + 56 * 60 + 2, entry.ModifiedSeconds);
            }
            Assert.Equal((int)size, AmigaSmartFileSystemReadOnlyCore.Read(ref disk, node, 0, Target, (uint)size, Meta, Data, out error));
            Assert.Equal(DOS.Error.None, error);
            Assert.Equal(Enumerable.Range(0, (int)size).Select(i => (byte)(i * 17 + 3)), disk.Ram.Skip(4096).Take((int)size));
        }
        uint cursor = 1; var names = new List<string>();
        while (AmigaSmartFileSystemReadOnlyCore.ExamineNext(ref disk, 1, ref cursor, Meta, out var entry) == DOS.Error.None)
            names.Add(disk.Text(entry.Name, entry.NameLength));
        Assert.Contains("Docs", names); Assert.Contains("LengthProbe", names); Assert.DoesNotContain(".recycled", names);
        var docs = AmigaSmartFileSystemReadOnlyCore.Resolve(ref disk, 1, disk.Name("Docs"), 4, Meta, out _);
        cursor = docs; var children = new List<string>(); DOS.Error enumeration;
        while ((enumeration = AmigaSmartFileSystemReadOnlyCore.ExamineNext(ref disk, docs, ref cursor, Meta, out var entry)) == DOS.Error.None)
            children.Add(disk.Text(entry.Name, entry.NameLength));
        Assert.Equal(DOS.Error.NoMoreEntries, enumeration); Assert.Equal(81, children.Count);
        Assert.Contains("Entry00", children); Assert.Contains("Entry79", children);
        var last = "Docs/Entry79";
        Assert.NotEqual(0u, AmigaSmartFileSystemReadOnlyCore.Resolve(ref disk, 1, disk.Name(last), (uint)last.Length, Meta, out var lastError));
        Assert.Equal(DOS.Error.None, lastError);
        Assert.True(AmigaSmartFileSystemReadOnlyCore.CountUsed(ref disk, Meta, out var used));
        var rootContainer = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(104));
        var free = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan((int)rootContainer * 512 + 484));
        Assert.Equal((uint)(bytes.Length / 512) - 3 - free, used);
        Assert.Equal(0, disk.Writes); Assert.Equal(0, disk.Flushes);
        Assert.Equal(hash, System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        foreach (var (n, block) in blocks) Assert.Equal(bytes.AsSpan((int)n * 512, 512).ToArray(), block);
    }
}
