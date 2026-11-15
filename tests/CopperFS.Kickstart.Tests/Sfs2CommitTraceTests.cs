using System.Buffers.Binary;
using System.Text.Json;
using CopperSFS.Check;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    private sealed class CommitTrace(uint marker) : WritableHook
    {
        public readonly List<object> Events = [];
        public readonly List<bool> DirtyAtBoundaryReply = [];
        public readonly Dictionary<int, int> FlushesAtMarker = [];
        private bool dirty;
        public void Mark(int marker, long cycles)
        {
            Events.Add(new { Kind = "packet", Marker = marker, Cycles = cycles, Flushes, WriteBytes, Dirty = dirty });
            FlushesAtMarker[marker] = Flushes;
            if (marker is 161 or 163 or 165 or 167 or 169 or 171 or 175) DirtyAtBoundaryReply.Add(dirty);
        }
        public override void Write(FileStream backing, long offset, ReadOnlySpan<byte> source)
        {
            var tag = source.Length >= 4 ? BinaryPrimitives.ReadUInt32BigEndian(source) : 0;
            var previous = new byte[source.Length];
            backing.Position = offset;
            backing.ReadExactly(previous);
            Events.Add(new { Kind = "write", Command = WriteCommands, Offset = offset, Bytes = source.Length,
                Tag = tag.ToString("X8"), Identical = source.SequenceEqual(previous) });
            base.Write(backing, offset, source);
            dirty = true;
        }
        public override void Flush(FileStream backing)
        {
            Span<byte> header = stackalloc byte[16]; backing.Position = (long)marker * 512;
            backing.ReadExactly(header);
            Events.Add(new { Kind = "flush", MarkerTag = BinaryPrimitives.ReadUInt32BigEndian(header).ToString("X8"),
                Next = BinaryPrimitives.ReadUInt32BigEndian(header[12..]), WriteBytes });
            base.Flush(backing);
            dirty = false;
        }
    }

    [CorrectnessTheory] [InlineData("sfs2-trst")]
    public async Task TraceSfs2CommitBoundaries(string profile)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == profile);
        Assert.Equal(pair.OriginalSha256, Hash(File.ReadAllBytes(pair.Original)));
        var initial = ReadMedia.Sfs2(); var marker = BinaryPrimitives.ReadUInt32BigEndian(initial.AsSpan(104)) + 2;
        var root = Path.Combine(Path.GetDirectoryName(path)!, "commit-trace");
        foreach (var arm in new[] { "original", "candidate" })
        {
            var trace = new CommitTrace(marker);
            var handler = arm == "original" ? pair.Original : pair.Candidate;
            var result = await Run(root, profile, arm, handler, initial, true, "CorrectnessProbe::TraceCommitSfs2",
                storage: trace, observe: (mark, cycle, _, _, _) => trace.Mark(mark, cycle));
            SfsImageChecker.Check(new MemoryStream(result), reservedBlocks: 1);
            Assert.Equal(7, trace.DirtyAtBoundaryReply.Count);
            if (arm == "candidate")
            {
                if (Environment.GetEnvironmentVariable("COPPERFS_SFS2_DEFERRED_CLOSE") == "1")
                {
                    for (var close = 160; close <= 170; close += 2)
                        Assert.Equal(trace.FlushesAtMarker[close], trace.FlushesAtMarker[close + 1]);
                    Assert.False(trace.DirtyAtBoundaryReply[^1]); // Explicit flush remains durable.
                }
                else Assert.All(trace.DirtyAtBoundaryReply, Assert.False);
            }
            File.WriteAllText(Path.Combine(root, arm, "commit-trace.json"), JsonSerializer.Serialize(new
            { HandlerSha256 = Hash(File.ReadAllBytes(handler)), trace.Flushes, trace.WriteBytes, trace.Events }, Json));
        }
    }
}
