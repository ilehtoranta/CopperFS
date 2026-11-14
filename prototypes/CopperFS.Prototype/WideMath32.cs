namespace CopperFS.Prototype;

/// <summary>Portable 68000 arithmetic kernels. Only 32-bit scalars; no allocation,
/// division, multiplication, shifts by 32, or data-dependent iteration.</summary>
public static class WideMath32
{
    public static bool Remainder(uint high, uint low, uint divisor, out uint remainder)
    {
        remainder = 0; if (divisor == 0) return false;
        if ((divisor & (divisor - 1)) == 0) { remainder = low & (divisor - 1); return true; }
        // Fixed 64 rounds; the carry represents the 33rd remainder bit.
        for (int bit = 63; bit >= 0; bit--)
        {
            uint input = bit >= 32 ? (high >> (bit - 32)) & 1 : (low >> bit) & 1;
            uint carry = remainder >> 31; remainder = (remainder << 1) | input;
            if (carry != 0 || remainder >= divisor) remainder = unchecked(remainder - divisor);
        }
        return true;
    }
    public static int Compare(uint ah, uint al, uint bh, uint bl)
    { if (ah != bh) return ah < bh ? -1 : 1; return al == bl ? 0 : al < bl ? -1 : 1; }

    public static bool Add(uint ah, uint al, uint bh, uint bl, out uint high, out uint low)
    {
        low = unchecked(al + bl); uint carry = low < al ? 1u : 0u;
        uint sum = unchecked(ah + bh); high = unchecked(sum + carry);
        return sum >= ah && high >= sum;
    }
    public static bool Subtract(uint ah, uint al, uint bh, uint bl, out uint high, out uint low)
    {
        uint borrow = al < bl ? 1u : 0u; low = unchecked(al - bl); high = unchecked(ah - bh - borrow);
        return Compare(ah, al, bh, bl) >= 0;
    }
    public static void ShiftRight(uint high, uint low, int shift, out uint resultHigh, out uint resultLow)
    {
        if (shift <= 0) { resultHigh = high; resultLow = low; return; }
        if (shift >= 64) { resultHigh = resultLow = 0; return; }
        if (shift >= 32) { resultHigh = 0; resultLow = high >> (shift - 32); return; }
        resultLow = (low >> shift) | (high << (32 - shift)); resultHigh = high >> shift;
    }
    public static bool ShiftLeft(uint high, uint low, int shift, out uint resultHigh, out uint resultLow)
    {
        if (shift < 0 || shift >= 64) { resultHigh = resultLow = 0; return false; }
        if (shift == 0) { resultHigh = high; resultLow = low; return true; }
        if (shift >= 32)
        {
            int extra = shift - 32; resultHigh = low << extra; resultLow = 0;
            return high == 0 && (extra == 0 || (low >> (32 - extra)) == 0);
        }
        resultHigh = (high << shift) | (low >> (32 - shift)); resultLow = low << shift;
        return (high >> (32 - shift)) == 0;
    }
    public static bool RoundUpUnits(uint high, uint low, int unitShift, out uint unitsHigh, out uint unitsLow)
    {
        unitsHigh = unitsLow = 0; if (unitShift < 0 || unitShift > 31) return false;
        ShiftRight(high, low, unitShift, out unitsHigh, out unitsLow);
        uint mask = unitShift == 0 ? 0u : (1u << unitShift) - 1;
        return (low & mask) == 0 || Add(unitsHigh, unitsLow, 0, 1, out unitsHigh, out unitsLow);
    }
    /// <summary>Translate a nonempty partition-relative AU range to absolute sectors.
    /// Both the relative end and absolute end are checked; failure outputs are zero.</summary>
    public static bool DeviceRange(uint baseHigh, uint baseLow, uint capacityHigh, uint capacityLow,
        uint unitHigh, uint unitLow, uint unitCount, int sectorShift,
        out uint firstHigh, out uint firstLow, out uint countHigh, out uint countLow)
    {
        firstHigh = firstLow = countHigh = countLow = 0;
        if (unitCount == 0 || sectorShift < 0 || sectorShift > 3 ||
            !ShiftLeft(unitHigh, unitLow, sectorShift, out uint rh, out uint rl) ||
            !ShiftLeft(0, unitCount, sectorShift, out uint ch, out uint cl) ||
            !Add(rh, rl, ch, cl, out uint eh, out uint el) || Compare(eh, el, capacityHigh, capacityLow) > 0 ||
            !Add(baseHigh, baseLow, eh, el, out _, out _) ||
            !Add(baseHigh, baseLow, rh, rl, out uint fh, out uint fl)) return false;
        firstHigh = fh; firstLow = fl; countHigh = ch; countLow = cl; return true;
    }
}
