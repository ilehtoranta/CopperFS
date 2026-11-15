namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    private const uint MutationRoot = 9600, MutationMode = 9608, MutationDepth = 9612, MutationSlot = 9616, MutationCount = 9620, MutationLevel = 9624, MutationScan = 9628, MutationSibling = 9632, MutationLeft = 9640, MutationCheckDepth = 9644, SaveSource = 10080, SaveRoot = 10088;
    private uint MutationRecord => Arena + 9728;
    private uint SaveRecord => Arena + 10112;
    private uint MutationWidth => memory.Read32(Arena + (MutationLevel)) == 0 ? SeekLeafBytes : SeekKeyBytes + 8;

    // mode: insert=0, replace=1, delete=2. Input is copied before yielding.
    private StepResult MutateTree(CpxTreeKind kind, ulong root, ulong owner, uint record, uint mode, uint next)
    {
        WW(MutationRoot, root);
        memory.Write32(Arena + (MutationMode), mode);
        Copy(MutationRecord, record, kind == CpxTreeKind.Object ? 256u : kind == CpxTreeKind.Directory ? 128u : 24u);
        StartSeek(kind, root, owner, GetWide(record), kind == CpxTreeKind.Directory ? GetWide(record + 8) : default, CpxSeek.Exact);
        return Call(CpxOperation.TreeMutation, next);
    }

    private void TreeHeader(uint page, ulong address, uint count, uint level)
    {
        Header(page, SeekType(level), address, RW(Generation) + 1);
        PutWide(page + 24, RW(SeekOwner));
        memory.Write16(page + 48, count);
        memory.Write16(page + 50, level);
        memory.Write16(page + 52, count * (level == 0 ? SeekLeafBytes : SeekKeyBytes + 8));
    }

    private StepResult TreeMutationStep()
    {
        uint p = Page(1), source = Page(0), right = Page(2), width = MutationWidth, count = memory.Read32(Arena + (MutationCount)), slot = memory.Read32(Arena + (MutationSlot));
        switch (memory.Read32(Arena + (Phase)))
        {
            case 0:
                if ((((ulong)(RW(MutationRoot))) == 0))
                {
                    if (memory.Read32(Arena + (MutationMode)) != 0)
                        return Fail(CpxError.Corrupt);
                    return TakeMetadata(20);
                }

                return Call(CpxOperation.ReadTree, 1);
            case 1:
                if (memory.Read32(Arena + (MutationMode)) == 0 && TreeFound)
                    return Reject(CpxError.AlreadyExists);
                if (memory.Read32(Arena + (MutationMode)) != 0 && !TreeFound)
                    return Fail(CpxError.Corrupt);
                memory.Write32(Arena + (MutationDepth), memory.Read32(Arena + (SeekDepth)));
                memory.Write32(Arena + (MutationLevel), 0);
                memory.Write32(Arena + (MutationCount), memory.Read16(p + 48));
                memory.Write32(Arena + (MutationScan), 0);
                memory.Write32(Arena + (MutationSlot), 0);
                if (memory.Read32(Arena + (MutationMode)) != 0)
                {
                    memory.Write32(Arena + (MutationSlot), memory.Read32(Arena + (SeekSlot)));
                    memory.Write32(Arena + (Phase), 3);
                }
                else if (memory.Read32(Arena + (SeekDepth)) == 7 && memory.Read16(p + 48) == (P - 64) / SeekLeafBytes)
                {
                    memory.Write32(Arena + (MutationCheckDepth), 6);
                    memory.Write32(Arena + (Phase), 24);
                }
                else
                    memory.Write32(Arena + (Phase), 2);
                return StepResult.Pending;
            case 2:
                uint scan = memory.Read32(Arena + (MutationScan)), end = scan + 64;
                if (end > count)
                    end = count;
                for (; scan < end; scan++)
                    if (SeekCompare(p + 64 + scan * width, GetWide(MutationRecord), SeekKeyBytes == 16 ? GetWide(MutationRecord + 8) : default) < 0)
                        memory.Write32(Arena + (MutationSlot), scan + 1);
                memory.Write32(Arena + (MutationScan), scan);
                if (scan == count)
                    memory.Write32(Arena + (Phase), 3);
                return StepResult.Pending;
            case 3:
                if (memory.Read32(Arena + (MutationMode)) == 1)
                {
                    Copy(p + 64 + slot * width, MutationRecord, width);
                    return StagePage(p, GetWide(SeekPath + memory.Read32(Arena + (MutationDepth)) * 16), 19);
                }

                if (memory.Read32(Arena + (MutationMode)) == 2)
                {
                    memory.Write32(Arena + (MutationScan), slot);
                    memory.Write32(Arena + (Phase), 4);
                    return StepResult.Pending;
                }

                Copy(source, p, P);
                if (count < (P - 64) / width)
                {
                    memory.Write32(Arena + (MutationScan), count);
                    memory.Write32(Arena + (Phase), 5);
                    return StepResult.Pending;
                }

                if (memory.Read32(Arena + (MutationDepth)) == 0 && memory.Read32(Arena + (MutationLevel)) == 7)
                    return Reject(CpxError.TreeTooDeep);
                return TakeMetadata(6);
            case 4:
                uint delete = memory.Read32(Arena + (MutationScan)), limit = delete + 64;
                if (limit > count - 1)
                    limit = count - 1;
                for (; delete < limit; delete++)
                    Copy(p + 64 + delete * width, p + 64 + (delete + 1) * width, width);
                memory.Write32(Arena + (MutationScan), delete);
                if (delete < count - 1)
                    return StepResult.Pending;
                Clear(p + 64 + (count - 1) * width, width);
                memory.Write16(p + 48, count - 1);
                memory.Write16(p + 52, (count - 1) * width);
                memory.Write32(Arena + (Phase), 18);
                return StepResult.Pending;
            case 5:
                uint move = memory.Read32(Arena + (MutationScan)), stop = move > 64 ? move - 64 : 0;
                if (stop < slot)
                    stop = slot;
                for (; move > stop; move--)
                    Copy(p + 64 + move * width, source + 64 + (move - 1) * width, width);
                memory.Write32(Arena + (MutationScan), move);
                if (move > slot)
                    return StepResult.Pending;
                Copy(p + 64 + slot * width, MutationRecord, width);
                memory.Write16(p + 48, count + 1);
                memory.Write16(p + 52, (count + 1) * width);
                memory.Write32(Arena + (Phase), 18);
                return StepResult.Pending;
            case 6:
                WW(MutationSibling, RW(MetadataResult));
                memory.Write32(Arena + (MutationLeft), (count + 1) >> 1);
                memory.Write32(Arena + (MutationScan), 0);
                TreeHeader(p, GetWide(SeekPath + memory.Read32(Arena + (MutationDepth)) * 16), memory.Read32(Arena + (MutationLeft)), memory.Read32(Arena + (MutationLevel)));
                TreeHeader(right, RW(MutationSibling), count + 1 - memory.Read32(Arena + (MutationLeft)), memory.Read32(Arena + (MutationLevel)));
                memory.Write32(Arena + (Phase), 7);
                return StepResult.Pending;
            case 7:
                uint item = memory.Read32(Arena + (MutationScan)), until = item + 64;
                if (until > count + 1)
                    until = count + 1;
                for (; item < until; item++)
                {
                    uint from = item == slot ? MutationRecord : source + 64 + (item < slot ? item : item - 1) * width;
                    uint to = item < memory.Read32(Arena + (MutationLeft)) ? p + 64 + item * width : right + 64 + (item - memory.Read32(Arena + (MutationLeft))) * width;
                    Copy(to, from, width);
                }

                memory.Write32(Arena + (MutationScan), item);
                if (item < count + 1)
                    return StepResult.Pending;
                Copy(MutationRecord, right + 64, SeekKeyBytes);
                PutWide(MutationRecord + SeekKeyBytes, RW(MutationSibling));
                memory.Write32(Arena + (Phase), 8);
                return StepResult.Pending;
            case 8:
                return StagePage(p, GetWide(SeekPath + memory.Read32(Arena + (MutationDepth)) * 16), 9);
            case 9:
                return StagePage(right, RW(MutationSibling), 10);
            case 10:
                if (memory.Read32(Arena + (MutationDepth)) == 0)
                    return TakeMetadata(12);
                memory.Write32(Arena + (MutationDepth), memory.Read32(Arena + (MutationDepth)) - 1);
                return ReadStaged(GetWide(SeekPath + memory.Read32(Arena + (MutationDepth)) * 16), p, 11);
            case 11:
                uint frame = SeekPath + memory.Read32(Arena + (MutationDepth)) * 16;
                if (!ValidPage(p, GetWide(frame)) || !ReadableGeneration(p) || memory.Read16(p + 50) != memory.Read32(frame + 12))
                    return Fail(CpxError.Corrupt);
                memory.Write32(Arena + (MutationLevel), memory.Read16(p + 50));
                memory.Write32(Arena + (MutationCount), memory.Read16(p + 48));
                memory.Write32(Arena + (MutationSlot), memory.Read32(frame + 8) + 1);
                memory.Write32(Arena + (Phase), 3);
                return StepResult.Pending;
            case 12:
                ulong root = RW(MetadataResult);
                TreeHeader(p, root, 2, memory.Read32(Arena + (MutationLevel)) + 1);
                PutWide(p + 64 + SeekKeyBytes, RW(MutationRoot));
                Copy(p + 64 + SeekKeyBytes + 8, MutationRecord, SeekKeyBytes + 8);
                WW(MutationRoot, root);
                return StagePage(p, root, 19);
            case 18:
                return StagePage(p, GetWide(SeekPath + memory.Read32(Arena + (MutationDepth)) * 16), 19);
            case 19:
                return Done();
            case 20:
                WW(MutationRoot, RW(MetadataResult));
                TreeHeader(p, RW(MutationRoot), 1, 0);
                Copy(p + 64, MutationRecord, SeekLeafBytes);
                return StagePage(p, RW(MutationRoot), 19);
            case 24:
                return ReadStaged(GetWide(SeekPath + memory.Read32(Arena + (MutationCheckDepth)) * 16), right, 25);
            case 25:
                uint checkedFrame = SeekPath + memory.Read32(Arena + (MutationCheckDepth)) * 16;
                if (!ValidPage(right, GetWide(checkedFrame)) || !ReadableGeneration(right) || memory.Read16(right + 50) != memory.Read32(checkedFrame + 12))
                    return Fail(CpxError.Corrupt);
                if (memory.Read16(right + 48) < (P - 64) / (SeekKeyBytes + 8))
                {
                    memory.Write32(Arena + (Phase), 2);
                    return StepResult.Pending;
                }

                if (memory.Read32(Arena + (MutationCheckDepth)) == 0)
                    return Reject(CpxError.TreeTooDeep);
                memory.Write32(Arena + (MutationCheckDepth), memory.Read32(Arena + (MutationCheckDepth)) - 1);
                memory.Write32(Arena + (Phase), 24);
                return StepResult.Pending;
            default:
                return Fail(CpxError.Corrupt);
        }
    }

    private StepResult SaveObject(uint record, uint next)
    {
        Copy(SaveRecord, record, 256);
        return Call(CpxOperation.SaveObject, next);
    }

    private StepResult SaveObjectStep()
    {
        switch (memory.Read32(Arena + (Phase)))
        {
            case 0:
                return ReadStaged(Anchor + 3 * Q, Page(3), 1);
            case 1:
                if (!Control(Page(3), 3, Anchor + 3 * Q) || !ReadableGeneration(Page(3)))
                    return Fail(CpxError.Corrupt);
                WW(SaveRoot, GetWide(Page(3) + 80));
                return MutateTree(CpxTreeKind.Object, RW(SaveRoot), default, SaveRecord, 1, 2);
            case 2:
                return Done();
            default:
                return Fail(CpxError.Corrupt);
        }
    }
}
