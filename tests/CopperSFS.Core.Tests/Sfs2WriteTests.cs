using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Amiga;
using CopperSFS.Bench;
using CopperSFS.Check;
using Xunit.Abstractions;
using static CopperSFS.AmigaSmartFileSystem2WriteCore;

namespace CopperSFS.Tests;

public sealed class Sfs2WriteTests(ITestOutputHelper output)
{
    internal static Work WorkFor(StudyDisk disk) => new()
    {
        Meta = disk.Work.Meta, Data = disk.Work.Data, Bitmap = disk.Work.Bitmap, Journal = disk.Work.Journal
    };
    internal static void Seal(Span<byte> data)
    {
        U32(data, 4, 0); uint sum = 2;
        for (var i = 0; i < data.Length; i += 4) sum = unchecked(sum + U32(data, i));
        U32(data, 4, unchecked(0u - sum));
    }
    internal static uint U32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
    internal static void U32(Span<byte> data, int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(data[at..], value);
    internal static void U16(Span<byte> data, int at, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(data[at..], value);

    internal static byte[] Empty(int bytes = 512, int capacity = 512 * 1024)
    {
        // Convert the repository's original EMPTY synthetic classic fixture.
        // This is not an independent formatter or interoperability evidence.
        var image = StudyFixture.Empty(bytes, Math.Max(capacity, bytes * 128));
        for (var n = 0; n < image.Length / bytes; n++)
        {
            var block = image.AsSpan(n * bytes, bytes); var id = U32(block, 0);
            if (id == 0) continue;
            if (id == 0x53465300) { U32(block, 0, 0x53465302); U16(block, 12, 4); }
            if (id == 0x4F424A43 && U32(block, 28) != 0)
            {
                var old = block.ToArray(); block[44..(bytes - 36)].Clear();
                old.AsSpan(44, 4).CopyTo(block[46..]); block[50] = old[48];
                var end = Array.IndexOf(old, (byte)0, 49);
                old.AsSpan(49, end - 49).CopyTo(block[51..]);
            }
            if (id == 0x424E4443) block[15] = 16;
            Seal(block);
        }
        Inspect(image, bytes);
        return image;
    }
    internal static ImageReport Inspect(byte[] image, int bytes = 512, uint reserved = 1)
    {
        using var stream = new MemoryStream(image, false);
        return SfsImageChecker.Check(stream, bytes, reserved);
    }
    internal static string Manifest(byte[] image, int bytes = 512, uint reserved = 1)
    {
        var report = Inspect(image, bytes, reserved);
        return JsonSerializer.Serialize(new { report.FreeBlocks, report.Entries });
    }
    internal static uint CreateFile(ref StudyDisk disk, string name, uint parent = 1, bool directory = false)
    {
        Encoding.Latin1.GetBytes(name).CopyTo(disk.Ram, (int)disk.NameBuffer.Raw);
        var w = WorkFor(disk); var r = new Request { Directory = parent, Name = disk.NameBuffer, Length = (uint)name.Length };
        Assert.True(Create(ref disk, ref w, ref r, directory), w.Error.ToString()); return r.Node;
    }
    internal static void PutFile(ref StudyDisk disk, uint node, ulong position, byte[] bytes, bool batched = false)
    {
        bytes.CopyTo(disk.Ram, (int)disk.Payload.Raw); var w = WorkFor(disk);
        var r = new Request { Node = node, Position = position, Length = (uint)bytes.Length, Buffer = disk.Payload };
        var ok = batched ? WriteBatched(ref disk, ref w, ref r, disk.BatchBuffer, 65536, out var actual) :
            Write(ref disk, ref w, ref r, out actual);
        Assert.True(ok, w.Error.ToString()); Assert.Equal((uint)bytes.Length, actual);
    }
    internal static (byte[] Image, uint Node) Seed(int bytes = 512)
    {
        var disk = new StudyDisk(Empty(bytes), bytes);
        var node = CreateFile(ref disk, "Data"); PutFile(ref disk, node, 0, StudyFixture.Pattern(1027, 723), true);
        var keep = CreateFile(ref disk, "Keep"); PutFile(ref disk, keep, 0, StudyFixture.Pattern(513, 724), true);
        CreateFile(ref disk, "Docs", directory: true);
        Inspect(disk.Snapshot(), bytes); return (disk.Snapshot(), node);
    }
    internal static bool Mutate(ref StudyDisk disk, uint node, string scenario, bool redo = false)
    {
        var w = WorkFor(disk); var data = StudyFixture.Pattern(7, 900);
        data.CopyTo(disk.Ram, (int)disk.Payload.Raw); Encoding.ASCII.GetBytes("Changed").CopyTo(disk.Ram, (int)disk.NameBuffer.Raw);
        var r = new Request { Node = node, Directory = 1, Name = disk.NameBuffer, Length = 7, Buffer = disk.Payload };
        var op = scenario switch
        {
            "create" => TrstOperation.CreateFile, "mkdir" => TrstOperation.CreateDirectory,
            "remove" => TrstOperation.Remove, "rename" => TrstOperation.Rename,
            "truncate" => TrstOperation.Truncate, "grow" or "shrink" => TrstOperation.Resize,
            "protect" => TrstOperation.SetProtection, "comment" => TrstOperation.SetComment,
            "date" => TrstOperation.SetDate, _ => TrstOperation.Write
        };
        if (scenario == "append") r.Position = 1027;
        if (scenario == "overwrite") r.Position = 510;
        if (scenario == "grow") r.Position = 1601;
        if (scenario == "shrink") r.Position = 517;
        if (scenario == "protect") r.Position = 128;
        if (scenario == "comment") Encoding.ASCII.GetBytes("comment").CopyTo(disk.Ram, (int)disk.Payload.Raw);
        if (scenario == "date") { disk.WriteUInt32(disk.Payload, 0, 1234); disk.WriteUInt32(disk.Payload, 4, 5); disk.WriteUInt32(disk.Payload, 8, 100); }
        bool ok;
        if (redo) ok = ExecuteTrst(ref disk, ref w, ref r, op, disk.BatchBuffer, 65536, out _);
        else ok = op switch
        {
            TrstOperation.CreateFile => Create(ref disk, ref w, ref r, false),
            TrstOperation.CreateDirectory => Create(ref disk, ref w, ref r, true),
            TrstOperation.Remove => Remove(ref disk, ref w, node), TrstOperation.Rename => Rename(ref disk, ref w, ref r),
            TrstOperation.Resize => Resize(ref disk, ref w, node, r.Position), TrstOperation.Truncate => Truncate(ref disk, ref w, node),
            TrstOperation.SetProtection => SetMetadata(ref disk, ref w, ref r, DosPacketAction.SetProtection),
            TrstOperation.SetComment => SetMetadata(ref disk, ref w, ref r, DosPacketAction.SetComment),
            TrstOperation.SetDate => SetMetadata(ref disk, ref w, ref r, DosPacketAction.SetDate),
            _ => Write(ref disk, ref w, ref r, out _)
        };
        if (!ok && disk.FailAtEvent == 0 && disk.FailAtRead == 0) Assert.True(ok, $"{scenario}: {w.Error}");
        return ok;
    }

    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(4096)]
    public void BothJournalPoliciesProduceTheSameIndependentManifest(int bytes)
    {
        var (image, node) = Seed(bytes);
        foreach (var scenario in new[] { "create", "mkdir", "overwrite", "append", "grow", "shrink", "truncate", "rename", "remove", "comment", "protect", "date" })
        {
            var scalar = new StudyDisk(image, bytes); Assert.True(Mutate(ref scalar, node, scenario));
            var redo = new StudyDisk(image, bytes); Assert.True(Mutate(ref redo, node, scenario, true));
            Assert.Equal(Manifest(scalar.Snapshot(), bytes), Manifest(redo.Snapshot(), bytes));
        }
    }

    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(4096)]
    public void PackedObjectsNodeGrowthAndMetadataRelocationPreserveWideHeaders(int bytes)
    {
        var disk = new StudyDisk(Empty(bytes), bytes); var parent = CreateFile(ref disk, "Parent", directory: true);
        var files = new List<uint>();
        for (var i = 0; i < (bytes - 20) / 10 + 12; i++) files.Add(CreateFile(ref disk, $"Child{i:D4}", parent));
        PutFile(ref disk, files[0], 0, StudyFixture.Pattern(1301), true);
        var w = WorkFor(disk); var comment = new string('c', 79); Encoding.ASCII.GetBytes(comment).CopyTo(disk.Ram, (int)disk.Payload.Raw);
        var r = new Request { Node = files[0], Buffer = disk.Payload, Length = 79 };
        Assert.True(SetMetadata(ref disk, ref w, ref r, DosPacketAction.SetComment), w.Error.ToString());
        var name = new string('n', 107); Encoding.ASCII.GetBytes(name).CopyTo(disk.Ram, (int)disk.NameBuffer.Raw);
        r = new Request { Node = files[0], Directory = 1, Name = disk.NameBuffer, Length = 107 };
        Assert.True(Rename(ref disk, ref w, ref r), w.Error.ToString());
        var report = Inspect(disk.Snapshot(), bytes);
        Assert.Equal(comment, report.Entries.Single(e => e.Node == files[0]).Comment);
        Assert.Equal(1301ul, report.Entries.Single(e => e.Node == files[0]).Size);
        foreach (var file in files.Skip(1)) Assert.True(Remove(ref disk, ref w, file), w.Error.ToString());
        Assert.True(Remove(ref disk, ref w, parent), w.Error.ToString()); Inspect(disk.Snapshot(), bytes);
    }

