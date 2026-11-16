namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    private const uint AllocationAddress = 10000, AllocationCount = 10008, AllocationUsed = 10012, MetadataRelease = 10016, MetadataResult = 10024, RefillScan = 10400, RefillWindows = 10408, RefillCount = 10412, RefillFirstWindow = 10416, RefillMinimum = 10424;
    private StepResult EnsureReserve(uint minimum, uint next)
    {
        memory.Write32(Arena + (RefillMinimum), minimum);
        return Call(CpxOperation.Refill, next);
    }

    private StepResult TakeMetadata(uint next)
    {
        memory.Write32(Arena + (MetadataRelease), 0);
        return Call(CpxOperation.MetadataAllocation, next);
    }

    private StepResult ReleaseMetadata(ulong address, uint next)
    {
        uint cpxArenaBase = Arena;
        memory.Write32(cpxArenaBase + (MetadataRelease), 1);
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(MetadataResult);
            ulong cpxWideValue = address;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        return Call(CpxOperation.MetadataAllocation, next);
    }

    private StepResult ChangeAllocation(ulong address, uint count, bool used, uint next)
    {
        uint cpxArenaBase = Arena;
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(AllocationAddress);
            ulong cpxWideValue = address;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(cpxArenaBase + (AllocationCount), count);
        memory.Write32(cpxArenaBase + (AllocationUsed), used ? 1u : 0u);
        return Call(CpxOperation.ChangeAllocation, next);
    }

    private StepResult MetadataAllocationStep()
    {
        uint cpxArenaBase = Arena;
        uint p = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(3)) * 4))), count = memory.Read16(p + 64);
        switch (memory.Read32(cpxArenaBase + (Phase)))
        {
            case 0:
                return ReadStaged((((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + 8 * (memory.Read32(cpxArenaBase + (GeometryQ))), p, 1);
            case 1:
                if (!Control(p, 4, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + 8 * (memory.Read32(cpxArenaBase + (GeometryQ)))) || !ReadableGeneration(p) || count > ReserveCapacity)
                    return Fail(CpxError.Corrupt);
                if (memory.Read32(cpxArenaBase + (MetadataRelease)) == 0)
                {
                    if (memory.Read32(cpxArenaBase + (MetadataReleased)) != 0 || memory.Read32(cpxArenaBase + (MetadataTaken)) >= memory.Read32(cpxArenaBase + (MetadataLimit)))
                        return Fail(CpxError.Corrupt);
                    if (count == 0)
                        return Reject(CpxError.ReserveExhausted);
                    ulong address = ((((ulong)(memory.Read32(((uint)(p + 72 + (count - 1) * 8))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 72 + (count - 1) * 8)) + 4)))));
                    if (!Allocatable(address))
                        return Fail(CpxError.Corrupt);
                    {
                        uint cpxWideAddress = cpxArenaBase + (uint)(MetadataResult);
                        ulong cpxWideValue = address;
                        memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                        memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                    }

                    Clear(p + 72 + (count - 1) * 8, 8);
                    memory.Write16(p + 64, count - 1);
                    memory.Write32(cpxArenaBase + (MetadataTaken), memory.Read32(cpxArenaBase + (MetadataTaken)) + 1);
                }
                else
                {
                    memory.Write32(cpxArenaBase + (MetadataReleased), 1);
                    if (!Allocatable(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(MetadataResult)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(MetadataResult)) + 4))))))))
                        return Fail(CpxError.Corrupt);
                    for (uint n = 0; n < count; n++)
                        if ((((ulong)(((((ulong)(memory.Read32(((uint)(p + 72 + n * 8))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 72 + n * 8)) + 4))))))) == ((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(MetadataResult)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(MetadataResult)) + 4))))))))))
                            return Fail(CpxError.Corrupt);
                    if (count == ReserveCapacity)
                        return ChangeAllocation(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(MetadataResult)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(MetadataResult)) + 4)))))), MetadataSpan, false, 3);
                    {
                        uint cpxWideAddress = (uint)(p + 72 + count * 8);
                        ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(MetadataResult)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(MetadataResult)) + 4))))));
                        memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                        memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                    }

                    memory.Write16(p + 64, count + 1);
                }

                if (DirectInodes && R(MetadataRelease) != 0) SortInodeReserve(p);
                return StagePage(p, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + 8 * (memory.Read32(cpxArenaBase + (GeometryQ))), 3);
            case 3:
                return Done();
            default:
                return Fail(CpxError.Corrupt);
        }
    }

    private StepResult ChangeAllocationStep()
    {
        uint cpxArenaBase = Arena;
        uint p = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(3)) * 4))), count = memory.Read32(cpxArenaBase + (AllocationCount));
        ulong at = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(AllocationAddress)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(AllocationAddress)) + 4)))))), group = (((ulong)(((uint)(((ulong)(at)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(at))) & 0xffff0000)));
        ulong descriptor = (((ulong)(group)) == 0) ? ((ulong)(131072u >> (int)memory.Read32(cpxArenaBase + (UnitShift)))) : group;
        switch (memory.Read32(cpxArenaBase + (Phase)))
        {
            case 0:
                if (count == 0 || count > BitmapWindowUnits - (((uint)((ulong)(at))) & BitmapWindowMask))
                    return Fail(CpxError.Corrupt);
                return ReadStaged(Bitmap(at), p, 1);
            case 1:
                if (!ControlBitmap(p, at, group) || !ReadableGeneration(p))
                    return Fail(CpxError.Corrupt);
                memory.Write32(cpxArenaBase + (Phase), 2);
                return StepResult.Pending;
            case 2:
                // Admission confines this range to one bitmap window. After a
                // valid first unit, its only unavailable suffix is the backup;
                // checking both endpoints covers the whole reserved prefix.
                if (!DataUnit(at) || !DataUnit(at + (count - 1)))
                    return Fail(CpxError.Corrupt);
                NoteBufferedAllocation(at, count, memory.Read32(cpxArenaBase + (AllocationUsed)) != 0);
                uint firstBit = ((uint)((ulong)(at))) & BitmapWindowMask, left = count;
                while (left != 0)
                {
                    uint offset = firstBit & 7, take = 8 - offset;
                    if (take > left)
                        take = left;
                    uint byteAt = p + 64 + (firstBit >> 3), mask = ((1u << (int)take) - 1) << (int)(8 - offset - take), value = memory.Read8(byteAt);
                    if ((value & mask) != (memory.Read32(cpxArenaBase + (AllocationUsed)) != 0 ? 0u : mask))
                        return Fail(CpxError.Corrupt);
                    memory.Write8(byteAt, (byte)(memory.Read32(cpxArenaBase + (AllocationUsed)) != 0 ? value | mask : value & ~mask));
                    firstBit += take;
                    left -= take;
                }

                memory.Write32(cpxArenaBase + (Phase), 3);
                return StepResult.Pending;
            case 3:
                return StagePage(p, Bitmap(at), 4);
            case 4:
                return ReadStaged(descriptor, p, 5);
            case 5:
                uint local = 80 + ((((uint)((ulong)(at))) & 65535) >> (int)BitmapWindowShift) * 4, free = memory.Read32(p + 68), window = memory.Read32(p + local);
                if (!ValidPage(p, descriptor) || !ReadableGeneration(p) || memory.Read16(p + 4) != 6 || !(((ulong)(((((ulong)(memory.Read32(((uint)(p + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 24)) + 4))))))) == ((ulong)(group))) || memory.Read16(p + 52) != GroupPayloadBytes || (memory.Read32(cpxArenaBase + (AllocationUsed)) != 0 ? (free < count || window < count) : (free > 65536 - count || window > BitmapWindowUnits - count)))
                    return Fail(CpxError.Corrupt);
                memory.Write32(p + 68, memory.Read32(cpxArenaBase + (AllocationUsed)) != 0 ? free - count : free + count);
                memory.Write32(p + local, memory.Read32(cpxArenaBase + (AllocationUsed)) != 0 ? window - count : window + count);
                return StagePage(p, descriptor, 6);
            case 6:
                return ReadStaged(StateAddress, p, 7);
            case 7:
                if (!Control(p, 3, StateAddress) || !ReadableGeneration(p))
                    return Fail(CpxError.Corrupt);
                ulong available;
                if (memory.Read32(cpxArenaBase + (AllocationUsed)) != 0 ? !Scalar64.Subtract(((((ulong)(memory.Read32(((uint)(p + 96))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 96)) + 4))))), ((ulong)(count)), out available) : !Scalar64.Add(((((ulong)(memory.Read32(((uint)(p + 96))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 96)) + 4))))), ((ulong)(count)), out available))
                    return Fail(CpxError.Corrupt);
            {
                uint cpxWideAddress = (uint)(p + 96);
                ulong cpxWideValue = available;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                return StagePage(p, StateAddress, 8);
            case 8:
                return Done();
            default:
                return Fail(CpxError.Corrupt);
        }
    }

    // Refill is retired separately from the foreground operation. Each refill
    // transaction touches at most two bitmap windows and six distinct images.
    private StepResult RefillStep()
    {
        uint cpxArenaBase = Arena;
        uint p = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(3)) * 4)));
        ulong at = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(RefillScan)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(RefillScan)) + 4))))));
        switch (memory.Read32(cpxArenaBase + (Phase)))
        {
            case 0:
                return ReadStaged((((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + 8 * (memory.Read32(cpxArenaBase + (GeometryQ))), p, 1);
            case 1:
                if (!Control(p, 4, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + 8 * (memory.Read32(cpxArenaBase + (GeometryQ)))) || !ReadableGeneration(p) || memory.Read16(p + 64) > ReserveCapacity)
                    return Fail(CpxError.Corrupt);
                if (memory.Read16(p + 64) >= memory.Read32(cpxArenaBase + (RefillMinimum)))
                    return Done();
                memory.Write32(cpxArenaBase + (RefillCount), memory.Read16(p + 64));
                memory.Write32(cpxArenaBase + (RefillWindows), 0);
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(RefillScan);
                ulong cpxWideValue = ((ulong)(2097152u >> (int)memory.Read32(cpxArenaBase + (UnitShift))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                return BeginTx(6, 2);
            case 2:
                if ((at) >= ((((((ulong)(memory.Read32(cpxArenaBase + (((uint)(GeometryBackup)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(GeometryBackup)) + 4)))))))))
                    return memory.Read32(cpxArenaBase + (RefillWindows)) == 0 ? Call(CpxOperation.AbortTransaction, 10) : Call(CpxOperation.CommitTransaction, 10);
                ulong group = (((ulong)(((uint)(((ulong)(at)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(at))) & 0xffff0000)));
                Scalar64.Subtract(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Units)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Units)) + 4)))))), group, out var remaining);
                if (!(((ulong)(group)) == 0) && ((uint)(((ulong)(remaining)) >> 32)) == 0 && ((uint)((ulong)(remaining))) < (GroupMetadataPages + 1) * MetadataSpan)
                {
                    {
                        uint cpxWideAddress = cpxArenaBase + (uint)(RefillScan);
                        ulong cpxWideValue = (((((ulong)(memory.Read32(cpxArenaBase + (((uint)(GeometryBackup)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(GeometryBackup)) + 4)))))));
                        memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                        memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                    }

                    return StepResult.Pending;
                }

                return ReadStaged(Bitmap(at), p, 3);
            case 3:
                if (!ControlBitmap(p, at, (((ulong)(((uint)(((ulong)(at)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(at))) & 0xffff0000)))) || !ReadableGeneration(p))
                    return Fail(CpxError.Corrupt);
                memory.Write32(cpxArenaBase + (Phase), 4);
                return StepResult.Pending;
            case 4:
                ulong window = (((ulong)(((uint)(((ulong)(at)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(at))) & ~BitmapWindowMask)));
                uint q = MetadataSpan;
                for (uint bit = ((uint)((ulong)(at))) & BitmapWindowMask; bit < BitmapWindowUnits; bit += q)
                {
                    if (memory.Read8(p + 64 + (bit >> 3)) == 255)
                    {
                        bit = (bit | 7) + 1 - q;
                        continue;
                    }

                    bool free = true;
                    for (uint n = 0; n < q; n++)
                        if ((memory.Read8(p + 64 + ((bit + n) >> 3)) & (0x80 >> (int)((bit + n) & 7))) != 0)
                            free = false;
                    if (!free)
                        continue;
                    if (!Allocatable(window + bit))
                        continue;
                    if (memory.Read32(cpxArenaBase + (RefillWindows)) == 0 || !(((ulong)(window)) == ((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(RefillFirstWindow)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(RefillFirstWindow)) + 4))))))))))
                    {
                        if (memory.Read32(cpxArenaBase + (RefillWindows)) == 2)
                            return Call(CpxOperation.CommitTransaction, 0);
                        memory.Write32(cpxArenaBase + (RefillWindows), memory.Read32(cpxArenaBase + (RefillWindows)) + 1);
                        {
                            uint cpxWideAddress = cpxArenaBase + (uint)(RefillFirstWindow);
                            ulong cpxWideValue = window;
                            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                        }
                    }

                    {
                        uint cpxWideAddress = cpxArenaBase + (uint)(RefillScan);
                        ulong cpxWideValue = window + bit;
                        memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                        memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                    }

                    return ChangeAllocation(window + bit, MetadataSpan, true, 5);
                }

            {
                uint cpxWideAddress = cpxArenaBase + (uint)(RefillScan);
                ulong cpxWideValue = window + BitmapWindowUnits;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write32(cpxArenaBase + (Phase), 2);
                return StepResult.Pending;
            case 5:
                return ReleaseMetadata(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(RefillScan)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(RefillScan)) + 4)))))), 6);
            case 6:
                memory.Write32(cpxArenaBase + (RefillCount), memory.Read32(cpxArenaBase + (RefillCount)) + 1);
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(RefillScan);
                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(RefillScan)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(RefillScan)) + 4)))))) + MetadataSpan;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                if (memory.Read32(cpxArenaBase + (RefillCount)) >= 32)
                    return Call(CpxOperation.CommitTransaction, 11);
                memory.Write32(cpxArenaBase + (Phase), 2);
                return StepResult.Pending;
            case 10:
                return memory.Read32(cpxArenaBase + (RefillCount)) >= memory.Read32(cpxArenaBase + (RefillMinimum)) ? Done() : Reject(CpxError.ReserveExhausted);
            case 11:
                return Done();
            default:
                return Fail(CpxError.Corrupt);
        }
    }
}