using System.Security.Cryptography;
using System.Text.Json;
using Amiga;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    [HandlerSizePerformanceTheory]
    [InlineData("ofs", false)] [InlineData("ffs", false)]
    [InlineData("ofs", true)] [InlineData("ffs", true)]
    public async System.Threading.Tasks.Task AppendAndOverwriteOriginalFile(string profile, bool chip)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_SIZE_MANIFEST")!;
        var pair = JsonSerializer.Deserialize<SizeManifest>(File.ReadAllText(path))!.Profiles.Single(p => p.Name == profile);
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(
            Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!))!;
        var reference = manifest.Profiles.Single(p => p.Name == profile);
        Assert.Equal(reference.OriginalSha256, Hash(File.ReadAllBytes(reference.Original)));
        var root = Path.Combine(Path.GetDirectoryName(path)!, chip ? "append-chip" : "append-fast", profile);
        var entry = "FfsAppendProbe::" + (profile == "ofs" ? "Ofs" : "Ffs");
        var seed = await Run(root, profile, "seed", reference.Original, AutomaticImage(manifest, profile), true, entry + "Seed");
        var original = DosImageInspection.Check(seed, false, false);
        if (profile == "ofs") Assert.NotEmpty(original.RestartedSequences);
        var expected = Enumerable.Repeat((byte)0x35, 12 * 4096).Concat(Enumerable.Repeat((byte)0x66, 32 * 257)).ToArray();
        for (var i = 0; i < 12; i++) Array.Fill(expected, (byte)0xAC, i * 4096, 511);
        var samples = new List<SizeSample>();
        foreach (var trial in Enumerable.Range(0, 3))
        foreach (var arm in trial % 2 == 0 ? new[] { "baseline", "candidate" } : new[] { "candidate", "baseline" })
        {
            long start = 0; int reads = 0, writes = 0, flushes = 0;
            var result = await Run(root, profile, $"{trial}-{arm}", arm == "baseline" ? pair.Baseline : pair.Candidate,
                seed, true, entry + (chip ? "Chip" : ""), (marker, cycle, r, w, f) =>
                {
                    if (marker is 220 or 222) { start = cycle; reads = r; writes = w; flushes = f; }
                    if (marker is 221 or 223) samples.Add(new(trial, arm, marker == 221 ? "append" : "overwrite",
                        cycle - start, r - reads, w - writes, f - flushes));
                });
            var file = DosImageInspection.Check(result, false, false).Files.Single(f => f.Path == "/Chain");
            Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), file.Sha256);
        }
        File.WriteAllText(Path.Combine(root, "cycles.json"), JsonSerializer.Serialize(new { Samples = samples,
            SeedSha256 = Hash(seed), BaselineSha256 = Hash(File.ReadAllBytes(pair.Baseline)),
            CandidateSha256 = Hash(File.ReadAllBytes(pair.Candidate)) }, Json));
        foreach (var phase in samples.GroupBy(s => s.Phase))
            Assert.True(phase.Where(s => s.Arm == "candidate").Average(s => s.Cycles) <=
                phase.Where(s => s.Arm == "baseline").Average(s => s.Cycles) * 1.001, $"See {root}/cycles.json");
    }
}

public static class FfsAppendProbe
{
    public static int OfsSeed() => Run(0, true, false);
    public static int FfsSeed() => Run(1, true, false);
    public static int Ofs() => Run(0, false, false);
    public static int Ffs() => Run(1, false, false);
    public static int OfsChip() => Run(0, false, true);
    public static int FfsChip() => Run(1, false, true);

    private static int Run(uint kind, bool seed, bool chip)
    {
        var lib = Exec.OpenLibrary("dos.library", 37);
        if (!lib.HasValue) { CorrectnessProbe.Report(250); return 1; }
        DOS.DOSLibraryBase = lib.Value;
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)), 184, uint.MaxValue);
        var control = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var data = APTR.FromPointer(Exec.AllocMem(4096, Exec.MemoryFlags.Public | (chip ? Exec.MemoryFlags.Chip : Exec.MemoryFlags.Fast)));
        if (control.IsNull || data.IsNull) { CorrectnessProbe.Report(250); return 2; }
        CorrectnessProbe.Report(200);
        for (var shift = 24; shift >= 0; shift -= 8) CorrectnessProbe.Report((int)(control.Raw >> shift));
        if (CorrectnessProbe.Mount(control, kind) != 0) return CorrectnessProbe.Failed(control, 3);
        var port = DOS.DeviceProc("SEQ0:");
        if (port.IsNull) return CorrectnessProbe.Failed(control, 4);
        for (var i = 0; i < 4096; i++) APTR.WriteUInt8(data, i, seed ? (byte)0x35 : (byte)0x66);
        if (seed)
        {
            // Two interleaved files allocate physically separated runs. Reopen at
            // every append so the reference emits its historical OFS boundaries.
            for (var i = 0; i < 12; i++)
            {
                var file = DOS.Open("SEQ0:Chain", i == 0 ? DOS.FileMode.NewFile : DOS.FileMode.ReadWrite);
                if (!file.HasValue || DOS.Seek(file.Value, 0, 1) < 0 || DOS.Write(file.Value, data, 4096) != 4096 ||
                    DOS.Close(file.Value) == 0) return CorrectnessProbe.Failed(control, 5);
                file = DOS.Open("SEQ0:Gap", i == 0 ? DOS.FileMode.NewFile : DOS.FileMode.ReadWrite);
                if (!file.HasValue || DOS.Seek(file.Value, 0, 1) < 0 || DOS.Write(file.Value, data, 4096) != 4096 ||
                    DOS.Close(file.Value) == 0) return CorrectnessProbe.Failed(control, 6);
            }
        }
        else
        {
            var file = DOS.Open("SEQ0:Chain", DOS.FileMode.ReadWrite);
            if (!file.HasValue || DOS.Seek(file.Value, 0, 1) < 0) return CorrectnessProbe.Failed(control, 7);
            CorrectnessProbe.Report(220);
            for (var i = 0; i < 32; i++) if (DOS.Write(file.Value, data, 257) != 257) return CorrectnessProbe.Failed(control, 8);
            if (DOS.DoPkt(port, (int)DosPacketAction.Flush, 0, 0, 0, 0, 0) == 0) return CorrectnessProbe.Failed(control, 9);
            CorrectnessProbe.Report(221);
            for (var i = 0; i < 511; i++) APTR.WriteUInt8(data, i, 0xAC);
            CorrectnessProbe.Report(222);
            for (var i = 0; i < 12; i++) if (DOS.Seek(file.Value, i * 4096, -1) < 0 ||
                DOS.Write(file.Value, data, 511) != 511) return CorrectnessProbe.Failed(control, 10);
            if (DOS.Close(file.Value) == 0 || DOS.DoPkt(port, (int)DosPacketAction.Flush, 0, 0, 0, 0, 0) == 0)
                return CorrectnessProbe.Failed(control, 11);
            CorrectnessProbe.Report(223);
        }
        if (DOS.DoPkt(port, (int)DosPacketAction.Flush, 0, 0, 0, 0, 0) == 0) return CorrectnessProbe.Failed(control, 12);
        CorrectnessProbe.Report(210); return 0;
    }
}
