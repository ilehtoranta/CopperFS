using System.Text;
using System.Text.Json;
using Amiga;
using Xunit.Abstractions;
using static CopperSFS.Tests.ReliabilityFixture;

namespace CopperSFS.Tests;

public sealed class FileSystemCompatibilityTests(ITestOutputHelper output)
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
    [InlineData(false)]
    [InlineData(true)]
    public void NewFirstExtentMustRecordItsOwner(bool resize)
    {
        var disk = new ReliabilityDisk(Image()); var node = Create(ref disk, "Owner");
        var work = Work(); disk.Ram[4000] = 0xA7;
        var request = new AmigaSmartFileSystemWriteCore.Request
            { Node = node, Buffer = APTR.FromPointer(4000), Length = 1 };
        Assert.True(resize ? AmigaSmartFileSystemWriteCore.Resize(ref disk, ref work, node, 1) :
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

    [Theory]
    [InlineData(32)]
    [InlineData(2)]
    public void CheckerAcceptsReferenceAndLegacyCopperAdminHeaders(byte bits)
    {
        var image = Image(); Inspect(image);
        Block(image, 10)[20] = bits; Seal(Block(image, 10));
        var disk = new ReliabilityDisk(image); var work = Work();
        Assert.True(AmigaSmartFileSystemCore.Mount(ref disk, work.Meta));
        Inspect(image);
    }

    private static int AddObject(Span<byte> block, int at, uint node, string name, string comment)
    {
        U32(block, at + 4, node); U32(block, at + 8, 15);
        Encoding.Latin1.GetBytes(name).CopyTo(block[(at + 25)..]);
        Encoding.Latin1.GetBytes(comment).CopyTo(block[(at + 26 + name.Length)..]);
        return (at + 28 + name.Length + comment.Length) & ~1;
    }

    private static byte[] PackedImage(int tailBytes)
    {
        var image = Image(); var end = 96;
        for (uint node = 5; node <= 9; node++)
        {
            var name = $"Packed{node:D2}" + new string('P', 42);
            Assert.Equal(50, name.Length);
            end = AddObject(Block(image, 7), end, node, name, node == 9 ? new string('c', 26 - tailBytes) : "");
            ushort hash = (ushort)name.Length;
            foreach (var c in name.ToUpperInvariant()) hash = unchecked((ushort)(hash * 13 + c));
            var bucket = 16 + hash % 124 * 4;
            var slot = 20 + ((int)node - 1) * 10;
            U32(Block(image, 6), slot, 7);
            U32(Block(image, 6), slot + 4, U32(Block(image, 3), bucket));
            U16(Block(image, 6), slot + 8, hash);
            U32(Block(image, 3), bucket, node);
        }
        Assert.Equal(512 - tailBytes, end);
        foreach (var block in new[] { 3, 6, 7 }) Seal(Block(image, block));
        Assert.Equal(9, Inspect(image).Entries.Count);
        return image;
    }

    [Theory]
    [InlineData(0, "create")]
    [InlineData(26, "create")]
    [InlineData(0, "remove")]
    [InlineData(26, "remove")]
    [InlineData(0, "rename")]
    [InlineData(26, "rename")]
    [InlineData(0, "comment")]
    [InlineData(26, "comment")]
    public void ValidPackedObjectContainersMustRemainWritable(int tailBytes, string operation)
    {
        var disk = new ReliabilityDisk(PackedImage(tailBytes)); var work = Work();
        var before = Inspect(disk.Snapshot());
        Assert.Equal(1300, AmigaSmartFileSystemCore.Read(ref disk, 3, 0, APTR.FromPointer(5000),
            1300, work.Meta, work.Data, out _));
        var request = new AmigaSmartFileSystemWriteCore.Request
            { Directory = 1, Node = 3, Name = disk.Name("New"), Length = 3, Buffer = APTR.FromPointer(4000) };
        Encoding.Latin1.GetBytes("new").CopyTo(disk.Ram, 4000);
        var ok = operation switch
        {
            "create" => AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref request, false),
            "remove" => AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, 3),
            "rename" => AmigaSmartFileSystemWriteCore.Rename(ref disk, ref work, ref request),
            _ => AmigaSmartFileSystemWriteCore.SetMetadata(ref disk, ref work, ref request, DosPacketAction.SetComment)
        };
        Assert.True(ok, $"{operation} rejected a valid container with {tailBytes} unused tail bytes: {work.Error}");
        var after = Inspect(disk.Snapshot());
        var expected = before.Entries.Where(e => operation != "remove" || e.Node != 3)
            .Select(e => e.Node != 3 ? e : operation switch
            {
                "rename" => e with { Path = "/New" },
                "comment" => e with { Comment = "new" },
                _ => e
            }).OrderBy(e => e.Path, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, after.Entries.Where(e => operation != "create" || e.Node != request.Node).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void EmbeddedNullCommentMustNotHideOtherObjects(int nullOffset)
    {
        var image = Image(); var disk = new ReliabilityDisk(image); var work = Work();
        disk.Ram.AsSpan(4000, 6).Fill((byte)'x'); disk.Ram[4000 + nullOffset] = 0;
        var request = new AmigaSmartFileSystemWriteCore.Request
            { Node = 2, Buffer = APTR.FromPointer(4000), Length = 6 };
        var ok = AmigaSmartFileSystemWriteCore.SetMetadata(ref disk, ref work, ref request, DosPacketAction.SetComment);
        Assert.False(ok); Assert.Equal(DOS.Error.BadNumber, work.Error);
        Assert.Equal(0, disk.Writes); Assert.Equal(image, disk.Snapshot());
        output.WriteLine($"SetComment success={ok}, error={work.Error}, marker=0x{U32(Block(disk.Snapshot(), 4), 0):X8}");
        var restarted = new ReliabilityDisk(disk.Snapshot()); work = Work();
        Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref restarted, ref work));
        var read = AmigaSmartFileSystemCore.Read(ref restarted, 3, 0, APTR.FromPointer(5000),
            1300, work.Meta, work.Data, out var error);
        output.WriteLine($"Neighbor Hello after restart: read={read}, error={error}");
        Assert.Equal(1300, read);
        Inspect(restarted.Snapshot());
    }

    [Fact]
    public void UndeletableFlagMustPreventCommittedDeletion()
    {
        var image = Image(); Block(image, 7)[80] |= 2; Seal(Block(image, 7)); Inspect(image);
        var disk = new ReliabilityDisk(image); var work = Work();
        var ok = AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, 3);
        var report = Inspect(disk.Snapshot());
        output.WriteLine($"Remove undeletable Hello: success={ok}, error={work.Error}, exists={report.Entries.Any(e => e.Node == 3)}");
        Assert.False(ok, "Remove committed deletion despite the SFS UNDELETABLE object flag.");
        Assert.Equal(DOS.Error.ObjectInUse, work.Error);
        Assert.Equal(0, disk.Writes); Assert.Equal(image, disk.Snapshot());
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

    [Fact]
    public void FillingNodeLeafMustSetItsParentFullBit()
    {
        var disk = new ReliabilityDisk(Image());
        for (var i = 0; i < 94; i++) Create(ref disk, $"F{i:D3}");
        var image = disk.Snapshot(); Assert.Equal(98, Inspect(image).Entries.Count);
        var root = Block(image, 6); Assert.Equal(49u, U32(root, 16));
        var pointer = U32(root, 24); var leaf = Block(image, (int)(pointer >> 4));
        for (var offset = 20; offset + 10 <= 512; offset += 10) Assert.NotEqual(0u, U32(leaf, offset));
        output.WriteLine($"Full second leaf at block {pointer >> 4}, parent pointer=0x{pointer:X8}");
        Assert.Equal(1u, pointer & 1);
        U32(root, 24, pointer & ~1u); Seal(root);
        Assert.Throws<InvalidDataException>(() => Inspect(image));
    }

    [Fact]
    public void FreeingNodeLeafSlotMustClearItsParentFullBit()
    {
        var disk = new ReliabilityDisk(Image());
        for (var i = 0; i < 46; i++) Create(ref disk, $"F{i:D3}");
        var work = Work(); Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, 5), work.Error.ToString());
        var image = disk.Snapshot(); Inspect(image);
        var pointer = U32(Block(image, 6), 20); var leaf = Block(image, (int)(pointer >> 4));
        Assert.Equal(0u, U32(leaf, 20 + 4 * 10));
        output.WriteLine($"First leaf has a free slot, parent pointer=0x{pointer:X8}");
        Assert.Equal(0u, pointer & 1);
        U32(Block(image, 6), 20, pointer | 1); Seal(Block(image, 6));
        Assert.Throws<InvalidDataException>(() => Inspect(image));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PackedContainerMustStillRejectUnterminatedStrings(bool name)
    {
        var image = PackedImage(0);
        Block(image, 7)[(name ? 483 : 510)..].Fill((byte)'x'); Seal(Block(image, 7));
        var disk = new ReliabilityDisk(image); var work = Work(); disk.Ram[4000] = (byte)'c';
        var request = new AmigaSmartFileSystemWriteCore.Request
            { Node = 2, Buffer = APTR.FromPointer(4000), Length = 1 };
        Assert.False(AmigaSmartFileSystemWriteCore.SetMetadata(ref disk, ref work, ref request, DosPacketAction.SetComment));
        Assert.Equal(DOS.Error.DiskNotValidated, work.Error);
        Assert.Equal(0, disk.Writes); Assert.Equal(image, disk.Snapshot());
    }

    private static byte[] NestedNodeIndex(byte[] image)
    {
        var admin = Block(image, 10); var used = U32(admin, 28);
        var free = Enumerable.Range(0, 32).First(bit => (used & (0x80000000u >> bit)) == 0);
        var child = free + 1; var root = Block(image, 6);
        Assert.Equal(49u, U32(root, 16));
        root.CopyTo(Block(image, child)); U32(Block(image, child), 8, (uint)child); Seal(Block(image, child));
        root.Clear(); U32(root, 0, 0x4E444320); U32(root, 8, 6); U32(root, 12, 1);
        U32(root, 16, 49 * 123); U32(root, 20, (uint)child << 4); Seal(root);
        U32(admin, 28, used | (0x80000000u >> free)); Seal(admin);
        Inspect(image);
        return image;
    }

    private static byte[] CrashSeed(string scenario)
    {
        if (scenario == "packed-remove") return PackedImage(0);
        var disk = new ReliabilityDisk(scenario.Contains("admin", StringComparison.Ordinal) ? FullAdminPool() : Image());
        if (scenario == "rename-admin-growth") Create(ref disk, "Target", directory: true);
        else if (scenario == "comment-admin-growth")
            for (var i = 0; i < 13; i++) Create(ref disk, $"F{i:D3}", 2);
        else
        {
            var count = scenario == "comment-relocation" ? 12 : scenario == "node-fill" ? 93 : 46;
            for (var i = 0; i < count; i++) Create(ref disk, $"F{i:D3}");
        }
        return scenario.StartsWith("node-", StringComparison.Ordinal) ? NestedNodeIndex(disk.Snapshot()) : disk.Snapshot();
    }

    private static bool CrashMutation(ref ReliabilityDisk disk, string scenario)
    {
        var work = Work();
        if (scenario == "packed-remove") return AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, 3);
        if (scenario == "node-free") return AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, 5);
        if (scenario.StartsWith("comment-", StringComparison.Ordinal))
        {
            disk.Ram.AsSpan(4000, 79).Fill((byte)'c');
            var comment = new AmigaSmartFileSystemWriteCore.Request
                { Node = scenario == "comment-admin-growth" ? 4u : 2u, Buffer = APTR.FromPointer(4000), Length = 79 };
            return AmigaSmartFileSystemWriteCore.SetMetadata(ref disk, ref work, ref comment, DosPacketAction.SetComment);
        }
        var request = new AmigaSmartFileSystemWriteCore.Request
            { Node = 3, Directory = 1, Name = disk.Name(scenario == "rename-admin-growth" ? "Target/Moved" : "Extra"),
                Length = scenario == "rename-admin-growth" ? 12u : 5u };
        return scenario == "rename-admin-growth" ? AmigaSmartFileSystemWriteCore.Rename(ref disk, ref work, ref request) :
            AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref request, false);
    }

    [Theory]
    [InlineData("packed-remove")]
    [InlineData("rename-admin-growth")]
    [InlineData("comment-relocation")]
    [InlineData("comment-admin-growth")]
    [InlineData("node-fill")]
    [InlineData("node-free")]
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
