namespace CopperCPX;
public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    private StepResult FastDirectCreateStep()
    {
        if ((memory.Read32(Arena + (Phase))) == 601)
        {
            if (!Control((memory.Read32(Arena + (GeometryPages + (3) * 4))), 4, Anchor + 8 * Q) || !ReadableGeneration((memory.Read32(Arena + (GeometryPages + (3) * 4))))) return Fail(CpxError.Corrupt);
            W(Phase, 600); return StepResult.Pending;
        }
        if (!PrepareDirectCreate(out var pending)) return pending;
        ApplyDirectCreate(); return Done();
    }

    private bool PrepareDirectCreate(out StepResult pending)
    {
        pending = StepResult.Pending;
        ulong parent = ((((ulong)(memory.Read32((Arena) + ((NsParent))))) << 32 | ((ulong)(memory.Read32((Arena) + ((NsParent) + 4))))));
        uint cached = CachedObject(parent), state = CachedMetadata(StateAddress), reserve = CachedMetadata(Anchor + 8 * Q);
        if (cached == 0 || state == 0 || (memory.Read32(Arena + (NsDirectory))) > 1 || (memory.Read32(Arena + (StagedCount))) > 37 || ((((ulong)(memory.Read32((state + 88)))) << 32 | ((ulong)(memory.Read32((state + 88) + 4))))) != 0 ||
            !FastName(NsName, (memory.Read32(Arena + (NsLength))))) return DeclineFastCreate();
        ulong parentAt = ((((ulong)(memory.Read32((cached - 12)))) << 32 | ((ulong)(memory.Read32((cached - 12) + 4))))), dirAt = ((((ulong)(memory.Read32((cached + 32)))) << 32 | ((ulong)(memory.Read32((cached + 32) + 4)))));
        uint inode = CachedMetadata(parentAt), dir = dirAt == 0 ? 0 : CachedMetadata(dirAt);
        if (inode == 0 || memory.Read16(inode + 4) != 23 || ((((ulong)(memory.Read32((inode + 24)))) << 32 | ((ulong)(memory.Read32((inode + 24) + 4))))) != parent ||
            memory.Read8(cached + 84) != 2 || (memory.Read32(cached + 72) & 4) != 0 ||
            dirAt != 0 && !FastDirectoryShape(dir, parent)) return DeclineFastCreate();
        uint entries = dir == 0 ? 0 : memory.Read16(dir + 48);
        if (entries >= (P - 64) >> 7 || ((((ulong)(memory.Read32((cached + 8)))) << 32 | ((ulong)(memory.Read32((cached + 8) + 4))))) != entries ||
            dir != 0 && FastFindName(dir, NsName, (memory.Read32(Arena + (NsLength)))) != -1 ||
            !Scalar64.Add(((((ulong)(memory.Read32((state + 72)))) << 32 | ((ulong)(memory.Read32((state + 72) + 4))))), 1, out _) || !Scalar64.Add(((((ulong)(memory.Read32((state + 104)))) << 32 | ((ulong)(memory.Read32((state + 104) + 4))))), 1, out _) ||
            !Scalar64.Add(((((ulong)(memory.Read32((Arena) + ((Generation))))) << 32 | ((ulong)(memory.Read32((Arena) + ((Generation) + 4)))))), 1, out _)) return DeclineFastCreate();
        if (reserve == 0) { pending = ReadStaged(Anchor + 8 * Q, (memory.Read32(Arena + (GeometryPages + (3) * 4))), 601); return false; }
        uint need = dir == 0 ? 2u : 1u, available = memory.Read16(reserve + 64);
        if (available < need || available > ReserveCapacity) return DeclineFastCreate();
        for (uint n = 0; n < need; n++)
            if (!Allocatable(((((ulong)(memory.Read32((reserve + 72 + (available - 1 - n) * 8)))) << 32 | ((ulong)(memory.Read32((reserve + 72 + (available - 1 - n) * 8) + 4)))))) ||
                FindStaged(((((ulong)(memory.Read32((reserve + 72 + (available - 1 - n) * 8)))) << 32 | ((ulong)(memory.Read32((reserve + 72 + (available - 1 - n) * 8) + 4)))))) >= 0) return DeclineFastCreate();
        ulong address = ((((ulong)(memory.Read32((reserve + 72 + (available - 1) * 8)))) << 32 | ((ulong)(memory.Read32((reserve + 72 + (available - 1) * 8) + 4)))));
        // Retyping an image already released in this buffer needs the ordinary
        // mutation path, which updates its descriptor as well as its header.
        if (FindStaged(address) >= 0 || !InodeCodec(out var codec) ||
            codec.TryAssign(((((ulong)(memory.Read32((state + 64)))) << 32 | ((ulong)(memory.Read32((state + 64) + 4))))), address << (int)((memory.Read32(Arena + (UnitShift))) - (memory.Read32(Arena + (SectorShift)))), out var id, out var following) != CpxError.None)
            return DeclineFastCreate();
        uint plan = CreatePlan;
        PutWide(plan, parent); PutWide(plan + 8, parentAt); PutWide(plan + 16, dirAt); PutWide(plan + 24, id);
        PutWide(plan + 32, ((((ulong)(memory.Read32((state + 72)))) << 32 | ((ulong)(memory.Read32((state + 72) + 4)))))); memory.Write32(plan + 40, state); memory.Write32(plan + 44, inode);
        memory.Write32(plan + 48, reserve); memory.Write32(plan + 52, dir); PutWide(plan + 56, address);
        PutWide(plan + 64, following); memory.Write32(plan + 72, available); memory.Write32(plan + 76, entries);
        return true;
    }

    private void ApplyDirectCreate()
    {
        uint plan = CreatePlan;
        BeginFastGroup();
        uint state = OwnMetadata(memory.Read32(plan + 40), StateAddress, 3, 0);
        uint parent = OwnMetadata(memory.Read32(plan + 44), ((((ulong)(memory.Read32((plan + 8)))) << 32 | ((ulong)(memory.Read32((plan + 8) + 4))))), 23, ((((ulong)(memory.Read32((plan)))) << 32 | ((ulong)(memory.Read32((plan) + 4))))));
        uint reserve = OwnMetadata(memory.Read32(plan + 48), Anchor + 8 * Q, 4, 0);
        uint available = memory.Read32(plan + 72) - 1, dir = memory.Read32(plan + 52);
        Clear(reserve + 72 + available * 8, 8);
        uint fresh = OwnMetadata(0, ((((ulong)(memory.Read32((plan + 56)))) << 32 | ((ulong)(memory.Read32((plan + 56) + 4))))), 23, ((((ulong)(memory.Read32((plan + 24)))) << 32 | ((ulong)(memory.Read32((plan + 24) + 4))))));
        if (dir == 0)
        {
            PutWide(plan + 16, GetWide(reserve + 72 + (--available) * 8)); Clear(reserve + 72 + available * 8, 8);
            dir = OwnMetadata(0, ((((ulong)(memory.Read32((plan + 16)))) << 32 | ((ulong)(memory.Read32((plan + 16) + 4))))), 13, ((((ulong)(memory.Read32((plan)))) << 32 | ((ulong)(memory.Read32((plan) + 4)))))); PutWide(dir + 24, ((((ulong)(memory.Read32((plan)))) << 32 | ((ulong)(memory.Read32((plan) + 4))))));
        }
        else dir = OwnMetadata(dir, ((((ulong)(memory.Read32((plan + 16)))) << 32 | ((ulong)(memory.Read32((plan + 16) + 4))))), 13, ((((ulong)(memory.Read32((plan)))) << 32 | ((ulong)(memory.Read32((plan) + 4))))));
        PutWide(NsEntry + 8, ((((ulong)(memory.Read32((plan + 32)))) << 32 | ((ulong)(memory.Read32((plan + 32) + 4)))))); PutWide(NsEntry + 16, ((((ulong)(memory.Read32((plan + 24)))) << 32 | ((ulong)(memory.Read32((plan + 24) + 4))))));
        InsertFastEntry(dir, memory.Read32(plan + 76));
        PutWide(parent + 64 + 32, ((((ulong)(memory.Read32((plan + 16)))) << 32 | ((ulong)(memory.Read32((plan + 16) + 4)))))); PutWide(parent + 64 + 8, memory.Read32(plan + 76) + 1);
        Clear(NsObject, 256); PutWide(NsObject, ((((ulong)(memory.Read32((plan + 24)))) << 32 | ((ulong)(memory.Read32((plan + 24) + 4)))))); memory.Write32(NsObject + 76, 1); memory.Write32(NsObject + 80, 1);
        memory.Write8(NsObject + 84, (memory.Read32(Arena + (NsDirectory))) != 0 ? (byte)2 : (byte)1);
        if ((memory.Read32(Arena + (NsDirectory))) != 0) PutWide(NsObject + 40, ((((ulong)(memory.Read32((plan)))) << 32 | ((ulong)(memory.Read32((plan) + 4))))));
        InodeHeader(fresh, ((((ulong)(memory.Read32((plan + 56)))) << 32 | ((ulong)(memory.Read32((plan + 56) + 4))))), NsObject);
        memory.Write16(reserve + 64, available); PutWide(state + 64, ((((ulong)(memory.Read32((plan + 64)))) << 32 | ((ulong)(memory.Read32((plan + 64) + 4))))));
        PutWide(state + 72, ((((ulong)(memory.Read32((plan + 32)))) << 32 | ((ulong)(memory.Read32((plan + 32) + 4))))) + 1); AdjustWide(state + 104, 1, true);
        CacheBufferedObjects(parent, ((((ulong)(memory.Read32((plan + 8)))) << 32 | ((ulong)(memory.Read32((plan + 8) + 4)))))); CacheBufferedObjects(fresh, ((((ulong)(memory.Read32((plan + 56)))) << 32 | ((ulong)(memory.Read32((plan + 56) + 4))))));
        WW(NsResult, ((((ulong)(memory.Read32((plan + 24)))) << 32 | ((ulong)(memory.Read32((plan + 24) + 4)))))); WW(NsEntryId, ((((ulong)(memory.Read32((plan + 32)))) << 32 | ((ulong)(memory.Read32((plan + 32) + 4)))))); W(NsPublished, 1);
    }
}

