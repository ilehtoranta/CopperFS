using System.Text.Json;
using Amiga;
using CopperSFS.Check;

namespace CopperFS.Kickstart.Tests;

public static class SfsOverwriteContractProbe
{
    public static int Aligned() => Run(0, 2048);
    public static int Partial() => Run(37, 1200);

    private static int Run(int offset, int count)
    {
        var library = global::Amiga.Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) return Fail();
        DOS.DOSLibraryBase = library.Value;
        APTR.WriteUInt32(global::Amiga.Exec.FindTask(CString.FromPointer(0)), 184, uint.MaxValue);
        var control = APTR.FromPointer(global::Amiga.Exec.AllocMem(1024,
            global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear));
        var data = APTR.FromPointer(global::Amiga.Exec.AllocMem(4096, global::Amiga.Exec.MemoryFlags.Public));
        if (control.IsNull || data.IsNull || CorrectnessProbe.Mount(control, 2) != 0) return Fail();
        var port = DOS.DeviceProc("SEQ0:");
        var file = DOS.Open("SEQ0:Contract", DOS.FileMode.NewFile);
        for (var i = 0; i < 4096; i++) APTR.WriteUInt8(data, i, 0xA7);
        if (!file.HasValue || DOS.Write(file.Value, data, 4096) != 4096 || DOS.Close(file.Value) == 0) return Fail();
        var neighbor = DOS.Open("SEQ0:Neighbor", DOS.FileMode.NewFile);
        for (var i = 0; i < 4096; i++) APTR.WriteUInt8(data, i, 0x5C);
        if (!neighbor.HasValue || DOS.Write(neighbor.Value, data, 4096) != 4096 || DOS.Close(neighbor.Value) == 0 ||
            DOS.DoPkt(port, (int)DosPacketAction.Flush, 0, 0, 0, 0, 0) == 0) return Fail();
        file = DOS.Open("SEQ0:Contract", DOS.FileMode.ReadWrite);
        if (!file.HasValue || DOS.Seek(file.Value, offset, -1) < 0) return Fail();
        for (var i = 0; i < count; i++) APTR.WriteUInt8(data, i, 0xD4);
        CorrectnessProbe.Report(220);
        var written = DOS.Write(file.Value, data, count);
        CorrectnessProbe.Report(written == count ? 221 : 226);
        if (DOS.Close(file.Value) == 0) return Fail();
        CorrectnessProbe.Report(222);
        var flushed = DOS.DoPkt(port, (int)DosPacketAction.Flush, 0, 0, 0, 0, 0);
        CorrectnessProbe.Report(flushed != 0 ? 223 : 227);
        CorrectnessProbe.Report(210);
        return 0;
    }

    private static int Fail() { CorrectnessProbe.Report(250); return 1; }
}

public sealed partial class OriginalHandlerCorrectnessTests
{
    private sealed record ContractEvent(int Stage, long Offset, byte[] Data);
    private sealed class ContractTrace(bool failWrite = false, bool failFlush = false) : WritableHook
    {
        public int Stage;
        public bool Failed;
        public override bool CutAfterFrame => Failed;
        public byte[]? Seed;
        public List<ContractEvent> Events { get; } = [];

        private void CaptureSeed(FileStream backing)
        {
            if (Seed != null) return;
            Seed = new byte[backing.Length];
            backing.Position = 0;
            backing.ReadExactly(Seed);
        }

        public override void Write(FileStream backing, long offset, ReadOnlySpan<byte> source)
        {
            if (Stage == 0) { base.Write(backing, offset, source); return; }
            CaptureSeed(backing);
            for (var i = 0; i < source.Length; i += 512)
            {
                var sector = source.Slice(i, 512);
                base.Write(backing, offset + i, sector);
                Events.Add(new(Stage, offset + i, sector.ToArray()));
                if (failWrite && !Failed && sector.Contains((byte)0xD4))
                {
                    Failed = true;
                    throw new IOException("Injected partial overwrite");
                }
            }
        }

