using Amiga;
using System.Text.Json;
using Xunit.Abstractions;
using static CopperSFS.Tests.ReliabilityFixture;

namespace CopperSFS.Tests;

public sealed class FileSystemRegressionTests(ITestOutputHelper output)
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

    private static byte[] IndexedImage(int levels = 1)
    {
        var image = Image();
        var tree = Block(image, 5).ToArray();
        var left = Block(image, 12); left.Clear();
        U32(left, 0, 0x424E4443); U32(left, 8, 12); U16(left, 12, 2); left[14] = 1; left[15] = 14;
        tree.AsSpan(16, 28).CopyTo(left[16..]); Seal(left);
        var right = Block(image, 13); right.Clear();
        U32(right, 0, 0x424E4443); U32(right, 8, 13); U16(right, 12, 1); right[14] = 1; right[15] = 14;
        tree.AsSpan(44, 14).CopyTo(right[16..]); Seal(right);
        var root = Block(image, 5); root.Clear();
        U32(root, 0, 0x424E4443); U32(root, 8, 5); U16(root, 12, 2); root[15] = 8;
        U32(root, 16, 0); U32(root, 20, 12); U32(root, 24, 44); U32(root, 28, 13); Seal(root);
        var admin = Block(image, 10);
        U32(admin, 28, U32(admin, 28) | (0x80000000u >> 11) | (0x80000000u >> 12)); Seal(admin);
        for (var level = 1; level < levels; level++)
        {
            var child = 13 + level;
            root.CopyTo(Block(image, child)); U32(Block(image, child), 8, (uint)child); Seal(Block(image, child));
            root.Clear(); U32(root, 0, 0x424E4443); U32(root, 8, 5); U16(root, 12, 1); root[15] = 8;
            U32(root, 20, (uint)child); Seal(root);
            U32(admin, 28, U32(admin, 28) | (0x80000000u >> (child - 1))); Seal(admin);
        }
        Inspect(image);
        return image;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CommittedWriteAfterPruningLowestExtentLeafMustRemainReadable(int levels)
    {
        var disk = new ReliabilityDisk(IndexedImage(levels)); var work = Work();
        var untouched = Inspect(disk.Snapshot()).Entries.Where(e => e.Node != 3).ToArray();
        Assert.True(AmigaSmartFileSystemWriteCore.Truncate(ref disk, ref work, 3), work.Error.ToString());
        Inspect(disk.Snapshot());
        disk.Ram[4000] = 0xA7;
        var request = new AmigaSmartFileSystemWriteCore.Request
        { Node = 3, Buffer = APTR.FromPointer(4000), Length = 1 };
        work = Work();
        Assert.True(AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref request, out var actual), work.Error.ToString());
        Assert.Equal(1u, actual);
        var durable = disk.Snapshot();
        Assert.Equal(untouched, Inspect(durable).Entries.Where(e => e.Node != 3).ToArray());
        var restarted = new ReliabilityDisk(durable); work = Work();
        Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref restarted, ref work));
        Assert.True(AmigaSmartFileSystemCore.Mount(ref restarted, work.Meta));
        var read = AmigaSmartFileSystemCore.Read(ref restarted, 3, 0, APTR.FromPointer(5000), 1, work.Meta, work.Data, out var error);
        Assert.True(read == 1 && restarted.Ram[5000] == 0xA7, $"Committed data unreadable: read={read}, error={error}");
    }

    [Fact]
    public void WriteBelowExistingExtentIndexMinimumUpdatesBounds()
    {
        var image = IndexedImage(2);
        // A valid tree may use its actual minimum instead of a zero sentinel.
        U32(Block(image, 5), 16, 40); Seal(Block(image, 5));
        U32(Block(image, 14), 16, 40); Seal(Block(image, 14));
        Inspect(image);
        var disk = new ReliabilityDisk(image); var node = Create(ref disk, "BelowMinimum");
        var work = Work(); disk.Ram[4000] = 0xA7;
        var request = new AmigaSmartFileSystemWriteCore.Request { Node = node, Buffer = APTR.FromPointer(4000), Length = 1 };
        Assert.True(AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref request, out var actual), work.Error.ToString());
        Assert.Equal(1u, actual);
        var restarted = new ReliabilityDisk(disk.Snapshot()); Inspect(restarted.Snapshot());
        work = Work();
        Assert.Equal(1, AmigaSmartFileSystemCore.Read(ref restarted, node, 0, APTR.FromPointer(5000), 1,
            work.Meta, work.Data, out var error));
        Assert.Equal(DOS.Error.None, error); Assert.Equal(0xA7, restarted.Ram[5000]);
    }

    [Fact]
    public void RemoveMustWorkAfterExtentTreeSplits()
    {
        var disk = new ReliabilityDisk(Image());
        var nodes = new List<uint>();
        for (var i = 0; i < 33; i++)
        {
            var node = Create(ref disk, $"F{i:D3}");
            nodes.Add(node);
            var work = Work(); disk.Ram[4000] = (byte)i;
            var request = new AmigaSmartFileSystemWriteCore.Request
            { Node = node, Buffer = APTR.FromPointer(4000), Length = 1 };
            Assert.True(AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref request, out _), work.Error.ToString());
        }
        var before = Inspect(disk.Snapshot());
        Assert.Equal(0, Block(disk.Snapshot(), 5)[14]);
        var removeWork = Work();
        var removed = AmigaSmartFileSystemWriteCore.Remove(ref disk, ref removeWork, nodes[0]);
        Assert.True(removed, $"Remove after extent split: {removeWork.Error}");
        Assert.Equal(before.Entries.Where(e => e.Node != nodes[0]).ToArray(), Inspect(disk.Snapshot()).Entries.ToArray());
        // Remove all remaining extents, including the fragmented original file,
        // so pruning both sides of the split is covered as well.
        foreach (var node in nodes.Skip(1).Concat(new uint[] { 3, 4 }))
        {
            removeWork = Work();
            Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref disk, ref removeWork, node), removeWork.Error.ToString());
        }
        Assert.Equal(new uint[] { 1, 2 }, Inspect(disk.Snapshot()).Entries.Select(e => e.Node).ToArray());
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
        Assert.Equal(304, Inspect(restarted.Snapshot()).Entries.Count);
        for (var i = 0; i < createdNodes.Count; i++)
            Assert.Equal(createdNodes[i], AmigaSmartFileSystemCore.Resolve(ref restarted, 1,
                restarted.Name($"F{i:D3}"), 4, readWork.Meta, out _));
    }

    public static IEnumerable<object[]> ParentPaths =>
        from rename in new[] { false, true }
        from path in new (uint From, string Name, uint Parent)[]
        {
            (2, "/ParentFile", 1), (2, "//ParentFile", 1),
            (1, "Docs//ParentFile", 1), (1, "Docs/ParentFile", 2),
            (2, "SFSVOL:Docs//ParentFile", 1), (2, "SFSVOL:ParentFile", 1)
        }
        select new object[] { rename, path.From, path.Name, path.Parent };

    [Theory]
    [MemberData(nameof(ParentPaths))]
    public void CreateAndRenameAgreeWithResolveForParentRelativePaths(bool rename, uint from, string path, uint parent)
    {
        var disk = new ReliabilityDisk(Image());
        var work = Work();
        var request = new AmigaSmartFileSystemWriteCore.Request
        { Node = 3, Directory = from, Name = disk.Name(path), Length = (uint)path.Length };
        Assert.True(rename ? AmigaSmartFileSystemWriteCore.Rename(ref disk, ref work, ref request) :
            AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref request, false), work.Error.ToString());
        var restarted = new ReliabilityDisk(disk.Snapshot()); Inspect(restarted.Snapshot());
        var found = AmigaSmartFileSystemCore.Resolve(ref restarted, from, restarted.Name(path), (uint)path.Length, work.Meta, out var error);
        Assert.Equal(DOS.Error.None, error); Assert.Equal(request.Node, found);
        Assert.True(AmigaSmartFileSystemCore.Describe(ref restarted, found, work.Meta, out var size, out _, out var actualParent));
        Assert.Equal(parent, actualParent); Assert.Equal(rename ? 1300u : 0, size);
        if (rename)
        {
            Assert.Equal(1300, AmigaSmartFileSystemCore.Read(ref restarted, found, 0, APTR.FromPointer(5000), 1300,
                work.Meta, work.Data, out error));
            Assert.Equal(DOS.Error.None, error);
            Assert.Equal(Enumerable.Range(0, 1300).Select(i => (byte)(i * 17)), restarted.Ram.AsSpan(5000, 1300).ToArray());
        }
    }

    private static byte[] FullAdminImage()
    {
        var seed = new ReliabilityDisk(Image());
        for (var i = 0; i < 45; i++) Create(ref seed, $"F{i:D3}");
        var old = seed.Snapshot(); Inspect(old);
        const int largeBlocks = 2304, adminBlocks = 61 * 32;
        var image = new byte[largeBlocks * Bytes]; old.CopyTo(image, 0);
        Block(old, Blocks - 1).CopyTo(Block(image, largeBlocks - 1));
        foreach (var number in new[] { 0, largeBlocks - 1 })
        {
            var root = Block(image, number);
            U32(root, 8, (uint)number); U32(root, 44, largeBlocks * Bytes); U32(root, 48, largeBlocks); Seal(root);
        }
        foreach (var (from, to) in new[] { (40, 2000), (41, 2001), (43, 2002), (44, 2003) })
            Block(old, from).CopyTo(Block(image, to));
        var tree = Block(image, 5);
        U32(tree, 16, 2000); U32(tree, 20, 2002); U32(tree, 30, 2002); U32(tree, 38, 2000); U32(tree, 44, 2003); Seal(tree);
        U32(Block(image, 7), 68, 2000); Seal(Block(image, 7));
        U32(Block(image, 8), 36, 2003); Seal(Block(image, 8));
        var metadata = new HashSet<int>();
        var oldAdmin = Block(old, 10);
        for (var at = 24; at + 8 <= Bytes; at += 8)
        {
            var first = U32(oldAdmin, at); if (first == 0) continue;
            for (var bit = 0; bit < 32; bit++)
                if ((U32(oldAdmin, at + 4) & (0x80000000u >> bit)) != 0) metadata.Add((int)first + bit);
        }
        var tail = 7;
        while (U32(Block(image, tail), 16) != 0) tail = (int)U32(Block(image, tail), 16);
        // Reachable empty directory containers consume every admin slot without
        // forging orphan allocations. The independent checker validates this seed.
        for (var number = 1; number <= adminBlocks; number++)
        {
            if (metadata.Contains(number)) continue;
            U32(Block(image, tail), 16, (uint)number); Seal(Block(image, tail));
            var container = Block(image, number); container.Clear();
            U32(container, 0, 0x4F424A43); U32(container, 8, (uint)number);
            U32(container, 12, 1); U32(container, 20, (uint)tail); Seal(container);
            tail = number;
        }
        var admin = Block(image, 10); admin[24..].Clear(); admin[20] = 32;
        for (var i = 0; i < 61; i++)
        { U32(admin, 24 + i * 8, (uint)(1 + i * 32)); U32(admin, 28 + i * 8, uint.MaxValue); }
        Seal(admin);
        var bitmap = Block(image, 9); bitmap[12..].Fill(255);
        foreach (var number in Enumerable.Range(0, adminBlocks + 1).Concat(new[] { 2000, 2001, 2002, 2003, largeBlocks - 1 }))
            bitmap[12 + number / 8] &= (byte)~(0x80 >> (number % 8));
        Seal(bitmap);
        U32(Block(image, 2), Bytes - 28, largeBlocks - adminBlocks - 6); Seal(Block(image, 2));
        Inspect(image);
        return image;
    }

    [Fact]
    public void NewAdminContainerUsesReferenceBitCount()
    {
        var disk = new ReliabilityDisk(FullAdminImage()); var work = Work();
        // Keep journal allocations away from the newly reserved admin range so
        // this test isolates the on-disk admin header from journal aliasing.
        work.JournalCursor = 2100;
        var request = new AmigaSmartFileSystemWriteCore.Request
        { Directory = 1, Name = disk.Name("Extra"), Length = 5 };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref request, false), work.Error.ToString());
        var image = disk.Snapshot(); var newAdmin = U32(Block(image, 10), 12);
        Assert.NotEqual(0u, newAdmin);
        Assert.Equal(32, Block(image, (int)newAdmin)[20]);
        Inspect(image);
    }

    [Fact]
    public void MetadataGrowthEvictsSoftReservationsBeforeReportingDiskFull()
    {
        var disk = new ReliabilityDisk(FullAdminImage()); var work = Work();
        work.Reservations = APTR.FromPointer(8192);
        // Two still-free tails split the remaining space into runs smaller
        // than an admin group, while leaving ample journal space outside them.
        disk.WriteUInt32(work.Reservations, 0, 2);
        disk.WriteUInt32(work.Reservations, 4, 2);
        disk.WriteUInt32(work.Reservations, 8, 1953);
        disk.WriteUInt32(work.Reservations, 12, 20);
        disk.WriteUInt32(work.Reservations, 36, 3);
        disk.WriteUInt32(work.Reservations, 40, 2004);
        disk.WriteUInt32(work.Reservations, 44, 279);
        var request = new AmigaSmartFileSystemWriteCore.Request
            { Directory = 1, Name = disk.Name("Extra"), Length = 5 };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref request, false), work.Error.ToString());
        Assert.Equal(0u, disk.ReadUInt32(work.Reservations));
        Inspect(disk.Snapshot());
    }

    [Fact]
    public void MetadataGrowthMustNotAllocateItsOwnUndoBlocks()
    {
        var disk = new ReliabilityDisk(FullAdminImage()); var work = Work();
        var request = new AmigaSmartFileSystemWriteCore.Request
        { Directory = 1, Name = disk.Name("Extra"), Length = 5 };
        var created = AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref request, false);
        Assert.True(created, $"Metadata expansion failed with {work.Error} despite 346 initially free blocks");
        var restarted = new ReliabilityDisk(disk.Snapshot()); work = Work();
        Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref restarted, ref work));
        Inspect(restarted.Snapshot());
        Assert.Equal(request.Node, AmigaSmartFileSystemCore.Resolve(ref restarted, 1, restarted.Name("Extra"), 5, work.Meta, out _));
    }

    private static byte[] CrashSeed(string scenario)
    {
        if (scenario == "admin-container-growth") return FullAdminImage();
        if (scenario is "extent-prune" or "indexed-delete") return IndexedImage(2);
        var disk = new ReliabilityDisk(Image());
        for (var i = 0; i < 94; i++) Create(ref disk, $"F{i:D3}");
        Inspect(disk.Snapshot());
        return disk.Snapshot();
    }

    private static bool CrashMutation(ref ReliabilityDisk disk, string scenario)
    {
        var work = Work();
        if (scenario == "extent-prune") return AmigaSmartFileSystemWriteCore.Truncate(ref disk, ref work, 3);
        if (scenario == "indexed-delete") return AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, 3);
        var request = new AmigaSmartFileSystemWriteCore.Request
        { Directory = 1, Name = disk.Name("Extra"), Length = 5 };
        return AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref request, false);
    }

    [Theory]
    [InlineData("admin-container-growth")]
    [InlineData("node-branch-growth")]
    [InlineData("extent-prune")]
    [InlineData("indexed-delete")]
    public void ChangedAllocationAndPruningPathsSurviveWriteAndFlushFailures(string scenario)
    {
        var image = CrashSeed(scenario);
        var before = JsonSerializer.Serialize(Inspect(image).Entries);
        var probe = new ReliabilityDisk(image);
        Assert.True(CrashMutation(ref probe, scenario));
        var after = JsonSerializer.Serialize(Inspect(probe.Snapshot()).Entries);
        var images = 0;
        for (var cut = 1; cut <= probe.Events; cut++)
        {
            var disk = new ReliabilityDisk(image) { CutAfterEvent = cut };
            Assert.Throws<SimulatedPowerCut>(() => CrashMutation(ref disk, scenario));
            foreach (var saved in disk.CrashImages())
                ReliabilityTests.CheckRecovered(saved, before, after, $"{scenario}, cut {cut}, image {images++}");
        }
        foreach (var persistent in new[] { false, true })
            for (var fail = 1; fail <= probe.Events; fail++)
            {
                var disk = new ReliabilityDisk(image) { FailAtEvent = fail, PersistentFailure = persistent };
                Assert.False(CrashMutation(ref disk, scenario), $"Missed failure {scenario}, event {fail}");
                foreach (var saved in disk.CrashImages())
                    ReliabilityTests.CheckRecovered(saved, before, after,
                        $"{scenario}, failure {fail}, persistent {persistent}, image {images++}");
            }
        output.WriteLine($"{scenario}: {probe.Writes} writes, {probe.Events} write/flush points; {images} mutation crash images, 0 recovery crash images.");
    }
}
