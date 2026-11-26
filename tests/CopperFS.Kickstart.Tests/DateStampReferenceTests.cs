using Amiga;
using CopperStart.Floppy.Native.Tests;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    // Kickstart 3.1's own filesystem is the reference for which objects get a
    // new modification date after each operation: the object itself, its
    // parent directory and the root. Exact times differ between runs, so each
    // word records 2 (changed to at least the time before the operation),
    // 1 (changed to something older) or 0 (unchanged).
    [LinkReferenceFact]
    public async System.Threading.Tasks.Task CopperFfsMatchesKickstartDateStamps()
    {
        var handler = Environment.GetEnvironmentVariable("COPPERFS_FFS_HANDLER")!;
        var root = (Environment.GetEnvironmentVariable("COPPERFS_LINK_ARTIFACTS") is { Length: > 0 } configured
            ? configured : Path.Combine(Path.GetTempPath(), "copperfs-link-reference")) + "-stamps";
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var initial = DosDiskImage.Create(true, includeLargeFile: false, blockCount: 32768,
            firstFileBlock: 2, payload: [1, 2, 3], volumeName: "Stamps").Bytes;
        var results = new Dictionary<string, uint[]>();
        foreach (var arm in new[] { "Rom", "Copper" })
        {
            var label = arm.ToLowerInvariant() + "-stamps";
            await Run(root, "ffs", label, handler, initial, true, "DateStampProbe::" + arm);
            results[arm] = ProbeData(root, label);
            output.WriteLine(arm + ": " + string.Join(",", results[arm].Take(21)));
        }
        Assert.True(results["Rom"].SequenceEqual(results["Copper"]),
            "Differs from Kickstart:\nrom    " + string.Join(",", results["Rom"]) + "\ncopper " + string.Join(",", results["Copper"]));
        Directory.Delete(root, true);
    }
}

public static class DateStampProbe
{
    public static int Rom() => Run(true);
    public static int Copper() => Run(false);

    // Days, minutes and ticks of `name` into stamp[0..2]; false when absent.
    private static bool Stamp(CString name, APTR fib, APTR stamp)
    {
        var locked = DOS.Lock(name, DOS.LockMode.Shared);
        if (!locked.HasValue) return false;
        var ok = DOS.Examine(locked.Value, fib) != 0;
        DOS.UnLock(locked.Value);
        if (!ok) return false;
        APTR.WriteUInt32(stamp, 0, APTR.ReadUInt32(fib, FileInfoBlock.DateDaysOffset));
        APTR.WriteUInt32(stamp, 4, APTR.ReadUInt32(fib, FileInfoBlock.DateMinuteOffset));
        APTR.WriteUInt32(stamp, 8, APTR.ReadUInt32(fib, FileInfoBlock.DateTickOffset));
        return true;
    }

    private static bool Same(APTR a, APTR b) =>
        APTR.ReadUInt32(a, 0) == APTR.ReadUInt32(b, 0) && APTR.ReadUInt32(a, 4) == APTR.ReadUInt32(b, 4) && APTR.ReadUInt32(a, 8) == APTR.ReadUInt32(b, 8);

    private static bool NotOlder(APTR value, APTR reference)
    {
        if (APTR.ReadUInt32(value, 0) != APTR.ReadUInt32(reference, 0)) return APTR.ReadUInt32(value, 0) > APTR.ReadUInt32(reference, 0);
        if (APTR.ReadUInt32(value, 4) != APTR.ReadUInt32(reference, 4)) return APTR.ReadUInt32(value, 4) > APTR.ReadUInt32(reference, 4);
        return APTR.ReadUInt32(value, 8) >= APTR.ReadUInt32(reference, 8);
    }

    private static uint Classify(CString name, APTR fib, APTR before, APTR after, APTR now)
    {
        if (!Stamp(name, fib, after)) return 9;
        if (Same(before, after)) return 0;
        return NotOlder(after, now) ? 2u : 1u;
    }

    private static int Run(bool rom)
    {
        var library = Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) { CorrectnessProbe.Report(250); return 10; }
        DOS.DOSLibraryBase = library.Value;
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)), 184, uint.MaxValue);
        var control = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var fib = APTR.FromPointer(Exec.AllocMem(260, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var s = APTR.FromPointer(Exec.AllocMem(128, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        if (control.IsNull || fib.IsNull || s.IsNull) { CorrectnessProbe.Report(250); return 11; }
        CorrectnessProbe.Report(200);
        for (var shift = 24; shift >= 0; shift -= 8) CorrectnessProbe.Report((int)(control.Raw >> shift));
        var setup = CorrectnessProbe.Mount(control, 1, romFileSystem: rom);
        if (setup != 0) return CorrectnessProbe.Failed(control, (uint)setup);
        var d = APTR.FromPointer(control.Raw + 512);
        // Slots: file, file-before, drawer, drawer-before, root, root-before, now, scratch.
        var file = s; var fileBefore = APTR.FromPointer(s.Raw + 12);
        var drawer = APTR.FromPointer(s.Raw + 24); var drawerBefore = APTR.FromPointer(s.Raw + 36);
        var volume = APTR.FromPointer(s.Raw + 48); var volumeBefore = APTR.FromPointer(s.Raw + 60);
        var now = APTR.FromPointer(s.Raw + 72);
        var made = DOS.CreateDir("SEQ0:D");
        if (made.HasValue) DOS.UnLock(made.Value);
        for (var step = 0; step < 7; step++)
        {
            Stamp("SEQ0:D/F", fib, fileBefore); Stamp("SEQ0:D/G", fib, fileBefore);
            Stamp("SEQ0:D", fib, drawerBefore); Stamp("SEQ0:", fib, volumeBefore);
            DOS.Delay(60);
            DOS.DateStamp(now);
            if (step == 0) { var h = DOS.Open("SEQ0:D/F", DOS.FileMode.NewFile); if (h.HasValue) DOS.Close(h.Value); }
            else if (step == 1) { var h = DOS.Open("SEQ0:D/F", DOS.FileMode.OldFile); if (h.HasValue) { DOS.Write(h.Value, s, 100); DOS.Close(h.Value); } }
            else if (step == 2) DOS.Rename("SEQ0:D/F", "SEQ0:D/G");
            else if (step == 3) DOS.SetProtection("SEQ0:D/G", 2);
            else if (step == 4) { var h = DOS.Open("SEQ0:D/H", DOS.FileMode.NewFile); if (h.HasValue) DOS.Close(h.Value); DOS.DeleteFile("SEQ0:D/H"); }
            else if (step == 5) { var e = DOS.CreateDir("SEQ0:E"); if (e.HasValue) DOS.UnLock(e.Value); }
            else { var h = DOS.Open("SEQ0:D/G", DOS.FileMode.ReadWrite); if (h.HasValue) { DOS.Seek(h.Value, 0, 1); DOS.Write(h.Value, s, 10); DOS.Close(h.Value); } }
            var target = step >= 2 ? CString.FromLiteral("SEQ0:D/G") : CString.FromLiteral("SEQ0:D/F");
            APTR.WriteUInt32(d, step * 12, Classify(target, fib, fileBefore, file, now));
            APTR.WriteUInt32(d, step * 12 + 4, Classify("SEQ0:D", fib, drawerBefore, drawer, now));
            APTR.WriteUInt32(d, step * 12 + 8, Classify("SEQ0:", fib, volumeBefore, volume, now));
        }
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (DOS.Inhibit("SEQ0:", 1) != 0) { CorrectnessProbe.Report(210); return 0; }
            DOS.Delay(10);
        }
        return CorrectnessProbe.Failed(control, 90);
    }
}
