using Amiga;
using System.Text.Json;
using Xunit.Abstractions;
using static CopperSFS.Tests.ReliabilityFixture;

namespace CopperSFS.Tests;

public sealed class TransactionRegressionTests(ITestOutputHelper output)
{
    private static AmigaSmartFileSystemWriteCore.Work Work() => new()
    {
        Meta = APTR.FromPointer(512), Data = APTR.FromPointer(1024),
        Bitmap = APTR.FromPointer(1536), Journal = APTR.FromPointer(2048)
    };

    [Theory]
    [InlineData("write")]
    [InlineData("resize")]
    [InlineData("grow")]
    [InlineData("truncate")]
    public void ContentChangesMustClearTheArchiveFlag(string operation)
    {
        var disk = new ReliabilityDisk(Image()); var work = Work();
        var request = new AmigaSmartFileSystemWriteCore.Request { Node = 3, Position = 16 | 32 | 64 };
        Assert.True(AmigaSmartFileSystemWriteCore.SetMetadata(ref disk, ref work, ref request, DosPacketAction.SetProtection));
        request = new() { Node = 3, Buffer = APTR.FromPointer(4000), Length = 1 };
        disk.Ram[4000] = 0xA5;
        Assert.True(operation switch
        {
            "write" => AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref request, out _),
            "resize" => AmigaSmartFileSystemWriteCore.Resize(ref disk, ref work, 3, 100),
            "grow" => AmigaSmartFileSystemWriteCore.Resize(ref disk, ref work, 3, 1500),
            _ => AmigaSmartFileSystemWriteCore.Truncate(ref disk, ref work, 3)
        }, work.Error.ToString());
        Inspect(disk.Snapshot());
        Assert.True(AmigaSmartFileSystemCore.Examine(ref disk, 3, APTR.FromPointer(6000), work.Meta));
        var protection = disk.ReadUInt32(APTR.FromPointer(6000), 116);
        output.WriteLine($"{operation}: DOS protection after committed content change = 0x{protection:X}");
        Assert.Equal(32u | 64u, protection);
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("rename")]
    public void RemovingTheLastChildMustClearTheNativeDirectoryPointer(string operation)
    {
        var disk = new ReliabilityDisk(Image()); var work = Work();
        var request = new AmigaSmartFileSystemWriteCore.Request
            { Node = 4, Directory = 1, Name = disk.Name("Moved"), Length = 5 };
        Assert.True(operation == "remove"
            ? AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, 4)
            : AmigaSmartFileSystemWriteCore.Rename(ref disk, ref work, ref request), work.Error.ToString());
        Inspect(disk.Snapshot());
        Assert.True(AmigaSmartFileSystemCore.Examine(ref disk, 2, APTR.FromPointer(6000), work.Meta));
        Assert.Equal(DOS.Error.NoMoreEntries,
            AmigaSmartFileSystemCore.ExamineNext(ref disk, 2, APTR.FromPointer(6000), work.Meta, work.Data));
        var firstChildBlock = U32(Block(disk.Snapshot(), 7), 24 + 16);
        output.WriteLine($"Empty Docs firstdirblock: {firstChildBlock}.");
        Assert.Equal(0u, firstChildBlock);
        // Block 8 belonged to the first admin range (base 1).
        Assert.Equal(0u, U32(Block(disk.Snapshot(), 10), 28) & (0x80000000u >> 7));
        Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, 2), work.Error.ToString());
        Inspect(disk.Snapshot());
    }

    [Fact]
    public void FailedOverwriteMustNotExposeAReadSuccessfulMixture()
    {
        var original = Image();
        var baseline = new ReliabilityDisk(original); var work = Work();
        baseline.Ram.AsSpan(4000, 1024).Fill(0xA5);
        var request = new AmigaSmartFileSystemWriteCore.Request
            { Node = 3, Buffer = APTR.FromPointer(4000), Length = 1024 };
        Assert.True(AmigaSmartFileSystemWriteCore.Write(ref baseline, ref work, ref request, out _));
        var blocked = 0;
        for (var point = 1; point <= baseline.Events; point++)
        {
            var disk = new ReliabilityDisk(original) { FailAtEvent = point }; work = Work();
            disk.Ram.AsSpan(4000, 1024).Fill(0xA5);
            request = new() { Node = 3, Buffer = APTR.FromPointer(4000), Length = 1024 };
            var success = AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref request, out var actual);
            Assert.False(success, $"Missed failure at event {point}");
            Assert.Equal(0u, actual);
            disk.Ram.AsSpan(6000, 1024).Fill(0xCC);
            var count = AmigaSmartFileSystemCore.Read(ref disk, 3, 0, APTR.FromPointer(6000), 1024,
                work.Meta, work.Data, out var error);
            var observed = disk.Ram.AsSpan(6000, 1024).ToArray();
            var before = Enumerable.Range(0, 1024).Select(i => (byte)(i * 17)).ToArray();
            var after = Enumerable.Repeat((byte)0xA5, 1024).ToArray();
            if (!AmigaSmartFileSystemCore.Mount(ref disk, work.Meta))
            {
                blocked++;
                Assert.Equal(-1, count);
                Assert.Equal(DOS.Error.DiskNotValidated, error);
                Assert.All(observed, b => Assert.Equal((byte)0xCC, b));
                AssertReadersBlocked(ref disk);
            }
            else
            {
                Assert.Equal(1024, count); Assert.Equal(DOS.Error.None, error);
                Assert.True(observed.SequenceEqual(before) || observed.SequenceEqual(after));
            }
            work = Work(); Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref disk, ref work));
            Assert.Equal(1024, AmigaSmartFileSystemCore.Read(ref disk, 3, 0, APTR.FromPointer(6000),
                1024, work.Meta, work.Data, out error));
            Assert.Equal(DOS.Error.None, error);
            var recovered = disk.Ram.AsSpan(6000, 1024).ToArray();
            Assert.True(recovered.SequenceEqual(before) || recovered.SequenceEqual(after));
            // A failed final commit flush can leave a complete TROK in the device
            // cache. Recover may see it as clean; persist that cache for inspection.
            Assert.True(disk.Flush());
            Inspect(disk.Snapshot());
        }
        Assert.True(blocked > 0);
        output.WriteLine($"All {baseline.Events} write/flush errors checked; {blocked} dirty states blocked reads.");
    }

    [Fact]
    public void DiskFullOverwriteMustNotExposeUncommittedBytes()
    {
        var original = Image();
        // Give every otherwise-free block except 252..254 to Nested.
        // Its chain is 44 -> 33 -> 42 -> 96, while Hello stays untouched.
        var tree = Block(original, 5);
        tree[16..].Clear(); U16(tree, 12, 6);
        (uint Key, uint Next, uint Previous, ushort Count)[] extents =
        [ (33, 42, 44, 7), (40, 43, 0x80000003, 2), (42, 96, 33, 1),
          (43, 0, 40, 1), (44, 33, 0x80000004, 20), (96, 0, 42, 156) ];
        for (var i = 0; i < extents.Length; i++)
        {
            var (key, next, previous, count) = extents[i]; var at = 16 + i * 14;
            U32(tree, at, key); U32(tree, at + 4, next); U32(tree, at + 8, previous); U16(tree, at + 12, count);
        }
        Seal(tree); U32(Block(original, 8), 40, 184 * 512); Seal(Block(original, 8));
        var bitmap = Block(original, 9);
        for (var block = 1; block < 252; block++) bitmap[12 + block / 8] &= (byte)~(0x80 >> (block % 8));
        Seal(bitmap); U32(Block(original, 2), 484, 3); Seal(Block(original, 2));
        Inspect(original);
        var disk = new ReliabilityDisk(original); var work = Work();
        disk.Ram.AsSpan(4000, 1024).Fill(0xA5);
        var request = new AmigaSmartFileSystemWriteCore.Request
            { Node = 3, Buffer = APTR.FromPointer(4000), Length = 1024 };
        Assert.False(AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref request, out var actual));
        Assert.Equal(DOS.Error.DiskFull, work.Error);
        Assert.Equal(0u, actual);
        disk.Ram.AsSpan(6000, 1024).Fill(0xCC);
        Assert.Equal(-1, AmigaSmartFileSystemCore.Read(ref disk, 3, 0, APTR.FromPointer(6000), 1024,
            work.Meta, work.Data, out var error));
        Assert.Equal(DOS.Error.DiskNotValidated, error);
        Assert.All(disk.Ram.AsSpan(6000, 1024).ToArray(), b => Assert.Equal((byte)0xCC, b));
        AssertReadersBlocked(ref disk);
        work = Work(); Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref disk, ref work));
        Assert.Equal(1024, AmigaSmartFileSystemCore.Read(ref disk, 3, 0, APTR.FromPointer(6000), 1024,
            work.Meta, work.Data, out _));
        var recovered = disk.Ram.AsSpan(6000, 1024).ToArray();
        Assert.Equal(Enumerable.Range(0, 1024).Select(i => (byte)(i * 17)), recovered);
        Assert.Equal(JsonSerializer.Serialize(Inspect(original).Entries), JsonSerializer.Serialize(Inspect(disk.Snapshot()).Entries));
    }

    private static void AssertReadersBlocked(ref ReliabilityDisk disk)
    {
        var work = Work(); var fib = APTR.FromPointer(8000);
        Assert.False(AmigaSmartFileSystemCore.Describe(ref disk, 3, work.Meta, out _, out _, out _));
        Assert.Equal(0u, AmigaSmartFileSystemCore.Resolve(ref disk, 1, disk.Name("Hello"), 5, work.Meta, out var error));
        Assert.Equal(DOS.Error.DiskNotValidated, error);
        Assert.False(AmigaSmartFileSystemCore.Examine(ref disk, 3, fib, work.Meta));
        Assert.Equal(DOS.Error.DiskNotValidated, AmigaSmartFileSystemCore.ExamineNext(ref disk, 1, fib, work.Meta, work.Data));
        Assert.False(AmigaSmartFileSystemCore.CountUsed(ref disk, work.Meta, out _));
    }

    [Theory]
    [InlineData("write")]
    [InlineData("resize")]
    [InlineData("truncate")]
    public void NoOpContentOperationsPreserveArchiveFlag(string operation)
    {
        var disk = new ReliabilityDisk(Image()); var work = Work();
        if (operation == "truncate") Assert.True(AmigaSmartFileSystemWriteCore.Truncate(ref disk, ref work, 3));
        var request = new AmigaSmartFileSystemWriteCore.Request { Node = 3, Position = 16 };
        Assert.True(AmigaSmartFileSystemWriteCore.SetMetadata(ref disk, ref work, ref request, DosPacketAction.SetProtection));
        var writes = disk.Writes;
        request = new() { Node = 3, Buffer = APTR.FromPointer(4000) };
        Assert.True(operation switch
        {
            "write" => AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref request, out _),
            "resize" => AmigaSmartFileSystemWriteCore.Resize(ref disk, ref work, 3, 1300),
            _ => AmigaSmartFileSystemWriteCore.Truncate(ref disk, ref work, 3)
        });
        Assert.Equal(writes, disk.Writes);
        Assert.Equal(31u, Inspect(disk.Snapshot()).Entries.Single(e => e.Node == 3).Protection);
    }

    private static byte[] ChainImage(int selected, out uint victim, out uint removed, out uint[] surviving)
    {
        var disk = new ReliabilityDisk(Image()); var work = Work();
        var nodes = new List<uint> { 4 };
        for (var i = 0; i < 8; i++)
        {
            var name = $"F{i}" + new string('x', 98);
            var request = new AmigaSmartFileSystemWriteCore.Request
                { Directory = 2, Name = disk.Name(name), Length = (uint)name.Length };
            Assert.True(AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref request, false));
            nodes.Add(request.Node);
        }
        var image = disk.Snapshot(); Inspect(image);
        var chain = new List<uint>();
        for (var block = U32(Block(image, 7), 40); block != 0; block = U32(Block(image, (int)block), 16))
            chain.Add(block);
        Assert.Equal(3, chain.Count);
        var container = chain[selected]; removed = container;
        var occupants = nodes.Where(node => U32(Block(image, 6), 20 + ((int)node - 1) * 10) == container).ToArray();
        foreach (var node in occupants[..^1])
            Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, node), work.Error.ToString());
        victim = occupants[^1]; surviving = chain.Where(block => block != container).ToArray();
        Inspect(disk.Snapshot());
        return disk.Snapshot();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EmptyContainersAreUnlinkedAtEveryChainPosition(int selected)
    {
        var image = ChainImage(selected, out var victim, out var removed, out var expected);
        var before = Inspect(image);
        var disk = new ReliabilityDisk(image); var work = Work();
        Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, victim), work.Error.ToString());
        image = disk.Snapshot(); var after = Inspect(image);
        var chain = new List<uint>();
        for (var block = U32(Block(image, 7), 40); block != 0; block = U32(Block(image, (int)block), 16))
            chain.Add(block);
        Assert.Equal(expected, chain);
        Assert.Equal(0u, U32(Block(image, 10), 28) & (0x80000000u >> ((int)removed - 1)));
        Assert.Equal(before.Entries.Where(e => e.Node != victim).OrderBy(e => e.Path), after.Entries.OrderBy(e => e.Path));
    }

    [Fact]
    public void RenamingOnlyChildWithinItsDirectoryPreservesItsContent()
    {
        var disk = new ReliabilityDisk(OccupiedFirstAdminSlotImage()); var work = Work();
        var before = Inspect(disk.Snapshot()).Entries.Single(e => e.Node == 4);
        var request = new AmigaSmartFileSystemWriteCore.Request
            { Node = 4, Directory = 2, Name = disk.Name("Renamed"), Length = 7 };
        Assert.True(AmigaSmartFileSystemWriteCore.Rename(ref disk, ref work, ref request), work.Error.ToString());
        var after = Inspect(disk.Snapshot()).Entries.Single(e => e.Node == 4);
        Assert.Equal(before with { Path = "/Docs/Renamed" }, after);
        Assert.Equal(8u, U32(Block(disk.Snapshot(), 7), 40)); // Reuses the just-freed admin slot.
    }

    private static byte[] OccupiedFirstAdminSlotImage()
    {
        var disk = new ReliabilityDisk(Image()); var work = Work();
        var request = new AmigaSmartFileSystemWriteCore.Request
            { Directory = 1, Name = disk.Name("Hold"), Length = 4 };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref request, true));
        request = new() { Directory = request.Node, Name = disk.Name("Child"), Length = 5 };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref request, false));
        Inspect(disk.Snapshot());
        return disk.Snapshot();
    }

    private static bool Mutate(ref ReliabilityDisk disk, string scenario, uint victim)
    {
        var work = Work();
        if (scenario.StartsWith("unlink-", StringComparison.Ordinal))
            return AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, victim);
        if (scenario == "archive-resize") return AmigaSmartFileSystemWriteCore.Resize(ref disk, ref work, 3, 100);
        if (scenario == "archive-truncate") return AmigaSmartFileSystemWriteCore.Truncate(ref disk, ref work, 3);
        disk.Ram[4000] = 0xA5;
        var request = new AmigaSmartFileSystemWriteCore.Request
            { Node = scenario == "archive-write" ? 3u : 4u, Buffer = APTR.FromPointer(4000), Length = 1 };
        if (scenario == "archive-write") return AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref request, out _);
        request.Directory = scenario == "rename-same-parent" ? 2u : 1u;
        request.Name = disk.Name("Moved"); request.Length = 5;
        return AmigaSmartFileSystemWriteCore.Rename(ref disk, ref work, ref request);
    }

    [Theory]
    [InlineData("unlink-head", 0)]
    [InlineData("unlink-middle", 1)]
    [InlineData("unlink-tail", 2)]
    [InlineData("unlink-only", -1)]
    [InlineData("rename-same-parent", -1)]
    [InlineData("rename-other-parent", -1)]
    [InlineData("archive-write", -1)]
    [InlineData("archive-resize", -1)]
    [InlineData("archive-truncate", -1)]
    public void ChangedTransactionsSurviveEveryWriteAndFlushFailure(string scenario, int selected)
    {
        uint victim = 4;
        var image = selected >= 0 ? ChainImage(selected, out victim, out _, out _) : Image();
        if (scenario == "rename-same-parent") image = OccupiedFirstAdminSlotImage();
        if (scenario.StartsWith("archive-", StringComparison.Ordinal))
        {
            var seed = new ReliabilityDisk(image); var work = Work();
            var request = new AmigaSmartFileSystemWriteCore.Request { Node = 3, Position = 16 };
            Assert.True(AmigaSmartFileSystemWriteCore.SetMetadata(ref seed, ref work, ref request, DosPacketAction.SetProtection));
            image = seed.Snapshot();
        }
        var before = JsonSerializer.Serialize(Inspect(image).Entries);
        var probe = new ReliabilityDisk(image);
        Assert.True(Mutate(ref probe, scenario, victim));
        var after = JsonSerializer.Serialize(Inspect(probe.Snapshot()).Entries);
        var images = 0;
        for (var cut = 1; cut <= probe.Events; cut++)
        {
            var disk = new ReliabilityDisk(image) { CutAfterEvent = cut };
            Assert.Throws<SimulatedPowerCut>(() => Mutate(ref disk, scenario, victim));
            foreach (var saved in disk.CrashImages())
                ReliabilityTests.CheckRecovered(saved, before, after, $"{scenario}, cut {cut}, image {images++}");
        }
        foreach (var persistent in new[] { false, true })
            for (var fail = 1; fail <= probe.Events; fail++)
            {
                var disk = new ReliabilityDisk(image) { FailAtEvent = fail, PersistentFailure = persistent };
                Assert.False(Mutate(ref disk, scenario, victim), $"Missed failure {scenario}, event {fail}");
                foreach (var saved in disk.CrashImages())
                    ReliabilityTests.CheckRecovered(saved, before, after,
                        $"{scenario}, failure {fail}, persistent {persistent}, image {images++}");
            }
        output.WriteLine($"{scenario}: {probe.Events} write/flush points; {images} mutation crash images, 0 recovery crash images.");
    }
}

