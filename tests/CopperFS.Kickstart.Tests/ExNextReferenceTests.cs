using Amiga;
using CopperStart.Floppy.Native.Tests;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    // Kickstart 3.1's own filesystem is the reference for ExNext: the key it
    // reports in fib_DiskKey and how a scan continues while its entries are
    // deleted (the Delete ALL / MatchNext pattern).
    [LinkReferenceFact]
    public async System.Threading.Tasks.Task CopperFfsMatchesKickstartExNext()
    {
        var handler = Environment.GetEnvironmentVariable("COPPERFS_FFS_HANDLER")!;
        // A sibling of the link reference directory, which that test recreates.
        var root = (Environment.GetEnvironmentVariable("COPPERFS_LINK_ARTIFACTS") is { Length: > 0 } configured
            ? configured : Path.Combine(Path.GetTempPath(), "copperfs-link-reference")) + "-exnext";
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var initial = DosDiskImage.Create(true, includeLargeFile: false, blockCount: 32768,
            firstFileBlock: 2, payload: [1, 2, 3], volumeName: "ExNext").Bytes;
        var created = await Run(root, "ffs", "rom-create", handler, initial, true, "ExNextProbe::RomCreate");
        Assert.Equal(40u, ProbeData(root, "rom-create")[0]);
        var mismatches = new List<string>();
        var report = new Dictionary<string, object>();
        foreach (var phase in new[] { "Keys", "DeleteAll" })
        {
            var results = new Dictionary<string, uint[]>();
            foreach (var arm in new[] { "Rom", "Copper" })
            {
                var label = arm.ToLowerInvariant() + "-" + phase.ToLowerInvariant();
                await Run(root, "ffs", label, handler, created, phase != "Keys", "ExNextProbe::" + arm + phase);
                results[arm] = ProbeData(root, label);
            }
            report[phase] = results;
            if (!results["Rom"].SequenceEqual(results["Copper"])) mismatches.Add(phase);
        }
        File.WriteAllText(Path.Combine(root, "exnext-reference.json"), System.Text.Json.JsonSerializer.Serialize(report, Json));
        Assert.True(mismatches.Count == 0, "Differs from Kickstart: " + string.Join(", ", mismatches) + "; see " + root);
    }
}

public static class ExNextProbe
{
    public static int RomCreate() => Run(true, 0);
    public static int RomKeys() => Run(true, 1);
    public static int CopperKeys() => Run(false, 1);
    public static int RomDeleteAll() => Run(true, 2);
    public static int CopperDeleteAll() => Run(false, 2);

    private static int Run(bool rom, int phase)
    {
        var library = Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) { CorrectnessProbe.Report(250); return 10; }
        DOS.DOSLibraryBase = library.Value;
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)), 184, uint.MaxValue);
        var control = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var path = APTR.FromPointer(Exec.AllocMem(64, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var fib = APTR.FromPointer(Exec.AllocMem(260, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var probe = APTR.FromPointer(Exec.AllocMem(260, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        if (control.IsNull || path.IsNull || fib.IsNull || probe.IsNull) { CorrectnessProbe.Report(250); return 11; }
        CorrectnessProbe.Report(200);
        for (var shift = 24; shift >= 0; shift -= 8) CorrectnessProbe.Report((int)(control.Raw >> shift));
        var setup = CorrectnessProbe.Mount(control, 1, romFileSystem: rom);
        if (setup != 0) return CorrectnessProbe.Failed(control, (uint)setup);
        var d = APTR.FromPointer(control.Raw + 512);
        // "SEQ0:E/" followed by the listed name.
        var prefix = APTR.FromPointer(CString.ToUInt32(CString.FromLiteral("SEQ0:E/")));
        for (var i = 0; i < 7; i++) APTR.WriteUInt8(path, i, APTR.ReadUInt8(prefix, i));
        if (phase == 0) Create(d, path);
        else if (phase == 1) Keys(d, path, fib, probe);
        else DeleteAll(d, path, fib);
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (DOS.Inhibit("SEQ0:", 1) != 0) { CorrectnessProbe.Report(210); return 0; }
            DOS.Delay(10);
        }
        return CorrectnessProbe.Failed(control, 90);
    }

    private static void Put(APTR d, int index, uint value) => APTR.WriteUInt32(d, index * 4, value);

    private static void Create(APTR d, APTR path)
    {
        var directory = DOS.CreateDir("SEQ0:E");
        if (directory.HasValue) DOS.UnLock(directory.Value);
        uint made = 0;
        for (var i = 0; i < 40; i++)
        {
            APTR.WriteUInt8(path, 7, (byte)('A' + i / 10)); APTR.WriteUInt8(path, 8, (byte)('0' + i % 10)); APTR.WriteUInt8(path, 9, 0);
            var file = DOS.Open(CString.FromPointer(path.Raw), DOS.FileMode.NewFile);
            if (!file.HasValue) continue;
            DOS.Write(file.Value, path, 3);
            if (DOS.Close(file.Value) != 0) made++;
        }
        Put(d, 0, made);
    }

    private static void Name(APTR path, APTR fib)
    {
        var i = 0;
        for (; i < 30 && APTR.ReadUInt8(fib, 8 + i) != 0; i++) APTR.WriteUInt8(path, 7 + i, APTR.ReadUInt8(fib, 8 + i));
        APTR.WriteUInt8(path, 7 + i, 0);
    }

    // fib_DiskKey after Examine and after each ExNext, and whether locking the
    // listed name and examining it reports the same key.
    private static void Keys(APTR d, APTR path, APTR fib, APTR probe)
    {
        var directory = DOS.Lock("SEQ0:E", DOS.LockMode.Shared);
        if (!directory.HasValue || DOS.Examine(directory.Value, fib) == 0) { Put(d, 0, 0xFFFFFFFF); return; }
        Put(d, 0, APTR.ReadUInt32(fib, 0));
        uint count = 0, same = 0;
        while (DOS.ExNext(directory.Value, fib) != 0)
        {
            if (count < 8) Put(d, 4 + (int)count, APTR.ReadUInt32(fib, 0));
            count++;
            Name(path, fib);
            var entry = DOS.Lock(CString.FromPointer(path.Raw), DOS.LockMode.Shared);
            if (entry.HasValue)
            {
                if (DOS.Examine(entry.Value, probe) != 0 && APTR.ReadUInt32(probe, 0) == APTR.ReadUInt32(fib, 0)) same++;
                DOS.UnLock(entry.Value);
            }
        }
        Put(d, 1, count); Put(d, 2, same); Put(d, 3, unchecked((uint)DOS.IoErr()));
        DOS.UnLock(directory.Value);
    }

    // List one entry, delete it, list the next: every entry must go.
    private static void DeleteAll(APTR d, APTR path, APTR fib)
    {
        var directory = DOS.Lock("SEQ0:E", DOS.LockMode.Shared);
        if (!directory.HasValue || DOS.Examine(directory.Value, fib) == 0) { Put(d, 0, 0xFFFFFFFF); return; }
        uint deleted = 0, failed = 0;
        while (DOS.ExNext(directory.Value, fib) != 0)
        {
            Name(path, fib);
            if (DOS.DeleteFile(CString.FromPointer(path.Raw)) != 0) deleted++; else failed++;
        }
        Put(d, 0, deleted); Put(d, 1, failed); Put(d, 2, unchecked((uint)DOS.IoErr()));
        uint remaining = 0;
        if (DOS.Examine(directory.Value, fib) != 0) while (DOS.ExNext(directory.Value, fib) != 0) remaining++;
        Put(d, 3, remaining);
        DOS.UnLock(directory.Value);
    }
}
