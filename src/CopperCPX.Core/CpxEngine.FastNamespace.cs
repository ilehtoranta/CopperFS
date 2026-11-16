namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    // The dispatcher can omit a separate object-read operation for an absolute
    // resize. BeginResize still owns admission, protection and size changes.
    internal bool HasCachedRegularFile(Wide id)
    {
        uint context = Arena;
        if (memory.Read32(context + Signature) != CpxArena.Magic || memory.Read32(context + Waiting) != 0 ||
            memory.Read32(context + Finished) == 0 || memory.Read32(context + Mounted) == 0 ||
            memory.Read32(context + Poisoned) != 0 || memory.Read32(context + BufferEnabled) != 1) return false;
        uint record = CachedObject(id);
        return record != 0 && memory.Read8(record + 84) == 1;
    }

    private uint CachedObject(ulong id)
    {
        uint objects = (memory.Read32(Arena + 4088));
        for (uint n = 0; n < 8; n++)
        {
            uint p = objects + n * 272;
            if (memory.Read32(p + 268) != 0 && (((ulong)(((((ulong)(memory.Read32(((uint)(p + 12))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 12)) + 4))))))) == ((ulong)(id))))
                return p + 12;
        }

        return 0;
    }

    private uint CachedMetadata(ulong address)
    {
        uint cpxArenaBase = Arena;
        int slot = FindStaged(address);
        if (slot >= 0)
            return (memory.Read32(cpxArenaBase + (BufferAddress)) + 4096 + ((uint)((uint)slot)) * (memory.Read32(cpxArenaBase + (PageBytes))));
        uint cache = (memory.Read32(cpxArenaBase + 4084)), pages = memory.Read32(cache + 4);
        for (uint n = 0; n < pages; n++)
        {
            uint entry = cache + 8 + n * 16;
            if (memory.Read32(entry + 8) != 0 && (((ulong)(((((ulong)(memory.Read32(((uint)(entry))))) << 32 | ((ulong)(memory.Read32(((uint)(entry)) + 4))))))) == ((ulong)(address))))
                return cache + 8 + pages * 16 + n * (memory.Read32(cpxArenaBase + (PageBytes)));
        }

        return 0;
    }

    // Called only after a bounded plan has established all rejection and capacity
    // checks. These pages stay mount-owned until the ordinary journal retires.
    private void BeginFastGroup()
    {
        uint cpxArenaBase = Arena;
        if (memory.Read32(cpxArenaBase + (Active)) == 0)
        {
            Clear((memory.Read32(cpxArenaBase + (GeometryIndex))), CpxArena.IndexBytes);
            memory.Write32(cpxArenaBase + (StagedCount), 0);
        }

        memory.Write32(cpxArenaBase + (Active), 1);
        memory.Write32(cpxArenaBase + (StagedLimit), 42);
        memory.Write32(cpxArenaBase + (BufferSnapshot), 0);
    }

    private uint OwnMetadata(uint source, ulong address, uint type, ulong owner)
    {
        uint cpxArenaBase = Arena;
        int found = FindStaged(address);
        if (found >= 0)
            return (memory.Read32(cpxArenaBase + (BufferAddress)) + 4096 + ((uint)((uint)found)) * (memory.Read32(cpxArenaBase + (PageBytes))));
        uint slot = memory.Read32(cpxArenaBase + (StagedCount)), p = (memory.Read32(cpxArenaBase + (BufferAddress)) + 4096 + ((uint)(slot)) * (memory.Read32(cpxArenaBase + (PageBytes)))), d = (memory.Read32(cpxArenaBase + (GeometryIndex))) + slot * 32;
        if (source != 0)
            Copy(p, source, (memory.Read32(cpxArenaBase + (PageBytes))));
        else
            Header(p, type, address, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)) + 4)))))) + 1);
        {
            uint cpxWideAddress = (uint)(p + 32);
            ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)) + 4)))))) + 1;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(p + 8, 0);
        {
            uint cpxWideAddress = (uint)(d);
            ulong cpxWideValue = address;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        {
            uint cpxWideAddress = (uint)(d + 8);
            ulong cpxWideValue = owner;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(d + 16, slot);
        memory.Write32(d + 20, 0);
        memory.Write16(d + 24, type);
        memory.Write32(cpxArenaBase + (StagedCount), slot + 1);
        InvalidateBufferedClean(address);
        return p;
    }

    private bool FastName(uint source, uint length)
    {
        uint cpxArenaBase = Arena;
        if (length > 80)
            return false;
        uint encoded = 0, hash = uint.MaxValue;
        for (uint n = 0; n < length; n++)
        {
            uint c = memory.Read8(source + n), fold = FoldLatin(c);
            if (encoded + (c < 128 ? 1u : 2u) > 80)
                return false;
            if (c < 128)
                memory.Write8((cpxArenaBase + 12720) + encoded++, (byte)c);
            else
            {
                memory.Write8((cpxArenaBase + 12720) + encoded++, (byte)(0xc0 | (c >> 6)));
                memory.Write8((cpxArenaBase + 12720) + encoded++, (byte)(0x80 | (c & 63)));
            }

            if (fold < 128)
                hash = ((((uint)(hash)) >> 8) ^ memory.Read32((memory.Read32(cpxArenaBase + (GeometryCrc))) + ((((uint)(hash)) ^ ((uint)(fold))) & 255) * 4));
            else
            {
                hash = ((((uint)(hash)) >> 8) ^ memory.Read32((memory.Read32(cpxArenaBase + (GeometryCrc))) + ((((uint)(hash)) ^ ((uint)(0xc0 | (fold >> 6)))) & 255) * 4));
                hash = ((((uint)(hash)) >> 8) ^ memory.Read32((memory.Read32(cpxArenaBase + (GeometryCrc))) + ((((uint)(hash)) ^ ((uint)(0x80 | (fold & 63)))) & 255) * 4));
            }
        }

        Clear((cpxArenaBase + 11824), 128);
        {
            uint cpxWideAddress = (uint)((cpxArenaBase + 11824));
            ulong cpxWideValue = (((ulong)(~hash)) << 32 | ((ulong)(0)));
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write16((cpxArenaBase + 11824) + 32, encoded);
        memory.Write16((cpxArenaBase + 11824) + 36, length);
        Copy((cpxArenaBase + 11824) + 48, (cpxArenaBase + 12720), encoded);
        return true;
    }

    private int FastFindName(uint page, uint name, uint length)
    {
        uint cpxArenaBase = Arena;
        Copy(((cpxArenaBase + CpxArena.MountBytes) + 1024), name, length);
        memory.Write32(cpxArenaBase + (NameLength), length);
        uint count = memory.Read16(page + 48);
        uint hash = uint.MaxValue;
        for (uint n = 0; n < length; n++)
        {
            uint c = FoldLatin(memory.Read8(name + n));
            if (c < 128)
                hash = ((((uint)(hash)) >> 8) ^ memory.Read32((memory.Read32(cpxArenaBase + (GeometryCrc))) + ((((uint)(hash)) ^ ((uint)(c))) & 255) * 4));
            else
            {
                hash = ((((uint)(hash)) >> 8) ^ memory.Read32((memory.Read32(cpxArenaBase + (GeometryCrc))) + ((((uint)(hash)) ^ ((uint)(0xc0 | (c >> 6)))) & 255) * 4));
                hash = ((((uint)(hash)) >> 8) ^ memory.Read32((memory.Read32(cpxArenaBase + (GeometryCrc))) + ((((uint)(hash)) ^ ((uint)(0x80 | (c & 63)))) & 255) * 4));
            }
        }

        for (uint n = 0; n < count; n++)
        {
            uint entry = page + 64 + n * 128, len = memory.Read16(entry + 32);
            if (!(((ulong)(((((ulong)(memory.Read32(((uint)(entry + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(entry + 24)) + 4))))))) == 0) || len == 0 || len > 80)
                return -2;
            if (SameLegacy(entry + 48, len))
                return (((ulong)(((((ulong)(memory.Read32(((uint)(entry))))) << 32 | ((ulong)(memory.Read32(((uint)(entry)) + 4))))))) == ((ulong)((((ulong)(~hash)) << 32 | ((ulong)(0)))))) ? (int)n : -2;
        }

        return -1;
    }

    private bool FastDirectoryShape(uint page, ulong owner)
    {
        if (page == 0 || memory.Read16(page + 4) != 13 || memory.Read16(page + 50) != 0 || !(((ulong)(((((ulong)(memory.Read32(((uint)(page + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(page + 24)) + 4))))))) == ((ulong)(owner))) || !(((ulong)(((((ulong)(memory.Read32(((uint)(page + 40))))) << 32 | ((ulong)(memory.Read32(((uint)(page + 40)) + 4))))))) == 0))
            return false;
        uint count = memory.Read16(page + 48);
        if (count > ((memory.Read32(Arena + (PageBytes))) - 64) >> 7 || memory.Read16(page + 52) != count * 128)
            return false;
        for (uint n = 0; n < count; n++)
        {
            uint at = page + 64 + n * 128;
            if (memory.Read32(at + 4) != 0)
                return false;
            if (n == 0)
                continue;
            uint previous = memory.Read32(at - 128), current = memory.Read32(at);
            if (previous > current || previous == current && (((((ulong)(memory.Read32(((uint)(at - 120))))) << 32 | ((ulong)(memory.Read32(((uint)(at - 120)) + 4)))))) >= (((((ulong)(memory.Read32(((uint)(at + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(at + 8)) + 4)))))))
                return false;
        }

        return true;
    }

    private void InsertFastEntry(uint page, uint count)
    {
        uint cpxArenaBase = Arena;
        uint hash = memory.Read32((cpxArenaBase + 11824)), slot = 0;
        while (slot < count)
        {
            uint entry = page + 64 + slot * 128, key = memory.Read32(entry);
            if (key > hash || key == hash && (((((ulong)(memory.Read32(((uint)(entry + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(entry + 8)) + 4)))))) > (((((ulong)(memory.Read32(((uint)((cpxArenaBase + 11824) + 8))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 11824) + 8)) + 4)))))))
                break;
            slot++;
        }

        for (uint n = count; n > slot; n--)
            Copy(page + 64 + n * 128, page + 64 + (n - 1) * 128, 128);
        Copy(page + 64 + slot * 128, (cpxArenaBase + 11824), 128);
        memory.Write16(page + 48, count + 1);
        memory.Write16(page + 52, (count + 1) * 128);
    }



    private bool TryFastRename()
    {
        uint cpxArenaBase = Arena;
        if (!((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) && memory.Read32(cpxArenaBase + (Active)) != 0 && memory.Read32(cpxArenaBase + (StagedCount)) != 0) || memory.Read32(cpxArenaBase + (StagedCount)) > 41 || !FastName((cpxArenaBase + 12464), memory.Read32(cpxArenaBase + (NsNewLength))))
            return false;
        ulong parent = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsParent)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsParent)) + 4))))));
        uint cached = CachedObject(parent);
        if (cached == 0 || memory.Read8(cached + 84) != 2 || (memory.Read32(cached + 72) & 4) != 0)
            return false;
        ulong root = ((((ulong)(memory.Read32(((uint)(cached + 32))))) << 32 | ((ulong)(memory.Read32(((uint)(cached + 32)) + 4)))));
        uint page = CachedMetadata(root);
        if (!FastDirectoryShape(page, parent))
            return false;
        int old = FastFindName(page, (cpxArenaBase + 12208), memory.Read32(cpxArenaBase + (NsLength)));
        if (old < 0)
            return false;
        int target = FastFindName(page, (cpxArenaBase + 12464), memory.Read32(cpxArenaBase + (NsNewLength)));
        if (target == -2 || target >= 0 && target != old)
            return false;
        uint record = page + 64 + (uint)old * 128;
        ulong id = ((((ulong)(memory.Read32(((uint)(record + 16))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 16)) + 4))))), entry = ((((ulong)(memory.Read32(((uint)(record + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 8)) + 4)))));
        uint objectAt = CachedObject(id);
        if (objectAt == 0 || memory.Read8(objectAt + 84) != 1 || (memory.Read32(objectAt + 72) & 1) != 0 || !(((ulong)(((((ulong)(memory.Read32(((uint)(record + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 24)) + 4))))))) == 0))
            return false;
        uint count = memory.Read16(page + 48);
        BeginFastGroup();
        page = OwnMetadata(page, root, 13, parent);
        for (uint n = (uint)old; n + 1 < count; n++)
            Copy(page + 64 + n * 128, page + 64 + (n + 1) * 128, 128);
        Clear(page + 64 + (count - 1) * 128, 128);
        {
            uint cpxWideAddress = (uint)((cpxArenaBase + 11824) + 8);
            ulong cpxWideValue = entry;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        {
            uint cpxWideAddress = (uint)((cpxArenaBase + 11824) + 16);
            ulong cpxWideValue = id;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        InsertFastEntry(page, count - 1);
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(NsResult);
            ulong cpxWideValue = id;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(cpxArenaBase + (NsPublished), 1);
        return true;
    }
}
