using Amiga;
using CopperSFS.Bench;
using static CopperSFS.AmigaSmartFileSystem2WriteCore;
using static CopperSFS.Tests.Sfs2WriteTests;

namespace CopperSFS.Tests;

public sealed class Sfs2TrstGroupTests
{
    private static void Initialize(ref StudyDisk disk, bool owned)
    {
        if (owned)
        {
            var work = WorkFor(disk);
            Assert.True(Sfs2MountContext.Mount(ref disk, ref work, APTR.FromPointer(disk.Payload.Raw + 900000), 16384), work.Error.ToString());
        }
        Assert.True(InitializeTrstGroup(ref disk, disk.BatchBuffer, 65536));
    }

    private static void Accept(ref StudyDisk disk, uint node, byte value, ulong position = 0)
    {
        disk.Ram[(int)disk.Payload.Raw] = value;
        var w = WorkFor(disk);
        var r = new Request { Node = node, Buffer = disk.Payload, Length = 1, Position = position };
        Assert.True(ExecuteTrstGroup(ref disk, ref w, ref r, TrstOperation.Write, disk.BatchBuffer, out var actual), w.Error.ToString());
        Assert.Equal(1u, actual);
    }

    [Theory] [InlineData(512)] [InlineData(1024)] [InlineData(4096)]
    public void PredecessorCloneSkipsUnchangedImagesAndInvalidatesStaleHandles(int bytes)
    {
        var (image, node) = Seed(bytes); var seed = new StudyDisk(image, bytes);
        PutFile(ref seed, node, 0, StudyFixture.Pattern(8 * bytes)); image = seed.Snapshot();
        var disk = new StudyDisk(image, bytes); Initialize(ref disk, true);
        var expected = new StudyDisk(image, bytes);
        for (uint i = 0; i < 4; i++)
        { Accept(ref disk, node, (byte)(90 + i), i * (uint)bytes); PutFile(ref expected, node, i * (uint)bytes, [(byte)(90 + i)]); }
        var active = disk.RedoOverlay; var prefix = disk.ReadUInt32(active, 20);
        var full = prefix + disk.ReadUInt32(active) * ((uint)bytes + Sfs2MetadataStore.SlotBytes);
        var first = Sfs2MetadataStore.Slot(ref disk, active, 0);
        Assert.True(Sfs2MetadataStore.Acquire(ref disk, active, disk.ReadUInt32(first), 0, out var handle));
        var stale = handle; Assert.True(Sfs2MetadataStore.Release(ref disk, ref handle));
        disk.CopyRanges = new();
        Accept(ref disk, node, 94, 0); PutFile(ref expected, node, 0, [94]);
        var begin = disk.BatchBuffer.Raw; var end = begin + 65536;
        var clone = disk.CopyRanges!.Where(c => c.From >= begin && c.From < end && c.To >= begin && c.To < end).ToArray();
        Assert.NotEmpty(clone); Assert.True(clone.Sum(c => c.Bytes) < full, $"Clone copied {clone.Sum(c => c.Bytes)} of {full} bytes");
        // Reuse the old arena at another generation, including attempt wrap.
        disk.WriteUInt32(disk.Sfs2Context, 48, uint.MaxValue);
        Accept(ref disk, node, 95, 1); PutFile(ref expected, node, 1, [95]);
        Assert.False(Sfs2MetadataStore.Current(ref disk, stale));
        Assert.False(Sfs2MetadataStore.Release(ref disk, ref stale));
        var work = WorkFor(disk); Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
        // New groups can reuse slot ordinals/revisions for entirely different keys.
        Accept(ref disk, node, 96, 6u * (uint)bytes); PutFile(ref expected, node, 6u * (uint)bytes, [96]);
        Accept(ref disk, node, 97, 5u * (uint)bytes); PutFile(ref expected, node, 5u * (uint)bytes, [97]);
        Accept(ref disk, node, 98, 4u * (uint)bytes); PutFile(ref expected, node, 4u * (uint)bytes, [98]);
        Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
        Assert.Equal(Manifest(expected.Snapshot(), bytes), Manifest(disk.Snapshot(), bytes));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void RejectedPartialOrSealedTrialCannotQualifyForPredecessorReuse(bool rejectAfterSeal)
    {
        var (image, node) = Seed(); var seed = new StudyDisk(image, 512);
        PutFile(ref seed, node, 0, StudyFixture.Pattern(65536)); image = seed.Snapshot();
        var disk = new StudyDisk(image, 512); Initialize(ref disk, true);
        Accept(ref disk, node, 91); Accept(ref disk, node, 92, 512); Accept(ref disk, node, 93, 1024);
        var active = disk.RedoOverlay; var full = disk.ReadUInt32(active, 20) + disk.ReadUInt32(active) * (512 + Sfs2MetadataStore.SlotBytes);
        var accepted = disk.Ram.AsSpan((int)active.Raw, (int)full).ToArray();
        var context = disk.Sfs2Context; var epoch = disk.ReadUInt32(context, 8); var generation = disk.ReadUInt32(context, 12);
        for (var retry = 0; retry < 2; retry++)
        {
            if (rejectAfterSeal) { disk.WriteUInt32(context, 8, uint.MaxValue); disk.WriteUInt32(context, 12, uint.MaxValue); }
            disk.Ram.AsSpan((int)disk.Payload.Raw, 65536).Fill(23);
            var work = WorkFor(disk); var request = new Request { Node = node, Buffer = disk.Payload, Length = rejectAfterSeal ? 1u : 65536u };
            Assert.False(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out var actual));
            Assert.Equal(0u, actual); Assert.Equal(active, disk.RedoOverlay);
            Assert.Equal(accepted, disk.Ram.AsSpan((int)active.Raw, (int)full).ToArray());
            disk.WriteUInt32(context, 8, epoch); disk.WriteUInt32(context, 12, generation);
        }
        disk.CopyRanges = new(); Accept(ref disk, node, 94, 1536);
        Assert.Contains(disk.CopyRanges!, c => c.From == active.Raw && c.To == disk.RedoOverlay.Raw && c.Bytes == full);
        var w = WorkFor(disk); Assert.True(FlushTrstGroup(ref disk, ref w, disk.BatchBuffer));
        var expected = new StudyDisk(image, 512);
        PutFile(ref expected, node, 0, [91]); PutFile(ref expected, node, 512, [92]);
        PutFile(ref expected, node, 1024, [93]); PutFile(ref expected, node, 1536, [94]);
        Assert.Equal(Manifest(expected.Snapshot()), Manifest(disk.Snapshot()));
    }

