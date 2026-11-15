using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Sfs2MutationConsumesExpiringReadOnlyViewsBeforeFurtherIo(bool combine)
    {
        var disk = new Disk(Sfs2WriteTests.Empty()) { Sfs2MetadataViews = true };
        if (combine) Combine(ref disk, 4);
        AmigaSmartFileSystem2WriteCore.Work Work() => new()
        {
            Meta = APTR.FromPointer(4000), Data = APTR.FromPointer(4512),
            Bitmap = APTR.FromPointer(5024), Journal = APTR.FromPointer(5536)
        };
        var w = Work();
        var r = new AmigaSmartFileSystem2WriteCore.Request { Directory = 1, Name = disk.Name("Views"), Length = 5 };
        Assert.True(AmigaSmartFileSystem2WriteCore.Create(ref disk, ref w, ref r, false), w.Error.ToString());
        var node = r.Node;
        var payload = APTR.FromPointer(8192);
        for (var i = 0; i < 16387; i++) disk.Ram[payload.Raw + i] = (byte)(i * 37 + 11);
        r = new() { Node = node, Buffer = payload, Length = 16387 }; w = Work();
        Assert.True(AmigaSmartFileSystem2WriteCore.Write(ref disk, ref w, ref r, out var actual), w.Error.ToString());
        Assert.Equal(r.Length, actual);
        Assert.True(disk.Flush()); Sfs2WriteTests.Inspect(disk.Snapshot());
        w = Work(); Assert.True(AmigaSmartFileSystem2WriteCore.Resize(ref disk, ref w, node, 513), w.Error.ToString());
        Assert.True(disk.Flush()); Sfs2WriteTests.Inspect(disk.Snapshot());
        w = Work(); Assert.True(AmigaSmartFileSystem2WriteCore.Remove(ref disk, ref w, node), w.Error.ToString());
        Assert.True(disk.Flush()); Sfs2WriteTests.Inspect(disk.Snapshot());
    }
}
