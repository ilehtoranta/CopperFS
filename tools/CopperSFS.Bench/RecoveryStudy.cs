using System.Security.Cryptography;
using System.Text.Json;
using CopperSFS.Check;

namespace CopperSFS.Bench;

internal sealed record RecoveryResult(int BlockBytes, string Scenario, int MutationCutPoints,
    int MutationImages, int RecoveryCutPoints, int RecoveryImages, int TornRecovered, int TornRefused);

internal static class RecoveryStudy
{
    internal static readonly string[] Scenarios =
        ["create", "mkdir", "overwrite", "append", "shrink", "grow", "truncate", "rename", "delete", "rmdir"];

    internal static IReadOnlyList<RecoveryResult> Run(int bytes, Action<string>? progress = null)
    {
        var disk = new StudyDisk(StudyFixture.Empty(bytes, 1024 * 1024), bytes);
        var node = FsOps.Create(ref disk, "Data");
        var original = StudyFixture.Pattern(8317, 100);
        FsOps.Write(ref disk, node, 0, original);
        var keep = StudyFixture.Pattern(3073, 200);
        FsOps.Write(ref disk, FsOps.Create(ref disk, "Keep"), 0, keep);
        var directory = FsOps.Create(ref disk, "Dir", true);
        var baseline = disk.Snapshot();
        var beforeModel = new Dictionary<string, byte[]?> { ["/"] = null, ["/Data"] = original, ["/Keep"] = keep, ["/Dir"] = null };
        ValidateExpected(StudyFixture.Inspect(baseline, bytes), beforeModel);
        var before = Manifest(baseline, bytes);
        var results = new List<RecoveryResult>();
        foreach (var scenario in Scenarios)
        {
            var model = beforeModel.ToDictionary(p => p.Key, p => p.Value?.ToArray());
            var changed = original.ToArray();
            var payload = StudyFixture.Pattern(scenario == "append" ? 1237 : 5001, 300);
            DiskAction action = scenario switch
            {
                "create" => (ref StudyDisk d) => FsOps.Create(ref d, "New"),
                "mkdir" => (ref StudyDisk d) => FsOps.Create(ref d, "New", true),
                "overwrite" => (ref StudyDisk d) => FsOps.Write(ref d, node, 399, payload),
                "append" => (ref StudyDisk d) => FsOps.Write(ref d, node, (uint)original.Length, payload),
                "shrink" => (ref StudyDisk d) => FsOps.Resize(ref d, node, 513),
                "grow" => (ref StudyDisk d) => FsOps.Resize(ref d, node, 10401),
                "truncate" => (ref StudyDisk d) => FsOps.Resize(ref d, node, 0),
                "rename" => (ref StudyDisk d) => FsOps.Rename(ref d, node, "Dir/Moved"),
                "delete" => (ref StudyDisk d) => FsOps.Remove(ref d, node),
                "rmdir" => (ref StudyDisk d) => FsOps.Remove(ref d, directory),
                _ => throw new ArgumentException(scenario)
            };
            switch (scenario)
            {
                case "create": model.Add("/New", []); break;
                case "mkdir": model.Add("/New", null); break;
                case "overwrite": payload.CopyTo(changed, 399); model["/Data"] = changed; break;
                case "append": model["/Data"] = [.. original, .. payload]; break;
                case "shrink": model["/Data"] = original[..513]; break;
                case "grow": Array.Resize(ref changed, 10401); model["/Data"] = changed; break;
                case "truncate": model["/Data"] = []; break;
                case "rename": model.Remove("/Data"); model.Add("/Dir/Moved", original); break;
                case "delete": model.Remove("/Data"); break;
                case "rmdir": model.Remove("/Dir"); break;
            }
            var probe = new StudyDisk(baseline, bytes, crashModel: true);
            action(ref probe);
            var afterImage = probe.Snapshot();
            ValidateExpected(StudyFixture.Inspect(afterImage, bytes), model);
            var after = Manifest(afterImage, bytes);
            var mutationImages = 0; byte[]? dirty = null;
            for (var cut = 1; cut <= probe.Events; cut++)
            {
                disk = new StudyDisk(baseline, bytes, crashModel: true) { CutAfterEvent = cut };
                ExpectCut(ref disk, action);
                foreach (var image in disk.CrashImages())
                {
                    CheckRecovered(image, bytes, before, after, false, $"{scenario}, mutation event {cut}");
                    mutationImages++;
                    if (StudyFixture.U32(image, 4 * bytes) == 0x54524641 && StudyFixture.U32(image, 4 * bytes + 12) != 0)
                        dirty = image;
                }
            }
            FsOps.Require(dirty is not null, "Scenario never produced a recoverable dirty journal");
            var recoveryProbe = new StudyDisk(dirty!, bytes, crashModel: true);
            FsOps.Recover(ref recoveryProbe);
            var recoveryImages = 0;
            for (var cut = 1; cut <= recoveryProbe.Events; cut++)
            {
                disk = new StudyDisk(dirty!, bytes, crashModel: true) { CutAfterEvent = cut };
                ExpectCut(ref disk, FsOps.Recover);
                foreach (var image in disk.CrashImages())
                {
                    CheckRecovered(image, bytes, before, after, false, $"{scenario}, recovery event {cut}");
                    recoveryImages++;
                }
            }
            var tornRecovered = 0; var tornRefused = 0;
            // A filesystem block larger than a 512-byte device sector can tear
            // between sectors. This is prefix sampling, not arbitrary reordering.
            for (var write = 1; write <= probe.Counts.BlockWrites; write++)
                for (var prefix = 512; prefix < bytes; prefix += 512)
                {
                    disk = new StudyDisk(baseline, bytes, crashModel: true) { TearAtWrite = write, TearBytes = prefix };
                    ExpectCut(ref disk, action);
                    if (CheckRecovered(disk.Snapshot(), bytes, before, after, true, $"{scenario}, write {write}, prefix {prefix}"))
                        tornRecovered++;
                    else tornRefused++;
                }
            results.Add(new(bytes, scenario, probe.Events, mutationImages, recoveryProbe.Events, recoveryImages, tornRecovered, tornRefused));
            progress?.Invoke($"Recovery {bytes}/{scenario}: {mutationImages + recoveryImages} crash images; {tornRecovered} tears recovered, {tornRefused} refused");
        }
        return results;
    }
    private static void ExpectCut(ref StudyDisk disk, DiskAction action)
    {
        try { action(ref disk); }
        catch (StudyPowerCut) { return; }
        throw new InvalidOperationException("Requested power cut was not reached");
    }
    private static string Manifest(byte[] image, int bytes)
    {
        var report = StudyFixture.Inspect(image, bytes);
        return JsonSerializer.Serialize(new { report.FreeBlocks, report.Entries });
    }
    private static bool CheckRecovered(byte[] image, int bytes, string before, string after, bool allowRefusal, string context)
    {
        try
        {
            var disk = new StudyDisk(image, bytes, crashModel: true);
            var work = disk.Work;
            if (!AmigaSmartFileSystemWriteCore.Recover(ref disk, ref work))
            {
                FsOps.Require(allowRefusal, "Whole-block recovery failed");
                FsOps.Require(!AmigaSmartFileSystemCore.Mount(ref disk, work.Meta), "Damaged image was mountable");
                try { StudyFixture.Inspect(disk.Snapshot(), bytes); }
                catch (InvalidDataException) { return false; }
                throw new InvalidOperationException("Refused image unexpectedly passed the checker");
            }
            FsOps.Require(AmigaSmartFileSystemCore.Mount(ref disk, work.Meta), "Recovered image did not mount");
            var recovered = disk.Snapshot();
            var manifest = Manifest(recovered, bytes);
            FsOps.Require(manifest == before || manifest == after, "Recovery produced mixed contents or allocation accounting");
            FsOps.Recover(ref disk);
            FsOps.Require(recovered.AsSpan().SequenceEqual(disk.Snapshot()), "Recovery was not idempotent");
            return true;
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"{bytes}-byte blocks: {context}", error);
        }
    }
    private static void ValidateExpected(ImageReport report, Dictionary<string, byte[]?> model)
    {
        FsOps.Require(report.Entries.Count == model.Count, "Unexpected committed manifest size");
        foreach (var entry in report.Entries)
        {
            FsOps.Require(model.TryGetValue(entry.Path, out var data), $"Unexpected path {entry.Path}");
            FsOps.Require(entry.Directory == (data is null), $"Wrong type for {entry.Path}");
            if (data is not null) FsOps.Require(entry.Size == (ulong)data.Length &&
                entry.Sha256 == Convert.ToHexString(SHA256.HashData(data)), $"Wrong bytes for {entry.Path}");
        }
    }
}
