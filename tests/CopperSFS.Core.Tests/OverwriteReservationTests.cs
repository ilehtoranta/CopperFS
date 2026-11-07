using System.Security.Cryptography;
using CopperSFS.Bench;
using Core = CopperSFS.AmigaSmartFileSystemWriteCore;

namespace CopperSFS.Tests;

// Adapted from ReservedOverwriteTests in CopperSFS research commit 4a902a1.
public sealed class OverwriteReservationTests
{
    private const int Bytes = 512, Chunk = 65536;
    private static StudyDisk Volume() => new(StudyFixture.Empty(Bytes, 4 * 1024 * 1024), Bytes, batchedWrites: true);

    private static bool Reserved(ref StudyDisk disk, uint node)
    {
        var work = disk.Work;
        return Core.HasOverwriteReservation(ref disk, ref work, node);
    }

    private static StudyDisk Reopen(ref StudyDisk disk)
    {
        var reopened = new StudyDisk(disk.Snapshot(), Bytes, batchedWrites: true);
        FsOps.Recover(ref reopened);
        return reopened;
    }

    [Theory]
    [InlineData("close")]
    [InlineData("discontinuous write")]
    [InlineData("resize")]
    [InlineData("truncate")]
    [InlineData("remove")]
    [InlineData("scalar write")]
    [InlineData("enter TRST")]
    public void ReleasePreservesAllocationAndContentsAfterReopen(string operation)
    {
        var disk = Volume();
        var node = FsOps.Create(ref disk, "File");
        var expected = StudyFixture.Pattern(2 * Chunk, 148);
        FsOps.Write(ref disk, node, 0, expected);
        var free = StudyFixture.Inspect(disk.Snapshot(), Bytes).FreeBlocks;
        var prefix = StudyFixture.Pattern(Chunk, 149);
        FsOps.Write(ref disk, node, 0, prefix);
        prefix.CopyTo(expected, 0);
        Assert.True(Reserved(ref disk, node));
        Assert.Equal(free, StudyFixture.Inspect(disk.Snapshot(), Bytes).FreeBlocks);
        var work = disk.Work;
        switch (operation)
        {
            case "close":
                Core.ReleaseOverwriteReservation(ref disk, ref work, node);
                break;
            case "discontinuous write":
            case "scalar write":
                disk.BatchedWrites = operation != "scalar write";
                var offset = disk.BatchedWrites ? 8192 : Chunk;
                var patch = StudyFixture.Pattern(Bytes, 145);
                FsOps.Write(ref disk, node, (uint)offset, patch);
                patch.CopyTo(expected, offset);
                break;
            case "resize":
                FsOps.Resize(ref disk, node, Chunk);
                expected = expected[..Chunk];
                free += Chunk / Bytes;
                break;
            case "truncate":
                Assert.True(Core.Truncate(ref disk, ref work, node), work.Error.ToString());
                expected = [];
                free += 2 * Chunk / Bytes;
                break;
            case "remove":
                FsOps.Remove(ref disk, node);
                free += 2 * Chunk / Bytes;
                break;
            case "enter TRST":
                "Other"u8.CopyTo(disk.Ram.AsSpan((int)disk.NameBuffer.Raw));
                var request = new Core.Request { Directory = 1, Name = disk.NameBuffer, Length = 5 };
                Assert.True(Core.ExecuteTrst(ref disk, ref work, ref request, Core.TrstOperation.CreateFile,
                    disk.BatchBuffer, disk.BatchBytes, out _), work.Error.ToString());
                break;
            default: throw new ArgumentOutOfRangeException(nameof(operation));
        }
        Assert.False(Reserved(ref disk, node));
        disk = Reopen(ref disk);
        Assert.False(Reserved(ref disk, node));
        var report = StudyFixture.Inspect(disk.Snapshot(), Bytes);
        Assert.Equal(free, report.FreeBlocks);
        if (operation == "remove") Assert.DoesNotContain(report.Entries, e => e.Path == "/File");
        else Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), report.Entries.Single(e => e.Path == "/File").Sha256);
        if (operation == "enter TRST") Assert.Contains(report.Entries, e => e.Path == "/Other");
    }

    [Fact]
    public void FullTableFallsBackAndCompetingAllocationReclaimsReservations()
    {
        var disk = Volume();
        // The mount workspace documents eight simultaneous reserved streams.
        var nodes = new uint[9];
        var original = StudyFixture.Pattern(2 * Chunk, 148);
        var replacement = StudyFixture.Pattern(Chunk, 149);
        for (var i = 0; i < nodes.Length; i++)
        {
            nodes[i] = FsOps.Create(ref disk, $"File{i}");
            FsOps.Write(ref disk, nodes[i], 0, original);
        }
        var pressure = FsOps.Create(ref disk, "Pressure");
        var free = StudyFixture.Inspect(disk.Snapshot(), Bytes).FreeBlocks;
        foreach (var node in nodes) FsOps.Write(ref disk, node, 0, replacement);
        for (var i = 0; i < nodes.Length; i++) Assert.Equal(i < nodes.Length - 1, Reserved(ref disk, nodes[i]));
        Assert.Equal(free, StudyFixture.Inspect(disk.Snapshot(), Bytes).FreeBlocks);

        uint position = 0;
        using var pressureHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (Reserved(ref disk, nodes[0]))
        {
            Assert.True(position / Bytes + Chunk / Bytes <= free, "Reservation survived until the disk was full");
            FsOps.Write(ref disk, pressure, position, replacement);
            position += Chunk;
            pressureHash.AppendData(replacement);
        }
        foreach (var node in nodes)
        {
            FsOps.Write(ref disk, node, Chunk, replacement);
            Assert.False(Reserved(ref disk, node));
        }
        disk = Reopen(ref disk);
        var report = StudyFixture.Inspect(disk.Snapshot(), Bytes);
        Assert.Equal(free - position / Bytes, report.FreeBlocks);
        var hash = Convert.ToHexString(SHA256.HashData([.. replacement, .. replacement]));
        for (var i = 0; i < nodes.Length; i++)
            Assert.Equal(hash, report.Entries.Single(e => e.Path == $"/File{i}").Sha256);
        Assert.Equal(Convert.ToHexString(pressureHash.GetHashAndReset()), report.Entries.Single(e => e.Path == "/Pressure").Sha256);
    }
}
