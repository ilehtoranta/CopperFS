namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    private const uint CachedObjectCompletionPhase = 400;

    private bool TryBeginCachedObjectRead(ulong id)
    {
        uint context=Arena;
        if(memory.Read32(context+Signature)!=CpxArena.Magic||memory.Read32(context+Waiting)!=0||
            memory.Read32(context+Finished)==0||memory.Read32(context+AbortOnly)!=0||
            memory.Read32(context+Mounted)==0||memory.Read32(context+Poisoned)!=0||
            memory.Read32(context+BufferEnabled)!=1) return false;
        uint record=CachedObject(id);
        if(record==0) return false;
        InvalidateFastWrite();
        memory.Write32(context+CallDepth,0); memory.Write32(context+OperationAt,(uint)CpxOperation.ReadObject);
        memory.Write32(context+Phase,CachedObjectCompletionPhase); memory.Write32(context+ErrorAt,0); memory.Write32(context+Finished,0);
        memory.Write32(context+CancelAt,0); memory.Write32(context+IoReady,0); memory.Write32(context+NsPublished,0);
        memory.Write32(context+Target,(uint)(id>>32)); memory.Write32(context+Target+4,(uint)id);
        memory.Write32(context+ObjectKey,(uint)(id>>32)); memory.Write32(context+ObjectKey+4,(uint)id);
        if(!DirectInodes)
        {
            memory.Write32(context+TreeDepth,0); memory.Write32(context+ExpectedLevel,uint.MaxValue);
        }
        Copy(context+CpxArena.MountBytes,record,256);
        memory.Write32(context+TreeAddress,memory.Read32(record-12)); memory.Write32(context+TreeAddress+4,memory.Read32(record-8));
        memory.Write32(context+TreeSlot,memory.Read32(record-4)); memory.Write32(context+SourcePage,1);
        // Completion still passes through Step's cancellation boundary. No
        // device request, lease change or transaction snapshot is introduced.
        return true;
    }
}
