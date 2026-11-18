// Device setup/reporting derived from tools/CopperCPX.Bench/MixedProbe.cs at 6a4380d.
using Amiga;

public static class CachePressureProbe
{
    private static APTR control;
    private static APTR data;
    private static APTR port;

    public static int Entry()
    {
        APTR dos = Exec.OpenLibraryRaw(CString.FromLiteral("dos.library"), 37);
        if (dos.IsNull) { Report(250); return 1; }
        DOS.DOSLibraryBase = dos;
        APTR process = Exec.FindTask(CString.FromPointer(0));
        APTR.WriteUInt32(process, 184, uint.MaxValue);
        control = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Fast | Exec.MemoryFlags.Clear));
        data = APTR.FromPointer(Exec.AllocMem(8192, Exec.MemoryFlags.Public | Exec.MemoryFlags.Fast | Exec.MemoryFlags.Clear));
        if (control.IsNull || data.IsNull) { Report(250); return 2; }
        Report(200); Report((int)(control.Raw >> 24)); Report((int)(control.Raw >> 16)); Report((int)(control.Raw >> 8)); Report((int)control.Raw);

        var segment = DOS.LoadSeg("HANDLER");
        if (!segment.HasValue) return Fail(3);
        APTR.WriteUInt32(control,32,(segment.Value.Raw<<2)+4);
        APTR node = DOS.MakeDosEntry(CString.FromLiteral("CPX0"), (int)DosListType.Device);
        if (node.IsNull) return Fail(4);
        APTR startup = APTR.FromPointer(control.Raw + 64);
        APTR name = APTR.FromPointer(control.Raw + 96);
        APTR environment = APTR.FromPointer(control.Raw + 128);
        CString device = CString.FromLiteral("copperhdf.device");
        APTR.WriteUInt8(name, 0, 16);
        for (int i = 0; i < 16; i++) APTR.WriteUInt8(name, i + 1, APTR.ReadUInt8(APTR.FromPointer(CString.ToUInt32(device)), i));
        APTR.WriteUInt32(startup, DosLayout.FileSysStartupMsg.Unit, 0);
        APTR.WriteUInt32(startup, DosLayout.FileSysStartupMsg.Device, name.Raw >> 2);
        APTR.WriteUInt32(startup, DosLayout.FileSysStartupMsg.Environment, environment.Raw >> 2);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.TableSize, 19);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.SizeBlock, 128);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.Surfaces, 2);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.SectorsPerBlock, 1);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.BlocksPerTrack, 128);
        uint flavor = APTR.ReadUInt32(control, 60);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.Reserved, flavor == 0 ? 0u : 2u);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.LowCylinder, 0);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.HighCylinder, 127);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.NumberOfBuffers, 32);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.BufferMemoryType, 1);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.MaximumTransfer, 65536);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.Mask, 0xFFFFFFFE);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.BootPriority, 0xFFFFFF7F);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.DosType, flavor == 0 ? 0x43505834u : flavor == 1 ? 0x444F5301u : 0x53465300u);
        APTR.WriteUInt32(node, DosLayout.DeviceNode.StackSize, 32768);
        APTR.WriteUInt32(node, DosLayout.DeviceNode.Priority, 10);
        APTR.WriteUInt32(node, DosLayout.DeviceNode.Startup, startup.Raw >> 2);
        APTR.WriteUInt32(node, DosLayout.DeviceNode.SegmentList, segment.Value.Raw);
        APTR.WriteUInt32(node, DosLayout.DeviceNode.GlobalVector, uint.MaxValue);
        if (DOS.AddDosEntry(node) == 0) return Fail(5);
        port = DOS.DeviceProc("CPX0:");
        if (port.IsNull) return Fail(6);


        APTR path=APTR.FromPointer(data.Raw+512);
        APTR.WriteUInt8(path,0,67); APTR.WriteUInt8(path,1,80); APTR.WriteUInt8(path,2,88);
        APTR.WriteUInt8(path,3,48); APTR.WriteUInt8(path,4,58); APTR.WriteUInt8(path,5,100);
        APTR.WriteUInt8(path,7,47); APTR.WriteUInt8(path,8,115); APTR.WriteUInt8(path,10,47);
        APTR.WriteUInt8(path,11,102); APTR.WriteUInt8(path,13,0);
        Report(228);
        if(DOS.Inhibit("CPX0:",1)==0||DOS.Inhibit("CPX0:",0)==0) return Fail(30);
        Report(229);
        Report(220);
        for(int round=0;round<3;round++)
            for(int a=0;a<6;a++) for(int b=0;b<6;b++) for(int c=0;c<2;c++)
            {
                if(!ReadFile(a,b,c)) return FailAt(31,(uint)(round*72+a*12+b*2+c));
                for(int hot=0;hot<6;hot++) if(!ReadFile(hot,0,0)) return Fail(32);
            }
        Report(221);
        Report(226); Report(227); Report(222); Report(223); Report(210);
        return 0;
    }
    private static bool ReadFile(int a,int b,int c)
    {
        APTR path=APTR.FromPointer(data.Raw+512);
        APTR.WriteUInt8(path,6,(byte)(48+a)); APTR.WriteUInt8(path,9,(byte)(48+b)); APTR.WriteUInt8(path,12,(byte)(48+c));
        var file=DOS.Open(CString.FromPointer(path.Raw),DOS.FileMode.OldFile);
        if(!file.HasValue) return false;
        bool ok=DOS.Read(file.Value,data,1)==1&&APTR.ReadUInt8(data,0)==(byte)(a*12+b*2+c);
        return DOS.Close(file.Value)!=0&&ok;
    }
    private static int Fail(uint stage)
    {
        APTR.WriteUInt32(control, 20, stage);
        APTR.WriteUInt32(control, 24, unchecked((uint)DOS.IoErr()));
        Report(250);
        return (int)stage;
    }
    private static int FailAt(uint stage, uint detail)
    {
        APTR.WriteUInt32(control, 28, detail);
        return Fail(stage);
    }
    private static void Report(int value)
    {
        APTR.WriteUInt8(APTR.FromPointer(0xBFE301), 0, 255);
        APTR.WriteUInt8(APTR.FromPointer(0xBFE101), 0, (byte)value);
    }
}

