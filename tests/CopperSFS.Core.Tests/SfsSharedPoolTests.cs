using Amiga;
using static CopperSFS.AmigaSmartFileSystemWriteCore;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AcceptedAttributesPreserveRollbackAndExplicitFlush(bool borrowed)
    {
        var seed = GroupSeed(512);
        var disk = new Disk(seed.Image.ToArray()) { BorrowRedoImages = borrowed };
        var w = TrstWork();
        Assert.True(InitializeSharedTrstGroup(ref disk, GroupAddress, 9756));
        var r = new Request { Node = seed.Node, Position = 32, Buffer = APTR.FromPointer(4000) };
        Assert.True(ExecuteTrstGroup(ref disk, ref w, ref r, TrstOperation.SetProtection, GroupAddress, out _));
        var accepted = disk.RedoOverlay;
        var before = disk.Snapshot();
        var flushes = disk.Flushes;
        var writes = disk.Writes;
        disk.WriteUInt32(r.Buffer, 4, 1440);
        Assert.False(ExecuteTrstGroup(ref disk, ref w, ref r, TrstOperation.SetDate, GroupAddress, out _));
        Assert.Equal(DOS.Error.BadNumber, w.Error);
        Assert.Equal(accepted, disk.RedoOverlay);
        disk.WriteUInt32(r.Buffer, 0, 13); disk.WriteUInt32(r.Buffer, 4, 7); disk.WriteUInt32(r.Buffer, 8, 50);
        Assert.True(ExecuteTrstGroup(ref disk, ref w, ref r, TrstOperation.SetDate, GroupAddress, out _));
        if (borrowed) Assert.Equal(accepted, disk.RedoOverlay);
        r.Node = 0; disk.WriteUInt32(r.Buffer, 4, 1440);
        Assert.False(ExecuteTrstGroup(ref disk, ref w, ref r, TrstOperation.SetDate, GroupAddress, out _));
        Assert.Equal(DOS.Error.DiskNotValidated, w.Error);
        Assert.Equal(writes, disk.Writes); Assert.Equal(flushes, disk.Flushes);
        Assert.Equal(before, disk.Snapshot());
        Assert.True(FlushTrstGroup(ref disk, ref w, GroupAddress));
        var remount = new Disk(disk.Snapshot());
        var fib = APTR.FromPointer(8000);
        Assert.True(AmigaSmartFileSystemCore.Examine(ref remount, seed.Node, fib, w.Meta));
        Assert.Equal(32u, remount.ReadUInt32(fib, 116));
        Assert.Equal(13u, remount.ReadUInt32(fib, 132));
        Assert.Equal(7u, remount.ReadUInt32(fib, 136));
        Assert.Equal(50u, remount.ReadUInt32(fib, 140));
    }

    [Fact]
    public void OwnedMountReusesCleanRootsUntilRecoveryOrRemount()
    {
        var disk = new Disk(Image()) { SfsMountStorage = APTR.FromPointer(7000) };
        var work = TrstWork();
        Assert.True(AmigaSmartFileSystemCore.Mount(ref disk, work.Meta));
        var container = disk.ReadUInt32(disk.SfsMountStorage);
        Assert.NotEqual(0u, container);
        disk.FailReadBlock = container + 2;
        Assert.Equal(3u, AmigaSmartFileSystemCore.Resolve(ref disk, 1, disk.Name("Hello"), 5, work.Meta, out _));
        var read = new AmigaSmartFileSystemCore.ReadCursor();
        Assert.Equal(1300, AmigaSmartFileSystemCore.Read(ref disk, ref read, 3, 0,
            APTR.FromPointer(4000), 1300, work.Meta, work.Data, out _));
        var expected = disk.Ram.AsSpan(4000, 1300).ToArray();
        expected.AsSpan(101, 951).Fill(0xA5);
        disk.Ram.AsSpan(4000, 951).Fill(0xA5);
        Assert.True(InitializeSharedTrstGroup(ref disk, GroupAddress, 9756));
        var request = new Request { Node = 3, Position = 101, Buffer = APTR.FromPointer(4000), Length = 951 };
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, GroupAddress, out var written));
        Assert.Equal(951u, written);
        disk.FailReadBlock = uint.MaxValue;
        disk.FailAtFlush = disk.Flushes + 2;
        Assert.False(FlushTrstGroup(ref disk, ref work, GroupAddress));
        Assert.Equal(0u, disk.ReadUInt32(disk.SfsMountStorage));
        disk.FailAtFlush = 0;
        Assert.True(FlushTrstGroup(ref disk, ref work, GroupAddress));
        Assert.True(AmigaSmartFileSystemCore.Mount(ref disk, work.Meta));
        Assert.Equal(expected, GroupRead(ref disk, 3));
        disk.FailReadBlock = container + 2;
        Assert.False(Recover(ref disk, ref work));
        Assert.Equal(0u, disk.ReadUInt32(disk.SfsMountStorage));
        Assert.Equal(0u, AmigaSmartFileSystemCore.Resolve(ref disk, 1, disk.Name("Hello"), 5, work.Meta, out _));
        Assert.False(AmigaSmartFileSystemCore.Mount(ref disk, work.Meta));
        Assert.Equal(0u, disk.ReadUInt32(disk.SfsMountStorage));
    }

    [Theory]
    [InlineData(9756u, 17u)] [InlineData(8716u, 15u)]
    public void SharedPoolCopiesOnlyChangedImagesAndRejectsWideEdits(uint budget, uint capacity)
    {
        var disk = new Disk(Image());
        disk.Ram.AsSpan((int)GroupAddress.Raw - 4, (int)budget + 8).Fill(0xA5);
        Assert.True(InitializeSharedTrstGroup(ref disk, GroupAddress, budget));
        var accepted = APTR.FromPointer(disk.ReadUInt32(GroupAddress, 16));
        var trial = APTR.FromPointer(disk.ReadUInt32(GroupAddress, 20));
        Assert.False(Sfs2MetadataStore.IsStore(ref disk, accepted));
        Assert.Equal(capacity, disk.ReadUInt32(accepted, 4));
        Assert.Equal(budget, disk.ReadUInt32(GroupAddress, 8));
        var source = APTR.FromPointer(4000);
        // Deliberate hash collisions exercise bounded probing and stable order.
        for (var i = 0u; i < 9; i++)
        {
            disk.WriteUInt32(source, 0, i);
            Assert.True(AmigaSmartFileSystemRedoArena.Store(ref disk, accepted, i * 32, source));
        }
        SfsSharedImagePool.Finish(ref disk, accepted);
        var unchanged = AmigaSmartFileSystemRedoArena.Find(ref disk, accepted, 32);
        var original = AmigaSmartFileSystemRedoArena.Find(ref disk, accepted, 0);
        SfsSharedImagePool.Begin(ref disk, accepted, trial, true);
        Assert.Equal(unchanged, AmigaSmartFileSystemRedoArena.Find(ref disk, trial, 32));
        disk.WriteUInt32(source, 0, 123);
        Assert.True(AmigaSmartFileSystemRedoArena.Store(ref disk, trial, 0, source));
        var changed = AmigaSmartFileSystemRedoArena.Find(ref disk, trial, 0);
        Assert.NotEqual(original, changed);
        Assert.Equal(0u, disk.ReadUInt32(original));
        Assert.Equal(123u, disk.ReadUInt32(changed));
        // A second edit reuses the private image and does not need another slot.
        Assert.True(AmigaSmartFileSystemRedoArena.Store(ref disk, trial, 0, source));
        Assert.Equal(changed, AmigaSmartFileSystemRedoArena.Find(ref disk, trial, 0));
        for (var i = 1u; i < capacity - 9; i++)
            Assert.True(AmigaSmartFileSystemRedoArena.Store(ref disk, trial, i * 32, source));
        Assert.False(AmigaSmartFileSystemRedoArena.Store(ref disk, trial, (capacity - 9) * 32, source));
        SfsSharedImagePool.Finish(ref disk, accepted); // reject; accepted bytes remain intact
        for (var i = 0u; i < 9; i++)
            Assert.Equal(i, disk.ReadUInt32(AmigaSmartFileSystemRedoArena.Find(ref disk, accepted, i * 32)));
        // Reuse all rejected slots repeatedly. No gradual loss of capacity.
        for (var iteration = 0; iteration < 40; iteration++)
        {
            SfsSharedImagePool.Begin(ref disk, accepted, trial, true);
            Assert.True(AmigaSmartFileSystemRedoArena.Store(ref disk, trial, 0, source));
            Assert.Equal(unchanged, AmigaSmartFileSystemRedoArena.Find(ref disk, trial, 32));
            SfsSharedImagePool.Finish(ref disk, trial);
            (accepted, trial) = (trial, accepted);
        }
        Assert.Equal(0xA5A5A5A5u, disk.ReadUInt32(APTR.FromPointer(GroupAddress.Raw - 4)));
        Assert.Equal(0xA5A5A5A5u, disk.ReadUInt32(APTR.FromPointer(GroupAddress.Raw + budget)));
        Assert.Equal(0, disk.Writes); Assert.Equal(0, disk.Flushes);
    }

    [Fact]
    public void SharedPoolEditableBorrowKeepsAcceptedBytesAndCertificateImmutable()
    {
        var disk = new Disk(Image());
        Assert.True(InitializeSharedTrstGroup(ref disk, GroupAddress, 9756));
        var accepted = APTR.FromPointer(disk.ReadUInt32(GroupAddress, 16));
        var trial = APTR.FromPointer(disk.ReadUInt32(GroupAddress, 20));
        var source = APTR.FromPointer(4000);
        Assert.True(disk.ReadBlock(2, source));
        Assert.True(AmigaSmartFileSystemRedoArena.StoreSealed(ref disk, accepted, 2, source));
        SfsSharedImagePool.Finish(ref disk, accepted);
        var read = AmigaSmartFileSystemRedoArena.MetadataView(ref disk, accepted, 2, Objects, false, out var found);
        Assert.True(found); Assert.True(read.IsNotNull);
        var bytes = disk.Ram.AsSpan((int)read.Raw, 512).ToArray();
        SfsSharedImagePool.Begin(ref disk, accepted, trial, true);
        var edit = AmigaSmartFileSystemRedoArena.MetadataView(ref disk, trial, 2, Objects, true, out found);
        Assert.True(found); Assert.True(edit.IsNotNull); Assert.NotEqual(read, edit);
        Assert.Equal(0u, disk.ReadUInt32(APTR.FromPointer(edit.Raw - 4)));
        Assert.Equal(Objects, disk.ReadUInt32(APTR.FromPointer(read.Raw - 4)));
        disk.WriteUInt32(edit, 12, 123);
        Assert.Equal(bytes, disk.Ram.AsSpan((int)read.Raw, 512).ToArray());
        SfsSharedImagePool.Finish(ref disk, accepted);
        // The editable handle's lifetime has ended; acquire a new read view.
        Assert.Equal(read, AmigaSmartFileSystemRedoArena.MetadataView(ref disk, accepted, 2, Objects, false, out found));
        Assert.True(found);
        Assert.Equal(bytes, disk.Ram.AsSpan((int)read.Raw, 512).ToArray());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void SharedPoolPressureRetainsAcceptedStateAndExplicitFlush(int failedBarrier)
    {
        var seed = GroupSeed(16384); var disk = new Disk(seed.Image.ToArray()) { Buffered = true };
        var work = TrstWork();
        Assert.True(InitializeSharedTrstGroup(ref disk, GroupAddress, 9756));
        var expected = seed.Bytes.ToArray();
        uint position;
        Request request = default;
        for (position = 0; position < 16384; position += 512)
        {
            disk.Ram[4000] = 93;
            request = new Request { Node = seed.Node, Position = position, Buffer = APTR.FromPointer(4000), Length = 1 };
            if (!ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, GroupAddress, out _)) break;
            expected[position] = 93;
        }
        Assert.InRange(position, 512u, 15872u);
        Assert.Equal(DOS.Error.NoFreeStore, work.Error);
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
        var remount = new Disk(disk.Snapshot());
        Assert.True(Recover(ref remount, ref work));
        Assert.Equal(expected, GroupRead(ref remount, seed.Node));
        disk.Ram[4000] = 93;
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, GroupAddress, out _));
        expected[position] = 93;
        Assert.True(FlushTrstGroup(ref disk, ref work, GroupAddress));
        remount = new Disk(disk.Snapshot());
        Assert.True(Recover(ref remount, ref work));
        Assert.Equal(expected, GroupRead(ref remount, seed.Node));
    }
}
