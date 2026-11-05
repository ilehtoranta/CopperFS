using System.Buffers.Binary;
using Amiga;
using CopperSFS;
using CopperStart.Dos;

namespace CopperStart.FileSystem.Tests;

public sealed partial class VolumeGeometryTests
{
    private static (Storage Storage, Memory Platform, AmigaVolumeGeometry Geometry, AmigaFfsWriteCore.Request File)
        AllocationFixture(bool ofs, uint bytes = 512, uint blocks = 3520, uint track = 1, uint first = 100)
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(blocks, bytes, 2, out var g));
        g.TrackBlocks = track;
        var p = new Memory(storage, g); Format(ref p, g, first);
        SetDosType(ref p, ofs ? 0x444F5300u : 0x444F5301u);
        var file = OfsFile(ref p, storage, g, "Runs");
        Assert.Equal(first, file.Header);
        return (storage, p, g, file);
    }

    private static void AllocationBit(ref Memory p, AmigaVolumeGeometry g, uint key, bool free)
    {
        var bit = key - g.ReservedBlocks;
        var map = g.RootBlock + 1 + bit / g.BitmapBits;
        Assert.True(p.ReadBlock(map, Bitmap));
        var at = 4 + (int)(bit % g.BitmapBits / 32) * 4; var mask = 1u << (int)(bit % 32);
        var value = p.ReadUInt32(Bitmap, at);
        p.WriteUInt32(Bitmap, at, free ? value | mask : value & ~mask);
        Checksum(ref p, Bitmap, g.BlockBytes, 0); Assert.True(p.WriteBlock(map, Bitmap));
    }

    // Inspect persisted bytes without production traversal or allocation helpers.
    private static uint[] InspectAllocation(Storage storage, AmigaVolumeGeometry g, uint header, bool ofs, byte[] expected)
    {
        uint Word(byte[] b, int at) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
        byte[] Block(uint key, bool checksum)
        {
            Assert.InRange(key, g.ReservedBlocks, g.BlockCount - 1);
            var b = storage.Sectors[g.StartByte + key * (ulong)g.BlockBytes];
            if (checksum)
            {
                uint sum = 0; for (var at = 0; at < b.Length; at += 4) sum = unchecked(sum + Word(b, at));
                Assert.Equal(0u, sum);
            }
            return b;
        }
        var root = Block(g.RootBlock, true); Assert.Equal(uint.MaxValue, Word(root, g.Tail + 312));
        var file = Block(header, true); Assert.Equal((uint)expected.Length, Word(file, g.Tail + 324));
        var owned = new HashSet<uint>(); var dataKeys = new List<uint>();
        var table = header; var copied = 0; var nextData = Word(file, 16);
        var payload = (int)g.BlockBytes - (ofs ? 24 : 0);
        while (table != 0)
        {
            Assert.True(owned.Add(table)); var meta = Block(table, true);
            Assert.Equal(table == header ? 2u : 16u, Word(meta, 0)); Assert.Equal(table, Word(meta, 4));
            Assert.Equal(0xFFFFFFFDu, Word(meta, g.Tail + 508));
            if (table != header) Assert.Equal(header, Word(meta, g.Tail + 500));
            var count = Math.Min(g.TableEntries, (uint)((expected.Length - copied + payload - 1) / payload));
            Assert.Equal(count, Word(meta, 8));
            for (var i = 0u; i < count; i++)
            {
                var key = Word(meta, g.Tail + 308 - (int)i * 4);
                Assert.True(owned.Add(key)); var data = Block(key, ofs); dataKeys.Add(key);
                var length = Math.Min(payload, expected.Length - copied);
                if (ofs)
                {
                    Assert.Equal(nextData, key); Assert.Equal(8u, Word(data, 0)); Assert.Equal(header, Word(data, 4));
                    Assert.Equal((uint)dataKeys.Count, Word(data, 8)); Assert.Equal((uint)length, Word(data, 12));
                    nextData = Word(data, 16);
                }
                Assert.Equal(expected.AsSpan(copied, length).ToArray(), data.AsSpan(ofs ? 24 : 0, length).ToArray());
                copied += length;
            }
            table = Word(meta, g.Tail + 504);
        }
        Assert.Equal(expected.Length, copied); if (ofs) Assert.Equal(0u, nextData);
        foreach (var key in owned)
        {
            var bit = key - g.ReservedBlocks;
            var map = Block(g.RootBlock + 1 + bit / g.BitmapBits, true);
            Assert.Equal(0u, Word(map, 4 + (int)(bit % g.BitmapBits / 32) * 4) & (1u << (int)(bit % 32)));
        }
        return dataKeys.ToArray();
    }

    [Theory]
    [InlineData(false, 512u, 11u)] [InlineData(true, 512u, 11u)]
    [InlineData(false, 512u, 22u)] [InlineData(true, 512u, 22u)]
    [InlineData(false, 1024u, 1u)] [InlineData(true, 1024u, 1u)]
    [InlineData(false, 4096u, 0u)] [InlineData(true, 4096u, 0u)]
    public void DataRunsContinueAcrossRequestsTracksAndPartialTailGrowth(bool ofs, uint bytes, uint track)
    {
        var (s, p, g, file) = AllocationFixture(ofs, bytes, track: track, first: 8);
        var payload = (int)bytes - (ofs ? 24 : 0);
        var expected = Enumerable.Range(0, payload * 3 - 7).Select(i => (byte)(i * 17)).ToArray();
        OfsWrite(ref p, s, ref file, 0, expected);
        AllocationBit(ref p, g, 2, true); AllocationBit(ref p, g, 3, true);
        var before = Used(ref p);
        var patch = Enumerable.Range(0, payload * 5 + 13).Select(i => (byte)(i * 31 + 5)).ToArray();
        Array.Resize(ref expected, payload * 6 + 8); patch.CopyTo(expected, payload - 5);
        OfsWrite(ref p, s, ref file, (uint)(payload - 5), patch);
        for (var append = 0; append < 3; append++)
        {
            OfsWrite(ref p, s, ref file, (uint)expected.Length, [0x91, 0x27]);
            expected = [.. expected, 0x91, 0x27];
        }
        Assert.Equal(Enumerable.Range(9, 7).Select(i => (uint)i), InspectAllocation(s, g, file.Header, ofs, expected));
        Assert.Equal(before + 4, Used(ref p)); // No speculative reservation or extra metadata.
        var readback = new Memory(s, g);
        Assert.Equal(expected.Length, AmigaDiskFileSystemCore.Read(ref readback, file.Header,
            ofs ? 0x444F5300u : 0x444F5301u, 0, Output, (uint)expected.Length, Meta, Data, out var error));
        Assert.Equal(DOS.Error.None, error); Assert.Equal(expected, s.Ram.AsSpan((int)Output.Raw, expected.Length).ToArray());
    }

    [Theory]
    [InlineData(false, 1u, 4)] [InlineData(true, 1u, 4)]
    [InlineData(false, 11u, 4)] [InlineData(true, 22u, 4)]
    [InlineData(false, 1u, 5)] [InlineData(true, 1u, 5)]
    public void BlockedContinuationChoosesAFullRunOrTheEarliestLongestShortRun(bool ofs, uint track, int blocks)
    {
        var (s, p, g, file) = AllocationFixture(ofs, track: track);
        var payload = 512 - (ofs ? 24 : 0);
        OfsWrite(ref p, s, ref file, 0, new byte[payload]);
        foreach (var key in new uint[] { 102, 105, 110, 115 }) AllocationBit(ref p, g, key, false);
        if (blocks == 5)
            for (var key = 116u; key < 102 + g.TableEntries; key++) AllocationBit(ref p, g, key, false);
        AllocationBit(ref p, g, 2, true);
        OfsWrite(ref p, s, ref file, (uint)payload, new byte[payload * blocks]);
        var keys = InspectAllocation(s, g, file.Header, ofs, new byte[payload * (blocks + 1)]);
        Assert.Equal(new uint[] { 101, 106, 107, 108, 109 }, keys.Take(5));
        if (blocks == 5) Assert.Equal(111u, keys[5]);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AdjacentShortPrefixWinsOverALongerLocalRun(bool ofs)
    {
        var (s, p, g, file) = AllocationFixture(ofs);
        var payload = 512 - (ofs ? 24 : 0);
        OfsWrite(ref p, s, ref file, 0, new byte[payload]);
        AllocationBit(ref p, g, 103, false);
        OfsWrite(ref p, s, ref file, (uint)payload, new byte[payload * 3]);
        Assert.Equal(new uint[] { 101, 102, 104, 105 }, InspectAllocation(s, g, file.Header, ofs, new byte[payload * 4]));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OptionalRunSearchDoesNotReadTheNextBitmapPage(bool ofs)
    {
        const uint boundary = 4066;
        var (s, p, g, file) = AllocationFixture(ofs, blocks: 12000, first: boundary - 3);
        var payload = 512 - (ofs ? 24 : 0);
        OfsWrite(ref p, s, ref file, 0, new byte[payload]);
        AllocationBit(ref p, g, boundary - 1, false); AllocationBit(ref p, g, 2, true);
        s.Transfers.Clear();
        OfsWrite(ref p, s, ref file, (uint)payload, new byte[payload]);
        var invalidation = s.Transfers.FindIndex(t => t.Command == 3 && t.Offset == g.RootBlock * 512UL);
        Assert.True(invalidation >= 0);
        Assert.DoesNotContain(s.Transfers.Skip(invalidation), t => t.Command == 2 && t.Offset == (g.RootBlock + 2) * 512UL);
        Assert.Equal(new uint[] { boundary - 2, 2 }, InspectAllocation(s, g, file.Header, ofs, new byte[payload * 2]));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EndOfVolumeAndFreeBitmapPaddingFallBackToRealEarlierBlocks(bool ofs)
    {
        var (s, p, g, file) = AllocationFixture(ofs, blocks: 10003, first: 9999);
        var payload = 512 - (ofs ? 24 : 0);
        OfsWrite(ref p, s, ref file, 0, new byte[payload]);
        for (var key = 2u; key < 8; key++) AllocationBit(ref p, g, key, true);
        // Deliberately free all padding bits in the last bitmap page.
        for (var key = g.BlockCount; key < g.ReservedBlocks + 3 * g.BitmapBits; key++) AllocationBit(ref p, g, key, true);
        OfsWrite(ref p, s, ref file, (uint)payload, new byte[payload * 5]);
        Assert.Equal(new uint[] { 10000, 10001, 10002, 2, 3, 4 },
            InspectAllocation(s, g, file.Header, ofs, new byte[payload * 6]));
    }

    [Theory]
    [InlineData(false, 11u, 107u)] [InlineData(true, 11u, 107u)]
    [InlineData(false, 22u, 129u)] [InlineData(true, 22u, 129u)]
    [InlineData(false, 1u, 100u)] [InlineData(true, 0u, 100u)]
    public void OptionalLocalSearchStopsAtItsTrackOrUnknownGeometryWindow(bool ofs, uint track, uint first)
    {
        var (s, p, g, file) = AllocationFixture(ofs, track: track, first: first);
        var payload = 512 - (ofs ? 24 : 0);
        OfsWrite(ref p, s, ref file, 0, new byte[payload]);
        var preferred = first + 2;
        var window = track > 1 ? track - preferred % track : g.TableEntries;
        for (var key = preferred; key < preferred + window; key++) AllocationBit(ref p, g, key, false);
        AllocationBit(ref p, g, 2, true); AllocationBit(ref p, g, 3, true);
        OfsWrite(ref p, s, ref file, (uint)payload, new byte[payload * 2]);
        Assert.Equal(new uint[] { first + 1, 2, 3 }, InspectAllocation(s, g, file.Header, ofs, new byte[payload * 3]));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FragmentedSpaceAndMetadataCanUseEarlierHolesAfterHighDataPlacement(bool ofs)
    {
        var (s, p, g, file) = AllocationFixture(ofs, blocks: 1760, track: 11, first: 1678);
        var payload = 512 - (ofs ? 24 : 0);
        var expected = new byte[payload * 72]; OfsWrite(ref p, s, ref file, 0, expected);
        // Tail is 1750. Force fallback across the volume end, including a new table.
        for (var key = 1751u; key < g.BlockCount; key++) AllocationBit(ref p, g, key, false);
        foreach (var key in new uint[] { 2, 4, 6, 8 }) AllocationBit(ref p, g, key, true);
        OfsWrite(ref p, s, ref file, (uint)expected.Length, new byte[payload * 3]);
        Array.Resize(ref expected, payload * 75);
        var keys = InspectAllocation(s, g, file.Header, ofs, expected);
        Assert.Equal(new uint[] { 4, 6, 8 }, keys.Skip(72)); // Block 2 belongs to the extension table.
        var work = Work(); Assert.True(AmigaFfsWriteCore.Remove(ref p, ref work, file.Header, false));
    }

    [Theory]
    [InlineData(false, 512u)] [InlineData(true, 512u)]
    [InlineData(false, 1024u)] [InlineData(true, 4096u)]
    public void DataRunsCrossBitmapPagesAndPointerTablesWithoutReservingExtraBlocks(bool ofs, uint bytes)
    {
        var boundary = (bytes - 4) * 8 + 2;
        var (s, p, g, file) = AllocationFixture(ofs, bytes, blocks: boundary + 2000, first: boundary - 3);
        var baseline = Used(ref p); var payload = (int)bytes - (ofs ? 24 : 0);
        var count = (int)g.TableEntries + 3;
        var expected = Enumerable.Range(0, payload * count).Select(i => (byte)(i * 7)).ToArray();
        for (var offset = 0; offset < expected.Length; offset += 128 * 1024)
            OfsWrite(ref p, s, ref file, (uint)offset, expected.AsSpan(offset, Math.Min(128 * 1024, expected.Length - offset)).ToArray());
        var keys = InspectAllocation(s, g, file.Header, ofs, expected);
        Assert.Equal(boundary, keys[2]);
        Assert.Equal(baseline + (uint)count + 1, Used(ref p));
        var work = Work(); Assert.True(AmigaFfsWriteCore.Remove(ref p, ref work, file.Header, false));
        Assert.Equal(baseline - 1, Used(ref p));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EveryFailedRunGrowthWritePreservesTheExistingInvalidationContract(bool ofs)
    {
        int Exercise(int fail)
        {
            var (s, p, g, file) = AllocationFixture(ofs, blocks: 1760, track: 11);
            var payload = 512 - (ofs ? 24 : 0);
            OfsWrite(ref p, s, ref file, 0, new byte[payload * 72 - 3]);
            AllocationBit(ref p, g, 2, true);
            s.WriteCalls = 0; s.FailWriteCall = fail;
            file.Position = (uint)(payload * 72 - 5); file.Length = (uint)(payload * 3 + 9); file.Buffer = Payload;
            s.Ram.AsSpan((int)Payload.Raw, (int)file.Length).Fill(0x5A);
            var work = Work(); var okay = AmigaFfsWriteCore.Write(ref p, ref work, ref file, out var actual);
            var calls = s.WriteCalls;
            if (fail == 0)
            {
                Assert.True(okay); Assert.Equal(file.Length, actual);
                var expected = new byte[file.Position + file.Length];
                expected.AsSpan((int)file.Position).Fill(0x5A); InspectAllocation(s, g, file.Header, ofs, expected);
            }
            else
            {
                Assert.False(okay); Assert.Equal(DOS.Error.DiskNotValidated, work.Error);
                Assert.True(actual <= file.Length); s.FailWriteCall = 0;
                Assert.True(p.ReadBlock(g.RootBlock, Meta));
                Assert.Equal(fail == 1 ? uint.MaxValue : 0u, p.ReadUInt32(Meta, g.Tail + 312));
                if (fail > 1)
                {
                    Assert.False(AmigaFfsWriteCore.Trim(ref p, ref work, APTR.Null, 0, out _)); Assert.Empty(s.Discards);
                }
            }
            return calls;
        }
        var calls = Exercise(0); for (var fail = 1; fail <= calls; fail++) Exercise(fail);
    }
}
