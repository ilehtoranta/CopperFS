using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Copper68k;
using CopperDisk;
using CopperMod.Amiga.Lightweight;
using CopperSharp.Compiler;
using CopperSharp.Compiler.BootAdf;
using CopperSharp.Targets.Amiga;
using CopperSFS.Check;
using Xunit.Abstractions;
using Task = System.Threading.Tasks.Task;

namespace CopperSFS.Native.Tests;

public class NativeSfs2FactAttribute : FactAttribute
{
    public NativeSfs2FactAttribute()
    {
        foreach (var key in new[] { "COPPERSFS_SFS2_HANDLER", "COPPERSFS_SFS2_REFERENCE_IMAGE", "COPPERSHARP_KICKSTART31_ROM" })
            if (!File.Exists(Environment.GetEnvironmentVariable(key))) Skip = "Requires the native handler, original SFS2 image and licensed ROM; see docs/native-sfs2.md.";
    }
}
public sealed class NativeSfs2TrstFactAttribute : NativeSfs2FactAttribute
{
    public NativeSfs2TrstFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("COPPERSFS_SFS2_TRST_QUALIFICATION") != "1") Skip = "Arena exhaustion is specific to the SFS2 TRST build profile.";
    }
}
public sealed class NativeSfs2TheoryAttribute : TheoryAttribute
{
    public NativeSfs2TheoryAttribute()
    {
        foreach (var key in new[] { "COPPERSFS_SFS2_HANDLER", "COPPERSFS_SFS2_REFERENCE_IMAGE", "COPPERSHARP_KICKSTART31_ROM" })
            if (!File.Exists(Environment.GetEnvironmentVariable(key))) Skip = "Requires native SFS2 qualification inputs.";
    }
}

