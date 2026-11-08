using System.Buffers.Binary;
using System.Text.Json;
using CopperSFS.Check;
using Task = System.Threading.Tasks.Task;

namespace CopperFS.Kickstart.Tests;

public sealed class Sfs2RecoveryTheoryAttribute : TheoryAttribute
{
    public Sfs2RecoveryTheoryAttribute(bool csun = false)
    {
        foreach (var name in csun ? new[] { "COPPERSHARP_KICKSTART31_ROM", "COPPERFS_SFS2_CSUN_HANDLER" } : new[] { "COPPERSHARP_KICKSTART31_ROM", "COPPERFS_ORIGINAL_SFS", "COPPERFS_SFS2_TRST_HANDLER" })
            if (!File.Exists(Environment.GetEnvironmentVariable(name))) Skip = $"Supply {name} for native SFS2 recovery.";
    }
}

public sealed partial class OriginalHandlerCorrectnessTests
{
    // Research snapshot 4a902a1: atomic 512-byte writes, volatile until flush.
    // Reads on the running guest see its pending writes. A power cut sees the
    // previous durable image until flush publishes the entire pending set.
    private sealed class PowerCut : Exception;

    private sealed class DurableTrace(byte[] initial, int cutAt = 0) : WritableHook
    {
        private readonly Dictionary<long, byte[]> pending = new();
        private readonly HashSet<string> seen = [];
        private byte[] durable = initial;
        private int sequence;
        public readonly List<(int Cut, string Hash)> Prefixes = [];
        public int SectorCuts, FlushCuts;

        private void Boundary(bool newPrefix = false)
        {
            sequence++;
            if (sequence == cutAt) throw new PowerCut();
            if (cutAt == 0 && (sequence == 1 || newPrefix))
            {
                var hash = Hash(durable);
                if (seen.Add(hash))
                    Prefixes.Add((sequence, hash));
            }
        }

        public override void Read(FileStream backing, long offset, Span<byte> destination)
        {
            base.Read(backing, offset, destination);
            foreach (var (at, sector) in pending)
            {
                var start = Math.Max(at, offset);
                var end = Math.Min(at + 512, offset + destination.Length);
                if (start < end) sector.AsSpan((int)(start - at), (int)(end - start)).CopyTo(destination[(int)(start - offset)..]);
            }
        }

        public override void Write(FileStream backing, long offset, ReadOnlySpan<byte> source)
        {
            Assert.Equal(0L, offset % 512);
            Assert.Equal(0, source.Length % 512);
            for (var i = 0; i < source.Length; i += 512)
            {
                pending[offset + i] = source.Slice(i, 512).ToArray();
                SectorCuts++;
                Boundary();
            }
        }

        public override void Flush(FileStream backing)
        {
            var changed = pending.Count != 0;
            foreach (var (at, sector) in pending) base.Write(backing, at, sector);
            base.Flush(backing);
            pending.Clear();
            if (changed && cutAt == 0)
            {
                durable = new byte[durable.Length];
                backing.Position = 0;
                backing.ReadExactly(durable);
            }
            FlushCuts++;
            Boundary(changed);
        }
    }

    public static TheoryData<string> Mutations => new() { "Overwrite", "Truncate", "Rename", "Delete", "Create", "Comment" };

    [Sfs2RecoveryTheory]
    [MemberData(nameof(Mutations))]
    public async Task NativeSfs2RecoveryMatrix(string operation)
    {
        var candidate = Environment.GetEnvironmentVariable("COPPERFS_SFS2_TRST_HANDLER")!;
        var original = Environment.GetEnvironmentVariable("COPPERFS_ORIGINAL_SFS")!;
        Assert.Equal("C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205", Hash(File.ReadAllBytes(original)));
        await RunRecoveryMatrix(operation, candidate, original);
    }

    [Sfs2RecoveryTheory(csun: true)]
    [MemberData(nameof(Mutations))]
    public Task NativeSfs2CsunRecoveryMatrix(string operation) =>
        RunRecoveryMatrix(operation, Environment.GetEnvironmentVariable("COPPERFS_SFS2_CSUN_HANDLER")!, null);

