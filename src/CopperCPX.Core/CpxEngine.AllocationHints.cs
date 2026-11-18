namespace CopperCPX;
public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // Advisory next-fit state lives after the owned checkpoint template. It is
    // never evidence of free space and need not participate in rollback. A
    // failed/retried operation may move the hint, but every search authenticates
    // the current bitmap and wraps before reporting exhaustion.
    private uint AllocationHintAddress => ShadowStorage + ShadowStorageBytes(P) - 8;
    private ulong AllocationFloor => ShadowVolume ? GetWide(ShadowStorage + 40) : memory.Read32(Arena + GeometryFirst);
    private ulong AllocationSearchStart()
    {
        if (ShadowVolume)
        {
            ulong hint = GetWide(AllocationHintAddress);
            if (DataUnit(hint)) return hint;
        }
        return AllocationFloor;
    }
    private void RememberAllocationEnd(ulong end)
    {
        if (ShadowVolume) PutWide(AllocationHintAddress, end);
    }
}
