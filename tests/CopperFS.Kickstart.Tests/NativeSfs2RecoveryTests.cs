using System.Buffers.Binary;
using System.Text.Json;
using CopperSFS.Check;
using Task = System.Threading.Tasks.Task;

namespace CopperFS.Kickstart.Tests;

public sealed class Sfs2RecoveryTheoryAttribute : TheoryAttribute
{
    public Sfs2RecoveryTheoryAttribute()
    {
        foreach (var name in new[] { "COPPERSHARP_KICKSTART31_ROM", "COPPERFS_ORIGINAL_SFS", "COPPERFS_SFS2_TRST_HANDLER" })
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
        public readonly List<(int Cut, byte[] Image)> Prefixes = [];
        public int SectorCuts, FlushCuts;

        private void Boundary(bool newPrefix = false)
        {
            sequence++;
            if (sequence == cutAt) throw new PowerCut();
            if (cutAt == 0 && (sequence == 1 || newPrefix) && seen.Add(Hash(durable)))
                Prefixes.Add((sequence, durable));
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

    [Sfs2RecoveryTheory]
    [InlineData("Overwrite")]
    [InlineData("Truncate")]
    [InlineData("Rename")]
    [InlineData("Delete")]
    [InlineData("Create")]
    [InlineData("Comment")]
    public async Task NativeSfs2RecoveryMatrix(string operation)
    {
        var candidate = Environment.GetEnvironmentVariable("COPPERFS_SFS2_TRST_HANDLER")!;
        var original = Environment.GetEnvironmentVariable("COPPERFS_ORIGINAL_SFS")!;
        Assert.Equal("C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205", Hash(File.ReadAllBytes(original)));
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
            var written = await Run(directory, "sfs2-trst", operation, candidate, initial, true,
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
            Assert.Contains(trace.Prefixes, p => p.Image.AsSpan().SequenceEqual(written));
            Assert.Contains(trace.Prefixes, p => Marker(p.Image) == dirty);
            foreach (var (cut, expected) in trace.Prefixes)
            {
                var interrupted = new DurableTrace(initial, cut);
                await Assert.ThrowsAsync<PowerCut>(() => Run(directory, "sfs2-trst", "cut", candidate, initial,
                    true, "Sfs2RecoveryProbe::" + operation, storage: interrupted));
                var prefix = File.ReadAllBytes(Path.Combine(directory, "cut", "disk.hdf"));
                Assert.Equal(Hash(expected), Hash(prefix));
                if (Marker(prefix) == dirty)
                {
                    var head = BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(marker + 12));
                    Assert.Equal(0x54525354u, BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(checked((int)head * 512))));
                }
                foreach (var handler in new[] { candidate, original })
                {
                    var recovered = await Run(directory, "sfs2-trst", "recover", handler, prefix, true, "Sfs2RecoveryProbe::Recover");
                    Assert.Equal(clean, Marker(recovered));
                    var manifest = ManifestOf(recovered);
                    if (Marker(prefix) == dirty) Assert.Equal(after, manifest);
                    else Assert.Contains(manifest, new[] { before, after });
                    await Run(directory, "sfs2-trst", "reopen", handler, recovered, false, "Sfs2RecoveryProbe::Recover");
                }
            }
            output.WriteLine($"{operation}: {trace.SectorCuts} sector writes, {trace.FlushCuts} flushes, {trace.Prefixes.Count} distinct durable images.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
