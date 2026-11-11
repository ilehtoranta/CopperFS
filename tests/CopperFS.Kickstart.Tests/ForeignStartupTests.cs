using System.Text.Json;
using System.Buffers.Binary;
using CopperStart.Floppy.Native.Tests;
using CopperSFS.Check;
using CopperDisk;

namespace CopperFS.Kickstart.Tests;

public sealed class ForeignStartupTheoryAttribute : TheoryAttribute
{
    public ForeignStartupTheoryAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("COPPERSHARP_KICKSTART31_ROM")) ||
            !File.Exists(Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COPPERFS_FOREIGN_BASELINE_ROOT")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COPPERFS_FOREIGN_CANDIDATE_ROOT")))
            Skip = "Supply the ROM, correctness manifest and baseline/candidate handler roots.";
    }
}
public sealed partial class OriginalHandlerCorrectnessTests
{
    private class StartupTrace : WritableHook
    {
        public readonly List<string> Operations = [];
        public override void Read(FileStream backing, long offset, Span<byte> destination)
        {
            base.Read(backing, offset, destination);
            Operations.Add($"R:{offset}:{destination.Length}");
        }
        public override void Write(FileStream backing, long offset, ReadOnlySpan<byte> source)
        {
            Operations.Add($"W:{offset}:{source.Length}:{Hash(source.ToArray())}");
            base.Write(backing, offset, source);
        }
        public override void Flush(FileStream backing)
        {
            Operations.Add("F");
            base.Flush(backing);
        }
    }

    private sealed class DiscardTrace : StartupTrace, IAmigaHardfileDiscardIoHook
    {
        public bool SupportsDiscard => true;
        public uint MaximumDiscardSectors => 4096;
        public int DiscardCount;
        public void Discard(FileStream backing, long offset, int bytes)
        {
            Operations.Add($"D:{offset}:{bytes}"); DiscardCount++;
            backing.Position = offset;
            var zero = new byte[Math.Min(bytes, 65536)];
            while (bytes != 0) { var count = Math.Min(bytes, zero.Length); backing.Write(zero, 0, count); bytes -= count; }
        }
    }

