using Amiga;

// Invoked from the Workbench startup-sequence after LoadWB. RDB supplies the handler.
public static class WorkbenchProbe
{
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
