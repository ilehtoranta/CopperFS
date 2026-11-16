using CopperCPX.Reference.Tests;
using CopperFS.Cpx4.Reference;
using static CopperFS.Cpx4.Reference.Layout;

namespace CopperCPX.Tests;

public class InodeCatalogueTests
{
    private const uint Identity = 12345;
    private static CpxShadowMapGeometry Geometry()
    {
        Assert.Equal(CpxError.None, CpxShadowMapGeometry.TryCreate(512, 512, Wide.From(8192), Wide.From(512), out var geometry, inodeCatalogue: true));
        Assert.Equal(2u, geometry.Height); Assert.Equal(4UL, geometry.NodeCount(0));
        Assert.Equal(520UL, (ulong)geometry.NodePairSector(1, 0)); return geometry;
    }
    private static byte[] Page(ushort type, ulong tag, ulong owner, ulong generation, byte marker = 0)
    {
        var page = new byte[512]; Header(page, (PageType)type, tag, owner, generation, version: 5);
        page[64] = marker; Put32(page, 8, Crc(page, true)); return page;
    }
    private static MemoryDevice Fresh()
    {
        var disk = new MemoryDevice();
        for (uint slot = 0; slot < 3; slot++)
        {
            var page = Page(2, 2 + slot, 0, 1); Put16(page, 52, 32); Put64(page, 72, Identity); Put64(page, 80, 1);
            Put32(page, 8, Crc(page, true)); disk.Write(2 + slot, page);
        }
        foreach (ulong pair in new ulong[] { 16, 4096 })
            for (uint copy = 0; copy < 2; copy++) disk.Write(pair + copy, Page(11, pair, 0, 1, 1));
        return disk;
    }
    private static (ulong Generation, ulong Root, uint Survivor) Checkpoint(byte[] disk)
    {
        ulong generation = 0, root = 0; uint survivor = 0;
        for (uint slot = 0; slot < 3; slot++)
        {
            var page = disk.AsSpan((int)(2 + slot) * 512, 512);
            if (U32(page, 0) != 0x43505835 || U16(page, 4) != 2 || U16(page, 6) != 5 ||
                U64(page, 16) != 2 + slot || U64(page, 72) != Identity || U64(page, 32) != U64(page, 80) ||
                U32(page, 8) != Crc(page, true)) continue;
            if (U64(page, 80) > generation) { generation = U64(page, 80); root = U64(page, 88); survivor = slot; }
        }
        Assert.NotEqual(0UL, generation); return (generation, root, survivor);
    }
    // Independent fixed-fixture decoder: four leaves at pairs512..518 and root520.
    private static (uint Copy, bool Inode, byte Marker) Decode(byte[] disk, ulong pair)
    {
        var checkpoint = Checkpoint(disk); uint value = 0;
        ReadOnlySpan<byte> Node(ulong sector, ushort type, ulong tag, uint marker)
        {
            var page = disk.AsSpan((int)sector * 512, 512);
            Assert.Equal(0x43505835u, U32(page, 0)); Assert.Equal(type, U16(page, 4)); Assert.Equal(5, U16(page, 6));
            Assert.Equal(tag, U64(page, 16)); Assert.Equal(Identity, U64(page, 24)); Assert.Equal(marker, U32(page, 12));
            Assert.InRange(U64(page, 32), 1UL, checkpoint.Generation); Assert.Equal(Crc(page, true), U32(page, 8)); return page;
        }
        if (checkpoint.Root != 0)
        {
            Assert.True(checkpoint.Root is 520 or 521);
            var branch = Node(checkpoint.Root, 21, 0x8000000000000208, 0);
            uint child = (uint)(pair >> 11), selection = ((uint)branch[64] >> (int)(6 - child * 2)) & 3;
            Assert.NotEqual(3u, selection);
            if (selection != 0)
            {
                ulong leafPair = 512 + child * 2;
                var leaf = Node(leafPair + selection - 1, 20, 0x8000000000000000UL | leafPair, 2);
                uint index = (uint)(pair >> 1) & 1023;
                value = ((uint)leaf[64 + (int)(index >> 2)] >> (int)(6 - (index & 3) * 2)) & 3;
            }
        }
        var image = disk.AsSpan((int)(pair + (value & 1)) * 512, 512);
        Assert.Equal(pair, U64(image, 16)); Assert.Equal(Crc(image, true), U32(image, 8));
        return (value & 1, (value & 2) != 0, image[64]);
    }
    private static void Publish(MemoryDevice disk, ushort a, ushort b)
    {
        var old = Checkpoint(disk.Bytes); var ram = new Ram(new byte[65536]); uint count = 0;
        void Add(ulong pair, ushort type, uint buffer)
        {
            if (type == 0) return;
            Page(type, pair, type == 23 ? 42UL : 0UL, old.Generation + 1, (byte)(type == 23 ? 2 : type == 24 ? 3 : 4)).CopyTo(ram.Bytes, buffer);
            uint at = 2048 + count++ * 32; Put64(ram.Bytes.AsSpan((int)at), 0, pair); Put64(ram.Bytes.AsSpan((int)at), 8, pair); ram.Write32(at + 16, buffer);
        }
        Add(4096, b, 12288); Add(16, a, 8192);
        var map = new CpxShadowMapWriteCursor<Ram>(ram, 32, Geometry());
        Assert.Equal(CpxError.None, map.Begin(old.Root, old.Generation, Wide.From(Identity), 2048, count, 4096));
        bool complete = false;
        for (int turn = 0; turn < 512; turn++)
        {
            map = new CpxShadowMapWriteCursor<Ram>(ram, 32, Geometry()); var result = map.Step();
            if (result == StepResult.Complete) { complete = true; break; } Assert.NotEqual(StepResult.Failed, result);
            if (result != StepResult.NeedIo) continue;
            var page = ram.Bytes.AsSpan((int)map.RequestBuffer, (int)map.RequestBytes);
            if (map.RequestKind == IoKind.Read) disk.Read(map.RequestSector, page); else disk.Write(map.RequestSector, page);
            Assert.Equal(CpxError.None, map.AcceptIo(true));
        }
        Assert.True(complete); Wide proposed = map.MappingRoot;
        var commit = new CpxShadowCommit<Ram>(ram, 32);
        Assert.Equal(CpxError.None, commit.Initialize(512, 512, Wide.From(8192), Wide.From(Identity), old.Generation, Wide.From(2), old.Survivor, 16384, old.Root));
        Assert.Equal(CpxError.None, commit.Begin(2048, count, proposed)); complete = false;
        for (int turn = 0; turn < 512; turn++)
        {
            var result = commit.Step(); if (result == StepResult.Complete) { complete = true; break; } Assert.NotEqual(StepResult.Failed, result);
            if (result != StepResult.NeedIo) continue;
            var io = commit.Request;
            if (io.Kind == IoKind.Write) disk.Write(io.Sector, ram.Bytes.AsSpan((int)io.Buffer, (int)io.Bytes)); else disk.Flush();
            Assert.Equal(CpxError.None, commit.CompleteIo(io.Token, io.Bytes, true));
        }
        Assert.True(complete);
    }
    private static (uint Copy, bool Inode) Lookup(MemoryDevice disk, ulong pair)
    {
        var checkpoint = Checkpoint(disk.Bytes); var ram = new Ram(new byte[8192]);
        var cursor = new CpxShadowMapReadCursor<Ram>(ram, 32, Geometry());
        Assert.Equal(CpxError.None, cursor.Begin(checkpoint.Root, checkpoint.Generation, Wide.From(Identity), pair, 4096));
        for (int turn = 0; turn < 32; turn++)
        {
            var result = cursor.Step(); if (result == StepResult.Complete) return (cursor.SelectedCopy, cursor.IsInode);
            Assert.NotEqual(StepResult.Failed, result); if (result != StepResult.NeedIo) continue;
            disk.Read(cursor.ReadSector, ram.Bytes.AsSpan((int)cursor.ReadBuffer, 512)); cursor.AcceptRead(true);
        }
        Assert.Fail("Lookup did not finish."); return default;
    }
    [Fact]
    public void PublicationAndTombstoneChangeCopyAndInodeStatusAtomically()
    {
        var disk = Fresh(); Publish(disk, 23, 11);
        Assert.Equal((1u, true, (byte)2), Decode(disk.Bytes, 16)); Assert.Equal((1u, false, (byte)4), Decode(disk.Bytes, 4096));
        Assert.Equal((1u, true), Lookup(disk, 16)); Assert.Equal((1u, false), Lookup(disk, 4096));
        Publish(disk, 24, 23);
        Assert.Equal((0u, false, (byte)3), Decode(disk.Bytes, 16)); Assert.Equal((0u, true, (byte)2), Decode(disk.Bytes, 4096));
        Assert.Equal((0u, false), Lookup(disk, 16)); Assert.Equal((0u, true), Lookup(disk, 4096));
    }
    [Fact]
    public void EveryPowerCutThenADifferentWriteSetKeepsTheSelectedInodeCatalogue()
    {
        var initial = Fresh(); var trace = new CrashDevice(initial.Bytes); Publish(trace, 23, 11);
        for (int cut = 1; cut <= trace.Events.Count; cut++)
        for (int persistence = 0; persistence < 5; persistence++)
        {
            var fault = new CrashDevice(initial.Bytes) { Cut = cut, Persistence = persistence };
            Assert.Throws<PowerLoss>(() => Publish(fault, 23, 11));
            var disk = new MemoryDevice(fault.Durable); var before = Decode(disk.Bytes, 16);
            Publish(disk, 0, 23);
            Assert.Equal(before, Decode(disk.Bytes, 16)); Assert.True(Decode(disk.Bytes, 4096).Inode);
            Assert.Equal((before.Copy, before.Inode), Lookup(disk, 16)); Assert.True(Lookup(disk, 4096).Inode);
        }
    }
}
