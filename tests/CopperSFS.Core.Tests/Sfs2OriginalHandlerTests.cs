using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Amiga;
using CopperSFS.Bench;
using Xunit.Abstractions;
using static CopperSFS.AmigaSmartFileSystem2WriteCore;
using static CopperSFS.Tests.Sfs2WriteTests;
using Task = System.Threading.Tasks.Task;
using Process = System.Diagnostics.Process;

namespace CopperSFS.Tests;

public sealed class Sfs2NativeRedoFactAttribute : FactAttribute
{
    public Sfs2NativeRedoFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("COPPERSFS_SFS2_NATIVE_REDO_IMAGE")))
            Skip = "Requires the original-handler capture-redo fixture; see docs/sfs2-writes.md.";
    }
}
public sealed class Sfs2NativeHandoffFactAttribute : FactAttribute
{
    public Sfs2NativeHandoffFactAttribute()
    {
        foreach (var key in new[] { "COPPERSFS_SFS2_REFERENCE_IMAGE", "COPPERSFS_SFS2_ORIGINAL_HANDLER", "COPPERSHARP_KICKSTART31_ROM" })
            if (!File.Exists(Environment.GetEnvironmentVariable(key)))
                Skip = "Requires the original SFS2 handler/image and licensed ROM; see docs/sfs2-writes.md.";
    }
}

public sealed class Sfs2OriginalHandlerTests(ITestOutputHelper output)
{
    [Sfs2NativeRedoFact]
    public void CopperSfs2RecoversAnActualOriginalHandlerRedoTransaction()
    {
        var source = File.ReadAllBytes(Environment.GetEnvironmentVariable("COPPERSFS_SFS2_NATIVE_REDO_IMAGE")!);
        var disk = new StudyDisk(source, 512, reservedBlocks: 2); var w = WorkFor(disk);
        Assert.False(AmigaSmartFileSystemReadOnlyCore.Mount(ref disk, w.Meta, out _));
        Assert.True(Recover(ref disk, ref w), w.Error.ToString());
        var report = Inspect(disk.Snapshot(), reserved: 2);
        Assert.Contains(report.Entries, e => e.Path == "/Docs/Payload" && e.Size == 1301);
        Assert.Contains(report.Entries, e => e.Path == "/Docs/Entry79");
        Assert.Contains(report.Entries, e => e.Path == "/LengthProbe" && e.Size == 513);
        var recovered = disk.Snapshot(); Assert.True(Recover(ref disk, ref w)); Assert.Equal(recovered, disk.Snapshot());
        output.WriteLine("Original SFS2 TRST capture recovered, independently checked, and repeat recovery was byte-identical.");
    }

    private static byte[] PrepareHandoff()
    {
        var image = File.ReadAllBytes(Environment.GetEnvironmentVariable("COPPERSFS_SFS2_REFERENCE_IMAGE")!);
        var disk = new StudyDisk(image, 512, reservedBlocks: 2); var w = WorkFor(disk);
        var node = CreateFile(ref disk, "CopperWrite");
        PutFile(ref disk, node, 0, Enumerable.Range(0, 8195).Select(i => (byte)(i * 29 + 7)).ToArray(), true);
        var dir = CreateFile(ref disk, "CopperDir", directory: true);
        var child = CreateFile(ref disk, "BeforeRename", dir);
        PutFile(ref disk, child, 0, Enumerable.Range(0, 1027).Select(i => (byte)(i * 17 + 11)).ToArray());
        Encoding.ASCII.GetBytes("Renamed").CopyTo(disk.Ram, (int)disk.NameBuffer.Raw);
        var r = new Request { Node = child, Directory = dir, Name = disk.NameBuffer, Length = 7 };
        Assert.True(Rename(ref disk, ref w, ref r), w.Error.ToString());
        Encoding.ASCII.GetBytes("Written by CopperSFS2").CopyTo(disk.Ram, (int)disk.Payload.Raw);
        r = new Request { Node = child, Buffer = disk.Payload, Length = 21 };
        Assert.True(SetMetadata(ref disk, ref w, ref r, DosPacketAction.SetComment), w.Error.ToString());
        r.Position = 128; Assert.True(SetMetadata(ref disk, ref w, ref r, DosPacketAction.SetProtection), w.Error.ToString());
        disk.WriteUInt32(disk.Payload, 0, 1234); disk.WriteUInt32(disk.Payload, 4, 5); disk.WriteUInt32(disk.Payload, 8, 100);
        Assert.True(SetMetadata(ref disk, ref w, ref r, DosPacketAction.SetDate), w.Error.ToString());
        var path = "Docs/Payload"; Encoding.ASCII.GetBytes(path).CopyTo(disk.Ram, (int)disk.NameBuffer.Raw);
        node = AmigaSmartFileSystemReadOnlyCore.Resolve(ref disk, 1, disk.NameBuffer, (uint)path.Length, w.Meta, out _);
        PutFile(ref disk, node, 510, Enumerable.Range(0, 7).Select(i => (byte)(i * 29 + 7)).ToArray());
        Assert.True(Resize(ref disk, ref w, node, 1601), w.Error.ToString());
        path = "LengthProbe"; Encoding.ASCII.GetBytes(path).CopyTo(disk.Ram, (int)disk.NameBuffer.Raw);
        node = AmigaSmartFileSystemReadOnlyCore.Resolve(ref disk, 1, disk.NameBuffer, (uint)path.Length, w.Meta, out _);
        Assert.True(Remove(ref disk, ref w, node), w.Error.ToString());
        Inspect(disk.Snapshot(), reserved: 2); return disk.Snapshot();
    }

