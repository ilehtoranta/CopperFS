namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    private const uint NsInode = 10704;
    private bool DirectInodes => ((memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0) && memory.Read32((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 56) >= 3;

    private bool InodeCodec(out CpxInodeIdentity codec)
    {
        codec = CpxInodeIdentity.Restore(ref memory, (memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 224);
        return codec.SlotBits != 0;
    }

    private bool InodeAddress(ulong id, out ulong address)
    {
        address = 0;
        if (!InodeCodec(out var codec) || !codec.TryPair(id, out var pair)) return false;
        address = (ulong)pair >> (int)(memory.Read32(Arena + (UnitShift)) - memory.Read32(Arena + (SectorShift)));
        return Allocatable(address);
    }

    private bool ValidInode(uint page, ulong address)
        => ValidPage(page, address) && memory.Read16(page + 4) == 23 && ReadableGeneration(page) && memory.Read16(page + 48) == 1 &&
            memory.Read16(page + 50) == 0 && memory.Read16(page + 52) == 256 &&
            ((((ulong)(memory.Read32((page + 24)))) << 32 | ((ulong)(memory.Read32((page + 24) + 4))))) == ((((ulong)(memory.Read32((page + 64)))) << 32 | ((ulong)(memory.Read32((page + 64) + 4))))) && ((((ulong)(memory.Read32((page + 24)))) << 32 | ((ulong)(memory.Read32((page + 24) + 4))))) != 0;

    private StepResult DirectObjectStep()
    {
        uint p = (memory.Read32(Arena + (GeometryPages + (0) * 4)));
        switch (memory.Read32(Arena + (Phase)))
        {
            case 0:
                memory.Write32(Arena + (SourcePage), 0);
                if (BufferedWrites && ReadBufferedObject(((((ulong)(memory.Read32((Arena) + ((ObjectKey))))) << 32 | ((ulong)(memory.Read32((Arena) + ((ObjectKey) + 4)))))))) return Done();
                if (!InodeAddress(((((ulong)(memory.Read32((Arena) + ((ObjectKey))))) << 32 | ((ulong)(memory.Read32((Arena) + ((ObjectKey) + 4)))))), out var address)) return Done();
                WW(TreeAddress, address);
                return ReadStaged(address, p, 1, true);
            case 1:
                if (memory.Read32((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 4) == 0) return Done();
                if (!ValidInode(p, ((((ulong)(memory.Read32((Arena) + ((TreeAddress))))) << 32 | ((ulong)(memory.Read32((Arena) + ((TreeAddress) + 4)))))))) return Fail(CpxError.Corrupt);
                if (((((ulong)(memory.Read32((p + 24)))) << 32 | ((ulong)(memory.Read32((p + 24) + 4))))) != ((((ulong)(memory.Read32((Arena) + ((ObjectKey))))) << 32 | ((ulong)(memory.Read32((Arena) + ((ObjectKey) + 4))))))) return Done();
                CacheBufferedObjects(p, ((((ulong)(memory.Read32((Arena) + ((TreeAddress))))) << 32 | ((ulong)(memory.Read32((Arena) + ((TreeAddress) + 4)))))));
                Copy(ObjectRecord, p + 64, 256); memory.Write32(Arena + (TreeSlot), 1); memory.Write32(Arena + (SourcePage), 1);
                return Done();
            default: return Fail(CpxError.Corrupt);
        }
    }

    private void InodeHeader(uint p, ulong address, uint record)
    {
        Header(p, 23, address, ((((ulong)(memory.Read32((Arena) + ((Generation))))) << 32 | ((ulong)(memory.Read32((Arena) + ((Generation) + 4)))))) + 1);
        PutWide(p + 24, ((((ulong)(memory.Read32((record)))) << 32 | ((ulong)(memory.Read32((record) + 4))))));
        memory.Write16(p + 48, 1); memory.Write16(p + 52, 256);
        Copy(p + 64, record, 256);
    }

    private StepResult DirectSaveObjectStep()
    {
        uint p = (memory.Read32(Arena + (GeometryPages + (0) * 4)));
        switch (memory.Read32(Arena + (Phase)))
        {
            case 0:
                if (!InodeAddress(((((ulong)(memory.Read32(((Arena + 10112))))) << 32 | ((ulong)(memory.Read32(((Arena + 10112)) + 4))))), out var address)) return Fail(CpxError.Corrupt);
                if (TryUpdateBufferedInode(address, (Arena + 10112), out var updated)) return updated;
                WW(SaveRoot, address);
                return ReadStaged(address, p, 1, true);
            case 1:
                if (memory.Read32((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 4) == 0 || !ValidInode(p, ((((ulong)(memory.Read32((Arena) + ((SaveRoot))))) << 32 | ((ulong)(memory.Read32((Arena) + ((SaveRoot) + 4))))))) ||
                    ((((ulong)(memory.Read32((p + 24)))) << 32 | ((ulong)(memory.Read32((p + 24) + 4))))) != ((((ulong)(memory.Read32(((Arena + 10112))))) << 32 | ((ulong)(memory.Read32(((Arena + 10112)) + 4)))))) return Fail(CpxError.Corrupt);
                Copy(p + 64, (Arena + 10112), 256);
                return StagePage(p, ((((ulong)(memory.Read32((Arena) + ((SaveRoot))))) << 32 | ((ulong)(memory.Read32((Arena) + ((SaveRoot) + 4)))))), 2);
            case 2: return Done();
            default: return Fail(CpxError.Corrupt);
        }
    }

    private StepResult DirectMutationStep()
    {
        uint p = (memory.Read32(Arena + (GeometryPages + (1) * 4))), mode = memory.Read32(Arena + (MutationMode));
        if (!InodeAddress(((((ulong)(memory.Read32(((Arena + 9728))))) << 32 | ((ulong)(memory.Read32(((Arena + 9728)) + 4))))), out var address)) return Fail(CpxError.Corrupt);
        switch (memory.Read32(Arena + (Phase)))
        {
            case 0:
                if (mode == 0)
                {
                    if (address != ((((ulong)(memory.Read32((Arena) + ((NsInode))))) << 32 | ((ulong)(memory.Read32((Arena) + ((NsInode) + 4)))))) || ((((ulong)(memory.Read32(((Arena + 9728))))) << 32 | ((ulong)(memory.Read32(((Arena + 9728)) + 4))))) == 1) return Fail(CpxError.Corrupt);
                    InodeHeader(p, address, (Arena + 9728));
                    return StagePage(p, address, 3);
                }
                if (mode == 1 && TryUpdateBufferedInode(address, (Arena + 9728), out var updated)) return updated;
                return ReadStaged(address, p, 1, true);
            case 1:
                if (memory.Read32((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 4) == 0 || !ValidInode(p, address) ||
                    ((((ulong)(memory.Read32((p + 24)))) << 32 | ((ulong)(memory.Read32((p + 24) + 4))))) != ((((ulong)(memory.Read32(((Arena + 9728))))) << 32 | ((ulong)(memory.Read32(((Arena + 9728)) + 4)))))) return Fail(CpxError.Corrupt);
                if (mode == 1)
                {
                    Copy(p + 64, (Arena + 9728), 256);
                    return StagePage(p, address, 3);
                }
                if (mode != 2 || ((((ulong)(memory.Read32(((Arena + 9728))))) << 32 | ((ulong)(memory.Read32(((Arena + 9728)) + 4))))) == 1) return Fail(CpxError.Corrupt);
                // Publish a selector without the inode flag in the same root as
                // namespace removal and reserve/allocation release.
                Header(p, 24, address, ((((ulong)(memory.Read32((Arena) + ((Generation))))) << 32 | ((ulong)(memory.Read32((Arena) + ((Generation) + 4)))))) + 1);
                PutWide(p + 24, ((((ulong)(memory.Read32(((Arena + 9728))))) << 32 | ((ulong)(memory.Read32(((Arena + 9728)) + 4))))));
                return StagePage(p, address, 2);
            case 2: return ReleaseMetadata(address, 3);
            case 3: return Done();
            default: return Fail(CpxError.Corrupt);
        }
    }

    // A dirty inode already belongs to this retirement group. Update its
    // record directly after admission, keeping the same per-operation undo
    // snapshot. This avoids a full-page scratch read and restage for every
    // record edit; the final publication still authenticates and seals it.
    private bool TryUpdateBufferedInode(ulong address, uint record, out StepResult result)
    {
        result = StepResult.Pending;
        if (!BufferedWrites) return false;
        int slot = FindStaged(address);
        if (slot < 0) return false;
        uint image = (memory.Read32((Arena) + (BufferAddress)) + 4096 + ((uint)slot) * (memory.Read32((Arena) + (PageBytes))));
        ulong id = ((((ulong)(memory.Read32((record)))) << 32 | ((ulong)(memory.Read32((record) + 4)))));
        if (memory.Read32(image) != (((memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0) ? 0x43505835u : CpxArena.Magic) || memory.Read16(image + 6) != 5 || memory.Read16(image + 4) != 23 ||
            ((((ulong)(memory.Read32((image + 16)))) << 32 | ((ulong)(memory.Read32((image + 16) + 4))))) != address || ((((ulong)(memory.Read32((image + 24)))) << 32 | ((ulong)(memory.Read32((image + 24) + 4))))) != id || ((((ulong)(memory.Read32((image + 64)))) << 32 | ((ulong)(memory.Read32((image + 64) + 4))))) != id ||
            ((((ulong)(memory.Read32((image + 32)))) << 32 | ((ulong)(memory.Read32((image + 32) + 4))))) != ((((ulong)(memory.Read32((Arena) + ((Generation))))) << 32 | ((ulong)(memory.Read32((Arena) + ((Generation) + 4)))))) + 1 || memory.Read16(image + 48) != 1 ||
            memory.Read16(image + 50) != 0 || memory.Read16(image + 52) != 256)
        { result = Fail(CpxError.Corrupt); return true; }
        SaveBufferedUndo((uint)slot);
        InvalidateBufferedClean(address);
        Copy(image + 64, record, 256); memory.Write32(image + 8, 0);
        memory.Write32((memory.Read32(Arena + (GeometryIndex))) + (uint)slot * 32 + 20, 0);
        CacheBufferedObjects(image, address);
        result = Done(); return true;
    }

    // At most 32 entries. The lowest physical slot is popped first, so fresh
    // identities do not consume an entire serial epoch per inode allocation.
    private void SortInodeReserve(uint p)
    {
        uint count = memory.Read16(p + 64);
        for (uint n = 1; n < count; n++)
        {
            ulong value = ((((ulong)(memory.Read32((p + 72 + n * 8)))) << 32 | ((ulong)(memory.Read32((p + 72 + n * 8) + 4))))); uint at = n;
            while (at > 0 && ((((ulong)(memory.Read32((p + 72 + (at - 1) * 8)))) << 32 | ((ulong)(memory.Read32((p + 72 + (at - 1) * 8) + 4))))) < value)
            { PutWide(p + 72 + at * 8, ((((ulong)(memory.Read32((p + 72 + (at - 1) * 8)))) << 32 | ((ulong)(memory.Read32((p + 72 + (at - 1) * 8) + 4)))))); at--; }
            PutWide(p + 72 + at * 8, value);
        }
    }
}
