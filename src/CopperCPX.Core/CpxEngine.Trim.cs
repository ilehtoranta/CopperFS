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
        uint cpxArenaBase = Arena;
        if (((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) && memory.Read32(cpxArenaBase + (Active)) != 0 && memory.Read32(cpxArenaBase + (StagedCount)) != 0))
            return Done();
        uint p = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(0)) * 4)));
        ulong at = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Target)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Target)) + 4))))));
        switch (memory.Read32(cpxArenaBase + (Phase)))
        {
            case 0:
                if (memory.Read32(cpxArenaBase + (TrimDisabled)) != 0)
                    return Done();
                ulong cursor = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(TrimCursor)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(TrimCursor)) + 4)))))), first = ((ulong)(2097152u >> (int)memory.Read32(cpxArenaBase + (UnitShift))));
                if ((cursor) < (first) || (cursor) >= ((((((ulong)(memory.Read32(cpxArenaBase + (((uint)(GeometryBackup)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(GeometryBackup)) + 4)))))))))
                    cursor = first;
                ulong group = (((ulong)(((uint)(((ulong)(cursor)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(cursor))) & 0xffff0000)));
                Scalar64.Subtract(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Units)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Units)) + 4)))))), group, out var remaining);
                if (!(((ulong)(group)) == 0) && ((uint)(((ulong)(remaining)) >> 32)) == 0 && ((uint)((ulong)(remaining))) < (GroupMetadataPages + 1) * MetadataSpan)
                {
                    {
                        uint cpxWideAddress = cpxArenaBase + (uint)(TrimCursor);
                        ulong cpxWideValue = first;
                        memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                        memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                    }

                    return Done();
                }

            {
                uint cpxWideAddress = cpxArenaBase + (uint)(Target);
                ulong cpxWideValue = cursor;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                return ReadStaged((((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + 8 * (memory.Read32(cpxArenaBase + (GeometryQ))), (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(3)) * 4))), 1);
            case 1:
                uint reserve = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(3)) * 4))), count = memory.Read16(reserve + 64);
                if (!Control(reserve, 4, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + 8 * (memory.Read32(cpxArenaBase + (GeometryQ)))) || !ReadableGeneration(reserve) || count > ReserveCapacity || memory.Read16(reserve + 52) != ReservePayloadBytes)
                    return Fail(CpxError.Corrupt);
                return ReadStaged(Bitmap(at), p, 2);
            case 2:
                if (!ControlBitmap(p, at, (((ulong)(((uint)(((ulong)(at)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(at))) & 0xffff0000)))) || !ReadableGeneration(p))
                    return Fail(CpxError.Corrupt);
                memory.Write32(cpxArenaBase + (Phase), 3);
                return StepResult.Pending;
            case 3:
                // Exclude reserve even if damaged free-space bits claim it is free.
                uint r = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(3)) * 4))), entries = memory.Read16(r + 64);
                for (uint n = 0; n < entries; n++)
                {
                    ulong held = ((((ulong)(memory.Read32(((uint)(r + 72 + n * 8))))) << 32 | ((ulong)(memory.Read32(((uint)(r + 72 + n * 8)) + 4)))));
                    if (!Allocatable(held))
                        return Fail(CpxError.Corrupt);
                    if (((uint)(((ulong)(held)) >> 32)) != ((uint)(((ulong)(at)) >> 32)) || (((uint)((ulong)(held))) >> (int)BitmapWindowShift) != (((uint)((ulong)(at))) >> (int)BitmapWindowShift))
                        continue;
                    for (uint q = 0; q < MetadataSpan; q++)
                    {
                        uint bit = (((uint)((ulong)(held))) & BitmapWindowMask) + q, byteAt = p + 64 + (bit >> 3);
                        memory.Write8(byteAt, (byte)(memory.Read8(byteAt) | (0x80 >> (int)(bit & 7))));
                    }
                }

                memory.Write32(cpxArenaBase + (Phase), 4);
                return StepResult.Pending;
            case 4:
                ulong window = (((ulong)(((uint)(((ulong)(at)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(at))) & ~BitmapWindowMask)));
                for (uint bit = ((uint)((ulong)(at))) & BitmapWindowMask; bit < BitmapWindowUnits; bit++)
                {
                    if ((memory.Read8(p + 64 + (bit >> 3)) & (0x80 >> (int)(bit & 7))) != 0 || !DataUnit(window + bit))
                        continue;
                    {
                        uint cpxWideAddress = cpxArenaBase + (uint)(TrimCursor);
                        ulong cpxWideValue = window + bit + 1;
                        memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                        memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                    }

                    return Issue(IoKind.Discard, window + bit, 0, 1u << (int)memory.Read32(cpxArenaBase + (UnitShift)), 5);
                }

            {
                uint cpxWideAddress = cpxArenaBase + (uint)(TrimCursor);
                ulong cpxWideValue = window + BitmapWindowUnits;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                return Done();
            case 5:
                if (memory.Read32(cpxArenaBase + (TrimDisabled)) == 0)
                    memory.Write32(cpxArenaBase + (FileValue), 1);
                return Done();
            default:
                return Fail(CpxError.Corrupt);
        }
    }

    /// <summary>One retired range, within one bitmap window. No mutation can be
    /// admitted until revalidation and the single discard completion have finished.</summary>
    public CpxError BeginTrim(Wide first, uint units)
    {
        uint cpxArenaBase = Arena;
        if (units == 0 || units > BitmapWindowUnits - (((uint)((ulong)(first))) & BitmapWindowMask) || !Scalar64.Add(first, ((ulong)(units)), out var end) || (end) > (((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Units)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Units)) + 4))))))))
            return CpxError.InvalidArgument;
        CpxError error = StartOperation(CpxOperation.Trim, first);
        if (error == CpxError.None)
            memory.Write32(cpxArenaBase + (TrimCount), units);
        return error;
    }

    private bool DataUnit(ulong unit)
    {
        uint context = Arena, policy = memory.Read32(context + CheckpointPolicy), low = (uint)unit, high = (uint)(unit >> 32);
        if ((policy & 4) != 0)
        {
            uint storage = policy & ~7u, firstHigh = memory.Read32(storage + 40);
            if (high < firstHigh || high == firstHigh && low < memory.Read32(storage + 44)) return false;
        }
        if (high == 0 && low < memory.Read32(context + GeometryFirst)) return false;
        uint backupHigh = memory.Read32(context + GeometryBackup);
        if (high > backupHigh || high == backupHigh && low >= memory.Read32(context + GeometryBackup + 4)) return false;
        // A unit below the backup and past a group's reserved prefix implies
        // that the group contains its complete 17-page metadata prefix.
        uint span = memory.Read32(context + GeometryQ) * ((policy & 4) != 0 ? 2u : 1u);
        uint prefix = (memory.Read32(context + PageBytes) == 512 ? 33u : 17u) * span;
        return high == 0 && low < 65536 || (low & 65535) >= prefix;
    }

    private StepResult TrimStep()
    {
        uint cpxArenaBase = Arena;
        if (((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) && memory.Read32(cpxArenaBase + (Active)) != 0 && memory.Read32(cpxArenaBase + (StagedCount)) != 0))
            return Done();
        uint p = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(0)) * 4)));
        ulong at = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Target)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Target)) + 4))))));
        switch (memory.Read32(cpxArenaBase + (Phase)))
        {
            case 0:
                if (memory.Read32(cpxArenaBase + (TrimCount)) == 0)
                    return Reject(CpxError.InvalidArgument);
                if (memory.Read32(cpxArenaBase + (TrimDisabled)) != 0)
                    return Done();
                if (!DataUnit(at) || !DataUnit(at + (memory.Read32(cpxArenaBase + (TrimCount)) - 1)))
                    return Reject(CpxError.InvalidArgument);
                return ReadStaged((((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + 8 * (memory.Read32(cpxArenaBase + (GeometryQ))), (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(3)) * 4))), 1);
            case 1:
                uint reserve = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(3)) * 4))), count = memory.Read16(reserve + 64);
                if (!Control(reserve, 4, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + 8 * (memory.Read32(cpxArenaBase + (GeometryQ)))) || !ReadableGeneration(reserve) || count > ReserveCapacity || memory.Read16(reserve + 52) != ReservePayloadBytes)
                    return Fail(CpxError.Corrupt);
                ulong end = at + memory.Read32(cpxArenaBase + (TrimCount));
                for (uint n = 0; n < count; n++)
                {
                    ulong reserved = ((((ulong)(memory.Read32(((uint)(reserve + 72 + n * 8))))) << 32 | ((ulong)(memory.Read32(((uint)(reserve + 72 + n * 8)) + 4)))));
                    if (!Allocatable(reserved))
                        return Fail(CpxError.Corrupt);
                    if ((reserved) < (end) && (at) < (reserved + MetadataSpan))
                        return Done();
                }

                return ReadStaged(Bitmap(at), p, 2);
            case 2:
                if (!ControlBitmap(p, at, (((ulong)(((uint)(((ulong)(at)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(at))) & 0xffff0000)))) || !ReadableGeneration(p))
                    return Fail(CpxError.Corrupt);
                // All allocated space is excluded, including unwritten extents. Do
                // not split a stale hint: a later maintenance step may retry it.
                uint bit = ((uint)((ulong)(at))) & BitmapWindowMask;
                for (uint n = 0; n < memory.Read32(cpxArenaBase + (TrimCount)); n++)
                    if ((memory.Read8(p + 64 + ((bit + n) >> 3)) & (0x80 >> (int)((bit + n) & 7))) != 0)
                        return Done();
                return Issue(IoKind.Discard, at, 0, memory.Read32(cpxArenaBase + (TrimCount)) << (int)memory.Read32(cpxArenaBase + (UnitShift)), 3);
            case 3:
                if (memory.Read32(cpxArenaBase + (TrimDisabled)) == 0)
                    memory.Write32(cpxArenaBase + (FileValue), memory.Read32(cpxArenaBase + (TrimCount)));
                return Done();
            default:
                return Fail(CpxError.Corrupt);
        }
    }
}
