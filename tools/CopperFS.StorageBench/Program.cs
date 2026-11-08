using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

if(args.Length>0 && args[0]=="matrix")
    args=await MatrixCommand.Prepare(args[1..]);
if (args.Length < 2 || args[0] is not ("run" or "report"))
    throw new ArgumentException("matrix --RepositoryRoot <repo> --NativeArtifactRoot <native> --ArtifactRoot <new-output> --OriginalFfsHandler <FFS> --OriginalSfs2Handler <SFS> [options] | run <matrix.json> <test.dll> [workers=4] | report <matrix.json>");
var manifestPath = Path.GetFullPath(args[1]);
var Indented = new JsonSerializerOptions { WriteIndented=true };
var root = Path.GetDirectoryName(manifestPath)!;
var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
var scenarios = manifest["Scenarios"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
var variants = manifest["Variants"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
var profiles=StorageMatrixDefinition.Profiles(manifest["Profiles"]?.Deserialize<string[]>());
foreach(var scenario in scenarios) _=StorageMatrixDefinition.Parse(scenario);
var ideLayout=manifest["IdeLayout"]?.GetValue<string>();
if(ideLayout is not (null or StorageIdePartition.Layout)) throw new InvalidDataException("Unknown IDE fixture layout.");
if (args[0] == "run")
{
    if(File.Exists(Path.Combine(root,"harness-hashes.json")) || Directory.Exists(Path.Combine(root,"matrix")))
        throw new IOException("This matrix already has execution records; use a fresh artifact directory.");
    using(var marker=new FileStream(Path.Combine(root,"run-started.json"),FileMode.CreateNew,FileAccess.Write,FileShare.Read))
        JsonSerializer.Serialize(marker,new { StartedUtc=DateTimeOffset.UtcNow,Protocol=manifest["Protocol"]?.GetValue<string>() ?? "kickstart-storage-v1" },Indented);
    var assembly = Path.GetFullPath(args[2]);
    var workers = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 4;
    if (workers is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(workers));
    var receipts = new Dictionary<string,string>();
    foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(assembly)!, "*.dll"))
        receipts[Path.GetFileName(file)] = Hash(File.ReadAllBytes(file));
    File.WriteAllText(Path.Combine(root,"harness-hashes.json"), JsonSerializer.Serialize(receipts,Indented));
    var executor=typeof(MatrixCommand).Assembly.Location;
    File.WriteAllText(Path.Combine(root,"executor.json"),JsonSerializer.Serialize(new
    {
        Assembly=Path.GetFileName(executor),Sha256=Hash(File.ReadAllBytes(executor)),
        Runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        Os=System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        Architecture=System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),Workers=workers
    },Indented));
    if(manifest["SourceRoots"] is JsonArray sourceRoots)
    {
        var sources=new Dictionary<string,string>();
        foreach(var entry in sourceRoots)
        {
            var sourceRoot=Path.GetFullPath(entry!.GetValue<string>());
            foreach(var file in Directory.EnumerateFiles(sourceRoot,"*.cs",SearchOption.AllDirectories)
                        .Where(f=>!Path.GetRelativePath(sourceRoot,f).Split(Path.DirectorySeparatorChar).Any(p=>p is "bin" or "obj")))
            {
                var relative=Path.Combine(Path.GetFileName(sourceRoot),Path.GetRelativePath(sourceRoot,file));
                var target=Path.Combine(root,"source",relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file,target,false); sources[relative]=Hash(File.ReadAllBytes(target));
            }
        }
        File.WriteAllText(Path.Combine(root,"source-hashes.json"),JsonSerializer.Serialize(sources,Indented));
    }
    var failures = 0;
    await Parallel.ForEachAsync(scenarios, new ParallelOptions { MaxDegreeOfParallelism=workers }, async (scenario, token) =>
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
        foreach (var arg in new[] { "test",assembly,"--filter",$"FullyQualifiedName~StorageMatrixThroughputTests&DisplayName~{scenario}",
                     "--logger",$"trx;LogFileName={scenario}.trx","--results-directory",root }) start.ArgumentList.Add(arg);
        start.Environment["COPPERFS_STORAGE_MATRIX_MANIFEST"] = manifestPath;
        start.Environment.Remove("COPPERFS_STORAGE_TRACE");
        start.Environment.Remove("COPPERFS_STORAGE_SCALAR");
        start.Environment.Remove("COPPERFS_STORAGE_MOUNT_TRACE");
        start.Environment.Remove("COPPERFS_STORAGE_CPU_PROFILE");
        using var process = Process.Start(start)!;
        Console.WriteLine($"{DateTimeOffset.Now:O} starting {scenario}, process {process.Id}");
        using var log=new StreamWriter(Path.Combine(root,scenario+".log"));
        async Task Pump(StreamReader reader)
        {
            while(await reader.ReadLineAsync(token) is { } line)
                lock(log) { log.WriteLine(line); log.Flush(); }
        }
        var stdout=Pump(process.StandardOutput); var stderr=Pump(process.StandardError);
        await process.WaitForExitAsync(token);
        await Task.WhenAll(stdout,stderr);
        Console.WriteLine($"{DateTimeOffset.Now:O} {scenario}: exit {process.ExitCode}");
        if(process.ExitCode!=0) Interlocked.Increment(ref failures);
    });
    if(failures!=0) throw new InvalidOperationException($"{failures} scenarios failed; partial results are not acceptance.");
}
var smoke=Environment.GetEnvironmentVariable("COPPERFS_STORAGE_SMOKE")=="1";
var expectedSizes=smoke ? new[] {4096} : new[] {64,512,4096,65536};
var expectedTrials=smoke ? 1 : 3;
var rows=new List<JsonObject>();
var unavailableRows=new List<JsonObject>();
foreach(var scenario in scenarios)
foreach(var profile in profiles)
foreach(var variant in variants)
{
    var path=Path.Combine(root,"matrix",scenario,profile+"-"+variant,"results.json");
    if(File.Exists(Path.Combine(Path.GetDirectoryName(path)!,"diagnostic-cpu-profile.json")))
        throw new InvalidDataException("Scalar CPU profiles are diagnostic, not throughput acceptance.");
    var unavailablePath=Path.Combine(Path.GetDirectoryName(path)!,"unavailable.json");
    if(File.Exists(unavailablePath))
    {
        if(ideLayout is not null) throw new InvalidDataException("Partitioned IDE runs require successful reads, including original handlers.");
        if(File.Exists(path) || !scenario.Contains("-ide-") || profile=="ffs" || variant!="original")
            throw new InvalidDataException($"Unexpected unavailable configuration: {unavailablePath}");
        var attempts=JsonNode.Parse(File.ReadAllText(unavailablePath))!.AsArray();
        if(attempts.Count!=expectedSizes.Length*expectedTrials) throw new InvalidDataException($"Incomplete {unavailablePath}");
        foreach(var size in expectedSizes)
        {
            var group=attempts.Where(r=>r!["PacketBytes"]!.GetValue<int>()==size).Select(r=>r!.AsObject()).ToArray();
            if(!group.Select(r=>r["Trial"]!.GetValue<int>()).Order().SequenceEqual(Enumerable.Range(0,expectedTrials)))
                throw new InvalidDataException($"Duplicate/missing unavailable attempts: {unavailablePath}");
            foreach(var item in group)
            {
                if(item["Scenario"]!.GetValue<string>()!=scenario || item["Profile"]!.GetValue<string>()!=profile ||
                    item["Variant"]!.GetValue<string>()!=variant || !StorageMatrixDefinition.KnownGeometryRejection(StorageMatrixDefinition.Parse(scenario),item["GeometryIoError"]!.GetValue<int>()) ||
                    item["DosError"]!.GetValue<int>()!=225 || item["GeometryTotalSectors"]!.GetValue<int>()!=0 || item["PioBytes"]!.GetValue<int>()<=0)
                    throw new InvalidDataException($"Missing native capability evidence: {unavailablePath}");
                unavailableRows.Add((JsonObject)item.DeepClone());
            }
        }
        continue;
    }
    var results=JsonNode.Parse(File.ReadAllText(path))!.AsArray();
    if(results.Count!=expectedSizes.Length*expectedTrials) throw new InvalidDataException($"Incomplete {path}");
    foreach(var size in expectedSizes)
    {
        var group=results.Where(r=>r!["PacketBytes"]!.GetValue<int>()==size).Select(r=>r!.AsObject()).ToArray();
        if(!group.Select(r=>r["Trial"]!.GetValue<int>()).Order().SequenceEqual(Enumerable.Range(0,expectedTrials)))
            throw new InvalidDataException($"Duplicate/missing trials in {path}, {size}");
        foreach(var field in new[] {"HandlerSha256","ProbeSha256","MediaSha256","DataSha256","RomSha256","TimingSha256","MemoryClass","MountControl"})
            if(group.Select(r=>r[field]!.ToJsonString()).Distinct().Count()!=1) throw new InvalidDataException($"Inconsistent {field} in {path}");
        foreach(var item in group)
        {
            var ide=scenario.Contains("-ide-");
            var expectedControl=variant=="bypass" ? ide ? 0x43504302u : 0x43504301u : 0;
            if(item["Scenario"]!.GetValue<string>()!=scenario || item["Profile"]!.GetValue<string>()!=profile ||
                item["Variant"]!.GetValue<string>()!=variant || item["MountControl"]!.GetValue<uint>()!=expectedControl ||
                item["Device"]!.GetValue<string>()!=(ide ? "scsi.device" : "copperhdf.device"))
                throw new InvalidDataException($"Mismatched case identity or device policy in {path}");
            if(ide && ideLayout is not null && (item["MediaLayout"]?.GetValue<string>()!=ideLayout ||
                item["PartitionFirstSector"]?.GetValue<uint>()!=StorageIdePartition.FirstSector ||
                item["PartitionSectors"]?.GetValue<uint>()!=32768))
                throw new InvalidDataException($"Mismatched IDE partition geometry in {path}");
            rows.Add((JsonObject)item.DeepClone());
        }
    }
}
foreach(var scenario in scenarios)
{
    var trx=Path.Combine(root,scenario+".trx");
    if(!File.Exists(trx)) throw new InvalidDataException($"Missing test receipt: {trx}");
    var counters=XDocument.Load(trx).Descendants().Single(e=>e.Name.LocalName=="Counters");
    if((int?)counters.Attribute("passed")!=profiles.Length*variants.Length || (int?)counters.Attribute("failed")!=0)
        throw new InvalidDataException($"Incomplete test receipt: {trx}");
}
foreach(var group in rows.GroupBy(r=>(r["Scenario"]!.GetValue<string>(),r["Profile"]!.GetValue<string>(),r["PacketBytes"]!.GetValue<int>())))
{
    foreach(var field in new[] {"RomSha256","ProbeSha256","MediaSha256","DataSha256","TimingSha256","MemoryClass","Device"})
        if(group.Select(r=>r[field]!.ToJsonString()).Distinct().Count()!=1)
            throw new InvalidDataException($"Cannot compare different {field} in {group.Key}");
    if(ideLayout is not null)
        foreach(var field in new[]{"MediaLayout","PartitionFirstSector","PartitionSectors"})
            if(group.Select(r=>r[field]!.ToJsonString()).Distinct().Count()!=1)
                throw new InvalidDataException($"Cannot compare different {field} in {group.Key}");
}
Preserve(Path.Combine(root,"measurements.json"),JsonSerializer.Serialize(rows,Indented));
Preserve(Path.Combine(root,"unavailable.json"),JsonSerializer.Serialize(unavailableRows,Indented));
var groups=rows.GroupBy(r=>(Scenario:r["Scenario"]!.GetValue<string>(),Profile:r["Profile"]!.GetValue<string>(),
    Variant:r["Variant"]!.GetValue<string>(),Size:r["PacketBytes"]!.GetValue<int>()));
