using Amiga;
using CopperSFS.Bench;
using static CopperSFS.AmigaSmartFileSystem2WriteCore;
using static CopperSFS.Tests.Sfs2WriteTests;

namespace CopperSFS.Tests;

public sealed class Sfs2OwnedStateTests
{
    [Fact]
    public void InvalidatedCacheSlotsAreReusedWithoutRevivingEarlierHandles()
    {
        var disk = Mounted(); var work = WorkFor(disk);
        var root = disk.ReadUInt32(Sfs2MountContext.RootImage(disk.Sfs2Context), 104);
        var cache = Sfs2MountContext.Cache(ref disk, disk.Sfs2Context);
        Assert.True(Sfs2MetadataStore.Acquire(ref disk, root, 0x4F424A43, work.Meta, out var pin));
        var stale = pin; Assert.True(Sfs2MetadataStore.Release(ref disk, ref pin));
        var occupied = disk.ReadUInt32(cache);
        for (var i = 0; i < 50; i++)
        {
            Assert.True(Sfs2MetadataStore.Invalidate(ref disk, cache, root, 1));
            Assert.True(Sfs2MetadataStore.Acquire(ref disk, root, 0x4F424A43, work.Meta, out pin));
            Assert.False(Sfs2MetadataStore.Current(ref disk, stale));
            Assert.False(Sfs2MetadataStore.Release(ref disk, ref stale));
            Assert.Equal(occupied, disk.ReadUInt32(cache));
            stale = pin; Assert.True(Sfs2MetadataStore.Release(ref disk, ref pin));
        }
    }

    [Fact]
    public void GenericEditableViewPreservesPinsAndCannotHideChangesFromPredecessorClone()
    {
        var disk = Mounted(); var work = WorkFor(disk);
        var root = disk.ReadUInt32(Sfs2MountContext.RootImage(disk.Sfs2Context), 104);
        var source = disk.BatchBuffer; const uint budget = 32752;
        var destination = APTR.FromPointer(source.Raw + budget);
        Assert.True(disk.ReadBlock(root, work.Meta));
        Assert.True(Sfs2MetadataStore.Initialize(ref disk, source, budget, 512, 1));
        Assert.True(Sfs2MetadataStore.Store(ref disk, source, root, work.Meta, 0x4F424A43));
        Assert.True(Sfs2MetadataStore.Clone(ref disk, source, destination, budget, 2));
        disk.WriteUInt32(source, 24, 0); disk.WriteUInt32(destination, 24, 0);
        Assert.True(Sfs2MetadataStore.Acquire(ref disk, source, root, 0x4F424A43, out var pin));
        Assert.True(AmigaSmartFileSystemRedoArena.MetadataView(ref disk, source, root, 0x4F424A43, true, out var found).IsNull);
        Assert.True(found); Assert.True(Sfs2MetadataStore.Release(ref disk, ref pin));
        var image = AmigaSmartFileSystemRedoArena.MetadataView(ref disk, source, root, 0x4F424A43, true, out found);
        Assert.True(found); Assert.True(image.IsNotNull);
        disk.WriteUInt32(image, 36, 0x10203040);
        Seal(disk.Ram.AsSpan((int)image.Raw, 512));
        Assert.True(Sfs2MetadataStore.Clone(ref disk, source, destination, budget, 3, true));
        var copied = Sfs2MetadataStore.Find(ref disk, destination, root);
        Assert.Equal(0x10203040u, disk.ReadUInt32(copied, 36));
        Assert.True(AmigaFileSystemBlockIo.ValidSfs2Sum(ref disk, copied, 512));
    }

