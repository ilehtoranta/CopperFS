namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // The append capability pins owned dirty images for the active group.
    // Admission validates the entire next range before changing its plan; a
    // declined request retains the general operation's error and fallback path.
    private bool TryBeginCachedAppend(ulong id,ulong position,uint buffer,uint bytes)
    {
        uint context=Arena;
        if(memory.Read32(context+Signature)!=CpxArena.Magic||memory.Read32(context+Waiting)!=0||
            memory.Read32(context+Finished)==0||memory.Read32(context+AbortOnly)!=0||
            memory.Read32(context+Mounted)==0||memory.Read32(context+Poisoned)!=0||
            memory.Read32(context+ReadOnlyAt)!=0||!BeginFastAppend(id,position,bytes)) return false;
        memory.Write32(context+CallDepth,0); memory.Write32(context+OperationAt,(uint)CpxOperation.WriteMapped);
        memory.Write32(context+ErrorAt,0); memory.Write32(context+Finished,0);
        memory.Write32(context+CancelAt,0); memory.Write32(context+IoReady,0); memory.Write32(context+NsPublished,0);
        memory.Write32(context+Target,(uint)(id>>32)); memory.Write32(context+Target+4,(uint)id);
        memory.Write32(context+ObjectKey,(uint)(id>>32)); memory.Write32(context+ObjectKey+4,(uint)id);
        memory.Write32(context+SourcePage,0); memory.Write32(context+FilePosition,0); memory.Write32(context+FilePosition+4,(uint)position);
        memory.Write32(context+FileBuffer,buffer); memory.Write32(context+FileBytes,bytes); memory.Write32(context+FileValue,0);
        memory.Write32(context+DataProgress,0); memory.Write32(context+TrimCount,0);
        memory.Write32(context+WriteGapPrepared,0); memory.Write32(context+WriteDirect,0);
        // BeginFastAppend supplied Phase and WriteRunUnits, including the
        // bounded cross-window continuation. No I/O is issued from Begin.
        return true;
    }
}
