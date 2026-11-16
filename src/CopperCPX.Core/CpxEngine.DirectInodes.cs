namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    private const uint NsInode = 10704;
    private bool DirectInodes => ShadowVolume && memory.Read32(ShadowStorage + 56) == 3;

    private bool InodeCodec(out CpxInodeIdentity codec)
    {
        codec = CpxInodeIdentity.Restore(ref memory, ShadowStorage + 224);
        return codec.SlotBits != 0;
    }

    private bool InodeAddress(ulong id, out ulong address)
    {
        address = 0;
        if (!InodeCodec(out var codec) || !codec.TryPair(id, out var pair)) return false;
        address = (ulong)pair >> (int)(R(UnitShift) - R(SectorShift));
        return Allocatable(address);
    }

    private bool ValidInode(uint page, ulong address)
        => ValidPage(page, address) && memory.Read16(page + 4) == 23 && ReadableGeneration(page) && memory.Read16(page + 48) == 1 &&
            memory.Read16(page + 50) == 0 && memory.Read16(page + 52) == 256 &&
            GetWide(page + 24) == GetWide(page + 64) && GetWide(page + 24) != 0;

    private StepResult DirectObjectStep()
    {
        uint p = Page(0);
        switch (R(Phase))
        {
            case 0:
                W(SourcePage, 0);
                if (BufferedWrites && ReadBufferedObject(RW(ObjectKey))) return Done();
                if (!InodeAddress(RW(ObjectKey), out var address)) return Done();
                WW(TreeAddress, address);
                return ReadStaged(address, p, 1, true);
            case 1:
                if (memory.Read32(ShadowStorage + 4) == 0) return Done();
                if (!ValidInode(p, RW(TreeAddress))) return Fail(CpxError.Corrupt);
                if (GetWide(p + 24) != RW(ObjectKey)) return Done();
                CacheBufferedObjects(p, RW(TreeAddress));
                Copy(ObjectRecord, p + 64, 256); W(TreeSlot, 1); W(SourcePage, 1);
                return Done();
            default: return Fail(CpxError.Corrupt);
        }
    }

    private void InodeHeader(uint p, ulong address, uint record)
    {
        Header(p, 23, address, RW(Generation) + 1);
        PutWide(p + 24, GetWide(record));
        memory.Write16(p + 48, 1); memory.Write16(p + 52, 256);
        Copy(p + 64, record, 256);
    }

    private StepResult DirectSaveObjectStep()
    {
        uint p = Page(0);
        switch (R(Phase))
        {
            case 0:
                if (!InodeAddress(GetWide(SaveRecord), out var address)) return Fail(CpxError.Corrupt);
                WW(SaveRoot, address);
                return ReadStaged(address, p, 1, true);
            case 1:
                if (memory.Read32(ShadowStorage + 4) == 0 || !ValidInode(p, RW(SaveRoot)) ||
                    GetWide(p + 24) != GetWide(SaveRecord)) return Fail(CpxError.Corrupt);
                Copy(p + 64, SaveRecord, 256);
                return StagePage(p, RW(SaveRoot), 2);
            case 2: return Done();
            default: return Fail(CpxError.Corrupt);
        }
    }

    private StepResult DirectMutationStep()
    {
        uint p = Page(1), mode = R(MutationMode);
        if (!InodeAddress(GetWide(MutationRecord), out var address)) return Fail(CpxError.Corrupt);
        switch (R(Phase))
        {
            case 0:
                if (mode == 0)
                {
                    if (address != RW(NsInode) || GetWide(MutationRecord) == 1) return Fail(CpxError.Corrupt);
                    InodeHeader(p, address, MutationRecord);
                    return StagePage(p, address, 3);
                }
                return ReadStaged(address, p, 1, true);
            case 1:
                if (memory.Read32(ShadowStorage + 4) == 0 || !ValidInode(p, address) ||
                    GetWide(p + 24) != GetWide(MutationRecord)) return Fail(CpxError.Corrupt);
                if (mode == 1)
                {
                    Copy(p + 64, MutationRecord, 256);
                    return StagePage(p, address, 3);
                }
                if (mode != 2 || GetWide(MutationRecord) == 1) return Fail(CpxError.Corrupt);
                // Publish a selector without the inode flag in the same root as
                // namespace removal and reserve/allocation release.
                Header(p, 24, address, RW(Generation) + 1);
                PutWide(p + 24, GetWide(MutationRecord));
                return StagePage(p, address, 2);
            case 2: return ReleaseMetadata(address, 3);
            case 3: return Done();
            default: return Fail(CpxError.Corrupt);
        }
    }

    // At most 32 entries. The lowest physical slot is popped first, so fresh
    // identities do not consume an entire serial epoch per inode allocation.
    private void SortInodeReserve(uint p)
    {
        uint count = memory.Read16(p + 64);
        for (uint n = 1; n < count; n++)
        {
            ulong value = GetWide(p + 72 + n * 8); uint at = n;
            while (at > 0 && GetWide(p + 72 + (at - 1) * 8) < value)
            { PutWide(p + 72 + at * 8, GetWide(p + 72 + (at - 1) * 8)); at--; }
            PutWide(p + 72 + at * 8, value);
        }
    }
}
