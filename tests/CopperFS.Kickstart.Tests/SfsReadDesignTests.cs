using System.Text.Json;
using CopperSFS.Check;

namespace CopperFS.Kickstart.Tests;

public sealed class SfsOverwriteProfileTheoryAttribute : TheoryAttribute
{
    public SfsOverwriteProfileTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("COPPERFS_OVERWRITE_PROFILE") != "1" ||
            !File.Exists(Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")) ||
            !File.Exists(Environment.GetEnvironmentVariable("COPPERSHARP_KICKSTART31_ROM")))
            Skip = "Requires the SFS overwrite profile, qualification manifest and licensed ROM.";
    }
}

public sealed partial class OriginalHandlerCorrectnessTests
{
    [SfsOverwriteProfileTheory]
    [InlineData("sfs")]
    public async System.Threading.Tasks.Task ProfileSfsOverwriteAndFlush(string profile)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == profile);
        var initial = File.ReadAllBytes(manifest.ClassicBase);
        var root = Path.Combine(Path.GetDirectoryName(path)!, "overwrite-profile");
        var written = await Run(root, profile, "candidate", pair.Candidate, initial, true, "LargeWriteProbe::Sfs");
        var file = SfsImageChecker.Check(new MemoryStream(written), reservedBlocks: 2).Entries.Single(e => e.Path == "/Large");
        Assert.Equal(Hash(Enumerable.Repeat((byte)0xD4, 98304).ToArray()), file.Sha256);
    }
    [SfsGroupedTheory]
    [InlineData(8, false)] [InlineData(8, true)] [InlineData(128, false)] [InlineData(128, true)]
    [InlineData(16, false)]
    public async System.Threading.Tasks.Task SfsReadPlacementAndCacheBudget(int mib, bool fragmented)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == "sfs");
        var baseline = Environment.GetEnvironmentVariable("COPPERFS_SFS_REFERENCE")!;
        var initial = ReadMedia.Sfs(mib, fragmented);
        var seed = SfsImageChecker.Check(new MemoryStream(initial), reservedBlocks: 1);
        Assert.Equal(Hash(ReadMedia.Payload), seed.Entries.Single(x => x.Path == "/Payload").Sha256);
        var matched = mib == 16;
        var root = Path.Combine(Path.GetDirectoryName(path)!, "read-matrix", $"{mib}-{fragmented}");
        var samples = new List<object>();
        foreach (var chip in new[] { false, true })
        foreach (var arm in new[] { "original", "baseline", "candidate" })
        {
            // At 32 buffers these two shared-pool builds retain 126,008
            // bytes. Original SFS retains 156,480. Each AddBuffers page costs
            // 536 bytes including Exec rounding (57 pages match within 80 B).
            uint add = matched && arm != "original" ? 57u : 0u;
            var label = (chip ? "chip-" : "fast-") + arm;
            long start = 0; int reads = 0, writes = 0, flushes = 0;
            var result = await Run(root, "sfs", label, arm == "original" ? pair.Original :
                arm == "baseline" ? baseline : pair.Candidate, initial, true,
                "CorrectnessProbe::ReadPlacement" + (chip ? "Chip" : "") + "Sfs",
                observe: (mark, cycle, r, w, f) =>
                {
                    if (mark is 218 or 220 or 222)
                    { start = cycle; reads = r; writes = w; flushes = f; }
                    if (mark is 219 or 221 or 223)
                        samples.Add(new { Arm = arm, Chip = chip, Phase = mark == 219 ? "mount" :
                            mark == 221 ? "cold-read" : "warm-read",
                            Cycles = cycle - start, Reads = r - reads, Writes = w - writes, Flushes = f - flushes });
                }, probeBuffers: 32, probeCylinders: (uint)mib * 8, probeExistingPayload: true, extraBuffers: add);
            var report = SfsImageChecker.Check(new MemoryStream(result), reservedBlocks: 1);
            Assert.Equal(Hash(ReadMedia.Payload), report.Entries.Single(x => x.Path == "/Payload").Sha256);
        }
        File.WriteAllText(Path.Combine(root, "cycles.json"), JsonSerializer.Serialize(samples, Json));
    }
}
