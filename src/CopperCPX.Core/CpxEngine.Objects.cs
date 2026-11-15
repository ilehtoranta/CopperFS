namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    public uint ObjectRecord => Arena + CpxArena.MountBytes;
    public bool ObjectFound => memory.Read32(Arena + (OperationAt)) == (uint)CpxOperation.ReadObject && memory.Read32(Arena + (Finished)) != 0 && memory.Read32(Arena + (ErrorAt)) == 0 && memory.Read32(Arena + (SourcePage)) != 0;

    private StepResult ReadStaged(ulong at, uint buffer, uint nextPhase)
    {
        int staged = FindStaged(at);
        if (staged >= 0 && BufferedWrites)
        {
            Copy(buffer, BufferedImage((uint)staged), P);
            TrustBufferedPage(buffer, at);
            memory.Write32(Arena + (Phase), nextPhase);
            return StepResult.Pending;
        }

        if (staged < 0 && BufferedWrites && ReadBufferedClean(at, buffer))
        {
            memory.Write32(Arena + (Phase), nextPhase);
            return StepResult.Pending;
        }

        return Issue(IoKind.Read, staged < 0 ? at : Image(RW(Generation) + 1, (uint)staged), buffer, P, nextPhase);
    }

    private StepResult ObjectStep()
    {
        uint p = Page(0);
        switch (memory.Read32(Arena + (Phase)))
        {
            case 0:
                memory.Write32(Arena + (SourcePage), 0);
                memory.Write32(Arena + (TreeDepth), 0);
                memory.Write32(Arena + (ExpectedLevel), uint.MaxValue);
                if (BufferedWrites && (CpxOperation)memory.Read32(Arena + (OperationAt))is CpxOperation.ReadObject or CpxOperation.ReadFile or CpxOperation.WriteMapped or CpxOperation.Lookup && ReadBufferedObject(RW(ObjectKey)))
                    return Done();
                return ReadStaged(Anchor + 3 * Q, p, 1);
            case 1:
                if (!Control(p, 3, Anchor + 3 * Q) || !ReadableGeneration(p))
                    return Fail(CpxError.Corrupt);
                WW(TreeAddress, GetWide(p + 80));
                memory.Write32(Arena + (Phase), 2);
                return StepResult.Pending;
            case 2:
                if (memory.Read32(Arena + (TreeDepth)) == 8 || !Allocatable(RW(TreeAddress)))
                    return Fail(CpxError.Corrupt);
                memory.Write32(Arena + (TreeDepth), memory.Read32(Arena + (TreeDepth)) + 1);
                return ReadStaged(RW(TreeAddress), p, 3);
            case 3:
                uint level = memory.Read16(p + 50), count = memory.Read16(p + 48), width = level == 0 ? 256u : 16u;
                if (!Control(p, level == 0 ? 11u : 12u, RW(TreeAddress)) || !ReadableGeneration(p) || !(((ulong)(GetWide(p + 40))) == 0) || level >= 8 || (memory.Read32(Arena + (ExpectedLevel)) != uint.MaxValue && level != memory.Read32(Arena + (ExpectedLevel))) || count > (P - 64) / width || (level != 0 && count == 0) || memory.Read16(p + 52) != count * width)
                    return Fail(CpxError.Corrupt);
                memory.Write32(Arena + (TreeCount), count);
                memory.Write32(Arena + (TreeLevel), level);
                memory.Write32(Arena + (ScanIndex), 0);
                memory.Write32(Arena + (TreeSlot), 0);
                memory.Write32(Arena + (Phase), 4);
                return StepResult.Pending;
            case 4:
                uint size = memory.Read32(Arena + (TreeLevel)) == 0 ? 256u : 16u, index = memory.Read32(Arena + (ScanIndex)), end = index + 64;
                if (end > memory.Read32(Arena + (TreeCount)))
                    end = memory.Read32(Arena + (TreeCount));
                for (; index < end; index++)
                {
                    ulong key = GetWide(p + 64 + index * size);
                    if (index > 0 && (GetWide(p + 64 + (index - 1) * size)) >= (key))
                        return Fail(CpxError.Corrupt);
                    if (memory.Read32(Arena + (TreeLevel)) != 0)
                    {
                        if (!Allocatable(GetWide(p + 64 + index * size + 8)))
                            return Fail(CpxError.Corrupt);
                        if ((key) <= (RW(ObjectKey)))
                            memory.Write32(Arena + (TreeSlot), index);
                    }
                    else if ((((ulong)(key)) == ((ulong)(RW(ObjectKey)))))
                        memory.Write32(Arena + (TreeSlot), index + 1);
                }

                memory.Write32(Arena + (ScanIndex), index);
                if (index < memory.Read32(Arena + (TreeCount)))
                    return StepResult.Pending;
                if (memory.Read32(Arena + (TreeLevel)) != 0)
                {
                    WW(TreeAddress, GetWide(p + 64 + memory.Read32(Arena + (TreeSlot)) * size + 8));
                    memory.Write32(Arena + (ExpectedLevel), memory.Read32(Arena + (TreeLevel)) - 1);
                    memory.Write32(Arena + (Phase), 2);
                    return StepResult.Pending;
                }

                CacheBufferedObjects(p, RW(TreeAddress));
                if (memory.Read32(Arena + (TreeSlot)) != 0)
                {
                    uint source = p + 64 + (memory.Read32(Arena + (TreeSlot)) - 1) * 256;
                    Copy(ObjectRecord, source, 256);
                    memory.Write32(Arena + (SourcePage), 1);
                }

                return Done();
            default:
                return Fail(CpxError.Corrupt);
        }
    }
}
