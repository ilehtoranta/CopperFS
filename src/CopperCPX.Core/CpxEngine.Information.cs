namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // ReadMetadata owns this page until the next engine operation. Consumers
    // copy records they need before issuing another operation.
    public uint MetadataBuffer => Page(0);
    public Wide StatePageAddress => Anchor+3*Q;
    public Wide AllocationUnits => RW(Units);
    public uint AllocationUnitBytes => 1u<<(int)R(UnitShift);
    public Wide CreatedEntryId => RW(NsEntryId);
}
