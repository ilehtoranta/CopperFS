using System.Buffers.Binary;
using System.Text;
using Amiga;
using CopperSFS.Check;

namespace CopperSFS.Tests;

internal sealed class SimulatedPowerCut : Exception;

// The backing device includes guard blocks on both sides of the partition. Bounds errors
// throw before a copy; every snapshot also verifies that the guard bytes stayed intact.
internal struct ReliabilityDisk
    : IAmigaWritableFileSystemPlatform
{
    internal const int Bytes = 512;
    internal static long CrashStateLimit
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("COPPERSFS_MAX_CRASH_STATES");
            if (string.IsNullOrEmpty(configured)) return 16_777_216;
            if (!long.TryParse(configured, out var limit) || limit < 1)
                throw new InvalidOperationException("COPPERSFS_MAX_CRASH_STATES must be a positive integer.");
            return limit;
        }
    }
    private readonly byte[] device;
    private readonly uint blocks;
    private readonly Dictionary<uint, List<byte[]>> pending = new();
    internal readonly byte[] Ram = new byte[16384];
    internal int Events, Writes, Reads, CutAfterEvent, FailAtEvent, FailAtRead, TearAtWrite, TearBytes;
    internal bool PersistentFailure;
    internal ReliabilityDisk(byte[] image)
    {
        blocks = (uint)(image.Length / Bytes);
        device = new byte[image.Length + Bytes * 2];
        device.AsSpan(0, Bytes).Fill(0xA5);
        device.AsSpan(device.Length - Bytes).Fill(0x5A);
        image.CopyTo(device, Bytes);
    }
    public uint BlockCount => Geometry.BlockCount;
    public uint BlockBytes => Geometry.BlockBytes;
    public uint ReservedBlocks => Geometry.ReservedBlocks;
    public bool ReadMetadataBlock(uint block, uint id, APTR target) => AmigaFileSystemBlockIo.ReadMetadata(ref this, block, id, target);
    public APTR ReadMetadataView(uint block, uint id, APTR target) => ReadMetadataBlock(block, id, target) ? target : APTR.Null;
    public uint ReadDataBlocks(uint block, uint count, APTR target) => AmigaFileSystemBlockIo.Read(ref this, block, count, target);
    public bool WriteDataBlocks(uint block, uint count, APTR source) => AmigaFileSystemBlockIo.Write(ref this, block, count, source);
    public AmigaVolumeGeometry Geometry
    {
        get { AmigaVolumeGeometry.Create(blocks, Bytes, 1, out var result); return result; }
    }
    public bool WriteProtected() => false;
    private bool Failed(int count, int at) => at > 0 && (PersistentFailure ? count >= at : count == at);
    public bool ReadBlock(uint block, APTR destination)
    {
        if (block >= blocks) throw new InvalidOperationException($"Read outside partition: {block}");
        if (Failed(++Reads, FailAtRead)) return false;
        if (!IsMapped(destination, Bytes)) return false;
        if (pending.TryGetValue(block, out var versions)) versions[^1].CopyTo(Ram, (int)destination.Raw);
        else device.AsSpan(Bytes + (int)block * Bytes, Bytes).CopyTo(Ram.AsSpan((int)destination.Raw));
        return true;
    }
    public bool WriteBlock(uint block, APTR source)
    {
        if (block < 1 || block >= blocks - 1) throw new InvalidOperationException($"Write outside writable partition: {block}");
        Writes++;
        if (Failed(++Events, FailAtEvent)) return false;
        if (!IsMapped(source, Bytes)) return false;
        var value = Ram.AsSpan((int)source.Raw, Bytes).ToArray();
        if (Writes == TearAtWrite)
        {
            value.AsSpan(0, TearBytes).CopyTo(device.AsSpan(Bytes + (int)block * Bytes));
            throw new SimulatedPowerCut();
        }
        if (!pending.TryGetValue(block, out var versions)) pending.Add(block, versions = new());
        versions.Add(value);
        if (Events == CutAfterEvent) throw new SimulatedPowerCut();
        return true;
    }
    public bool Flush()
    {
        if (Failed(++Events, FailAtEvent)) return false;
        foreach (var (block, versions) in pending)
            versions[^1].CopyTo(device, Bytes + (int)block * Bytes);
        pending.Clear();
        if (Events == CutAfterEvent) throw new SimulatedPowerCut();
        return true;
    }
    internal byte[] Snapshot()
    {
        if (device.AsSpan(0, Bytes).ContainsAnyExcept((byte)0xA5) ||
            device.AsSpan(device.Length - Bytes).ContainsAnyExcept((byte)0x5A))
            throw new InvalidOperationException("Partition guard was overwritten.");
        return device.AsSpan(Bytes, (int)blocks * Bytes).ToArray();
    }
    internal IEnumerable<byte[]> CrashImages()
    {
        // A pending block can retain its old value or any complete version written
        // since the last barrier. Enumerate the Cartesian product across blocks.
        var writes = pending.ToArray();
        long combinations = 1;
        foreach (var pair in writes) combinations = checked(combinations * (pair.Value.Count + 1));
        if (combinations > CrashStateLimit)
            throw new InvalidOperationException($"Crash state space too large: {combinations} (limit {CrashStateLimit})");
        for (long selection = 0; selection < combinations; selection++)
        {
            var image = Snapshot(); var digits = selection;
            foreach (var (block, versions) in writes)
            {
                var choice = (int)(digits % (versions.Count + 1)); digits /= versions.Count + 1;
                if (choice != 0) versions[choice - 1].CopyTo(image, (int)block * Bytes);
            }
            yield return image;
        }
    }
    internal APTR Name(string name)
    {
        Encoding.Latin1.GetBytes(name).CopyTo(Ram, 3000); return APTR.FromPointer(3000);
    }
    public byte ReadUInt8(APTR a, int offset = 0) => Ram[(int)a.Raw + offset];
    public ushort ReadUInt16(APTR a, int offset = 0) => BinaryPrimitives.ReadUInt16BigEndian(Ram.AsSpan((int)a.Raw + offset));
    public uint ReadUInt32(APTR a, int offset = 0) => BinaryPrimitives.ReadUInt32BigEndian(Ram.AsSpan((int)a.Raw + offset));
    public void WriteUInt8(APTR a, int offset, byte value) => Ram[(int)a.Raw + offset] = value;
    public void WriteUInt16(APTR a, int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(Ram.AsSpan((int)a.Raw + offset), value);
    public void WriteUInt32(APTR a, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(Ram.AsSpan((int)a.Raw + offset), value);
    public bool IsMapped(APTR a, uint length) => a.Raw <= Ram.Length && length <= Ram.Length - a.Raw;
    public void Clear(APTR a, uint length) => Ram.AsSpan((int)a.Raw, (int)length).Clear();
    public void Copy(APTR from, APTR to, uint length) => Ram.AsSpan((int)from.Raw, (int)length).CopyTo(Ram.AsSpan((int)to.Raw));
}

internal static class ReliabilityFixture
{
    internal const int Blocks = 256, Bytes = 512;
    internal static Span<byte> Block(byte[] image, int number) => image.AsSpan(number * Bytes, Bytes);
    internal static void U16(Span<byte> data, int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(data[offset..], value);
    internal static void U32(Span<byte> data, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(data[offset..], value);
    internal static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
    internal static void Seal(Span<byte> data)
    {
        U32(data, 4, 0); uint sum = 1;
        for (var i = 0; i < data.Length; i += 4) sum = unchecked(sum + U32(data, i));
        U32(data, 4, unchecked(0u - sum));
    }
    internal static byte[] Image()
    {
        // Normalize the older traversal fixture into a fully accounted image:
        // all metadata lives in admin ranges and all file blocks have owners.
        var image = new byte[Blocks * Bytes];
        AmigaSmartFileSystemTests.Image().CopyTo(image, 0);
        Block(image, 127).CopyTo(Block(image, Blocks - 1));
        foreach (var number in new[] { 0, Blocks - 1 })
        {
            var root = Block(image, number);
            U32(root, 8, (uint)number); U32(root, 44, Blocks * Bytes); U32(root, 48, Blocks); Seal(root);
        }
        Block(image, 20).CopyTo(Block(image, 40)); Block(image, 21).CopyTo(Block(image, 41));
        Block(image, 23).CopyTo(Block(image, 43)); Block(image, 24).CopyTo(Block(image, 44));
        var tree = Block(image, 5);
        U32(tree, 16, 40); U32(tree, 20, 43); U32(tree, 30, 43); U32(tree, 38, 40); U32(tree, 44, 44); Seal(tree);
        var rootObject = Block(image, 2); U32(rootObject, 36, 3); Seal(rootObject);
        var directory = Block(image, 7);
        U32(directory, 36, 11); U32(directory, 68, 40); Seal(directory);
        U32(Block(image, 8), 36, 44); Seal(Block(image, 8));
        foreach (var (number, parent) in new[] { (3, 1), (11, 2) })
        {
            var table = Block(image, number); table.Clear();
            U32(table, 0, 0x48544142); U32(table, 8, (uint)number); U32(table, 12, (uint)parent);
        }
        foreach (var (name, node, table) in new[] { ("Docs", 2, 3), ("Hello", 3, 3), ("Nested", 4, 11) })
        {
            ushort hash = (ushort)name.Length;
            foreach (var c in name.ToUpperInvariant()) hash = unchecked((ushort)(hash * 13 + c));
            var bucket = 16 + hash % 124 * 4;
            var nodes = Block(image, 6); var slot = 20 + (node - 1) * 10;
            U32(nodes, slot + 4, U32(Block(image, table), bucket)); U16(nodes, slot + 8, hash);
            U32(Block(image, table), bucket, (uint)node);
        }
        Seal(Block(image, 6)); Seal(Block(image, 3)); Seal(Block(image, 11));
        var admin = Block(image, 10); admin.Clear();
        U32(admin, 0, 0x41444D43); U32(admin, 8, 10); admin[20] = 32;
        U32(admin, 24, 1); U32(admin, 32, 64);
        uint used = 0;
        foreach (var number in new[] { 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }) used |= 0x80000000u >> (number - 1);
        U32(admin, 28, used); Seal(admin);
        var bitmap = Block(image, 9);
        bitmap[12..].Fill(0xFF);
        var allocated = Enumerable.Range(1, 32).Concat(Enumerable.Range(64, 32))
            .Concat(new[] { 0, Blocks - 1, 40, 41, 43, 44 }).ToArray();
        foreach (var number in allocated) bitmap[12 + number / 8] &= (byte)~(0x80 >> (number % 8));
        Seal(bitmap);
        U32(rootObject, Bytes - 28, (uint)(Blocks - allocated.Length)); Seal(rootObject);
        return image;
    }
    internal static ImageReport Inspect(byte[] image)
    {
        using var stream = new MemoryStream(image, writable: false);
        return SfsImageChecker.Check(stream);
    }
}
