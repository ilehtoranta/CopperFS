namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    // This top-level-operation union overlaps formatter state. Formatting never
    // calls link operations; namespace/reclamation helpers do not use this area.
    private const uint LinkLength = 15524, LinkEncoded = 15528, LinkScan = 15532, LinkOutput = 15536, LinkBuffer = 15540, LinkCapacity = 15544;
    private uint LinkText => Arena + 15552;

    public CpxError BeginSymlink(Wide parent, uint name, uint nameLength, uint target, uint length)
    {
        uint cpxArenaBase = Arena;
        if (target == 0 || length == 0 || length > 768 || target > uint.MaxValue - length)
            return CpxError.InvalidArgument;
        CpxError error = BeginCreate(parent, name, nameLength);
        if (error != CpxError.None)
            return error;
        memory.Write32(cpxArenaBase + (NsDirectory), 3);
        memory.Write32(cpxArenaBase + (LinkLength), length);
        Copy((cpxArenaBase + 15552), target, length);
        return error;
    }

    private StepResult EncodeSymlinkStep()
    {
        uint cpxArenaBase = Arena;
        switch (memory.Read32(cpxArenaBase + (Phase)))
        {
            case 0:
                memory.Write32(cpxArenaBase + (LinkScan), 0);
                memory.Write32(cpxArenaBase + (LinkEncoded), 0);
                memory.Write32(cpxArenaBase + (Phase), 1);
                return StepResult.Pending;
            case 1:
                uint at = memory.Read32(cpxArenaBase + (LinkScan)), end = at + 64, encoded = memory.Read32(cpxArenaBase + (LinkEncoded));
                if (end > memory.Read32(cpxArenaBase + (LinkLength)))
                    end = memory.Read32(cpxArenaBase + (LinkLength));
                for (; at < end; at++)
                {
                    uint c = memory.Read8((cpxArenaBase + 15552) + at);
                    if (c == 0)
                        return Reject(CpxError.InvalidArgument);
                    encoded += c < 128 ? 1u : 2u;
                }

                if (encoded > 768)
                    return Reject(CpxError.InvalidArgument);
                memory.Write32(cpxArenaBase + (LinkScan), at);
                memory.Write32(cpxArenaBase + (LinkEncoded), encoded);
                if (at < memory.Read32(cpxArenaBase + (LinkLength)))
                    return StepResult.Pending;
                memory.Write32(cpxArenaBase + (LinkOutput), encoded);
                memory.Write32(cpxArenaBase + (Phase), 2);
                return StepResult.Pending;
            case 2:
                uint input = memory.Read32(cpxArenaBase + (LinkScan)), output = memory.Read32(cpxArenaBase + (LinkOutput)), limit = input > 64 ? input - 64 : 0;
                while (input > limit)
                {
                    uint c = memory.Read8((cpxArenaBase + 15552) + --input);
                    if (c >= 128)
                    {
                        memory.Write8((cpxArenaBase + 15552) + --output, (byte)(0x80 | (c & 63)));
                        c = 0xc0 | (c >> 6);
                    }

                    memory.Write8((cpxArenaBase + 15552) + --output, (byte)c);
                }

                memory.Write32(cpxArenaBase + (LinkScan), input);
                memory.Write32(cpxArenaBase + (LinkOutput), output);
                return input == 0 ? Done() : StepResult.Pending;
            default:
                return Fail(CpxError.Corrupt);
        }
    }

    public CpxError BeginHardLink(Wide target, Wide parent, uint name, uint length)
    {
        uint cpxArenaBase = Arena;
        if (!ValidComponent(name, length, true))
            return CpxError.InvalidArgument;
        CpxError error = StartOperation(CpxOperation.CreateHardLink);
        if (error != CpxError.None)
            return error;
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(NsResult);
            ulong cpxWideValue = target;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        {
            uint cpxWideAddress = cpxArenaBase + (uint)(NsParent);
            ulong cpxWideValue = parent;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(cpxArenaBase + (NsLength), length);
        Copy((cpxArenaBase + 12208), name, length);
        return error;
    }

    private StepResult HardLinkStep()
    {
        uint cpxArenaBase = Arena;
        uint p = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(3)) * 4)));
        if (memory.Read32(cpxArenaBase + (Phase))is 80 or 81)
            return NamespaceStep();
        switch (memory.Read32(cpxArenaBase + (Phase)))
        {
            case 0:
                return Call(CpxOperation.Reclaim, 1);
            case 1:
                return LookupName(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsParent)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsParent)) + 4)))))), (cpxArenaBase + 12208), memory.Read32(cpxArenaBase + (NsLength)), 2);
            case 2:
                if ((memory.Read32(cpxArenaBase + (LookupFoundAt)) != 0))
                    return Reject(CpxError.AlreadyExists);
                Copy((cpxArenaBase + 10800), (cpxArenaBase + CpxArena.MountBytes), 256);
                if ((memory.Read32((cpxArenaBase + 10800) + 72) & 4) != 0)
                    return Reject(CpxError.Protected);
                return LoadObject(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsResult)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsResult)) + 4)))))), 3);
            case 3:
                if (memory.Read32(cpxArenaBase + (SourcePage)) == 0 || memory.Read32((cpxArenaBase + CpxArena.MountBytes) + 76) == 0)
                    return Reject(CpxError.NotFound);
                if (memory.Read8((cpxArenaBase + CpxArena.MountBytes) + 84) != 1)
                    return Reject(CpxError.WrongType);
                if (memory.Read32((cpxArenaBase + CpxArena.MountBytes) + 76) == uint.MaxValue)
                    return Reject(CpxError.ResourceExhausted);
                Copy((cpxArenaBase + 11312), (cpxArenaBase + CpxArena.MountBytes), 256);
                memory.Write32((cpxArenaBase + 11312) + 76, memory.Read32((cpxArenaBase + 11312) + 76) + 1);
                return EnsureReserve(22, 4);
            case 4:
                return BeginTx(22, 5);
            case 5:
                return ReadStaged(StateAddress, p, 6);
            case 6:
                if (!Control(p, 3, StateAddress) || !ReadableGeneration(p))
                    return Fail(CpxError.Corrupt);
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(NsEntryId);
                ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)(p + 72))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 72)) + 4)))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                if (!AdjustWide(p + 72, 1, true) || !AdjustWide(p + 104, 1, true))
                    return Reject(CpxError.ResourceExhausted);
                return StagePage(p, StateAddress, 7);
            case 7:
                return BuildEntry(8);
            case 8:
                return MutateTree(CpxTreeKind.Directory, ((((ulong)(memory.Read32(((uint)((cpxArenaBase + 10800) + 32))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 10800) + 32)) + 4))))), ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsParent)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsParent)) + 4)))))), (cpxArenaBase + 11824), 0, 9);
            case 9:
            {
                uint cpxWideAddress = (uint)((cpxArenaBase + 10800) + 32);
                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(MutationRoot)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(MutationRoot)) + 4))))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                if (!AdjustWide((cpxArenaBase + 10800) + 8, 1, true))
                    return Fail(CpxError.Corrupt);
                return SaveObject((cpxArenaBase + 10800), 10);
            case 10:
                return SaveObject((cpxArenaBase + 11312), 11);
            case 11:
                return Call(CpxOperation.CommitTransaction, 12);
            case 12:
                memory.Write32(cpxArenaBase + (NsPublished), 1);
                return Done();
            default:
                return Fail(CpxError.Corrupt);
        }
    }

    public CpxError BeginReadSymlink(Wide id, uint destination, uint capacity)
    {
        uint cpxArenaBase = Arena;
        if (destination == 0 || capacity > 768 || destination > uint.MaxValue - capacity)
            return CpxError.InvalidArgument;
        CpxError error = StartOperation(CpxOperation.ReadSymlink, id);
        if (error != CpxError.None)
            return error;
        memory.Write32(cpxArenaBase + (LinkBuffer), destination);
        memory.Write32(cpxArenaBase + (LinkCapacity), capacity);
        return error;
    }

    private StepResult ReadSymlinkStep()
    {
        uint cpxArenaBase = Arena;
        uint p = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(4)) * 4)));
        switch (memory.Read32(cpxArenaBase + (Phase)))
        {
            case 0:
                return LoadObject(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ObjectKey)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ObjectKey)) + 4)))))), 1);
            case 1:
                if (memory.Read32(cpxArenaBase + (SourcePage)) == 0)
                    return Reject(CpxError.NotFound);
                if (memory.Read8((cpxArenaBase + CpxArena.MountBytes) + 84) != 3)
                    return Reject(CpxError.WrongType);
                ulong size = ((((ulong)(memory.Read32(((uint)((cpxArenaBase + CpxArena.MountBytes) + 8))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + CpxArena.MountBytes) + 8)) + 4)))));
                if (((uint)(((ulong)(size)) >> 32)) != 0 || ((uint)((ulong)(size))) == 0 || ((uint)((ulong)(size))) > 768)
                    return Fail(CpxError.Corrupt);
                if (((uint)((ulong)(size))) > memory.Read32(cpxArenaBase + (LinkCapacity)))
                    return Reject(CpxError.InvalidArgument);
                memory.Write32(cpxArenaBase + (LinkLength), ((uint)((ulong)(size))));
                memory.Write32(cpxArenaBase + (LinkScan), 0);
                if (memory.Read8((cpxArenaBase + CpxArena.MountBytes) + 85) == 0)
                {
                    if (((uint)((ulong)(size))) > 160 || memory.Read16((cpxArenaBase + CpxArena.MountBytes) + 88) != ((uint)((ulong)(size))))
                        return Fail(CpxError.Corrupt);
                    memory.Write32(cpxArenaBase + (LinkOutput), (cpxArenaBase + CpxArena.MountBytes) + 96);
                    memory.Write32(cpxArenaBase + (Phase), 3);
                    return StepResult.Pending;
                }

                if (memory.Read8((cpxArenaBase + CpxArena.MountBytes) + 85) != 3 || !Allocatable(((((ulong)(memory.Read32(((uint)((cpxArenaBase + CpxArena.MountBytes) + 96))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + CpxArena.MountBytes) + 96)) + 4)))))))
                    return Fail(CpxError.Corrupt);
                return ReadStaged(((((ulong)(memory.Read32(((uint)((cpxArenaBase + CpxArena.MountBytes) + 96))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + CpxArena.MountBytes) + 96)) + 4))))), p, 2);
            case 2:
                if (!ValidPage(p, ((((ulong)(memory.Read32(((uint)((cpxArenaBase + CpxArena.MountBytes) + 96))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + CpxArena.MountBytes) + 96)) + 4)))))) || !ReadableGeneration(p) || memory.Read16(p + 4) != 19 || !(((ulong)(((((ulong)(memory.Read32(((uint)(p + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 24)) + 4))))))) == ((ulong)(((((ulong)(memory.Read32(((uint)((cpxArenaBase + CpxArena.MountBytes)))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + CpxArena.MountBytes))) + 4)))))))) || !PayloadValid(p) || memory.Read16(p + 64) != memory.Read32(cpxArenaBase + (LinkLength)))
                    return Fail(CpxError.Corrupt);
                memory.Write32(cpxArenaBase + (LinkOutput), p + 68);
                memory.Write32(cpxArenaBase + (Phase), 3);
                return StepResult.Pending;
            case 3:
                uint count = memory.Read32(cpxArenaBase + (LinkLength)) - memory.Read32(cpxArenaBase + (LinkScan));
                if (count > 64)
                    count = 64;
                Copy(memory.Read32(cpxArenaBase + (LinkBuffer)) + memory.Read32(cpxArenaBase + (LinkScan)), memory.Read32(cpxArenaBase + (LinkOutput)) + memory.Read32(cpxArenaBase + (LinkScan)), count);
                memory.Write32(cpxArenaBase + (LinkScan), memory.Read32(cpxArenaBase + (LinkScan)) + count);
                if (memory.Read32(cpxArenaBase + (LinkScan)) < memory.Read32(cpxArenaBase + (LinkLength)))
                    return StepResult.Pending;
                memory.Write32(cpxArenaBase + (FileValue), memory.Read32(cpxArenaBase + (LinkLength)));
                return Done();
            default:
                return Fail(CpxError.Corrupt);
        }
    }
}