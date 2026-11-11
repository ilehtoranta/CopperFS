using Amiga;
using System.Text.Json;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    private sealed record PacketSample(int Trial, string Arm, string Phase, long Cycles, int Reads, int Writes, int Flushes);

    [HandlerSizePerformanceTheory]
    [InlineData("ofs")] [InlineData("ffs")] [InlineData("sfs")]
    [InlineData("sfs2-csun")] [InlineData("sfs2-trst")]
    public async System.Threading.Tasks.Task ActiveCodeChangesPreserveSmallPacketCycles(string profile)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_SIZE_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<SizeManifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == profile);
        var initial = profile == "sfs" ? File.ReadAllBytes(manifest.ClassicBase)
            : profile.StartsWith("sfs2") ? ReadMedia.Sfs2()
            : CopperStart.Floppy.Native.Tests.DosDiskImage.Create(profile == "ffs",
                includeLargeFile: false, blockCount: 32768, firstFileBlock: 2, payload: [1, 2, 3]).Bytes;
        if (profile == "sfs") Assert.Equal(manifest.ClassicBaseSha256, Hash(initial));
        var root = Path.Combine(Path.GetDirectoryName(path)!, "small-packets", profile);
        var entry = "ActiveCodeProbe::" + (profile == "ofs" ? "Ofs" : profile == "ffs" ? "Ffs" : profile == "sfs" ? "Sfs" : "Sfs2");
        var samples = new List<PacketSample>();
        foreach (var trial in Enumerable.Range(0, 3))
        foreach (var arm in trial % 2 == 0 ? new[] { "baseline", "candidate" } : new[] { "candidate", "baseline" })
        {
            long start = -1; int reads = 0, writes = 0, flushes = 0;
            await Run(root, profile, $"{trial}-{arm}", arm == "baseline" ? pair.Baseline : pair.Candidate,
                initial, true, entry, (marker, cycle, r, w, f) =>
                {
                    if (marker is 220 or 222 or 224) { start = cycle; reads = r; writes = w; flushes = f; }
                    if (marker is 221 or 223 or 225)
                    {
                        Assert.True(start >= 0);
                        samples.Add(new(trial, arm, marker == 221 ? "seek" : marker == 223 ? "read-one-byte" : "read-zero-bytes",
                            cycle - start, r - reads, w - writes, f - flushes));
                        start = -1;
                    }
                });
        }
        File.WriteAllText(Path.Combine(root, "cycles.json"), JsonSerializer.Serialize(new
        {
            BaselineSha256 = Hash(File.ReadAllBytes(pair.Baseline)),
            CandidateSha256 = Hash(File.ReadAllBytes(pair.Candidate)), Samples = samples
        }, Json));
        Assert.Equal(18, samples.Count);
        foreach (var phase in new[] { "seek", "read-one-byte", "read-zero-bytes" })
        {
            var before = samples.Where(s => s.Arm == "baseline" && s.Phase == phase).OrderBy(s => s.Trial).ToArray();
            var after = samples.Where(s => s.Arm == "candidate" && s.Phase == phase).OrderBy(s => s.Trial).ToArray();
            for (var trial = 0; trial < 3; trial++)
                Assert.Equal((before[trial].Reads, before[trial].Writes, before[trial].Flushes),
                    (after[trial].Reads, after[trial].Writes, after[trial].Flushes));
            var ratio = after.Average(s => s.Cycles) / before.Average(s => s.Cycles);
            Assert.True(ratio <= 1.001, $"{profile}/{phase}: candidate uses {(ratio - 1) * 100:F3}% more cycles; see cycles.json.");
        }
    }
}

public static class ActiveCodeProbe
{
    public static int Ofs() => Run(0);
    public static int Ffs() => Run(1);
    public static int Sfs() => Run(2);
    public static int Sfs2() => Run(3);

    private static int Run(uint kind)
    {
        var library = global::Amiga.Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) return Fail();
        DOS.DOSLibraryBase = library.Value;
        var control = APTR.FromPointer(global::Amiga.Exec.AllocMem(1024,
            global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear));
        var buffer = APTR.FromPointer(global::Amiga.Exec.AllocMem(1024, global::Amiga.Exec.MemoryFlags.Public));
        if (control.IsNull || buffer.IsNull || CorrectnessProbe.Mount(control, kind) != 0) return Fail();
        for (var i = 0; i < 1024; i++) APTR.WriteUInt8(buffer, i, (byte)(i + 0x5A));
        var file = DOS.Open("SEQ0:Tiny", DOS.FileMode.NewFile);
        if (!file.HasValue || DOS.Write(file.Value, buffer, 1024) != 1024 || DOS.Close(file.Value) == 0) return Fail();
        file = DOS.Open("SEQ0:Tiny", DOS.FileMode.OldFile);
        if (!file.HasValue) return Fail();
        for (var i = 0; i < 32; i++)
            if (DOS.Seek(file.Value, 0, -1) < 0 || DOS.Read(file.Value, buffer, 1) != 1) return Fail();
        CorrectnessProbe.Report(220);
        for (var i = 0; i < 512; i++)
            if (DOS.Seek(file.Value, i & 1, -1) < 0) return Fail();
        CorrectnessProbe.Report(221);
        CorrectnessProbe.Report(222);
        for (var i = 0; i < 512; i++)
            if (DOS.Seek(file.Value, 0, -1) < 0 || DOS.Read(file.Value, buffer, 1) != 1 ||
                APTR.ReadUInt8(buffer, 0) != 0x5A) return Fail();
        CorrectnessProbe.Report(223);
        CorrectnessProbe.Report(224);
        for (var i = 0; i < 512; i++)
            if (DOS.Read(file.Value, buffer, 0) != 0) return Fail();
        CorrectnessProbe.Report(225);
        if (DOS.Close(file.Value) == 0 || DOS.Inhibit("SEQ0:", 1) == 0) return Fail();
        CorrectnessProbe.Report(210);
        return 0;
    }

    private static int Fail() { CorrectnessProbe.Report(250); return 1; }
}
