using Amiga;
using System.Text.Json;
using CopperSFS.Check;
using Task = System.Threading.Tasks.Task;

namespace CopperFS.Kickstart.Tests;

public sealed class SfsGroupedTheoryAttribute : TheoryAttribute
{
    public SfsGroupedTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("COPPERFS_SFS_GROUPED") != "1" ||
            !File.Exists(Environment.GetEnvironmentVariable("COPPERSHARP_KICKSTART31_ROM")) ||
            !File.Exists(Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")))
            Skip = "Requires grouped SFS profile, licensed ROM and qualification manifest.";
    }
}

public sealed partial class OriginalHandlerCorrectnessTests
{
    [SfsGroupedTheory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task SfsGroupedPressureFailureRetainsAcceptedGroup(int barrier)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == "sfs");
        var initial = File.ReadAllBytes(manifest.ClassicBase);
        var root = Path.Combine(Path.GetDirectoryName(path)!, "pressure-failures", barrier.ToString());
        var trace = new DeferredFlushFailure(initial, barrier);
        var result = await Run(root, "sfs", "candidate", pair.Candidate, initial, true, "SfsGroupedProbe::PressureFailure",
            storage: trace, observe: (mark, _, _, _, _) =>
            { if (mark == 219) trace.Armed = true; if (mark == 225) trace.ClearFailure = true; });
        Assert.True(trace.Failures >= 2);
        var expected = new byte[20480];
        for (var i = 0; i < 32; i++) expected[i * 512] = 93;
        Assert.Equal(Hash(expected), SfsImageChecker.Check(new MemoryStream(result), reservedBlocks: 2).Entries.Single(e => e.Path == "/Pressure").Sha256);
        await Run(root, "sfs", "original", pair.Original, result, true, "OriginalTrstProbe::Recover");
    }

    [SfsGroupedTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task SfsGroupedPressureRetryDefersOnlyTheNewGroup(bool cut)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == "sfs");
        var initial = File.ReadAllBytes(manifest.ClassicBase);
        var root = Path.Combine(Path.GetDirectoryName(path)!, "pressure", cut.ToString());
        var trace = new DurableTrace(initial); int before = -1, after = -1;
        var run = () => Run(root, "sfs", "candidate", pair.Candidate, initial, true, "SfsGroupedProbe::Pressure",
            storage: trace, observe: (mark, _, _, _, _) =>
            {
                if (mark == 220) before = trace.FlushCuts;
                if (mark == 221) { after = trace.FlushCuts; if (cut) throw new PowerCut(); }
            });
        if (cut) await Assert.ThrowsAsync<PowerCut>(run); else await run();
        Assert.Equal(before, after); Assert.True(before > 4); // At least one pressure commit after seeding.
        var image = File.ReadAllBytes(Path.Combine(root, "candidate", "disk.hdf"));
        foreach (var arm in new[] { "candidate", "original" })
        {
            var recovered = await Run(root, "sfs", "recover-" + arm, arm == "candidate" ? pair.Candidate : pair.Original,
                image, true, "OriginalTrstProbe::Recover");
            var report = SfsImageChecker.Check(new MemoryStream(recovered), reservedBlocks: 2);
            var file = report.Entries.Single(e => e.Path == "/Pressure");
            var possible = new List<string>();
            // Every pressure checkpoint contains a prefix of complete byte edits.
            // The last retry remains volatile until the explicit filesystem flush.
            for (var count = cut ? 1 : 32; count <= (cut ? 31 : 32); count++)
            {
                var bytes = new byte[16384];
                for (var i = 0; i < count; i++) bytes[i * 512] = 93;
                possible.Add(Hash(bytes));
            }
            Assert.Contains(file.Sha256, possible);
        }
    }

    [SfsGroupedTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task SfsGroupedWarmComparison(bool chip)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == "sfs");
        var previous = Environment.GetEnvironmentVariable("COPPERFS_SFS_REFERENCE")!;
        Assert.True(File.Exists(previous));
        var root = Path.Combine(Path.GetDirectoryName(path)!, chip ? "warm-chip" : "warm-fast");
        var samples = new List<object>(); var initial = File.ReadAllBytes(manifest.ClassicBase);
        for (var trial = 0; trial < 3; trial++)
        foreach (var arm in trial % 2 == 0 ? new[] { "original", "previous", "candidate" } : new[] { "candidate", "previous", "original" })
        {
            long started = 0; int reads = 0, writes = 0, flushes = 0;
            var image = await Run(root, "sfs", $"{trial}-{arm}", arm == "original" ? pair.Original : arm == "previous" ? previous : pair.Candidate,
                initial, true, "CorrectnessProbe::OwnedBenchmark" + (chip ? "Chip" : "") + "Sfs", observe: (mark, cycle, r, w, f) =>
                {
                    if (mark is 218 or 220 or 222) { started = cycle; reads = r; writes = w; flushes = f; }
                    if (mark is 219 or 221 or 223) samples.Add(new { Trial = trial, Arm = arm, Phase = mark == 219 ? "mount" : mark == 221 ? "mutate" : "verify",
                        Cycles = cycle - started, Reads = r - reads, Writes = w - writes, Flushes = f - flushes });
                });
            SfsImageChecker.Check(new MemoryStream(image), reservedBlocks: 2);
        }
        File.WriteAllText(Path.Combine(root, "cycles.json"), JsonSerializer.Serialize(new
        { Original = Hash(File.ReadAllBytes(pair.Original)), Previous = Hash(File.ReadAllBytes(previous)), Candidate = Hash(File.ReadAllBytes(pair.Candidate)), Samples = samples }, Json));
    }

    [SfsGroupedTheory]
    [InlineData("sfs")]
    public async Task SfsGroupedPublishedPrefixesRecoverWithBothHandlers(string profile)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == profile);
        var initial = File.ReadAllBytes(manifest.ClassicBase);
        var root = Path.Combine(Path.GetDirectoryName(path)!, "group-cuts");
        string Describe(byte[] bytes) => JsonSerializer.Serialize(SfsImageChecker.Check(new MemoryStream(bytes), reservedBlocks: 2));
        var before = Describe(initial); var trace = new DurableTrace(initial);
        var final = await Run(root, profile, "complete", pair.Candidate, initial, true, "SfsGroupedProbe::Flush", storage: trace);
        var after = Describe(final); Assert.NotEqual(before, after);
        foreach (var prefix in trace.Prefixes)
        {
            var label = "cut-" + prefix.Cut;
            await Assert.ThrowsAsync<PowerCut>(() => Run(root, profile, label, pair.Candidate, initial, true,
                "SfsGroupedProbe::Flush", storage: new DurableTrace(initial, prefix.Cut)));
            var image = File.ReadAllBytes(Path.Combine(root, label, "disk.hdf"));
            foreach (var arm in new[] { "candidate", "original" })
            {
                var recovered = await Run(root, profile, label + "-" + arm, arm == "candidate" ? pair.Candidate : pair.Original,
                    image, true, "OriginalTrstProbe::Recover");
                var actual = Describe(recovered);
                Assert.True(actual == before || actual == after);
            }
        }
        output.WriteLine($"{trace.SectorCuts} sector boundaries, {trace.FlushCuts} flush boundaries, {trace.Prefixes.Count} distinct durable prefixes.");
    }

    [SfsGroupedTheory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task SfsGroupedCommitFailureRefusesMutationUntilRecovery(int barrier)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == "sfs");
        var initial = File.ReadAllBytes(manifest.ClassicBase);
        var trace = new DeferredFlushFailure(initial, barrier);
        var root = Path.Combine(Path.GetDirectoryName(path)!, "group-failures", barrier.ToString());
        var result = await Run(root, "sfs", "candidate", pair.Candidate, initial, true, "SfsGroupedProbe::Failure",
            storage: trace, observe: (mark, _, _, _, _) =>
            { if (mark == 220) trace.Armed = true; if (mark == 225) trace.ClearFailure = true; });
        Assert.True(trace.Failures >= 2);
        var entry = SfsImageChecker.Check(new MemoryStream(result), reservedBlocks: 2).Entries.Single(e => e.Path == "/GroupedRenamed");
        Assert.Equal(Hash(Enumerable.Repeat((byte)93, 1024).ToArray()), entry.Sha256);
        await Run(root, "sfs", "original", pair.Original, result, true, "OriginalTrstProbe::Recover");
    }

    [SfsGroupedTheory]
    [InlineData("Flush", false)] [InlineData("Timer", false)] [InlineData("Inhibit", false)]
    [InlineData("Trim", false)]
    [InlineData("Flush", true)]
    public async Task SfsGroupedCloseBoundary(string boundary, bool cut)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == "sfs");
        var initial = File.ReadAllBytes(manifest.ClassicBase);
        var root = Path.Combine(Path.GetDirectoryName(path)!, "closed", boundary, cut.ToString());
        var trace = new DurableTrace(initial); int before = -1, closed = -1;
        var discard = boundary == "Trim" ? new AutomaticDiscardHook(false) : null;
        Task<byte[]> Execute() => Run(root, "sfs", "candidate", pair.Candidate, initial, true,
            "SfsGroupedProbe::" + boundary, storage: trace, deviceHook: discard,
            observe: (mark, _, _, _, f) =>
            {
                var flushes = discard?.Flushes ?? f;
                if (mark == 219) before = flushes;
                if (mark == 220) { closed = flushes; if (cut) throw new PowerCut(); }
            });
        byte[] image;
        if (cut) { await Assert.ThrowsAsync<PowerCut>(Execute); image = File.ReadAllBytes(Path.Combine(root, "candidate", "disk.hdf")); }
        else image = await Execute();
        Assert.True(before >= 0); Assert.Equal(before, closed);
        if (discard != null) { Assert.Equal(0, discard.Discards); Assert.True(discard.Flushes > closed); }
        foreach (var arm in new[] { "candidate", "original" })
        {
            var recovered = await Run(root, "sfs", arm + "-restart", arm == "candidate" ? pair.Candidate : pair.Original,
                image, true, "OriginalTrstProbe::Recover");
            var entries = SfsImageChecker.Check(new MemoryStream(recovered), reservedBlocks: 2).Entries;
            if (cut) Assert.DoesNotContain(entries, e => e.Path == "/GroupedRenamed");
            else
            {
                var file = entries.Single(e => e.Path == "/GroupedRenamed");
                Assert.Equal(Hash(Enumerable.Repeat((byte)93, 1024).ToArray()), file.Sha256);
                Assert.Equal("grouped", file.Comment);
            }
        }
    }
}

