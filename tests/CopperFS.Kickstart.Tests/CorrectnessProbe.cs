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
    public static int ForeignFfs() => ForeignStartup(1);
    public static int ForeignSfs() => ForeignStartup(2);
    public static int ForeignSfs2() => ForeignStartup(3);
    public static int FfsResize64Status() => ForeignStartup(1, true);

    private static int ForeignStartup(uint kind, bool resize64 = false)
    {
        var library = global::Amiga.Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) { Report(250); return 10; }
        DOS.DOSLibraryBase = library.Value;
        var process = global::Amiga.Exec.FindTask(CString.FromPointer(0));
        APTR.WriteUInt32(process, 184, uint.MaxValue);
        var control = APTR.FromPointer(global::Amiga.Exec.AllocMem(1024,
            global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear));
        if (control.IsNull) { Report(250); return 11; }
        Report(200); Report((int)(control.Raw >> 24)); Report((int)(control.Raw >> 16));
        Report((int)(control.Raw >> 8)); Report((int)control.Raw);
        var setup = Mount(control, kind);
        if (setup != 0) return Failed(control, (uint)setup);
        var port = DOS.DeviceProc("SEQ0:");
        var error = DOS.IoErr();
        var result = port.IsNull ? 0u : 1u;
        if (resize64 && port.IsNotNull)
        {
            result = unchecked((uint)DOS.DoPkt(port, 26401, 0, 0, 0, 0, 0));
            error = DOS.IoErr();
        }
        APTR.WriteUInt32(control, 20, result);
        APTR.WriteUInt32(control, 24, unchecked((uint)error));
        Report(210);
        return 0;
    }

    public static int AutoOfs() => Run(0, true, true);
    public static int AutoFfs() => Run(1, true, true);
    public static int AutoSfs() => Run(2, true, true);
    public static int AutoSfs2() => Run(3, true, true);
    public static int AutoLargeOfs() => Run(0, true, true, trimScenario: 1);
    public static int AutoLargeFfs() => Run(1, true, true, trimScenario: 1);
    public static int AutoLargeSfs() => Run(2, true, true, trimScenario: 1);
    public static int AutoLargeSfs2() => Run(3, true, true, trimScenario: 1);
    public static int AutoFragmentedOfs() => Run(0, true, true, trimScenario: 2, chip: true);
    public static int AutoFragmentedFfs() => Run(1, true, true, trimScenario: 2, chip: true);
    public static int AutoFragmentedSfs() => Run(2, true, true, trimScenario: 2, chip: true);
    public static int AutoFragmentedSfs2() => Run(3, true, true, trimScenario: 2, chip: true);
    public static int AutoWarmOfs() => Run(0, true, true, trimScenario: 3);
    public static int AutoWarmFfs() => Run(1, true, true, trimScenario: 3);
    public static int AutoWarmSfs() => Run(2, true, true, trimScenario: 3);
    public static int AutoWarmSfs2() => Run(3, true, true, trimScenario: 3);

    public static int AutoLifecycleOfs() => Run(0, true, true, true);
    public static int AutoLifecycleFfs() => Run(1, true, true, true);
    public static int AutoLifecycleSfs() => Run(2, true, true, true);
    public static int AutoLifecycleSfs2() => Run(3, true, true, true);

    public static int BenchmarkOfs() => Run(0, true, benchmark: true);
    public static int BenchmarkFfs() => Run(1, true, benchmark: true);
    public static int BenchmarkSfs() => Run(2, true, benchmark: true);
    public static int BenchmarkSfs2() => Run(3, true, benchmark: true);
    public static int BenchmarkChipOfs() => Run(0, true, benchmark: true, chip: true);
    public static int BenchmarkChipFfs() => Run(1, true, benchmark: true, chip: true);
    public static int BenchmarkChipSfs() => Run(2, true, benchmark: true, chip: true);
    public static int BenchmarkChipSfs2() => Run(3, true, benchmark: true, chip: true);
    public static int OwnedBenchmarkFfs() => Run(1, true, benchmark: true, splitMount: true, repeatRead: true);
    public static int OwnedBenchmarkOfs() => Run(0, true, benchmark: true, splitMount: true, repeatRead: true);
    public static int OwnedBenchmarkChipFfs() => Run(1, true, benchmark: true, chip: true, splitMount: true, repeatRead: true);
    public static int OwnedBenchmarkChipOfs() => Run(0, true, benchmark: true, chip: true, splitMount: true, repeatRead: true);
    public static int OwnedBenchmarkSfs2() => Run(3, true, benchmark: true, splitMount: true);
    public static int OwnedBenchmarkSfs() => Run(2, true, benchmark: true, splitMount: true);
    public static int OwnedBenchmarkChipSfs() => Run(2, true, benchmark: true, chip: true, splitMount: true);
    public static int ReadPlacementSfs() => Run(2, false, splitMount: true, readPlacement: true);
    public static int ReadPlacementChipSfs() => Run(2, false, chip: true, splitMount: true, readPlacement: true);
    public static int OwnedBenchmarkChipSfs2() => Run(3, true, benchmark: true, chip: true, splitMount: true);
    public static int TraceCommitSfs2() => Run(3, true, benchmark: true, splitMount: true, commitTrace: true);
    public static int TraceCommitSfs() => Run(2, true, benchmark: true, splitMount: true, commitTrace: true);
    public static int TraceCommitFfs() => Run(1, true, benchmark: true, splitMount: true, commitTrace: true);
    public static int TraceCommitOfs() => Run(0, true, benchmark: true, splitMount: true, commitTrace: true);

    public static int CutOfs() => Run(0, true, true, cutSchedule: true);
    public static int CutFfs() => Run(1, true, true, cutSchedule: true);
    public static int CutSfs() => Run(2, true, true, cutSchedule: true);
    public static int CutSfs2() => Run(3, true, true, cutSchedule: true);

    private static int Run(uint kind, bool write, bool automatic = false, bool lifecycle = false, bool benchmark = false, bool cutSchedule = false, bool mountOnly = false,
        uint trimScenario = 0, bool chip = false, bool splitMount = false, bool commitTrace = false, bool readPlacement = false, bool repeatRead = false)
    {
        var library = global::Amiga.Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) { Report(250); return 10; }
        DOS.DOSLibraryBase = library.Value;
        var control = APTR.FromPointer(global::Amiga.Exec.AllocMem(1024,
            global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear));
        var data = APTR.FromPointer(global::Amiga.Exec.AllocMem(100000,
            global::Amiga.Exec.MemoryFlags.Public | (chip ? global::Amiga.Exec.MemoryFlags.Chip : global::Amiga.Exec.MemoryFlags.Fast)));
        if (control.IsNull || data.IsNull) { Report(250); return 11; }
        Report(200); Report((int)(control.Raw >> 24)); Report((int)(control.Raw >> 16));
        Report((int)(control.Raw >> 8)); Report((int)control.Raw);
        var result = Mount(control, kind);
        if (splitMount && result == 0)
        {
            APTR.WriteUInt32(control, 900, global::Amiga.Exec.AvailMem(0));
            Report(218);
            if (DOS.DeviceProc("SEQ0:").IsNull) return Failed(control, 100);
            var add = APTR.ReadUInt32(control, 944);
            if (add != 0 && DOS.DoPkt(DOS.DeviceProc("SEQ0:"), (int)DosPacketAction.MoreCache,
                (int)add, 0, 0, 0, 0) == 0) return Failed(control, 102);
            Report(219);
            APTR.WriteUInt32(control, 904, global::Amiga.Exec.AvailMem(0));
        }
        if (result == 0 && APTR.ReadUInt32(control, 948) == 1 && !WarmDiscardDevice(data))
            return Failed(control, 106);
        if (readPlacement)
        {
            Report(220);
            if (result != 0 || !VerifyExistingPayload(data)) return Failed(control, 101);
            Report(221);
            Report(222);
            if (!VerifyExistingPayload(data)) return Failed(control, 101);
            Report(223);
            APTR.WriteUInt32(control, 912, global::Amiga.Exec.AvailMem(0));
            if (DOS.Inhibit("SEQ0:", 1) == 0) return Failed(control, 90);
            Report(210); return 0;
        }
        Report(220);
        if (result == 0 && write) result = Mutate(control, data, kind == 2, kind == 3, commitTrace);
        if (result == 0 && automatic)
        {
            // Recycling does not unallocate data. Establish real freed blocks
            // for automatic qualification, without deleting the recycled files
            // whose contents and metadata the normal workload verifies.
            if (DOS.Inhibit("SEQ0:", 1) == 0 || DOS.Inhibit("SEQ0:", 0) == 0) return Failed(control, 95);
            Report(225); // A fresh mount also resets automatic-TRIM suspension.
            if (trimScenario is 1 or 2)
            { if (!CreateTrimScenario(control, data, kind == 2, trimScenario)) return Failed(control, 96); }
            else if (!CreateTrimSample(data, kind == 2)) return Failed(control, 96);
        }
        if (result == 0 && benchmark)
        {
            if (commitTrace) Report(174);
            // A filesystem checkpoint, not a DOS stream Flush(file). Use the
            // standard packet so outstanding locks do not prevent the barrier.
            var port = DOS.DeviceProc("SEQ0:");
            if (port.IsNull || DOS.DoPkt(port, (int)DosPacketAction.Flush, 0, 0, 0, 0, 0) == 0)
                return Failed(control, 94);
            if (commitTrace) Report(175);
        }
        Report(221);
        if (splitMount) APTR.WriteUInt32(control, 908, global::Amiga.Exec.AvailMem(0));
        if (result == 0 && automatic) DOS.Delay(cutSchedule ? 2500 : 650);
        if (result == 0 && trimScenario == 3)
        {
            Report(230);
            if (!CreateTrimSample(data, kind == 2)) return Failed(control, 96);
            Report(231); DOS.Delay(650);
        }
        if (result == 0 && lifecycle)
        {
            Report(226);
            if (DOS.Inhibit("SEQ0:", 1) == 0) return Failed(control, 91);
            DOS.Delay(300); Report(227);
            if (DOS.Inhibit("SEQ0:", 0) == 0) return Failed(control, 92);
            var cycle = DOS.Open("SEQ0:Cycle", DOS.FileMode.NewFile);
            if (!cycle.HasValue || DOS.Write(cycle.Value, data, 512) != 512 ||
                DOS.Close(cycle.Value) == 0 || DOS.DeleteFile("SEQ0:Cycle") == 0) return Failed(control, 93);
            if (kind == 2 && DOS.DeleteFile("SEQ0:.recycled/Cycle") == 0) return Failed(control, 98);
            Report(228); DOS.Delay(300); Report(229);
        }
        Report(222);
        if (result == 0 && trimScenario == 2 && !VerifyTrimKeepers(control, data)) return Failed(control, 99);
        if (result == 0 && !mountOnly) result = Verify(control, data);
        if (result == 0 && kind == 2) result = VerifyReplaced(control, data);
        Report(223);
        if (splitMount) APTR.WriteUInt32(control, 912, global::Amiga.Exec.AvailMem(0));
        if (result != 0) return Failed(control, (uint)result);
        if (repeatRead)
        {
            // Repeat reads of one mapped block on an open handle; exclude open,
            // initial fill and close, and check the returned payload each time.
            var file = DOS.Open("SEQ0:AB/Renamed", DOS.FileMode.OldFile);
            if (!file.HasValue || DOS.Read(file.Value, data, 256) != 256) return Failed(control, 103);
            Report(228);
            for (var pass = 0; pass < 32; pass++)
            {
                if (DOS.Seek(file.Value, 0, -1) != 256 || DOS.Read(file.Value, data, 256) != 256) return Failed(control, 103);
                for (var i = 0; i < 256; i++) if (APTR.ReadUInt8(data, i) != Expected(i)) return Failed(control, 104);
            }
            Report(229);
            if (DOS.Close(file.Value) == 0) return Failed(control, 105);
        }
        if (splitMount && APTR.ReadUInt32(control, 940) != 0)
        {
            Report(228);
            if (!VerifyExistingPayload(data)) return Failed(control, 101);
            Report(229);
        }
        if (DOS.Inhibit("SEQ0:", 1) == 0) return Failed(control, 90);
        Report(210); return 0;
    }

    private static bool VerifyExistingPayload(APTR data)
    {
        var file = DOS.Open("SEQ0:Payload", DOS.FileMode.OldFile);
        if (!file.HasValue) return false;
        for (var position = 0; position < 1048576; position += 16384)
        {
            if (DOS.Read(file.Value, data, 16384) != 16384) return false;
            for (var at = 0; at < 16384; at++)
            {
                var index = position + at;
                if (APTR.ReadUInt8(data, at) != (byte)(index * 17 + (index >> 9) * 31 + (index >> 17) * 7 + 3)) return false;
            }
        }
        return DOS.Close(file.Value) != 0;
    }

    internal static bool WarmDiscardDevice(APTR data)
    {
        var file = DOS.Open("SEQ0:WarmDiscard", DOS.FileMode.NewFile);
        if (!file.HasValue || DOS.Write(file.Value, data, 512) != 512 || DOS.Close(file.Value) == 0 ||
            DOS.DeleteFile("SEQ0:WarmDiscard") == 0) return false;
        DOS.Delay(650);
        return true;
    }

    private static bool CreateTrimSample(APTR data, bool recycled)
    {
        var file = DOS.Open("SEQ0:TrimSchedule", DOS.FileMode.NewFile);
        if (!file.HasValue || DOS.Write(file.Value, data, 32768) != 32768 || DOS.Close(file.Value) == 0) return false;
        Report(232);
        return DOS.DeleteFile("SEQ0:TrimSchedule") != 0 &&
            (!recycled || DOS.DeleteFile("SEQ0:.recycled/TrimSchedule") != 0);
    }

    private static CString TrimName(APTR control, uint index, bool keep, bool recycled)
    {
        var name = APTR.FromPointer(control.Raw + 600);
        var prefix = APTR.FromPointer(CString.ToUInt32(recycled ? CString.FromLiteral("SEQ0:.recycled/T") :
            keep ? CString.FromLiteral("SEQ0:K") : CString.FromLiteral("SEQ0:T")));
        var at = 0;
        while (APTR.ReadUInt8(prefix, at) != 0) { APTR.WriteUInt8(name, at, APTR.ReadUInt8(prefix, at)); at++; }
        APTR.WriteUInt8(name, at++, (byte)('0' + index / 100));
        APTR.WriteUInt8(name, at++, (byte)('0' + index / 10 % 10));
        APTR.WriteUInt8(name, at++, (byte)('0' + index % 10)); APTR.WriteUInt8(name, at, 0);
        return CString.FromPointer(name.Raw);
    }

    private static bool CreateTrimScenario(APTR control, APTR data, bool recycled, uint scenario)
    {
        for (var at = 0; at < 65536; at++) APTR.WriteUInt8(data, at, 0x3B);
        var files = scenario == 1 ? 1u : 16u;
        for (var i = 0u; i < files; i++)
        {
            var file = DOS.Open(TrimName(control, i, false, false), DOS.FileMode.NewFile);
            if (!file.HasValue) return false;
            if (scenario == 1)
            {
                // More than one 4096-block service budget at 512-byte blocks.
                for (var n = 0; n < 36; n++) if (DOS.Write(file.Value, data, 65536) != 65536) return false;
            }
            else if (DOS.Write(file.Value, data, 4096) != 4096) return false;
            if (DOS.Close(file.Value) == 0) return false;
            if (scenario != 1)
            {
                var keep = DOS.Open(TrimName(control, i, true, false), DOS.FileMode.NewFile);
                if (!keep.HasValue || DOS.Write(keep.Value, data, 4096) != 4096 || DOS.Close(keep.Value) == 0) return false;
            }
        }
        Report(232); // Foreground deletion starts; setup writes are separately priced.
        for (var i = 0u; i < files; i++)
            if (DOS.DeleteFile(TrimName(control, i, false, false)) == 0 ||
                recycled && DOS.DeleteFile(TrimName(control, i, false, true)) == 0) return false;
        return true;
    }

    private static bool VerifyTrimKeepers(APTR control, APTR data)
    {
        for (var i = 0u; i < 16; i++)
        {
            var file = DOS.Open(TrimName(control, i, true, false), DOS.FileMode.OldFile);
            if (!file.HasValue || DOS.Read(file.Value, data, 4096) != 4096 || DOS.Close(file.Value) == 0) return false;
            for (var at = 0; at < 4096; at++) if (APTR.ReadUInt8(data, at) != 0x3B) return false;
        }
        return true;
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

    internal static int Mutate(APTR control, APTR data, bool classic, bool sfs2, bool commitTrace = false)
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
        if (DOS.Write(donor.Value, data, 32768) != 32768) return 33;
        if (commitTrace) Report(160);
        if (DOS.Close(donor.Value) == 0) return 33;
        if (commitTrace) Report(161);
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
        if (commitTrace) Report(162);
        if (DOS.Close(file.Value) == 0) return 36;
        if (commitTrace) Report(163);
        if (sfs2 && !InputHasSize("SEQ0:AB/Donor", 32768)) return 91;
        if (DOS.DeleteFile("SEQ0:AB/Donor") == 0) return 36;
        if (commitTrace) Report(176);
        if (sfs2 && !InputIsMissing("SEQ0:AB/Donor")) return 92;
        file = DOS.Open("SEQ0:AB/Main", DOS.FileMode.ReadWrite);
        if (!file.HasValue) return 37;
        if (commitTrace) Report(177);
        if (DOS.Seek(file.Value, 0, 1) != 0 || DOS.Write(file.Value,
            APTR.FromPointer(data.Raw + 81937), 8193) != 8193) return 38;
        if (commitTrace) Report(178);
        for (var pass = 0; pass < 2; pass++)
        {
            var at = pass == 0 ? 487 : 35000;
            for (var i = 0; i < 777; i++) APTR.WriteUInt8(data, i, (byte)(i * 23 + 9));
            if (DOS.Seek(file.Value, at, -1) < 0 || DOS.Write(file.Value, data, 777) != 777) return 39;
        }
        if (commitTrace) Report(164);
        if (DOS.Close(file.Value) == 0) return 40;
        if (commitTrace) Report(165);
        if (sfs2 && !InputHasSize("SEQ0:AB/Main", 90130)) return 93;
        if (DOS.Rename("SEQ0:AB/Main", "SEQ0:AB/Renamed") == 0) return 40;
        if (sfs2 && (!InputIsMissing("SEQ0:AB/Main") || !InputHasSize("SEQ0:AB/Renamed", 90130))) return 94;
        file = DOS.Open("SEQ0:AB/Truncated", DOS.FileMode.NewFile);
        if (!file.HasValue || DOS.Write(file.Value, data, 4097) != 4097) return 41;
        if (commitTrace) Report(166);
        if (DOS.Close(file.Value) == 0) return 41;
        if (commitTrace) Report(167);
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
        if (!file.HasValue || DOS.Write(file.Value, data, 7) != 7) return 42;
        if (commitTrace) Report(168);
        if (DOS.Close(file.Value) == 0) return 42;
        if (commitTrace) Report(169);
        if (sfs2 && !InputHasSize("SEQ0:AB/Truncated", 7)) return 96;
        if (classic)
        {
            file = DOS.Open("SEQ0:AB/Truncated", DOS.FileMode.NewFile);
            if (!file.HasValue || DOS.Write(file.Value, data, 7) != 7 || DOS.Close(file.Value) == 0) return 48;
        }
        file = DOS.Open("SEQ0:AB/Empty", DOS.FileMode.NewFile);
        if (!file.HasValue) return 43;
        if (commitTrace) Report(170);
        if (DOS.Close(file.Value) == 0) return 43;
        if (commitTrace) Report(171);
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

    internal static int Verify(APTR control, APTR data)
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

    internal static int Mount(APTR control, uint kind, bool floppy = false, bool unknown = false, uint hardDiskHighCylinder = 127, uint sfs2ReservedBlocks = 1,
        bool romFileSystem = false)
    {
        var suppliedCylinders = APTR.ReadUInt32(control, 408);
        if (suppliedCylinders != 0) hardDiskHighCylinder = suppliedCylinders - 1;
        var format = kind % 10;
        uint romSegment = 0, romVector = uint.MaxValue;
        if (romFileSystem)
        {
            // Reference arm: reuse the boot floppy's Kickstart filesystem segment.
            const uint flags = (uint)(DosListLockFlags.Read | DosListLockFlags.Devices);
            var list = DOS.LockDosList(flags);
            var boot = DOS.FindDosEntry(list, CString.FromLiteral("DF0"), flags);
            if (boot.IsNotNull)
            {
                romSegment = APTR.ReadUInt32(boot, DosLayout.DeviceNode.SegmentList);
                romVector = APTR.ReadUInt32(boot, DosLayout.DeviceNode.GlobalVector);
            }
            DOS.UnLockDosList(flags);
            if (romSegment == 0) return 12;
        }
        var segmentList = romSegment;
        if (!romFileSystem)
        {
            var loaded = DOS.LoadSeg("HANDLER");
            if (!loaded.HasValue) return 12;
            segmentList = loaded.Value.Raw;
        }
        Report(224); // Code loaded; capture cold initialization before starting the handler.
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
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.Reserved, format == 3 ? sfs2ReservedBlocks : 2u);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.HighCylinder, kind >= 10 ? 79u : hardDiskHighCylinder);
        var requestedBuffers = APTR.ReadUInt32(control, 404);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.NumberOfBuffers, requestedBuffers == 0 ? 32u : requestedBuffers);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.BufferMemoryType, 1);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.MaximumTransfer, 65536);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.Mask, 0xFFFFFFFE);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.BootPriority, 0xFFFFFF7F);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.DosType, format == 0 ? 0x444F5300u : format == 1 ? 0x444F5301u : format == 2 ? 0x53465300u : 0x53465302u);
        APTR.WriteUInt32(node, DosLayout.DeviceNode.StackSize, 32768);
        APTR.WriteUInt32(node, DosLayout.DeviceNode.Priority, 10);
        APTR.WriteUInt32(node, DosLayout.DeviceNode.Startup, startup.Raw >> 2);
        APTR.WriteUInt32(node, DosLayout.DeviceNode.SegmentList, segmentList);
        APTR.WriteUInt32(node, DosLayout.DeviceNode.GlobalVector, romVector);
        return DOS.AddDosEntry(node) == 0 ? 14 : 0;
    }


    internal static int Failed(APTR control, uint stage)
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
