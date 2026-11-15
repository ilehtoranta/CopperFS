namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    public uint TreeRecord => ObjectRecord + 512;
    private uint SeekPath => ObjectRecord + 768;
    public bool TreeFound => memory.Read32(Arena + (SeekFound)) != 0;

    public CpxError BeginTreeRead(CpxTreeKind kind, Wide root, Wide owner, Wide high, Wide low, CpxSeek seek)
    {
        if (kind > CpxTreeKind.Extent || seek > CpxSeek.After)
            return CpxError.InvalidArgument;
        CpxError error = StartOperation(CpxOperation.ReadTree);
        if (error == CpxError.None)
            StartSeek(kind, root, owner, high, low, seek);
        return error;
    }

    private void StartSeek(CpxTreeKind kind, Wide root, Wide owner, Wide high, Wide low, CpxSeek seek)
    {
        memory.Write32(Arena + (TreeKindAt), (uint)kind);
        memory.Write32(Arena + (SeekAt), (uint)seek);
        WW(SeekRoot, root);
        WW(SeekAddress, root);
        WW(SeekOwner, owner);
        WW(SeekHigh, high);
        WW(SeekLow, low);
        memory.Write32(Arena + (SeekFound), 0);
        memory.Write32(Arena + (SeekDepth), 0);
        memory.Write32(Arena + (SeekExpected), uint.MaxValue);
        memory.Write32(Arena + (SeekDirection), 0);
        WW(SeekAddress, root);
    }

    private uint SeekKeyBytes => memory.Read32(Arena + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u;
    private uint SeekLeafBytes => memory.Read32(Arena + (TreeKindAt)) == 0 ? 256u : memory.Read32(Arena + (TreeKindAt)) == 1 ? 128u : 24u;

    private uint SeekType(uint level) => 11 + memory.Read32(Arena + (TreeKindAt)) * 2 + (level != 0 ? 1u : 0u);
    private int SeekCompare(uint record, ulong high, ulong low)
    {
        int order = Scalar64.Compare(GetWide(record), high);
        return order != 0 || SeekKeyBytes == 8 ? order : Scalar64.Compare(GetWide(record + 8), low);
    }

    private StepResult SeekStep()
    {
        uint p = Page(1), phase = memory.Read32(Arena + (Phase));
        switch (phase)
        {
            case 0:
                if ((((ulong)(RW(SeekRoot))) == 0))
                    return Done();
                memory.Write32(Arena + (Phase), 1);
                return StepResult.Pending;
            case 1:
                if (memory.Read32(Arena + (SeekDepth)) >= 8 || !Allocatable(RW(SeekAddress)))
                    return Fail(CpxError.Corrupt);
                return ReadStaged(RW(SeekAddress), p, 2);
            case 2:
                uint level = memory.Read16(p + 50), count = memory.Read16(p + 48), width = level == 0 ? SeekLeafBytes : SeekKeyBytes + 8;
                if (!ValidPage(p, RW(SeekAddress)) || !ReadableGeneration(p) || memory.Read16(p + 4) != SeekType(level) || !(((ulong)(GetWide(p + 24))) == ((ulong)(RW(SeekOwner)))) || !(((ulong)(GetWide(p + 40))) == 0) || level >= 8 || (memory.Read32(Arena + (SeekExpected)) != uint.MaxValue && memory.Read32(Arena + (SeekExpected)) != level) || count > (P - 64) / width || (level != 0 && count == 0) || memory.Read16(p + 52) != count * width)
                    return Fail(CpxError.Corrupt);
                memory.Write32(Arena + (SeekCount), count);
                memory.Write32(Arena + (SeekLevel), level);
                memory.Write32(Arena + (SeekScan), 0);
                memory.Write32(Arena + (SeekSlot), level == 0 ? uint.MaxValue : 0);
                memory.Write32(Arena + (Phase), 3);
                return StepResult.Pending;
            case 3:
                uint size = memory.Read32(Arena + (SeekLevel)) == 0 ? SeekLeafBytes : SeekKeyBytes + 8, index = memory.Read32(Arena + (SeekScan)), end = index + 64;
                if (end > memory.Read32(Arena + (SeekCount)))
                    end = memory.Read32(Arena + (SeekCount));
                for (; index < end; index++)
                {
                    uint record = p + 64 + index * size;
                    ulong high = GetWide(record), low = SeekKeyBytes == 16 ? GetWide(record + 8) : default;
                    if (index != 0 && SeekCompare(record - size, high, low) >= 0)
                        return Fail(CpxError.Corrupt);
                    int order = SeekCompare(record, RW(SeekHigh), RW(SeekLow));
                    if (memory.Read32(Arena + (SeekLevel)) != 0)
                    {
                        if (!Allocatable(GetWide(record + SeekKeyBytes)))
                            return Fail(CpxError.Corrupt);
                        if (order <= 0)
                            memory.Write32(Arena + (SeekSlot), index);
                    }
                    else if (memory.Read32(Arena + (SeekDirection)) == 1 || memory.Read32(Arena + (SeekAt)) == (uint)CpxSeek.Floor && order <= 0 && memory.Read32(Arena + (SeekDirection)) == 0)
                        memory.Write32(Arena + (SeekSlot), index);
                    else if (memory.Read32(Arena + (SeekSlot)) == uint.MaxValue && (memory.Read32(Arena + (SeekDirection)) == 2 || memory.Read32(Arena + (SeekDirection)) == 0 && (memory.Read32(Arena + (SeekAt)) == (uint)CpxSeek.Exact && order == 0 || memory.Read32(Arena + (SeekAt)) == (uint)CpxSeek.Next && order >= 0 || memory.Read32(Arena + (SeekAt)) == (uint)CpxSeek.After && order > 0)))
                        memory.Write32(Arena + (SeekSlot), index);
                }

                memory.Write32(Arena + (SeekScan), index);
                if (index < memory.Read32(Arena + (SeekCount)))
                    return StepResult.Pending;
                uint frame = SeekPath + memory.Read32(Arena + (SeekDepth)) * 16;
                PutWide(frame, RW(SeekAddress));
                memory.Write32(frame + 12, memory.Read32(Arena + (SeekLevel)));
                if (memory.Read32(Arena + (SeekLevel)) != 0)
                {
                    uint slot = memory.Read32(Arena + (SeekDirection)) == 1 ? memory.Read32(Arena + (SeekCount)) - 1 : memory.Read32(Arena + (SeekDirection)) == 2 ? 0 : memory.Read32(Arena + (SeekSlot));
                    memory.Write32(frame + 8, slot);
                    memory.Write32(Arena + (SeekDepth), memory.Read32(Arena + (SeekDepth)) + 1);
                    memory.Write32(Arena + (SeekExpected), memory.Read32(Arena + (SeekLevel)) - 1);
                    WW(SeekAddress, GetWide(p + 64 + slot * size + SeekKeyBytes));
                    memory.Write32(Arena + (Phase), 1);
                    return StepResult.Pending;
                }

                if (memory.Read32(Arena + (SeekSlot)) != uint.MaxValue)
                {
                    memory.Write32(frame + 8, memory.Read32(Arena + (SeekSlot)));
                    uint leafBytes = SeekLeafBytes;
                    Copy(TreeRecord, p + 64 + memory.Read32(Arena + (SeekSlot)) * leafBytes, leafBytes);
                    memory.Write32(Arena + (SeekFound), 1);
                    return Done();
                }

                if (memory.Read32(Arena + (SeekAt)) == (uint)CpxSeek.Exact)
                    return Done();
                memory.Write32(Arena + (SeekDirection), memory.Read32(Arena + (SeekAt)) == (uint)CpxSeek.Floor ? 1u : 2u);
                memory.Write32(Arena + (Phase), 4);
                return StepResult.Pending;
            case 4:
                if (memory.Read32(Arena + (SeekDepth)) == 0)
                    return Done();
                memory.Write32(Arena + (SeekDepth), memory.Read32(Arena + (SeekDepth)) - 1);
                WW(SeekAddress, GetWide(SeekPath + memory.Read32(Arena + (SeekDepth)) * 16));
                return ReadStaged(RW(SeekAddress), p, 5);
            case 5:
                // Parent was fully validated on descent, but is reread after an I/O
                // boundary: recheck identity, checksum and shape before following it.
                uint parent = SeekPath + memory.Read32(Arena + (SeekDepth)) * 16, lev = memory.Read32(parent + 12), cnt = memory.Read16(p + 48), sz = SeekKeyBytes + 8;
                if (!ValidPage(p, RW(SeekAddress)) || !ReadableGeneration(p) || memory.Read16(p + 4) != SeekType(lev) || !(((ulong)(GetWide(p + 24))) == ((ulong)(RW(SeekOwner)))) || memory.Read16(p + 50) != lev || cnt == 0 || cnt > (P - 64) / sz || memory.Read16(p + 52) != cnt * sz || !(((ulong)(GetWide(p + 40))) == 0))
                    return Fail(CpxError.Corrupt);
                uint old = memory.Read32(parent + 8);
                if (old >= cnt)
                    return Fail(CpxError.Corrupt);
                if (memory.Read32(Arena + (SeekDirection)) == 1 ? old == 0 : old + 1 == cnt)
                {
                    memory.Write32(Arena + (Phase), 4);
                    return StepResult.Pending;
                }

                uint next = memory.Read32(Arena + (SeekDirection)) == 1 ? old - 1 : old + 1;
                memory.Write32(parent + 8, next);
                WW(SeekAddress, GetWide(p + 64 + next * sz + SeekKeyBytes));
                memory.Write32(Arena + (SeekExpected), lev - 1);
                memory.Write32(Arena + (SeekDepth), memory.Read32(Arena + (SeekDepth)) + 1);
                memory.Write32(Arena + (Phase), 1);
                return StepResult.Pending;
            default:
                return Fail(CpxError.Corrupt);
        }
    }
}
