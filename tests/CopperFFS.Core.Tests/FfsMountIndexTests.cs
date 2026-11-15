using Amiga;
using CopperSFS;
using CopperStart.Dos;

namespace CopperStart.FileSystem.Tests;

public sealed partial class VolumeGeometryTests
{
    [Fact]
    public void DifferentlySizedRootHashCollisionAvoidsAReadAndExpiresOnRename()
    {
        var (s, p, g, _) = AllocationFixture(false);
        var w = Work(); w.ReadsGroupedRoot = true; w.Index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        AmigaFfsWriteCore.Request Named(string value)
        {
            System.Text.Encoding.ASCII.GetBytes(value).CopyTo(s.Ram, (int)Name.Raw);
            return new() { Directory = g.RootBlock, Name = Name, Length = (uint)value.Length };
        }
        var first = Named("F25");
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref first, false));
        var second = Named("Large"); // Same complete DOS hash as F25.
        s.Transfers.Clear();
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref second, false));
        Assert.DoesNotContain(s.Transfers, t => t.Command is 2 or 24 &&
            t.Offset == g.StartByte + (ulong)first.Header * g.BlockBytes);
        Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, second.Header, false));
        var rename = Named("Large"); rename.Header = first.Header;
        Assert.True(AmigaFfsWriteCore.Rename(ref p, ref w, ref rename));
        second = Named("Large");
        Assert.False(AmigaFfsWriteCore.Create(ref p, ref w, ref second, false));
        Assert.Equal(DOS.Error.ObjectExists, w.Error);
    }

    [Fact]
    public void CachedDirectoryPrefixExpiresOnRenameAndDirectoryRemoval()
    {
        var (s, p, g, _) = AllocationFixture(false);
        var w = Work(); w.ReadsGroupedRoot = true; w.Index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        AmigaFfsWriteCore.Request Path(string path)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(path);
            bytes.CopyTo(s.Ram, (int)Name.Raw);
            return new() { Directory = g.RootBlock, Name = Name, Length = (uint)bytes.Length };
        }
        var a = Path("A"); Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref a, true));
        var b = Path("A/B"); Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref b, true));
        var leaf = Path("A/B/F"); Assert.True(AmigaFfsWriteCore.Parent(ref p, ref w, ref leaf));
        Assert.Equal(b.Header, leaf.Directory);
        var rename = Path("Z"); rename.Header = a.Header;
        Assert.True(AmigaFfsWriteCore.Rename(ref p, ref w, ref rename));
        leaf = Path("A/B/F"); Assert.False(AmigaFfsWriteCore.Parent(ref p, ref w, ref leaf));
        leaf = Path("Z/B/F"); Assert.True(AmigaFfsWriteCore.Parent(ref p, ref w, ref leaf));
        Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, b.Header, false));
        Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, a.Header, false));
        leaf = Path("Z/B/F"); Assert.False(AmigaFfsWriteCore.Parent(ref p, ref w, ref leaf));
        Assert.Equal(DOS.Error.ObjectNotFound, w.Error);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DamagedStoredNameLengthRejectsUnlinkBeforeChangingTheVolume(bool rename)
    {
        var (s, p, g, file) = AllocationFixture(false);
        Assert.True(p.ReadBlock(file.Header, Meta));
        p.WriteUInt8(Meta, g.Tail + 432, 255);
        Checksum(ref p, Meta, g.BlockBytes, 20);
        Assert.True(p.WriteBlock(file.Header, Meta));
        var w = Work();
        w.Index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        s.Transfers.Clear();
        file.Directory = g.RootBlock;
        file.Name = Name;
        file.Length = 1;
        s.Ram[(int)Name.Raw] = (byte)'Z';
        Assert.False(rename ? AmigaFfsWriteCore.Rename(ref p, ref w, ref file) :
            AmigaFfsWriteCore.Remove(ref p, ref w, file.Header, false));
        Assert.Equal(DOS.Error.DiskNotValidated, w.Error);
        Assert.DoesNotContain(s.Transfers, t => t.Command is 3 or 25);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void FirstShortAppendWaitsForPublicationAfterValidatingAnExistingFile(bool ofs, bool failRoot)
    {
        var (s, p, g, file) = AllocationFixture(ofs);
        var seed = Work();
        var contents = new byte[8000];
        new Random(32).NextBytes(contents);
        contents.CopyTo(s.Ram, (int)Payload.Raw);
        file.Buffer = Payload;
        file.Length = (uint)contents.Length;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref seed, ref file, out _, true));
        var w = Work();
        w.Index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        w.GroupFileWrites = w.BufferSmallAppends = true;
        s.BulkTransfers = true;
        s.Transfers.Clear();
        var rootOffset = g.StartByte + g.RootBlock * (ulong)g.BlockBytes;
        if (failRoot) s.FailWrite = rootOffset;
        file.Position = file.Length;
        file.Length = 257;
        Assert.Equal(!failRoot, AmigaFfsWriteCore.Write(ref p, ref w, ref file, out var written, true));
        Assert.Equal(failRoot ? 0u : 257u, written);
        if (failRoot)
        {
            Assert.False(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
            return;
        }
        file.Position += written;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _, true));
        Assert.Equal(rootOffset, Assert.Single(s.Transfers, t => t.Command is 3 or 25).Offset);
        Assert.True(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
        InspectAllocation(s, g, file.Header, ofs, contents.Concat(contents.Take(257)).Concat(contents.Take(257)).ToArray());
    }

    [Theory]
    [InlineData(257)] [InlineData(511)]
    public void BufferedAppendsDrainWholeSectorsFromAnAlignedAddress(int prefix)
    {
        var (s, p, g, file) = AllocationFixture(false);
        var w = Work();
        w.Index = APTR.FromPointer(0x100008);
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        s.BulkTransfers = w.GroupFileWrites = true;
        var expected = new byte[prefix + 16 * 257];
        new Random(16).NextBytes(expected);
        expected.CopyTo(s.Ram, (int)Payload.Raw);
        file.Buffer = Payload;
        file.Length = (uint)prefix;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _, true));
        w.BufferSmallAppends = true;
        file.Length = 257;
        for (file.Position = (uint)prefix; file.Position < expected.Length; file.Position += file.Length)
        {
            file.Buffer = APTR.FromPointer(Payload.Raw + file.Position);
            Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _, true));
        }
        Assert.True(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
        Assert.NotEqual(0u, s.LastBulkWriteBuffer);
        Assert.Equal(0u, s.LastBulkWriteBuffer % g.BlockBytes);
        InspectAllocation(s, g, file.Header, false, expected);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LargeDeleteHintsCoverEveryOwnedBlockAndPreserveAnAllocatedGap(bool gap)
    {
        var (s, p, g, file) = AllocationFixture(false, blocks: 20000);
        var other = file.Header + 180;
        if (gap) AllocationBit(ref p, g, other, false);
        var w = Work();
        w.Index = s.FfsIndex = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 256 * 1024);
        w.GroupFileWrites = s.BulkTransfers = true;
        file.Buffer = Payload; file.Length = 65536;
        for (var packet = 0; packet < 36; packet++)
        {
            Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out var written, true));
            file.Position += written;
        }
        Assert.True(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
        s.FreedQueue = APTR.FromPointer(0x150000);
        w.GroupFileWrites = false;
        Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, file.Header, false));
        var ranges = AmigaFreedRangeQueue.Count(ref p, s.FreedQueue);
        var released = new HashSet<uint>();
        for (var i = 0; i < ranges; i++)
        {
            var first = p.ReadUInt32(s.FreedQueue, 4 + i * 8);
            var count = p.ReadUInt32(s.FreedQueue, 8 + i * 8);
            for (uint n = 0; n < count; n++) released.Add(first + n);
        }
        var data = file.Position / g.BlockBytes;
        Assert.Equal(data + (data - 1) / g.TableEntries + 1, (uint)released.Count);
        Assert.Contains(file.Header, released);
        if (gap) Assert.DoesNotContain(other, released);
        else Assert.Equal(1u, ranges);
        CompareIndexCount(ref p, s);
    }

    [Fact]
    public void TruncatingAContiguousFileAcrossBitmapPagesReportsExactlyTheReleasedBlocks()
    {
        var (s, p, g, file) = AllocationFixture(false, blocks: 12000, first: 4020);
        var w = Work();
        s.FfsIndex = w.Index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        s.BulkTransfers = true;
        w.GroupFileWrites = true;
        var data = new byte[126 * 512];
        new Random(62).NextBytes(data);
        data.CopyTo(s.Ram, (int)Payload.Raw);
        file.Length = (uint)data.Length;
        file.Buffer = Payload;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out var actual, true));
        Assert.Equal(file.Length, actual);
        Assert.True(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
        var dataKeys = InspectAllocation(s, g, file.Header, false, data);
        Assert.Equal(dataKeys[0] + 125, dataKeys[^1]);
        Assert.InRange(g.ReservedBlocks + g.BitmapBits, dataKeys[0] + 1, dataKeys[^1]);
        Assert.True(p.ReadBlock(file.Header, Meta));
        var extension = p.ReadUInt32(Meta, g.Tail + 504);
        Assert.NotEqual(0u, extension);
        var expected = dataKeys.Append(extension).Order().ToArray();

        s.FreedQueue = APTR.FromPointer(0x120000);
        w.GroupFileWrites = false;
        Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, file.Header, true));
        var released = new List<uint>();
        for (var i = 0; i < AmigaFreedRangeQueue.Count(ref p, s.FreedQueue); i++)
        {
            var first = p.ReadUInt32(s.FreedQueue, 4 + i * 8);
            var count = p.ReadUInt32(s.FreedQueue, 8 + i * 8);
            for (var n = 0u; n < count; n++) released.Add(first + n);
        }
        Assert.Equal(expected, released.Order().ToArray());
        CompareIndexCount(ref p, s);
        InspectAllocation(s, g, file.Header, false, []);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LargeAppendCompletesTheBufferedSectorBeforeWritingWholeSectors(bool failRemainder)
    {
        var (s, p, g, _) = AllocationFixture(false);
        var w = Work();
        w.Index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        s.BulkTransfers = true;
        s.Ram[(int)Name.Raw] = (byte)'Q';
        var file = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 1 };
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref file, false, true));
        w.GroupFileWrites = w.BufferSmallAppends = true;
        var all = new byte[9 * 512]; new Random(45).NextBytes(all);
        all.CopyTo(s.Ram, (int)Payload.Raw);
        file.Buffer = Payload; file.Length = 507;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _, true));
        file.Position = 507; file.Buffer = APTR.FromPointer(Payload.Raw + 507); file.Length = (uint)all.Length - 507;
        s.Transfers.Clear();
        if (failRemainder) s.FailWriteCall = s.WriteCalls + 2;
        Assert.Equal(!failRemainder, AmigaFfsWriteCore.Write(ref p, ref w, ref file, out var actual, true));
        if (failRemainder)
        {
            Assert.False(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
            Assert.True(p.ReadBlock(g.RootBlock, Meta));
            Assert.Equal(0u, p.ReadUInt32(Meta, g.Tail + 312));
        }
        else
        {
            Assert.Equal(file.Length, actual);
            Assert.Equal(new uint[] { 512, 4096 }, s.Transfers.Where(t => t.Command is 3 or 25).Select(t => t.Bytes).ToArray());
            Assert.True(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
            InspectAllocation(s, g, file.Header, false, all);
        }
    }

    [Theory]
    [InlineData("aa", false, 0)] [InlineData("ZZ", false, 1)] [InlineData("ZZ", true, 1)]
    public void IndexedRenameUpdatesTheContainingDirectoryOnce(string targetName, bool collidingHead, int parentWrites)
    {
        var (s, p, g, _) = AllocationFixture(false);
        var w = Work();
        w.Index = s.FfsIndex = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        AmigaFfsWriteCore.Request Named(uint parent, string name)
        {
            System.Text.Encoding.ASCII.GetBytes(name).CopyTo(s.Ram, (int)Name.Raw);
            return new() { Directory = parent, Name = Name, Length = (uint)name.Length };
        }
        var dir = Named(g.RootBlock, "D");
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref dir, true));
        var entry = Named(dir.Header, "AA");
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref entry, false));
        var other = Named(dir.Header, "B4"); // Same DOS hash as AA.
        if (collidingHead) Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref other, false));
        var rename = Named(dir.Header, targetName);
        rename.Header = entry.Header;
        s.Transfers.Clear();
        Assert.True(AmigaFfsWriteCore.Rename(ref p, ref w, ref rename), w.Error.ToString());
        Assert.Equal(parentWrites, s.Transfers.Count(t => t.Command is 3 or 25 &&
            t.Offset == g.StartByte + dir.Header * (ulong)g.BlockBytes));
        s.FfsIndex = APTR.Null;
        Assert.Equal(entry.Header, AmigaDiskFileSystemCore.Resolve(ref p, dir.Header, Name, 2, w.DosType, Meta, out _));
        if (collidingHead)
        {
            Named(dir.Header, "B4");
            Assert.Equal(other.Header, AmigaDiskFileSystemCore.Resolve(ref p, dir.Header, Name, 2, w.DosType, Meta, out _));
        }
        if (targetName != "aa")
        {
            Named(dir.Header, "AA");
            Assert.Equal(0u, AmigaDiskFileSystemCore.Resolve(ref p, dir.Header, Name, 2, w.DosType, Meta, out _));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LargeAppendKeepsItsPartialLastSectorThroughTheNextPacket(bool failClose)
    {
        var (s, p, g, file) = AllocationFixture(false, blocks: 20000);
        var w = Work(); w.Index = APTR.FromPointer(0x100000);
        w.GroupFileWrites = w.BufferSmallAppends = true;
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        var expected = new byte[9 * 512];
        new Random(230).NextBytes(expected);
        expected.CopyTo(s.Ram, (int)Payload.Raw);
        file.Buffer = Payload; file.Length = 4096 + 7;
        s.BulkTransfers = true; s.Transfers.Clear();
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out var actual, true), w.Error.ToString());
        Assert.Equal(file.Length, actual);
        var span = Assert.Single(s.Transfers, t => t.Command is 3 or 25 && t.Bytes == 4096);
        var tail = span.Offset + 4096;
        Assert.DoesNotContain(s.Transfers, t => t.Command is 3 or 25 && t.Offset == tail);
        file.Position = file.Length; file.Length = 505;
        file.Buffer = APTR.FromPointer(Payload.Raw + file.Position);
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out actual, true));
        Assert.Equal(505u, actual);
        if (failClose) s.FailWrite = tail;
        Assert.Equal(!failClose, AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
        Assert.Single(s.Transfers, t => t.Command is 3 or 25 && t.Offset == tail);
        if (failClose)
        {
            Assert.True(AmigaDiskFileSystemCore.Header(ref p, g.RootBlock, Meta));
            Assert.Equal(0u, p.ReadUInt32(Meta, g.Tail + 312));
        }
        else InspectAllocation(s, g, file.Header, false, expected);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DirectoryAndFirstChildrenShareOneParentWrite(bool ofs)
    {
        var (s, p, g, _) = AllocationFixture(ofs);
        var w = Work();
        w.Index = s.FfsIndex = APTR.FromPointer(0x100000);
        w.GroupMetadata = w.ReadsGroupedRoot = w.ReadsGroupedDirectories = true;
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        s.Transfers.Clear();
        uint Create(uint parent, byte name, bool directory, bool open)
        {
            s.Ram[(int)Name.Raw] = name;
            var request = new AmigaFfsWriteCore.Request { Directory = parent, Name = Name, Length = 1 };
            Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref request, directory, open), w.Error.ToString());
            return request.Header;
        }
        var parent = Create(g.RootBlock, (byte)'Q', true, false);
        var nested = Create(parent, (byte)'R', true, false);
        Assert.Equal(nested, AmigaDiskFileSystemCore.Resolve(ref p, parent, Name, 1, w.DosType, Meta, out _));
        var file = Create(parent, (byte)'S', false, true);
        var address = g.StartByte + (ulong)parent * g.BlockBytes;
        Assert.DoesNotContain(s.Transfers, t => t.Command is 3 or 25 && t.Offset == address);
        Assert.True(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
        Assert.Single(s.Transfers, t => t.Command is 3 or 25 && t.Offset == address);
        var published = AmigaFfsMountIndex.PublishedMetadataImage(ref p, w.Index, parent);
        Assert.True(published.IsNotNull);
        Assert.True(AmigaDiskFileSystemCore.Checksum(ref p, published));
        Assert.Equal(parent, p.ReadUInt32(published, 4));
        s.FfsIndex = APTR.Null;
        Assert.Equal(file, AmigaDiskFileSystemCore.Resolve(ref p, parent, Name, 1, w.DosType, Meta, out _));
        s.Ram[(int)Name.Raw] = (byte)'R';
        Assert.Equal(nested, AmigaDiskFileSystemCore.Resolve(ref p, parent, Name, 1, w.DosType, Meta, out _));
        Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, nested, false));
        Assert.True(AmigaFfsMountIndex.PublishedMetadataImage(ref p, w.Index, parent).IsNull);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RemovingAnUnpublishedDirectoryExpiresItsImageBeforeReuse(bool fail)
    {
        var (s, p, g, _) = AllocationFixture(false);
        var w = Work();
        w.Index = s.FfsIndex = APTR.FromPointer(0x100000);
        w.GroupMetadata = w.ReadsGroupedRoot = w.ReadsGroupedDirectories = true;
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        s.Ram[(int)Name.Raw] = (byte)'Q';
        var r = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 1 };
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref r, true));
        var removed = r.Header;
        if (fail) s.FailWrite = g.StartByte + removed * (ulong)g.BlockBytes;
        Assert.Equal(!fail, AmigaFfsWriteCore.Remove(ref p, ref w, removed, false));
        if (fail)
        {
            Assert.False(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
            s.FfsIndex = APTR.Null;
            Assert.True(AmigaDiskFileSystemCore.Header(ref p, g.RootBlock, Meta));
            Assert.Equal(0u, p.ReadUInt32(Meta, g.Tail + 312));
            return;
        }
        s.Ram[(int)Name.Raw] = (byte)'R';
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref r, false, true));
        Assert.Equal(removed, r.Header);
        Assert.True(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
        s.FfsIndex = APTR.Null;
        Assert.True(AmigaDiskFileSystemCore.Header(ref p, r.Header, Meta));
        Assert.Equal(0xFFFFFFFDu, p.ReadUInt32(Meta, g.Tail + 508));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void GroupedRootLinksAreVisibleBeforeOneFinalPublication(bool ofs)
    {
        var (s, p, g, _) = AllocationFixture(ofs);
        var w = Work();
        w.Index = s.FfsIndex = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        w.GroupMetadata = true;
        w.ReadsGroupedRoot = true;
        var rootOffset = g.StartByte + (ulong)g.RootBlock * g.BlockBytes;
        s.Transfers.Clear();
        uint Create(byte name)
        {
            s.Ram[(int)Name.Raw] = name;
            var request = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 1 };
            Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref request, true));
            Assert.Equal(request.Header, AmigaDiskFileSystemCore.Resolve(ref p, g.RootBlock, Name, 1, w.DosType, Meta, out _));
            return request.Header;
        }
        var first = Create((byte)'Q');
        var second = Create((byte)'R');
        Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, first, false));
        s.Ram[(int)Name.Raw] = (byte)'Q';
        Assert.Equal(0u, AmigaDiskFileSystemCore.Resolve(ref p, g.RootBlock, Name, 1, w.DosType, Meta, out var error));
        Assert.Equal(DOS.Error.ObjectNotFound, error);
        Assert.Single(s.Transfers, t => t.Command is 3 or 25 && t.Offset == rootOffset);
        Assert.True(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
        Assert.Equal(2, s.Transfers.Count(t => t.Command is 3 or 25 && t.Offset == rootOffset));
        s.FfsIndex = APTR.Null;
        s.Ram[(int)Name.Raw] = (byte)'R';
        Assert.Equal(second, AmigaDiskFileSystemCore.Resolve(ref p, g.RootBlock, Name, 1, w.DosType, Meta, out _));
    }

    [Fact]
    public void TruncationExpiresTheVerifiedRunBeforeItsBlocksCanBeReallocated()
    {
        var (s, p, g, file) = AllocationFixture(false);
        var w = Work();
        w.Index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        s.BulkTransfers = true;
        s.Ram[(int)Name.Raw] = (byte)'Q';
        var other = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 1 };
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref other, false));

        void Fill(ref AmigaFfsWriteCore.Request target, byte value)
        {
            target.Position = 0;
            target.Length = 8192;
            target.Buffer = Payload;
            s.Ram.AsSpan((int)Payload.Raw, (int)target.Length).Fill(value);
            w.GroupFileWrites = true;
            Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref target, out var actual, true), w.Error.ToString());
            Assert.Equal(target.Length, actual);
            Assert.True(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
        }

        Fill(ref file, 1);
        var original = InspectAllocation(s, g, file.Header, false, Enumerable.Repeat((byte)1, 8192).ToArray());
        w.GroupFileWrites = false;
        Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, file.Header, true));
        // Single-sector appends use the ordinary allocator on this same mount.
        var sentinel = Enumerable.Repeat((byte)2, 8192).ToArray();
        sentinel.CopyTo(s.Ram, (int)Payload.Raw);
        other.Buffer = Payload;
        other.Length = g.BlockBytes;
        for (other.Position = 0; other.Position < sentinel.Length; other.Position += other.Length)
            Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref other, out _, true));
        var recycled = InspectAllocation(s, g, other.Header, false, sentinel);
        Assert.NotEmpty(original.Intersect(recycled));
        Fill(ref file, 3);
        Fill(ref file, 4);
        InspectAllocation(s, g, file.Header, false, Enumerable.Repeat((byte)4, 8192).ToArray());
        InspectAllocation(s, g, other.Header, false, sentinel);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExistingWriterPublishesAnEarlierNamespaceChangeAtClose(bool ofs)
    {
        var (s, p, g, file) = AllocationFixture(ofs);
        var w = Work();
        w.Index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        w.GroupMetadata = true;
        s.Ram[(int)Name.Raw] = (byte)'Q';
        var dir = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 1 };
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref dir, true));
        var next = Work();
        next.Index = w.Index;
        next.GroupMetadata = next.GroupFileWrites = true;
        file.Length = 4096;
        file.Buffer = Payload;
        s.Transfers.Clear();
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref next, ref file, out _, true));
        var root = g.StartByte + g.RootBlock * (ulong)g.BlockBytes;
        Assert.DoesNotContain(s.Transfers, t => t.Command is 3 or 25 && t.Offset == root);
        Assert.True(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref next));
        Assert.Single(s.Transfers, t => t.Command is 3 or 25 && t.Offset == root);
        Assert.Equal(dir.Header, AmigaDiskFileSystemCore.Resolve(ref p, g.RootBlock, Name, 1, w.DosType, Meta, out _));
        InspectAllocation(s, g, file.Header, ofs, new byte[4096]);
    }

    [Theory]
    [InlineData(1, 37400)] [InlineData(511, 36865)] [InlineData(512, 36865)]
    public void IndexedAppendPreservesPartialSectorsAcrossTableBoundary(int initial, int added)
    {
        var (s, p, g, file) = AllocationFixture(false);
        var w = Work();
        w.Index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        w.GroupFileWrites = true;
        var bytes = new byte[initial + added];
        new Random(27).NextBytes(bytes);
        bytes.CopyTo(s.Ram, (int)Payload.Raw);
        file.Buffer = Payload;
        file.Length = (uint)initial;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _, true));
        file.Buffer = APTR.FromPointer(Payload.Raw + (uint)initial);
        file.Position = (uint)initial;
        file.Length = (uint)added;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out var written, true));
        Assert.Equal(file.Length, written);
        Assert.True(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
        InspectAllocation(s, g, file.Header, false, bytes);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ReopeningAnEmptyFileForOutputNeedsNoDiskWrites(bool ofs)
    {
        var (s, p, _, file) = AllocationFixture(ofs);
        var w = Work();
        w.Index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        w.GroupFileWrites = true;
        s.Transfers.Clear();
        Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, file.Header, true));
        Assert.True(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
        Assert.DoesNotContain(s.Transfers, t => t.Command is 3 or 25);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SwitchingFileBanksPreservesBothMultiTableFiles(bool ofs)
    {
        var (s, p, g, _) = AllocationFixture(ofs, blocks: 20000);
        var w = Work();
        w.Index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        s.BulkTransfers = true;
        s.Ram[(int)Name.Raw] = (byte)'A';
        var first = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 1 };
        var firstBytes = new byte[400000];
        new Random(24).NextBytes(firstBytes);
        firstBytes.CopyTo(s.Ram, (int)Payload.Raw);
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref first, false, true));
        first.Buffer = Payload;
        first.Length = (uint)firstBytes.Length;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref first, out _, true));

        s.Ram[(int)Name.Raw] = (byte)'B';
        var second = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 1 };
        Assert.True(AmigaFfsWriteCore.TryCreateGroupedSibling(ref p, ref w, ref second));
        Assert.Equal(DOS.Error.None, w.Error);
        var secondBytes = new byte[90000];
        new Random(25).NextBytes(secondBytes);
        secondBytes.CopyTo(s.Ram, (int)Payload.Raw);
        second.Buffer = Payload;
        second.Length = (uint)secondBytes.Length;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref second, out _, true));
        Assert.True(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));

        first.Position = first.Length;
        first.Length = 4096;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref first, out _, true));
        Assert.True(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
        InspectAllocation(s, g, first.Header, ofs, firstBytes.Concat(secondBytes.Take(4096)).ToArray());
        InspectAllocation(s, g, second.Header, ofs, secondBytes);
    }

    [Fact]
    public void ExtendingExistingFfsTablesIncludesOriginalUnusedWordsInTheirChecksums()
    {
        var (s, p, g, file) = AllocationFixture(false, blocks: 20000);
        var bytes = new byte[92 * 512];
        new Random(44).NextBytes(bytes);
        bytes.CopyTo(s.Ram, (int)Payload.Raw);
        var seed = Work();
        file.Buffer = Payload;
        file.Length = 76 * 512;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref seed, ref file, out _, true));
        Assert.True(p.ReadBlock(file.Header, Meta));
        uint extension = p.ReadUInt32(Meta, g.Tail + 504);
        p.WriteUInt32(Meta, g.Tail + 328, 0x01410000);
        Checksum(ref p, Meta, g.BlockBytes, 20);
        Assert.True(p.WriteBlock(file.Header, Meta));
        Assert.True(p.ReadBlock(extension, Meta));
        p.WriteUInt32(Meta, 24, 0x12345678);
        p.WriteUInt32(Meta, g.Tail + 360, 0x90ABCDEF);
        Checksum(ref p, Meta, g.BlockBytes, 20);
        Assert.True(p.WriteBlock(extension, Meta));

        var w = Work();
        w.Index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        w.GroupFileWrites = w.ReadsGroupedRoot = true;
        file.Position = file.Length;
        file.Buffer = APTR.FromPointer(Payload.Raw + file.Position);
        file.Length = 16 * 512;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _, true));
        Assert.True(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
        InspectAllocation(s, g, file.Header, false, bytes);
        Assert.True(p.ReadBlock(extension, Meta));
        Assert.True(AmigaDiskFileSystemCore.Checksum(ref p, Meta));
        Assert.Equal(0x12345678u, p.ReadUInt32(Meta, 24));
        Assert.Equal(0x90ABCDEFu, p.ReadUInt32(Meta, g.Tail + 360));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void TruncateAndRewriteShareAllocationUntilClose(bool ofs, bool failHeader)
    {
        var (s, p, g, file) = AllocationFixture(ofs);
        var index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, index, 65536);
        var w = Work();
        w.Index = index;
        file.Buffer = Payload;
        file.Length = 8192;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _, true));
        s.Transfers.Clear();
        w.GroupFileWrites = true;
        Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, file.Header, true));
        file.Length = 17;
        s.Ram[(int)Payload.Raw] = 0xC3;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _, true));
        var header = g.StartByte + file.Header * (ulong)g.BlockBytes;
        Assert.DoesNotContain(s.Transfers, t => t.Command is 3 or 25 && t.Offset == header);
        if (failHeader) s.FailWrite = header;
        Assert.Equal(!failHeader, AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
        Assert.True(p.ReadBlock(g.RootBlock, Meta));
        Assert.Equal(failHeader ? 0u : uint.MaxValue, p.ReadUInt32(Meta, g.Tail + 312));
        if (!failHeader) InspectAllocation(s, g, file.Header, ofs, s.Ram.AsSpan((int)Payload.Raw, 17).ToArray());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void NewFileInheritsDirectoryGroupAndPublishesBothOnClose(bool ofs)
    {
        var (s, p, g, _) = AllocationFixture(ofs);
        var w = Work();
        w.Index = APTR.FromPointer(0x100000);
        w.GroupMetadata = true;
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        s.Ram[(int)Name.Raw] = (byte)'Q';
        var dir = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 1 };
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref dir, true));
        s.Transfers.Clear();
        s.Ram[(int)Name.Raw] = (byte)'N';
        var child = new AmigaFfsWriteCore.Request { Directory = dir.Header, Name = Name, Length = 1 };
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref child, false, true));
        Assert.DoesNotContain(s.Transfers, t => t.Command is 3 or 25 && t.Offset == g.StartByte + g.RootBlock * (ulong)g.BlockBytes);
        var fileWork = Work();
        fileWork.Index = w.Index;
        fileWork.GroupFileWrites = true;
        child.Buffer = Payload; child.Length = 17;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref fileWork, ref child, out _, true));
        var bitmapOffset = g.StartByte + (g.RootBlock + 1) * (ulong)g.BlockBytes;
        Assert.DoesNotContain(s.Transfers, t => t.Command is 3 or 25 && t.Offset == bitmapOffset);
        Assert.True(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref fileWork));
        Assert.Single(s.Transfers, t => t.Command is 3 or 25 && t.Offset == bitmapOffset);
        Assert.Equal(child.Header, AmigaDiskFileSystemCore.Resolve(ref p, dir.Header, Name, 1, w.DosType, Meta, out _));
        Assert.True(p.ReadBlock(g.RootBlock, Meta));
        Assert.Equal(uint.MaxValue, p.ReadUInt32(Meta, g.Tail + 312));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void IndexedGrowthReusesVerifiedBitmapPagesAndPersistsEveryTransition(bool ofs)
    {
        var (s, p, g, file) = AllocationFixture(ofs, blocks: 20000);
        var index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, index, 65536);
        var w = Work();
        w.Index = index;
        file.Buffer = Payload;
        file.Length = 4096;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _, true));
        var maps = new HashSet<ulong>();
        Assert.True(p.ReadBlock(g.RootBlock, Meta));
        for (var page = 0; page < (g.BlockCount - g.ReservedBlocks + g.BitmapBits - 1) / g.BitmapBits; page++)
            maps.Add(g.StartByte + p.ReadUInt32(Meta, g.Tail + 316 + page * 4) * (ulong)g.BlockBytes);
        s.Transfers.Clear();
        file.Position = file.Length;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _, true));
        Assert.DoesNotContain(s.Transfers, t => t.Command is 2 or 24 && maps.Contains(t.Offset));
        Assert.True(AmigaDiskFileSystemCore.CountUsed(ref p, Meta, Bitmap, out var indexed, index));
        Assert.True(AmigaDiskFileSystemCore.CountUsed(ref p, Meta, Bitmap, out var fresh));
        Assert.Equal(fresh, indexed);
        InspectAllocation(s, g, file.Header, ofs, new byte[8192]);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void NamespacePacketsShareValidityAndPublishAtHandoff(bool ofs, bool failPublication)
    {
        var (s, p, g, _) = AllocationFixture(ofs);
        var w = Work();
        w.Index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        w.GroupMetadata = true;
        s.Ram[(int)Name.Raw] = (byte)'Q';
        var dir = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 1 };
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref dir, true));
        Assert.Equal(dir.Header, AmigaDiskFileSystemCore.Resolve(ref p, g.RootBlock, Name, 1, w.DosType, Meta, out _));
        s.Transfers.Clear();
        dir.Position = 4;
        Assert.True(AmigaFfsWriteCore.SetMetadata(ref p, ref w, ref dir, DosPacketAction.SetProtection));
        dir.Position = 0;
        Assert.True(AmigaFfsWriteCore.SetMetadata(ref p, ref w, ref dir, DosPacketAction.SetProtection));
        Assert.DoesNotContain(s.Transfers, t => t.Command is 3 or 25);
        Assert.True(p.ReadBlock(g.RootBlock, Meta));
        Assert.Equal(0u, p.ReadUInt32(Meta, g.Tail + 312));
        s.Ram[(int)Name.Raw] = (byte)'R';
        Assert.True(AmigaFfsWriteCore.Rename(ref p, ref w, ref dir));
        Assert.Equal(dir.Header, AmigaDiskFileSystemCore.Resolve(ref p, g.RootBlock, Name, 1, w.DosType, Meta, out _));
        Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, dir.Header, false));
        Assert.Equal(0u, AmigaDiskFileSystemCore.Resolve(ref p, g.RootBlock, Name, 1, w.DosType, Meta, out _));
        var bitmapOffset = g.StartByte + (g.RootBlock + 1) * (ulong)g.BlockBytes;
        Assert.DoesNotContain(s.Transfers, t => t.Command is 3 or 25 && t.Offset == bitmapOffset);
        if (failPublication) s.FailWrite = g.StartByte + g.RootBlock * (ulong)g.BlockBytes;
        var close = Work();
        close.Index = w.Index;
        Assert.Equal(!failPublication, AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref close));
        Assert.True(p.ReadBlock(g.RootBlock, Meta));
        Assert.Equal(failPublication ? 0u : uint.MaxValue, p.ReadUInt32(Meta, g.Tail + 312));
        Assert.Equal(!failPublication, AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref close));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    [InlineData(false, false, 4096)]
    [InlineData(false, false, 36864)]
    [InlineData(false, false, 23, true)]
    public void SiblingOpenSharesBitmapAndPublishesBothFilesAtFirstClose(bool ofs, bool failFirstHeader, int append = 23, bool failSibling = false)
    {
        var (s, p, g, _) = AllocationFixture(ofs, blocks: 20000);
        var w = Work(); w.Index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        s.BulkTransfers = true;
        s.Transfers.Clear();
        s.Ram[(int)Name.Raw] = (byte)'A';
        var first = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 1 };
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref first, false, true));
        var content = new byte[76 * 512];
        new Random(14).NextBytes(content); content.CopyTo(s.Ram, (int)Payload.Raw);
        first.Buffer = Payload; first.Length = (uint)content.Length;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref first, out _, true));
        s.Ram[(int)Name.Raw] = (byte)'B';
        var second = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 1 };
        Assert.True(AmigaFfsWriteCore.TryCreateGroupedSibling(ref p, ref w, ref second));
        Assert.Equal(DOS.Error.None, w.Error);
        second.Buffer = Payload; second.Length = 100;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref second, out _, true));
        if (failFirstHeader) s.FailWrite = g.StartByte + first.Header * (ulong)g.BlockBytes;
        if (failSibling) s.FailWrite = g.StartByte + second.Header * (ulong)g.BlockBytes;
        var closeFailed = failFirstHeader || failSibling;
        Assert.Equal(!closeFailed, AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
        if (!ofs)
            Assert.Contains(s.Transfers, t => t.Command is 3 or 25 &&
                t.Offset == g.StartByte + (second.Header - 1) * (ulong)g.BlockBytes && t.Bytes == 2 * g.BlockBytes);
        var writes = s.Transfers.Where(t => t.Command is 3 or 25).Select(t => (t.Offset - g.StartByte) / g.BlockBytes).ToList();
        Assert.Equal(closeFailed ? 1 : 2, writes.Count(k => k == g.RootBlock));
        Assert.Single(writes, k => k == g.RootBlock + 1);
        if (!failSibling) Assert.True(writes.IndexOf(g.RootBlock + 1) < writes.IndexOf(first.Header));
        if (closeFailed)
        {
            Assert.True(AmigaDiskFileSystemCore.Header(ref p, g.RootBlock, Meta));
            Assert.Equal(0u, p.ReadUInt32(Meta, g.Tail + 312));
            Assert.False(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
            return;
        }
        Assert.Equal(second.Header, AmigaDiskFileSystemCore.Resolve(ref p, g.RootBlock, Name, 1, w.DosType, Meta, out _));
        s.Ram[(int)Name.Raw] = (byte)'A';
        Assert.Equal(first.Header, AmigaDiskFileSystemCore.Resolve(ref p, g.RootBlock, Name, 1, w.DosType, Meta, out _));
        var keys = InspectAllocation(s, g, first.Header, ofs, content);
        InspectAllocation(s, g, second.Header, ofs, content[..100]);
        Assert.True(p.ReadBlock(first.Header, Meta));
        var tail = p.ReadUInt32(Meta, g.Tail + 504);
        s.Transfers.Clear();
        first.Position = first.Length; first.Length = (uint)append;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref first, out _, true));
        Assert.True(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
        if (ofs)
            Assert.DoesNotContain(s.Transfers, t => t.Command is 2 or 24 && t.Offset == g.StartByte + keys[10] * (ulong)g.BlockBytes);
        if (append >= 1024)
        {
            Assert.DoesNotContain(s.Transfers, t => t.Command is 2 or 24 &&
                (t.Offset == g.StartByte + first.Header * (ulong)g.BlockBytes || t.Offset == g.StartByte + tail * (ulong)g.BlockBytes));
            Assert.Single(s.Transfers, t => t.Command is 3 or 25 && t.Offset == g.StartByte + tail * (ulong)g.BlockBytes);
        }
        InspectAllocation(s, g, first.Header, ofs, content.Concat(content.Take(append)).ToArray());
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void BufferedAppendsReserveSpaceAndPublishAtClose(bool ofs, bool failData)
    {
        // Exactly one free data block remains after creating the second file.
        var (s, p, g, _) = AllocationFixture(ofs, blocks: 3520, first: 3517);
        var w = Work(); w.Index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        s.Ram[(int)Name.Raw] = (byte)'B';
        var file = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 1 };
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref file, false, true));
        var expected = new byte[400];
        new Random(391).NextBytes(expected);
        file.Buffer = Payload; file.Length = 100; w.BufferSmallAppends = true;
        s.Transfers.Clear();
        for (var offset = 0; offset < expected.Length; offset += 100)
        {
            expected.AsSpan(offset, 100).CopyTo(s.Ram.AsSpan((int)Payload.Raw));
            file.Position = (uint)offset;
            Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out var actual, true));
            Assert.Equal(100u, actual);
        }
        file.Position = 400; file.Length = 200;
        Assert.False(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _, true));
        Assert.Equal(DOS.Error.DiskFull, w.Error);
        Assert.Empty(s.Transfers);
        if (failData) s.FailWriteCall = s.WriteCalls + 1;
        var close = Work(); close.Index = w.Index;
        Assert.Equal(!failData, AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref close));
        if (failData)
        {
            Assert.False(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref close));
            Assert.True(AmigaDiskFileSystemCore.Header(ref p, g.RootBlock, Meta));
            Assert.Equal(0u, p.ReadUInt32(Meta, g.Tail + 312));
        }
        else InspectAllocation(s, g, file.Header, ofs, expected);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void OpenAndFirstWriteShareOneRootPublication(bool ofs, bool write)
    {
        var (s, p, g, _) = AllocationFixture(ofs, blocks: 20000);
        var w = Work(); w.Index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        s.Ram[(int)Name.Raw] = (byte)'Q';
        var file = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 1 };
        s.Transfers.Clear();
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref file, false, true));
        var expected = write ? new byte[1200] : Array.Empty<byte>();
        if (write)
        {
            new Random(19).NextBytes(expected);
            expected.CopyTo(s.Ram, (int)Payload.Raw);
            file.Buffer = Payload; file.Length = (uint)expected.Length;
            Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _, true));
        }
        Assert.True(AmigaFfsWriteCore.HasFileWriteGroup(ref p, w.Index));
        Assert.True(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
        Assert.Equal(2, s.Transfers.Count(t => t.Command is 3 or 25 && t.Offset == (ulong)g.RootBlock * g.BlockBytes));
        Assert.Single(s.Transfers, t => t.Command is 3 or 25 && t.Offset == (ulong)file.Header * g.BlockBytes);
        Assert.Equal(file.Header, AmigaDiskFileSystemCore.Resolve(ref p, g.RootBlock, Name, 1, w.DosType, Meta, out _));
        InspectAllocation(s, g, file.Header, ofs, expected);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void GroupedCreatePublishesNestedDirectoryLinkAfterFile(bool ofs, bool failParent)
    {
        var (s, p, g, _) = AllocationFixture(ofs, blocks: 20000);
        var w = Work(); w.Index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        s.Ram[(int)Name.Raw] = (byte)'D';
        var dir = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 1 };
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref dir, true));
        s.Ram[(int)Name.Raw] = (byte)'F';
        var file = new AmigaFfsWriteCore.Request { Directory = dir.Header, Name = Name, Length = 1 };
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref file, false, true));
        Assert.Equal(0u, AmigaDiskFileSystemCore.Resolve(ref p, dir.Header, Name, 1, w.DosType, Meta, out _));
        var expected = new byte[1200];
        new Random(32).NextBytes(expected);
        expected.CopyTo(s.Ram, (int)Payload.Raw);
        file.Buffer = Payload; file.Length = (uint)expected.Length;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _, true));
        if (failParent) s.FailWrite = (ulong)dir.Header * g.BlockBytes + g.StartByte;
        s.Transfers.Clear();
        Assert.Equal(!failParent, AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
        var writes = s.Transfers.Where(t => t.Command is 3 or 25).Select(t => (t.Offset - g.StartByte) / g.BlockBytes).ToArray();
        Assert.True(Array.IndexOf(writes, (ulong)file.Header) < Array.IndexOf(writes, (ulong)dir.Header));
        Assert.True(AmigaDiskFileSystemCore.Header(ref p, g.RootBlock, Meta));
        Assert.Equal(failParent ? 0u : uint.MaxValue, p.ReadUInt32(Meta, g.Tail + 312));
        Assert.Equal(failParent ? 0u : file.Header, AmigaDiskFileSystemCore.Resolve(ref p, dir.Header, Name, 1, w.DosType, Meta, out _));
        if (!failParent) InspectAllocation(s, g, file.Header, ofs, expected);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RemovingCrossLinkedFileFailsBeforeChangingEitherAllocation(bool aliasesHeader)
    {
        var (s, p, g, retained) = AllocationFixture(false, blocks: 20000);
        var w = Work(); w.Index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        s.Ram[(int)Name.Raw] = (byte)'Q';
        var other = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 1 };
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref other, false));
        other.Buffer = Payload; other.Length = 512;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref other, out _, true));
        retained.Buffer = Payload; retained.Length = 512;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref retained, out _, true));
        Assert.True(p.ReadBlock(retained.Header, Meta));
        var shared = aliasesHeader ? retained.Header : p.ReadUInt32(Meta, g.Tail + 308);
        Assert.True(p.ReadBlock(other.Header, Meta));
        p.WriteUInt32(Meta, 16, shared); p.WriteUInt32(Meta, g.Tail + 308, shared);
        Checksum(ref p, Meta, g.BlockBytes, 20);
        Assert.True(p.WriteBlock(other.Header, Meta));
        s.Transfers.Clear();
        Assert.False(AmigaFfsWriteCore.Remove(ref p, ref w, other.Header, false));
        Assert.Equal(DOS.Error.DiskNotValidated, w.Error);
        Assert.DoesNotContain(s.Transfers, t => t.Command is 3 or 25);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)]
    public void RemovingClosedFileAfterSiblingGrowthReusesItsVerifiedChain(bool ofs, bool collision)
    {
        var (s, p, g, donor) = AllocationFixture(ofs, blocks: 20000);
        var w = Work(); w.Index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        var bytes = new byte[60123];
        new Random(27).NextBytes(bytes);
        bytes.CopyTo(s.Ram, (int)Payload.Raw);
        w.GroupFileWrites = true; w.ReadsGroupedRoot = true;
        donor.Buffer = Payload; donor.Length = (uint)bytes.Length;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref donor, out _, true));
        Assert.True(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
        var data = InspectAllocation(s, g, donor.Header, ofs, bytes);
        Assert.True(p.ReadBlock(donor.Header, Meta));
        var extension = p.ReadUInt32(Meta, g.Tail + 504);
        Assert.NotEqual(0u, extension);
        // "Runs" and "P18" share the same 72-entry hash bucket.
        var name = collision ? "P18"u8.ToArray() : "Q"u8.ToArray();
        name.CopyTo(s.Ram, (int)Name.Raw);
        var sibling = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = (uint)name.Length };
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref sibling, false, true));
        sibling.Buffer = Payload; sibling.Length = (uint)bytes.Length;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref sibling, out _, true));
        Assert.True(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
        s.Transfers.Clear();
        Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, donor.Header, false));
        Assert.DoesNotContain(s.Transfers, t => t.Command is 2 or 24 &&
            (t.Offset == g.StartByte + (ulong)extension * g.BlockBytes ||
             t.Offset == g.StartByte + (ulong)data[10] * g.BlockBytes));
        Assert.True(p.ReadBlock(sibling.Header, Meta));
        var siblingTail = p.ReadUInt32(Meta, g.Tail + 504);
        Assert.Equal(0u, p.ReadUInt32(Meta, g.Tail + 496));
        s.Transfers.Clear();
        sibling.Position = sibling.Length; sibling.Length = 2000;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref sibling, out _, true));
        Assert.True(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
        if (!ofs) Assert.DoesNotContain(s.Transfers, t => t.Command is 2 or 24 && t.Offset == g.StartByte + (ulong)siblingTail * g.BlockBytes);
        Assert.True(p.ReadBlock(sibling.Header, Meta));
        Assert.Equal(0u, p.ReadUInt32(Meta, g.Tail + 496));
        InspectAllocation(s, g, sibling.Header, ofs, bytes.Concat(bytes.Take(2000)).ToArray());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RemovingAnotherFileKeepsDisjointAllocationProof(bool ofs)
    {
        var (s, p, g, retained) = AllocationFixture(ofs, blocks: 20000);
        var w = Work(); w.Index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        s.Ram[(int)Name.Raw] = (byte)'Q';
        var other = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 1 };
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref other, false));
        other.Buffer = Payload; other.Length = 501;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref other, out _, true));
        var original = new byte[120000];
        new Random(36).NextBytes(original); original.CopyTo(s.Ram, (int)Payload.Raw);
        retained.Buffer = Payload; retained.Length = (uint)original.Length;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref retained, out _, true));
        var keys = InspectAllocation(s, g, retained.Header, ofs, original);
        Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, other.Header, false));
        s.Transfers.Clear();
        retained.Position = retained.Length; retained.Length = 8;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref retained, out _, true));
        if (ofs) Assert.DoesNotContain(s.Transfers, t => t.Command is 2 or 24 && t.Offset == (ulong)keys[10] * g.BlockBytes);
        InspectAllocation(s, g, retained.Header, ofs, original.Concat(original.Take(8)).ToArray());
    }

    [Theory]
    [InlineData(512u)] [InlineData(1024u)]
    public void GroupedAlignedGrowthPublishesCompleteExtensionTables(uint bytes)
    {
        var (s, p, g, file) = AllocationFixture(false, bytes, blocks: 20000, first: 4020);
        var index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, index, 65536);
        var packet = (int)(g.TableEntries + 3) * (int)bytes;
        var expected = new byte[packet * 2];
        new Random(117).NextBytes(expected);
        s.BulkTransfers = true;
        var w = Work(); w.Index = index; w.GroupFileWrites = true;
        file.Buffer = Payload; file.Length = (uint)packet;
        for (var offset = 0; offset < expected.Length; offset += packet)
        {
            expected.AsSpan(offset, packet).CopyTo(s.Ram.AsSpan((int)Payload.Raw));
            file.Position = (uint)offset;
            Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out var written, true), w.Error.ToString());
            Assert.Equal(file.Length, written);
        }
        Assert.True(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
        InspectAllocation(s, g, file.Header, false, expected);
    }

    [Fact]
    public void ClosePublishesAdjacentExtensionsChildFirstAndRejectsPartialTransfer()
    {
        ulong parentOffset = ulong.MaxValue;
        foreach (var fail in new[] { false, true })
        {
            var (s, p, g, file) = AllocationFixture(false, blocks: 20000);
            var w = Work(); w.Index = APTR.FromPointer(0x100000);
            AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
            w.GroupFileWrites = true; w.ReadsGroupedRoot = true; s.BulkTransfers = true;
            var data = new byte[512 * 150]; new Random(509).NextBytes(data);
            data.CopyTo(s.Ram, (int)Payload.Raw);
            file.Buffer = Payload; file.Length = (uint)data.Length;
            Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _, true));
            s.Transfers.Clear();
            if (fail) s.FailWrite = parentOffset;
            Assert.Equal(!fail, AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
            var pair = Assert.Single(s.Transfers, t => t.Command is 3 or 25 && t.Bytes == 2 * g.BlockBytes);
            parentOffset = pair.Offset + g.BlockBytes;
            Assert.True(p.ReadBlock(g.RootBlock, Meta));
            Assert.Equal(fail ? 0u : uint.MaxValue, p.ReadUInt32(Meta, g.Tail + 312));
            if (fail)
            {
                Assert.False(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
                continue;
            }
            Assert.True(p.ReadBlock(file.Header, Meta));
            var parent = p.ReadUInt32(Meta, g.Tail + 504);
            Assert.Equal(parentOffset, g.StartByte + (ulong)parent * g.BlockBytes);
            Assert.True(p.ReadBlock(parent, Meta));
            Assert.Equal(parent - 1, p.ReadUInt32(Meta, g.Tail + 504));
            InspectAllocation(s, g, file.Header, false, data);
        }
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    [InlineData(true, false, 2540)] [InlineData(true, true, 2540)]
    public void GroupedPartialOverwriteSubmitsAdjacentSectorsTogether(bool ofs, bool fail, int lengthToPatch = 777)
    {
        var (s, p, g, file) = AllocationFixture(ofs, blocks: 20000);
        var w = Work();
        w.Index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        s.BulkTransfers = true;
        var expected = new byte[4096];
        new Random(602).NextBytes(expected);
        expected.CopyTo(s.Ram, (int)Payload.Raw);
        file.Buffer = Payload; file.Length = (uint)expected.Length;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _, true));
        var keys = InspectAllocation(s, g, file.Header, ofs, expected);
        Assert.Equal(keys[0] + 2, keys[2]);
        w.GroupFileWrites = true;
        file.Length = 1;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _, true));
        file.Position = 487; file.Length = (uint)lengthToPatch;
        Array.Fill(s.Ram, (byte)93, (int)Payload.Raw, lengthToPatch);
        s.Transfers.Clear();
        if (fail) s.FailWriteCall = s.WriteCalls + 1;
        Assert.Equal(!fail, AmigaFfsWriteCore.Write(ref p, ref w, ref file, out var written, true));
        if (fail)
        {
            Assert.Equal(0u, written);
            Assert.False(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
            Assert.True(AmigaDiskFileSystemCore.Header(ref p, g.RootBlock, Meta));
            Assert.Equal(0u, p.ReadUInt32(Meta, 312));
            return;
        }
        Assert.Equal(file.Length, written);
        var transfer = Assert.Single(s.Transfers, t => t.Command is 3 or 25);
        Assert.Equal(g.StartByte + keys[0] * (ulong)g.BlockBytes, transfer.Offset);
        var payload = ofs ? g.BlockBytes - 24 : g.BlockBytes;
        Assert.Equal(((487 + file.Length + payload - 1) / payload) * g.BlockBytes, transfer.Bytes);
        Array.Fill(expected, (byte)93, 487, lengthToPatch);
        if (ofs)
        {
            // Vary both edges of the checksum delta, including the threshold
            // for a complete checksum and a patch that crosses a sector.
            for (var alignment = 0; alignment < 4; alignment++)
            foreach (var length in new[] { 1, 3, 4, 63, 64, 65 })
            {
                var start = 2 * 488 - 5 + alignment;
                file.Position = (uint)start; file.Length = (uint)length;
                new Random(17 * length + alignment).NextBytes(s.Ram.AsSpan((int)Payload.Raw, length));
                s.Ram.AsSpan((int)Payload.Raw, length).CopyTo(expected.AsSpan(start));
                Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out written, true));
                Assert.Equal(file.Length, written);
            }
        }
        Assert.True(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
        InspectAllocation(s, g, file.Header, ofs, expected);
    }

    [Fact]
    public void FragmentedOverwriteWithAdjacentEndpointsPreservesLogicalOrder()
    {
        var (s, p, g, file) = AllocationFixture(false, blocks: 20000);
        var w = Work(); w.Index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        var expected = new byte[4 * 512];
        file.Buffer = Payload; file.Length = (uint)expected.Length;
        Array.Clear(s.Ram, (int)Payload.Raw, expected.Length);
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _, true));
        var keys = InspectAllocation(s, g, file.Header, false, expected);
        Assert.Equal(keys[0] + 3, keys[3]);
        Assert.True(p.ReadBlock(file.Header, Meta));
        p.WriteUInt32(Meta, g.Tail + 304, keys[2]);
        p.WriteUInt32(Meta, g.Tail + 300, keys[1]);
        Checksum(ref p, Meta, g.BlockBytes, 20);
        Assert.True(p.WriteBlock(file.Header, Meta));
        AmigaFfsMountIndex.Forget(ref p, w.Index);
        w.GroupFileWrites = true;
        file.Length = 1;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _, true));
        new Random(701).NextBytes(expected);
        expected.CopyTo(s.Ram, (int)Payload.Raw);
        file.Length = (uint)expected.Length;
        s.BulkTransfers = true; s.Transfers.Clear();
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _, true));
        Assert.DoesNotContain(s.Transfers, t => t.Command is 3 or 25 && t.Bytes == 4 * 512);
        Assert.True(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
        InspectAllocation(s, g, file.Header, false, expected);
    }

    [Fact]
    public void CachedOfsPayloadExpiresBeforeGroupOverwrite()
    {
        var (s, p, _, file) = AllocationFixture(true, blocks: 20000);
        var w = Work(); w.Index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        file.Buffer = Payload; file.Length = 600;
        var output = APTR.FromPointer(0x70000);
        Array.Fill(s.Ram, (byte)42, (int)Payload.Raw, 600);
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _, true));
        Assert.Equal(256, AmigaDiskFileSystemCore.Read(ref p, file.Header, 0x444F5300, 0, output, 256, Meta, Data, out _, w.Index));
        s.Transfers.Clear();
        Assert.Equal(42, AmigaDiskFileSystemCore.Read(ref p, file.Header, 0x444F5300, 400, output, 42, Meta, Data, out _, w.Index));
        Assert.Empty(s.Transfers);
        Assert.Equal(42, s.Ram[(int)output.Raw]);
        Array.Fill(s.Ram, (byte)71, (int)Payload.Raw, 600);
        w.GroupFileWrites = true;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _, true));
        Assert.True(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
        Assert.Equal(42, AmigaDiskFileSystemCore.Read(ref p, file.Header, 0x444F5300, 400, output, 42, Meta, Data, out _, w.Index));
        Assert.Equal(71, s.Ram[(int)output.Raw]);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void GroupedAppendsLeaveRootInvalidUntilHandoff(bool ofs)
    {
        var (s, p, g, file) = AllocationFixture(ofs, blocks: 20000, first: 4020);
        var index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, index, 65536);
        var expected = new byte[38 * 1021];
        new Random(43).NextBytes(expected);
        s.BulkTransfers = true; s.Transfers.Clear();
        file.Buffer = Payload; file.Length = 1021;
        var w = Work();
        for (var offset = 0; offset < expected.Length; offset += 1021)
        {
            w = Work(); w.Index = index; w.GroupFileWrites = true;
            file.Position = (uint)offset;
            Array.Copy(expected, offset, s.Ram, (int)Payload.Raw, 1021);
            Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out var written, true), w.Error.ToString());
            Assert.Equal(1021u, written);
            Assert.True(AmigaFfsWriteCore.HasFileWriteGroup(ref p, index));
        }
        Assert.True(AmigaDiskFileSystemCore.Header(ref p, g.RootBlock, Meta));
        Assert.Equal(0u, p.ReadUInt32(Meta, 312));
        Assert.DoesNotContain(s.Transfers, t => t.Command is 3 or 25 && t.Offset == (ulong)file.Header * g.BlockBytes);
        // Packet dispatch supplies a fresh work record for the closing packet.
        w = Work(); w.Index = index;
        Assert.True(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
        Assert.Single(s.Transfers, t => t.Command is 3 or 25 && t.Offset == (ulong)file.Header * g.BlockBytes);
        Assert.Equal(2, s.Transfers.Count(t => t.Command is 3 or 25 && t.Offset == (ulong)g.RootBlock * g.BlockBytes));
        InspectAllocation(s, g, file.Header, ofs, expected);
    }

    [Theory]
    [InlineData(false, 1)] [InlineData(false, 2)] [InlineData(false, 3)]
    [InlineData(true, 1)] [InlineData(true, 2)] [InlineData(true, 3)]
    public void FailedGroupPublicationLeavesInvalidRoot(bool ofs, int failedWrite)
    {
        var (s, p, g, file) = AllocationFixture(ofs, blocks: 20000);
        var w = Work();
        w.Index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        file.Buffer = Payload; file.Length = 1024; w.GroupFileWrites = true;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _, true));
        s.FailWriteCall = s.WriteCalls + failedWrite;
        Assert.False(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
        Assert.Equal(DOS.Error.DiskNotValidated, w.Error);
        Assert.False(AmigaFfsWriteCore.HasFileWriteGroup(ref p, w.Index));
        Assert.False(AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref w));
        Assert.True(AmigaDiskFileSystemCore.Header(ref p, g.RootBlock, Meta));
        Assert.Equal(0u, p.ReadUInt32(Meta, 312));
    }

    [Theory]
    [InlineData(0)] [InlineData(2)] [InlineData(3)]
    public void ConsecutiveFixedDiskOverwritesShareOnlyTheRootValidityWrites(int fail)
    {
        var (s, p, g, file) = AllocationFixture(false, blocks: 20000);
        var work = Work();
        work.Index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, work.Index, 65536);
        s.BulkTransfers = true;
        file.Buffer = Payload;
        file.Length = 4 * 512;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref work, ref file, out _, true));
        s.Transfers.Clear(); s.WriteCalls = 0;
        work.GroupFileWrites = true;
        file.Length = 512;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref work, ref file, out _, true));
        Assert.True(AmigaFfsWriteCore.HasFileWriteGroup(ref p, work.Index));
        Assert.True(AmigaDiskFileSystemCore.Header(ref p, g.RootBlock, Meta));
        Assert.Equal(0u, p.ReadUInt32(Meta, 312));
        s.FailWriteCall = fail == 0 ? 0 : fail + 1;
        file.Position = 512;
        var success = AmigaFfsWriteCore.Write(ref p, ref work, ref file, out var written, true);
        Assert.Equal(fail != 2, success);
        Assert.Equal(success ? 512u : 0u, written);
        if (success) Assert.Equal(fail == 0, AmigaFfsWriteCore.FinishFileWriteGroup(ref p, ref work));
        Assert.False(AmigaFfsWriteCore.HasFileWriteGroup(ref p, work.Index));
        Assert.True(AmigaDiskFileSystemCore.Header(ref p, g.RootBlock, Meta));
        Assert.Equal(fail == 0 ? uint.MaxValue : 0u, p.ReadUInt32(Meta, 312));
        if (fail == 0) Assert.Equal(4, s.WriteCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void IndexedCreatePublishesRootEntryAfterBitmap(bool ofs)
    {
        var (s, p, g, _) = AllocationFixture(ofs, blocks: 20000);
        var work = Work();
        work.Index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, work.Index, 65536);
        s.Ram[(int)Name.Raw] = (byte)'Q';
        var request = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 1 };
        s.Transfers.Clear();
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref work, ref request, false));
        var writes = s.Transfers.Where(t => t.Command is 3 or 25).Select(t => t.Offset / g.BlockBytes).ToArray();
        Assert.Equal(new ulong[] { g.RootBlock, request.Header, g.RootBlock + 1, g.RootBlock }, writes);
        Assert.Equal(request.Header, AmigaDiskFileSystemCore.Resolve(ref p, g.RootBlock, Name, 1, work.DosType, Meta, out _));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FirstTableGrowthPublishesSizeWithFinalPointers(bool ofs)
    {
        var (s, p, g, file) = AllocationFixture(ofs, blocks: 20000);
        var work = Work();
        work.Index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, work.Index, 65536);
        s.BulkTransfers = true;
        file.Buffer = Payload;
        file.Length = ofs ? 3 * 488u : 3 * 512u;
        s.Transfers.Clear();
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref work, ref file, out var written, true));
        Assert.Equal(file.Length, written);
        Assert.Single(s.Transfers, t => t.Command is 3 or 25 && t.Offset == (ulong)file.Header * g.BlockBytes);
        Assert.True(AmigaDiskFileSystemCore.Header(ref p, file.Header, Meta));
        Assert.Equal(written, p.ReadUInt32(Meta, g.Tail + 324));
    }

    [Theory]
    [InlineData(false, 64)] [InlineData(true, 64)]
    [InlineData(false, 256)] [InlineData(true, 256)]
    [InlineData(false, 1024)] [InlineData(true, 1024)]
    public void MountIndexMatchesIndependentAllocationAcrossPacketBoundaries(bool ofs, int kib)
    {
        var (s, p, g, file) = AllocationFixture(ofs, blocks: 20000, first: 4020);
        s.FfsIndex = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, s.FfsIndex, (uint)kib * 1024);
        s.BulkTransfers = true;
        var expected = new byte[112879];
        new Random(31).NextBytes(expected);
        uint position = 0;
        foreach (var length in new[] { 1, 487, 1763, 40333, 61455, 8840 })
        {
            Array.Copy(expected, position, s.Ram, Payload.Raw, length);
            file.Position = position; file.Length = (uint)length; file.Buffer = Payload;
            var w = Work(); w.Index = s.FfsIndex;
            Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out var written, true), w.Error.ToString());
            position += written;
            CompareIndexCount(ref p, s);
        }
        Assert.Equal((uint)expected.Length, position);
        var patch = new byte[8192]; new Random(91).NextBytes(patch);
        for (int i = 0; i < 12; i++)
        {
            var at = i * patch.Length;
            patch.CopyTo(expected, at); patch.CopyTo(s.Ram, (int)Payload.Raw);
            file.Position = (uint)at; file.Length = (uint)patch.Length;
            var w = Work(); w.Index = s.FfsIndex;
            Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _, true), w.Error.ToString());
        }
        InspectAllocation(s, g, file.Header, ofs, expected);
        var output = APTR.FromPointer(0x70000);
        Assert.Equal(expected.Length, AmigaDiskFileSystemCore.Read(ref p, file.Header, ofs ? 0x444F5300u : 0x444F5301u,
            0, output, (uint)expected.Length, Meta, Data, out var error, s.FfsIndex));
        Assert.Equal(DOS.Error.None, error);
        Assert.Equal(expected, s.Ram.AsSpan((int)output.Raw, expected.Length).ToArray());
        var work = Work(); work.Index = s.FfsIndex;
        Assert.True(AmigaFfsWriteCore.Remove(ref p, ref work, file.Header, true), work.Error.ToString());
        CompareIndexCount(ref p, s);
        InspectAllocation(s, g, file.Header, ofs, []);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void IndexedGrowthFailureNeverPublishesCountOrMissingData(bool ofs)
    {
        var (s, p, g, file) = AllocationFixture(ofs, blocks: 20000, first: 4020);
        var index = APTR.FromPointer(0x100000);
        var prefix = Enumerable.Repeat((byte)0x59, 900).ToArray();
        prefix.CopyTo(s.Ram, (int)Payload.Raw);
        var w = Work(); w.Index = index;
        AmigaFfsMountIndex.Initialize(ref p, index, 65536);
        file.Buffer = Payload; file.Length = (uint)prefix.Length;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _, true));
        var before = s.Sectors.ToDictionary(x => x.Key, x => x.Value.ToArray());
        var ram = s.Ram.ToArray();
        file.Position = file.Length; file.Length = 41000;
        s.WriteCalls = 0; s.BulkTransfers = true;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _, true));
        for (var fail = 1; fail <= s.WriteCalls; fail++)
        {
            var broken = new Storage { BulkTransfers = true, FailWriteCall = fail };
            ram.CopyTo(broken.Ram, 0);
            foreach (var sector in before) broken.Sectors[sector.Key] = sector.Value.ToArray();
            var disk = new Memory(broken, g);
            w = Work(); w.Index = index;
            Assert.False(AmigaFfsWriteCore.Write(ref disk, ref w, ref file, out _, true));
            Assert.Equal(DOS.Error.DiskNotValidated, w.Error);
            Assert.True(AmigaDiskFileSystemCore.Header(ref disk, g.RootBlock, Meta));
            Assert.Equal(fail == 1 ? uint.MaxValue : 0u, disk.ReadUInt32(Meta, g.Tail + 312));
            broken.Transfers.Clear();
            AmigaDiskFileSystemCore.CountUsed(ref disk, Meta, Bitmap, out _, index);
            Assert.NotEmpty(broken.Transfers);
            Assert.True(AmigaFfsWriteCore.Write(ref disk, ref w, ref file, out _, true) == (fail == 1));
        }
    }

    [Theory]
    [InlineData(64)] [InlineData(1024)]
    public void SmallWorkspaceFallsBackWhenLayoutOrChainExceedsCapacity(int bytes)
    {
        var (s, p, g, file) = AllocationFixture(true, blocks: 20000);
        s.FfsIndex = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, s.FfsIndex, (uint)bytes);
        var payload = new byte[60111]; new Random(53).NextBytes(payload);
        payload.CopyTo(s.Ram, (int)Payload.Raw);
        var w = Work(); w.Index = s.FfsIndex;
        file.Buffer = Payload; file.Length = (uint)payload.Length;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _, true), w.Error.ToString());
        InspectAllocation(s, g, file.Header, true, payload);
        Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, file.Header, true));
        InspectAllocation(s, g, file.Header, true, []);
    }

    [Fact]
    public void ManualTrimRechecksAliasedBitmapEvenWithAWarmIndex()
    {
        var (s, p, g, file) = AllocationFixture(false, blocks: 20000);
        var index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, index, 65536);
        var w = Work(); w.Index = index;
        file.Buffer = Payload; file.Length = 512;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _, true));
        Assert.True(p.ReadBlock(g.RootBlock, Meta));
        p.WriteUInt32(Meta, g.Tail + 320, p.ReadUInt32(Meta, g.Tail + 316));
        Checksum(ref p, Meta, g.BlockBytes, 20);
        Assert.True(p.WriteBlock(g.RootBlock, Meta));
        Assert.False(AmigaFfsWriteCore.Trim(ref p, ref w, APTR.Null, 0, out _));
        Assert.Equal(DOS.Error.DiskNotValidated, w.Error);
        Assert.Empty(s.Discards);
    }

    [Theory]
    [InlineData(2u)] [InlineData(3u)] [InlineData(4u)] [InlineData(5u)]
    public void IndexedClassicVariantsPersistWritesAndRejectManualDiscard(uint dos)
    {
        var s = new Storage();
        Assert.True(AmigaVolumeGeometry.Create(12000, 512, 2, out var g));
        var p = new Memory(s, g);
        var type = 0x444F5300u + dos;
        VariantFormat(ref p, s, type);
        var work = Work(); work.Index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, work.Index, 65536);
        s.Ram[(int)Name.Raw] = (byte)'V';
        var file = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 1 };
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref work, ref file, false), work.Error.ToString());
        var expected = new byte[16385]; new Random(72).NextBytes(expected);
        expected.CopyTo(s.Ram, (int)Payload.Raw);
        file.Buffer = Payload; file.Length = (uint)expected.Length;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref work, ref file, out _, true), work.Error.ToString());
        var target = APTR.FromPointer(0x70000);
        Assert.Equal(expected.Length, AmigaDiskFileSystemCore.Read(ref p, file.Header, type, 0, target,
            file.Length, Meta, Data, out var error));
        Assert.Equal(DOS.Error.None, error);
        Assert.Equal(expected, s.Ram.AsSpan((int)target.Raw, expected.Length).ToArray());
        Assert.False(AmigaFfsWriteCore.Trim(ref p, ref work, APTR.Null, 0, out _));
        Assert.Equal(DOS.Error.ActionNotKnown, work.Error); Assert.Empty(s.Discards);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OriginalOfsHeaderMustNameACheckedRunHead(bool invalidHead)
    {
        var (s, p, g, file) = AllocationFixture(true);
        var payload = new byte[4 * 488]; new Random(7).NextBytes(payload);
        OfsWrite(ref p, s, ref file, 0, payload);
        var keys = InspectAllocation(s, g, file.Header, true, payload);
        Assert.True(p.ReadBlock(keys[1], Meta));
        p.WriteUInt32(Meta, 16, 0);
        Checksum(ref p, Meta, 512, 20); Assert.True(p.WriteBlock(keys[1], Meta));
        for (var i = 2; i < 4; i++)
        {
            Assert.True(p.ReadBlock(keys[i], Meta)); p.WriteUInt32(Meta, 8, (uint)i - 1);
            Checksum(ref p, Meta, 512, 20); Assert.True(p.WriteBlock(keys[i], Meta));
        }
        Assert.True(p.ReadBlock(file.Header, Meta)); p.WriteUInt32(Meta, 16, keys[invalidHead ? 3 : 2]);
        Checksum(ref p, Meta, 512, 20); Assert.True(p.WriteBlock(file.Header, Meta));
        var w = Work(); w.Index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        file.Position = (uint)payload.Length; file.Length = 17; file.Buffer = Payload;
        s.Transfers.Clear();
        Assert.Equal(!invalidHead, AmigaFfsWriteCore.Write(ref p, ref w, ref file, out var actual, true));
        Assert.Equal(invalidHead ? 0u : 17u, actual);
        if (invalidHead)
        {
            Assert.Equal(DOS.Error.DiskNotValidated, w.Error);
            Assert.DoesNotContain(s.Transfers, t => t.Command is 3 or 25);
        }
    }

    [Fact]
    public void OfsAppendRejectsACorruptSectorInsideABulkPreflightRead()
    {
        var (s, p, g, file) = AllocationFixture(true);
        var payload = new byte[4 * 488];
        OfsWrite(ref p, s, ref file, 0, payload);
        var keys = InspectAllocation(s, g, file.Header, true, payload);
        Assert.Equal(keys[0] + 1, keys[1]);
        s.Sectors[g.StartByte + keys[1] * (ulong)g.BlockBytes][24] ^= 1;
        var w = Work();
        w.Index = APTR.FromPointer(0x100000);
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        s.BulkTransfers = true;
        s.Transfers.Clear();
        file.Position = (uint)payload.Length;
        file.Length = 1;
        file.Buffer = Payload;
        Assert.False(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out var actual, true));
        Assert.Equal(0u, actual);
        Assert.Equal(DOS.Error.DiskNotValidated, w.Error);
        Assert.Contains(s.Transfers, t => t.Command is 2 or 24 &&
            t.Offset == g.StartByte + keys[0] * (ulong)g.BlockBytes && t.Bytes >= 2 * g.BlockBytes);
        Assert.DoesNotContain(s.Transfers, t => t.Command is 3 or 25);
    }

    private static void CompareIndexCount(ref Memory p, Storage s)
    {
        var index = s.FfsIndex;
        s.Transfers.Clear();
        Assert.True(AmigaDiskFileSystemCore.CountUsed(ref p, Meta, Bitmap, out var cached, index));
        Assert.Empty(s.Transfers);
        Assert.True(AmigaDiskFileSystemCore.CountUsed(ref p, Meta, Bitmap, out var actual));
        Assert.Equal(actual, cached);
    }
}
