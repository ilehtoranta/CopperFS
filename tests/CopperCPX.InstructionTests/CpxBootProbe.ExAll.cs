using Amiga;
namespace CopperCPX.InstructionTests;

public static partial class CpxBootProbe
{
    // Called after the existing workload restores the nine root entries.
    private static uint ExAllWork(APTR port,APTR data)
    {
        APTR progress=APTR.FromPointer(data.Raw-512);
        APTR.WriteUInt32(progress,28,500);
        var root=DOS.Lock("CPX0:",DOS.LockMode.Shared); if(!root.HasValue) return 500;
        APTR eac=DOS.AllocDosObject((uint)DosObjectType.ExAllControl,APTR.Null); if(eac.IsNull) return 501;
        for(int n=0;n<16;n+=4) APTR.WriteUInt32(eac,n,0);
        APTR.WriteUInt32(progress,28,502);
        if(DOS.ExAll(root.Value,data,512,6,eac)!=0||(uint)DOS.IoErr()!=232||APTR.ReadUInt32(eac,0)!=9) return 502;
        uint at=data.Raw,aliases=0,latin=0,inline=0;
        for(uint n=0;n<9;n++)
        {
            if((at&1)!=0||at<data.Raw||at>data.Raw+512-36) return 503;
            APTR entry=APTR.FromPointer(at); uint name=APTR.ReadUInt32(entry,4);
            if(name<at+36||name>=data.Raw+512) return 504;
            APTR text=APTR.FromPointer(name); byte first=APTR.ReadUInt8(text,0);
            if(first==95&&APTR.ReadUInt8(text,1)==95&&APTR.ReadUInt8(text,2)==117) aliases++;
            if(first==0xe9) latin++;
            if(first==73)
            {
                uint comment=APTR.ReadUInt32(entry,32); if(comment<at+36||comment>=data.Raw+512) return 505;
                if(APTR.ReadUInt8(APTR.FromPointer(comment),0)!=110) return 506; inline++;
            }
            uint next=APTR.ReadUInt32(entry,0);
            if(n==8&&next!=0||n<8&&(next<=at||next>=data.Raw+512)) return 507;
            at=next;
        }
        if(aliases!=2||latin!=1||inline!=1) return 508;
        APTR.WriteUInt32(progress,28,509);
        APTR.WriteUInt32(eac,4,0);
        if(DOS.ExAll(root.Value,data,30,1,eac)==0||APTR.ReadUInt32(eac,0)==0) return 509;
        var copy=DOS.DupLock(root.Value); if(!copy.HasValue) return 510;
        DOS.UnLock(root.Value); root=copy;
        if(DOS.ExAll(root.Value,data,30,1,eac)==0) return 511;
        DOS.ExAllEnd(root.Value,data,30,1,eac);
        if(APTR.ReadUInt32(eac,4)!=uint.MaxValue) return 512;
        DOS.FreeDosObject((uint)DosObjectType.ExAllControl,eac);

        // Packet-level pool exhaustion uses caller-owned control structs; only
        // library-level calls require AllocDosObject's additional private state.
        APTR.WriteUInt32(progress,28,513);
        for(uint n=0;n<17;n++)
        {
            APTR control=APTR.FromPointer(data.Raw+512+n*16);
            for(int offset=0;offset<16;offset+=4) APTR.WriteUInt32(control,offset,0);
            int more=DOS.DoPkt(port,1033,(int)root.Value.Raw,(int)data.Raw,30,1,(int)control.Raw);
            if(n<16) { if(more==0||APTR.ReadUInt32(control,0)==0) return 513; }
            else if(more!=0||(uint)DOS.IoErr()!=103) return 514;
        }
        for(uint n=0;n<16;n++)
            if(DOS.DoPkt(port,1035,(int)root.Value.Raw,0,0,0,(int)(data.Raw+512+n*16))==0) return 515;

        APTR.WriteUInt32(progress,28,516);
        eac=DOS.AllocDosObject((uint)DosObjectType.ExAllControl,APTR.Null); if(eac.IsNull) return 516;
        APTR parsed=APTR.FromPointer(data.Raw+800);
        if(DOS.ParsePatternNoCase("payload",parsed,64)<0) return 517;
        APTR.WriteUInt32(eac,8,parsed.Raw);
        if(DOS.ExAll(root.Value,data,512,6,eac)!=0||(uint)DOS.IoErr()!=232||APTR.ReadUInt32(eac,0)!=1) return 518;
        uint filtered=APTR.ReadUInt32(data,4);
        if(filtered<data.Raw||filtered>=data.Raw+512||APTR.ReadUInt8(APTR.FromPointer(filtered),0)!=80) return 519;
        DOS.FreeDosObject((uint)DosObjectType.ExAllControl,eac);

        // A caller-owned hook rejects everything. If the handler ignores it,
        // the final count is nonzero. The callback runs in DOS's fallback path.
        APTR.WriteUInt32(progress,28,520);
        eac=DOS.AllocDosObject((uint)DosObjectType.ExAllControl,APTR.Null); if(eac.IsNull) return 520;
        APTR hook=APTR.FromPointer(data.Raw+880),code=APTR.FromPointer(data.Raw+912);
        for(int n=0;n<20;n+=4) APTR.WriteUInt32(hook,n,0);
        APTR.WriteUInt32(code,0,0x70004e75); // moveq #0,d0 / rts (MC68000 hook)
        APTR.WriteUInt32(hook,8,code.Raw); APTR.WriteUInt32(eac,12,hook.Raw);
        if(DOS.ExAll(root.Value,data,512,6,eac)!=0||(uint)DOS.IoErr()!=232||APTR.ReadUInt32(eac,0)!=0) return 521;
        DOS.FreeDosObject((uint)DosObjectType.ExAllControl,eac);
        DOS.UnLock(root.Value); return 0;
    }
}