public static class SfsGroupedProbe
{
    public static int Pressure() => PressureCore(false);
    public static int PressureFailure() => PressureCore(true);
    private static int PressureCore(bool fail)
    {
        var library = global::Amiga.Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) { CorrectnessProbe.Report(250); return 1; }
        DOS.DOSLibraryBase = library.Value;
        APTR.WriteUInt32(global::Amiga.Exec.FindTask(CString.FromPointer(0)), 184, uint.MaxValue);
        var control = APTR.FromPointer(global::Amiga.Exec.AllocMem(17408, global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear));
        if (control.IsNull) { CorrectnessProbe.Report(250); return 2; }
        CorrectnessProbe.Report(200); CorrectnessProbe.Report((int)(control.Raw >> 24));
        CorrectnessProbe.Report((int)(control.Raw >> 16)); CorrectnessProbe.Report((int)(control.Raw >> 8)); CorrectnessProbe.Report((int)control.Raw);
        if (CorrectnessProbe.Mount(control, 2) != 0) return CorrectnessProbe.Failed(control, 3);
        var data = APTR.FromPointer(control.Raw + 1024);
        var file = DOS.Open("SEQ0:Pressure", DOS.FileMode.NewFile);
        if (!file.HasValue || DOS.Write(file.Value, data, 16384) != 16384) return CorrectnessProbe.Failed(control, 4);
        if (DOS.DoPkt(DOS.DeviceProc("SEQ0:"), (int)DosPacketAction.Flush, 0, 0, 0, 0, 0) == 0) return CorrectnessProbe.Failed(control, 5);
        CorrectnessProbe.Report(219);
        // Leave unpublished append data before pressure, exercising the first
        // durability barrier as well as journal/home/clean-marker barriers.
        if (fail && (DOS.Seek(file.Value, 16384, -1) < 0 || DOS.Write(file.Value, data, 4096) != 4096))
            return CorrectnessProbe.Failed(control, 12);
        APTR.WriteUInt8(data, 0, 93);
        var failed = false;
        for (var i = 0; i < 32; i++)
        {
            if (DOS.Seek(file.Value, i * 512, -1) < 0) return CorrectnessProbe.Failed(control, 6);
            if (DOS.Write(file.Value, data, 1) != 1)
            {
                if (!fail || failed || DOS.SetProtection("SEQ0:Pressure", 32) != 0) return CorrectnessProbe.Failed(control, 9);
                failed = true; CorrectnessProbe.Report(225);
                if (DOS.DoPkt(DOS.DeviceProc("SEQ0:"), (int)DosPacketAction.Flush, 0, 0, 0, 0, 0) == 0 ||
                    DOS.Seek(file.Value, i * 512, -1) < 0 || DOS.Write(file.Value, data, 1) != 1) return CorrectnessProbe.Failed(control, 10);
            }
        }
        if (fail && !failed) return CorrectnessProbe.Failed(control, 11);
        CorrectnessProbe.Report(220);
        if (DOS.Close(file.Value) == 0) return CorrectnessProbe.Failed(control, 7);
        CorrectnessProbe.Report(221);
        if (DOS.DoPkt(DOS.DeviceProc("SEQ0:"), (int)DosPacketAction.Flush, 0, 0, 0, 0, 0) == 0) return CorrectnessProbe.Failed(control, 8);
        CorrectnessProbe.Report(210); return 0;
    }

    public static int Flush() => Execute(0);
    public static int Timer() => Execute(1);
    public static int Inhibit() => Execute(2);
    public static int Failure() => Execute(3);
    public static int Trim() => Execute(4);
    private static int Execute(int boundary)
    {
        var library = global::Amiga.Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) { CorrectnessProbe.Report(250); return 1; }
        DOS.DOSLibraryBase = library.Value;
        APTR.WriteUInt32(global::Amiga.Exec.FindTask(CString.FromPointer(0)), 184, uint.MaxValue);
        var control = APTR.FromPointer(global::Amiga.Exec.AllocMem(2048, global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear));
        if (control.IsNull) { CorrectnessProbe.Report(250); return 2; }
        CorrectnessProbe.Report(200); CorrectnessProbe.Report((int)(control.Raw >> 24));
        CorrectnessProbe.Report((int)(control.Raw >> 16)); CorrectnessProbe.Report((int)(control.Raw >> 8)); CorrectnessProbe.Report((int)control.Raw);
        if (CorrectnessProbe.Mount(control, 2) != 0) return CorrectnessProbe.Failed(control, 3);
        if (DOS.DeviceProc("SEQ0:").IsNull) return CorrectnessProbe.Failed(control, 4);
        CorrectnessProbe.Report(219);
        var data = APTR.FromPointer(control.Raw + 1024);
        for (var i = 0; i < 1024; i++) APTR.WriteUInt8(data, i, 93);
        var file = DOS.Open("SEQ0:Grouped", DOS.FileMode.NewFile);
        if (!file.HasValue || DOS.Write(file.Value, data, 1024) != 1024 || DOS.Close(file.Value) == 0) return CorrectnessProbe.Failed(control, 5);
        if (DOS.Rename("SEQ0:Grouped", "SEQ0:GroupedRenamed") == 0 || DOS.SetComment("SEQ0:GroupedRenamed", "grouped") == 0) return CorrectnessProbe.Failed(control, 6);
        file = DOS.Open("SEQ0:GroupedRenamed", DOS.FileMode.OldFile);
        if (!file.HasValue || DOS.Read(file.Value, data, 1024) != 1024 || DOS.Close(file.Value) == 0) return CorrectnessProbe.Failed(control, 7);
        for (var i = 0; i < 1024; i++) if (APTR.ReadUInt8(data, i) != 93) return CorrectnessProbe.Failed(control, 8);
        CorrectnessProbe.Report(220);
        if (boundary == 3)
        {
            if (DOS.DoPkt(DOS.DeviceProc("SEQ0:"), (int)DosPacketAction.Flush, 0, 0, 0, 0, 0) != 0) return CorrectnessProbe.Failed(control, 11);
            if (DOS.SetProtection("SEQ0:GroupedRenamed", 32) != 0) return CorrectnessProbe.Failed(control, 12);
            CorrectnessProbe.Report(225);
            if (DOS.DoPkt(DOS.DeviceProc("SEQ0:"), (int)DosPacketAction.Flush, 0, 0, 0, 0, 0) == 0) return CorrectnessProbe.Failed(control, 13);
        }
        else if (boundary == 4)
        {
            var record = APTR.FromPointer(control.Raw + 512);
            APTR.WriteUInt32(record, 0, 32); APTR.WriteUInt32(record, 4, 1); APTR.WriteUInt32(record, 8, 1);
            if (DOS.DoPkt(DOS.DeviceProc("SEQ0:"), 0x43535452, unchecked((int)record.Raw), 0, 0, 0, 0) == 0) return CorrectnessProbe.Failed(control, 14);
        }
        else if (boundary == 1) DOS.Delay(650);
        else if (boundary == 2) { if (DOS.Inhibit("SEQ0:", 1) == 0) return CorrectnessProbe.Failed(control, 9); }
        else if (DOS.DoPkt(DOS.DeviceProc("SEQ0:"), (int)DosPacketAction.Flush, 0, 0, 0, 0, 0) == 0) return CorrectnessProbe.Failed(control, 10);
        CorrectnessProbe.Report(223); CorrectnessProbe.Report(210); return 0;
    }
}
