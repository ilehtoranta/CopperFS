using System.Security.Cryptography;
using CopperSFS.Check;

namespace CopperSFS.Bench;

internal delegate void DiskAction(ref StudyDisk disk);
internal sealed record Workload(string Name, byte[] Image, bool ReadOnly, long PayloadBytes,
    int Operations, DiskAction Prepare, DiskAction Execute, DiskAction Verify);
internal sealed record SpaceUsage(int BlockBytes, int FileCount, long FileBytes,
    long AllocatedFileBytes, long TailWasteBytes, long MetadataAndReservedBytes, int CoreScratchBytes);

internal sealed class Workloads
{
    internal const int Capacity = 8 * 1024 * 1024, FileCount = 128, LargeBytes = 1024 * 1024, PacketBytes = 64 * 1024;
    internal readonly List<Workload> Items = new();
    internal readonly SpaceUsage Space;
    private static readonly string[] Names = Enumerable.Range(0, FileCount).Select(i => $"File{i:D3}").ToArray();
    private static readonly byte[][] Small = Enumerable.Range(0, FileCount)
        .Select(i => StudyFixture.Pattern(64 + i * 977 % 3009, i + 1)).ToArray();
    private static readonly int[] Offsets = Small.Select((_, i) => Small.Take(i).Sum(b => b.Length)).ToArray();
    private static readonly byte[] SmallFlat = Small.SelectMany(b => b).ToArray();
    private static readonly byte[] Large = StudyFixture.Pattern(LargeBytes, 123), Replacement = StudyFixture.Pattern(LargeBytes, 456);
    private static readonly byte[] Patch = StudyFixture.Pattern(32, 789);
    private readonly int bytes;

