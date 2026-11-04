using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using CopperSFS.Bench;

var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 0; i < args.Length; i += 2)
{
    if (i + 1 == args.Length || args[i] is not ("--output" or "--repetitions" or "--mode") || !options.TryAdd(args[i], args[i + 1]))
        throw new ArgumentException("Usage: CopperSFS.Bench [--output report.json] [--repetitions 5] [--mode all|benchmark|recovery]");
}
var repetitions = int.Parse(options.GetValueOrDefault("--repetitions", "5"));
if (repetitions < 3 || repetitions > 100) throw new ArgumentOutOfRangeException(nameof(repetitions));
var mode = options.GetValueOrDefault("--mode", "all");
if (mode is not ("all" or "benchmark" or "recovery")) throw new ArgumentException("Invalid mode");
var output = Path.GetFullPath(options.GetValueOrDefault("--output",
    $"artifacts/block-size/{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}/report.json"));
if (File.Exists(output)) throw new IOException("Choose a new output path; existing evidence is not overwritten.");
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var root = FindRoot();
var report = new StudyReport
{
    StartedUtc = DateTime.UtcNow, Mode = mode, Repetitions = repetitions,
    Runtime = RuntimeInformation.FrameworkDescription, Os = RuntimeInformation.OSDescription,
    Architecture = RuntimeInformation.ProcessArchitecture.ToString(), LogicalProcessors = Environment.ProcessorCount,
    TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
    SourceHashes = HashSources(root), BinaryHashes = HashFiles(AppContext.BaseDirectory, Directory.GetFiles(AppContext.BaseDirectory, "*.dll"))
};
void Save() => File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Save();
try
{
    foreach (var bytes in new[] { 512, 1024, 2048, 4096 })
    {
        if (mode is "all" or "benchmark")
        {
            Console.WriteLine($"Preparing {bytes}-byte benchmarks...");
            var workloads = new Workloads(bytes);
            report.Space.Add(workloads.Space);
            var cases = workloads.Items.SelectMany(workload => (workload.ReadOnly
                ? new[] { ("direct", 0, 0), ("cache-64k", 65536, bytes), ("cache-64k-read-ahead-32k", 65536, 32768) }
                : new[] { ("direct", 0, 0), ("cache-64k", 65536, bytes) }).Select(config => new BenchCase(workload, config.Item1, config.Item2, config.Item3)))
                .Concat(bytes == 512 ? workloads.Items.Where(w => !w.ReadOnly && w.Name != "small-create")
                    .SelectMany(w => new[] { new BenchCase(w, "direct-batched-64k", 0, 0, true),
                        new BenchCase(w, "cache-64k-batched-64k", 65536, bytes, true),
                        new BenchCase(w, "direct-batched-relocate-64k", 0, 0, true, true),
                        new BenchCase(w, "cache-64k-batched-relocate-64k", 65536, bytes, true, true) }) : [])
                .ToArray();
            // One verified warm-up per case. Each sample starts with a new image,
            // RAM and empty adapter cache; preparation/checking are outside timing.
            foreach (var item in cases) Sample(item, bytes);
            for (var repetition = 0; repetition < repetitions; repetition++)
            {
                foreach (var item in cases.Skip(repetition % cases.Length).Concat(cases.Take(repetition % cases.Length)))
                    item.Samples.Add(Sample(item, bytes));
                Console.WriteLine($"{bytes}-byte benchmarks: sample {repetition + 1}/{repetitions} verified");
            }
            foreach (var item in cases)
            {
                var counts = item.Samples[0].Io;
                FsOps.Require(item.Samples.All(s => s.Io == counts), "I/O counts changed across identical samples");
                var times = item.Samples.Select(s => s.Milliseconds).Order().ToArray();
                var middle = times.Length / 2;
                var median = times.Length % 2 == 0 ? (times[middle - 1] + times[middle]) / 2 : times[middle];
                report.Benchmarks.Add(new(bytes, item.Workload.Name, item.Adapter, item.CacheBytes,
                    item.TransferBytes == 0 ? bytes : item.TransferBytes, item.Workload.PayloadBytes,
                    item.Workload.Operations, median, times[0], times[^1], counts, item.Samples));
            }
            Save();
        }
        if (mode is "all" or "recovery")
        {
            report.Recovery.AddRange(RecoveryStudy.Run(bytes, Console.WriteLine));
            Save();
        }
    }
    report.SourceChangedDuringRun = !report.SourceHashes.SequenceEqual(HashSources(root));
    FsOps.Require(!report.SourceChangedDuringRun, "Source files changed during the run; rerun for valid evidence");
    report.Status = "passed";
}
catch (Exception error)
{
    report.Status = "failed"; report.Error = error.ToString();
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}
finally
{
    report.FinishedUtc = DateTime.UtcNow;
    report.SourceChangedDuringRun = !report.SourceHashes.SequenceEqual(HashSources(root));
    Save();
    Console.WriteLine($"Block-size evidence ({report.Status}): {output}");
}

