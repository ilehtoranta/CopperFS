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
}
