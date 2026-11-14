using System.Text.Json;
using CopperDisk;
using CopperSFS.Check;

namespace CopperFS.Kickstart.Tests;

public sealed class AutomaticTrimTheoryAttribute : TheoryAttribute
{
    public AutomaticTrimTheoryAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("COPPERSHARP_KICKSTART31_ROM")) ||
            !File.Exists(Environment.GetEnvironmentVariable("COPPERFS_AUTOTRIM_MANIFEST")))
            Skip = "Requires Kickstart and COPPERFS_AUTOTRIM_MANIFEST pointing to AutoTrim builds.";
    }
}

public sealed partial class OriginalHandlerCorrectnessTests
{
    [AutomaticTrimTheory]
    [InlineData("ofs", false)] [InlineData("ffs", false)]
    [InlineData("sfs", false)] [InlineData("sfs2-csun", false)] [InlineData("sfs2-trst", false)]
    [InlineData("ffs", true)] [InlineData("sfs", true)] [InlineData("sfs2-trst", true)]
    public async System.Threading.Tasks.Task AutomaticTrimAfterDeletePreservesReallocatedFiles(string profile, bool fail)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_AUTOTRIM_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == profile);
        Assert.Equal(pair.OriginalSha256, Hash(File.ReadAllBytes(pair.Original)));
        var image = profile == "sfs" ? File.ReadAllBytes(manifest.ClassicBase)
            : profile.StartsWith("sfs2") ? ReadMedia.Sfs2()
            : CopperStart.Floppy.Native.Tests.DosDiskImage.Create(profile == "ffs", includeLargeFile: false,
                blockCount: 32768, firstFileBlock: 2, payload: [1, 2, 3], volumeName: "Correctness").Bytes;
        if (profile == "sfs") Assert.Equal(manifest.ClassicBaseSha256, Hash(image));
        var hook = new AutomaticDiscardHook(fail);
        var afterReset = -1;
        long setupStarted = 0, setupCycles = 0, deleteStarted = 0, deleteCycles = 0;
        var entry = profile == "ofs" ? "AutoOfs" : profile == "ffs" ? "AutoFfs"
            : profile == "sfs" ? "AutoSfs" : "AutoSfs2";
        var result = await Run(Path.Combine(Path.GetDirectoryName(path)!, "automatic", profile), profile,
            fail ? "partial-failure" : "poison", pair.Candidate, image, true,
            "CorrectnessProbe::" + entry,
            observe: (marker, cycle, _, _, _) =>
            {
                if (marker == 225) { afterReset = hook.Discards; setupStarted = cycle; }
                if (marker == 232) deleteStarted = cycle;
                if (marker == 221 && deleteStarted != 0) deleteCycles = cycle - deleteStarted;
                if (marker == 221 && setupStarted != 0) setupCycles = cycle - setupStarted;
            },
            deviceHook: hook);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "automatic", profile,
            fail ? "failure-foreground.json" : "foreground.json"), JsonSerializer.Serialize(new
        { Profile = profile, CandidateSha256 = Hash(File.ReadAllBytes(pair.Candidate)), ForegroundDeleteCycles = deleteCycles,
            CreateCloseAndDeleteCycles = setupCycles }, Json));
        Assert.True(hook.Discards > 0, "The timer must issue actual UNMAP without CopperTrim.");
        Assert.True(afterReset >= 0);
        if (Environment.GetEnvironmentVariable("COPPERFS_TRIM_PROFILE") == "1" && !fail)
        {
            var profiles = Directory.GetFiles(Path.Combine(Path.GetDirectoryName(path)!, "automatic", profile, "poison"),
                "deferred-*-service-profile.json");
            Assert.NotEmpty(profiles);
            Assert.Contains(profiles, file =>
            {
                using var sample = JsonDocument.Parse(File.ReadAllText(file));
                var root = sample.RootElement;
                return root.GetProperty("ScopeEnd").GetProperty("Discards").GetInt32() >
                    root.GetProperty("ScopeStart").GetProperty("Discards").GetInt32();
            });
        }
        // A long workload may fail maintenance before the explicit remount. Each
        // mount must suspend after its first failure, and remount permits a retry.
        if (fail) Assert.Equal(1, hook.Discards - afterReset);
        else Assert.True(hook.DiscardBytes < 1024 * 1024, "Only unallocated ranges should be trimmed, not the whole free volume.");
        if (profile.StartsWith("sfs"))
            SfsImageChecker.Check(new MemoryStream(result), reservedBlocks: profile == "sfs" ? 2u : 1u);
        else DosImageInspection.Check(result, true);
        await Run(Path.Combine(Path.GetDirectoryName(path)!, "automatic", profile), profile,
            fail ? "failure-original-reader" : "original-reader", pair.Original, result, false);
    }

    [AutomaticTrimTheory]
    [InlineData("ofs")] [InlineData("ffs")] [InlineData("sfs")]
    [InlineData("sfs2-csun")] [InlineData("sfs2-trst")]
    public async System.Threading.Tasks.Task AutomaticTrimStopsWhileInhibitedAndRestartsAfterRemount(string profile)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_AUTOTRIM_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == profile);
        var image = AutomaticImage(manifest, profile);
        var hook = new AutomaticDiscardHook(false);
        int inhibited = -1, resumed = -1;
        var root = Path.Combine(Path.GetDirectoryName(path)!, "lifecycle", profile);
        var entry = profile == "ofs" ? "Ofs" : profile == "ffs" ? "Ffs" : profile == "sfs" ? "Sfs" : "Sfs2";
        var result = await Run(root, profile, "cycle", pair.Candidate, image, true,
            "CorrectnessProbe::AutoLifecycle" + entry, (marker, _, _, _, _) =>
            {
                if (marker == 226) inhibited = hook.Discards;
                if (marker == 227) Assert.Equal(inhibited, hook.Discards);
                if (marker == 228) resumed = hook.Discards;
                if (marker == 229) Assert.True(hook.Discards > resumed);
            }, deviceHook: hook);
        Assert.True(inhibited > 0 && resumed >= inhibited);
        await Run(root, profile, "original-reader", pair.Original, result, false);
    }

    [AutomaticTrimTheory]
    [InlineData("ofs", 1)] [InlineData("ffs", 37)] [InlineData("sfs", 1)]
    [InlineData("sfs", 37)] [InlineData("sfs2-csun", 1)] [InlineData("sfs2-trst", 37)]
    public async System.Threading.Tasks.Task PowerCutDuringAutomaticDiscardPreservesDurableFiles(string profile, int cut)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_AUTOTRIM_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == profile);
        var root = Path.Combine(Path.GetDirectoryName(path)!, "automatic-cuts", profile, cut.ToString());
        var entry = profile == "ofs" ? "CutOfs" : profile == "ffs" ? "CutFfs" : profile == "sfs" ? "CutSfs" : "CutSfs2";
        var hook = new AutomaticDiscardHook(false, cut);
        var exception = await Record.ExceptionAsync(() => Run(root, profile, "cut", pair.Candidate,
            AutomaticImage(manifest, profile), true, "CorrectnessProbe::" + entry,
            observe: (marker, _, _, _, _) => { if (marker == 221) hook.CutArmed = true; },
            deviceHook: hook));
        Assert.True(exception is AutomaticPowerCutException,
            $"Expected cut {cut}; got {exception}; armed={hook.CutArmed}, requests={hook.Discards}, sectors={hook.DiscardSectors}.");
        var saved = File.ReadAllBytes(Path.Combine(root, "cut", "disk.hdf"));
        await Run(root, profile, "candidate-restart", pair.Candidate, saved, false);
        await Run(root, profile, "original-restart", pair.Original, saved, false);
    }

    private static byte[] AutomaticImage(Manifest manifest, string profile) => profile == "sfs"
        ? File.ReadAllBytes(manifest.ClassicBase) : profile.StartsWith("sfs2") ? ReadMedia.Sfs2()
        : CopperStart.Floppy.Native.Tests.DosDiskImage.Create(profile == "ffs", includeLargeFile: false,
            blockCount: 32768, firstFileBlock: 2, payload: [1, 2, 3], volumeName: "Correctness").Bytes;

    private sealed class AutomaticPowerCutException : Exception { }

    private sealed class AutomaticDiscardHook(bool fail, int cutAt = 0) : IAmigaHardfileDiscardIoHook
    {
        public int Discards, DiscardSectors, Writes;
        public int ScsiCommands, ReadCommands, WriteCommands, Flushes;
        public bool CutArmed;
        public long MaximumEnd, DiscardBytes, ReadBytes, WriteBytes;
        public bool SupportsDiscard => true;
        public uint MaximumDiscardSectors => 4096;
        public bool FlushOnDispose => false;
        public void ObserveOpen(int unit) { }
        public void ObserveCommand(ushort command)
        {
            if (command == 28) ScsiCommands++;
            if (command is 2 or 24) ReadCommands++;
            if (command is 3 or 25) WriteCommands++;
        }
        public void Read(FileStream file, long offset, Span<byte> bytes)
        { ReadBytes += bytes.Length; file.Position = offset; file.ReadExactly(bytes); }
        public void Write(FileStream file, long offset, ReadOnlySpan<byte> bytes)
        { Writes++; WriteBytes += bytes.Length; file.Position = offset; file.Write(bytes); }
        public void Flush(FileStream file) { Flushes++; file.Flush(true); }
        public void Discard(FileStream file, long offset, int bytes)
        {
            Discards++;
            DiscardBytes += fail ? 512 : bytes;
            MaximumEnd = Math.Max(MaximumEnd, offset + bytes);
            if (cutAt != 0 && CutArmed)
            {
                var sector = Enumerable.Repeat((byte)0xD7, 512).ToArray();
                for (var at = 0; at < bytes; at += 512)
                {
                    file.Position = offset + at; file.Write(sector); file.Flush(true);
                    if (++DiscardSectors == cutAt) throw new AutomaticPowerCutException();
                }
                return;
            }
            var poison = new byte[fail ? 512 : bytes];
            Array.Fill(poison, (byte)0xD7);
            file.Position = offset; file.Write(poison); file.Flush(true);
            if (fail) throw new IOException("Injected partial UNMAP failure.");
        }
    }
}
