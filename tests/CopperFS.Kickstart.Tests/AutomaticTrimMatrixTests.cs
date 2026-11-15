using System.Text.Json;
using CopperSFS.Check;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    [AutomaticTrimTheory]
    [InlineData("ofs", "Large")] [InlineData("ffs", "Large")] [InlineData("sfs", "Large")]
    [InlineData("sfs2-csun", "Large")] [InlineData("sfs2-trst", "Large")]
    [InlineData("ofs", "Fragmented")] [InlineData("ffs", "Fragmented")] [InlineData("sfs", "Fragmented")]
    [InlineData("sfs2-csun", "Fragmented")] [InlineData("sfs2-trst", "Fragmented")]
    [InlineData("ofs", "Warm")] [InlineData("ffs", "Warm")] [InlineData("sfs", "Warm")]
    [InlineData("sfs2-csun", "Warm")] [InlineData("sfs2-trst", "Warm")]
    public async System.Threading.Tasks.Task AutomaticTrimBoundedIdleTurnsReclaimFreedData(string profile, string scenario)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_AUTOTRIM_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == profile);
        Assert.Equal(pair.OriginalSha256, Hash(File.ReadAllBytes(pair.Original)));
        var hook = new AutomaticDiscardHook(false);
        var root = Path.Combine(Path.GetDirectoryName(path)!, "trim-matrix", profile, scenario);
        var kind = profile == "ofs" ? "Ofs" : profile == "ffs" ? "Ffs" : profile == "sfs" ? "Sfs" : "Sfs2";
        var markers = new List<object>();
        int baseScsi = 0, baseDiscards = 0, scsi = 0, discards = 0;
        long baseBytes = 0, bytes = 0, deleteStart = 0, deleteCycles = 0;
        var result = await Run(root, profile, "candidate", pair.Candidate, AutomaticImage(manifest, profile), true,
            "CorrectnessProbe::Auto" + scenario + kind, (marker, cycle, _, _, _) =>
            {
                if (marker == 225)
                { baseScsi = hook.ScsiCommands; baseDiscards = hook.Discards; baseBytes = hook.DiscardBytes; }
                if (marker == 232) deleteStart = cycle;
                if (marker == 221 && deleteStart != 0) deleteCycles = cycle - deleteStart;
                if (marker is 225 or 232 or 221 or 230 or 231 or 222)
                    markers.Add(new { Marker = marker, Cycle = cycle, hook.ScsiCommands, hook.Discards,
                        hook.DiscardBytes, hook.ReadCommands, hook.WriteCommands, hook.Flushes });
                if (marker == 222)
                { scsi = hook.ScsiCommands - baseScsi; discards = hook.Discards - baseDiscards; bytes = hook.DiscardBytes - baseBytes; }
            }, deviceHook: hook);
        var expected = scenario == "Large" ? 36 * 65536 : 65536;
        File.WriteAllText(Path.Combine(root, "trim-matrix.json"), JsonSerializer.Serialize(new
        {
            Profile = profile, Scenario = scenario, BufferMemory = scenario == "Fragmented" ? "Chip" : "Fast",
            CandidateSha256 = Hash(File.ReadAllBytes(pair.Candidate)), ForegroundDeleteCycles = deleteCycles,
            ExpectedFreedDataBytes = expected, ReclaimedBytes = bytes, Discards = discards,
            DiscoveryCommands = scsi - discards, Markers = markers
        }, Json));
        Assert.True(bytes >= expected, $"Reclaimed {bytes} bytes; expected at least {expected}.");
        // This transport advertises one descriptor. Every extra SCSI command
        // must belong to the single three-command capability probe per mount.
        Assert.Equal(3, scsi - discards);
        if (profile.StartsWith("sfs")) SfsImageChecker.Check(new MemoryStream(result), reservedBlocks: profile == "sfs" ? 2u : 1u);
        else DosImageInspection.Check(result, true);
        await Run(root, profile, "original-reader", pair.Original, result, false);
    }
}
