using Amiga;
using System.Buffers.Binary;
namespace CopperSFS.Tests;
public sealed partial class AmigaSmartFileSystemTests
{
    [Fact]
    public void OverlappingSuccessorCannotCertifyTwoPhysicalSourceRuns()
    {
        // The checksum and bitmap are consistent, but four live blocks
        // belong to both extent records.
        var seed = ContinuationSeed(5994, nextExtent: true, length: 42);
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true };
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeSharedTrstGroup(ref disk, GroupAddress, 9756));
        var payload = Enumerable.Repeat((byte)0x5B, 1024).ToArray();
        var accepted = 0;
        while (accepted < 10 * 512 && GroupWrite(ref disk, 3, (uint)accepted, payload))
            accepted += payload.Length;
        Assert.InRange(accepted, 0, 4 * 512);
        var cut = new Disk(disk.Snapshot());
        Assert.Equal(seed.Bytes, GroupRead(ref cut, 3));
        Assert.Equal("hello"u8.ToArray(), GroupRead(ref cut, 4));
    }
    [Fact]
    public void FailedPacketCrossingTwoSourcesCanRetryBeforeExplicitFlush()
    {
        var seed = ContinuationSeed(5000, nextExtent: true, length: 42);
        var expected = seed.Bytes.ToArray();
        var disk = StageTrstProof(seed.Image, expected, 1024, shared: true);
        disk.FreedQueue = APTR.FromPointer((uint)disk.Ram.Length - 4096);
        var work = TrstWork();
        foreach (int offset in new[] { 3072, 4096 })
        {
            int length = offset == 3072 ? 1024 : 2048;
            var payload = Enumerable.Repeat((byte)(offset / 512), length).ToArray();
            payload.CopyTo(disk.Ram, 4000);
            var request = new AmigaSmartFileSystemWriteCore.Request { Node = 3, Position = (uint)offset,
                Buffer = APTR.FromPointer(4000), Length = (uint)length };
            if (offset == 4096)
            {
                disk.FailAtWrite = disk.Writes + 2;
                Assert.False(AmigaSmartFileSystemWriteCore.TryContinueTrstGroup(ref disk, GroupAddress,
                    work.Meta, APTR.Null, ref request, out var accepted, out _, true));
                Assert.False(accepted);
                Assert.Equal(1u, AmigaFreedRangeQueue.Count(ref disk, disk.FreedQueue));
                disk.FailAtWrite = 0;
            }
            Assert.True(AmigaSmartFileSystemWriteCore.TryContinueTrstGroup(ref disk, GroupAddress,
                work.Meta, APTR.Null, ref request, out var handled, out _, true));
            Assert.True(handled);
            payload.CopyTo(expected, offset);
        }
        Assert.True(AmigaFreedRangeQueue.Take(ref disk, disk.FreedQueue, 128, 0, out var start, out var count));
        Assert.Equal((5006u, 4u), (start, count));
        Assert.True(AmigaFreedRangeQueue.Take(ref disk, disk.FreedQueue, 128, 0, out start, out count));
        Assert.Equal((6000u, 2u), (start, count));
        var cut = new Disk(disk.Snapshot());
        Assert.Equal(seed.Bytes, GroupRead(ref cut, 3));
        Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot());
        Assert.Equal(expected, GroupRead(ref remount, 3));
        Assert.Equal("hello"u8.ToArray(), GroupRead(ref remount, 4));
    }
    [Theory]
    [InlineData(false, 1000)] [InlineData(true, 2000)] [InlineData(true, 5000)]
    public void WholeExtentContinuationRepairsSuccessorBeforeStartingItsNewProof(bool deferred, int first)
    {
        const int packet = 1024;
        var seed = ContinuationSeed(first, nextExtent: true, length: 42);
        var expected = seed.Bytes.ToArray();
        var disk = StageTrstProof(seed.Image, expected, packet, shared: true);
        if (first == 5000) disk.FreedQueue = APTR.FromPointer((uint)disk.Ram.Length - 4096);
        var arena = disk.RedoOverlay;
        var payload = Enumerable.Repeat((byte)0xDB, packet).ToArray();
        for (var at = 3 * packet; at < expected.Length; at += packet)
        {
            if (at < 5 * packet || first == 5000)
            {
                var request = new AmigaSmartFileSystemWriteCore.Request { Node = 3, Position = (uint)at,
                    Buffer = APTR.FromPointer(4000), Length = packet };
                payload.CopyTo(disk.Ram, 4000);
                Assert.True(AmigaSmartFileSystemWriteCore.TryContinueTrstGroup(ref disk, GroupAddress,
                    TrstWork().Meta, APTR.Null, ref request, out var handled, out _, deferred));
                Assert.True(handled);
                Assert.Equal(arena, disk.RedoOverlay);
            }
            else Assert.True(GroupWrite(ref disk, 3, (uint)at, payload));
            payload.CopyTo(expected, at);
        }
        if (first == 5000)
        {
            // Tracking starts after the three staged two-sector packets.
            Assert.Equal(2u, AmigaFreedRangeQueue.Count(ref disk, disk.FreedQueue));
            Assert.True(AmigaFreedRangeQueue.Take(ref disk, disk.FreedQueue, 128, 0, out var start, out var count));
            Assert.Equal((5006u, 4u), (start, count));
            Assert.True(AmigaFreedRangeQueue.Take(ref disk, disk.FreedQueue, 128, 0, out start, out count));
            Assert.Equal((6000u, 32u), (start, count));
        }
        var durable = new Disk(disk.Snapshot());
        Assert.Equal(seed.Bytes, GroupRead(ref durable, 3));
        var work = TrstWork();
        Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot());
        Assert.Equal(expected, GroupRead(ref remount, 3));
        Assert.Equal("hello"u8.ToArray(), GroupRead(ref remount, 4));
        Assert.True(AmigaSmartFileSystemCore.CountUsed(ref remount, work.Meta, out var used));
        Assert.Equal((uint)seed.Used.Count - 2, used);
    }

    [Theory]
    [InlineData(false, false, 2000)] [InlineData(true, false, 2000)]
    [InlineData(false, true, 2000)] [InlineData(true, true, 2000)]
    [InlineData(false, false, 1000)] [InlineData(true, true, 1000)]
    public void SharedPoolCanFinishLastExtentWithoutReplacingAcceptedArena(bool deferred, bool failLast, int first)
    {
        const int packet = 1024;
        var seed = ContinuationSeed(first, length: 10);
        var expected = seed.Bytes.ToArray();
        var disk = StageTrstProof(seed.Image, expected, packet, shared: true);
        var arena = disk.RedoOverlay;
        var payload = Enumerable.Repeat((byte)0xDA, packet).ToArray();
        for (var at = 3 * packet; at < expected.Length; at += packet)
        {
            var r = new AmigaSmartFileSystemWriteCore.Request { Node = 3, Position = (uint)at,
                Buffer = APTR.FromPointer(4000), Length = packet };
            payload.CopyTo(disk.Ram, 4000);
            if (failLast && at + packet == expected.Length)
            {
                disk.FailAtWrite = disk.Writes + 2;
                Assert.False(AmigaSmartFileSystemWriteCore.TryContinueTrstGroup(ref disk, GroupAddress,
                    TrstWork().Meta, APTR.Null, ref r, out var accepted, out _, deferred));
                Assert.False(accepted);
                disk.FailAtWrite = 0;
                Assert.Equal(expected, DeferredRead(ref disk, expected.Length));
                payload.CopyTo(disk.Ram, 4000);
            }
            Assert.True(AmigaSmartFileSystemWriteCore.TryContinueTrstGroup(ref disk, GroupAddress,
                TrstWork().Meta, APTR.Null, ref r, out var handled, out _, deferred));
            if (failLast && !deferred && at + packet == expected.Length)
            {
                // The immediate-write error revokes the hint. The next
                // normal packet must still retry against the accepted group.
                Assert.False(handled);
                Assert.True(GroupWrite(ref disk, 3, (uint)at, payload));
            }
            else
            {
                Assert.True(handled);
                Assert.Equal(arena, disk.RedoOverlay);
            }
            payload.CopyTo(expected, at);
        }
        var durable = new Disk(disk.Snapshot());
        Assert.Equal(seed.Bytes, GroupRead(ref durable, 3));
        var work = TrstWork();
        Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot());
        Assert.Equal(expected, GroupRead(ref remount, 3));
        Assert.Equal("hello"u8.ToArray(), GroupRead(ref remount, 4));
        Assert.True(AmigaSmartFileSystemCore.CountUsed(ref remount, work.Meta, out var used));
        Assert.Equal((uint)seed.Used.Count - 2, used);
    }

    [Theory]
    [InlineData(2000, false, false)] [InlineData(2000, true, false)]
    [InlineData(2000, false, true)] [InlineData(2000, true, true)]
    [InlineData(3984, false, false)] [InlineData(3984, true, true)]
    public void SharedPoolTrstContinuationUpdatesOwnedImagesWithoutCloningAndPreservesNeighbors(int first, bool nextExtent, bool scratchView)
    {
        var seed = ContinuationSeed(first, nextExtent); var expected = seed.Bytes.ToArray();
        var disk = StageContinuation(seed.Image, expected, 8192, scratchView, shared: true);
        var active = disk.RedoOverlay;
        var payload = Enumerable.Range(0, 8192).Select(i => (byte)(i * 31 + 9)).ToArray();
        Assert.True(GroupWrite(ref disk, 3, 16384, payload));
        Assert.Equal(active, disk.RedoOverlay); // The generic path must swap arenas.
        payload.CopyTo(expected, 16384);
        Assert.Equal(expected, GroupRead(ref disk, 3));
        Assert.Equal("hello"u8.ToArray(), GroupRead(ref disk, 4));
        var work = TrstWork();
        Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var final = disk.Snapshot(); var remount = new Disk(final);
        Assert.Equal(expected, GroupRead(ref remount, 3));
        Assert.Equal("hello"u8.ToArray(), GroupRead(ref remount, 4));
        Assert.True(AmigaSmartFileSystemCore.CountUsed(ref remount, work.Meta, out var used));
        Assert.Equal((uint)seed.Used.Count - 2, used);
        var expectedUsed = seed.Used.ToHashSet();
        foreach (var b in Enumerable.Range(first, 48)) Assert.True(expectedUsed.Remove(b));
        foreach (var b in Enumerable.Range(1000, 48)) Assert.True(expectedUsed.Add(b));
        for (var b = 0; b < 16384; b++)
        {
            var map = Block(final, 12 + b / 4000); var bit = b % 4000;
            Assert.Equal(!expectedUsed.Contains(b), (map[12 + bit / 8] & (128 >> (bit & 7))) != 0);
        }
        var leaf = Block(final, 5); var recordCount = BinaryPrimitives.ReadUInt16BigEndian(leaf[12..]);
        uint key = 1000, previous = 0x80000003; var allocated = 0;
        while (key != 0)
        {
            var at = 16;
            while (at < 16 + recordCount * 14 && BinaryPrimitives.ReadUInt32BigEndian(leaf[at..]) != key) at += 14;
            Assert.True(at < 16 + recordCount * 14);
            Assert.Equal(previous, BinaryPrimitives.ReadUInt32BigEndian(leaf[(at + 8)..]));
            allocated += BinaryPrimitives.ReadUInt16BigEndian(leaf[(at + 12)..]); Assert.InRange(allocated, 1, 128);
            previous = key; key = BinaryPrimitives.ReadUInt32BigEndian(leaf[(at + 4)..]);
        }
        Assert.Equal(128, allocated);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SharedPoolTrstProofReusesDurableFreeWindowWithoutReadingTheOriginalBitmap(bool scratchView)
    {
        var seed = ContinuationSeed(nextExtent: true); var expected = seed.Bytes.ToArray();
        var disk = StageTrstProof(seed.Image, expected, scratchView: scratchView, shared: true);
        var active = disk.RedoOverlay;
        disk.FailReadBlock = disk.ReadUInt32(GroupAddress, 124 + 60);
        var payload = Enumerable.Repeat((byte)0xC8, 1024).ToArray();
        Assert.True(GroupWrite(ref disk, 3, 3072, payload)); payload.CopyTo(expected, 3072);
        Assert.Equal(active, disk.RedoOverlay);
        Assert.Equal(expected, GroupRead(ref disk, 3));
        disk.FailReadBlock = uint.MaxValue;
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        Assert.Equal(0u, disk.ReadUInt32(GroupAddress, 124));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
    }

    [Theory]
    [InlineData(4)] [InlineData(8)] [InlineData(12)] [InlineData(16)] [InlineData(20)] [InlineData(24)]
    [InlineData(28)] [InlineData(32)] [InlineData(36)] [InlineData(40)] [InlineData(44)] [InlineData(48)]
    [InlineData(52)] [InlineData(56)] [InlineData(60)] [InlineData(64)] [InlineData(68)] [InlineData(72)]
    public void SharedPoolTrstProofRejectsCorruptPrivateCertificateBeforeDataIo(int field)
    {
        var seed = ContinuationSeed(nextExtent: true); var expected = seed.Bytes.ToArray();
        var disk = StageTrstProof(seed.Image, expected, shared: true);
        var active = disk.RedoOverlay; var writes = disk.Writes;
        disk.WriteUInt32(GroupAddress, 124 + field, disk.ReadUInt32(GroupAddress, 124 + field) ^ 1);
        Assert.False(GroupWrite(ref disk, 3, 3072, new byte[1024]));
        Assert.Equal(writes, disk.Writes); Assert.Equal(active, disk.RedoOverlay);
        Assert.Equal(0u, disk.ReadUInt32(GroupAddress, 124));
        Assert.Equal(expected, GroupRead(ref disk, 3));
    }

    [Theory]
    [InlineData(1024, false)] [InlineData(1024, true)]
    [InlineData(8192, false)] [InlineData(65536, true)]
    public void SharedPoolTrstDeferredRunKeepsImagesUntilReadAndPreservesBoundedMemory(int packet, bool scratch)
    {
        var seed = ContinuationSeed(nextExtent: true, length: 1024); var expected = seed.Bytes.ToArray();
        var disk = StageTrstProof(seed.Image, expected, packet, scratch, shared: true); var active = disk.RedoOverlay;
        var span = disk.ReadUInt32(GroupAddress, 8); Assert.InRange(span, 1u, 192u * 1024);
        var slotBase = disk.ReadUInt32(GroupAddress, 224); var length = disk.ReadUInt32(GroupAddress, 228) * (512 + 16);
        var images = disk.Ram.AsSpan((int)slotBase, (int)length).ToArray();
        for (var i = 3; i < 6; i++)
        {
            var payload = Enumerable.Repeat((byte)(0xC0 + i), packet).ToArray();
            Assert.True(DeferredWrite(ref disk, (uint)(i * packet), payload, out var handled, out var error));
            Assert.True(handled); Assert.Equal(DOS.Error.None, error); payload.CopyTo(expected, i * packet);
            Assert.Equal(active, disk.RedoOverlay); Assert.Equal(span, disk.ReadUInt32(GroupAddress, 8));
            Assert.Equal(GroupAddress.Raw, disk.ReadUInt32(active, 12));
            Assert.Equal((uint)((i - 2) * packet / Bytes), disk.ReadUInt32(GroupAddress, 200));
            Assert.Equal(images, disk.Ram.AsSpan((int)slotBase, (int)length).ToArray());
        }
        Assert.Equal(expected, DeferredRead(ref disk, expected.Length));
        Assert.Equal(0u, disk.ReadUInt32(active, 12)); Assert.Equal(0x10000200u, disk.ReadUInt32(active, 8));
        Assert.Equal(0u, disk.ReadUInt32(GroupAddress, 200));
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, DeferredRead(ref remount, expected.Length));
        Assert.Equal("hello"u8.ToArray(), GroupRead(ref remount, 4));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SharedPoolTrstDeferredRunFailedPayloadPreservesAcceptedDescriptorAndImages(bool scratch)
    {
        var seed = ContinuationSeed(nextExtent: true);
        for (var sector = 1; sector <= 16; sector++)
        {
            var expected = seed.Bytes.ToArray(); var disk = StageTrstProof(seed.Image, expected, 8192, scratch, shared: true);
            var payload = Enumerable.Repeat((byte)0xD9, 8192).ToArray();
            Assert.True(DeferredWrite(ref disk, 24576, payload, out var handled, out _)); Assert.True(handled);
            payload.CopyTo(expected, 24576);
            var before = disk.Ram.AsSpan((int)GroupAddress.Raw, (int)disk.ReadUInt32(GroupAddress, 8)).ToArray();
            var failed = Enumerable.Repeat((byte)0xEA, 8192).ToArray(); disk.FailAtWrite = disk.Writes + sector;
            Assert.False(DeferredWrite(ref disk, 32768, failed, out handled, out var error));
            Assert.False(handled); Assert.Equal(DOS.Error.DiskNotValidated, error);
            Assert.Equal(before, disk.Ram.AsSpan((int)GroupAddress.Raw, before.Length).ToArray());
            disk.FailAtWrite = 0; Assert.Equal(expected, DeferredRead(ref disk, expected.Length));
            Assert.True(DeferredWrite(ref disk, 32768, failed, out handled, out _)); Assert.True(handled);
            failed.CopyTo(expected, 32768);
            var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
            var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, DeferredRead(ref remount, expected.Length));
        }
    }

    [Theory]
    [InlineData(4)] [InlineData(8)] [InlineData(12)] [InlineData(16)] [InlineData(52)] [InlineData(60)]
    [InlineData(76)] [InlineData(80)] [InlineData(124)] [InlineData(144)] [InlineData(196)]
    [InlineData(200)] [InlineData(204)] [InlineData(208)] [InlineData(212)] [InlineData(216)]
    public void SharedPoolTrstDeferredRunRejectsDamagedPrivateStateBeforeFurtherIo(int offset)
    {
        var seed = ContinuationSeed(); var expected = seed.Bytes.ToArray(); var disk = StageTrstProof(seed.Image, expected, shared: true);
        var payload = Enumerable.Repeat((byte)0xDF, 1024).ToArray();
        Assert.True(DeferredWrite(ref disk, 3072, payload, out var handled, out _)); Assert.True(handled);
        payload.CopyTo(expected, 3072); var writes = disk.Writes;
        var old = disk.ReadUInt32(GroupAddress, offset); disk.WriteUInt32(GroupAddress, offset, old ^ 1);
        Assert.False(DeferredWrite(ref disk, 4096, payload, out handled, out var error));
        Assert.False(handled); Assert.NotEqual(DOS.Error.None, error); Assert.Equal(writes, disk.Writes);
        disk.WriteUInt32(GroupAddress, offset, old);
        Assert.Equal(expected, DeferredRead(ref disk, expected.Length));
    }

    [Fact]
    public void SharedPoolTrstDeferredRunCutsAndReorderedSectorsKeepWholeAcceptedGroup()
    {
        var seed = ContinuationSeed(nextExtent: true); var expected = seed.Bytes.ToArray();
        var payloads = Enumerable.Range(0, 6).Select(n => Enumerable.Repeat((byte)(0xE0 + n), 512).ToArray()).ToArray();
        for (var i = 0; i < payloads.Length; i++) payloads[i].CopyTo(expected, i * 512);
        void Write(ref Disk disk)
        {
            Assert.True(AmigaSmartFileSystemWriteCore.InitializeSharedTrstGroup(ref disk, GroupAddress, 9756));
            for (var i = 0; i < 3; i++) Assert.True(GroupWrite(ref disk, 3, (uint)(i * 512), payloads[i]));
            for (var i = 3; i < 6; i++)
            {
                Assert.True(DeferredWrite(ref disk, (uint)(i * 512), payloads[i], out var handled, out _)); Assert.True(handled);
            }
            var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        }
        var baseline = new Disk(seed.Image.ToArray()) { Buffered = true }; Write(ref baseline);
        for (var cut = 1; cut <= baseline.IoEvents; cut++)
        {
            var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CutAfterIo = cut };
            Assert.Throws<PowerCut>(() => Write(ref disk));
            foreach (var image in disk.CrashImages())
            {
                var recovered = new Disk(image); var work = TrstWork();
                Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref recovered, ref work));
                var contents = GroupRead(ref recovered, 3);
                Assert.True(contents.SequenceEqual(seed.Bytes) || contents.SequenceEqual(expected), $"Partial deferred group at cut {cut}");
                Assert.Equal("hello"u8.ToArray(), GroupRead(ref recovered, 4));
            }
        }
    }


    [Theory]
    [InlineData(2000, 3999, false)] [InlineData(2000, 3999, true)]
    [InlineData(3999, 11999, false)] [InlineData(3999, 11999, true)]
    public void SharedPoolTrstBitmapBoundaryCanAdmitDestinationAndBothMissingMaps(int first, int replacement, bool scratch)
    {
        var seed = ContinuationSeed(first, nextExtent: true); var expected = seed.Bytes.ToArray();
        // Reserve the lower free range so the first replacement ends at the
        // selected destination-map boundary. Preserve every existing used bit.
        for (var block = 1000; block < replacement; block++)
        {
            var bit = block % 4000;
            Block(seed.Image, 12 + block / 4000)[12 + bit / 8] &= (byte)~(128 >> (bit & 7));
            seed.Used.Add(block);
        }
        // Keep later holes shorter than the 96-block source extent. The
        // placement preference must fall back to the selected last block of
        // this bitmap so the next Write really exercises missing-map admission.
        for (var block = replacement + 64; block < 16383; block += 64)
        {
            var bit = block % 4000;
            Block(seed.Image, 12 + block / 4000)[12 + bit / 8] &= (byte)~(128 >> (bit & 7));
            seed.Used.Add(block);
        }
        for (var map = 12; map < 17; map++) Sum(Block(seed.Image, map));
        U32(Block(seed.Image, 2), Bytes - 28, (uint)(16384 - seed.Used.Count)); Sum(Block(seed.Image, 2));
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratch };
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeSharedTrstGroup(ref disk, GroupAddress, 9756));
        var payload = Enumerable.Repeat((byte)0xA7, 512).ToArray();
        Assert.True(GroupWrite(ref disk, 3, 0, payload)); payload.CopyTo(expected, 0);
        Assert.Equal((uint)replacement, disk.ReadUInt32(GroupAddress, 92));
        payload = Enumerable.Repeat((byte)0xA8, 8192).ToArray();
        Assert.True(GroupWrite(ref disk, 3, 512, payload)); payload.CopyTo(expected, 512);
        Assert.Equal(expected, GroupRead(ref disk, 3));
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
        Assert.Equal("hello"u8.ToArray(), GroupRead(ref remount, 4));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SharedPoolTrstBitmapBoundaryUnprovenJournalBoundFallsBackWithoutLosingWrites(bool scratch)
    {
        var seed = ContinuationSeed(3984, nextExtent: true); var expected = seed.Bytes.ToArray();
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratch };
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeSharedTrstGroup(ref disk, GroupAddress, 9756));
        var first = Enumerable.Repeat((byte)0xA9, 8192).ToArray();
        Assert.True(GroupWrite(ref disk, 3, 0, first)); first.CopyTo(expected, 0);
        // The conservative proof cannot establish admission with this lower
        // bound; the general writer must search actual journal space instead.
        disk.WriteUInt32(GroupAddress, 60, 0);
        var second = Enumerable.Repeat((byte)0xAA, 8192).ToArray();
        Assert.True(GroupWrite(ref disk, 3, 8192, second)); second.CopyTo(expected, 8192);
        Assert.Equal(0u, disk.ReadUInt32(GroupAddress, 124));
        Assert.Equal(expected, GroupRead(ref disk, 3));
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
    }

    [Theory]
    [InlineData(3968, false)] [InlineData(3968, true)]
    [InlineData(3984, false)] [InlineData(3984, true)]
    [InlineData(3992, false)] [InlineData(3992, true)]
    public void SharedPoolTrstBitmapBoundaryAndSpanningWritesKeepTheWholeFile(int first, bool scratch)
    {
        var seed = ContinuationSeed(first, nextExtent: true); var expected = seed.Bytes.ToArray();
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratch };
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeSharedTrstGroup(ref disk, GroupAddress, 9756));
        for (var i = 0; i < 4; i++)
        {
            var payload = Enumerable.Repeat((byte)(0xB1 + i), 8192).ToArray();
            var position = (uint)i * 8192;
            if (i == 1 && first == 3968)
            {
                Assert.True(DeferredWrite(ref disk, position, payload, out var handled, out _));
                Assert.True(handled); Assert.NotEqual(0u, disk.ReadUInt32(GroupAddress, 200));
            }
            else Assert.True(GroupWrite(ref disk, 3, position, payload));
            payload.CopyTo(expected, (int)position);
        }
        Assert.InRange(disk.ReadUInt32(GroupAddress, 8), 1u, 192u * 1024);
        Assert.Equal(expected, GroupRead(ref disk, 3));
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot());
        Assert.Equal(expected, GroupRead(ref remount, 3));
        Assert.Equal("hello"u8.ToArray(), GroupRead(ref remount, 4));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SharedPoolTrstBitmapBoundarySectorFailuresRetainAcceptedArenaAndRetry(bool scratch)
    {
        var seed = ContinuationSeed(3984, nextExtent: true);
        for (var sector = 1; sector <= 16; sector++)
        {
            var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratch };
            Assert.True(AmigaSmartFileSystemWriteCore.InitializeSharedTrstGroup(ref disk, GroupAddress, 9756));
            var expected = seed.Bytes.ToArray();
            var first = Enumerable.Repeat((byte)0xB5, 8192).ToArray();
            Assert.True(GroupWrite(ref disk, 3, 0, first)); first.CopyTo(expected, 0);
            var active = disk.RedoOverlay; var span = (int)disk.ReadUInt32(GroupAddress, 12);
            var before = SharedAcceptedImages(ref disk, active);
            var second = Enumerable.Repeat((byte)0xB6, 8192).ToArray();
            disk.FailAtWrite = disk.Writes + sector;
            Assert.False(GroupWrite(ref disk, 3, 8192, second));
            Assert.Equal(active, disk.RedoOverlay);
            Assert.Equal(before, SharedAcceptedImages(ref disk, active));
            disk.FailAtWrite = 0; Assert.Equal(expected, GroupRead(ref disk, 3));
            Assert.True(GroupWrite(ref disk, 3, 8192, second)); second.CopyTo(expected, 8192);
            var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
            var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SharedPoolTrstBitmapBoundaryMissingMapReadFailureKeepsPreviousWriteFlushable(bool scratch)
    {
        var seed = ContinuationSeed(3984, nextExtent: true); var expected = seed.Bytes.ToArray();
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratch };
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeSharedTrstGroup(ref disk, GroupAddress, 9756));
        var first = Enumerable.Repeat((byte)0xB7, 8192).ToArray();
        Assert.True(GroupWrite(ref disk, 3, 0, first)); first.CopyTo(expected, 0);
        var active = disk.RedoOverlay; var span = (int)disk.ReadUInt32(GroupAddress, 12);
        var before = SharedAcceptedImages(ref disk, active);
        disk.FailReadBlock = 13;
        Assert.False(GroupWrite(ref disk, 3, 8192, Enumerable.Repeat((byte)0xB8, 8192).ToArray()));
        Assert.Equal(active, disk.RedoOverlay);
        Assert.Equal(before, SharedAcceptedImages(ref disk, active));
        disk.FailReadBlock = uint.MaxValue;
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SharedPoolTrstBitmapBoundaryProofReadFailureCannotPublishTheCandidate(bool scratch)
    {
        var seed = ContinuationSeed(3984, nextExtent: true); var expected = seed.Bytes.ToArray();
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratch };
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeSharedTrstGroup(ref disk, GroupAddress, 9756));
        var first = Enumerable.Repeat((byte)0xB9, 8192).ToArray();
        Assert.True(GroupWrite(ref disk, 3, 0, first)); first.CopyTo(expected, 0);
        var active = disk.RedoOverlay; var span = (int)disk.ReadUInt32(GroupAddress, 12);
        var before = SharedAcceptedImages(ref disk, active);
        disk.FailAtDurableRead = disk.DurableReads + 1;
        Assert.False(GroupWrite(ref disk, 3, 8192, Enumerable.Repeat((byte)0xBA, 8192).ToArray()));
        Assert.Equal(disk.FailAtDurableRead, disk.DurableReads);
        Assert.Equal(active, disk.RedoOverlay);
        Assert.Equal(before, SharedAcceptedImages(ref disk, active));
        disk.FailAtDurableRead = 0;
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
    }

    [Fact]
    public void SharedPoolTrstBitmapBoundaryCrashCutsAndPendingSectorSubsetsRecoverTheWholeGroup()
    {
        var seed = ContinuationSeed(3999, nextExtent: true); var expected = seed.Bytes.ToArray();
        var first = Enumerable.Repeat((byte)0xBB, 512).ToArray(); var second = Enumerable.Repeat((byte)0xBC, 512).ToArray();
        first.CopyTo(expected, 0); second.CopyTo(expected, 512);
        void Run(ref Disk disk)
        {
            Assert.True(AmigaSmartFileSystemWriteCore.InitializeSharedTrstGroup(ref disk, GroupAddress, 9756));
            Assert.True(GroupWrite(ref disk, 3, 0, first)); Assert.True(GroupWrite(ref disk, 3, 512, second));
            var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        }
        var baseline = new Disk(seed.Image.ToArray()) { Buffered = true }; Run(ref baseline);
        for (var cut = 1; cut <= baseline.IoEvents; cut++)
        {
            var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CutAfterIo = cut };
            Assert.Throws<PowerCut>(() => Run(ref disk));
            foreach (var image in disk.CrashImages())
            {
                var remount = new Disk(image); var work = TrstWork();
                Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref remount, ref work));
                var actual = GroupRead(ref remount, 3);
                Assert.True(actual.SequenceEqual(seed.Bytes) || actual.SequenceEqual(expected), $"Partial bitmap-boundary group at cut {cut}");
                Assert.Equal("hello"u8.ToArray(), GroupRead(ref remount, 4));
            }
        }
    }


    private static byte[] SharedAcceptedImages(ref Disk disk, APTR active)
    {
        var bytes = new List<byte>(); var order = APTR.FromPointer(disk.ReadUInt32(active, 20));
        for (var i = 0u; i < disk.ReadUInt32(active); i++)
        {
            var key = disk.ReadUInt32(order, (int)i * 4);
            bytes.AddRange(disk.Ram.AsSpan((int)key - 8, 528).ToArray());
        }
        return bytes.ToArray();
    }

    [Theory]
    [InlineData(0)] [InlineData(4)] [InlineData(8)] [InlineData(12)]
    [InlineData(232)] [InlineData(236)] [InlineData(240)] [InlineData(224)] [InlineData(228)]
    public void SharedPoolContinuationRejectsStaleOwnershipRevisionAndGeometry(int field)
    {
        var seed = ContinuationSeed(); var expected = seed.Bytes.ToArray();
        var disk = StageTrstProof(seed.Image, expected, shared: true);
        var payload = Enumerable.Repeat((byte)0xD7, 1024).ToArray();
        Assert.True(DeferredWrite(ref disk, 3072, payload, out var handled, out _)); Assert.True(handled);
        payload.CopyTo(expected, 3072);
        var at = field < 16 ? APTR.FromPointer(disk.ReadUInt32(GroupAddress, 224) + disk.ReadUInt32(GroupAddress, 148) * 528 + (uint)field) : APTR.FromPointer(GroupAddress.Raw + (uint)field);
        var old = disk.ReadUInt32(at); var writes = disk.Writes;
        disk.WriteUInt32(at, 0, old ^ 1);
        Assert.False(DeferredWrite(ref disk, 4096, payload, out handled, out var error));
        Assert.False(handled); Assert.NotEqual(DOS.Error.None, error); Assert.Equal(writes, disk.Writes);
        var work = TrstWork(); Assert.False(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        Assert.Equal(writes, disk.Writes);
        disk.WriteUInt32(at, 0, old);
        Assert.Equal(expected, DeferredRead(ref disk, expected.Length));
        Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
    }

    [Theory]
    [InlineData(9756u)] [InlineData(8716u)]
    public void SharedPoolContinuationSurvivesRevisionWrapWithinBothExistingBudgets(uint budget)
    {
        var seed = ContinuationSeed(nextExtent: true); var expected = seed.Bytes.ToArray();
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true };
        disk.Ram.AsSpan((int)GroupAddress.Raw - 4, (int)budget + 8).Fill(0xA5);
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeSharedTrstGroup(ref disk, GroupAddress, budget));
        var slots = disk.ReadUInt32(GroupAddress, 224);
        for (var i = 0u; i < disk.ReadUInt32(GroupAddress, 228); i++)
            disk.WriteUInt32(APTR.FromPointer(slots + i * 528), 4, uint.MaxValue);
        for (var i = 0; i < 6; i++)
        {
            var payload = Enumerable.Repeat((byte)(0xA0 + i), 1024).ToArray();
            if (i < 3) Assert.True(GroupWrite(ref disk, 3, (uint)i * 1024, payload));
            else { Assert.True(DeferredWrite(ref disk, (uint)i * 1024, payload, out var handled, out _)); Assert.True(handled); }
            payload.CopyTo(expected, i * 1024);
        }
        Assert.Equal(0u, disk.ReadUInt32(GroupAddress, 232));
        Assert.Equal(budget, disk.ReadUInt32(GroupAddress, 8));
        Assert.Equal(0xA5A5A5A5u, disk.ReadUInt32(APTR.FromPointer(GroupAddress.Raw - 4)));
        Assert.Equal(0xA5A5A5A5u, disk.ReadUInt32(APTR.FromPointer(GroupAddress.Raw + budget)));
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
    }
}
