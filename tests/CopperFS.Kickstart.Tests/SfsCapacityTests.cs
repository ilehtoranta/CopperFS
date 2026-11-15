using Amiga;
using System.Text.Json;
using CopperSFS.Check;
using Task = System.Threading.Tasks.Task;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    [SfsGroupedTheory] [InlineData("sfs")]
    public async Task SfsEnumerationPreservesLongResults(string profile)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == profile);
        foreach (var arm in new[] { "candidate", "original" })
            await Run(Path.Combine(Path.GetDirectoryName(path)!, "long-enumeration"), profile, arm,
                arm == "candidate" ? pair.Candidate : pair.Original, File.ReadAllBytes(manifest.ClassicBase),
                true, "SfsCapacityProbe::LongResults");
    }
    [SfsGroupedTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task HarmlessSfsRejectionsLeaveAcceptedWorkPending(bool cut)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == "sfs");
        var root = Path.Combine(Path.GetDirectoryName(path)!, "rejections", cut.ToString());
        var trace = new DurableTrace(File.ReadAllBytes(manifest.ClassicBase));
        int writes = -1, flushes = -1;
        var run = () => Run(root, "sfs", "candidate", pair.Candidate, File.ReadAllBytes(manifest.ClassicBase), true,
            "SfsCapacityProbe::Rejections", storage: trace, observe: (mark, _, _, _, _) =>
            {
                if (mark == 220) { writes = trace.SectorCuts; flushes = trace.FlushCuts; }
                if (mark == 221)
                {
                    Assert.Equal(writes, trace.SectorCuts); Assert.Equal(flushes, trace.FlushCuts);
                    if (cut) throw new PowerCut();
                }
            });
        if (cut) await Assert.ThrowsAsync<PowerCut>(run); else await run();
        var image = File.ReadAllBytes(Path.Combine(root, "candidate", "disk.hdf"));
        foreach (var arm in new[] { "original", "candidate" })
        {
            var recovered = await Run(root, "sfs", "recover-" + arm, arm == "original" ? pair.Original : pair.Candidate,
                image, true, "OriginalTrstProbe::Recover");
            var report = SfsImageChecker.Check(new MemoryStream(recovered), reservedBlocks: 2);
            var entry = report.Entries.Single(e => e.Path == "/Reject");
            Assert.Equal(cut ? "" : "pending", entry.Comment);
        }
    }

    [SfsGroupedTheory]
    [InlineData(24)] [InlineData(64)]
    public async Task SfsEnumerationCapacityComparison(int entries)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == "sfs");
        var previous = Environment.GetEnvironmentVariable("COPPERFS_SFS_REFERENCE")!;
        var root = Path.Combine(Path.GetDirectoryName(path)!, "enumeration", entries.ToString());
        var samples = new List<object>();
        foreach (var arm in new[] { "original", "previous", "candidate" })
        {
            long start = 0; int reads = 0, writes = 0, flushes = 0;
            var result = await Run(root, "sfs", arm, arm == "original" ? pair.Original : arm == "previous" ? previous : pair.Candidate,
                File.ReadAllBytes(manifest.ClassicBase), true, entries == 24 ? "SfsCapacityProbe::Enumerate24" : "SfsCapacityProbe::Enumerate64",
                observe: (mark, cycle, r, w, f) =>
                {
                    if (mark == 220) { start = cycle; reads = r; writes = w; flushes = f; }
                    if (mark == 221) samples.Add(new { Arm = arm, Cycles = cycle - start, Reads = r - reads, Writes = w - writes, Flushes = f - flushes });
                });
            Assert.Equal(entries, SfsImageChecker.Check(new MemoryStream(result), reservedBlocks: 2).Entries.Count(e => e.Path.StartsWith("/E/")));
        }
        File.WriteAllText(Path.Combine(root, "cycles.json"), JsonSerializer.Serialize(samples, Json));
    }
}

