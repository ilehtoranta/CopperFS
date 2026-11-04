using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    [Fact]
    public void BufferedNodeFullnessSurvivesFillFreeAndReopen()
    {
        var disk = new Disk(ReliabilityFixture.Image()) { Buffered = true, CheckReadOnlyViews = true };
        var work = BufferedWork(ref disk);
        for (var i = 0; i < 94; i++)
        {
            var name = $"F{i:D3}";
            var request = new AmigaSmartFileSystemWriteCore.Request { Directory = 1, Name = disk.Name(name), Length = 4 };
            Assert.True(AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref request, false), work.Error.ToString());
        }
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work));
        Assert.Equal(98, ReliabilityFixture.Inspect(disk.Snapshot()).Entries.Count);
        Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, 5), work.Error.ToString());
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work));
        var reopened = new Disk(disk.Snapshot()); var recovered = TrstWork();
        Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref reopened, ref recovered));
        Assert.Equal(97, ReliabilityFixture.Inspect(reopened.Snapshot()).Entries.Count);
    }
}
