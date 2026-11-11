using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Amiga;
using Copper68k;
using CopperDisk;
using CopperMod.Amiga.Lightweight;
using CopperSharp.Compiler;
using CopperSharp.Compiler.BootAdf;
using CopperSharp.Targets.Amiga;
using Xunit.Abstractions;
using Task = System.Threading.Tasks.Task;

namespace CopperFS.Kickstart.Tests;

public sealed class KickstartReadTheoryAttribute : TheoryAttribute
{
    public KickstartReadTheoryAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("COPPERSHARP_KICKSTART31_ROM")) ||
            !File.Exists(Environment.GetEnvironmentVariable("COPPERFS_KICKSTART_READ_MANIFEST")))
            Skip = "Supply a licensed Kickstart 3.1 ROM and COPPERFS_KICKSTART_READ_MANIFEST.";
    }
}

public sealed class SequentialReadThroughputTests(ITestOutputHelper output)
{
    private sealed record Pair(string Name, bool Sfs2, string Before, string After,
        uint BeforeControl = 0, uint AfterControl = 0);
    private sealed record Manifest(Pair[] Pairs, bool PrimeOpen = false, bool MeasureMount = false, uint PhaseDelay = 0);
    private sealed record DeviceRead(long ByteOffset, int Length);
    private sealed record Phase(long BusCycles, int ReadRequests, long DeviceReadBytes, DeviceRead[] Reads);
    private sealed record MountOpen(Phase Mount, Phase FirstOpen)
    {
        public long CombinedBusCycles => Mount.BusCycles + FirstOpen.BusCycles;
    }
    private sealed record Result(string Profile, string Variant, uint PacketBytes, uint ReadBytes, uint Trial,
        long OpenBusCycles, long ReadBusCycles, long CloseBusCycles, long SessionBusCycles, long ReadCpuCycles, long SessionCpuCycles,
        double ReadSeconds, double SessionSeconds, double ReadKiBPerSecond, double SessionKiBPerSecond,
        int ReadRequests, long DeviceReadBytes, string DataSha256, string MediaSha256, string HandlerSha256,
        string ProbeSha256, string KickstartSha256, uint BufferAddress, double HostSeconds, uint MountControl, bool PrimeOpen,
        MountOpen? RemountAndFirstOpen);

