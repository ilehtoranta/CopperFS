using Amiga;

namespace CopperStart.Dos;

// Private, already validated FFS bitmap words. Padding bits never contribute.
public static class AmigaBitmapWordCore
{
    public static uint Population(uint value)
    {
        value -= (value >> 1) & 0x55555555;
        value = (value & 0x33333333) + ((value >> 2) & 0x33333333);
        value = (value + (value >> 4)) & 0x0F0F0F0F;
        value += value >> 8; value += value >> 16;
        return value & 63;
    }

    // Callers have already found a nonzero free-word mask.
    public static uint FirstSet(uint value)
    {
        uint bit = 0;
        if ((value & 0xFFFF) == 0) { value >>= 16; bit += 16; }
        if ((value & 0xFF) == 0) { value >>= 8; bit += 8; }
        if ((value & 15) == 0) { value >>= 4; bit += 4; }
        if ((value & 3) == 0) { value >>= 2; bit += 2; }
        if ((value & 1) == 0) bit++;
        return bit;
    }

    public static uint CountFree<T>(ref T p, APTR bitmap, uint bits) where T : struct, IAmigaGuestMemory
    {
        uint free = 0;
        for (uint at = 0; at < bits; at += 32)
        {
            var word = p.ReadUInt32(bitmap, 4 + (int)(at / 32) * 4);
            var count = bits - at;
            if (count < 32) word &= (1u << (int)count) - 1;
            free += Population(word);
        }
        return free;
    }
}