// Exercise the current write-back and redo entry points as well as strict CSUN.
public sealed partial class AmigaSmartFileSystemTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArchiveAndLastChildChangesSurviveCheckpointAndReopen(bool buffered)
    {
        var disk = new Disk(ReliabilityFixture.Image()) { Buffered = true, CheckReadOnlyViews = true };
        var work = buffered ? BufferedWork(ref disk) : TrstWork();
        var request = new AmigaSmartFileSystemWriteCore.Request { Node = 3, Position = 16 | 64 };
        bool Apply(AmigaSmartFileSystemWriteCore.TrstOperation operation)
        {
            if (!buffered)
                return AmigaSmartFileSystemWriteCore.ExecuteTrst(ref disk, ref work, ref request,
                    operation, APTR.FromPointer(65536), 65536, out _);
            return operation switch
            {
                AmigaSmartFileSystemWriteCore.TrstOperation.SetProtection =>
                    AmigaSmartFileSystemWriteCore.SetMetadata(ref disk, ref work, ref request, DosPacketAction.SetProtection),
                AmigaSmartFileSystemWriteCore.TrstOperation.Write =>
                    AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref request, out _),
                _ => AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, request.Node)
            };
        }

        Assert.True(Apply(AmigaSmartFileSystemWriteCore.TrstOperation.SetProtection), work.Error.ToString());
        request.Position = 1; request.Buffer = disk.Name("Z"); request.Length = 1;
        Assert.True(Apply(AmigaSmartFileSystemWriteCore.TrstOperation.Write), work.Error.ToString());
        request.Node = 4;
        Assert.True(Apply(AmigaSmartFileSystemWriteCore.TrstOperation.Remove), work.Error.ToString());
        if (buffered) Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work), work.Error.ToString());

        var reopened = new Disk(disk.Snapshot());
        work = TrstWork();
        Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref reopened, ref work));
        var image = reopened.Snapshot();
        var report = ReliabilityFixture.Inspect(image);
        Assert.Equal(64u | 15u, report.Entries.Single(e => e.Node == 3).Protection);
        Assert.DoesNotContain(report.Entries, e => e.Node == 4);
        Assert.Equal(0u, ReliabilityFixture.U32(ReliabilityFixture.Block(image, 7), 40));
        Assert.Equal(1, AmigaSmartFileSystemCore.Read(ref reopened, 3, 1, APTR.FromPointer(6000),
            1, work.Meta, work.Data, out _));
        Assert.Equal((byte)'Z', reopened.Ram[6000]);
    }
}
