using Amiga;

public static class Probe
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

        Report(220);
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

        Report(226);
        if (DOS.DoPkt(port, (int)DosPacketAction.Flush, 0, 0, 0, 0, 0) == 0) return Fail(23);
        Report(227);
        Report(228);
        if (DOS.Inhibit("CPX0:", 1) == 0 || DOS.Inhibit("CPX0:", 0) == 0) return Fail(24);
        Report(229);
        port = DOS.DeviceProc("CPX0:");
        if (port.IsNull) return Fail(25);

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
        Report(210);
        return 0;
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

