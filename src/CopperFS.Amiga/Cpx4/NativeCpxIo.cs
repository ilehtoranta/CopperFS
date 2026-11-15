#if COPPERFS_CPX4
using Amiga;
using CopperSharp.Compiler;
using CopperSharp.Sdk.Amiga;

namespace CopperCPX.Amiga;

/// <summary>Allocation-free Exec boundary. State is a caller-owned 16-byte record.
/// Submit returns immediately. Reap releases a quick completion directly or a
/// deferred completion after WaitIO.</summary>
public static class NativeCpxIo
{
    [AmigaLibrary("device",AmigaLibraryBasePolicy.CallerProvided)]
    [AmigaLvo(-30)]
    private static extern void BeginIO([M68kRegister(M68kRegister.A6)] APTR device,[M68kRegister(M68kRegister.A1)] APTR request);
    private static void Start(APTR request,APTR state,bool quick)
    {
        if(!quick) { Exec.SendIO(request); return; }
        APTR.WriteUInt8(request,ExecLayout.IORequest.Flags,1);
        BeginIO(APTR.FromPointer(APTR.ReadUInt32(request,ExecLayout.IORequest.Device)),request);
        if((APTR.ReadUInt8(request,ExecLayout.IORequest.Flags)&1)!=0) APTR.WriteUInt32(state,0,3);
    }
    public static CpxError Submit(APTR request,APTR state,DeviceRequest command,uint token) => Submit(request,state,command,token,false);
    public static CpxError Submit(APTR request,APTR state,DeviceRequest command,uint token,bool quick)
    {
        if(APTR.ReadUInt32(state,0)!=0) return CpxError.Busy;
        if(token==0||command.Command is not (2 or 3 or 24 or 25)||command.Length==0||command.Buffer==0||
            command.Length>uint.MaxValue-command.Buffer||(command.Command<24&&command.OffsetHigh!=0)) return CpxError.InvalidArgument;
        Prepare(request,command.Command,command.Buffer,command.Length,command.OffsetLow,command.Command>=24?command.OffsetHigh:0);
        APTR.WriteUInt32(state,4,command.Length); APTR.WriteUInt32(state,8,token); APTR.WriteUInt32(state,12,0); APTR.WriteUInt32(state,0,1);
        Start(request,state,quick); return CpxError.None;
    }
    public static CpxError SubmitFlush(APTR request,APTR state,uint token) => SubmitFlush(request,state,token,false);
    public static CpxError SubmitFlush(APTR request,APTR state,uint token,bool quick)
    {
        if(APTR.ReadUInt32(state,0)!=0) return CpxError.Busy;
        if(token==0) return CpxError.InvalidArgument;
        Prepare(request,4,0,0,0,0); APTR.WriteUInt32(state,4,0); APTR.WriteUInt32(state,8,token); APTR.WriteUInt32(state,12,1); APTR.WriteUInt32(state,0,1);
        Start(request,state,quick); return CpxError.None;
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
        APTR.WriteUInt8(request,8,5); // NT_MESSAGE, including reused quick requests.
        APTR.WriteUInt16(request,ExecLayout.IORequest.Command,(ushort)command);
        APTR.WriteUInt8(request,ExecLayout.IORequest.Flags,0); APTR.WriteUInt8(request,ExecLayout.IORequest.Error,0);
        APTR.WriteUInt32(request,ExecLayout.IOStdReq.Data,buffer); APTR.WriteUInt32(request,ExecLayout.IOStdReq.Length,length);
        APTR.WriteUInt32(request,ExecLayout.IOStdReq.Offset,offset); APTR.WriteUInt32(request,ExecLayout.IOStdReq.Actual,actual);
    }
    public static bool Reap(APTR request,APTR state,out uint token,out uint actual,out CpxError error)
    {
        token=actual=0; error=CpxError.None;
        uint phase=APTR.ReadUInt32(state,0);
        if(phase==0||phase!=3&&Exec.CheckIO(request).IsNull) return false;
        bool aborted=phase==2;
        int result=phase==3?0:Exec.WaitIO(request); token=APTR.ReadUInt32(state,8); actual=APTR.ReadUInt32(request,ExecLayout.IOStdReq.Actual);
        if(result!=0||APTR.ReadUInt8(request,ExecLayout.IORequest.Error)!=0||
            (APTR.ReadUInt32(state,12)==0&&actual!=APTR.ReadUInt32(state,4))) error=CpxError.Io;
        if(aborted&&error==CpxError.None) error=CpxError.Cancelled;
        APTR.WriteUInt32(state,0,0); return true;
    }
    public static void RequestAbort(APTR request,APTR state)
    { if(APTR.ReadUInt32(state,0)==1) { APTR.WriteUInt32(state,0,2); Exec.AbortIO(request); } }
}
#endif
