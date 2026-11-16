namespace CopperCPX;
public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    private StepResult FastDirectCreateStep()
    {
        if (R(Phase) == 601)
        {
            if (!Control(Page(3), 4, Anchor + 8 * Q) || !ReadableGeneration(Page(3))) return Fail(CpxError.Corrupt);
            W(Phase, 600); return StepResult.Pending;
        }
        if (!PrepareDirectCreate(out var pending)) return pending;
        ApplyDirectCreate(); return Done();
    }

    private bool PrepareDirectCreate(out StepResult pending)
    {
        pending = StepResult.Pending;
        ulong parent = RW(NsParent);
        uint cached = CachedObject(parent), state = CachedMetadata(StateAddress), reserve = CachedMetadata(Anchor + 8 * Q);
        if (cached == 0 || state == 0 || R(NsDirectory) > 1 || R(StagedCount) > 37 || GetWide(state + 88) != 0 ||
            !FastName(NsName, R(NsLength))) return DeclineFastCreate();
        ulong parentAt = GetWide(cached - 12), dirAt = GetWide(cached + 32);
        uint inode = CachedMetadata(parentAt), dir = dirAt == 0 ? 0 : CachedMetadata(dirAt);
        if (inode == 0 || memory.Read16(inode + 4) != 23 || GetWide(inode + 24) != parent ||
            memory.Read8(cached + 84) != 2 || (memory.Read32(cached + 72) & 4) != 0 ||
            dirAt != 0 && !FastDirectoryShape(dir, parent)) return DeclineFastCreate();
        uint entries = dir == 0 ? 0 : memory.Read16(dir + 48);
        if (entries >= (P - 64) >> 7 || GetWide(cached + 8) != entries ||
            dir != 0 && FastFindName(dir, NsName, R(NsLength)) != -1 ||
            !Scalar64.Add(GetWide(state + 72), 1, out _) || !Scalar64.Add(GetWide(state + 104), 1, out _) ||
            !Scalar64.Add(RW(Generation), 1, out _)) return DeclineFastCreate();
        if (reserve == 0) { pending = ReadStaged(Anchor + 8 * Q, Page(3), 601); return false; }
        uint need = dir == 0 ? 2u : 1u, available = memory.Read16(reserve + 64);
        if (available < need || available > ReserveCapacity) return DeclineFastCreate();
        for (uint n = 0; n < need; n++)
            if (!Allocatable(GetWide(reserve + 72 + (available - 1 - n) * 8)) ||
                FindStaged(GetWide(reserve + 72 + (available - 1 - n) * 8)) >= 0) return DeclineFastCreate();
        ulong address = GetWide(reserve + 72 + (available - 1) * 8);
        // Retyping an image already released in this buffer needs the ordinary
        // mutation path, which updates its descriptor as well as its header.
        if (FindStaged(address) >= 0 || !InodeCodec(out var codec) ||
            codec.TryAssign(GetWide(state + 64), address << (int)(R(UnitShift) - R(SectorShift)), out var id, out var following) != CpxError.None)
            return DeclineFastCreate();
        uint plan = CreatePlan;
        PutWide(plan, parent); PutWide(plan + 8, parentAt); PutWide(plan + 16, dirAt); PutWide(plan + 24, id);
        PutWide(plan + 32, GetWide(state + 72)); memory.Write32(plan + 40, state); memory.Write32(plan + 44, inode);
        memory.Write32(plan + 48, reserve); memory.Write32(plan + 52, dir); PutWide(plan + 56, address);
        PutWide(plan + 64, following); memory.Write32(plan + 72, available); memory.Write32(plan + 76, entries);
        return true;
    }

    private void ApplyDirectCreate()
    {
        uint plan = CreatePlan;
        BeginFastGroup();
        uint state = OwnMetadata(memory.Read32(plan + 40), StateAddress, 3, 0);
        uint parent = OwnMetadata(memory.Read32(plan + 44), GetWide(plan + 8), 23, GetWide(plan));
        uint reserve = OwnMetadata(memory.Read32(plan + 48), Anchor + 8 * Q, 4, 0);
        uint available = memory.Read32(plan + 72) - 1, dir = memory.Read32(plan + 52);
        Clear(reserve + 72 + available * 8, 8);
        uint fresh = OwnMetadata(0, GetWide(plan + 56), 23, GetWide(plan + 24));
        if (dir == 0)
        {
            PutWide(plan + 16, GetWide(reserve + 72 + (--available) * 8)); Clear(reserve + 72 + available * 8, 8);
            dir = OwnMetadata(0, GetWide(plan + 16), 13, GetWide(plan)); PutWide(dir + 24, GetWide(plan));
        }
        else dir = OwnMetadata(dir, GetWide(plan + 16), 13, GetWide(plan));
        PutWide(NsEntry + 8, GetWide(plan + 32)); PutWide(NsEntry + 16, GetWide(plan + 24));
        InsertFastEntry(dir, memory.Read32(plan + 76));
        PutWide(parent + 64 + 32, GetWide(plan + 16)); PutWide(parent + 64 + 8, memory.Read32(plan + 76) + 1);
        Clear(NsObject, 256); PutWide(NsObject, GetWide(plan + 24)); memory.Write32(NsObject + 76, 1); memory.Write32(NsObject + 80, 1);
        memory.Write8(NsObject + 84, R(NsDirectory) != 0 ? (byte)2 : (byte)1);
        if (R(NsDirectory) != 0) PutWide(NsObject + 40, GetWide(plan));
        InodeHeader(fresh, GetWide(plan + 56), NsObject);
        memory.Write16(reserve + 64, available); PutWide(state + 64, GetWide(plan + 64));
        PutWide(state + 72, GetWide(plan + 32) + 1); AdjustWide(state + 104, 1, true);
        CacheBufferedObjects(parent, GetWide(plan + 8)); CacheBufferedObjects(fresh, GetWide(plan + 56));
        WW(NsResult, GetWide(plan + 24)); WW(NsEntryId, GetWide(plan + 32)); W(NsPublished, 1);
    }
}

