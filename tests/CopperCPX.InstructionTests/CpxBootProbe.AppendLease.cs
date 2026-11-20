using Amiga;
namespace CopperCPX.InstructionTests;

public static partial class CpxBootProbe
{
    private static byte LeaseByte(uint index,uint chunk)=>(byte)(index*17+chunk*29+3);

    private static uint AppendLeaseWork(APTR control,APTR port)
    {
        APTR.WriteUInt32(control,36,600);
        if(DOS.DoPkt(port,27,0,0,0,0,0)==0) return 1600;
        // The guest owns this transfer buffer; the handler's separately
        // observed operation-time Exec allocation count must remain zero.
        APTR buffer=APTR.FromPointer(Exec.AllocMem(8192,Exec.MemoryFlags.Public|Exec.MemoryFlags.Clear));
        if(buffer.IsNull) return 1601;
        var file=DOS.Open("CPX0:Lease",DOS.FileMode.NewFile); if(!file.HasValue) return 1602;
        uint handle=APTR.ReadUInt32(APTR.FromPointer(file.Value.Raw<<2),36);
        uint arena=handle-(CpxArena.Scratch+6*512+CpxArena.IndexBytes+8),seen=0;
        for(uint chunk=0;chunk<3;chunk++)
        {
            for(uint n=0;n<8192;n++) APTR.WriteUInt8(buffer,(int)n,LeaseByte(n,chunk));
            if(DOS.Write(file.Value,buffer,8192)!=8192) return 1603;
            uint hint=APTR.ReadUInt32(APTR.FromPointer(arena+4076),0);
            if(hint!=0&&APTR.ReadUInt32(APTR.FromPointer(hint),0)==4) seen|=1;
        }
        if((seen&1)==0) return 1604;
        Report(230);
        if(DOS.DoPkt(port,27,0,0,0,0,0)==0||DOS.Close(file.Value)==0) return 1605;
        Report(231);
        if(DOS.Inhibit("CPX0:",1)==0||DOS.Inhibit("CPX0:",0)==0) return 1606;
        file=DOS.Open("CPX0:Lease",DOS.FileMode.ReadWrite); if(!file.HasValue) return 1607;
        handle=APTR.ReadUInt32(APTR.FromPointer(file.Value.Raw<<2),36);
        arena=handle-(CpxArena.Scratch+6*512+CpxArena.IndexBytes+8);
        for(uint chunk=0;chunk<3;chunk++)
        {
            if(DOS.Read(file.Value,buffer,8192)!=8192) return 1608;
            for(uint n=0;n<8192;n++) if(APTR.ReadUInt8(buffer,(int)n)!=LeaseByte(n,chunk)) return 1609;
        }
        for(uint chunk=3;chunk<5;chunk++)
        {
            for(uint n=0;n<8192;n++) APTR.WriteUInt8(buffer,(int)n,LeaseByte(n,chunk));
            if(DOS.Write(file.Value,buffer,8192)!=8192) return 1610;
            uint hint=APTR.ReadUInt32(APTR.FromPointer(arena+4076),0);
            if(hint!=0&&APTR.ReadUInt32(APTR.FromPointer(hint),0)==4) seen|=2;
        }
        if(seen!=3) return 1611;
        if(DOS.Close(file.Value)==0) return 1612;
        Exec.FreeMem(buffer.Raw,8192);
        APTR.WriteUInt32(control,1960,seen); APTR.WriteUInt32(control,1964,40960);
        return 0;
    }
}
