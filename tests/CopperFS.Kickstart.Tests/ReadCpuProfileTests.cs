using System.Buffers.Binary;

namespace CopperFS.Kickstart.Tests;

public sealed class ReadCpuProfileTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CodeLocationSupportsCopperAndOriginalFfsEmptySecondaryHunk(bool secondary)
    {
        var (code, mask) = ReadCpuProfile.FirstCodeImage(Image(secondary, 0, 4));
        Assert.Equal(8200, code.Length);
        Assert.Equal(8192, mask.Length);
        Assert.All(mask.Skip(4).Take(4), bit => Assert.False(bit));
        Assert.True(mask[3]); Assert.True(mask[8]);
    }

    [Fact]
    public void RelocationStraddlingTheSearchPrefixMasksItsVisibleBytes()
    {
        var (_, mask) = ReadCpuProfile.FirstCodeImage(Image(false, 0, 8190));
        Assert.True(mask[8189]); Assert.False(mask[8190]); Assert.False(mask[8191]);
    }

    [Fact]
    public void NonemptySecondaryHunkCannotBeSilentlyOmittedFromAttribution()
        => Assert.ThrowsAny<Exception>(() => ReadCpuProfile.FirstCodeImage(Image(true, 1, 4)));

    [Fact]
    public void InvalidRelocationCannotBeUsedToLocateUnrelatedCode()
        => Assert.ThrowsAny<Exception>(() => ReadCpuProfile.FirstCodeImage(Image(false, 0, 8198)));

    private static byte[] Image(bool secondary, uint secondaryWords, uint relocation)
    {
        using var stream = new MemoryStream();
        void Word(uint n) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, n); stream.Write(bytes); }
        Word(1011); Word(0); Word(secondary ? 2u : 1u); Word(0); Word(secondary ? 1u : 0u);
        Word(2050); if (secondary) Word(secondaryWords);
        Word(1001); Word(2050); stream.Write(new byte[8200]);
        Word(1004); Word(1); Word(0); Word(relocation); Word(0); Word(1010);
        if (secondary) { Word(1002); Word(secondaryWords); Word(1010); }
        return stream.ToArray();
    }
}
