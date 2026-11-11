using System.Text.Json;

namespace CopperFS.Kickstart.Tests;

public sealed class WriteQualificationTheoryAttribute : TheoryAttribute
{
    public WriteQualificationTheoryAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("COPPERSHARP_KICKSTART31_ROM")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COPPERFS_FOREIGN_BASELINE_ROOT")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COPPERFS_FOREIGN_CANDIDATE_ROOT")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COPPERFS_WRITE_RESULTS")))
            Skip = "Supply the ROM, baseline and candidate roots, and write-results directory.";
    }
}

public sealed class WriteProfileTheoryAttribute : TheoryAttribute
{
    public WriteProfileTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("COPPERFS_WRITE_PROFILE") != "1" ||
            !File.Exists(Environment.GetEnvironmentVariable("COPPERSHARP_KICKSTART31_ROM")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COPPERFS_FOREIGN_BASELINE_ROOT")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COPPERFS_FOREIGN_CANDIDATE_ROOT")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COPPERFS_WRITE_RESULTS")))
            Skip = "Opt in to the write profiler and supply the ROM, handler roots and output directory.";
    }
}

public sealed partial class OriginalHandlerCorrectnessTests
{
    [WriteQualificationTheory]
    [InlineData("ffs")]
    [InlineData("sfs")]
    public async Task SweepWriteSchedulingPhase(string profile)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("COPPERFS_WRITE_RESULTS")!, profile, "phase-sweep");
        var name = profile == "ffs" ? "CopperFFS" : "CopperSFS";
        var handlers = new[] {
            Path.Combine(Environment.GetEnvironmentVariable("COPPERFS_FOREIGN_BASELINE_ROOT")!, profile, name),
            Path.Combine(Environment.GetEnvironmentVariable("COPPERFS_FOREIGN_CANDIDATE_ROOT")!, profile, name) };
        var media = profile == "ffs" ? ReadMedia.Ffs() : ReadMedia.Sfs();
        var probe = profile == "ffs" ? "WriteQualificationProbe::FfsAppend" : "WriteQualificationProbe::SfsBulkAppend";
        var addresses = new List<uint>(); var rows = new List<object>();
        foreach (var delay in new uint[] { 0, 200, 500, 1000, 2000, 4000 })
        for (var arm = 0; arm < handlers.Length; arm++)
        {
            var label = $"{delay}-{(arm == 0 ? "baseline" : "candidate")}";
            long start = -1, cycles = -1; int reads = 0, writes = 0, flushes = 0;
            var image = await Run(root, profile, label, handlers[arm], media, true,
                probe, (marker, cycle, r, w, f) =>
                {
                    if (marker == 220) { start = cycle; reads = r; writes = w; flushes = f; }
                    if (marker == 221) { Assert.True(start >= 0); cycles = cycle - start; reads = r - reads; writes = w - writes; flushes = f - flushes; }
                }, probeDelay: delay);
            Assert.True(cycles > 0); Assert.True(writes > 0);
            using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, label, "result.json")));
            var address = result.RootElement.GetProperty("Stage").GetUInt32();
            Assert.True(address != 0 && (address & 65535) == 0); addresses.Add(address);
            rows.Add(new { Delay = delay, Arm = arm == 0 ? "baseline" : "candidate", BusCycles = cycles,
                ReadCommands = reads, WriteCommands = writes, Flushes = flushes, BufferAddress = address, WrittenSha256 = Hash(image) });
        }
        Assert.Single(addresses.Distinct());
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "phase-results.json"), JsonSerializer.Serialize(rows, Json));
    }

    [WriteProfileTheory]
    [InlineData("ffs")] [InlineData("sfs")] [InlineData("sfs2-trst")]
    public async Task ProfileSmartBulkAppend(string profile)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("COPPERFS_WRITE_RESULTS")!, "profile", profile);
        var media = profile == "ffs" ? ReadMedia.Ffs() : profile == "sfs" ? ReadMedia.Sfs() : ReadMedia.Sfs2();
        foreach (var arm in new[] { "baseline", "candidate" })
        {
            var variable = arm == "baseline" ? "COPPERFS_FOREIGN_BASELINE_ROOT" : "COPPERFS_FOREIGN_CANDIDATE_ROOT";
            var handler = Path.Combine(Environment.GetEnvironmentVariable(variable)!, profile, profile == "ffs" ? "CopperFFS" : "CopperSFS");
            Assert.True(File.Exists(handler)); Assert.True(File.Exists(handler + ".map"));
            await Run(root, profile, arm, handler, media, true,
                profile == "ffs" ? "WriteQualificationProbe::FfsAppend" : profile == "sfs" ? "WriteQualificationProbe::SfsBulkAppend" : "WriteQualificationProbe::Sfs2BulkAppend");
            Assert.True(File.Exists(Path.Combine(root, arm, "write-profile.json")));
        }
    }

    [WriteQualificationTheory]
    [InlineData("ffs", false, false)] [InlineData("ffs", true, false)] [InlineData("ffs", true, true)]
    [InlineData("sfs", false, false)] [InlineData("sfs", true, false)] [InlineData("sfs", true, true)]
    [InlineData("sfs2-trst", false, false)] [InlineData("sfs2-trst", true, false)] [InlineData("sfs2-trst", true, true)]
    [InlineData("sfs2-classic", false, false)] [InlineData("sfs2-classic", true, false)] [InlineData("sfs2-classic", true, true)]
    public async Task PairedWritesPreserveContentsAcrossColdBoot(string profile, bool append, bool bulk)
    {
        var baselineRoot = Environment.GetEnvironmentVariable("COPPERFS_FOREIGN_BASELINE_ROOT")!;
        var candidateRoot = Environment.GetEnvironmentVariable("COPPERFS_FOREIGN_CANDIDATE_ROOT")!;
        var root = Path.Combine(Environment.GetEnvironmentVariable("COPPERFS_WRITE_RESULTS")!, profile,
            bulk ? "bulk-append" : append ? "append" : "overwrite");
        var filename = profile == "ffs" ? "CopperFFS" : "CopperSFS";
        var handlerProfile = profile == "sfs2-classic" ? "sfs2-trst" : profile;
        var handlers = new[] { Path.Combine(baselineRoot, handlerProfile, filename), Path.Combine(candidateRoot, handlerProfile, filename) };
        foreach (var handler in handlers) Assert.True(File.Exists(handler), $"Missing handler: {handler}");
        var classic = profile is "sfs" or "sfs2-classic";
        var media = profile == "ffs" ? ReadMedia.Ffs() : classic ? ReadMedia.Sfs() : ReadMedia.Sfs2();
        var stem = profile == "ffs" ? "Ffs" : classic ? "Sfs" : "Sfs2";
        var mode = bulk ? "BulkAppend" : append ? "Append" : "Overwrite";
        var rows = new List<object>();
        var addresses = new List<uint>();
        for (var trial = 0; trial < 3; trial++)
        for (var arm = 0; arm < handlers.Length; arm++)
        {
            var label = $"{(arm == 0 ? "baseline" : "candidate")}-{trial}";
            long start = -1, cycles = -1; int reads = 0, writes = 0, flushes = 0;
            var written = await Run(root, profile, label, handlers[arm], media, true,
                "WriteQualificationProbe::" + stem + mode,
                (marker, cycle, r, w, f) =>
                {
                    if (marker == 220) { start = cycle; reads = r; writes = w; flushes = f; }
                    if (marker == 221)
                    {
                        Assert.True(start >= 0); cycles = cycle - start;
                        reads = r - reads; writes = w - writes; flushes = f - flushes;
                    }
                });
            Assert.True(cycles > 0); Assert.True(writes > 0);
            using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, label, "result.json")));
            var address = result.RootElement.GetProperty("Stage").GetUInt32();
            Assert.True(address != 0 && (address & 65535) == 0); addresses.Add(address);
            // A fresh guest verifies the changed region, an unchanged region,
            // and the final size; its read-only mount must not change media.
            await Run(root, profile, label + "-verify", handlers[arm], written, false,
                "WriteQualificationProbe::" + stem + "Verify" + mode);
            rows.Add(new { Arm = arm == 0 ? "baseline" : "candidate", Trial = trial,
                BusCycles = cycles, ReadCommands = reads, WriteCommands = writes, Flushes = flushes,
                BufferAddress = address, HandlerSha256 = Hash(File.ReadAllBytes(handlers[arm])),
                InitialSha256 = Hash(media), WrittenSha256 = Hash(written) });
        }
        Assert.Single(addresses.Distinct());
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "write-results.json"), JsonSerializer.Serialize(rows, Json));
    }
}
