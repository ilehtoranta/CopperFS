using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    private sealed class DeviceProtectionState(Disk disk)
    {
        public Disk Disk = disk;
        public bool Enforce, ProtectAfterFailure;
        public int Queries, Attempts;
        public uint JournalPages;
        public bool Borrowed, Released, FailJournalRun;
    }

    private struct DeviceProtectionPlatform(DeviceProtectionState state) : IAmigaWritableFileSystemPlatform
    {
        public AmigaVolumeGeometry Geometry => state.Disk.Geometry;
        public uint BlockCount => state.Disk.BlockCount;
        public uint BlockBytes => state.Disk.BlockBytes;
        public uint ReservedBlocks => state.Disk.ReservedBlocks;
        public APTR RedoOverlay { get => state.Disk.RedoOverlay; set => state.Disk.RedoOverlay = value; }
        public bool WriteProtected() { state.Queries++; return state.Disk.Protected; }
        public bool SupportsUnpublishedDataWrites => true;
        public bool UnpublishedDataWritesEnforceProtection => state.Enforce;
        public bool WriteUnpublishedDataBlocks(uint block, uint count, APTR source)
        {
            state.Attempts++;
            Assert.True(state.Disk.RedoOverlay.IsNull);
            if (state.Disk.Protected) return false;
            if (state.FailJournalRun && state.Borrowed && count > 1)
            {
                state.Disk.FailAtWrite = state.Disk.Writes + 2;
                state.FailJournalRun = false;
            }
            var result = state.Disk.WriteDataBlocks(block, count, source);
            if (!result && state.ProtectAfterFailure) state.Disk.Protected = true;
            return result;
        }
        public APTR BorrowSfsJournalPages(uint bytes, out uint capacity)
        {
            capacity = state.JournalPages;
            if (capacity == 0) return APTR.Null;
            Assert.False(state.Borrowed);
            state.Borrowed = true;
            return APTR.FromPointer(450000);
        }

        public void ReleaseSfsJournalPages(APTR pages)
        {
            Assert.Equal(450000u, pages.Raw);
            Assert.True(state.Borrowed);
            state.Borrowed = false;
            state.Released = true;
        }
        public bool IsMapped(APTR address, uint bytes) => state.Disk.IsMapped(address, bytes);
        public byte ReadUInt8(APTR address, int offset = 0) => state.Disk.ReadUInt8(address, offset);
        public ushort ReadUInt16(APTR address, int offset = 0) => state.Disk.ReadUInt16(address, offset);
        public uint ReadUInt32(APTR address, int offset = 0) => state.Disk.ReadUInt32(address, offset);
        public void WriteUInt8(APTR address, int offset, byte value) => state.Disk.WriteUInt8(address, offset, value);
        public void WriteUInt16(APTR address, int offset, ushort value) => state.Disk.WriteUInt16(address, offset, value);
        public void WriteUInt32(APTR address, int offset, uint value) => state.Disk.WriteUInt32(address, offset, value);
        public void Clear(APTR address, uint bytes) => state.Disk.Clear(address, bytes);
        public void Copy(APTR source, APTR destination, uint bytes) => state.Disk.Copy(source, destination, bytes);
        public bool ReadBlock(uint block, APTR destination) => state.Disk.ReadBlock(block, destination);
        public bool ReadMetadataBlock(uint block, uint id, APTR destination) => state.Disk.ReadMetadataBlock(block, id, destination);
        public APTR ReadMetadataView(uint block, uint id, APTR scratch) => state.Disk.ReadMetadataView(block, id, scratch);
        public uint ReadDataBlocks(uint block, uint count, APTR destination, bool readAhead) =>
            ReadDataBlocks(block, count, destination);
        public uint ReadDataBlocks(uint block, uint count, APTR destination) => state.Disk.ReadDataBlocks(block, count, destination);
        public bool WriteBlock(uint block, APTR source) => state.Disk.WriteBlock(block, source);
        public bool WriteDataBlocks(uint block, uint count, APTR source) => state.Disk.WriteDataBlocks(block, count, source);
        public bool Flush() => state.Disk.Flush();
    }

    [Fact]
    public void FailedJournalRunRestoresWorkspaceAndRetriesWholeGroup()
    {
        var seed = GroupSeed(4096);
        var state = new DeviceProtectionState(new Disk(seed.Image.ToArray())) { JournalPages = 2 };
        var payload = seed.Bytes.ToArray();
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeSharedTrstGroup(ref state.Disk, GroupAddress, 192 * 1024));
        // Partial existing sectors stay in the transaction and produce a
        // multi-page redo stream, independent of bulk relocation policy.
        var patch = Enumerable.Range(0, 460).Select(i => (byte)(i * 19 + 7)).ToArray();
        for (var sector = 0; sector < 8; sector++)
        {
            var offset = sector * 512 + 11;
            patch.CopyTo(payload, offset);
            Assert.True(GroupWrite(ref state.Disk, seed.Node, (uint)offset, patch));
        }
        var p = new DeviceProtectionPlatform(state);
        var w = TrstWork();
        var scratch = w.Journal;
        state.FailJournalRun = true;
        Assert.False(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref p, ref w, GroupAddress));
        Assert.True(state.Released);
        Assert.Equal(scratch, w.Journal);
        Assert.Contains(state.Disk.DataWrites, run => run.Count == 2);
        var interrupted = new Disk(state.Disk.Snapshot());
        Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref interrupted, ref w));
        Assert.Equal(seed.Bytes, GroupRead(ref interrupted, seed.Node));

        state.Disk.FailAtWrite = 0;
        state.Released = false;
        Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref p, ref w, GroupAddress));
        Assert.True(state.Released);
        Assert.Equal(scratch, w.Journal);
        var committed = new Disk(state.Disk.Snapshot());
        Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref committed, ref w));
        Assert.Equal(payload, GroupRead(ref committed, seed.Node));
    }
    [Theory]
    [InlineData(false, 0, false)] [InlineData(false, 0, true)]
    [InlineData(false, 1, false)] [InlineData(false, 1, true)]
    [InlineData(false, 2, false)] [InlineData(false, 2, true)]
    [InlineData(false, 3, false)] [InlineData(false, 3, true)]
    [InlineData(true, 0, false)] [InlineData(true, 0, true)]
    [InlineData(true, 1, false)] [InlineData(true, 1, true)]
    [InlineData(true, 2, false)] [InlineData(true, 2, true)]
    [InlineData(true, 3, false)] [InlineData(true, 3, true)]
    public void TrstDirectWritesEnforceProtectionAndRetainAcceptedGroup(bool enforce, int failure, bool scratch)
    {
        var seed = ContinuationSeed(); var expected = seed.Bytes.ToArray();
        var disk = StageTrstProof(seed.Image, expected, scratchView: scratch);
        var earlier = Enumerable.Repeat((byte)0xD3, 1024).ToArray();
        Assert.True(DeferredWrite(ref disk, 3072, earlier, out var handled, out _)); Assert.True(handled);
        earlier.CopyTo(expected, 3072);
        var active = disk.RedoOverlay;
        var before = disk.Ram.AsSpan((int)GroupAddress.Raw, (int)disk.ReadUInt32(GroupAddress, 8)).ToArray();
        var payload = Enumerable.Repeat((byte)0xE4, 1024).ToArray(); payload.CopyTo(disk.Ram, 4000);
        if (failure == 1) disk.Protected = true;
        if (failure >= 2) disk.FailAtWrite = disk.Writes + 2; // Fail after the first unpublished sector.
        var state = new DeviceProtectionState(disk) { Enforce = enforce, ProtectAfterFailure = failure == 3 };
        var platform = new DeviceProtectionPlatform(state);
        var request = new AmigaSmartFileSystemWriteCore.Request
            { Node = 3, Position = 4096, Length = 1024, Buffer = APTR.FromPointer(4000) };
        var success = AmigaSmartFileSystemWriteCore.TryContinueTrstGroup(ref platform, GroupAddress,
            TrstWork().Meta, APTR.Null, ref request, out handled, out var error, true);
        Assert.Equal(failure == 0, success); Assert.Equal(success, handled);
        Assert.Equal(enforce && success ? 0 : 1, state.Queries);
        Assert.Equal(!enforce && failure == 1 ? 0 : 1, state.Attempts);
        Assert.Equal(active, state.Disk.RedoOverlay);
        if (failure == 0)
        {
            Assert.Equal(DOS.Error.None, error); payload.CopyTo(expected, 4096);
        }
        else
        {
            Assert.Equal(failure == 1 || enforce && failure == 3 ? DOS.Error.DiskWriteProtected : DOS.Error.DiskNotValidated, error);
            Assert.Equal(before, state.Disk.Ram.AsSpan((int)GroupAddress.Raw, before.Length).ToArray());
        }
        state.Disk.Protected = false; state.Disk.FailAtWrite = 0;
        Assert.Equal(expected, DeferredRead(ref state.Disk, expected.Length));
        if (!success)
        {
            Assert.True(GroupWrite(ref state.Disk, 3, 4096, payload)); payload.CopyTo(expected, 4096);
        }
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref state.Disk, ref work, GroupAddress));
        var remount = new Disk(state.Disk.Snapshot()); Assert.Equal(expected, DeferredRead(ref remount, expected.Length));
        Assert.Equal("hello"u8.ToArray(), GroupRead(ref remount, 4));
    }
}
