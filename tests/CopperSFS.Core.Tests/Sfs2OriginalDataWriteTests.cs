using Amiga;
using CopperSFS.Bench;
using static CopperSFS.AmigaSmartFileSystem2WriteCore;
using static CopperSFS.Tests.Sfs2WriteTests;

namespace CopperSFS.Tests;

public sealed class Sfs2OriginalDataWriteTests
{
    [Fact]
    public void EmptyOverwriteOverlayMaterializesBeforeFollowingMetadataMutation()
    {
        var (image, node) = Seed(); var disk = Mount(image, true); var work = WorkFor(disk);
        disk.Ram.AsSpan((int)disk.Payload.Raw, 512).Fill(0xD4);
        var request = new Request { Node = node, Buffer = disk.Payload, Length = 512 };
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out _));
        var empty = disk.RedoOverlay;
        Assert.Equal(0u, disk.ReadUInt32(empty)); Assert.Equal(0u, disk.ReadUInt32(empty, 4));
        request = new Request { Node = node, Position = 128 };
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.SetProtection, disk.BatchBuffer, out _));
        Assert.NotEqual(empty, disk.RedoOverlay); Assert.True(disk.ReadUInt32(disk.RedoOverlay, 4) > 0);
        Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
        var reference = Mount(image, false); var referenceWork = WorkFor(reference);
        reference.Ram.AsSpan((int)reference.Payload.Raw, 512).Fill(0xD4);
        request = new Request { Node = node, Buffer = reference.Payload, Length = 512 };
        Assert.True(ExecuteTrstGroup(ref reference, ref referenceWork, ref request, TrstOperation.Write, reference.BatchBuffer, out _));
        request = new Request { Node = node, Position = 128 };
        Assert.True(ExecuteTrstGroup(ref reference, ref referenceWork, ref request, TrstOperation.SetProtection, reference.BatchBuffer, out _));
        Assert.True(FlushTrstGroup(ref reference, ref referenceWork, reference.BatchBuffer));
        Assert.Equal(Manifest(reference.Snapshot()), Manifest(disk.Snapshot()));
    }

    [Fact]
    public void AlignedAppendPublishesReservedScalarsAndInvalidatesBorrowedHandles()
    {
        var seed = new StudyDisk(Empty(), 512); var node = CreateFile(ref seed, "Growth");
        var disk = Mount(seed.Snapshot(), true); var work = WorkFor(disk);
        var cursor = APTR.FromPointer(disk.Payload.Raw + 800000);
        disk.Ram.AsSpan((int)disk.Payload.Raw, 4096).Fill(0xA7);
        var request = new Request { Node = node, Buffer = disk.Payload, Length = 4096 };
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out _, cursor));
        var active = disk.RedoOverlay; var generation = disk.ReadUInt32(disk.Sfs2Context, 12);
        Assert.True(Sfs2MetadataStore.Acquire(ref disk, active, work.ExtentRoot, 0x424E4443, out var pin));
        var stale = pin; Assert.True(Sfs2MetadataStore.Release(ref disk, ref pin));
        disk.CopyRanges = new(); disk.Ram.AsSpan((int)disk.Payload.Raw, 4096).Fill(0xD4);
        request.Position = 4096;
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out var actual, cursor));
        Assert.Equal(4096u, actual); Assert.Equal(active, disk.RedoOverlay);
        Assert.Equal(generation + 1, disk.ReadUInt32(disk.Sfs2Context, 12));
        Assert.True(Sfs2MetadataStore.Unpinned(ref disk, active));
        Assert.True(Sfs2MetadataStore.Acquire(ref disk, active, work.ExtentRoot, 0x424E4443, out pin));
        Assert.False(Sfs2MetadataStore.Current(ref disk, stale));
        Assert.True(Sfs2MetadataStore.Release(ref disk, ref pin));
        var begin = disk.BatchBuffer.Raw; var end = begin + 65536;
        Assert.DoesNotContain(disk.CopyRanges!, c => c.From >= begin && c.From < end && c.To >= begin && c.To < end);
        Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
        var expected = new StudyDisk(seed.Snapshot(), 512);
        PutFile(ref expected, node, 0, Enumerable.Repeat((byte)0xA7, 4096).ToArray(), true);
        PutFile(ref expected, node, 4096, Enumerable.Repeat((byte)0xD4, 4096).ToArray(), true);
        Assert.Equal(Manifest(expected.Snapshot()), Manifest(disk.Snapshot()));
    }

    [Fact]
    public void FailedReservedAppendKeepsEveryAcceptedByteAndCanRetry()
    {
        var seed = new StudyDisk(Empty(), 512); var node = CreateFile(ref seed, "Growth"); var image = seed.Snapshot();
        for (var failure = 1; failure <= 8; failure++)
        {
            var disk = Mount(image, true); var work = WorkFor(disk);
            var cursor = APTR.FromPointer(disk.Payload.Raw + 800000);
            disk.Ram.AsSpan((int)disk.Payload.Raw, 4096).Fill(0xA7);
            var request = new Request { Node = node, Buffer = disk.Payload, Length = 4096 };
            Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out _, cursor));
            var active = disk.RedoOverlay;
            var bytes = disk.ReadUInt32(active, 20) + disk.ReadUInt32(active) * (512 + Sfs2MetadataStore.SlotBytes);
            var accepted = disk.Ram.AsSpan((int)active.Raw, (int)bytes).ToArray();
            disk.Ram.AsSpan((int)disk.Payload.Raw, 4096).Fill(0xD4); request.Position = 4096;
            disk.FailAtEvent = disk.Events + failure;
            Assert.False(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out var actual, cursor));
            Assert.Equal(0u, actual); Assert.Equal(active, disk.RedoOverlay);
            Assert.Equal(accepted, disk.Ram.AsSpan((int)active.Raw, (int)bytes).ToArray());
            Assert.True(Sfs2MetadataStore.Unpinned(ref disk, active));
            disk.FailAtEvent = 0;
            Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out actual, cursor));
            Assert.Equal(4096u, actual); Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
            Inspect(disk.Snapshot());
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    public void FailedFirstBulkWritePreservesPendingCreateAndCanRetry(int failedSector)
    {
        var image = Empty();
        var disk = Mount(image, true);
        var work = WorkFor(disk);
        System.Text.Encoding.ASCII.GetBytes("Growth").CopyTo(disk.Ram, (int)disk.NameBuffer.Raw);
        var create = new Request { Directory = 1, Name = disk.NameBuffer, Length = 6 };
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref create, TrstOperation.CreateFile, disk.BatchBuffer, out _));
        var active = disk.RedoOverlay;
        var generation = disk.ReadUInt32(disk.Sfs2Context, 12);
        var acceptedBytes = disk.ReadUInt32(active, 20) + disk.ReadUInt32(active) * (512 + Sfs2MetadataStore.SlotBytes);
        var accepted = disk.Ram.AsSpan((int)active.Raw, (int)acceptedBytes).ToArray();
        Assert.True(Sfs2MetadataStore.Find(ref disk, active, work.ExtentRoot).IsNull);

        var payload = StudyFixture.Pattern(65536, 93);
        payload.CopyTo(disk.Ram, (int)disk.Payload.Raw);
        var write = new Request { Node = create.Node, Buffer = disk.Payload, Length = (uint)payload.Length };
        disk.FailAtEvent = disk.Events + failedSector;
        Assert.False(ExecuteTrstGroup(ref disk, ref work, ref write, TrstOperation.Write, disk.BatchBuffer, out var actual));
        Assert.Equal(0u, actual);
        Assert.Equal(active, disk.RedoOverlay);
        Assert.Equal(generation, disk.ReadUInt32(disk.Sfs2Context, 12));
        Assert.Equal(accepted, disk.Ram.AsSpan((int)active.Raw, (int)acceptedBytes).ToArray());
        Assert.True(Sfs2MetadataStore.Unpinned(ref disk, active));
        Assert.True(AmigaSmartFileSystemReadOnlyCore.Describe(ref disk, create.Node, work.Meta, out var empty));
        Assert.Equal(0ul, empty.Size);

        disk.FailAtEvent = 0;
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref write, TrstOperation.Write, disk.BatchBuffer, out actual));
        Assert.Equal((uint)payload.Length, actual);
        Assert.Equal(active, disk.RedoOverlay); // The reserved first extent was accepted in place.
        Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));

        var reference = new StudyDisk(image, 512);
        var referenceNode = CreateFile(ref reference, "Growth");
        PutFile(ref reference, referenceNode, 0, payload, true);
        Assert.Equal(Manifest(reference.Snapshot()), Manifest(disk.Snapshot()));
    }

    [Fact]
    public void ReservedAppendChecksAcceptanceWrapBeforePayloadIo()
    {
        var seed = new StudyDisk(Empty(), 512); var node = CreateFile(ref seed, "Growth");
        var disk = Mount(seed.Snapshot(), true); var work = WorkFor(disk);
        disk.Ram.AsSpan((int)disk.Payload.Raw, 512).Fill(0xA7);
        var request = new Request { Node = node, Buffer = disk.Payload, Length = 512 };
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out _));
        var active = disk.RedoOverlay;
        var bytes = disk.ReadUInt32(active, 20) + disk.ReadUInt32(active) * (512 + Sfs2MetadataStore.SlotBytes);
        var accepted = disk.Ram.AsSpan((int)active.Raw, (int)bytes).ToArray(); var writes = disk.Counts.BlockWrites;
        disk.WriteUInt32(disk.Sfs2Context, 8, uint.MaxValue); disk.WriteUInt32(disk.Sfs2Context, 12, uint.MaxValue);
        request.Position = 512;
        Assert.False(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out var actual));
        Assert.Equal(0u, actual); Assert.Equal(DOS.Error.DiskNotValidated, work.Error);
        Assert.Equal(writes, disk.Counts.BlockWrites);
        Assert.Equal(accepted, disk.Ram.AsSpan((int)active.Raw, (int)bytes).ToArray());
    }

    [Fact]
    public void ReservedAppendCommitCutsRecoverOldOrCompleteMetadata()
    {
        var seed = new StudyDisk(Empty(), 512); var node = CreateFile(ref seed, "Growth"); var image = seed.Snapshot();
        StudyDisk Start()
        {
            var disk = new StudyDisk(image, 512, crashModel: true) { UsesOriginalSfs2DataWrites = true };
            var work = WorkFor(disk);
            Assert.True(Sfs2MountContext.Mount(ref disk, ref work, APTR.FromPointer(disk.Payload.Raw + 900000), 16508));
            Assert.True(InitializeTrstGroup(ref disk, disk.BatchBuffer, 65536)); return disk;
        }
        void Run(ref StudyDisk disk)
        {
            var work = WorkFor(disk);
            for (var i = 0; i < 2; i++)
            {
                disk.Ram.AsSpan((int)disk.Payload.Raw, 512).Fill((byte)(0xA7 + i));
                var request = new Request { Node = node, Position = (uint)i * 512, Buffer = disk.Payload, Length = 512 };
                Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out _));
            }
            Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
        }
        var baseline = Start(); Run(ref baseline); var before = Manifest(image); var after = Manifest(baseline.Snapshot());
        for (var cut = 1; cut <= baseline.Events; cut++)
        {
            var disk = Start(); disk.CutAfterEvent = cut; Assert.Throws<StudyPowerCut>(() => Run(ref disk));
            foreach (var saved in disk.CrashImages())
            {
                var recovered = new StudyDisk(saved, 512); var work = WorkFor(recovered);
                Assert.True(Recover(ref recovered, ref work));
                Assert.Contains(Manifest(recovered.Snapshot()), new[] { before, after });
            }
        }
    }

    [Theory] [InlineData(-1)] [InlineData(0)] [InlineData(1)] [InlineData(31)] [InlineData(32)]
    public void FirstBulkExtentMatchesTheCheckedWriterForEmptyFullAndBranchRoots(int otherFiles)
    {
        var seed = new StudyDisk(Empty(), 512);
        if (otherFiles == -1)
        {
            var low = CreateFile(ref seed, "Low"); PutFile(ref seed, low, 0, StudyFixture.Pattern(65536, 1), true);
            var high = CreateFile(ref seed, "High"); PutFile(ref seed, high, 0, StudyFixture.Pattern(512, 2), true);
            Assert.True(Mutate(ref seed, low, "remove", true));
        }
        for (var i = 0; i < otherFiles; i++)
        {
            var other = CreateFile(ref seed, $"Other{i:D2}");
            PutFile(ref seed, other, 0, StudyFixture.Pattern(512, i), true);
        }
        var node = CreateFile(ref seed, "Growth"); var image = seed.Snapshot();
        string Run(bool original)
        {
            var disk = Mount(image, original); var work = WorkFor(disk);
            var cursor = APTR.FromPointer(disk.Payload.Raw + 800000);
            foreach (var position in new[] { 0u, 65536u })
            {
                var length = position == 0 ? 65536 : 4096;
                StudyFixture.Pattern(length, (int)position + 93).CopyTo(disk.Ram, (int)disk.Payload.Raw);
                var request = new Request { Node = node, Position = position, Length = (uint)length, Buffer = disk.Payload };
                Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out var actual, cursor));
                Assert.Equal((uint)length, actual);
            }
            Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
            return Manifest(disk.Snapshot());
        }
        Assert.Equal(Run(false), Run(true));
    }

    [Fact]
    public void RegionClonesPreserveAppendsAcrossFailedPacketsAndUntrackedMetadataEdits()
    {
        var seed = new StudyDisk(Empty(), 512); var node = CreateFile(ref seed, "Growth");
        PutFile(ref seed, node, 0, StudyFixture.Pattern(4096, 79), true);
        var image = seed.Snapshot();
        (string Manifest, long Copied) Run(bool original)
        {
            var disk = Mount(image, original); var work = WorkFor(disk);
            disk.CopyRanges = new();
            var cursor = APTR.FromPointer(disk.Payload.Raw + 800000);
            for (var i = 0; i < 24; i++)
            {
                StudyFixture.Pattern(4096, 80 + i).CopyTo(disk.Ram, (int)disk.Payload.Raw);
                var request = new Request { Node = node, Position = (uint)(i + 1) * 4096, Buffer = disk.Payload, Length = 4096 };
                if (i == 4)
                {
                    var active = disk.RedoOverlay;
                    var size = disk.ReadUInt32(active, 20) + disk.ReadUInt32(active) * (512 + Sfs2MetadataStore.SlotBytes);
                    var accepted = disk.Ram.AsSpan((int)active.Raw, (int)size).ToArray();
                    disk.FailAtEvent = disk.Events + 1;
                    Assert.False(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out _, cursor));
                    Assert.Equal(active, disk.RedoOverlay);
                    Assert.Equal(accepted, disk.Ram.AsSpan((int)active.Raw, (int)size).ToArray());
                    disk.FailAtEvent = 0;
                }
                Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out _, cursor));
                if (i is 7 or 9)
                {
                    disk.Ram.AsSpan((int)disk.Payload.Raw, 79).Fill((byte)'c');
                    request = new Request { Node = node, Buffer = disk.Payload, Length = i == 7 ? 79u : 0u };
                    Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.SetComment, disk.BatchBuffer, out _));
                }
            }
            var begin = disk.BatchBuffer.Raw; var end = begin + 65536;
            var copied = disk.CopyRanges!.Where(c => c.From >= begin && c.From < end && c.To >= begin && c.To < end)
                .Sum(c => (long)c.Bytes);
            Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
            return (Sfs2WriteTests.Manifest(disk.Snapshot()), copied);
        }
        var full = Run(false); var sparse = Run(true);
        Assert.Equal(full.Manifest, sparse.Manifest);
        Assert.True(sparse.Copied < full.Copied, $"Region clone copied {sparse.Copied} versus {full.Copied} bytes.");
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void OriginalAppendCommitCutsKeepOldOrAcceptedMetadata(bool retainedWrites, bool firstRun)
    {
        var (image, node) = Seed();
        if (firstRun)
        {
            var seed = new StudyDisk(Empty(), 512); node = CreateFile(ref seed, "Growth"); image = seed.Snapshot();
        }
        var before = Manifest(image);
        StudyDisk Start()
        {
            var disk = new StudyDisk(image, 512, crashModel: true)
            {
                UsesOriginalSfs2DataWrites = true, SupportsRetainedBlockWrites = retainedWrites
            };
            var work = WorkFor(disk);
            Assert.True(Sfs2MountContext.Mount(ref disk, ref work, APTR.FromPointer(disk.Payload.Raw + 900000), 16508));
            Assert.True(InitializeTrstGroup(ref disk, disk.BatchBuffer, 65536));
            return disk;
        }
        bool Run(ref StudyDisk disk)
        {
            if (!firstRun) return Mutate(ref disk, node, "appendrun", true);
            var work = WorkFor(disk); StudyFixture.Pattern(2048, 95).CopyTo(disk.Ram, (int)disk.Payload.Raw);
            var request = new Request { Node = node, Buffer = disk.Payload, Length = 2048 };
            return ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out _) &&
                FlushTrstGroup(ref disk, ref work, disk.BatchBuffer);
        }
        var baseline = Start(); Assert.True(Run(ref baseline));
        Assert.Equal(retainedWrites, baseline.RetainedWrites > 0);
        var after = Manifest(baseline.Snapshot());
        for (var cut = 1; cut <= baseline.Events; cut++)
        {
            var disk = Start(); disk.CutAfterEvent = cut;
            Assert.Throws<StudyPowerCut>(() => Run(ref disk));
            foreach (var saved in disk.CrashImages())
            {
                var recovered = new StudyDisk(saved, 512); var work = WorkFor(recovered);
                Assert.True(Recover(ref recovered, ref work));
                Assert.Contains(Manifest(recovered.Snapshot()), new[] { before, after });
            }
        }
    }

    [Theory]
    [InlineData(1, false)] [InlineData(3, false)]
    [InlineData(1, true)] [InlineData(3, true)]
    public void AppendingAcrossSizeCarryKeepsMetadataChecksumsAndObjectNeighbours(int paddingNameLength, bool pendingMetadata)
    {
        var seed = new StudyDisk(Empty(), 512);
        CreateFile(ref seed, new string('p', paddingNameLength));
        var node = CreateFile(ref seed, "Growth");
        PutFile(ref seed, node, 0, StudyFixture.Pattern(65530, 331), true);
        var image = seed.Snapshot();
        string Run(bool original)
        {
            var disk = Mount(image, original); var work = WorkFor(disk);
            if (pendingMetadata)
            {
                var protection = new Request { Node = node, Position = 16 };
                Assert.True(ExecuteTrstGroup(ref disk, ref work, ref protection, TrstOperation.SetProtection,
                    disk.BatchBuffer, out _));
            }
            var position = 65530u;
            foreach (var length in new[] { 17, 1024, 4096, 1, 4096 })
            {
                StudyFixture.Pattern(length, (int)position).CopyTo(disk.Ram, (int)disk.Payload.Raw);
                var request = new Request { Node = node, Position = position, Buffer = disk.Payload, Length = (uint)length };
                Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write,
                    disk.BatchBuffer, out var actual));
                Assert.Equal((uint)length, actual); position += actual;
            }
            Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
            // The independent image checker checks checksums, allocation and all
            // neighbouring records as well as the file's complete payload.
            return Manifest(disk.Snapshot());
        }
        Assert.Equal(Run(false), Run(true));
    }

    private static StudyDisk Mount(byte[] image, bool original)
    {
        var disk = new StudyDisk(image, 512) { UsesOriginalSfs2DataWrites = original, OriginalDataWrites = [] };
        var work = WorkFor(disk);
        Assert.True(Sfs2MountContext.Mount(ref disk, ref work, APTR.FromPointer(disk.Payload.Raw + 900000), 16508));
        Assert.True(InitializeTrstGroup(ref disk, disk.BatchBuffer, 65536));
        return disk;
    }

    [Theory]
    [InlineData(false, 0, 1024)] [InlineData(true, 0, 1024)]
    [InlineData(false, 7, 777)] [InlineData(true, 7, 777)]
    public void OnlyOptInWritesExistingPayloadBeforeMetadataCommit(bool original, int position, int length)
    {
        var (image, node) = Seed(); var disk = Mount(image, original); var work = WorkFor(disk);
        var payload = StudyFixture.Pattern(length, 91);
        payload.CopyTo(disk.Ram, (int)disk.Payload.Raw);
        var request = new Request { Node = node, Position = (uint)position, Buffer = disk.Payload, Length = (uint)length };
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out var actual));
        Assert.Equal((uint)length, actual);
        Assert.True(TrstGroupPending(ref disk, disk.BatchBuffer));
        Assert.False(Sfs2MountContext.Durable(ref disk, disk.Sfs2Context));
        Assert.Equal(0, disk.Counts.Flushes);
        Assert.Equal(original ? (position == 0 ? 1 : 2) : 0, disk.OriginalDataWrites!.Count);
        if (original)
        {
            Assert.Equal(0u, disk.ReadUInt32(disk.RedoOverlay)); // Data-only commit must still flush.
            var first = disk.OriginalDataWrites[0].Block;
            Assert.Equal(2u, (uint)disk.OriginalDataWrites.Sum(r => r.Count));
            var expected = image.ToArray(); payload.CopyTo(expected, (int)first * 512 + position);
            Assert.Equal(expected, disk.Snapshot()); // No metadata has escaped its trial.
            for (var i = 0u; i < 2; i++)
            {
                Assert.True(AmigaSmartFileSystemRedoArena.TryFind(ref disk, disk.RedoOverlay, first + i, out var staged));
                Assert.True(staged.IsNull);
            }
        }
        else Assert.Equal(image, disk.Snapshot());
        Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
        if (original) Assert.True(disk.Counts.Flushes > 0);
        var reference = new StudyDisk(image, 512);
        PutFile(ref reference, node, (uint)position, payload, true);
        Assert.Equal(Manifest(reference.Snapshot()), Manifest(disk.Snapshot()));
    }

    [Fact]
    public void OriginalWriteStillUpdatesAnArchiveBitInAcceptedMetadata()
    {
        var (image, node) = Seed(); var disk = Mount(image, true); var work = WorkFor(disk);
        var request = new Request { Node = node, Position = 16 };
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.SetProtection, disk.BatchBuffer, out _));
        disk.Ram.AsSpan((int)disk.Payload.Raw, 512).Fill(0xD4);
        request = new Request { Node = node, Buffer = disk.Payload, Length = 512 };
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out _));
        Assert.True(AmigaSmartFileSystemReadOnlyCore.Describe(ref disk, node, work.Meta, out var entry));
        Assert.Equal(0u, entry.Protection);
        Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
        Inspect(disk.Snapshot());
    }

    [Fact]
    public void FailedOriginalOverwriteMayChangePayloadPrefixButNeverPublishesMetadata()
    {
        var (image, node) = Seed(); var disk = Mount(image, true); var work = WorkFor(disk);
        var payload = StudyFixture.Pattern(1024, 95);
        payload.CopyTo(disk.Ram, (int)disk.Payload.Raw);
        disk.FailAtEvent = 2;
        var request = new Request { Node = node, Buffer = disk.Payload, Length = 1024 };
        Assert.False(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out var actual));
        Assert.Equal(0u, actual); Assert.Equal(DOS.Error.DiskNotValidated, work.Error);
        Assert.False(TrstGroupPending(ref disk, disk.BatchBuffer));
        var expected = image.ToArray();
        payload.AsSpan(0, 512).CopyTo(expected.AsSpan((int)disk.OriginalDataWrites![0].Block * 512, 512));
        Assert.Equal(expected, disk.Snapshot());
        // The same handle can retry without any failed trial metadata escaping.
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out actual));
        Assert.Equal(1024u, actual); Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
        var reference = new StudyDisk(image, 512); PutFile(ref reference, node, 0, payload, true);
        Assert.Equal(Manifest(reference.Snapshot()), Manifest(disk.Snapshot()));
    }

    [Fact]
    public void ReusedCommittedPayloadRemainsPrivateAcrossFollowingOverwrite()
    {
        var (image, node) = Seed(); var disk = Mount(image, true); var work = WorkFor(disk);
        var cursor = APTR.FromPointer(disk.Payload.Raw + 800000);
        var request = new Request { Node = node };
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Truncate, disk.BatchBuffer, out _));
        foreach (var value in new byte[] { 0xA7, 0xD4 })
        {
            disk.Ram.AsSpan((int)disk.Payload.Raw, 512).Fill(value);
            request = new Request { Node = node, Buffer = disk.Payload, Length = 512 };
            Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out _, cursor));
            Assert.True(TryContinueOriginalDataWrite(ref disk, disk.BatchBuffer, cursor, ref request, out var handled, out _));
            Assert.False(handled);
            Assert.Empty(disk.OriginalDataWrites!);
            Assert.Equal(image, disk.Snapshot());
        }
        Assert.Equal(512, AmigaSmartFileSystemReadOnlyCore.Read(ref disk, node, 0, disk.Payload, 512, work.Meta, work.Data, out _));
        Assert.All(disk.Ram.AsSpan((int)disk.Payload.Raw, 512).ToArray(), b => Assert.Equal(0xD4, b));
        Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
        Inspect(disk.Snapshot());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DataOnlyPacketsKeepAcceptedArenaAndFollowingMetadataTrial(bool pendingMetadata)
    {
        var (image, node) = Seed(); var disk = Mount(image, true); var work = WorkFor(disk);
        var request = new Request { Node = node, Position = 2 };
        if (pendingMetadata)
            Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.SetProtection, disk.BatchBuffer, out _));
        APTR accepted = default;
        foreach (var value in new byte[] { 0xA7, 0xD4 })
        {
            disk.Ram.AsSpan((int)disk.Payload.Raw, 1024).Fill(value);
            request = new Request { Node = node, Buffer = disk.Payload, Length = 1024 };
            Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out var actual));
            Assert.Equal(1024u, actual);
            if (accepted.IsNotNull) Assert.Equal(accepted, disk.RedoOverlay);
            accepted = disk.RedoOverlay;
        }
        // The next metadata operation must clone the same acknowledged view.
        request = new Request { Node = node, Position = 14 };
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.SetProtection, disk.BatchBuffer, out _));
        Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
        var check = new StudyDisk(disk.Snapshot(), 512); var checkWork = WorkFor(check);
        Assert.Equal(1024, AmigaSmartFileSystemReadOnlyCore.Read(ref check, node, 0, check.Payload, 1024,
            checkWork.Meta, checkWork.Data, out _));
        Assert.All(check.Ram.AsSpan((int)check.Payload.Raw, 1024).ToArray(), b => Assert.Equal(0xD4, b));
        Assert.True(AmigaSmartFileSystemReadOnlyCore.Describe(ref check, node, checkWork.Meta, out var description));
        Assert.Equal(14u, description.Protection);
        Inspect(disk.Snapshot());
    }

    [Fact]
    public void EmptyDataOnlyCommitCannotHideAFailedDeviceFlush()
    {
        var (image, node) = Seed(); var disk = Mount(image, true); var work = WorkFor(disk);
        disk.Ram.AsSpan((int)disk.Payload.Raw, 1024).Fill(0xD4);
        var request = new Request { Node = node, Buffer = disk.Payload, Length = 1024 };
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out _));
        Assert.Equal(0u, disk.ReadUInt32(disk.RedoOverlay));
        disk.FailAtEvent = disk.Events + 1;
        Assert.False(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
        Assert.Equal(DOS.Error.DiskNotValidated, work.Error);
        Assert.False(Sfs2MountContext.Durable(ref disk, disk.Sfs2Context));
        Assert.True(RecoverTrstGroup(ref disk, ref work, disk.BatchBuffer));
        Assert.True(Sfs2MountContext.Durable(ref disk, disk.Sfs2Context));
        Inspect(disk.Snapshot());
    }

    [Fact]
    public void RetainedExtentStartsAnOverwriteAfterFlushAndExpiresOnMetadataChange()
    {
        var seed = new StudyDisk(Empty(), 512);
        var node = CreateFile(ref seed, "Growth");
        var image = seed.Snapshot();
        var disk = Mount(image, true);
        var work = WorkFor(disk);
        var cursor = APTR.FromPointer(disk.Payload.Raw + 800000);
        var retained = APTR.FromPointer(cursor.Raw + OriginalDataCursorBytes);
        disk.Clear(cursor, OriginalDataCursorBytes);
        var original = StudyFixture.Pattern(8192, 701);
        original.CopyTo(disk.Ram, (int)disk.Payload.Raw);
        for (var at = 0u; at < 8192; at += 4096)
        {
            var append = new Request { Node = node, Position = at, Buffer = APTR.FromPointer(disk.Payload.Raw + at), Length = 4096 };
            Assert.True(ExecuteTrstGroup(ref disk, ref work, ref append, TrstOperation.Write, disk.BatchBuffer, out _, cursor));
        }
        disk.Copy(cursor, retained, OriginalDataCursorBytes);
        Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));

        var payload = StudyFixture.Pattern(8192, 702);
        payload.CopyTo(disk.Ram, (int)disk.Payload.Raw);
        var overwrite = new Request { Node = node, Buffer = disk.Payload, Length = 8192 };
        disk.FailAtEvent = disk.Events + 2;
        Assert.False(TryContinueOriginalDataWrite(ref disk, disk.BatchBuffer, retained, ref overwrite,
            out var handled, out var error, reuseAfterFlush: true));
        Assert.False(handled);
        Assert.Equal(DOS.Error.DiskNotValidated, error);
        Assert.False(TrstGroupPending(ref disk, disk.BatchBuffer));
        Assert.True(disk.RedoOverlay.IsNull);

        disk.FailAtEvent = 0;
        Assert.True(TryContinueOriginalDataWrite(ref disk, disk.BatchBuffer, retained, ref overwrite,
            out handled, out error, reuseAfterFlush: true));
        Assert.True(handled);
        Assert.Equal(DOS.Error.None, error);
        Assert.True(TrstGroupPending(ref disk, disk.BatchBuffer));
        Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
        PutFile(ref seed, node, 0, payload, true);
        Assert.Equal(Manifest(seed.Snapshot()), Manifest(disk.Snapshot()));

        var protection = new Request { Node = node, Position = 4 };
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref protection, TrstOperation.SetProtection, disk.BatchBuffer, out _));
        Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
        var writes = disk.Events;
        Assert.True(TryContinueOriginalDataWrite(ref disk, disk.BatchBuffer, retained, ref overwrite,
            out handled, out error, reuseAfterFlush: true));
        Assert.False(handled);
        Assert.Equal(writes, disk.Events);
    }

    [Fact]
    public void OriginalContinuationCoversTheCheckedExtentAndExpiresOnMetadataOrFlush()
    {
        var (image, node) = Seed(); var disk = Mount(image, true); var work = WorkFor(disk);
        var cursor = APTR.FromPointer(disk.Payload.Raw + 800000);
        disk.Ram.AsSpan((int)disk.Payload.Raw, 512).Fill(0xA7);
        var request = new Request { Node = node, Buffer = disk.Payload, Length = 512 };
        Assert.True(TryContinueOriginalDataWrite(ref disk, disk.BatchBuffer, cursor, ref request, out var handled, out _));
        Assert.False(handled);
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out _, cursor));
        Assert.Equal(node, disk.ReadUInt32(cursor));
        request.Position = 512;
        disk.Ram.AsSpan((int)disk.Payload.Raw, 512).Fill(0xD4);
        Assert.True(TryContinueOriginalDataWrite(ref disk, disk.BatchBuffer, cursor, ref request, out handled, out _));
        Assert.True(handled);
        Assert.True(TrstGroupPending(ref disk, disk.BatchBuffer));
        Assert.Equal(2, disk.OriginalDataWrites!.Count);
        var metadata = new Request { Node = node, Position = 4 };
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref metadata, TrstOperation.SetProtection, disk.BatchBuffer, out _));
        Assert.True(TryContinueOriginalDataWrite(ref disk, disk.BatchBuffer, cursor, ref request, out handled, out _));
        Assert.False(handled);
        Assert.False(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out _, cursor));
        Assert.Equal(DOS.Error.WriteProtected, work.Error);
        Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
        Assert.True(TryContinueOriginalDataWrite(ref disk, disk.BatchBuffer, cursor, ref request, out handled, out _));
        Assert.False(handled);
        Assert.Equal(1024, AmigaSmartFileSystemReadOnlyCore.Read(ref disk, node, 0, disk.Payload, 1024,
            work.Meta, work.Data, out _));
        Assert.All(disk.Ram.AsSpan((int)disk.Payload.Raw, 512).ToArray(), b => Assert.Equal(0xA7, b));
        Assert.All(disk.Ram.AsSpan((int)disk.Payload.Raw + 512, 512).ToArray(), b => Assert.Equal(0xD4, b));
        Inspect(disk.Snapshot());
    }

    [Fact]
    public void OriginalContinuationRejectsAnOldMountEvenWhenViewAndArenaMatch()
    {
        var (image, node) = Seed(); var disk = Mount(image, true); var work = WorkFor(disk);
        var cursor = APTR.FromPointer(disk.Payload.Raw + 800000);
        disk.Ram.AsSpan((int)disk.Payload.Raw, 512).Fill(0xA7);
        var request = new Request { Node = node, Buffer = disk.Payload, Length = 512 };
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out _, cursor));
        Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
        Assert.True(Sfs2MountContext.Mount(ref disk, ref work, disk.Sfs2Context, 16508));
        Assert.True(InitializeTrstGroup(ref disk, disk.BatchBuffer, 65536));
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out _));
        Assert.Equal(disk.ReadUInt32(cursor, 12), disk.ReadUInt32(disk.Sfs2Context, 12));
        Assert.Equal(disk.ReadUInt32(cursor, 20), disk.RedoOverlay.Raw);
        Assert.NotEqual(disk.ReadUInt32(cursor, 8), disk.ReadUInt32(disk.Sfs2Context, 8));
        Assert.True(TryContinueOriginalDataWrite(ref disk, disk.BatchBuffer, cursor, ref request, out var handled, out _));
        Assert.False(handled);
    }

    [Theory]
    [InlineData(17)] [InlineData(512)]
    public void AcceptedWriteDescriptionTracksGrowthFromAnEmptyFileAndExpiresOnProtection(int length)
    {
        var (image, node) = Seed(); var disk = Mount(image, true); var work = WorkFor(disk);
        var cursor = APTR.FromPointer(disk.Payload.Raw + 800000);
        var request = new Request { Node = node };
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Truncate, disk.BatchBuffer, out _));
        var expected = new byte[3 * length];
        for (var i = 0; i < 3; i++)
        {
            var payload = StudyFixture.Pattern(length, (byte)(91 + i));
            payload.CopyTo(expected, i * length); payload.CopyTo(disk.Ram, (int)disk.Payload.Raw);
            request = new Request { Node = node, Position = (uint)(i * length), Buffer = disk.Payload, Length = (uint)length };
            Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out var actual, cursor));
            Assert.Equal((uint)length, actual);
            Assert.Equal((uint)((i + 1) * length), disk.ReadUInt32(cursor, 24));
            Assert.NotEqual(0u, disk.ReadUInt32(cursor, 44));
            Assert.True(TryContinueOriginalDataWrite(ref disk, disk.BatchBuffer, cursor, ref request, out var handled, out _));
            Assert.False(handled); // A description alone never certifies a payload run.
        }
        var metadata = new Request { Node = node, Position = 4 };
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref metadata, TrstOperation.SetProtection, disk.BatchBuffer, out _));
        request.Position = (uint)expected.Length;
        Assert.False(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out _, cursor));
        Assert.Equal(DOS.Error.WriteProtected, work.Error);
        Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
        Assert.Equal(expected.Length, AmigaSmartFileSystemReadOnlyCore.Read(ref disk, node, 0, disk.Payload,
            (uint)expected.Length, work.Meta, work.Data, out _));
        Assert.Equal(expected, disk.Ram.AsSpan((int)disk.Payload.Raw, expected.Length).ToArray());
        Inspect(disk.Snapshot());
    }

    [Fact]
    public void ZeroLengthWriteCannotCertifyAnArchiveClearDescription()
    {
        var (image, node) = Seed(); var disk = Mount(image, true); var work = WorkFor(disk);
        var cursor = APTR.FromPointer(disk.Payload.Raw + 800000);
        var request = new Request { Node = node, Position = 16 };
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.SetProtection, disk.BatchBuffer, out _));
        request = new Request { Node = node, Buffer = disk.Payload };
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out var actual, cursor));
        Assert.Equal(0u, actual);
        request.Length = 512; disk.Ram.AsSpan((int)disk.Payload.Raw, 512).Fill(0xD4);
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out _, cursor));
        Assert.True(AmigaSmartFileSystemReadOnlyCore.Describe(ref disk, node, work.Meta, out var entry));
        Assert.Equal(0u, entry.Protection);
        Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
        Inspect(disk.Snapshot());
    }

    [Fact]
    public void RenameExpiresTheObjectLocationBeforeFollowingGrowth()
    {
        var (image, node) = Seed(); var disk = Mount(image, true); var work = WorkFor(disk);
        var cursor = APTR.FromPointer(disk.Payload.Raw + 800000);
        disk.Ram.AsSpan((int)disk.Payload.Raw, 17).Fill(0xD4);
        var request = new Request { Node = node, Buffer = disk.Payload, Length = 17 };
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out _, cursor));
        var name = new string('r', 80);
        System.Text.Encoding.ASCII.GetBytes(name).CopyTo(disk.Ram, (int)disk.NameBuffer.Raw);
        request = new Request { Node = node, Directory = 1, Name = disk.NameBuffer, Length = 80 };
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Rename, disk.BatchBuffer, out _));
        Assert.True(AmigaSmartFileSystemReadOnlyCore.Describe(ref disk, node, work.Meta, out var entry));
        request = new Request { Node = node, Position = entry.Size, Buffer = disk.Payload, Length = 17 };
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out var actual, cursor));
        Assert.Equal(17u, actual);
        Assert.True(AmigaSmartFileSystemReadOnlyCore.Describe(ref disk, node, work.Meta, out var grown));
        Assert.Equal(entry.Size + 17, grown.Size);
        Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
        Assert.Contains(name, Manifest(disk.Snapshot()));
        Inspect(disk.Snapshot());
    }

    [Fact]
    public void ConsecutiveAppendEditsTheCorrectLeafAfterExtentTreeSplits()
    {
        var (image, node) = Seed(); var disk = Mount(image, true); var work = WorkFor(disk);
        var request = new Request { Node = node };
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Truncate, disk.BatchBuffer, out _));
        Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
        System.Text.Encoding.ASCII.GetBytes("Blocker").CopyTo(disk.Ram, (int)disk.NameBuffer.Raw);
        request = new Request { Directory = 1, Name = disk.NameBuffer, Length = 7 };
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.CreateFile, disk.BatchBuffer, out _));
        var blocker = request.Node; var cursor = APTR.FromPointer(disk.Payload.Raw + 800000);
        for (var i = 0; i < 40; i++)
        {
            disk.Ram.AsSpan((int)disk.Payload.Raw, 512).Fill(0xA7);
            request = new Request { Node = node, Position = (uint)i * 512, Buffer = disk.Payload, Length = 512 };
            Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out _, cursor));
            disk.Ram.AsSpan((int)disk.Payload.Raw, 512).Fill(0xB5);
            request = new Request { Node = blocker, Position = (uint)i * 512, Buffer = disk.Payload, Length = 512 };
            Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out _));
        }
        disk.Ram.AsSpan((int)disk.Payload.Raw, 1024).Fill(0xA7);
        request = new Request { Node = node, Position = 40 * 512, Buffer = disk.Payload, Length = 512 };
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out _, cursor));
        request.Position += 512; request.Length = 1024;
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out var actual, cursor));
        Assert.Equal(1024u, actual);
        Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
        Assert.Equal(43 * 512, AmigaSmartFileSystemReadOnlyCore.Read(ref disk, node, 0, disk.Payload,
            43 * 512, work.Meta, work.Data, out _));
        Assert.All(disk.Ram.AsSpan((int)disk.Payload.Raw, 43 * 512).ToArray(), b => Assert.Equal(0xA7, b));
        Inspect(disk.Snapshot());
    }
}
