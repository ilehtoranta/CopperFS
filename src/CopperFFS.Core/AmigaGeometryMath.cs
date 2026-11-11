using System.Runtime.CompilerServices;

namespace CopperStart.Dos;

// The native mount validates geometry before publishing it. Keep its variable
// divisors checked at the arithmetic boundary too, without duplicating an
// MC68000 long-division loop (and exception stub) at every filesystem call site.
internal static class AmigaGeometryMath
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static uint Divide(uint value, uint divisor)
    { if (divisor == 0) return 0; return value / divisor; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static uint Remainder(uint value, uint divisor)
    { if (divisor == 0) return 0; return value % divisor; }

    // Keep both operations together so native arithmetic selection can retain
    // both outputs of a single division, including the MC68000 software path.
    // Byte-copy callers copy the out temporary to a scalar local; otherwise its
    // address-taken home can cause a native memory load on every copied byte.
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static uint DivRem(uint value, uint divisor, out uint remainder)
    {
        if (divisor == 0) { remainder = 0; return 0; }
        var quotient = value / divisor;
        remainder = value % divisor;
        return quotient;
    }
}