    [Fact]
    public void Sfs2MutationsRefuseClassicVolumesAndOversizedRequestsWithoutWrites()
    {
        var disk = new StudyDisk(StudyFixture.Empty(512, 512 * 128), 512); var w = WorkFor(disk);
        var r = new Request { Directory = 1, Name = disk.NameBuffer, Length = 1, Node = 1, Buffer = disk.Payload };
        disk.Ram[(int)disk.NameBuffer.Raw] = (byte)'A';
        Assert.False(Recover(ref disk, ref w)); Assert.False(Create(ref disk, ref w, ref r, false));
        Assert.False(Write(ref disk, ref w, ref r, out _)); Assert.False(Resize(ref disk, ref w, 1, MaxFileSize + 1));
        Assert.Equal(0, disk.Counts.BlockWrites); Assert.Equal(0, disk.Counts.Flushes);
    }

    [Theory]
    [InlineData("create", false)]
    [InlineData("create", true)]
    [InlineData("mkdir", false)]
    [InlineData("mkdir", true)]
    [InlineData("overwrite", false)]
    [InlineData("overwrite", true)]
    [InlineData("append", false)]
    [InlineData("append", true)]
    [InlineData("grow", false)]
    [InlineData("grow", true)]
    [InlineData("shrink", false)]
    [InlineData("shrink", true)]
    [InlineData("truncate", false)]
    [InlineData("truncate", true)]
    [InlineData("rename", false)]
    [InlineData("rename", true)]
    [InlineData("remove", false)]
    [InlineData("remove", true)]
    [InlineData("comment", false)]
    [InlineData("comment", true)]
    [InlineData("protect", false)]
    [InlineData("protect", true)]
    [InlineData("date", false)]
    [InlineData("date", true)]
    public void EveryCutAndIoFailureRecoversCompleteBeforeOrAfterManifest(string scenario, bool redo)
    {
        var (image, node) = Seed(); var before = Manifest(image);
        var baseline = new StudyDisk(image, 512, crashModel: true); Assert.True(Mutate(ref baseline, node, scenario, redo));
        var after = Manifest(baseline.Snapshot()); long checkedImages = 0;
        void Check(byte[] saved)
        {
            var restarted = new StudyDisk(saved, 512); var w = WorkFor(restarted);
            Assert.True(Recover(ref restarted, ref w), $"{scenario}: recovery {w.Error}");
            var recovered = restarted.Snapshot(); Assert.Contains(Manifest(recovered), new[] { before, after });
            Assert.True(Recover(ref restarted, ref w)); Assert.Equal(recovered, restarted.Snapshot()); checkedImages++;
        }
        for (var cut = 1; cut <= baseline.Events; cut++)
        {
            var disk = new StudyDisk(image, 512, crashModel: true) { CutAfterEvent = cut };
            Assert.Throws<StudyPowerCut>(() => Mutate(ref disk, node, scenario, redo));
            foreach (var saved in disk.CrashImages()) Check(saved);
            foreach (var persistent in new[] { false, true })
            {
                disk = new StudyDisk(image, 512, crashModel: true) { FailAtEvent = cut, PersistentFailure = persistent };
                var ok = Mutate(ref disk, node, scenario, redo);
                if (ok) Assert.Equal(after, Manifest(disk.Snapshot()));
                foreach (var saved in disk.CrashImages()) Check(saved);
            }
        }
        for (var read = 1; read <= baseline.Counts.BlockReads; read++)
            foreach (var persistent in new[] { false, true })
            {
                var disk = new StudyDisk(image, 512, crashModel: true) { FailAtRead = read, PersistentFailure = persistent };
                var ok = Mutate(ref disk, node, scenario, redo);
                if (ok) Assert.Equal(after, Manifest(disk.Snapshot()));
                foreach (var saved in disk.CrashImages()) Check(saved);
            }
        output.WriteLine($"SFS2 {scenario} redo={redo}: {checkedImages} exhaustive mutation crash/failure image checks.");
    }

