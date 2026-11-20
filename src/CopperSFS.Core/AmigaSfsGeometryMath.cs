using System.Runtime.CompilerServices;

namespace CopperSFS;

internal static class AmigaSfsGeometryMath
{
    // SFS2 positions are 64-bit, but geometry divisors and remainders are 32-bit.
    // Native 64-bit / and % otherwise make two separate runtime divisions.
    // Copy the out temporary to a scalar local before using it in a byte loop.
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static ulong DivRem(ulong value, uint divisor, out uint remainder)
    {
        // The common filesystem sector size needs neither a native 64-bit
        // division helper nor the multiplication used to recover its remainder.
        // Keep the general arithmetic contract for every other divisor.
        if (divisor == 512)
        {
            remainder = (uint)value & 511;
            return value >> 9;
        }
        var quotient = value / divisor;
        // The true remainder is below divisor and fits in uint. Computing the
        // low word modulo 2^32 avoids another division or a 64-bit multiply.
        remainder = unchecked((uint)value - (uint)quotient * divisor);
        return quotient;
    }
}
