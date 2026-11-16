using System.Numerics;
using System.Reflection;

namespace CopperCPX.Tests;

public class AllocationGeometryBoundaryTests
{
    [Theory]
    [InlineData(512, 512, false)][InlineData(512, 512, true)]
    [InlineData(512, 1024, false)][InlineData(512, 1024, true)]
    [InlineData(4096, 4096, false)][InlineData(4096, 4096, true)]
    public void MetadataPairsMatchUnboundedArithmeticAtEveryReservedBoundary(uint unit, uint page, bool shadow)
    {
        foreach (ulong units in new[] { 8192UL, 131075UL, 0x100000003UL, 0x10000000001UL })
        {
            var ram = new Ram(new byte[1000000]); var engine = new CpxEngine<Ram>(ram, 32);
            Assert.Equal(CpxError.None, engine.Initialize(512, unit, units * (unit / 512), 1, metadataPageBytes: page));
            if (shadow) Assert.Equal(CpxError.None, engine.EnableShadowVolume(900000, 32768));
            uint q = page / unit, span = (shadow ? 2u : 1u) * q;
            ulong first = 2097152 / unit, backup = (units & ~(ulong)(q - 1)) - q;
            ulong floor = shadow ? Math.Max(first, units / 8) : 0;
            if (shadow) { ram.Write32(900040, (uint)(floor >> 32)); ram.Write32(900044, (uint)floor); }
            uint prefix = (page == 512 ? 33u : 17u) * span;
            var points = new HashSet<ulong> { 0, 1, ulong.MaxValue, ulong.MaxValue - span };
            foreach (ulong boundary in new[] { first, floor, backup, units, 65536UL, 131072UL, 0x100000000UL, units & ~65535UL })
            for (int delta = -(int)span - 2; delta <= (int)span + 2; delta++)
                points.Add(unchecked(boundary + (ulong)delta));
            foreach (ulong group in new[] { 65536UL, 131072UL, 0x100000000UL, units & ~65535UL })
            for (int delta = -2; delta <= (int)span + 2; delta++) points.Add(unchecked(group + prefix + (ulong)delta));
            uint random = 0x9137ab;
            for (int n = 0; n < 256; n++) { random ^= random << 13; random ^= random >> 17; random ^= random << 5; points.Add(random); }
            var method = typeof(CpxEngine<Ram>).GetMethod("Allocatable", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var dataMethod = typeof(CpxEngine<Ram>).GetMethod("DataUnit", BindingFlags.Instance | BindingFlags.NonPublic)!;
            bool Data(BigInteger at) => at >= first && at >= floor && at < backup &&
                (at < 65536 || (uint)(at & 65535) >= prefix);
            foreach (ulong address in points)
            {
                BigInteger start = address, end = start + span;
                bool expected = (address & (span - 1)) == 0 && end <= units && Data(start) && Data(end - 1);
                bool actual = (bool)method.Invoke(engine, [address])!;
                Assert.True(expected == actual, $"unit={unit}, page={page}, shadow={shadow}, units={units:X}, address={address:X}, expected={expected}");
                Assert.Equal(Data(start), (bool)dataMethod.Invoke(engine, [address])!);
            }
        }
    }
}
