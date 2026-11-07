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
}
