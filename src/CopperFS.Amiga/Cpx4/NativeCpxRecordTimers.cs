#if COPPERFS_CPX4
using Amiga;
namespace CopperCPX.Amiga;

/// <summary>Sixteen startup-owned timer requests. No allocation, request reuse
/// or packet publication while timer.device still owns a request.</summary>
public static class NativeCpxRecordTimers
{
    private static APTR State(uint arena)=>APTR.FromPointer(arena+2816);
    private static uint Slot(uint arena,uint n)=>arena+CpxArena.Scratch+6*APTR.ReadUInt32(APTR.FromPointer(arena),12)+CpxArena.IndexBytes+14336+n*64+8;
    public static uint SignalMask(uint arena)=>1u<<APTR.ReadUInt8(APTR.FromPointer(APTR.ReadUInt32(State(arena),192)),15);
    public static bool Active(uint arena)=>APTR.ReadUInt32(State(arena),204)!=0;
    public static bool HasReplies(uint arena)
    { uint port=APTR.ReadUInt32(State(arena),192); return port!=0&&APTR.ReadUInt32(APTR.FromPointer(port),20)!=port+24; }
    public static bool Initialize(uint arena)
    {
        StepResult step;
        while((step=OpenOne(arena))==StepResult.Pending) { }
        if(step==StepResult.Complete) return true;
        Destroy(arena); return false;
    }
    internal static StepResult OpenOne(uint arena)
    {
        APTR s=State(arena),port=APTR.FromPointer(APTR.ReadUInt32(s,192));
        if(port.IsNull)
        {
            port=Exec.CreateMsgPort(); if(port.IsNull) return StepResult.Failed;
            APTR.WriteUInt32(s,192,port.Raw); return StepResult.Pending;
        }
        uint n=APTR.ReadUInt32(s,200);
        APTR request=Exec.CreateIORequest(port,TimerRequest.Size);
        if(request.IsNull) return StepResult.Failed;
        if(Exec.OpenDevice(CString.FromLiteral("timer.device"),(uint)TimerUnit.MicroHz,request,0)!=0)
        { Exec.DeleteIORequest(request); return StepResult.Failed; }
        APTR.WriteUInt32(s,(int)(n*12),request.Raw); APTR.WriteUInt32(s,200,n+1);
        return n+1==16?StepResult.Complete:StepResult.Pending;
    }
    public static void Destroy(uint arena)
    {
        while(CloseOne(arena)==StepResult.Pending) { }
    }
    internal static StepResult CloseOne(uint arena)
    {
        APTR s=State(arena); uint count=APTR.ReadUInt32(s,200);
        if(count!=0)
        {
            uint index=count-1; APTR request=APTR.FromPointer(APTR.ReadUInt32(s,(int)(index*12)));
            Exec.CloseDevice(request); Exec.DeleteIORequest(request);
            APTR.WriteUInt32(s,(int)(index*12),0); APTR.WriteUInt32(s,200,index);
            return StepResult.Pending;
        }
        APTR port=APTR.FromPointer(APTR.ReadUInt32(s,192)); if(port.IsNotNull) Exec.DeleteMsgPort(port);
        APTR.WriteUInt32(s,192,0); return StepResult.Complete;
    }
    private static bool Reap(uint arena,uint index,ref CpxDosDispatcher<NativeCpxMemory> dispatcher)
    {
        APTR s=APTR.FromPointer(arena+2816+index*12); uint active=APTR.ReadUInt32(s,8);
        if(active==0) return false;
        APTR request=APTR.FromPointer(APTR.ReadUInt32(s,0)); if(Exec.CheckIO(request).IsNull) return false;
        int error=Exec.WaitIO(request); uint token=APTR.ReadUInt32(s,4); APTR.WriteUInt32(s,8,0);
        APTR all=State(arena); APTR.WriteUInt32(all,204,APTR.ReadUInt32(all,204)-1);
        dispatcher.CompleteRecordTimer(Slot(arena,index),token,active==2||error==0?0u:213u); return true;
    }
    public static bool ReapOne(uint arena,ref CpxDosDispatcher<NativeCpxMemory> dispatcher)
    {
        APTR s=State(arena); uint index=APTR.ReadUInt32(s,196); APTR.WriteUInt32(s,196,(index+1)&15);
        return Reap(arena,index,ref dispatcher);
    }
    public static bool ReapForSlot(uint arena,uint slot,ref CpxDosDispatcher<NativeCpxMemory> dispatcher)
    { for(uint n=0;n<16;n++) if(Slot(arena,n)==slot) return Reap(arena,n,ref dispatcher); return false; }
    public static bool Service(uint arena,ref CpxDosDispatcher<NativeCpxMemory> dispatcher)
    {
        if(!dispatcher.TryTakeRecordTimer(out var timer)) return false;
        for(uint n=0;n<16;n++)
        {
            if(Slot(arena,n)!=timer.Slot) continue;
            APTR s=APTR.FromPointer(arena+2816+n*12),request=APTR.FromPointer(APTR.ReadUInt32(s,0));
            if(timer.Cancel)
            {
                // Abort is asynchronous. Reap is the only ownership release.
                APTR.WriteUInt32(s,8,2); Exec.AbortIO(request); return true;
            }
            APTR.WriteUInt32(s,4,timer.Token); APTR.WriteUInt32(s,8,1);
            APTR all=State(arena); APTR.WriteUInt32(all,204,APTR.ReadUInt32(all,204)+1);
            APTR.WriteUInt16(request,28,(ushort)TimerCommand.AddRequest); APTR.WriteUInt8(request,30,0); APTR.WriteUInt8(request,31,0);
            // Only u32 division by a small constant; no general wide division.
            APTR.WriteUInt32(request,32,timer.Ticks/50); APTR.WriteUInt32(request,36,(timer.Ticks%50)*20000);
            Exec.SendIO(request); return true;
        }
        return false;
    }
}
#endif
