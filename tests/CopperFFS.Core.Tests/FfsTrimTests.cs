using Amiga;
using CopperSFS;
using CopperStart.Dos;

namespace CopperStart.FileSystem.Tests;

public sealed partial class VolumeGeometryTests
{
    private static void SetDosType(ref Memory p, uint type)
    { Assert.True(p.ReadBlock(0, Meta)); p.WriteUInt32(Meta, 0, type); Assert.True(p.WriteBlock(0, Meta)); }

    private sealed partial class Storage
    {
        public bool Protected, FailFlush, DiscardUnsupported;
        public int Flushes, Discarded, FailDiscardAt;
        public AmigaFileSystemDiscardLimits Limits = new() { MaximumBlocks = 128, Granularity = 1 };
        public readonly List<(uint Block, uint Count)> Discards = [];
    }

    private partial struct Memory
    {
        public bool QueryDiscard(APTR control, out AmigaFileSystemDiscardLimits limits)
        { limits = storage.Limits; return !storage.DiscardUnsupported; }
        public bool DiscardBlocks(APTR control, uint block, uint count)
        {
            Assert.True(storage.Flushes > 0 && !storage.FailFlush);
            Assert.True(block >= geometry.ReservedBlocks && count <= geometry.BlockCount - block);
            storage.Discards.Add((block, count));
            for (var key = block; key < block + count; key++)
            {
                // Read actual persisted allocation independently of the scan's scratch.
                var index = (key - geometry.ReservedBlocks) / geometry.BitmapBits;
                var bit = (key - geometry.ReservedBlocks) % geometry.BitmapBits;
                var address = geometry.StartByte + (geometry.RootBlock + 1 + index) * (ulong)geometry.BlockBytes;
                var word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
                    storage.Sectors[address].AsSpan(4 + (int)(bit / 32) * 4));
                Assert.NotEqual(0u, word & 1u << (int)(bit % 32));
                for (var at = 0u; at < geometry.BlockBytes; at += geometry.SectorBytes)
                    storage.Sectors[geometry.StartByte + key * (ulong)geometry.BlockBytes + at] =
                        Enumerable.Repeat((byte)0xD7, (int)geometry.SectorBytes).ToArray();
                if (++storage.Discarded == storage.FailDiscardAt) return false;
            }
            return true;
        }
    }

    [Theory]
    [InlineData(512u)] [InlineData(1024u)] [InlineData(4096u)]
    public void FfsTrimQueryDryRunAndExecutionPreserveAllocatedBytesAtWideOffsets(uint bytes)
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(9003, bytes, 2, out var g));
        g.StartByte = 5UL * 1024 * 1024 * 1024 + 512;
        var p = new Memory(storage, g); Format(ref p, g);
        System.Text.Encoding.ASCII.GetBytes("Keep").CopyTo(storage.Ram, (int)Name.Raw);
        var w = Work();
        var keep = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 4 };
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref keep, false));
        for (var i = 0; i < 1001; i++) storage.Ram[(int)Payload.Raw + i] = (byte)(i * 17 + 9);
        keep.Buffer = Payload; keep.Length = 1001;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref keep, out var written)); Assert.Equal(1001u, written);
        var before = storage.Sectors.ToDictionary(x => x.Key, x => x.Value.ToArray());
        Assert.True(AmigaFfsWriteCore.Trim(ref p, ref w, APTR.Null, 2, out _));
        Assert.Equal(0, storage.Flushes);
        Assert.True(AmigaFfsWriteCore.Trim(ref p, ref w, APTR.Null, 1, out var dry));
        Assert.Empty(storage.Discards);
        Assert.True(AmigaFfsWriteCore.Trim(ref p, ref w, APTR.Null, 0, out var real), w.Error.ToString());
        Assert.Equal(dry.Blocks, real.Blocks); Assert.Equal(dry.Ranges, real.Ranges);
        Assert.Equal(g.BlockCount - Used(ref p), real.Blocks);
        foreach (var sector in before) Assert.Equal(sector.Value, storage.Sectors[sector.Key]);
    }

    [Theory]
    [InlineData(110000u, false)] [InlineData(110000u, true)] [InlineData(630000u, false)] [InlineData(630000u, true)]
    public void FfsTrimDryRunWalksEveryValidBitmapExtension(uint blocks, bool ofs)
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(blocks, 512, 2, out var g));
        var p = new Memory(storage, g); Format(ref p, g); if (ofs) SetDosType(ref p, 0x444F5300); var w = Work();
        Assert.True(AmigaFfsWriteCore.Trim(ref p, ref w, APTR.Null, 1, out var result), w.Error.ToString());
        Assert.Equal(g.BlockCount - Used(ref p), result.Blocks); Assert.Empty(storage.Discards);
    }

    [Theory]
    [InlineData(8u, 3u, 31u, false)] [InlineData(8u, 3u, 31u, true)] [InlineData(128u, 127u, 512u, false)] [InlineData(128u, 127u, 512u, true)]
    public void FfsTrimHonoursDeviceRangeAlignmentWithoutTouchingNeighbours(uint granularity, uint alignment, uint maximum, bool ofs)
    {
        var storage = new Storage { Limits = new() { Granularity = granularity, Alignment = alignment, MaximumBlocks = maximum } };
        Assert.True(AmigaVolumeGeometry.Create(3520, 512, 2, out var g));
        var p = new Memory(storage, g); Format(ref p, g); if (ofs) SetDosType(ref p, 0x444F5300); var w = Work();
        Assert.True(AmigaFfsWriteCore.Trim(ref p, ref w, APTR.Null, 0, out _));
        foreach (var range in storage.Discards)
        {
            Assert.Equal(alignment, range.Block % granularity);
            Assert.Equal(0u, range.Count % granularity); Assert.InRange(range.Count, 1u, maximum);
            Assert.True(range.Block + range.Count <= g.BlockCount);
        }
    }

    [Theory]
    [InlineData(0, false)] [InlineData(0, true)] [InlineData(1, false)] [InlineData(1, true)] [InlineData(2, false)] [InlineData(2, true)] [InlineData(3, false)] [InlineData(3, true)]
    [InlineData(4, false)] [InlineData(4, true)] [InlineData(5, false)] [InlineData(5, true)] [InlineData(6, false)] [InlineData(6, true)] [InlineData(7, false)] [InlineData(7, true)]
    [InlineData(8, false)] [InlineData(8, true)] [InlineData(9, false)] [InlineData(9, true)]
    public void FfsTrimRejectsAllInvalidAllocationBeforeFirstDiscard(int damage, bool ofs)
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(110000, 512, 2, out var g));
        var p = new Memory(storage, g); Format(ref p, g); if (ofs) SetDosType(ref p, 0x444F5300);
        var maps = (g.BlockCount - 3) / g.BitmapBits + 1;
        var ext = g.RootBlock + 1 + maps;
        if (damage <= 2 || damage == 9)
        {
            var key = damage == 0 ? g.RootBlock : damage == 1 ? g.RootBlock + 1 : ext;
            var index = damage == 9 ? maps - 1 : (key - 2) / g.BitmapBits;
            Assert.True(AmigaDiskFileSystemCore.BitmapBlock(ref p, index, Meta, out var map));
            Assert.True(p.ReadBlock(map, Bitmap));
            if (damage == 9) p.WriteUInt32(Bitmap, 0, p.ReadUInt32(Bitmap) ^ 1);
            else
            {
                var bit = (key - 2) % g.BitmapBits; var at = 4 + (int)(bit / 32) * 4;
                p.WriteUInt32(Bitmap, at, p.ReadUInt32(Bitmap, at) | 1u << (int)(bit % 32));
                Checksum(ref p, Bitmap, 512, 0);
            }
            Assert.True(p.WriteBlock(map, Bitmap));
        }
        else if (damage is 6 or 7)
        {
            Assert.True(p.ReadBlock(ext, Meta));
            p.WriteUInt32(Meta, damage == 6 ? 508 : 0, damage == 6 ? ext : g.BlockCount);
            Assert.True(p.WriteBlock(ext, Meta));
        }
        else
        {
            Assert.True(p.ReadBlock(g.RootBlock, Meta));
            if (damage == 3) p.WriteUInt32(Meta, 312, 0); // Unvalidated volume.
            if (damage == 4) p.WriteUInt32(Meta, 320, g.RootBlock + 1); // Aliased map.
            if (damage == 5) p.WriteUInt32(Meta, 416, 0); // Truncated extension chain.
            Checksum(ref p, Meta, 512, 20);
            if (damage == 8) p.WriteUInt32(Meta, 20, p.ReadUInt32(Meta, 20) ^ 1);
            Assert.True(p.WriteBlock(g.RootBlock, Meta));
        }
        var w = Work();
        Assert.False(AmigaFfsWriteCore.Trim(ref p, ref w, APTR.Null, 0, out _));
        Assert.Equal(DOS.Error.DiskNotValidated, w.Error); Assert.Empty(storage.Discards);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(128)] [InlineData(129)] [InlineData(258)]
    public void FfsTrimPartialFailuresExcludeTheFailedRequestAndRetryFreshAllocation(int fail)
    {
        var storage = new Storage { FailDiscardAt = fail };
        Assert.True(AmigaVolumeGeometry.Create(3520, 512, 2, out var g));
        var p = new Memory(storage, g); Format(ref p, g); var w = Work();
        Assert.False(AmigaFfsWriteCore.Trim(ref p, ref w, APTR.Null, 0, out var result));
        Assert.Equal(DOS.Error.SeekError, w.Error);
        Assert.Equal((uint)(storage.Discards.Count - 1), result.Ranges);
        Assert.Equal((uint)storage.Discards.SkipLast(1).Sum(x => (long)x.Count), result.Blocks);
        // Allocation between calls must be respected by the retry's fresh scan.
        System.Text.Encoding.ASCII.GetBytes("NewFile").CopyTo(storage.Ram, (int)Name.Raw);
        var request = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 7 };
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref request, false));
        var saved = storage.Sectors[g.StartByte + request.Header * 512UL].ToArray();
        storage.FailDiscardAt = 0; storage.Discards.Clear();
        Assert.True(AmigaFfsWriteCore.Trim(ref p, ref w, APTR.Null, 0, out _));
        Assert.Equal(saved, storage.Sectors[g.StartByte + request.Header * 512UL]);
        Assert.DoesNotContain(storage.Discards, r => request.Header >= r.Block && request.Header - r.Block < r.Count);
    }

    [Theory]
    [InlineData(0, false)] [InlineData(0, true)] [InlineData(1, false)] [InlineData(1, true)] [InlineData(2, false)] [InlineData(2, true)] [InlineData(3, false)] [InlineData(3, true)] [InlineData(4, false)] [InlineData(4, true)]
    public void FfsTrimRefusesFailedBarrierReadOnlyUnsupportedDeviceAndInvalidLimits(int failure, bool ofs)
    {
        var storage = new Storage { FailFlush = failure == 0, Protected = failure == 1, DiscardUnsupported = failure == 2 };
        Assert.True(AmigaVolumeGeometry.Create(3520, 512, 2, out var g));
        var p = new Memory(storage, g); Format(ref p, g); if (ofs) SetDosType(ref p, 0x444F5300); var w = Work();
        if (failure == 3) storage.Limits.Granularity = 0;
        if (failure == 4) { p.WriteUInt32(Meta, 0, 0x444F5302); Assert.True(p.WriteBlock(0, Meta)); }
        Assert.False(AmigaFfsWriteCore.Trim(ref p, ref w, APTR.Null, 0, out _));
        Assert.Empty(storage.Discards);
    }
}
