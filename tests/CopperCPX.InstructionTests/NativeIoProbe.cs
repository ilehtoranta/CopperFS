using Amiga;
using CopperCPX.Amiga;
namespace CopperCPX.InstructionTests;

public static class NativeIoProbe
{
    public static uint Entry()
    {
        var request=APTR.FromPointer(0x8200); var state=APTR.FromPointer(0x8400);
        var command=new DeviceRequest { Command=25,OffsetHigh=1,OffsetLow=512,ActualInput=1,Buffer=0x2000,Length=512 };
        if(NativeCpxIo.Submit(request,state,command,123)!=CpxError.None) return 1;
        if(NativeCpxIo.Submit(request,state,command,124)!=CpxError.Busy) return 2;
        if(NativeCpxIo.Reap(request,state,out _,out _,out _)) return 3;
        if(!NativeCpxIo.Reap(request,state,out uint token,out uint actual,out CpxError error)||token!=123||actual!=512||error!=CpxError.None) return 4;
        if(NativeCpxIo.SubmitFlush(request,state,125)!=CpxError.None) return 5;
        if(NativeCpxIo.Reap(request,state,out _,out _,out _)) return 6;
        if(!NativeCpxIo.Reap(request,state,out token,out actual,out error)||token!=125||error!=CpxError.None) return 7;
        if(NativeCpxIo.Submit(request,state,command,126)!=CpxError.None) return 8;
        NativeCpxIo.RequestAbort(request,state);
        NativeCpxIo.RequestAbort(request,state); // Repeated cancellation must not submit another AbortIO.
        if(APTR.ReadUInt32(state,0)==0) return 9;
        if(NativeCpxIo.Reap(request,state,out _,out _,out _)) return 10;
        if(!NativeCpxIo.Reap(request,state,out token,out actual,out error)||token!=126||error!=CpxError.Io) return 11;
        return 0;
    }
}
