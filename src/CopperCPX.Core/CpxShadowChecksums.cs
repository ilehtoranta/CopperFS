namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    internal static uint ChecksumForShadow(ref TMemory memory, in CpxCrcInput input)
        => CrcKernel(ref memory, in input);
}

internal static class CpxShadowChecksums<TMemory> where TMemory : struct, ICpxMemory
{
    internal static uint Compute(ref TMemory memory, uint buffer, uint bytes, uint table, uint zeros, uint word)
    {
        if (table != 0)
        {
            var input = new CpxCrcInput { Buffer = buffer, Bytes = bytes, Table = table, Zeros = zeros, Word = word, Page = true };
            return CpxEngine<TMemory>.ChecksumForShadow(ref memory, in input);
        }
        uint crc = uint.MaxValue;
        for (uint at = 0; at < bytes; at++)
        {
            crc ^= at is >= 8 and < 12 ? 0u : memory.Read8(buffer + at);
            for (uint bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0x82f63b78u & (0u - (crc & 1)));
        }
        return ~crc;
    }
}
