using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(26)]
    public void BufferedPackedMutationsAndRejectedCommentSurviveReopen(int tailBytes)
    {
        var disk = new Disk(PackedObjectTests.PackedImage(tailBytes)) { Buffered = true, CheckReadOnlyViews = true };
        var work = BufferedWork(ref disk);
        var request = new AmigaSmartFileSystemWriteCore.Request
            { Node = 3, Buffer = disk.Name("new"), Length = 3 };
        Assert.True(AmigaSmartFileSystemWriteCore.SetMetadata(ref disk, ref work, ref request, DosPacketAction.SetComment), work.Error.ToString());
        request.Buffer = disk.Name("x\0x");
        Assert.False(AmigaSmartFileSystemWriteCore.SetMetadata(ref disk, ref work, ref request, DosPacketAction.SetComment));
        Assert.Equal(DOS.Error.BadNumber, work.Error);
        request = new() { Directory = 1, Name = disk.Name("Added"), Length = 5 };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref request, false), work.Error.ToString());
        Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, 9), work.Error.ToString());
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work), work.Error.ToString());
        var reopened = new Disk(disk.Snapshot()); var recovered = TrstWork();
        Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref reopened, ref recovered));
        var report = ReliabilityFixture.Inspect(reopened.Snapshot());
        Assert.Equal("new", report.Entries.Single(e => e.Node == 3).Comment);
        Assert.Contains(report.Entries, e => e.Path == "/Added");
        Assert.DoesNotContain(report.Entries, e => e.Node == 9);
        Assert.Equal(9, report.Entries.Count);
    }
}
