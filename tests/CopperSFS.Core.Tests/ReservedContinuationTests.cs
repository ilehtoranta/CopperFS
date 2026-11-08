using System.Buffers.Binary;
using System.Security.Cryptography;
using CopperSFS.Bench;
using Core = CopperSFS.AmigaSmartFileSystemWriteCore;
using static CopperSFS.Tests.ReliabilityFixture;

namespace CopperSFS.Tests;

public sealed partial class BatchBoundaryTests
{
    // Port of ReservedContinuationSurvivesEveryCutAndIoFailure at research commit 4a902a1.
    [Fact]
    public void ReservedContinuationSurvivesFaultsRecoveryCutsAndTornWrites()
    {
        const int chunk = 65536;
        var (initial, node) = FragmentedRelocationSeed([64], 512, 1024 * 1024);
        var first = new StudyDisk(initial, Bytes);
        var prefix = StudyFixture.Pattern(chunk, 146);
        var continuation = StudyFixture.Pattern(chunk, 147);
        Assert.True(Write(ref first, node, 0, prefix, out var actual, out var error), error.ToString());
        Assert.Equal((uint)chunk, actual);
        var work = first.Work;
        Assert.True(Core.HasOverwriteReservation(ref first, ref work, node));
        var reservation = first.Ram.AsSpan((int)work.Reservations.Raw, (int)Core.OverwriteReservationBytes).ToArray();
        var image = first.Snapshot();

        // Each injected request resumes the same live mount. Actual reopens below start with empty RAM.
        StudyDisk Resume()
        {
            var disk = new StudyDisk(image, Bytes, crashModel: true);
            reservation.CopyTo(disk.Ram, (int)disk.Work.Reservations.Raw);
            return disk;
        }

        var before = StudyFixture.Inspect(image, Bytes);
        var probe = Resume();
        Assert.True(Write(ref probe, node, chunk, continuation, out actual, out error), error.ToString());
        Assert.Equal((uint)chunk, actual);
        var committed = probe.Snapshot();
        var after = StudyFixture.Inspect(committed, Bytes);
        var expected = StudyFixture.Pattern(512 * Bytes, 111);
        prefix.CopyTo(expected, 0);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), before.Entries.Single(e => e.Node == node).Sha256);
        continuation.CopyTo(expected, chunk);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), after.Entries.Single(e => e.Node == node).Sha256);
        var seed = StudyFixture.Inspect(initial, Bytes);
        Assert.Equal(seed.FreeBlocks, before.FreeBlocks);
        Assert.Equal(seed.FreeBlocks, after.FreeBlocks);
        Assert.Equal(seed.Entries.Where(e => e.Node != node), before.Entries.Where(e => e.Node != node));
        Assert.Equal(before.Entries.Where(e => e.Node != node), after.Entries.Where(e => e.Node != node));

        (uint First, int Length) OwnedExtent(byte[] snapshot)
        {
            var tree = Block(snapshot, 5);
            Assert.Equal(1, tree[14]);
            var records = BinaryPrimitives.ReadUInt16BigEndian(tree[12..]);
            for (var i = 0; i < records; i++)
            {
                var record = tree[(16 + i * 14)..];
                if (U32(record, 8) == (0x80000000u | node))
                    return (U32(record, 0), BinaryPrimitives.ReadUInt16BigEndian(record[12..]));
            }
            throw new InvalidDataException("Missing first extent for reserved stream");
        }
        var (start, length) = OwnedExtent(image);
        Assert.NotEqual(64u, start);
        Assert.Equal(chunk / Bytes, length);
        Assert.Equal((start, 2 * chunk / Bytes), OwnedExtent(committed));

        void CheckReopened(ref StudyDisk reopened, bool rollbackOnly)
        {
            var saved = reopened.Snapshot();
            var report = StudyFixture.Inspect(saved, Bytes);
            var expectedReport = !rollbackOnly && report.Entries.Single(e => e.Node == node).Sha256 ==
                after.Entries.Single(e => e.Node == node).Sha256 ? after : before;
            Assert.Equal(expectedReport.Entries.ToArray(), report.Entries.ToArray());
            Assert.Equal(expectedReport.FreeBlocks, report.FreeBlocks);
            FsOps.Recover(ref reopened);
            Assert.True(saved.AsSpan().SequenceEqual(reopened.Snapshot()));
        }

        int exhaustive = 0, sampled = 0;
        void CheckCrashStates(StudyDisk disk, bool rollbackOnly = false)
        {
            var all = disk.CrashSpaceFits(4096);
            foreach (var snapshot in all ? disk.CrashImages() : disk.SampledCrashImages())
            {
                var reopened = new StudyDisk(snapshot, Bytes, crashModel: true);
                FsOps.Recover(ref reopened);
                CheckReopened(ref reopened, rollbackOnly);
                if (all) exhaustive++; else sampled++;
            }
        }

        byte[]? dirty = null;
        var marker = checked((int)U32(image, 104) + 2);
        for (var cut = 1; cut <= probe.Events; cut++)
        {
            var disk = Resume();
            disk.CutAfterEvent = cut;
            Assert.Throws<StudyPowerCut>(() => Write(ref disk, node, chunk, continuation, out _, out _));
            CheckCrashStates(disk);
            var saved = disk.Snapshot();
            if (U32(Block(saved, marker), 0) == 0x54524641 && U32(Block(saved, marker), 12) != 0)
                dirty = saved;
        }

        foreach (var persistent in new[] { false, true })
        for (var fail = 1; fail <= probe.Events; fail++)
        {
            var disk = Resume();
            disk.FailAtEvent = fail;
            disk.PersistentFailure = persistent;
            Assert.False(Write(ref disk, node, chunk, continuation, out actual, out _));
            Assert.Equal(0u, actual);
            var failedWork = disk.Work;
            Assert.False(Core.HasOverwriteReservation(ref disk, ref failedWork, node),
                $"Failed write/flush {fail} (persistent={persistent}) retained its reservation.");
            CheckCrashStates(disk);
        }

        Assert.NotNull(dirty);
        var recovery = new StudyDisk(dirty, Bytes, crashModel: true);
        var recoveryWork = recovery.Work;
        Assert.True(Core.Recover(ref recovery, ref recoveryWork), recoveryWork.Error.ToString());
        var recoveryReads = recovery.Counts.BlockReads;
        Assert.True(recovery.Events > 0);
        var recoveryEvents = recovery.Events;
        CheckReopened(ref recovery, rollbackOnly: true);
        for (var cut = 1; cut <= recoveryEvents; cut++)
        {
            var disk = new StudyDisk(dirty, Bytes, crashModel: true) { CutAfterEvent = cut };
            Assert.Throws<StudyPowerCut>(() => FsOps.Recover(ref disk));
            CheckCrashStates(disk, rollbackOnly: true);
        }

        // Interrupt reads of the dirty journal as well as writes of restored blocks.
        foreach (var persistent in new[] { false, true })
        {
            var failures = 0;
            for (var read = 1; read <= recoveryReads; read++)
            {
                var disk = new StudyDisk(dirty, Bytes, crashModel: true) { FailAtRead = read, PersistentFailure = persistent };
                var readWork = disk.Work;
                if (!Core.Recover(ref disk, ref readWork)) failures++;
                CheckCrashStates(disk, rollbackOnly: true);
            }
            Assert.True(failures > 0);
        }

        int recovered = 0, refused = 0;
        foreach (var bytes in new[] { 1, 256, 511 })
        for (var write = 1; write <= probe.Counts.BlockWrites; write++)
        {
            var disk = Resume();
            disk.TearAtWrite = write;
            disk.TearBytes = bytes;
            Assert.Throws<StudyPowerCut>(() => Write(ref disk, node, chunk, continuation, out _, out _));
            var reopened = new StudyDisk(disk.Snapshot(), Bytes, crashModel: true);
            var reopenWork = reopened.Work;
            if (Core.Recover(ref reopened, ref reopenWork))
            {
                Assert.True(AmigaSmartFileSystemCore.Mount(ref reopened, reopenWork.Meta));
                CheckReopened(ref reopened, rollbackOnly: false);
                recovered++;
            }
            else
            {
                Assert.False(AmigaSmartFileSystemCore.Mount(ref reopened, reopenWork.Meta));
                Assert.Throws<InvalidDataException>(() => StudyFixture.Inspect(reopened.Snapshot(), Bytes));
                refused++;
            }
        }
        Assert.True(recovered > 0);
        output.WriteLine($"{probe.Events} continuation and {recoveryEvents} recovery write/flush boundaries; " +
            $"{exhaustive} exhaustive and {sampled} sampled crash images; torn sectors: {recovered} recovered, {refused} refused.");
    }
}
