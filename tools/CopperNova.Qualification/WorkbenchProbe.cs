using Amiga;

// Invoked from the Workbench startup-sequence after LoadWB. RDB supplies the handler.
public static partial class WorkbenchProbe
{
    public const int FullDiskPayload=2*1024*1024;
    public const int RetirementPayload=48*1024*1024;

    public static int FullDiskEntry()=>FullDiskWorkload(FullDiskPayload,false,false);
    public static int FullDiskRetireEntry()=>FullDiskWorkload(RetirementPayload,true,false);
    public static int SyncedDiskEntry()=>FullDiskWorkload(FullDiskPayload,false,true);
    public static int FormatEntry()=>FormatWorkload(false);
    public static int FormatGrowingEntry()=>FormatWorkload(false,false,true);
    public static int FormatRejectEntry()=>FormatWorkload(true);

    public static int RetiredFormatEntry()=>FormatWorkload(true,true);

    private static int FormatWorkload(bool reject,bool retired=false,bool growing=false)
    {
        var dos=Exec.OpenLibraryRaw("dos.library",37);
        if(dos.IsNull) { Mark(240); return 20; }
        DOS.DOSLibraryBase=dos;
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)),184,uint.MaxValue);
        var segment=DOS.LoadSeg("SYS:CopperNovaFormat");
        if(!segment.HasValue) { Mark(241); return 20; }
        Mark(180);
        string arguments=growing?"CNP0: INITIALIZE V6\n":"CNP0: INITIALIZE\n";
        int result=DOS.RunCommand(segment.Value,4096,APTR.FromPointer(CString.FromLiteral(arguments)),arguments.Length);
        DOS.UnLoadSeg(segment.Value);
        var root=DOS.Lock("CNP0:",(DOS.LockMode)(-2));
        if(root.HasValue) DOS.UnLock(root.Value);
        if(result!=(reject?20:0)||root.HasValue==retired) { Mark(242); return 20; }
        Mark(reject?(byte)215:(byte)214);
        return 0;
    }

    private static int FullDiskWorkload(int payload,bool retire,bool synchronizedStart)
    {
        var dos=Exec.OpenLibraryRaw(CString.FromLiteral("dos.library"),37);
        if(dos.IsNull) { Mark(240); return 20; }
        DOS.DOSLibraryBase=dos;
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)),184,uint.MaxValue);
        var buffer=APTR.FromPointer(Exec.AllocMem(65536,Exec.MemoryFlags.Public|Exec.MemoryFlags.Fast));
        var port=DOS.DeviceProc("CNP0:");
        if(buffer.IsNull||port.IsNull) { Mark(240); return 20; }
        var existing=DOS.Open("CNP0:Dst/Keep",DOS.FileMode.OldFile);
        if(existing.HasValue)
        {
            if(retire)
            {
                if(DOS.Close(existing.Value)==0) { Mark(243); return 20; }
                Mark(181);
                if(DOS.DeleteFile("CNP0:Dst/Keep")==0) { Mark(252); return 20; }
                Mark(213); return 0;
            }
            Mark(182);
            for(int offset=0;offset<payload;offset+=65536)
            {
                if(DOS.Read(existing.Value,buffer,65536)!=65536) { Mark(241); return 20; }
                for(int at=0;at<65536;at+=511)
                    if(APTR.ReadUInt8(buffer,at)!=(byte)(at^0x6d)) { Mark(242); return 20; }
            }
            if(DOS.Read(existing.Value,buffer,1)!=0||DOS.Close(existing.Value)==0) { Mark(243); return 20; }
            Mark(211); return 0;
        }

        if(retire)
        {
            var surviving=DOS.Open("CNP0:Dst/Gap",DOS.FileMode.OldFile);
            if(surviving.HasValue)
            {
                if(DOS.Read(surviving.Value,buffer,512)!=512||DOS.Close(surviving.Value)==0) { Mark(241); return 20; }
                for(int at=0;at<512;at++)
                    if(APTR.ReadUInt8(buffer,at)!=(byte)(at^0x6d)) { Mark(242); return 20; }
                Mark(211); return 0;
            }
        }
        for(int i=0;i<65536;i++) APTR.WriteUInt8(buffer,i,(byte)(i^0x6d));
        if(synchronizedStart)
        {
            // Port 1 is separate from Workbench's mouse. Waiting here leaves
            // regular CIA, raster and input.device interrupts running.
            Mark(179);
            var joy1=APTR.FromPointer(0xdff00c);
            while((APTR.ReadUInt16(joy1,0)&2)==0) { }
        }
        Mark(181);
        var directory=DOS.CreateDir("CNP0:Dst");
        if(!directory.HasValue) { Mark(244); return 20; }
        DOS.UnLock(directory.Value);
        var keep=DOS.Open("CNP0:Dst/Keep",DOS.FileMode.NewFile);
        if(!keep.HasValue||DOS.Write(keep.Value,buffer,65536)!=65536) { Mark(245); return 20; }
        // Force an additional extent for Keep so cold readback exercises
        // both the inline wide address and the published extent page.
        var gap=DOS.Open("CNP0:Dst/Gap",DOS.FileMode.NewFile);
        if(!gap.HasValue||DOS.Write(gap.Value,buffer,512)!=512||DOS.Close(gap.Value)==0) { Mark(246); return 20; }
        for(int written=65536;written<payload;written+=65536)
            if(DOS.Write(keep.Value,buffer,65536)!=65536) { Mark(247); return 20; }
        if(DOS.Close(keep.Value)==0) { Mark(250); return 20; }
        Mark(183);
        if(DOS.DoPkt(port,27,0,0,0,0,0)==0) { Mark(251); return 20; }
        Mark(210); return 0;
    }

    public static int ExtentCreateEntry()=>ExtentWorkload(0);
    public static int ExtentReadEntry()=>ExtentWorkload(1);
    public static int ExtentDeleteEntry()=>ExtentWorkload(2);
    public static int ExtentNeighborEntry()=>ExtentWorkload(3);

    private static int ExtentWorkload(int mode)
    {
        var dos=Exec.OpenLibraryRaw(CString.FromLiteral("dos.library"),37);
        if(dos.IsNull) { Mark(240); return 20; }
        DOS.DOSLibraryBase=dos;
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)),184,uint.MaxValue);
        var port=DOS.DeviceProc("CNP0:");
        var buffer=APTR.FromPointer(Exec.AllocMem(512,Exec.MemoryFlags.Public));
        if(port.IsNull||buffer.IsNull) { Mark(241); return 20; }
        if(mode==2)
        {
            if(DOS.DeleteFile("CNP0:target")==0 || DOS.DoPkt(port,27,0,0,0,0,0)==0) { Mark(242); return 20; }
            Mark(212); return 0;
        }
        var target=DOS.Open("CNP0:target",mode==0?DOS.FileMode.NewFile:DOS.FileMode.OldFile);
        var neighbor=DOS.Open("CNP0:neighbor",mode==0?DOS.FileMode.NewFile:DOS.FileMode.OldFile);
        if(target.HasValue!=(mode!=3)||!neighbor.HasValue) { Mark(243); return 20; }
        for(int block=0;block<64;block++)
        for(int which=mode==3?1:0;which<2;which++)
        {
            var file=which==0?target.Value:neighbor.Value;
            if(mode!=0 && DOS.Read(file,buffer,512)!=512) { Mark(244); return 20; }
            for(int at=0;at<512;at++)
            {
                byte expected=(byte)(at+block*17+which*97);
                if(mode==0) APTR.WriteUInt8(buffer,at,expected);
                else if(APTR.ReadUInt8(buffer,at)!=expected) { Mark(245); return 20; }
            }
            if(mode==0 && DOS.Write(file,buffer,512)!=512) { Mark(246); return 20; }
        }
        if(mode!=3 && DOS.Close(target.Value)==0 || DOS.Close(neighbor.Value)==0) { Mark(247); return 20; }
        if(mode==0 && DOS.DoPkt(port,27,0,0,0,0,0)==0) { Mark(248); return 20; }
        Mark((byte)(210+mode)); return 0;
    }

    private static void Mark(byte value)
    {
        APTR.WriteUInt8(APTR.FromPointer(0xbfe301), 0, 255);
        APTR.WriteUInt8(APTR.FromPointer(0xbfe101), 0, value);
    }

    public static int Entry()
    {
        var dos = Exec.OpenLibraryRaw(CString.FromLiteral("dos.library"), 37);
        DOS.DOSLibraryBase = dos;
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)),184,uint.MaxValue);
        APTR buf = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Clear | Exec.MemoryFlags.Public));
        Mark(180);
        APTR port = DOS.DeviceProc("CNP0:");
        if (port.IsNull || buf.IsNull) { Mark(240); return 20; }
        var existing = DOS.Open("CNP0:Dst/Keep", DOS.FileMode.OldFile);
        if (existing.HasValue) {
            if (DOS.Read(existing.Value,buf,512)!=512) { Mark(241); return 20; }
            for (int i=0;i<512;i++) if (APTR.ReadUInt8(buf,i)!=(byte)(i ^ 0x6d)) { Mark(242); return 20; }
            DOS.Close(existing.Value);
            var dst = DOS.Lock("CNP0:Dst",(DOS.LockMode)(-2));
            if (!dst.HasValue || DOS.Examine(dst.Value,buf)==0 || DOS.ExNext(dst.Value,buf)==0 ||
                APTR.ReadUInt32(buf,124)!=512 || APTR.ReadUInt8(buf,8)!=75 ||
                DOS.ExNext(dst.Value,buf)!=0 || (int)DOS.IoErr()!=232) { Mark(243); return 20; }
            DOS.UnLock(dst.Value);
            DOS.PutStr("CNP2 cold readback passed.\n"); Mark(211); return 0;
        }
        Mark(181);
        var a=DOS.CreateDir("CNP0:Src"); if(!a.HasValue) { Mark(244);return 20; } DOS.UnLock(a.Value);
        var b=DOS.CreateDir("CNP0:Dst"); if(!b.HasValue) { Mark(245);return 20; } DOS.UnLock(b.Value);
        var old=DOS.Open("CNP0:Src/Old",DOS.FileMode.NewFile);
        if(!old.HasValue||DOS.Write(old.Value,buf,512)!=512||DOS.Close(old.Value)==0) { Mark(246);return 20; }
        if(DOS.Rename("CNP0:Src/Old","CNP0:Dst/Moved")==0) { Mark(247);return 20; }
        b=DOS.Lock("CNP0:Dst",(DOS.LockMode)(-2));
        if(!b.HasValue||DOS.Examine(b.Value,buf)==0||DOS.ExNext(b.Value,buf)==0||
            APTR.ReadUInt8(buf,8)!=77||APTR.ReadUInt32(buf,124)!=512) { Mark(248);return 20; }
        DOS.UnLock(b.Value);
        if(DOS.DeleteFile("CNP0:Dst/Moved")==0||DOS.DeleteFile("CNP0:Src")==0) { Mark(249);return 20; }
        var keep=DOS.Open("CNP0:Dst/Keep",DOS.FileMode.NewFile);
        for(int i=0;i<512;i++) APTR.WriteUInt8(buf,i,(byte)(i ^ 0x6d));
        if(!keep.HasValue||DOS.Write(keep.Value,buf,512)!=512||DOS.Close(keep.Value)==0) { Mark(250);return 20; }
        if(DOS.DoPkt(port,27,0,0,0,0,0)==0) { Mark(251);return 20; }
        DOS.PutStr("CNP2 flush acknowledged.\n"); Mark(210); return 0;
    }
}
