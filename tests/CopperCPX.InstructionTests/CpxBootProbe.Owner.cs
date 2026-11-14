using Amiga;
namespace CopperCPX.InstructionTests;
public static partial class CpxBootProbe
{
    private static int OwnerWork(APTR control,APTR port,APTR data)
    {
        APTR.WriteUInt32(control,36,800); APTR.WriteUInt32(control,20,800);
        APTR text=APTR.FromPointer(control.Raw+1600);
        for(int n=0;n<79;n++) APTR.WriteUInt8(text,n,0xe9); APTR.WriteUInt8(text,79,0);
        if(DOS.SetComment(CString.FromLiteral("CPX0:Inline"),CString.FromPointer(text.Raw))==0) return Fail(control,801);
        if(DOS.SetOwner(CString.FromLiteral("CPX0:Inline"),unchecked((int)0x89abcdef))==0) return Fail(control,802);
        var file=DOS.Open("CPX0:Inline",DOS.FileMode.OldFile); if(!file.HasValue) return Fail(control,803);
        uint handle=APTR.ReadUInt32(APTR.FromPointer(file.Value.Raw<<2),36);
        if(!CheckUnsupportedPackets(control,port,handle,209)) return Fail(control,960);
        if(DOS.DoPkt(port,26410,(int)handle,(int)(data.Raw>>2),0,0,0)==0||APTR.ReadUInt32(data,224)!=0x89abcdef||APTR.ReadUInt8(data,144)!=79) return Fail(control,804);
        for(int n=0;n<79;n++) if(APTR.ReadUInt8(data,n+145)!=0xe9) return Fail(control,805);
        if(DOS.Close(file.Value)==0) return Fail(control,806);
        if(DOS.SetOwner(CString.FromLiteral("CPX0:Inline"),0)==0||DOS.SetComment("CPX0:Inline","")==0) return Fail(control,807);
        if(DOS.SetOwner(CString.FromLiteral("CPX0:Inline"),unchecked((int)0xfedc9876))==0) return Fail(control,808);
        if(DOS.SetComment("CPX0:Inline","owned")==0) return Fail(control,809);
        if(DOS.SetOwner(CString.FromLiteral("CPX0:"),0x12340005)==0) return Fail(control,810);
        if(DOS.SetOwner(CString.FromLiteral("CPX0:missing"),1)!=0||(uint)DOS.IoErr()!=205) return Fail(control,811);
        if(DOS.DoPkt(port,1023,1,0,0,0,0)==0) return Fail(control,812);
        if(DOS.SetOwner(CString.FromLiteral("CPX0:Inline"),1)!=0||(uint)DOS.IoErr()!=214) return Fail(control,813);
        if(DOS.DoPkt(port,1023,0,0,0,0,0)==0) return Fail(control,814);
        if(DOS.Relabel(CString.FromLiteral("CPX0:"),CString.FromLiteral("Owned"))==0) return Fail(control,815);
        APTR.WriteUInt32(control,20,816);
        if(DOS.Inhibit(CString.FromLiteral("CPX0:"),-1)==0||DOS.Inhibit(CString.FromLiteral("CPX0:"),0)==0) return Fail(control,816);
        var root=DOS.Lock("CPX0:",DOS.LockMode.Shared); if(!root.HasValue) return Fail(control,817);
        if(DOS.Examine(root.Value,data)==0||APTR.ReadUInt32(data,224)!=0x12340005) return Fail(control,818);
        APTR enumeration=APTR.FromPointer(control.Raw+1700);
        for(int n=0;n<20;n++) APTR.WriteUInt8(enumeration,n,0);
        int result=DOS.DoPkt(port,1033,(int)root.Value.Raw,(int)data.Raw,1024,7,(int)enumeration.Raw);
        if(result!=0||(uint)DOS.IoErr()!=232||APTR.ReadUInt32(enumeration,0)!=9) return Fail(control,819);
        APTR cursor=data; uint owners=0;
        for(uint n=0;n<9;n++)
        {
            if(cursor.IsNull) return Fail(control,820);
            uint packed=APTR.ReadUInt32(cursor,36);
            if(packed!=0) { if(packed!=0xfedc9876) return Fail(control,821); owners++; }
            cursor=APTR.FromPointer(APTR.ReadUInt32(cursor,0));
        }
        if(owners!=1||!cursor.IsNull) return Fail(control,822);
        DOS.UnLock(root.Value);
        var original=DOS.Lock("CPX0:Inline",DOS.LockMode.Shared);
        if(!original.HasValue||DOS.MakeLink("CPX0:Hard",(int)original.Value.Raw,0)==0) return Fail(control,901);
        DOS.UnLock(original.Value);
        if(DOS.MakeLink("CPX0:Soft",(int)CString.ToUInt32(CString.FromLiteral(":Inline")),1)==0) return Fail(control,902);
        APTR.WriteUInt32(control,36,900);
        int hard=(int)CString.ToUInt32(CString.FromLiteral("Hard"));
        int soft=(int)CString.ToUInt32(CString.FromLiteral("Soft"));
        if(DOS.DoPkt(port,26406,0,hard,0,0,0)!=13) return Fail(control,903);
        APTR.WriteUInt8(data,0,0xa5); APTR.WriteUInt8(data,13,0xa5);
        if(DOS.DoPkt(port,26406,0,soft,(int)data.Raw,12,0)!=-2||(uint)DOS.IoErr()!=120||APTR.ReadUInt8(data,0)!=0xa5) return Fail(control,904);
        APTR expected=APTR.FromPointer(CString.ToUInt32(CString.FromLiteral("Owned:Inline")));
        for(int n=0;n<2;n++)
        {
            if(DOS.DoPkt(port,26406,0,n==0?hard:soft,(int)data.Raw,13,0)!=12) return Fail(control,905);
            for(int i=0;i<13;i++) if(APTR.ReadUInt8(data,i)!=APTR.ReadUInt8(expected,i)) return Fail(control,906);
            if(APTR.ReadUInt8(data,13)!=0xa5) return Fail(control,907);
        }
        APTR.WriteUInt32(control,36,0);
        if(DOS.DeleteFile("CPX0:Hard")==0||DOS.DeleteFile("CPX0:Soft")==0) return Fail(control,908);
        if(!PrivateTrim(control,port,data,true)) return Fail(control,950);
        if(DOS.DoPkt(port,27,0,0,0,0,0)==0) return Fail(control,823);
        APTR.WriteUInt32(control,48,Exec.AvailMem(Exec.MemoryFlags.Public)); Report(226);
        if(DOS.DoPkt(port,5,0,0,0,0,0)==0) return Fail(control,824);
        DOS.Delay(5); APTR.WriteUInt32(control,52,Exec.AvailMem(Exec.MemoryFlags.Public)); Report(210); return 0;
    }
}
