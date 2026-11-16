namespace CopperCPX;
public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // Internal continuation only; no public operation or DOS packet ABI change.
    private const CpxOperation BlobOperation = (CpxOperation)42;
    private const uint BlobHead = 13800, BlobTail = 13808, BlobOwner = 13816,
        BlobBuffer = 13824, BlobLength = 13828, BlobType = 13832, BlobMode = 13836,
        BlobProgress = 13840, BlobHeadBytes = 13844, BlobGeneration = 13848;

    private StepResult BeginBlob(ulong head, uint type, ulong owner, uint bytes, uint buffer, uint mode, uint next)
    {
        WW(BlobHead, head); WW(BlobOwner, owner); WW(BlobTail, 0);
        W(BlobType, type); W(BlobLength, bytes); W(BlobBuffer, buffer); W(BlobMode, mode);
        return Call(BlobOperation, next);
    }
    private uint BlobPart(uint length) => length > P - 68 ? P - 68 : length;
    private bool BlobHeadShape(uint p)
    {
        uint length = memory.Read16(p + 64), part = BlobPart(length);
        ulong tail = GetWide(p + 56);
        return length is > 0 and <= 768 && memory.Read16(p + 66) == 0 &&
            memory.Read16(p + 52) == 4 + part && (length == part ? tail == 0 :
                SmallMetadata && tail != GetWide(p + 16) && Allocatable(tail));
    }
    private bool BlobTailShape(uint p)
        => SmallMetadata && memory.Read16(p + 64) is > 0 and <= 324 &&
            memory.Read16(p + 66) == P - 68 && memory.Read16(p + 52) == 4 + memory.Read16(p + 64) &&
            GetWide(p + 56) != GetWide(p + 16) && Allocatable(GetWide(p + 56));
    private bool BlobHeadValid(uint p)
        => ValidPage(p, RW(BlobHead)) && ReadableGeneration(p) && memory.Read16(p + 4) == R(BlobType) &&
            GetWide(p + 24) == RW(BlobOwner) && BlobHeadShape(p) &&
            (R(BlobMode) == 2 || memory.Read16(p + 64) == R(BlobLength));
    private bool BlobTailValid(uint p)
        => ValidPage(p, RW(BlobTail)) && ReadableGeneration(p) && memory.Read16(p + 4) == 22 &&
            GetWide(p + 24) == RW(BlobOwner) && GetWide(p + 32) == RW(BlobGeneration) &&
            GetWide(p + 56) == RW(BlobHead) && BlobTailShape(p) &&
            memory.Read16(p + 64) == R(BlobLength) - R(BlobHeadBytes);

    private StepResult StageBlobHead(uint p)
    {
        uint length = R(BlobLength), part = BlobPart(length);
        Header(p, R(BlobType), RW(BlobHead), RW(Generation) + 1);
        PutWide(p + 24, RW(BlobOwner)); PutWide(p + 56, RW(BlobTail));
        memory.Write16(p + 64, length); memory.Write16(p + 52, 4 + part);
        Copy(p + 68, R(BlobBuffer), part);
        return StagePage(p, RW(BlobHead), 4);
    }

    private StepResult BlobStep()
    {
        uint p = Page(4), phase = R(Phase), mode = R(BlobMode);
        switch (phase)
        {
            case 0:
                if (!Allocatable(RW(BlobHead)) || R(BlobType) is not (17 or 19) || RW(BlobOwner) == 0)
                    return Fail(CpxError.Corrupt);
                if (mode != 0) return ReadStaged(RW(BlobHead), p, mode == 2 ? 20u : 10u);
                if (R(BlobLength) is 0 or > 768 || R(BlobBuffer) == 0) return Fail(CpxError.Corrupt);
                if (R(BlobLength) > P - 68) return SmallMetadata ? TakeMetadata(1) : Fail(CpxError.Corrupt);
                return StageBlobHead(p);
            case 1:
                WW(BlobTail, RW(MetadataResult));
                if (RW(BlobTail) == RW(BlobHead)) return Fail(CpxError.Corrupt);
                uint part = P - 68, tailBytes = R(BlobLength) - part;
                Header(p, 22, RW(BlobTail), RW(Generation) + 1);
                PutWide(p + 24, RW(BlobOwner)); PutWide(p + 56, RW(BlobHead));
                memory.Write16(p + 64, tailBytes); memory.Write16(p + 66, part); memory.Write16(p + 52, 4 + tailBytes);
                Copy(p + 68, R(BlobBuffer) + part, tailBytes);
                return StagePage(p, RW(BlobTail), 2);
            case 2: return StageBlobHead(p);
            case 4: return Done();
            case 10:
                if (!BlobHeadValid(p)) return Fail(CpxError.Corrupt);
                WW(BlobTail, GetWide(p + 56)); WW(BlobGeneration, GetWide(p + 32));
                W(BlobHeadBytes, BlobPart(R(BlobLength))); W(BlobProgress, 0); W(Phase, 11);
                return StepResult.Pending;
            case 11:
                uint progress = R(BlobProgress), take = R(BlobHeadBytes) - progress;
                if (take > 64) take = 64;
                Copy(R(BlobBuffer) + progress, p + 68 + progress, take); W(BlobProgress, progress + take);
                if (progress + take != R(BlobHeadBytes)) return StepResult.Pending;
                if (RW(BlobTail) == 0) return Done();
                return ReadStaged(RW(BlobTail), p, 12);
            case 12:
                if (!BlobTailValid(p)) return Fail(CpxError.Corrupt);
                W(Phase, 13); return StepResult.Pending;
            case 13:
                uint copied = R(BlobProgress), remaining = R(BlobLength) - copied;
                if (remaining > 64) remaining = 64;
                Copy(R(BlobBuffer) + copied, p + 68 + copied - R(BlobHeadBytes), remaining);
                W(BlobProgress, copied + remaining);
                return copied + remaining == R(BlobLength) ? Done() : StepResult.Pending;
            case 20:
                if (!BlobHeadValid(p)) return Fail(CpxError.Corrupt);
                W(BlobLength, memory.Read16(p + 64)); W(BlobHeadBytes, BlobPart(R(BlobLength)));
                WW(BlobTail, GetWide(p + 56)); WW(BlobGeneration, GetWide(p + 32));
                return RW(BlobTail) == 0 ? ReleaseMetadata(RW(BlobHead), 24) : ReadStaged(RW(BlobTail), p, 21);
            case 21:
                if (!BlobTailValid(p)) return Fail(CpxError.Corrupt);
                return ReleaseMetadata(RW(BlobTail), 22);
            case 22: return ReleaseMetadata(RW(BlobHead), 24);
            case 24: return Done();
            default: return Fail(CpxError.Corrupt);
        }
    }
}