public sealed class NativeSfs2Tests(ITestOutputHelper output)
{
    private static string Input(string key) => Path.GetFullPath(Environment.GetEnvironmentVariable(key)!);
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CopperSFS.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }
    private static string RunDirectory(string name)
    {
        var root = Environment.GetEnvironmentVariable("COPPERSFS_SFS2_NATIVE_EVIDENCE") ?? Path.Combine(Root(), "artifacts", "native-sfs2", "tests");
        var directory = Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); return directory;
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Manifest(string path)
    { using var input = File.OpenRead(path); return JsonSerializer.Serialize(SfsImageChecker.Check(input, 512, 2)); }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<byte[]>> Probes = new();
    private static byte[] Probe(string mode) => Probes.GetOrAdd(mode, mode => new(() =>
    {
        var compilation = AmigaM68kCompiler.Compile(new M68kCompilationRequest
        {
            AssemblyPath = typeof(NativeSfs2Probe).Assembly.Location, EntryPoint = typeof(NativeSfs2Probe).FullName + "::Run" + mode,
            Cpu = M68kCpuTarget.M68000, OutputFormat = M68kOutputFormat.Hunk,
            RuntimeProfile = M68kRuntimeProfile.Application, ExceptionMode = M68kExceptionMode.Full, IncludedExportNames = []
        });
        Assert.True(compilation.FrameworkAnalysis.IsCompatible); return compilation.Image;
    })).Value;
    private static async Task<byte[]> Boot(string directory, string mode, string? handler = null)
    {
        var stage = Path.Combine(directory, "boot-" + mode + (handler is null ? "" : "-original")); Directory.CreateDirectory(Path.Combine(stage, "S"));
        File.WriteAllBytes(Path.Combine(stage, "PROBE"), Probe(mode));
        File.Copy(handler ?? Input("COPPERSFS_SFS2_HANDLER"), Path.Combine(stage, "SFSHANDLER"));
        File.WriteAllText(Path.Combine(stage, "S", "startup-sequence"), "PROBE\n", Encoding.ASCII);
        return await FileSystemAdf.CreateAsync(stage, "NativeSfs2");
    }
    private sealed record Receipt(int Status, long Frames, long Cycles, string Pc, double Seconds);
    private static Receipt Run(string image, byte[] boot, SectorHook? hook = null, bool cpu68000 = false, bool wide = false)
    {
        using var machine = new LightweightA500Machine(new LightweightA500Configuration
        {
            CpuModel = cpu68000 ? M68kCpuModel.M68000 : M68kCpuModel.M68020,
            ChipRamBytes = 512 * 1024, FastRamBytes = 2 * 1024 * 1024, FloppyDriveCount = 1,
            Hardfiles = [new AmigaHardfileConfiguration(0, image, mountMode: AmigaHardfileMountMode.Partition,
                partition: new AmigaHardfilePartitionMetadata
                {
                    DeviceName = "DH0", SizeBlockLongs = 128, Surfaces = 2, SectorsPerBlock = 1,
                    BlocksPerTrack = 32, LowCylinder = 0, HighCylinder = wide ? 0x11000000u / 64 - 1 : 511u,
                    ReservedBlocks = wide ? 1u : 2u, NumBuffers = 32, DosType = 0x53465302,
                    BootPriority = -127, MaxTransfer = 0x1FE00, Mask = 0x7FFFFFFF
                }) { ExposeBootNode = false, IoHook = hook }]
        });
        machine.LoadKickstart(File.ReadAllBytes(Input("COPPERSHARP_KICKSTART31_ROM")));
        machine.MountAdf(0, boot); machine.SetDriveWriteProtected(0, false); machine.Reset();
        var elapsed = Stopwatch.StartNew(); var started = false; var status = 255;
        for (var frame = 0; frame < 30000 && elapsed.Elapsed < TimeSpan.FromMinutes(3); frame++)
        {
            try { machine.ExecuteFrame(); }
            catch (PowerCut) { status = 99; break; }
            status = machine.ParallelOutputPins; if (status == 101) started = true;
            if (started && status is > 0 and < 100) break;
            if (machine.Cpu.Halted) break;
        }
        hook?.FinishTrace();
        return new(status, machine.CompletedFrames, machine.Cpu.Cycles, $"{machine.Cpu.ProgramCounter:X8}", elapsed.Elapsed.TotalSeconds);
    }
    private void Report(string directory, object details)
    {
        File.WriteAllText(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(new
        {
            handler = Input("COPPERSFS_SFS2_HANDLER"), handlerSha256 = Hash(Input("COPPERSFS_SFS2_HANDLER")),
            romSha256 = Hash(Input("COPPERSHARP_KICKSTART31_ROM")), fixtureSha256 = Hash(Input("COPPERSFS_SFS2_REFERENCE_IMAGE")),
            testAssemblySha256 = Hash(typeof(NativeSfs2Tests).Assembly.Location), compilerSha256 = Hash(typeof(M68kCompilationRequest).Assembly.Location),
            emulatorSha256 = Hash(typeof(LightweightA500Machine).Assembly.Location), details
        }, new JsonSerializerOptions { WriteIndented = true }));
        output.WriteLine(directory);
    }

    [NativeSfs2Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NativeHandlerReadsAndMutatesOriginalSfs2ThenRemounts(bool cpu68000)
    {
        var directory = RunDirectory(cpu68000 ? "68000" : "68020"); var image = Path.Combine(directory, "image.hdf");
        File.Copy(Input("COPPERSFS_SFS2_REFERENCE_IMAGE"), image); var before = Manifest(image);
        var readBoot = await Boot(directory, "Read"); var read = Run(image, readBoot, cpu68000: cpu68000);
        Report(directory, new { phase = "read", read }); Assert.Equal(42, read.Status); Assert.Equal(before, Manifest(image));
        var write = Run(image, await Boot(directory, "Full"), cpu68000: cpu68000);
        Report(directory, new { phase = "full", read, write }); Assert.Equal(42, write.Status);
        var manifest = Manifest(image); Assert.NotEqual(before, manifest);
        var remount = Run(image, await Boot(directory, "Recover"), cpu68000: cpu68000);
        Assert.Equal(42, remount.Status); Assert.Equal(manifest, Manifest(image));
        var verify = Run(image, await Boot(directory, "VerifyFull"), cpu68000: cpu68000); Assert.Equal(42, verify.Status);
        var originalPath = Input("COPPERSFS_SFS2_ORIGINAL_HANDLER");
        var original = Run(image, await Boot(directory, "VerifyFull", originalPath));
        Report(directory, new { read, write, remount, verify, original, originalHandlerSha256 = Hash(originalPath), imageSha256 = Hash(image), manifest });
        Assert.Equal(42, original.Status); Assert.Equal(manifest, Manifest(image));
    }

    [NativeSfs2TrstFact]
    public async Task NativeTrstArenaExhaustionLeavesFilePositionIntactAndCanBeReused()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("COPPERSFS_SFS2_TRST_QUALIFICATION"));
        var directory = RunDirectory("arena"); var image = Path.Combine(directory, "image.hdf");
        File.Copy(Input("COPPERSFS_SFS2_REFERENCE_IMAGE"), image);
        var hook = new SectorHook(File.ReadAllBytes(image)); var result = Run(image, await Boot(directory, "Arena"), hook);
        Report(directory, new { result, hook.Events }); Assert.Equal(42, result.Status); Manifest(image);
        Assert.Contains(hook.Events, e => e.Id == 0x54525354);
        Assert.DoesNotContain(hook.Events, e => e.Id == 0x4353554E);
    }

    [NativeSfs2Fact]
    public async Task NativeWidePacketsReadWriteResizeAndSharePositionsAboveFourGiB()
    {
        var directory = RunDirectory("wide"); var image = Path.Combine(directory, "wide.hdf"); NativeWideFixture.Create(image);
        var result = Run(image, await Boot(directory, "Wide"), wide: true);
        Report(directory, new { result, logicalBlocks = NativeWideFixture.Blocks, initialFileSize = NativeWideFixture.OldSize,
            boundary = "Sparse host backing; affected metadata/content/allocation checks, not a full-volume integrity scan." });
        Assert.Equal(42, result.Status); NativeWideFixture.Verify(image);
        var repeated = Run(image, await Boot(directory, "WideRecover"), wide: true);
        Assert.Equal(42, repeated.Status); NativeWideFixture.Verify(image);
    }

    [NativeSfs2Fact]
    public async Task NativeHandlerRecoversOriginalSfs2RedoOnMount()
    {
        var nativeRedo = Input("COPPERSFS_SFS2_NATIVE_REDO_IMAGE");
        var directory = RunDirectory("original-redo"); var image = Path.Combine(directory, "image.hdf"); File.Copy(nativeRedo, image);
        var receipt = Run(image, await Boot(directory, "Read")); Report(directory, new { inputSha256 = Hash(nativeRedo), receipt });
        Assert.Equal(42, receipt.Status); Manifest(image);
    }

    [NativeSfs2Theory]
    [InlineData("Overwrite")] [InlineData("Truncate")] [InlineData("Rename")]
    [InlineData("Delete")] [InlineData("Create")] [InlineData("Comment")]
    public async Task EveryNativeSectorAndFlushCutRecoversCompleteManifest(string mode)
    {
        var directory = RunDirectory("cuts-" + mode); var baselinePath = Path.Combine(directory, "baseline.hdf");
        var source = File.ReadAllBytes(Input("COPPERSFS_SFS2_REFERENCE_IMAGE")); var before = Manifest(Input("COPPERSFS_SFS2_REFERENCE_IMAGE"));
        File.WriteAllBytes(baselinePath, source); var boot = await Boot(directory, mode); var recoverBoot = await Boot(directory, "Recover");
        var trace = new SectorHook(source, retainPrefixes: true); var baseline = Run(baselinePath, boot, trace);
        Assert.Equal(42, baseline.Status); var after = Manifest(baselinePath); Assert.NotEqual(before, after);
        var nativeRedo = Environment.GetEnvironmentVariable("COPPERSFS_SFS2_TRST_QUALIFICATION") == "1";
        byte[]? originalBoot = null;
        if (nativeRedo)
        {
            Assert.Contains(trace.Events, e => e.Id == 0x54525354);
            Assert.DoesNotContain(trace.Events, e => e.Id == 0x4353554E);
            originalBoot = await Boot(directory, "Recover", Input("COPPERSFS_SFS2_ORIGINAL_HANDLER"));
        }
        var points = trace.Events.Where(e => e.Kind != "flush" || trace.Events.Any(w => w.Kind == "write" && w.Sequence < e.Sequence)).ToArray();
        var recoveries = new List<object>();
        foreach (var group in points.GroupBy(e => e.Prefix))
        {
            var point = group.First(); var cutPath = Path.Combine(directory, "cut.hdf"); File.WriteAllBytes(cutPath, source);
            var hook = new SectorHook(source) { CutAfter = point.Sequence }; var interrupted = Run(cutPath, boot, hook);
            Assert.Equal(99, interrupted.Status); Assert.True(hook.Crashed);
            var durable = File.ReadAllBytes(cutPath); Assert.Equal(trace.Prefixes[point.Prefix], durable);
            Receipt? originalRecovery = null;
            if (originalBoot is not null)
            {
                var originalPath = Path.Combine(directory, "original-cut.hdf"); File.WriteAllBytes(originalPath, durable);
                originalRecovery = Run(originalPath, originalBoot); Assert.Equal(42, originalRecovery.Status);
                Assert.Contains(Manifest(originalPath), new[] { before, after });
            }
            var recovery = Run(cutPath, recoverBoot); Assert.Equal(42, recovery.Status);
            var recoveredManifest = Manifest(cutPath); Assert.Contains(recoveredManifest, new[] { before, after });
            var stable = File.ReadAllBytes(cutPath); var repeat = Run(cutPath, recoverBoot);
            Assert.Equal(42, repeat.Status); Assert.Equal(stable, File.ReadAllBytes(cutPath));
            recoveries.Add(new { point, covered = group.Select(p => p.Sequence).ToArray(), recovery, repeat, originalRecovery, manifest = recoveredManifest });
        }
        Report(directory, new { mode, baseline, coveredCutPoints = points.Length, actualCutReboots = recoveries.Count,
            model = "Atomic 512-byte sectors; writes remain pending until a reliable flush. Every write/flush point is mapped to its byte-identical durable prefix; one actual cut/reboot per prefix.", trace.Events, recoveries });
        output.WriteLine($"SFS2 native {mode}: {points.Length} sector/flush cuts, {recoveries.Count} actual cut/reboot groups; full manifests and repeated recovery passed.");
    }

    private sealed class PowerCut : Exception;
    private sealed record IoEvent(long Sequence, string Kind, long Offset, uint Id, string Prefix);
    private sealed class SectorHook(byte[] source, bool retainPrefixes = false) : IAmigaHardfileIoHook
    {
        private readonly Dictionary<long, byte[]> pending = new();
        private string prefix = Hash(source); private long sequence; private bool tracing = true;
        internal readonly List<IoEvent> Events = [];
        internal readonly Dictionary<string, byte[]> Prefixes = new() { [Hash(source)] = source };
        internal long CutAfter; internal bool Crashed;
        public bool FlushOnDispose => !Crashed;
        internal void FinishTrace() => tracing = false;
        public void Read(FileStream backing, long offset, Span<byte> target)
        {
            backing.Position = offset; backing.ReadExactly(target);
            foreach (var (at, bytes) in pending)
            {
                var start = Math.Max(at, offset); var end = Math.Min(at + bytes.Length, offset + target.Length);
                if (start < end) bytes.AsSpan((int)(start - at), (int)(end - start)).CopyTo(target[(int)(start - offset)..]);
            }
        }
        public void Write(FileStream backing, long offset, ReadOnlySpan<byte> bytes)
        {
            Assert.Equal(512, bytes.Length); pending[offset] = bytes.ToArray();
            Record("write", offset, BinaryPrimitives.ReadUInt32BigEndian(bytes));
        }
        public void Flush(FileStream backing)
        {
            foreach (var (at, bytes) in pending) { backing.Position = at; backing.Write(bytes); }
            backing.Flush(true); pending.Clear();
            if (tracing && retainPrefixes)
            {
                var current = new byte[source.Length]; backing.Position = 0; backing.ReadExactly(current); prefix = Hash(current);
                if (!Prefixes.ContainsKey(prefix)) Prefixes.Add(prefix, current);
            }
            Record("flush", 0, 0);
        }
        private void Record(string kind, long offset, uint id)
        {
            if (!tracing) return;
            var item = new IoEvent(++sequence, kind, offset, id, prefix); Events.Add(item);
            if (CutAfter != sequence) return;
            Crashed = true; pending.Clear(); throw new PowerCut();
        }
    }
}
