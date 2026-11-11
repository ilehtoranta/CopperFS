using System.Text.Json;
using Amiga;

namespace CopperFS.Kickstart.Tests;

public sealed class HandlerSizePerformanceTheoryAttribute : TheoryAttribute
{
    public HandlerSizePerformanceTheoryAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("COPPERSHARP_KICKSTART31_ROM")) ||
            !File.Exists(Environment.GetEnvironmentVariable("COPPERFS_SIZE_MANIFEST")))
            Skip = "Requires a licensed Kickstart ROM and COPPERFS_SIZE_MANIFEST with frozen baseline/candidate handlers.";
    }
}

public sealed partial class OriginalHandlerCorrectnessTests
{
    private sealed record SizePair(string Name, string Baseline, string Candidate);
    private sealed record SizeManifest(SizePair[] Profiles, string ClassicBase, string ClassicBaseSha256);
    private sealed record SizeSample(int Trial, string Arm, string Phase, long Cycles, int Reads, int Writes, int Flushes);

    [HandlerSizePerformanceTheory]
    [InlineData("ffs", "sfs")]
    [InlineData("sfs", "ffs")]
    [InlineData("sfs2-csun", "ffs")]
    [InlineData("sfs2-trst", "ffs")]
    public async System.Threading.Tasks.Task SpecializedHandlerRejectsOtherFamilyWithoutWrites(string handler, string media)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_SIZE_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<SizeManifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == handler);
        var initial = media == "sfs" ? File.ReadAllBytes(manifest.ClassicBase)
            : CopperStart.Floppy.Native.Tests.DosDiskImage.Create(true, includeLargeFile: false,
                blockCount: 32768, firstFileBlock: 2, payload: [1, 2, 3]).Bytes;
        if (media == "sfs") Assert.Equal(manifest.ClassicBaseSha256, Hash(initial));
        var writes = -1;
        await Run(Path.Combine(Path.GetDirectoryName(path)!, "family-rejection"), media, handler,
            pair.Candidate, initial, false, "HandlerFamilyProbe::" + (media == "sfs" ? "RejectSfs" : "RejectFfs"),
            (marker, _, _, w, _) => { if (marker == 210) writes = w; });
        Assert.Equal(0, writes);
    }

    // Time the same growth, overwrite, metadata, enumeration and verified-read
    // workloads in both handlers, after startup. Rotate order between trials.
    // This exercises real DOS/Exec and the frozen HUNK, not the host core.
    [HandlerSizePerformanceTheory]
    [InlineData("ofs")] [InlineData("ffs")] [InlineData("sfs")]
    [InlineData("sfs2-csun")] [InlineData("sfs2-trst")]
    public async System.Threading.Tasks.Task HandlerSizeChangesPreserveWorkloadCycles(string profile)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_SIZE_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<SizeManifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == profile);
        var initial = profile == "sfs" ? File.ReadAllBytes(manifest.ClassicBase)
            : profile.StartsWith("sfs2") ? ReadMedia.Sfs2()
            : CopperStart.Floppy.Native.Tests.DosDiskImage.Create(profile == "ffs",
                includeLargeFile: false, blockCount: 32768, firstFileBlock: 2,
                payload: [1, 2, 3], volumeName: "Correctness").Bytes;
        if (profile == "sfs") Assert.Equal(manifest.ClassicBaseSha256, Hash(initial));
        var root = Path.Combine(Path.GetDirectoryName(path)!, "performance", profile);
        var samples = new List<SizeSample>();
        foreach (var trial in Enumerable.Range(0, 3))
        foreach (var arm in trial % 2 == 0 ? new[] { "baseline", "candidate" } : new[] { "candidate", "baseline" })
        {
            long started = -1; int reads = 0, writes = 0, flushes = 0;
            var handler = arm == "baseline" ? pair.Baseline : pair.Candidate;
            var written = await Run(root, profile, $"{trial}-{arm}", handler, initial, true,
                observe: (marker, cycle, r, w, f) =>
                {
                    if (marker is 220 or 222) { started = cycle; reads = r; writes = w; flushes = f; }
                    if (marker is 221 or 223)
                    {
                        Assert.True(started >= 0);
                        samples.Add(new(trial, arm, marker == 221 ? "mutate" : "verify",
                            cycle - started, r - reads, w - writes, f - flushes));
                        started = -1;
                    }
                });
            if (profile.StartsWith("sfs")) CopperSFS.Check.SfsImageChecker.Check(new MemoryStream(written),
                reservedBlocks: profile == "sfs" ? 2u : 1u);
            else DosImageInspection.Check(written, true);
        }
        File.WriteAllText(Path.Combine(root, "cycles.json"), JsonSerializer.Serialize(new
        {
            BaselineSha256 = Hash(File.ReadAllBytes(pair.Baseline)),
            CandidateSha256 = Hash(File.ReadAllBytes(pair.Candidate)), Samples = samples
        }, Json));
        Assert.Equal(12, samples.Count);
        foreach (var phase in new[] { "mutate", "verify" })
        {
            var before = samples.Where(s => s.Arm == "baseline" && s.Phase == phase).OrderBy(s => s.Trial).ToArray();
            var after = samples.Where(s => s.Arm == "candidate" && s.Phase == phase).OrderBy(s => s.Trial).ToArray();
            for (var trial = 0; trial < 3; trial++)
                Assert.Equal((before[trial].Reads, before[trial].Writes, before[trial].Flushes),
                    (after[trial].Reads, after[trial].Writes, after[trial].Flushes));
            // Allow 0.1% for timer/interrupt placement; retain every raw sample.
            // A passing probe only qualifies these workloads and ROM/CPU.
            var ratio = after.Average(s => s.Cycles) / before.Average(s => s.Cycles);
            Assert.True(ratio <= 1.001, $"{profile}/{phase}: candidate uses {(ratio - 1) * 100:F3}% more bus cycles; see cycles.json.");
        }
    }
}

public static class HandlerFamilyProbe
{
    public static int RejectFfs() => Reject(1);
    public static int RejectSfs() => Reject(2);

    private static int Reject(uint kind)
    {
        var library = global::Amiga.Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) { CorrectnessProbe.Report(250); return 1; }
        DOS.DOSLibraryBase = library.Value;
        var control = APTR.FromPointer(global::Amiga.Exec.AllocMem(1024,
            global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear));
        if (control.IsNull || CorrectnessProbe.Mount(control, kind) != 0)
        { CorrectnessProbe.Report(250); return 2; }
        var root = DOS.Lock("SEQ0:", DOS.LockMode.Shared);
        if (root.HasValue) { DOS.UnLock(root.Value); CorrectnessProbe.Report(250); return 3; }
        CorrectnessProbe.Report(210);
        return 0;
    }
}
