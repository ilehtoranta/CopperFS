namespace CopperCPX.Tests;

public class ShadowMapGeometryTests
{
    [Theory]
    [InlineData(512, 512, 4ul * 1024 * 1024)]
    [InlineData(512, 1024, 16ul * 1024 * 1024)]
    [InlineData(512, 1024, 4ul * 1024 * 1024 * 1024)]
    [InlineData(512, 1024, 1024ul * 1024 * 1024 * 1024)]
    [InlineData(4096, 4096, 16ul * 1024 * 1024 * 1024 * 1024)]
    public void RadixGeometryCoversWidePartitionsWithBoundedDepth(uint sector, uint page, ulong bytes)
    {
        Assert.Equal(CpxError.None, CpxShadowMapGeometry.TryCreate(sector, page, bytes / sector, Wide.From(512), out var geometry));
        Assert.InRange(geometry.Height, 1u, 8u);
        Assert.Equal(bytes / (2 * page), geometry.PairCount);
        ulong previousEnd = geometry.MapBaseSector;
        for (uint level = 0; level < geometry.Height; level++)
        {
            ulong count = geometry.NodeCount(level);
            Assert.NotEqual(0ul, count);
            Assert.Equal(previousEnd, (ulong)geometry.NodePairSector(level, 0));
            Assert.InRange(geometry.Entries(level, count - 1), 1u, level == 0 ? geometry.LeafEntries : geometry.BranchEntries);
            previousEnd += count * (2 * page / sector);
            Assert.True(geometry.NodePairSector(level, count).IsZero);
        }
        Assert.Equal(geometry.MapEndSector, previousEnd);
        Assert.Equal(1ul, geometry.NodeCount(geometry.Height - 1));
        ulong last = ((bytes / sector) >> (int)geometry.PairShift) << (int)geometry.PairShift;
        Assert.False(geometry.ContainsPair(last));
        Assert.True(geometry.ContainsPair(last - (2 * page / sector)));
        Assert.False(geometry.ContainsPair(last - 1));
    }

    [Fact]
    public void InvalidCapacityAlignmentAndUnrepresentableByteRangesAreRejected()
    {
        Assert.Equal(CpxError.InvalidArgument, CpxShadowMapGeometry.TryCreate(512, 1024, Wide.From(8192), Wide.From(513), out _));
        Assert.Equal(CpxError.InvalidArgument, CpxShadowMapGeometry.TryCreate(512, 1024, ulong.MaxValue, Wide.From(512), out _));
        Assert.Equal(CpxError.InvalidArgument, CpxShadowMapGeometry.TryCreate(512, 1024, Wide.From(1023), Wide.From(1020), out _));
    }
}
