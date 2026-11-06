// Adapted from FileSystemRegressionTests in research commit 4a902a1.
using Amiga;
using System.Text.Json;
using Xunit.Abstractions;
using static CopperSFS.Tests.ReliabilityFixture;

namespace CopperSFS.Tests;

public sealed class NodeBranchGrowthTests(ITestOutputHelper output)
{
    private static AmigaSmartFileSystemWriteCore.Work Work() => new()
    {
        Meta = APTR.FromPointer(512), Data = APTR.FromPointer(1024),
        Bitmap = APTR.FromPointer(1536), Journal = APTR.FromPointer(2048)
    };

    private static uint Create(ref ReliabilityDisk disk, string name, uint parent = 1, bool directory = false)
    {
        var work = Work();
        var request = new AmigaSmartFileSystemWriteCore.Request
        { Directory = parent, Name = disk.Name(name), Length = (uint)name.Length };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref request, directory),
            $"Create {name}: {work.Error}");
        return request.Node;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NodeTreeMustUseEmptyBranchesBeforeReportingDiskFull(bool nestedIndex)
    {
        var image = Image();
        if (nestedIndex)
        {
            var root = Block(image, 6); root.CopyTo(Block(image, 14));
            U32(Block(image, 14), 8, 14); Seal(Block(image, 14));
            root.Clear(); U32(root, 0, 0x4E444320); U32(root, 8, 6); U32(root, 12, 1);
            U32(root, 16, 49); U32(root, 20, 14 << 4); Seal(root);
            root.CopyTo(Block(image, 15)); U32(Block(image, 15), 8, 15); Seal(Block(image, 15));
            U32(root, 16, 49 * 123); U32(root, 20, 15 << 4); Seal(root);
            var admin = Block(image, 10);
            U32(admin, 28, U32(admin, 28) | (0x80000000u >> 13) | (0x80000000u >> 14)); Seal(admin);
        }
        Inspect(image);
        var disk = new ReliabilityDisk(image); var createdNodes = new List<uint>();
        for (var i = 0; i < 300; i++)
        {
            var work = Work(); var name = $"F{i:D3}";
            var request = new AmigaSmartFileSystemWriteCore.Request
            { Directory = 1, Name = disk.Name(name), Length = (uint)name.Length };
            var created = AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref request, false);
            if (!created)
            {
                var originalError = work.Error;
                var recovery = Work();
                Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref disk, ref recovery));
                var report = Inspect(disk.Snapshot());
                Assert.Fail($"Create failed after {i} new empty files, {report.Entries.Count} total objects, " +
                    $"{report.FreeBlocks} free blocks: {originalError}. Root stride={U32(Block(disk.Snapshot(), 6), 16)}");
            }
            createdNodes.Add(request.Node);
        }
        var restarted = new ReliabilityDisk(disk.Snapshot()); var readWork = Work();
        Assert.True(AmigaSmartFileSystemCore.Mount(ref restarted, readWork.Meta));
        var saved = restarted.Snapshot();
        Assert.Equal(304, Inspect(saved).Entries.Count);
        Assert.Equal(nestedIndex ? 49u * 123 : 49u, U32(Block(saved, 6), 16));
        for (var i = 0; i < createdNodes.Count; i++)
            Assert.Equal(createdNodes[i], AmigaSmartFileSystemCore.Resolve(ref restarted, 1,
                restarted.Name($"F{i:D3}"), 4, readWork.Meta, out _));
    }

    private static byte[] CrashSeed()
    {
        var disk = new ReliabilityDisk(Image());
        for (var i = 0; i < 94; i++) Create(ref disk, $"F{i:D3}");
        Inspect(disk.Snapshot());
        return disk.Snapshot();
    }

    private static bool CrashMutation(ref ReliabilityDisk disk)
    {
        var work = Work();
        var request = new AmigaSmartFileSystemWriteCore.Request
        { Directory = 1, Name = disk.Name("Extra"), Length = 5 };
        return AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref request, false);
    }

    [Fact]
    public void NodeBranchGrowthSurvivesWriteAndFlushFailures()
    {
        const string scenario = "node-branch-growth";
        var image = CrashSeed();
        // Two full 49-node leaves. The next create must install the third leaf
        // in the existing index, so every injected failure exercises that allocation.
        Assert.Equal(49u, U32(Block(image, 6), 16));
        Assert.Equal(0u, U32(Block(image, 6), 28));
        var before = JsonSerializer.Serialize(Inspect(image).Entries);
        var probe = new ReliabilityDisk(image);
        Assert.True(CrashMutation(ref probe));
        var grown = probe.Snapshot();
        Assert.Equal(49u, U32(Block(grown, 6), 16));
        var child = U32(Block(grown, 6), 28) >> 4;
        Assert.NotEqual(0u, child);
        Assert.Equal(99u, U32(Block(grown, (int)child), 12));
        var after = JsonSerializer.Serialize(Inspect(grown).Entries);
        var images = 0;
        for (var cut = 1; cut <= probe.Events; cut++)
        {
            var disk = new ReliabilityDisk(image) { CutAfterEvent = cut };
            Assert.Throws<SimulatedPowerCut>(() => CrashMutation(ref disk));
            foreach (var saved in disk.CrashImages())
                ReliabilityTests.CheckRecovered(saved, before, after, $"{scenario}, cut {cut}, image {images++}");
        }
        foreach (var persistent in new[] { false, true })
            for (var fail = 1; fail <= probe.Events; fail++)
            {
                var disk = new ReliabilityDisk(image) { FailAtEvent = fail, PersistentFailure = persistent };
                Assert.False(CrashMutation(ref disk), $"Missed failure {scenario}, event {fail}");
                foreach (var saved in disk.CrashImages())
                    ReliabilityTests.CheckRecovered(saved, before, after,
                        $"{scenario}, failure {fail}, persistent {persistent}, image {images++}");
            }
        output.WriteLine($"{scenario}: {probe.Writes} writes, {probe.Events} write/flush points; {images} mutation crash images, 0 recovery crash images.");
    }
}
