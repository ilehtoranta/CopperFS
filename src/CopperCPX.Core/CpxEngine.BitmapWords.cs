namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // The caller admits a range within one bitmap window. Metadata pages and
    // their bitmap payloads are four-byte aligned. Only edge words need masks;
    // full words avoid per-byte shifts and preserve all surrounding bits.
    private bool BitmapRangeMatches(uint page, uint first, uint count, bool used)
    {
        while (count != 0)
        {
            uint offset = first & 31, take = 32 - offset;
            if (take > count) take = count;
            uint mask = offset == 0 ? uint.MaxValue : uint.MaxValue >> (int)offset;
            if (take != 32 - offset) mask &= uint.MaxValue << (int)(32 - offset - take);
            uint value = memory.Read32(page + 64 + (first >> 5) * 4) & mask;
            if (value != (used ? mask : 0u)) return false;
            first += take; count -= take;
        }
        return true;
    }

    private void SetBitmapRange(uint page, uint first, uint count, bool used)
    {
        while (count != 0)
        {
            uint offset = first & 31, take = 32 - offset;
            if (take > count) take = count;
            uint mask = offset == 0 ? uint.MaxValue : uint.MaxValue >> (int)offset;
            if (take != 32 - offset) mask &= uint.MaxValue << (int)(32 - offset - take);
            uint at = page + 64 + (first >> 5) * 4, value = memory.Read32(at);
            memory.Write32(at, used ? value | mask : value & ~mask);
            first += take; count -= take;
        }
    }
}
