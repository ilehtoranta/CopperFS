using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    private static AmigaSmartFileSystemWriteCore.Work OutputWork(ref Disk disk, int mode) =>
        mode == 1 ? BatchWork(ref disk) : mode == 2 ? BufferedWork(ref disk) : TrstWork();

    private static bool ReplaceOutput(ref Disk disk, ref AmigaSmartFileSystemWriteCore.Work work,
        ref AmigaSmartFileSystemWriteCore.Request request, int mode) => mode == 3
        ? AmigaSmartFileSystemWriteCore.ExecuteTrst(ref disk, ref work, ref request,
            AmigaSmartFileSystemWriteCore.TrstOperation.ReplaceFile, APTR.FromPointer(131072), 65536, out _)
        : AmigaSmartFileSystemWriteCore.ReplaceFile(ref disk, ref work, ref request);

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ModeNewFilePreservesOldBytesAndHandlesCollisionsAndEmptyReopens(int mode)
    {
        var disk = new Disk(RecycledImage()) { CheckReadOnlyViews = true };
        var work = OutputWork(ref disk, mode);
        var request = new AmigaSmartFileSystemWriteCore.Request { Node = 3, Directory = 1, Name = disk.Name("Hello"), Length = 5 };
        Assert.True(ReplaceOutput(ref disk, ref work, ref request, mode), work.Error.ToString());
        var active = request.Node; Assert.NotEqual(3u, active);
        Assert.Equal(active, AmigaSmartFileSystemCore.Resolve(ref disk, 1, disk.Name("Hello"), 5, work.Meta, out _));
        Assert.Equal(3u, AmigaSmartFileSystemCore.Resolve(ref disk, 2, disk.Name("Hello"), 5, work.Meta, out _));
        Assert.Equal(1300, AmigaSmartFileSystemCore.Read(ref disk, 3, 0, APTR.FromPointer(4000), 1300, work.Meta, work.Data, out _));
        Assert.Equal(Enumerable.Range(0, 1300).Select(i => (byte)(i * 17)), disk.Ram.AsSpan(4000, 1300).ToArray());
        Assert.True(AmigaSmartFileSystemCore.Describe(ref disk, active, work.Meta, out var size, out _, out _)); Assert.Equal(0u, size);
        AssertRecycledCounters(ref disk, 2, 4);
        disk.Ram[4000] = 0xA7;
        var write = new AmigaSmartFileSystemWriteCore.Request { Node = active, Buffer = APTR.FromPointer(4000), Length = 1 };
        Assert.True(AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref write, out _), work.Error.ToString());
        request = new() { Node = active, Directory = 1, Name = disk.Name("Hello"), Length = 5 };
        Assert.True(ReplaceOutput(ref disk, ref work, ref request, mode), work.Error.ToString());
        Assert.Equal(active, AmigaSmartFileSystemCore.Resolve(ref disk, 2, disk.Name("Hello$AAA"), 9, work.Meta, out _));
        AssertRecycledCounters(ref disk, 3, 5);
        var empty = request.Node; request.Name = disk.Name("Hello");
        Assert.True(ReplaceOutput(ref disk, ref work, ref request, mode), work.Error.ToString());
        Assert.NotEqual(empty, request.Node);
        Assert.Equal(empty, AmigaSmartFileSystemCore.Resolve(ref disk, 2, disk.Name("Hello$AAB"), 9, work.Meta, out _));
        AssertRecycledCounters(ref disk, 4, 5);
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work), work.Error.ToString());
        var remount = new Disk(disk.Snapshot()); work = TrstWork();
        Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref remount, ref work)); AssertRecycledCounters(ref remount, 4, 5);
        Assert.Equal(1, AmigaSmartFileSystemCore.Read(ref remount, active, 0, APTR.FromPointer(4000), 1, work.Meta, work.Data, out _));
        Assert.Equal(0xA7, remount.Ram[4000]);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ModeNewFileWithoutRecyclingRetainsInPlaceTruncation(int mode)
    {
        var disk = new Disk(Image()); var work = OutputWork(ref disk, mode);
        var request = new AmigaSmartFileSystemWriteCore.Request { Node = 3, Directory = 1, Name = disk.Name("Hello"), Length = 5 };
        Assert.True(ReplaceOutput(ref disk, ref work, ref request, mode), work.Error.ToString());
        Assert.Equal(3u, request.Node);
        Assert.True(AmigaSmartFileSystemCore.Describe(ref disk, 3, work.Meta, out var size, out _, out _)); Assert.Equal(0u, size);
    }

    [Theory]
    [InlineData(0, 1u)] [InlineData(1, 1u)] [InlineData(2, 1u)] [InlineData(3, 1u)]
    [InlineData(0, 4u)] [InlineData(1, 4u)] [InlineData(2, 4u)] [InlineData(3, 4u)]
    public void ModeNewFileHonorsProtectionBeforeRecycling(int mode, uint protection)
    {
        var disk = new Disk(RecycledImage()); var work = TrstWork();
        var metadata = new AmigaSmartFileSystemWriteCore.Request { Node = 3, Position = protection };
        Assert.True(AmigaSmartFileSystemWriteCore.SetMetadata(ref disk, ref work, ref metadata, DosPacketAction.SetProtection));
        var before = disk.Snapshot(); work = OutputWork(ref disk, mode);
        var request = new AmigaSmartFileSystemWriteCore.Request { Node = 3, Directory = 1, Name = disk.Name("Hello"), Length = 5 };
        var success = ReplaceOutput(ref disk, ref work, ref request, mode);
        if (protection == 4)
        {
            Assert.True(success, work.Error.ToString()); Assert.NotEqual(3u, request.Node);
            Assert.Equal(3u, AmigaSmartFileSystemCore.Resolve(ref disk, 2, disk.Name("Hello"), 5, work.Meta, out _));
            AssertRecycledCounters(ref disk, 2, 4);
        }
        else
        {
            Assert.False(success); Assert.Equal(DOS.Error.DeleteProtected, work.Error);
            Assert.Equal(before, disk.Snapshot()); Assert.Equal(3u, request.Node);
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ModeNewFileRecoversAtomicallyAtEveryWriteAndFlush(int mode)
    {
        var original = RecycledImage(); var before = AllocatedState(original);
        var baseline = new Disk(original.ToArray()) { Buffered = true }; var work = OutputWork(ref baseline, mode);
        var request = new AmigaSmartFileSystemWriteCore.Request { Node = 3, Directory = 1, Name = baseline.Name("Hello"), Length = 5 };
        Assert.True(ReplaceOutput(ref baseline, ref work, ref request, mode), work.Error.ToString());
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref baseline, ref work)); var after = AllocatedState(baseline.Snapshot());
        for (var cut = 1; cut <= baseline.IoEvents; cut++)
        {
            var disk = new Disk(original.ToArray()) { Buffered = true }; work = OutputWork(ref disk, mode);
            request = new() { Node = 3, Directory = 1, Name = disk.Name("Hello"), Length = 5 }; disk.CutAfterIo = cut;
            Assert.Throws<PowerCut>(() =>
            { Assert.True(ReplaceOutput(ref disk, ref work, ref request, mode)); Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work)); });
            var recovered = new Disk(disk.Snapshot()); work = TrstWork();
            Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref recovered, ref work), $"mode {mode}, cut {cut}: {work.Error}");
            var state = AllocatedState(recovered.Snapshot());
            Assert.True(state.SequenceEqual(before) || state.SequenceEqual(after), $"mode {mode}, cut {cut}/{baseline.IoEvents}");
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ModeNewFileIoFailuresRecoverWithoutLosingThePreviousFile(int mode)
    {
        var original = RecycledImage(); var before = AllocatedState(original);
        var baseline = new Disk(original.ToArray()) { Buffered = true }; var work = OutputWork(ref baseline, mode);
        var request = new AmigaSmartFileSystemWriteCore.Request { Node = 3, Directory = 1, Name = baseline.Name("Hello"), Length = 5 };
        Assert.True(ReplaceOutput(ref baseline, ref work, ref request, mode));
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref baseline, ref work)); var after = AllocatedState(baseline.Snapshot());
        foreach (var flush in new[] { false, true })
        for (var fail = 1; fail <= (flush ? baseline.Flushes : baseline.Writes); fail++)
        {
            var disk = new Disk(original.ToArray()) { Buffered = true }; work = OutputWork(ref disk, mode);
            request = new() { Node = 3, Directory = 1, Name = disk.Name("Hello"), Length = 5 };
            if (flush) disk.FailAtFlush = fail; else disk.FailAtWrite = fail;
            var success = ReplaceOutput(ref disk, ref work, ref request, mode) && AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work);
            Assert.False(success, $"mode {mode}, flush {flush}, fail {fail}");
            var recovered = new Disk(disk.Snapshot()); work = TrstWork();
            Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref recovered, ref work));
            var state = AllocatedState(recovered.Snapshot());
            Assert.True(state.SequenceEqual(before) || state.SequenceEqual(after), $"mode {mode}, flush {flush}, fail {fail}");
        }
    }

    [Fact]
    public void ModeNewFileTrstArenaExhaustionDoesNotPublishTheRecycleMove()
    {
        var image = RecycledImage(); var before = image.ToArray(); var disk = new Disk(image); var work = TrstWork();
        var request = new AmigaSmartFileSystemWriteCore.Request { Node = 3, Directory = 1, Name = disk.Name("Hello"), Length = 5 };
        Assert.False(AmigaSmartFileSystemWriteCore.ExecuteTrst(ref disk, ref work, ref request,
            AmigaSmartFileSystemWriteCore.TrstOperation.ReplaceFile, APTR.FromPointer(131072), 1024, out _));
        Assert.Equal(DOS.Error.NoFreeStore, work.Error); Assert.Equal(0, disk.Writes); Assert.Equal(before, disk.Snapshot());
    }
}
