namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    private const uint CommentId = 15040, CommentPage = 15048, CommentLength = 15056, CommentScan = 15060, CommentEncoded = 15064, CommentOwner = 15068, OwnerInput = 15072;
    private uint CommentInput => Arena + 15104;
    private uint CommentObject => Arena + 15264;

    public CpxError BeginOwner(Wide id, uint ownerInfo)
    {
        CpxError error = StartOperation(CpxOperation.SetOwner);
        if (error != CpxError.None)
            return error;
        WW(CommentId, id);
        memory.Write32(Arena + (OwnerInput), ownerInfo);
        memory.Write32(Arena + (CommentLength), 0);
        return error;
    }

    public uint AttributeOwnerInfo(uint p) => memory.Read16(p + 64) == 2 ? memory.Read32(p + 68) : 0;
    public uint AttributeCommentBytes(uint p) => memory.Read16(p + (memory.Read16(p + 64) == 2 ? 74u : 70u));
    public uint AttributeCommentData(uint p) => p + (memory.Read16(p + 64) == 2 ? 76u : 72u);
    public bool ValidAttribute(uint p, Wide id) => memory.Read16(p + 4) == 18 && (((ulong)(GetWide(p + 24))) == ((ulong)(id))) && PayloadValid(p) && (memory.Read16(p + 64) == 1 || OwnershipEnabled);
    private bool AttributePayloadValid(uint p)
    {
        uint version = memory.Read16(p + 64), length = AttributeCommentBytes(p);
        if (memory.Read16(p + 66) != 0 || length > 158)
            return false;
        if (version == 1)
            return length != 0 && memory.Read16(p + 68) == 1 && memory.Read16(p + 52) == 8 + length;
        return version == 2 && memory.Read32(p + 68) != 0 && memory.Read16(p + 72) == 1 && memory.Read16(p + 52) == 12 + length;
    }

    public CpxError BeginComment(Wide id, uint latin1, uint length)
    {
        if (length > 79 || latin1 > uint.MaxValue - length || (length != 0 && latin1 == 0))
            return CpxError.InvalidArgument;
        for (uint n = 0; n < length; n++)
            if (memory.Read8(latin1 + n) == 0)
                return CpxError.InvalidArgument;
        CpxError error = StartOperation(CpxOperation.SetComment);
        if (error != CpxError.None)
            return error;
        WW(CommentId, id);
        memory.Write32(Arena + (CommentLength), length);
        Copy(CommentInput, latin1, length);
        return error;
    }

    private StepResult CommentStep()
    {
        uint obj = CommentObject, p = Page(4);
        ulong page = RW(CommentPage);
        bool owner = (CpxOperation)memory.Read32(Arena + (OperationAt)) == CpxOperation.SetOwner;
        switch (memory.Read32(Arena + (Phase)))
        {
            case 0:
                return LoadObject(RW(CommentId), 1);
            case 1:
                if (memory.Read32(Arena + (SourcePage)) == 0 || memory.Read32(ObjectRecord + 76) == 0)
                    return Reject(CpxError.NotFound);
                Copy(obj, ObjectRecord, 256);
                WW(CommentPage, GetWide(obj + 48));
                memory.Write32(Arena + (CommentOwner), owner ? memory.Read32(Arena + (OwnerInput)) : 0);
                if (!(((ulong)(GetWide(obj + 48))) == 0))
                    return ReadStaged(GetWide(obj + 48), p, 2);
                if (memory.Read32(Arena + (CommentLength)) == 0 && memory.Read32(Arena + (CommentOwner)) == 0)
                    return Done();
                return EnsureReserve(13, 3); // One attribute page plus the 12-page margin.
            case 2:
                if (!ValidPage(p, page) || !ReadableGeneration(p) || !ValidAttribute(p, RW(CommentId)))
                    return Fail(CpxError.Corrupt);
                if (owner)
                {
                    if (AttributeOwnerInfo(p) == memory.Read32(Arena + (OwnerInput)))
                        return Done();
                    memory.Write32(Arena + (CommentLength), AttributeCommentBytes(p));
                    Copy(CommentInput, AttributeCommentData(p), memory.Read32(Arena + (CommentLength)));
                }
                else
                    memory.Write32(Arena + (CommentOwner), AttributeOwnerInfo(p));
                memory.Write32(Arena + (Phase), 3);
                return StepResult.Pending;
            case 3:
                return BeginTx(6, 20);
            case 20:
                if (memory.Read32(Arena + (CommentOwner)) != 0 && !OwnershipEnabled)
                    return ReadStaged(StatePageAddress, p, 21);
                memory.Write32(Arena + (Phase), 4);
                return StepResult.Pending;
            case 21:
                if (!Control(p, 3, StatePageAddress) || !ReadableGeneration(p) || !ValidStateLabel(p))
                    return Fail(CpxError.Corrupt);
                if (memory.Read16(p + 52) == 48)
                {
                    memory.Write32(Arena + (LabelLength), memory.Read8(VolumeLabelAddress));
                    Copy(LabelInput, VolumeLabelAddress + 1, memory.Read32(Arena + (LabelLength)));
                    EncodeStateLabel(p);
                }

                memory.Write16(p + 52, 136);
                memory.Write32(p + 192, 0x4f574e31);
                memory.Write32(p + 196, 1);
                return StagePage(p, StatePageAddress, 4);
            case 4:
                if (memory.Read32(Arena + (CommentLength)) == 0 && memory.Read32(Arena + (CommentOwner)) == 0)
                {
                    PutWide(obj + 48, default);
                    return ReleaseMetadata(page, 9);
                }

                if ((((ulong)(page)) == 0))
                    return TakeMetadata(5);
                memory.Write32(Arena + (Phase), 6);
                return StepResult.Pending;
            case 5:
                WW(CommentPage, RW(MetadataResult));
                memory.Write32(Arena + (Phase), 6);
                return StepResult.Pending;
            case 6:
                Header(p, 18, page, RW(Generation) + 1);
                PutWide(p + 24, RW(CommentId));
                memory.Write16(p + 64, memory.Read32(Arena + (CommentOwner)) == 0 ? 1u : 2u);
                if (memory.Read32(Arena + (CommentOwner)) != 0)
                    memory.Write32(p + 68, memory.Read32(Arena + (CommentOwner)));
                memory.Write16(p + (memory.Read32(Arena + (CommentOwner)) == 0 ? 68u : 72u), 1);
                memory.Write32(Arena + (CommentScan), 0);
                memory.Write32(Arena + (CommentEncoded), 0);
                memory.Write32(Arena + (Phase), 7);
                return StepResult.Pending;
            case 7:
                uint scan = memory.Read32(Arena + (CommentScan)), encoded = memory.Read32(Arena + (CommentEncoded)), end = scan + 64;
                if (end > memory.Read32(Arena + (CommentLength)))
                    end = memory.Read32(Arena + (CommentLength));
                uint data = AttributeCommentData(p);
                for (; scan < end; scan++)
                {
                    uint c = memory.Read8(CommentInput + scan);
                    if (!owner && c >= 128)
                    {
                        memory.Write8(data + encoded++, (byte)(0xc0 | (c >> 6)));
                        c = 0x80 | (c & 63);
                    }

                    memory.Write8(data + encoded++, (byte)c);
                }

                memory.Write32(Arena + (CommentScan), scan);
                memory.Write32(Arena + (CommentEncoded), encoded);
                if (scan < memory.Read32(Arena + (CommentLength)))
                    return StepResult.Pending;
                memory.Write16(data - 2, encoded);
                memory.Write16(p + 52, data - p - 64 + encoded);
                PutWide(obj + 48, page);
                return StagePage(p, page, 9);
            case 9:
                return SaveObject(obj, 10);
            case 10:
                return Call(CpxOperation.CommitTransaction, 11);
            case 11:
                if (memory.Read32(Arena + (CommentOwner)) != 0)
                    memory.Write32(Arena + (OwnershipFeature), 1);
                return Done();
            default:
                return Fail(CpxError.Corrupt);
        }
    }
}
