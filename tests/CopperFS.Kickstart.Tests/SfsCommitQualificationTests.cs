using System.Text.Json;
using Task = System.Threading.Tasks.Task;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    private sealed class HomeRunCut(uint marker) : WritableHook
    {
        private byte[]? publication;
        private int firstCommand = -1;
        public readonly List<(byte[] Published, byte[] Partial)> Cuts = [];

        private static byte[] Snapshot(FileStream backing)
        {
            var image = new byte[checked((int)backing.Length)];
            backing.Position = 0;
            backing.ReadExactly(image);
            return image;
        }

        public override void Flush(FileStream backing)
        {
            base.Flush(backing);
            Span<byte> tag = stackalloc byte[4];
            backing.Position = marker * 512L;
            backing.ReadExactly(tag);
            // The first flush after TRFA is the durable publication. The
            // next flush completes home writes, even if TRFA is still present.
            if (tag.SequenceEqual("TRFA"u8) && firstCommand < 0)
            {
                publication = Snapshot(backing);
                firstCommand = WriteCommands;
            }
            else { publication = null; firstCommand = -1; }
        }

        public override void Write(FileStream backing, long offset, ReadOnlySpan<byte> source)
        {
            // The backing hook receives sectors. A second sector of the same
            // guest command proves the first already reached the device.
            if (publication != null && WriteCommands == firstCommand)
            {
                Cuts.Add((publication, Snapshot(backing)));
                publication = null;
            }
            base.Write(backing, offset, source);
            if (publication != null) firstCommand = WriteCommands;
        }
    }

    [SfsGroupedTheory] [InlineData("sfs")]
    public async Task SfsPartialHomeRunRecoversPublishedGroup(string profile)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == profile);
        var initial = File.ReadAllBytes(manifest.ClassicBase);
        var marker = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(initial.AsSpan(104)) + 2;
        var root = Path.Combine(Path.GetDirectoryName(path)!, "partial-home");
        var trace = new HomeRunCut(marker);
        await Run(root, profile, "trace", pair.Candidate, initial, true, "CorrectnessProbe::BenchmarkSfs", storage: trace);
        Assert.NotEmpty(trace.Cuts);
        for (var i = 0; i < trace.Cuts.Count; i++)
        {
            var reference = await Run(root, profile, $"published-{i}", pair.Original, trace.Cuts[i].Published,
                true, "OriginalTrstProbe::Recover");
            foreach (var arm in new[] { "candidate", "original" })
            {
                var replay = await Run(root, profile, $"{arm}-{i}", arm == "candidate" ? pair.Candidate : pair.Original,
                    trace.Cuts[i].Partial, true, "OriginalTrstProbe::Recover");
                var expected = CopperSFS.Check.SfsImageChecker.Check(new MemoryStream(reference), reservedBlocks: 2);
                var actual = CopperSFS.Check.SfsImageChecker.Check(new MemoryStream(replay), reservedBlocks: 2);
                Assert.Equal(expected.FreeBlocks, actual.FreeBlocks);
                Assert.Equal(expected.Entries.Select(e => (e.Path, e.Size, e.Sha256)),
                    actual.Entries.Select(e => (e.Path, e.Size, e.Sha256)));
            }
        }
    }

#if COPPERFS_SFS_POOL
    private sealed class JournalOriginalReadFault : WritableHook
    {
        private int command = -1;
        public bool Failed { get; private set; }

        public override void Read(FileStream backing, long offset, Span<byte> destination)
        {
            // The mixed fixture stages sectors 107 through 110 together.
            // Fail after the device has transferred the first journal original.
            if (offset == 107 * 512L) command = ReadCommands;
            if (!Failed && offset == 108 * 512L && command == ReadCommands)
            {
                Failed = true;
                throw new IOException("Injected failure in the second journal original");
            }
            base.Read(backing, offset, destination);
        }
    }

    [SfsGroupedTheory] [InlineData("sfs")]
    public async Task SfsJournalOriginalReadRetriesAfterPartialTransfer(string profile)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == profile);
        var initial = File.ReadAllBytes(manifest.ClassicBase);
        var root = Path.Combine(Path.GetDirectoryName(path)!, "journal-original-read");
        var expected = await Run(root, profile, "clean", pair.Candidate, initial, true, "CorrectnessProbe::BenchmarkSfs");
        var fault = new JournalOriginalReadFault();
        var actual = await Run(root, profile, "retry", pair.Candidate, initial, true, "CorrectnessProbe::BenchmarkSfs", storage: fault);
        Assert.True(fault.Failed);
        Assert.Equal(Hash(expected), Hash(actual));
    }

    // Writes arrive one sector at a time even for a multi-sector guest command.
    // Capture the medium immediately before and one sector into an unpublished
    // journal run. Neither image may expose the group the run describes.
    private sealed class JournalRunCut(uint marker) : WritableHook
    {
        private int command = -1;
        private byte[]? before;
        public readonly List<(byte[] Before, byte[] Partial)> Cuts = [];

        private static byte[] Snapshot(FileStream backing)
        {
            var bytes = new byte[checked((int)backing.Length)];
            backing.Position = 0;
            backing.ReadExactly(bytes);
            return bytes;
        }

        public override void Write(FileStream backing, long offset, ReadOnlySpan<byte> source)
        {
            if (command != WriteCommands)
            {
                command = WriteCommands;
                before = offset != marker * 512L && source.StartsWith("TRST"u8) ? Snapshot(backing) : null;
            }
            else if (before != null)
            {
                Assert.True(source.StartsWith("TRST"u8));
                Cuts.Add((before, Snapshot(backing)));
                before = null;
            }
            base.Write(backing, offset, source);
        }
    }

    [SfsGroupedTheory] [InlineData("sfs")]
    public async Task SfsPartialJournalRunLeavesPreviousGroupIntact(string profile)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == profile);
        var initial = File.ReadAllBytes(manifest.ClassicBase);
        var marker = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(initial.AsSpan(104)) + 2;
        var root = Path.Combine(Path.GetDirectoryName(path)!, "partial-journal");
        var trace = new JournalRunCut(marker);
        await Run(root, profile, "trace", pair.Candidate, initial, true, "CorrectnessProbe::BenchmarkSfs", storage: trace);
        Assert.NotEmpty(trace.Cuts);
        for (var i = 0; i < trace.Cuts.Count; i++)
        {
            var expected = CopperSFS.Check.SfsImageChecker.Check(new MemoryStream(trace.Cuts[i].Before), reservedBlocks: 2);
            foreach (var arm in new[] { "candidate", "original" })
            {
                var replay = await Run(root, profile, $"{arm}-{i}", arm == "candidate" ? pair.Candidate : pair.Original,
                    trace.Cuts[i].Partial, true, "OriginalTrstProbe::Recover");
                var actual = CopperSFS.Check.SfsImageChecker.Check(new MemoryStream(replay), reservedBlocks: 2);
                Assert.Equal(expected.FreeBlocks, actual.FreeBlocks);
                Assert.Equal(expected.Entries.Select(e => (e.Path, e.Size, e.Sha256)),
                    actual.Entries.Select(e => (e.Path, e.Size, e.Sha256)));
            }
        }
    }