    [Fact]
    public void FullBlockRunOverwriteKeepsPrivateImagesAcrossFragmentedExtents()
    {
        var (image, node) = Seed();
        var seed = new StudyDisk(image, 512);
        PutFile(ref seed, node, 1027, StudyFixture.Pattern(8192, 53), true);
        image = seed.Snapshot();
        var payload = StudyFixture.Pattern(4096, 91);
        string Run(bool owned)
        {
            var disk = new StudyDisk(image, 512); var work = WorkFor(disk);
            if (owned)
            {
                Assert.True(Sfs2MountContext.Mount(ref disk, ref work, APTR.FromPointer(disk.Payload.Raw + 900000), 16508));
                Assert.True(InitializeTrstGroup(ref disk, disk.BatchBuffer, 65536));
            }
            var source = APTR.FromPointer(disk.Payload.Raw + 1);
            payload.CopyTo(disk.Ram, (int)source.Raw);
            var request = new Request { Node = node, Position = 512, Buffer = source, Length = 4096 };
            uint actual;
            Assert.True(owned ? ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out actual) :
                ExecuteTrst(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, 65536, out actual));
            Assert.Equal(4096u, actual);
            if (owned)
            {
                Assert.Equal(image, disk.Snapshot()); // Nothing reached the physical device.
                disk.Ram.AsSpan((int)source.Raw, 4096).Fill(0xA7);
                Assert.Equal(4096, AmigaSmartFileSystemReadOnlyCore.Read(ref disk, node, 512,
                    disk.Payload, 4096, work.Meta, work.Data, out _));
                Assert.Equal(payload, disk.Ram.AsSpan((int)disk.Payload.Raw, 4096).ToArray());
                Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
            }
            return Manifest(disk.Snapshot());
        }
        Assert.Equal(Run(false), Run(true));
    }

    private static StudyDisk Mounted(uint capacity = 16384)
    {
        var (image, _) = Seed(); var disk = new StudyDisk(image, 512); var work = WorkFor(disk);
        Assert.True(Sfs2MountContext.Mount(ref disk, ref work, APTR.FromPointer(disk.Payload.Raw + 900000), capacity));
        return disk;
    }

    [Fact]
    public void WarmTraversalDoesNotReadRootsOrMarkerAgain()
    {
        var disk = Mounted(); var work = WorkFor(disk);
        Assert.True(AmigaSmartFileSystemReadOnlyCore.DescribeInformation(ref disk, 1, work.Meta, out _, out _, out _));
        var reads = disk.Counts.BlockReads;
        disk.FailAtRead = (int)reads + 1;
        for (var i = 0; i < 20; i++)
            Assert.True(AmigaSmartFileSystemReadOnlyCore.DescribeInformation(ref disk, 1, work.Meta, out _, out _, out _));
        Assert.Equal(reads, disk.Counts.BlockReads);
    }

    [Fact]
    public void PinnedCacheImageSurvivesPressureAndCannotBeInvalidated()
    {
        // Exactly one cache image forces scratch fallback while that image is pinned.
        var disk = Mounted(1152); var work = WorkFor(disk);
        var root = disk.ReadUInt32(Sfs2MountContext.RootImage(disk.Sfs2Context), 104);
        Assert.True(Sfs2MetadataStore.Acquire(ref disk, root, 0x4F424A43, work.Meta, out var pinned));
        var original = disk.Ram.AsSpan((int)pinned.Image.Raw, 512).ToArray();
        var cache = Sfs2MountContext.Cache(ref disk, disk.Sfs2Context);
        Assert.False(Sfs2MetadataStore.Invalidate(ref disk, cache, root, 1));
        Assert.True(Sfs2MetadataStore.Acquire(ref disk, root + 2, 0x54524F4B, work.Data, out var fallback));
        Assert.Equal(work.Data, fallback.Image); Assert.True(fallback.Arena.IsNull);
        Assert.Equal(original, disk.Ram.AsSpan((int)pinned.Image.Raw, 512).ToArray());
        Assert.True(Sfs2MetadataStore.Release(ref disk, ref fallback));
        var stale = pinned; Assert.True(Sfs2MetadataStore.Release(ref disk, ref pinned));
        Assert.True(Sfs2MetadataStore.Invalidate(ref disk, cache, root, 1));
        Assert.False(Sfs2MetadataStore.Current(ref disk, stale));
        Assert.True(Sfs2MetadataStore.Acquire(ref disk, root + 2, 0x54524F4B, work.Data, out var replacement));
        Assert.False(Sfs2MetadataStore.Release(ref disk, ref stale));
        Assert.True(Sfs2MetadataStore.Release(ref disk, ref replacement));
    }

    [Fact]
    public void RemountAndSuspensionInvalidateAllBorrowedHandles()
    {
        var disk = Mounted(); var context = disk.Sfs2Context; var work = WorkFor(disk);
        var root = disk.ReadUInt32(Sfs2MountContext.RootImage(context), 104);
        Assert.True(Sfs2MetadataStore.Acquire(ref disk, root, 0x4F424A43, work.Meta, out var handle));
        var epoch = disk.ReadUInt32(context, 8);
        Sfs2MountContext.Suspend(ref disk, context);
        Assert.False(Sfs2MetadataStore.Current(ref disk, handle));
        Assert.False(AmigaSmartFileSystemReadOnlyCore.DescribeInformation(ref disk, 1, work.Meta, out _, out _, out _));
        Assert.True(Sfs2MountContext.Mount(ref disk, ref work, context, 16384));
        Assert.NotEqual(epoch, disk.ReadUInt32(context, 8));
        Assert.False(Sfs2MetadataStore.Release(ref disk, ref handle));
    }

    [Fact]
    public void PrivateEditDoesNotChangeAcceptedImageAndRawReadersRefuseItUntilPublish()
    {
        var disk = Mounted(); var context = disk.Sfs2Context; var work = WorkFor(disk);
        var root = disk.ReadUInt32(Sfs2MountContext.RootImage(context), 104);
        var arena = disk.BatchBuffer;
        Assert.True(Sfs2MetadataStore.Initialize(ref disk, arena, 16384, 512, 1));
        disk.WriteUInt32(arena, 24, Sfs2MetadataStore.Trial);
        disk.WriteUInt32(context, 40, arena.Raw); disk.RedoOverlay = arena;
        Assert.True(Sfs2MetadataStore.Edit(ref disk, root, 0x4F424A43, work.Meta, out var edit));
        disk.WriteUInt32(edit.Image, 36, 0x10203040);
        Assert.False(Sfs2MetadataStore.Publish(ref disk, arena));
        Assert.False(Sfs2MetadataStore.Acquire(ref disk, root, 0x4F424A43, work.Data, out _));
        Assert.True(Sfs2MetadataStore.ReleaseEdit(ref disk, ref edit));
        Assert.False(disk.ReadBlock(root, work.Data));
        Assert.True(Sfs2MetadataStore.Publish(ref disk, arena));
        Assert.True(disk.ReadBlock(root, work.Data));
        Assert.True(AmigaFileSystemBlockIo.ValidSfs2Sum(ref disk, work.Data, 512));
        disk.RedoOverlay = APTR.Null; disk.WriteUInt32(context, 40, 0);
        var clean = Sfs2MetadataStore.ReadView(ref disk, root, 0x4F424A43, work.Meta);
        Assert.NotEqual(0x10203040u, disk.ReadUInt32(clean, 36));
        Assert.Equal(0, disk.Counts.BlockWrites);
    }

    [Fact]
    public void HashCollisionsAndEvictionKeepEveryRemainingKeyReachable()
    {
        var disk = Mounted(); var arena = disk.BatchBuffer;
        Assert.True(Sfs2MetadataStore.Initialize(ref disk, arena, 16384, 512, 1));
        var source = disk.Payload; disk.Clear(source, 512);
        var spacing = disk.ReadUInt32(arena, 16) + 1;
        var capacity = disk.ReadUInt32(arena, 4);
        for (var i = 0u; i < capacity; i++)
        { disk.WriteUInt32(source, 0, i + 1); Assert.True(Sfs2MetadataStore.Store(ref disk, arena, i * spacing, source)); }
        Assert.False(Sfs2MetadataStore.Store(ref disk, arena, capacity * spacing, source));
        for (var i = 0u; i < capacity; i += 2) Assert.True(Sfs2MetadataStore.Invalidate(ref disk, arena, i * spacing, 1));
        for (var i = 0u; i < capacity; i++)
        {
            var image = Sfs2MetadataStore.Find(ref disk, arena, i * spacing);
            if ((i & 1) == 0) Assert.True(image.IsNull);
            else Assert.Equal(i + 1, disk.ReadUInt32(image));
        }
    }

    [Fact]
    public void RevisionWrapRefusesEditAndGenerationWrapChangesMountEpoch()
    {
        var disk = Mounted(); var work = WorkFor(disk); var context = disk.Sfs2Context;
        var root = disk.ReadUInt32(Sfs2MountContext.RootImage(context), 104);
        Assert.True(Sfs2MetadataStore.Acquire(ref disk, root, 0x4F424A43, work.Meta, out var handle));
        var slot = handle.Slot; Assert.True(Sfs2MetadataStore.Release(ref disk, ref handle));
        disk.WriteUInt32(slot, 8, uint.MaxValue);
        Assert.False(Sfs2MetadataStore.Store(ref disk, Sfs2MountContext.Cache(ref disk, context), root, work.Meta));
        var oldEpoch = disk.ReadUInt32(context, 8); disk.WriteUInt32(context, 12, uint.MaxValue);
        Assert.True(InitializeTrstGroup(ref disk, disk.BatchBuffer, 65536));
        var request = new Request { Node = 1, Position = 0 };
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.SetProtection, disk.BatchBuffer, out _));
        Assert.NotEqual(oldEpoch, disk.ReadUInt32(context, 8)); Assert.Equal(1u, disk.ReadUInt32(context, 12));
    }

    [Fact]
    public void OverlappingOrUnmappedArenasCannotBeCloned()
    {
        var disk = Mounted(); var arena = disk.BatchBuffer;
        Assert.True(Sfs2MetadataStore.Initialize(ref disk, arena, 16384, 512, 1));
        Assert.False(Sfs2MetadataStore.Clone(ref disk, arena, APTR.FromPointer(arena.Raw + 4), 16384, 2));
        Assert.False(Sfs2MetadataStore.Clone(ref disk, arena, APTR.FromPointer(uint.MaxValue - 100), 16384, 2));
        Assert.False(Sfs2MetadataStore.Clone(ref disk, arena, disk.Payload, 0, 2));
        Assert.False(Sfs2MetadataStore.Clone(ref disk, arena, disk.Payload, 31, 2));
        Assert.False(Sfs2MetadataStore.Initialize(ref disk, arena, 64, 512, 1));
    }

    [Fact]
    public void ContextCannotOverlapRecoveryScratch()
    {
        var (image, _) = Seed(); var disk = new StudyDisk(image, 512); var work = WorkFor(disk);
        foreach (var scratch in new[] { work.Meta, work.Data, work.Bitmap, work.Journal })
            Assert.False(Sfs2MountContext.Mount(ref disk, ref work, scratch, 16384));
        Assert.Equal(0, disk.Counts.BlockWrites); Assert.Equal(image, disk.Snapshot());
    }

    [Fact]
    public void EpochExhaustionRejectsPacketWithoutPublishingTrial()
    {
        var disk = Mounted(); var work = WorkFor(disk); var context = disk.Sfs2Context;
        Assert.True(InitializeTrstGroup(ref disk, disk.BatchBuffer, 65536));
        disk.WriteUInt32(context, 8, uint.MaxValue); disk.WriteUInt32(context, 12, uint.MaxValue);
        var root = disk.Ram.AsSpan((int)Sfs2MountContext.RootImage(context).Raw, 512).ToArray();
        var request = new Request { Node = 1, Position = 7 };
        Assert.False(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.SetProtection, disk.BatchBuffer, out _));
        Assert.True(disk.RedoOverlay.IsNull); Assert.True(Sfs2MountContext.Durable(ref disk, context));
        Assert.Equal(root, disk.Ram.AsSpan((int)Sfs2MountContext.RootImage(context).Raw, 512).ToArray());
        Assert.Equal(0, disk.Counts.BlockWrites);
    }

    [Fact]
    public void DeviceFlushCannotCertifyAnAcceptedFilesystemTransaction()
    {
        var disk = Mounted(); var work = WorkFor(disk); var context = disk.Sfs2Context;
        Assert.True(InitializeTrstGroup(ref disk, disk.BatchBuffer, 65536));
        var request = new Request { Node = 1, Position = 7 };
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.SetProtection, disk.BatchBuffer, out _));
        Assert.True(disk.Flush()); Assert.False(Sfs2MountContext.Durable(ref disk, context));
        // Even detaching an overlay cannot turn pending acceptance into durable completion.
        var overlay = disk.ReadUInt32(context, 40); disk.WriteUInt32(context, 40, 0);
        Assert.False(Sfs2MountContext.Durable(ref disk, context)); disk.WriteUInt32(context, 40, overlay);
        Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
        Assert.True(Sfs2MountContext.Durable(ref disk, context));
    }

    [Theory] [InlineData(512)] [InlineData(1024)] [InlineData(2048)] [InlineData(4096)]
    public void EveryMemoryBudgetMatchesTheStrictTransactionReference(int bytes)
    {
        var (image, node) = Seed(bytes);
        foreach (var scenario in new[] { "create", "mkdir", "overwrite", "append", "appendrun", "grow", "shrink", "truncate", "rename", "remove", "comment", "protect", "date" })
        {
            var reference = new StudyDisk(image, bytes); Assert.True(Mutate(ref reference, node, scenario, true));
            var expected = Manifest(reference.Snapshot(), bytes);
            foreach (var extra in new uint[] { 0, 16 * 1024, 64 * 1024, 256 * 1024 })
            {
                var disk = new StudyDisk(image, bytes); var work = WorkFor(disk);
                var budget = AmigaSmartFileSystemTransactionBuffer.StorageBytes((uint)bytes, 32) - 12 + extra;
                Assert.True(Sfs2MountContext.Mount(ref disk, ref work, APTR.FromPointer(disk.Payload.Raw + 512000), budget));
                Assert.True(InitializeTrstGroup(ref disk, disk.BatchBuffer, 65536));
                Assert.True(Mutate(ref disk, node, scenario, true));
                Assert.Equal(expected, Manifest(disk.Snapshot(), bytes));
                Assert.True(AmigaSmartFileSystemReadOnlyCore.CountUsed(ref disk, work.Meta, out var used));
                Assert.Equal((uint)(image.Length / bytes - 2) - Inspect(disk.Snapshot(), bytes).FreeBlocks, used);
            }
        }
    }

    [Theory] [InlineData(0)] [InlineData(256)]
    public void GroupedNodeTreeGrowthAndRecordRelocationMatchStrictReference(int extraKiB)
    {
        string Run(bool owned)
        {
            var disk = new StudyDisk(Empty(), 512); var work = WorkFor(disk);
            if (owned)
            {
                Assert.True(Sfs2MountContext.Mount(ref disk, ref work, APTR.FromPointer(disk.Payload.Raw + 512000),
                    16508u + (uint)extraKiB * 1024));
                Assert.True(InitializeTrstGroup(ref disk, disk.BatchBuffer, 65536));
            }
            var packets = 0;
            Request Packet(Request request, TrstOperation op)
            {
                var ok = owned ? ExecuteTrstGroup(ref disk, ref work, ref request, op, disk.BatchBuffer, out _) :
                    ExecuteTrst(ref disk, ref work, ref request, op, disk.BatchBuffer, 65536, out _);
                Assert.True(ok, work.Error.ToString());
                if (owned && ++packets % 8 == 0) Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
                return request;
            }
            var files = new List<uint>();
            for (var i = 0; i < 80; i++)
            {
                var name = System.Text.Encoding.ASCII.GetBytes($"Child{i:D4}");
                name.CopyTo(disk.Ram, (int)disk.NameBuffer.Raw);
                files.Add(Packet(new Request { Directory = 1, Name = disk.NameBuffer, Length = (uint)name.Length }, TrstOperation.CreateFile).Node);
            }
            var payload = StudyFixture.Pattern(1301); payload.CopyTo(disk.Ram, (int)disk.Payload.Raw);
            Packet(new Request { Node = files[0], Buffer = disk.Payload, Length = 1301 }, TrstOperation.Write);
            System.Text.Encoding.ASCII.GetBytes(new string('c', 79)).CopyTo(disk.Ram, (int)disk.Payload.Raw);
            Packet(new Request { Node = files[0], Buffer = disk.Payload, Length = 79 }, TrstOperation.SetComment);
            System.Text.Encoding.ASCII.GetBytes(new string('n', 107)).CopyTo(disk.Ram, (int)disk.NameBuffer.Raw);
            Packet(new Request { Node = files[0], Directory = 1, Name = disk.NameBuffer, Length = 107 }, TrstOperation.Rename);
            if (owned) Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
            var grown = Manifest(disk.Snapshot());
            Assert.True(U32(disk.Snapshot().AsSpan(6 * 512), 16) > 1); // Root became a node-tree branch.
            foreach (var node in files.Skip(1)) Packet(new Request { Node = node }, TrstOperation.Remove);
            if (owned) Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
            return grown + Manifest(disk.Snapshot());
        }
        Assert.Equal(Run(false), Run(true));
    }

    [Theory]
    [InlineData("create")] [InlineData("mkdir")] [InlineData("overwrite")] [InlineData("appendrun")]
    [InlineData("grow")] [InlineData("shrink")] [InlineData("truncate")] [InlineData("rename")]
    [InlineData("remove")] [InlineData("comment")] [InlineData("protect")] [InlineData("date")]
    public void EveryMutationCommitCutRecoversOldOrAcceptedView(string scenario)
    {
        var (image, node) = Seed(); var before = Manifest(image);
        StudyDisk Start()
        {
            var disk = new StudyDisk(image, 512, crashModel: true); var work = WorkFor(disk);
            Assert.True(Sfs2MountContext.Mount(ref disk, ref work, APTR.FromPointer(disk.Payload.Raw + 900000), 16384));
            Assert.True(InitializeTrstGroup(ref disk, disk.BatchBuffer, 65536)); return disk;
        }
        var baseline = Start(); Assert.True(Mutate(ref baseline, node, scenario, true));
        var after = Manifest(baseline.Snapshot());
        for (var cut = 1; cut <= baseline.Events; cut++)
        {
            var disk = Start(); disk.CutAfterEvent = cut;
            Assert.Throws<StudyPowerCut>(() => Mutate(ref disk, node, scenario, true));
            foreach (var saved in disk.CrashImages())
            {
                var recovered = new StudyDisk(saved, 512); var work = WorkFor(recovered);
                Assert.True(Recover(ref recovered, ref work));
                Assert.Contains(Manifest(recovered.Snapshot()), new[] { before, after });
            }
        }
    }

    [Theory] [InlineData("truncate")] [InlineData("shrink")]
    public void WordBatchedUnallocationCrossesBitmapPagesAndPreservesNeighbours(string operation)
    {
        var seed = new StudyDisk(Empty(capacity: 4 * 1024 * 1024), 512);
        var node = CreateFile(ref seed, "Data"); var payload = StudyFixture.Pattern(65536, 198);
        const int length = 4033 * 512;
        for (var at = 0; at < length; at += payload.Length)
            PutFile(ref seed, node, (uint)at, payload[..Math.Min(payload.Length, length - at)], true);
        var keep = CreateFile(ref seed, "Keep"); PutFile(ref seed, keep, 0, StudyFixture.Pattern(513, 99), true);
        var image = seed.Snapshot(); var reference = new StudyDisk(image, 512);
        Assert.True(Mutate(ref reference, node, operation, true));
        var disk = new StudyDisk(image, 512); var work = WorkFor(disk);
        Assert.True(Sfs2MountContext.Mount(ref disk, ref work, APTR.FromPointer(disk.Payload.Raw + 900000), 16508));
        Assert.True(InitializeTrstGroup(ref disk, disk.BatchBuffer, 65536));
        disk.FreedQueue = APTR.FromPointer(disk.Payload.Raw + 800000);
        Assert.True(Mutate(ref disk, node, operation, true));
        Assert.Equal(4, disk.Counts.Flushes);
        Assert.Equal(Manifest(reference.Snapshot()), Manifest(disk.Snapshot()));
        Assert.InRange(AmigaFreedRangeQueue.Count(ref disk, disk.FreedQueue), 1u, 4u);
        uint reclaimed = 0;
        for (var i = 0u; i < AmigaFreedRangeQueue.Count(ref disk, disk.FreedQueue); i++)
            reclaimed += disk.ReadUInt32(disk.FreedQueue, 8 + (int)i * 8);
        Assert.Equal(operation == "truncate" ? 4033u : 4031u, reclaimed);
    }

    [Fact]
    public void SingleBlockUnpublishedGrowthProtectsAcceptedOverwriteAndEveryCommitCut()
    {
        var seed = new StudyDisk(Empty(), 512); var node = CreateFile(ref seed, "Data");
        var image = seed.Snapshot(); var before = Manifest(image);
        StudyDisk Start()
        {
            var disk = new StudyDisk(image, 512, crashModel: true); var work = WorkFor(disk);
            Assert.True(Sfs2MountContext.Mount(ref disk, ref work, APTR.FromPointer(disk.Payload.Raw + 900000), 16508));
            Assert.True(InitializeTrstGroup(ref disk, disk.BatchBuffer, 65536)); return disk;
        }
        void Run(ref StudyDisk disk)
        {
            var work = WorkFor(disk); disk.Ram.AsSpan((int)disk.Payload.Raw, 37).Fill(97);
            var request = new Request { Node = node, Buffer = disk.Payload, Length = 37 };
            Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out _));
            Assert.Equal(1, disk.Counts.BlockWrites); Assert.Equal(0, disk.Counts.Flushes);
            var cursor = new AmigaSmartFileSystemReadOnlyCore.ReadCursor();
            Assert.Equal(1, AmigaSmartFileSystemReadOnlyCore.Read(ref disk, ref cursor, node, 0, disk.Payload, 1,
                work.Meta, work.Data, out _));
            Assert.True(AmigaSmartFileSystemRedoArena.TryFind(ref disk, disk.RedoOverlay, cursor.First, out var staged));
            Assert.True(staged.IsNull); // Unreachable data needs no redo copy.
            disk.Ram.AsSpan((int)disk.Payload.Raw, 32768).Fill(99);
            request.Length = 32768; request.Position = 37;
            Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out _));
            var physicalWrites = disk.Counts.BlockWrites;
            disk.Ram.AsSpan((int)disk.Payload.Raw, 32768).Fill(23);
            request.Position = 0;
            Assert.False(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out _));
            Assert.Equal(1, AmigaSmartFileSystemReadOnlyCore.Read(ref disk, node, 0, disk.Payload, 1, work.Meta, work.Data, out _));
            Assert.Equal(97, disk.Ram[(int)disk.Payload.Raw]);
            disk.Ram[(int)disk.Payload.Raw] = 98; request.Length = 1;
            Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out _));
            Assert.Equal(physicalWrites, disk.Counts.BlockWrites); // Accepted data overwrite stays private.
            Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
        }
        var baseline = Start(); Run(ref baseline); var after = Manifest(baseline.Snapshot());
        for (var cut = 1; cut <= baseline.Events; cut++)
        {
            var disk = Start(); disk.CutAfterEvent = cut;
            Assert.Throws<StudyPowerCut>(() => Run(ref disk));
            // Exhaustive small pending sets; ten deterministic subsets for
            // large unpublished payloads. Every I/O/barrier cut is still run.
            foreach (var saved in disk.CrashSpaceFits(128) ? disk.CrashImages() : disk.SampledCrashImages())
            {
                var recovered = new StudyDisk(saved, 512); var work = WorkFor(recovered);
                Assert.True(Recover(ref recovered, ref work));
                Assert.Contains(Manifest(recovered.Snapshot()), new[] { before, after });
            }
        }
    }
}
