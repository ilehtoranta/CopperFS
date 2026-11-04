namespace CopperStart.Dos;

/// <summary>Signed seek deltas applied to the unsigned 32-bit file sizes in OFS/FFS.</summary>
public static class AmigaFilePositionCore
{
    /// <summary>Apply a signed QUAD displacement without narrowing a file position.</summary>
    public static bool Offset64(ulong current, ulong length, uint high, uint low,
        int mode, ulong maximum, out ulong next)
    {
        next = current;
        if (mode < -1 || mode > 1 || current > length) return false;
        var origin = mode == -1 ? 0UL : mode == 0 ? current : length;
        var value = ((ulong)high << 32) | low;
        if ((high & 0x80000000) != 0)
        {
            var distance = unchecked(0UL - value);
            if (distance > origin) return false;
            next = origin - distance;
        }
        else
        {
            if (origin > maximum || value > maximum - origin) return false;
            next = origin + value;
        }
        return next <= maximum;
    }

    // Keeping the QUAD as words avoids narrowing either an absolute position or
    // a negative displacement at the 2 GiB boundary. The disk format still has
    // a 32-bit file-size field; this is independent of the volume's byte size.
    public static bool Seek(uint current, uint length, uint high, uint low,
        int mode, out uint next)
    {
        next = current;
        if (mode < -1 || mode > 1 || current > length) return false;
        var origin = mode == -1 ? 0u : mode == 0 ? current : length;
        if (high == 0)
        {
            if (low > length - origin) return false;
            next = origin + low;
            return true;
        }
        if (high != uint.MaxValue || low == 0) return false;
        var distance = unchecked(0u - low);
        if (distance > origin) return false;
        next = origin - distance;
        return true;
    }
}
