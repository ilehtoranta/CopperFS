using System.Reflection;

namespace CopperCPX.Tests;

public class BitmapWordRangeTests
{
    [Theory][InlineData(2048, false)][InlineData(2048, true)][InlineData(4096, false)][InlineData(4096, true)]
    public void WordEditsMatchIndependentBitOracleAndPreserveEveryOutsideByte(int bits, bool used)
    {
        var matches = typeof(CpxEngine<Ram>).GetMethod("BitmapRangeMatches", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var change = typeof(CpxEngine<Ram>).GetMethod("SetBitmapRange", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.NotNull(matches); Assert.NotNull(change);
        var random = new Random(71839);
        for (int trial = 0; trial < 128; trial++)
        {
            int first = trial < 32 ? trial : random.Next(bits + 1);
            int count = trial < 32 ? Math.Min(bits - first, trial * 33) : random.Next(bits - first + 1);
            byte[] original = new byte[bits / 8 + 128]; random.NextBytes(original);
            var ram = new Ram((byte[])original.Clone()); object engine = new CpxEngine<Ram>(ram, 32);
            bool All(bool value) => Enumerable.Range(first, count).All(n => ((ram.Bytes[64 + n / 8] & (0x80 >> (n % 8))) != 0) == value);
            Assert.Equal(All(used), (bool)matches.Invoke(engine, [0u, (uint)first, (uint)count, used])!);
            byte[] expected = (byte[])original.Clone();
            for (int n = first; n < first + count; n++)
            {
                int at = 64 + n / 8, mask = 0x80 >> (n % 8);
                expected[at] = (byte)(used ? expected[at] | mask : expected[at] & ~mask);
            }
            change.Invoke(engine, [0u, (uint)first, (uint)count, used]);
            Assert.Equal(expected, ram.Bytes);
            Assert.True((bool)matches.Invoke(engine, [0u, (uint)first, (uint)count, used])!);
            Assert.Equal(count == 0, (bool)matches.Invoke(engine, [0u, (uint)first, (uint)count, !used])!);
        }
    }
}
