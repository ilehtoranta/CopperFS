using Amiga;
using CopperSFS.Bench;
using Xunit.Abstractions;
using static CopperSFS.AmigaSmartFileSystem2WriteCore;
using static CopperSFS.Tests.Sfs2WriteTests;

namespace CopperSFS.Tests;

public sealed class Sfs2BatchedWriteTests(ITestOutputHelper output)
{
    private static (byte[] Image, uint Node, uint Keep) Fragmented(int count, int chunk)
    {
        var disk = new StudyDisk(Empty(), 512); var node = CreateFile(ref disk, "Fragmented"); var keep = CreateFile(ref disk, "Keep");
        for (var i = 0; i < count; i++)
        {
            PutFile(ref disk, node, (uint)(i * chunk), StudyFixture.Pattern(chunk, i + 10), true);
            PutFile(ref disk, keep, (uint)(i * chunk), StudyFixture.Pattern(chunk, i + 100), true);
        }
        Inspect(disk.Snapshot()); return (disk.Snapshot(), node, keep);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullBatchesRelocationAndCutsPreserveEveryFile(bool relocate)
    {
        const int length = 20 * 4096;
        var (image, node, keep) = Fragmented(20, 4096); var before = Manifest(image);
        var data = StudyFixture.Pattern(length, 1234);
        bool Mutate(ref StudyDisk disk)
        {
            data.CopyTo(disk.Ram, (int)disk.Payload.Raw); var w = WorkFor(disk);
            var r = new Request { Node = node, Buffer = disk.Payload, Length = length };
            return WriteBatched(ref disk, ref w, ref r, disk.BatchBuffer, 65536, out _, relocate);
        }
        var baseline = new StudyDisk(image, 512, crashModel: true); Assert.True(Mutate(ref baseline)); var after = Manifest(baseline.Snapshot());
        var beforeReport = Inspect(image); var afterReport = Inspect(baseline.Snapshot());
        Assert.Equal(beforeReport.FreeBlocks, afterReport.FreeBlocks);
        Assert.Equal(beforeReport.Entries.Single(e => e.Node == keep), afterReport.Entries.Single(e => e.Node == keep));
        long exhaustive = 0, sampled = 0;
        for (var cut = 1; cut <= baseline.Events; cut++)
        {
            var disk = new StudyDisk(image, 512, crashModel: true) { CutAfterEvent = cut };
            Assert.Throws<StudyPowerCut>(() => Mutate(ref disk));
            var fits = disk.CrashSpaceFits(4096);
            foreach (var saved in fits ? disk.CrashImages() : disk.SampledCrashImages())
            {
                var recovered = new StudyDisk(saved, 512); var w = WorkFor(recovered);
                Assert.True(Recover(ref recovered, ref w), w.Error.ToString());
                Assert.Contains(Manifest(recovered.Snapshot()), new[] { before, after });
                var stable = recovered.Snapshot(); Assert.True(Recover(ref recovered, ref w)); Assert.Equal(stable, recovered.Snapshot());
                if (fits) exhaustive++; else sampled++;
            }
        }
        // Delete through the split tree, then reuse the now-empty extent root.
        var clean = new StudyDisk(baseline.Snapshot(), 512); var work = WorkFor(clean);
        Assert.True(Remove(ref clean, ref work, node), work.Error.ToString());
        Assert.True(Remove(ref clean, ref work, keep), work.Error.ToString()); Inspect(clean.Snapshot());
        node = CreateFile(ref clean, "AfterPruning"); PutFile(ref clean, node, 0, [0x42]); Inspect(clean.Snapshot());
        output.WriteLine($"SFS2 batches relocate={relocate}: {exhaustive} exhaustive image checks; {sampled} sampled image checks; every range sector cut covered.");
    }

    [Fact]
    public void BatchedFailureReportsZeroAndRecoveryRestoresAnEntireRequest()
    {
        var (image, node, _) = Fragmented(8, 4096); var before = Manifest(image);
        bool Mutate(ref StudyDisk disk, out uint actual)
        {
            var data = StudyFixture.Pattern(32768, 300); data.CopyTo(disk.Ram, (int)disk.Payload.Raw);
            var r = new Request { Node = node, Buffer = disk.Payload, Length = 32768 }; var w = WorkFor(disk);
            return WriteBatched(ref disk, ref w, ref r, disk.BatchBuffer, 65536, out actual);
        }
        var baseline = new StudyDisk(image, 512, crashModel: true); Assert.True(Mutate(ref baseline, out var actual)); Assert.Equal(32768u, actual);
        var after = Manifest(baseline.Snapshot()); long checks = 0;
        for (var point = 1; point <= baseline.Events; point++)
            foreach (var persistent in new[] { false, true })
            {
                var disk = new StudyDisk(image, 512, crashModel: true) { FailAtEvent = point, PersistentFailure = persistent };
                Assert.False(Mutate(ref disk, out actual)); Assert.Equal(0u, actual);
                foreach (var saved in disk.SampledCrashImages())
                {
                    var recovered = new StudyDisk(saved, 512); var w = WorkFor(recovered); Assert.True(Recover(ref recovered, ref w), w.Error.ToString());
                    Assert.Contains(Manifest(recovered.Snapshot()), new[] { before, after }); checks++;
                }
            }
        output.WriteLine($"SFS2 batch failures: {checks} sampled image checks across all write/flush errors.");
    }
}
