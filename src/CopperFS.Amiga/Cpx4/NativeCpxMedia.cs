#if COPPERFS_CPX4
using Amiga;
namespace CopperCPX.Amiga;

/// <summary>Startup-owned trackdisk change subscription. The software interrupt
/// only latches an event and signals the task; all filesystem work stays in Run.</summary>
public static class NativeCpxMedia
{
    // Information scratch ends at 3716. Stub/Interrupt/data/control occupy
    // 3744..3859, below the DMA page at 4096. No per-event allocation.
    private static APTR S(uint arena)=>APTR.FromPointer(arena+3824);
    public static uint SignalMask(uint arena)=>APTR.ReadUInt32(S(arena),4);
    public static bool Active(uint arena)=>APTR.ReadUInt32(S(arena),8)!=0;
    public static bool Initialize(uint arena,APTR primary,APTR ioPort,APTR task)
    {
        APTR s=S(arena); for(int n=0;n<36;n+=4) APTR.WriteUInt32(s,n,0);
        sbyte signal=Exec.AllocSignal(-1); if(signal<0) return false;
        APTR request=Exec.CreateIORequest(ioPort,56);
        if(request.IsNull) { Exec.FreeSignal(signal); return false; }
        APTR.WriteUInt32(s,0,request.Raw); APTR.WriteUInt32(s,4,1u<<signal); APTR.WriteUInt32(s,12,(uint)signal);
        // This request borrows the open device/unit; the primary stays open
        // until the change subscription has been removed and reaped.
        APTR.WriteUInt32(request,20,APTR.ReadUInt32(primary,20)); APTR.WriteUInt32(request,24,APTR.ReadUInt32(primary,24));
        APTR code=APTR.FromPointer(arena+3744),interrupt=APTR.FromPointer(arena+3784),data=APTR.FromPointer(arena+3808);
        // MC68000 software-interrupt ABI: A1=is_Data. Preserve scratch registers
        // around Signal, return D0=0. Executed by compiled callback tests.
        APTR.WriteUInt32(code,0,0x48e7c0c2); // movem.l d0-d1/a0-a1/a6,-(sp)
        APTR.WriteUInt16(code,4,0x22bc); APTR.WriteUInt32(code,6,1); // move.l #1,(a1)
        APTR.WriteUInt32(code,10,0x20290004); // move.l 4(a1),d0
        APTR.WriteUInt32(code,14,0x22690008); // move.l 8(a1),a1
        APTR.WriteUInt32(code,18,0x2c780004); // move.l 4.w,a6
        APTR.WriteUInt32(code,22,0x4eaefebc); // jsr Signal(a6)
        APTR.WriteUInt32(code,26,0x4cdf4303); // movem.l (sp)+,d0-d1/a0-a1/a6
        APTR.WriteUInt32(code,30,0x70004e75); // moveq #0,d0 / rts
        for(int n=0;n<24;n+=4) APTR.WriteUInt32(interrupt,n,0);
        APTR.WriteUInt8(interrupt,8,2); APTR.WriteUInt32(interrupt,14,data.Raw); APTR.WriteUInt32(interrupt,18,code.Raw);
        APTR.WriteUInt32(data,0,0); APTR.WriteUInt32(data,4,1u<<signal); APTR.WriteUInt32(data,8,task.Raw);
        Exec.CacheClearU();
        Prepare(request,20,interrupt.Raw); APTR.WriteUInt32(s,8,1); Exec.SendIO(request); return true;
    }
    private static void Prepare(APTR request,ushort command,uint interrupt)
    {
        APTR.WriteUInt16(request,28,command); APTR.WriteUInt8(request,30,0); APTR.WriteUInt8(request,31,0);
        APTR.WriteUInt32(request,32,0); APTR.WriteUInt32(request,36,22); APTR.WriteUInt32(request,40,interrupt); APTR.WriteUInt32(request,44,0);
        APTR.WriteUInt32(request,48,0); APTR.WriteUInt32(request,52,0);
    }
    public static bool Poll(uint arena)
    {
        APTR s=S(arena); if(APTR.ReadUInt32(s,8)!=1) return false;
        APTR request=APTR.FromPointer(APTR.ReadUInt32(s,0)); if(Exec.CheckIO(request).IsNull) return false;
        int error=Exec.WaitIO(request); APTR.WriteUInt32(s,8,0);
        // Only IOERR_NOCMD is a clean capability refusal. Any other unexpected
        // subscription completion invalidates media, even a nominal success.
        if(error==0||APTR.ReadUInt8(request,31)!=253)
        { APTR.WriteUInt32(s,8,4); APTR.WriteUInt32(APTR.FromPointer(arena+3808),0,1); }
        return true;
    }
    public static bool TakeChange(uint arena)
    {
        if(APTR.ReadUInt32(S(arena),0)==0) return false;
        Exec.Disable(); APTR data=APTR.FromPointer(arena+3808); bool changed=APTR.ReadUInt32(data,0)!=0;
        APTR.WriteUInt32(data,0,0); Exec.Enable(); return changed;
    }
    public static StepResult Detach(uint arena)
    {
        APTR s=S(arena); uint state=APTR.ReadUInt32(s,8); if(state==0) return StepResult.Complete;
        if(state==3) return StepResult.Failed; // failed detach cannot prove callback ownership ended
        APTR request=APTR.FromPointer(APTR.ReadUInt32(s,0));
        if(state is 1 or 4)
        {
            if(state==1&&Exec.CheckIO(request).IsNotNull)
            {
                Poll(arena);
                if(APTR.ReadUInt32(s,8)==0) return StepResult.Complete;
                // An unexpected registration completion has already been
                // reaped. Submit removal now: Pending must have an outstanding
                // request that can wake the shutdown loop.
            }
            // TD_REMCHANGEINT explicitly requires the original held request.
            Prepare(request,21,arena+3784); APTR.WriteUInt32(s,8,2); Exec.SendIO(request); return StepResult.Pending;
        }
        if(Exec.CheckIO(request).IsNull) return StepResult.Pending;
        int error=Exec.WaitIO(request);
        if(error!=0||APTR.ReadUInt8(request,31)!=0) { APTR.WriteUInt32(s,8,3); return StepResult.Failed; }
        APTR.WriteUInt32(s,8,0); return StepResult.Complete;
    }
    public static bool Destroy(uint arena)
    {
        APTR s=S(arena); if(APTR.ReadUInt32(s,8)!=0) return false;
        APTR request=APTR.FromPointer(APTR.ReadUInt32(s,0));
        if(request.IsNotNull) { Exec.DeleteIORequest(request); Exec.FreeSignal((sbyte)APTR.ReadUInt32(s,12)); }
        APTR.WriteUInt32(s,0,0); APTR.WriteUInt32(s,4,0); return true;
    }
}
#endif
