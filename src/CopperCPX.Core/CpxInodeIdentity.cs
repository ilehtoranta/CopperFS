namespace CopperCPX;

/// <summary>Experimental direct-inode identities. A physical slot is only an
/// address hint: readers must match the complete stored identity before use.</summary>
public readonly struct CpxInodeIdentity
{
    private readonly ulong mask, pairs, root;
    private readonly uint pairShift;
    public uint SlotBits { get; }

    private CpxInodeIdentity(ulong mask, ulong pairs, ulong root, uint shift, uint bits)
    { this.mask = mask; this.pairs = pairs; this.root = root; pairShift = shift; SlotBits = bits; }

    public static CpxError TryCreate(CpxShadowMapGeometry geometry, Wide rootPair, out CpxInodeIdentity identity)
    {
        identity = default; ulong at = rootPair;
        if (geometry.Height == 0 || at == 0 || !geometry.ContainsPair(rootPair) ||
            at >= geometry.MapBaseSector && at < geometry.MapEndSector) return CpxError.InvalidArgument;
        uint bits = 0; ulong maximum = geometry.PairCount - 1;
        while (maximum != 0) { maximum >>= 1; bits++; }
        if (bits == 0 || bits >= 63) return CpxError.InvalidArgument;
        identity = new((1UL << (int)bits) - 1, geometry.PairCount, at, geometry.PairShift, bits);
        return CpxError.None;
    }

    public CpxError TryAssign(Wide nextIdentity, Wide physicalPair, out Wide assigned, out Wide following)
    {
        assigned = following = default;
        ulong next = nextIdentity, pair = physicalPair;
        if (pairs == 0 || next < 2 || (pair & ((1UL << (int)pairShift) - 1)) != 0 ||
            pair == root || (pair >> (int)pairShift) >= pairs) return CpxError.InvalidArgument;
        ulong candidate = (next & ~mask) | (pair >> (int)pairShift), stride = mask + 1;
        if (candidate < next)
        {
            if (candidate > ulong.MaxValue - stride) return CpxError.ResourceExhausted;
            candidate += stride;
        }
        if (candidate < 2 || candidate == ulong.MaxValue) return CpxError.ResourceExhausted;
        assigned = candidate; following = candidate + 1; return CpxError.None;
    }

    public bool TryPair(Wide requestedIdentity, out Wide physicalPair)
    {
        physicalPair = default; ulong id = requestedIdentity;
        if (pairs == 0 || id == 0) return false;
        if (id == 1) { physicalPair = root; return true; }
        ulong slot = id & mask;
        if (slot >= pairs || (slot << (int)pairShift) == root) return false;
        physicalPair = slot << (int)pairShift; return true;
    }

    public bool Matches(Wide requestedIdentity, Wide storedIdentity, Wide physicalPair)
        => Wide.Equal(requestedIdentity, storedIdentity) && TryPair(requestedIdentity, out var expected) && Wide.Equal(expected, physicalPair);

    internal void Store<TMemory>(ref TMemory memory, uint at) where TMemory : struct, ICpxMemory
    {
        memory.Write32(at, (uint)(mask >> 32)); memory.Write32(at + 4, (uint)mask);
        memory.Write32(at + 8, (uint)(pairs >> 32)); memory.Write32(at + 12, (uint)pairs);
        memory.Write32(at + 16, (uint)(root >> 32)); memory.Write32(at + 20, (uint)root);
        memory.Write32(at + 24, pairShift); memory.Write32(at + 28, SlotBits);
    }

    internal static CpxInodeIdentity Restore<TMemory>(ref TMemory memory, uint at) where TMemory : struct, ICpxMemory
        => new(((ulong)memory.Read32(at) << 32) | memory.Read32(at + 4),
            ((ulong)memory.Read32(at + 8) << 32) | memory.Read32(at + 12),
            ((ulong)memory.Read32(at + 16) << 32) | memory.Read32(at + 20),
            memory.Read32(at + 24), memory.Read32(at + 28));
}
