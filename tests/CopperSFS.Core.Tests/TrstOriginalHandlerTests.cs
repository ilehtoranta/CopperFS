using System.Reflection;
using System.Diagnostics;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amiga;
using CopperSFS.Bench;
using Xunit.Abstractions;
using static CopperSFS.AmigaSmartFileSystemWriteCore;
using Task = System.Threading.Tasks.Task;

namespace CopperSFS.Tests;

public sealed class OriginalTrstFactAttribute : FactAttribute
{
    public OriginalTrstFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COPPERSFS_NATIVE_TEST_ASSEMBLY")))
            Skip = "Requires the optional native reference harness, original handler and licensed ROM; see docs/trst-journaling.md.";
    }
}

public sealed class TrstOriginalHandlerTests(ITestOutputHelper output)
{
    // The native harness owns boot construction, machine setup, pinned reference
    // identities and independent amidisk checks. No private source/media is copied.
    private sealed class NativeReference : IDisposable
    {
        private readonly string directory;
        private readonly Assembly assembly;
        internal readonly Type Driver;
        internal readonly object Fixture, Original;
        internal NativeReference()
        {
            var path = Path.GetFullPath(Environment.GetEnvironmentVariable("COPPERSFS_NATIVE_TEST_ASSEMBLY")!);
            directory = Path.GetDirectoryName(path)!;
            AssemblyLoadContext.Default.Resolving += Resolve;
            assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            Driver = assembly.GetType("CopperStart.Sfs.Native.Tests.NativeSfsAbTests", true)!;
            var fixtureType = Driver.GetNestedType("Fixture", BindingFlags.NonPublic)!;
            Fixture = fixtureType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
            Original = ((Array)fixtureType.GetProperty("Handlers")!.GetValue(Fixture)!).GetValue(0)!;
        }
        private Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            var path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        }
        internal string BaseImage => (string)Fixture.GetType().GetProperty("BaseImage")!.GetValue(Fixture)!;
        private async Task<object?> Call(string method, params object?[] args)
        {
            var task = (Task)Driver.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args)!;
            await task;
            return task.GetType().GetProperty("Result")!.GetValue(task);
        }
        internal async Task<byte[]> Boot(string directory) =>
            (byte[])(await Call("BootImage", directory, Original.GetType().GetProperty("Path")!.GetValue(Original), false, false, 4))!;
        internal async Task<uint> Recover(string source, string exported, byte[] boot)
        {
            var result = (await Call("Run", Fixture, Original, false, false, source, exported, 4, boot, null))!;
            return (uint)result.GetType().GetProperty("Status")!.GetValue(result)!;
        }
        internal async Task<string> Manifest(string image)
        {
            var start = new ProcessStartInfo((string)Fixture.GetType().GetProperty("Python")!.GetValue(Fixture)!)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.Environment["PYTHONPATH"] = Path.Combine((string)Fixture.GetType().GetProperty("AmidiskRoot")!.GetValue(Fixture)!, "amidisk_python", "src");
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Inspect-TrstReference.py"));
            start.ArgumentList.Add(image);
            using var process = System.Diagnostics.Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, await stderr);
            return (await stdout).Trim();
        }
        public void Dispose() => AssemblyLoadContext.Default.Resolving -= Resolve;
    }

    [OriginalTrstFact]
    public async Task OriginalHandlerRecoversGeneratedDataAndMetadataRedoTransactions()
    {
        using var native = new NativeReference();
        var root = Environment.GetEnvironmentVariable("COPPERSFS_TRST_EVIDENCE") ?? Directory.CreateTempSubdirectory("CopperSfsTrstOriginal-").FullName;
        Directory.CreateDirectory(root);
        var boot = await native.Boot(Path.Combine(root, "reference-boot"));
        var receipts = new List<object>();
        // All mutation write/flush cut points. Native boots cover each distinct
        // dirty durable prefix and each distinct all-pending-writes prefix;
        // arbitrary subsets are exhaustively covered by the separate host suite.
        foreach (var scenario in new[] { "overwrite", "append", "first-write", "create", "rename", "truncate", "remove" })
        {
            var seed = new StudyDisk(File.ReadAllBytes(native.BaseImage), 512, batchedWrites: true);
            var node = FsOps.Create(ref seed, "ABWrite");
            var pattern = Enumerable.Range(0, 1024).Select(i => (byte)(i * 29 + 7)).ToArray();
            if (scenario != "first-write")
                FsOps.Write(ref seed, node, 0, scenario == "append" ? pattern[..512] : StudyFixture.Pattern(1024, 800));
            var image = seed.Snapshot();
            bool Mutate(ref StudyDisk disk)
            {
                pattern.CopyTo(disk.Ram, (int)disk.Payload.Raw);
                Encoding.Latin1.GetBytes("Renamed").CopyTo(disk.Ram, (int)disk.NameBuffer.Raw);
                var request = new Request { Node = node, Directory = 1, Buffer = disk.Payload,
                    Name = disk.NameBuffer, Length = 7 };
                var op = scenario switch { "create" => TrstOperation.CreateFile, "rename" => TrstOperation.Rename,
                    "truncate" => TrstOperation.Truncate, "remove" => TrstOperation.Remove, _ => TrstOperation.Write };
                if (op == TrstOperation.Write)
                {
                    request.Length = scenario == "append" ? 512u : 1024u;
                    request.Position = scenario == "append" ? 512u : 0;
                }
                var work = disk.Work;
                return ExecuteTrst(ref disk, ref work, ref request, op, disk.BatchBuffer, 65536, out _);
            }
            var baseline = new StudyDisk(image, 512, crashModel: true);
            Assert.True(Mutate(ref baseline));
            var expectedPath = Path.Combine(root, scenario + "-expected.hdf");
            File.WriteAllBytes(expectedPath, baseline.Snapshot());
            var after = await native.Manifest(expectedPath);
            var seen = new HashSet<string>();
            for (var cut = 1; cut <= baseline.Events; cut++)
            {
                var disk = new StudyDisk(image, 512, crashModel: true) { CutAfterEvent = cut };
                Assert.Throws<StudyPowerCut>(() => Mutate(ref disk));
                foreach (var saved in disk.SampledCrashImages().Take(2))
                {
                    if (!TrstWriteTests.Dirty(saved)) continue;
                    var hash = Convert.ToHexString(SHA256.HashData(saved));
                    if (!seen.Add(hash)) continue;
                    var source = Path.Combine(root, $"{scenario}-{cut}-{hash[..8]}.hdf");
                    var exported = source + ".recovered.hdf";
                    File.WriteAllBytes(source, saved);
                    var status = await native.Recover(source, exported, boot);
                    Assert.Equal(after, await native.Manifest(exported));
                    if (scenario is "overwrite" or "append" or "first-write") Assert.Equal(82u, status);
                    // The original must durably clear TRFA, not merely read through it.
                    Assert.False(TrstWriteTests.Dirty(File.ReadAllBytes(exported)));
                    receipts.Add(new { scenario, cut, inputSha256 = hash, status, passed = true });
                    File.Delete(source); File.Delete(exported);
                }
            }
            Assert.NotEmpty(seen);
            output.WriteLine($"Original SmartFilesystem: {scenario}, {baseline.Events} host cut points, {seen.Count} distinct dirty images recovered.");
        }
        var receiptPath = Path.Combine(root, "receipts.json");
        File.WriteAllText(receiptPath, JsonSerializer.Serialize(receipts, new JsonSerializerOptions { WriteIndented = true }));
        output.WriteLine($"Native reference receipts: {receiptPath}");
    }
}
