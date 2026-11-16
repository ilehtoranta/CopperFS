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
    internal void Clear()
    {
        for (uint n = 0; n < Bytes; n += 4) memory.Write32(address + n, 0);
        memory.Write32(address + 4, Tag);
    }
    internal bool Valid(ulong pairs, bool inline)
    {
        uint start = address, count = memory.Read32(start);
        if (count > Capacity || memory.Read32(start + 4) != Tag || !inline && count != 0) return false;
        bool bounded = (pairs >> 62) == 0;
        uint limitHigh = (uint)(pairs >> 30), limitLow = (uint)(pairs << 2);
        for (uint n = 0; n < count; n++)
        {
            uint at = start + 8 + n * 8, high = memory.Read32(at), low = memory.Read32(at + 4);
            if ((low & 3) == 0 || bounded && (high > limitHigh || high == limitHigh && (low & ~3u) >= limitLow)) return false;
            for (uint before = 0; before < n; before++)
            {
                uint previous = start + 8 + before * 8;
                if (memory.Read32(previous) == high && ((memory.Read32(previous + 4) ^ low) & ~3u) == 0) return false;
            }
        }
        for (uint n = 8 + count * 8; n < Bytes; n += 4) if (memory.Read32(start + n) != 0) return false;
        return true;
    }
    internal uint Lookup(ulong pair)
    {
        if ((pair >> 62) != 0) return 0;
        uint start = address, count = memory.Read32(start), high = (uint)(pair >> 30), low = (uint)(pair << 2);
        for (uint n = 0; n < count; n++)
        {
            uint at = start + 8 + n * 8;
            if (memory.Read32(at) != high) continue;
            uint value = memory.Read32(at + 4);
            if ((value & ~3u) == low) return value & 3;
        }
        return 0;
    }
    // Full means unchanged. The owner can then seed the general map from the
    // old catalogue and this transaction without exposing a partial catalogue.
    internal bool Set(ulong pair, uint flags)
    {
        uint start = address, count = memory.Read32(start), high = (uint)(pair >> 30), low = (uint)(pair << 2);
        if ((pair >> 62) == 0)
        for (uint n = 0; n < count; n++)
        {
            uint at = start + 8 + n * 8;
            if (memory.Read32(at) != high || (memory.Read32(at + 4) & ~3u) != low) continue;
            if (flags == 0)
            {
                uint last = start + 8 + (count - 1) * 8;
                memory.Write32(at, memory.Read32(last)); memory.Write32(at + 4, memory.Read32(last + 4));
                memory.Write32(last, 0); memory.Write32(last + 4, 0); memory.Write32(start, count - 1);
            }
            else { memory.Write32(at, high); memory.Write32(at + 4, low | flags); }
            return true;
        }
        if (flags == 0) return true;
        if (count == Capacity) return false;
        uint fresh = start + 8 + count * 8;
        memory.Write32(fresh, high); memory.Write32(fresh + 4, low | flags); memory.Write32(start, count + 1); return true;
    }
}
