using System.Buffers.Binary;
using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    private static (byte[] Image, byte[] Bytes) PrefixSeed(int layout, int first = 2000, bool constrainReplacementRuns = false)
    {
        var seed = ContinuationSeed(first, nextExtent: true, constrainReplacementRuns: constrainReplacementRuns);
        var original = Block(seed.Image, 5).ToArray();
        if (layout == 3)
        {
            // Fill a leaf with a second file's live extents so replacement
            // insertion must split it. Its bytes and links must survive.
            var leaf = Block(seed.Image, 5); leaf.Slice(16).Clear(); U16(leaf, 12, 35);
            for (var i = 0; i < 33; i++)
            {
                var key = i == 0 ? 24u : (uint)(99 + i); var at = 16 + i * 14;
                U32(leaf, at, key); U32(leaf, at + 4, i == 32 ? 0u : (uint)(100 + i));
                U32(leaf, at + 8, i == 0 ? 0x80000004u : i == 1 ? 24u : (uint)(98 + i));
                U16(leaf, at + 12, 1);
                Block(seed.Image, (int)key).Fill((byte)(i + 17));
            }
            original.AsSpan(30, 28).CopyTo(leaf.Slice(16 + 33 * 14)); Sum(leaf);
            U32(Block(seed.Image, 8), 24 + 16, 33 * Bytes); Sum(Block(seed.Image, 8));
        }
        else if (layout != 0)
        {
            var root = Block(seed.Image, 5); root.Clear(); Header(seed.Image, 5, Tree);
            var children = layout == 1 ? 3 : 2;
            U16(root, 12, (ushort)children); root[15] = 8;
            for (var i = 0; i < children; i++)
            {
                U32(root, 16 + i * 8, i == 0 ? 0u : i == children - 1 ? 6000u : (uint)first);
                U32(root, 20 + i * 8, (uint)(64 + i));
                var leaf = Block(seed.Image, 64 + i); leaf.Clear(); Header(seed.Image, 64 + i, Tree);
                var records = layout == 2 && i == 0 ? 2 : 1;
                var from = layout == 2 && i == 1 ? 2 : i;
                U16(leaf, 12, (ushort)records); leaf[14] = 1; leaf[15] = 14;
                original.AsSpan(16 + from * 14, records * 14).CopyTo(leaf.Slice(16)); Sum(leaf);
            }
            Sum(root); U32(Block(seed.Image, 10), 28, children == 3 ? 0xE0000000u : 0xC0000000u);
            Sum(Block(seed.Image, 10));
        }
        return (seed.Image, seed.Bytes);
    }

    private static Dictionary<uint, (uint Next, uint Previous, int Length)> PrefixRecords(byte[] image)
    {
        var result = new Dictionary<uint, (uint, uint, int)>(); var visited = new HashSet<uint>();
        void Visit(uint block, uint lower, uint upper)
        {
            Assert.True(visited.Add(block)); Assert.InRange(visited.Count, 1, 32);
            var bytes = Block(image, (int)block);
            var count = BinaryPrimitives.ReadUInt16BigEndian(bytes[12..]); var stride = bytes[15];
            Assert.InRange(16 + count * stride, 16, Bytes);
            uint last = lower;
            for (var i = 0; i < count; i++)
            {
                var at = 16 + i * stride; var key = BinaryPrimitives.ReadUInt32BigEndian(bytes[at..]);
                Assert.True(key >= lower && key < upper && (i == 0 || key > last)); last = key;
                if (bytes[14] == 0)
                {
                    var limit = i + 1 == count ? upper : BinaryPrimitives.ReadUInt32BigEndian(bytes[(at + stride)..]);
                    Visit(BinaryPrimitives.ReadUInt32BigEndian(bytes[(at + 4)..]), key, limit);
                }
                else
                {
                    var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[(at + 12)..]); Assert.True(length > 0);
                    Assert.True(result.TryAdd(key, (BinaryPrimitives.ReadUInt32BigEndian(bytes[(at + 4)..]),
                        BinaryPrimitives.ReadUInt32BigEndian(bytes[(at + 8)..]), length)));
                }
            }
        }
        Visit(5, 0, 16384); return result;
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SharedPoolRelocationConsumesWholeExtentsInOneNonRootLeaf(bool scratch)
    {
        var seed = PrefixSeed(2);
        // Put the file's two extents in one child and its neighbour in the
        // other. The destination sorts before the source, the successor after.
        var root = Block(seed.Image, 5);
        U32(root, 24, 1000); Sum(root);
        var left = Block(seed.Image, 64); var right = Block(seed.Image, 65);
        right.Slice(16, 14).CopyTo(right.Slice(30, 14));
        left.Slice(30, 14).CopyTo(right.Slice(16, 14));
        left.Slice(30, 14).Clear(); U16(left, 12, 1); U16(right, 12, 2);
        Sum(left); Sum(right);
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true,
            ReturnScratchMetadataViews = scratch, CheckReadOnlyViews = true };
        var neighbor = GroupRead(ref disk, 4);
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeSharedTrstGroup(ref disk, GroupAddress, 9756));
        var expected = seed.Bytes.ToArray();
        for (var at = 0; at < expected.Length; at += 8192)
        {
            var payload = Enumerable.Repeat((byte)(0xD0 + at / 8192), 8192).ToArray();
            Assert.True(GroupWrite(ref disk, 3, (uint)at, payload));
            payload.CopyTo(expected, at);
        }
        var cold = new Disk(disk.Snapshot());
        Assert.Equal(seed.Bytes, GroupRead(ref cold, 3));
        Assert.Equal(expected, GroupRead(ref disk, 3));
        var work = TrstWork();
        Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var image = disk.Snapshot(); var reopened = new Disk(image);
        Assert.Equal(expected, GroupRead(ref reopened, 3));
        Assert.Equal(neighbor, GroupRead(ref reopened, 4));
        var records = PrefixRecords(image);
        Assert.Equal(2, records.Count);
        Assert.Contains((0u, 0x80000003u, 128), records.Values);
    }

    [Fact]
    public void ConsumingPredecessorKeepsSuccessorSearchableAcrossStaleSeparator()
    {
        var seed = PrefixSeed(2);
        var root = Block(seed.Image, 5);
        U32(root, 24, 6008); Sum(root);
        var left = Block(seed.Image, 64); var right = Block(seed.Image, 65);
        // Move the terminal record (6000..6031) to the left child. The
        // neighbour owns another block to the right of the stale separator.
        right.Slice(16, 14).CopyTo(left.Slice(44, 14));
        U16(left, 12, 3); U32(left, 20, 7000); Sum(left);
        U32(right, 16, 7000); U32(right, 20, 0); U32(right, 24, 24);
        U16(right, 28, 1); Sum(right);
        U32(Block(seed.Image, 8), 24 + 16, 2 * Bytes); Sum(Block(seed.Image, 8));
        Block(seed.Image, 7000).Fill(0x4A);
        var map = Block(seed.Image, 13); map[12 + 3000 / 8] &= 0x7F; Sum(map);
        var info = Block(seed.Image, 2);
        U32(info, Bytes - 28, BinaryPrimitives.ReadUInt32BigEndian(info[(Bytes - 28)..]) - 1);
        Sum(info);

        var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true };
        var neighbor = GroupRead(ref disk, 4);
        Assert.Equal(seed.Bytes, GroupRead(ref disk, 3));
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeSharedTrstGroup(ref disk, GroupAddress, 9756));
        var expected = seed.Bytes.ToArray();
        // Stop with the new tail key strictly beyond 6008. Finishing the
        // entire overwrite would remove that record and hide a routing bug.
        for (var at = 0; at < 112 * Bytes; at += 8192)
        {
            var payload = Enumerable.Repeat((byte)(0xD0 + at / 8192), 8192).ToArray();
            Assert.True(GroupWrite(ref disk, 3, (uint)at, payload));
            payload.CopyTo(expected, at);
        }
        var work = TrstWork();
        Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var image = disk.Snapshot(); var reopened = new Disk(image);
        Assert.Equal(expected, GroupRead(ref reopened, 3));
        Assert.Equal(neighbor, GroupRead(ref reopened, 4));
        PrefixRecords(image);
    }

    [Theory]
    [InlineData(0, false)] [InlineData(0, true)]
    [InlineData(1, false)] [InlineData(1, true)]
    [InlineData(2, false)] [InlineData(2, true)]
    [InlineData(3, false)] [InlineData(3, true)]
    public void TrstPrefixPreservesLogicalLinksAcrossLeavesAndInsertionSplits(int layout, bool scratch)
    {
        var seed = PrefixSeed(layout); var expected = seed.Bytes.ToArray();
        var before = new Disk(seed.Image); var neighbor = GroupRead(ref before, 4);
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratch };
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
        var payload = Enumerable.Repeat((byte)0xD4, 8192).ToArray();
        Assert.True(GroupWrite(ref disk, 3, 0, payload)); payload.CopyTo(expected, 0);
        Assert.Equal(expected, GroupRead(ref disk, 3)); Assert.Equal(neighbor, GroupRead(ref disk, 4));
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var final = disk.Snapshot(); var remount = new Disk(final);
        Assert.Equal(expected, GroupRead(ref remount, 3)); Assert.Equal(neighbor, GroupRead(ref remount, 4));
        var records = PrefixRecords(final);
        Assert.Equal((2016u, 0x80000003u, 16), records[1000]);
        Assert.Equal((6000u, 1000u, 80), records[2016]);
        Assert.Equal((0u, 2016u, 32), records[6000]);
        Assert.False(records.ContainsKey(2000));
        if (layout == 3) Assert.Equal((byte)0, Block(final, 5)[14]);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TrstPrefixNoncontiguousReplacementFailureRetainsPriorWritesAndAllowsRetry(bool scratch)
    {
        var seed = PrefixSeed(1, 1040, constrainReplacementRuns: true);
        for (var sector = 1; sector <= 16; sector++)
        {
            var expected = seed.Bytes.ToArray();
            var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratch };
            Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
            for (var i = 0; i < 2; i++)
            {
                var bytes = Enumerable.Repeat((byte)(0xB1 + i), 8192).ToArray();
                Assert.True(GroupWrite(ref disk, 3, (uint)i * 8192, bytes)); bytes.CopyTo(expected, i * 8192);
            }
            var active = disk.RedoOverlay; var size = (int)disk.ReadUInt32(GroupAddress, 12);
            var accepted = disk.Ram.AsSpan((int)active.Raw, size).ToArray();
            var payload = Enumerable.Repeat((byte)0xD5, 8192).ToArray(); disk.FailAtWrite = disk.Writes + sector;
            Assert.False(GroupWrite(ref disk, 3, 16384, payload));
            Assert.Equal(active, disk.RedoOverlay); Assert.Equal(accepted, disk.Ram.AsSpan((int)active.Raw, size).ToArray());
            disk.FailAtWrite = 0; Assert.Equal(expected, GroupRead(ref disk, 3));
            Assert.True(GroupWrite(ref disk, 3, 16384, payload)); payload.CopyTo(expected, 16384);
            var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
            var final = disk.Snapshot(); var remount = new Disk(final); Assert.Equal(expected, GroupRead(ref remount, 3));
            var records = PrefixRecords(final); var replacement = records[1000].Next;
            Assert.NotEqual(1032u, replacement);
            Assert.Equal((1088u, 1000u, 16), records[replacement]);
            Assert.Equal((6000u, replacement, 48), records[1088]);
            Assert.Equal((0u, 1088u, 32), records[6000]);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TrstPrefixMetadataFailureAfterPayloadPreservesAcceptedGroup(bool splitFailure)
    {
        var seed = PrefixSeed(splitFailure ? 3 : 1);
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true };
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
        Assert.True(GroupWrite(ref disk, 4, 0, "world"u8.ToArray()));
        var active = disk.RedoOverlay; var size = (int)disk.ReadUInt32(GroupAddress, 12);
        var accepted = disk.Ram.AsSpan((int)active.Raw, size).ToArray(); var writes = disk.Writes;
        disk.FailReadBlock = splitFailure ? 10u : 66u;
        var payload = Enumerable.Repeat((byte)0xD6, 8192).ToArray();
        Assert.False(GroupWrite(ref disk, 3, 0, payload));
        Assert.Equal(writes + 16, disk.Writes);
        Assert.Equal(active, disk.RedoOverlay); Assert.Equal(accepted, disk.Ram.AsSpan((int)active.Raw, size).ToArray());
        disk.FailReadBlock = uint.MaxValue;
        Assert.Equal(seed.Bytes, GroupRead(ref disk, 3)); Assert.Equal("world"u8.ToArray(), GroupRead(ref disk, 4).Take(5));
        Assert.True(GroupWrite(ref disk, 3, 0, payload)); var expected = seed.Bytes.ToArray(); payload.CopyTo(expected, 0);
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
        Assert.Equal("world"u8.ToArray(), GroupRead(ref remount, 4).Take(5));
    }

    [Theory]
    [InlineData(2048, false)] [InlineData(8192, false)]
    [InlineData(2048, true)] [InlineData(8192, true)]
    public void TrstPrefixCrossingAnInternalSeparatorRemainsSearchable(int firstBytes, bool afterInOtherLeaf)
    {
        var seed = PrefixSeed(2);
        // A separator may retain an old lower bound after deletion. A data
        // extent can span it even though its current key belongs to the left
        // leaf. Advancing the key past 2010 must move it to the right leaf.
        U32(Block(seed.Image, 5), 24, 2010); Sum(Block(seed.Image, 5));
        var expected = seed.Bytes.ToArray();
        if (!afterInOtherLeaf)
        {
            U32(Block(seed.Image, 64), 34, 0); Sum(Block(seed.Image, 64));
            var objectBlock = Block(seed.Image, 7); var at = 24;
            while (BinaryPrimitives.ReadUInt32BigEndian(objectBlock[(at + 4)..]) != 3)
            {
                var end = at + 25; while (objectBlock[end] != 0) end++;
                end++; while (objectBlock[end] != 0) end++; at = (end + 2) & ~1;
            }
            U32(objectBlock, at + 16, 96 * Bytes); Sum(objectBlock); expected = expected[..(96 * Bytes)];
        }
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true };
        Assert.Equal(expected, GroupRead(ref disk, 3));
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
        var first = Enumerable.Repeat((byte)0xC5, firstBytes).ToArray();
        Assert.True(GroupWrite(ref disk, 3, 0, first)); first.CopyTo(expected, 0);
        var second = Enumerable.Repeat((byte)0xC6, 8192).ToArray();
        Assert.True(GroupWrite(ref disk, 3, (uint)firstBytes, second)); second.CopyTo(expected, firstBytes);
        Assert.Equal(expected, GroupRead(ref disk, 3));
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var final = disk.Snapshot(); var remount = new Disk(final);
        Assert.Equal(expected, GroupRead(ref remount, 3)); PrefixRecords(final);
    }

    [Theory]
    [InlineData(1)] [InlineData(3)]
    public void TrstPrefixCrashCutsAndPendingSectorSubsetsRecoverWholeWrite(int layout)
    {
        var seed = PrefixSeed(layout); var expected = seed.Bytes.ToArray();
        var payload = Enumerable.Repeat((byte)0xD7, 1024).ToArray(); payload.CopyTo(expected, 0);
        var before = new Disk(seed.Image); var neighbor = GroupRead(ref before, 4);
        void Write(ref Disk disk)
        {
            Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
            Assert.True(GroupWrite(ref disk, 3, 0, payload));
            var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        }
        var baseline = new Disk(seed.Image.ToArray()) { Buffered = true }; Write(ref baseline);
        for (var cut = 1; cut <= baseline.IoEvents; cut++)
        {
            var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CutAfterIo = cut };
            Assert.Throws<PowerCut>(() => Write(ref disk));
            foreach (var image in disk.CrashImages())
            {
                var recovered = new Disk(image); var work = TrstWork();
                Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref recovered, ref work));
                var contents = GroupRead(ref recovered, 3);
                Assert.True(contents.SequenceEqual(seed.Bytes) || contents.SequenceEqual(expected), $"Torn prefix at cut {cut}");
                Assert.Equal(neighbor, GroupRead(ref recovered, 4)); PrefixRecords(recovered.Snapshot());
            }
        }
    }
}
