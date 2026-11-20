using CopperCPX.Reference.Tests;
using CopperFS.Cpx4.Reference;
using static CopperFS.Cpx4.Reference.Layout;

namespace CopperCPX.Tests;

public class ShadowTreePublicationTests
{
    private const uint Page = 512, RootBase = 2, Identity = 12345;
    private const ulong PairA = 16, PairB = 4096;
    private static CpxShadowMapGeometry Geometry()
    {
        Assert.Equal(CpxError.None, CpxShadowMapGeometry.TryCreate(512, Page, Wide.From(8192), Wide.From(512), out var geometry));
        Assert.Equal(2u, geometry.Height); return geometry;
    }
    private static byte[] Image(ushort type, ulong tag, ulong generation, byte marker = 0)
    {
        byte[] image = new byte[Page]; Put32(image, 0, 0x43505835); Put16(image, 4, type); Put16(image, 6, 5);
        Put64(image, 16, tag); Put64(image, 32, generation); image[64] = marker; return image;
    }
    private static void Seal(Span<byte> image) => Put32(image, 8, Crc(image, true));
    private static MemoryDevice Fresh()
    {
        var disk = new MemoryDevice();
        for (uint slot = 0; slot < 3; slot++)
        {
            var root = Image(2, RootBase + slot, 1); Put16(root, 52, 32);
            Put64(root, 72, Identity); Put64(root, 80, 1); Seal(root); disk.Write(RootBase + slot, root);
        }
        foreach (ulong pair in new[] { PairA, PairB })
        for (uint copy = 0; copy < 2; copy++)
        { var image = Image(11, pair, 1, 1); Seal(image); disk.Write(pair + copy, image); }
        return disk;
    }
    // Independent decoder for this fixed two-level fixture: branch at pair
    // 516, leaves at pairs 512/514. It does not call either production cursor.
    private static (ulong Generation, uint Survivor, ulong MappingRoot, byte A, byte B) Read(byte[] bytes)
    {
        ReadOnlySpan<byte> At(ulong sector) => bytes.AsSpan((int)sector * 512, 512);
        bool Valid(ReadOnlySpan<byte> page, ushort type, ulong tag)
            => U32(page, 0) == 0x43505835 && U16(page, 6) == 5 && U16(page, 4) == type
            && U64(page, 16) == tag && U32(page, 8) == Crc(page, true);
        ulong generation = 0, mapping = 0; uint survivor = 0;
        for (uint slot = 0; slot < 3; slot++)
        {
            var root = At(RootBase + slot); if (!Valid(root, 2, RootBase + slot)) continue;
            Assert.Equal(Identity, U64(root, 72)); Assert.Equal(U64(root, 32), U64(root, 80));
            if (U64(root, 32) > generation) { generation = U64(root, 32); survivor = slot; mapping = U64(root, 88); }
        }
        Assert.NotEqual(0ul, generation);
        byte Select(ulong pair)
        {
            uint copy = 0;
            if (mapping != 0)
            {
                Assert.True(mapping is 516 or 517);
                var branch = At(mapping); Assert.True(Valid(branch, 21, 0x8000000000000204));
                Assert.Equal(Identity, U64(branch, 24)); Assert.InRange(U64(branch, 32), 1ul, generation);
                uint child = pair >= 4096 ? 1u : 0u;
                uint selected = ((uint)branch[64] >> (int)(6 - child * 2)) & 3;
                Assert.NotEqual(3u, selected);
                if (selected != 0)
                {
                    ulong leafPair = 512 + child * 2;
                    var leaf = At(leafPair + selected - 1); Assert.True(Valid(leaf, 20, 0x8000000000000000 | leafPair));
                    Assert.Equal(Identity, U64(leaf, 24)); Assert.InRange(U64(leaf, 32), 1ul, generation);
                    uint bit = (uint)(pair >> 1) & 2047;
                    copy = ((uint)leaf[(int)(64 + (bit >> 3))] >> (int)(7 - (bit & 7))) & 1;
                }
            }
            var image = At(pair + copy); Assert.True(Valid(image, 11, pair));
            Assert.InRange(U64(image, 32), 1ul, generation); return image[64];
        }
        return (generation, survivor, mapping, Select(PairA), Select(PairB));
    }
    private static void Publish(MemoryDevice disk, byte a, byte b, bool updateA, bool updateB)
    {
        var previous = Read(disk.Bytes); var ram = new Ram(new byte[65536]); uint count = 0;
        void Add(ulong pair, byte marker, uint buffer)
        {
            var image = Image(11, pair, previous.Generation + 1, marker); Seal(image); image.CopyTo(ram.Bytes, buffer);
            var descriptor = ram.Bytes.AsSpan(2048 + (int)count * 32, 32);
            Put64(descriptor, 0, pair); Put64(descriptor, 8, pair); Put32(descriptor, 16, buffer); count++;
        }
        if (updateB) Add(PairB, b, 12288); // reverse order exercises bounded sorting
        if (updateA) Add(PairA, a, 8192);
        var map = new CpxShadowMapWriteCursor<Ram>(ram, 32, Geometry());
        Assert.Equal(CpxError.None, map.Begin(previous.MappingRoot, previous.Generation, Wide.From(Identity), 2048, count, 4096));
        bool finished = false;
        for (int turn = 0; turn < 512; turn++)
        {
            map = new CpxShadowMapWriteCursor<Ram>(ram, 32, Geometry());
            var result = map.Step();
            if (result == StepResult.Complete) { finished = true; break; }
            Assert.NotEqual(StepResult.Failed, result);
            if (result != StepResult.NeedIo) continue;
            Assert.Equal(StepResult.NeedIo, map.Step());
            var image = ram.Bytes.AsSpan((int)map.RequestBuffer, (int)map.RequestBytes);
            if (map.RequestKind == IoKind.Read) disk.Read(map.RequestSector, image);
            else { Assert.Equal(IoKind.Write, map.RequestKind); disk.Write(map.RequestSector, image); }
            Assert.Equal(CpxError.None, map.AcceptIo(true));
        }
        Assert.True(finished);
        var commit = new CpxShadowCommit<Ram>(ram, 1200);
        Assert.Equal(CpxError.None, commit.Initialize(512, Page, (Wide)disk.SectorCount, Wide.From(Identity), previous.Generation,
            Wide.From(RootBase), previous.Survivor, 16384, previous.MappingRoot));
        Assert.Equal(CpxError.None, commit.Begin(2048, count, map.MappingRoot));
        finished = false;
        for (int turn = 0; turn < 512; turn++)
        {
            commit = new CpxShadowCommit<Ram>(ram, 1200); var result = commit.Step();
            if (result == StepResult.Complete) { finished = true; break; }
            Assert.NotEqual(StepResult.Failed, result);
            if (result != StepResult.NeedIo) continue;
            var io = commit.Request;
            if (io.Kind == IoKind.Write) disk.Write(io.Sector, ram.Bytes.AsSpan((int)io.Buffer, (int)io.Bytes));
            else { Assert.Equal(IoKind.Flush, io.Kind); disk.Flush(); }
            Assert.Equal(CpxError.None, commit.CompleteIo(io.Token, io.Bytes, true));
        }
        Assert.True(finished);
    }

