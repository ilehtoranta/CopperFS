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
        var captured = false;

        void CapturePublishedTransaction(FileStream disk)
        {
            if (captured) return;
            Span<byte> header = stackalloc byte[16];
            disk.Position = markerOffset;
            disk.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32BigEndian(header) == 0x54524F4B) // TROK
            {
                captured = interrupted != null;
                return;
            }
            var head = BinaryPrimitives.ReadUInt32BigEndian(header[12..]);
            if (head == 0) return;
            Assert.Equal(0x54524641u, BinaryPrimitives.ReadUInt32BigEndian(header)); // TRFA
            disk.Position = (long)head * bytes;
            disk.ReadExactly(header);
            Assert.Equal(journalId, BinaryPrimitives.ReadUInt32BigEndian(header));

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
        var before = SfsImageChecker.Check(new MemoryStream(initial), reservedBlocks: 1);
        foreach (var name in new[] { "sfs2-trst", "sfs2-csun" })
        {
            var reader = manifest.Profiles.Single(p => p.Name == name);
            var recovered = await Run(root, name, "recover-with-" + name, reader.Candidate, interrupted, true,
                guestEntry: "CorrectnessProbe::RecoverSfs2");
            Assert.Equal(0x54524F4Bu, BinaryPrimitives.ReadUInt32BigEndian(recovered.AsSpan((int)markerOffset)));
            var after = SfsImageChecker.Check(new MemoryStream(recovered), reservedBlocks: 1);
            // The first mutation in the guest workload creates /AB.
            Assert.Equal(redo, after.Entries.Any(e => e.Directory && e.Path == "/AB"));
            Assert.Equal(before.Entries.Select(e => e.Path).Order(), after.Entries.Where(e => e.Path != "/AB").Select(e => e.Path).Order());
            Assert.Equal(before.Entries.Where(e => !e.Directory).OrderBy(e => e.Path),
                after.Entries.Where(e => !e.Directory).OrderBy(e => e.Path));
        }
    }
}
