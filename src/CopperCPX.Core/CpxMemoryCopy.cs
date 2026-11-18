namespace CopperCPX;

public static class CpxMemoryCopy
{
    // The scalar fallback keeps the original ordered memory operations,
    // including odd addresses and overlapping ranges. It borrows only the
    // supplied memory value and never retains a frame or allocates storage.
    public static void Forward<TMemory>(ref TMemory memory, uint destination, uint source, uint bytes)
        where TMemory : struct, ICpxMemory
    {
        uint n = 0;
        if (((destination | source) & 1) == 0)
        {
            for (; bytes - n >= 32; n += 32)
            {
                memory.Write32(destination + n, memory.Read32(source + n));
                memory.Write32(destination + n + 4, memory.Read32(source + n + 4));
                memory.Write32(destination + n + 8, memory.Read32(source + n + 8));
                memory.Write32(destination + n + 12, memory.Read32(source + n + 12));
                memory.Write32(destination + n + 16, memory.Read32(source + n + 16));
                memory.Write32(destination + n + 20, memory.Read32(source + n + 20));
                memory.Write32(destination + n + 24, memory.Read32(source + n + 24));
                memory.Write32(destination + n + 28, memory.Read32(source + n + 28));
            }
            for (; bytes - n >= 4; n += 4) memory.Write32(destination + n, memory.Read32(source + n));
        }
        for (; n < bytes; n++) memory.Write8(destination + n, memory.Read8(source + n));
    }
}
