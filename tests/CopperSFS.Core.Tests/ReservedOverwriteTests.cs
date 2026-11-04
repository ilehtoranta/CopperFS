using System.Buffers.Binary;
using Amiga;
using CopperSFS.Bench;

namespace CopperSFS.Tests;

public sealed partial class RelocationTests
{
    private static byte[] ReservationRam(ref StudyDisk disk) =>
        disk.Ram.AsSpan((int)disk.Work.Reservations.Raw, (int)AmigaSmartFileSystemWriteCore.OverwriteReservationBytes).ToArray();
    private static StudyDisk ReservedDisk(byte[] image, byte[] reservations, bool crash = false)
    {
        var disk = new StudyDisk(image.ToArray(), Bytes, crashModel: crash, batchedWrites: true);
        reservations.CopyTo(disk.Ram, (int)disk.Work.Reservations.Raw);
        return disk;
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ReservedFullOverwriteSkipsSmallHoleAndSurvivesInterleavedAllocations(bool interleaved)
    {
        var seed = Fragmented([512], [512], 2 * 1024 * 1024);
        var disk = new StudyDisk(seed.Image.ToArray(), Bytes, batchedWrites: true);
        var other = interleaved ? FsOps.Create(ref disk, "Other") : 0;
        var data = StudyFixture.Pattern(seed.Data.Length, 141);
        uint destination = 0;
        for (var position = 0; position < data.Length; position += 65536)
        {
            Assert.True(Write(ref disk, seed.Node, (uint)position, data[position..(position + 65536)], out var actual));
            Assert.Equal(65536u, actual);
            var mapped = Blocks(disk.Snapshot(), seed.Node);
            if (position == 0) { destination = mapped[0]; Assert.True(destination >= 1024); }
            Assert.Equal(Enumerable.Range((int)destination, (position + 65536) / Bytes).Select(n => (uint)n), mapped.Take((position + 65536) / Bytes));
            if (interleaved) FsOps.Write(ref disk, other, (uint)position, StudyFixture.Pattern(65536, 142));
        }
        Check(disk.Snapshot(), data);
        var result = Blocks(disk.Snapshot(), seed.Node);
        Assert.Equal(Enumerable.Range((int)destination, 512).Select(n => (uint)n), result);
        var work = disk.Work;
        var tree = disk.Snapshot().AsSpan(5 * Bytes, Bytes).ToArray();
        var record = Enumerable.Range(0, BinaryPrimitives.ReadUInt16BigEndian(tree.AsSpan(12)))
            .Select(i => 16 + i * 14).Single(at => StudyFixture.U32(tree, at) == destination);
        Assert.Equal(512, BinaryPrimitives.ReadUInt16BigEndian(tree.AsSpan(record + 12)));
        Assert.Equal(0u, StudyFixture.U32(tree, record + 4));
        Assert.False(AmigaSmartFileSystemWriteCore.HasOverwriteReservation(ref disk, ref work, seed.Node));
        Assert.Equal(128u, disk.Counts.MaxRangeBlocks);
    }

    [Fact]
    public void ReservedFullOverwriteCrossesBitmapAndFinishesWithPartialBlock()
    {
        var seed = Fragmented([64], [2049], 4 * 1024 * 1024);
        var size = 1024 * 1024 + 123;
        U32(seed.Image.AsSpan(7 * Bytes), 40, (uint)size); Seal(seed.Image.AsSpan(7 * Bytes, Bytes));
        var disk = new StudyDisk(seed.Image.ToArray(), Bytes);
        var data = StudyFixture.Pattern(size, 143);
        for (var position = 0; position < size; position += 65536)
            Assert.True(Write(ref disk, seed.Node, (uint)position, data[position..Math.Min(position + 65536, size)], out _));
        Check(disk.Snapshot(), data);
        var blocks = Blocks(disk.Snapshot(), seed.Node);
        Assert.NotEqual(blocks[0] / 4000, blocks[^1] / 4000);
        Assert.Equal(Enumerable.Range((int)blocks[0], 2049).Select(n => (uint)n), blocks);
        Assert.Equal(128u, disk.Counts.MaxRangeBlocks);
    }

    [Theory]
    [InlineData("close")] [InlineData("seek")] [InlineData("resize")]
    [InlineData("truncate")] [InlineData("remove")] [InlineData("scalar")]
    public void ReservationsReleaseWithoutPersistentFreeSpaceLeak(string operation)
    {
        var seed = Fragmented([512], [512], 2 * 1024 * 1024);
        var disk = new StudyDisk(seed.Image.ToArray(), Bytes);
        Assert.True(Write(ref disk, seed.Node, 0, StudyFixture.Pattern(65536, 144), out _));
        var work = disk.Work;
        Assert.True(AmigaSmartFileSystemWriteCore.HasOverwriteReservation(ref disk, ref work, seed.Node));
        Assert.Equal(StudyFixture.Inspect(seed.Image, Bytes).FreeBlocks, StudyFixture.Inspect(disk.Snapshot(), Bytes).FreeBlocks);
        switch (operation)
        {
            case "close": AmigaSmartFileSystemWriteCore.ReleaseOverwriteReservation(ref disk, ref work, seed.Node); break;
            case "seek": Assert.True(Write(ref disk, seed.Node, 8192, StudyFixture.Pattern(512, 145), out _)); break;
            case "resize": Assert.True(AmigaSmartFileSystemWriteCore.Resize(ref disk, ref work, seed.Node, 65536)); break;
            case "truncate": Assert.True(AmigaSmartFileSystemWriteCore.Truncate(ref disk, ref work, seed.Node)); break;
            case "remove": Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, seed.Node)); break;
            case "scalar": FsOps.Write(ref disk, seed.Node, 65536, StudyFixture.Pattern(512, 145)); break;
        }
        Assert.False(AmigaSmartFileSystemWriteCore.HasOverwriteReservation(ref disk, ref work, seed.Node));
        StudyFixture.Inspect(disk.Snapshot(), Bytes);
        var restart = new StudyDisk(disk.Snapshot(), Bytes); work = restart.Work;
        Assert.False(AmigaSmartFileSystemWriteCore.HasOverwriteReservation(ref restart, ref work, seed.Node));
    }

    [Fact]
    public void EnteringTrstReleasesPriorCsUnReservations()
    {
        var seed = Fragmented([512], [512], 2 * 1024 * 1024);
        var disk = new StudyDisk(seed.Image.ToArray(), Bytes);
        Assert.True(Write(ref disk, seed.Node, 0, StudyFixture.Pattern(65536, 150), out _));
        var work = disk.Work;
        Assert.True(AmigaSmartFileSystemWriteCore.HasOverwriteReservation(ref disk, ref work, seed.Node));
        Assert.True(TrstWriteTests.Execute(ref disk, seed.Node, "create", out _, out var error), error.ToString());
        Assert.False(AmigaSmartFileSystemWriteCore.HasOverwriteReservation(ref disk, ref work, seed.Node));
        StudyFixture.Inspect(disk.Snapshot(), Bytes);
    }

    [Fact]
    public void ReservationCapacityAndAllocationPressureFallBackWithoutLeakingSpace()
    {
        var disk = new StudyDisk(StudyFixture.Empty(Bytes, 4 * 1024 * 1024), Bytes);
        var nodes = Enumerable.Range(0, 9).Select(_ => 0u).ToArray();
        var old = StudyFixture.Pattern(131072, 148); var data = StudyFixture.Pattern(65536, 149);
        for (var i = 0; i < nodes.Length; i++)
        {
            nodes[i] = FsOps.Create(ref disk, "File" + i);
            Assert.True(Write(ref disk, nodes[i], 0, old, out _));
        }
        for (var i = 0; i < nodes.Length; i++)
        {
            Assert.True(Write(ref disk, nodes[i], 0, data, out _));
            var work = disk.Work;
            Assert.Equal(i < 8, AmigaSmartFileSystemWriteCore.HasOverwriteReservation(ref disk, ref work, nodes[i]));
        }
        var fill = FsOps.Create(ref disk, "Pressure");
        var position = 0u; var evicted = false;
        for (var i = 0; i < 64; i++)
        {
            Assert.True(Write(ref disk, fill, position, data, out _)); position += 65536;
            var work = disk.Work;
            if (!AmigaSmartFileSystemWriteCore.HasOverwriteReservation(ref disk, ref work, nodes[0]))
            { evicted = true; break; }
        }
        Assert.True(evicted);
        StudyFixture.Inspect(disk.Snapshot(), Bytes);
        for (var i = 0; i < nodes.Length; i++)
        {
            Assert.True(Write(ref disk, nodes[i], 65536, data, out _));
            var work = disk.Work;
            Assert.False(AmigaSmartFileSystemWriteCore.HasOverwriteReservation(ref disk, ref work, nodes[i]));
        }
        StudyFixture.Inspect(disk.Snapshot(), Bytes);
    }

    [Fact]
    public void ReservedContinuationSurvivesEveryCutAndIoFailure()
    {
        var seed = Fragmented([512], [512], 1024 * 1024);
        var first = new StudyDisk(seed.Image.ToArray(), Bytes);
        Assert.True(Write(ref first, seed.Node, 0, StudyFixture.Pattern(65536, 146), out _));
        var reservations = ReservationRam(ref first); var image = first.Snapshot();
        var data = StudyFixture.Pattern(65536, 147);
        var probe = ReservedDisk(image, reservations, true);
        Assert.True(Write(ref probe, seed.Node, 65536, data, out _));
        var mapped = Blocks(probe.Snapshot(), seed.Node);
        Assert.Equal(mapped[127] + 1, mapped[128]);
        var before = Manifest(image); var after = Manifest(probe.Snapshot());
        var exhaustive = 0; var sampled = 0;
        byte[]? dirty = null;
        for (var cut = 1; cut <= probe.Events; cut++)
        {
            var disk = ReservedDisk(image, reservations, true); disk.CutAfterEvent = cut;
            Cut(ref disk, (ref StudyDisk d) => Write(ref d, seed.Node, 65536, data, out _));
            CrashStates(ref disk, before, after, $"reserved continuation cut {cut}", ref exhaustive, ref sampled);
            var saved = disk.Snapshot();
            if (StudyFixture.U32(saved, 4 * Bytes) == 0x54524641 && StudyFixture.U32(saved, 4 * Bytes + 12) != 0) dirty = saved;
        }
        foreach (var persistent in new[] { false, true })
        for (var fail = 1; fail <= probe.Events; fail++)
        {
            var disk = ReservedDisk(image, reservations, true); disk.FailAtEvent = fail; disk.PersistentFailure = persistent;
            Assert.False(Write(ref disk, seed.Node, 65536, data, out var actual)); Assert.Equal(0u, actual);
            var work = disk.Work;
            Assert.False(AmigaSmartFileSystemWriteCore.HasOverwriteReservation(ref disk, ref work, seed.Node));
            CrashStates(ref disk, before, after, $"reserved continuation failure {fail}/{persistent}", ref exhaustive, ref sampled);
        }
        Assert.NotNull(dirty);
        var recovery = new StudyDisk(dirty, Bytes, crashModel: true); FsOps.Recover(ref recovery);
        for (var cut = 1; cut <= recovery.Events; cut++)
        {
            var disk = new StudyDisk(dirty, Bytes, crashModel: true) { CutAfterEvent = cut };
            Cut(ref disk, FsOps.Recover);
            CrashStates(ref disk, before, after, $"reserved recovery cut {cut}", ref exhaustive, ref sampled);
        }
        var recovered = 0; var refused = 0;
        foreach (var prefix in new[] { 1, 256, 511 })
        for (var write = 1; write <= probe.Counts.BlockWrites; write++)
        {
            var disk = ReservedDisk(image, reservations, true); disk.TearAtWrite = write; disk.TearBytes = prefix;
            Cut(ref disk, (ref StudyDisk d) => Write(ref d, seed.Node, 65536, data, out _));
            var saved = disk.Snapshot(); var restart = new StudyDisk(saved, Bytes, crashModel: true); var work = restart.Work;
            if (AmigaSmartFileSystemWriteCore.Recover(ref restart, ref work))
            { Recovered(saved, before, after, $"reserved tear {write}/{prefix}"); recovered++; }
            else
            {
                Assert.False(AmigaSmartFileSystemCore.Mount(ref restart, work.Meta));
                Assert.Throws<InvalidDataException>(() => StudyFixture.Inspect(restart.Snapshot(), Bytes)); refused++;
            }
        }
        output.WriteLine($"Relocation: {exhaustive} exhaustive image checks, {sampled} sampled image checks; reserved continuation.");
        output.WriteLine($"Relocation tears: {recovered} recovered, {refused} refused mount.");
    }
}
