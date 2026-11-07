using System.Buffers.Binary;
using System.Text;
using Amiga;
using CopperSFS.Bench;
using CopperSFS.Check;
using static CopperSFS.AmigaSmartFileSystemWriteCore;
using Task = System.Threading.Tasks.Task;

namespace CopperFS.Kickstart.Tests;

public static class OriginalTrstProbe
{
    public static int Recover() => MountAndVerify(false);
    public static int RecoverData() => MountAndVerify(true);

    private static int MountAndVerify(bool verifyData)
    {
        var dos = global::Amiga.Exec.OpenLibrary("dos.library", 37);
        if (!dos.HasValue) { CorrectnessProbe.Report(250); return 1; }
        DOS.DOSLibraryBase = dos.Value;
        var control = APTR.FromPointer(global::Amiga.Exec.AllocMem(1024, global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear));
        if (control.IsNull || CorrectnessProbe.Mount(control, 2) != 0) { CorrectnessProbe.Report(250); return 2; }
        var root = DOS.Lock("SEQ0:", DOS.LockMode.Shared);
        if (!root.HasValue) { CorrectnessProbe.Report(250); return 3; }
        DOS.UnLock(root.Value);
        if (verifyData)
        {
            var file = DOS.Open("SEQ0:ABWrite", DOS.FileMode.OldFile);
            var data = APTR.FromPointer(global::Amiga.Exec.AllocMem(1025, global::Amiga.Exec.MemoryFlags.Public));
            if (!file.HasValue || data.IsNull || DOS.Read(file.Value, data, 1025) != 1024)
            { CorrectnessProbe.Report(250); return 5; }
            for (var i = 0; i < 1024; i++)
                if (APTR.ReadUInt8(data, i) != (byte)(i * 29 + 7)) { CorrectnessProbe.Report(250); return 6; }
            if (DOS.Close(file.Value) == 0) { CorrectnessProbe.Report(250); return 7; }
        }
        if (DOS.Inhibit("SEQ0:", 1) == 0) { CorrectnessProbe.Report(250); return 4; }
        CorrectnessProbe.Report(210);
        return 0;
    }
}

public sealed class OriginalTrstTheoryAttribute : TheoryAttribute
{
    public OriginalTrstTheoryAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("COPPERSHARP_KICKSTART31_ROM")) ||
            !File.Exists(Environment.GetEnvironmentVariable("COPPERFS_ORIGINAL_SFS")))
            Skip = "Requires Kickstart 3.1 and the original SmartFilesystem 1.279 handler.";
    }
}

public sealed partial class OriginalHandlerCorrectnessTests
{
    // Port of CopperSFS research commit 4a902a1. Each boot uses the original handler alone.
    [OriginalTrstTheory]
    [InlineData("overwrite")]
    [InlineData("append")]
    [InlineData("first-write")]
    [InlineData("create")]
    [InlineData("rename")]
    [InlineData("truncate")]
    [InlineData("remove")]
    public async Task OriginalHandlerRecoversCopperSfsTrst(string scenario)
    {
        var handler = Environment.GetEnvironmentVariable("COPPERFS_ORIGINAL_SFS")!;
        Assert.Equal("C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205", Hash(File.ReadAllBytes(handler)));
        const int bytes = 512;
        var seed = new StudyDisk(StudyFixture.Empty(bytes, 16 * 1024 * 1024), bytes);
        var node = FsOps.Create(ref seed, "ABWrite");
        var pattern = Enumerable.Range(0, 1024).Select(i => (byte)(i * 29 + 7)).ToArray();
        if (scenario != "first-write")
            FsOps.Write(ref seed, node, 0, scenario == "append" ? pattern[..512] : StudyFixture.Pattern(1024, 800));
        var image = seed.Snapshot();
        var rootBlock = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(104));
        var marker = checked((int)(rootBlock + 2) * bytes);
        var entry = scenario is "first-write" or "append" or "overwrite"
            ? "OriginalTrstProbe::RecoverData" : "OriginalTrstProbe::Recover";
        void Mutate(ref StudyDisk disk)
        {
            pattern.CopyTo(disk.Ram, (int)disk.Payload.Raw);
            Encoding.ASCII.GetBytes("Renamed").CopyTo(disk.Ram, (int)disk.NameBuffer.Raw);
            var request = new Request { Node = node, Directory = 1, Name = disk.NameBuffer, Length = 7, Buffer = disk.Payload };
            var operation = scenario switch
            {
                "create" => TrstOperation.CreateFile, "rename" => TrstOperation.Rename,
                "truncate" => TrstOperation.Truncate, "remove" => TrstOperation.Remove, _ => TrstOperation.Write
            };
            if (operation == TrstOperation.Write)
            {
                request.Position = scenario == "append" ? 512u : 0u;
                request.Length = scenario == "append" ? 512u : 1024u;
            }
            var work = disk.Work;
            Assert.True(ExecuteTrst(ref disk, ref work, ref request, operation, disk.BatchBuffer, 65536, out var actual), work.Error.ToString());
            if (operation == TrstOperation.Write) Assert.Equal(request.Length, actual);
        }
        var baseline = new StudyDisk(image, bytes, crashModel: true);
        Mutate(ref baseline);
        static ImageReport Inspect(byte[] snapshot)
        {
            using var input = new MemoryStream(snapshot, writable: false);
            return SfsImageChecker.Check(input);
        }
        var expected = Inspect(baseline.Snapshot());
        Assert.False(Inspect(image).Entries.SequenceEqual(expected.Entries));
        var seen = new HashSet<string>();
        var directory = Directory.CreateTempSubdirectory("CopperFS-Trst-").FullName;
        try
        {
            for (var cut = 1; cut <= baseline.Events; cut++)
            {
                var disk = new StudyDisk(image, bytes, crashModel: true) { CutAfterEvent = cut };
                Assert.Throws<StudyPowerCut>(() => Mutate(ref disk));
                foreach (var saved in disk.SampledCrashImages().Take(2))
                {
                    var id = BinaryPrimitives.ReadUInt32BigEndian(saved.AsSpan(marker));
                    if (id is not (0x54524641 or 0x54525354) || !seen.Add(Hash(saved))) continue;
                    var recovered = await Run(directory, "sfs", scenario, handler, saved, true, entry);
                    Assert.True(recovered.AsSpan(marker, 4).SequenceEqual("TROK"u8));
                    var report = Inspect(recovered);
                    Assert.Equal(expected.FreeBlocks, report.FreeBlocks);
                    Assert.Equal(expected.Entries.ToArray(), report.Entries.ToArray());
                }
            }
            Assert.NotEmpty(seen);
            output.WriteLine($"{scenario}: {baseline.Events} cut points, {seen.Count} distinct dirty images recovered.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
