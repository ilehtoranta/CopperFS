using Amiga;
using CopperStart.Floppy.Native.Tests;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    // Kickstart 3.1's own filesystem is the reference for ACTION_SET_DATE:
    // dp_Arg4 is a CPTR to the DateStamp. A DateStamp in low chip memory
    // must not be mistaken for a BPTR whose expanded address is also RAM.
    [LinkReferenceFact]
    public async System.Threading.Tasks.Task CopperFfsMatchesKickstartSetDate()
    {
        var handler = Environment.GetEnvironmentVariable("COPPERFS_FFS_HANDLER")!;
        var root = (Environment.GetEnvironmentVariable("COPPERFS_LINK_ARTIFACTS") is { Length: > 0 } configured
            ? configured : Path.Combine(Path.GetTempPath(), "copperfs-link-reference")) + "-date";
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var initial = DosDiskImage.Create(true, includeLargeFile: false, blockCount: 32768,
            firstFileBlock: 2, payload: [1, 2, 3], volumeName: "Dates").Bytes;
        var results = new Dictionary<string, uint[]>();
        foreach (var arm in new[] { "Rom", "Copper" })
        {
            var label = arm.ToLowerInvariant() + "-date";
            await Run(root, "ffs", label, handler, initial, true, "DateProbe::" + arm);
            results[arm] = ProbeData(root, label);
        }
        Assert.True(results["Rom"].SequenceEqual(results["Copper"]),
            "Differs from Kickstart:\nrom    " + string.Join(",", results["Rom"]) + "\ncopper " + string.Join(",", results["Copper"]));
        Directory.Delete(root, true);
    }
}

public static class DateProbe
{
    public static int Rom() => Run(true);
    public static int Copper() => Run(false);

    private static void Put(APTR d, int index, uint value) => APTR.WriteUInt32(d, index * 4, value);

    // Set through the packet with the DateStamp at `stamp`, then Examine.
    private static void SetAndRead(APTR d, int index, APTR stamp, APTR name, APTR fib, uint days, uint minutes, uint ticks)
    {
        APTR.WriteUInt32(stamp, 0, days); APTR.WriteUInt32(stamp, 4, minutes); APTR.WriteUInt32(stamp, 8, ticks);
        // Its BPTR reading (stamp << 2) is other RAM when the stamp is in low chip memory.
        var r = DOS.DoPkt(DOS.DeviceProc("SEQ0:"), (int)DosPacketAction.SetDate, 0, 0,
            unchecked((int)(name.Raw >> 2)), unchecked((int)stamp.Raw), 0);
        Put(d, index, r != 0 ? 1u : unchecked((uint)DOS.IoErr()));
        var locked = DOS.Lock("SEQ0:Data/Payload", DOS.LockMode.Shared);
        if (!locked.HasValue) return;
        if (DOS.Examine(locked.Value, fib) != 0)
        {
            Put(d, index + 1, APTR.ReadUInt32(fib, FileInfoBlock.DateDaysOffset));
            Put(d, index + 2, APTR.ReadUInt32(fib, FileInfoBlock.DateMinuteOffset));
            Put(d, index + 3, APTR.ReadUInt32(fib, FileInfoBlock.DateTickOffset));
        }
        DOS.UnLock(locked.Value);
    }

    private static int Run(bool rom)
    {
        var library = Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) { CorrectnessProbe.Report(250); return 10; }
        DOS.DOSLibraryBase = library.Value;
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)), 184, uint.MaxValue);
        var control = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var name = APTR.FromPointer(Exec.AllocMem(64, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var fib = APTR.FromPointer(Exec.AllocMem(260, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        // Chip memory is low enough that stamp << 2 is still inside chip RAM.
        var chip = APTR.FromPointer(Exec.AllocMem(64, Exec.MemoryFlags.Chip | Exec.MemoryFlags.Clear));
        var fast = APTR.FromPointer(Exec.AllocMem(64, Exec.MemoryFlags.Fast | Exec.MemoryFlags.Clear));
        if (control.IsNull || name.IsNull || fib.IsNull || chip.IsNull || fast.IsNull) { CorrectnessProbe.Report(250); return 11; }
        CorrectnessProbe.Report(200);
        for (var shift = 24; shift >= 0; shift -= 8) CorrectnessProbe.Report((int)(control.Raw >> shift));
        var setup = CorrectnessProbe.Mount(control, 1, romFileSystem: rom);
        if (setup != 0) return CorrectnessProbe.Failed(control, (uint)setup);
        var d = APTR.FromPointer(control.Raw + 512);
        // BSTR "Data/Payload" relative to the root (lock 0).
        var text = APTR.FromPointer(CString.ToUInt32(CString.FromLiteral("Data/Payload")));
        APTR.WriteUInt8(name, 0, 12);
        for (var i = 0; i < 12; i++) APTR.WriteUInt8(name, 1 + i, APTR.ReadUInt8(text, i));
        Put(d, 20, chip.Raw << 2 < 512 * 1024 ? 1u : 0u); // the low-address case is really exercised
        SetAndRead(d, 0, chip, name, fib, 1234, 56, 789);
        SetAndRead(d, 4, fast, name, fib, 4321, 65, 987);
        // dos.library's own SetFileDate on the same object.
        APTR.WriteUInt32(fast, 0, 999); APTR.WriteUInt32(fast, 4, 1); APTR.WriteUInt32(fast, 8, 2);
        Put(d, 8, unchecked((uint)DOS.SetFileDate("SEQ0:Data/Payload", fast)));
        var locked = DOS.Lock("SEQ0:Data/Payload", DOS.LockMode.Shared);
        if (locked.HasValue)
        {
            if (DOS.Examine(locked.Value, fib) != 0) Put(d, 9, APTR.ReadUInt32(fib, FileInfoBlock.DateDaysOffset));
            DOS.UnLock(locked.Value);
        }
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (DOS.Inhibit("SEQ0:", 1) != 0) { CorrectnessProbe.Report(210); return 0; }
            DOS.Delay(10);
        }
        return CorrectnessProbe.Failed(control, 90);
    }
}
