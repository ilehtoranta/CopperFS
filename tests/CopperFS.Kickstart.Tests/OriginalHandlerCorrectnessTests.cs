using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Copper68k;
using CopperDisk;
using CopperMod.Amiga.Lightweight;
using CopperSharp.Compiler;
using CopperSharp.Compiler.BootAdf;
using CopperSharp.Targets.Amiga;
using CopperSFS.Bench;
using CopperSFS.Check;
using CopperStart.Floppy.Native.Tests;
using Xunit.Abstractions;
using Task = System.Threading.Tasks.Task;

namespace CopperFS.Kickstart.Tests;

public sealed class CorrectnessTheoryAttribute : TheoryAttribute
{
    public CorrectnessTheoryAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("COPPERSHARP_KICKSTART31_ROM")) ||
            !File.Exists(Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")))
            Skip = "Supply a licensed Kickstart ROM and COPPERFS_CORRECTNESS_MANIFEST.";
    }
}

public sealed partial class OriginalHandlerCorrectnessTests(ITestOutputHelper output)
{
    public sealed record Profile(string Name, string Original, string Candidate, string OriginalSha256);
    public sealed record Manifest(Profile[] Profiles, string ClassicBase, string ClassicBaseSha256);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> Probes = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [CorrectnessTheory]
    [InlineData("ofs")] [InlineData("ffs")] [InlineData("sfs")]
    [InlineData("sfs2-csun")] [InlineData("sfs2-trst")]
    [InlineData("ofs-dd")] [InlineData("ofs-hd")]
    [InlineData("ffs-dd")] [InlineData("ffs-hd")]
    public async Task SharedMutationsAndEveryCleanHandoff(string profile)
    {
        var manifestPath = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath))!;
        var pair = manifest.Profiles.Single(p => p.Name == profile);
        Assert.Equal(pair.OriginalSha256, Hash(File.ReadAllBytes(pair.Original)));
        var root = Path.Combine(Path.GetDirectoryName(manifestPath)!, profile);
        Directory.CreateDirectory(root);
        var candidateHash = Hash(File.ReadAllBytes(pair.Candidate));
        var baseImage = !profile.StartsWith("sfs")
            ? DosDiskImage.Create(profile.StartsWith("ffs"), includeLargeFile: false,
                blockCount: profile.EndsWith("-dd") ? 1760u : profile.EndsWith("-hd") ? 3520u : 32768u,
                firstFileBlock: 2, payload: [1, 2, 3], volumeName: "Correctness").Bytes
            : profile == "sfs" ? File.ReadAllBytes(manifest.ClassicBase) : ReadMedia.Sfs2();
        if (profile == "sfs") Assert.Equal(manifest.ClassicBaseSha256, Hash(baseImage));
        File.WriteAllBytes(Path.Combine(root, "base.hdf"), baseImage);
        var arms = new[] { (Name: "original", Path: pair.Original), (Name: "copper", Path: pair.Candidate) };
        var reports = new List<object>();
        string[]? originalFiles = null;
        var writerFiles = new Dictionary<string, string[]>();
        var equivalent = true;
        foreach (var writer in arms)
        {
            var image = Path.Combine(root, writer.Name + "-written.hdf");
            var written = await Run(root, profile, writer.Name + "-write", writer.Path, baseImage, true);
            File.WriteAllBytes(image, written);
            object inspection;
            string[] files;
            if (profile.StartsWith("sfs"))
            {
                var report = SfsImageChecker.Check(new MemoryStream(written), reservedBlocks: profile == "sfs" ? 2u : 1u);
                Assert.Equal((ulong)90130, report.Entries.Single(e => e.Path == "/AB/Renamed").Size);
                files = report.Entries.Where(e => !e.Directory).Select(e => $"{e.Path}:{e.Size}:{e.Sha256}").Order().ToArray();
                inspection = report;
            }
            else
            {
                var report = DosImageInspection.Check(written, writer.Name == "copper");
                files = report.Files.Select(e => $"{e.Path}:{e.Size}:{e.Sha256}").Order().ToArray();
                inspection = report;
            }
            if (originalFiles == null) originalFiles = files;
            else equivalent = originalFiles.SequenceEqual(files);
            writerFiles.Add(writer.Name, files);
            File.WriteAllText(Path.Combine(root, writer.Name + "-inspection.json"), JsonSerializer.Serialize(inspection, Json));
            foreach (var reader in arms)
            {
                await Run(root, profile, writer.Name + "-to-" + reader.Name, reader.Path, written, false);
                reports.Add(new { Writer = writer.Name, Reader = reader.Name, MediaSha256 = Hash(written) });
            }
        }
        Assert.Equal(candidateHash, Hash(File.ReadAllBytes(pair.Candidate)));
        File.WriteAllText(Path.Combine(root, "qualification.json"), JsonSerializer.Serialize(new
        {
            Profile = profile, OriginalSha256 = pair.OriginalSha256, CandidateSha256 = candidateHash,
            BaseSha256 = Hash(baseImage), CrossReaderBoots = reports, WriterFileDigests = writerFiles,
            EquivalentFileDigests = equivalent, Passed = equivalent
        }, Json));
        Assert.True(equivalent, "Original and Copper persisted file manifests differ; see qualification.json and both inspection reports.");
    }

    private async Task<byte[]> Run(string root, string profile, string label, string handler, byte[] initial, bool write,
        string? guestEntry = null, Action<byte, long, int, int, int>? observe = null, bool floppy = false, WritableHook? storage = null,
        M68kCpuModel cpu = M68kCpuModel.M68020, Action<FileStream>? afterFlush = null)
    {
        var directory = Path.Combine(root, label);
        Directory.CreateDirectory(directory);
        var media = Path.Combine(directory, floppy ? "disk.adf" : "disk.hdf");
        File.WriteAllBytes(media, initial);
        var hook = storage ?? (afterFlush != null ? new WritableHook(afterFlush) : null);
        await BootMedia(directory, profile, label, handler, media, write, guestEntry, observe,
            floppy ? initial : null, hook, Hash(initial), cpu);
        return File.ReadAllBytes(media);
    }

    private async Task BootMedia(string directory, string profile, string label, string handler, string media,
        bool write, string? guestEntry, Action<byte, long, int, int, int>? observe = null,
        byte[]? floppyImage = null, WritableHook? storage = null, string? initialHash = null, M68kCpuModel cpu = M68kCpuModel.M68020)
    {
        Directory.CreateDirectory(Path.Combine(directory, "boot", "S"));
        var floppy = floppyImage != null;
        var kind = profile.StartsWith("ofs") ? "Ofs" : profile.StartsWith("ffs") ? "Ffs" : profile == "sfs" ? "Sfs" : "Sfs2";
        if (profile.EndsWith("-dd")) kind += "Dd";
        if (profile.EndsWith("-hd")) kind += "Hd";
        var entry = guestEntry ?? ("CorrectnessProbe::" + (write ? "Write" : "Read") + kind);
        var probe = Probes.GetOrAdd(entry, _ =>
        {
            var compiled = AmigaM68kCompiler.Compile(new M68kCompilationRequest
            {
                AssemblyPath = typeof(CorrectnessProbe).Assembly.Location,
                EntryPoint = "CopperFS.Kickstart.Tests." + entry,
                Cpu = M68kCpuTarget.M68000, OutputFormat = M68kOutputFormat.Hunk,
                RuntimeProfile = M68kRuntimeProfile.Application, ExceptionMode = M68kExceptionMode.Full,
                IncludedExportNames = []
            });
            Assert.True(compiled.FrameworkAnalysis.IsCompatible); Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
            return compiled.Image;
        });
        File.WriteAllBytes(Path.Combine(directory, "boot", "PROBE"), probe);
        File.Copy(handler, Path.Combine(directory, "boot", "HANDLER"), true);
        File.WriteAllText(Path.Combine(directory, "boot", "S", "startup-sequence"), "PROBE\n");
        var boot = await FileSystemAdf.CreateAsync(Path.Combine(directory, "boot"), "Correctness");
        var rom = File.ReadAllBytes(Environment.GetEnvironmentVariable("COPPERSHARP_KICKSTART31_ROM")!);
        var hook = storage ?? new WritableHook();
        uint control = 0, pointer = 0; var octets = 0; var done = false; var failed = false;
        uint stage = 0, error = 0;
        long frames; uint pc;
        var watch = Stopwatch.StartNew();
        using (var machine = new LightweightA500Machine(new()
        {
            CpuModel = cpu, ChipRamBytes = 512 * 1024, FastRamBytes = 2 * 1024 * 1024,
            SlowRamBytes = 0, FramebufferWidth = 908, FloppyDriveCount = floppy ? 2 : 1,
            FloppyDriveTypes = floppyImage?.Length == 3520 * 512
                ? [LightweightFloppyDriveType.DoubleDensity, LightweightFloppyDriveType.HighDensity] : [],
            Hardfiles = floppy ? [] : [new(0, media, readOnly: false) { ExposeBootNode = false, IoHook = hook }]
        }))
        {
            machine.LoadKickstart(rom); machine.MountAdf(0, boot); machine.SetDriveWriteProtected(0, true);
            if (floppy) { machine.MountAdf(1, floppyImage!); machine.SetDriveWriteProtected(1, !write); }
            machine.Reset();
            machine.ParallelDataWritten += (value, _, busCycle) =>
            {
                if (octets != 0) { pointer = pointer << 8 | value; if (--octets == 0) control = pointer; return; }
                if (value == 200) { octets = 4; pointer = 0; }
                if (value == 210) done = true;
                if (value == 250) failed = true;
                observe?.Invoke(value, busCycle, hook.ReadCommands, hook.WriteCommands, hook.Flushes);
            };
            for (var frame = 0; frame < 100000 && watch.Elapsed < TimeSpan.FromMinutes(10) && !done && !failed; frame++)
                machine.ExecuteFrame();
            frames = machine.CompletedFrames; pc = machine.Cpu.ProgramCounter;
            if (floppy) File.WriteAllBytes(media, machine.ExportAdf(1));
            if (control != 0)
            {
                var memory = control < machine.ChipRam.Length ? machine.ChipRam : machine.FastRam;
                var at = control < machine.ChipRam.Length ? control : control - machine.FastRamBase!.Value;
                stage = BinaryPrimitives.ReadUInt32BigEndian(memory.Span.Slice((int)at + 20, 4));
                error = BinaryPrimitives.ReadUInt32BigEndian(memory.Span.Slice((int)at + 24, 4));
            }
        }
        var result = new { Profile = profile, Cpu = cpu.ToString(), Label = label, Passed = done && !failed, Stage = stage, IoErr = error,
            PC = pc.ToString("X8"), Frames = frames, HostSeconds = watch.Elapsed.TotalSeconds,
            HandlerSha256 = Hash(File.ReadAllBytes(handler)), ProbeSha256 = Hash(probe), KickstartSha256 = Hash(rom),
            InitialSha256 = initialHash, ResultSha256 = initialHash == null ? null : Hash(File.ReadAllBytes(media)), hook.Writes, hook.Flushes };
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(result, Json));
        output.WriteLine(JsonSerializer.Serialize(result));
        Assert.True(result.Passed, $"{profile}/{label}: stage {stage}, IoErr {error}, PC {pc:X8}, {frames} frames");
        if (!write && initialHash != null) Assert.Equal(result.InitialSha256, result.ResultSha256);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private class WritableHook(Action<FileStream>? afterFlush = null) : IAmigaHardfileIoHook
    {
        public int ReadCommands, WriteCommands, Writes, Flushes;
        public bool FlushOnDispose => false;
        public void ObserveOpen(int unit) { }
        public void ObserveCommand(ushort command)
        {
            if (command is 2 or 24) ReadCommands++;
            if (command is 3 or 25) WriteCommands++;
        }
        public virtual void Read(FileStream backing, long offset, Span<byte> destination)
        { backing.Position = offset; backing.ReadExactly(destination); }
        public virtual void Write(FileStream backing, long offset, ReadOnlySpan<byte> source)
        { Writes++; backing.Position = offset; backing.Write(source); }
        public virtual void Flush(FileStream backing) { Flushes++; backing.Flush(true); afterFlush?.Invoke(backing); }
    }
}
