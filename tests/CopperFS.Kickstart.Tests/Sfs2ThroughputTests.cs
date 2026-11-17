using System.Text.Json;
using Amiga;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    [PerformanceProfileTheory]
    [InlineData(512)] [InlineData(4096)] [InlineData(65536)]
    public async System.Threading.Tasks.Task ProfileSfs2ReadWriteThroughput(int packet)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var pair = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!.Profiles.Single(p => p.Name == "sfs2-trst");
        var profileDirectory = packet == 65536 ? "sfs2-throughput-profile" : $"sfs2-throughput-profile-{packet}";
        var image = await Run(Path.Combine(Path.GetDirectoryName(path)!, profileDirectory), "sfs2-trst", "candidate",
            pair.Candidate, ReadMedia.Sfs2(), true, $"Sfs2ThroughputProbe::Packet{packet}");
        var file = CopperSFS.Check.SfsImageChecker.Check(new MemoryStream(image), reservedBlocks: 1)
            .Entries.Single(e => e.Path == "/Throughput");
        Assert.Equal(Hash(Enumerable.Repeat((byte)0xD4, 131072).ToArray()), file.Sha256);
    }

    [CorrectnessTheory]
    [InlineData(512, false)] [InlineData(4096, false)] [InlineData(65536, false)]
    [InlineData(512, true)] [InlineData(4096, true)] [InlineData(65536, true)]
    public async System.Threading.Tasks.Task Sfs2ReadWriteThroughputWithinTenPercent(int packet, bool chip)
    {
        Assert.NotEqual("1", Environment.GetEnvironmentVariable("COPPERFS_PERFORMANCE_PROFILE"));
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var pair = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!.Profiles.Single(p => p.Name == "sfs2-trst");
        var allPhasesAligned = Environment.GetEnvironmentVariable("COPPERFS_PERFORMANCE_FRAME_ALIGNED_ALL") == "1";
        var frameAligned = allPhasesAligned || Environment.GetEnvironmentVariable("COPPERFS_PERFORMANCE_FRAME_ALIGNED_READ") == "1";
        var phaseSuffix = allPhasesAligned ? "FrameAlignedAll" : frameAligned ? "FrameAligned" : "";
        Assert.Equal(pair.OriginalSha256, Hash(File.ReadAllBytes(pair.Original)));
        var root = Path.Combine(Path.GetDirectoryName(path)!, "sfs2-throughput", $"{packet}-{(chip ? "chip" : "fast")}{(allPhasesAligned ? "-all-phases-aligned" : frameAligned ? "-frame-aligned" : "")}");
        var samples = new List<ThroughputSample>();
        foreach (var trial in Enumerable.Range(0, 3))
        foreach (var arm in trial % 2 == 0 ? new[] { "original", "candidate" } : new[] { "candidate", "original" })
        {
            long start = -1; int reads = 0, writes = 0, flushes = 0;
            var image = await Run(root, "sfs2-trst", $"{trial}-{arm}", arm == "original" ? pair.Original : pair.Candidate,
                ReadMedia.Sfs2(), true, $"Sfs2ThroughputProbe::Packet{packet}{(chip ? "Chip" : "")}{phaseSuffix}",
                observe: (marker, cycle, r, w, f) =>
                {
                    if (marker is 220 or 222 or 224) { start = cycle; reads = r; writes = w; flushes = f; }
                    if (marker is 221 or 223 or 225)
                    {
                        Assert.True(start >= 0 && cycle > start);
                        var phase = marker == 221 ? "grow" : marker == 223 ? "overwrite" : "read";
                        samples.Add(new(trial, arm, phase, cycle - start,
                            128 * 7_093_790.0 / (cycle - start), r - reads, w - writes, f - flushes));
                        start = -1;
                    }
                });
            var file = CopperSFS.Check.SfsImageChecker.Check(new MemoryStream(image), reservedBlocks: 1)
                .Entries.Single(e => e.Path == "/Throughput");
            Assert.Equal(131072ul, file.Size);
            Assert.Equal(Hash(Enumerable.Repeat((byte)0xD4, 131072).ToArray()), file.Sha256);
        }
        Assert.Equal(18, samples.Count);
        File.WriteAllText(Path.Combine(root, "cycles.json"), JsonSerializer.Serialize(new
        {
            OriginalSha256 = Hash(File.ReadAllBytes(pair.Original)), CandidateSha256 = Hash(File.ReadAllBytes(pair.Candidate)),
            PayloadBytes = 131072, PacketBytes = packet, ChipPayload = chip, FrameAlignedReadStart = frameAligned,
            FrameAlignedWriteStart = allPhasesAligned, BusHz = 7_093_790,
            WriteIncludesCloseAndFlush = true, Samples = samples
        }, Json));
        var failures = new List<string>();
        foreach (var phase in new[] { "grow", "overwrite", "read" })
        {
            var original = samples.Where(s => s.Arm == "original" && s.Phase == phase).Average(s => s.Cycles);
            var candidate = samples.Where(s => s.Arm == "candidate" && s.Phase == phase).Average(s => s.Cycles);
            var ratio = candidate / original;
            output.WriteLine($"{packet}/{chip}/{phase}: {100 * (ratio - 1):F2}% cycles; {128 * 7_093_790 / candidate:F2} KiB/s versus {128 * 7_093_790 / original:F2} KiB/s");
            // Both requirements apply: <=110% cycles and >=90% throughput.
            if (ratio > 1.10 || 1 / ratio < .90) failures.Add($"{phase}: {100 * (ratio - 1):F2}% more cycles");
        }
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    private sealed record ThroughputSample(int Trial, string Arm, string Phase, long Cycles,
        double KiBPerSecond, int Reads, int Writes, int Flushes);
}

