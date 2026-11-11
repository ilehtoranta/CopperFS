using CopperSFS;
using Amiga;
using CopperStart.Dos;

namespace CopperStart.FileSystem.Tests;

public sealed partial class VolumeGeometryTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper output;
    public VolumeGeometryTests(Xunit.Abstractions.ITestOutputHelper output) => this.output = output;

    // Independent, fully allocated file layout. Payload blocks remain sparse
    // until touched; metadata is ordinary FFS, not a special sparse-file format.
    private static void FileChain(ref Memory p, AmigaVolumeGeometry g, uint blocks)
    {
        for (var first = 0u; first < blocks; first += g.TableEntries)
        {
            var table = 10 + first / g.TableEntries;
            var count = Math.Min(g.TableEntries, blocks - first);
            p.Clear(Meta, g.BlockBytes);
            p.WriteUInt32(Meta, 0, first == 0 ? 2u : 16u);
            p.WriteUInt32(Meta, 4, table); p.WriteUInt32(Meta, 8, count);
            p.WriteUInt32(Meta, g.Tail + 508, 0xFFFFFFFD);
            p.WriteUInt32(Meta, g.Tail + 500, first == 0 ? g.RootBlock : 10);
            if (first == 0) p.WriteUInt32(Meta, g.Tail + 324, blocks * g.BlockBytes);
            if (first + count < blocks) p.WriteUInt32(Meta, g.Tail + 504, table + 1);
            for (var i = 0u; i < count; i++) p.WriteUInt32(Meta, g.Tail + 308 - (int)i * 4, 1000 + first + i);
            Checksum(ref p, Meta, g.BlockBytes, 20); Assert.True(p.WriteBlock(table, Meta));
        }
    }

    [Theory]
    [InlineData(512u)]
    [InlineData(1024u)]
    [InlineData(4096u)]
    public void ShortOverwriteWorkDependsOnTheTouchedPathNotFileOrVolumeLength(uint bytes)
    {
        static (int Transfers, long Reads) Overwrite(uint bytes, uint volume, uint blocks, uint position)
        {
            var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(volume, bytes, 2, out var g));
            var p = new Memory(storage, g); Format(ref p, g, volume); FileChain(ref p, g, blocks);
            var before = Used(ref p); var work = Work();
            storage.Ram[(int)Payload.Raw] = 0xA5; storage.Ram[(int)Payload.Raw + 1] = 0x5A;
            var r = new AmigaFfsWriteCore.Request { Header = 10, Position = position, Buffer = Payload, Length = 2 };
            storage.Transfers.Clear(); storage.LongReads = 0;
            Assert.True(AmigaFfsWriteCore.Write(ref p, ref work, ref r, out var actual), work.Error.ToString());
            Assert.Equal(2u, actual);
            var result = (storage.Transfers.Count, storage.LongReads);
            // No allocation bitmap is written, and every data block outside the
            // requested range stays absent from this initially sparse storage.
            Assert.DoesNotContain(storage.Transfers, t => t.Command is 3 or 25 &&
                t.Offset >= (ulong)(g.RootBlock + 1) * bytes);
            Assert.Equal(before, Used(ref p));
            Assert.Equal(4, AmigaDiskFileSystemCore.Read(ref p, 10, 0x444F5301, position - 1, Output, 4, Meta, Data, out var error));
            Assert.Equal(DOS.Error.None, error);
            Assert.Equal(new byte[] { 0, 0xA5, 0x5A, 0 }, storage.Ram.AsSpan((int)Output.Raw, 4).ToArray());
            return result;
        }
        var small = Overwrite(bytes, 10000, 2, 3);
        var large = Overwrite(bytes, 8650752, 20000, 3);
        output.WriteLine($"{bytes}-byte blocks: small overwrite {small}; large overwrite {large} (block transfers, longword reads).");
        // The large volume needs a bounded bitmap-extension lookup for the root,
        // but must not visit thousands of bitmap pages or the file's suffix.
        Assert.InRange(large.Transfers, 1, small.Transfers + 20);
        Assert.InRange(large.Reads, 1, small.Reads + 5000);
        var acrossTable = Overwrite(bytes, 8650752, 20000, (bytes / 4 - 56) * bytes - 1);
        output.WriteLine($"Across pointer tables: {acrossTable}.");
        Assert.InRange(acrossTable.Transfers, 1, large.Transfers + 10);
    }

    [Theory]
    [InlineData(0)] // target table checksum
    [InlineData(1)] // target extension owner
    [InlineData(2)] // target data pointer outside volume
    [InlineData(3)] // target data marked free
    [InlineData(4)] // target bitmap checksum
    [InlineData(5)] // missing extension
    [InlineData(6)] // short preceding pointer table
    [InlineData(7)] // file header marked free
    [InlineData(8)] // data pointer references the volume root
    public void OverwritePreflightRejectsDamageOnItsPathBeforeAnyWrite(int damage)
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(12000, 512, 2, out var g));
        var p = new Memory(storage, g); Format(ref p, g, g.BlockCount); FileChain(ref p, g, 200);
        if (damage is 3 or 4 or 7)
        {
            var key = damage == 7 ? 10u : 1072u;
            Assert.True(AmigaDiskFileSystemCore.BitmapBlock(ref p, (key - 2) / g.BitmapBits, Meta, out var map));
            Assert.True(p.ReadBlock(map, Bitmap));
            var bit = (key - 2) % g.BitmapBits;
            var at = 4 + (int)(bit / 32) * 4;
            p.WriteUInt32(Bitmap, at, p.ReadUInt32(Bitmap, at) | 1u << (int)(bit % 32));
            if (damage != 4) Checksum(ref p, Bitmap, g.BlockBytes, 0);
            Assert.True(p.WriteBlock(map, Bitmap));
        }
        else
        {
            var table = damage is 5 or 6 ? 10u : 11u;
            Assert.True(p.ReadBlock(table, Meta));
            if (damage == 0) p.WriteUInt32(Meta, 20, 1);
            if (damage == 1) p.WriteUInt32(Meta, 500, 123);
            if (damage == 2) p.WriteUInt32(Meta, 308, g.BlockCount);
            if (damage == 8) p.WriteUInt32(Meta, 308, g.RootBlock);
            if (damage == 5) p.WriteUInt32(Meta, 504, 0);
            if (damage == 6) p.WriteUInt32(Meta, 8, 71);
            if (damage != 0) Checksum(ref p, Meta, g.BlockBytes, 20);
            Assert.True(p.WriteBlock(table, Meta));
        }
        // Cross the table boundary, so rejecting the second block must happen
        // before changing even the valid byte at the end of the first table.
        var r = new AmigaFfsWriteCore.Request { Header = 10, Position = 72 * 512 - 1, Buffer = Payload, Length = 2 };
        var work = Work(); storage.Transfers.Clear();
        Assert.False(AmigaFfsWriteCore.Write(ref p, ref work, ref r, out var actual));
        Assert.Equal(0u, actual); Assert.Equal(DOS.Error.DiskNotValidated, work.Error);
        Assert.DoesNotContain(storage.Transfers, t => t.Command is 3 or 25);
    }

    [Fact]
    public void OverwriteDoesNotVisitTheSuffixButGrowthAndTruncationStillValidateIt()
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(12000, 512, 2, out var g));
        var p = new Memory(storage, g); Format(ref p, g, g.BlockCount); FileChain(ref p, g, 200);
        // An unreadable suffix must not turn a short valid overwrite into a
        // whole-file scan. Operations that alter allocation must still reject it.
        storage.Sectors.Remove(12 * 512UL);
        var r = new AmigaFfsWriteCore.Request { Header = 10, Position = 1, Buffer = Payload, Length = 1 };
        var work = Work(); storage.Transfers.Clear();
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref work, ref r, out _), work.Error.ToString());
        Assert.DoesNotContain(storage.Transfers, t => t.Offset == 12 * 512UL);
        r.Position = 200 * 512;
        storage.Transfers.Clear();
        Assert.False(AmigaFfsWriteCore.Write(ref p, ref work, ref r, out _));
        Assert.Equal(DOS.Error.DiskNotValidated, work.Error);
        Assert.DoesNotContain(storage.Transfers, t => t.Command is 3 or 25);
        Assert.False(AmigaFfsWriteCore.Remove(ref p, ref work, 10, true));
        Assert.Equal(DOS.Error.DiskNotValidated, work.Error);
        Assert.DoesNotContain(storage.Transfers, t => t.Command is 3 or 25);
    }

    [Fact]
    public void FailedOverwriteKeepsItsWrittenPrefixAndLeavesRootInvalid()
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(12000, 512, 2, out var g));
        var p = new Memory(storage, g); Format(ref p, g, g.BlockCount); FileChain(ref p, g, 200);
        var work = Work(); var r = new AmigaFfsWriteCore.Request { Header = 10, Position = 511, Buffer = Payload, Length = 2 };
        storage.FailWrite = 1001 * 512UL; storage.Ram[(int)Payload.Raw] = 0x5A;
        Assert.False(AmigaFfsWriteCore.Write(ref p, ref work, ref r, out var actual));
        Assert.Equal(1u, actual); Assert.Equal(DOS.Error.DiskNotValidated, work.Error);
        Assert.Equal(0x5A, storage.Sectors[1000 * 512UL][511]);
        Assert.True(AmigaDiskFileSystemCore.Header(ref p, g.RootBlock, Meta)); Assert.Equal(0u, p.ReadUInt32(Meta, 312));
        storage.FailWrite = ulong.MaxValue; storage.Transfers.Clear();
        Assert.False(AmigaFfsWriteCore.Write(ref p, ref work, ref r, out _));
        Assert.DoesNotContain(storage.Transfers, t => t.Command is 3 or 25);
    }
}