var report=new StringBuilder($"# Synthetic Kickstart storage benchmark\n\n{rows.Count} validated cold workload trials; {unavailableRows.Count} unavailable workload attempts; {expectedTrials} per workload. " +
    "Each trial constructs a fresh machine and drive. Rates use emulated PAL machine time (7,093,790 ticks/s); host file latency does not set guest deadlines. " +
    "HDD/CF profiles are synthetic assumptions. IDE uses native ROM PIO with modeled wait states; AA-Gayle physical cycle accuracy is unverified.\n\n");
if(ideLayout is not null)
    report.AppendLine("IDE media layout: partitioned-v1, a 16 MiB filesystem after one reserved 128 KiB cylinder (first sector 256). SFS2 primary and backup root byte ranges match the partition. Zero/timed CopperHDF fixtures retain their flat layout. Original-handler mount failures fail this protocol; they cannot become unavailable read passes.\n");
report.AppendLine("| Machine/device | FS | Policy | Read | Session KiB/s median [min,max] | Relative to original | Commands | Media KiB | PIO KiB |");
report.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|");
var csv=new StringBuilder("Scenario,Profile,Variant,PacketBytes,MedianSessionKiBps,MinSessionKiBps,MaxSessionKiBps,RelativeToOriginal,Commands,TransportBytes,MediaFetches,MediaBytes,PioBytes,MedianReadCycles,MedianSessionCycles,MedianReadGuestSeconds,MedianSessionGuestSeconds,MedianReadNativeTimelineCycles,MedianSessionNativeTimelineCycles\n");
foreach(var group in groups)
{
    var key=group.Key; var items=group.ToArray();
    var rates=items.Select(r=>r["SessionKiBPerSecond"]!.GetValue<double>()).Order().ToArray();
    var originals=rows.Where(r=>r["Scenario"]!.GetValue<string>()==key.Scenario && r["Profile"]!.GetValue<string>()==key.Profile &&
        r["Variant"]!.GetValue<string>()=="original" && r["PacketBytes"]!.GetValue<int>()==key.Size).Select(r=>r["SessionKiBPerSecond"]!.GetValue<double>()).ToArray();
    double? relative=originals.Length==0 ? null : Median(rates)/Median(originals);
    var relativeText=relative is double ratio ? ratio.ToString("F3",CultureInfo.InvariantCulture)+"×" : "unavailable";
    double Traffic(string field)=>Median(items.Select(r=>r["SessionTraffic"]![field]!.GetValue<double>()).ToArray());
    var commands=Traffic("Commands"); var media=Traffic("MediaBytes"); var pio=Traffic("PioBytes");
    report.AppendLine(FormattableString.Invariant($"| {key.Scenario} | {key.Profile} | {key.Variant} | {key.Size} | {Median(rates):F2} [{rates[0]:F2}, {rates[^1]:F2}] | {relativeText} | {commands:F0} | {media/1024:F1} | {pio/1024:F1} |"));
    double Metric(string field)=>Median(items.Select(r=>r[field]!.GetValue<double>()).ToArray());
    csv.AppendLine(FormattableString.Invariant($"{key.Scenario},{key.Profile},{key.Variant},{key.Size},{Median(rates):F6},{rates[0]:F6},{rates[^1]:F6},{relative:F6},{commands},{Traffic("TransportBytes")},{Traffic("MediaFetches")},{media},{pio},{Metric("ReadBusCycles")},{Metric("SessionBusCycles")},{Metric("ReadGuestSeconds")},{Metric("SessionGuestSeconds")},{Metric("ReadNativeTimelineCycles")},{Metric("SessionNativeTimelineCycles")}"));
}
if(unavailableRows.Count!=0)
{
    var rejectedRoms=unavailableRows.Select(r=>StorageMatrixDefinition.Parse(r["Scenario"]!.GetValue<string>()))
        .Select(m=>$"{m.RomVersion}.{m.RomRevision}").Distinct().Order().ToArray();
    var romDescription=rejectedRoms.SequenceEqual(new[]{"39.106"}) ? "39.106" : string.Join(", ",rejectedRoms);
    var errors=string.Join("/",unavailableRows.Select(r=>r["GeometryIoError"]!.GetValue<int>()).Distinct().Order());
    report.AppendLine($"\nOriginal SFS 1.279 IDE comparisons are unavailable: unmodified Kickstart {romDescription} scsi.device returns io_Error={errors} for TD_GETGEOMETRY (22), and the handler refuses the mount with DOS error 225 before reading the filesystem. Each unavailable attempt records the native result and input hashes in unavailable.json. These are not successful read benchmarks; no original throughput or relative ratio is invented.\n");
}
report.AppendLine("\nAll raw trials retain ROM, handler, probe, media, payload and timing hashes, buffer address/memory class, mount control, machine/CPU timelines and transport counts in measurements.json. CPU timeline cycles include stalls and waiting; they are not retired instruction cycles. Comparisons only match the same machine, ROM and backend. Machine boot is outside the marked open/read/close session; the first open performs the cold filesystem mount.\n");
report.AppendLine("Media fetches are modeled backing-sector fetches, not measurements of physical host-disk requests or Windows cache misses.\n");
Preserve(Path.Combine(root,"report.md"),report.ToString()); Preserve(Path.Combine(root,"summary.csv"),csv.ToString());
Console.WriteLine($"Validated {rows.Count} read trials and {unavailableRows.Count} unavailable attempts; report: {Path.Combine(root,"report.md")}");

static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
static double Median(double[] values) { Array.Sort(values); return values[values.Length/2]; }
static void Preserve(string path,string content)
{
    if(File.Exists(path))
    {
        if(File.ReadAllText(path)!=content) throw new IOException($"Existing report differs; retain it and use a fresh output directory: {path}");
        return;
    }
    File.WriteAllText(path,content);
}
