using System.Buffers.Binary;
using System.Security.Cryptography;
using CopperSFS.Bench;
using Core = CopperSFS.AmigaSmartFileSystemWriteCore;
using static CopperSFS.Tests.ReliabilityFixture;

namespace CopperSFS.Tests;

public sealed partial class BatchBoundaryTests
{
    private static ReadOnlySpan<byte> FirstExtent(byte[] image, uint node)
    {
        var tree = Block(image, 5);
        Assert.Equal(1, tree[14]);
        var records = BinaryPrimitives.ReadUInt16BigEndian(tree[12..]);
        for (var i = 0; i < records; i++)
        {
            var record = tree.Slice(16 + i * 14, 14);
            if (U32(record, 8) == (0x80000000u | node)) return record;
        }
        throw new InvalidDataException("Missing first extent for reserved stream");
    }

    private static bool FreeInBitmap(byte[] image, int block)
    {
        const int bits = (Bytes - 12) * 8;
        var bit = block % bits;
        return (Block(image, 9 + block / bits)[12 + bit / 8] & (0x80 >> (bit % 8))) != 0;
    }

    // Adapted from ReservedOverwriteTests at research commit 4a902a1.
    [Fact]
    public void ReservedLayoutCrossesBitmapAndFinishesWithPartialBlock()
    {
        const int length = 1024 * 1024 + 123;
        var (image, node) = FragmentedRelocationSeed([64], 2049, 4 * 1024 * 1024);
        U32(Block(image, 7), 40, length);
        Seal(Block(image, 7));
        var before = StudyFixture.Inspect(image, Bytes);
        var data = StudyFixture.Pattern(length, 143);
        var disk = new StudyDisk(image, Bytes);
        var work = disk.Work;
        uint destination = 0;
        for (var position = 0; position < length; position += 65536)
        {
            var batch = data[position..Math.Min(position + 65536, length)];
            Assert.True(Write(ref disk, node, (uint)position, batch, out var actual, out var error), error.ToString());
            Assert.Equal((uint)batch.Length, actual);
            var extent = FirstExtent(disk.Snapshot(), node);
            if (position == 0) destination = U32(extent, 0);
            Assert.NotEqual(64u, destination);
            Assert.Equal(destination, U32(extent, 0));
            Assert.Equal((position + batch.Length + Bytes - 1) / Bytes, BinaryPrimitives.ReadUInt16BigEndian(extent[12..]));
            Assert.Equal(position + batch.Length < length, Core.HasOverwriteReservation(ref disk, ref work, node));
        }
        output.WriteLine($"Reserved destination {destination}..{destination + 2048}; final request {length % 65536} bytes.");
        var reopened = new StudyDisk(disk.Snapshot(), Bytes);
        FsOps.Recover(ref reopened);
        var saved = reopened.Snapshot();
        var final = FirstExtent(saved, node);
        const int bits = (Bytes - 12) * 8;
        Assert.True(destination / bits < (destination + 2048) / bits);
        Assert.Equal(destination, U32(final, 0));
        Assert.Equal(2049, BinaryPrimitives.ReadUInt16BigEndian(final[12..]));
        Assert.Equal(0u, U32(final, 4));
        var after = StudyFixture.Inspect(saved, Bytes);
        var file = after.Entries.Single(e => e.Node == node);
        Assert.Equal((ulong)length, file.Size);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)), file.Sha256);
        Assert.Equal(before.FreeBlocks, after.FreeBlocks);
        Assert.Equal(before.Entries.Where(e => e.Node != node), after.Entries.Where(e => e.Node != node));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReservedLayoutSkipsShortRunDuringCompetingAllocations(bool interleaved)
    {
        const int blocks = 512, batch = 65536;
        var (image, node) = FragmentedRelocationSeed([512], blocks, 2 * 1024 * 1024);
        var disk = new StudyDisk(image, Bytes, batchedWrites: true);
        var other = interleaved ? FsOps.Create(ref disk, "Other") : 0u;
        image = disk.Snapshot();
        // The free run just before Data fits a single batch but not the complete file.
        var hole = 0;
        while (hole < blocks && FreeInBitmap(image, 511 - hole)) hole++;
        Assert.InRange(hole, batch / Bytes, blocks - 1);
        var before = StudyFixture.Inspect(image, Bytes);
        var data = StudyFixture.Pattern(blocks * Bytes, 141);
        var competing = StudyFixture.Pattern(data.Length, 142);
        var work = disk.Work;
        uint start = 0;
        for (var position = 0; position < data.Length; position += batch)
        {
            Assert.True(Write(ref disk, node, (uint)position, data[position..(position + batch)], out var actual, out var error), error.ToString());
            Assert.Equal((uint)batch, actual);
            if (interleaved) FsOps.Write(ref disk, other, (uint)position, competing[position..(position + batch)]);
            var saved = disk.Snapshot();
            var extent = FirstExtent(saved, node);
            if (position == 0) start = U32(extent, 0);
            Assert.True(start >= 1024, "Reserved destination did not skip the short run and original extent");
            Assert.Equal(start, U32(extent, 0));
            var written = (position + batch) / Bytes;
            Assert.Equal(written, BinaryPrimitives.ReadUInt16BigEndian(extent[12..]));
            Assert.Equal(written < blocks, Core.HasOverwriteReservation(ref disk, ref work, node));
            for (var i = written; i < blocks; i++)
                Assert.True(FreeInBitmap(saved, checked((int)start + i)), "Unused reservation tail was allocated");
        }
        var reopened = new StudyDisk(disk.Snapshot(), Bytes);
        FsOps.Recover(ref reopened);
        output.WriteLine($"Interleaved={interleaved}: skipped {hole}-block run, reserved {start}..{start + blocks - 1}.");
        var result = reopened.Snapshot();
        var final = FirstExtent(result, node);
        Assert.Equal(start, U32(final, 0));
        Assert.Equal(blocks, BinaryPrimitives.ReadUInt16BigEndian(final[12..]));
        Assert.Equal(0u, U32(final, 4));
        var after = StudyFixture.Inspect(result, Bytes);
        Assert.Equal(before.FreeBlocks - (interleaved ? (uint)blocks : 0u), after.FreeBlocks);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)), after.Entries.Single(e => e.Node == node).Sha256);
        if (interleaved)
            Assert.Equal(Convert.ToHexString(SHA256.HashData(competing)), after.Entries.Single(e => e.Node == other).Sha256);
        Assert.Equal(before.Entries.Single(e => e.Path == "/Keep"), after.Entries.Single(e => e.Path == "/Keep"));
    }
}
