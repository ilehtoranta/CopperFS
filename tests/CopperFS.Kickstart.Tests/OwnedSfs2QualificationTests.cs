using System.Text.Json;

namespace CopperFS.Kickstart.Tests;

public sealed class OwnedSfs2TheoryAttribute : TheoryAttribute
{
    public OwnedSfs2TheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("COPPERFS_SFS2_OWNED") != "1" ||
            !File.Exists(Environment.GetEnvironmentVariable("COPPERSHARP_KICKSTART31_ROM")) ||
            !File.Exists(Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")) ||
            !File.Exists(Environment.GetEnvironmentVariable("COPPERFS_GROUPED_REFERENCE")))
            Skip = "Requires owned SFS2 candidate, pinned grouped reference, original SFS and licensed ROM.";
    }
}

public sealed partial class OriginalHandlerCorrectnessTests
{
    [PerformanceProfileTheory] [InlineData("sfs2-trst")]
    public async System.Threading.Tasks.Task ProfileOwnedSfs2WarmWorkload(string profile)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == profile);
        var result = await Run(Path.Combine(Path.GetDirectoryName(path)!, "owned-cpu-profile"), profile, "candidate",
            pair.Candidate, AutomaticImage(manifest, profile), true, "CorrectnessProbe::OwnedBenchmarkSfs2");
        CopperSFS.Check.SfsImageChecker.Check(new MemoryStream(result), reservedBlocks: 1);
    }

    [OwnedSfs2Theory] [InlineData(false)] [InlineData(true)]
    public async System.Threading.Tasks.Task OwnedSfs2SeparatesMountAndSteadyState(bool chip)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == "sfs2-trst");
        var grouped = Environment.GetEnvironmentVariable("COPPERFS_GROUPED_REFERENCE")!;
        var referenceHash = Environment.GetEnvironmentVariable("COPPERFS_GROUPED_REFERENCE_SHA256") ??
            "0733EAA7EFE6B90EFC93800B3209677CAF4830456FC2CB59D07B4BB937ABDE34";
        Assert.Equal(referenceHash, Hash(File.ReadAllBytes(grouped)));
        var originalBuffers = uint.TryParse(Environment.GetEnvironmentVariable("COPPERFS_MATCH_ORIGINAL_BUFFERS"), out var original) ? original : 32u;
        var groupedBuffers = uint.TryParse(Environment.GetEnvironmentVariable("COPPERFS_MATCH_GROUPED_BUFFERS"), out var previous) ? previous : 32u;
        var volumeMiB = int.TryParse(Environment.GetEnvironmentVariable("COPPERFS_OWNED_VOLUME_MIB"), out var size) ? size : 16;
        var fragmented = Environment.GetEnvironmentVariable("COPPERFS_OWNED_FRAGMENTED") == "1";
        var trials = int.TryParse(Environment.GetEnvironmentVariable("COPPERFS_OWNED_TRIALS"), out var repeats) ? repeats : 3;
        Assert.InRange(volumeMiB, 8, 128); Assert.InRange(trials, 1, 3);
        var suffix = $"{(chip ? "chip" : "fast")}-{originalBuffers}-{groupedBuffers}-{volumeMiB}-{fragmented}";
        var root = Path.Combine(Path.GetDirectoryName(path)!, "owned-performance", suffix);
        var samples = new List<object>();
        var initial = ReadMedia.Sfs2(volumeMiB, fragmented);
        foreach (var trial in Enumerable.Range(0, trials))
        foreach (var arm in trial % 2 == 0 ? new[] { "original", "grouped", "candidate" } : new[] { "candidate", "grouped", "original" })
        {
            long started = 0; int reads = 0, writes = 0, flushes = 0;
            var handler = arm == "original" ? pair.Original : arm == "grouped" ? grouped : pair.Candidate;
            var buffers = arm == "original" ? originalBuffers : arm == "grouped" ? groupedBuffers : 32u;
            var result = await Run(root, "sfs2-trst", $"{trial}-{arm}", handler, initial, true,
                "CorrectnessProbe::OwnedBenchmark" + (chip ? "Chip" : "") + "Sfs2",
                observe: (marker, cycle, r, w, f) =>
                {
                    if (marker is 218 or 220 or 222 or 228) { started = cycle; reads = r; writes = w; flushes = f; }
                    if (marker is 219 or 221 or 223 or 229)
                        samples.Add(new { Trial = trial, Arm = arm, Phase = marker == 219 ? "mount" : marker == 221 ? "mutate" : marker == 229 ? "fragmented-read" : "verify",
                            Cycles = cycle - started, Reads = r - reads, Writes = w - writes, Flushes = f - flushes, Buffers = buffers });
                }, probeBuffers: buffers, probeCylinders: (uint)volumeMiB * 8, probeExistingPayload: fragmented);
            CopperSFS.Check.SfsImageChecker.Check(new MemoryStream(result), reservedBlocks: 1);
        }
        Assert.Equal(trials * (fragmented ? 12 : 9), samples.Count);
        File.WriteAllText(Path.Combine(root, "cycles.json"), JsonSerializer.Serialize(new
        {
            OriginalSha256 = Hash(File.ReadAllBytes(pair.Original)), GroupedSha256 = Hash(File.ReadAllBytes(grouped)),
            CandidateSha256 = Hash(File.ReadAllBytes(pair.Candidate)), Samples = samples
        }, Json));
    }
}
