using CopperSFS.Bench;
using System.Text;
using System.Text.Json;
using Amiga;
using Xunit.Abstractions;
using static CopperSFS.Tests.ReliabilityFixture;

namespace CopperSFS.Tests;

public sealed class PackedObjectTests(ITestOutputHelper output)
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

    private static int AddObject(Span<byte> block, int at, uint node, string name, string comment)
    {
        U32(block, at + 4, node); U32(block, at + 8, 15);
        Encoding.Latin1.GetBytes(name).CopyTo(block[(at + 25)..]);
        Encoding.Latin1.GetBytes(comment).CopyTo(block[(at + 26 + name.Length)..]);
        return (at + 28 + name.Length + comment.Length) & ~1;
    }

    internal static byte[] PackedImage(int tailBytes)
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


    [Fact]
    public void CreateAndCommentGrowthCanUseTheLastBytesOfAContainer()
    {
        var disk = new ReliabilityDisk(PackedImage(26)); var work = Work();
        disk.Ram.AsSpan(4000, 26).Fill((byte)'c');
        var request = new AmigaSmartFileSystemWriteCore.Request { Node = 9, Buffer = APTR.FromPointer(4000), Length = 26 };
        Assert.True(AmigaSmartFileSystemWriteCore.SetMetadata(ref disk, ref work, ref request, DosPacketAction.SetComment));
        Assert.Equal(new string('c', 26), Inspect(disk.Snapshot()).Entries.Single(e => e.Node == 9).Comment);
        Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, 9));
        // Removed record is 104 bytes. A 77-character name consumes exactly that space.
        var before = Inspect(disk.Snapshot());
        var name = new string('n', 77); var node = Create(ref disk, name);
        var after = Inspect(disk.Snapshot());
        Assert.Equal(before.FreeBlocks, after.FreeBlocks);
        Assert.Contains(after.Entries, e => e.Node == node && e.Path == "/" + name);
        Assert.Equal(7u, U32(Block(disk.Snapshot(), 6), 20 + ((int)node - 1) * 10));
    }

    [Theory]
    [InlineData(0)] [InlineData(79)]
    public void EmptyAndMaximumLengthCommentsRemainValid(int length)
    {
        var disk = new ReliabilityDisk(Image()); var work = Work();
        disk.Ram.AsSpan(4000, length).Fill((byte)'c');
        var request = new AmigaSmartFileSystemWriteCore.Request { Node = 3, Buffer = APTR.FromPointer(4000), Length = (uint)length };
        Assert.True(AmigaSmartFileSystemWriteCore.SetMetadata(ref disk, ref work, ref request, DosPacketAction.SetComment));
        Assert.Equal(new string('c', length), Inspect(disk.Snapshot()).Entries.Single(e => e.Node == 3).Comment);
    }

    [Theory]
    [InlineData("create", false)] [InlineData("create", true)]
    [InlineData("remove", false)] [InlineData("remove", true)]
    [InlineData("rename", false)] [InlineData("rename", true)]
    [InlineData("comment", false)] [InlineData("comment", true)]
    public void PackedMutationsRecoverAfterEveryWriteAndFlushFailure(string operation, bool redo)
    {
        var image = PackedImage(0);
        string Manifest(byte[] bytes) => JsonSerializer.Serialize(Inspect(bytes));
        bool Mutate(ref StudyDisk disk)
        {
            var work = disk.Work;
            Encoding.ASCII.GetBytes("New").CopyTo(disk.Ram, (int)disk.NameBuffer.Raw);
            Encoding.ASCII.GetBytes("new").CopyTo(disk.Ram, (int)disk.Payload.Raw);
            var request = new AmigaSmartFileSystemWriteCore.Request
                { Directory = 1, Node = 3, Name = disk.NameBuffer, Buffer = disk.Payload, Length = 3 };
            if (redo) return AmigaSmartFileSystemWriteCore.ExecuteTrst(ref disk, ref work, ref request,
                operation switch { "create" => AmigaSmartFileSystemWriteCore.TrstOperation.CreateFile,
                    "remove" => AmigaSmartFileSystemWriteCore.TrstOperation.Remove,
                    "rename" => AmigaSmartFileSystemWriteCore.TrstOperation.Rename,
                    _ => AmigaSmartFileSystemWriteCore.TrstOperation.SetComment }, disk.BatchBuffer, 65536, out _);
            return operation switch
            {
                "create" => AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref request, false),
                "remove" => AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, 3),
                "rename" => AmigaSmartFileSystemWriteCore.Rename(ref disk, ref work, ref request),
                _ => AmigaSmartFileSystemWriteCore.SetMetadata(ref disk, ref work, ref request, DosPacketAction.SetComment)
            };
        }
        var baseline = new StudyDisk(image, 512, crashModel: true); Assert.True(Mutate(ref baseline));
        var before = Manifest(image); var after = Manifest(baseline.Snapshot()); Assert.NotEqual(before, after);
        for (var cut = 1; cut <= baseline.Events; cut++)
            foreach (var failure in new[] { false, true })
            {
                var disk = new StudyDisk(image, 512, crashModel: true)
                    { CutAfterEvent = failure ? 0 : cut, FailAtEvent = failure ? cut : 0 };
                if (failure) Assert.False(Mutate(ref disk));
                else Assert.Throws<StudyPowerCut>(() => Mutate(ref disk));
                foreach (var saved in disk.CrashImages())
                {
                    var recovered = new StudyDisk(saved, 512); FsOps.Recover(ref recovered);
                    var stable = recovered.Snapshot(); Assert.Contains(Manifest(stable), new[] { before, after });
                    FsOps.Recover(ref recovered); Assert.Equal(stable, recovered.Snapshot());
                }
            }
    }
}