static BenchSample Sample(BenchCase item, int bytes)
{
    var disk = new StudyDisk(item.Workload.Image, bytes, cacheBytes: item.CacheBytes, readAheadBytes: item.TransferBytes,
        batchedWrites: item.BatchedWrites) { RelocateFragmented = item.RelocateFragmented };
    item.Workload.Prepare(ref disk);
    var allocated = GC.GetAllocatedBytesForCurrentThread();
    var start = Stopwatch.GetTimestamp();
    item.Workload.Execute(ref disk);
    var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
    var counts = disk.Counts;
    item.Workload.Verify(ref disk);
    return new(elapsed, allocated, counts);
}
static string FindRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CopperSFS.slnx"))) directory = directory.Parent;
    return directory?.FullName ?? throw new DirectoryNotFoundException("Run from a checkout containing CopperSFS.slnx");
}
static FileHash[] HashSources(string root)
{
    var files = new[] { "src", "tools", "tests", "scripts" }.SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*", SearchOption.AllDirectories))
        .Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj" or "TestResults"))
        .Where(path => Path.GetExtension(path) is ".cs" or ".csproj" or ".props" or ".ps1")
        .Concat(new[] { Path.Combine(root, "Directory.Build.props"), Path.Combine(root, "CopperSFS.slnx") });
    return HashFiles(root, files);
}
static FileHash[] HashFiles(string root, IEnumerable<string> files) => files.Order(StringComparer.Ordinal).Select(path =>
    new FileHash(Path.GetRelativePath(root, path).Replace('\\', '/'), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))).ToArray();

internal sealed record FileHash(string Path, string Sha256);
internal sealed record BenchSample(double Milliseconds, long HostAllocatedBytes, IoCounts Io);
internal sealed record BenchResult(int BlockBytes, string Workload, string Adapter, int CacheBytes,
    int ReadTransferBytes, long PayloadBytes, int Operations, double MedianMilliseconds,
    double MinMilliseconds, double MaxMilliseconds, IoCounts Io, List<BenchSample> Samples);
internal sealed record BenchCase(Workload Workload, string Adapter, int CacheBytes, int TransferBytes,
    bool BatchedWrites = false, bool RelocateFragmented = false)
{
    internal List<BenchSample> Samples { get; } = new();
}
internal sealed class StudyReport
{
    public string Status { get; set; } = "running";
    public string? Error { get; set; }
    public DateTime StartedUtc { get; init; }
    public DateTime FinishedUtc { get; set; }
    public string Mode { get; init; } = "";
    public int Repetitions { get; init; }
    public string Runtime { get; init; } = "";
    public string Os { get; init; } = "";
    public string Architecture { get; init; } = "";
    public int LogicalProcessors { get; init; }
    public string? TieredCompilation { get; init; }
    public bool SourceChangedDuringRun { get; set; }
    public FileHash[] SourceHashes { get; init; } = [];
    public FileHash[] BinaryHashes { get; init; } = [];
    public string Boundary { get; init; } = "Portable-core host study on synthetic images and an in-memory device. Timings are host CPU costs, not disk or Amiga throughput. Device counts include scalar requests, optional read-through caches and optional batched writes; production device integration remains external. The Recovery array covers scalar operations across block sizes: trusted flushes, exhaustive pending whole-block versions, and 512-byte sector-prefix tears. Batched crash/failure coverage is recorded separately by BatchedWriteTests in the reliability report. No real device or compiled MC68000 qualification.";
    public List<SpaceUsage> Space { get; } = new();
    public List<BenchResult> Benchmarks { get; } = new();
    public List<RecoveryResult> Recovery { get; } = new();
}
