using Amiga;

public static partial class WorkbenchProbe
{
    public static int LabelFormatEntry()=>LabelWorkload(0);
    public static int LabelRenameEntry()=>LabelWorkload(1);
    public static int LabelColdEntry()=>LabelWorkload(2);

    public static int CommentWriteEntry()=>LabelWorkload(3);

    private static int LabelWorkload(int mode)
    {
        DOS.DOSLibraryBase=Exec.OpenLibraryRaw(CString.FromLiteral("dos.library"),37);
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)),184,uint.MaxValue);
        APTR buffer=APTR.FromPointer(Exec.AllocMem(512,Exec.MemoryFlags.Public|Exec.MemoryFlags.Clear));
        if(buffer.IsNull) { Mark(240); return 20; }
        if(mode==0)
        {
            var segment=DOS.LoadSeg("SYS:CopperNovaFormat");
            if(!segment.HasValue) { Mark(241); return 20; }
            const string args="CNP0: INITIALIZE NAME \"Work Disk\"\n";
            int rc=DOS.RunCommand(segment.Value,4096,APTR.FromPointer(CString.FromLiteral(args)),args.Length);
            DOS.UnLoadSeg(segment.Value);
            if(rc!=0) { Mark(242); return 20; }
        }
        var root=DOS.Lock("CNP0:",(DOS.LockMode)(-2));
        if(!root.HasValue) { Mark(243); return 20; }
        APTR name=APTR.FromPointer(buffer.Raw+400);
        APTR.WriteUInt32(name,0,0x4b6573e4); // Native bytes for Kesä.
        if(mode==1 && DOS.Relabel("CNP0:",CString.FromPointer(name.Raw))==0) { Mark(244); return 20; }
        if(DOS.Examine(root.Value,buffer)==0) { Mark(245); return 20; }
        if(mode==0)
        {
            if(APTR.ReadUInt32(buffer,8)!=0x576f726b || APTR.ReadUInt32(buffer,12)!=0x20446973 ||
                APTR.ReadUInt8(buffer,16)!='k'||APTR.ReadUInt8(buffer,17)!=0) { Mark(246); return 20; }
        }
        else if(APTR.ReadUInt32(buffer,8)!=0x4b6573e4 || APTR.ReadUInt8(buffer,12)!=0) { Mark(247); return 20; }
        DOS.UnLock(root.Value);
        APTR.WriteUInt8(name,4,58);
        var named=DOS.Lock(mode==0?CString.FromLiteral("Work Disk:"):CString.FromPointer(name.Raw),(DOS.LockMode)(-2));
        if(!named.HasValue) { Mark(248); return 20; }
        DOS.UnLock(named.Value);
        if(mode==1)
        {
            var old=DOS.Lock("Work Disk:",(DOS.LockMode)(-2));
            if(old.HasValue) { Mark(249); return 20; }
        }
        APTR dir=APTR.FromPointer(buffer.Raw+300),oldPath=APTR.FromPointer(buffer.Raw+320),path=APTR.FromPointer(buffer.Raw+340);
        for(int i=0;i<3;i++)
        {
            APTR p=i==0?dir:i==1?oldPath:path;
            APTR.WriteUInt32(p,0,0x434e5030); APTR.WriteUInt8(p,4,58);
            APTR.WriteUInt8(p,5,(byte)(i==0?0xc4:0xe4));
            if(i!=0) { APTR.WriteUInt8(p,6,47); APTR.WriteUInt8(p,7,(byte)(i==1?0xf6:0xfc)); }
        }
        if(mode==3)
        {
            var directory=DOS.CreateDir(CString.FromPointer(dir.Raw));
            if(!directory.HasValue) { Mark(235); return 20; }
            DOS.UnLock(directory.Value);
            var file=DOS.Open(CString.FromPointer(oldPath.Raw),DOS.FileMode.NewFile);
            if(!file.HasValue || DOS.Close(file.Value)==0) { Mark(230); return 20; }
            APTR.WriteUInt8(oldPath,7,0xd6);
            if(DOS.Rename(CString.FromPointer(oldPath.Raw),CString.FromPointer(path.Raw))==0) { Mark(236); return 20; }
            APTR.WriteUInt8(oldPath,5,0xc5); APTR.WriteUInt8(oldPath,6,0);
            if(DOS.Rename(CString.FromPointer(dir.Raw),CString.FromPointer(oldPath.Raw))==0) { Mark(237); return 20; }
            APTR.WriteUInt8(path,5,0xe5); APTR.WriteUInt8(path,7,0xdc);
            for(int i=0;i<79;i++) APTR.WriteUInt8(name,i,0xff);
            APTR.WriteUInt8(name,79,0);
            if(DOS.SetComment(CString.FromPointer(path.Raw),CString.FromPointer(name.Raw))==0 ||
                DOS.DoPkt(DOS.DeviceProc("CNP0:"),27,0,0,0,0,0)==0) { Mark(231); return 20; }
        }
        if(mode==2)
        {
            APTR.WriteUInt8(dir,5,0xe5);
            var directory=DOS.Lock(CString.FromPointer(dir.Raw),(DOS.LockMode)(-2));
            if(!directory.HasValue || DOS.Examine(directory.Value,buffer)==0 || APTR.ReadUInt8(buffer,8)!=0xc5 ||
                DOS.ExNext(directory.Value,buffer)==0 || APTR.ReadUInt8(buffer,8)!=0xfc || APTR.ReadUInt8(buffer,9)!=0)
            { Mark(238); return 20; }
            DOS.UnLock(directory.Value);
            APTR.WriteUInt8(path,5,0xe5); APTR.WriteUInt8(path,7,0xdc);
            var file=DOS.Lock(CString.FromPointer(path.Raw),(DOS.LockMode)(-2));
            if(!file.HasValue || DOS.Examine(file.Value,buffer)==0) { Mark(232); return 20; }
            for(int i=0;i<79;i++) if(APTR.ReadUInt8(buffer,144+i)!=255) { Mark(233); return 20; }
            if(APTR.ReadUInt8(buffer,223)!=0) { Mark(234); return 20; }
            DOS.UnLock(file.Value);
        }
        Mark((byte)(210+mode)); return 0;
    }
}
