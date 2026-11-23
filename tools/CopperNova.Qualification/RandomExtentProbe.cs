using Amiga;

public static partial class WorkbenchProbe
{
    public static int CreateEightExtents()=>CreateInterleaved(8);
    public static int CreateSixtyFourExtents()=>CreateInterleaved(64);
    public static int CreateThousandExtents()=>CreateInterleaved(1024);

    private static int CreateInterleaved(int extents)
    {
        DOS.DOSLibraryBase=Exec.OpenLibraryRaw(CString.FromLiteral("dos.library"),37);
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)),184,uint.MaxValue);
        var buffer=APTR.FromPointer(Exec.AllocMem(65536,Exec.MemoryFlags.Fast|Exec.MemoryFlags.Clear));
        var port=DOS.DeviceProc("CNP0:");
        var target=DOS.Open("CNP0:Target",DOS.FileMode.NewFile);
        var other=DOS.Open("CNP0:Spacer",DOS.FileMode.NewFile);
        if(buffer.IsNull||port.IsNull||!target.HasValue||!other.HasValue) { Mark(240); return 20; }
        int sectors=1024/extents;
        for(int block=0;block<1024;block+=sectors)
        {
            for(int i=0;i<sectors;i++) APTR.WriteUInt32(buffer,i*512,(uint)(block+i)+0x54000000);
            if(DOS.Write(target.Value,buffer,sectors*512)!=sectors*512) { Mark(241); return 20; }
            for(int i=0;i<sectors;i++) APTR.WriteUInt32(buffer,i*512,(uint)(block+i)+0x53000000);
            if(DOS.Write(other.Value,buffer,sectors*512)!=sectors*512) { Mark(242); return 20; }
        }
        if(DOS.Close(target.Value)==0||DOS.Close(other.Value)==0||DOS.DoPkt(port,27,0,0,0,0,0)==0) { Mark(243); return 20; }
        Mark(210); return 0;
    }

    // Stratified permutation over the same 512 KiB address space for every layout.
    // Complementary sector pairs give an exactly balanced sample of chain depths.
    public static int RandomReadSector(int i)
    {
        int stratum=(i*13+7)&31;
        stratum^=stratum>>2;
        int half=stratum<16?stratum:31-stratum;
        int offset=(half*13+5)&31;
        return stratum*32+(stratum<16?offset:31-offset);
    }

    public static int RandomExtentReadEntry()
    {
        DOS.DOSLibraryBase=Exec.OpenLibraryRaw(CString.FromLiteral("dos.library"),37);
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)),184,uint.MaxValue);
        var buffer=APTR.FromPointer(Exec.AllocMem(512,Exec.MemoryFlags.Fast));
        var target=DOS.Open("CNP0:Target",DOS.FileMode.OldFile);
        if(buffer.IsNull||!target.HasValue) { Mark(244); return 20; }
        for(int pass=0;pass<3;pass++)
        for(int i=0;i<32;i++)
        {
            int sector=RandomReadSector(i);
            Mark(181);
            if(DOS.Seek(target.Value,sector*512,-1)<0) { Mark(245); return 20; }
            Mark(182);
            if(DOS.Read(target.Value,buffer,512)!=512) { Mark(246); return 20; }
            Mark(183);
            if(APTR.ReadUInt32(buffer,0)!=0x54000000+(uint)sector) { Mark(247); return 20; }
            for(int at=4;at<512;at+=4) if(APTR.ReadUInt32(buffer,at)!=0) { Mark(248); return 20; }
        }
        if(DOS.Close(target.Value)==0) { Mark(249); return 20; }
        Mark(211); return 0;
    }
}
