using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)]
    public void IdenticalMetadataDoesNotPublishAnotherTransaction(int kind, bool trst)
    {
        var disk = new Disk(Image());
        var work = TrstWork();
        var request = new AmigaSmartFileSystemWriteCore.Request
        { Node = 3, Position = 32, Buffer = APTR.FromPointer(4000), Length = 3 };
        disk.Ram[4000] = 65; disk.Ram[4001] = 66; disk.Ram[4002] = 67;
        if (kind == 1)
        { disk.WriteUInt32(request.Buffer, 0, 10); disk.WriteUInt32(request.Buffer, 4, 123); disk.WriteUInt32(request.Buffer, 8, 100); }
        var action = kind == 0 ? DosPacketAction.SetProtection : kind == 1 ? DosPacketAction.SetDate : DosPacketAction.SetComment;
        var operation = kind == 0 ? AmigaSmartFileSystemWriteCore.TrstOperation.SetProtection : kind == 1 ? AmigaSmartFileSystemWriteCore.TrstOperation.SetDate : AmigaSmartFileSystemWriteCore.TrstOperation.SetComment;
        bool Apply() => trst
            ? AmigaSmartFileSystemWriteCore.ExecuteTrst(ref disk, ref work, ref request, operation, APTR.FromPointer(200000), 196608, out _)
            : AmigaSmartFileSystemWriteCore.SetMetadata(ref disk, ref work, ref request, action);
        Assert.True(Apply(), work.Error.ToString());
        var image = disk.Snapshot(); var writes = disk.Writes; var flushes = disk.Flushes;
        Assert.True(Apply(), work.Error.ToString());
        Assert.Equal(writes, disk.Writes);
        // ExecuteTrst retains its pre-staging flush (which the native clean
        // device elides); no journal publication or commit barriers follow.
        Assert.Equal(flushes + (trst ? 1 : 0), disk.Flushes);
        Assert.Equal(image, disk.Snapshot());
        if (kind == 0) request.Position = 0;
        else if (kind == 1) disk.WriteUInt32(request.Buffer, 0, 11);
        else disk.Ram[4000] = 68;
        Assert.True(Apply(), work.Error.ToString());
        Assert.True(disk.Writes > writes); Assert.True(disk.Flushes > flushes);
    }
}
