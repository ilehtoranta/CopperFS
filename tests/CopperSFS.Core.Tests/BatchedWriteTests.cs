using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Amiga;
using CopperSFS.Bench;
using Xunit.Abstractions;

namespace CopperSFS.Tests;

public sealed class BatchedWriteTests(ITestOutputHelper output)
{
    private const int Bytes = 512;
    private static (byte[] Image, uint Node, byte[] Data) Seed(int length, int capacity = 128 * 1024)
    {
        var disk = new StudyDisk(StudyFixture.Empty(Bytes, capacity), Bytes, batchedWrites: true);
        var node = FsOps.Create(ref disk, "Data");
        var data = StudyFixture.Pattern(length, 71);
        FsOps.Write(ref disk, node, 0, data);
        var keep = FsOps.Create(ref disk, "Keep");
        FsOps.Write(ref disk, keep, 0, StudyFixture.Pattern(513, 72));
        var work = disk.Work;
        var request = new AmigaSmartFileSystemWriteCore.Request { Node = node, Position = 16 | 32 | 64 };
        Assert.True(AmigaSmartFileSystemWriteCore.SetMetadata(ref disk, ref work, ref request, DosPacketAction.SetProtection));
        StudyFixture.Inspect(disk.Snapshot(), Bytes);
        return (disk.Snapshot(), node, data);
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
    private static string Manifest(byte[] image)
    {
        var report = StudyFixture.Inspect(image, Bytes);
        return JsonSerializer.Serialize(new { report.FreeBlocks, report.Entries });
    }
    private static void CheckFile(byte[] image, byte[] expected)
    {
        var report = StudyFixture.Inspect(image, Bytes);
        var file = report.Entries.Single(e => e.Path == "/Data");
        Assert.Equal((uint)expected.Length, file.Size);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), file.Sha256);
    }
    private static void Recovered(byte[] image, string before, string after, string context)
    {
        try
        {
            var disk = new StudyDisk(image, Bytes, crashModel: true);
            FsOps.Recover(ref disk); // Existing CSUN recovery, no batched recovery API.
            var saved = disk.Snapshot(); var manifest = Manifest(saved);
            Assert.True(manifest == before || manifest == after, "Mixed manifest or leaked allocation");
            FsOps.Recover(ref disk);
            Assert.True(saved.AsSpan().SequenceEqual(disk.Snapshot()), "Recovery was not idempotent");
        }
        catch (Exception error) { throw new InvalidOperationException(context, error); }
    }
    private static void ExpectCut(ref StudyDisk disk, DiskAction action)
    {
        try { action(ref disk); }
        catch (StudyPowerCut) { return; }
        Assert.Fail("Power cut was not reached");
    }

    [Theory]
    [InlineData(0, 1536)]
    [InlineData(97, 1537)]
    [InlineData(4099, 1537)]
    public void SmallBatchesSurviveEveryCutAndIoFailure(int position, int length)
    {
        var (image, node, original) = Seed(4099);
        var data = StudyFixture.Pattern(length, 73);
        var expected = original.ToArray(); Array.Resize(ref expected, Math.Max(original.Length, position + length));
        data.CopyTo(expected, position);
        var before = Manifest(image);
        var probe = new StudyDisk(image, Bytes, crashModel: true) { BatchBytes = 1024 };
        Assert.True(Write(ref probe, node, (uint)position, data, out var actual, out _));
        Assert.Equal((uint)length, actual);
        CheckFile(probe.Snapshot(), expected);
        Assert.Equal(15u | 32u | 64u, StudyFixture.Inspect(probe.Snapshot(), Bytes).Entries.Single(e => e.Node == node).Protection);
        var after = Manifest(probe.Snapshot());
        var cases = 0; byte[]? dirty = null;
        for (var cut = 1; cut <= probe.Events; cut++)
        {
            var disk = new StudyDisk(image, Bytes, crashModel: true) { BatchBytes = 1024, CutAfterEvent = cut };
            ExpectCut(ref disk, (ref StudyDisk d) => Write(ref d, node, (uint)position, data, out _, out _));
            foreach (var saved in disk.CrashImages())
            {
                Recovered(saved, before, after, $"cut {cut}"); cases++;
                if (StudyFixture.U32(saved, 4 * Bytes) == 0x54524641 && StudyFixture.U32(saved, 4 * Bytes + 12) != 0)
                    dirty = saved;
            }
        }
        foreach (var persistent in new[] { false, true })
        {
            for (var fail = 1; fail <= probe.Events; fail++)
            {
                var disk = new StudyDisk(image, Bytes, crashModel: true)
                { BatchBytes = 1024, FailAtEvent = fail, PersistentFailure = persistent };
                Assert.False(Write(ref disk, node, (uint)position, data, out actual, out _));
                Assert.Equal(0u, actual);
                foreach (var saved in disk.CrashImages()) { Recovered(saved, before, after, $"failure {fail}"); cases++; }
            }
            for (var fail = 1; fail <= probe.Counts.BlockReads; fail++)
            {
                var disk = new StudyDisk(image, Bytes, crashModel: true)
                { BatchBytes = 1024, FailAtRead = fail, PersistentFailure = persistent };
                var ok = Write(ref disk, node, (uint)position, data, out actual, out _);
                Assert.Equal(ok ? (uint)length : 0u, actual);
                foreach (var saved in disk.CrashImages()) { Recovered(saved, ok ? after : before, after, $"read failure {fail}"); cases++; }
            }
        }
        Assert.NotNull(dirty);
        var recovery = new StudyDisk(dirty, Bytes, crashModel: true);
        FsOps.Recover(ref recovery);
        var recoveryCases = 0;
        for (var cut = 1; cut <= recovery.Events; cut++)
        {
            var disk = new StudyDisk(dirty, Bytes, crashModel: true) { CutAfterEvent = cut };
            ExpectCut(ref disk, FsOps.Recover);
            foreach (var saved in disk.CrashImages()) { Recovered(saved, before, after, $"recovery cut {cut}"); recoveryCases++; }
        }
        output.WriteLine($"Batched exhaustive: {cases} mutation/failure images, {recoveryCases} recovery images; {probe.Events} cut points.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullSizeTransfersAndMultipleBatchesAreNotAssumedAtomic(bool append)
    {
        var (image, node, original) = Seed(append ? 0 : 131072, 512 * 1024);
        var data = StudyFixture.Pattern(131072, 74);
        var before = Manifest(image);
        var probe = new StudyDisk(image, Bytes, crashModel: true);
        Assert.True(Write(ref probe, node, 0, data, out var actual, out _));
        Assert.Equal((uint)data.Length, actual);
        Assert.Equal(128u, probe.Counts.MaxRangeBlocks);
        CheckFile(probe.Snapshot(), data);
        var after = Manifest(probe.Snapshot()); var cases = 0; byte[]? dirty = null;
        for (var cut = 1; cut <= probe.Events; cut++)
        {
            var disk = new StudyDisk(image, Bytes, crashModel: true) { CutAfterEvent = cut };
            ExpectCut(ref disk, (ref StudyDisk d) => Write(ref d, node, 0, data, out _, out _));
            foreach (var saved in disk.SampledCrashImages())
            {
                Recovered(saved, before, after, $"full-size cut {cut}"); cases++;
                if (StudyFixture.U32(saved, 4 * Bytes) == 0x54524641 && StudyFixture.U32(saved, 4 * Bytes + 12) != 0)
                    dirty = saved;
            }
        }
        Assert.NotNull(dirty);
        var recovery = new StudyDisk(dirty, Bytes, crashModel: true);
        FsOps.Recover(ref recovery);
        var recoveryCases = 0;
        for (var cut = 1; cut <= recovery.Events; cut++)
        {
            var disk = new StudyDisk(dirty, Bytes, crashModel: true) { CutAfterEvent = cut };
            ExpectCut(ref disk, FsOps.Recover);
            foreach (var saved in disk.SampledCrashImages()) { Recovered(saved, before, after, $"full-size recovery {cut}"); recoveryCases++; }
        }
        output.WriteLine($"Batched sampled: {cases} mutation images, {recoveryCases} recovery images; every one of {probe.Events} sector/flush cut points, ten persistence samples each.");
    }

    [Theory]
    [InlineData(512u, false)]
    [InlineData(1536u, false)]
    [InlineData(65536u, false)]
    [InlineData(65536u, true)]
    public void TransfersRespectDeviceLimitsAndScalarAdapters(uint maximum, bool scalar)
    {
        var (image, node, _) = Seed(65539, 512 * 1024);
        var disk = new StudyDisk(image, Bytes, maxTransfer: maximum) { ScalarRanges = scalar };
        var data = StudyFixture.Pattern(65539, 75);
        Assert.True(Write(ref disk, node, 0, data, out var actual, out _));
        Assert.Equal((uint)data.Length, actual);
        Assert.InRange(disk.Counts.MaxRangeBlocks, 1u, maximum / Bytes);
        CheckFile(disk.Snapshot(), data);
    }

    [Fact]
    public void EmptyWritePreservesArchiveFlagAndDoesNotWrite()
    {
        var (image, node, _) = Seed(4099);
        var disk = new StudyDisk(image, Bytes);
        Assert.True(Write(ref disk, node, 0, [], out var actual, out _));
        Assert.Equal(0u, actual); Assert.Equal(0, disk.Events);
        Assert.True(image.AsSpan().SequenceEqual(disk.Snapshot()));
        Assert.Equal(15u | 16u | 32u | 64u, StudyFixture.Inspect(disk.Snapshot(), Bytes).Entries.Single(e => e.Node == node).Protection);
    }

    [Fact]
    public void InvalidScratchIsRejectedBeforeWritingAndNoScratchUsesScalarPath()
    {
        var (image, node, _) = Seed(1536);
        var disk = new StudyDisk(image, Bytes);
        var work = disk.Work;
        var request = new AmigaSmartFileSystemWriteCore.Request { Node = node, Buffer = disk.Payload, Length = 1024 };
        foreach (var buffer in new[] { disk.Payload, work.Meta, work.Data, work.Bitmap, work.Journal, APTR.FromPointer(uint.MaxValue - 256) })
        {
            Assert.False(AmigaSmartFileSystemWriteCore.WriteBatched(ref disk, ref work, ref request, buffer, 1024, out var actual));
            Assert.Equal(0u, actual); Assert.Equal(DOS.Error.NoFreeStore, work.Error); Assert.Equal(0, disk.Events);
        }
        Assert.True(AmigaSmartFileSystemWriteCore.WriteBatched(ref disk, ref work, ref request, default, 0, out var complete));
        Assert.Equal(1024u, complete); Assert.Equal(0, disk.Counts.RangeWrites);
        StudyFixture.Inspect(disk.Snapshot(), Bytes);
    }

    // Extend a single-file seed directly to reach format boundaries without
    // thousands of setup transactions. Data is all zero, metadata is sealed and
    // independently checked before it is ever passed to the writer under test.
    private static (byte[] Image, uint Node) ExpandedImage(int blocks, int capacity = 0, bool scattered = false)
    {
        if (capacity == 0) capacity = (blocks + 1024) * Bytes;
        var disk = new StudyDisk(StudyFixture.Empty(Bytes, capacity), Bytes, batchedWrites: true);
        var node = FsOps.Create(ref disk, "Data");
        FsOps.Write(ref disk, node, 0, new byte[Bytes]);
        var image = disk.Snapshot();
        var original = StudyFixture.Inspect(image, Bytes);
        var tree = image.AsSpan(5 * Bytes, Bytes);
        var first = (int)StudyFixture.U32(tree, 16);
        Assert.Equal(33, first);
        Array.Clear(image, first * Bytes, blocks * Bytes);
        var bitmapCount = (capacity / Bytes + 3999) / 4000;
        for (var number = first; number < first + blocks; number++)
            image[(9 + number / 4000) * Bytes + 12 + number % 4000 / 8] &= (byte)~(0x80 >> (number % 8));
        BinaryPrimitives.WriteUInt16BigEndian(tree[28..], (ushort)blocks);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(7 * Bytes + 40), (uint)(blocks * Bytes));
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(2 * Bytes + Bytes - 28), original.FreeBlocks - (uint)blocks + 1);
        if (scattered)
        {
            Assert.Equal(218, blocks); Assert.Equal(128 * 1024, capacity);
            var holes = new[] { 230, 236, 242, 248 };
            for (var number = first; number < capacity / Bytes - 1; number++)
            {
                var offset = (9 + number / 4000) * Bytes + 12 + number % 4000 / 8;
                var mask = (byte)(0x80 >> (number % 8));
                if (holes.Contains(number)) image[offset] |= mask;
                else image[offset] &= (byte)~mask;
                image.AsSpan(number * Bytes, Bytes).Clear();
            }
            var runs = new[] { (33, 197), (231, 5), (237, 5), (243, 5), (249, 6) };
            tree[16..].Clear(); BinaryPrimitives.WriteUInt16BigEndian(tree[12..], (ushort)runs.Length);
            for (var i = 0; i < runs.Length; i++)
            {
                var entry = tree[(16 + i * 14)..];
                BinaryPrimitives.WriteUInt32BigEndian(entry, (uint)runs[i].Item1);
                BinaryPrimitives.WriteUInt32BigEndian(entry[4..], i == runs.Length - 1 ? 0u : (uint)runs[i + 1].Item1);
                BinaryPrimitives.WriteUInt32BigEndian(entry[8..], i == 0 ? 0x80000000u | node : (uint)runs[i - 1].Item1);
                BinaryPrimitives.WriteUInt16BigEndian(entry[12..], (ushort)runs[i].Item2);
            }
        }
        foreach (var number in Enumerable.Range(9, bitmapCount).Concat(new[] { 2, 5, 7 })) Seal(image.AsSpan(number * Bytes, Bytes));
        CheckFile(image, new byte[blocks * Bytes]);
        return (image, node);
    }
    private static void Seal(Span<byte> block)
    {
        BinaryPrimitives.WriteUInt32BigEndian(block[4..], 0); uint sum = 1;
        for (var at = 0; at < block.Length; at += 4) sum = unchecked(sum + StudyFixture.U32(block, at));
        BinaryPrimitives.WriteUInt32BigEndian(block[4..], unchecked(0u - sum));
    }

    [Theory]
    [InlineData(3960, 128)] // Allocation crosses the 4,000-bit bitmap boundary.
    [InlineData(65535, 2)] // A full 16-bit extent must link to a new extent.
    public void AppendsSplitAtBitmapAndExtentBoundaries(int blocks, int appendBlocks)
    {
        var (image, node) = ExpandedImage(blocks);
        var disk = new StudyDisk(image, Bytes);
        var data = StudyFixture.Pattern(appendBlocks * Bytes, 76);
        Assert.True(Write(ref disk, node, (uint)(blocks * Bytes), data, out var actual, out _));
        Assert.Equal((uint)data.Length, actual);
        var expected = new byte[(blocks + appendBlocks) * Bytes]; data.CopyTo(expected, blocks * Bytes);
        CheckFile(disk.Snapshot(), expected);
    }

    [Fact]
    public void ExhaustionAfterFirstBatchRollsBackWholeRequest()
    {
        var (image, node) = ExpandedImage(218, 128 * 1024);
        Assert.Equal(4u, StudyFixture.Inspect(image, Bytes).FreeBlocks);
        var data = StudyFixture.Pattern(1536, 77);
        var probe = new StudyDisk(image, Bytes, crashModel: true) { BatchBytes = 1024 };
        Assert.False(Write(ref probe, node, 0, data, out var actual, out var error));
        Assert.Equal(0u, actual); Assert.Equal(DOS.Error.DiskFull, error);
        Assert.True(probe.Counts.RangeWrites >= 3); // Undo images, entries, then new data.
        var before = Manifest(image); var cases = 0;
        foreach (var saved in probe.CrashImages()) { Recovered(saved, before, before, "disk full"); cases++; }
        for (var cut = 1; cut <= probe.Events; cut++)
        {
            var disk = new StudyDisk(image, Bytes, crashModel: true) { BatchBytes = 1024, CutAfterEvent = cut };
            ExpectCut(ref disk, (ref StudyDisk d) => Write(ref d, node, 0, data, out _, out _));
            foreach (var saved in disk.CrashImages()) { Recovered(saved, before, before, $"disk full cut {cut}"); cases++; }
        }
        output.WriteLine($"Batched near-full: {cases} exhaustive image checks, including cuts after the first applied batch.");
    }

    [Fact]
    public void ScatteredJournalHolesUseScalarFallback()
    {
        var (image, node) = ExpandedImage(218, 128 * 1024, scattered: true);
        var data = StudyFixture.Pattern(Bytes, 78);
        var probe = new StudyDisk(image, Bytes, crashModel: true);
        Assert.True(Write(ref probe, node, 0, data, out var actual, out _));
        Assert.Equal((uint)Bytes, actual);
        Assert.Equal(0, probe.Counts.RangeReads);
        var expected = new byte[218 * Bytes]; data.CopyTo(expected, 0);
        CheckFile(probe.Snapshot(), expected);
        var before = Manifest(image); var after = Manifest(probe.Snapshot()); var cases = 0;
        for (var cut = 1; cut <= probe.Events; cut++)
        {
            var disk = new StudyDisk(image, Bytes, crashModel: true) { CutAfterEvent = cut };
            ExpectCut(ref disk, (ref StudyDisk d) => Write(ref d, node, 0, data, out _, out _));
            foreach (var saved in disk.CrashImages()) { Recovered(saved, before, after, $"scattered journal cut {cut}"); cases++; }
        }
        output.WriteLine($"Batched fragmented journal: {cases} exhaustive image checks.");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(256)]
    [InlineData(511)]
    public void TornSectorsRecoverOrRefuseMount(int prefix)
    {
        var (image, node, original) = Seed(4096);
        var data = StudyFixture.Pattern(1536, 79);
        var probe = new StudyDisk(image, Bytes, crashModel: true) { BatchBytes = 1024 };
        Assert.True(Write(ref probe, node, 0, data, out _, out _));
        var before = Manifest(image); var after = Manifest(probe.Snapshot());
        var recovered = 0; var refused = 0;
        for (var write = 1; write <= probe.Counts.BlockWrites; write++)
        {
            var disk = new StudyDisk(image, Bytes, crashModel: true) { BatchBytes = 1024, TearAtWrite = write, TearBytes = prefix };
            ExpectCut(ref disk, (ref StudyDisk d) => Write(ref d, node, 0, data, out _, out _));
            var saved = disk.Snapshot(); var restarted = new StudyDisk(saved, Bytes, crashModel: true);
            var work = restarted.Work;
            if (AmigaSmartFileSystemWriteCore.Recover(ref restarted, ref work))
            {
                Recovered(saved, before, after, $"torn write {write}/{prefix}"); recovered++;
            }
            else
            {
                Assert.False(AmigaSmartFileSystemCore.Mount(ref restarted, work.Meta));
                Assert.Throws<InvalidDataException>(() => StudyFixture.Inspect(restarted.Snapshot(), Bytes)); refused++;
            }
        }
        output.WriteLine($"Batched torn sectors: {recovered} recovered, {refused} refused mount for prefix {prefix}.");
    }

    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(4096)]
    public void ScratchBoundsAndUnalignedWritesPreserveGeometry(int bytes)
    {
        var disk = new StudyDisk(StudyFixture.Empty(bytes, 1024 * 1024), bytes, batchedWrites: true);
        var node = FsOps.Create(ref disk, "Data");
        var original = StudyFixture.Pattern(20001, 80);
        FsOps.Write(ref disk, node, 0, original);
        var data = StudyFixture.Pattern(17003, 81);
        data.CopyTo(disk.Ram, (int)disk.Payload.Raw);
        var batch = APTR.FromPointer(disk.BatchBuffer.Raw + 16);
        var batchBytes = (uint)bytes * 3;
        disk.Ram.AsSpan((int)disk.BatchBuffer.Raw, 16).Fill(0xA7);
        disk.Ram.AsSpan((int)batch.Raw + (int)batchBytes, 16).Fill(0xB8);
        var work = disk.Work;
        var request = new AmigaSmartFileSystemWriteCore.Request
        { Node = node, Position = 13, Length = (uint)data.Length, Buffer = disk.Payload };
        Assert.True(AmigaSmartFileSystemWriteCore.WriteBatched(ref disk, ref work, ref request, batch, batchBytes, out var actual));
        Assert.Equal((uint)data.Length, actual);
        Assert.False(disk.Ram.AsSpan((int)disk.BatchBuffer.Raw, 16).ContainsAnyExcept((byte)0xA7));
        Assert.False(disk.Ram.AsSpan((int)batch.Raw + (int)batchBytes, 16).ContainsAnyExcept((byte)0xB8));
        data.CopyTo(original, 13);
        var report = StudyFixture.Inspect(disk.Snapshot(), bytes);
        Assert.Equal(bytes, report.BlockBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(original)), report.Entries.Single(e => e.Path == "/Data").Sha256);
    }

    [Theory]
    [InlineData("seek", DOS.Error.SeekError)]
    [InlineData("unmapped", DOS.Error.BadNumber)]
    [InlineData("directory", DOS.Error.ObjectWrongType)]
    [InlineData("file-protection", DOS.Error.WriteProtected)]
    [InlineData("device-protection", DOS.Error.DiskWriteProtected)]
    public void RejectedRequestsNeverBeginATransaction(string kind, DOS.Error expected)
    {
        var (image, node, _) = Seed(1536);
        var disk = new StudyDisk(image, Bytes);
        if (kind == "file-protection")
        {
            var metadataWork = disk.Work;
            var metadataRequest = new AmigaSmartFileSystemWriteCore.Request { Node = node, Position = 4 };
            Assert.True(AmigaSmartFileSystemWriteCore.SetMetadata(ref disk, ref metadataWork, ref metadataRequest, DosPacketAction.SetProtection));
            disk = new StudyDisk(disk.Snapshot(), Bytes);
        }
        if (kind == "device-protection") disk.ReadOnly = true;
        var before = disk.Snapshot();
        var work = disk.Work;
        var request = new AmigaSmartFileSystemWriteCore.Request
        {
            Node = kind == "directory" ? 1u : node, Position = kind == "seek" ? 1537u : 0,
            Buffer = kind == "unmapped" ? APTR.FromPointer(uint.MaxValue - 1) : disk.Payload, Length = 2
        };
        Assert.False(AmigaSmartFileSystemWriteCore.WriteBatched(ref disk, ref work, ref request, disk.BatchBuffer, disk.BatchBytes, out var actual));
        Assert.Equal(expected, work.Error); Assert.Equal(0u, actual); Assert.Equal(0, disk.Events);
        Assert.True(before.AsSpan().SequenceEqual(disk.Snapshot()));
    }
}
