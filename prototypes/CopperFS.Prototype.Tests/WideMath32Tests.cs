using System.Numerics;
using CopperFS.Prototype;

namespace CopperFS.Prototype.Tests;

public class WideMath32Tests
{
    [Fact] public void PairOperationsMatchIndependentBigIntegerOracle()
    {
        var random = new Random(68000); BigInteger limit = (BigInteger.One << 64) - 1;
        ulong[] edges = [0, 1, uint.MaxValue, 1UL << 32, (1UL << 32) + 1, 1UL << 63, ulong.MaxValue];
        for (int n = 0; n < 10000; n++)
        {
            ulong a = n < edges.Length * edges.Length ? edges[n / edges.Length] : ((ulong)(uint)random.NextInt64() << 32) | (uint)random.NextInt64();
            ulong b = n < edges.Length * edges.Length ? edges[n % edges.Length] : ((ulong)(uint)random.NextInt64() << 32) | (uint)random.NextInt64();
            BigInteger aa = a, bb = b;
            uint divisor = (uint)b | 1;
            Assert.True(WideMath32.Remainder((uint)(a >> 32), (uint)a, divisor, out uint rem));
            Assert.Equal((uint)(aa % divisor), rem);
            bool ok = WideMath32.Add((uint)(a >> 32), (uint)a, (uint)(b >> 32), (uint)b, out uint h, out uint l);
            Assert.Equal(aa + bb <= limit, ok); Assert.Equal((ulong)((aa + bb) & limit), AddressMath.Join(h, l));
            ok = WideMath32.Subtract((uint)(a >> 32), (uint)a, (uint)(b >> 32), (uint)b, out h, out l);
            Assert.Equal(a >= b, ok); Assert.Equal((ulong)((aa - bb) & limit), AddressMath.Join(h, l));
            Assert.Equal(a.CompareTo(b), WideMath32.Compare((uint)(a >> 32), (uint)a, (uint)(b >> 32), (uint)b));
            for (int shift = 0; shift <= 64; shift++)
            {
                WideMath32.ShiftRight((uint)(a >> 32), (uint)a, shift, out h, out l); Assert.Equal((ulong)(aa >> shift), AddressMath.Join(h, l));
                ok = WideMath32.ShiftLeft((uint)(a >> 32), (uint)a, shift, out h, out l);
                Assert.Equal(shift < 64 && (aa << shift) <= limit, ok);
                if (shift < 64) Assert.Equal((ulong)((aa << shift) & limit), AddressMath.Join(h, l));
            }
            for (int shift = 9; shift <= 12; shift++)
            {
                Assert.True(WideMath32.RoundUpUnits((uint)(a >> 32), (uint)a, shift, out h, out l));
                Assert.Equal((ulong)((aa + (1 << shift) - 1) >> shift), AddressMath.Join(h, l));
            }
        }
    }
    [Fact] public void PartitionCarryAndBothEndsOfDeviceRangeAreChecked()
    {
        Assert.True(WideMath32.DeviceRange(0, uint.MaxValue, 2, 0, 0, 2, 3, 1, out uint h, out uint l, out uint ch, out uint cl));
        Assert.Equal(1u, h); Assert.Equal(3u, l); Assert.Equal(0u, ch); Assert.Equal(6u, cl);
        Assert.False(WideMath32.DeviceRange(uint.MaxValue, uint.MaxValue - 1, 1, 0, 0, 0, 2, 0, out h, out l, out ch, out cl));
        Assert.Equal(0u, h | l | ch | cl);
        Assert.False(WideMath32.DeviceRange(0, 0, 0, 7, 0, 3, 1, 1, out _, out _, out _, out _));
        Assert.True(WideMath32.DeviceRange(0, 0, 0, 8, 0, 3, 1, 1, out _, out _, out _, out _));
        Assert.False(WideMath32.DeviceRange(0, 0, uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue, 1, 3, out _, out _, out _, out _));
    }
}