// Identical real DOS packets for both handlers. Pattern creation, buffer clearing
// and verification are outside timing. Writes include close and explicit flush.
public static class Sfs2ThroughputProbe
{
    public static int Packet512() => Run(512, false);
    public static int Packet4096() => Run(4096, false);
    public static int Packet65536() => Run(65536, false);
    public static int Packet512Chip() => Run(512, true);
    public static int Packet4096Chip() => Run(4096, true);
    public static int Packet65536Chip() => Run(65536, true);
    public static int Packet512FrameAligned() => Run(512, false, true);
    public static int Packet4096FrameAligned() => Run(4096, false, true);
    public static int Packet65536FrameAligned() => Run(65536, false, true);
    public static int Packet512ChipFrameAligned() => Run(512, true, true);
    public static int Packet4096ChipFrameAligned() => Run(4096, true, true);
    public static int Packet65536ChipFrameAligned() => Run(65536, true, true);
    public static int Packet512FrameAlignedAll() => Run(512, false, true, true);
    public static int Packet4096FrameAlignedAll() => Run(4096, false, true, true);
    public static int Packet65536FrameAlignedAll() => Run(65536, false, true, true);
    public static int Packet512ChipFrameAlignedAll() => Run(512, true, true, true);
    public static int Packet4096ChipFrameAlignedAll() => Run(4096, true, true, true);
    public static int Packet65536ChipFrameAlignedAll() => Run(65536, true, true, true);

    private static int Run(int packet, bool chip, bool frameAligned = false, bool allPhasesAligned = false)
    {
        var library = global::Amiga.Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) return Fail(APTR.Null, 1);
        DOS.DOSLibraryBase = library.Value;
        var control = APTR.FromPointer(global::Amiga.Exec.AllocMem(1024, global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear));
        var data = APTR.FromPointer(global::Amiga.Exec.AllocMem(131072,
            global::Amiga.Exec.MemoryFlags.Public | (chip ? global::Amiga.Exec.MemoryFlags.Chip : global::Amiga.Exec.MemoryFlags.Fast)));
        if (control.IsNull || data.IsNull) return Fail(control, 2);
        CorrectnessProbe.Report(200); CorrectnessProbe.Report((int)(control.Raw >> 24));
        CorrectnessProbe.Report((int)(control.Raw >> 16)); CorrectnessProbe.Report((int)(control.Raw >> 8));
        CorrectnessProbe.Report((int)control.Raw);
        if (CorrectnessProbe.Mount(control, 3) != 0) return Fail(control, 3);
        if (frameAligned)
        {
            var graphics = global::Amiga.Exec.OpenLibrary(global::Amiga.Graphics.Name, 0);
            if (!graphics.HasValue) return Fail(control, 13);
            global::Amiga.Graphics.GraphicsLibraryBase = graphics.Value;
        }
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < 131072; i++) APTR.WriteUInt8(data, i, pass == 0 ? (byte)0xA7 : (byte)0xD4);
            var file = DOS.Open("SEQ0:Throughput", pass == 0 ? DOS.FileMode.NewFile : DOS.FileMode.ReadWrite);
            if (!file.HasValue) return Fail(control, 4);
            if (allPhasesAligned) global::Amiga.Graphics.WaitTOF();
            CorrectnessProbe.Report(pass == 0 ? 220 : 222);
            for (var at = 0; at < 131072; at += packet)
                if (DOS.Write(file.Value, APTR.FromPointer(data.Raw + (uint)at), packet) != packet) return Fail(control, 5);
            if (DOS.Close(file.Value) == 0) return Fail(control, 6);
            var port = DOS.DeviceProc("SEQ0:");
            if (port.IsNull || DOS.DoPkt(port, (int)DosPacketAction.Flush, 0, 0, 0, 0, 0) == 0) return Fail(control, 7);
            CorrectnessProbe.Report(pass == 0 ? 221 : 223);
        }
        for (var i = 0; i < 131072; i++) APTR.WriteUInt8(data, i, 0);
        var reader = DOS.Open("SEQ0:Throughput", DOS.FileMode.OldFile);
        if (!reader.HasValue) return Fail(control, 8);
        if (frameAligned)
        {
            global::Amiga.Graphics.WaitTOF();
        }
        CorrectnessProbe.Report(224);
        for (var at = 0; at < 131072; at += packet)
            if (DOS.Read(reader.Value, APTR.FromPointer(data.Raw + (uint)at), packet) != packet) return Fail(control, 9);
        if (DOS.Close(reader.Value) == 0) return Fail(control, 10);
        CorrectnessProbe.Report(225);
        if (frameAligned) global::Amiga.Exec.CloseLibrary(global::Amiga.Graphics.GraphicsLibraryBase);
        for (var i = 0; i < 131072; i++) if (APTR.ReadUInt8(data, i) != 0xD4) return Fail(control, 11);
        if (DOS.Inhibit("SEQ0:", 1) == 0) return Fail(control, 12);
        CorrectnessProbe.Report(210); return 0;
    }

    private static int Fail(APTR control, uint stage)
    {
        if (control.IsNotNull) { APTR.WriteUInt32(control, 20, stage); APTR.WriteUInt32(control, 24, (uint)DOS.IoErr()); }
        CorrectnessProbe.Report(250); return (int)stage;
    }
}
