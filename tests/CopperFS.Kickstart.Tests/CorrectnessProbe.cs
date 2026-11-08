using Amiga;

namespace CopperFS.Kickstart.Tests;

// Identical DOS workload for original and Copper handlers. Guest code is compiled
// from C#; no test doubles, native patching or host filesystem dispatch is used.
public static class CorrectnessProbe
{
    public static int WriteOfs() => Run(0, true);
    public static int ReadOfs() => Run(0, false);
    public static int WriteFfs() => Run(1, true);
    public static int ReadFfs() => Run(1, false);
    public static int WriteOfsDd() => Run(10, true);
    public static int ReadOfsDd() => Run(10, false);
    public static int WriteFfsDd() => Run(11, true);
    public static int ReadFfsDd() => Run(11, false);
    public static int WriteOfsHd() => Run(20, true);
    public static int ReadOfsHd() => Run(20, false);
    public static int WriteFfsHd() => Run(21, true);
    public static int ReadFfsHd() => Run(21, false);
    public static int WriteSfs() => Run(2, true);
    public static int ReadSfs() => Run(2, false);
    public static int WriteSfs2() => Run(3, true);
    public static int ReadSfs2() => Run(3, false);
    public static int RecoverSfs2() => Run(3, false, mountOnly: true);

    private static int Run(uint kind, bool write, bool mountOnly = false)
    {
        var library = global::Amiga.Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) { Report(250); return 10; }
        DOS.DOSLibraryBase = library.Value;
        var control = APTR.FromPointer(global::Amiga.Exec.AllocMem(1024,
            global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear));
        var data = APTR.FromPointer(global::Amiga.Exec.AllocMem(100000,
            global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Fast));
        if (control.IsNull || data.IsNull) { Report(250); return 11; }
        Report(200); Report((int)(control.Raw >> 24)); Report((int)(control.Raw >> 16));
        Report((int)(control.Raw >> 8)); Report((int)control.Raw);
        var result = Mount(control, kind);
        if (result == 0 && write) result = Mutate(control, data, kind == 2, kind == 3);
        if (result == 0 && !mountOnly) result = Verify(control, data);
        if (result == 0 && kind == 2) result = VerifyReplaced(control, data);
        if (result != 0) return Failed(control, (uint)result);
        if (DOS.Inhibit("SEQ0:", 1) == 0) return Failed(control, 90);
        Report(210); return 0;
    }

    private static byte Initial(int i) => (byte)(i * 17 + (i >> 9) * 31 + 3);
    private static byte Expected(int i)
    {
        // Overwrites overlap a sector boundary, then a pointer-table boundary.
        if (i >= 35000 && i < 35777) return (byte)((i - 35000) * 23 + 9);
        if (i >= 487 && i < 1264) return (byte)((i - 487) * 23 + 9);
        return Initial(i);
    }

    private static bool InputHasSize(CString path, int size)
    {
        var file = DOS.Open(path, DOS.FileMode.OldFile);
        if (!file.HasValue) return false;
        var matches = DOS.Seek(file.Value, 0, 1) == 0 && DOS.Seek(file.Value, 0, 0) == size;
        return DOS.Close(file.Value) != 0 && matches;
    }

    private static bool InputIsMissing(CString path)
    {
        var file = DOS.Open(path, DOS.FileMode.OldFile);
        if (!file.HasValue) return DOS.IoErr() == DOS.Error.ObjectNotFound;
        DOS.Close(file.Value);
        return false;
    }

    private static int Mutate(APTR control, APTR data, bool classic, bool sfs2)
    {
        var dir = DOS.CreateDir("SEQ0:AB");
        if (!dir.HasValue) return 30;
        DOS.UnLock(dir.Value);
        dir = DOS.CreateDir("SEQ0:AB/Sub");
        if (!dir.HasValue) return 31;
        DOS.UnLock(dir.Value);
        for (var i = 0; i < 90130; i++) APTR.WriteUInt8(data, i, Initial(i));
        var donor = DOS.Open("SEQ0:AB/Donor", DOS.FileMode.NewFile);
        if (!donor.HasValue) return 32;
        if (DOS.Write(donor.Value, data, 32768) != 32768 || DOS.Close(donor.Value) == 0) return 33;
        var file = DOS.Open("SEQ0:AB/Main", DOS.FileMode.NewFile);
        if (!file.HasValue) return 34;
        var position = 0;
        for (var step = 0; step < 8; step++)
        {
            var count = step == 0 ? 1 : step == 1 ? 487 : step == 2 ? 2 : step == 3 ? 511 :
                step == 4 ? 512 : step == 5 ? 513 : step == 6 ? 32768 : 81937 - position;
            if (DOS.Write(file.Value, APTR.FromPointer(data.Raw + (uint)position), count) != count) return 35;
            position += count;
        }
        if (DOS.Close(file.Value) == 0) return 36;
        if (sfs2 && !InputHasSize("SEQ0:AB/Donor", 32768)) return 91;
        if (DOS.DeleteFile("SEQ0:AB/Donor") == 0) return 36;
        if (sfs2 && !InputIsMissing("SEQ0:AB/Donor")) return 92;
        file = DOS.Open("SEQ0:AB/Main", DOS.FileMode.ReadWrite);
        if (!file.HasValue) return 37;
        if (DOS.Seek(file.Value, 0, 1) != 0 || DOS.Write(file.Value,
            APTR.FromPointer(data.Raw + 81937), 8193) != 8193) return 38;
        for (var pass = 0; pass < 2; pass++)
        {
            var at = pass == 0 ? 487 : 35000;
            for (var i = 0; i < 777; i++) APTR.WriteUInt8(data, i, (byte)(i * 23 + 9));
            if (DOS.Seek(file.Value, at, -1) < 0 || DOS.Write(file.Value, data, 777) != 777) return 39;
        }
        if (DOS.Close(file.Value) == 0) return 40;
        if (sfs2 && !InputHasSize("SEQ0:AB/Main", 90130)) return 93;
        if (DOS.Rename("SEQ0:AB/Main", "SEQ0:AB/Renamed") == 0) return 40;
        if (sfs2 && (!InputIsMissing("SEQ0:AB/Main") || !InputHasSize("SEQ0:AB/Renamed", 90130))) return 94;
        file = DOS.Open("SEQ0:AB/Truncated", DOS.FileMode.NewFile);
        if (!file.HasValue || DOS.Write(file.Value, data, 4097) != 4097 || DOS.Close(file.Value) == 0) return 41;
        if (sfs2 && !InputHasSize("SEQ0:AB/Truncated", 4097)) return 95;
        if (classic)
        {
            if (DOS.SetComment("SEQ0:AB/Truncated", "old truncation") == 0 ||
                DOS.SetProtection("SEQ0:AB/Truncated", 32) == 0) return 46;
            var oldDate = APTR.FromPointer(control.Raw + 800);
            APTR.WriteUInt32(oldDate, 0, 2345); APTR.WriteUInt32(oldDate, 4, 67); APTR.WriteUInt32(oldDate, 8, 100);
            if (DOS.SetFileDate("SEQ0:AB/Truncated", oldDate) == 0) return 47;
        }
        file = DOS.Open("SEQ0:AB/Truncated", DOS.FileMode.NewFile);
        if (!file.HasValue || DOS.Write(file.Value, data, 7) != 7 || DOS.Close(file.Value) == 0) return 42;
        if (sfs2 && !InputHasSize("SEQ0:AB/Truncated", 7)) return 96;
        if (classic)
        {
            file = DOS.Open("SEQ0:AB/Truncated", DOS.FileMode.NewFile);
            if (!file.HasValue || DOS.Write(file.Value, data, 7) != 7 || DOS.Close(file.Value) == 0) return 48;
        }
        file = DOS.Open("SEQ0:AB/Empty", DOS.FileMode.NewFile);
        if (!file.HasValue || DOS.Close(file.Value) == 0) return 43;
        if (classic)
        {
            file = DOS.Open("SEQ0:AB/Empty", DOS.FileMode.NewFile);
            if (!file.HasValue || DOS.Close(file.Value) == 0) return 49;
            for (var pass = 0; pass < 2; pass++)
            {
                var path = pass == 0 ? "SEQ0:AB/Sub/DeleteProtected" : "SEQ0:AB/Sub/WriteProtected";
                file = DOS.Open(path, DOS.FileMode.NewFile);
                if (!file.HasValue || DOS.Write(file.Value, data, 7) != 7 || DOS.Close(file.Value) == 0) return 80;
                if (DOS.SetProtection(path, pass == 0 ? 1 : 4) == 0) return 81;
                file = DOS.Open(path, DOS.FileMode.NewFile);
                if (pass == 0)
                { if (file.HasValue || DOS.IoErr() != DOS.Error.DeleteProtected) return 82; }
                else if (!file.HasValue || DOS.Close(file.Value) == 0) return 84;
                if (DOS.SetProtection(path, 0) == 0) return 83;
            }
        }
        if (DOS.SetComment("SEQ0:AB/Renamed", "AB metadata") == 0 ||
            DOS.SetProtection("SEQ0:AB/Renamed", 32) == 0) return 44;
        var date = APTR.FromPointer(control.Raw + 800);
        APTR.WriteUInt32(date, 0, 1234); APTR.WriteUInt32(date, 4, 56); APTR.WriteUInt32(date, 8, 100);
        if (DOS.SetFileDate("SEQ0:AB/Renamed", date) == 0) return 45;
        return 0;
    }

    private static int VerifyReplaced(APTR control, APTR data)
    {
        var file = DOS.Open("SEQ0:.recycled/Truncated", DOS.FileMode.OldFile);
        if (!file.HasValue || DOS.Read(file.Value, data, 5000) != 4097 || DOS.Close(file.Value) == 0) return 71;
        for (var i = 0; i < 4097; i++)
            if (APTR.ReadUInt8(data, i) != (i < 777 ? (byte)(i * 23 + 9) : Initial(i))) return 72;
        file = DOS.Open("SEQ0:.recycled/Truncated$AAA", DOS.FileMode.OldFile);
        if (!file.HasValue || DOS.Read(file.Value, data, 20) != 7 || DOS.Close(file.Value) == 0) return 73;
        for (var i = 0; i < 7; i++) if (APTR.ReadUInt8(data, i) != (byte)(i * 23 + 9)) return 74;
        var entry = DOS.Lock("SEQ0:.recycled/Truncated", DOS.LockMode.Shared);
        var fib = APTR.FromPointer(control.Raw + 400);
        if (!entry.HasValue || DOS.Examine(entry.Value, fib) == 0) return 75;
        DOS.UnLock(entry.Value);
        if (APTR.ReadUInt32(fib, FileInfoBlock.ProtectionOffset) != 32 ||
            APTR.ReadUInt32(fib, FileInfoBlock.DateDaysOffset) != 2345 ||
            APTR.ReadUInt32(fib, FileInfoBlock.DateMinuteOffset) != 67 ||
            APTR.ReadUInt32(fib, FileInfoBlock.DateTickOffset) != 100 ||
            !Text(fib, FileInfoBlock.CommentOffset, "old truncation")) return 76;
        entry = DOS.Lock("SEQ0:AB/Truncated", DOS.LockMode.Shared);
        if (!entry.HasValue || DOS.Examine(entry.Value, fib) == 0) return 77;
        DOS.UnLock(entry.Value);
        if (APTR.ReadUInt32(fib, FileInfoBlock.ProtectionOffset) != 0 || !Text(fib, FileInfoBlock.CommentOffset, "")) return 78;
        file = DOS.Open("SEQ0:.recycled/Empty", DOS.FileMode.OldFile);
        if (!file.HasValue || DOS.Read(file.Value, data, 1) != 0 || DOS.Close(file.Value) == 0) return 79;
        return 0;
    }

    private static int Verify(APTR control, APTR data)
    {
        var file = DOS.Open("SEQ0:AB/Renamed", DOS.FileMode.OldFile);
        if (!file.HasValue) return 50;
        var position = 0;
        for (var step = 0; position < 90130; step++)
        {
            var count = step % 4 == 0 ? 487 : step % 4 == 1 ? 513 : step % 4 == 2 ? 8193 : 32768;
            if (count > 90130 - position) count = 90130 - position;
            if (DOS.Read(file.Value, data, count) != count) return 51;
            for (var i = 0; i < count; i++) if (APTR.ReadUInt8(data, i) != Expected(position + i)) return 52;
            position += count;
        }
        if (DOS.Read(file.Value, data, 1) != 0 || DOS.Seek(file.Value, -513, 1) != 90130 ||
            DOS.Read(file.Value, data, 1024) != 513) return 53;
        for (var i = 0; i < 513; i++) if (APTR.ReadUInt8(data, i) != Expected(89617 + i)) return 54;
        if (DOS.Seek(file.Value, 487, -1) != 90130 || DOS.Read(file.Value, data, 777) != 777) return 55;
        for (var i = 0; i < 777; i++) if (APTR.ReadUInt8(data, i) != Expected(487 + i)) return 56;
        if (DOS.Close(file.Value) == 0) return 57;
        var entry = DOS.Lock("SEQ0:AB/Renamed", DOS.LockMode.Shared);
        if (!entry.HasValue) return 58;
        var fib = APTR.FromPointer(control.Raw + 400);
        var examined = DOS.Examine(entry.Value, fib); DOS.UnLock(entry.Value);
        if (examined == 0 || APTR.ReadUInt32(fib, FileInfoBlock.SizeOffset) != 90130 ||
            APTR.ReadUInt32(fib, FileInfoBlock.ProtectionOffset) != 32 ||
            APTR.ReadUInt32(fib, FileInfoBlock.DateDaysOffset) != 1234 ||
            APTR.ReadUInt32(fib, FileInfoBlock.DateMinuteOffset) != 56 ||
            APTR.ReadUInt32(fib, FileInfoBlock.DateTickOffset) != 100) return 59;
        if (!Text(fib, FileInfoBlock.CommentOffset, "AB metadata")) return 60;
        file = DOS.Open("SEQ0:AB/Truncated", DOS.FileMode.OldFile);
        if (!file.HasValue || DOS.Read(file.Value, data, 20) != 7 || DOS.Close(file.Value) == 0) return 61;
        for (var i = 0; i < 7; i++) if (APTR.ReadUInt8(data, i) != (byte)(i * 23 + 9)) return 62;
        file = DOS.Open("SEQ0:AB/Empty", DOS.FileMode.OldFile);
        if (!file.HasValue || DOS.Read(file.Value, data, 1) != 0 || DOS.Close(file.Value) == 0) return 63;
        if (DOS.Open("SEQ0:AB/Missing", DOS.FileMode.OldFile).HasValue || (int)DOS.IoErr() != 205) return 64;
        if (DOS.DeleteFile("SEQ0:AB") != 0 || (int)DOS.IoErr() != 216) return 65;
        if (DOS.Lock("SEQ0:AB/Main", DOS.LockMode.Shared).HasValue || (int)DOS.IoErr() != 205) return 66;
        if (DOS.Lock("SEQ0:AB/Donor", DOS.LockMode.Shared).HasValue || (int)DOS.IoErr() != 205) return 67;
        entry = DOS.Lock("SEQ0:AB", DOS.LockMode.Shared);
        if (!entry.HasValue || DOS.Examine(entry.Value, fib) == 0) return 68;
        var seen = 0;
        while (DOS.ExNext(entry.Value, fib) != 0)
        {
            var name = FileInfoBlock.FileNameOffset;
            var bit = Text(fib, name, "Renamed") ? 1 : Text(fib, name, "Truncated") ? 2 :
                Text(fib, name, "Empty") ? 4 : Text(fib, name, "Sub") ? 8 : 0;
            if (bit == 0 || (seen & bit) != 0) return 69;
            seen |= bit;
        }
        var error = DOS.IoErr(); DOS.UnLock(entry.Value);
        if (seen != 15 || (int)error != 232) return 70;
        return 0;
    }

    private static bool Text(APTR source, int offset, string value)
    {
        for (var i = 0; i < value.Length; i++) if (APTR.ReadUInt8(source, offset + i) != value[i]) return false;
        return APTR.ReadUInt8(source, offset + value.Length) == 0;
    }

    internal static int Mount(APTR control, uint kind, bool floppy = false, bool unknown = false)
    {
        var format = kind % 10;
        var segment = DOS.LoadSeg("HANDLER");
        if (!segment.HasValue) return 12;
        var node = DOS.MakeDosEntry(CString.FromLiteral("SEQ0"), (int)DosListType.Device);
        if (node.IsNull) return 13;
        var startup = APTR.FromPointer(control.Raw + 64);
        var name = APTR.FromPointer(control.Raw + 96);
        var environment = APTR.FromPointer(control.Raw + 128);
        var device = floppy ? CString.FromLiteral("trackdisk.device") : CString.FromLiteral("copperhdf.device");
        if (unknown)
        {
            // Give the fixed disk an unrecognized driver name in this guest.
            var exec = APTR.ReadUInt32(APTR.FromPointer(4), 0);
            var driver = global::Amiga.Exec.FindName(APTR.FromPointer(exec + ExecLayout.ExecBase.DeviceList), device);
            if (driver.IsNull) return 15;
            device = CString.FromLiteral("opaquehdf.device");
            APTR.WriteUInt32(driver, ExecLayout.Node.Name, CString.ToUInt32(device));
        }
        APTR.WriteUInt8(name, 0, 16);
        for (var i = 0; i < 16; i++) APTR.WriteUInt8(name, i + 1, APTR.ReadUInt8(APTR.FromPointer(CString.ToUInt32(device)), i));
        APTR.WriteUInt32(startup, DosLayout.FileSysStartupMsg.Unit, floppy ? 1u : 0u);
        APTR.WriteUInt32(startup, DosLayout.FileSysStartupMsg.Device, name.Raw >> 2);
        APTR.WriteUInt32(startup, DosLayout.FileSysStartupMsg.Environment, environment.Raw >> 2);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.TableSize, 19);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.SizeBlock, 128);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.Surfaces, 2);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.SectorsPerBlock, 1);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.BlocksPerTrack, kind >= 20 ? 22u : kind >= 10 ? 11u : 128u);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.Reserved, format == 3 ? 1u : 2u);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.HighCylinder, kind >= 10 ? 79u : 127u);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.NumberOfBuffers, 32);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.BufferMemoryType, 1);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.MaximumTransfer, 65536);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.Mask, 0xFFFFFFFE);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.BootPriority, 0xFFFFFF7F);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.DosType, format == 0 ? 0x444F5300u : format == 1 ? 0x444F5301u : format == 2 ? 0x53465300u : 0x53465302u);
        APTR.WriteUInt32(node, DosLayout.DeviceNode.StackSize, 32768);
        APTR.WriteUInt32(node, DosLayout.DeviceNode.Priority, 10);
        APTR.WriteUInt32(node, DosLayout.DeviceNode.Startup, startup.Raw >> 2);
        APTR.WriteUInt32(node, DosLayout.DeviceNode.SegmentList, segment.Value.Raw);
        APTR.WriteUInt32(node, DosLayout.DeviceNode.GlobalVector, uint.MaxValue);
        return DOS.AddDosEntry(node) == 0 ? 14 : 0;
    }


    private static int Failed(APTR control, uint stage)
    {
        APTR.WriteUInt32(control, 20, stage); APTR.WriteUInt32(control, 24, (uint)DOS.IoErr());
        Report(250); return (int)stage;
    }
    internal static void Report(int value)
    {
        APTR.WriteUInt8(APTR.FromPointer(0xBFE301), 0, 255);
        APTR.WriteUInt8(APTR.FromPointer(0xBFE101), 0, (byte)value);
    }
}
