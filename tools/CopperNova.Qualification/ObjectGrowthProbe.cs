using Amiga;

public static class ObjectGrowthProbe
{
#if NOVA_TREE_COMPARISON
    private const int Fanout=3;
#else
    private const int Fanout=5;
#endif
#if NOVA_TREE_PAYLOAD
    public const bool PopulateAll=true;
#else
    public const bool PopulateAll=false;
#endif
    public const int Files=Fanout*Fanout*Fanout*Fanout*Fanout;
    public static int Entry()
    {
        var dos=Exec.OpenLibraryRaw("dos.library",37);
        if(dos.IsNull) return Mark(240);
        DOS.DOSLibraryBase=dos;
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)),184,uint.MaxValue);
        var port=DOS.DeviceProc("CNP0:");
        var path=APTR.FromPointer(Exec.AllocMem(32,Exec.MemoryFlags.Public));
        if(port.IsNull) return Mark(241);
        if(path.IsNull) return Mark(248);
        APTR.WriteUInt32(path,0,0x434e5030); APTR.WriteUInt8(path,4,58);
        int total=0,perDirectory=0;
        var config=DOS.Open("SYS:scale",DOS.FileMode.OldFile);
        if(config.HasValue)
        {
            var options=APTR.FromPointer(path.Raw+24);
            if(DOS.Read(config.Value,options,8)!=8) return Mark(255);
            total=(int)APTR.ReadUInt32(options,0); perDirectory=(int)APTR.ReadUInt32(options,4);
            if(DOS.Close(config.Value)==0||total<1||perDirectory<1) return Mark(255);
        }
        var cutting=DOS.Lock("SYS:cut-mode",(DOS.LockMode)(-2));
        if(cutting.HasValue)
        {
            DOS.UnLock(cutting.Value);
            var trial=DOS.Open(total==0?"CNP0:C/trial":"CNP0:trial",DOS.FileMode.NewFile);
            if(!trial.HasValue||DOS.Close(trial.Value)==0) return Mark(249);
            Mark(180);
            DOS.DoPkt(port,27,0,0,0,0,0);
            return Mark(250);
        }
        var trialLock=DOS.Lock(total==0?"CNP0:C/trial":"CNP0:trial",(DOS.LockMode)(-2));
        if(trialLock.HasValue||(int)DOS.IoErr()!=205) return Mark(251);
        var gone=DOS.Lock(total==0?"CNP0:B/gone":"CNP0:gone",(DOS.LockMode)(-2));
        var born=DOS.Lock(total==0?"CNP0:A/born":"CNP0:born",(DOS.LockMode)(-2));
        int phase=gone.HasValue?2:born.HasValue?1:0;
        Mark(178);
        if(born.HasValue) DOS.UnLock(born.Value);
        if(gone.HasValue) DOS.UnLock(gone.Value);
        if(total!=0)
        {
            int result=FlatTree(path,total,perDirectory,phase);
            if(result!=0) return result;
        }
        if(total==0&&phase==0)
            for(int depth=1,children=Fanout;depth<=4;depth++,children*=Fanout)
                for(int n=0;n<children;n++)
                {
                    Path(path,depth,n,-1);
                    var directory=DOS.CreateDirRaw(CString.FromPointer(path.Raw));
                    if(directory.Raw==0) return Mark(242);
                    DOS.UnLock(directory);
                }
        for(int i=0;total==0&&i<Files;i++)
        {
            Path(path,4,i/Fanout,i%Fanout);
            var name=CString.FromPointer(path.Raw);
            var file=DOS.OpenRaw(name,phase==0?DOS.FileMode.NewFile:DOS.FileMode.OldFile);
            if(phase==2)
            {
                if(file.Raw!=0||(int)DOS.IoErr()!=205) return Mark(243);
            }
            else
            {
                if(file.Raw==0) return Mark(244);
                if(PopulateAll||i==Files-1)
                {
                    var payload=APTR.FromPointer(path.Raw+24);
                    if(phase==0)
                    {
                        APTR.WriteUInt32(payload,0,0x43504e34);
                        if(DOS.Write(file,payload,4)!=4) return Mark(252);
                    }
                    else if(DOS.Read(file,payload,4)!=4||APTR.ReadUInt32(payload,0)!=0x43504e34) return Mark(253);
                }
                if(DOS.Close(file)==0) return Mark(244);
                if(phase==1&&DOS.DeleteFile(name)==0) return Mark(245);
            }
        }
        if(phase<2)
        {
            var tag=phase==0?DOS.CreateDir(total==0?"CNP0:A/born":"CNP0:born"):DOS.CreateDir(total==0?"CNP0:B/gone":"CNP0:gone");
            if(!tag.HasValue) return Mark(246);
            DOS.UnLock(tag.Value);
            if(DOS.DoPkt(port,27,0,0,0,0,0)==0) return Mark(247);
        }
        return Mark((byte)(210+phase));
    }

    private static void Hex(APTR path,int at,int value)
    {
        for(int i=0;i<6;i++)
        {
            int digit=value&15;
            APTR.WriteUInt8(path,at+5-i,(byte)(digit<10?48+digit:65+digit-10));
            value>>=4;
        }
    }

    private static int FlatTree(APTR path, int total, int perDirectory, int phase)
    {
        BPTR original=BPTR.Null;
        bool relative=total>100000;
        for(int i=0;i<total;i++)
        {
            if((i&2047)==2047) Mark(170);
            Hex(path,5,i/perDirectory);
            if((phase==0||relative)&&i%perDirectory==0)
            {
                APTR.WriteUInt8(path,11,0);
                var absolute=CString.FromPointer(path.Raw);
                var directory=phase==0?DOS.CreateDirRaw(absolute):DOS.LockRaw(absolute,(DOS.LockMode)(-2));
                if(directory.Raw==0) return Mark(242);
                if(relative)
                {
                    var previous=DOS.CurrentDirRaw(directory);
                    if(i==0) original=previous;
                    else DOS.UnLock(previous);
                }
                else DOS.UnLock(directory);
            }
            APTR.WriteUInt8(path,11,47); Hex(path,12,i); APTR.WriteUInt8(path,18,0);
            var name=CString.FromPointer(relative?path.Raw+12:path.Raw);
            var file=DOS.OpenRaw(name,phase==0?DOS.FileMode.NewFile:DOS.FileMode.OldFile);
            if(phase==2)
            {
                if(file.Raw!=0||(int)DOS.IoErr()!=205) return Mark(243);
                continue;
            }
            if(file.Raw==0) return Mark(244);
            var payload=APTR.FromPointer(path.Raw+24);
            if(i%perDirectory==0||i==total-1)
            {
                if(phase==0)
                {
                    APTR.WriteUInt32(payload,0,(uint)i);
                    if(DOS.Write(file,payload,4)!=4) return Mark(252);
                }
                else if(DOS.Read(file,payload,4)!=4||APTR.ReadUInt32(payload,0)!=(uint)i) return Mark(253);
            }
            if(DOS.Close(file)==0) return Mark(244);
            if(phase==1&&DOS.DeleteFile(name)==0) return Mark(245);
        }
        if(relative) DOS.UnLock(DOS.CurrentDirRaw(original));
        return 0;
    }

    private static void Path(APTR p,int depth,int number,int file)
    {
        int end=5;
        for(int j=0;j<depth;j++)
        {
            if(j!=0) APTR.WriteUInt8(p,end++,47);
            APTR.WriteUInt8(p,end++,(byte)(65+number%Fanout));
            number/=Fanout;
        }
        if(file>=0) { APTR.WriteUInt8(p,end++,47); APTR.WriteUInt8(p,end++,(byte)(48+file)); }
        APTR.WriteUInt8(p,end,0);
    }

    private static int Mark(byte value)
    {
        APTR.WriteUInt8(APTR.FromPointer(0xbfe301),0,255);
        APTR.WriteUInt8(APTR.FromPointer(0xbfe101),0,value);
        return value>=240?20:0;
    }
}