    [Fact]
    public void TwoBarriersPublishARealRadixMappingTreeAcrossBothLeaves()
    {
        var disk = Fresh(); int flushes = disk.Flushes;
        Publish(disk, 2, 2, true, true); Assert.Equal(2, disk.Flushes - flushes);
        var first = Read(disk.Bytes); Assert.Equal((byte)2, first.A); Assert.Equal((byte)2, first.B);
        Publish(disk, 3, 2, true, false);
        var second = Read(disk.Bytes); Assert.Equal((byte)3, second.A); Assert.Equal((byte)2, second.B);
        Assert.NotEqual(first.MappingRoot, second.MappingRoot);
    }
    [Fact]
    public void RealMappingTreeSurvivesEveryPowerCutThenAChangedWriteSet()
    {
        var initial = Fresh(); var trace = new CrashDevice(initial.Bytes);
        Publish(trace, 2, 2, true, true); int firstAcknowledgement = trace.Events.Count;
        Publish(trace, 3, 2, true, false);
        for (int cut = 1; cut <= trace.Events.Count; cut++)
        for (int persistence = 0; persistence < 5; persistence++)
        {
            var disk = new CrashDevice(initial.Bytes) { Cut = cut, Persistence = persistence };
            Assert.Throws<PowerLoss>(() => { Publish(disk, 2, 2, true, true); Publish(disk, 3, 2, true, false); });
            var before = Read(disk.Durable);
            Assert.True(before.Generation >= (cut > firstAcknowledgement ? 2ul : 1ul));
            Assert.Contains((before.A, before.B), new (byte, byte)[] { (1, 1), (2, 2), (3, 2) });
            var restart = new MemoryDevice(disk.Durable);
            Publish(restart, before.A, 7, false, true);
            var after = Read(restart.Bytes); Assert.Equal(before.A, after.A); Assert.Equal((byte)7, after.B);
        }
    }
}