    private static string Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CopperSFS.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
    private async Task<string> NativeVerify(byte[] image, string name)
    {
        var repository = Repository();
        var root = Path.Combine(repository, "artifacts", "sfs2-write", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var source = Path.Combine(root, "input.hdf"); File.WriteAllBytes(source, image);
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = repository, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(repository, "tools", "CopperSFS.Sfs2Reference", "bin", "Release", "net10.0", "CopperSFS.Sfs2Reference.dll"));
        start.ArgumentList.Add(Environment.GetEnvironmentVariable("COPPERSFS_SFS2_ORIGINAL_HANDLER")!);
        start.ArgumentList.Add(Environment.GetEnvironmentVariable("COPPERSHARP_KICKSTART31_ROM")!);
        var result = Path.Combine(root, "native"); start.ArgumentList.Add(result); start.ArgumentList.Add("verify"); start.ArgumentList.Add(source);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); var log = await stdout + "\n" + await stderr; File.WriteAllText(Path.Combine(root, "native.log"), log);
        Assert.True(process.ExitCode == 0, log);
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(result, "report.json")));
        Assert.Equal(42, report.RootElement.GetProperty("status").GetInt32());
        var recovered = File.ReadAllBytes(Path.Combine(result, "reference.hdf"));
        Inspect(recovered, reserved: 2); Assert.Equal(image, File.ReadAllBytes(source));
        output.WriteLine($"Original SFS2 native handoff: {root}"); return Manifest(recovered, reserved: 2);
    }

    [Sfs2NativeHandoffFact]
    public async Task OriginalHandlerReadsCopperSfs2MutationsAfterCleanHandoff()
    {
        var image = PrepareHandoff(); Assert.Equal(Manifest(image, reserved: 2), await NativeVerify(image, "clean-handoff"));
    }

    [Sfs2NativeHandoffFact]
    public async Task OriginalHandlerReplaysCopperSfs2RedoBeforeAndDuringHomeWrites()
    {
        var completed = PrepareHandoff(); var seed = new StudyDisk(completed, 512, reservedBlocks: 2); var w = WorkFor(seed);
        Encoding.ASCII.GetBytes("CopperWrite").CopyTo(seed.Ram, (int)seed.NameBuffer.Raw);
        var node = AmigaSmartFileSystemReadOnlyCore.Resolve(ref seed, 1, seed.NameBuffer, 11, w.Meta, out _);
        PutFile(ref seed, node, 0, new byte[8195]); var before = seed.Snapshot();
        bool WriteRedo(ref StudyDisk disk)
        {
            var work = WorkFor(disk);
            for (var i = 0; i < 8195; i++) disk.Ram[(int)disk.Payload.Raw + i] = (byte)(i * 29 + 7);
            var r = new Request { Node = node, Buffer = disk.Payload, Length = 8195 };
            return ExecuteTrst(ref disk, ref work, ref r, TrstOperation.Write, disk.BatchBuffer, 65536, out _);
        }
        var baseline = new StudyDisk(before, 512, crashModel: true, reservedBlocks: 2); Assert.True(WriteRedo(ref baseline));
        var expected = Manifest(baseline.Snapshot(), reserved: 2); byte[]? first = null, last = null;
        for (var cut = 1; cut <= baseline.Events; cut++)
        {
            var disk = new StudyDisk(before, 512, crashModel: true, reservedBlocks: 2) { CutAfterEvent = cut };
            Assert.Throws<StudyPowerCut>(() => WriteRedo(ref disk));
            foreach (var saved in disk.SampledCrashImages().Take(2))
                if (U32(saved.AsSpan(5 * 512), 0) == 0x54524641 && U32(saved.AsSpan(5 * 512), 12) != 0)
                { first ??= saved; last = saved; }
        }
        Assert.NotNull(first); Assert.NotNull(last);
        Assert.Equal(expected, await NativeVerify(first, "redo-first"));
        Assert.Equal(expected, await NativeVerify(last, "redo-last"));
    }
}
