using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Amiga;
using CopperSFS.Bench;
using Xunit.Abstractions;

namespace CopperSFS.Tests;

public sealed partial class RelocationTests(ITestOutputHelper output)
{
    private const int Bytes = 512;
    private sealed record Seed(byte[] Image, uint Node, byte[] Data);

    // Construct a small, explicitly fragmented file in an independently checked
    // image, retaining a second file whose data/metadata must survive every cut.
    private static Seed Fragmented(int[] starts, int[] lengths, int capacity = 512 * 1024, int bytes = Bytes)
    {
        var disk = new StudyDisk(StudyFixture.Empty(bytes, capacity), bytes, batchedWrites: true);
        var node = FsOps.Create(ref disk, "Data");
        var keep = FsOps.Create(ref disk, "Keep");
        FsOps.Write(ref disk, keep, 0, StudyFixture.Pattern(513, 110));
        var work = disk.Work;
        var request = new AmigaSmartFileSystemWriteCore.Request { Node = node, Position = 16 | 32 | 64 };
        Assert.True(AmigaSmartFileSystemWriteCore.SetMetadata(ref disk, ref work, ref request, DosPacketAction.SetProtection));
        const int container = 7, at = 24;
        var image = disk.Snapshot(); var data = StudyFixture.Pattern(lengths.Sum() * bytes, 111);
        Assert.Equal(node, StudyFixture.U32(image, container * bytes + at + 4));
        var tree = image.AsSpan(5 * bytes, bytes); var bits = (bytes - 12) * 8;
        var existing = BinaryPrimitives.ReadUInt16BigEndian(tree[12..]);
        tree[(16 + existing * 14)..].Clear(); U16(tree, 12, existing + lengths.Length);
        var logical = 0;
        for (var i = 0; i < starts.Length; i++)
        {
            var entry = tree[(16 + (existing + i) * 14)..];
            U32(entry, 0, (uint)starts[i]); U32(entry, 4, i + 1 == starts.Length ? 0 : (uint)starts[i + 1]);
            U32(entry, 8, i == 0 ? node | 0x80000000 : (uint)starts[i - 1]); U16(entry, 12, lengths[i]);
            data.AsSpan(logical * bytes, lengths[i] * bytes).CopyTo(image.AsSpan(starts[i] * bytes));
            for (var block = starts[i]; block < starts[i] + lengths[i]; block++)
            {
                var location = (9 + block / bits) * bytes + 12 + block % bits / 8;
                var mask = (byte)(0x80 >> (block % 8));
                Assert.NotEqual(0, image[location] & mask); image[location] &= (byte)~mask;
            }
            logical += lengths[i];
        }
        U32(image.AsSpan((int)container * bytes), at + 12, (uint)starts[0]);
        U32(image.AsSpan((int)container * bytes), at + 16, (uint)data.Length);
        var root = image.AsSpan(2 * bytes, bytes);
        U32(root, bytes - 28, StudyFixture.U32(root, bytes - 28) - (uint)logical);
        foreach (var block in new[] { 2, 5, (int)container }.Concat(Enumerable.Range(9, (capacity / bytes + bits - 1) / bits)).Distinct())
            Seal(image.AsSpan(block * bytes, bytes));
        StudyFixture.Inspect(image, bytes);
        return new(image, node, data);
    }
    private static void U16(Span<byte> data, int at, int value) => BinaryPrimitives.WriteUInt16BigEndian(data[at..], (ushort)value);
    private static void U32(Span<byte> data, int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(data[at..], value);
    private static void Seal(Span<byte> data)
    {
        U32(data, 4, 0); uint sum = 1;
        for (var at = 0; at < data.Length; at += 4) sum = unchecked(sum + StudyFixture.U32(data, at));
        U32(data, 4, unchecked(0u - sum));
    }
    private static bool Write(ref StudyDisk disk, uint node, uint position, byte[] data, out uint actual)
        => Write(ref disk, node, position, data, out actual, out _);
    private static bool Write(ref StudyDisk disk, uint node, uint position, byte[] data, out uint actual, out DOS.Error error)
    {
        data.CopyTo(disk.Ram, (int)disk.Payload.Raw);
        var work = disk.Work;
        var request = new AmigaSmartFileSystemWriteCore.Request
        { Node = node, Position = position, Buffer = disk.Payload, Length = (uint)data.Length };
        var result = AmigaSmartFileSystemWriteCore.WriteBatched(ref disk, ref work, ref request,
            disk.BatchBuffer, disk.BatchBytes, out actual, disk.RelocateFragmented);
        error = work.Error;
        return result;
    }
    private static uint[] Blocks(byte[] image, uint node, int bytes = Bytes)
    {
        Assert.Equal(node, StudyFixture.U32(image, 7 * bytes + 28));
        var key = StudyFixture.U32(image, 7 * bytes + 36); var blocks = new List<uint>();
        var entries = new Dictionary<uint, (uint Next, int Count)>();
        void Tree(uint block, int depth)
        {
            Assert.InRange(depth, 0, 31);
            var tree = image.AsSpan((int)block * bytes, bytes);
            var count = BinaryPrimitives.ReadUInt16BigEndian(tree[12..]); var stride = tree[15];
            for (var i = 0; i < count; i++)
            {
                var entry = tree[(16 + i * stride)..];
                if (tree[14] == 1) entries.Add(StudyFixture.U32(entry, 0), (StudyFixture.U32(entry, 4), BinaryPrimitives.ReadUInt16BigEndian(entry[12..])));
                else Tree(StudyFixture.U32(entry, 4), depth + 1);
            }
        }
        Tree(5, 0);
        while (key != 0)
        {
            var entry = entries[key];
            for (uint n = 0; n < entry.Count; n++) blocks.Add(key + n);
            key = entry.Next;
        }
        return blocks.ToArray();
    }
    private static string Manifest(byte[] image) => JsonSerializer.Serialize(StudyFixture.Inspect(image, Bytes));
    private static void Check(byte[] image, byte[] expected, int bytes = Bytes)
    {
        var file = StudyFixture.Inspect(image, bytes).Entries.Single(e => e.Path == "/Data");
        Assert.Equal((uint)expected.Length, file.Size);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), file.Sha256);
        Assert.Equal(15u | 32u | 64u, file.Protection);
    }
    private static void Recovered(byte[] image, string before, string after, string context)
    {
        try
        {
            var disk = new StudyDisk(image, Bytes, crashModel: true);
            FsOps.Recover(ref disk);
            var saved = disk.Snapshot(); var state = Manifest(saved);
            Assert.True(state == before || state == after, "Mixed contents, metadata or free-space count");
            FsOps.Recover(ref disk); Assert.True(saved.AsSpan().SequenceEqual(disk.Snapshot()));
        }
        catch (Exception error)
        {
            var directory = Environment.GetEnvironmentVariable("COPPERSFS_RELIABILITY_ARTIFACTS");
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "relocation-" + Guid.NewGuid().ToString("N"));
                File.WriteAllBytes(path + ".img", image);
                File.WriteAllText(path + ".json", JsonSerializer.Serialize(new { context, before, after }));
            }
            throw new InvalidOperationException(context, error);
        }
    }
    private static void CrashStates(ref StudyDisk disk, string before, string after, string context, ref int exhaustive, ref int sampled)
    {
        var all = disk.CrashSpaceFits(4096);
        foreach (var image in all ? disk.CrashImages() : disk.SampledCrashImages())
        {
            Recovered(image, before, after, context);
            if (all) exhaustive++; else sampled++;
        }
    }
    private static void Cut(ref StudyDisk disk, DiskAction action)
    {
        try { action(ref disk); }
        catch (StudyPowerCut) { return; }
        Assert.Fail("Cut was not reached");
    }

    [Theory]
    [InlineData(0, 24576)]
    [InlineData(3601, 20000)]
    [InlineData(8192, 16384)]
    [InlineData(0, 16384)]
    [InlineData(0, 25576)]
    public void ReplacementsPreserveEdgesAndOldData(int position, int length)
    {
        var seed = Fragmented([64, 160, 256], [16, 16, 16]);
        var data = StudyFixture.Pattern(length, 112); var expected = seed.Data.ToArray();
        Array.Resize(ref expected, Math.Max(expected.Length, position + length)); data.CopyTo(expected, position);
        var disk = new StudyDisk(seed.Image, Bytes);
        Assert.True(Write(ref disk, seed.Node, (uint)position, data, out var actual)); Assert.Equal((uint)length, actual);
        var after = disk.Snapshot(); Check(after, expected);
        var beforeBlocks = Blocks(seed.Image, seed.Node); var afterBlocks = Blocks(after, seed.Node);
        var begin = (position + Bytes - 1) / Bytes; var end = Math.Min((position + length) / Bytes, beforeBlocks.Length);
        for (var i = begin; i < end; i++)
        {
            Assert.DoesNotContain(afterBlocks[i], beforeBlocks);
            if (i > begin) Assert.Equal(afterBlocks[i - 1] + 1, afterBlocks[i]);
            Assert.True(seed.Image.AsSpan((int)beforeBlocks[i] * Bytes, Bytes).SequenceEqual(after.AsSpan((int)beforeBlocks[i] * Bytes, Bytes)));
        }
        for (var i = 0; i < beforeBlocks.Length; i++)
            if (i < begin || i >= end) Assert.Equal(beforeBlocks[i], afterBlocks[i]);
        var afterFree = StudyFixture.Inspect(after, Bytes).FreeBlocks;
        Assert.Equal(StudyFixture.Inspect(seed.Image, Bytes).FreeBlocks - (uint)(afterBlocks.Length - beforeBlocks.Length), afterFree);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(8192, false)]
    [InlineData(32768, false)]
    [InlineData(8192, true)]
    public void LargeContiguousReplacementsPreserveEdgesAndRepeatedFreeSpace(int position, bool adjacentRecords)
    {
        var seed = adjacentRecords ? Fragmented([128, 192, 256], [64, 64, 64]) : Fragmented([128], [192]);
        var current = seed.Image; var expected = seed.Data.ToArray();
        var free = StudyFixture.Inspect(current, Bytes).FreeBlocks;
        for (var pass = 0; pass < 4; pass++)
        {
            var data = StudyFixture.Pattern(65536, 130 + pass); data.CopyTo(expected, position);
            var beforeBlocks = Blocks(current, seed.Node);
            var disk = new StudyDisk(current.ToArray(), Bytes);
            Assert.True(Write(ref disk, seed.Node, (uint)position, data, out var actual)); Assert.Equal(65536u, actual);
            var saved = disk.Snapshot(); Check(saved, expected);
            var afterBlocks = Blocks(saved, seed.Node); var first = position / Bytes;
            for (var i = 0; i < beforeBlocks.Length; i++)
            {
                if (i < first || i >= first + 128) Assert.Equal(beforeBlocks[i], afterBlocks[i]);
                else
                {
                    Assert.DoesNotContain(afterBlocks[i], beforeBlocks);
                    if (i > first) Assert.Equal(afterBlocks[i - 1] + 1, afterBlocks[i]);
                    Assert.Equal(current.AsSpan((int)beforeBlocks[i] * Bytes, Bytes).ToArray(), saved.AsSpan((int)beforeBlocks[i] * Bytes, Bytes).ToArray());
                }
            }
            Assert.Equal(free, StudyFixture.Inspect(saved, Bytes).FreeBlocks);
            current = saved;
        }
    }

    [Theory]
    [InlineData("threshold")]
    [InlineData("transfer-limit")]
    [InlineData("disabled")]
    [InlineData("no-run")]
    public void ContiguousReplacementFallsBackWithoutSuitableRange(string kind)
    {
        var seed = kind == "no-run"
            ? Fragmented(new[] { 64 }.Concat(Enumerable.Range(0, 23).Select(i => 208 + 32 * i)).ToArray(),
                new[] { 128 }.Concat(Enumerable.Repeat(16, 23)).ToArray())
            : Fragmented([128], [192]);
        var disk = new StudyDisk(seed.Image.ToArray(), Bytes, maxTransfer: kind == "transfer-limit" ? 32768u : 65536u)
            { RelocateFragmented = kind != "disabled" };
        var data = StudyFixture.Pattern(kind == "threshold" ? 65535 : 65536, 134);
        var expected = seed.Data.ToArray(); data.CopyTo(expected, 0);
        Assert.True(Write(ref disk, seed.Node, 0, data, out _)); Check(disk.Snapshot(), expected);
        Assert.Equal(Blocks(seed.Image, seed.Node), Blocks(disk.Snapshot(), seed.Node));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RelocationSurvivesEveryCutAndIoFailure(bool splitEdges, bool contiguous)
    {
        var seed = contiguous ? Fragmented([128], [192], 256 * 1024)
            : Fragmented([64, 160, 256], [16, 16, 16], 256 * 1024);
        var position = splitEdges ? 3601u : 0u;
        var data = StudyFixture.Pattern(contiguous ? (splitEdges ? 66048 : 65536) : (splitEdges ? 20000 : 24576), 113);
        var expected = seed.Data.ToArray(); data.CopyTo(expected, (int)position);
        var probe = new StudyDisk(seed.Image, Bytes, crashModel: true);
        Assert.True(Write(ref probe, seed.Node, position, data, out _)); Check(probe.Snapshot(), expected);
        Assert.NotEqual(Blocks(seed.Image, seed.Node)[8], Blocks(probe.Snapshot(), seed.Node)[8]);
        var before = Manifest(seed.Image); var after = Manifest(probe.Snapshot());
        var exhaustive = 0; var sampled = 0; byte[]? dirty = null;
        for (var cut = 1; cut <= probe.Events; cut++)
        {
            var disk = new StudyDisk(seed.Image, Bytes, crashModel: true) { CutAfterEvent = cut };
            Cut(ref disk, (ref StudyDisk d) => Write(ref d, seed.Node, position, data, out _));
            CrashStates(ref disk, before, after, $"relocation cut {cut}", ref exhaustive, ref sampled);
            var saved = disk.SampledCrashImages().Skip(1).First();
            if (StudyFixture.U32(saved, 4 * Bytes) == 0x54524641 && StudyFixture.U32(saved, 4 * Bytes + 12) != 0) dirty = saved;
        }
        foreach (var persistent in new[] { false, true })
        {
            for (var fail = 1; fail <= probe.Events; fail++)
            {
                var disk = new StudyDisk(seed.Image, Bytes, crashModel: true) { FailAtEvent = fail, PersistentFailure = persistent };
                Assert.False(Write(ref disk, seed.Node, position, data, out var actual)); Assert.Equal(0u, actual);
                CrashStates(ref disk, before, after, $"relocation I/O {fail}/{persistent}", ref exhaustive, ref sampled);
            }
            for (var fail = 1; fail <= probe.Counts.BlockReads; fail++)
            {
                var disk = new StudyDisk(seed.Image, Bytes, crashModel: true) { FailAtRead = fail, PersistentFailure = persistent };
                var ok = Write(ref disk, seed.Node, position, data, out var actual);
                Assert.Equal(ok ? (uint)data.Length : 0u, actual);
                CrashStates(ref disk, ok ? after : before, after, $"relocation read {fail}/{persistent}", ref exhaustive, ref sampled);
            }
        }
        Assert.NotNull(dirty);
        var recovery = new StudyDisk(dirty, Bytes, crashModel: true); FsOps.Recover(ref recovery);
        for (var cut = 1; cut <= recovery.Events; cut++)
        {
            var disk = new StudyDisk(dirty, Bytes, crashModel: true) { CutAfterEvent = cut };
            Cut(ref disk, FsOps.Recover);
            CrashStates(ref disk, before, after, $"relocation recovery cut {cut}", ref exhaustive, ref sampled);
        }
        foreach (var persistent in new[] { false, true })
        {
            for (var fail = 1; fail <= recovery.Events; fail++)
            {
                var disk = new StudyDisk(dirty, Bytes, crashModel: true) { FailAtEvent = fail, PersistentFailure = persistent };
                var work = disk.Work; Assert.False(AmigaSmartFileSystemWriteCore.Recover(ref disk, ref work));
                CrashStates(ref disk, before, after, $"recovery I/O {fail}/{persistent}", ref exhaustive, ref sampled);
            }
            for (var fail = 1; fail <= recovery.Counts.BlockReads; fail++)
            {
                var disk = new StudyDisk(dirty, Bytes, crashModel: true) { FailAtRead = fail, PersistentFailure = persistent };
                var work = disk.Work; AmigaSmartFileSystemWriteCore.Recover(ref disk, ref work);
                CrashStates(ref disk, before, after, $"recovery read {fail}/{persistent}", ref exhaustive, ref sampled);
            }
        }
        output.WriteLine($"Relocation: {exhaustive} exhaustive image checks, {sampled} sampled image checks; includes mutation and recovery I/O failures.");
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(256, false)]
    [InlineData(511, false)]
    [InlineData(1, true)]
    [InlineData(256, true)]
    [InlineData(511, true)]
    public void TornWritesRecoverOrRefuseMount(int prefix, bool contiguous)
    {
        var seed = contiguous ? Fragmented([128], [192], 256 * 1024)
            : Fragmented([64, 160, 256], [16, 16, 16], 256 * 1024);
        var data = StudyFixture.Pattern(contiguous ? 66048 : 20000, 114);
        var probe = new StudyDisk(seed.Image, Bytes, crashModel: true);
        Assert.True(Write(ref probe, seed.Node, 3601, data, out _));
        var before = Manifest(seed.Image); var after = Manifest(probe.Snapshot()); var recovered = 0; var refused = 0;
        for (var write = 1; write <= probe.Counts.BlockWrites; write++)
        {
            var disk = new StudyDisk(seed.Image, Bytes, crashModel: true) { TearAtWrite = write, TearBytes = prefix };
            Cut(ref disk, (ref StudyDisk d) => Write(ref d, seed.Node, 3601, data, out _));
            var image = disk.Snapshot(); var restart = new StudyDisk(image, Bytes, crashModel: true); var work = restart.Work;
            if (AmigaSmartFileSystemWriteCore.Recover(ref restart, ref work)) { Recovered(image, before, after, $"tear {write}/{prefix}"); recovered++; }
            else
            {
                Assert.False(AmigaSmartFileSystemCore.Mount(ref restart, work.Meta));
                Assert.Throws<InvalidDataException>(() => StudyFixture.Inspect(restart.Snapshot(), Bytes)); refused++;
            }
        }
        output.WriteLine($"Relocation tears: {recovered} recovered, {refused} refused mount.");
    }

    [Theory]
    [InlineData("small")]
    [InlineData("disabled")]
    [InlineData("small-buffer")]
    [InlineData("contiguous")]
    [InlineData("no-run")]
    public void UnsuitableReplacementsUseInPlaceWrites(string kind)
    {
        var seed = kind == "no-run"
            ? Fragmented(Enumerable.Range(0, 30).Select(i => 64 + 32 * i).ToArray(), Enumerable.Repeat(16, 30).ToArray())
            : Fragmented(kind == "contiguous" ? [64, 80, 96] : [64, 160, 256], [16, 16, 16]);
        var disk = new StudyDisk(seed.Image, Bytes)
        { RelocateFragmented = kind != "disabled", BatchBytes = kind == "small-buffer" ? 1024u : 65536u };
        var position = kind == "small" ? 15 * Bytes : 0;
        var data = StudyFixture.Pattern(kind == "small" ? 1024 : Math.Min(seed.Data.Length, 65536), 115);
        var expected = seed.Data.ToArray(); data.CopyTo(expected, position);
        Assert.True(Write(ref disk, seed.Node, (uint)position, data, out _)); Check(disk.Snapshot(), expected);
        Assert.Equal(Blocks(seed.Image, seed.Node), Blocks(disk.Snapshot(), seed.Node));
    }

    [Fact]
    public void ShortFreeRunAtBitmapBoundaryIsSkipped()
    {
        var seed = Fragmented([64, 81], [16, 3900], 4608 * Bytes);
        var disk = new StudyDisk(seed.Image, Bytes);
        var data = StudyFixture.Pattern(16384, 116); var expected = seed.Data.ToArray(); data.CopyTo(expected, 0);
        Assert.True(Write(ref disk, seed.Node, 0, data, out _)); Check(disk.Snapshot(), expected);
        var blocks = Blocks(disk.Snapshot(), seed.Node);
        Assert.Equal(4000u, blocks[0]); Assert.Equal(4031u, blocks[31]);
        Assert.Equal(Blocks(seed.Image, seed.Node)[32], blocks[32]);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(4096)]
    public void RelocationRespectsGeometryAndTransferLimits(int bytes)
    {
        var seed = Fragmented([64, 128, 192], [16, 16, 16], 2 * 1024 * 1024, bytes);
        var disk = new StudyDisk(seed.Image, bytes, maxTransfer: 32768) { BatchBytes = 65536 };
        var data = StudyFixture.Pattern(48 * bytes, 117);
        Assert.True(Write(ref disk, seed.Node, 0, data, out var actual)); Assert.Equal((uint)data.Length, actual);
        Check(disk.Snapshot(), data, bytes); Assert.InRange(disk.Counts.MaxRangeBlocks, 1u, (uint)(32768 / bytes));
        // At larger block sizes each extent already holds a whole device range.
        if (bytes == 512) Assert.NotEqual(Blocks(seed.Image, seed.Node, bytes)[0], Blocks(disk.Snapshot(), seed.Node, bytes)[0]);
    }

    [Fact]
    public void MultipleFullBatchesMergeAndRecoverAsOneRequest()
    {
        var seed = Fragmented(Enumerable.Range(0, 8).Select(i => 64 + 64 * i).ToArray(), Enumerable.Repeat(32, 8).ToArray());
        var data = StudyFixture.Pattern(131072, 118);
        var probe = new StudyDisk(seed.Image, Bytes, crashModel: true);
        Assert.True(Write(ref probe, seed.Node, 0, data, out _)); Check(probe.Snapshot(), data);
        Assert.Equal(128u, probe.Counts.MaxRangeBlocks);
        var oldBlocks = Blocks(seed.Image, seed.Node); var newBlocks = Blocks(probe.Snapshot(), seed.Node);
        Assert.Equal(Enumerable.Range((int)newBlocks[0], 256).Select(i => (uint)i), newBlocks);
        foreach (var block in oldBlocks)
            Assert.True(seed.Image.AsSpan((int)block * Bytes, Bytes).SequenceEqual(probe.Snapshot().AsSpan((int)block * Bytes, Bytes)));
        var before = Manifest(seed.Image); var after = Manifest(probe.Snapshot()); var cases = 0; byte[]? dirty = null;
        for (var cut = 1; cut <= probe.Events; cut++)
        {
            var disk = new StudyDisk(seed.Image, Bytes, crashModel: true) { CutAfterEvent = cut };
            Cut(ref disk, (ref StudyDisk d) => Write(ref d, seed.Node, 0, data, out _));
            foreach (var saved in disk.SampledCrashImages())
            {
                Recovered(saved, before, after, $"multi-batch relocation {cut}"); cases++;
                if (StudyFixture.U32(saved, 4 * Bytes) == 0x54524641 && StudyFixture.U32(saved, 4 * Bytes + 12) != 0) dirty = saved;
            }
        }
        Assert.NotNull(dirty); var recovery = new StudyDisk(dirty, Bytes, crashModel: true); FsOps.Recover(ref recovery);
        for (var cut = 1; cut <= recovery.Events; cut++)
        {
            var disk = new StudyDisk(dirty, Bytes, crashModel: true) { CutAfterEvent = cut };
            Cut(ref disk, FsOps.Recover);
            foreach (var saved in disk.SampledCrashImages()) { Recovered(saved, before, after, $"multi-batch recovery {cut}"); cases++; }
        }
        // Large repeat overwrites also relocate the newly contiguous layout.
        var repeat = new StudyDisk(probe.Snapshot(), Bytes);
        Assert.True(Write(ref repeat, seed.Node, 0, seed.Data, out _)); Check(repeat.Snapshot(), seed.Data);
        var repeatedBlocks = Blocks(repeat.Snapshot(), seed.Node);
        Assert.DoesNotContain(repeatedBlocks[0], newBlocks);
        Assert.Equal(Enumerable.Range((int)repeatedBlocks[0], 256).Select(i => (uint)i), repeatedBlocks);
        output.WriteLine($"Relocation: 0 exhaustive image checks, {cases} sampled image checks; all full-batch and recovery cuts.");
    }

    [Fact]
    public void ExhaustionAfterRelocationRollsBackTheEntireRequest()
    {
        var seed = Fragmented([64, 144, 224, 304], [64, 64, 64, 64], 256 * 1024);
        var data = StudyFixture.Pattern(131072, 119); var before = Manifest(seed.Image);
        var probe = new StudyDisk(seed.Image, Bytes, crashModel: true);
        Assert.False(Write(ref probe, seed.Node, 0, data, out var actual, out var error)); Assert.Equal(0u, actual);
        Assert.Equal(DOS.Error.DiskFull, error);
        Assert.Equal(128u, probe.Counts.MaxRangeBlocks); Assert.True(probe.Counts.RangeReads > 0);
        var cases = 0;
        foreach (var saved in probe.SampledCrashImages()) { Recovered(saved, before, before, "exhaustion after relocation"); cases++; }
        for (var cut = 1; cut <= probe.Events; cut++)
        {
            var disk = new StudyDisk(seed.Image, Bytes, crashModel: true) { CutAfterEvent = cut };
            Cut(ref disk, (ref StudyDisk d) => Write(ref d, seed.Node, 0, data, out _));
            foreach (var saved in disk.SampledCrashImages()) { Recovered(saved, before, before, $"exhaustion cut {cut}"); cases++; }
        }
        output.WriteLine($"Relocation: 0 exhaustive image checks, {cases} sampled image checks; disk full after relocating the first batch.");
    }

    [Fact]
    public void ConsecutiveRequestsContinueTheRelocatedRun()
    {
        var seed = Fragmented(Enumerable.Range(0, 8).Select(i => 64 + 64 * i).ToArray(), Enumerable.Repeat(32, 8).ToArray());
        var data = StudyFixture.Pattern(131072, 123); var disk = new StudyDisk(seed.Image, Bytes);
        Assert.True(Write(ref disk, seed.Node, 0, data[..65536], out _));
        var expected = seed.Data.ToArray(); data.AsSpan(0, 65536).CopyTo(expected);
        Check(disk.Snapshot(), expected);
        Assert.True(Write(ref disk, seed.Node, 65536, data[65536..], out _)); Check(disk.Snapshot(), data);
        var blocks = Blocks(disk.Snapshot(), seed.Node);
        Assert.Equal(Enumerable.Range((int)blocks[0], blocks.Length).Select(i => (uint)i), blocks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndexedExtentReplacementSurvivesCutsAndWriteFailures(bool growsTree)
    {
        byte[] image, original; uint node;
        if (growsTree)
        {
            var seed = Fragmented(Enumerable.Range(0, 33).Select(i => 64 + i * 32).ToArray(), Enumerable.Repeat(16, 33).ToArray(), 1024 * 1024);
            image = seed.Image; original = seed.Data; node = seed.Node;
            Assert.Equal(1, image[5 * Bytes + 14]);
            Assert.Equal(35, BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(5 * Bytes + 12)));
        }
        else
        {
            var seedDisk = new StudyDisk(StudyFixture.Empty(Bytes, 512 * 1024), Bytes);
            node = FsOps.Create(ref seedDisk, "Data"); original = StudyFixture.Pattern(131072, 120);
            FsOps.Write(ref seedDisk, node, 0, original);
            var keep = FsOps.Create(ref seedDisk, "Keep"); FsOps.Write(ref seedDisk, keep, 0, StudyFixture.Pattern(513, 121));
            image = seedDisk.Snapshot(); Assert.Equal(0, image[5 * Bytes + 14]);
        }
        StudyFixture.Inspect(image, Bytes);
        var data = StudyFixture.Pattern(65536, 122); var expected = original.ToArray(); data.CopyTo(expected, 13);
        var probe = new StudyDisk(image, Bytes, crashModel: true);
        Assert.True(Write(ref probe, node, 13, data, out _));
        var file = StudyFixture.Inspect(probe.Snapshot(), Bytes).Entries.Single(e => e.Node == node);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), file.Sha256);
        Assert.NotEqual(Blocks(image, node)[1], Blocks(probe.Snapshot(), node)[1]);
        var before = Manifest(image); var after = Manifest(probe.Snapshot()); var cases = 0;
        for (var cut = 1; cut <= probe.Events; cut++)
        {
            var disk = new StudyDisk(image, Bytes, crashModel: true) { CutAfterEvent = cut };
            Cut(ref disk, (ref StudyDisk d) => Write(ref d, node, 13, data, out _));
            foreach (var saved in disk.SampledCrashImages()) { Recovered(saved, before, after, $"indexed cut {cut}"); cases++; }
        }
        foreach (var persistent in new[] { false, true })
        {
            for (var fail = 1; fail <= probe.Events; fail++)
            {
                var disk = new StudyDisk(image, Bytes, crashModel: true) { FailAtEvent = fail, PersistentFailure = persistent };
                Assert.False(Write(ref disk, node, 13, data, out var actual)); Assert.Equal(0u, actual);
                foreach (var saved in disk.SampledCrashImages()) { Recovered(saved, before, after, $"indexed failure {fail}/{persistent}"); cases++; }
            }
        }
        output.WriteLine($"Relocation: 0 exhaustive image checks, {cases} sampled image checks; indexed-tree mutation cuts and write/flush errors.");
    }
}
