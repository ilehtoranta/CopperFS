using Amiga;
using CopperSFS;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    [Theory]
    [InlineData(0u, 1024u)]
    [InlineData(37u, 700u)]
    public void InPlacePolicyChangesOnlyExistingPayloadAndHonorsFlush(uint position, uint length)
    {
        var initial = Image();
        var leaf = Block(initial, 5);
        U32(leaf, 24, 0x80000003); // Explicit first-extent owner for this opt-in policy.
        Sum(leaf);
        var disk = new Disk(initial.ToArray()) { Buffered = true };
        var work = new AmigaSmartFileSystemWriteCore.Work
        {
            Meta = APTR.FromPointer(512), Data = APTR.FromPointer(1024),
            Bitmap = APTR.FromPointer(1536), Journal = APTR.FromPointer(2048)
        };
        var source = APTR.FromPointer(4000);
        Array.Fill(disk.Ram, (byte)0xD4, 4000, (int)length);
        var request = new AmigaSmartFileSystemWriteCore.Request { Node = 3, Position = position, Length = length, Buffer = source };
        Assert.True(AmigaSmartFileSystemWriteCore.TryInPlaceOverwrite(ref disk, ref work, ref request, APTR.Null, out var handled));
        Assert.True(handled);
        Assert.True(disk.Flush());
        var expected = initial.ToArray();
        Array.Fill(expected, (byte)0xD4, (int)(20 * 512 + position), (int)length);
        Assert.Equal(expected, disk.Snapshot());

        request.Position = 1300;
        Assert.True(AmigaSmartFileSystemWriteCore.TryInPlaceOverwrite(ref disk, ref work, ref request, APTR.Null, out handled));
        Assert.False(handled);
        request.Position = 0;
        request.Buffer = work.Meta;
        Assert.True(AmigaSmartFileSystemWriteCore.TryInPlaceOverwrite(ref disk, ref work, ref request, APTR.Null, out handled));
        Assert.False(handled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OddBodyAfterPartialSectorUsesBulkTransferAndPreservesErrorPosition(bool failBody)
    {
        var disk = new Disk(Image()) { SupportsUnalignedDataReads = true, Copies = [] };
        if (failBody) disk.FailReadBlock = 21;
        var cursor = new AmigaSmartFileSystemCore.ReadCursor();
        Array.Fill(disk.Ram, (byte)0xA7);
        var count = AmigaSmartFileSystemCore.Read(ref disk, ref cursor, 3, 7, APTR.FromPointer(4000),
            1280, APTR.FromPointer(512), APTR.FromPointer(1024), out var error);
        Assert.Equal(failBody ? 505 : 1280, count);
        Assert.Equal(failBody ? DOS.Error.DiskNotValidated : DOS.Error.None, error);
        Assert.Equal(new[] { (21u, 1u) }, disk.DataReads);
        var edges = disk.Copies!.Where(c => c.Destination >= 4000 && c.Destination < 5280)
            .Select(c => (c.Destination, c.Length));
        Assert.Equal(failBody ? [(4000u, 505u)] : new[] { (4000u, 505u), (5017u, 263u) }, edges);
        if (failBody) Assert.Equal(0u, cursor.Node);
        for (var i = -1; i <= 1280; i++)
            Assert.Equal(i >= 0 && i < count ? (byte)((i + 7) * 17) : (byte)0xA7, disk.Ram[4000 + i]);
    }
    [Fact]
    public void SequentialAlignedReadsUseTheSameExtentAndResetAfterFailure()
    {
        var disk = new Disk(Image());
        var cursor = new AmigaSmartFileSystemCore.ReadCursor();
        var metadata = APTR.FromPointer(512); var data = APTR.FromPointer(1024);
        Assert.Equal(512, AmigaSmartFileSystemCore.Read(ref disk, ref cursor, 3, 0,
            APTR.FromPointer(4000), 512, metadata, data, out var error));
        Assert.Equal(DOS.Error.None, error);
        Assert.Equal(512, AmigaSmartFileSystemCore.Read(ref disk, ref cursor, 3, 512,
            APTR.FromPointer(4512), 512, metadata, data, out error));
        Assert.Equal(DOS.Error.None, error);
        for (var index = 0; index < 1024; index++)
            Assert.Equal((byte)(index * 17), disk.Ram[4000 + index]);
        Assert.Equal(new[] { (20u, 1u), (21u, 1u) }, disk.DataReads);

        var failing = new Disk(Image()) { FailReadBlock = 21 };
        cursor = new AmigaSmartFileSystemCore.ReadCursor();
        Assert.Equal(512, AmigaSmartFileSystemCore.Read(ref failing, ref cursor, 3, 0,
            APTR.FromPointer(4000), 512, metadata, data, out error));
        Assert.Equal(-1, AmigaSmartFileSystemCore.Read(ref failing, ref cursor, 3, 512,
            APTR.FromPointer(4512), 512, metadata, data, out error));
        Assert.Equal(DOS.Error.DiskNotValidated, error);
        Assert.Equal(0u, cursor.Node);
    }

    [Fact]
    public void BulkReadReportsOnlyTheCompletedPrefixAfterADeviceFailure()
    {
        var disk = new Disk(Image()) { FailReadBlock = 21 };
        Array.Fill(disk.Ram, (byte)0xA7);
        var cursor = new AmigaSmartFileSystemCore.ReadCursor();
        Assert.Equal(512, AmigaSmartFileSystemCore.Read(ref disk, ref cursor, 3, 0,
            APTR.FromPointer(4000), 1300, APTR.FromPointer(512), APTR.FromPointer(1024), out var error));
        Assert.Equal(DOS.Error.DiskNotValidated, error);
        Assert.Equal(0u, cursor.Node);
        Assert.Equal(new[] { (20u, 2u) }, disk.DataReads);
        for (var index = 0; index < 1300; index++)
            Assert.Equal(index < 512 ? (byte)(index * 17) : (byte)0xA7, disk.Ram[4000 + index]);
    }

    [Fact]
    public void ReadCursorsSupportInterleavedHandlesBackwardSeeksAndMutationInvalidation()
    {
        var disk = new Disk(Image()) { Buffered = true };
        var first = new AmigaSmartFileSystemCore.ReadCursor();
        var second = new AmigaSmartFileSystemCore.ReadCursor();
        var metadata = APTR.FromPointer(512); var data = APTR.FromPointer(1024);
        foreach (var position in new uint[] { 0, 512, 1024, 3, 700, 1300 })
        {
            var count = Math.Min(300u, 1300 - position);
            Assert.Equal((int)count, AmigaSmartFileSystemCore.Read(ref disk, ref first, 3, position,
                APTR.FromPointer(4000), count, metadata, data, out var error));
            Assert.Equal(DOS.Error.None, error);
            for (var i = 0u; i < count; i++) Assert.Equal((byte)((position + i) * 17), disk.Ram[4000 + i]);
            Assert.Equal(100, AmigaSmartFileSystemCore.Read(ref disk, ref second, 3, 800,
                APTR.FromPointer(5000), 100, metadata, data, out error));
            Assert.Equal(DOS.Error.None, error);
        }
        var work = BatchWork(ref disk);
        Assert.True(AmigaSmartFileSystemWriteCore.Resize(ref disk, ref work, 3, 600));
        first.Node = second.Node = 0; // The owner invalidates all handles after mutation.
        Assert.Equal(88, AmigaSmartFileSystemCore.Read(ref disk, ref first, 3, 512,
            APTR.FromPointer(4000), 300, metadata, data, out var result));
        Assert.Equal(DOS.Error.None, result);
        Assert.Equal(-1, AmigaSmartFileSystemCore.Read(ref disk, ref second, 3, 800,
            APTR.FromPointer(5000), 100, metadata, data, out result));
        Assert.Equal(DOS.Error.SeekError, result);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    public void ReadsAcrossFragmentedExtentsPreserveUnalignedRangesAndGuards(uint alignment)
    {
        foreach (var position in new uint[] { 0, 1, 511, 512, 513, 1023, 1299, 1300 })
        foreach (var length in new uint[] { 0, 1, 511, 512, 513, 1300 })
        {
            var disk = new Disk(Image()) { RequireAlignedBlockBuffers = true };
            Array.Fill(disk.Ram, (byte)0xA7);
            var destination = 4000u + alignment;
            var count = Math.Min(length, 1300 - position);
            Assert.Equal((int)count, AmigaSmartFileSystemCore.Read(ref disk, 3, position,
                APTR.FromPointer(destination), length, APTR.FromPointer(512), APTR.FromPointer(1024), out var error));
            Assert.Equal(DOS.Error.None, error);
            for (var index = 3999u; index < destination + length + 1; index++)
                Assert.Equal(index >= destination && index < destination + count
                    ? (byte)((position + index - destination) * 17) : (byte)0xA7, disk.Ram[index]);
        }
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    public void CompleteAndPartialOverwritesPreserveBytesOutsideTheCallerRange(uint alignment)
    {
        foreach (var position in new uint[] { 0, 1, 511, 512 })
        {
            var disk = new Disk(Image()) { Buffered = true, RequireAlignedBlockBuffers = true };
            var work = BatchWork(ref disk);
            var source = 4000u + alignment;
            const uint length = 788;
            for (var index = 0u; index < length; index++) disk.Ram[source + index] = (byte)(index * 29 + 3);
            var write = new AmigaSmartFileSystemWriteCore.Request
            { Node = 3, Position = position, Buffer = APTR.FromPointer(source), Length = length };
            Assert.True(AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref write, out var actual), work.Error.ToString());
            Assert.Equal(length, actual);
            var remounted = new Disk(disk.Snapshot()) { RequireAlignedBlockBuffers = true };
            Assert.Equal(1300, AmigaSmartFileSystemCore.Read(ref remounted, 3, 0,
                APTR.FromPointer(4000), 1300, APTR.FromPointer(512), APTR.FromPointer(1024), out var error));
            Assert.Equal(DOS.Error.None, error);
            for (var index = 0u; index < 1300; index++)
                Assert.Equal(index >= position && index < position + length
                    ? (byte)((index - position) * 29 + 3) : (byte)(index * 17), remounted.Ram[4000 + index]);
        }
    }
}