    [Theory]
    [InlineData(512, false)] [InlineData(1024, false)] [InlineData(512, true)] [InlineData(1024, true)]
    public void AcceptedViewSurvivesFailedPacketAndCommitsOnce(int bytes, bool owned)
    {
        var (image, node) = Seed(bytes); var seed = new StudyDisk(image, bytes);
        PutFile(ref seed, node, 0, StudyFixture.Pattern(65536, 198)); image = seed.Snapshot();
        var disk = new StudyDisk(image, bytes);
        Initialize(ref disk, owned);
        Accept(ref disk, node, 93); var active = disk.RedoOverlay;
        var w = WorkFor(disk); var r = new Request { Node = node, Buffer = disk.Payload, Length = 65536 };
        Assert.False(ExecuteTrstGroup(ref disk, ref w, ref r, TrstOperation.Write, disk.BatchBuffer, out var actual));
        Assert.Equal(DOS.Error.NoFreeStore, w.Error); Assert.Equal(0u, actual); Assert.Equal(active, disk.RedoOverlay);
        Accept(ref disk, node, 94, 512);
        Assert.Equal(0, disk.Counts.BlockWrites); Assert.Equal(0, disk.Counts.Flushes);
        Assert.Equal(image, disk.Snapshot());
        Assert.Equal(1, AmigaSmartFileSystemReadOnlyCore.Read(ref disk, node, 0, disk.Payload, 1, w.Meta, w.Data, out _));
        Assert.Equal(93, disk.Ram[(int)disk.Payload.Raw]);
        Assert.True(FlushTrstGroup(ref disk, ref w, disk.BatchBuffer), w.Error.ToString());
        Assert.Equal(4, disk.Counts.Flushes); Assert.False(TrstGroupPending(ref disk, disk.BatchBuffer));
        var expected = new StudyDisk(image, bytes); PutFile(ref expected, node, 0, [93]); PutFile(ref expected, node, 512, [94]);
        Assert.Equal(Manifest(expected.Snapshot(), bytes), Manifest(disk.Snapshot(), bytes));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void NewDataRemainsPrivateAndTrimCannotCertifyPendingGroup(bool owned)
    {
        var (image, node) = Seed(); var disk = new StudyDisk(image, 512);
        Initialize(ref disk, owned);
        disk.Ram.AsSpan((int)disk.Payload.Raw, 1024).Fill(97);
        var w = WorkFor(disk);
        var append = new Request { Node = node, Buffer = disk.Payload, Length = 1024, Position = 1027 };
        Assert.True(ExecuteTrstGroup(ref disk, ref w, ref append, TrstOperation.Write, disk.BatchBuffer, out _));
        Assert.Equal(Manifest(image), Manifest(disk.Snapshot()));
        var control = APTR.FromPointer(disk.Payload.Raw + 65536);
        Assert.False(Trim(ref disk, ref w, control, 0, out _));
        Assert.Equal(DOS.Error.DiskNotValidated, w.Error); Assert.Equal(0, disk.Discarded);
        // A later rejected packet must not overwrite unpublished accepted data.
        disk.Ram.AsSpan((int)disk.Payload.Raw, 65536).Fill(23);
        var r = new Request { Node = node, Buffer = disk.Payload, Length = 1024 * 1024, Position = 1536 };
        Assert.False(ExecuteTrstGroup(ref disk, ref w, ref r, TrstOperation.Write, disk.BatchBuffer, out _));
        Assert.Equal(1, AmigaSmartFileSystemReadOnlyCore.Read(ref disk, node, 1536, disk.Payload, 1, w.Meta, w.Data, out _));
        Assert.Equal(97, disk.Ram[(int)disk.Payload.Raw]);
        Assert.True(FlushTrstGroup(ref disk, ref w, disk.BatchBuffer)); Inspect(disk.Snapshot());
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void ReadFailuresLeaveAcceptedGroupUnchanged(bool owned)
    {
        var (image, node) = Seed();
        for (var failure = 1; failure <= 24; failure++)
        {
            var disk = new StudyDisk(image, 512);
            Initialize(ref disk, owned); Accept(ref disk, node, 93);
            var active = disk.RedoOverlay; var w = WorkFor(disk);
            disk.FailAtRead = (int)disk.Counts.BlockReads + failure;
            var r = new Request { Node = node, Buffer = disk.Payload, Length = 1024, Position = 900 };
            var ok = ExecuteTrstGroup(ref disk, ref w, ref r, TrstOperation.Write, disk.BatchBuffer, out _);
            if (!ok) Assert.Equal(active, disk.RedoOverlay);
            disk.FailAtRead = 0;
            Assert.Equal(1, AmigaSmartFileSystemReadOnlyCore.Read(ref disk, node, 0, disk.Payload, 1, w.Meta, w.Data, out _));
            Assert.Equal(93, disk.Ram[(int)disk.Payload.Raw]);
            Assert.True(FlushTrstGroup(ref disk, ref w, disk.BatchBuffer)); Inspect(disk.Snapshot());
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void EveryGroupCommitCutRecoversWholeOldOrNewView(bool owned)
    {
        var (image, node) = Seed(); var before = Manifest(image);
        var baseline = new StudyDisk(image, 512, crashModel: true);
        Initialize(ref baseline, owned);
        Accept(ref baseline, node, 93); Accept(ref baseline, node, 94, 512);
        Accept(ref baseline, node, 95, 513); Accept(ref baseline, node, 96);
        var w = WorkFor(baseline); Assert.True(FlushTrstGroup(ref baseline, ref w, baseline.BatchBuffer));
        var after = Manifest(baseline.Snapshot());
        for (var cut = 1; cut <= baseline.Events; cut++)
        {
            var disk = new StudyDisk(image, 512, crashModel: true) { CutAfterEvent = cut };
            Initialize(ref disk, owned);
            Accept(ref disk, node, 93); Accept(ref disk, node, 94, 512);
            Accept(ref disk, node, 95, 513); Accept(ref disk, node, 96);
            var work = WorkFor(disk);
            Assert.Throws<StudyPowerCut>(() => FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
            foreach (var saved in disk.CrashImages())
            {
                var restart = new StudyDisk(saved, 512); var recovery = WorkFor(restart);
                Assert.True(Recover(ref restart, ref recovery), recovery.Error.ToString());
                Assert.Contains(Manifest(restart.Snapshot()), new[] { before, after });
            }
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void FailedCommitRequiresRecoveryAndRejectsFurtherPackets(bool owned)
    {
        var (image, node) = Seed(); var baseline = new StudyDisk(image, 512);
        Initialize(ref baseline, owned); Accept(ref baseline, node, 93);
        var w = WorkFor(baseline); Assert.True(FlushTrstGroup(ref baseline, ref w, baseline.BatchBuffer));
        for (var failure = 1; failure <= baseline.Events; failure++)
        {
            var disk = new StudyDisk(image, 512) { FailAtEvent = failure };
            Initialize(ref disk, owned); Accept(ref disk, node, 93);
            w = WorkFor(disk); Assert.False(FlushTrstGroup(ref disk, ref w, disk.BatchBuffer));
            var r = new Request { Node = node, Position = 32 };
            Assert.False(ExecuteTrstGroup(ref disk, ref w, ref r, TrstOperation.SetProtection, disk.BatchBuffer, out _));
            Assert.Equal(DOS.Error.DiskNotValidated, w.Error);
            disk.FailAtEvent = 0; Assert.True(RecoverTrstGroup(ref disk, ref w, disk.BatchBuffer));
            Assert.False(TrstGroupPending(ref disk, disk.BatchBuffer)); Inspect(disk.Snapshot());
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void RecoveryRetriesFailedFinalBarrierBeforeRestoringDurableEligibility(bool owned)
    {
        var (image, node) = Seed(); var baseline = new StudyDisk(image, 512, crashModel: true);
        Initialize(ref baseline, owned); Accept(ref baseline, node, 93);
        var work = WorkFor(baseline); Assert.True(FlushTrstGroup(ref baseline, ref work, baseline.BatchBuffer));
        var disk = new StudyDisk(image, 512, crashModel: true) { FailAtEvent = baseline.Events };
        Initialize(ref disk, owned); Accept(ref disk, node, 93); work = WorkFor(disk);
        Assert.False(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
        // Fail the recovery barrier too: seeing TROK in volatile storage must
        // neither clear the failed group nor certify the context for discard.
        disk.FailAtEvent = disk.Events + 1;
        Assert.False(RecoverTrstGroup(ref disk, ref work, disk.BatchBuffer));
        Assert.True(TrstGroupPending(ref disk, disk.BatchBuffer));
        if (owned) Assert.False(Sfs2MountContext.Durable(ref disk, disk.Sfs2Context));
        var flushes = disk.Counts.Flushes; disk.FailAtEvent = 0;
        Assert.True(RecoverTrstGroup(ref disk, ref work, disk.BatchBuffer));
        Assert.Equal(flushes + 1, disk.Counts.Flushes);
        Assert.Single(disk.CrashImages()); // No unbarriered clean marker remains.
        Assert.Equal(Manifest(baseline.Snapshot()), Manifest(disk.CrashImages().Single()));
        if (owned) Assert.True(Sfs2MountContext.Durable(ref disk, disk.Sfs2Context));
    }
}
