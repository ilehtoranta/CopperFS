using System.Buffers.Binary;
using System.Text.Json;
using CopperSFS.Check;
using Xunit;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    [CorrectnessTheory]
    [InlineData("sfs2-trst", 0x54525354u)]
    [InlineData("sfs2-csun", 0x4353554Eu)]
    public async Task BothSfs2HandlersRecoverThePublishedJournal(string writer, uint journalId)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var root = Path.Combine(Path.GetDirectoryName(path)!, writer + "-recovery");
        var initial = ReadMedia.Sfs2();
        const int bytes = 512;
        var markerOffset = (BinaryPrimitives.ReadUInt32BigEndian(initial.AsSpan(104)) + 2L) * bytes;
        var redo = writer == "sfs2-trst";
        byte[]? interrupted = null;
        byte[]? committed = null;
        var captured = false;

        void CapturePublishedTransaction(FileStream disk)
        {
            if (committed != null) return;
            Span<byte> header = stackalloc byte[16];
            disk.Position = markerOffset;
            disk.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32BigEndian(header) == 0x54524F4B) // TROK
            {
                if (interrupted != null)
                {
                    captured = true; committed = new byte[initial.Length];
                    disk.Position = 0; disk.ReadExactly(committed);
                }
                return;
            }
            if (captured) return;
            if (redo && BinaryPrimitives.ReadUInt32BigEndian(header) == 0x54525354)
                Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(header[12..])); // Complete inline log.
            else
            {
                var head = BinaryPrimitives.ReadUInt32BigEndian(header[12..]);
                if (head == 0) return;
                Assert.Equal(0x54524641u, BinaryPrimitives.ReadUInt32BigEndian(header)); // TRFA
                disk.Position = (long)head * bytes;
                disk.ReadExactly(header);
                Assert.Equal(journalId, BinaryPrimitives.ReadUInt32BigEndian(header));
            }

            // For redo, take the first published log before home writes. For undo,
            // retain the last durable dirty image before the writer marks it clean.
            interrupted ??= new byte[initial.Length];
            disk.Position = 0;
            disk.ReadExactly(interrupted);
            captured = redo;
        }

        var source = manifest.Profiles.Single(p => p.Name == writer);
        await Run(root, writer, "write", source.Candidate, initial, true, afterFlush: CapturePublishedTransaction);
        Assert.True(captured);
        Assert.NotNull(interrupted);
        Assert.NotNull(committed);
        var before = SfsImageChecker.Check(new MemoryStream(initial), reservedBlocks: 1);
        // A grouped writer can accept several packets before the first close.
        // Its first clean checkpoint is the exact expected redo result.
        var expected = redo ? SfsImageChecker.Check(new MemoryStream(committed!), reservedBlocks: 1) : before;
        // Original SFS can replay the native redo format. CSUN remains Copper
        // recovery only; never ask the original handler to interpret that log.
        if (redo) Assert.Equal(source.OriginalSha256, Hash(File.ReadAllBytes(source.Original)));
        foreach (var name in redo ? new[] { "sfs2-trst", "sfs2-csun", "original" } : new[] { "sfs2-trst", "sfs2-csun" })
        {
            var handler = name == "original" ? source.Original : manifest.Profiles.Single(p => p.Name == name).Candidate;
            var recovered = await Run(root, writer, "recover-with-" + name, handler, interrupted, true,
                guestEntry: "CorrectnessProbe::RecoverSfs2");
            Assert.Equal(0x54524F4Bu, BinaryPrimitives.ReadUInt32BigEndian(recovered.AsSpan((int)markerOffset)));
            var after = SfsImageChecker.Check(new MemoryStream(recovered), reservedBlocks: 1);
            // The first mutation in the guest workload creates /AB.
            Assert.Equal(redo, after.Entries.Any(e => e.Directory && e.Path == "/AB"));
            Assert.Equal(expected.Entries.Select(e => e.Path).Order(), after.Entries.Select(e => e.Path).Order());
            Assert.Equal(expected.Entries.Where(e => !e.Directory).OrderBy(e => e.Path),
                after.Entries.Where(e => !e.Directory).OrderBy(e => e.Path));
        }
    }
}
