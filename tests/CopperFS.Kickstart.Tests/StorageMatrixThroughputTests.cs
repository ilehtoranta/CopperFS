using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
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

public sealed class StorageMatrixTheoryAttribute : TheoryAttribute
{
    public StorageMatrixTheoryAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("COPPERFS_STORAGE_MATRIX_MANIFEST")))
            Skip = "Supply COPPERFS_STORAGE_MATRIX_MANIFEST and licensed ROM inputs.";
    }
}

public sealed class StorageMatrixThroughputTests(ITestOutputHelper output)
{
    private sealed record Inputs(string NativeManifest, string OriginalFfs, string OriginalSfs2,
        string A1200Rom, string LegacyRom, LightweightDriveTimingProfile? Hdd = null, LightweightDriveTimingProfile? Cf = null,
        string[]? Scenarios = null, string[]? Variants = null, string A1200Rom31 = "", string[]? Profiles = null,
        string? IdeLayout=null,string Protocol="kickstart-storage-v1");
    private sealed record Pair(string Name, bool Sfs2, string Before, string After);
    private sealed record NativeInputs(Pair[] Pairs);
    private readonly record struct Traffic(long Commands, long TransportBytes, long MediaFetches, long MediaBytes, long PioBytes, long Completions)
    {
        public static Traffic operator -(Traffic a, Traffic b) => new(a.Commands-b.Commands, a.TransportBytes-b.TransportBytes,
            a.MediaFetches-b.MediaFetches, a.MediaBytes-b.MediaBytes, a.PioBytes-b.PioBytes, a.Completions-b.Completions);
    }
    private sealed record Measurement(string Scenario, string Profile, string Variant, uint PacketBytes, uint ReadBytes, uint Trial,
        string Device, string MemoryClass, uint BufferAddress, uint MountControl,
        long ReadBusCycles, long SessionBusCycles, long ReadCpuTimelineCycles, long SessionCpuTimelineCycles,
        long ReadNativeTimelineCycles, long SessionNativeTimelineCycles, double ReadGuestSeconds, double SessionGuestSeconds,
        double ReadKiBPerSecond, double SessionKiBPerSecond, Traffic ReadTraffic, Traffic SessionTraffic,
        string HandlerSha256, string ProbeSha256, string MediaSha256, string DataSha256, string RomSha256, string TimingSha256,
        string MediaLayout,uint PartitionFirstSector,uint PartitionSectors);
    public static IEnumerable<object[]> Cases()
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_STORAGE_MATRIX_MANIFEST");
        var inputs = File.Exists(path) ? JsonSerializer.Deserialize<Inputs>(File.ReadAllText(path!)) : null;
        var scenarios = inputs?.Scenarios ?? (inputs is null ? ["legacy-zero"] : StorageMatrixDefinition.DefaultScenarios);
        var variants = inputs?.Variants ?? (inputs is null ? ["original"] : ["original", "cached", "bypass"]);
        foreach (var scenario in scenarios)
        foreach (var profile in StorageMatrixDefinition.Profiles(inputs?.Profiles))
        foreach (var variant in variants) yield return [scenario, profile, variant];
    }
    private static readonly Lazy<M68kCompilationResult> FfsProbe = new(() => CompileProbe(false));
    private static readonly Lazy<M68kCompilationResult> SfsProbe = new(() => CompileProbe(true));
    [Fact]
    public void SequentialProbesCompileWithoutManagedAllocationSites()
    {
        _=FfsProbe.Value;
        _=SfsProbe.Value;
    }
    private static M68kCompilationResult CompileProbe(bool sfs2)
    {
        var result = AmigaM68kCompiler.Compile(new()
        {
            AssemblyPath = typeof(SequentialReadProbe).Assembly.Location,
            EntryPoint = "CopperFS.Kickstart.Tests.SequentialReadProbe::" + (sfs2 ? "RunSfs2" : "RunFfs"),
            IncludedExportNames = [], Cpu = M68kCpuTarget.M68000, OutputFormat = M68kOutputFormat.Hunk,
            RuntimeProfile = M68kRuntimeProfile.Application, ExceptionMode = M68kExceptionMode.Full
        });
        Assert.True(result.FrameworkAnalysis.IsCompatible); Assert.Empty(result.FrameworkAnalysis.ManagedAllocationSites); return result;
    }

    [StorageMatrixTheory]
    [MemberData(nameof(Cases))]
    public async Task ColdReadThroughRealDosAndConfiguredStorage(string scenario, string profile, string variant)
    {
        var selectedMachine=StorageMatrixDefinition.Parse(scenario);
        var manifest = Environment.GetEnvironmentVariable("COPPERFS_STORAGE_MATRIX_MANIFEST")!;
        var inputs = JsonSerializer.Deserialize<Inputs>(File.ReadAllText(manifest))!;
        using (var json=JsonDocument.Parse(File.ReadAllText(manifest)))
            inputs=inputs with { Hdd=StorageTimingJson.Override(json.RootElement,"Hdd",LightweightDriveTimingProfile.Hdd),
                Cf=StorageTimingJson.Override(json.RootElement,"Cf",LightweightDriveTimingProfile.Cf) };
        var pair = JsonSerializer.Deserialize<NativeInputs>(File.ReadAllText(inputs.NativeManifest))!.Pairs.Single(p => p.Name == profile);
        var path = variant == "original" ? pair.Sfs2 ? inputs.OriginalSfs2 : inputs.OriginalFfs : pair.After;
        var handlerHash = Hash(File.ReadAllBytes(path));
        var compiled = (pair.Sfs2 ? SfsProbe : FfsProbe).Value;
        var media = pair.Sfs2 ? ReadMedia.Sfs2() : ReadMedia.Ffs();
        if(inputs.IdeLayout is not (null or StorageIdePartition.Layout)) throw new InvalidDataException("Unknown IDE fixture layout.");
        var partitioned=selectedMachine.Ide && inputs.IdeLayout==StorageIdePartition.Layout;
        var mediaLayout=partitioned ? StorageIdePartition.Layout : "flat-v1";
        var firstSector=partitioned ? StorageIdePartition.FirstSector : 0u;
        var partitionSectors=(uint)media.Length/512;
        if(partitioned) media=StorageIdePartition.Create(media,pair.Sfs2);
        var mediaHash = Hash(media);
        var ide = selectedMachine.Ide; var legacy = selectedMachine.Legacy; var stock = selectedMachine.Stock;
        var timing = scenario.EndsWith("hdd") ? inputs.Hdd ?? LightweightDriveTimingProfile.Hdd :
            scenario.EndsWith("cf") ? inputs.Cf ?? LightweightDriveTimingProfile.Cf : null;
        var rom = StorageMatrixDefinition.LoadRom(legacy ? inputs.LegacyRom : selectedMachine.Kickstart31 ? inputs.A1200Rom31 : inputs.A1200Rom); var romHash = Hash(rom);
        StorageMatrixDefinition.ValidateRom(selectedMachine,rom);
        var root = Path.Combine(Path.GetDirectoryName(manifest)!, "matrix", scenario, profile + "-" + variant);
        Directory.CreateDirectory(Path.Combine(root, "boot", "S"));
        File.WriteAllBytes(Path.Combine(root, "boot", "PROBE"), compiled.Image);
        File.Copy(path, Path.Combine(root, "boot", "HANDLER"), true);
        File.WriteAllText(Path.Combine(root, "boot", "S", "startup-sequence"), "PROBE\n");
        var boot = await FileSystemAdf.CreateAsync(Path.Combine(root, "boot"), "ReadBench");
        var mediaPath = Path.Combine(root, "read.hdf"); File.WriteAllBytes(mediaPath, media);
        var controlWord = variant == "bypass" ? ide ? 0x43504302u : 0x43504301u : 0;
        var timingHash = Hash(JsonSerializer.SerializeToUtf8Bytes(timing));
        File.WriteAllText(Path.Combine(root,"configuration.json"),JsonSerializer.Serialize(new
        {
            inputs.Protocol,Scenario=scenario,Cpu=legacy ? "M68020" : "M68EC020",RomSha256=romHash,
            RomVersion=selectedMachine.RomVersion,RomRevision=selectedMachine.RomRevision,
            ChipRamBytes=legacy ? 524288 : 2097152,FastRamBytes=legacy ? 2097152 : stock ? 0 : 8388608,
            Timing=timing,TimingSha256=timingHash,IdeWaitMotherTicks=ide ? 5 : 0,
            AaGaylePhysicalTimingVerified=false,ColdStartEachTrial=true,
            MediaLayout=mediaLayout,PartitionFirstSector=firstSector,PartitionSectors=partitionSectors
        },new JsonSerializerOptions { WriteIndented=true }));
        var measurements = new List<Measurement>();
        var profileSizes=Environment.GetEnvironmentVariable("COPPERFS_STORAGE_CPU_PROFILE");
        var sizes = profileSizes is not null ? profileSizes.Split(',').Select(uint.Parse).ToArray() :
            Environment.GetEnvironmentVariable("COPPERFS_STORAGE_SMOKE") == "1" ? new[] { 4096u } : new[] { 64u, 512u, 4096u, 65536u };
        Assert.True(sizes.Length!=0 && sizes.Distinct().Count()==sizes.Length && sizes.All(n=>n is 64 or 512 or 4096 or 65536));
        var trials = profileSizes is not null || Environment.GetEnvironmentVariable("COPPERFS_STORAGE_SMOKE") == "1" ? 1u : 3u;
        if(profileSizes is not null) File.WriteAllText(Path.Combine(root,"diagnostic-cpu-profile.json"),
            JsonSerializer.Serialize(new { Sizes=sizes, ScalarExecution=true, Acceptance=false }));
        for (uint trial = 0; trial < trials; trial++)
        foreach (var packetBytes in sizes)
        {
            var hook = new ReadOnlyHook(); var observer = new Observer { TracePath = Path.Combine(root, "ata-live.txt") };
            if (profileSizes is not null) observer.Events = [];
            var hardfile = new AmigaHardfileConfiguration(0, mediaPath, true) { ExposeBootNode = false, IoHook = hook };
            using var machine = new LightweightA500Machine(new()
            {
                CpuModel = legacy ? M68kCpuModel.M68020 : M68kCpuModel.M68EC020,
                AgnusModel = legacy ? LightweightAgnusModel.Mos8371 : LightweightAgnusModel.Mos8374Alice,
                DeniseModel = legacy ? LightweightDeniseModel.Mos8362 : LightweightDeniseModel.Lisa4203,
                ChipRamBytes = legacy ? 524288 : 2097152, SlowRamBytes = 0,
                FastRamBytes = legacy ? 2097152 : stock ? 0 : 8388608, FramebufferWidth = 908,
                Hardfiles = ide ? [] : [hardfile],
                HardfileTimings = !ide && timing is not null ? new Dictionary<int, LightweightDriveTimingProfile> { [0] = timing } : new Dictionary<int, LightweightDriveTimingProfile>(),
                IdeMaster = ide ? new(hardfile) { Timing = timing! } : null,
                StorageObserver = observer
            });
            observer.Machine = machine;
            hook.Machine=machine; hook.TracePath=Path.Combine(root,"hdf-commands.txt");
            machine.LoadKickstart(rom); machine.MountAdf(0, boot); machine.SetDriveWriteProtected(0, true); machine.Reset();
            using var mountTrace=Environment.GetEnvironmentVariable("COPPERFS_STORAGE_MOUNT_TRACE")=="1"
                ? new StorageMountTrace(machine,rom,Path.Combine(root,"mount-trace.txt")) : null;
            Assert.False(profileSizes is not null && mountTrace is not null,"Select one scalar diagnostic observer.");
            var cpuProfile=profileSizes is not null ? new ReadCpuProfile(machine,path,rom) : null;
            uint control = 0, pointer = 0, buffer = 0, length = 0; int octets = 0;
            var done = false; var failed = false; var captured = false; var dataHash = "";
            long sessionBus = 0, readBus = 0, stopBus = 0, sessionCpu = 0, readCpu = 0, stopCpu = 0;
            long sessionNative=0,readNative=0,stopNative=0;
            Traffic sessionTraffic = default, readTraffic = default, stopTraffic = default;
            Traffic Snapshot() => new(timing is null ? hook.Commands : observer.Commands,
                timing is null ? hook.Bytes : observer.TransportBytes, hook.Reads, hook.Bytes, observer.PioBytes, observer.Completions);
            uint Word(uint address) => BinaryPrimitives.ReadUInt32BigEndian(Memory(machine, address, 4).Span);
            void Write(uint address, uint value) => BinaryPrimitives.WriteUInt32BigEndian(MemoryMarshal.AsMemory(Memory(machine, address, 4)).Span, value);
            machine.ParallelDataWritten += (value, cpu, bus) =>
            {
                if (octets != 0)
                {
                    pointer = pointer << 8 | value;
                    if (--octets == 0)
                    {
                        control = pointer; Write(control + 28, controlWord); Write(control + 32, stock ? 2u : 4u);
                        Write(control + 36, ide ? 1u : 0u); Write(control + 40, 0); Write(control + 44, packetBytes); Write(control + 48, trial);
                        Write(control+288,firstSector/StorageIdePartition.SectorsPerCylinder);
                        Write(control+292,ide && mountTrace is not null ? 1u : 0u);
                    }
                    return;
                }
                if (value == 200) { pointer = 0; octets = 4; }
                else if (value == 250) failed = true;
                else if (value == 210) done = true;
                else if (value == 201)
                {
                    observer.Phase = "open";
                    mountTrace?.Control(control);
                    buffer = Word(control + 12); length = Word(control + 4);
                    cpuProfile?.SetCallerBuffer(buffer,length);
                    cpuProfile?.Begin($"{packetBytes}-open",bus);
                    Assert.Equal(packetBytes, Word(control)); Assert.Equal(trial, Word(control + 8));
                    sessionBus = bus; sessionCpu = cpu; sessionTraffic = Snapshot();
                    sessionNative=machine.Cpu.NativeCycles;
                }
                else if (value == 202)
                {
                    observer.Phase = "read";
                    readBus = bus; readCpu = cpu; readNative=machine.Cpu.NativeCycles; readTraffic = Snapshot();
                    cpuProfile?.End(root,bus); cpuProfile?.Begin($"{packetBytes}-read",bus);
                }
                else if (value == 203)
                {
                    observer.Phase = "close";
                    stopBus = bus; stopCpu = cpu; stopTraffic = Snapshot();
                    stopNative=machine.Cpu.NativeCycles;
                    cpuProfile?.End(root,bus); cpuProfile?.Begin($"{packetBytes}-close",bus);
                    dataHash = Hash(Memory(machine, buffer, (int)length).Span);
                    Assert.Equal(Hash(ReadMedia.Payload.AsSpan(0, (int)length)), dataHash);
                }
                else if (value == 204)
                {
                    cpuProfile?.End(root,bus);
                    const double hz = 7093790;
                    var sessionCycles = bus - sessionBus; var readCycles = stopBus - readBus;
                    Assert.True(sessionCycles >= readCycles && readCycles > 0);
                    Assert.True((buffer & 3) == 0);
                    if (stock) Assert.True(buffer + length <= 2097152);
                    else Assert.True(buffer >= 0x200000 && buffer + length <= (legacy ? 0x400000u : 0xA00000u));
                    measurements.Add(new(scenario, profile, variant, packetBytes, length, trial, ide ? "scsi.device" : "copperhdf.device",
                        stock ? "Chip" : "Fast", buffer, controlWord, readCycles, sessionCycles, stopCpu-readCpu, cpu-sessionCpu,
                        stopNative-readNative,machine.Cpu.NativeCycles-sessionNative,readCycles/hz,sessionCycles/hz,
                        length / 1024.0 * hz / readCycles, length / 1024.0 * hz / sessionCycles,
                        stopTraffic-readTraffic, Snapshot()-sessionTraffic, handlerHash, Hash(compiled.Image), mediaHash, dataHash, romHash, timingHash,
                        mediaLayout,firstSector,partitionSectors));
                    File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
                    captured = true;
                    File.WriteAllText(Path.Combine(root, "progress.txt"), $"{measurements.Count}/{sizes.Length*trials}: {packetBytes} B trial {trial}");
                }
            };
            var watch = Stopwatch.StartNew();
            for (var frame = 0; frame < 60000 && watch.Elapsed < TimeSpan.FromMinutes(12) && !done && !failed; frame++)
            {
                if(cpuProfile is not null) cpuProfile.ExecuteFrame();
                else if(mountTrace is null) machine.ExecuteFrame(); else mountTrace.ExecuteFrame();
            }
            if (!done || failed)
            {
                File.WriteAllLines(Path.Combine(root, "ata-trace.txt"), observer.Trace);
                File.WriteAllText(Path.Combine(root,"failure.json"),JsonSerializer.Serialize(new
                {
                    Stage=control==0 ? 0 : Word(control+20),DosError=control==0 ? 0 : Word(control+24),
                    GeometryIoError=control==0 ? 0 : unchecked((sbyte)Word(control+56)),
                    GeometryProbed=mountTrace is not null,
                    GeometryTotalSectors=control==0 ? 0 : Word(control+60),RomSha256=romHash,
                    RomVersion=selectedMachine.RomVersion,RomRevision=selectedMachine.RomRevision,
                    observer.Commands,observer.PioBytes
                },new JsonSerializerOptions { WriteIndented=true }));
            }
            Assert.True(done && !failed && captured, $"{scenario}/{profile}/{variant}: {packetBytes} B trial {trial}; PC={machine.Cpu.ProgramCounter:X8}; " +
                $"stage={(control==0 ? 0 : Word(control+20))}; IoErr={(control==0 ? 0 : Word(control+24))}; ATA commands={observer.Commands}; PIO={observer.PioBytes}");
            Assert.Equal(0, hook.Writes);
            if (ide)
            {
                Assert.Equal(0,hook.Commands); // No CopperHDF gateway on the native IDE path.
                Assert.True(observer.PioBytes >= length, "Native IDE must deliver the payload through data-register strobes.");
            }
            Assert.Equal(mediaHash, Hash(File.ReadAllBytes(mediaPath)));
            if (observer.Events is not null)
                File.WriteAllText(Path.Combine(root, $"{packetBytes}-storage-events.json"), JsonSerializer.Serialize(observer.Events));
            output.WriteLine($"{scenario}/{profile}/{variant}: {packetBytes} B trial {trial}: {measurements[^1].SessionKiBPerSecond:F3} KiB/s");
        }
        Assert.Equal(sizes.Length * trials, (uint)measurements.Count);
    }

    private static ReadOnlyMemory<byte> Memory(LightweightA500Machine machine, uint address, int length)
    {
        if (address < machine.ChipRam.Length && length <= machine.ChipRam.Length-address) return machine.ChipRam.Slice((int)address, length);
        if (machine.FastRamBase is uint start && address >= start && address-start <= machine.FastRam.Length &&
            length <= machine.FastRam.Length-(address-start)) return machine.FastRam.Slice((int)(address-start), length);
        throw new InvalidDataException($"Guest range {address:X8}+{length} is outside fitted RAM.");
    }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private sealed class Observer : ILightweightStorageObserver
    {
        internal readonly record struct StorageEvent(string Kind, string Phase, ulong Sector, uint Bytes, long Cycle);
        internal List<StorageEvent>? Events;
        internal string Phase = "boot";
        internal long Commands, TransportBytes, PioBytes, Completions;
        private readonly bool tracing = Environment.GetEnvironmentVariable("COPPERFS_STORAGE_TRACE") == "1";
        internal readonly List<string> Trace = [];
        internal string? TracePath;
        internal LightweightA500Machine? Machine;
        private uint lastAddress=uint.MaxValue, lastValue; private bool lastWrite;
        public void ReadCommand(bool ide, ulong sector, uint bytes, long cycle)
        { Commands++; TransportBytes += bytes; Record("command", sector, bytes, cycle); }
        public void MediaRead(ulong sector, long cycle) => Record("media", sector, 512, cycle);
        public void PioRead(int bytes, long cycle) => PioBytes += bytes;
        public void Completed(bool ide, long cycle) { Completions++; Record("complete", 0, 0, cycle); }
        private void Record(string kind, ulong sector, uint bytes, long cycle)
        {
            if (Events is { Count: < 100000 }) Events.Add(new(kind, Phase, sector, bytes, cycle));
        }
        public void AtaAccess(uint address, int bytes, bool write, uint value, long cycle)
        {
            if (!write && !lastWrite && address==lastAddress && value==lastValue) return;
            lastAddress=address; lastValue=value; lastWrite=write;
            if (tracing && Trace.Count < 4096 && address >= 0xDA0000)
            {
                Trace.Add($"{cycle} {(write ? 'W' : 'R')}{bytes} {address:X8} {value:X8}");
                if (write && address == 0xDA201C && Machine is { } machine)
                {
                    Trace.Add($"PC={machine.Cpu.LastInstructionProgramCounter:X8} D={string.Join(',', machine.Cpu.D.Select(v => v.ToString("X8")))} A={string.Join(',', machine.Cpu.A.Select(v => v.ToString("X8")))}");
                    for (var i = 0; i < 7; i++)
                        try { Trace.Add($"A{i}={Convert.ToHexString(Memory(machine, machine.Cpu.A[i], 256).Span)}"); } catch (InvalidDataException) { }
                }
                if (write && TracePath is not null) File.WriteAllLines(TracePath, Trace);
            }
        }
    }
    private sealed class ReadOnlyHook : IAmigaHardfileIoHook
    {
        internal long Commands, Bytes, Writes, Reads;
        internal LightweightA500Machine? Machine;
        internal string? TracePath;
        private readonly bool tracing=Environment.GetEnvironmentVariable("COPPERFS_STORAGE_TRACE")=="1";
        private int traced;
        public bool FlushOnDispose => false;
        public void ObserveCommand(ushort command)
        {
            if (command is 2 or 24) Commands++;
            if(tracing && traced++<4096 && Machine is { } machine && TracePath is { } path)
            {
                var address=machine.Cpu.A[1];
                try
                {
                    var request=Memory(machine,address,48).Span;
                    File.AppendAllText(path,$"{machine.Cycle} PC={machine.Cpu.LastInstructionProgramCounter:X8} IO={address:X8} cmd={command} raw={Convert.ToHexString(request)}\n");
                }
                catch(InvalidDataException) { }
            }
        }
        public void Read(FileStream stream, long offset, Span<byte> destination) { stream.Position=offset; stream.ReadExactly(destination); Bytes+=destination.Length; Reads++; }
        public void Write(FileStream stream, long offset, ReadOnlySpan<byte> source) { Writes++; throw new InvalidOperationException("Read benchmark attempted a write."); }
        public void Flush(FileStream stream) { }
    }
}
