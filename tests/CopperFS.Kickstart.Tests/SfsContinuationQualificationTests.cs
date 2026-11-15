using System.Text.Json;
using CopperSFS.Check;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    [SfsGroupedTheory]
    [InlineData("sfs")]
    public async System.Threading.Tasks.Task SfsContinuationCommitCutsRecoverWithOriginal(string profile)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == profile);
        var initial = File.ReadAllBytes(manifest.ClassicBase);
        var root = Path.Combine(Path.GetDirectoryName(path)!, "continuation-cuts");
        var trace = new DurableTrace(initial); var overwriteStart = 0;
        await Run(root, profile, "complete", pair.Candidate, initial, true, "LargeWriteProbe::Sfs", storage: trace,
            observe: (mark, _, _, _, _) => { if (mark == 222) overwriteStart = trace.SectorCuts + trace.FlushCuts; });
        Assert.True(overwriteStart > 0);
        var cuts = trace.Prefixes.Where(p => p.Cut > overwriteStart).SelectMany(p => new[] { p.Cut - 1, p.Cut })
            .Append(overwriteStart + 1).Distinct().Order().ToArray();
        Assert.True(cuts.Length >= 8);
        var before = Hash(Enumerable.Repeat((byte)0xA7, 98304).ToArray());
        var after = Hash(Enumerable.Repeat((byte)0xD4, 98304).ToArray());
        foreach (var cut in cuts)
        {
            var label = "cut-" + cut;
            await Assert.ThrowsAsync<PowerCut>(() => Run(root, profile, label, pair.Candidate, initial, true,
                "LargeWriteProbe::Sfs", storage: new DurableTrace(initial, cut)));
            var image = File.ReadAllBytes(Path.Combine(root, label, "disk.hdf"));
            foreach (var arm in new[] { "original", "candidate" })
            {
                var recovered = await Run(root, profile, label + "-" + arm, arm == "original" ? pair.Original : pair.Candidate,
                    image, true, "OriginalTrstProbe::Recover");
                var file = SfsImageChecker.Check(new MemoryStream(recovered), reservedBlocks: 2).Entries.Single(e => e.Path == "/Large");
                Assert.Equal(98304ul, file.Size);
                Assert.True(file.Sha256 == before || file.Sha256 == after, $"Partial overwrite at cut {cut}, {arm}");
            }
        }
        output.WriteLine($"{cuts.Length} overwrite cuts, recovered with both original SFS and the candidate.");
    }

    [SfsGroupedTheory]
    [InlineData(false)] [InlineData(true)]
    public async System.Threading.Tasks.Task SfsContinuationComparisonIncludesExplicitCommits(bool chip)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == "sfs");
        var previous = Environment.GetEnvironmentVariable("COPPERFS_SFS_REFERENCE")!;
        Assert.True(File.Exists(previous));
        var initial = File.ReadAllBytes(manifest.ClassicBase);
        Assert.Equal(manifest.ClassicBaseSha256, Hash(initial));
        var root = Path.Combine(Path.GetDirectoryName(path)!, chip ? "continuation-chip" : "continuation-fast");
        var samples = new List<object>();
        foreach (var trial in Enumerable.Range(0, 3))
        foreach (var arm in trial % 2 == 0 ? new[] { "original", "previous", "candidate" } : new[] { "candidate", "previous", "original" })
        {
            long start = 0, totalStart = 0; int reads = 0, writes = 0, flushes = 0, totalReads = 0, totalWrites = 0, totalFlushes = 0;
            var result = await Run(root, "sfs", $"{trial}-{arm}", arm == "original" ? pair.Original : arm == "previous" ? previous : pair.Candidate,
                initial, true, chip ? "LargeWriteProbe::SfsChip" : "LargeWriteProbe::Sfs", observe: (mark, cycle, r, w, f) =>
                {
                    if (mark == 220) { totalStart = cycle; totalReads = r; totalWrites = w; totalFlushes = f; }
                    if (mark is 220 or 222 or 224) { start = cycle; reads = r; writes = w; flushes = f; }
                    if (mark is 221 or 223 or 225) samples.Add(new { Trial = trial, Arm = arm, Phase = mark == 221 ? "grow" : mark == 223 ? "overwrite" : "verify",
                        Cycles = cycle - start, Reads = r - reads, Writes = w - writes, Flushes = f - flushes });
                    if (mark == 225) samples.Add(new { Trial = trial, Arm = arm, Phase = "total", Cycles = cycle - totalStart,
                        Reads = r - totalReads, Writes = w - totalWrites, Flushes = f - totalFlushes });
                });
            var report = SfsImageChecker.Check(new MemoryStream(result), reservedBlocks: 2);
            var file = report.Entries.Single(e => e.Path == "/Large");
            Assert.Equal(98304ul, file.Size);
            Assert.Equal(Hash(Enumerable.Repeat((byte)0xD4, 98304).ToArray()), file.Sha256);
        }
        File.WriteAllText(Path.Combine(root, "cycles.json"), JsonSerializer.Serialize(new
        { Original = Hash(File.ReadAllBytes(pair.Original)), Previous = Hash(File.ReadAllBytes(previous)), Candidate = Hash(File.ReadAllBytes(pair.Candidate)), Samples = samples }, Json));
    }
}
