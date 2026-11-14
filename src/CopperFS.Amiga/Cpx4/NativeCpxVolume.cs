#if COPPERFS_CPX4
using Amiga;
namespace CopperCPX.Amiga;

internal static class NativeCpxVolume
{
    // One startup allocation owns the DOS node. Its original name pointer is
    // restored before FreeDosEntry; the published BSTR is fixed arena storage.
    internal static void Initialize(uint arena,APTR volume,uint label)
    {
        APTR state=APTR.FromPointer(arena+3936);
        APTR.WriteUInt32(state,0,volume.Raw); APTR.WriteUInt32(state,4,APTR.ReadUInt32(volume,40));
        Copy(arena,label); APTR.WriteUInt32(volume,40,(arena+3904)>>2);
    }
    internal static void Publish(uint arena,uint label)
    {
        if(APTR.ReadUInt32(APTR.FromPointer(arena+3936),0)==0) return;
        bool changed=false;
        for(int n=0;n<32;n++) if(APTR.ReadUInt8(APTR.FromPointer(arena+3904),n)!=APTR.ReadUInt8(APTR.FromPointer(label),n)) changed=true;
        if(!changed) return;
        // DOS owns the semaphore wait; no polling or temporary allocation.
        uint flags=(uint)(DosListLockFlags.Volumes|DosListLockFlags.Write);
        DOS.LockDosList(flags); Copy(arena,label); DOS.UnLockDosList(flags);
    }
    private static void Copy(uint arena,uint label)
    { for(int n=0;n<32;n++) APTR.WriteUInt8(APTR.FromPointer(arena+3904),n,APTR.ReadUInt8(APTR.FromPointer(label),n)); }
    internal static void Restore(uint arena,APTR volume)
    { APTR.WriteUInt32(volume,40,APTR.ReadUInt32(APTR.FromPointer(arena+3936),4)); APTR.WriteUInt32(APTR.FromPointer(arena+3936),0,0); }
}
#endif
