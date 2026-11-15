namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    private const uint NsParent = 10600, NsDestination = 10608, NsLength = 10616, NsNewLength = 10620, NsDirectory = 10624, NsReplace = 10628, NsReplacing = 10632, NsResult = 10640, NsEntryId = 10648, NsRoot = 10656, NsAncestor = 10664, NsHops = 10672, NsNameScan = 10680, NsEncodedLength = 10684, NsHash = 10688, NsNameReturn = 10692, NsPublished = 10696;
    private uint NsParentRecord => Arena + 10800;
    private uint NsDestinationRecord => Arena + 11056;
    private uint NsObject => Arena + 11312;
    private uint NsVictim => Arena + 11568;
    private uint NsEntry => Arena + 11824;
    private uint NsOldEntry => Arena + 11952;
    private uint NsPrevious => Arena + 12080;
    private uint NsName => Arena + 12208;
    private uint NsNewName => Arena + 12464;
    private uint NsEncoded => Arena + 12720;
    public Wide MutatedObject => RW(NsResult);
    public bool NamespacePublished => memory.Read32(Arena + (NsPublished)) != 0;

    private bool ValidComponent(uint name, uint length, bool creating)
    {
        if (name == 0 || length == 0 || length > 255 || name > uint.MaxValue - length)
            return false;
        if (length <= 2 && memory.Read8(name) == 46 && (length == 1 || memory.Read8(name + 1) == 46))
            return false;
        for (uint n = 0; n < length; n++)
            if (memory.Read8(name + n)is 0 or 47 or 58)
                return false;
        if (creating && length == 19 && memory.Read8(name) == 95 && memory.Read8(name + 1) == 95 && FoldLatin(memory.Read8(name + 2)) == 117)
        {
            bool alias = true;
            for (uint n = 3; n < 19; n++)
            {
                uint c = FoldLatin(memory.Read8(name + n));
                if (!(c is >= 48 and <= 57 or >= 97 and <= 102))
                    alias = false;
            }

            if (alias)
                return false;
        }

        return true;
    }

    public CpxError BeginCreate(Wide parent, uint name, uint length, bool directory = false)
    {
        if (!ValidComponent(name, length, true))
            return CpxError.InvalidArgument;
        CpxError error = StartOperation(CpxOperation.Create);
        if (error != CpxError.None)
            return error;
        WW(NsParent, parent);
        memory.Write32(Arena + (NsLength), length);
        memory.Write32(Arena + (NsDirectory), directory ? 1u : 0u);
        WW(NsResult, default);
        Copy(NsName, name, length);
        if (BufferedWrites)
            memory.Write32(Arena + (Phase), 600);
        return error;
    }

    public CpxError BeginDelete(Wide parent, uint name, uint length)
    {
        if (!ValidComponent(name, length, false))
            return CpxError.InvalidArgument;
        CpxError error = StartOperation(CpxOperation.Delete);
        if (error != CpxError.None)
            return error;
        WW(NsParent, parent);
        memory.Write32(Arena + (NsLength), length);
        WW(NsResult, default);
        Copy(NsName, name, length);
        return error;
    }

    public CpxError BeginRename(Wide parent, uint name, uint length, Wide destination, uint newName, uint newLength, bool replace = false)
    {
        if (!ValidComponent(name, length, false) || !ValidComponent(newName, newLength, true))
            return CpxError.InvalidArgument;
        CpxError error = StartOperation(CpxOperation.Rename);
        if (error != CpxError.None)
            return error;
        WW(NsParent, parent);
        WW(NsDestination, destination);
        memory.Write32(Arena + (NsLength), length);
        memory.Write32(Arena + (NsNewLength), newLength);
        memory.Write32(Arena + (NsReplace), replace ? 1u : 0u);
        memory.Write32(Arena + (NsReplacing), 0);
        WW(NsResult, default);
        Copy(NsName, name, length);
        Copy(NsNewName, newName, newLength);
        if (BufferedWrites && !replace && (((ulong)(parent)) == ((ulong)(destination))))
            memory.Write32(Arena + (Phase), 610);
        return error;
    }

    private StepResult LookupName(ulong parent, uint name, uint length, uint next)
    {
        WW(ObjectKey, parent);
        memory.Write32(Arena + (NameLength), length);
        memory.Write32(Arena + (LookupFoundAt), 0);
        WW(LookupTarget, default);
        Copy(LegacyName, name, length);
        return Call(CpxOperation.Lookup, next);
    }

    private StepResult LoadObject(ulong id, uint next)
    {
        WW(ObjectKey, id);
        return Call(CpxOperation.ReadObject, next);
    }

    private StepResult BuildEntry(uint next)
    {
        memory.Write32(Arena + (NsNameScan), 0);
        memory.Write32(Arena + (NsEncodedLength), 0);
        memory.Write32(Arena + (NsHash), uint.MaxValue);
        memory.Write32(Arena + (NsNameReturn), next);
        memory.Write32(Arena + (Phase), 80);
        return StepResult.Pending;
    }

    private uint HashByte(uint crc, uint value) => (crc >> 8) ^ memory.Read32(CrcTable + ((crc ^ value) & 255) * 4);
    private bool AdjustWide(uint address, uint count, bool add)
    {
        ulong result;
        bool ok = add ? Scalar64.Add(GetWide(address), ((ulong)(count)), out result) : Scalar64.Subtract(GetWide(address), ((ulong)(count)), out result);
        if (ok)
            PutWide(address, result);
        return ok;
    }

    private StepResult NamespaceStep()
    {
        uint phase = memory.Read32(Arena + (Phase)), p = Page(3);
        CpxOperation op = (CpxOperation)memory.Read32(Arena + (OperationAt));
        if (phase is 600 or 601)
            return FastCreateStep();
        if (phase == 610)
        {
            if (TryFastRename())
                return Done();
            memory.Write32(Arena + (Phase), 0);
            return StepResult.Pending;
        }

        switch (phase)
        {
            case 0:
                if (op == CpxOperation.Create && memory.Read32(Arena + (NsDirectory)) == 3)
                    return Call(CpxOperation.EncodeSymlink, 100);
                return Call(CpxOperation.Reclaim, 1);
            case 1:
                return LookupName(RW(NsParent), NsName, memory.Read32(Arena + (NsLength)), 2);
            case 2:
                Copy(NsParentRecord, ObjectRecord, 256);
                if ((memory.Read32(NsParentRecord + 72) & 4) != 0)
                    return Reject(CpxError.Protected);
                if (op == CpxOperation.Create)
                {
                    if (LookupFound)
                        return Reject(CpxError.AlreadyExists);
                    return EnsureReserve(32, 10);
                }

                if (!LookupFound)
                    return Reject(CpxError.NotFound);
                Copy(NsOldEntry, TreeRecord, 128);
                WW(NsResult, LookupId);
                return LoadObject(LookupId, 3);
            case 3:
                if (memory.Read32(Arena + (SourcePage)) == 0)
                    return Fail(CpxError.Corrupt);
                Copy(NsObject, ObjectRecord, 256);
                if ((memory.Read32(NsObject + 72) & 1) != 0)
                    return Reject(CpxError.Protected);
                if (op == CpxOperation.Delete)
                {
                    if (memory.Read8(NsObject + 84) == 2 && !(((ulong)(GetWide(NsObject + 8))) == 0))
                        return Reject(CpxError.DirectoryNotEmpty);
                    return BeginTx(7, 30);
                }

                return LookupName(RW(NsDestination), NsNewName, memory.Read32(Arena + (NsNewLength)), 4);
            case 4:
                Copy(NsDestinationRecord, ObjectRecord, 256);
                if ((memory.Read32(NsDestinationRecord + 72) & 4) != 0)
                    return Reject(CpxError.Protected);
                if (LookupFound && !(((ulong)(GetWide(TreeRecord + 8))) == ((ulong)(GetWide(NsOldEntry + 8)))))
                {
                    if (memory.Read32(Arena + (NsReplace)) == 0)
                        return Reject(CpxError.AlreadyExists);
                    if ((((ulong)(LookupId)) == ((ulong)(RW(NsResult)))))
                        return Done();
                    Copy(NsPrevious, TreeRecord, 128);
                    memory.Write32(Arena + (NsReplacing), 1);
                    return LoadObject(LookupId, 5);
                }

                memory.Write32(Arena + (Phase), 6);
                return StepResult.Pending;
            case 5:
                if (memory.Read32(Arena + (SourcePage)) == 0)
                    return Fail(CpxError.Corrupt);
                Copy(NsVictim, ObjectRecord, 256);
                if ((memory.Read32(NsVictim + 72) & 1) != 0)
                    return Reject(CpxError.Protected);
                if (memory.Read8(NsVictim + 84) != memory.Read8(NsObject + 84))
                    return Reject(CpxError.WrongType);
                if (memory.Read8(NsVictim + 84) == 2 && !(((ulong)(GetWide(NsVictim + 8))) == 0))
                    return Reject(CpxError.DirectoryNotEmpty);
                memory.Write32(Arena + (Phase), 6);
                return StepResult.Pending;
            case 6:
                if (memory.Read8(NsObject + 84) != 2)
                    return EnsureReserve(10, 40);
                WW(NsAncestor, RW(NsDestination));
                WW(NsHops, default);
                memory.Write32(Arena + (Phase), 7);
                return StepResult.Pending;
            case 7:
                if ((((ulong)(RW(NsAncestor))) == ((ulong)(RW(NsResult)))))
                    return Reject(CpxError.InvalidArgument);
                if ((((ulong)(RW(NsAncestor))) == 0))
                    return EnsureReserve(10, 40);
                return LoadObject(RW(NsAncestor), 8);
            case 8:
                if (memory.Read32(Arena + (SourcePage)) == 0 || memory.Read8(ObjectRecord + 84) != 2)
                    return Fail(CpxError.Corrupt);
                WW(NsAncestor, GetWide(ObjectRecord + 40));
                WW(NsHops, RW(NsHops) + 1);
                return ReadStaged(Anchor + 3 * Q, p, 9);
            case 9:
                if (!Control(p, 3, Anchor + 3 * Q) || (RW(NsHops)) >= (GetWide(p + 64)))
                    return Fail(CpxError.Corrupt);
                memory.Write32(Arena + (Phase), 7);
                return StepResult.Pending;
            case 10:
                return BeginTx(39, 11);
            case 11:
                return ReadStaged(Anchor + 3 * Q, p, 12);
            case 12:
                if (!Control(p, 3, Anchor + 3 * Q) || !ReadableGeneration(p))
                    return Fail(CpxError.Corrupt);
                WW(NsResult, GetWide(p + 64));
                WW(NsEntryId, GetWide(p + 72));
                if (!AdjustWide(p + 64, 1, true) || !AdjustWide(p + 72, 1, true) || !AdjustWide(p + 104, 1, true))
                    return Reject(CpxError.ResourceExhausted);
                Clear(NsObject, 256);
                PutWide(NsObject, RW(NsResult));
                memory.Write32(NsObject + 76, 1);
                memory.Write32(NsObject + 80, 1);
                memory.Write8(NsObject + 84, (byte)(memory.Read32(Arena + (NsDirectory)) == 3 ? 3 : memory.Read32(Arena + (NsDirectory)) != 0 ? 2 : 1));
                if (memory.Read32(Arena + (NsDirectory)) == 1)
                    PutWide(NsObject + 40, RW(NsParent));
                return StagePage(p, Anchor + 3 * Q, 13);
            case 13:
                if (memory.Read32(Arena + (NsDirectory)) == 3)
                {
                    PutWide(NsObject + 8, ((ulong)(memory.Read32(Arena + (LinkEncoded)))));
                    if (memory.Read32(Arena + (LinkEncoded)) > 160)
                        return TakeMetadata(102);
                    memory.Write16(NsObject + 88, memory.Read32(Arena + (LinkEncoded)));
                    Copy(NsObject + 96, LinkText, memory.Read32(Arena + (LinkEncoded)));
                }

                return BuildEntry(14);
            case 14:
                return MutateTree(CpxTreeKind.Directory, GetWide(NsParentRecord + 32), RW(NsParent), NsEntry, 0, 15);
            case 15:
                PutWide(NsParentRecord + 32, RW(MutationRoot));
                if (!AdjustWide(NsParentRecord + 8, 1, true))
                    return Fail(CpxError.Corrupt);
                return SaveObject(NsParentRecord, 16);
            case 16:
                return ReadStaged(Anchor + 3 * Q, p, 17);
            case 17:
                if (!Control(p, 3, Anchor + 3 * Q))
                    return Fail(CpxError.Corrupt);
                return MutateTree(CpxTreeKind.Object, GetWide(p + 80), default, NsObject, 0, 18);
            case 18:
                WW(NsRoot, RW(MutationRoot));
                return ReadStaged(Anchor + 3 * Q, p, 19);
            case 19:
                if (!Control(p, 3, Anchor + 3 * Q))
                    return Fail(CpxError.Corrupt);
                PutWide(p + 80, RW(NsRoot));
                return StagePage(p, Anchor + 3 * Q, 20);
            case 20:
                return Call(CpxOperation.CommitTransaction, 99);
            case 30:
                return MutateTree(CpxTreeKind.Directory, GetWide(NsParentRecord + 32), RW(NsParent), NsOldEntry, 2, 31);
            case 31:
                if (!AdjustWide(NsParentRecord + 8, 1, false))
                    return Fail(CpxError.Corrupt);
                return SaveObject(NsParentRecord, 32);
            case 32:
                if (!(((ulong)(GetWide(NsOldEntry + 24))) == 0))
                    return ReleaseMetadata(GetWide(NsOldEntry + 24), 33);
                memory.Write32(Arena + (Phase), 33);
                return StepResult.Pending;
            case 33:
                Copy(NsVictim, NsObject, 256);
                memory.Write32(Arena + (Phase), 60);
                return StepResult.Pending;
            case 40:
                return BeginTx(30, 41);
            case 41:
                WW(NsEntryId, GetWide(NsOldEntry + 8));
                return BuildEntry(42);
            case 42:
                return MutateTree(CpxTreeKind.Directory, GetWide(NsParentRecord + 32), RW(NsParent), NsOldEntry, 2, 43);
            case 43:
                if (memory.Read32(Arena + (NsReplacing)) != 0)
                    return MutateTree(CpxTreeKind.Directory, GetWide(NsDestinationRecord + 32), RW(NsDestination), NsPrevious, 2, 44);
                memory.Write32(Arena + (Phase), 44);
                return StepResult.Pending;
            case 44:
                return MutateTree(CpxTreeKind.Directory, GetWide(NsDestinationRecord + 32), RW(NsDestination), NsEntry, 0, 45);
            case 45:
                PutWide(NsDestinationRecord + 32, RW(MutationRoot));
                if (!(((ulong)(RW(NsParent))) == ((ulong)(RW(NsDestination)))))
                {
                    if (!AdjustWide(NsDestinationRecord + 8, 1, true))
                        return Fail(CpxError.Corrupt);
                }

                if (memory.Read32(Arena + (NsReplacing)) != 0 && !AdjustWide(NsDestinationRecord + 8, 1, false))
                    return Fail(CpxError.Corrupt);
                return SaveObject(NsDestinationRecord, 46);
            case 46:
                if ((((ulong)(RW(NsParent))) == ((ulong)(RW(NsDestination)))))
                {
                    memory.Write32(Arena + (Phase), 48);
                    return StepResult.Pending;
                }

                if (!AdjustWide(NsParentRecord + 8, 1, false))
                    return Fail(CpxError.Corrupt);
                return SaveObject(NsParentRecord, 47);
            case 47:
                if (memory.Read8(NsObject + 84) == 2)
                {
                    PutWide(NsObject + 40, RW(NsDestination));
                    return SaveObject(NsObject, 48);
                }

                memory.Write32(Arena + (Phase), 48);
                return StepResult.Pending;
            case 48:
                if (!(((ulong)(GetWide(NsOldEntry + 24))) == 0))
                    return ReleaseMetadata(GetWide(NsOldEntry + 24), 49);
                memory.Write32(Arena + (Phase), 49);
                return StepResult.Pending;
            case 49:
                if (memory.Read32(Arena + (NsReplacing)) == 0)
                    return Call(CpxOperation.CommitTransaction, 99);
                if (!(((ulong)(GetWide(NsPrevious + 24))) == 0))
                    return ReleaseMetadata(GetWide(NsPrevious + 24), 60);
                memory.Write32(Arena + (Phase), 60);
                return StepResult.Pending;
            case 60:
                return ReadStaged(Anchor + 3 * Q, p, 61);
            case 61:
                if (!Control(p, 3, Anchor + 3 * Q) || memory.Read32(NsVictim + 76) == 0 || memory.Read16(NsVictim + 86) != 0)
                    return Fail(CpxError.Corrupt);
                uint links = memory.Read32(NsVictim + 76) - 1;
                memory.Write32(NsVictim + 76, links);
                if (links == 0)
                {
                    memory.Write16(NsVictim + 86, 1);
                    PutWide(NsVictim + 40, GetWide(p + 88));
                    PutWide(p + 88, GetWide(NsVictim));
                }

                if (!AdjustWide(p + 104, 1, false))
                    return Fail(CpxError.Corrupt);
                return StagePage(p, Anchor + 3 * Q, 62);
            case 62:
                return SaveObject(NsVictim, 63);
            case 63:
                return Call(CpxOperation.CommitTransaction, 64);
            case 64:
                memory.Write32(Arena + (NsPublished), 1);
                return Call(CpxOperation.Reclaim, 99);
            case 80:
                uint name = op == CpxOperation.Rename ? NsNewName : NsName, length = op == CpxOperation.Rename ? memory.Read32(Arena + (NsNewLength)) : memory.Read32(Arena + (NsLength));
                uint scan = memory.Read32(Arena + (NsNameScan)), end = scan + 64, encoded = memory.Read32(Arena + (NsEncodedLength)), hash = memory.Read32(Arena + (NsHash));
                if (end > length)
                    end = length;
                for (; scan < end; scan++)
                {
                    uint c = memory.Read8(name + scan), fold = FoldLatin(c);
                    if (c < 128)
                        memory.Write8(NsEncoded + encoded++, (byte)c);
                    else
                    {
                        memory.Write8(NsEncoded + encoded++, (byte)(0xc0 | (c >> 6)));
                        memory.Write8(NsEncoded + encoded++, (byte)(0x80 | (c & 63)));
                    }

                    if (fold < 128)
                        hash = HashByte(hash, fold);
                    else
                    {
                        hash = HashByte(hash, 0xc0 | (fold >> 6));
                        hash = HashByte(hash, 0x80 | (fold & 63));
                    }
                }

                memory.Write32(Arena + (NsNameScan), scan);
                memory.Write32(Arena + (NsEncodedLength), encoded);
                memory.Write32(Arena + (NsHash), hash);
                if (scan < length)
                    return StepResult.Pending;
                Clear(NsEntry, 128);
                PutWide(NsEntry, (((ulong)(~hash)) << 32 | ((ulong)(0))));
                PutWide(NsEntry + 8, RW(NsEntryId));
                PutWide(NsEntry + 16, RW(NsResult));
                memory.Write16(NsEntry + 32, encoded);
                memory.Write16(NsEntry + 36, length);
                if (encoded <= 80)
                {
                    Copy(NsEntry + 48, NsEncoded, encoded);
                    memory.Write32(Arena + (Phase), memory.Read32(Arena + (NsNameReturn)));
                    return StepResult.Pending;
                }

                return TakeMetadata(81);
            case 81:
                uint page = Page(4);
                ulong address = RW(MetadataResult);
                PutWide(NsEntry + 24, address);
                Header(page, 17, address, RW(Generation) + 1);
                PutWide(page + 24, RW(NsEntryId));
                memory.Write16(page + 64, memory.Read32(Arena + (NsEncodedLength)));
                Copy(page + 68, NsEncoded, memory.Read32(Arena + (NsEncodedLength)));
                memory.Write16(page + 52, memory.Read32(Arena + (NsEncodedLength)) + 4);
                return StagePage(page, address, memory.Read32(Arena + (NsNameReturn)));
            case 99:
                memory.Write32(Arena + (NsPublished), 1);
                return Done();
            case 100:
                return Call(CpxOperation.Reclaim, 1);
            case 102:
                ulong linkPage = RW(MetadataResult);
                uint link = Page(4);
                memory.Write8(NsObject + 85, 3);
                PutWide(NsObject + 96, linkPage);
                Header(link, 19, linkPage, RW(Generation) + 1);
                PutWide(link + 24, RW(NsResult));
                memory.Write16(link + 64, memory.Read32(Arena + (LinkEncoded)));
                Copy(link + 68, LinkText, memory.Read32(Arena + (LinkEncoded)));
                memory.Write16(link + 52, 4 + memory.Read32(Arena + (LinkEncoded)));
                return StagePage(link, linkPage, 103);
            case 103:
                return BuildEntry(14);
            default:
                return Fail(CpxError.Corrupt);
        }
    }
}
