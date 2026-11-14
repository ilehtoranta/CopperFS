#if COPPERFS_CPX4
using Amiga;
namespace CopperCPX.Amiga;

/// <summary>SBC UNMAP with read-only B2/B0/READ CAPACITY(16) discovery. One
/// descriptor and one discard per engine request; all waits are resumable.</summary>
public static class NativeCpxUnmap
{
    public static StepResult Service(uint arena,CpxIo io,out uint completed,out bool unsupported)
    {
        APTR state=APTR.FromPointer(arena+2304),control=APTR.FromPointer(NativeCpxTransport.ScsiAddress(arena)),transport=APTR.FromPointer(arena+2048);
        APTR request=APTR.FromPointer(APTR.ReadUInt32(transport,16)); completed=0; unsupported=false;
        uint phase=APTR.ReadUInt32(state,0);
        if(phase==6) { unsupported=true; return StepResult.Complete; }
        uint mask=APTR.ReadUInt32(transport,40);
        if(!Td64Transfer.DmaRange(control.Raw,208,mask)||!Td64Transfer.DmaRange(control.Raw+32,16,mask)||
            !Td64Transfer.DmaRange(control.Raw+48,32,mask)||!Td64Transfer.DmaRange(control.Raw+80,64,mask))
        { APTR.WriteUInt32(state,0,6); unsupported=true; return StepResult.Complete; }
        if(APTR.ReadUInt32(transport,0)!=0)
        {
            if(!NativeCpxIo.Reap(request,transport,out uint token,out _,out CpxError error)) return StepResult.Pending;
            if(token!=io.Token) return StepResult.Failed;
            if(error!=CpxError.None||APTR.ReadUInt8(control,21)!=0)
            {
                byte code=APTR.ReadUInt8(request,31); uint sense=(uint)APTR.ReadUInt8(control,50)&15;
                bool unsupportedCommand=code==253||(APTR.ReadUInt16(control,28)>=14&&sense==5&&APTR.ReadUInt8(control,60) is 0x20 or 0x24);
                if(!unsupportedCommand) return StepResult.Failed;
                APTR.WriteUInt32(state,0,6); unsupported=true; return StepResult.Complete;
            }
            uint actual=APTR.ReadUInt32(control,8),wanted=APTR.ReadUInt32(control,4);
            if(actual>wanted||APTR.ReadUInt16(control,18)!=(phase<=2?6:phase==3?16:10)) return StepResult.Failed;
            if(phase==1)
            {
                if(actual<8||APTR.ReadUInt8(control,81)!=0xb2||APTR.ReadUInt16(control,82)<4) return StepResult.Failed;
                if((APTR.ReadUInt8(control,85)&128)==0) { APTR.WriteUInt32(state,0,6); unsupported=true; return StepResult.Complete; }
                Prepare(control,6,64,true); APTR.WriteUInt8(control,32,0x12); APTR.WriteUInt8(control,33,1); APTR.WriteUInt8(control,34,0xb0); APTR.WriteUInt8(control,36,64);
                APTR.WriteUInt32(state,0,2); return Submit(request,transport,control,io.Token);
            }
            if(phase==2)
            {
                if(actual<28||APTR.ReadUInt8(control,81)!=0xb0||APTR.ReadUInt16(control,82)<24) return StepResult.Failed;
                uint maximum=APTR.ReadUInt32(control,100),descriptors=APTR.ReadUInt32(control,104);
                if(maximum==0||descriptors==0) { APTR.WriteUInt32(state,0,6); unsupported=true; return StepResult.Complete; }
                APTR.WriteUInt32(state,4,maximum);
                Prepare(control,16,32,true); APTR.WriteUInt8(control,32,0x9e); APTR.WriteUInt8(control,33,0x10); APTR.WriteUInt32(control,42,32);
                APTR.WriteUInt32(state,0,3); return Submit(request,transport,control,io.Token);
            }
            if(phase==3)
            {
                if(actual<12) return StepResult.Failed;
                int shift=NativeCpxTransport.SectorShift(arena);
                if(APTR.ReadUInt32(control,88)!=(1u<<shift)) { APTR.WriteUInt32(state,0,6); unsupported=true; return StepResult.Complete; }
                Wide last=new(APTR.ReadUInt32(control,80),APTR.ReadUInt32(control,84));
                Wide start=Wide.Right(new(APTR.ReadUInt32(transport,20),APTR.ReadUInt32(transport,24)),shift);
                Wide length=new(APTR.ReadUInt32(transport,28),APTR.ReadUInt32(transport,32));
                if(length.IsZero||!Wide.Add(start,length,out var end)||Wide.Compare(end-1,last)>0) return StepResult.Failed;
                APTR.WriteUInt32(state,0,4); return StepResult.Pending;
            }
            if(phase==5)
            { if(actual!=24) return StepResult.Failed; APTR.WriteUInt32(state,0,4); completed=io.Bytes; return StepResult.Complete; }
            return StepResult.Failed;
        }
        if(phase==0)
        {
            Prepare(control,6,64,true); APTR.WriteUInt8(control,32,0x12); APTR.WriteUInt8(control,33,1); APTR.WriteUInt8(control,34,0xb2); APTR.WriteUInt8(control,36,64);
            APTR.WriteUInt32(state,0,1); return Submit(request,transport,control,io.Token);
        }
        if(phase!=4) return StepResult.Failed;
        if(io.Kind==IoKind.QueryDiscard)
        {
            if(io.Buffer==0||io.Bytes!=4) return StepResult.Failed;
            APTR.WriteUInt32(APTR.FromPointer(io.Buffer),0,APTR.ReadUInt32(state,4));
            completed=4; return StepResult.Complete;
        }
        int sectorShift=NativeCpxTransport.SectorShift(arena);
        uint count=io.Bytes>>sectorShift;
        Wide partition=new(APTR.ReadUInt32(transport,28),APTR.ReadUInt32(transport,32));
        Wide baseSector=Wide.Right(new(APTR.ReadUInt32(transport,20),APTR.ReadUInt32(transport,24)),sectorShift);
        if(count>APTR.ReadUInt32(state,4)) { unsupported=true; return StepResult.Complete; }
        if(io.Bytes==0||(io.Bytes&((1u<<sectorShift)-1))!=0||!Wide.Add(io.Sector,Wide.From(count),out var limit)||Wide.Compare(limit,partition)>0||
            !Wide.Add(baseSector,io.Sector,out var lba)) return StepResult.Failed;
        Prepare(control,10,24,false); APTR.WriteUInt8(control,32,0x42); APTR.WriteUInt8(control,40,24);
        APTR.WriteUInt16(control,80,22); APTR.WriteUInt16(control,82,16); APTR.WriteUInt32(control,88,lba.High); APTR.WriteUInt32(control,92,lba.Low); APTR.WriteUInt32(control,96,count);
        APTR.WriteUInt32(state,0,5); return Submit(request,transport,control,io.Token);
    }
    private static StepResult Submit(APTR request,APTR state,APTR control,uint token)
        => NativeCpxIo.SubmitScsi(request,state,control.Raw,token)==CpxError.None?StepResult.Pending:StepResult.Failed;
    private static void Prepare(APTR p,ushort cdb,uint bytes,bool read)
    {
        for(int n=0;n<208;n+=4) APTR.WriteUInt32(p,n,0);
        APTR.WriteUInt32(p,0,p.Raw+80); APTR.WriteUInt32(p,4,bytes); APTR.WriteUInt32(p,12,p.Raw+32); APTR.WriteUInt16(p,16,cdb);
        APTR.WriteUInt8(p,20,read?(byte)3:(byte)2); APTR.WriteUInt32(p,22,p.Raw+48); APTR.WriteUInt16(p,26,32);
    }
}
#endif
