namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    private uint ShadowCatalogueTemporary => BufferedUndo(0) + 5120 + memory.Read32((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 72) * (memory.Read32(Arena + (PageBytes)));

    private void ClearShadowDescriptorFlags(uint descriptors, uint count)
    {
        for (uint n = 0; n < count; n++) PutWide(descriptors + n * 32 + 24, 0);
    }

    // One dirty pair per turn. The saved catalogue is immutable during this
    // continuation; overflow restores it before building a general map.
    private StepResult ShadowCatalogueUpdate(uint descriptors)
    {
        uint cursor = memory.Read32(Arena + (Cursor)), storage = (memory.Read32(Arena + (CheckpointPolicy)) & ~7u), count = memory.Read32(storage + 60);
        if (cursor == count)
        {
            ClearShadowDescriptorFlags(descriptors, count);
            return BeginShadowKernel(CpxCheckpointCatalogue<TMemory>.InlineRoot);
        }
        uint descriptor = descriptors + cursor * 32, image = memory.Read32(descriptor + 16);
        ulong pair = GetWide(descriptor + 8) >> (int)memory.Read32(storage + 76);
        var old = new CpxCheckpointCatalogue<TMemory>(memory, ShadowCatalogueTemporary);
        uint previous = old.Lookup(pair), inode = memory.Read16(image + 4) == 23 && GetWide(image + 24) != 0 ? 2u : 0u;
        memory.Write32(descriptor + 20, previous & 1);
        var next = new CpxCheckpointCatalogue<TMemory>(memory, ((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 288 + 3 * (memory.Read32(Arena + (PageBytes)))) + 496);
        if (!next.Set(pair, inode | (1u - (previous & 1))))
        {
            Copy(((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 288 + 3 * (memory.Read32(Arena + (PageBytes)))) + 496, ShadowCatalogueTemporary, CpxCheckpointCatalogue<TMemory>.Bytes);
            memory.Write32(storage + 20, count); memory.Write32(Arena + (Cursor), 0); memory.Write32(Arena + (Phase), 105); return StepResult.Pending;
        }
        memory.Write32(Arena + (Cursor), cursor + 1); return StepResult.Pending;
    }

    private StepResult ShadowCataloguePromote(uint descriptors)
    {
        uint storage = (memory.Read32(Arena + (CheckpointPolicy)) & ~7u), normal = memory.Read32(storage + 60), cursor = memory.Read32(Arena + (Cursor));
        uint shift = memory.Read32(storage + 76);
        var old = new CpxCheckpointCatalogue<TMemory>(memory, ShadowCatalogueTemporary);
        if (cursor < normal)
        {
            uint d = descriptors + cursor * 32;
            uint flags = old.Lookup(GetWide(d + 8) >> (int)shift);
            memory.Write32(d + 20, flags & 1); PutWide(d + 24, flags);
            memory.Write32(Arena + (Cursor), cursor + 1); return StepResult.Pending;
        }
        if (cursor - normal < old.Count)
        {
            ulong entry = old.Entry(cursor - normal), pair = (entry >> 2) << (int)shift;
            bool dirty = false;
            for (uint n = 0; n < normal; n++) if (GetWide(descriptors + n * 32 + 8) == pair) { dirty = true; break; }
            if (!dirty)
            {
                uint count = memory.Read32(storage + 20), d = descriptors + count * 32;
                PutWide(d, pair >> (int)(memory.Read32(Arena + (UnitShift)) - memory.Read32(Arena + (SectorShift)))); PutWide(d + 8, pair);
                memory.Write32(d + 16, 0); memory.Write32(d + 20, (uint)entry & 3); PutWide(d + 24, 0);
                memory.Write32(storage + 20, count + 1);
            }
            memory.Write32(Arena + (Cursor), cursor + 1); return StepResult.Pending;
        }
        GetShadowGeometry(out var geometry);
        var writer = new CpxShadowMapWriteCursor<TMemory>(memory, BufferedUndo(0), geometry);
        CpxError error = writer.BeginCatalogue(RW(Generation), RW(Identity), descriptors, memory.Read32(storage + 20),
            BufferedUndo(0) + 5120, (memory.Read32(Arena + (GeometryCrc))), BufferedStorageAddress);
        if (error != CpxError.None) return Fail(error);
        memory.Write32(storage + 4, 1); memory.Write32(storage, 2); memory.Write32(Arena + (Phase), 102); return StepResult.Pending;
    }
}
