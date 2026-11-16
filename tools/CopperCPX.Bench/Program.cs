using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Amiga;
using Copper68k;
using CopperDisk;
using CopperFS.Cpx4.Reference;
using CopperMod.Amiga.Lightweight;
using CopperSharp.Compiler;
using CopperSharp.Compiler.BootAdf;
using CopperSharp.Targets.Amiga;
using CopperStart.Floppy.Native.Tests;
using CopperSFS.Check;

internal static class Program
{
    private static readonly string Repo = FindRoot();
    private static readonly string ArtifactRoot = Environment.GetEnvironmentVariable("CPX_BENCH_ROOT") ?? Path.Combine(Repo,"artifacts","cpx4-crossfs-benchmark");
    private static readonly string CpxHandler = Environment.GetEnvironmentVariable("CPX_BENCH_HANDLER") ?? Path.Combine(Repo,"artifacts","native-cpx4-handler","CopperCPX4");
    private static string Input(string name) => Environment.GetEnvironmentVariable(name) ?? throw new ArgumentException($"Set {name} to the required external input path. See tools/CopperCPX.Bench/README.md.");
    private static bool Streaming;
    private static int PayloadBytes => Streaming?2097152:6003;
    private const int DiskBytes = 16 * 1024 * 1024;
    private const int SectorBytes = 512;
    private const uint DiskBlocks = DiskBytes / SectorBytes;
    private static byte[]? probe;

