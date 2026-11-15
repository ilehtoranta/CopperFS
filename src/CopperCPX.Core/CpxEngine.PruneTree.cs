namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    private const uint PruneRoot = 13900, PruneDepth = 13908, PruneLevel = 13912, PruneScan = 13916, PruneChanged = 13920, PruneSlot = 13924, PruneScanOnly = 13928, PruneObjectRoot = 14600;
    private uint PrunePath => Arena + 14000;

    private StepResult PruneTree(CpxTreeKind kind, ulong root, ulong owner, uint next, bool scanOnly = false)
    {
        StartSeek(kind, root, owner, default, default, CpxSeek.Exact);
        WW(PruneRoot, root);
        memory.Write32(Arena + (PruneChanged), 0);
        memory.Write32(Arena + (PruneDepth), 0);
        memory.Write32(Arena + (PruneScanOnly), scanOnly ? 1u : 0u);
        return Call(CpxOperation.PruneTree, next);
    }

    private StepResult PruneTreeStep()
    {
        uint p = Page(1), frame = PrunePath + memory.Read32(Arena + (PruneDepth)) * 16;
        switch (memory.Read32(Arena + (Phase)))
        {
            case 0:
                if ((((ulong)(RW(PruneRoot))) == 0))
                    return Done();
                PutWide(frame, RW(PruneRoot));
                memory.Write32(frame + 12, uint.MaxValue);
                memory.Write32(Arena + (Phase), 1);
                return StepResult.Pending;
            case 1:
                if (memory.Read32(Arena + (PruneDepth)) >= 8 || !Allocatable(GetWide(frame)))
                    return Fail(CpxError.Corrupt);
                return ReadStaged(GetWide(frame), p, 2);
            case 2:
                uint level = memory.Read16(p + 50), count = memory.Read16(p + 48), width = level == 0 ? SeekLeafBytes : SeekKeyBytes + 8, expected = memory.Read32(frame + 12);
                if (!ValidPage(p, GetWide(frame)) || !ReadableGeneration(p) || memory.Read16(p + 4) != SeekType(level) || !(((ulong)(GetWide(p + 24))) == ((ulong)(RW(SeekOwner)))) || !(((ulong)(GetWide(p + 40))) == 0) || level >= 8 || (expected != uint.MaxValue && level != expected) || count > (P - 64) / width || (level != 0 && count == 0) || memory.Read16(p + 52) != count * width)
                    return Fail(CpxError.Corrupt);
                memory.Write32(Arena + (PruneLevel), level);
                memory.Write32(Arena + (PruneScan), 0);
                memory.Write32(frame + 12, level);
                memory.Write32(frame + 8, 0);
                memory.Write32(Arena + (Phase), 3);
                return StepResult.Pending;
            case 3:
                uint lev = memory.Read32(Arena + (PruneLevel)), cnt = memory.Read16(p + 48), sz = lev == 0 ? SeekLeafBytes : SeekKeyBytes + 8, scan = memory.Read32(Arena + (PruneScan)), end = scan + 64;
                if (end > cnt)
                    end = cnt;
                for (; scan < end; scan++)
                {
                    uint r = p + 64 + scan * sz;
                    if (scan != 0 && SeekCompare(r - sz, GetWide(r), SeekKeyBytes == 16 ? GetWide(r + 8) : default) >= 0)
                        return Fail(CpxError.Corrupt);
                    if (lev != 0 && !Allocatable(GetWide(r + SeekKeyBytes)))
                        return Fail(CpxError.Corrupt);
                }

                memory.Write32(Arena + (PruneScan), scan);
                if (scan < cnt)
                    return StepResult.Pending;
                if (lev != 0)
                {
                    PutWide(frame + 16, GetWide(p + 64 + SeekKeyBytes));
                    memory.Write32(frame + 28, lev - 1);
                    memory.Write32(Arena + (PruneDepth), memory.Read32(Arena + (PruneDepth)) + 1);
                    memory.Write32(Arena + (Phase), 1);
                    return StepResult.Pending;
                }

                if (cnt == 0)
                {
                    memory.Write32(Arena + (PruneChanged), 1);
                    return memory.Read32(Arena + (PruneScanOnly)) != 0 ? Done() : ReleaseMetadata(GetWide(frame), 6);
                }

                memory.Write32(Arena + (Phase), 4);
                return StepResult.Pending;
            case 4:
                if (memory.Read32(Arena + (PruneDepth)) == 0)
                    return Done();
                memory.Write32(Arena + (PruneDepth), memory.Read32(Arena + (PruneDepth)) - 1);
                return ReadStaged(GetWide(PrunePath + memory.Read32(Arena + (PruneDepth)) * 16), p, 5);
            case 5:
                if (!ValidPage(p, GetWide(frame)) || !ReadableGeneration(p))
                    return Fail(CpxError.Corrupt);
                uint next = memory.Read32(frame + 8) + 1;
                if (next >= memory.Read16(p + 48))
                {
                    memory.Write32(Arena + (Phase), 4);
                    return StepResult.Pending;
                }

                memory.Write32(frame + 8, next);
                PutWide(frame + 16, GetWide(p + 64 + next * (SeekKeyBytes + 8) + SeekKeyBytes));
                memory.Write32(frame + 28, memory.Read32(frame + 12) - 1);
                memory.Write32(Arena + (PruneDepth), memory.Read32(Arena + (PruneDepth)) + 1);
                memory.Write32(Arena + (Phase), 1);
                return StepResult.Pending;
            case 6:
                if (memory.Read32(Arena + (PruneDepth)) == 0)
                {
                    WW(PruneRoot, default);
                    return Done();
                }

                memory.Write32(Arena + (PruneDepth), memory.Read32(Arena + (PruneDepth)) - 1);
                return ReadStaged(GetWide(PrunePath + memory.Read32(Arena + (PruneDepth)) * 16), p, 7);
            case 7:
                if (!ValidPage(p, GetWide(frame)) || !ReadableGeneration(p) || memory.Read16(p + 48) == 0)
                    return Fail(CpxError.Corrupt);
                if (memory.Read16(p + 48) == 1)
                    return ReleaseMetadata(GetWide(frame), 6);
                memory.Write32(Arena + (PruneSlot), memory.Read32(frame + 8));
                memory.Write32(Arena + (PruneScan), memory.Read32(Arena + (PruneSlot)));
                Copy(MutationRecord, p + 64, SeekKeyBytes);
                memory.Write32(Arena + (Phase), 8);
                return StepResult.Pending;
            case 8:
                uint cursor = memory.Read32(Arena + (PruneScan)), limit = cursor + 64, total = memory.Read16(p + 48), size = SeekKeyBytes + 8;
                if (limit > total - 1)
                    limit = total - 1;
                for (; cursor < limit; cursor++)
                    Copy(p + 64 + cursor * size, p + 64 + (cursor + 1) * size, size);
                memory.Write32(Arena + (PruneScan), cursor);
                if (cursor < total - 1)
                    return StepResult.Pending;
                Clear(p + 64 + (total - 1) * size, size);
                if (memory.Read32(Arena + (PruneSlot)) == 0)
                    Copy(p + 64, MutationRecord, SeekKeyBytes);
                memory.Write16(p + 48, total - 1);
                memory.Write16(p + 52, (total - 1) * size);
                memory.Write32(Arena + (Phase), 9);
                return StepResult.Pending;
            case 9:
                return StagePage(p, GetWide(frame), 10);
            case 10:
                return Done();
            default:
                return Fail(CpxError.Corrupt);
        }
    }

    // Empty nodes remain reachable if power fails after the last orphan is
    // dequeued. Mount/flush also run this scan, so these pages are recoverable
    // without another durable queue record or any unbounded in-memory list.
    private StepResult PruneObjectsStep()
    {
        uint p = Page(3);
        switch (memory.Read32(Arena + (Phase)))
        {
            case 0:
                return ReadStaged(Anchor + 3 * Q, p, 1);
            case 1:
                if (!Control(p, 3, Anchor + 3 * Q) || !ReadableGeneration(p))
                    return Fail(CpxError.Corrupt);
                WW(PruneObjectRoot, GetWide(p + 80));
                return PruneTree(CpxTreeKind.Object, RW(PruneObjectRoot), default, 2, true);
            case 2:
                if (memory.Read32(Arena + (PruneChanged)) == 0)
                    return Done();
                return BeginTx(27, 3);
            case 3:
                return PruneTree(CpxTreeKind.Object, RW(PruneObjectRoot), default, 4);
            case 4:
                if (memory.Read32(Arena + (PruneChanged)) == 0 || (((ulong)(RW(PruneRoot))) == 0))
                    return Fail(CpxError.Corrupt);
                WW(PruneObjectRoot, RW(PruneRoot));
                return ReadStaged(Anchor + 3 * Q, p, 5);
            case 5:
                if (!Control(p, 3, Anchor + 3 * Q) || !ReadableGeneration(p))
                    return Fail(CpxError.Corrupt);
                PutWide(p + 80, RW(PruneObjectRoot));
                return StagePage(p, Anchor + 3 * Q, 6);
            case 6:
                return Call(CpxOperation.CommitTransaction, 0);
            default:
                return Fail(CpxError.Corrupt);
        }
    }
}