        public override void Flush(FileStream backing)
        {
            if (Stage != 0)
            {
                CaptureSeed(backing);
                Events.Add(new(Stage, -1, []));
                if (failFlush && !Failed) { Failed = true; throw new IOException("Injected explicit flush failure"); }
            }
            base.Flush(backing);
        }
    }

    [SfsGroupedTheory]
    [InlineData(false)] [InlineData(true)]
    public async System.Threading.Tasks.Task SfsOverwriteRecoveryContract(bool partial)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == "sfs");
        var initial = File.ReadAllBytes(manifest.ClassicBase);
        var root = Path.Combine(Path.GetDirectoryName(path)!, "overwrite-contract", partial ? "partial" : "aligned");
        var reference = Enumerable.Repeat((byte)0xA7, 4096).ToArray();
        var offset = partial ? 37 : 0;
        var count = partial ? 1200 : 2048;
        var possible = new Dictionary<string, int> { [Hash(reference)] = 0 };
        for (int i = offset; i < offset + count;)
        {
            var n = Math.Min(512 - i % 512, offset + count - i);
            Array.Fill(reference, (byte)0xD4, i, n);
            i += n;
            possible.Add(Hash(reference), i - offset);
        }
        foreach (var arm in new[] { "original", "candidate" })
        foreach (var failure in new[] { "none", "write", "flush" })
        {
            var handler = arm == "original" ? pair.Original : pair.Candidate;
            var trace = new ContractTrace(failure == "write", failure == "flush");
            var marks = new List<int>();
            var directory = Path.Combine(root, arm, failure);
            System.Threading.Tasks.Task<byte[]> Record() => Run(directory, "sfs", "trace", handler, initial, true,
                "SfsOverwriteContractProbe::" + (partial ? "Partial" : "Aligned"), storage: trace,
                observe: (mark, _, _, _, _) =>
                {
                    if (mark == 220 || trace.Stage != 0 && mark is 221 or 222 or 223 or 226 or 227)
                    { trace.Stage = mark; marks.Add(mark); }
                });
            if (failure == "none") await Record();
            else await Assert.ThrowsAsync<PowerCut>(Record);
            Assert.NotNull(trace.Seed);
            Assert.Equal(failure != "none", trace.Failed);
            var bytes = trace.Seed!;
            var seed = SfsImageChecker.Check(new MemoryStream(bytes), reservedBlocks: 2);
            var cuts = new List<object>();
            var neighborHash = Hash(Enumerable.Repeat((byte)0x5C, 4096).ToArray());
            File.WriteAllText(Path.Combine(directory, "device.json"), JsonSerializer.Serialize(new { marks, trace.Events }, Json));
            for (var i = 0; i <= trace.Events.Count; i++)
            {
                if (i != 0 && trace.Events[i - 1] is { Offset: >= 0 } e) e.Data.CopyTo(bytes, e.Offset);
                // Every sector prefix can survive a write-through device losing power.
                var replay = await Run(directory, "sfs", "cut-" + i, pair.Original, bytes, true, "OriginalTrstProbe::Recover");
                var report = SfsImageChecker.Check(new MemoryStream(replay), reservedBlocks: 2);
                Assert.Equal(seed.FreeBlocks, report.FreeBlocks);
                Assert.Equal(neighborHash, report.Entries.Single(x => x.Path == "/Neighbor").Sha256);
                var changed = report.Entries.Single(x => x.Path == "/Contract");
                Assert.Equal(4096ul, changed.Size);
                Assert.True(possible.TryGetValue(changed.Sha256!, out var prefix), $"Unexpected payload at {arm}/{failure}/{i}");
                cuts.Add(new { Cut = i, ChangedBytes = prefix });
                if (arm == "candidate" && Environment.GetEnvironmentVariable("COPPERFS_TEST_IN_PLACE") != "1")
                    Assert.True(prefix == 0 || prefix == count, "Whole-group overwrite recovered a partial payload.");
                if (i == trace.Events.Count && marks.Contains(221) && marks.Contains(223)) Assert.Equal(count, prefix);
            }
            File.WriteAllText(Path.Combine(directory, "recovery.json"), JsonSerializer.Serialize(cuts, Json));
        }
    }
}
