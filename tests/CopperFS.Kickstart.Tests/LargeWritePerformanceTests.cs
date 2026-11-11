using Amiga;
using System.Text.Json;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    [HandlerSizePerformanceTheory]
    [InlineData("ofs")] [InlineData("ffs")] [InlineData("sfs")]
    public async System.Threading.Tasks.Task LargeWritesPreserveCyclesAndDiskOperations(string profile)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_SIZE_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<SizeManifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == profile);
        var initial = profile == "sfs" ? File.ReadAllBytes(manifest.ClassicBase)
            : CopperStart.Floppy.Native.Tests.DosDiskImage.Create(profile == "ffs",
                includeLargeFile: false, blockCount: 32768, firstFileBlock: 2, payload: [1, 2, 3]).Bytes;
        if (profile == "sfs") Assert.Equal(manifest.ClassicBaseSha256, Hash(initial));
        var root = Path.Combine(Path.GetDirectoryName(path)!, "large-writes", profile);
        var entry = "LargeWriteProbe::" + (profile == "ofs" ? "Ofs" : profile == "ffs" ? "Ffs" : "Sfs");
        var samples = new List<PacketSample>();
        foreach (var trial in Enumerable.Range(0, 3))
        foreach (var arm in trial % 2 == 0 ? new[] { "baseline", "candidate" } : new[] { "candidate", "baseline" })
        {
            long start = -1; int reads = 0, writes = 0, flushes = 0;
            var written = await Run(root, profile, $"{trial}-{arm}", arm == "baseline" ? pair.Baseline : pair.Candidate,
                initial, true, entry, (marker, cycle, r, w, f) =>
                {
                    if (marker is 220 or 222 or 224) { start = cycle; reads = r; writes = w; flushes = f; }
                    if (marker is 221 or 223 or 225)
                    {
                        Assert.True(start >= 0);
                        samples.Add(new(trial, arm, marker == 221 ? "grow" : marker == 223 ? "overwrite" : "verify",
                            cycle - start, r - reads, w - writes, f - flushes));
                        start = -1;
                    }
                });
            if (profile == "sfs")
            {
                var report = CopperSFS.Check.SfsImageChecker.Check(new MemoryStream(written), reservedBlocks: 2);
                Assert.Equal(98304ul, report.Entries.Single(e => e.Path == "/Large").Size);
            }
            else
            {
                var report = DosImageInspection.Check(written, true, checkSharedWorkload: false);
                Assert.Equal(98304u, report.Files.Single(e => e.Path == "/Large").Size);
                var expected = System.Security.Cryptography.SHA256.HashData(Enumerable.Repeat((byte)0xD4, 98304).ToArray());
                Assert.Equal(Convert.ToHexString(expected), report.Files.Single(e => e.Path == "/Large").Sha256);
            }
        }
        File.WriteAllText(Path.Combine(root, "cycles.json"), JsonSerializer.Serialize(new
        {
            BaselineSha256 = Hash(File.ReadAllBytes(pair.Baseline)),
            CandidateSha256 = Hash(File.ReadAllBytes(pair.Candidate)), Samples = samples
        }, Json));
        Assert.Equal(18, samples.Count);
        foreach (var phase in new[] { "grow", "overwrite", "verify" })
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

// Real DOS traffic: fill the classic SFS extent tree, grow a file across FFS/OFS
// pointer tables, and overwrite both separated source runs in small packets.
public static class LargeWriteProbe
{
    public static int Ofs() => Run(0);
    public static int Ffs() => Run(1);
    public static int Sfs() => Run(2);

    private static int Run(uint kind)
    {
        var library = global::Amiga.Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) return Fail(APTR.Null, 1);
        DOS.DOSLibraryBase = library.Value;
        var control = APTR.FromPointer(global::Amiga.Exec.AllocMem(1024,
            global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear));
        var data = APTR.FromPointer(global::Amiga.Exec.AllocMem(65536, global::Amiga.Exec.MemoryFlags.Public));
        if (control.IsNull || data.IsNull) return Fail(control, 2);
        CorrectnessProbe.Report(200); CorrectnessProbe.Report((int)(control.Raw >> 24));
        CorrectnessProbe.Report((int)(control.Raw >> 16)); CorrectnessProbe.Report((int)(control.Raw >> 8));
        CorrectnessProbe.Report((int)control.Raw);
        if (CorrectnessProbe.Mount(control, kind) != 0) return Fail(control, 3);
        for (var i = 0; i < 65536; i++) APTR.WriteUInt8(data, i, 0xA7);
        var name = APTR.FromPointer(control.Raw + 400);
        APTR.WriteUInt32(name, 0, 0x53455130); // SEQ0:F00
        APTR.WriteUInt32(name, 4, 0x3A463030); APTR.WriteUInt8(name, 8, 0);
        for (var i = 0; i < 40; i++)
        {
            APTR.WriteUInt8(name, 6, (byte)('0' + i / 10)); APTR.WriteUInt8(name, 7, (byte)('0' + i % 10));
            var filler = DOS.Open(CString.FromPointer(name.Raw), DOS.FileMode.NewFile);
            if (!filler.HasValue || DOS.Write(filler.Value, data, 4096) != 4096 || DOS.Close(filler.Value) == 0)
                return Fail(control, 4);
        }
        CorrectnessProbe.Report(220);
        var file = DOS.Open("SEQ0:Large", DOS.FileMode.NewFile);
        if (!file.HasValue || DOS.Write(file.Value, data, 49152) != 49152) return Fail(control, 5);
        var gap = DOS.Open("SEQ0:Gap", DOS.FileMode.NewFile);
        if (!gap.HasValue || DOS.Write(gap.Value, data, 4096) != 4096 || DOS.Close(gap.Value) == 0 ||
            DOS.Write(file.Value, data, 49152) != 49152 || DOS.Close(file.Value) == 0) return Fail(control, 6);
        CorrectnessProbe.Report(221);
        file = DOS.Open("SEQ0:Large", DOS.FileMode.ReadWrite);
        if (!file.HasValue) return Fail(control, 7);
        for (var i = 0; i < 8192; i++) APTR.WriteUInt8(data, i, 0xD4);
        CorrectnessProbe.Report(222);
        for (var i = 0; i < 12; i++)
            if (DOS.Write(file.Value, data, 8192) != 8192) return Fail(control, 8);
        if (DOS.Close(file.Value) == 0) return Fail(control, 9);
        CorrectnessProbe.Report(223);
        CorrectnessProbe.Report(224);
        file = DOS.Open("SEQ0:Large", DOS.FileMode.OldFile);
        if (!file.HasValue) return Fail(control, 10);
        for (var part = 0; part < 12; part++)
        {
            if (DOS.Read(file.Value, data, 8192) != 8192) return Fail(control, 11);
            for (var i = 0; i < 8192; i++) if (APTR.ReadUInt8(data, i) != 0xD4) return Fail(control, 12);
        }
        if (DOS.Close(file.Value) == 0) return Fail(control, 13);
        CorrectnessProbe.Report(225);
        if (DOS.Inhibit("SEQ0:", 1) == 0) return Fail(control, 14);
        CorrectnessProbe.Report(210);
        return 0;
    }

    private static int Fail(APTR control, uint stage)
    {
        if (control.IsNotNull)
        { APTR.WriteUInt32(control, 20, stage); APTR.WriteUInt32(control, 24, (uint)DOS.IoErr()); }
        CorrectnessProbe.Report(250); return (int)stage;
    }
}
