using CopperStart.Dos;

namespace CopperStart.FileSystem.Tests;

public sealed class FilePositionTests
{
    [Theory]
    [InlineData(0u, 0x80000000u)]
    [InlineData(0x80000000u, 0x80000001u)]
    [InlineData(0x80000001u, 0xFFFFFFFEu)]
    [InlineData(0xFFFFFFFEu, 0xFFFFFFFFu)]
    public void AbsoluteQuadPositionsPreserveTheUnsignedFileSize(uint current, uint target)
    {
        Assert.True(AmigaFilePositionCore.Seek(current, uint.MaxValue, 0, target, -1, out var next));
        Assert.Equal(target, next);
    }

    [Theory]
    [InlineData(0xFFFFFFFFu, 0x80000000u, 0x7FFFFFFFu)]
    [InlineData(0xFFFFFFFFu, 1u, 0u)]
    [InlineData(0x80000000u, 0x80000000u, 0u)]
    public void NegativeQuadOffsetsCanRewindAcrossTwoGiB(uint current, uint deltaLow, uint expected)
    {
        Assert.True(AmigaFilePositionCore.Seek(current, uint.MaxValue, uint.MaxValue, deltaLow, 0, out var next));
        Assert.Equal(expected, next);
    }

    [Theory]
    [InlineData(1u, 0u, -1)] // 4 GiB is outside the on-disk file-size field.
    [InlineData(0xFFFFFFFFu, 0u, 1)] // minus 4 GiB before EOF.
    [InlineData(0x7FFFFFFFu, 0xFFFFFFFFu, 0)]
    [InlineData(0x80000000u, 0u, 0)]
    [InlineData(0u, 1u, 1)] // beyond EOF.
    [InlineData(0u, 0u, 2)]
    public void OutOfRangeSeekPreservesPosition(uint high, uint low, int mode)
    {
        Assert.False(AmigaFilePositionCore.Seek(17, uint.MaxValue, high, low, mode, out var next));
        Assert.Equal(17u, next);
    }
}
