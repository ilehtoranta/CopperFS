namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    public bool DiscardEnabled => memory.Read32(Arena + (TrimDisabled)) == 0;
    public uint DiscardedUnits => memory.Read32(Arena + (FileValue));

    /// <summary>Examines at most one bitmap window and discards at most one unit.
    /// The cursor advances even on a full window, bounding maintenance latency.</summary>
    private StepResult TrimFreeStep()
    {
        if (HasBufferedChanges)
            return Done();
        uint p = Page(0);
        ulong at = RW(Target);
        switch (memory.Read32(Arena + (Phase)))
        {
            case 0:
                if (memory.Read32(Arena + (TrimDisabled)) != 0)
                    return Done();
                ulong cursor = RW(TrimCursor), first = ((ulong)(2097152u >> (int)memory.Read32(Arena + (UnitShift))));
                if ((cursor) < (first) || (cursor) >= (Backup))
                    cursor = first;
                ulong group = (((ulong)(((uint)(((ulong)(cursor)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(cursor))) & 0xffff0000)));
                Scalar64.Subtract(RW(Units), group, out var remaining);
                if (!(((ulong)(group)) == 0) && ((uint)(((ulong)(remaining)) >> 32)) == 0 && ((uint)((ulong)(remaining))) < 18 * Q)
                {
                    WW(TrimCursor, first);
                    return Done();
                }

                WW(Target, cursor);
                return Issue(IoKind.Read, Anchor + 8 * Q, Page(3), P, 1);
            case 1:
                uint reserve = Page(3), count = memory.Read16(reserve + 64);
                if (!Control(reserve, 4, Anchor + 8 * Q) || !ReadableGeneration(reserve) || count > 64 || memory.Read16(reserve + 52) != 520)
                    return Fail(CpxError.Corrupt);
                return Issue(IoKind.Read, Bitmap(at), p, P, 2);
            case 2:
                if (!ControlBitmap(p, at, (((ulong)(((uint)(((ulong)(at)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(at))) & 0xffff0000)))) || !ReadableGeneration(p))
                    return Fail(CpxError.Corrupt);
                memory.Write32(Arena + (Phase), 3);
                return StepResult.Pending;
            case 3:
                // Exclude reserve even if damaged free-space bits claim it is free.
                uint r = Page(3), entries = memory.Read16(r + 64);
                for (uint n = 0; n < entries; n++)
                {
                    ulong held = GetWide(r + 72 + n * 8);
                    if (!Allocatable(held))
                        return Fail(CpxError.Corrupt);
                    if (((uint)(((ulong)(held)) >> 32)) != ((uint)(((ulong)(at)) >> 32)) || (((uint)((ulong)(held))) >> 12) != (((uint)((ulong)(at))) >> 12))
                        continue;
                    for (uint q = 0; q < Q; q++)
                    {
                        uint bit = (((uint)((ulong)(held))) & 4095) + q, byteAt = p + 64 + (bit >> 3);
                        memory.Write8(byteAt, (byte)(memory.Read8(byteAt) | (0x80 >> (int)(bit & 7))));
                    }
                }

                memory.Write32(Arena + (Phase), 4);
                return StepResult.Pending;
            case 4:
                ulong window = (((ulong)(((uint)(((ulong)(at)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(at))) & ~4095u)));
                for (uint bit = ((uint)((ulong)(at))) & 4095; bit < 4096; bit++)
                {
                    if ((memory.Read8(p + 64 + (bit >> 3)) & (0x80 >> (int)(bit & 7))) != 0 || !DataUnit(window + bit))
                        continue;
                    WW(TrimCursor, window + bit + 1);
                    return Issue(IoKind.Discard, window + bit, 0, 1u << (int)memory.Read32(Arena + (UnitShift)), 5);
                }

                WW(TrimCursor, window + 4096);
                return Done();
            case 5:
                if (memory.Read32(Arena + (TrimDisabled)) == 0)
                    memory.Write32(Arena + (FileValue), 1);
                return Done();
            default:
                return Fail(CpxError.Corrupt);
        }
    }

    /// <summary>One retired range, within one bitmap window. No mutation can be
    /// admitted until revalidation and the single discard completion have finished.</summary>
    public CpxError BeginTrim(Wide first, uint units)
    {
        if (units == 0 || units > 4096 - (((uint)((ulong)(first))) & 4095) || !Scalar64.Add(first, ((ulong)(units)), out var end) || (end) > (RW(Units)))
            return CpxError.InvalidArgument;
        CpxError error = StartOperation(CpxOperation.Trim, first);
        if (error == CpxError.None)
            memory.Write32(Arena + (TrimCount), units);
        return error;
    }

    private bool DataUnit(ulong unit)
    {
        if (((uint)(((ulong)(unit)) >> 32)) == 0 && ((uint)((ulong)(unit))) < memory.Read32(Arena + (GeometryFirst)))
            return false;
        uint high = memory.Read32(Arena + (GeometryBackup));
        if (((uint)(((ulong)(unit)) >> 32)) > high || ((uint)(((ulong)(unit)) >> 32)) == high && ((uint)((ulong)(unit))) >= memory.Read32(Arena + (GeometryBackup + 4)))
            return false;
        // A unit below the backup and past a group's reserved prefix implies
        // that the group contains its complete 17-page metadata prefix.
        return ((uint)(((ulong)(unit)) >> 32)) == 0 && (((uint)((ulong)(unit))) & 0xffff0000) == 0 || (((uint)((ulong)(unit))) & 65535) >= 17 * Q;
    }

    private StepResult TrimStep()
    {
        if (HasBufferedChanges)
            return Done();
        uint p = Page(0);
        ulong at = RW(Target);
        switch (memory.Read32(Arena + (Phase)))
        {
            case 0:
                if (memory.Read32(Arena + (TrimCount)) == 0)
                    return Reject(CpxError.InvalidArgument);
                if (memory.Read32(Arena + (TrimDisabled)) != 0)
                    return Done();
                if (!DataUnit(at) || !DataUnit(at + (memory.Read32(Arena + (TrimCount)) - 1)))
                    return Reject(CpxError.InvalidArgument);
                return Issue(IoKind.Read, Anchor + 8 * Q, Page(3), P, 1);
            case 1:
                uint reserve = Page(3), count = memory.Read16(reserve + 64);
                if (!Control(reserve, 4, Anchor + 8 * Q) || !ReadableGeneration(reserve) || count > 64 || memory.Read16(reserve + 52) != 520)
                    return Fail(CpxError.Corrupt);
                ulong end = at + memory.Read32(Arena + (TrimCount));
                for (uint n = 0; n < count; n++)
                {
                    ulong reserved = GetWide(reserve + 72 + n * 8);
                    if (!Allocatable(reserved))
                        return Fail(CpxError.Corrupt);
                    if ((reserved) < (end) && (at) < (reserved + Q))
                        return Done();
                }

                return Issue(IoKind.Read, Bitmap(at), p, P, 2);
            case 2:
                if (!ControlBitmap(p, at, (((ulong)(((uint)(((ulong)(at)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(at))) & 0xffff0000)))) || !ReadableGeneration(p))
                    return Fail(CpxError.Corrupt);
                // All allocated space is excluded, including unwritten extents. Do
                // not split a stale hint: a later maintenance step may retry it.
                uint bit = ((uint)((ulong)(at))) & 4095;
                for (uint n = 0; n < memory.Read32(Arena + (TrimCount)); n++)
                    if ((memory.Read8(p + 64 + ((bit + n) >> 3)) & (0x80 >> (int)((bit + n) & 7))) != 0)
                        return Done();
                return Issue(IoKind.Discard, at, 0, memory.Read32(Arena + (TrimCount)) << (int)memory.Read32(Arena + (UnitShift)), 3);
            case 3:
                if (memory.Read32(Arena + (TrimDisabled)) == 0)
                    memory.Write32(Arena + (FileValue), memory.Read32(Arena + (TrimCount)));
                return Done();
            default:
                return Fail(CpxError.Corrupt);
        }
    }
}
