using System.Text.Json;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    [HandlerSizePerformanceTheory]
    [InlineData("ffs", false)] [InlineData("ofs", false)]
    [InlineData("ffs", true)] [InlineData("ofs", true)]
    public async System.Threading.Tasks.Task MeasureFfsMountAndCachedReads(string profile, bool chip)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_SIZE_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<SizeManifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == profile);
        var allowance = ClassicCycleRatio(profile, manifest.ClassicAutoTrimComparison);
        var image = CopperStart.Floppy.Native.Tests.DosDiskImage.Create(profile == "ffs", includeLargeFile: false,
            blockCount: 32768, firstFileBlock: 2, payload: [1, 2, 3], volumeName: "Correctness").Bytes;
        var root = Path.Combine(Path.GetDirectoryName(path)!, chip ? "mount-chip" : "mount-fast", profile);
        var entry = "CorrectnessProbe::OwnedBenchmark" + (chip ? "Chip" : "") + (profile == "ffs" ? "Ffs" : "Ofs");
        var samples = new List<SizeSample>();
        var memory = new List<object>();
        foreach (var trial in Enumerable.Range(0, 3))
        foreach (var arm in trial % 2 == 0 ? new[] { "baseline", "candidate" } : new[] { "candidate", "baseline" })
        {
            long start = 0; int reads = 0, writes = 0, flushes = 0;
            var label = $"{trial}-{arm}";
            var result = await Run(root, profile, label, arm == "baseline" ? pair.Baseline : pair.Candidate, image, true,
                entry, (marker, cycle, r, w, f) =>
                {
                    if (marker is 218 or 220 or 222 or 228) { start = cycle; reads = r; writes = w; flushes = f; }
                    if (marker is 219 or 221 or 223 or 229)
                        samples.Add(new(trial, arm, marker == 219 ? "mount" : marker == 221 ? "mutate" :
                            marker == 223 ? "verify" : "cached-read", cycle - start, r - reads, w - writes, f - flushes));
                });
            DosImageInspection.Check(result, arm == "candidate");
            using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, label, "result.json")));
            var ram = report.RootElement.GetProperty("ProbeData");
            memory.Add(new { Trial = trial, Arm = arm, MountBytes = ram[0].GetInt64() - ram[1].GetInt64(),
                AfterMutationBytes = ram[0].GetInt64() - ram[2].GetInt64(), AfterVerifyBytes = ram[0].GetInt64() - ram[3].GetInt64() });
        }
        File.WriteAllText(Path.Combine(root, "cycles.json"), JsonSerializer.Serialize(new { Samples = samples, Memory = memory,
            BaselineSha256 = Hash(File.ReadAllBytes(pair.Baseline)), CandidateSha256 = Hash(File.ReadAllBytes(pair.Candidate)),
            MaximumCycleRatio = allowance }, Json));
        var cached = samples.Where(s => s.Phase == "cached-read").ToArray();
        Assert.Equal(6, cached.Length);
        Assert.True(cached.Where(s => s.Arm == "candidate").Average(s => s.Cycles) <=
            cached.Where(s => s.Arm == "baseline").Average(s => s.Cycles) * allowance, $"See {root}/cycles.json");
    }
}
