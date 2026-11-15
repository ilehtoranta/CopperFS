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
        if (target == 0 || length == 0 || length > 768 || target > uint.MaxValue - length)
            return CpxError.InvalidArgument;
        CpxError error = BeginCreate(parent, name, nameLength);
        if (error != CpxError.None)
            return error;
        memory.Write32(Arena + (NsDirectory), 3);
        memory.Write32(Arena + (LinkLength), length);
        Copy(LinkText, target, length);
        return error;
    }

    private StepResult EncodeSymlinkStep()
    {
        switch (memory.Read32(Arena + (Phase)))
        {
            case 0:
                memory.Write32(Arena + (LinkScan), 0);
                memory.Write32(Arena + (LinkEncoded), 0);
                memory.Write32(Arena + (Phase), 1);
                return StepResult.Pending;
            case 1:
                uint at = memory.Read32(Arena + (LinkScan)), end = at + 64, encoded = memory.Read32(Arena + (LinkEncoded));
                if (end > memory.Read32(Arena + (LinkLength)))
                    end = memory.Read32(Arena + (LinkLength));
                for (; at < end; at++)
                {
                    uint c = memory.Read8(LinkText + at);
                    if (c == 0)
                        return Reject(CpxError.InvalidArgument);
                    encoded += c < 128 ? 1u : 2u;
                }

                if (encoded > 768)
                    return Reject(CpxError.InvalidArgument);
                memory.Write32(Arena + (LinkScan), at);
                memory.Write32(Arena + (LinkEncoded), encoded);
                if (at < memory.Read32(Arena + (LinkLength)))
                    return StepResult.Pending;
                memory.Write32(Arena + (LinkOutput), encoded);
                memory.Write32(Arena + (Phase), 2);
                return StepResult.Pending;
            case 2:
                uint input = memory.Read32(Arena + (LinkScan)), output = memory.Read32(Arena + (LinkOutput)), limit = input > 64 ? input - 64 : 0;
                while (input > limit)
                {
                    uint c = memory.Read8(LinkText + --input);
                    if (c >= 128)
                    {
                        memory.Write8(LinkText + --output, (byte)(0x80 | (c & 63)));
                        c = 0xc0 | (c >> 6);
                    }

                    memory.Write8(LinkText + --output, (byte)c);
                }

                memory.Write32(Arena + (LinkScan), input);
                memory.Write32(Arena + (LinkOutput), output);
                return input == 0 ? Done() : StepResult.Pending;
            default:
                return Fail(CpxError.Corrupt);
        }
    }

    public CpxError BeginHardLink(Wide target, Wide parent, uint name, uint length)
    {
        if (!ValidComponent(name, length, true))
            return CpxError.InvalidArgument;
        CpxError error = StartOperation(CpxOperation.CreateHardLink);
        if (error != CpxError.None)
            return error;
        WW(NsResult, target);
        WW(NsParent, parent);
        memory.Write32(Arena + (NsLength), length);
        Copy(NsName, name, length);
        return error;
    }

    private StepResult HardLinkStep()
    {
        uint p = Page(3);
        if (memory.Read32(Arena + (Phase))is 80 or 81)
            return NamespaceStep();
        switch (memory.Read32(Arena + (Phase)))
        {
            case 0:
                return Call(CpxOperation.Reclaim, 1);
            case 1:
                return LookupName(RW(NsParent), NsName, memory.Read32(Arena + (NsLength)), 2);
            case 2:
                if (LookupFound)
                    return Reject(CpxError.AlreadyExists);
                Copy(NsParentRecord, ObjectRecord, 256);
                if ((memory.Read32(NsParentRecord + 72) & 4) != 0)
                    return Reject(CpxError.Protected);
                return LoadObject(RW(NsResult), 3);
            case 3:
                if (memory.Read32(Arena + (SourcePage)) == 0 || memory.Read32(ObjectRecord + 76) == 0)
                    return Reject(CpxError.NotFound);
                if (memory.Read8(ObjectRecord + 84) != 1)
                    return Reject(CpxError.WrongType);
                if (memory.Read32(ObjectRecord + 76) == uint.MaxValue)
                    return Reject(CpxError.ResourceExhausted);
                Copy(NsObject, ObjectRecord, 256);
                memory.Write32(NsObject + 76, memory.Read32(NsObject + 76) + 1);
                return EnsureReserve(22, 4);
            case 4:
                return BeginTx(22, 5);
            case 5:
                return ReadStaged(Anchor + 3 * Q, p, 6);
            case 6:
                if (!Control(p, 3, Anchor + 3 * Q) || !ReadableGeneration(p))
                    return Fail(CpxError.Corrupt);
                WW(NsEntryId, GetWide(p + 72));
                if (!AdjustWide(p + 72, 1, true) || !AdjustWide(p + 104, 1, true))
                    return Reject(CpxError.ResourceExhausted);
                return StagePage(p, Anchor + 3 * Q, 7);
            case 7:
                return BuildEntry(8);
            case 8:
                return MutateTree(CpxTreeKind.Directory, GetWide(NsParentRecord + 32), RW(NsParent), NsEntry, 0, 9);
            case 9:
                PutWide(NsParentRecord + 32, RW(MutationRoot));
                if (!AdjustWide(NsParentRecord + 8, 1, true))
                    return Fail(CpxError.Corrupt);
                return SaveObject(NsParentRecord, 10);
            case 10:
                return SaveObject(NsObject, 11);
            case 11:
                return Call(CpxOperation.CommitTransaction, 12);
            case 12:
                memory.Write32(Arena + (NsPublished), 1);
                return Done();
            default:
                return Fail(CpxError.Corrupt);
        }
    }

    public CpxError BeginReadSymlink(Wide id, uint destination, uint capacity)
    {
        if (destination == 0 || capacity > 768 || destination > uint.MaxValue - capacity)
            return CpxError.InvalidArgument;
        CpxError error = StartOperation(CpxOperation.ReadSymlink, id);
        if (error != CpxError.None)
            return error;
        memory.Write32(Arena + (LinkBuffer), destination);
        memory.Write32(Arena + (LinkCapacity), capacity);
        return error;
    }

    private StepResult ReadSymlinkStep()
    {
        uint p = Page(4);
        switch (memory.Read32(Arena + (Phase)))
        {
            case 0:
                return LoadObject(RW(ObjectKey), 1);
            case 1:
                if (memory.Read32(Arena + (SourcePage)) == 0)
                    return Reject(CpxError.NotFound);
                if (memory.Read8(ObjectRecord + 84) != 3)
                    return Reject(CpxError.WrongType);
                ulong size = GetWide(ObjectRecord + 8);
                if (((uint)(((ulong)(size)) >> 32)) != 0 || ((uint)((ulong)(size))) == 0 || ((uint)((ulong)(size))) > 768)
                    return Fail(CpxError.Corrupt);
                if (((uint)((ulong)(size))) > memory.Read32(Arena + (LinkCapacity)))
                    return Reject(CpxError.InvalidArgument);
                memory.Write32(Arena + (LinkLength), ((uint)((ulong)(size))));
                memory.Write32(Arena + (LinkScan), 0);
                if (memory.Read8(ObjectRecord + 85) == 0)
                {
                    if (((uint)((ulong)(size))) > 160 || memory.Read16(ObjectRecord + 88) != ((uint)((ulong)(size))))
                        return Fail(CpxError.Corrupt);
                    memory.Write32(Arena + (LinkOutput), ObjectRecord + 96);
                    memory.Write32(Arena + (Phase), 3);
                    return StepResult.Pending;
                }

                if (memory.Read8(ObjectRecord + 85) != 3 || !Allocatable(GetWide(ObjectRecord + 96)))
                    return Fail(CpxError.Corrupt);
                return ReadStaged(GetWide(ObjectRecord + 96), p, 2);
            case 2:
                if (!ValidPage(p, GetWide(ObjectRecord + 96)) || !ReadableGeneration(p) || memory.Read16(p + 4) != 19 || !(((ulong)(GetWide(p + 24))) == ((ulong)(GetWide(ObjectRecord)))) || !PayloadValid(p) || memory.Read16(p + 64) != memory.Read32(Arena + (LinkLength)))
                    return Fail(CpxError.Corrupt);
                memory.Write32(Arena + (LinkOutput), p + 68);
                memory.Write32(Arena + (Phase), 3);
                return StepResult.Pending;
            case 3:
                uint count = memory.Read32(Arena + (LinkLength)) - memory.Read32(Arena + (LinkScan));
                if (count > 64)
                    count = 64;
                Copy(memory.Read32(Arena + (LinkBuffer)) + memory.Read32(Arena + (LinkScan)), memory.Read32(Arena + (LinkOutput)) + memory.Read32(Arena + (LinkScan)), count);
                memory.Write32(Arena + (LinkScan), memory.Read32(Arena + (LinkScan)) + count);
                if (memory.Read32(Arena + (LinkScan)) < memory.Read32(Arena + (LinkLength)))
                    return StepResult.Pending;
                memory.Write32(Arena + (FileValue), memory.Read32(Arena + (LinkLength)));
                return Done();
            default:
                return Fail(CpxError.Corrupt);
        }
    }
}