    [KickstartReadTheory]
    [InlineData("ffs", "before")] [InlineData("ffs", "after")]
    [InlineData("sfs-classic", "before")] [InlineData("sfs-classic", "after")]
    [InlineData("sfs2-csun", "before")] [InlineData("sfs2-csun", "after")]
    [InlineData("sfs2-trst", "before")] [InlineData("sfs2-trst", "after")]
    public async Task ReadThroughKickstartDosAndLoadedHandler(string profile, string variant)
    {
        var manifestPath = Environment.GetEnvironmentVariable("COPPERFS_KICKSTART_READ_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath))!;
        var pair = manifest.Pairs.Single(p => p.Name == profile);
        var artifacts = Path.Combine(Path.GetDirectoryName(manifestPath)!, "kickstart");
        var runRoot = Path.Combine(artifacts, profile + "-" + variant);
        Directory.CreateDirectory(Path.Combine(runRoot, "boot", "S"));
        var handlerPath = variant == "before" ? pair.Before : pair.After;
        var mountControl = variant == "before" ? pair.BeforeControl : pair.AfterControl;
        var handlerHash = Hash(File.ReadAllBytes(handlerPath));
        var rom = File.ReadAllBytes(Environment.GetEnvironmentVariable("COPPERSHARP_KICKSTART31_ROM")!);
        var classic = profile == "sfs-classic";
        var media = classic ? ReadMedia.Sfs() : pair.Sfs2 ? ReadMedia.Sfs2() : ReadMedia.Ffs();
        var mediaHash = Hash(media); var mediaPath = Path.Combine(runRoot, "read.hdf");
        File.WriteAllBytes(mediaPath, media);
        var compiled = AmigaM68kCompiler.Compile(new M68kCompilationRequest
        {
            AssemblyPath = typeof(SequentialReadProbe).Assembly.Location,
            EntryPoint = "CopperFS.Kickstart.Tests.SequentialReadProbe::" + (classic ? "RunSfs" : pair.Sfs2 ? "RunSfs2" : "RunFfs"),
            Cpu = M68kCpuTarget.M68000, OutputFormat = M68kOutputFormat.Hunk,
            RuntimeProfile = M68kRuntimeProfile.Application, ExceptionMode = M68kExceptionMode.Full,
            IncludedExportNames = []
        });
        Assert.True(compiled.FrameworkAnalysis.IsCompatible);
        Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        File.WriteAllBytes(Path.Combine(runRoot, "boot", "PROBE"), compiled.Image);
        File.Copy(handlerPath, Path.Combine(runRoot, "boot", "HANDLER"), true);
        File.WriteAllText(Path.Combine(runRoot, "boot", "S", "startup-sequence"), "PROBE\n");
        var boot = await FileSystemAdf.CreateAsync(Path.Combine(runRoot, "boot"), "ReadBench");
        File.WriteAllBytes(Path.Combine(runRoot, "boot.adf"), boot);
        var hook = new ReadHook();
        using var machine = new LightweightA500Machine(new()
        {
            CpuModel = M68kCpuModel.M68020, ChipRamBytes = 512 * 1024,
            FastRamBytes = 2 * 1024 * 1024, SlowRamBytes = 0, FramebufferWidth = 908,
            FloppyDriveCount = 1,
            Hardfiles = [new(0, mediaPath, readOnly: true) { ExposeBootNode = false, IoHook = hook }]
        });
        machine.LoadKickstart(rom); machine.MountAdf(0, boot); machine.SetDriveWriteProtected(0, true); machine.Reset();
        var profiler = Environment.GetEnvironmentVariable("COPPERFS_KICKSTART_PROFILE") == "1"
            ? new ReadCpuProfile(machine, handlerPath, rom) : null;
        var profilePacket = profiler == null ? 0u :
            uint.Parse(Environment.GetEnvironmentVariable("COPPERFS_KICKSTART_PROFILE_PACKET") ?? "0");
        var profileAllTrials = profiler != null && Environment.GetEnvironmentVariable("COPPERFS_KICKSTART_PROFILE_ALL_TRIALS") == "1";
        Assert.True(profilePacket is 0 or 64 or 512 or 4096 or 65536, "Unknown profiling packet size.");
        hook.ObserveRead = command => profiler?.ObserveRead(command);
        uint control = 0, packet = 0, length = 0, trial = 0, buffer = 0;
        long sessionBus = 0, sessionCpu = 0, readBus = 0, readCpu = 0, stopBus = 0, stopCpu = 0;
        int baselineRequests = 0; long baselineBytes = 0;
        var pointerOctets = 0; uint pointer = 0; var done = false; var failed = false;
        var streamHash = ""; var results = new List<Result>(); var watch = Stopwatch.StartNew();
        var resultPath = Path.Combine(artifacts, profile + "-" + variant + ".json");
        Phase? mount = null;
        MountOpen? remountOpen = null;
        bool ProfileWorkload() => profilePacket == 0 || profilePacket == Word(control);
        string PhaseName(uint bytes, uint repetition, string phase) =>
            profileAllTrials ? $"{bytes}-trial{repetition}-{phase}" : $"{bytes}-{phase}";
        void FinishFirstOpen(long cycle)
        {
            remountOpen = new MountOpen(Assert.IsType<Phase>(mount), hook.EndPhase(cycle));
            mount = null;
        }
        uint Word(uint address) => BinaryPrimitives.ReadUInt32BigEndian(Memory(machine, address, 4));
        machine.ParallelDataWritten += (value, cpuCycle, busCycle) =>
        {
            if (pointerOctets != 0)
            {
                pointer = pointer << 8 | value;
                if (--pointerOctets == 0)
                {
                    control = pointer;
                    WriteWord(machine, control + 28, mountControl);
                    WriteWord(machine, control + 52, manifest.PrimeOpen ? 1u : 0u);
                    WriteWord(machine, control + 296, manifest.MeasureMount ? 1u : 0u);
                    WriteWord(machine, control + 300, manifest.PhaseDelay);
                }
                return;
            }
            if (value == 200) { pointer = 0; pointerOctets = 4; return; }
            if (value == 250) { failed = true; return; }
            if (value == 210) { done = true; return; }
            if (value == 205 || value == 208)
            {
                hook.BeginPhase(busCycle);
                if (value == 205 || ProfileWorkload())
                    profiler?.Begin(value == 205 ? "startup-mount" : PhaseName(Word(control), Word(control + 8), "remount"), busCycle);
            }
            if (value == 206 || value == 209)
            {
                mount = hook.EndPhase(busCycle);
                profiler?.End(runRoot, busCycle);
                hook.BeginPhase(busCycle);
                if (value == 206 || (manifest.PrimeOpen && ProfileWorkload()))
                    profiler?.Begin(value == 206 ? "startup-open" : PhaseName(Word(control), Word(control + 8), "priming-open"), busCycle);
            }
            if (value == 207)
            {
                var startup = new MountOpen(Assert.IsType<Phase>(mount), hook.EndPhase(busCycle));
                File.WriteAllText(Path.Combine(runRoot, "startup.json"), JsonSerializer.Serialize(startup));
                profiler?.End(runRoot, busCycle);
                mount = null;
            }
            if (value == 211)
            {
                FinishFirstOpen(busCycle);
                profiler?.End(runRoot, busCycle);
            }
            if (value == 201)
            {
                packet = Word(control); length = Word(control + 4); trial = Word(control + 8); buffer = Word(control + 12);
                sessionBus = busCycle; sessionCpu = cpuCycle;
                profiler?.SetCallerBuffer(buffer, length);
                if (profilePacket == 0 || profilePacket == packet) profiler?.Begin(PhaseName(packet, trial, "open"), busCycle);
            }
            if (value == 202)
            {
                if (manifest.MeasureMount && !manifest.PrimeOpen) FinishFirstOpen(busCycle);
                readBus = busCycle; readCpu = cpuCycle; baselineRequests = hook.ReadRequests; baselineBytes = hook.ReadBytes;
                profiler?.End(runRoot, busCycle);
                if (profilePacket == 0 || profilePacket == packet) profiler?.Begin(PhaseName(packet, trial, "read"), busCycle);
            }
            if (value == 203)
            {
                stopBus = busCycle; stopCpu = cpuCycle;
                profiler?.End(runRoot, busCycle);
                if (profilePacket == 0 || profilePacket == packet) profiler?.Begin(PhaseName(packet, trial, "close"), busCycle);
                streamHash = Hash(Memory(machine, buffer, (int)length));
                Assert.Equal(Hash(ReadMedia.Payload.AsSpan(0, (int)length)), streamHash);
            }
            if (value == 204)
            {
                const double hz = 7_093_790.0;
                profiler?.End(runRoot, busCycle);
                var readSeconds = (stopBus - readBus) / hz; var sessionSeconds = (busCycle - sessionBus) / hz;
                Assert.True(readSeconds > 0 && sessionSeconds >= readSeconds);
                var result = new Result(profile, variant, packet, length, trial, readBus - sessionBus, stopBus - readBus,
                    busCycle - stopBus, busCycle - sessionBus,
                    stopCpu - readCpu, cpuCycle - sessionCpu, readSeconds, sessionSeconds,
                    length / 1024.0 / readSeconds, length / 1024.0 / sessionSeconds,
                    hook.ReadRequests - baselineRequests, hook.ReadBytes - baselineBytes, streamHash,
                    mediaHash, handlerHash, Hash(compiled.Image), Hash(rom), buffer, watch.Elapsed.TotalSeconds, mountControl, manifest.PrimeOpen,
                    remountOpen);
                Assert.Equal(manifest.MeasureMount, remountOpen != null);
                remountOpen = null;
                Assert.True(result.OpenBusCycles > 0 && result.CloseBusCycles > 0);
                Assert.Equal(result.SessionBusCycles, result.OpenBusCycles + result.ReadBusCycles + result.CloseBusCycles);
                results.Add(result);
                File.WriteAllText(resultPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                File.WriteAllText(Path.Combine(runRoot, "progress.txt"), $"{profile} {variant}: {results.Count}/12; {packet} B: {result.ReadKiBPerSecond:F3} KiB/s");
                output.WriteLine($"{profile} {variant} {packet} B trial {trial}: read {result.ReadKiBPerSecond:F3} KiB/s; session {result.SessionKiBPerSecond:F3} KiB/s");
                if (Environment.GetEnvironmentVariable("COPPERFS_KICKSTART_SMOKE") == "1") done = true;
                if (profiler != null && !profileAllTrials && results.Count == 4) done = true;
            }
        };
        for (var frame = 0; frame < 500000 && watch.Elapsed < TimeSpan.FromMinutes(45) && !done && !failed; frame++)
            if (profiler == null) machine.ExecuteFrame(); else profiler.ExecuteFrame();
        Assert.True(done && !failed, $"{profile}/{variant}: status {machine.ParallelOutputPins}, stage {(control == 0 ? 0 : Word(control + 20))}, " +
            $"IoErr {(control == 0 ? 0 : Word(control + 24))}, PC {machine.Cpu.ProgramCounter:X8}, frames {machine.CompletedFrames}, rows {results.Count}");
        Assert.Equal(Environment.GetEnvironmentVariable("COPPERFS_KICKSTART_SMOKE") == "1" ? 1 : profiler != null && !profileAllTrials ? 4 : 12, results.Count);
        Assert.Equal(0, hook.WriteRequests); Assert.Equal(mediaHash, Hash(File.ReadAllBytes(mediaPath)));
    }

    private static ReadOnlySpan<byte> Memory(LightweightA500Machine machine, uint address, int bytes)
    {
        if (address < machine.ChipRam.Length && bytes <= machine.ChipRam.Length - address)
            return machine.ChipRam.Span.Slice((int)address, bytes);
        if (machine.FastRamBase is uint lower && address >= lower && address - lower <= machine.FastRam.Length &&
            bytes <= machine.FastRam.Length - (address - lower))
            return machine.FastRam.Span.Slice((int)(address - lower), bytes);
        throw new InvalidDataException($"Guest range {address:X8}+{bytes} is outside fitted RAM.");
    }
    private static void WriteWord(LightweightA500Machine machine, uint address, uint value)
    {
        if (address < machine.ChipRam.Length && 4 <= machine.ChipRam.Length - address)
            BinaryPrimitives.WriteUInt32BigEndian(System.Runtime.InteropServices.MemoryMarshal.AsMemory(machine.ChipRam).Span.Slice((int)address, 4), value);
        else if (machine.FastRamBase is uint lower && address >= lower && address - lower <= machine.FastRam.Length &&
            4 <= machine.FastRam.Length - (address - lower))
            BinaryPrimitives.WriteUInt32BigEndian(System.Runtime.InteropServices.MemoryMarshal.AsMemory(machine.FastRam).Span.Slice((int)(address - lower), 4), value);
        else throw new InvalidDataException("Probe control is outside fitted RAM.");
    }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed class ReadHook : IAmigaHardfileIoHook
    {
        internal Action<ushort>? ObserveRead;
        internal int ReadRequests, WriteRequests;
        internal long ReadBytes;
        private List<DeviceRead>? phaseReads;
        private long phaseStart, phaseStartBytes;
        private int phaseStartRequests;
        internal void BeginPhase(long cycle)
        {
            Assert.Null(phaseReads);
            phaseReads = []; phaseStart = cycle; phaseStartBytes = ReadBytes; phaseStartRequests = ReadRequests;
        }
        internal Phase EndPhase(long cycle)
        {
            var reads = Assert.IsType<List<DeviceRead>>(phaseReads);
            phaseReads = null;
            return new Phase(cycle - phaseStart, ReadRequests - phaseStartRequests, ReadBytes - phaseStartBytes, reads.ToArray());
        }
        public bool FlushOnDispose => false;
        public void ObserveOpen(int unit) { }
        public void ObserveCommand(ushort command)
        { if (command is 2 or 24) { ReadRequests++; ObserveRead?.Invoke(command); } if (command is 3 or 25) WriteRequests++; }
        public void Read(FileStream backing, long offset, Span<byte> destination)
        {
            backing.Position = offset; backing.ReadExactly(destination); ReadBytes += destination.Length;
            phaseReads?.Add(new DeviceRead(offset, destination.Length));
        }
        public void Write(FileStream backing, long offset, ReadOnlySpan<byte> source) => throw new InvalidOperationException("Read benchmark attempted a write.");
        public void Flush(FileStream backing) { }
    }
}
