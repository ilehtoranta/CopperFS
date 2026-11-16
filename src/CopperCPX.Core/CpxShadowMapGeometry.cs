namespace CopperCPX;

/// <summary>Deterministic paired-page radix-map placement for experimental CPX5.
/// Geometry is independent of allocated files and uses no managed storage.</summary>
public readonly struct CpxShadowMapGeometry
{
    public readonly uint SectorBytes, PageBytes, Height, PairShift, LeafShift, BranchShift;
    public readonly ulong CapacitySectors, MapBaseSector, MapEndSector, PairCount;
    public uint PageSectors => PageBytes / SectorBytes;
    public uint LeafEntries => 1u << (int)LeafShift;
    public uint BranchEntries => 1u << (int)BranchShift;

    private CpxShadowMapGeometry(uint sector, uint page, uint height, ulong capacity, ulong start, ulong end)
    {
        SectorBytes = sector; PageBytes = page; Height = height;
        PairShift = (uint)(CpxArena.Shift(page) - CpxArena.Shift(sector) + 1);
        LeafShift = (uint)CpxArena.Shift(page) + 2;
        BranchShift = (uint)CpxArena.Shift(page) + 1;
        CapacitySectors = capacity; MapBaseSector = start; MapEndSector = end;
        PairCount = capacity >> (int)PairShift;
    }

    public static CpxError TryCreate(uint sectorBytes, uint pageBytes, Wide capacitySectors,
        Wide mapBaseSector, out CpxShadowMapGeometry geometry)
    {
        geometry = default;
        if (!CpxArena.ValidUnit(sectorBytes) || !CpxArena.ValidUnit(pageBytes)
            || sectorBytes > pageBytes) return CpxError.InvalidArgument;
        int sectorShift = CpxArena.Shift(sectorBytes), pageShift = CpxArena.Shift(pageBytes);
        int pairShift = pageShift - sectorShift + 1, leafShift = pageShift + 2, branchShift = pageShift + 1;
        ulong capacity = capacitySectors, start = mapBaseSector;
        if (capacity == 0 || capacity > (ulong.MaxValue >> sectorShift) || start == 0
            || start >= capacity || (start & ((1ul << pairShift) - 1)) != 0)
            return CpxError.InvalidArgument;
        ulong pairs = capacity >> pairShift;
        if (pairs == 0) return CpxError.InvalidArgument;
        ulong count = ((pairs - 1) >> leafShift) + 1, total = count;
        uint height = 1;
        while (count > 1 && height < CpxArena.MaxHeight)
        {
            count = ((count - 1) >> branchShift) + 1;
            total += count; height++;
        }
        if (count != 1 || total > ((capacity - start) >> pairShift)) return CpxError.InvalidArgument;
        geometry = new(sectorBytes, pageBytes, height, capacity, start, start + (total << pairShift));
        return CpxError.None;
    }

    public ulong NodeCount(uint level)
    {
        if (level >= Height) return 0;
        ulong count = ((PairCount - 1) >> (int)LeafShift) + 1;
        for (uint current = 0; current < level; current++) count = ((count - 1) >> (int)BranchShift) + 1;
        return count;
    }

    public Wide NodePairSector(uint level, ulong index)
    {
        if (level >= Height || index >= NodeCount(level)) return default;
        ulong offset = index;
        for (uint lower = 0; lower < level; lower++) offset += NodeCount(lower);
        return MapBaseSector + (offset << (int)PairShift);
    }

    public uint Entries(uint level, ulong index)
    {
        if (level >= Height || index >= NodeCount(level)) return 0;
        uint shift = level == 0 ? LeafShift : BranchShift;
        ulong total = level == 0 ? PairCount : NodeCount(level - 1);
        ulong remaining = total - (index << (int)shift), maximum = 1ul << (int)shift;
        return (uint)(remaining < maximum ? remaining : maximum);
    }

    public bool ContainsPair(Wide pairSector)
    {
        ulong pair = pairSector;
        return (pair & ((1ul << (int)PairShift) - 1)) == 0 && (pair >> (int)PairShift) < PairCount;
    }

    public ulong PairIndex(Wide pairSector) => (ulong)pairSector >> (int)PairShift;
}
