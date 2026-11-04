using System.Buffers.Binary;
using System.Text.Json;
using Amiga;
using CopperSFS.Bench;
using Xunit.Abstractions;
using static CopperSFS.AmigaSmartFileSystem2WriteCore;
using static CopperSFS.Tests.Sfs2WriteTests;

namespace CopperSFS.Tests;

public sealed class Sfs2WideWriteTests(ITestOutputHelper output)
{
    private const uint First = 0x10000000, ExtentBlocks = 0x800001, Blocks = 0x11000000, MapBase = 128;
    private const ulong OldSize = (ulong)ExtentBlocks * 512 - 3;
    private const ulong Position = 0xFFFFFFFD;
    private static readonly APTR Meta = APTR.FromPointer(512), Data = APTR.FromPointer(1024), Payload = APTR.FromPointer(8192);
    private static Work Work() => new() { Meta = Meta, Data = Data, Bitmap = APTR.FromPointer(1536), Journal = APTR.FromPointer(2048), JournalCursor = 33 };

    // Sparse HOST backing for an ordinarily allocated file; not SFS sparse files.
    private struct Disk(Dictionary<uint, byte[]> source, uint volumeBlocks = Blocks) : IAmigaBlockRangeFileSystemPlatform
    {
        private readonly Dictionary<uint, byte[]> durable = source.ToDictionary(x => x.Key, x => x.Value.ToArray());
        private readonly Dictionary<uint, List<byte[]>> pending = new();
        internal readonly byte[] Ram = new byte[131072];
        internal int Events, CutAfter;
        public uint BlockCount => Geometry.BlockCount;
        public uint BlockBytes => Geometry.BlockBytes;
        public uint ReservedBlocks => Geometry.ReservedBlocks;
        public bool ReadMetadataBlock(uint block, uint id, APTR target) => AmigaFileSystemBlockIo.ReadMetadata(ref this, block, id, target);
        public APTR ReadMetadataView(uint block, uint id, APTR target) => ReadMetadataBlock(block, id, target) ? target : APTR.Null;
        public uint ReadDataBlocks(uint block, uint count, APTR target) => AmigaFileSystemBlockIo.Read(ref this, block, count, target);
        public bool WriteDataBlocks(uint block, uint count, APTR source) => AmigaFileSystemBlockIo.Write(ref this, block, count, source);
        public AmigaVolumeGeometry Geometry { get { AmigaVolumeGeometry.Create(volumeBlocks, 512, 1, out var g); return g; } }
        public bool WriteProtected() => false;
        public APTR RedoOverlay { get; set; }
        public bool ReadBlock(uint block, APTR target)
        {
            if (RedoOverlay.IsNotNull) { if (!AmigaSmartFileSystemRedoArena.TryFind(ref this, RedoOverlay, block, out var view)) return false; if (view.IsNotNull) { Copy(view, target, BlockBytes); return true; } }
            Assert.True(block < volumeBlocks);
            if (pending.TryGetValue(block, out var versions)) versions[^1].CopyTo(Ram, (int)target.Raw);
            else if (durable.TryGetValue(block, out var bytes)) bytes.CopyTo(Ram, (int)target.Raw);
            else if (block >= First && block < First + ExtentBlocks)
                for (var i = 0; i < 512; i++) Ram[(int)target.Raw + i] = (byte)(i * 17 + 3);
            else Ram.AsSpan((int)target.Raw, 512).Clear();
            return true;
        }
        public bool WriteBlock(uint block, APTR source)
        {
            if (RedoOverlay.IsNotNull) return AmigaSmartFileSystemRedoArena.Store(ref this, RedoOverlay, block, source);
            Assert.InRange(block, 1u, volumeBlocks - 2);
            if (!pending.TryGetValue(block, out var versions)) pending.Add(block, versions = new());
            versions.Add(Ram.AsSpan((int)source.Raw, 512).ToArray());
            if (++Events == CutAfter) throw new StudyPowerCut(); return true;
        }
        public bool Flush()
        {
            foreach (var (block, versions) in pending) durable[block] = versions[^1].ToArray();
            pending.Clear(); if (++Events == CutAfter) throw new StudyPowerCut(); return true;
        }
        public bool ReadBlocks(uint first, uint count, APTR target)
        { for (uint i = 0; i < count; i++) if (!ReadBlock(first + i, APTR.FromPointer(target.Raw + i * 512))) return false; return true; }
        public bool WriteBlocks(uint first, uint count, APTR source)
        { for (uint i = 0; i < count; i++) if (!WriteBlock(first + i, APTR.FromPointer(source.Raw + i * 512))) return false; return true; }
        internal Dictionary<uint, byte[]> Snapshot() => durable.ToDictionary(x => x.Key, x => x.Value.ToArray());
        internal IEnumerable<Dictionary<uint, byte[]>> CrashImages()
        {
            var blocks = pending.ToArray(); long count = 1;
            foreach (var (_, versions) in blocks) count = checked(count * (versions.Count + 1));
            Assert.InRange(count, 1, 65536);
            for (long selection = 0; selection < count; selection++)
            {
                var image = Snapshot(); var digits = selection;
                foreach (var (block, versions) in blocks)
                {
                    var choice = (int)(digits % (versions.Count + 1)); digits /= versions.Count + 1;
                    if (choice > 0) image[block] = versions[choice - 1].ToArray();
                }
                yield return image;
            }
        }
        public byte ReadUInt8(APTR a, int at = 0) => Ram[(int)a.Raw + at];
        public ushort ReadUInt16(APTR a, int at = 0) => BinaryPrimitives.ReadUInt16BigEndian(Ram.AsSpan((int)a.Raw + at));
        public uint ReadUInt32(APTR a, int at = 0) => U32(Ram, (int)a.Raw + at);
        public void WriteUInt8(APTR a, int at, byte value) => Ram[(int)a.Raw + at] = value;
        public void WriteUInt16(APTR a, int at, ushort value) => U16(Ram, (int)a.Raw + at, value);
        public void WriteUInt32(APTR a, int at, uint value) => U32(Ram, (int)a.Raw + at, value);
        public bool IsMapped(APTR a, uint count) => a.Raw <= Ram.Length && count <= Ram.Length - a.Raw;
        public void Clear(APTR a, uint count) => Ram.AsSpan((int)a.Raw, (int)count).Clear();
        public void Copy(APTR a, APTR b, uint count) => Ram.AsSpan((int)a.Raw, (int)count).CopyTo(Ram.AsSpan((int)b.Raw));
    }
    private static Dictionary<uint, byte[]> Image()
    {
        var blocks = new Dictionary<uint, byte[]>();
        byte[] Header(uint n, uint id) { var b = new byte[512]; U32(b, 0, id); U32(b, 8, n); blocks[n] = b; return b; }
        foreach (var n in new[] { 0u, Blocks - 1 })
        {
            var b = Header(n, 0x53465302); U16(b, 12, 4); U32(b, 48, Blocks); U32(b, 52, 512);
            U32(b, 96, MapBase); U32(b, 100, 8); U32(b, 104, 2); U32(b, 108, 5); U32(b, 112, 6);
        }
        var root = Header(2, 0x4F424A43); U32(root, 28, 1); U32(root, 32, 15); U32(root, 40, 7); root[50] = 128; root[51] = (byte)'R'; U32(root, 484, 97);
        Header(4, 0x54524F4B);
        var tree = Header(5, 0x424E4443); U16(tree, 12, 1); tree[14] = 1; tree[15] = 16;
        U32(tree, 16, First); U32(tree, 24, 0x80000002); U32(tree, 28, ExtentBlocks);
        var nodes = Header(6, 0x4E444320); U32(nodes, 12, 1); U32(nodes, 16, 1); U32(nodes, 20, 2); U32(nodes, 30, 7);
        var obj = Header(7, 0x4F424A43); U32(obj, 12, 1); U32(obj, 28, 2); U32(obj, 32, 31); U32(obj, 36, First);
        U32(obj, 40, (uint)(OldSize >> 16)); U16(obj, 44, (ushort)(OldSize & 65535));
        obj[51] = (byte)'W'; obj[52] = (byte)'i'; obj[53] = (byte)'d'; obj[54] = (byte)'e';
        var admin = Header(8, 0x41444D43); admin[20] = 32; U32(admin, 24, 1); U32(admin, 28, 0x7F000000);
        var map0 = Header(MapBase, 0x42544D50);
        for (uint b = 33; b < 128; b++) map0[12 + b / 8] |= (byte)(128 >> (int)(b % 8));
        var map = Header(MapBase + (First + ExtentBlocks) / 4000, 0x42544D50);
        for (uint b = First + ExtentBlocks; b < First + ExtentBlocks + 2; b++) map[12 + b % 4000 / 8] |= (byte)(128 >> (int)(b % 8));
        foreach (var b in blocks.Values) Seal(b); return blocks;
    }

