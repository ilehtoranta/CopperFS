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
        memory.Write32(Arena + (MetadataRelease), 1);
        WW(MetadataResult, address);
        return Call(CpxOperation.MetadataAllocation, next);
    }

    private StepResult ChangeAllocation(ulong address, uint count, bool used, uint next)
    {
        WW(AllocationAddress, address);
        memory.Write32(Arena + (AllocationCount), count);
        memory.Write32(Arena + (AllocationUsed), used ? 1u : 0u);
        return Call(CpxOperation.ChangeAllocation, next);
    }

    private StepResult MetadataAllocationStep()
    {
        uint p = Page(3), count = memory.Read16(p + 64);
        switch (memory.Read32(Arena + (Phase)))
        {
            case 0:
                return ReadStaged(Anchor + 8 * Q, p, 1);
            case 1:
                if (!Control(p, 4, Anchor + 8 * Q) || !ReadableGeneration(p) || count > 64)
                    return Fail(CpxError.Corrupt);
                if (memory.Read32(Arena + (MetadataRelease)) == 0)
                {
                    if (memory.Read32(Arena + (MetadataReleased)) != 0 || memory.Read32(Arena + (MetadataTaken)) >= memory.Read32(Arena + (MetadataLimit)))
                        return Fail(CpxError.Corrupt);
                    if (count == 0)
                        return Reject(CpxError.ReserveExhausted);
                    ulong address = GetWide(p + 72 + (count - 1) * 8);
                    if (!Allocatable(address))
                        return Fail(CpxError.Corrupt);
                    WW(MetadataResult, address);
                    Clear(p + 72 + (count - 1) * 8, 8);
                    memory.Write16(p + 64, count - 1);
                    memory.Write32(Arena + (MetadataTaken), memory.Read32(Arena + (MetadataTaken)) + 1);
                }
                else
                {
                    memory.Write32(Arena + (MetadataReleased), 1);
                    if (!Allocatable(RW(MetadataResult)))
                        return Fail(CpxError.Corrupt);
                    for (uint n = 0; n < count; n++)
                        if ((((ulong)(GetWide(p + 72 + n * 8))) == ((ulong)(RW(MetadataResult)))))
                            return Fail(CpxError.Corrupt);
                    if (count == 64)
                        return ChangeAllocation(RW(MetadataResult), Q, false, 3);
                    PutWide(p + 72 + count * 8, RW(MetadataResult));
                    memory.Write16(p + 64, count + 1);
                }

                return StagePage(p, Anchor + 8 * Q, 3);
            case 3:
                return Done();
            default:
                return Fail(CpxError.Corrupt);
        }
    }

    private StepResult ChangeAllocationStep()
    {
        uint p = Page(3), count = memory.Read32(Arena + (AllocationCount));
        ulong at = RW(AllocationAddress), group = (((ulong)(((uint)(((ulong)(at)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(at))) & 0xffff0000)));
        ulong descriptor = (((ulong)(group)) == 0) ? ((ulong)(131072u >> (int)memory.Read32(Arena + (UnitShift)))) : group;
        switch (memory.Read32(Arena + (Phase)))
        {
            case 0:
                if (count == 0 || count > 4096 - (((uint)((ulong)(at))) & 4095))
                    return Fail(CpxError.Corrupt);
                return ReadStaged(Bitmap(at), p, 1);
            case 1:
                if (!ControlBitmap(p, at, group) || !ReadableGeneration(p))
                    return Fail(CpxError.Corrupt);
                memory.Write32(Arena + (Phase), 2);
                return StepResult.Pending;
            case 2:
                // Admission confines this range to one bitmap window. After a
                // valid first unit, its only unavailable suffix is the backup;
                // checking both endpoints covers the whole reserved prefix.
                if (!DataUnit(at) || !DataUnit(at + (count - 1)))
                    return Fail(CpxError.Corrupt);
                NoteBufferedAllocation(at, count, memory.Read32(Arena + (AllocationUsed)) != 0);
                uint firstBit = ((uint)((ulong)(at))) & 4095, left = count;
                while (left != 0)
                {
                    uint offset = firstBit & 7, take = 8 - offset;
                    if (take > left)
                        take = left;
                    uint byteAt = p + 64 + (firstBit >> 3), mask = ((1u << (int)take) - 1) << (int)(8 - offset - take), value = memory.Read8(byteAt);
                    if ((value & mask) != (memory.Read32(Arena + (AllocationUsed)) != 0 ? 0u : mask))
                        return Fail(CpxError.Corrupt);
                    memory.Write8(byteAt, (byte)(memory.Read32(Arena + (AllocationUsed)) != 0 ? value | mask : value & ~mask));
                    firstBit += take;
                    left -= take;
                }

                memory.Write32(Arena + (Phase), 3);
                return StepResult.Pending;
            case 3:
                return StagePage(p, Bitmap(at), 4);
            case 4:
                return ReadStaged(descriptor, p, 5);
            case 5:
                uint local = 80 + ((((uint)((ulong)(at))) & 65535) >> 12) * 4, free = memory.Read32(p + 68), window = memory.Read32(p + local);
                if (!ValidPage(p, descriptor) || !ReadableGeneration(p) || memory.Read16(p + 4) != 6 || !(((ulong)(GetWide(p + 24))) == ((ulong)(group))) || memory.Read16(p + 52) != 80 || (memory.Read32(Arena + (AllocationUsed)) != 0 ? (free < count || window < count) : (free > 65536 - count || window > 4096 - count)))
                    return Fail(CpxError.Corrupt);
                memory.Write32(p + 68, memory.Read32(Arena + (AllocationUsed)) != 0 ? free - count : free + count);
                memory.Write32(p + local, memory.Read32(Arena + (AllocationUsed)) != 0 ? window - count : window + count);
                return StagePage(p, descriptor, 6);
            case 6:
                return ReadStaged(Anchor + 3 * Q, p, 7);
            case 7:
                if (!Control(p, 3, Anchor + 3 * Q) || !ReadableGeneration(p))
                    return Fail(CpxError.Corrupt);
                ulong available;
                if (memory.Read32(Arena + (AllocationUsed)) != 0 ? !Scalar64.Subtract(GetWide(p + 96), ((ulong)(count)), out available) : !Scalar64.Add(GetWide(p + 96), ((ulong)(count)), out available))
                    return Fail(CpxError.Corrupt);
                PutWide(p + 96, available);
                return StagePage(p, Anchor + 3 * Q, 8);
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
        uint p = Page(3);
        ulong at = RW(RefillScan);
        switch (memory.Read32(Arena + (Phase)))
        {
            case 0:
                return ReadStaged(Anchor + 8 * Q, p, 1);
            case 1:
                if (!Control(p, 4, Anchor + 8 * Q) || !ReadableGeneration(p) || memory.Read16(p + 64) > 64)
                    return Fail(CpxError.Corrupt);
                if (memory.Read16(p + 64) >= memory.Read32(Arena + (RefillMinimum)))
                    return Done();
                memory.Write32(Arena + (RefillCount), memory.Read16(p + 64));
                memory.Write32(Arena + (RefillWindows), 0);
                WW(RefillScan, ((ulong)(2097152u >> (int)memory.Read32(Arena + (UnitShift)))));
                return BeginTx(6, 2);
            case 2:
                if ((at) >= (Backup))
                    return memory.Read32(Arena + (RefillWindows)) == 0 ? Call(CpxOperation.AbortTransaction, 10) : Call(CpxOperation.CommitTransaction, 10);
                ulong group = (((ulong)(((uint)(((ulong)(at)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(at))) & 0xffff0000)));
                Scalar64.Subtract(RW(Units), group, out var remaining);
                if (!(((ulong)(group)) == 0) && ((uint)(((ulong)(remaining)) >> 32)) == 0 && ((uint)((ulong)(remaining))) < 18 * Q)
                {
                    WW(RefillScan, Backup);
                    return StepResult.Pending;
                }

                return ReadStaged(Bitmap(at), p, 3);
            case 3:
                if (!ControlBitmap(p, at, (((ulong)(((uint)(((ulong)(at)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(at))) & 0xffff0000)))) || !ReadableGeneration(p))
                    return Fail(CpxError.Corrupt);
                memory.Write32(Arena + (Phase), 4);
                return StepResult.Pending;
            case 4:
                ulong window = (((ulong)(((uint)(((ulong)(at)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(at))) & ~4095u)));
                uint q = Q;
                for (uint bit = ((uint)((ulong)(at))) & 4095; bit < 4096; bit += q)
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
                    if (memory.Read32(Arena + (RefillWindows)) == 0 || !(((ulong)(window)) == ((ulong)(RW(RefillFirstWindow)))))
                    {
                        if (memory.Read32(Arena + (RefillWindows)) == 2)
                            return Call(CpxOperation.CommitTransaction, 0);
                        memory.Write32(Arena + (RefillWindows), memory.Read32(Arena + (RefillWindows)) + 1);
                        WW(RefillFirstWindow, window);
                    }

                    WW(RefillScan, window + bit);
                    return ChangeAllocation(window + bit, Q, true, 5);
                }

                WW(RefillScan, window + 4096);
                memory.Write32(Arena + (Phase), 2);
                return StepResult.Pending;
            case 5:
                return ReleaseMetadata(RW(RefillScan), 6);
            case 6:
                memory.Write32(Arena + (RefillCount), memory.Read32(Arena + (RefillCount)) + 1);
                WW(RefillScan, RW(RefillScan) + Q);
                if (memory.Read32(Arena + (RefillCount)) >= 32)
                    return Call(CpxOperation.CommitTransaction, 11);
                memory.Write32(Arena + (Phase), 2);
                return StepResult.Pending;
            case 10:
                return memory.Read32(Arena + (RefillCount)) >= memory.Read32(Arena + (RefillMinimum)) ? Done() : Reject(CpxError.ReserveExhausted);
            case 11:
                return Done();
            default:
                return Fail(CpxError.Corrupt);
        }
    }
}
