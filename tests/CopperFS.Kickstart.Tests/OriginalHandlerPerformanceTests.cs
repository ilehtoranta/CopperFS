using System.Text.Json;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    // Release gate: compare against the original implementations. Internal
    // before/after comparisons remain useful diagnostics, not release criteria.
    [CorrectnessTheory]
    [InlineData("ofs")] [InlineData("ffs")] [InlineData("sfs")]
    [InlineData("sfs2-csun")] [InlineData("sfs2-trst")]
    public async System.Threading.Tasks.Task ReleaseCandidateStaysOnParWithOriginalHandler(string profile)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == profile);
        Assert.Equal(pair.OriginalSha256, Hash(File.ReadAllBytes(pair.Original)));
        var initial = profile == "sfs" ? File.ReadAllBytes(manifest.ClassicBase)
            : profile.StartsWith("sfs2") ? ReadMedia.Sfs2()
            : CopperStart.Floppy.Native.Tests.DosDiskImage.Create(profile == "ffs", includeLargeFile: false,
                blockCount: 32768, firstFileBlock: 2, payload: [1, 2, 3], volumeName: "Correctness").Bytes;
        if (profile == "sfs") Assert.Equal(manifest.ClassicBaseSha256, Hash(initial));
        var samples = new List<SizeSample>();
        var entry = profile == "ofs" ? "Ofs" : profile == "ffs" ? "Ffs" : profile == "sfs" ? "Sfs" : "Sfs2";
        var root = Path.Combine(Path.GetDirectoryName(path)!, "original-performance", profile);
        // CorrectnessProbe allocates the Fast RAM payload before loading either
        // handler. Both arms use identical media, CPU, buffer class and packets.
        foreach (var trial in Enumerable.Range(0, 3))
        foreach (var arm in trial % 2 == 0 ? new[] { "original", "candidate" } : new[] { "candidate", "original" })
        {
            long started = -1; int reads = 0, writes = 0, flushes = 0;
            var written = await Run(root, profile, $"{trial}-{arm}", arm == "original" ? pair.Original : pair.Candidate,
                initial, true, "CorrectnessProbe::Benchmark" + entry, observe: (marker, cycle, r, w, f) =>
                {
                    if (marker is 220 or 222) { started = cycle; reads = r; writes = w; flushes = f; }
                    if (marker is 221 or 223)
                        samples.Add(new(trial, arm, marker == 221 ? "mutate" : "verify",
                            cycle - started, r - reads, w - writes, f - flushes));
                });
            if (profile.StartsWith("sfs")) CopperSFS.Check.SfsImageChecker.Check(new MemoryStream(written),
                reservedBlocks: profile == "sfs" ? 2u : 1u);
            else DosImageInspection.Check(written, arm == "candidate");
        }
        Assert.Equal(12, samples.Count);
        File.WriteAllText(Path.Combine(root, "cycles.json"), JsonSerializer.Serialize(new
        {
            OriginalSha256 = Hash(File.ReadAllBytes(pair.Original)),
            CandidateSha256 = Hash(File.ReadAllBytes(pair.Candidate)), Samples = samples
        }, Json));
        var failures = new List<string>();
        foreach (var phase in new[] { "mutate", "verify" })
        {
            var original = samples.Where(s => s.Arm == "original" && s.Phase == phase).Average(s => s.Cycles);
            var candidate = samples.Where(s => s.Arm == "candidate" && s.Phase == phase).Average(s => s.Cycles);
            var percent = 100 * (candidate / original - 1);
            output.WriteLine($"{profile}/{phase}: {percent:F3}% versus original");
            if (candidate > original * 1.001) failures.Add($"{phase}: {percent:F3}% slower");
        }
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }
}
