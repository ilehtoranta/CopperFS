namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // A single written extent can retire its suffix atomically with the inode.
    // Admission is read-only; after it succeeds no I/O or fallible operation is
    // needed. Larger or incomplete plans use the ordinary orphan continuation.
    private bool TryFastShrink(uint record, ulong size)
    {
        if (record == 0 || !BufferedWrites || !DirectInodes || R(StagedCount) > 38 ||
            (size >> 32) != 0 || size <= 160 || memory.Read8(record + 84) != 1 ||
            memory.Read8(record + 85) != 1 || memory.Read16(record + 86) != 0 ||
            memory.Read16(record + 90) != 1 || memory.Read32(record + 76) == 0 ||
            (memory.Read32(record + 72) & 4) != 0 || GetWide(record + 24) != 0 ||
            GetWide(record + 96) != 0 || memory.Read32(record + 116) != 0)
            return false;
        ulong old = GetWide(record + 8), physical = GetWide(record + 104);
        uint shift = R(UnitShift), mask = (1u << (int)shift) - 1;
        if (size >= old || (old >> 32) != 0 || (physical >> 32) != 0 ||
            old > uint.MaxValue - mask || size > uint.MaxValue - mask)
            return false;
        uint allocated = memory.Read32(record + 112), keep = ((uint)size + mask) >> (int)shift;
        if (allocated == 0 || allocated > 65536 || allocated != ((uint)old + mask) >> (int)shift ||
            GetWide(record + 16) != allocated || keep >= allocated || allocated - keep > 128 ||
            physical > uint.MaxValue - allocated)
            return false;
        uint first = (uint)physical + keep, count = allocated - keep, last = first + count - 1;
        if ((first & ~BitmapWindowMask) != (last & ~BitmapWindowMask) ||
            ((uint)physical >> 16) != (last >> 16) || !DataUnit(physical) || !DataUnit(last))
            return false;
        ulong group = (ulong)(first & 0xffff0000u), descriptor = group == 0 ? 131072u >> (int)shift : group;
        ulong inodeAt = GetWide(record - 12), bitmapAt = Bitmap(first), id = GetWide(record);
        uint state = CachedMetadata(StateAddress), inode = CachedMetadata(inodeAt);
        uint bitmap = CachedMetadata(bitmapAt), counts = CachedMetadata(descriptor);
        if (state == 0 || inode == 0 || bitmap == 0 || counts == 0 ||
            !InodeAddress(id, out var expected) || expected != inodeAt ||
            memory.Read16(inode + 4) != 23 || GetWide(inode + 24) != id || GetWide(inode + 64) != id ||
            memory.Read16(state + 4) != 3 || GetWide(state + 16) != StateAddress || GetWide(state + 88) != 0 ||
            memory.Read16(bitmap + 4) != 7 || GetWide(bitmap + 16) != bitmapAt || GetWide(bitmap + 24) != group ||
            memory.Read32(bitmap + 12) != ((first & 65535) >> (int)BitmapWindowShift) ||
            memory.Read16(bitmap + 52) != BitmapPayloadBytes || GetWide(counts + 16) != descriptor ||
            memory.Read16(counts + 4) != 6 || GetWide(counts + 24) != group ||
            memory.Read16(counts + 52) != GroupPayloadBytes ||
            !Scalar64.Add(GetWide(state + 96), count, out var free) || free > RW(Units) ||
            !Scalar64.Add(RW(Generation), 1, out _))
            return false;
        uint window = 80 + ((first & 65535) >> (int)BitmapWindowShift) * 4;
        if (memory.Read32(counts + 68) > 65536 - count ||
            memory.Read32(counts + window) > BitmapWindowUnits - count)
            return false;
        for (uint n = 0; n < count; n++)
        {
            uint bit = (first + n) & BitmapWindowMask;
            if ((memory.Read8(bitmap + 64 + (bit >> 3)) & (0x80u >> (int)(bit & 7))) == 0)
                return false;
        }

        // CachedMetadata returns only authenticated clean images or images
        // owned by this buffered group. Dirty images deliberately have CRC=0.
        BeginFastGroup();
        state = OwnMetadata(state, StateAddress, 3, 0);
        inode = OwnMetadata(inode, inodeAt, 23, id);
        bitmap = OwnMetadata(bitmap, bitmapAt, 7, group);
        counts = OwnMetadata(counts, descriptor, 6, group);
        // Old data stays unavailable for new allocation until publication.
        NoteBufferedAllocation(first, count, false);
        for (uint n = 0; n < count; n++)
        {
            uint bit = (first + n) & BitmapWindowMask, at = bitmap + 64 + (bit >> 3);
            memory.Write8(at, (byte)(memory.Read8(at) & ~(0x80u >> (int)(bit & 7))));
        }
        memory.Write32(counts + 68, memory.Read32(counts + 68) + count);
        memory.Write32(counts + window, memory.Read32(counts + window) + count);
        PutWide(state + 96, free);
        PutWide(inode + 64 + 8, size);
        PutWide(inode + 64 + 16, keep);
        memory.Write32(inode + 64 + 112, keep);
        CacheBufferedObjects(inode, inodeAt);
        return true;
    }
}