    [ForeignStartupTheory]
    [InlineData("ffs", "ffs", 0)] [InlineData("ffs", "ffs", 1)] [InlineData("ffs", "ffs", 2)]
    [InlineData("sfs", "sfs", 0)] [InlineData("sfs", "sfs", 1)] [InlineData("sfs", "sfs", 2)]
    [InlineData("sfs2-trst", "sfs2-trst", 0)] [InlineData("sfs2-trst", "sfs2-trst", 1)] [InlineData("sfs2-trst", "sfs2-trst", 2)]
    [InlineData("sfs2-trst", "sfs", 0)] [InlineData("sfs2-trst", "sfs", 1)] [InlineData("sfs2-trst", "sfs", 2)]
    public async Task SupportedTrimPreservesResultsRangesAndData(string handlerProfile, string mediaProfile, int flags)
    {
        var manifestPath = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var baselineRoot = Environment.GetEnvironmentVariable("COPPERFS_FOREIGN_BASELINE_ROOT")!;
        var candidateRoot = Environment.GetEnvironmentVariable("COPPERFS_FOREIGN_CANDIDATE_ROOT")!;
        var root = Path.Combine(Path.GetDirectoryName(manifestPath)!, "supported-trim", handlerProfile + "-" + mediaProfile + "-" + flags);
        var name = handlerProfile == "ffs" ? "CopperFFS" : "CopperSFS";
        var media = mediaProfile == "ffs" ? ReadMedia.Ffs() : mediaProfile == "sfs" ? ReadMedia.Sfs() : ReadMedia.Sfs2();
        var stem = mediaProfile == "ffs" ? "Ffs" : mediaProfile == "sfs" ? "Sfs" : "Sfs2";
        var entry = "TrimQualificationProbe::" + stem + (flags == 0 ? "Execute" : flags == 1 ? "DryRun" : "Query");
        var traceA = new DiscardTrace(); var traceB = new DiscardTrace();
        var a = await Run(root, mediaProfile, "baseline", Path.Combine(baselineRoot, handlerProfile, name),
            media, true, guestEntry: entry, storage: traceA);
        var b = await Run(root, mediaProfile, "candidate", Path.Combine(candidateRoot, handlerProfile, name),
            media, true, guestEntry: entry, storage: traceB);
        using var before = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "baseline", "result.json")));
        using var after = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "candidate", "result.json")));
        Assert.Equal(uint.MaxValue, before.RootElement.GetProperty("Stage").GetUInt32());
        Assert.Equal(0u, before.RootElement.GetProperty("IoErr").GetUInt32());
        foreach (var field in new[] { "Stage", "IoErr", "ProbeData" })
            Assert.Equal(before.RootElement.GetProperty(field).GetRawText(), after.RootElement.GetProperty(field).GetRawText());
        Assert.Equal(traceA.Operations, traceB.Operations);
        Assert.Equal(Hash(a), Hash(b));
        if (flags == 0) Assert.True(traceA.DiscardCount > 0); else Assert.Equal(0, traceA.DiscardCount);
    }

    [ForeignStartupTheory]
    [InlineData("ffs", "ffs")] [InlineData("sfs", "sfs")]
    [InlineData("sfs2-trst", "sfs2-trst")] [InlineData("sfs2-trst", "sfs")]
    public async Task NotificationLookupPreservesResultAndDeviceOrdering(string handlerProfile, string mediaProfile)
    {
        var manifestPath = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var baselineRoot = Environment.GetEnvironmentVariable("COPPERFS_FOREIGN_BASELINE_ROOT")!;
        var candidateRoot = Environment.GetEnvironmentVariable("COPPERFS_FOREIGN_CANDIDATE_ROOT")!;
        var root = Path.Combine(Path.GetDirectoryName(manifestPath)!, "notify-qualification", handlerProfile + "-" + mediaProfile);
        var name = handlerProfile == "ffs" ? "CopperFFS" : "CopperSFS";
        var media = mediaProfile == "ffs" ? ReadMedia.Ffs() : mediaProfile == "sfs" ? ReadMedia.Sfs() : ReadMedia.Sfs2();
        var stem = mediaProfile == "ffs" ? "Ffs" : mediaProfile == "sfs" ? "Sfs" : "Sfs2";
        var traceA = new StartupTrace(); var traceB = new StartupTrace();
        var a = await Run(root, mediaProfile, "baseline", Path.Combine(baselineRoot, handlerProfile, name),
            media, true, guestEntry: "NotificationQualificationProbe::" + stem, storage: traceA);
        var b = await Run(root, mediaProfile, "candidate", Path.Combine(candidateRoot, handlerProfile, name),
            media, true, guestEntry: "NotificationQualificationProbe::" + stem, storage: traceB);
        using var before = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "baseline", "result.json")));
        using var after = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "candidate", "result.json")));
        Assert.Equal(before.RootElement.GetProperty("Stage").GetUInt32(), after.RootElement.GetProperty("Stage").GetUInt32());
        Assert.Equal(before.RootElement.GetProperty("IoErr").GetUInt32(), after.RootElement.GetProperty("IoErr").GetUInt32());
        Assert.Equal(mediaProfile == "ffs" ? uint.MaxValue : 0u, before.RootElement.GetProperty("Stage").GetUInt32());
        Assert.Equal(mediaProfile == "ffs" ? 0u : (uint)Amiga.DOS.Error.DiskNotValidated, before.RootElement.GetProperty("IoErr").GetUInt32());
        Assert.Equal(traceA.Operations, traceB.Operations);
        Assert.Equal(Hash(a), Hash(b));
    }

    [ForeignStartupTheory]
    [InlineData("ffs", "ffs", false)] [InlineData("ffs", "ffs", true)]
    [InlineData("sfs", "sfs", false)] [InlineData("sfs", "sfs", true)]
    [InlineData("sfs2-trst", "sfs2-trst", false)] [InlineData("sfs2-trst", "sfs2-trst", true)]
    [InlineData("sfs2-trst", "sfs", false)] [InlineData("sfs2-trst", "sfs", true)]
    public async Task TrimPacketPreservesResultsAndDeviceOrdering(string handlerProfile, string mediaProfile, bool dryRun)
    {
        var manifestPath = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var baselineRoot = Environment.GetEnvironmentVariable("COPPERFS_FOREIGN_BASELINE_ROOT")!;
        var candidateRoot = Environment.GetEnvironmentVariable("COPPERFS_FOREIGN_CANDIDATE_ROOT")!;
        var root = Path.Combine(Path.GetDirectoryName(manifestPath)!, "trim-qualification",
            handlerProfile + "-" + mediaProfile + (dryRun ? "-dry" : "-query"));
        var name = handlerProfile == "ffs" ? "CopperFFS" : "CopperSFS";
        var media = mediaProfile == "ffs" ? ReadMedia.Ffs() : mediaProfile == "sfs" ? ReadMedia.Sfs() : ReadMedia.Sfs2();
        var stem = mediaProfile == "ffs" ? "Ffs" : mediaProfile == "sfs" ? "Sfs" : "Sfs2";
        var entry = "TrimQualificationProbe::" + stem + (dryRun ? "DryRun" : "Query");
        var beforeTrace = new StartupTrace(); var afterTrace = new StartupTrace();
        var before = await Run(root, mediaProfile, "baseline", Path.Combine(baselineRoot, handlerProfile, name),
            media, true, guestEntry: entry, storage: beforeTrace);
        var after = await Run(root, mediaProfile, "candidate", Path.Combine(candidateRoot, handlerProfile, name),
            media, true, guestEntry: entry, storage: afterTrace);
        using var oldResult = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "baseline", "result.json")));
        using var newResult = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "candidate", "result.json")));
        foreach (var field in new[] { "Stage", "IoErr", "ProbeData" })
            Assert.Equal(oldResult.RootElement.GetProperty(field).GetRawText(), newResult.RootElement.GetProperty(field).GetRawText());
        Assert.Equal(beforeTrace.Operations, afterTrace.Operations);
        Assert.Equal(Hash(before), Hash(after));
    }

    [ForeignStartupTheory]
    [InlineData("ffs")]
    public async Task FfsRejectsUnsupportedWideResizeWithOriginalPacketResult(string profile)
    {
        var manifestPath = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var baselineRoot = Environment.GetEnvironmentVariable("COPPERFS_FOREIGN_BASELINE_ROOT")!;
        var candidateRoot = Environment.GetEnvironmentVariable("COPPERFS_FOREIGN_CANDIDATE_ROOT")!;
        var root = Path.Combine(Path.GetDirectoryName(manifestPath)!, "ffs-unsupported-resize");
        var media = ReadMedia.Ffs();
        var beforeTrace = new StartupTrace(); var afterTrace = new StartupTrace();
        var before = await Run(root, profile, "baseline", Path.Combine(baselineRoot, profile, "CopperFFS"),
            media, true, guestEntry: "CorrectnessProbe::FfsResize64Status", storage: beforeTrace);
        var after = await Run(root, profile, "candidate", Path.Combine(candidateRoot, profile, "CopperFFS"),
            media, true, guestEntry: "CorrectnessProbe::FfsResize64Status", storage: afterTrace);
        using var oldResult = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "baseline", "result.json")));
        using var newResult = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "candidate", "result.json")));
        Assert.Equal(0u, oldResult.RootElement.GetProperty("Stage").GetUInt32());
        Assert.Equal((uint)Amiga.DOS.Error.ActionNotKnown, oldResult.RootElement.GetProperty("IoErr").GetUInt32());
        Assert.Equal(oldResult.RootElement.GetProperty("Stage").GetUInt32(), newResult.RootElement.GetProperty("Stage").GetUInt32());
        Assert.Equal(oldResult.RootElement.GetProperty("IoErr").GetUInt32(), newResult.RootElement.GetProperty("IoErr").GetUInt32());
        Assert.Equal(beforeTrace.Operations, afterTrace.Operations);
        Assert.Equal(Hash(before), Hash(after));
    }

    [ForeignStartupTheory]
    [InlineData("sfs2-trst")]
    public async Task Sfs2HandlerPreservesClassicSfsMutationAndCleanHandoffs(string profile)
    {
        var manifestPath = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath))!;
        var baselineRoot = Environment.GetEnvironmentVariable("COPPERFS_FOREIGN_BASELINE_ROOT")!;
        var candidateRoot = Environment.GetEnvironmentVariable("COPPERFS_FOREIGN_CANDIDATE_ROOT")!;
        var root = Path.Combine(Path.GetDirectoryName(manifestPath)!, "sfs2-classic-handoffs");
        var media = File.ReadAllBytes(manifest.ClassicBase);
        Assert.Equal(manifest.ClassicBaseSha256, Hash(media));
        var handlers = new[] { Path.Combine(baselineRoot, profile, "CopperSFS"),
            Path.Combine(candidateRoot, profile, "CopperSFS") };
        string[]? expected = null;
        for (var writer = 0; writer < handlers.Length; writer++)
        {
            var written = await Run(root, "sfs", $"write-{writer}", handlers[writer], media, true);
            var report = SfsImageChecker.Check(new MemoryStream(written), reservedBlocks: 2);
            var files = report.Entries.Where(e => !e.Directory)
                .Select(e => $"{e.Path}:{e.Size}:{e.Sha256}").Order().ToArray();
            if (expected == null) expected = files; else Assert.Equal(expected, files);
            for (var reader = 0; reader < handlers.Length; reader++)
                await Run(root, "sfs", $"write-{writer}-read-{reader}", handlers[reader], written, false);
        }
    }

    [ForeignStartupTheory]
    [InlineData("ffs", "sfs")]
    [InlineData("ffs", "sfs2-trst")]
    [InlineData("sfs", "ffs")]
    [InlineData("sfs2-trst", "ffs")]
    [InlineData("sfs2-trst", "sfs")]
    public async Task ForeignFormatStartupPreservesResultAndDeviceOrdering(string handlerProfile, string mediaProfile)
    {
        var manifestPath = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath))!;
        var candidateRoot = Environment.GetEnvironmentVariable("COPPERFS_FOREIGN_CANDIDATE_ROOT");
        var baselineRoot = Environment.GetEnvironmentVariable("COPPERFS_FOREIGN_BASELINE_ROOT");
        Assert.True(Directory.Exists(candidateRoot), "Set COPPERFS_FOREIGN_CANDIDATE_ROOT.");
        Assert.True(Directory.Exists(baselineRoot), "Set COPPERFS_FOREIGN_BASELINE_ROOT.");
        var name = handlerProfile == "ffs" ? "CopperFFS" : "CopperSFS";
        var baseline = Path.Combine(baselineRoot!, handlerProfile, name);
        var candidate = Path.Combine(candidateRoot!, handlerProfile, name);
        Assert.True(File.Exists(baseline)); Assert.True(File.Exists(candidate));
        var media = mediaProfile == "ffs" ? DosDiskImage.Create(true, includeLargeFile: false,
            blockCount: 32768u, firstFileBlock: 2, payload: [1, 2, 3], volumeName: "Correctness").Bytes
            : mediaProfile == "sfs" ? File.ReadAllBytes(manifest.ClassicBase) : ReadMedia.Sfs2();
        if (mediaProfile == "sfs") Assert.Equal(manifest.ClassicBaseSha256, Hash(media));
        var entry = mediaProfile == "ffs" ? "CorrectnessProbe::ForeignFfs" :
            mediaProfile == "sfs" ? "CorrectnessProbe::ForeignSfs" : "CorrectnessProbe::ForeignSfs2";
        var root = Path.Combine(Path.GetDirectoryName(manifestPath)!, "foreign", handlerProfile + "-on-" + mediaProfile);
        var beforeTrace = new StartupTrace(); var afterTrace = new StartupTrace();
        var before = await Run(root, mediaProfile, "baseline", baseline, media, true, guestEntry: entry, storage: beforeTrace);
        var after = await Run(root, mediaProfile, "candidate", candidate, media, true, guestEntry: entry, storage: afterTrace);
        using var beforeResult = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "baseline", "result.json")));
        using var afterResult = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "candidate", "result.json")));
        Assert.Equal(beforeResult.RootElement.GetProperty("Stage").GetUInt32(), afterResult.RootElement.GetProperty("Stage").GetUInt32());
        // The SFS2 handler also accepts classic SFS, so eliminating its
        // classic runtime engine would change an existing supported path.
        if (handlerProfile == "sfs2-trst" && mediaProfile == "sfs")
            Assert.Equal(1u, beforeResult.RootElement.GetProperty("Stage").GetUInt32());
        if (handlerProfile == "ffs")
        {
            Assert.Equal(0u, afterResult.RootElement.GetProperty("Stage").GetUInt32());
            Assert.Equal((uint)Amiga.DOS.Error.ObjectWrongType, afterResult.RootElement.GetProperty("IoErr").GetUInt32());
            Assert.DoesNotContain(afterTrace.Operations, op => op.StartsWith("W:") || op == "F");
            Assert.Equal(Hash(media), Hash(after));
        }
        else
        {
            Assert.Equal(beforeResult.RootElement.GetProperty("IoErr").GetUInt32(), afterResult.RootElement.GetProperty("IoErr").GetUInt32());
            Assert.Equal(beforeTrace.Operations, afterTrace.Operations);
            Assert.Equal(Hash(before), Hash(after));
        }
    }
    [ForeignStartupTheory]
    [InlineData("ffs")]
    [InlineData("sfs")]
    [InlineData("sfs2-trst")]
    public async Task ForeignDirtySfs2StartupPreservesRecoveryOrdering(string handlerProfile)
    {
        var manifestPath = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath))!;
        var candidateRoot = Environment.GetEnvironmentVariable("COPPERFS_FOREIGN_CANDIDATE_ROOT");
        var baselineRoot = Environment.GetEnvironmentVariable("COPPERFS_FOREIGN_BASELINE_ROOT");
        Assert.True(Directory.Exists(candidateRoot)); Assert.True(Directory.Exists(baselineRoot));
        var root = Path.Combine(Path.GetDirectoryName(manifestPath)!, "foreign-dirty", handlerProfile);
        var initial = ReadMedia.Sfs2();
        var markerOffset = (BinaryPrimitives.ReadUInt32BigEndian(initial.AsSpan(104)) + 2L) * 512;
        byte[]? interrupted = null;
        void Capture(FileStream disk)
        {
            Span<byte> header = stackalloc byte[16];
            disk.Position = markerOffset; disk.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32BigEndian(header) != 0x54524641u) return;
            var head = BinaryPrimitives.ReadUInt32BigEndian(header[12..]);
            if (head == 0) return;
            disk.Position = (long)head * 512; disk.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32BigEndian(header) != 0x4353554Eu) return;
            interrupted ??= new byte[initial.Length]; disk.Position = 0; disk.ReadExactly(interrupted);
        }
        var writer = manifest.Profiles.Single(p => p.Name == "sfs2-csun").Candidate;
        await Run(root, "sfs2-csun", "writer", writer, initial, true, afterFlush: Capture);
        Assert.NotNull(interrupted);
        var name = handlerProfile == "ffs" ? "CopperFFS" : "CopperSFS";
        var beforeTrace = new StartupTrace(); var afterTrace = new StartupTrace();
        var before = await Run(root, "sfs2-trst", "baseline", Path.Combine(baselineRoot!, handlerProfile, name),
            interrupted, true, guestEntry: "CorrectnessProbe::ForeignSfs2", storage: beforeTrace);
        var after = await Run(root, "sfs2-trst", "candidate", Path.Combine(candidateRoot!, handlerProfile, name),
            interrupted, true, guestEntry: "CorrectnessProbe::ForeignSfs2", storage: afterTrace);
        using var oldResult = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "baseline", "result.json")));
        using var newResult = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "candidate", "result.json")));
        Assert.Equal(oldResult.RootElement.GetProperty("Stage").GetUInt32(), newResult.RootElement.GetProperty("Stage").GetUInt32());
        if (handlerProfile == "ffs")
        {
            Assert.Equal(0u, newResult.RootElement.GetProperty("Stage").GetUInt32());
            Assert.Equal((uint)Amiga.DOS.Error.ObjectWrongType, newResult.RootElement.GetProperty("IoErr").GetUInt32());
            Assert.DoesNotContain(afterTrace.Operations, op => op.StartsWith("W:") || op == "F");
            Assert.Equal(Hash(interrupted), Hash(after));
        }
        else
        {
            Assert.Equal(oldResult.RootElement.GetProperty("IoErr").GetUInt32(), newResult.RootElement.GetProperty("IoErr").GetUInt32());
            Assert.Equal(beforeTrace.Operations, afterTrace.Operations);
            Assert.Equal(Hash(before), Hash(after));
        }
        if (handlerProfile == "sfs2-trst")
        {
            Assert.Contains(beforeTrace.Operations, op => op.StartsWith("W:"));
            Assert.Contains(beforeTrace.Operations, op => op == "F");
            Assert.Equal(0x54524F4Bu, BinaryPrimitives.ReadUInt32BigEndian(before.AsSpan((int)markerOffset)));
        }
    }
    [ForeignStartupTheory]
    [InlineData("ffs")]
    [InlineData("sfs2-trst")]
    public async Task ForeignDirtyClassicStartupPreservesRecoveryOrdering(string handlerProfile)
    {
        var manifestPath = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath))!;
        var candidateRoot = Environment.GetEnvironmentVariable("COPPERFS_FOREIGN_CANDIDATE_ROOT");
        var baselineRoot = Environment.GetEnvironmentVariable("COPPERFS_FOREIGN_BASELINE_ROOT");
        Assert.True(Directory.Exists(candidateRoot)); Assert.True(Directory.Exists(baselineRoot));
        var root = Path.Combine(Path.GetDirectoryName(manifestPath)!, "foreign-dirty-classic", handlerProfile);
        var initial = File.ReadAllBytes(manifest.ClassicBase);
        Assert.Equal(manifest.ClassicBaseSha256, Hash(initial));
        var markerOffset = (BinaryPrimitives.ReadUInt32BigEndian(initial.AsSpan(104)) + 2L) * 512;
        byte[]? interrupted = null;
        void Capture(FileStream disk)
        {
            Span<byte> header = stackalloc byte[4]; disk.Position = markerOffset; disk.ReadExactly(header);
            var marker = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (marker is not (0x54524641u or 0x54525354u)) return;
            interrupted ??= new byte[initial.Length]; disk.Position = 0; disk.ReadExactly(interrupted);
        }
        var writer = manifest.Profiles.Single(p => p.Name == "sfs").Candidate;
        await Run(root, "sfs", "writer", writer, initial, true, afterFlush: Capture);
        Assert.NotNull(interrupted);
        var name = handlerProfile == "ffs" ? "CopperFFS" : "CopperSFS";
        var beforeTrace = new StartupTrace(); var afterTrace = new StartupTrace();
        var before = await Run(root, "sfs", "baseline", Path.Combine(baselineRoot!, handlerProfile, name),
            interrupted, true, guestEntry: "CorrectnessProbe::ForeignSfs", storage: beforeTrace);
        var after = await Run(root, "sfs", "candidate", Path.Combine(candidateRoot!, handlerProfile, name),
            interrupted, true, guestEntry: "CorrectnessProbe::ForeignSfs", storage: afterTrace);
        using var oldResult = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "baseline", "result.json")));
        using var newResult = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "candidate", "result.json")));
        Assert.Equal(oldResult.RootElement.GetProperty("Stage").GetUInt32(), newResult.RootElement.GetProperty("Stage").GetUInt32());
        if (handlerProfile == "ffs")
        {
            Assert.Equal(0u, newResult.RootElement.GetProperty("Stage").GetUInt32());
            Assert.Equal((uint)Amiga.DOS.Error.ObjectWrongType, newResult.RootElement.GetProperty("IoErr").GetUInt32());
            Assert.DoesNotContain(afterTrace.Operations, op => op.StartsWith("W:") || op == "F");
            Assert.Equal(Hash(interrupted), Hash(after));
        }
        else
        {
            Assert.Equal(oldResult.RootElement.GetProperty("IoErr").GetUInt32(), newResult.RootElement.GetProperty("IoErr").GetUInt32());
            Assert.Equal(beforeTrace.Operations, afterTrace.Operations);
            Assert.Equal(Hash(before), Hash(after));
        }
        Assert.Equal(handlerProfile == "ffs" ? 0u : 1u, oldResult.RootElement.GetProperty("Stage").GetUInt32());
        Assert.Contains(beforeTrace.Operations, op => op.StartsWith("W:"));
        Assert.Contains(beforeTrace.Operations, op => op == "F");
        Assert.NotEqual(Hash(interrupted), Hash(before));
        Assert.Equal(0x54524F4Bu, BinaryPrimitives.ReadUInt32BigEndian(before.AsSpan((int)markerOffset)));
    }
}
