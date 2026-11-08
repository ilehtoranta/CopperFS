using Amiga;
using CopperSFS;
using CopperStart.Dos;
using System.Buffers.Binary;

namespace CopperStart.FileSystem.Tests;

public sealed partial class VolumeGeometryTests
{
    private static (Storage, Memory, AmigaVolumeGeometry, AmigaFfsWriteCore.Request, byte[], uint[])
        FragmentedFile(int blocks, int run, bool ofs = false, uint bytes = 512, uint track = 32, int firstData = 500, int gap = 19)
    {
        var (s, p, g, file) = AllocationFixture(ofs, bytes, blocks: 12000, first: 2, track: track);
        var payload = (int)bytes - (ofs ? 24 : 0);
        var expected = Enumerable.Range(0, blocks * payload).Select(i => (byte)(i * 17 + i / payload)).ToArray();
        OfsWrite(ref p, s, ref file, 0, expected);
        var original = InspectAllocation(s, g, file.Header, ofs, expected);
        var keys = Enumerable.Range(0, blocks).Select(i => (uint)(firstData + i + i / run * gap)).ToArray();
        var table = file.Header;
        for (var first = 0; first < blocks; first += (int)g.TableEntries)
        {
            Assert.True(p.ReadBlock(table, Meta));
            var count = Math.Min((int)g.TableEntries, blocks - first);
            for (var slot = 0; slot < count; slot++)
            {
                var i = first + slot;
                Assert.True(p.ReadBlock(original[i], Data));
                if (ofs)
                {
                    p.WriteUInt32(Data, 16, i + 1 < blocks ? keys[i + 1] : 0);
                    Checksum(ref p, Data, bytes, 20);
                }
                Assert.True(p.WriteBlock(keys[i], Data));
                AllocationBit(ref p, g, original[i], true);
                AllocationBit(ref p, g, keys[i], false);
                p.WriteUInt32(Meta, g.Tail + 308 - slot * 4, keys[i]);
            }
            if (first == 0) p.WriteUInt32(Meta, 16, keys[0]);
            var next = p.ReadUInt32(Meta, g.Tail + 504);
            Checksum(ref p, Meta, bytes, 20);
            Assert.True(p.WriteBlock(table, Meta)); table = next;
        }
        Assert.Equal(keys, InspectAllocation(s, g, file.Header, ofs, expected));
        s.BulkTransfers = true;
        return (s, p, g, file, expected, keys);
    }

    private static bool FreeOnDisk(Storage s, AmigaVolumeGeometry g, uint key)
    {
        var bit = key - g.ReservedBlocks;
        var map = s.Sectors[(ulong)(g.RootBlock + 1 + bit / g.BitmapBits) * g.BlockBytes];
        var word = BinaryPrimitives.ReadUInt32BigEndian(map.AsSpan(4 + (int)(bit % g.BitmapBits / 32) * 4));
        return (word & 1u << (int)(bit % 32)) != 0;
    }

