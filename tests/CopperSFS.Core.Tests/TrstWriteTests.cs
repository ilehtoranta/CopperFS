using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amiga;
using CopperSFS.Bench;
using Xunit.Abstractions;
using static CopperSFS.AmigaSmartFileSystemWriteCore;

namespace CopperSFS.Tests;

public sealed class TrstWriteTests(ITestOutputHelper output)
{
    internal static string Manifest(byte[] image, int bytes = 512)
    {
        var report = StudyFixture.Inspect(image, bytes);
        return JsonSerializer.Serialize(new { report.FreeBlocks, report.Entries });
    }

    private static (byte[] Image, uint Node) Seed(int bytes = 512, int length = 1027)
    {
        var disk = new StudyDisk(StudyFixture.Empty(bytes, Math.Max(bytes * 256, 512 * 1024)), bytes, batchedWrites: true);
        var node = FsOps.Create(ref disk, "Data");
        FsOps.Write(ref disk, node, 0, StudyFixture.Pattern(length, 500));
        var keep = FsOps.Create(ref disk, "Keep");
        FsOps.Write(ref disk, keep, 0, StudyFixture.Pattern(513, 501));
        return (disk.Snapshot(), node);
    }

    internal static bool Execute(ref StudyDisk disk, uint node, string scenario, out uint actual,
        out DOS.Error error, uint stagingBytes = 65536)
    {
        var bytes = disk.Geometry.BlockBytes;
        var work = disk.Work;
        Encoding.Latin1.GetBytes("Changed").CopyTo(disk.Ram, (int)disk.NameBuffer.Raw);
        var request = new Request { Node = node, Directory = 1, Name = disk.NameBuffer,
            Length = 7, Buffer = disk.Payload };
        var operation = scenario switch
        {
            "create" => TrstOperation.CreateFile, "mkdir" => TrstOperation.CreateDirectory,
            "remove" => TrstOperation.Remove, "rename" => TrstOperation.Rename,
            "shrink" or "grow" => TrstOperation.Resize, "truncate" => TrstOperation.Truncate,
            "comment" => TrstOperation.SetComment, "protect" => TrstOperation.SetProtection,
            "date" => TrstOperation.SetDate, _ => TrstOperation.Write
        };
        if (operation == TrstOperation.Write)
        {
            request.Position = scenario == "append" ? 1027u : scenario == "overwrite" ? 97u : 0u;
            request.Length = scenario == "large" ? 131072u : 1025u;
            var data = StudyFixture.Pattern((int)request.Length, 502);
            // Include every compression mode and an unchanged word in overwrite.
            if (scenario != "large") { data.AsSpan(4, 16).Clear(); data.AsSpan(32, 16).Fill(255); }
            data.CopyTo(disk.Ram, (int)disk.Payload.Raw);
        }
        else if (scenario == "shrink") request.Position = 37;
        else if (scenario == "grow") request.Position = 2049;
        else if (scenario == "protect") request.Position = 16 | 32;
        else if (scenario == "comment") Encoding.Latin1.GetBytes("comment").CopyTo(disk.Ram, (int)disk.Payload.Raw);
        else if (scenario == "date")
        {
            disk.WriteUInt32(request.Buffer, 0, 2); disk.WriteUInt32(request.Buffer, 4, 3); disk.WriteUInt32(request.Buffer, 8, 100);
        }
        var staging = scenario == "large" ? APTR.FromPointer(disk.Payload.Raw + 262144) : disk.BatchBuffer;
        var ok = ExecuteTrst(ref disk, ref work, ref request, operation, staging, stagingBytes, out actual);
        error = work.Error;
        return ok;
    }

    private static byte[] Recover(byte[] image, string before, string after, bool requireAfter = false)
    {
        var disk = new StudyDisk(image, 512, crashModel: true);
        FsOps.Recover(ref disk);
        var saved = disk.Snapshot(); var manifest = Manifest(saved);
        Assert.True(manifest == after || !requireAfter && manifest == before, "Mixed data/metadata or leaked allocation");
        FsOps.Recover(ref disk); Assert.Equal(saved, disk.Snapshot());
        return saved;
    }

