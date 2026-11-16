namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    private const uint MutationRoot = 9600, MutationMode = 9608, MutationDepth = 9612, MutationSlot = 9616, MutationCount = 9620, MutationLevel = 9624, MutationScan = 9628, MutationSibling = 9632, MutationLeft = 9640, MutationCheckDepth = 9644, SaveSource = 10080, SaveRoot = 10088;
    private uint MutationRecord => Arena + 9728;
    private uint SaveRecord => Arena + 10112;

    private uint MutationWidth
    {
        get
        {
            uint cpxArenaBase = Arena;
            return memory.Read32(cpxArenaBase + (MutationLevel)) == 0 ? (memory.Read32(cpxArenaBase + (TreeKindAt)) == 0 ? 256u : memory.Read32(cpxArenaBase + (TreeKindAt)) == 1 ? 128u : 24u) : (memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u) + 8;
        }
    }

    // mode: insert=0, replace=1, delete=2. Input is copied before yielding.
    private StepResult MutateTree(CpxTreeKind kind, ulong root, ulong owner, uint record, uint mode, uint next)
    {
        uint cpxArenaBase = Arena;
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(MutationRoot);
            ulong cpxWideValue = root;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(cpxArenaBase + (MutationMode), mode);
        Copy((cpxArenaBase + 9728), record, kind == CpxTreeKind.Object ? 256u : kind == CpxTreeKind.Directory ? 128u : 24u);
        StartSeek(kind, root, owner, ((((ulong)(memory.Read32(((uint)(record))))) << 32 | ((ulong)(memory.Read32(((uint)(record)) + 4))))), kind == CpxTreeKind.Directory ? ((((ulong)(memory.Read32(((uint)(record + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 8)) + 4))))) : default, CpxSeek.Exact);
        return Call(CpxOperation.TreeMutation, next);
    }

    private void TreeHeader(uint page, ulong address, uint count, uint level)
    {
        uint cpxArenaBase = Arena;
        Header(page, SeekType(level), address, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)) + 4)))))) + 1);
        {
            uint cpxWideAddress = (uint)(page + 24);
            ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(SeekOwner)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(SeekOwner)) + 4))))));
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write16(page + 48, count);
        memory.Write16(page + 50, level);
        memory.Write16(page + 52, count * (level == 0 ? (memory.Read32(cpxArenaBase + (TreeKindAt)) == 0 ? 256u : memory.Read32(cpxArenaBase + (TreeKindAt)) == 1 ? 128u : 24u) : (memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u) + 8));
    }

    private StepResult TreeMutationStep()
    {
        uint cpxArenaBase = Arena;
        uint p = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(1)) * 4))), source = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(0)) * 4))), right = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(2)) * 4))), width = (memory.Read32(cpxArenaBase + (MutationLevel)) == 0 ? (memory.Read32(cpxArenaBase + (TreeKindAt)) == 0 ? 256u : memory.Read32(cpxArenaBase + (TreeKindAt)) == 1 ? 128u : 24u) : (memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u) + 8), count = memory.Read32(cpxArenaBase + (MutationCount)), slot = memory.Read32(cpxArenaBase + (MutationSlot));
        switch (memory.Read32(cpxArenaBase + (Phase)))
        {
            case 0:
                if ((((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(MutationRoot)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(MutationRoot)) + 4)))))))) == 0))
                {
                    if (memory.Read32(cpxArenaBase + (MutationMode)) != 0)
                        return Fail(CpxError.Corrupt);
                    return TakeMetadata(20);
                }

                return Call(CpxOperation.ReadTree, 1);
            case 1:
                if (memory.Read32(cpxArenaBase + (MutationMode)) == 0 && (memory.Read32(cpxArenaBase + (SeekFound)) != 0))
                    return Reject(CpxError.AlreadyExists);
                if (memory.Read32(cpxArenaBase + (MutationMode)) != 0 && !(memory.Read32(cpxArenaBase + (SeekFound)) != 0))
                    return Fail(CpxError.Corrupt);
                memory.Write32(cpxArenaBase + (MutationDepth), memory.Read32(cpxArenaBase + (SeekDepth)));
                memory.Write32(cpxArenaBase + (MutationLevel), 0);
                memory.Write32(cpxArenaBase + (MutationCount), memory.Read16(p + 48));
                memory.Write32(cpxArenaBase + (MutationScan), 0);
                memory.Write32(cpxArenaBase + (MutationSlot), 0);
                if (memory.Read32(cpxArenaBase + (MutationMode)) != 0)
                {
                    memory.Write32(cpxArenaBase + (MutationSlot), memory.Read32(cpxArenaBase + (SeekSlot)));
                    memory.Write32(cpxArenaBase + (Phase), 3);
                }
                else if (memory.Read32(cpxArenaBase + (SeekDepth)) == 7 && memory.Read16(p + 48) == ((memory.Read32(cpxArenaBase + (PageBytes))) - 64) / (memory.Read32(cpxArenaBase + (TreeKindAt)) == 0 ? 256u : memory.Read32(cpxArenaBase + (TreeKindAt)) == 1 ? 128u : 24u))
                {
                    memory.Write32(cpxArenaBase + (MutationCheckDepth), 6);
                    memory.Write32(cpxArenaBase + (Phase), 24);
                }
                else
                    memory.Write32(cpxArenaBase + (Phase), 2);
                return StepResult.Pending;
            case 2:
                uint scan = memory.Read32(cpxArenaBase + (MutationScan)), end = scan + 64;
                if (end > count)
                    end = count;
                for (; scan < end; scan++)
                    if (SeekCompare(p + 64 + scan * width, ((((ulong)(memory.Read32(((uint)((cpxArenaBase + 9728)))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 9728))) + 4))))), (memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u) == 16 ? ((((ulong)(memory.Read32(((uint)((cpxArenaBase + 9728) + 8))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 9728) + 8)) + 4))))) : default) < 0)
                        memory.Write32(cpxArenaBase + (MutationSlot), scan + 1);
                memory.Write32(cpxArenaBase + (MutationScan), scan);
                if (scan == count)
                    memory.Write32(cpxArenaBase + (Phase), 3);
                return StepResult.Pending;
            case 3:
                if (memory.Read32(cpxArenaBase + (MutationMode)) == 1)
                {
                    Copy(p + 64 + slot * width, (cpxArenaBase + 9728), width);
                    return StagePage(p, ((((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 768) + memory.Read32(cpxArenaBase + (MutationDepth)) * 16))))) << 32 | ((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 768) + memory.Read32(cpxArenaBase + (MutationDepth)) * 16)) + 4))))), 19);
                }

                if (memory.Read32(cpxArenaBase + (MutationMode)) == 2)
                {
                    memory.Write32(cpxArenaBase + (MutationScan), slot);
                    memory.Write32(cpxArenaBase + (Phase), 4);
                    return StepResult.Pending;
                }

                Copy(source, p, (memory.Read32(cpxArenaBase + (PageBytes))));
                if (count < ((memory.Read32(cpxArenaBase + (PageBytes))) - 64) / width)
                {
                    memory.Write32(cpxArenaBase + (MutationScan), count);
                    memory.Write32(cpxArenaBase + (Phase), 5);
                    return StepResult.Pending;
                }

                if (memory.Read32(cpxArenaBase + (MutationDepth)) == 0 && memory.Read32(cpxArenaBase + (MutationLevel)) == 7)
                    return Reject(CpxError.TreeTooDeep);
                return TakeMetadata(6);
            case 4:
                uint delete = memory.Read32(cpxArenaBase + (MutationScan)), limit = delete + 64;
                if (limit > count - 1)
                    limit = count - 1;
                for (; delete < limit; delete++)
                    Copy(p + 64 + delete * width, p + 64 + (delete + 1) * width, width);
                memory.Write32(cpxArenaBase + (MutationScan), delete);
                if (delete < count - 1)
                    return StepResult.Pending;
                Clear(p + 64 + (count - 1) * width, width);
                memory.Write16(p + 48, count - 1);
                memory.Write16(p + 52, (count - 1) * width);
                memory.Write32(cpxArenaBase + (Phase), 18);
                return StepResult.Pending;
            case 5:
                uint move = memory.Read32(cpxArenaBase + (MutationScan)), stop = move > 64 ? move - 64 : 0;
                if (stop < slot)
                    stop = slot;
                for (; move > stop; move--)
                    Copy(p + 64 + move * width, source + 64 + (move - 1) * width, width);
                memory.Write32(cpxArenaBase + (MutationScan), move);
                if (move > slot)
                    return StepResult.Pending;
                Copy(p + 64 + slot * width, (cpxArenaBase + 9728), width);
                memory.Write16(p + 48, count + 1);
                memory.Write16(p + 52, (count + 1) * width);
                memory.Write32(cpxArenaBase + (Phase), 18);
                return StepResult.Pending;
            case 6:
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(MutationSibling);
                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(MetadataResult)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(MetadataResult)) + 4))))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write32(cpxArenaBase + (MutationLeft), (count + 1) >> 1);
                memory.Write32(cpxArenaBase + (MutationScan), 0);
                TreeHeader(p, ((((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 768) + memory.Read32(cpxArenaBase + (MutationDepth)) * 16))))) << 32 | ((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 768) + memory.Read32(cpxArenaBase + (MutationDepth)) * 16)) + 4))))), memory.Read32(cpxArenaBase + (MutationLeft)), memory.Read32(cpxArenaBase + (MutationLevel)));
                TreeHeader(right, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(MutationSibling)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(MutationSibling)) + 4)))))), count + 1 - memory.Read32(cpxArenaBase + (MutationLeft)), memory.Read32(cpxArenaBase + (MutationLevel)));
                memory.Write32(cpxArenaBase + (Phase), 7);
                return StepResult.Pending;
            case 7:
                uint item = memory.Read32(cpxArenaBase + (MutationScan)), until = item + 64;
                if (until > count + 1)
                    until = count + 1;
                for (; item < until; item++)
                {
                    uint from = item == slot ? (cpxArenaBase + 9728) : source + 64 + (item < slot ? item : item - 1) * width;
                    uint to = item < memory.Read32(cpxArenaBase + (MutationLeft)) ? p + 64 + item * width : right + 64 + (item - memory.Read32(cpxArenaBase + (MutationLeft))) * width;
                    Copy(to, from, width);
                }

                memory.Write32(cpxArenaBase + (MutationScan), item);
                if (item < count + 1)
                    return StepResult.Pending;
                Copy((cpxArenaBase + 9728), right + 64, (memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u));
            {
                uint cpxWideAddress = (uint)((cpxArenaBase + 9728) + (memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u));
                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(MutationSibling)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(MutationSibling)) + 4))))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write32(cpxArenaBase + (Phase), 8);
                return StepResult.Pending;
            case 8:
                return StagePage(p, ((((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 768) + memory.Read32(cpxArenaBase + (MutationDepth)) * 16))))) << 32 | ((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 768) + memory.Read32(cpxArenaBase + (MutationDepth)) * 16)) + 4))))), 9);
            case 9:
                return StagePage(right, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(MutationSibling)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(MutationSibling)) + 4)))))), 10);
            case 10:
                if (memory.Read32(cpxArenaBase + (MutationDepth)) == 0)
                    return TakeMetadata(12);
                memory.Write32(cpxArenaBase + (MutationDepth), memory.Read32(cpxArenaBase + (MutationDepth)) - 1);
                return ReadStaged(((((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 768) + memory.Read32(cpxArenaBase + (MutationDepth)) * 16))))) << 32 | ((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 768) + memory.Read32(cpxArenaBase + (MutationDepth)) * 16)) + 4))))), p, 11);
            case 11:
                uint frame = ((cpxArenaBase + CpxArena.MountBytes) + 768) + memory.Read32(cpxArenaBase + (MutationDepth)) * 16;
                if (!ValidPage(p, ((((ulong)(memory.Read32(((uint)(frame))))) << 32 | ((ulong)(memory.Read32(((uint)(frame)) + 4)))))) || !ReadableGeneration(p) || memory.Read16(p + 50) != memory.Read32(frame + 12))
                    return Fail(CpxError.Corrupt);
                memory.Write32(cpxArenaBase + (MutationLevel), memory.Read16(p + 50));
                memory.Write32(cpxArenaBase + (MutationCount), memory.Read16(p + 48));
                memory.Write32(cpxArenaBase + (MutationSlot), memory.Read32(frame + 8) + 1);
                memory.Write32(cpxArenaBase + (Phase), 3);
                return StepResult.Pending;
            case 12:
                ulong root = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(MetadataResult)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(MetadataResult)) + 4))))));
                TreeHeader(p, root, 2, memory.Read32(cpxArenaBase + (MutationLevel)) + 1);
            {
                uint cpxWideAddress = (uint)(p + 64 + (memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u));
                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(MutationRoot)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(MutationRoot)) + 4))))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                Copy(p + 64 + (memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u) + 8, (cpxArenaBase + 9728), (memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u) + 8);
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(MutationRoot);
                ulong cpxWideValue = root;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                return StagePage(p, root, 19);
            case 18:
                return StagePage(p, ((((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 768) + memory.Read32(cpxArenaBase + (MutationDepth)) * 16))))) << 32 | ((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 768) + memory.Read32(cpxArenaBase + (MutationDepth)) * 16)) + 4))))), 19);
            case 19:
                return Done();
            case 20:
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(MutationRoot);
                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(MetadataResult)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(MetadataResult)) + 4))))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                TreeHeader(p, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(MutationRoot)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(MutationRoot)) + 4)))))), 1, 0);
                Copy(p + 64, (cpxArenaBase + 9728), (memory.Read32(cpxArenaBase + (TreeKindAt)) == 0 ? 256u : memory.Read32(cpxArenaBase + (TreeKindAt)) == 1 ? 128u : 24u));
                return StagePage(p, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(MutationRoot)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(MutationRoot)) + 4)))))), 19);
            case 24:
                return ReadStaged(((((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 768) + memory.Read32(cpxArenaBase + (MutationCheckDepth)) * 16))))) << 32 | ((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 768) + memory.Read32(cpxArenaBase + (MutationCheckDepth)) * 16)) + 4))))), right, 25);
            case 25:
                uint checkedFrame = ((cpxArenaBase + CpxArena.MountBytes) + 768) + memory.Read32(cpxArenaBase + (MutationCheckDepth)) * 16;
                if (!ValidPage(right, ((((ulong)(memory.Read32(((uint)(checkedFrame))))) << 32 | ((ulong)(memory.Read32(((uint)(checkedFrame)) + 4)))))) || !ReadableGeneration(right) || memory.Read16(right + 50) != memory.Read32(checkedFrame + 12))
                    return Fail(CpxError.Corrupt);
                if (memory.Read16(right + 48) < ((memory.Read32(cpxArenaBase + (PageBytes))) - 64) / ((memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u) + 8))
                {
                    memory.Write32(cpxArenaBase + (Phase), 2);
                    return StepResult.Pending;
                }

                if (memory.Read32(cpxArenaBase + (MutationCheckDepth)) == 0)
                    return Reject(CpxError.TreeTooDeep);
                memory.Write32(cpxArenaBase + (MutationCheckDepth), memory.Read32(cpxArenaBase + (MutationCheckDepth)) - 1);
                memory.Write32(cpxArenaBase + (Phase), 24);
                return StepResult.Pending;
            default:
                return Fail(CpxError.Corrupt);
        }
    }

    private StepResult SaveObject(uint record, uint next)
    {
        Copy((Arena + 10112), record, 256);
        return Call(CpxOperation.SaveObject, next);
    }

    private StepResult SaveObjectStep()
    {
        uint cpxArenaBase = Arena;
        switch (memory.Read32(cpxArenaBase + (Phase)))
        {
            case 0:
                return ReadStaged(StateAddress, (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(3)) * 4))), 1);
            case 1:
                if (!Control((memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(3)) * 4))), 3, StateAddress) || !ReadableGeneration((memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(3)) * 4)))))
                    return Fail(CpxError.Corrupt);
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(SaveRoot);
                ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)((memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(3)) * 4))) + 80))))) << 32 | ((ulong)(memory.Read32(((uint)((memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(3)) * 4))) + 80)) + 4)))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                return MutateTree(CpxTreeKind.Object, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(SaveRoot)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(SaveRoot)) + 4)))))), default, (cpxArenaBase + 10112), 1, 2);
            case 2:
                return Done();
            default:
                return Fail(CpxError.Corrupt);
        }
    }
}