    [Fact]
    public void CheckerAcceptsOriginalSfs2AndRejectsWrongChecksumAndNodePointerWidth()
    {
        var path = Environment.GetEnvironmentVariable("COPPERSFS_SFS2_REFERENCE_IMAGE");
        if (path is not null) Inspect(File.ReadAllBytes(path), reserved: 2);
        var (image, _) = Seed(); image[2 * 512 + 5] ^= 1;
        Assert.Throws<InvalidDataException>(() => Inspect(image));
        var disk = new StudyDisk(Empty(), 512);
        for (var i = 0; i < 50; i++) CreateFile(ref disk, $"Node{i:D3}");
        image = disk.Snapshot(); Inspect(image);
        var nodes = image.AsSpan(6 * 512, 512); var pointer = U32(nodes, 20);
        U32(nodes, 20, (pointer >> 1) << 4); Seal(nodes); // Wrong classic index-pointer encoding.
        Assert.Throws<InvalidDataException>(() => Inspect(image));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoverySurvivesEveryCutReadWriteFlushErrorAndTornMutation(bool redo)
    {
        var (image, node) = Seed(); var before = Manifest(image);
        var baseline = new StudyDisk(image, 512, crashModel: true); Assert.True(Mutate(ref baseline, node, "overwrite", redo));
        var after = Manifest(baseline.Snapshot());
        var interrupted = new StudyDisk(image, 512, crashModel: true) { CutAfterEvent = baseline.Events - 1 };
        Assert.Throws<StudyPowerCut>(() => Mutate(ref interrupted, node, "overwrite", redo));
        var dirty = interrupted.Snapshot(); var recoveredBaseline = new StudyDisk(dirty, 512, crashModel: true);
        var w = WorkFor(recoveredBaseline); Assert.True(Recover(ref recoveredBaseline, ref w), w.Error.ToString());
        var expected = Manifest(recoveredBaseline.Snapshot()); Assert.Equal(redo ? after : before, expected);
        long checks = 0; int tears = 0, refused = 0;
        void Validate(byte[] saved)
        {
            var disk = new StudyDisk(saved, 512); var work = WorkFor(disk); Assert.True(Recover(ref disk, ref work), work.Error.ToString());
            Assert.Equal(expected, Manifest(disk.Snapshot())); var stable = disk.Snapshot();
            Assert.True(Recover(ref disk, ref work)); Assert.Equal(stable, disk.Snapshot()); checks++;
        }
        for (var point = 1; point <= recoveredBaseline.Events; point++)
        {
            var disk = new StudyDisk(dirty, 512, crashModel: true) { CutAfterEvent = point }; var work = WorkFor(disk);
            Assert.Throws<StudyPowerCut>(() => Recover(ref disk, ref work));
            foreach (var saved in disk.CrashImages()) Validate(saved);
            foreach (var persistent in new[] { false, true })
            {
                disk = new StudyDisk(dirty, 512, crashModel: true) { FailAtEvent = point, PersistentFailure = persistent };
                work = WorkFor(disk); Recover(ref disk, ref work);
                foreach (var saved in disk.CrashImages()) Validate(saved);
            }
        }
        for (var read = 1; read <= recoveredBaseline.Counts.BlockReads; read++)
            foreach (var persistent in new[] { false, true })
            {
                var disk = new StudyDisk(dirty, 512, crashModel: true) { FailAtRead = read, PersistentFailure = persistent };
                var work = WorkFor(disk); Recover(ref disk, ref work);
                foreach (var saved in disk.CrashImages()) Validate(saved);
            }
        for (var write = 1; write <= baseline.Counts.BlockWrites; write++)
            foreach (var prefix in new[] { 1, 256, 511 })
            {
                var disk = new StudyDisk(image, 512, crashModel: true) { TearAtWrite = write, TearBytes = prefix };
                Assert.Throws<StudyPowerCut>(() => Mutate(ref disk, node, "overwrite", redo));
                disk = new StudyDisk(disk.Snapshot(), 512); var work = WorkFor(disk);
                if (Recover(ref disk, ref work)) { Assert.Contains(Manifest(disk.Snapshot()), new[] { before, after }); tears++; }
                else { Assert.False(AmigaSmartFileSystemReadOnlyCore.Mount(ref disk, work.Meta, out _)); refused++; }
            }
        var broken = dirty.ToArray(); var root = U32(broken, 104); var head = U32(broken.AsSpan((int)(root + 2) * 512), 12);
        Assert.NotEqual(0u, head); broken[(int)head * 512 + 5] ^= 1;
        var damaged = new StudyDisk(broken, 512); w = WorkFor(damaged);
        Assert.False(Recover(ref damaged, ref w)); Assert.False(AmigaSmartFileSystemReadOnlyCore.Mount(ref damaged, w.Meta, out _));
        Assert.Equal(0, damaged.Counts.BlockWrites);
        output.WriteLine($"SFS2 recovery redo={redo}: {checks} exhaustive recovery image checks; {tears} tears recovered, {refused} refused mount.");
    }
}
