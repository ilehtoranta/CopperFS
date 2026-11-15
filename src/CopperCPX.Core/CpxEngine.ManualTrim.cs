namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    // Exclusive operation arena, shared with namespace mutation. Page 0 holds
    // a bitmap, 1 its group descriptor and 3 the retired metadata reserve.
    private const uint ManualMode = 10800, ManualProtected = 10804, ManualMaximum = 10808, ManualBlocks = 10812, ManualRanges = 10816, ManualGroup = 10820, ManualMap = 10828, ManualPass = 10832, ManualBit = 10836, ManualGroupUnits = 10840, ManualGroupFree = 10844, ManualTotal = 10848, ManualExpected = 10856, ManualRoot = 10864, ManualFirst = 10872, ManualEnd = 10876, ManualDeviceLimit = 10880;
    public uint ManualTrimBlocks => memory.Read32(Arena + (ManualBlocks));
    public uint ManualTrimRanges => memory.Read32(Arena + (ManualRanges));
    public uint ManualTrimMaximum => memory.Read32(Arena + (ManualMaximum));

    public CpxError BeginManualTrim(uint flags, bool writeProtected = false)
    {
        if (flags > 2)
            return CpxError.InvalidArgument;
        CpxError error = StartOperation(CpxOperation.ManualTrim);
        if (error != CpxError.None)
            return error;
        memory.Write32(Arena + (ManualMode), flags);
        memory.Write32(Arena + (ManualProtected), writeProtected || IsReadOnly ? 1u : 0u);
        memory.Write32(Arena + (ManualMaximum), 0);
        memory.Write32(Arena + (ManualDeviceLimit), 0);
        memory.Write32(Arena + (ManualBlocks), 0);
        memory.Write32(Arena + (ManualRanges), 0);
        memory.Write32(Arena + (ManualPass), 0);
        WW(ManualGroup, default);
        WW(ManualTotal, default);
        return CpxError.None;
    }

    private ulong ManualDescriptor => (((ulong)(RW(ManualGroup))) == 0) ? ((ulong)(131072u >> (int)memory.Read32(Arena + (UnitShift)))) : RW(ManualGroup);
    private ulong ManualWindow => RW(ManualGroup) + memory.Read32(Arena + (ManualMap)) * 4096;

    private StepResult ManualTrimStep()
    {
        uint p = Page(0), descriptor = Page(1), reserve = Page(3);
        switch (memory.Read32(Arena + (Phase)))
        {
            case 0:
                if (HasBufferedChanges && memory.Read32(Arena + (ManualMode)) != 2 && memory.Read32(Arena + (ManualProtected)) == 0)
                    return RetireBufferedGroup(0);
                if (!DiscardEnabled)
                    return Reject(CpxError.Unsupported);
                return Issue(IoKind.QueryDiscard, default, Arena + ManualDeviceLimit, 4, 1);
            case 1:
                if (!DiscardEnabled)
                    return Reject(CpxError.Unsupported);
                uint maximum = memory.Read32(Arena + (ManualDeviceLimit)) >> (int)(memory.Read32(Arena + (UnitShift)) - memory.Read32(Arena + (SectorShift)));
                if (maximum == 0)
                {
                    memory.Write32(Arena + (TrimDisabled), 1);
                    return Reject(CpxError.Unsupported);
                }

                memory.Write32(Arena + (ManualMaximum), maximum > 4096 ? 4096 : maximum);
                if (memory.Read32(Arena + (ManualMode)) == 2)
                    return Done();
                if (memory.Read32(Arena + (ManualProtected)) != 0)
                    return Reject(CpxError.ReadOnly);
                return Issue(IoKind.Flush, default, 0, 0, 2);
            case 2:
                return Issue(IoKind.Read, StatePageAddress, p, P, 3);
            case 3:
                if (!Control(p, 3, StatePageAddress) || !ReadableGeneration(p) || !ValidStateLabel(p) || !Allocatable(GetWide(p + 80)))
                    return Fail(CpxError.Corrupt);
                WW(ManualRoot, GetWide(p + 80));
                WW(ManualExpected, GetWide(p + 96));
                return Issue(IoKind.Read, Anchor + 8 * Q, reserve, P, 4);
            case 4:
                if (!Control(reserve, 4, Anchor + 8 * Q) || !ReadableGeneration(reserve) || memory.Read16(reserve + 52) != 520 || memory.Read16(reserve + 64) > 64)
                    return Fail(CpxError.Corrupt);
                memory.Write32(Arena + (Phase), 5);
                return StepResult.Pending;
            case 5:
                for (uint n = 0; n < memory.Read16(reserve + 64); n++)
                {
                    ulong held = GetWide(reserve + 72 + n * 8);
                    if (!Allocatable(held) || (((ulong)(held)) == ((ulong)(RW(ManualRoot)))))
                        return Fail(CpxError.Corrupt);
                }

                memory.Write32(Arena + (Phase), 6);
                return StepResult.Pending;
            case 6:
                ulong group = RW(ManualGroup);
                if (!Scalar64.Subtract(RW(Units), group, out var left) || (((ulong)(left)) == 0) || !(((ulong)(group)) == 0) && ((uint)(((ulong)(left)) >> 32)) == 0 && ((uint)((ulong)(left))) < 18 * Q)
                    return FinishManualPass();
                memory.Write32(Arena + (ManualGroupUnits), ((uint)(((ulong)(left)) >> 32)) != 0 || ((uint)((ulong)(left))) >= 65536 ? 65536u : ((uint)((ulong)(left))));
                memory.Write32(Arena + (ManualMap), 0);
                memory.Write32(Arena + (ManualGroupFree), 0);
                return Issue(IoKind.Read, ManualDescriptor, descriptor, P, 7);
            case 7:
                if (!ValidPage(descriptor, ManualDescriptor) || !ReadableGeneration(descriptor) || memory.Read16(descriptor + 4) != 6 || !(((ulong)(GetWide(descriptor + 24))) == ((ulong)(RW(ManualGroup)))) || memory.Read16(descriptor + 52) != 80 || memory.Read32(descriptor + 64) != memory.Read32(Arena + (ManualGroupUnits)))
                    return Fail(CpxError.Corrupt);
                memory.Write32(Arena + (Phase), 8);
                return StepResult.Pending;
            case 8:
                return Issue(IoKind.Read, Bitmap(ManualWindow), p, P, 9);
            case 9:
                if (!ControlBitmap(p, ManualWindow, RW(ManualGroup)) || !ReadableGeneration(p))
                    return Fail(CpxError.Corrupt);
                memory.Write32(Arena + (Phase), 10);
                return StepResult.Pending;
            case 10:
                return ValidateTrimWindow();
            case 11:
                // Check every page of the reserve and the current object root
                // independently of the free-count summaries.
                for (uint n = 0; n < memory.Read16(reserve + 64); n++)
                    if (!TrimPageAllocated(GetWide(reserve + 72 + n * 8)))
                        return Fail(CpxError.Corrupt);
                if (!TrimPageAllocated(RW(ManualRoot)))
                    return Fail(CpxError.Corrupt);
                memory.Write32(Arena + (ManualBit), memory.Read32(Arena + (ManualFirst)));
                memory.Write32(Arena + (Phase), memory.Read32(Arena + (ManualPass)) == 0 ? 14u : 12u);
                return StepResult.Pending;
            case 12:
                return NextManualRange();
            case 13:
                if (!DiscardEnabled)
                    return Reject(CpxError.Unsupported);
                memory.Write32(Arena + (Phase), 12);
                return StepResult.Pending;
            case 14:
                if (memory.Read32(Arena + (ManualMap)) < 15)
                {
                    memory.Write32(Arena + (ManualMap), memory.Read32(Arena + (ManualMap)) + 1);
                    memory.Write32(Arena + (Phase), 8);
                    return StepResult.Pending;
                }

                if (memory.Read32(Arena + (ManualGroupFree)) != memory.Read32(descriptor + 68))
                    return Fail(CpxError.Corrupt);
                WW(ManualTotal, RW(ManualTotal) + memory.Read32(Arena + (ManualGroupFree)));
                WW(ManualGroup, RW(ManualGroup) + 65536);
                memory.Write32(Arena + (Phase), 6);
                return StepResult.Pending;
            default:
                return Fail(CpxError.Corrupt);
        }
    }

    private StepResult FinishManualPass()
    {
        if (!(((ulong)(RW(ManualTotal))) == ((ulong)(RW(ManualExpected)))))
            return Fail(CpxError.Corrupt);
        // v1 has unsigned 32-bit result counters. Refuse an unreportable
        // volume before submitting the first irreversible command.
        if (((uint)(((ulong)(RW(ManualTotal))) >> 32)) != 0)
            return Reject(CpxError.InvalidArgument);
        if (memory.Read32(Arena + (ManualPass)) == 1)
            return Done();
        WW(ManualGroup, default);
        WW(ManualTotal, default);
        memory.Write32(Arena + (ManualPass), 1);
        memory.Write32(Arena + (Phase), 6);
        return StepResult.Pending;
    }

    private bool TrimPageAllocated(ulong held)
    {
        ulong window = ManualWindow;
        if (((uint)(((ulong)(held)) >> 32)) != ((uint)(((ulong)(window)) >> 32)) || (((uint)((ulong)(held))) & ~4095u) != ((uint)((ulong)(window))))
            return true;
        uint bit = ((uint)((ulong)(held))) & 4095, p = Page(0) + 64;
        for (uint n = 0; n < Q; n++, bit++)
            if ((memory.Read8(p + (bit >> 3)) & (128 >> (int)(bit & 7))) == 0)
                return false;
        return true;
    }

    private StepResult ValidateTrimWindow()
    {
        ulong window = ManualWindow;
        uint floor = (((ulong)(RW(ManualGroup))) == 0) ? 2097152u >> (int)memory.Read32(Arena + (UnitShift)) : 17 * Q;
        uint offset = memory.Read32(Arena + (ManualMap)) * 4096, first = offset >= floor ? 0 : floor - offset, end = 4096;
        if (first > 4096)
            first = 4096;
        if (!Scalar64.Subtract(Backup, window, out var remaining))
            end = 0;
        else if (((uint)(((ulong)(remaining)) >> 32)) == 0 && ((uint)((ulong)(remaining))) < 4096)
            end = ((uint)((ulong)(remaining)));
        memory.Write32(Arena + (ManualFirst), first);
        memory.Write32(Arena + (ManualEnd), end);
        uint free = 0, p = Page(0) + 64;
        for (uint i = 0; i < 512; i++)
        {
            uint bits = (uint)(255 ^ memory.Read8(p + i));
            if (bits == 0)
                continue;
            if (i * 8 < first || (i + 1) * 8 > end)
                for (uint b = 0; b < 8; b++)
                    if ((bits & (128u >> (int)b)) != 0 && (i * 8 + b < first || i * 8 + b >= end))
                        return Fail(CpxError.Corrupt);
            bits -= ((bits >> 1) & 0x55);
            bits = (bits & 0x33) + ((bits >> 2) & 0x33);
            free += (bits + (bits >> 4)) & 0x0f;
        }

        if (free != memory.Read32(Page(1) + 80 + 4 * memory.Read32(Arena + (ManualMap))))
            return Fail(CpxError.Corrupt);
        memory.Write32(Arena + (ManualGroupFree), memory.Read32(Arena + (ManualGroupFree)) + free);
        memory.Write32(Arena + (Phase), 11);
        return StepResult.Pending;
    }

    private StepResult NextManualRange()
    {
        uint bit = memory.Read32(Arena + (ManualBit)), end = memory.Read32(Arena + (ManualEnd)), map = Page(0) + 64;
        while (bit < end && (memory.Read8(map + (bit >> 3)) & (128 >> (int)(bit & 7))) != 0)
            bit++;
        if (bit >= end)
        {
            memory.Write32(Arena + (Phase), 14);
            return StepResult.Pending;
        }

        uint first = bit, limit = first + memory.Read32(Arena + (ManualMaximum));
        if (limit > end)
            limit = end;
        while (bit < limit && (memory.Read8(map + (bit >> 3)) & (128 >> (int)(bit & 7))) == 0)
            bit++;
        uint count = bit - first;
        memory.Write32(Arena + (ManualBit), bit);
        memory.Write32(Arena + (TrimCount), count);
        if (count > uint.MaxValue - memory.Read32(Arena + (ManualBlocks)) || memory.Read32(Arena + (ManualRanges)) == uint.MaxValue)
            return Reject(CpxError.InvalidArgument);
        if (memory.Read32(Arena + (ManualMode)) == 1)
        {
            CountManualRange();
            return StepResult.Pending;
        }

        return Issue(IoKind.Discard, ManualWindow + first, 0, count << (int)memory.Read32(Arena + (UnitShift)), 13);
    }

    private void CountManualRange()
    {
        memory.Write32(Arena + (ManualBlocks), memory.Read32(Arena + (ManualBlocks)) + memory.Read32(Arena + (TrimCount)));
        memory.Write32(Arena + (ManualRanges), memory.Read32(Arena + (ManualRanges)) + 1);
    }
}
