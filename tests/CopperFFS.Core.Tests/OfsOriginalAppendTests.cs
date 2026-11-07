using Amiga;
using CopperSFS;
using CopperStart.Dos;

namespace CopperStart.FileSystem.Tests;

public sealed partial class VolumeGeometryTests
{
    [Theory]
    [InlineData(512u, 3u)] [InlineData(512u, 72u)] [InlineData(1024u, 200u)]
    public void ReadsGrowsAndReleasesOriginalHandlerAppendRuns(uint bytes, uint split)
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(3520, bytes, 2, out var g));
        var p = new Memory(storage, g); Format(ref p, g); SetDosType(ref p, 0x444F5300);
        var baseline = Used(ref p); var r = OfsFile(ref p, storage, g, "Legacy");
        var payload = bytes - 24;
        var contents = Enumerable.Range(0, (int)((split + 4) * payload + 17)).Select(i => (byte)(i * 31 + (i >> 8))).ToArray();
        OfsWrite(ref p, storage, ref r, 0, contents);
        uint Key(uint index)
        {
            var table = r.Header; Assert.True(p.ReadBlock(table, Meta));
            while (index >= g.TableEntries)
            { table = p.ReadUInt32(Meta, g.Tail + 504); Assert.True(p.ReadBlock(table, Meta)); index -= g.TableEntries; }
            return p.ReadUInt32(Meta, g.Tail + 308 - (int)index * 4);
        }
        // Reproduce independently observed FFS 40.1 bytes: previous EOF has
        // no forward link and new allocation starts its own sequence at one.
        var previous = Key(split - 1); Assert.True(p.ReadBlock(previous, Data));
        p.WriteUInt32(Data, 16, 0); Checksum(ref p, Data, bytes, 20); Assert.True(p.WriteBlock(previous, Data));
        for (var index = split; index <= split + 4; index++)
        {
            var key = Key(index); Assert.True(p.ReadBlock(key, Data)); p.WriteUInt32(Data, 8, index - split + 1);
            Checksum(ref p, Data, bytes, 20); Assert.True(p.WriteBlock(key, Data));
        }
        foreach (var position in new[] { 0u, split * payload, (split + 2) * payload + 13 })
        {
            var count = (uint)contents.Length - position;
            Assert.Equal((int)count, AmigaDiskFileSystemCore.Read(ref p, r.Header, 0x444F5300, position,
                Output, count, Meta, Data, out var error));
            Assert.Equal(DOS.Error.None, error);
            Assert.Equal(contents.AsSpan((int)position).ToArray(), storage.Ram.AsSpan((int)Output.Raw, (int)count).ToArray());
        }
        OfsWrite(ref p, storage, ref r, (split + 1) * payload + 5, [9, 8, 7]);
        OfsWrite(ref p, storage, ref r, (uint)contents.Length, [6, 5, 4]);
        new byte[] { 9, 8, 7 }.CopyTo(contents, (split + 1) * payload + 5);
        var grown = contents.Concat(new byte[] { 6, 5, 4 }).ToArray();
        Assert.Equal(grown.Length, AmigaDiskFileSystemCore.Read(ref p, r.Header, 0x444F5300, 0,
            Output, (uint)grown.Length, Meta, Data, out var grownError));
        Assert.Equal(DOS.Error.None, grownError);
        Assert.Equal(grown, storage.Ram.AsSpan((int)Output.Raw, grown.Length).ToArray());
        var w = Work(); Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, r.Header, false), w.Error.ToString());
        Assert.Equal(baseline, Used(ref p));
    }

    [Fact]
    public void OriginalAppendRecognitionDoesNotAllowReusingAnEarlierBlock()
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(3520, 512, 2, out var g));
        var p = new Memory(storage, g); Format(ref p, g); SetDosType(ref p, 0x444F5300);
        var r = OfsFile(ref p, storage, g, "Alias"); OfsWrite(ref p, storage, ref r, 0, new byte[3 * 488]);
        Assert.True(p.ReadBlock(r.Header, Meta)); var first = p.ReadUInt32(Meta, 308); var second = p.ReadUInt32(Meta, 304);
        p.WriteUInt32(Meta, 300, first); Checksum(ref p, Meta, 512, 20); Assert.True(p.WriteBlock(r.Header, Meta));
        Assert.True(p.ReadBlock(second, Data)); p.WriteUInt32(Data, 16, 0); Checksum(ref p, Data, 512, 20);
        Assert.True(p.WriteBlock(second, Data)); storage.Transfers.Clear();
        var w = Work(); r.Buffer = Payload; r.Position = 3 * 488; r.Length = 1;
        Assert.False(AmigaFfsWriteCore.Write(ref p, ref w, ref r, out _));
        Assert.Equal(DOS.Error.DiskNotValidated, w.Error);
        Assert.False(AmigaFfsWriteCore.Remove(ref p, ref w, r.Header, false));
        Assert.DoesNotContain(storage.Transfers, t => t.Command is 3 or 25);
    }
}
