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
        uint cpxArenaBase = Arena;
        if (flags > 2)
            return CpxError.InvalidArgument;
        CpxError error = StartOperation(CpxOperation.ManualTrim);
        if (error != CpxError.None)
            return error;
        memory.Write32(cpxArenaBase + (ManualMode), flags);
        memory.Write32(cpxArenaBase + (ManualProtected), writeProtected || (memory.Read32(cpxArenaBase + (ReadOnlyAt)) != 0) ? 1u : 0u);
        memory.Write32(cpxArenaBase + (ManualMaximum), 0);
        memory.Write32(cpxArenaBase + (ManualDeviceLimit), 0);
        memory.Write32(cpxArenaBase + (ManualBlocks), 0);
        memory.Write32(cpxArenaBase + (ManualRanges), 0);
        memory.Write32(cpxArenaBase + (ManualPass), 0);
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(ManualGroup);
            ulong cpxWideValue = default;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        {
            uint cpxWideAddress = cpxArenaBase + (uint)(ManualTotal);
            ulong cpxWideValue = default;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        return CpxError.None;
    }

    private ulong ManualDescriptor
    {
        get
        {
            uint cpxArenaBase = Arena;
            return (((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualGroup)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualGroup)) + 4)))))))) == 0) ? ((ulong)(131072u >> (int)memory.Read32(cpxArenaBase + (UnitShift)))) : ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualGroup)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualGroup)) + 4))))));
        }
    }

    private ulong ManualWindow
    {
        get
        {
            uint cpxArenaBase = Arena;
            return ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualGroup)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualGroup)) + 4)))))) + memory.Read32(cpxArenaBase + (ManualMap)) * 4096;
        }
    }

    private StepResult ManualTrimStep()
    {
        uint cpxArenaBase = Arena;
        uint p = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(0)) * 4))), descriptor = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(1)) * 4))), reserve = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(3)) * 4)));
        switch (memory.Read32(cpxArenaBase + (Phase)))
        {
            case 0:
                if (((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) && memory.Read32(cpxArenaBase + (Active)) != 0 && memory.Read32(cpxArenaBase + (StagedCount)) != 0) && memory.Read32(cpxArenaBase + (ManualMode)) != 2 && memory.Read32(cpxArenaBase + (ManualProtected)) == 0)
                    return RetireBufferedGroup(0);
                if (!(memory.Read32(cpxArenaBase + (TrimDisabled)) == 0))
                    return Reject(CpxError.Unsupported);
                return Issue(IoKind.QueryDiscard, default, cpxArenaBase + ManualDeviceLimit, 4, 1);
            case 1:
                if (!(memory.Read32(cpxArenaBase + (TrimDisabled)) == 0))
                    return Reject(CpxError.Unsupported);
                uint maximum = memory.Read32(cpxArenaBase + (ManualDeviceLimit)) >> (int)(memory.Read32(cpxArenaBase + (UnitShift)) - memory.Read32(cpxArenaBase + (SectorShift)));
                if (maximum == 0)
                {
                    memory.Write32(cpxArenaBase + (TrimDisabled), 1);
                    return Reject(CpxError.Unsupported);
                }

                memory.Write32(cpxArenaBase + (ManualMaximum), maximum > 4096 ? 4096 : maximum);
                if (memory.Read32(cpxArenaBase + (ManualMode)) == 2)
                    return Done();
                if (memory.Read32(cpxArenaBase + (ManualProtected)) != 0)
                    return Reject(CpxError.ReadOnly);
                return Issue(IoKind.Flush, default, 0, 0, 2);
            case 2:
                return ReadStaged(StatePageAddress, p, 3);
            case 3:
                if (!Control(p, 3, StatePageAddress) || !ReadableGeneration(p) || !ValidStateLabel(p) || !Allocatable(((((ulong)(memory.Read32(((uint)(p + 80))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 80)) + 4)))))))
                    return Fail(CpxError.Corrupt);
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(ManualRoot);
                ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)(p + 80))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 80)) + 4)))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

            {
                uint cpxWideAddress = cpxArenaBase + (uint)(ManualExpected);
                ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)(p + 96))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 96)) + 4)))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                return ReadStaged((((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + 8 * (memory.Read32(cpxArenaBase + (GeometryQ))), reserve, 4);
            case 4:
                if (!Control(reserve, 4, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + 8 * (memory.Read32(cpxArenaBase + (GeometryQ)))) || !ReadableGeneration(reserve) || memory.Read16(reserve + 52) != 520 || memory.Read16(reserve + 64) > 64)
                    return Fail(CpxError.Corrupt);
                memory.Write32(cpxArenaBase + (Phase), 5);
                return StepResult.Pending;
            case 5:
                for (uint n = 0; n < memory.Read16(reserve + 64); n++)
                {
                    ulong held = ((((ulong)(memory.Read32(((uint)(reserve + 72 + n * 8))))) << 32 | ((ulong)(memory.Read32(((uint)(reserve + 72 + n * 8)) + 4)))));
                    if (!Allocatable(held) || (((ulong)(held)) == ((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualRoot)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualRoot)) + 4))))))))))
                        return Fail(CpxError.Corrupt);
                }

                memory.Write32(cpxArenaBase + (Phase), 6);
                return StepResult.Pending;
            case 6:
                ulong group = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualGroup)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualGroup)) + 4))))));
                if (!Scalar64.Subtract(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Units)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Units)) + 4)))))), group, out var left) || (((ulong)(left)) == 0) || !(((ulong)(group)) == 0) && ((uint)(((ulong)(left)) >> 32)) == 0 && ((uint)((ulong)(left))) < 18 * MetadataSpan)
                    return FinishManualPass();
                memory.Write32(cpxArenaBase + (ManualGroupUnits), ((uint)(((ulong)(left)) >> 32)) != 0 || ((uint)((ulong)(left))) >= 65536 ? 65536u : ((uint)((ulong)(left))));
                memory.Write32(cpxArenaBase + (ManualMap), 0);
                memory.Write32(cpxArenaBase + (ManualGroupFree), 0);
                return ReadStaged(ManualDescriptor, descriptor, 7);
            case 7:
                if (!ValidPage(descriptor, ManualDescriptor) || !ReadableGeneration(descriptor) || memory.Read16(descriptor + 4) != 6 || !(((ulong)(((((ulong)(memory.Read32(((uint)(descriptor + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(descriptor + 24)) + 4))))))) == ((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualGroup)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualGroup)) + 4))))))))) || memory.Read16(descriptor + 52) != 80 || memory.Read32(descriptor + 64) != memory.Read32(cpxArenaBase + (ManualGroupUnits)))
                    return Fail(CpxError.Corrupt);
                memory.Write32(cpxArenaBase + (Phase), 8);
                return StepResult.Pending;
            case 8:
                return ReadStaged(Bitmap((((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualGroup)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualGroup)) + 4)))))) + memory.Read32(cpxArenaBase + (ManualMap)) * 4096)), p, 9);
            case 9:
                if (!ControlBitmap(p, (((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualGroup)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualGroup)) + 4)))))) + memory.Read32(cpxArenaBase + (ManualMap)) * 4096), ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualGroup)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualGroup)) + 4))))))) || !ReadableGeneration(p))
                    return Fail(CpxError.Corrupt);
                memory.Write32(cpxArenaBase + (Phase), 10);
                return StepResult.Pending;
            case 10:
                return ValidateTrimWindow();
            case 11:
                // Check every page of the reserve and the current object root
                // independently of the free-count summaries.
                for (uint n = 0; n < memory.Read16(reserve + 64); n++)
                    if (!TrimPageAllocated(((((ulong)(memory.Read32(((uint)(reserve + 72 + n * 8))))) << 32 | ((ulong)(memory.Read32(((uint)(reserve + 72 + n * 8)) + 4)))))))
                        return Fail(CpxError.Corrupt);
                if (!TrimPageAllocated(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualRoot)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualRoot)) + 4))))))))
                    return Fail(CpxError.Corrupt);
                memory.Write32(cpxArenaBase + (ManualBit), memory.Read32(cpxArenaBase + (ManualFirst)));
                memory.Write32(cpxArenaBase + (Phase), memory.Read32(cpxArenaBase + (ManualPass)) == 0 ? 14u : 12u);
                return StepResult.Pending;
            case 12:
                return NextManualRange();
            case 13:
                if (!(memory.Read32(cpxArenaBase + (TrimDisabled)) == 0))
                    return Reject(CpxError.Unsupported);
                memory.Write32(cpxArenaBase + (Phase), 12);
                return StepResult.Pending;
            case 14:
                if (memory.Read32(cpxArenaBase + (ManualMap)) < 15)
                {
                    memory.Write32(cpxArenaBase + (ManualMap), memory.Read32(cpxArenaBase + (ManualMap)) + 1);
                    memory.Write32(cpxArenaBase + (Phase), 8);
                    return StepResult.Pending;
                }

                if (memory.Read32(cpxArenaBase + (ManualGroupFree)) != memory.Read32(descriptor + 68))
                    return Fail(CpxError.Corrupt);
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(ManualTotal);
                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualTotal)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualTotal)) + 4)))))) + memory.Read32(cpxArenaBase + (ManualGroupFree));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

            {
                uint cpxWideAddress = cpxArenaBase + (uint)(ManualGroup);
                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualGroup)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualGroup)) + 4)))))) + 65536;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write32(cpxArenaBase + (Phase), 6);
                return StepResult.Pending;
            default:
                return Fail(CpxError.Corrupt);
        }
    }

    private StepResult FinishManualPass()
    {
        uint cpxArenaBase = Arena;
        if (!(((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualTotal)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualTotal)) + 4)))))))) == ((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualExpected)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualExpected)) + 4))))))))))
            return Fail(CpxError.Corrupt);
        // v1 has unsigned 32-bit result counters. Refuse an unreportable
        // volume before submitting the first irreversible command.
        if (((uint)(((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualTotal)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualTotal)) + 4)))))))) >> 32)) != 0)
            return Reject(CpxError.InvalidArgument);
        if (memory.Read32(cpxArenaBase + (ManualPass)) == 1)
            return Done();
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(ManualGroup);
            ulong cpxWideValue = default;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        {
            uint cpxWideAddress = cpxArenaBase + (uint)(ManualTotal);
            ulong cpxWideValue = default;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(cpxArenaBase + (ManualPass), 1);
        memory.Write32(cpxArenaBase + (Phase), 6);
        return StepResult.Pending;
    }

    private bool TrimPageAllocated(ulong held)
    {
        uint cpxArenaBase = Arena;
        ulong window = (((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualGroup)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualGroup)) + 4)))))) + memory.Read32(cpxArenaBase + (ManualMap)) * 4096);
        if (((uint)(((ulong)(held)) >> 32)) != ((uint)(((ulong)(window)) >> 32)) || (((uint)((ulong)(held))) & ~4095u) != ((uint)((ulong)(window))))
            return true;
        uint bit = ((uint)((ulong)(held))) & 4095, p = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(0)) * 4))) + 64;
        for (uint n = 0; n < MetadataSpan; n++, bit++)
            if ((memory.Read8(p + (bit >> 3)) & (128 >> (int)(bit & 7))) == 0)
                return false;
        return true;
    }

    private StepResult ValidateTrimWindow()
    {
        uint cpxArenaBase = Arena;
        ulong window = (((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualGroup)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualGroup)) + 4)))))) + memory.Read32(cpxArenaBase + (ManualMap)) * 4096);
        uint floor = (((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualGroup)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualGroup)) + 4)))))))) == 0) ? 2097152u >> (int)memory.Read32(cpxArenaBase + (UnitShift)) : 17 * MetadataSpan;
        uint offset = memory.Read32(cpxArenaBase + (ManualMap)) * 4096, first = offset >= floor ? 0 : floor - offset, end = 4096;
        if (first > 4096)
            first = 4096;
        if (!Scalar64.Subtract((((((ulong)(memory.Read32(cpxArenaBase + (((uint)(GeometryBackup)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(GeometryBackup)) + 4))))))), window, out var remaining))
            end = 0;
        else if (((uint)(((ulong)(remaining)) >> 32)) == 0 && ((uint)((ulong)(remaining))) < 4096)
            end = ((uint)((ulong)(remaining)));
        memory.Write32(cpxArenaBase + (ManualFirst), first);
        memory.Write32(cpxArenaBase + (ManualEnd), end);
        uint free = 0, p = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(0)) * 4))) + 64;
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

        if (free != memory.Read32((memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(1)) * 4))) + 80 + 4 * memory.Read32(cpxArenaBase + (ManualMap))))
            return Fail(CpxError.Corrupt);
        memory.Write32(cpxArenaBase + (ManualGroupFree), memory.Read32(cpxArenaBase + (ManualGroupFree)) + free);
        memory.Write32(cpxArenaBase + (Phase), 11);
        return StepResult.Pending;
    }

    private StepResult NextManualRange()
    {
        uint cpxArenaBase = Arena;
        uint bit = memory.Read32(cpxArenaBase + (ManualBit)), end = memory.Read32(cpxArenaBase + (ManualEnd)), map = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(0)) * 4))) + 64;
        while (bit < end && (memory.Read8(map + (bit >> 3)) & (128 >> (int)(bit & 7))) != 0)
            bit++;
        if (bit >= end)
        {
            memory.Write32(cpxArenaBase + (Phase), 14);
            return StepResult.Pending;
        }

        uint first = bit, limit = first + memory.Read32(cpxArenaBase + (ManualMaximum));
        if (limit > end)
            limit = end;
        while (bit < limit && (memory.Read8(map + (bit >> 3)) & (128 >> (int)(bit & 7))) == 0)
            bit++;
        uint count = bit - first;
        memory.Write32(cpxArenaBase + (ManualBit), bit);
        memory.Write32(cpxArenaBase + (TrimCount), count);
        if (count > uint.MaxValue - memory.Read32(cpxArenaBase + (ManualBlocks)) || memory.Read32(cpxArenaBase + (ManualRanges)) == uint.MaxValue)
            return Reject(CpxError.InvalidArgument);
        if (memory.Read32(cpxArenaBase + (ManualMode)) == 1)
        {
            CountManualRange();
            return StepResult.Pending;
        }

        return Issue(IoKind.Discard, (((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualGroup)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ManualGroup)) + 4)))))) + memory.Read32(cpxArenaBase + (ManualMap)) * 4096) + first, 0, count << (int)memory.Read32(cpxArenaBase + (UnitShift)), 13);
    }

    private void CountManualRange()
    {
        uint cpxArenaBase = Arena;
        memory.Write32(cpxArenaBase + (ManualBlocks), memory.Read32(cpxArenaBase + (ManualBlocks)) + memory.Read32(cpxArenaBase + (TrimCount)));
        memory.Write32(cpxArenaBase + (ManualRanges), memory.Read32(cpxArenaBase + (ManualRanges)) + 1);
    }
}