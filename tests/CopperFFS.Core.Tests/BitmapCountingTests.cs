using CopperStart.Dos;
using CopperSFS;

namespace CopperStart.FileSystem.Tests;

public sealed partial class VolumeGeometryTests
{
    [Theory]
    [InlineData(512u)] [InlineData(1024u)] [InlineData(4096u)]
    public void BitmapCountingMatchesIndividualBitsIncludingPartialWords(uint bytes)
    {
        var storage = new Storage();
        Assert.True(AmigaVolumeGeometry.Create(10003, bytes, 2, out var geometry));
        var p = new Memory(storage, geometry); Format(ref p, geometry);
        var random = new Random(7921); var expected = geometry.ReservedBlocks;
        var key = geometry.ReservedBlocks;
        for (var index = 0u; key < geometry.BlockCount; index++)
        {
            Assert.True(AmigaDiskFileSystemCore.BitmapBlock(ref p, index, Meta, out var map));
            p.Clear(Bitmap, bytes);
            for (var at = 4; at < bytes; at += 4)
            {
                // Exercise entire free/occupied groups and mixed populations.
                var word = at < 100 ? uint.MaxValue : at < 200 ? 0u : (uint)random.NextInt64(0, 1L << 32);
                p.WriteUInt32(Bitmap, at, word);
                for (var bit = 0; bit < 32 && key < geometry.BlockCount; bit++, key++)
                    if ((word & (1u << bit)) == 0) expected++;
            }
            Checksum(ref p, Bitmap, bytes, 0); Assert.True(p.WriteBlock(map, Bitmap));
        }
        Assert.True(AmigaDiskFileSystemCore.CountUsed(ref p, Meta, Bitmap, out var actual));
        Assert.Equal(expected, actual);

        // Changing unused padding leaves the population unchanged, but must
        // invalidate the bitmap checksum for both space queries and writers.
        Assert.True(AmigaDiskFileSystemCore.BitmapBlock(ref p, (geometry.BlockCount - geometry.ReservedBlocks - 1) /
            geometry.BitmapBits, Meta, out var lastMap));
        Assert.True(p.ReadBlock(lastMap, Bitmap));
        p.WriteUInt32(Bitmap, (int)bytes - 4, p.ReadUInt32(Bitmap, (int)bytes - 4) ^ 0x80000000u);
        Assert.True(p.WriteBlock(lastMap, Bitmap));
        Assert.False(AmigaDiskFileSystemCore.CountUsed(ref p, Meta, Bitmap, out _));
    }
}