    [Theory]
    [InlineData(true, 64, 249, true)] [InlineData(false, 64, 249, false)]
    [InlineData(true, 64, 19, false)] [InlineData(true, 32, 249, false)]
    public void OfsHardDiskPolicyPreservesBothChains(bool disk, int count, int gap, bool moved)
    {
        var (s, p, g, file, expected, old) = FragmentedFile(count, 8, ofs: true, gap: gap);
        var used = Used(ref p);
        Array.Fill(expected, (byte)73); expected.CopyTo(s.Ram, (int)Payload.Raw);
        var work = Work();
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref work, ref file, out var actual, disk));
        Assert.Equal(file.Length, actual); Assert.Equal(used, Used(ref p));
        var keys = InspectAllocation(s, g, file.Header, true, expected);
        if (moved)
        {
            Assert.Equal(Enumerable.Range((int)keys[0], count).Select(k => (uint)k), keys);
            Assert.All(old, k => Assert.True(FreeOnDisk(s, g, k)));
        }
        else Assert.Equal(old, keys);
    }

    [Theory]
    [InlineData(false, true, 512, 0, 64, true)]
    [InlineData(false, true, 512, 8, 80, true)] // interior, crossing the extension table
    [InlineData(false, true, 4096, 2, 32, true)]
    [InlineData(false, true, 512, 2, 7, false)]
    [InlineData(false, false, 512, 0, 64, false)] // default writer, even on large volumes
    [InlineData(true, true, 512, 0, 64, false)] // OFS on a confirmed HDD
    public void FragmentedOverwritePreservesContentsAndOwnership(bool ofs, bool disk, uint bytes,
        int start, int blocks, bool move)
    {
        var (s, p, g, file, expected, old) = FragmentedFile(96, 1, ofs, bytes);
        var payload = bytes - (ofs ? 24u : 0u);
        var used = Used(ref p);
        file.Position = (uint)start * payload; file.Length = (uint)blocks * payload; file.Buffer = Payload;
        var patch = Enumerable.Range(0, (int)file.Length).Select(i => (byte)(73 + i / payload + i * 3)).ToArray();
        patch.CopyTo(expected, (int)file.Position); patch.CopyTo(s.Ram, (int)Payload.Raw);
        var w = Work();
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out var actual, disk), w.Error.ToString());
        Assert.Equal(file.Length, actual); Assert.Equal(used, Used(ref p));
        var keys = InspectAllocation(s, g, file.Header, ofs, expected);
        for (var i = 0; i < keys.Length; i++)
        {
            var changed = move && i >= start && i < start + blocks;
            Assert.Equal(changed, old[i] != keys[i]);
            if (changed && !keys.Contains(old[i])) Assert.True(FreeOnDisk(s, g, old[i]));
            if (changed && i > start && i % g.TableEntries != 0) Assert.Equal(keys[i - 1] + 1, keys[i]);
        }
        var fresh = new Memory(s, g);
        Assert.Equal(expected.Length, AmigaDiskFileSystemCore.Read(ref fresh, file.Header, ofs ? 0x444F5300u : 0x444F5301u,
            0, Output, (uint)expected.Length, Meta, Data, out var error));
        Assert.Equal(DOS.Error.None, error); Assert.Equal(expected, s.Ram.AsSpan((int)Output.Raw, expected.Length).ToArray());
    }

    [Theory]
    [InlineData(0)] // fully allocated, no scratch space on disk
    [InlineData(1)] // mixed partial blocks
    [InlineData(2)] // growth after overwrite
    [InlineData(3)] // contiguous
    [InlineData(4)] // two runs
    [InlineData(5)] // old blocks span two allocation bitmap pages
    public void IneligibleOverwritesKeepTheirOriginalBlocks(int layout)
    {
        var (s, p, g, file, expected, old) = FragmentedFile(32, layout == 3 ? 32 : layout == 4 ? 16 : 1,
            firstData: layout == 5 ? 4000 : 500);
        if (layout == 0)
        {
            for (var page = 0u; page * g.BitmapBits < g.BlockCount - 2; page++)
            {
                p.Clear(Bitmap, g.BlockBytes); Assert.True(p.WriteBlock(g.RootBlock + 1 + page, Bitmap));
            }
        }
        file.Position = layout == 1 ? 1u : 0u;
        file.Length = (uint)expected.Length - file.Position + (layout == 2 ? 512u : 0u);
        Array.Resize(ref expected, (int)(file.Position + file.Length));
        s.Ram.AsSpan((int)Payload.Raw, (int)file.Length).Fill(0xD3);
        expected.AsSpan((int)file.Position).Fill(0xD3);
        var w = Work(); Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out var actual, true), w.Error.ToString());
        Assert.Equal(file.Length, actual);
        Assert.Equal(old, InspectAllocation(s, g, file.Header, false, expected).Take(old.Length));
        Assert.Equal(0, s.Flushes);
    }

    [Fact]
    public void RelocationDiscountsContinuityWithBothUntouchedNeighbors()
    {
        var (s, p, g, file, expected, old) = FragmentedFile(16, 2);
        file.Position = 512; file.Length = 8 * 512;
        s.Ram.AsSpan((int)Payload.Raw, (int)file.Length).Fill(0x63);
        expected.AsSpan((int)file.Position, (int)file.Length).Fill(0x63);
        var w = Work();
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _, true));
        Assert.Equal(old, InspectAllocation(s, g, file.Header, false, expected));
        Assert.Equal(0, s.Flushes);
    }

    [Fact]
    public void ExtensionPublicationFailureReportsOnlyTheFirstTable()
    {
        var (s, p, g, file, expected, old) = FragmentedFile(96, 1);
        Assert.True(p.ReadBlock(file.Header, Meta)); var extension = p.ReadUInt32(Meta, 504);
        s.FailWrite = extension * 512UL;
        s.Ram.AsSpan((int)Payload.Raw, expected.Length).Fill(0x73);
        var w = Work();
        Assert.False(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out var actual, true));
        Assert.Equal(DOS.Error.DiskNotValidated, w.Error); Assert.Equal(72 * 512u, actual);
        for (var i = 0; i < old.Length; i++)
        {
            if (i == 0 || i == 72)
            {
                Assert.True(p.ReadBlock(i == 0 ? file.Header : extension, Meta));
                Assert.True(AmigaDiskFileSystemCore.Checksum(ref p, Meta));
            }
            var key = p.ReadUInt32(Meta, 308 - i % 72 * 4);
            Assert.False(FreeOnDisk(s, g, key)); Assert.Equal(i >= 72, key == old[i]);
            var bytes = s.Sectors[key * 512UL];
            if (i < 72) Assert.True(bytes.AsSpan().IndexOfAnyExcept((byte)0x73) < 0);
            else Assert.Equal(expected.AsSpan(i * 512, 512).ToArray(), bytes);
        }
        Assert.True(p.ReadBlock(g.RootBlock, Meta)); Assert.Equal(0u, p.ReadUInt32(Meta, 312));
        var writes = s.WriteCalls;
        Assert.False(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out actual, true));
        Assert.Equal(0u, actual); Assert.Equal(writes, s.WriteCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FailedRelocationNeverFreesReachableData(bool ofs)
    {
        (int Writes, int Flushes) Run(int failWrite, int failFlush)
        {
            var (s, p, g, file, expected, old) = ofs ? FragmentedFile(64, 8, true, gap: 249) : FragmentedFile(24, 1);
            // Fail inside the device's split payload transfer as well as at
            // metadata publication, reclamation and all durability barriers.
            g.MaxTransfer = 3 * 512; p = new Memory(s, g);
            var w = Work(); file.Position = 0;
            s.Ram.AsSpan((int)Payload.Raw, expected.Length).Fill(0xDC);
            s.WriteCalls = 0; s.FailWriteCall = failWrite; s.FailFlushCall = failFlush;
            var ok = AmigaFfsWriteCore.Write(ref p, ref w, ref file, out var actual, true);
            Assert.Equal(failWrite == 0 && failFlush == 0, ok);
            if (!ok) Assert.Equal(DOS.Error.DiskNotValidated, w.Error);
            Assert.True(p.ReadBlock(g.RootBlock, Meta)); Assert.True(AmigaDiskFileSystemCore.Checksum(ref p, Meta));
            Assert.Equal(ok || failWrite == 1 ? uint.MaxValue : 0u, p.ReadUInt32(Meta, 312));
            Assert.True(p.ReadBlock(file.Header, Meta)); Assert.True(AmigaDiskFileSystemCore.Checksum(ref p, Meta));
            var published = p.ReadUInt32(Meta, 308) != old[0];
            Assert.Equal(published ? file.Length : 0u, actual);
            Assert.Equal(p.ReadUInt32(Meta, 16), p.ReadUInt32(Meta, 308));
            for (var i = 0; i < old.Length; i++)
            {
                var key = p.ReadUInt32(Meta, 308 - i * 4);
                Assert.False(FreeOnDisk(s, g, key));
                var data = s.Sectors[(ulong)key * 512];
                if (ofs)
                {
                    Assert.True(p.ReadBlock(key, Data)); Assert.True(AmigaDiskFileSystemCore.Checksum(ref p, Data));
                    Assert.Equal(i == old.Length - 1 ? 0u : p.ReadUInt32(Meta, 304 - i * 4), p.ReadUInt32(Data, 16));
                }
                var payload = data.AsSpan(ofs ? 24 : 0);
                if (published) Assert.True(payload.IndexOfAnyExcept((byte)0xDC) < 0);
                else Assert.True(expected.AsSpan(i * payload.Length, payload.Length).SequenceEqual(payload));
            }
            return (s.WriteCalls, s.Flushes);
        }
        var (writes, flushes) = Run(0, 0);
        for (var i = 1; i <= writes; i++) Run(i, 0);
        for (var i = 1; i <= flushes; i++) Run(0, i);
    }

    [Theory]
    [InlineData(4, 1, false)] [InlineData(8, 1, true)] [InlineData(8, 2, false)]
    [InlineData(16, 2, true)] [InlineData(32, 4, true)] [InlineData(32, 8, true)]
    [InlineData(32, 16, false)] [InlineData(64, 8, true)] [InlineData(64, 64, false)] [InlineData(96, 2, true)]
    public void BenchmarkFragmentedOverwriteAndColdSequentialRead(int blocks, int run, bool shouldMove)
    {
        foreach (var disk in new[] { false, true })
        {
            var (s, p, g, file, expected, _) = FragmentedFile(blocks, run);
            file.Position = 0; s.Ram.AsSpan((int)Payload.Raw, expected.Length).Fill(0xAC);
            var w = Work(); s.Transfers.Clear();
            Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out var actual, disk), w.Error.ToString());
            Assert.Equal(file.Length, actual);
            Assert.Equal(disk && shouldMove, s.Flushes != 0);
            var writes = s.Transfers.ToArray(); s.Transfers.Clear();
            var reader = new Memory(s, g);
            Assert.Equal(expected.Length, AmigaDiskFileSystemCore.Read(ref reader, file.Header, 0x444F5301, 0,
                Output, file.Length, Meta, Data, out var error));
            Assert.Equal(DOS.Error.None, error);
            Assert.True(s.Ram.AsSpan((int)Output.Raw, expected.Length).IndexOfAnyExcept((byte)0xAC) < 0);
            output.WriteLine($"blocks={blocks} run={run} HDD={disk} overwriteIO={writes.Length} " +
                $"writes={writes.Count(t => t.Command == 3)} barriers={s.Flushes} sequentialReadIO={s.Transfers.Count}");
        }
    }
}
