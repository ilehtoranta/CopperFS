namespace CopperCPX;

// Feature 5 checkpoints contain a small explicit selection map. A zero entry
// means copy A without an inode. Entries never authorize allocation: the owner
// still validates logical targets, bitmaps and full inode identities.
internal struct CpxCheckpointCatalogue<TMemory> where TMemory : struct, ICpxMemory
{
    internal const uint Bytes = 528, Capacity = 64, Tag = 0x494e4c31;
    internal const ulong InlineRoot = ulong.MaxValue;
    private TMemory memory;
    private uint address;
    internal CpxCheckpointCatalogue(TMemory memory, uint address) { this.memory = memory; this.address = address; }
    internal uint Count => memory.Read32(address);
    internal ulong Entry(uint n) => ((ulong)memory.Read32(address + 8 + n * 8) << 32) | memory.Read32(address + 12 + n * 8);
    private void Put(uint n, ulong value)
    { memory.Write32(address + 8 + n * 8, (uint)(value >> 32)); memory.Write32(address + 12 + n * 8, (uint)value); }
    internal void Clear()
    {
        for (uint n = 0; n < Bytes; n += 4) memory.Write32(address + n, 0);
        memory.Write32(address + 4, Tag);
    }
    internal bool Valid(ulong pairs, bool inline)
    {
        uint count = Count;
        if (count > Capacity || memory.Read32(address + 4) != Tag || !inline && count != 0) return false;
        for (uint n = 0; n < count; n++)
        {
            ulong entry = Entry(n), pair = entry >> 2;
            if ((entry & 3) == 0 || pair >= pairs) return false;
            for (uint before = 0; before < n; before++) if ((Entry(before) >> 2) == pair) return false;
        }
        for (uint n = 8 + count * 8; n < Bytes; n += 4) if (memory.Read32(address + n) != 0) return false;
        return true;
    }
    internal uint Lookup(ulong pair)
    {
        uint count = Count;
        for (uint n = 0; n < count; n++) { ulong entry = Entry(n); if ((entry >> 2) == pair) return (uint)entry & 3; }
        return 0;
    }
    // Full means unchanged. The owner can then seed the general map from the
    // old catalogue and this transaction without exposing a partial catalogue.
    internal bool Set(ulong pair, uint flags)
    {
        uint count = Count;
        for (uint n = 0; n < count; n++)
        {
            if ((Entry(n) >> 2) != pair) continue;
            if (flags == 0) { Put(n, Entry(count - 1)); Put(count - 1, 0); memory.Write32(address, count - 1); }
            else Put(n, (pair << 2) | flags);
            return true;
        }
        if (flags == 0) return true;
        if (count == Capacity) return false;
        Put(count, (pair << 2) | flags); memory.Write32(address, count + 1); return true;
    }
}
