using Amiga;
using static CopperSFS.AmigaSmartFileSystemWriteCore;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    [Theory]
    [InlineData(8, 0)] [InlineData(8, 1)] [InlineData(8, 2)] [InlineData(8, 3)] [InlineData(8, 4)]
    [InlineData(9, 0)] [InlineData(9, 1)] [InlineData(9, 2)] [InlineData(9, 3)] [InlineData(9, 4)]
    [InlineData(12, 0)] [InlineData(12, 1)] [InlineData(12, 2)] [InlineData(12, 3)] [InlineData(12, 4)]
    public void PressureCommitReusesEmptyArenasAndRetainsFailedCheckpoint(int slots, int failedBarrier)
    {
        var seed = GroupSeed(8192); var disk = new Disk(seed.Image.ToArray()) { Buffered = true };
        var work = TrstWork();
        var budget = TrstGroupHeaderBytes + 2 * (88 + (uint)slots * 520);
        Assert.True(InitializeTrstGroup(ref disk, GroupAddress, budget));
        var expected = seed.Bytes.ToArray();
        var payload = new byte[] { 93 };
        Request rejected = default;
        var position = 0u;
        for (; position < 8192; position += 512)
        {
            payload.CopyTo(disk.Ram, 4000);
            rejected = new Request { Node = seed.Node, Buffer = APTR.FromPointer(4000), Position = position, Length = 1 };
            if (!ExecuteTrstGroup(ref disk, ref work, ref rejected, TrstOperation.Write, GroupAddress, out _)) break;
            payload.CopyTo(expected, (int)position);
        }
        Assert.InRange(position, 512u, 7680u);
        Assert.Equal(DOS.Error.NoFreeStore, work.Error);
        Assert.Equal(position, rejected.Position);
        Assert.Equal(expected, GroupRead(ref disk, seed.Node));
        if (failedBarrier != 0)
        {
            disk.FailAtFlush = disk.Flushes + failedBarrier;
            Assert.False(FlushTrstGroup(ref disk, ref work, GroupAddress));
            Assert.True(TrstGroupPending(ref disk, GroupAddress));
            Assert.Equal(expected, GroupRead(ref disk, seed.Node));
            disk.FailAtFlush = 0;
        }
        Assert.True(FlushTrstGroup(ref disk, ref work, GroupAddress));
        var checkpoint = disk.Snapshot();
        var writes = disk.Writes; var flushes = disk.Flushes;
        payload.CopyTo(disk.Ram, 4000);
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref rejected, TrstOperation.Write, GroupAddress, out var actual));
        Assert.Equal(1u, actual); Assert.True(TrstGroupPending(ref disk, GroupAddress));
        Assert.Equal(budget, disk.ReadUInt32(GroupAddress, 8));
        Assert.Equal(writes, disk.Writes); // No journal or home write for the accepted retry.
        Assert.Equal(flushes + 1, disk.Flushes); // Portable Prepare flush; native clean-device elision removes it.
        var remount = new Disk(checkpoint);
        Assert.True(Recover(ref remount, ref work));
        Assert.Equal(expected, GroupRead(ref remount, seed.Node));
        payload.CopyTo(expected, (int)position);
        Assert.Equal(expected, GroupRead(ref disk, seed.Node));
        Assert.True(FlushTrstGroup(ref disk, ref work, GroupAddress));
        remount = new Disk(disk.Snapshot());
        Assert.True(Recover(ref remount, ref work));
        Assert.Equal(expected, GroupRead(ref remount, seed.Node));
    }

    [Fact]
    public void MixedGroupCapacityRejectionKeepsAcceptedMetadataFlushable()
    {
        var seed = GroupSeed(0); var disk = new Disk(seed.Image.ToArray()); var work = TrstWork();
        Assert.True(InitializeTrstGroup(ref disk, GroupAddress, TrstGroupHeaderBytes + 2 * (24 + 8 + 520)));
        var request = new Request { Node = seed.Node, Position = 32 };
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.SetProtection, GroupAddress, out _));
        var accepted = disk.RedoOverlay;
        request = new Request { Directory = 1, Name = disk.Name("Rejected"), Length = 8 };
        Assert.False(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.CreateDirectory, GroupAddress, out _));
        Assert.Equal(DOS.Error.NoFreeStore, work.Error); Assert.Equal(accepted, disk.RedoOverlay);
        Assert.Equal(0, disk.Writes);
        Assert.True(FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot());
        Assert.Equal(0u, AmigaSmartFileSystemCore.Resolve(ref remount, 1, remount.Name("Rejected"), 8, work.Meta, out _));
        var fib = APTR.FromPointer(8000);
        Assert.True(AmigaSmartFileSystemCore.Examine(ref remount, seed.Node, fib, work.Meta));
        Assert.Equal(32u, remount.ReadUInt32(fib, 116));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void MixedTrstPacketsRollbackAndRecoverTogether(bool failFlush, bool shared)
    {
        var seed = GroupSeed(1024);
        var payload = Enumerable.Repeat((byte)93, 1024).ToArray();
        var initialDisk = new Disk(seed.Image.ToArray());
        Assert.True(AmigaSmartFileSystemCore.CountUsed(ref initialDisk, TrstWork().Meta, out var oldUsed));
        uint finalUsed = 0;
        void Stage(ref Disk disk)
        {
            var work = TrstWork();
            Assert.True(shared ? InitializeSharedTrstGroup(ref disk, GroupAddress, 192 * 1024) : InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
            Assert.True(GroupWrite(ref disk, seed.Node, 0, payload));
            var request = new Request { Node = seed.Node, Position = 32 };
            Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.SetProtection, GroupAddress, out _));
            request = new Request { Node = seed.Node, Directory = 1, Name = disk.Name("Renamed"), Length = 7 };
            Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Rename, GroupAddress, out _), work.Error.ToString());
            request = new Request { Directory = 1, Name = disk.Name("Renamed"), Length = 7 };
            Assert.False(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.CreateFile, GroupAddress, out _));
            Assert.Equal(payload, GroupRead(ref disk, seed.Node));
            disk.Ram[5000] = 65;
            request = new Request { Node = seed.Node, Buffer = APTR.FromPointer(5000), Length = 1 };
            Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.SetComment, GroupAddress, out _));
            disk.WriteUInt32(APTR.FromPointer(6000), 0, 10);
            disk.WriteUInt32(APTR.FromPointer(6000), 4, 123);
            disk.WriteUInt32(APTR.FromPointer(6000), 8, 100);
            request = new Request { Node = seed.Node, Buffer = APTR.FromPointer(6000) };
            Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.SetDate, GroupAddress, out _));
            request = new Request { Directory = 1, Name = disk.Name("Directory"), Length = 9 };
            Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.CreateDirectory, GroupAddress, out _));
            request = new Request { Node = request.Node };
            Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Remove, GroupAddress, out _));
            request = new Request { Directory = 1, Name = disk.Name("Temporary"), Length = 9 };
            Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.CreateFile, GroupAddress, out _), work.Error.ToString());
            var temporary = request.Node;
            Assert.True(GroupWrite(ref disk, temporary, 0, payload));
            request = new Request { Node = temporary, Position = 513 };
            Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Resize, GroupAddress, out _));
            Assert.Equal(payload[..513], GroupRead(ref disk, temporary));
            request = new Request { Node = temporary, Directory = 1, Name = disk.Name("Temporary"), Length = 9 };
            Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.ReplaceFile, GroupAddress, out _));
            temporary = request.Node;
            Assert.True(GroupWrite(ref disk, temporary, 0, payload));
            request = new Request { Node = temporary };
            Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Truncate, GroupAddress, out _), work.Error.ToString());
            Assert.True(GroupWrite(ref disk, temporary, 0, payload));
            request = new Request { Node = temporary };
            Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Remove, GroupAddress, out _), work.Error.ToString());
            Assert.True(TrstGroupPending(ref disk, GroupAddress));
            Assert.Equal(payload, GroupRead(ref disk, seed.Node));
        }
        void Check(byte[] image, bool final = false)
        {
            var disk = new Disk(image); var work = TrstWork();
            Assert.True(Recover(ref disk, ref work));
            var renamed = AmigaSmartFileSystemCore.Resolve(ref disk, 1, disk.Name("Renamed"), 7, work.Meta, out _);
            Assert.Equal(renamed == 0 ? seed.Bytes : payload, GroupRead(ref disk, seed.Node));
            Assert.True(AmigaSmartFileSystemCore.CountUsed(ref disk, work.Meta, out var used));
            Assert.Equal(renamed == 0 ? oldUsed : finalUsed, used);
            if (final) Assert.Equal(seed.Node, renamed);
        }
        var baseline = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true };
        Stage(ref baseline);
        var work = TrstWork(); Assert.True(FlushTrstGroup(ref baseline, ref work, GroupAddress));
        Assert.True(AmigaSmartFileSystemCore.CountUsed(ref baseline, work.Meta, out finalUsed));
        Check(baseline.Snapshot(), true);
        for (var cut = 1; cut <= baseline.IoEvents; cut++)
        {
            var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CutAfterIo = cut };
            Assert.Throws<PowerCut>(() => { Stage(ref disk); var w = TrstWork(); FlushTrstGroup(ref disk, ref w, GroupAddress); });
            foreach (var image in disk.CrashImages()) Check(image);
        }
        var probes = failFlush ? baseline.Flushes : baseline.Writes;
        for (var stop = 1; stop <= probes; stop++)
        {
            var disk = new Disk(seed.Image.ToArray()) { Buffered = true };
            Stage(ref disk);
            if (failFlush) disk.FailAtFlush = disk.Flushes + stop;
            else disk.FailAtWrite = disk.Writes + stop;
            work = TrstWork();
            var ok = FlushTrstGroup(ref disk, ref work, GroupAddress);
            if (!ok) Assert.True(TrstGroupPending(ref disk, GroupAddress));
            Assert.Equal(payload, GroupRead(ref disk, seed.Node));
            disk.FailAtFlush = disk.FailAtWrite = 0;
            Assert.True(FlushTrstGroup(ref disk, ref work, GroupAddress), work.Error.ToString());
            Check(disk.Snapshot(), true);
        }
    }
}
