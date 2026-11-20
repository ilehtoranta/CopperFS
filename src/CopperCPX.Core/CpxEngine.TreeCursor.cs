namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    public uint TreeRecord => (Arena + CpxArena.MountBytes) + 512;
    private uint SeekPath => (Arena + CpxArena.MountBytes) + 768;
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
        uint cpxArenaBase = Arena;
        memory.Write32(cpxArenaBase + (TreeKindAt), (uint)kind);
        memory.Write32(cpxArenaBase + (SeekAt), (uint)seek);
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(SeekRoot);
            ulong cpxWideValue = root;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        {
            uint cpxWideAddress = cpxArenaBase + (uint)(SeekAddress);
            ulong cpxWideValue = root;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        {
            uint cpxWideAddress = cpxArenaBase + (uint)(SeekOwner);
            ulong cpxWideValue = owner;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        {
            uint cpxWideAddress = cpxArenaBase + (uint)(SeekHigh);
            ulong cpxWideValue = high;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        {
            uint cpxWideAddress = cpxArenaBase + (uint)(SeekLow);
            ulong cpxWideValue = low;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(cpxArenaBase + (SeekFound), 0);
        memory.Write32(cpxArenaBase + (SeekDepth), 0);
        memory.Write32(cpxArenaBase + (SeekExpected), uint.MaxValue);
        memory.Write32(cpxArenaBase + (SeekDirection), 0);
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(SeekAddress);
            ulong cpxWideValue = root;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }
    }

    private uint SeekKeyBytes => memory.Read32(Arena + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u;

    private uint SeekLeafBytes
    {
        get
        {
            uint cpxArenaBase = Arena;
            return memory.Read32(cpxArenaBase + (TreeKindAt)) == 0 ? 256u : memory.Read32(cpxArenaBase + (TreeKindAt)) == 1 ? 128u : 24u;
        }
    }

    private uint SeekType(uint level) => 11 + memory.Read32(Arena + (TreeKindAt)) * 2 + (level != 0 ? 1u : 0u);
    private int SeekCompare(uint record, ulong high, ulong low)
    {
        int order = Scalar64.Compare(((((ulong)(memory.Read32(((uint)(record))))) << 32 | ((ulong)(memory.Read32(((uint)(record)) + 4))))), high);
        return order != 0 || (memory.Read32(Arena + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u) == 8 ? order : Scalar64.Compare(((((ulong)(memory.Read32(((uint)(record + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 8)) + 4))))), low);
    }

    private StepResult SeekStep()
    {
        uint cpxArenaBase = Arena;
        uint p = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(1)) * 4))), phase = memory.Read32(cpxArenaBase + (Phase));
        switch (phase)
        {
            case 0:
                if ((((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(SeekRoot)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(SeekRoot)) + 4)))))))) == 0))
                    return Done();
                memory.Write32(cpxArenaBase + (Phase), 1);
                return StepResult.Pending;
            case 1:
                if (memory.Read32(cpxArenaBase + (SeekDepth)) >= 8 || !Allocatable(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(SeekAddress)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(SeekAddress)) + 4))))))))
                    return Fail(CpxError.Corrupt);
                return ReadStaged(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(SeekAddress)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(SeekAddress)) + 4)))))), p, 2);
            case 2:
                uint level = memory.Read16(p + 50), count = memory.Read16(p + 48), width = level == 0 ? (memory.Read32(cpxArenaBase + (TreeKindAt)) == 0 ? 256u : memory.Read32(cpxArenaBase + (TreeKindAt)) == 1 ? 128u : 24u) : (memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u) + 8;
                if (!ValidPage(p, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(SeekAddress)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(SeekAddress)) + 4))))))) || !ReadableGeneration(p) || memory.Read16(p + 4) != SeekType(level) || !(((ulong)(((((ulong)(memory.Read32(((uint)(p + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 24)) + 4))))))) == ((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(SeekOwner)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(SeekOwner)) + 4))))))))) || !(((ulong)(((((ulong)(memory.Read32(((uint)(p + 40))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 40)) + 4))))))) == 0) || level >= 8 || (memory.Read32(cpxArenaBase + (SeekExpected)) != uint.MaxValue && memory.Read32(cpxArenaBase + (SeekExpected)) != level) || count > ((memory.Read32(cpxArenaBase + (PageBytes))) - 64) / width || (level != 0 && count == 0) || memory.Read16(p + 52) != count * width)
                    return Fail(CpxError.Corrupt);
                memory.Write32(cpxArenaBase + (SeekCount), count);
                memory.Write32(cpxArenaBase + (SeekLevel), level);
                memory.Write32(cpxArenaBase + (SeekScan), 0);
                memory.Write32(cpxArenaBase + (SeekSlot), level == 0 ? uint.MaxValue : 0);
                memory.Write32(cpxArenaBase + (Phase), 3);
                return StepResult.Pending;
            case 3:
                uint size = memory.Read32(cpxArenaBase + (SeekLevel)) == 0 ? (memory.Read32(cpxArenaBase + (TreeKindAt)) == 0 ? 256u : memory.Read32(cpxArenaBase + (TreeKindAt)) == 1 ? 128u : 24u) : (memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u) + 8, index = memory.Read32(cpxArenaBase + (SeekScan)), end = index + 64;
                if (end > memory.Read32(cpxArenaBase + (SeekCount)))
                    end = memory.Read32(cpxArenaBase + (SeekCount));
                for (; index < end; index++)
                {
                    uint record = p + 64 + index * size;
                    ulong high = ((((ulong)(memory.Read32(((uint)(record))))) << 32 | ((ulong)(memory.Read32(((uint)(record)) + 4))))), low = (memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u) == 16 ? ((((ulong)(memory.Read32(((uint)(record + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 8)) + 4))))) : default;
                    if (index != 0 && SeekCompare(record - size, high, low) >= 0)
                        return Fail(CpxError.Corrupt);
                    int order = SeekCompare(record, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(SeekHigh)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(SeekHigh)) + 4)))))), ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(SeekLow)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(SeekLow)) + 4)))))));
                    if (memory.Read32(cpxArenaBase + (SeekLevel)) != 0)
                    {
                        if (!Allocatable(((((ulong)(memory.Read32(((uint)(record + (memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u)))))) << 32 | ((ulong)(memory.Read32(((uint)(record + (memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u))) + 4)))))))
                            return Fail(CpxError.Corrupt);
                        if (order <= 0)
                            memory.Write32(cpxArenaBase + (SeekSlot), index);
                    }
                    else if (memory.Read32(cpxArenaBase + (SeekDirection)) == 1 || memory.Read32(cpxArenaBase + (SeekAt)) == (uint)CpxSeek.Floor && order <= 0 && memory.Read32(cpxArenaBase + (SeekDirection)) == 0)
                        memory.Write32(cpxArenaBase + (SeekSlot), index);
                    else if (memory.Read32(cpxArenaBase + (SeekSlot)) == uint.MaxValue && (memory.Read32(cpxArenaBase + (SeekDirection)) == 2 || memory.Read32(cpxArenaBase + (SeekDirection)) == 0 && (memory.Read32(cpxArenaBase + (SeekAt)) == (uint)CpxSeek.Exact && order == 0 || memory.Read32(cpxArenaBase + (SeekAt)) == (uint)CpxSeek.Next && order >= 0 || memory.Read32(cpxArenaBase + (SeekAt)) == (uint)CpxSeek.After && order > 0)))
                        memory.Write32(cpxArenaBase + (SeekSlot), index);
                }

                memory.Write32(cpxArenaBase + (SeekScan), index);
                if (index < memory.Read32(cpxArenaBase + (SeekCount)))
                    return StepResult.Pending;
                uint frame = ((cpxArenaBase + CpxArena.MountBytes) + 768) + memory.Read32(cpxArenaBase + (SeekDepth)) * 16;
            {
                uint cpxWideAddress = (uint)(frame);
                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(SeekAddress)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(SeekAddress)) + 4))))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write32(frame + 12, memory.Read32(cpxArenaBase + (SeekLevel)));
                if (memory.Read32(cpxArenaBase + (SeekLevel)) != 0)
                {
                    uint slot = memory.Read32(cpxArenaBase + (SeekDirection)) == 1 ? memory.Read32(cpxArenaBase + (SeekCount)) - 1 : memory.Read32(cpxArenaBase + (SeekDirection)) == 2 ? 0 : memory.Read32(cpxArenaBase + (SeekSlot));
                    memory.Write32(frame + 8, slot);
                    memory.Write32(cpxArenaBase + (SeekDepth), memory.Read32(cpxArenaBase + (SeekDepth)) + 1);
                    memory.Write32(cpxArenaBase + (SeekExpected), memory.Read32(cpxArenaBase + (SeekLevel)) - 1);
                    {
                        uint cpxWideAddress = cpxArenaBase + (uint)(SeekAddress);
                        ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)(p + 64 + slot * size + (memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u)))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 64 + slot * size + (memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u))) + 4)))));
                        memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                        memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                    }

                    memory.Write32(cpxArenaBase + (Phase), 1);
                    return StepResult.Pending;
                }

                if (memory.Read32(cpxArenaBase + (SeekSlot)) != uint.MaxValue)
                {
                    memory.Write32(frame + 8, memory.Read32(cpxArenaBase + (SeekSlot)));
                    uint leafBytes = (memory.Read32(cpxArenaBase + (TreeKindAt)) == 0 ? 256u : memory.Read32(cpxArenaBase + (TreeKindAt)) == 1 ? 128u : 24u);
                    Copy(((cpxArenaBase + CpxArena.MountBytes) + 512), p + 64 + memory.Read32(cpxArenaBase + (SeekSlot)) * leafBytes, leafBytes);
                    memory.Write32(cpxArenaBase + (SeekFound), 1);
                    return Done();
                }

                if (memory.Read32(cpxArenaBase + (SeekAt)) == (uint)CpxSeek.Exact)
                    return Done();
                memory.Write32(cpxArenaBase + (SeekDirection), memory.Read32(cpxArenaBase + (SeekAt)) == (uint)CpxSeek.Floor ? 1u : 2u);
                memory.Write32(cpxArenaBase + (Phase), 4);
                return StepResult.Pending;
            case 4:
                if (memory.Read32(cpxArenaBase + (SeekDepth)) == 0)
                    return Done();
                memory.Write32(cpxArenaBase + (SeekDepth), memory.Read32(cpxArenaBase + (SeekDepth)) - 1);
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(SeekAddress);
                ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 768) + memory.Read32(cpxArenaBase + (SeekDepth)) * 16))))) << 32 | ((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 768) + memory.Read32(cpxArenaBase + (SeekDepth)) * 16)) + 4)))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                return ReadStaged(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(SeekAddress)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(SeekAddress)) + 4)))))), p, 5);
            case 5:
                // Parent was fully validated on descent, but is reread after an I/O
                // boundary: recheck identity, checksum and shape before following it.
                uint parent = ((cpxArenaBase + CpxArena.MountBytes) + 768) + memory.Read32(cpxArenaBase + (SeekDepth)) * 16, lev = memory.Read32(parent + 12), cnt = memory.Read16(p + 48), sz = (memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u) + 8;
                if (!ValidPage(p, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(SeekAddress)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(SeekAddress)) + 4))))))) || !ReadableGeneration(p) || memory.Read16(p + 4) != SeekType(lev) || !(((ulong)(((((ulong)(memory.Read32(((uint)(p + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 24)) + 4))))))) == ((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(SeekOwner)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(SeekOwner)) + 4))))))))) || memory.Read16(p + 50) != lev || cnt == 0 || cnt > ((memory.Read32(cpxArenaBase + (PageBytes))) - 64) / sz || memory.Read16(p + 52) != cnt * sz || !(((ulong)(((((ulong)(memory.Read32(((uint)(p + 40))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 40)) + 4))))))) == 0))
                    return Fail(CpxError.Corrupt);
                uint old = memory.Read32(parent + 8);
                if (old >= cnt)
                    return Fail(CpxError.Corrupt);
                if (memory.Read32(cpxArenaBase + (SeekDirection)) == 1 ? old == 0 : old + 1 == cnt)
                {
                    memory.Write32(cpxArenaBase + (Phase), 4);
                    return StepResult.Pending;
                }

                uint next = memory.Read32(cpxArenaBase + (SeekDirection)) == 1 ? old - 1 : old + 1;
                memory.Write32(parent + 8, next);
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(SeekAddress);
                ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)(p + 64 + next * sz + (memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u)))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 64 + next * sz + (memory.Read32(cpxArenaBase + (TreeKindAt)) == (uint)CpxTreeKind.Directory ? 16u : 8u))) + 4)))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write32(cpxArenaBase + (SeekExpected), lev - 1);
                memory.Write32(cpxArenaBase + (SeekDepth), memory.Read32(cpxArenaBase + (SeekDepth)) + 1);
                memory.Write32(cpxArenaBase + (Phase), 1);
                return StepResult.Pending;
            default:
                return Fail(CpxError.Corrupt);
        }
    }
}