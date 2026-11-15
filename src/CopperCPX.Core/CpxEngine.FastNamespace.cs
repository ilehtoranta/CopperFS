namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    private uint CachedObject(ulong id)
    {
        uint objects = BufferedObjects;
        for (uint n = 0; n < 8; n++)
        {
            uint p = objects + n * 272;
            if (memory.Read32(p + 268) != 0 && (((ulong)(GetWide(p + 12))) == ((ulong)(id))))
                return p + 12;
        }

        return 0;
    }

    private uint CachedMetadata(ulong address)
    {
        int slot = FindStaged(address);
        if (slot >= 0)
            return BufferedImage((uint)slot);
        uint cache = BufferedClean, pages = memory.Read32(cache + 4);
        for (uint n = 0; n < pages; n++)
        {
            uint entry = cache + 8 + n * 16;
            if (memory.Read32(entry + 8) != 0 && (((ulong)(GetWide(entry))) == ((ulong)(address))))
                return cache + 8 + pages * 16 + n * P;
        }

        return 0;
    }

    // Called only after a bounded plan has established all rejection and capacity
    // checks. These pages stay mount-owned until the ordinary journal retires.
    private void BeginFastGroup()
    {
        if (memory.Read32(Arena + (Active)) == 0)
        {
            Clear(Index, CpxArena.IndexBytes);
            memory.Write32(Arena + (StagedCount), 0);
        }

        memory.Write32(Arena + (Active), 1);
        memory.Write32(Arena + (StagedLimit), 42);
        memory.Write32(Arena + (BufferSnapshot), 0);
    }

    private uint OwnMetadata(uint source, ulong address, uint type, ulong owner)
    {
        int found = FindStaged(address);
        if (found >= 0)
            return BufferedImage((uint)found);
        uint slot = memory.Read32(Arena + (StagedCount)), p = BufferedImage(slot), d = Index + slot * 32;
        if (source != 0)
            Copy(p, source, P);
        else
            Header(p, type, address, RW(Generation) + 1);
        PutWide(p + 32, RW(Generation) + 1);
        memory.Write32(p + 8, 0);
        PutWide(d, address);
        PutWide(d + 8, owner);
        memory.Write32(d + 16, slot);
        memory.Write32(d + 20, 0);
        memory.Write16(d + 24, type);
        memory.Write32(Arena + (StagedCount), slot + 1);
        InvalidateBufferedClean(address);
        return p;
    }

    private bool FastName(uint source, uint length)
    {
        if (length > 80)
            return false;
        uint encoded = 0, hash = uint.MaxValue;
        for (uint n = 0; n < length; n++)
        {
            uint c = memory.Read8(source + n), fold = FoldLatin(c);
            if (encoded + (c < 128 ? 1u : 2u) > 80)
                return false;
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

        Clear(NsEntry, 128);
        PutWide(NsEntry, (((ulong)(~hash)) << 32 | ((ulong)(0))));
        memory.Write16(NsEntry + 32, encoded);
        memory.Write16(NsEntry + 36, length);
        Copy(NsEntry + 48, NsEncoded, encoded);
        return true;
    }

    private int FastFindName(uint page, uint name, uint length)
    {
        Copy(LegacyName, name, length);
        memory.Write32(Arena + (NameLength), length);
        uint count = memory.Read16(page + 48);
        uint hash = uint.MaxValue;
        for (uint n = 0; n < length; n++)
        {
            uint c = FoldLatin(memory.Read8(name + n));
            if (c < 128)
                hash = HashByte(hash, c);
            else
            {
                hash = HashByte(hash, 0xc0 | (c >> 6));
                hash = HashByte(hash, 0x80 | (c & 63));
            }
        }

        for (uint n = 0; n < count; n++)
        {
            uint entry = page + 64 + n * 128, len = memory.Read16(entry + 32);
            if (!(((ulong)(GetWide(entry + 24))) == 0) || len == 0 || len > 80)
                return -2;
            if (SameLegacy(entry + 48, len))
                return (((ulong)(GetWide(entry))) == ((ulong)((((ulong)(~hash)) << 32 | ((ulong)(0)))))) ? (int)n : -2;
        }

        return -1;
    }

    private bool FastDirectoryShape(uint page, ulong owner)
    {
        if (page == 0 || memory.Read16(page + 4) != 13 || memory.Read16(page + 50) != 0 || !(((ulong)(GetWide(page + 24))) == ((ulong)(owner))) || !(((ulong)(GetWide(page + 40))) == 0))
            return false;
        uint count = memory.Read16(page + 48);
        if (count > (P - 64) >> 7 || memory.Read16(page + 52) != count * 128)
            return false;
        for (uint n = 0; n < count; n++)
        {
            uint at = page + 64 + n * 128;
            if (memory.Read32(at + 4) != 0)
                return false;
            if (n == 0)
                continue;
            uint previous = memory.Read32(at - 128), current = memory.Read32(at);
            if (previous > current || previous == current && (GetWide(at - 120)) >= (GetWide(at + 8)))
                return false;
        }

        return true;
    }

    private void InsertFastEntry(uint page, uint count)
    {
        uint hash = memory.Read32(NsEntry), slot = 0;
        while (slot < count)
        {
            uint entry = page + 64 + slot * 128, key = memory.Read32(entry);
            if (key > hash || key == hash && (GetWide(entry + 8)) > (GetWide(NsEntry + 8)))
                break;
            slot++;
        }

        for (uint n = count; n > slot; n--)
            Copy(page + 64 + n * 128, page + 64 + (n - 1) * 128, 128);
        Copy(page + 64 + slot * 128, NsEntry, 128);
        memory.Write16(page + 48, count + 1);
        memory.Write16(page + 52, (count + 1) * 128);
    }

    private StepResult FastCreateStep()
    {
        if (memory.Read32(Arena + (Phase)) == 601)
        {
            if (!Control(Page(3), 4, Anchor + 8 * Q) || !ReadableGeneration(Page(3)))
                return Fail(CpxError.Corrupt);
            memory.Write32(Arena + (Phase), 600);
            return StepResult.Pending;
        }

        ulong parent = RW(NsParent);
        uint cached = CachedObject(parent), state = CachedMetadata(Anchor + 3 * Q), reserve = CachedMetadata(Anchor + 8 * Q);
        if (cached == 0 || state == 0 || memory.Read32(Arena + (NsDirectory)) > 1 || memory.Read32(Arena + (StagedCount)) > 35 || !(((ulong)(GetWide(state + 88))) == 0) || !FastName(NsName, memory.Read32(Arena + (NsLength))))
        {
            memory.Write32(Arena + (Phase), 0);
            return StepResult.Pending;
        }

        ulong root = GetWide(state + 80), dir = GetWide(cached + 32);
        uint objects = CachedMetadata(root), directory = (((ulong)(dir)) == 0) ? 0 : CachedMetadata(dir);
        if (objects == 0 || memory.Read16(objects + 4) != 11 || memory.Read16(objects + 50) != 0 || !(((ulong)(GetWide(objects + 24))) == 0) || !(((ulong)(GetWide(objects + 40))) == 0) || !(((ulong)(dir)) == 0) && !FastDirectoryShape(directory, parent) || memory.Read8(cached + 84) != 2 || (memory.Read32(cached + 72) & 4) != 0)
        {
            memory.Write32(Arena + (Phase), 0);
            return StepResult.Pending;
        }

        uint count = memory.Read16(objects + 48), capacity = (P - 64) >> 8, entries = directory == 0 ? 0 : memory.Read16(directory + 48);
        if (count == 0 || count > capacity || memory.Read16(objects + 52) != count * 256 || entries >= (P - 64) >> 7 || !(((ulong)(GetWide(cached + 8))) == ((ulong)(((ulong)(entries))))) || directory != 0 && FastFindName(directory, NsName, memory.Read32(Arena + (NsLength))) != -1 || ((uint)(((ulong)(GetWide(state + 64))) >> 32)) != 0 || ((uint)(((ulong)(GetWide(state + 72))) >> 32)) != 0 || memory.Read32(state + 68) == uint.MaxValue || memory.Read32(state + 76) == uint.MaxValue || !Scalar64.Add(GetWide(state + 104), ((ulong)(1)), out _) || !Scalar64.Add(RW(Generation), ((ulong)(1)), out _))
        {
            memory.Write32(Arena + (Phase), 0);
            return StepResult.Pending;
        }

        if (reserve == 0)
        {
            return ReadStaged(Anchor + 8 * Q, Page(3), 601);
        }

        uint need = (directory == 0 ? 1u : 0u) + (count == capacity ? 2u : 0u), available = memory.Read16(reserve + 64);
        if (available <= need || available > 64 || (GetWide(state + 64)) <= (GetWide(objects + 64 + (count - 1) * 256)))
        {
            memory.Write32(Arena + (Phase), 0);
            return StepResult.Pending;
        }

        uint parentSlot = uint.MaxValue;
        for (uint n = 0; n < count; n++)
            if ((((ulong)(GetWide(objects + 64 + n * 256))) == ((ulong)(parent))))
                parentSlot = n;
        if (parentSlot == uint.MaxValue)
        {
            memory.Write32(Arena + (Phase), 0);
            return StepResult.Pending;
        }

        for (uint n = 0; n < need; n++)
            if (!Allocatable(GetWide(reserve + 72 + (available - 1 - n) * 8)))
            {
                memory.Write32(Arena + (Phase), 0);
                return StepResult.Pending;
            }

        ulong id = GetWide(state + 64), entryId = GetWide(state + 72), directoryAddress = dir;
        BeginFastGroup();
        state = OwnMetadata(state, Anchor + 3 * Q, 3, default);
        objects = OwnMetadata(objects, root, 11, default);
        reserve = OwnMetadata(reserve, Anchor + 8 * Q, 4, default);
        uint parentRecord = objects + 64 + parentSlot * 256;
        if (directory == 0)
        {
            directoryAddress = GetWide(reserve + 72 + (--available) * 8);
            Clear(reserve + 72 + available * 8, 8);
            directory = OwnMetadata(0, directoryAddress, 13, parent);
            PutWide(directory + 24, parent);
            memory.Write16(directory + 48, 0);
            memory.Write16(directory + 50, 0);
        }
        else
            directory = OwnMetadata(directory, dir, 13, parent);
        PutWide(NsEntry + 8, entryId);
        PutWide(NsEntry + 16, id);
        InsertFastEntry(directory, entries);
        PutWide(parentRecord + 32, directoryAddress);
        PutWide(parentRecord + 8, ((ulong)(entries + 1)));
        Clear(NsObject, 256);
        PutWide(NsObject, id);
        memory.Write32(NsObject + 76, 1);
        memory.Write32(NsObject + 80, 1);
        memory.Write8(NsObject + 84, (byte)(memory.Read32(Arena + (NsDirectory)) != 0 ? 2 : 1));
        if (memory.Read32(Arena + (NsDirectory)) != 0)
            PutWide(NsObject + 40, parent);
        if (count < capacity)
        {
            Copy(objects + 64 + count * 256, NsObject, 256);
            memory.Write16(objects + 48, count + 1);
            memory.Write16(objects + 52, (count + 1) * 256);
        }
        else
        {
            ulong sibling = GetWide(reserve + 72 + (--available) * 8), newRoot = GetWide(reserve + 72 + (--available) * 8);
            Clear(reserve + 72 + available * 8, 16);
            uint right = OwnMetadata(0, sibling, 11, default), branch = OwnMetadata(0, newRoot, 12, default), left = (count + 1) >> 1, total = count + 1 - left;
            for (uint n = 0; n < total; n++)
            {
                uint index = left + n;
                Copy(right + 64 + n * 256, index == count ? NsObject : objects + 64 + index * 256, 256);
            }

            Clear(objects + 64 + left * 256, P - 64 - left * 256);
            memory.Write16(objects + 48, left);
            memory.Write16(objects + 52, left * 256);
            memory.Write16(right + 48, total);
            memory.Write16(right + 52, total * 256);
            memory.Write16(branch + 48, 2);
            memory.Write16(branch + 50, 1);
            memory.Write16(branch + 52, 32);
            PutWide(branch + 64, GetWide(objects + 64));
            PutWide(branch + 72, root);
            PutWide(branch + 80, GetWide(right + 64));
            PutWide(branch + 88, sibling);
            PutWide(state + 80, newRoot);
            CacheBufferedObjects(right, sibling);
        }

        memory.Write16(reserve + 64, available);
        PutWide(state + 64, id + 1);
        PutWide(state + 72, entryId + 1);
        AdjustWide(state + 104, 1, true);
        CacheBufferedObjects(objects, root);
        WW(NsResult, id);
        WW(NsEntryId, entryId);
        memory.Write32(Arena + (NsPublished), 1);
        return Done();
    }

    private bool TryFastRename()
    {
        if (!HasBufferedChanges || memory.Read32(Arena + (StagedCount)) > 41 || !FastName(NsNewName, memory.Read32(Arena + (NsNewLength))))
            return false;
        ulong parent = RW(NsParent);
        uint cached = CachedObject(parent);
        if (cached == 0 || memory.Read8(cached + 84) != 2 || (memory.Read32(cached + 72) & 4) != 0)
            return false;
        ulong root = GetWide(cached + 32);
        uint page = CachedMetadata(root);
        if (!FastDirectoryShape(page, parent))
            return false;
        int old = FastFindName(page, NsName, memory.Read32(Arena + (NsLength)));
        if (old < 0)
            return false;
        int target = FastFindName(page, NsNewName, memory.Read32(Arena + (NsNewLength)));
        if (target == -2 || target >= 0 && target != old)
            return false;
        uint record = page + 64 + (uint)old * 128;
        ulong id = GetWide(record + 16), entry = GetWide(record + 8);
        uint objectAt = CachedObject(id);
        if (objectAt == 0 || memory.Read8(objectAt + 84) != 1 || (memory.Read32(objectAt + 72) & 1) != 0 || !(((ulong)(GetWide(record + 24))) == 0))
            return false;
        uint count = memory.Read16(page + 48);
        BeginFastGroup();
        page = OwnMetadata(page, root, 13, parent);
        for (uint n = (uint)old; n + 1 < count; n++)
            Copy(page + 64 + n * 128, page + 64 + (n + 1) * 128, 128);
        Clear(page + 64 + (count - 1) * 128, 128);
        PutWide(NsEntry + 8, entry);
        PutWide(NsEntry + 16, id);
        InsertFastEntry(page, count - 1);
        WW(NsResult, id);
        memory.Write32(Arena + (NsPublished), 1);
        return true;
    }
}