    private static async Task<int> Main(string[] args)
    {
        if(args.Length>1||args.Length==1&&args[0] is not ("mixed" or "stream"))
        { Console.Error.WriteLine("Usage: CopperCPX.Bench [mixed|stream]"); return 2; }
        Streaming=args.Length==1&&args[0]=="stream";
        string FfsHandler=Input("CPX_BENCH_FFS_HANDLER"),SfsHandler=Input("CPX_BENCH_SFS_HANDLER"),SfsBase=Input("CPX_BENCH_SFS_IMAGE"),RomPath=Input("CPX_BENCH_ROM");
        Directory.CreateDirectory(ArtifactRoot);
        foreach (var path in new[] { CpxHandler, FfsHandler, SfsHandler, SfsBase, RomPath })
            if (!File.Exists(path)) throw new FileNotFoundException("Required benchmark input is unavailable.", path);

        var cpx = new MemoryDevice(new byte[DiskBytes]);
        if(Environment.GetEnvironmentVariable("CPX_BENCH_INODES")=="1")
            Cpx4Volume<MemoryDevice>.FormatDirectInodes(cpx, SectorBytes, new byte[49152], 0x43505842454E4348,
                Environment.GetEnvironmentVariable("CPX_BENCH_SMALL")=="1");
        else if(Environment.GetEnvironmentVariable("CPX_BENCH_SHADOW")=="1")
            Cpx4Volume<MemoryDevice>.FormatShadow(cpx, SectorBytes, new byte[49152], 0x43505842454E4348);
        else Cpx4Volume<MemoryDevice>.Format(cpx, SectorBytes, new byte[36864], 0x43505842454E4348);
        File.WriteAllBytes(Path.Combine(ArtifactRoot, "cpx4-empty-16m.hdf"), cpx.Bytes);
        var ffs = CreateEmptyFfs();
        File.WriteAllBytes(Path.Combine(ArtifactRoot, "ffs-empty-16m.hdf"), ffs);
        var sfs = File.ReadAllBytes(SfsBase);
        File.WriteAllBytes(Path.Combine(ArtifactRoot, "sfs-1.279-base-16m.hdf"), sfs);

        var compiled = AmigaM68kCompiler.Compile(new M68kCompilationRequest
        {
            AssemblyPath = typeof(Probe).Assembly.Location,
            EntryPoint = (Streaming?typeof(StreamingProbe):typeof(Probe)).FullName + "::Entry",
            Cpu = M68kCpuTarget.M68000,
            OutputFormat = M68kOutputFormat.Hunk,
            RuntimeProfile = M68kRuntimeProfile.Application,
            ExceptionMode = M68kExceptionMode.Full,
            IncludedExportNames = []
        });
        if (!compiled.FrameworkAnalysis.IsCompatible || compiled.FrameworkAnalysis.ManagedAllocationSites.Count != 0)
            throw new InvalidOperationException("Benchmark probe is incompatible with the native compiler.");
        probe = compiled.Image;
        File.WriteAllBytes(Path.Combine(ArtifactRoot, "BENCHPROBE"), probe);

        var arms = new[]
        {
            new Arm("CPX4", CpxHandler, cpx.Bytes, 0),
            new Arm("Kickstart FFS 40.1", FfsHandler, ffs, 1),
            new Arm("SmartFilesystem 1.279", SfsHandler, sfs, 2)
        };
        var reports = new List<RunResult>();
        // Alternate handlers and reverse the order for the second trial.
        var order = new[] { arms[0], arms[1], arms[2], arms[2], arms[0], arms[1] };
        foreach (var arm in Environment.GetEnvironmentVariable("CPX_ONLY") == "1" ? new[] { arms[0] } : order)
        {
            var trial = reports.Count(r => r.Name == arm.Name) + 1;
            var result = await Run(arm, trial,RomPath);
            reports.Add(result);
            Console.WriteLine(JsonSerializer.Serialize(result));
        }
        if (Environment.GetEnvironmentVariable("CPX_ONLY") == "1") return reports.All(r=>r.Passed)?0:1;
        var summaries = reports.GroupBy(result => result.Name).Select(group =>
        {
            var mutation = group.Select(result => result.PhaseCycles["mutationCycles"]).Order().ToArray();
            var flush = group.Select(result => result.PhaseCycles["flushCycles"]).Order().ToArray();
            var remount = group.Select(result => result.PhaseCycles["remountCycles"]).Order().ToArray();
            var verify = group.Select(result => result.PhaseCycles["verifyCycles"]).Order().ToArray();
            var total = group.Select(result => result.PhaseCycles.Values.Sum()).Order().ToArray();
            return new
            {
                Name = group.Key,
                Trials = group.Count(),
                AllPassed = group.All(result => result.Passed),
                MutationCyclesMedian = Median(mutation), MutationCyclesRange = new[] { mutation[0], mutation[^1] },
                FlushCyclesMedian = Median(flush), FlushCyclesRange = new[] { flush[0], flush[^1] },
                RemountCyclesMedian = Median(remount), RemountCyclesRange = new[] { remount[0], remount[^1] },
                VerifyCyclesMedian = Median(verify), VerifyCyclesRange = new[] { verify[0], verify[^1] },
                TotalMeasuredCyclesMedian = Median(total), TotalMeasuredCyclesRange = new[] { total[0], total[^1] }
            };
        }).ToArray();
        double Median(long[] values) => values.Length % 2 == 0
            ? (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2.0
            : values[values.Length / 2];
        var report = new
        {
            Schema = 1,
            Machine = "Kickstart 3.1, emulated 68020, 512 KiB Chip + 2 MiB Fast, 512-byte sectors, 16 MiB hardfile, 32 DOS buffers",
            Workload = Streaming?"32 writes of 64 KiB (2 MiB); two partial overwrites; no-op truncates; rename; second file; ACTION_FLUSH; remount; complete read/verify.":"Create directory; write 8 KiB; two partial overwrites; truncate to 6003 twice; close and rename; second file; ACTION_FLUSH; remount; complete read/verify.",
            Timing = "Emulated bus-cycle deltas; host wall time is informational only.",
            Trials = reports,
            Summary = summaries,
            Ratios = new
            {
                Cpx4MutationVsKickstartFfs = summaries.Single(summary => summary.Name == "CPX4").MutationCyclesMedian /
                    summaries.Single(summary => summary.Name == "Kickstart FFS 40.1").MutationCyclesMedian,
                Cpx4MutationVsSfs1279 = summaries.Single(summary => summary.Name == "CPX4").MutationCyclesMedian /
                    summaries.Single(summary => summary.Name == "SmartFilesystem 1.279").MutationCyclesMedian,
                Cpx4TotalVsKickstartFfs = summaries.Single(summary => summary.Name == "CPX4").TotalMeasuredCyclesMedian /
                    summaries.Single(summary => summary.Name == "Kickstart FFS 40.1").TotalMeasuredCyclesMedian,
                Cpx4TotalVsSfs1279 = summaries.Single(summary => summary.Name == "CPX4").TotalMeasuredCyclesMedian /
                    summaries.Single(summary => summary.Name == "SmartFilesystem 1.279").TotalMeasuredCyclesMedian
            },
            Inputs = new
            {
                Cpx4HandlerSha256 = Hash(File.ReadAllBytes(CpxHandler)),
                FfsHandlerSha256 = Hash(File.ReadAllBytes(FfsHandler)),
                Sfs1279HandlerSha256 = Hash(File.ReadAllBytes(SfsHandler)),
                KickstartSha256 = Hash(File.ReadAllBytes(RomPath)),
                SfsBaseSha256 = Hash(sfs),
                ProbeSha256 = Hash(probe),
                DiskBytes,
                SectorBytes
            }
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(ArtifactRoot, "benchmark.json"), json);
        Console.WriteLine(json);
        return reports.All(result=>result.Passed)?0:1;
    }

    private static async Task<RunResult> Run(Arm arm, int trial,string RomPath)
    {
        var root = Path.Combine(ArtifactRoot, Safe(arm.Name), "trial-" + trial);
        Directory.CreateDirectory(Path.Combine(root, "boot", "S"));
        var media = Path.Combine(root, "disk.hdf");
        await File.WriteAllBytesAsync(media, arm.Media);
        File.WriteAllBytes(Path.Combine(root, "boot", "HANDLER"), File.ReadAllBytes(arm.Handler));
        File.WriteAllBytes(Path.Combine(root, "boot", "BENCHPROBE"), probe!);
        await File.WriteAllTextAsync(Path.Combine(root, "boot", "S", "startup-sequence"), "BENCHPROBE\n");
        var boot = await FileSystemAdf.CreateAsync(Path.Combine(root, "boot"), "FSBenchmark");
        var hook = new IoHook();
        var machine = new LightweightA500Machine(new()
        {
            CpuModel = M68kCpuModel.M68020,
            ChipRamBytes = 512 * 1024,
            FastRamBytes = 2 * 1024 * 1024,
            SlowRamBytes = 0,
            FramebufferWidth = 908,
            FloppyDriveCount = 1,
            Hardfiles = [new(0, media, readOnly: false)
            { ExposeBootNode = false, IoHook = hook }]
        });
        machine.LoadKickstart(await File.ReadAllBytesAsync(RomPath));
        machine.MountAdf(0, boot);
        machine.SetDriveWriteProtected(0, true);
        machine.Reset();

        uint control = 0, pointer = 0;
        int pointerBytes = 0;
        bool done = false, failed = false;
        var markers = new Dictionary<int, long>();
        var samples = new Dictionary<uint, int>();
        machine.ParallelDataWritten += (value, _, busCycle) =>
        {
            if (pointerBytes != 0)
            {
                pointer = (pointer << 8) | value;
                if (--pointerBytes == 0)
                {
                    control = pointer;
                    WriteMachineUInt32(machine, control + 60, arm.Flavor);
                }
                return;
            }
            if (value == 200) { pointerBytes = 4; pointer = 0; }
            else if (value == 210) done = true;
            else if (value == 250) failed = true;
            else if (value is 220 or 221 or 222 or 223 or 226 or 227 or 228 or 229)
                markers[value] = busCycle;
        };
        var watch = Stopwatch.StartNew();
        uint stage = 0, error = 0, detail = 0;
        int frames = 0;
        for (; frames < 20000 && watch.Elapsed < TimeSpan.FromMinutes(12) && !done && !failed; frames++)
        {
            machine.ExecuteFrame();
            if (markers.ContainsKey(220) && !markers.ContainsKey(221)) { uint pcSample=machine.Cpu.ProgramCounter; samples[pcSample]=samples.GetValueOrDefault(pcSample)+1; }
            if (frames != 0 && frames % 1000 == 0)
                Console.WriteLine($"Progress {arm.Name} trial {trial}: {frames} frames, {watch.Elapsed.TotalSeconds:F1}s host, PC={machine.Cpu.ProgramCounter:X8}, markers={string.Join(',', markers.Keys.Order())}.");
        }
        watch.Stop();
        if (control != 0)
        {
            stage = ReadMachineUInt32(machine, control + 20);
            error = ReadMachineUInt32(machine, control + 24);
            detail = ReadMachineUInt32(machine, control + 28);
        }
        var completedFrames = machine.CompletedFrames;
        var pc = machine.Cpu.ProgramCounter;
        uint handlerBase=control!=0?ReadMachineUInt32(machine,control+32):0;
        File.WriteAllText(Path.Combine(root,"pc-samples.json"),JsonSerializer.Serialize(new { handlerBase, samples },new JsonSerializerOptions { WriteIndented=true }));
        machine.Dispose();
        var finishedMedia = await File.ReadAllBytesAsync(media);
        var phases=Markers();
        var result = new RunResult(arm.Name, trial, done && !failed && phases.Values.All(cycles=>cycles>=0), stage, error, frames,
            completedFrames, watch.Elapsed.TotalSeconds, phases, hook.ReadCommands,
            hook.WriteCommands, hook.ReadRequests, hook.WriteRequests, hook.ReadBytes, hook.WriteBytes,
            hook.Flushes, Hash(arm.Media), Hash(finishedMedia), Detail: detail);
        if (!result.Passed)
            Console.WriteLine($"Failed {arm.Name} trial {trial}: stage {stage}, DOS IoErr {error}, PC {pc:X8}, {frames} frames.");
        try
        {
            if (result.Passed && arm.Name == "CPX4")
            {
                var checkDevice = new MemoryDevice(finishedMedia);
                var volume = new Cpx4Volume<MemoryDevice>(checkDevice, new byte[Cpx4Volume<MemoryDevice>.RequiredScratchBytes(4096, true)]);
                var check=volume.Check();
                if(!volume.TryGetInfo("Bench"u8,out var directory)||!volume.TryGetInfo(directory.Id,"Renamed.bin"u8,out var file)||file.Length!=(ulong)PayloadBytes) throw new InvalidDataException("CPX4 persisted payload is missing or has the wrong length.");
                byte[] payload=new byte[PayloadBytes];
                if(volume.Read(file.Id,0,payload)!=payload.Length||Hash(payload)!=Hash(ExpectedPayload())) throw new InvalidDataException("CPX4 persisted payload differs from expected data.");
                result = result with { IndependentCheck = JsonSerializer.Serialize(check)+"; payloadSha256="+Hash(payload) };
            }
            else if (result.Passed && arm.Name == "Kickstart FFS 40.1")
            {
                result = result with { IndependentCheck = CheckFfsPayload(finishedMedia) };
            }
            else if (arm.Name == "SmartFilesystem 1.279")
            {
                var check = SfsImageChecker.Check(new MemoryStream(finishedMedia), reservedBlocks: 2);
                var fileBytes = check.Entries.Where(entry => !entry.Directory).Aggregate(0UL, (sum, entry) => sum + entry.Size);
                var payload = check.Entries.Single(entry => entry.Path == "/Bench/Renamed.bin");
                if (payload.Size != (ulong)PayloadBytes || payload.Sha256 != Hash(ExpectedPayload()))
                    throw new InvalidDataException("SFS payload on disk differs from the expected benchmark data.");
                result = result with { IndependentCheck = $"entries={check.Entries.Count}; fileBytes={fileBytes}; freeBlocks={check.FreeBlocks}; files={JsonSerializer.Serialize(check.Entries)}" };
            }
        }
        catch (Exception exception) { result = result with { Passed=false, IndependentCheck = arm.Name+" checker failed: " + exception.Message }; }
        await File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result;

        Dictionary<string, long> Markers()
        {
            long Delta(int start, int end) => markers.TryGetValue(start, out var a) && markers.TryGetValue(end, out var b) ? b - a : -1;
            return new Dictionary<string, long>
            {
                ["mutationCycles"] = Delta(220, 221),
                ["flushCycles"] = Delta(226, 227),
                ["remountCycles"] = Delta(228, 229),
                ["verifyCycles"] = Delta(222, 223)
            };
        }
    }

    private static void WriteMachineUInt32(LightweightA500Machine machine, uint address, uint value)
    {
        var ram = address < machine.ChipRam.Length ? machine.ChipRam : machine.FastRam;
        var offset = address < machine.ChipRam.Length ? address : address - machine.FastRamBase!.Value;
        BinaryPrimitives.WriteUInt32BigEndian(System.Runtime.InteropServices.MemoryMarshal.AsMemory(ram).Span.Slice((int)offset, 4), value);
    }
    private static uint ReadMachineUInt32(LightweightA500Machine machine, uint address)
    {
        var ram = address < machine.ChipRam.Length ? machine.ChipRam : machine.FastRam;
        var offset = address < machine.ChipRam.Length ? address : address - machine.FastRamBase!.Value;
        return BinaryPrimitives.ReadUInt32BigEndian(ram.Span.Slice((int)offset, 4));
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static byte Initial(int i) => (byte)(i*17+(i>>9)*31+3);
    private static string FindRoot()
    {
        for(var directory=new DirectoryInfo(AppContext.BaseDirectory);directory is not null;directory=directory.Parent)
            if(File.Exists(Path.Combine(directory.FullName,"CopperFS.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Run the benchmark from a build within the CopperFS workspace.");
    }
    private static string Safe(string value) => value.Replace(' ', '-').Replace('.', '-');
    private static byte[] ExpectedPayload()
    {
        var bytes = new byte[PayloadBytes];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = i >= 4093 && i < 4862 ? (byte)((i - 4093) * 13 + 5) :
                i >= 1023 && i < 2047 ? (byte)((i - 1023) * 23 + 9) : Initial(Streaming?i&65535:i);
        return bytes;
    }
    private static string CheckFfsPayload(byte[] image)
    {
        if (image.Length != DiskBytes || Read(0, 0) != 0x444F5301) throw new InvalidDataException("FFS DOS type or image geometry is invalid.");
        uint root = Read(0, 8);
        Checksum(root, 20);
        uint directory = FindChild(root, "Bench");
        uint file = FindChild(directory, "Renamed.bin");
        if (Read(file, 508) != 0xFFFFFFFD) throw new InvalidDataException("FFS benchmark entry is not a file header.");
        Checksum(file, 20);
        uint size = Read(file, 324);
        if (size != PayloadBytes) throw new InvalidDataException($"FFS benchmark file length is {size}, expected {PayloadBytes}.");
        var actual = new byte[size];
        var table = file;
        uint copied = 0;
        for (uint index = 0; copied < size; index++)
        {
            if (index != 0 && index % 72 == 0)
            {
                table = Read(table, 504);
                if (table == 0) throw new InvalidDataException("FFS extension table ended before the file data.");
                Checksum(table, 20);
            }
            uint dataBlock = Read(table, 308 - (int)(index % 72) * 4);
            if (dataBlock < 2 || dataBlock >= DiskBlocks) throw new InvalidDataException("FFS data pointer is out of range.");
            uint count = Math.Min((uint)SectorBytes, size - copied);
            image.AsSpan(checked((int)dataBlock * SectorBytes), (int)count).CopyTo(actual.AsSpan((int)copied));
            copied += count;
        }
        string actualHash = Hash(actual), expectedHash = Hash(ExpectedPayload());
        if (actualHash != expectedHash) throw new InvalidDataException("FFS payload on disk differs from the expected benchmark data.");
        return $"root={root}; fileBytes={size}; payloadSha256={actualHash}; headerChecksumValid={ChecksumValid(file, 20)}";

        uint Read(uint block, int offset) => BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(checked((int)block * SectorBytes + offset), 4));
        uint FindChild(uint parent, string name)
        {
            uint hash = (uint)name.Length;
            foreach (char character in name) hash = (hash * 13 + char.ToUpperInvariant(character)) & 0x7FF;
            int slot = 24 + (int)(hash % 72) * 4;
            for (uint entry = Read(parent, slot); entry != 0; entry = Read(entry, 496))
            {
                int length = image[checked((int)entry * SectorBytes + 432)];
                string actualName = System.Text.Encoding.ASCII.GetString(image, checked((int)entry * SectorBytes + 433), length);
                if (actualName.Equals(name, StringComparison.OrdinalIgnoreCase)) return entry;
            }
            throw new InvalidDataException($"FFS entry '{name}' was not found.");
        }
        void Checksum(uint block, int offset)
        {
            if (!ChecksumValid(block, offset)) throw new InvalidDataException($"FFS checksum failed for block {block}.");
        }
        bool ChecksumValid(uint block, int offset)
        {
            uint sum = 0;
            for (int at = 0; at < SectorBytes; at += 4) sum = unchecked(sum + Read(block, at));
            return sum == 0;
        }
    }

    private static byte[] CreateEmptyFfs()
    {
        var image = DosDiskImage.Create(true, omitStartup: true, includeLargeFile: false,
            volumeName: "BENCH", blockCount: DiskBlocks, firstFileBlock: 22, payload: []).Bytes;
        Array.Clear(image, 1024, image.Length - 1024);
        uint root = DiskBlocks / 2;
        uint firstBitmap = root + 1;
        uint bitmapCount = (DiskBlocks - 2 + 4063) / 4064;
        void Write(uint block, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(checked((int)block * SectorBytes + offset), 4), value);
        uint Read(uint block, int offset) => BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(checked((int)block * SectorBytes + offset), 4));
        for (uint page = 0; page < bitmapCount; page++)
        {
            uint map = firstBitmap + page;
            for (int offset = 4; offset < SectorBytes; offset += 4) Write(map, offset, uint.MaxValue);
            for (uint bit = 0; bit < 4064; bit++)
            {
                uint block = 2 + page * 4064 + bit;
                bool reserved = block >= DiskBlocks || block < 22 || block == root || block >= firstBitmap && block < firstBitmap + bitmapCount;
                if (reserved)
                {
                    int offset = 4 + (int)(bit / 32) * 4;
                    Write(map, offset, Read(map, offset) & ~(1u << (int)(bit % 32)));
                }
            }
            Write(map, 0, 0);
            uint sum = 0;
            for (int offset = 0; offset < SectorBytes; offset += 4) sum = unchecked(sum + Read(map, offset));
            Write(map, 0, unchecked(0u - sum));
        }
        Write(root, 0, 2); Write(root, 4, 0); Write(root, 12, 72);
        Write(root, 312, uint.MaxValue);
        for (uint page = 0; page < bitmapCount; page++) Write(root, 316 + (int)page * 4, firstBitmap + page);
        Write(root, 500, 0); Write(root, 508, 1);
        image[root * SectorBytes + 432] = 5;
        System.Text.Encoding.ASCII.GetBytes("BENCH").CopyTo(image, (int)root * SectorBytes + 433);
        Write(root, 20, 0);
        uint rootSum = 0;
        for (int offset = 0; offset < SectorBytes; offset += 4) rootSum = unchecked(rootSum + Read(root, offset));
        Write(root, 20, unchecked(0u - rootSum));
        return image;
    }

    private sealed record Arm(string Name, string Handler, byte[] Media, uint Flavor);
    private sealed record RunResult(string Name, int Trial, bool Passed, uint Stage, uint IoErr, int LoopFrames,
        long CompletedFrames, double HostSeconds, Dictionary<string, long> PhaseCycles,
        int ReadCommands, int WriteCommands, int ReadRequests, int WriteRequests,
        long ReadBytes, long WriteBytes, int Flushes, string InitialMediaSha256, string ResultMediaSha256,
        string? IndependentCheck = null, uint Detail = 0);

    private sealed class MemoryDevice(byte[] bytes) : IBlockDevice
    {
        public byte[] Bytes { get; } = bytes;
        public int SectorSize => SectorBytes;
        public ulong SectorCount => (ulong)(Bytes.Length / SectorBytes);
        public void Read(ulong firstSector, Span<byte> destination) => Bytes.AsSpan(checked((int)firstSector * SectorBytes), destination.Length).CopyTo(destination);
        public void Write(ulong firstSector, ReadOnlySpan<byte> source) => source.CopyTo(Bytes.AsSpan(checked((int)firstSector * SectorBytes), source.Length));
        public void Flush() { }
    }

    private sealed class IoHook : IAmigaHardfileIoHook
    {
        public int ReadCommands, WriteCommands, ReadRequests, WriteRequests, Flushes;
        public long ReadBytes, WriteBytes;
        public bool FlushOnDispose => false;
        public void ObserveOpen(int unit) { }
        public void ObserveCommand(ushort command)
        {
            if (command is 2 or 24) ReadCommands++;
            if (command is 3 or 25) WriteCommands++;
        }
        public void Read(FileStream backing, long offset, Span<byte> destination)
        { ReadRequests++; ReadBytes += destination.Length; backing.Position = offset; backing.ReadExactly(destination); }
        public void Write(FileStream backing, long offset, ReadOnlySpan<byte> source)
        { WriteRequests++; WriteBytes += source.Length; backing.Position = offset; backing.Write(source); }
        public void Flush(FileStream backing) { Flushes++; backing.Flush(true); }
    }
}

