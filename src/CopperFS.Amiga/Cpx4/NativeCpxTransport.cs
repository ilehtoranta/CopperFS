#if COPPERFS_CPX4
using Amiga;
namespace CopperCPX.Amiga;

/// <summary>All transport progress lives in the mount arena. Each service call
/// submits or reaps at most one command and returns to the handler's event loop.</summary>
public static class NativeCpxTransport
{
    // 2048..2191 control; 4096..8191 DMA bounce page, inside the 8 KiB mount budget.
    private static APTR State(uint arena)=>APTR.FromPointer(arena+2048);
    private const int Bounce=76, BounceBytes=80, OwnedBytes=84, ForceBounce=88, ScsiControl=92, OwnedBase=96;
    public static void Initialize(uint arena,APTR request,Wide start,Wide sectors,uint maximum,uint mask,bool td64,uint sectorBytes=512)
    {
        APTR s=State(arena); for(int n=0;n<144;n+=4) APTR.WriteUInt32(s,n,0);
        APTR.WriteUInt32(s,16,request.Raw); APTR.WriteUInt32(s,20,start.High); APTR.WriteUInt32(s,24,start.Low);
        APTR.WriteUInt32(s,28,sectors.High); APTR.WriteUInt32(s,32,sectors.Low); APTR.WriteUInt32(s,36,maximum); APTR.WriteUInt32(s,40,mask); APTR.WriteUInt32(s,44,td64?1u:0u);
        APTR.WriteUInt32(s,68,(uint)CpxArena.Shift(sectorBytes));
        APTR.WriteUInt32(s,Bounce,arena+4096); APTR.WriteUInt32(s,BounceBytes,4096);
        APTR.WriteUInt32(s,ScsiControl,arena+2560);
        APTR.WriteUInt32(APTR.FromPointer(arena+2304),0,0);
    }
    // This is called once, before opening the device. A non-public memory
    // class requires bouncing arbitrary caller buffers even if their addresses fit.
    internal static bool ReserveDma(uint arena,uint memoryType)
    {
        APTR s=State(arena);
        uint mask=APTR.ReadUInt32(s,40),sector=1u<<SectorShift(arena);
        uint required=(memoryType&0xffffu)|(uint)Exec.MemoryFlags.Public;
        APTR.WriteUInt32(s,ForceBounce,required==(uint)Exec.MemoryFlags.Public?0u:1u);
        uint page=arena+4096;
        if(Td64Transfer.DmaRange(page,4096,mask)&&
            (required==(uint)Exec.MemoryFlags.Public||(Exec.TypeOfMem(APTR.FromPointer(page))&required)==required))
        {
            APTR.WriteUInt32(s,ScsiControl,page);
            return true;
        }
        var flags=(Exec.MemoryFlags)required;
        for(uint size=4096;size>=sector;size>>=1)
        {
            if(TryAllocateDma(s,size,mask,flags)) return true;
            if((flags&Exec.MemoryFlags.Chip)==0)
            {
                if((flags&Exec.MemoryFlags.TwentyFourBitDma)==0&&TryAllocateDma(s,size,mask,flags|Exec.MemoryFlags.TwentyFourBitDma)) return true;
                if((flags&Exec.MemoryFlags.Fast)==0&&TryAllocateDma(s,size,mask,flags|Exec.MemoryFlags.Chip)) return true;
            }
        }
        return false;
    }
    private static bool TryAllocateDma(APTR s,uint bytes,uint mask,Exec.MemoryFlags flags)
    {
        uint alignment=mask&(0u-mask);
        if(alignment==0) return false;
        // Keep the whole Exec allocation and its exact size. At most one
        // extra page is needed to find an aligned sector/page inside it.
        uint padding=alignment<=4?0:alignment>4096?4096:alignment;
        uint owned=bytes+padding,raw=Exec.AllocMem(owned,flags);
        if(raw==0) return false;
        uint skip=(0u-raw)&(alignment-1),address=raw+skip;
        if(skip>padding||address<raw||!Td64Transfer.DmaRange(address,bytes,mask))
        { Exec.FreeMem(raw,owned); return false; }
        APTR.WriteUInt32(s,Bounce,address); APTR.WriteUInt32(s,BounceBytes,bytes);
        APTR.WriteUInt32(s,OwnedBase,raw); APTR.WriteUInt32(s,OwnedBytes,owned); APTR.WriteUInt32(s,ScsiControl,address); return true;
    }
    internal static uint ScsiAddress(uint arena)=>APTR.ReadUInt32(State(arena),ScsiControl);
    internal static void ReleaseDma(uint arena)
    {
        APTR s=State(arena); uint bytes=APTR.ReadUInt32(s,OwnedBytes);
        if(bytes!=0)
        {
            Exec.FreeMem(APTR.ReadUInt32(s,OwnedBase),bytes);
            APTR.WriteUInt32(s,OwnedBytes,0);
        }
    }
    public static bool Waiting(uint arena)=>APTR.ReadUInt32(State(arena),0)!=0;
    public static bool StopRequested(uint arena)=>APTR.ReadUInt32(State(arena),64)!=0;
    internal static Wide Capacity(uint arena)
    { APTR s=State(arena); return new(APTR.ReadUInt32(s,28),APTR.ReadUInt32(s,32)); }
    private static byte DeviceError(APTR s)=>APTR.ReadUInt8(APTR.FromPointer(APTR.ReadUInt32(s,16)),31);
    internal static bool DiskChanged(uint arena)=>DeviceError(State(arena))==29;
    internal static int SectorShift(uint arena)=>(int)APTR.ReadUInt32(State(arena),68);
    // Only the most recently reaped command may classify a discovery failure.
    internal static bool UnreadableSector(uint arena)
    { APTR s=State(arena); return APTR.ReadUInt32(s,72)!=0&&DeviceError(s) is >=21 and <=27; }
    internal static bool TryNarrowProbe(uint arena)
    {
        APTR s=State(arena);
        if(Waiting(arena)||StopRequested(arena)||APTR.ReadUInt32(s,72)==0||APTR.ReadUInt32(s,44)==0||
            APTR.ReadUInt32(s,52)!=0||DeviceError(s)!=253||
            !Td64Transfer.NarrowPartitionFits(new(APTR.ReadUInt32(s,20),APTR.ReadUInt32(s,24)),Capacity(arena),SectorShift(arena))) return false;
        APTR.WriteUInt32(s,44,0); APTR.WriteUInt32(s,48,0); APTR.WriteUInt32(s,52,0); return true;
    }
    public static void RequestStop(uint arena)
    {
        APTR s=State(arena); APTR.WriteUInt32(s,64,1);
        NativeCpxIo.RequestAbort(APTR.FromPointer(APTR.ReadUInt32(s,16)),s);
    }
    public static StepResult Quiesce(uint arena)
    {
        APTR s=State(arena); if(Waiting(arena)&&!NativeCpxIo.Reap(APTR.FromPointer(APTR.ReadUInt32(s,16)),s,out _,out _,out _)) return StepResult.Pending;
        // Never publish a bounce read, split prefix or discard result after stop.
        APTR.WriteUInt32(s,48,0); APTR.WriteUInt32(s,52,0); return StepResult.Complete;
    }
    public static bool ResetMedia(uint arena)
    {
        if(Waiting(arena)) return false;
        APTR s=State(arena); APTR.WriteUInt32(s,64,0); APTR.WriteUInt32(s,48,0); APTR.WriteUInt32(s,52,0);
        APTR.WriteUInt32(APTR.FromPointer(arena+2304),0,0); return true;
    }
    public static StepResult Service(uint arena,CpxIo io,out uint completed,out bool unsupported)
    {
        completed=0; unsupported=false; APTR s=State(arena),request=APTR.FromPointer(APTR.ReadUInt32(s,16));
        APTR.WriteUInt32(s,72,0);
        if(APTR.ReadUInt32(s,64)!=0) return Quiesce(arena)==StepResult.Pending?StepResult.Pending:StepResult.Failed;
        if(io.Kind is IoKind.Discard or IoKind.QueryDiscard) return NativeCpxUnmap.Service(arena,io,out completed,out unsupported);
        if(Waiting(arena))
        {
            if(!NativeCpxIo.Reap(request,s,out uint token,out uint actual,out CpxError error)) return StepResult.Pending;
            if(token!=io.Token) return StepResult.Failed;
            if(error!=CpxError.None) { APTR.WriteUInt32(s,72,1); return StepResult.Failed; }
            uint prior=APTR.ReadUInt32(s,52),take=APTR.ReadUInt32(s,56);
            if(io.Kind==IoKind.Read&&APTR.ReadUInt32(s,60)!=0)
                Copy(APTR.ReadUInt32(s,Bounce),io.Buffer+prior,take);
            completed=prior+take; APTR.WriteUInt32(s,52,completed);
            if(io.Kind==IoKind.Flush||completed==io.Bytes) { APTR.WriteUInt32(s,48,0); return StepResult.Complete; }
            return StepResult.Pending;
        }
        if(APTR.ReadUInt32(s,48)!=io.Token) { APTR.WriteUInt32(s,48,io.Token); APTR.WriteUInt32(s,52,0); }
        if(io.Kind==IoKind.Flush)
        { APTR.WriteUInt32(s,56,0); return NativeCpxIo.SubmitFlush(request,s,io.Token)==CpxError.None?StepResult.Pending:StepResult.Failed; }
        uint done=APTR.ReadUInt32(s,52);
        if(done>io.Bytes) return StepResult.Failed;
        var cursor=new Td64Transfer();
        int shift=SectorShift(arena);
        CpxError begin=cursor.Begin(new(APTR.ReadUInt32(s,20),APTR.ReadUInt32(s,24)),new(APTR.ReadUInt32(s,28),APTR.ReadUInt32(s,32)),
            io.Sector+(done>>shift),shift,io.Bytes-done,io.Buffer+done,APTR.ReadUInt32(s,36),APTR.ReadUInt32(s,40),
            APTR.ReadUInt32(s,Bounce),APTR.ReadUInt32(s,BounceBytes),io.Kind==IoKind.Write,APTR.ReadUInt32(s,44)!=0,APTR.ReadUInt32(s,ForceBounce)!=0);
        if(begin!=CpxError.None||cursor.Next(out var command)!=StepResult.NeedIo) return StepResult.Failed;
        APTR.WriteUInt32(s,56,command.Length); APTR.WriteUInt32(s,60,command.Bounced?1u:0u);
        if(io.Kind==IoKind.Write&&command.Bounced) Copy(io.Buffer+done,command.Buffer,command.Length);
        return NativeCpxIo.Submit(request,s,command,io.Token)==CpxError.None?StepResult.Pending:StepResult.Failed;
    }
    private static void Copy(uint from,uint to,uint bytes)
    { for(uint n=0;n<bytes;n++) APTR.WriteUInt8(APTR.FromPointer(to+n),0,APTR.ReadUInt8(APTR.FromPointer(from+n),0)); }
}
#endif
