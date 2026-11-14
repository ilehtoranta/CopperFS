namespace CopperFS.Cpx4.Reference;

// The host API/codec retains ulong. The arithmetic is delegated to the same
// high/low kernels compiled by the instruction tests. No wide divide/multiply.
internal static class AddressMath
{
    internal static uint Remainder(ulong value, uint divisor)
    {
        if (!WideMath32.Remainder((uint)(value >> 32), (uint)value, divisor, out uint remainder)) throw new DivideByZeroException();
        return remainder;
    }
    internal static ulong Join(uint high, uint low) => (ulong)high << 32 | low;
    internal static int PowerShift(uint power)
    {
        if (power == 0 || (power & (power - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(power));
        int shift = 0; while (power > 1) { power >>= 1; shift++; } return shift;
    }
    internal static ulong Right(ulong value, int shift)
    { WideMath32.ShiftRight((uint)(value >> 32), (uint)value, shift, out uint high, out uint low); return Join(high, low); }
    internal static ulong Left(ulong value, int shift)
    {
        if (!WideMath32.ShiftLeft((uint)(value >> 32), (uint)value, shift, out uint high, out uint low)) throw new OverflowException();
        return Join(high, low);
    }
    internal static ulong Add(ulong a, ulong b)
    {
        if (!WideMath32.Add((uint)(a >> 32), (uint)a, (uint)(b >> 32), (uint)b, out uint high, out uint low)) throw new OverflowException();
        return Join(high, low);
    }
    internal static ulong Units(ulong bytes, int shift)
    {
        if (!WideMath32.RoundUpUnits((uint)(bytes >> 32), (uint)bytes, shift, out uint high, out uint low)) throw new OverflowException();
        return Join(high, low);
    }
}
