namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    private const uint PruneRoot = 13900, PruneDepth = 13908, PruneLevel = 13912, PruneScan = 13916, PruneChanged = 13920, PruneSlot = 13924, PruneScanOnly = 13928, PruneObjectRoot = 14600;
    private uint PrunePath => Arena + 14000;

    private StepResult PruneTree(CpxTreeKind kind, ulong root, ulong owner, uint next, bool scanOnly = false)
    {
        uint cpxArenaBase = Arena;
        StartSeek(kind, root, owner, default, default, CpxSeek.Exact);
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(PruneRoot);
            ulong cpxWideValue = root;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(cpxArenaBase + (PruneChanged), 0);
        memory.Write32(cpxArenaBase + (PruneDepth), 0);
        memory.Write32(cpxArenaBase + (PruneScanOnly), scanOnly ? 1u : 0u);
        return Call(CpxOperation.PruneTree, next);
    }

    private StepResult PruneTreeStep()
    {
        uint cpxArenaBase = Arena;
        uint p = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(1)) * 4))), frame = (cpxArenaBase + 14000) + memory.Read32(cpxArenaBase + (PruneDepth)) * 16;
        switch (memory.Read32(cpxArenaBase + (Phase)))
        {
            case 0:
                if ((((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(PruneRoot)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(PruneRoot)) + 4)))))))) == 0))
                    return Done();
            {
                uint cpxWideAddress = (uint)(frame);
                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(PruneRoot)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(PruneRoot)) + 4))))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write32(frame + 12, uint.MaxValue);
                memory.Write32(cpxArenaBase + (Phase), 1);
                return StepResult.Pending;
            case 1:
                if (memory.Read32(cpxArenaBase + (PruneDepth)) >= 8 || !Allocatable(((((ulong)(memory.Read32(((uint)(frame))))) << 32 | ((ulong)(memory.Read32(((uint)(frame)) + 4)))))))
                    return Fail(CpxError.Corrupt);
                return ReadStaged(((((ulong)(memory.Read32(((uint)(frame))))) << 32 | ((ulong)(memory.Read32(((uint)(frame)) + 4))))), p, 2);
            case 2:
                uint level = memory.Read16(p + 50), count = memory.Read16(p + 48), width = level == 0 ? (memory.Read32(cpxArenaBase + (TreeKindAt)) == 0 ? 256u : memory.Read32(cpxArenaBase + (TreeKindAt)) == 1 ? 128u : 24u) : (memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u) + 8, expected = memory.Read32(frame + 12);
                if (!ValidPage(p, ((((ulong)(memory.Read32(((uint)(frame))))) << 32 | ((ulong)(memory.Read32(((uint)(frame)) + 4)))))) || !ReadableGeneration(p) || memory.Read16(p + 4) != SeekType(level) || !(((ulong)(((((ulong)(memory.Read32(((uint)(p + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 24)) + 4))))))) == ((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(SeekOwner)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(SeekOwner)) + 4))))))))) || !(((ulong)(((((ulong)(memory.Read32(((uint)(p + 40))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 40)) + 4))))))) == 0) || level >= 8 || (expected != uint.MaxValue && level != expected) || count > ((memory.Read32(cpxArenaBase + (PageBytes))) - 64) / width || (level != 0 && count == 0) || memory.Read16(p + 52) != count * width)
                    return Fail(CpxError.Corrupt);
                memory.Write32(cpxArenaBase + (PruneLevel), level);
                memory.Write32(cpxArenaBase + (PruneScan), 0);
                memory.Write32(frame + 12, level);
                memory.Write32(frame + 8, 0);
                memory.Write32(cpxArenaBase + (Phase), 3);
                return StepResult.Pending;
            case 3:
                uint lev = memory.Read32(cpxArenaBase + (PruneLevel)), cnt = memory.Read16(p + 48), sz = lev == 0 ? (memory.Read32(cpxArenaBase + (TreeKindAt)) == 0 ? 256u : memory.Read32(cpxArenaBase + (TreeKindAt)) == 1 ? 128u : 24u) : (memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u) + 8, scan = memory.Read32(cpxArenaBase + (PruneScan)), end = scan + 64;
                if (end > cnt)
                    end = cnt;
                for (; scan < end; scan++)
                {
                    uint r = p + 64 + scan * sz;
                    if (scan != 0 && SeekCompare(r - sz, ((((ulong)(memory.Read32(((uint)(r))))) << 32 | ((ulong)(memory.Read32(((uint)(r)) + 4))))), (memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u) == 16 ? ((((ulong)(memory.Read32(((uint)(r + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(r + 8)) + 4))))) : default) >= 0)
                        return Fail(CpxError.Corrupt);
                    if (lev != 0 && !Allocatable(((((ulong)(memory.Read32(((uint)(r + (memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u)))))) << 32 | ((ulong)(memory.Read32(((uint)(r + (memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u))) + 4)))))))
                        return Fail(CpxError.Corrupt);
                }

                memory.Write32(cpxArenaBase + (PruneScan), scan);
                if (scan < cnt)
                    return StepResult.Pending;
                if (lev != 0)
                {
                    {
                        uint cpxWideAddress = (uint)(frame + 16);
                        ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)(p + 64 + (memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u)))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 64 + (memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u))) + 4)))));
                        memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                        memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                    }

                    memory.Write32(frame + 28, lev - 1);
                    memory.Write32(cpxArenaBase + (PruneDepth), memory.Read32(cpxArenaBase + (PruneDepth)) + 1);
                    memory.Write32(cpxArenaBase + (Phase), 1);
                    return StepResult.Pending;
                }

                if (cnt == 0)
                {
                    memory.Write32(cpxArenaBase + (PruneChanged), 1);
                    return memory.Read32(cpxArenaBase + (PruneScanOnly)) != 0 ? Done() : ReleaseMetadata(((((ulong)(memory.Read32(((uint)(frame))))) << 32 | ((ulong)(memory.Read32(((uint)(frame)) + 4))))), 6);
                }

                memory.Write32(cpxArenaBase + (Phase), 4);
                return StepResult.Pending;
            case 4:
                if (memory.Read32(cpxArenaBase + (PruneDepth)) == 0)
                    return Done();
                memory.Write32(cpxArenaBase + (PruneDepth), memory.Read32(cpxArenaBase + (PruneDepth)) - 1);
                return ReadStaged(((((ulong)(memory.Read32(((uint)((cpxArenaBase + 14000) + memory.Read32(cpxArenaBase + (PruneDepth)) * 16))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 14000) + memory.Read32(cpxArenaBase + (PruneDepth)) * 16)) + 4))))), p, 5);
            case 5:
                if (!ValidPage(p, ((((ulong)(memory.Read32(((uint)(frame))))) << 32 | ((ulong)(memory.Read32(((uint)(frame)) + 4)))))) || !ReadableGeneration(p))
                    return Fail(CpxError.Corrupt);
                uint next = memory.Read32(frame + 8) + 1;
                if (next >= memory.Read16(p + 48))
                {
                    memory.Write32(cpxArenaBase + (Phase), 4);
                    return StepResult.Pending;
                }

                memory.Write32(frame + 8, next);
            {
                uint cpxWideAddress = (uint)(frame + 16);
                ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)(p + 64 + next * ((memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u) + 8) + (memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u)))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 64 + next * ((memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u) + 8) + (memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u))) + 4)))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write32(frame + 28, memory.Read32(frame + 12) - 1);
                memory.Write32(cpxArenaBase + (PruneDepth), memory.Read32(cpxArenaBase + (PruneDepth)) + 1);
                memory.Write32(cpxArenaBase + (Phase), 1);
                return StepResult.Pending;
            case 6:
                if (memory.Read32(cpxArenaBase + (PruneDepth)) == 0)
                {
                    {
                        uint cpxWideAddress = cpxArenaBase + (uint)(PruneRoot);
                        ulong cpxWideValue = default;
                        memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                        memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                    }

                    return Done();
                }

                memory.Write32(cpxArenaBase + (PruneDepth), memory.Read32(cpxArenaBase + (PruneDepth)) - 1);
                return ReadStaged(((((ulong)(memory.Read32(((uint)((cpxArenaBase + 14000) + memory.Read32(cpxArenaBase + (PruneDepth)) * 16))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 14000) + memory.Read32(cpxArenaBase + (PruneDepth)) * 16)) + 4))))), p, 7);
            case 7:
                if (!ValidPage(p, ((((ulong)(memory.Read32(((uint)(frame))))) << 32 | ((ulong)(memory.Read32(((uint)(frame)) + 4)))))) || !ReadableGeneration(p) || memory.Read16(p + 48) == 0)
                    return Fail(CpxError.Corrupt);
                if (memory.Read16(p + 48) == 1)
                    return ReleaseMetadata(((((ulong)(memory.Read32(((uint)(frame))))) << 32 | ((ulong)(memory.Read32(((uint)(frame)) + 4))))), 6);
                memory.Write32(cpxArenaBase + (PruneSlot), memory.Read32(frame + 8));
                memory.Write32(cpxArenaBase + (PruneScan), memory.Read32(cpxArenaBase + (PruneSlot)));
                Copy((cpxArenaBase + 9728), p + 64, (memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u));
                memory.Write32(cpxArenaBase + (Phase), 8);
                return StepResult.Pending;
            case 8:
                uint cursor = memory.Read32(cpxArenaBase + (PruneScan)), limit = cursor + 64, total = memory.Read16(p + 48), size = (memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u) + 8;
                if (limit > total - 1)
                    limit = total - 1;
                for (; cursor < limit; cursor++)
                    Copy(p + 64 + cursor * size, p + 64 + (cursor + 1) * size, size);
                memory.Write32(cpxArenaBase + (PruneScan), cursor);
                if (cursor < total - 1)
                    return StepResult.Pending;
                Clear(p + 64 + (total - 1) * size, size);
                if (memory.Read32(cpxArenaBase + (PruneSlot)) == 0)
                    Copy(p + 64, (cpxArenaBase + 9728), (memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u));
                memory.Write16(p + 48, total - 1);
                memory.Write16(p + 52, (total - 1) * size);
                memory.Write32(cpxArenaBase + (Phase), 9);
                return StepResult.Pending;
            case 9:
                return StagePage(p, ((((ulong)(memory.Read32(((uint)(frame))))) << 32 | ((ulong)(memory.Read32(((uint)(frame)) + 4))))), 10);
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
        uint cpxArenaBase = Arena;
        uint p = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(3)) * 4)));
        switch (memory.Read32(cpxArenaBase + (Phase)))
        {
            case 0:
                return ReadStaged(StateAddress, p, 1);
            case 1:
                if (!Control(p, 3, StateAddress) || !ReadableGeneration(p))
                    return Fail(CpxError.Corrupt);
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(PruneObjectRoot);
                ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)(p + 80))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 80)) + 4)))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                return PruneTree(CpxTreeKind.Object, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(PruneObjectRoot)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(PruneObjectRoot)) + 4)))))), default, 2, true);
            case 2:
                if (memory.Read32(cpxArenaBase + (PruneChanged)) == 0)
                    return Done();
                return BeginTx(27, 3);
            case 3:
                return PruneTree(CpxTreeKind.Object, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(PruneObjectRoot)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(PruneObjectRoot)) + 4)))))), default, 4);
            case 4:
                if (memory.Read32(cpxArenaBase + (PruneChanged)) == 0 || (((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(PruneRoot)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(PruneRoot)) + 4)))))))) == 0))
                    return Fail(CpxError.Corrupt);
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(PruneObjectRoot);
                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(PruneRoot)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(PruneRoot)) + 4))))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                return ReadStaged(StateAddress, p, 5);
            case 5:
                if (!Control(p, 3, StateAddress) || !ReadableGeneration(p))
                    return Fail(CpxError.Corrupt);
            {
                uint cpxWideAddress = (uint)(p + 80);
                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(PruneObjectRoot)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(PruneObjectRoot)) + 4))))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                return StagePage(p, StateAddress, 6);
            case 6:
                return Call(CpxOperation.CommitTransaction, 0);
            default:
                return Fail(CpxError.Corrupt);
        }
    }
}