    internal Workloads(int blockBytes)
    {
        bytes = blockBytes;
        var empty = StudyFixture.Empty(bytes, Capacity);
        var disk = new StudyDisk(empty, bytes);
        var nodes = new uint[FileCount];
        for (var i = 0; i < FileCount; i++) nodes[i] = FsOps.Create(ref disk, Names[i]);
        var smallEmpty = disk.Snapshot();
        var emptyFiles = Names.ToDictionary(n => "/" + n, _ => Array.Empty<byte>());
        Check(smallEmpty, emptyFiles);
        for (var i = 0; i < FileCount; i++) FsOps.Write(ref disk, nodes[i], 0, Small[i]);
        var smallFull = disk.Snapshot();
        var fullFiles = Names.Select((n, i) => (n, i)).ToDictionary(x => "/" + x.n, x => Small[x.i]);
        var report = Check(smallFull, fullFiles);
        var allocated = Small.Sum(data => ((data.Length + bytes - 1) / bytes) * (long)bytes);
        Space = new(bytes, FileCount, SmallFlat.Length, allocated, allocated - SmallFlat.Length,
            Capacity - (long)report.FreeBlocks * bytes - allocated, bytes * 4);
        disk = new StudyDisk(empty, bytes);
        var largeNode = FsOps.Create(ref disk, "Large");
        var largeEmpty = disk.Snapshot();
        Large.CopyTo(disk.Ram, (int)disk.Payload.Raw);
        SequentialWrite(ref disk, largeNode);
        var largeFull = disk.Snapshot();
        Check(largeFull, new() { ["/Large"] = Large });

        Items.Add(new("small-create", empty, false, 0, FileCount, NoPrepare,
            (ref StudyDisk d) => { foreach (var name in Names) FsOps.Create(ref d, name); },
            (ref StudyDisk d) => Check(d.Snapshot(), emptyFiles)));
        Items.Add(new("small-write", smallEmpty, false, SmallFlat.Length, FileCount,
            (ref StudyDisk d) => SmallFlat.CopyTo(d.Ram, (int)d.Payload.Raw),
            (ref StudyDisk d) => { for (var i = 0; i < FileCount; i++) FsOps.WriteFromRam(ref d, nodes[i], 0, Small[i].Length, Offsets[i]); },
            (ref StudyDisk d) => Check(d.Snapshot(), fullFiles)));
        Items.Add(new("small-read", smallFull, true, SmallFlat.Length, FileCount, NoPrepare,
            (ref StudyDisk d) => { for (var i = 0; i < FileCount; i++) FsOps.Read(ref d, nodes[i], 0, Small[i].Length, Offsets[i]); },
            (ref StudyDisk d) => VerifyRead(ref d, SmallFlat, fullFiles)));
        Items.Add(new("lookup", smallFull, true, 0, FileCount, NoPrepare,
            (ref StudyDisk d) =>
            {
                // A permutation avoids favoring only the first directory entries.
                for (var n = 0; n < FileCount; n++)
                {
                    var i = n * 73 % FileCount;
                    FsOps.Require(FsOps.Resolve(ref d, Names[i]) == nodes[i], "Lookup returned wrong object");
                }
            }, (ref StudyDisk d) => Check(d.Snapshot(), fullFiles)));
        var patched = fullFiles.ToDictionary(p => p.Key, p => p.Value.ToArray());
        foreach (var data in patched.Values) Patch.CopyTo(data, 0);
        Items.Add(new("small-patch-32", smallFull, false, 32 * FileCount, FileCount,
            (ref StudyDisk d) => Patch.CopyTo(d.Ram, (int)d.Payload.Raw),
            (ref StudyDisk d) => { foreach (var node in nodes) FsOps.WriteFromRam(ref d, node, 0, Patch.Length); },
            (ref StudyDisk d) => Check(d.Snapshot(), patched)));
        Items.Add(new("large-write", largeEmpty, false, LargeBytes, LargeBytes / PacketBytes,
            (ref StudyDisk d) => Large.CopyTo(d.Ram, (int)d.Payload.Raw),
            (ref StudyDisk d) => SequentialWrite(ref d, largeNode),
            (ref StudyDisk d) => Check(d.Snapshot(), new() { ["/Large"] = Large })));
        Items.Add(new("large-overwrite", largeFull, false, LargeBytes, LargeBytes / PacketBytes,
            (ref StudyDisk d) => Replacement.CopyTo(d.Ram, (int)d.Payload.Raw),
            (ref StudyDisk d) => SequentialWrite(ref d, largeNode),
            (ref StudyDisk d) => Check(d.Snapshot(), new() { ["/Large"] = Replacement })));
        Items.Add(new("large-read", largeFull, true, LargeBytes, LargeBytes / PacketBytes, NoPrepare,
            (ref StudyDisk d) =>
            {
                for (var at = 0; at < LargeBytes; at += PacketBytes) FsOps.Read(ref d, largeNode, (uint)at, PacketBytes, at);
            }, (ref StudyDisk d) => VerifyRead(ref d, Large, new() { ["/Large"] = Large })));
        if (bytes == 512)
        {
            // The original scalar allocator intersperses temporary journal holes
            // with file data. Also compare both writers on a file whose layout
            // was produced by the batched allocator, using identical seed bytes.
            disk = new StudyDisk(largeEmpty, bytes, batchedWrites: true);
            Large.CopyTo(disk.Ram, (int)disk.Payload.Raw);
            SequentialWrite(ref disk, largeNode);
            var contiguous = disk.Snapshot();
            Check(contiguous, new() { ["/Large"] = Large });
            Items.Add(new("large-overwrite-contiguous", contiguous, false, LargeBytes, LargeBytes / PacketBytes,
                (ref StudyDisk d) => Replacement.CopyTo(d.Ram, (int)d.Payload.Raw),
                (ref StudyDisk d) => SequentialWrite(ref d, largeNode),
                (ref StudyDisk d) => Check(d.Snapshot(), new() { ["/Large"] = Replacement })));
        }
    }
    private static void NoPrepare(ref StudyDisk disk) { }
    private static void SequentialWrite(ref StudyDisk disk, uint node)
    {
        for (var at = 0; at < LargeBytes; at += PacketBytes)
            FsOps.WriteFromRam(ref disk, node, (uint)at, PacketBytes, at);
    }
    private void VerifyRead(ref StudyDisk disk, byte[] data, Dictionary<string, byte[]> expected)
    {
        FsOps.Require(disk.Ram.AsSpan((int)disk.Payload.Raw, data.Length).SequenceEqual(data), "Read returned incorrect bytes");
        Check(disk.Snapshot(), expected);
    }
    private ImageReport Check(byte[] image, Dictionary<string, byte[]> expected)
    {
        var report = StudyFixture.Inspect(image, bytes);
        FsOps.Require(report.Entries.Count == expected.Count + 1, "Unexpected directory contents");
        foreach (var entry in report.Entries.Where(e => e.Path != "/"))
        {
            FsOps.Require(expected.TryGetValue(entry.Path, out var data), $"Unexpected file {entry.Path}");
            FsOps.Require(!entry.Directory && entry.Size == (ulong)data!.Length &&
                entry.Sha256 == Convert.ToHexString(SHA256.HashData(data)), $"Incorrect file {entry.Path}");
        }
        return report;
    }
}
