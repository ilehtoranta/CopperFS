namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    // ReadMetadata owns this page until the next engine operation. Consumers
    // copy records they need before issuing another operation.
    public uint MetadataBuffer => (memory.Read32(Arena + (GeometryPages + ((uint)(0)) * 4)));

    public Wide StatePageAddress
    {
        get
        {
            uint cpxArenaBase = Arena;
            return (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + 3 * (memory.Read32(cpxArenaBase + (GeometryQ)));
        }
    }

    public Wide AllocationUnits
    {
        get
        {
            uint cpxArenaBase = Arena;
            return ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Units)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Units)) + 4))))));
        }
    }

    public uint AllocationUnitBytes => 1u << (int)memory.Read32(Arena + (UnitShift));

    public Wide CreatedEntryId
    {
        get
        {
            uint cpxArenaBase = Arena;
            return ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsEntryId)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsEntryId)) + 4))))));
        }
    }
}