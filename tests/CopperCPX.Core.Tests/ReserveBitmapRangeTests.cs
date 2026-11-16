using System.Reflection;
using CopperCPX.Reference.Tests;

namespace CopperCPX.Tests;

public class ReserveBitmapRangeTests
{
    [Theory]
    [InlineData(11u)]
    [InlineData(12u)]
    public void AlignedRangeProofChecksEveryOwnedBitAndIgnoresNeighbours(uint windowShift)
    {
        var ram = new Ram(new byte[16384]);
        var engine = new CpxEngine<Ram>(ram, 32);
        var check = typeof(CpxEngine<Ram>).GetMethod("ReserveBitsAllocated", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var random = new Random(0x435058);
        const uint bitmap = 4096;
        uint units = 1u << (int)windowShift;
        foreach (uint width in new uint[] { 1, 2, 4, 8, 16 })
        for (int sample = 0; sample < 64; sample++)
        {
            uint bit = sample == 0 ? 0 : sample == 1 ? units - width : (uint)random.Next((int)(units / width)) * width;
            random.NextBytes(ram.Bytes.AsSpan((int)bitmap + 64, (int)units / 8));
            for (uint at = 0; at < width; at++)
                ram.Bytes[bitmap + 64 + ((bit + at) >> 3)] |= (byte)(128 >> (int)((bit + at) & 7));
            Assert.True(Read());
            // Each bit in the range independently invalidates the proof. This
            // includes both bytes of sixteen-unit metadata pairs.
            for (uint missing = 0; missing < width; missing++)
            {
                uint slot = bitmap + 64 + ((bit + missing) >> 3);
                byte mask = (byte)(128 >> (int)((bit + missing) & 7));
                ram.Bytes[slot] ^= mask;
                Assert.False(Read());
                ram.Bytes[slot] ^= mask;
            }
            if (bit != 0) ToggleNeighbour(bit - 1);
            if (bit + width < units) ToggleNeighbour(bit + width);

            bool Read() => (bool)check.Invoke(engine, new object[] { bitmap, bit + units * 7, width, windowShift })!;
            void ToggleNeighbour(uint neighbour)
            {
                ram.Bytes[bitmap + 64 + (neighbour >> 3)] ^= (byte)(128 >> (int)(neighbour & 7));
                Assert.True(Read());
            }
        }
    }
}
