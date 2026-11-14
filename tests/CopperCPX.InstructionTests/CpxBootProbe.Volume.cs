using Amiga;
namespace CopperCPX.InstructionTests;
public static partial class CpxBootProbe
{
    private static int FormatWork(APTR control,APTR port,APTR data)
    {
        APTR.WriteUInt32(control,36,700); APTR.WriteUInt32(control,20,700);
        if(DOS.DoPkt(port,25,(int)(data.Raw>>2),0,0,0,0)==0||APTR.ReadUInt32(data,24)!=uint.MaxValue) return Fail(control,701);
        if(DOS.Format(CString.FromLiteral("CPX0:"),CString.FromLiteral("Wrong"),0x444f5301)!=0||(uint)DOS.IoErr()!=115) return Fail(control,702);
        // Actual DOS passes Latin-1 bytes, not a UTF-8 C string.
        APTR label=APTR.FromPointer(control.Raw+1600);
        for(int n=0;n<30;n++) APTR.WriteUInt8(label,n,0xe9); APTR.WriteUInt8(label,30,0);
        APTR.WriteUInt32(control,20,703);
        if(DOS.Format(CString.FromLiteral("CPX0:"),CString.FromPointer(label.Raw),0x43505834)==0) return Fail(control,703);
        if(!PublishedLabel(port,label,30)) return Fail(control,704);
        var root=DOS.Lock("CPX0:",DOS.LockMode.Shared); if(!root.HasValue) return Fail(control,705);
        if(DOS.Format(CString.FromLiteral("CPX0:"),CString.FromLiteral("Busy"),0x43505834)!=0||(uint)DOS.IoErr()!=202) return Fail(control,706);
        DOS.UnLock(root.Value);
        if(DOS.Inhibit(CString.FromLiteral("CPX0:"),-1)==0) return Fail(control,707);
        if(DOS.Format(CString.FromLiteral("CPX0:"),CString.FromLiteral("Fresh"),0x43505834)==0) return Fail(control,708);
        var file=DOS.Open("CPX0:Kept",DOS.FileMode.NewFile); if(!file.HasValue) return Fail(control,709);
        if(!CheckUnsupportedPackets(control,port,APTR.ReadUInt32(APTR.FromPointer(file.Value.Raw<<2),36),209)) return Fail(control,960);
        APTR.WriteUInt32(data,0,0x12345678);
        if(DOS.Write(file.Value,data,4)!=4) return Fail(control,710);
        APTR.WriteUInt8(label,0,0xc4); APTR.WriteUInt8(label,1,87); APTR.WriteUInt8(label,2,111);
        APTR.WriteUInt8(label,3,114); APTR.WriteUInt8(label,4,107); APTR.WriteUInt8(label,5,0xff); APTR.WriteUInt8(label,6,0);
        APTR.WriteUInt32(control,20,711);
        if(DOS.Relabel(CString.FromLiteral("CPX0:"),CString.FromPointer(label.Raw))==0) return Fail(control,711);
        if(!PublishedLabel(port,label,6)) return Fail(control,712);
        APTR.WriteUInt8(label,6,58); APTR.WriteUInt8(label,7,0);
        root=DOS.Lock(CString.FromPointer(label.Raw),DOS.LockMode.Shared); if(!root.HasValue) return Fail(control,722);
        DOS.UnLock(root.Value); APTR.WriteUInt8(label,6,0);
        if(DOS.Close(file.Value)==0) return Fail(control,713);
        if(DOS.Inhibit(CString.FromLiteral("CPX0:"),-1)==0||DOS.Inhibit(CString.FromLiteral("CPX0:"),0)==0) return Fail(control,714);
        if(!PublishedLabel(port,label,6)) return Fail(control,715);
        if(DOS.DoPkt(port,23,0,(int)(data.Raw>>2),0,0,0)==0||APTR.ReadUInt8(data,8)!=6) return Fail(control,716);
        for(int n=0;n<6;n++) if(APTR.ReadUInt8(data,n+9)!=APTR.ReadUInt8(label,n)) return Fail(control,717);
        file=DOS.Open("CPX0:Kept",DOS.FileMode.OldFile); if(!file.HasValue) return Fail(control,718);
        if(DOS.Read(file.Value,data,4)!=4||APTR.ReadUInt32(data,0)!=0x12345678||DOS.Close(file.Value)==0) return Fail(control,719);
        if((APTR.ReadUInt32(control,60)&16)!=0&&!PrivateTrim(control,port,data,false)) return Fail(control,951);
        if(DOS.DoPkt(port,27,0,0,0,0,0)==0) return Fail(control,720);
        APTR.WriteUInt32(control,48,Exec.AvailMem(Exec.MemoryFlags.Public)); Report(226);
        if(DOS.DoPkt(port,5,0,0,0,0,0)==0) return Fail(control,721);
        DOS.Delay(5); APTR.WriteUInt32(control,52,Exec.AvailMem(Exec.MemoryFlags.Public)); Report(210); return 0;
    }
    private static bool PublishedLabel(APTR port,APTR label,uint length)
    {
        uint volume=(uint)DOS.DoPkt(port,7,0,0,0,0,0); if(volume==0) return false;
        APTR name=APTR.FromPointer(APTR.ReadUInt32(APTR.FromPointer(volume<<2),40)<<2);
        if(APTR.ReadUInt8(name,0)!=length) return false;
        for(uint n=0;n<length;n++) if(APTR.ReadUInt8(name,(int)n+1)!=APTR.ReadUInt8(label,(int)n)) return false;
        return true;
    }
}
