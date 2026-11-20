// Correctness-only derivative of A86 MixedProbe; see prepare_nova_gate.py.
using Amiga;

public static class NovaGateProbe
{
    private static APTR control;
    private static APTR data;
    private static APTR port;

    private static bool namespaceProbe;
    public static int NamespaceEntry() { namespaceProbe=true; return Entry(); }

    public static int Entry()
    {
        APTR dos = Exec.OpenLibraryRaw(CString.FromLiteral("dos.library"), 37);
        if (dos.IsNull) { Report(250); return 1; }
        DOS.DOSLibraryBase = dos;
        APTR process = Exec.FindTask(CString.FromPointer(0));
        APTR.WriteUInt32(process, 184, uint.MaxValue);
        control = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        data = APTR.FromPointer(Exec.AllocMem(8192, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
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
        APTR.WriteUInt32(node, DosLayout.DeviceNode.StackSize, 4096);
        APTR.WriteUInt32(node, DosLayout.DeviceNode.Priority, 10);
        APTR.WriteUInt32(node, DosLayout.DeviceNode.Startup, startup.Raw >> 2);
        APTR.WriteUInt32(node, DosLayout.DeviceNode.SegmentList, segment.Value.Raw);
        APTR.WriteUInt32(node, DosLayout.DeviceNode.GlobalVector, uint.MaxValue);
        if (DOS.AddDosEntry(node) == 0) return Fail(5);
        Report(224);
        port = DOS.DeviceProc("CPX0:");
        if (port.IsNull) return Fail(6);
        Report(225);

        Report(220);
        if(namespaceProbe && !CheckNamespace()) return Fail(60);
        var dir = DOS.CreateDir("CPX0:Bench");
        if (!dir.HasValue) return Fail(10);
        DOS.UnLock(dir.Value);
        var file = DOS.Open("CPX0:Bench/Data.bin", DOS.FileMode.NewFile);
        if (!file.HasValue) return Fail(11);
        for (int i = 0; i < 8192; i++) APTR.WriteUInt8(data, i, Initial(i));
        if (DOS.Write(file.Value, data, 8192) != 8192) return Fail(12);
        if (DOS.Seek(file.Value, 1023, -1) < 0) return Fail(13);
        for (int i = 0; i < 1024; i++) APTR.WriteUInt8(data, i, (byte)(i * 23 + 9));
        if (DOS.Write(file.Value, data, 1024) != 1024) return Fail(14);
        if (DOS.Seek(file.Value, 4093, -1) < 0) return Fail(15);
        for (int i = 0; i < 769; i++) APTR.WriteUInt8(data, i, (byte)(i * 13 + 5));
        if (DOS.Write(file.Value, data, 769) != 769) return Fail(16);
        if (DOS.SetFileSize(file.Value, 6003, -1) != 6003) return Fail(17);
        if (DOS.SetFileSize(file.Value, 6003, -1) != 6003) return Fail(18);
        if (DOS.Close(file.Value) == 0) return Fail(19);
        if (DOS.Rename("CPX0:Bench/Data.bin", "CPX0:Bench/Renamed.bin") == 0) return Fail(20);
        file = DOS.Open("CPX0:Bench/Temp.bin", DOS.FileMode.NewFile);
        if (!file.HasValue) return Fail(21);
        for (int i = 0; i < 1024; i++) APTR.WriteUInt8(data, i, 0x5A);
        if (DOS.Write(file.Value, data, 1024) != 1024 || DOS.Close(file.Value) == 0) return Fail(22);
        Report(221);

#if PACKED_TREE_GATE
        if(DOS.SetComment("CPX0:Bench/Renamed.bin","CNP2..........................................................................!")==0) return Fail(40);
        var branch=DOS.CreateDir("CPX0:MetadataMetadataMetadataMetadataMetadataMetadataMetadataMetadataMetadataMetadataMetadataMetadata"); if(!branch.HasValue) return Fail(41); DOS.UnLock(branch.Value);
        branch=DOS.CreateDir("CPX0:MetadataMetadataMetadataMetadataMetadataMetadataMetadataMetadataMetadataMetadataMetadataMetadata/DirectoryDirectoryDirectoryDirectoryDirectoryDirectoryDirectoryDirectoryDirectoryDirectory"); if(!branch.HasValue) return Fail(42); DOS.UnLock(branch.Value);
        var fragment=DOS.Open("CPX0:Bench/Fragment",DOS.FileMode.NewFile); var other=DOS.Open("CPX0:Bench/Other",DOS.FileMode.NewFile);
        if(!fragment.HasValue||!other.HasValue) return Fail(43);
        for(int part=0;part<8;part++) { APTR.WriteUInt8(data,0,(byte)(part+1)); if(DOS.Write(fragment.Value,data,512)!=512||DOS.Write(other.Value,data,512)!=512) return Fail(44); }
        if(DOS.Close(fragment.Value)==0||DOS.Close(other.Value)==0) return Fail(45);
#endif
        Report(226);
        if (DOS.DoPkt(port, (int)DosPacketAction.Flush, 0, 0, 0, 0, 0) == 0) return Fail(23);
        Report(227);
        Report(228);
        if (DOS.Inhibit("CPX0:", 1) == 0 || DOS.Inhibit("CPX0:", 0) == 0) return Fail(24);
        Report(229);
        port = DOS.DeviceProc("CPX0:");
        if (port.IsNull) return Fail(25);

#if PACKED_TREE_GATE
        var examined=DOS.Lock("CPX0:Bench/Renamed.bin",(DOS.LockMode)(-2)); if(!examined.HasValue) return Fail(46);
        if(DOS.DoPkt(port,23,(int)examined.Value.Raw,(int)(data.Raw>>2),0,0,0)==0||APTR.ReadUInt8(data,144)!=79||APTR.ReadUInt8(data,223)!=33) return Fail(47);
        DOS.UnLock(examined.Value);
        fragment=DOS.Open("CPX0:Bench/Fragment",DOS.FileMode.OldFile); if(!fragment.HasValue||DOS.Read(fragment.Value,data,4096)!=4096) return Fail(48);
        for(int part=0;part<8;part++) if(APTR.ReadUInt8(data,part*512)!=part+1) return Fail(49);
        if(DOS.Close(fragment.Value)==0) return Fail(50);
#endif
        Report(222);
        file = DOS.Open("CPX0:Bench/Renamed.bin", DOS.FileMode.OldFile);
        if (!file.HasValue) return Fail(26);
        for (int position = 0; position < 6003; position += 8192)
        {
            int count = position + 8192 > 6003 ? 6003 - position : 8192;
            if (DOS.Read(file.Value, data, count) != count) return Fail(27);
            for (int i = 0; i < count; i++)
                if (APTR.ReadUInt8(data, i) != Expected(position + i)) return FailAt(28, (uint)(position + i));
        }
        if (DOS.Close(file.Value) == 0) return Fail(29);
        Report(223);
        file = DOS.Open("CPX0:Bench/Renamed.bin", DOS.FileMode.ReadWrite);
        if (!file.HasValue) return Fail(30);
        if (DOS.Seek(file.Value, 0, 1) != 0) return Fail(31);
        for (int i = 0; i < 1024; i++) APTR.WriteUInt8(data, i, 0x5B);
        if (DOS.Write(file.Value, data, 1024) != 1024 || DOS.Close(file.Value) == 0) return Fail(32);
        Report(232);
        if (DOS.DoPkt(port, 5, 0, 0, 0, 0, 0) == 0) return Fail(33);
        Report(233);
        Report(210);
        return 0;
    }

    private static bool CheckNamespace()
    {
        var sub=DOS.CreateDir("CPX0:Sub");
        if(!sub.HasValue) return false;
        DOS.UnLock(sub.Value);
        var f=DOS.Open("CPX0:T",DOS.FileMode.NewFile);
        if(!f.HasValue||DOS.Write(f.Value,data,512)!=512||DOS.Close(f.Value)==0) return false;
        if(DOS.Rename("CPX0:T","CPX0:Sub/U")==0) return false;
        sub=DOS.Lock("CPX0:Sub",(DOS.LockMode)(-2));
        if(!sub.HasValue||DOS.Examine(sub.Value,data)==0||DOS.ExNext(sub.Value,data)==0||
            APTR.ReadUInt32(data,124)!=512||APTR.ReadUInt8(data,8)!=85) return false;
        DOS.UnLock(sub.Value);
        if(DOS.DeleteFile("CPX0:Sub/U")==0||DOS.DeleteFile("CPX0:Sub")==0) return false;
        return true;
    }

    private static byte Initial(int index) => (byte)(index * 17 + (index >> 9) * 31 + 3);
    private static byte Expected(int index)
    {
        if (index >= 6003) return 0;
        if (index >= 4093 && index < 4862) return (byte)((index - 4093) * 13 + 5);
        if (index >= 1023 && index < 2047) return (byte)((index - 1023) * 23 + 9);
        return Initial(index);
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

