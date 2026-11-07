using System.Buffers.Binary;
using System.Security.Cryptography;
using Amiga;
using CopperSFS.Bench;
using Xunit.Abstractions;
using static CopperSFS.Tests.ReliabilityFixture;

namespace CopperSFS.Tests;

public sealed class BatchBoundaryTests(ITestOutputHelper output)
{
    // Adapted from ExpandedImage in research commit 4a902a1. Start with a real
    // one-block file and expand its extent, allocation bitmap and object together.
    private static (byte[] Image, uint Node, int First) Seed(int blocks, int capacity)
    {
        var disk = new StudyDisk(StudyFixture.Empty(Bytes, capacity), Bytes, batchedWrites: true);
        var node = FsOps.Create(ref disk, "Data");
        FsOps.Write(ref disk, node, 0, new byte[Bytes]);
        var image = disk.Snapshot();
        var free = StudyFixture.Inspect(image, Bytes).FreeBlocks;
        var tree = Block(image, 5);
        var first = checked((int)U32(tree, 16));
        image.AsSpan(first * Bytes, blocks * Bytes).Clear();
        const int bitsPerMap = (Bytes - 12) * 8;
        for (var block = first; block < first + blocks; block++)
            Block(image, 9 + block / bitsPerMap)[12 + block % bitsPerMap / 8] &= (byte)~(0x80 >> (block % 8));
        for (var map = 0; map * bitsPerMap < capacity / Bytes; map++) Seal(Block(image, 9 + map));
        U16(tree, 28, checked((ushort)blocks));
        U32(Block(image, 7), 40, (uint)(blocks * Bytes));
        U32(Block(image, 2), Bytes - 28, free - (uint)blocks + 1);
        foreach (var number in new[] { 2, 5, 7 }) Seal(Block(image, number));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[blocks * Bytes])),
            StudyFixture.Inspect(image, Bytes).Entries.Single(e => e.Path == "/Data").Sha256);
        return (image, node, first);
    }

    private static bool Write(ref StudyDisk disk, uint node, uint position, byte[] data, out uint actual, out DOS.Error error)
    {
        data.CopyTo(disk.Ram, (int)disk.Payload.Raw);
        var work = disk.Work;
        var request = new AmigaSmartFileSystemWriteCore.Request
        { Node = node, Position = position, Length = (uint)data.Length, Buffer = disk.Payload };
        var ok = AmigaSmartFileSystemWriteCore.WriteBatched(ref disk, ref work, ref request, disk.BatchBuffer, disk.BatchBytes, out actual);
        error = work.Error;
        return ok;
    }

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public void AppendLinksANewExtentAfterMaximumLength(int appendBlocks)
    {
        const int blocks = ushort.MaxValue;
        var (image, node, first) = Seed(blocks, (blocks + 1024) * Bytes);
        var disk = new StudyDisk(image, Bytes);
        var data = StudyFixture.Pattern(appendBlocks * Bytes, 76);
        Assert.True(Write(ref disk, node, blocks * Bytes, data, out var actual, out var error), error.ToString());
        Assert.Equal((uint)data.Length, actual);
        var reopened = new StudyDisk(disk.Snapshot(), Bytes);
        FsOps.Recover(ref reopened);
        var saved = reopened.Snapshot();
        var tree = Block(saved, 5);
        Assert.Equal(ushort.MaxValue, BinaryPrimitives.ReadUInt16BigEndian(tree[28..]));
        Assert.Equal((uint)first, U32(tree, 16));
        Assert.Equal(U32(tree, 30), U32(tree, 20));
        Assert.NotEqual(0u, U32(tree, 20));
        Assert.Equal((uint)first, U32(tree, 38));
        var expected = new byte[(blocks + appendBlocks) * Bytes];
        data.CopyTo(expected, blocks * Bytes);
        var file = StudyFixture.Inspect(saved, Bytes).Entries.Single(e => e.Path == "/Data");
        Assert.Equal((ulong)expected.Length, file.Size);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), file.Sha256);
    }

    [Fact]
    public void DiskFullAfterFirstOverwriteBatchRollsBackWholeRequest()
    {
        var (image, node, first) = Seed(218, 128 * 1024);
        var before = StudyFixture.Inspect(image, Bytes);
        Assert.Equal(4u, before.FreeBlocks);
        var data = StudyFixture.Pattern(4096, 77);
        var probe = new StudyDisk(image, Bytes, crashModel: true) { BatchBytes = 2 * Bytes };
        Assert.False(Write(ref probe, node, 0, data, out var actual, out var error));
        Assert.Equal(0u, actual);
        Assert.Equal(DOS.Error.DiskFull, error);
        var firstBatchApplied = false;
        var cases = 0;
        void Verify(byte[] crash)
        {
            if (crash.AsSpan(first * Bytes, 2 * Bytes).SequenceEqual(data.AsSpan(0, 2 * Bytes)))
                firstBatchApplied = true;
            var recovered = new StudyDisk(crash, Bytes, crashModel: true);
            FsOps.Recover(ref recovered);
            var saved = recovered.Snapshot();
            var report = StudyFixture.Inspect(saved, Bytes);
            Assert.Equal(before.FreeBlocks, report.FreeBlocks);
            Assert.Equal(before.Entries.ToArray(), report.Entries.ToArray());
            FsOps.Recover(ref recovered);
            Assert.True(saved.AsSpan().SequenceEqual(recovered.Snapshot()));
            cases++;
        }
        foreach (var saved in probe.CrashImages()) Verify(saved);
        for (var cut = 1; cut <= probe.Events; cut++)
        {
            var disk = new StudyDisk(image, Bytes, crashModel: true) { BatchBytes = 2 * Bytes, CutAfterEvent = cut };
            Assert.Throws<StudyPowerCut>(() => Write(ref disk, node, 0, data, out _, out _));
            foreach (var saved in disk.CrashImages()) Verify(saved);
        }
        Assert.True(firstBatchApplied, "No crash image contained the complete first overwrite batch.");
        output.WriteLine($"{probe.Events} write/flush cuts, {cases} recovered images.");
    }

    private static (byte[] Image, uint Node) FragmentedRelocationSeed(int[] starts, ushort length, int capacity)
    {
        var disk = new StudyDisk(StudyFixture.Empty(Bytes, capacity), Bytes);
        var node = FsOps.Create(ref disk, "Data");
        var keep = FsOps.Create(ref disk, "Keep");
        FsOps.Write(ref disk, keep, 0, StudyFixture.Pattern(513, 110));
        var image = disk.Snapshot();
        var tree = Block(image, 5);
        var existing = BinaryPrimitives.ReadUInt16BigEndian(tree[12..]);
        var contents = StudyFixture.Pattern(starts.Length * length * Bytes, 111);
        U16(tree, 12, checked((ushort)(existing + starts.Length)));
        for (var i = 0; i < starts.Length; i++)
        {
            var entry = tree[(16 + (existing + i) * 14)..];
            U32(entry, 0, (uint)starts[i]);
            U32(entry, 4, i + 1 < starts.Length ? (uint)starts[i + 1] : 0);
            U32(entry, 8, i == 0 ? node | 0x80000000 : (uint)starts[i - 1]);
            U16(entry, 12, length);
            contents.AsSpan(i * length * Bytes, length * Bytes).CopyTo(image.AsSpan(starts[i] * Bytes));
            for (var b = starts[i]; b < starts[i] + length; b++)
            {
                var mask = (byte)(0x80 >> (b % 8));
                ref var bitmapByte = ref Block(image, 9)[12 + b / 8];
                Assert.NotEqual(0, bitmapByte & mask);
                bitmapByte &= (byte)~mask;
            }
        }
        var container = Block(image, 7);
        Assert.Equal(node, U32(container, 28));
        U32(container, 36, (uint)starts[0]);
        U32(container, 40, (uint)contents.Length);
        var root = Block(image, 2);
        U32(root, Bytes - 28, U32(root, Bytes - 28) - (uint)(starts.Length * length));
        foreach (var block in new[] { 2, 5, 7, 9 }) Seal(Block(image, block));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(contents)),
            StudyFixture.Inspect(image, Bytes).Entries.Single(e => e.Path == "/Data").Sha256);
        return (image, node);
    }

    [Fact]
    public void DiskFullAfterRelocationRestoresOriginalExtentOwnershipAndData()
    {
        var (image, node) = FragmentedRelocationSeed([64, 144, 224, 304], 64, 256 * 1024);
        var before = StudyFixture.Inspect(image, Bytes);
        var replacement = StudyFixture.Pattern(128 * 1024, 119);
        var probe = new StudyDisk(image, Bytes, crashModel: true);
        Assert.False(Write(ref probe, node, 0, replacement, out var actual, out var error));
        Assert.Equal(DOS.Error.DiskFull, error);
        Assert.Equal(0u, actual);

        bool HasMovedPrefix(byte[] snapshot)
        {
            var tree = Block(snapshot, 5);
            var count = BinaryPrimitives.ReadUInt16BigEndian(tree[12..]);
            for (var i = 0; i < count; i++)
            {
                var entry = tree[(16 + i * 14)..];
                var start = checked((int)U32(entry, 0));
                if (U32(entry, 8) == (node | 0x80000000) && start != 64 && U32(entry, 4) == 224 &&
                    BinaryPrimitives.ReadUInt16BigEndian(entry[12..]) == 128 &&
                    snapshot.AsSpan(start * Bytes, 128 * Bytes).SequenceEqual(replacement.AsSpan(0, 128 * Bytes)))
                    return true;
            }
            return false;
        }

        var cases = 0;
        void VerifyRecovery(byte[] snapshot)
        {
            var reopened = new StudyDisk(snapshot, Bytes, crashModel: true);
            FsOps.Recover(ref reopened);
            var saved = reopened.Snapshot();
            var report = StudyFixture.Inspect(saved, Bytes);
            Assert.Equal(before.Entries.ToArray(), report.Entries.ToArray());
            Assert.Equal(before.FreeBlocks, report.FreeBlocks);
            foreach (var block in new[] { 5, 7, 9 }) // extent records, object heads, allocation bitmap
                Assert.True(Block(image, block).SequenceEqual(Block(saved, block)), $"Block {block} changed");
            FsOps.Recover(ref reopened);
            Assert.True(saved.AsSpan().SequenceEqual(reopened.Snapshot()));
            cases++;
        }

        foreach (var saved in probe.SampledCrashImages()) VerifyRecovery(saved);
        var relocationCut = 0;
        for (var cut = 1; cut <= probe.Events; cut++)
        {
            var disk = new StudyDisk(image, Bytes, crashModel: true) { CutAfterEvent = cut };
            Assert.Throws<StudyPowerCut>(() => Write(ref disk, node, 0, replacement, out _, out _));
            // Sample 1 includes every pending write, the device view at this cut.
            var sample = 0;
            foreach (var saved in disk.SampledCrashImages())
            {
                if (relocationCut == 0 && sample == 1 && HasMovedPrefix(saved)) relocationCut = cut;
                VerifyRecovery(saved);
                sample++;
            }
        }
        Assert.InRange(relocationCut, 1, probe.Events - 1);
        output.WriteLine($"Relocated prefix at cut {relocationCut}; {probe.Events} cuts, {cases} sampled recoveries.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndexedExtentReplacementSurvivesCutsAndWriteFailures(bool alreadyIndexed)
    {
        // Research commit 4a902a1: leave an unaligned prefix and suffix around the replacement.
        var starts = Enumerable.Range(0, 33).Select(i => 64 + i * 32).ToArray();
        var (image, node) = FragmentedRelocationSeed(starts, 16, 1024 * 1024);
        Assert.Equal(1, Block(image, 5)[14]);
        Assert.Equal(35, BinaryPrimitives.ReadUInt16BigEndian(Block(image, 5)[12..]));
        if (alreadyIndexed)
        {
            var seed = new StudyDisk(image, Bytes);
            var extra = FsOps.Create(ref seed, "Extra");
            FsOps.Write(ref seed, extra, 0, StudyFixture.Pattern(Bytes, 120));
            image = seed.Snapshot();
            Assert.Equal(0, Block(image, 5)[14]);
        }

        var before = StudyFixture.Inspect(image, Bytes);
        var replacement = StudyFixture.Pattern(65536, 122);
        var expected = StudyFixture.Pattern(starts.Length * 16 * Bytes, 111);
        replacement.CopyTo(expected, 13);
        var probe = new StudyDisk(image, Bytes, crashModel: true);
        Assert.True(Write(ref probe, node, 13, replacement, out var actual, out var error), error.ToString());
        Assert.Equal((uint)replacement.Length, actual);
        var committed = probe.Snapshot();
        var after = StudyFixture.Inspect(committed, Bytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), after.Entries.Single(e => e.Node == node).Sha256);
        Assert.Equal(before.Entries.Where(e => e.Node != node), after.Entries.Where(e => e.Node != node));
        Assert.Equal(0, Block(committed, 5)[14]);
        // The original first block now ends a one-block extent. Its successor
        // supplies logical block one from a different physical address.
        var root = Block(committed, 5);
        var leaf = Block(committed, checked((int)U32(root, 20)));
        var count = BinaryPrimitives.ReadUInt16BigEndian(leaf[12..]);
        var offset = 16;
        var end = offset + count * 14;
        while (offset < end && U32(leaf, offset) != starts[0]) offset += 14;
        Assert.True(offset < end, "The original first extent disappeared.");
        var first = leaf[offset..];
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(first[12..]));
        Assert.NotEqual((uint)starts[0] + 1, U32(first, 4));
        Assert.NotEqual(0u, U32(first, 4));

        var cases = 0;
        void Verify(StudyDisk disk)
        {
            foreach (var snapshot in disk.SampledCrashImages())
            {
                var reopened = new StudyDisk(snapshot, Bytes, crashModel: true);
                FsOps.Recover(ref reopened);
                var stable = reopened.Snapshot();
                var report = StudyFixture.Inspect(stable, Bytes);
                var expectedReport = report.Entries.Single(e => e.Node == node).Sha256 ==
                    after.Entries.Single(e => e.Node == node).Sha256 ? after : before;
                Assert.Equal(expectedReport.FreeBlocks, report.FreeBlocks);
                Assert.Equal(expectedReport.Entries.ToArray(), report.Entries.ToArray());
                FsOps.Recover(ref reopened);
                Assert.True(stable.AsSpan().SequenceEqual(reopened.Snapshot()));
                cases++;
            }
        }

        for (var cut = 1; cut <= probe.Events; cut++)
        {
            var disk = new StudyDisk(image, Bytes, crashModel: true) { CutAfterEvent = cut };
            Assert.Throws<StudyPowerCut>(() => Write(ref disk, node, 13, replacement, out _, out _));
            Verify(disk);
        }
        foreach (var persistent in new[] { false, true })
        for (var fail = 1; fail <= probe.Events; fail++)
        {
            var disk = new StudyDisk(image, Bytes, crashModel: true) { FailAtEvent = fail, PersistentFailure = persistent };
            Assert.False(Write(ref disk, node, 13, replacement, out actual, out _));
            Assert.Equal(0u, actual);
            Verify(disk);
        }
        output.WriteLine($"{probe.Events} write/flush points, {cases} sampled recoveries.");
    }
}
