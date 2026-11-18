#if COPPERFS_CPX4
using Amiga;
namespace CopperCPX.Amiga;

// A one-shot shares the record timers' reply port. A due tick carries
// only a media epoch, never a trusted disk address or a queued discard.
internal static class NativeCpxMaintenance
{
    private enum Phase : uint { Ready, Armed, Due, Aborting, Disabled }
    private const int Request=0,Status=4,Epoch=8;
    internal const uint IntervalSeconds=30;
    private static APTR State(uint arena)=>APTR.FromPointer(arena+4004);
    internal static bool Open(uint arena)
    {
        APTR port=NativeCpxRecordTimers.Port(arena);
        APTR request=Exec.CreateIORequest(port,TimerRequest.Size);
        if(request.IsNull) return false;
        // A thirty-second idle deadline needs frame resolution. Keep this
        // separate from record-lock timers so an idle filesystem does not
        // require the CIA-backed high-resolution timer to count a long wait.
        if(Exec.OpenDevice(CString.FromLiteral("timer.device"),(uint)TimerUnit.VBlank,request,0)!=0)
        { Exec.DeleteIORequest(request); return false; }
        APTR.WriteUInt32(State(arena),Request,request.Raw); return true;
    }
    // One Exec timer action per visit. Hold a single due tick while a packet
    // owns the engine. Rearm only after the maintenance operation has retired.
    internal static bool Service(uint arena,ref CpxDosDispatcher<NativeCpxMemory> dispatcher)
    {
        APTR s=State(arena),request=APTR.FromPointer(APTR.ReadUInt32(s,Request));
        if(request.IsNull) return false;
        Phase phase=(Phase)APTR.ReadUInt32(s,Status);
        if(phase==Phase.Armed)
        {
            if(Exec.CheckIO(request).IsNull) return false;
            int error=Exec.WaitIO(request);
            APTR.WriteUInt32(s,Status,(uint)(error==0&&APTR.ReadUInt8(request,31)==0?Phase.Due:Phase.Disabled));
            return true;
        }
        if(phase==Phase.Due)
        {
            if(dispatcher.Busy) return false;
            if(APTR.ReadUInt32(s,Epoch)==dispatcher.MediaEpoch) dispatcher.TryBeginMaintenance();
            APTR.WriteUInt32(s,Status,(uint)Phase.Ready); return true;
        }
        if(phase!=Phase.Ready||dispatcher.Busy&&dispatcher.InternalMaintenance||!dispatcher.MaintenanceAllowed) return false;
        APTR.WriteUInt32(s,Epoch,dispatcher.MediaEpoch); APTR.WriteUInt32(s,Status,(uint)Phase.Armed);
        APTR.WriteUInt16(request,28,(ushort)TimerCommand.AddRequest); APTR.WriteUInt8(request,30,0); APTR.WriteUInt8(request,31,0);
        APTR.WriteUInt32(request,32,IntervalSeconds); APTR.WriteUInt32(request,36,0);
        Exec.SendIO(request); return true;
    }
    internal static StepResult CloseOne(uint arena)
    {
        APTR s=State(arena),request=APTR.FromPointer(APTR.ReadUInt32(s,Request));
        if(request.IsNull) return StepResult.Complete;
        Phase phase=(Phase)APTR.ReadUInt32(s,Status);
        if(phase==Phase.Armed||phase==Phase.Aborting)
        {
            if(Exec.CheckIO(request).IsNull)
            {
                if(phase==Phase.Armed) { APTR.WriteUInt32(s,Status,(uint)Phase.Aborting); Exec.AbortIO(request); }
                return StepResult.NeedIo;
            }
            Exec.WaitIO(request); APTR.WriteUInt32(s,Status,(uint)Phase.Ready); return StepResult.Pending;
        }
        Exec.CloseDevice(request); Exec.DeleteIORequest(request); APTR.WriteUInt32(s,Request,0); return StepResult.Pending;
    }
}
#endif