    private async Task RunRecoveryMatrix(string operation, string candidate, string? original)
    {
        var undo = original == null;
        var profile = undo ? "sfs2-csun" : "sfs2-trst";
        string[] handlers = original == null ? [candidate] : [candidate, original];
        var initial = ReadMedia.Sfs2();
        var marker = checked((int)(BinaryPrimitives.ReadUInt32BigEndian(initial.AsSpan(104)) + 2) * 512);
        const uint clean = 0x54524F4B, dirty = 0x54524641;
        uint Marker(byte[] image) => BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(marker));
        static string ManifestOf(byte[] image)
        {
            using var input = new MemoryStream(image, writable: false);
            return JsonSerializer.Serialize(SfsImageChecker.Check(input));
        }
        Assert.Equal(clean, Marker(initial));
        var trace = new DurableTrace(initial);
        var directory = Directory.CreateTempSubdirectory("CopperFS-Sfs2-").FullName;
        try
        {
            var written = await Run(directory, profile, operation, candidate, initial, true,
                "Sfs2RecoveryProbe::" + operation, storage: trace);
            var before = ManifestOf(initial);
            using var stream = new MemoryStream(written, writable: false);
            var report = SfsImageChecker.Check(stream);
            var payload = report.Entries.SingleOrDefault(e => e.Path == "/Payload");
            switch (operation)
            {
                case "Overwrite":
                    var bytes = ReadMedia.Payload.ToArray();
                    for (var i = 0; i < 513; i++) bytes[i] = (byte)(i * 29 + 7);
                    Assert.Equal(Hash(bytes), payload!.Sha256);
                    break;
                case "Truncate": Assert.Equal(Hash(ReadMedia.Payload[..513]), payload!.Sha256); break;
                case "Rename":
                    Assert.Null(payload);
                    Assert.Equal(Hash(ReadMedia.Payload), report.Entries.Single(e => e.Path == "/Renamed").Sha256);
                    break;
                case "Delete": Assert.Null(payload); break;
                case "Create": Assert.Contains(report.Entries, e => e.Path == "/Created" && !e.Directory && e.Size == 0); break;
                case "Comment": Assert.Equal("recovery", payload!.Comment); break;
            }
            var after = JsonSerializer.Serialize(report);
            Assert.NotEqual(before, after);
            Assert.Equal(clean, Marker(written));
            var finalHash = Hash(written);
            Assert.Contains(trace.Prefixes, p => p.Hash == finalHash);
            var publishedJournals = 0;
            var restoredUndoTarget = false;
            foreach (var (cut, expected) in trace.Prefixes)
            {
                var interrupted = new DurableTrace(initial, cut);
                await Assert.ThrowsAsync<PowerCut>(() => Run(directory, profile, "cut", candidate, initial,
                    true, "Sfs2RecoveryProbe::" + operation, storage: interrupted));
                var prefix = File.ReadAllBytes(Path.Combine(directory, "cut", "disk.hdf"));
                Assert.Equal(expected, Hash(prefix));
                string expectedManifest;
                if (Marker(prefix) == dirty)
                {
                    var head = BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(marker + 12));
                    // CSUN publishes an empty dirty marker before logging its first target.
                    if (!undo || head != 0)
                    {
                        Assert.NotEqual(0u, head);
                        Assert.Equal(undo ? 0x4353554Eu : 0x54525354u,
                            BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(checked((int)head * 512))));
                        publishedJournals++;
                    }
                    expectedManifest = undo ? before : after;
                }
                else
                {
                    Assert.Equal(clean, Marker(prefix));
                    expectedManifest = ManifestOf(prefix);
                    Assert.Contains(expectedManifest, new[] { before, after });
                    if (expected == finalHash) Assert.Equal(after, expectedManifest);
                }
                foreach (var handler in handlers)
                {
                    var recovered = await Run(directory, profile, "recover", handler, prefix, true, "Sfs2RecoveryProbe::Recover");
                    Assert.Equal(clean, Marker(recovered));
                    Assert.Equal(expectedManifest, ManifestOf(recovered));
                    // CSUN recovery writes the saved target sectors and the clean marker.
                    // Require a cut where rollback actually repairs bytes outside the marker.
                    if (undo && Marker(prefix) == dirty &&
                        (!prefix.AsSpan(0, marker).SequenceEqual(recovered.AsSpan(0, marker)) ||
                         !prefix.AsSpan(marker + 512).SequenceEqual(recovered.AsSpan(marker + 512))))
                        restoredUndoTarget = true;
                    await Run(directory, profile, "reopen", handler, recovered, false, "Sfs2RecoveryProbe::Recover");
                }
            }
            Assert.True(publishedJournals > 0, "No durable dirty marker referenced a journal descriptor.");
            if (undo) Assert.True(restoredUndoTarget, "No cut required undoing a durable target write.");
            output.WriteLine($"{operation}: {trace.SectorCuts} sector writes, {trace.FlushCuts} flushes, {trace.Prefixes.Count} distinct durable images.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