    [Fact]
    public void WritableVolumeLimitProtectsTheExtentOwnerTagBit()
    {
        var image = Image(); image.Remove(Blocks - 1);
        var last = new byte[512]; image[MaxVolumeBlocks] = last;
        image[0].CopyTo(last, 0); U32(last, 8, MaxVolumeBlocks);
        foreach (var n in new[] { 0u, MaxVolumeBlocks }) { U32(image[n], 48, MaxVolumeBlocks + 1); Seal(image[n]); }
        var disk = new Disk(image, MaxVolumeBlocks + 1); var w = Work();
        Assert.True(AmigaSmartFileSystemReadOnlyCore.Mount(ref disk, Meta, out _));
        Assert.False(Recover(ref disk, ref w)); Assert.Equal(0, disk.Events);
        Assert.False(WriteWide(ref disk, false)); Assert.Equal(0, disk.Events);
    }
    private static bool WriteWide(ref Disk disk, bool batched, bool redo = false)
    {
        var w = Work();
        for (var i = 0; i < 1027; i++) disk.Ram[(int)Payload.Raw + i] = (byte)(i * 29 + 7);
        var r = new Request { Node = 2, Position = Position, Buffer = Payload, Length = 1027 };
        if (redo) return ExecuteTrst(ref disk, ref w, ref r, TrstOperation.Write, APTR.FromPointer(65536), 65536, out _);
        return batched ? WriteBatched(ref disk, ref w, ref r, APTR.FromPointer(65536), 65536, out _) : Write(ref disk, ref w, ref r, out _);
    }
    private static string State(ref Disk disk)
    {
        Assert.True(AmigaSmartFileSystemReadOnlyCore.Describe(ref disk, 2, Meta, out var entry));
        Assert.True(AmigaSmartFileSystemReadOnlyCore.CountUsed(ref disk, Meta, out var used));
        var read = AmigaSmartFileSystemReadOnlyCore.Read(ref disk, 2, Position, Payload, 1027, Meta, Data, out var error);
        Assert.Equal(DOS.Error.None, error); Assert.True(read > 0);
        var snapshot = disk.Snapshot();
        return JsonSerializer.Serialize(new { entry.Size, entry.Protection, used, read,
            content = Convert.ToHexString(disk.Ram.AsSpan((int)Payload.Raw, read)),
            tree = Convert.ToHexString(snapshot[5]),
            map = Convert.ToHexString(snapshot[MapBase + (First + ExtentBlocks) / 4000]) });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void WideWriteResizeAndReopenPreserveContents(bool batched, bool redo)
    {
        var disk = new Disk(Image());
        Assert.True(WriteWide(ref disk, batched, redo));
        var expected = State(ref disk);
        var reopened = new Disk(disk.Snapshot()); var work = Work();
        Assert.True(Recover(ref reopened, ref work), work.Error.ToString());
        Assert.Equal(expected, State(ref reopened));
        Assert.True(AmigaSmartFileSystemReadOnlyCore.Describe(ref reopened, 2, Meta, out var entry));
        Assert.Equal(Position + 1027, entry.Size);
        Assert.Equal(ExtentBlocks + 1, U32(reopened.Snapshot()[5], 28));
        Assert.True(Resize(ref reopened, ref work, 2, OldSize));
        Assert.Equal(ExtentBlocks, U32(reopened.Snapshot()[5], 28));
        Assert.False(Resize(ref reopened, ref work, 2, MaxFileSize + 1));
        Assert.Equal(DOS.Error.ObjectTooLarge, work.Error);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void WideWritesAndEveryCutPreserveSizeExtentCountDataAndAllocation(bool batched, bool redo)
    {
        var source = Image(); var original = new Disk(source); var before = State(ref original);
        var baseline = new Disk(source); Assert.True(WriteWide(ref baseline, batched, redo)); var after = State(ref baseline);
        Assert.True(AmigaSmartFileSystemReadOnlyCore.Describe(ref baseline, 2, Meta, out var entry));
        Assert.Equal(Position + 1027, entry.Size); Assert.Equal(ExtentBlocks + 1, U32(baseline.Snapshot()[5], 28));
        long checkedImages = 0;
        for (var cut = 1; cut <= baseline.Events; cut++)
        {
            var disk = new Disk(source) { CutAfter = cut }; Assert.Throws<StudyPowerCut>(() => WriteWide(ref disk, batched, redo));
            foreach (var image in disk.CrashImages())
            {
                var recovered = new Disk(image); var work = Work(); Assert.True(Recover(ref recovered, ref work), work.Error.ToString());
                Assert.Contains(State(ref recovered), new[] { before, after });
                var stable = State(ref recovered); Assert.True(Recover(ref recovered, ref work)); Assert.Equal(stable, State(ref recovered)); checkedImages++;
            }
        }
        var w = Work(); Assert.True(Resize(ref baseline, ref w, 2, OldSize));
        Assert.True(AmigaSmartFileSystemReadOnlyCore.Describe(ref baseline, 2, Meta, out entry)); Assert.Equal(OldSize, entry.Size);
        Assert.Equal(ExtentBlocks, U32(baseline.Snapshot()[5], 28));
        Assert.False(Resize(ref baseline, ref w, 2, MaxFileSize + 1)); Assert.Equal(DOS.Error.ObjectTooLarge, w.Error);
        output.WriteLine($"SFS2 wide batched={batched} redo={redo}: {checkedImages} exhaustive >4 GiB write/recovery images on a >128 GiB logical volume.");
    }
}