#endif

    [CorrectnessTheory]
    [InlineData("sfs")]
    public async Task SfsCommitBoundaryTrace(string profile)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == profile);
        var initial = File.ReadAllBytes(manifest.ClassicBase);
        var marker = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(initial.AsSpan(104)) + 2;
        var root = Path.Combine(Path.GetDirectoryName(path)!, "commit-trace");
        foreach (var arm in new[] { "original", "candidate" })
        {
            var trace = new CommitTrace(marker);
            var handler = arm == "original" ? pair.Original : pair.Candidate;
            var image = await Run(root, profile, arm, handler, initial, true, "CorrectnessProbe::TraceCommitSfs",
                storage: trace, observe: (mark, cycle, _, _, _) => trace.Mark(mark, cycle));
            Assert.Equal(7, trace.DirtyAtBoundaryReply.Count);
            Assert.False(trace.DirtyAtBoundaryReply[^1]);
            if (arm == "candidate" && Environment.GetEnvironmentVariable("COPPERFS_SFS_GROUPED") == "1")
                for (var close = 160; close <= 170; close += 2)
                    Assert.Equal(trace.FlushesAtMarker[close], trace.FlushesAtMarker[close + 1]);
            CopperSFS.Check.SfsImageChecker.Check(new MemoryStream(image), reservedBlocks: 2);
            File.WriteAllText(Path.Combine(root, arm, "commit-trace.json"), JsonSerializer.Serialize(new
            { HandlerSha256 = Hash(File.ReadAllBytes(handler)), trace.Flushes, trace.WriteBytes, trace.Events }, Json));
        }
    }

    [CorrectnessTheory]
    [InlineData(false)] [InlineData(true)]
    public Task SfsCommitPerformance(bool chip) => CompareOriginalHandler("sfs", chip);

    [CorrectnessTheory]
    [InlineData("sfs")]
    public Task SfsCommitHandoff(string profile) => SharedMutationsAndEveryCleanHandoff(profile);

    [CorrectnessTheory]
    [InlineData("sfs")]
    public async Task SfsCommitIdenticalMetadata(string profile)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var handler = manifest.Profiles.Single(p => p.Name == profile).Candidate;
        var root = Path.Combine(Path.GetDirectoryName(path)!, "no-op");
        int writes = 0, flushes = 0, unchangedWrites = -1, unchangedFlushes = -1, changedFlushes = -1, completedFlushes = -1;
        var image = await Run(root, profile, "candidate", handler, File.ReadAllBytes(manifest.ClassicBase), true,
            "TrstNoOpProbe::ExecuteSfs", observe: (marker, _, _, w, f) =>
            {
                if (marker == 220) { writes = w; flushes = f; }
                if (marker == 221) { unchangedWrites = w - writes; unchangedFlushes = f - flushes; flushes = f; }
                if (marker == 223) changedFlushes = f - flushes;
                if (marker == 224) completedFlushes = f - flushes;
            });
        Assert.Equal(0, unchangedWrites); Assert.Equal(0, unchangedFlushes);
        // With no unpublished data, the clean pre-publication device flush
        // is elided. Journal, home images and clean marker still flush.
        Assert.Equal(Environment.GetEnvironmentVariable("COPPERFS_SFS_GROUPED") == "1" ? 0 : 3, changedFlushes);
        Assert.Equal(3, completedFlushes); // Explicit flush must finish the deferred transaction too.
        CopperSFS.Check.SfsImageChecker.Check(new MemoryStream(image), reservedBlocks: 2);
    }
}
