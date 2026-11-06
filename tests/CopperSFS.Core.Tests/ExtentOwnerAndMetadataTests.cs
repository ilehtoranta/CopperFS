// Compatibility fixtures and crash cases adapted from CopperSFS research commit 4a902a1.
using System.Text.Json;
using Amiga;
using Xunit.Abstractions;
using static CopperSFS.Tests.ReliabilityFixture;

namespace CopperSFS.Tests;

public sealed class ExtentOwnerAndMetadataTests(ITestOutputHelper output)
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

    private static byte[] NativeExtentImage()
    {
        var image = Image();
        Inspect(image);
        // Original SFS addblocks(): the first extent has the object's node
        // number with the high bit set, rather than a zero previous pointer.
        U32(Block(image, 5), 24, 0x80000003);
        U32(Block(image, 5), 52, 0x80000004);
        Seal(Block(image, 5));
        return image;
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("truncate")]
    [InlineData("resize-zero")]
    public void NativeFirstExtentOwnerMustNotBeTreatedAsAnExtentKey(string operation)
    {
        var image = NativeExtentImage();
        var disk = new ReliabilityDisk(image); var work = Work();
        Assert.True(AmigaSmartFileSystemCore.Mount(ref disk, work.Meta));
        Assert.Equal(1300, AmigaSmartFileSystemCore.Read(ref disk, 3, 0, APTR.FromPointer(5000),
            1300, work.Meta, work.Data, out var readError));
        Assert.Equal(DOS.Error.None, readError);
        var ok = operation switch
        {
            "remove" => AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, 3),
            "truncate" => AmigaSmartFileSystemWriteCore.Truncate(ref disk, ref work, 3),
            _ => AmigaSmartFileSystemWriteCore.Resize(ref disk, ref work, 3, 0)
        };
        var error = work.Error;
        output.WriteLine($"{operation}: success={ok}, error={error}");
        if (!ok)
        {
            work = Work();
            Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref disk, ref work), work.Error.ToString());
            Assert.Equal(1300, AmigaSmartFileSystemCore.Read(ref disk, 3, 0, APTR.FromPointer(5000),
                1300, work.Meta, work.Data, out _));
        }
        Assert.True(ok, $"{operation} on a readable native extent chain failed: {error}");
    }

    [Theory]
    [InlineData(false, 1u)]
    [InlineData(true, 1u)]
    [InlineData(false, 1024u)]
    public void NewFirstExtentMustRecordItsOwner(bool resize, uint length)
    {
        var disk = new ReliabilityDisk(Image()); var node = Create(ref disk, "Owner");
        var work = Work(); disk.Ram[4000] = 0xA7;
        var request = new AmigaSmartFileSystemWriteCore.Request
            { Node = node, Buffer = APTR.FromPointer(4000), Length = length };
        Assert.True(resize ? AmigaSmartFileSystemWriteCore.Resize(ref disk, ref work, node, length) :
            AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref request, out _), work.Error.ToString());
        var image = disk.Snapshot(); Inspect(image);
        var tree = Block(image, 5);
        for (var offset = 16; offset < 16 + 4 * 14; offset += 14)
        {
            if (U32(tree, offset) is 40 or 43 or 44) continue;
            var previous = U32(tree, offset + 8);
            output.WriteLine($"New node {node}, first extent {U32(tree, offset)}, previous=0x{previous:X8}");
            Assert.Equal(0x80000000u | node, previous);
            return;
        }
        Assert.Fail("No new extent found.");
    }

    [Fact]
    public void CheckerMustAcceptNativeFirstExtentOwners()
    {
        Inspect(NativeExtentImage());
    }

    [Fact]
    public void CommentGrowthMustRelocateAnObjectWhenItsContainerIsFull()
    {
        var disk = new ReliabilityDisk(Image());
        for (var i = 0; i < 12; i++) Create(ref disk, $"F{i:D3}");
        var before = Inspect(disk.Snapshot()); var work = Work();
        disk.Ram.AsSpan(4000, 79).Fill((byte)'c');
        var request = new AmigaSmartFileSystemWriteCore.Request
            { Node = 2, Buffer = APTR.FromPointer(4000), Length = 79 };
        var ok = AmigaSmartFileSystemWriteCore.SetMetadata(ref disk, ref work, ref request, DosPacketAction.SetComment);
        Assert.True(ok, $"SetComment failed with {before.FreeBlocks} free blocks: {work.Error}");
        Assert.Equal(before.Entries.Select(e => e.Node == 2 ? e with { Comment = new string('c', 79) } : e).ToArray(),
            Inspect(disk.Snapshot()).Entries.ToArray());
    }

    private static byte[] FullAdminPool()
    {
        var image = Image(); var admin = Block(image, 10); var bitmap = Block(image, 9);
        U32(admin, 32, 0); U32(admin, 36, 0);
        for (var block = 64; block < 96; block++) bitmap[12 + block / 8] |= (byte)(0x80 >> (block % 8));
        Seal(bitmap);
        U32(Block(image, 2), 484, U32(Block(image, 2), 484) + 32); Seal(Block(image, 2));
        // Fill the remaining admin slots with valid empty containers in the root directory chain.
        var emptyContainers = new[] { 1 }.Concat(Enumerable.Range(12, 21)).ToArray();
        U32(Block(image, 7), 16, (uint)emptyContainers[0]); Seal(Block(image, 7));
        for (var i = 0; i < emptyContainers.Length; i++)
        {
            var number = emptyContainers[i]; var container = Block(image, number); container.Clear();
            U32(container, 0, 0x4F424A43); U32(container, 8, (uint)number); U32(container, 12, 1);
            U32(container, 16, i + 1 < emptyContainers.Length ? (uint)emptyContainers[i + 1] : 0);
            U32(container, 20, i == 0 ? 7u : (uint)emptyContainers[i - 1]); Seal(container);
        }
        U32(admin, 28, uint.MaxValue); Seal(admin);
        Inspect(image);
        return image;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RenameMustBeAbleToGrowAdminSpace(bool directory)
    {
        var disk = new ReliabilityDisk(FullAdminPool());
        var destination = Create(ref disk, "Target", directory: true);
        var before = Inspect(disk.Snapshot()); var work = Work();
        var request = new AmigaSmartFileSystemWriteCore.Request
            { Node = directory ? 2u : 3u, Directory = destination, Name = disk.Name("Moved"), Length = 5 };
        var ok = AmigaSmartFileSystemWriteCore.Rename(ref disk, ref work, ref request); var error = work.Error;
        output.WriteLine($"Rename: success={ok}, error={error}, free blocks={before.FreeBlocks}");
        if (!ok)
        {
            work = Work(); Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref disk, ref work));
            Assert.Equal(before.Entries.ToArray(), Inspect(disk.Snapshot()).Entries.ToArray());
        }
        Assert.True(ok, $"Rename cannot grow admin space despite {before.FreeBlocks} free blocks: {error}");
        var oldPath = directory ? "/Docs" : "/Hello";
        Assert.Equal(before.Entries.Select(e => e.Path == oldPath || e.Path.StartsWith(oldPath + "/", StringComparison.Ordinal)
                ? e with { Path = "/Target/Moved" + e.Path[oldPath.Length..] } : e)
            .OrderBy(e => e.Path, StringComparer.Ordinal).ToArray(), Inspect(disk.Snapshot()).Entries.ToArray());
    }

    private static byte[] CrashSeed(string scenario)
    {
        if (scenario == "rename-admin-growth")
        {
            var full = new ReliabilityDisk(FullAdminPool());
            Create(ref full, "Target", directory: true);
            return full.Snapshot();
        }
        var grow = scenario == "comment-admin-growth";
        var disk = new ReliabilityDisk(grow ? FullAdminPool() : Image());
        for (var i = 0; i < (grow ? 13 : 12); i++) Create(ref disk, $"F{i:D3}", grow ? 2u : 1u);
        return disk.Snapshot();
    }

    private static bool CrashMutation(ref ReliabilityDisk disk, string scenario)
    {
        var work = Work();
        if (scenario == "rename-admin-growth")
        {
            var rename = new AmigaSmartFileSystemWriteCore.Request
                { Node = 3, Directory = 1, Name = disk.Name("Target/Moved"), Length = 12 };
            return AmigaSmartFileSystemWriteCore.Rename(ref disk, ref work, ref rename);
        }
        disk.Ram.AsSpan(4000, 79).Fill((byte)'c');
        var comment = new AmigaSmartFileSystemWriteCore.Request
            { Node = scenario == "comment-admin-growth" ? 4u : 2u, Buffer = APTR.FromPointer(4000), Length = 79 };
        return AmigaSmartFileSystemWriteCore.SetMetadata(ref disk, ref work, ref comment, DosPacketAction.SetComment);
    }

    [Theory]
    [InlineData("rename-admin-growth")]
    [InlineData("comment-relocation")]
    [InlineData("comment-admin-growth")]
    public void CompatibilityMutationsSurviveEveryWriteAndFlushFailure(string scenario)
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

public sealed partial class AmigaSmartFileSystemTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RelocatedFileRetainsItsDataAndDirectoryEntryAfterReopen(bool buffered)
    {
        var image = PackedObjectTests.PackedImage(0);
        var before = ReliabilityFixture.Inspect(image);
        var disk = new Disk(image) { Buffered = true, CheckReadOnlyViews = true };
        var work = buffered ? BufferedWork(ref disk) : TrstWork();
        var comment = new string('c', 79);
        var request = new AmigaSmartFileSystemWriteCore.Request
            { Node = 3, Buffer = disk.Name(comment), Length = 79 };
        if (buffered)
        {
            Assert.True(AmigaSmartFileSystemWriteCore.SetMetadata(ref disk, ref work, ref request,
                DosPacketAction.SetComment), work.Error.ToString());
            Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work), work.Error.ToString());
        }
        else
        {
            Assert.True(AmigaSmartFileSystemWriteCore.ExecuteTrst(ref disk, ref work, ref request,
                AmigaSmartFileSystemWriteCore.TrstOperation.SetComment, APTR.FromPointer(65536), 65536, out _),
                work.Error.ToString());
        }
        var reopened = new Disk(disk.Snapshot());
        var recovered = TrstWork();
        Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref reopened, ref recovered));
        var saved = reopened.Snapshot();
        Assert.NotEqual(7u, ReliabilityFixture.U32(ReliabilityFixture.Block(saved, 6), 20 + 2 * 10));
        Assert.Equal(before.Entries.Select(e => e.Node == 3 ? e with { Comment = comment } : e).ToArray(),
            ReliabilityFixture.Inspect(saved).Entries.ToArray());
    }
}