public static class SfsCapacityProbe
{
    private static APTR Start(uint bytes)
    {
        var library = global::Amiga.Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) { CorrectnessProbe.Report(250); return APTR.Null; }
        DOS.DOSLibraryBase = library.Value;
        APTR.WriteUInt32(global::Amiga.Exec.FindTask(CString.FromPointer(0)), 184, uint.MaxValue);
        var control = APTR.FromPointer(global::Amiga.Exec.AllocMem(bytes, global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear));
        if (control.IsNull) { CorrectnessProbe.Report(250); return APTR.Null; }
        CorrectnessProbe.Report(200); CorrectnessProbe.Report((int)(control.Raw >> 24));
        CorrectnessProbe.Report((int)(control.Raw >> 16)); CorrectnessProbe.Report((int)(control.Raw >> 8)); CorrectnessProbe.Report((int)control.Raw);
        if (CorrectnessProbe.Mount(control, 2) != 0) { CorrectnessProbe.Failed(control, 1); return APTR.Null; }
        return control;
    }

    public static int Rejections()
    {
        var control = Start(2048); if (control.IsNull) return 1;
        var data = APTR.FromPointer(control.Raw + 1024);
        var file = DOS.Open("SEQ0:Reject", DOS.FileMode.NewFile);
        if (!file.HasValue || DOS.Write(file.Value, data, 512) != 512 || DOS.Close(file.Value) == 0) return CorrectnessProbe.Failed(control, 2);
        var dir = DOS.CreateDir("SEQ0:Dir");
        if (!dir.HasValue) return CorrectnessProbe.Failed(control, 3);
        DOS.UnLock(dir.Value);
        file = DOS.Open("SEQ0:Dir/Child", DOS.FileMode.NewFile);
        if (!file.HasValue || DOS.Close(file.Value) == 0) return CorrectnessProbe.Failed(control, 4);
        if (DOS.DoPkt(DOS.DeviceProc("SEQ0:"), (int)DosPacketAction.Flush, 0, 0, 0, 0, 0) == 0) return CorrectnessProbe.Failed(control, 5);
        if (DOS.SetComment("SEQ0:Reject", "pending") == 0 || DOS.SetProtection("SEQ0:Reject", 1) == 0) return CorrectnessProbe.Failed(control, 6);
        CorrectnessProbe.Report(220);
        if (DOS.DeleteFile("SEQ0:Reject") != 0 || DOS.IoErr() != DOS.Error.DeleteProtected) return CorrectnessProbe.Failed(control, 7);
        if (DOS.DeleteFile("SEQ0:Missing") != 0 || DOS.IoErr() != DOS.Error.ObjectNotFound) return CorrectnessProbe.Failed(control, 8);
        if (DOS.CreateDir("SEQ0:Dir").HasValue || DOS.IoErr() != DOS.Error.ObjectExists) return CorrectnessProbe.Failed(control, 9);
        if (DOS.DeleteFile("SEQ0:Dir") != 0 || DOS.IoErr() != DOS.Error.DirectoryNotEmpty) return CorrectnessProbe.Failed(control, 10);
        if (DOS.SetProtection("SEQ0:Reject", 4) == 0) return CorrectnessProbe.Failed(control, 11);
        file = DOS.Open("SEQ0:Reject", DOS.FileMode.ReadWrite);
        if (!file.HasValue || DOS.Write(file.Value, data, 1) != -1 || DOS.IoErr() != DOS.Error.WriteProtected || DOS.Close(file.Value) == 0) return CorrectnessProbe.Failed(control, 12);
        if (DOS.SetProtection("SEQ0:Reject", 0) == 0) return CorrectnessProbe.Failed(control, 13);
        CorrectnessProbe.Report(221);
        if (DOS.DoPkt(DOS.DeviceProc("SEQ0:"), (int)DosPacketAction.Flush, 0, 0, 0, 0, 0) == 0) return CorrectnessProbe.Failed(control, 14);
        CorrectnessProbe.Report(210); return 0;
    }

    public static int Enumerate24() => Enumerate(24);
    public static int LongResults()
    {
        var control = Start(2048); if (control.IsNull) return 1;
        var dir = DOS.CreateDir("SEQ0:E"); if (!dir.HasValue) return CorrectnessProbe.Failed(control, 2);
        DOS.UnLock(dir.Value);
        var name = APTR.FromPointer(control.Raw + 512);
        var comment = APTR.FromPointer(control.Raw + 768);
        var prefix = APTR.FromPointer(CString.ToUInt32("SEQ0:E/"));
        for (var i = 0; i < 7; i++) APTR.WriteUInt8(name, i, APTR.ReadUInt8(prefix, i));
        for (var i = 0; i < 60; i++) APTR.WriteUInt8(name, 7 + i, (byte)('A' + i % 26));
        for (var i = 0; i < 70; i++) APTR.WriteUInt8(comment, i, (byte)('a' + i % 26));
        var path = CString.FromPointer(name.Raw);
        var file = DOS.Open(path, DOS.FileMode.NewFile);
        if (!file.HasValue || DOS.Close(file.Value) == 0 || DOS.SetComment(path, CString.FromPointer(comment.Raw)) == 0)
            return CorrectnessProbe.Failed(control, 3);
        dir = DOS.Lock("SEQ0:E", DOS.LockMode.Shared); if (!dir.HasValue) return CorrectnessProbe.Failed(control, 4);
        var fib = APTR.FromPointer(control.Raw + 1024);
        for (var pass = 0; pass < 3; pass++)
        {
            if (DOS.Examine(dir.Value, fib) == 0 || DOS.ExNext(dir.Value, fib) == 0)
                return CorrectnessProbe.Failed(control, 5);
            // DOS presents C strings to the application after packet return.
            for (var i = 0; i <= 60; i++)
                if (APTR.ReadUInt8(fib, 8 + i) != APTR.ReadUInt8(name, 7 + i)) return CorrectnessProbe.Failed(control, 6);
            for (var i = 0; i <= 70; i++)
                if (APTR.ReadUInt8(fib, 144 + i) != APTR.ReadUInt8(comment, i)) return CorrectnessProbe.Failed(control, 7);
            if (DOS.ExNext(dir.Value, fib) != 0) return CorrectnessProbe.Failed(control, 8);
        }
        DOS.UnLock(dir.Value);
        CorrectnessProbe.Report(210); return 0;
    }
    public static int Enumerate64() => Enumerate(64);
    private static int Enumerate(int entries)
    {
        var control = Start(2048); if (control.IsNull) return 1;
        var name = APTR.FromPointer(control.Raw + 512);
        var directory = DOS.CreateDir("SEQ0:E"); if (!directory.HasValue) return CorrectnessProbe.Failed(control, 2);
        DOS.UnLock(directory.Value);
        for (var i = 0; i < entries; i++)
        {
            var prefix = APTR.FromPointer(CString.ToUInt32("SEQ0:E/F"));
            for (var n = 0; n < 8; n++) APTR.WriteUInt8(name, n, APTR.ReadUInt8(prefix, n));
            APTR.WriteUInt8(name, 8, (byte)('0' + i / 10)); APTR.WriteUInt8(name, 9, (byte)('0' + i % 10)); APTR.WriteUInt8(name, 10, 0);
            var file = DOS.Open(CString.FromPointer(name.Raw), DOS.FileMode.NewFile);
            if (!file.HasValue || DOS.Close(file.Value) == 0) return CorrectnessProbe.Failed(control, 3);
        }
        if (DOS.DoPkt(DOS.DeviceProc("SEQ0:"), (int)DosPacketAction.Flush, 0, 0, 0, 0, 0) == 0) return CorrectnessProbe.Failed(control, 4);
        directory = DOS.Lock("SEQ0:E", DOS.LockMode.Shared); if (!directory.HasValue) return CorrectnessProbe.Failed(control, 5);
        var fib = APTR.FromPointer(control.Raw + 1024);
        for (var pass = 0; pass < 5; pass++)
        {
            if (pass == 1) CorrectnessProbe.Report(220);
            if (DOS.Examine(directory.Value, fib) == 0) return CorrectnessProbe.Failed(control, 6);
            var count = 0;
            while (DOS.ExNext(directory.Value, fib) != 0) count++;
            if (count != entries || DOS.IoErr() != DOS.Error.NoMoreEntries) return CorrectnessProbe.Failed(control, 7);
        }
        CorrectnessProbe.Report(221); DOS.UnLock(directory.Value);
        CorrectnessProbe.Report(210); return 0;
    }
}
