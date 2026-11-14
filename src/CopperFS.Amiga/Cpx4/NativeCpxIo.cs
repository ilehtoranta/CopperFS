#if COPPERFS_CPX4
using Amiga;

namespace CopperCPX.Amiga;

/// <summary>Allocation-free Exec boundary. State is a caller-owned 16-byte record.
/// Submit returns immediately. Only Reap releases ownership after WaitIO.</summary>
public static class NativeCpxIo
{
    public static CpxError Submit(APTR request,APTR state,DeviceRequest command,uint token)
    {
        if(APTR.ReadUInt32(state,0)!=0) return CpxError.Busy;
        if(token==0||command.Command is not (2 or 3 or 24 or 25)||command.Length==0||command.Buffer==0||
            command.Length>uint.MaxValue-command.Buffer||(command.Command<24&&command.OffsetHigh!=0)) return CpxError.InvalidArgument;
        Prepare(request,command.Command,command.Buffer,command.Length,command.OffsetLow,command.Command>=24?command.OffsetHigh:0);
        APTR.WriteUInt32(state,4,command.Length); APTR.WriteUInt32(state,8,token); APTR.WriteUInt32(state,12,0); APTR.WriteUInt32(state,0,1);
        Exec.SendIO(request); return CpxError.None;
    }
    public static CpxError SubmitFlush(APTR request,APTR state,uint token)
    {
        if(APTR.ReadUInt32(state,0)!=0) return CpxError.Busy;
        if(token==0) return CpxError.InvalidArgument;
        Prepare(request,4,0,0,0,0); APTR.WriteUInt32(state,4,0); APTR.WriteUInt32(state,8,token); APTR.WriteUInt32(state,12,1); APTR.WriteUInt32(state,0,1);
        Exec.SendIO(request); return CpxError.None;
    }
    public static CpxError SubmitScsi(APTR request,APTR state,uint control,uint token)
    {
        if(APTR.ReadUInt32(state,0)!=0) return CpxError.Busy;
        if(token==0||control==0||(control&1)!=0) return CpxError.InvalidArgument;
        Prepare(request,28,control,30,0,0);
        APTR.WriteUInt32(state,4,0); APTR.WriteUInt32(state,8,token); APTR.WriteUInt32(state,12,1); APTR.WriteUInt32(state,0,1);
        Exec.SendIO(request); return CpxError.None;
    }
    private static void Prepare(APTR request,uint command,uint buffer,uint length,uint offset,uint actual)
    {
        APTR.WriteUInt16(request,ExecLayout.IORequest.Command,(ushort)command);
        APTR.WriteUInt8(request,ExecLayout.IORequest.Flags,0); APTR.WriteUInt8(request,ExecLayout.IORequest.Error,0);
        APTR.WriteUInt32(request,ExecLayout.IOStdReq.Data,buffer); APTR.WriteUInt32(request,ExecLayout.IOStdReq.Length,length);
        APTR.WriteUInt32(request,ExecLayout.IOStdReq.Offset,offset); APTR.WriteUInt32(request,ExecLayout.IOStdReq.Actual,actual);
    }
    public static bool Reap(APTR request,APTR state,out uint token,out uint actual,out CpxError error)
    {
        token=actual=0; error=CpxError.None;
        if(APTR.ReadUInt32(state,0)==0||Exec.CheckIO(request).IsNull) return false;
        bool aborted=APTR.ReadUInt32(state,0)==2;
        int result=Exec.WaitIO(request); token=APTR.ReadUInt32(state,8); actual=APTR.ReadUInt32(request,ExecLayout.IOStdReq.Actual);
        if(result!=0||APTR.ReadUInt8(request,ExecLayout.IORequest.Error)!=0||
            (APTR.ReadUInt32(state,12)==0&&actual!=APTR.ReadUInt32(state,4))) error=CpxError.Io;
        if(aborted&&error==CpxError.None) error=CpxError.Cancelled;
        APTR.WriteUInt32(state,0,0); return true;
    }
    public static void RequestAbort(APTR request,APTR state)
    { if(APTR.ReadUInt32(state,0)==1) { APTR.WriteUInt32(state,0,2); Exec.AbortIO(request); } }
}
#endif