    internal static uint Marker(byte[] image) => BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(104)) + 2;
    internal static bool Dirty(byte[] image) => BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan((int)Marker(image) * 512)) == 0x54524641;

    [Theory]
    [InlineData("create")]
    [InlineData("mkdir")]
    [InlineData("overwrite")]
    [InlineData("append")]
    [InlineData("shrink")]
    [InlineData("grow")]
    [InlineData("truncate")]
    [InlineData("remove")]
    [InlineData("rename")]
    [InlineData("comment")]
    [InlineData("protect")]
    [InlineData("date")]
    public void EveryCutAndIoFailurePreservesWholeTransaction(string scenario)
    {
        var (image, node) = Seed(); var before = Manifest(image);
        var baseline = new StudyDisk(image, 512, crashModel: true);
        Assert.True(Execute(ref baseline, node, scenario, out _, out var error), error.ToString());
        var after = Manifest(baseline.Snapshot());
        Assert.NotEqual(before, after);
        var cases = 0; byte[]? dirty = null;
        for (var cut = 1; cut <= baseline.Events; cut++)
        {
            var disk = new StudyDisk(image, 512, crashModel: true) { CutAfterEvent = cut };
            Assert.Throws<StudyPowerCut>(() => Execute(ref disk, node, scenario, out _, out _));
            foreach (var saved in disk.CrashImages())
            {
                var published = Dirty(saved);
                Recover(saved, before, after, published); cases++;
                if (published) dirty = saved;
            }
        }
        foreach (var persistent in new[] { false, true })
        {
            for (var point = 1; point <= baseline.Events; point++)
            {
                var disk = new StudyDisk(image, 512, crashModel: true) { FailAtEvent = point, PersistentFailure = persistent };
                Assert.False(Execute(ref disk, node, scenario, out var actual, out _)); Assert.Equal(0u, actual);
                foreach (var saved in disk.CrashImages()) { Recover(saved, before, after, Dirty(saved)); cases++; }
                // Same-process retry of recovery must see the pending writes too.
                disk.FailAtEvent = 0; FsOps.Recover(ref disk);
                // A clean marker from a failed final flush can still be pending;
                // another cold boot must recover the durable dirty marker too.
                Recover(disk.Snapshot(), before, after);
            }
            for (var point = 1; point <= baseline.Counts.BlockReads; point++)
            {
                var disk = new StudyDisk(image, 512, crashModel: true) { FailAtRead = point, PersistentFailure = persistent };
                var ok = Execute(ref disk, node, scenario, out var actual, out _);
                Assert.Equal(ok && (scenario is "overwrite" or "append") ? 1025u : 0u, actual);
                foreach (var saved in disk.CrashImages()) { Recover(saved, before, after, ok || Dirty(saved)); cases++; }
            }
        }
        Assert.NotNull(dirty);
        var recovery = new StudyDisk(dirty, 512, crashModel: true); FsOps.Recover(ref recovery);
        for (var cut = 1; cut <= recovery.Events; cut++)
        {
            var disk = new StudyDisk(dirty, 512, crashModel: true) { CutAfterEvent = cut };
            Assert.Throws<StudyPowerCut>(() => FsOps.Recover(ref disk));
            foreach (var saved in disk.CrashImages()) { Recover(saved, before, after, true); cases++; }
        }
        foreach (var persistent in new[] { false, true })
        {
            for (var point = 1; point <= recovery.Events; point++)
            {
                var disk = new StudyDisk(dirty, 512, crashModel: true) { FailAtEvent = point, PersistentFailure = persistent };
                var work = disk.Work;
                Assert.False(AmigaSmartFileSystemWriteCore.Recover(ref disk, ref work));
                foreach (var saved in disk.CrashImages()) { Recover(saved, before, after, true); cases++; }
            }
            for (var point = 1; point <= recovery.Counts.BlockReads; point++)
            {
                var disk = new StudyDisk(dirty, 512, crashModel: true) { FailAtRead = point, PersistentFailure = persistent };
                var work = disk.Work;
                var ok = AmigaSmartFileSystemWriteCore.Recover(ref disk, ref work);
                foreach (var saved in disk.CrashImages()) { Recover(saved, before, after, true); cases++; }
            }
        }
        output.WriteLine($"{scenario}: {baseline.Events} cut points; {cases} exhaustive crash/failure/recovery images.");
    }

    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(4096)]
    public void NativeCompressedWritesMatchCsUnAtEveryBlockSize(int bytes)
    {
        var (image, node) = Seed(bytes);
        var expected = new StudyDisk(image, bytes, batchedWrites: true);
        var data = StudyFixture.Pattern(1025, 502); data.AsSpan(4, 16).Clear(); data.AsSpan(32, 16).Fill(255);
        FsOps.Write(ref expected, node, 97, data);
        var disk = new StudyDisk(image, bytes, crashModel: true);
        Assert.True(Execute(ref disk, node, "overwrite", out var actual, out var error), error.ToString());
        Assert.Equal(1025u, actual); Assert.Equal(Manifest(expected.Snapshot(), bytes), Manifest(disk.Snapshot(), bytes));
        Assert.Equal(4, disk.Counts.Flushes);
    }

    [Fact]
    public void StagingExhaustionAndRejectedRequestsLeaveOriginalImageUntouched()
    {
        var (image, node) = Seed(); var disk = new StudyDisk(image, 512, crashModel: true);
        Assert.False(Execute(ref disk, node, "overwrite", out var actual, out var error, 516));
        Assert.Equal(DOS.Error.NoFreeStore, error); Assert.Equal(0u, actual);
        Assert.Equal(0, disk.Counts.BlockWrites); Assert.Equal(image, disk.Snapshot());
        // Prove staging mode cannot escape into a subsequent ordinary CSUN call.
        FsOps.Write(ref disk, node, 0, new byte[] { 1, 2, 3 }); Manifest(disk.Snapshot());
    }

    [Fact]
    public void LargeWriteRemainsOneTransactionAcrossAllJournalPages()
    {
        var (image, node) = Seed(length: 131072); var before = Manifest(image);
        var baseline = new StudyDisk(image, 512, crashModel: true);
        Assert.True(Execute(ref baseline, node, "large", out var actual, out _, 300000)); Assert.Equal(131072u, actual);
        var after = Manifest(baseline.Snapshot()); var cases = 0;
        for (var cut = 1; cut <= baseline.Events; cut++)
        {
            var disk = new StudyDisk(image, 512, crashModel: true) { CutAfterEvent = cut };
            Assert.Throws<StudyPowerCut>(() => Execute(ref disk, node, "large", out _, out _, 300000));
            // Large write: every event, bounded deterministic persistence samples.
            foreach (var saved in disk.SampledCrashImages()) { Recover(saved, before, after, Dirty(saved)); cases++; }
        }
        output.WriteLine($"128 KiB: every {baseline.Events} sector/flush cuts, {cases} sampled persistence images.");
    }

    [Fact]
    public void ExistingCsUnJournalIsRecoveredBeforeTrstStaging()
    {
        var (image, node) = Seed(); byte[]? dirty = null;
        for (var cut = 1; dirty is null; cut++)
        {
            var disk = new StudyDisk(image, 512, crashModel: true) { CutAfterEvent = cut };
            Assert.Throws<StudyPowerCut>(() => FsOps.Write(ref disk, node, 0, new byte[] { 9, 8, 7 }));
            var saved = disk.Snapshot(); var marker = (int)Marker(saved) * 512;
            var head = BinaryPrimitives.ReadUInt32BigEndian(saved.AsSpan(marker + 12));
            if (Dirty(saved) && head != 0) dirty = saved;
        }
        var recovered = new StudyDisk(dirty, 512, crashModel: true);
        Assert.True(Execute(ref recovered, node, "overwrite", out _, out _)); Manifest(recovered.Snapshot());
    }

    [Fact]
    public void PackedMetadataRedoReducesWritesWithoutWeakeningBarriers()
    {
        var (image, node) = Seed();
        var undo = new StudyDisk(image, 512, crashModel: true); var work = undo.Work;
        var request = new Request { Node = node, Position = 16 | 32 };
        Assert.True(SetMetadata(ref undo, ref work, ref request, DosPacketAction.SetProtection));
        var redo = new StudyDisk(image, 512, crashModel: true);
        Assert.True(Execute(ref redo, node, "protect", out _, out _));
        Assert.Equal(Manifest(undo.Snapshot()), Manifest(redo.Snapshot()));
        Assert.True(redo.Counts.BlockWrites < undo.Counts.BlockWrites);
        Assert.Equal(4, redo.Counts.Flushes);
        output.WriteLine($"Protection change: CSUN {undo.Counts.BlockWrites} writes/{undo.Counts.Flushes} flushes; TRST {redo.Counts.BlockWrites} writes/{redo.Counts.Flushes} flushes.");
    }

    [Fact]
    public void NoJournalSpaceCannotPublishEvenWhenTruncateWouldFreeBlocks()
    {
        var image = ReliabilityFixture.Image();
        // Model a fully allocated but otherwise traversable volume. The writer
        // must not put its redo journal in blocks this truncate proposes freeing.
        var bitmap = image.AsSpan(9 * 512, 512); bitmap[12..].Clear(); ReliabilityFixture.Seal(bitmap);
        var disk = new StudyDisk(image, 512, crashModel: true);
        Assert.False(Execute(ref disk, 3, "truncate", out var actual, out var error));
        Assert.Equal(DOS.Error.DiskFull, error); Assert.Equal(0u, actual);
        Assert.Equal(0, disk.Counts.BlockWrites); Assert.Equal(image, disk.Snapshot());
    }

    [Fact]
    public void InvalidBuffersAndEmptyWritesDoNotPublishTransactions()
    {
        var (image, node) = Seed(); var disk = new StudyDisk(image, 512, crashModel: true);
        var work = disk.Work; var request = new Request { Node = node, Buffer = disk.Payload, Length = 12 };
        Assert.False(ExecuteTrst(ref disk, ref work, ref request, TrstOperation.SetDate, disk.Payload, 516, out _));
        Assert.Equal(DOS.Error.NoFreeStore, work.Error);
        Assert.False(ExecuteTrst(ref disk, ref work, ref request, (TrstOperation)(-1), disk.BatchBuffer, 65536, out _));
        Assert.Equal(DOS.Error.BadNumber, work.Error);
        request.Length = 0;
        Assert.True(ExecuteTrst(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, 65536, out var actual));
        Assert.Equal(0u, actual); Assert.Equal(0, disk.Counts.BlockWrites); Assert.Equal(image, disk.Snapshot());
    }
}
