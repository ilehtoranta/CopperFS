using CopperCPX;
using CopperCPX.Reference.Tests;
using CopperFS.Cpx4.Reference;
using static CopperFS.Cpx4.Reference.Layout;

namespace CopperCPX.Tests;

public class ShadowCommitTests
{
    private const uint Page = 1024, Sector = 512, RootBase = 2, Identity = 12345;
    private static byte[] Image(ulong tag, ulong generation, byte marker, uint bytes = Page)
    {
        byte[] image = new byte[bytes];
        Header(image, PageType.ObjectLeaf, tag, 0, generation, 0, 0);
        Put32(image, 0, CpxShadowCommit<Ram>.Magic); Put16(image, 6, 5);
        image[64] = marker;
        Put32(image, 8, Crc(image, true));
        return image;
    }
    private static byte[] Root(ulong address, ulong generation)
    {
        byte[] root = Image(address, generation, 0);
        Put16(root, 4, 2); Put16(root, 52, 32); Put64(root, 72, Identity); Put64(root, 80, generation);
        Put32(root, 8, Crc(root, true)); return root;
    }
    private static MemoryDevice Fresh()
    {
        var disk = new MemoryDevice();
        for (uint slot = 0; slot < 3; slot++) disk.Write(RootBase + slot * (Page / Sector), Root(RootBase + slot * (Page / Sector), 1));
        foreach (ulong pair in new ulong[] { 16, 20 })
        for (uint slot = 0; slot < 2; slot++) disk.Write(pair + slot * (Page / Sector), Image(pair, 1, 1));
        return disk;
    }
    // Independent selection reads durable bytes and authenticates each copy.
    // It never calls the publication kernel or interprets a CPX4 page as CPX5.
    // The small protocol fixture embeds a complete two-page selection map in
    // the opaque root token. The filesystem integration needs a persistent map
    // tree, with the same explicit reachability rule, for general volumes.
    private static (ulong Generation, uint Survivor, uint CopyA, uint CopyB, byte A, byte B, ulong MappingRoot) Read(byte[] image)
    {
        ReadOnlySpan<byte> At(ulong sector) => image.AsSpan(checked((int)(sector * Sector)), (int)Page);
        bool Valid(ulong at, ulong tag, ushort type)
        {
            var p = At(at);
            return U32(p, 0) == 0x43505835 && U16(p, 6) == 5 && U16(p, 4) == type
                && U64(p, 16) == tag && U32(p, 8) == Crc(p, true);
        }
        ulong generation = 0, mappingRoot = 0; uint survivor = 0;
        for (uint slot = 0; slot < 3; slot++)
        {
            ulong at = RootBase + slot * (Page / Sector);
            if (!Valid(at, at, 2)) continue;
            var p = At(at);
            Assert.Equal(Identity, U64(p, 72)); Assert.Equal(U64(p, 32), U64(p, 80)); Assert.Equal(32, U16(p, 52));
            if (U64(p, 32) > generation) { generation = U64(p, 32); survivor = slot; mappingRoot = U64(p, 88); }
        }
        Assert.NotEqual(0ul, generation);
        (uint Slot, byte Value) Select(ulong pair)
        {
            uint selected = (uint)((mappingRoot >> (int)pair) & 1);
            ulong at = pair + selected * (Page / Sector);
            Assert.True(Valid(at, pair, 11));
            var p = At(at); Assert.InRange(U64(p, 32), 1ul, generation);
            return (selected, p[64]);
        }
        var a = Select(16); var b = Select(20);
        return (generation, survivor, a.Slot, b.Slot, a.Value, b.Value, mappingRoot);
    }
    private static void Drive(ref CpxShadowCommit<Ram> commit, Ram ram, IBlockDevice disk)
    {
        for (int turn = 0; turn < 512; turn++)
        {
            commit = new CpxShadowCommit<Ram>(ram, commit.Arena);
            var result = commit.Step();
            if (result == StepResult.Complete) return;
            Assert.NotEqual(StepResult.Failed, result);
            if (result != StepResult.NeedIo) continue;
            var request = commit.Request;
            Assert.Equal(StepResult.NeedIo, commit.Step());
            Assert.Equal(CpxError.StaleCompletion, commit.CompleteIo(request.Token + 1, request.Bytes, true));
            if (request.Kind == IoKind.Write)
                disk.Write(request.Sector, ram.Bytes.AsSpan((int)request.Buffer, (int)request.Bytes));
            else { Assert.Equal(IoKind.Flush, request.Kind); disk.Flush(); }
            Assert.Equal(CpxError.None, commit.CompleteIo(request.Token, request.Bytes, true));
        }
        Assert.Fail("Shadow publication did not terminate.");
    }

    private static void Publish(MemoryDevice disk, byte[] currentImage, byte a, byte b, bool writeA, bool writeB)
    {
        var state = Read(currentImage);
        var ram = new Ram(new byte[16384]); var commit = new CpxShadowCommit<Ram>(ram, 32);
        Assert.Equal(CpxError.None, commit.Initialize(Sector, Page, (Wide)disk.SectorCount, Wide.From(Identity),
            state.Generation, Wide.From(RootBase), state.Survivor, 4096, state.MappingRoot));
        uint count = 0;
        void Add(ulong pair, uint selected, byte value, uint buffer)
        {
            Image(pair, state.Generation + 1, value).CopyTo(ram.Bytes, buffer);
            var d = ram.Bytes.AsSpan(1024 + (int)count * 32, 32);
            Put64(d, 0, pair); Put64(d, 8, pair); Put32(d, 16, buffer); Put32(d, 20, selected); count++;
        }
        if (writeA) Add(16, state.CopyA, a, 8192);
        if (writeB) Add(20, state.CopyB, b, 10240);
        ulong nextRoot = state.MappingRoot ^ (writeA ? 1ul << 16 : 0) ^ (writeB ? 1ul << 20 : 0);
        Assert.Equal(CpxError.None, commit.Begin(1024, count, nextRoot));
        Drive(ref commit, ram, disk);
        Assert.Equal(state.Generation + 1, (ulong)commit.DurableGeneration);
        Assert.Equal(nextRoot, (ulong)commit.DurableMappingRoot);
    }

    [Fact]
    public void PublishesThroughTwoBarriersAndRetainsThePreviousPairVersion()
    {
        var disk = Fresh(); var before = Read(disk.Bytes); int flushes = disk.Flushes;
        Publish(disk, disk.Bytes, 2, 2, true, true);
        Assert.Equal(2, disk.Flushes - flushes);
        var after = Read(disk.Bytes); Assert.Equal(2ul, after.Generation); Assert.Equal((byte)2, after.A); Assert.Equal((byte)2, after.B);
        Assert.Equal((byte)1, disk.Bytes[16 * Sector + before.CopyA * Page + 64]);
    }

    [Fact]
    public void ThreePublicationsRemainAtomicAcrossEverySectorBarrierAndTornPersistence()
    {
        var initial = Fresh();
        void Sequence(CrashDevice disk, List<int>? acknowledgements = null)
        {
            Publish(disk, disk.Bytes, 2, 2, true, true); acknowledgements?.Add(disk.Events.Count);
            Publish(disk, disk.Bytes, 3, 2, true, false); acknowledgements?.Add(disk.Events.Count);
            Publish(disk, disk.Bytes, 3, 4, false, true); acknowledgements?.Add(disk.Events.Count);
        }
        var trace = new CrashDevice(initial.Bytes); var acknowledgedAt = new List<int>(); Sequence(trace, acknowledgedAt);
        for (int cut = 1; cut <= trace.Events.Count; cut++)
        for (int persistence = 0; persistence < 5; persistence++)
        {
            var disk = new CrashDevice(initial.Bytes) { Cut = cut, Persistence = persistence };
            Assert.Throws<PowerLoss>(() => Sequence(disk));
            var state = Read(disk.Durable);
            Assert.True(state.Generation >= 1ul + (uint)acknowledgedAt.Count(at => at < cut));
            Assert.Contains((state.A, state.B), new (byte, byte)[] { (1, 1), (2, 2), (3, 2), (3, 4) });
            var restarted = new MemoryDevice(disk.Durable);
            Publish(restarted, restarted.Bytes, 7, state.B, true, false);
            var resumed = Read(restarted.Bytes);
            Assert.Equal((byte)7, resumed.A); Assert.Equal(state.B, resumed.B);
        }
    }

    [Fact]
    public void RestartDoesNotPublishAbandonedPagesWhenTheNextWriteSetDiffers()
    {
        var initial = Fresh(); var trace = new CrashDevice(initial.Bytes);
        Publish(trace, trace.Bytes, 2, 2, true, true);
        int cut = trace.Events.FindIndex(e => e == "before flush") + 1;
        Assert.True(cut > 0);
        var fault = new CrashDevice(initial.Bytes) { Cut = cut, Persistence = 1 };
        Assert.Throws<PowerLoss>(() => Publish(fault, fault.Bytes, 2, 2, true, true));
        Assert.Equal(1ul, Read(fault.Durable).Generation);
        var restarted = new MemoryDevice(fault.Durable);
        Publish(restarted, restarted.Bytes, 7, 1, true, false);
        var result = Read(restarted.Bytes);
        Assert.Equal((byte)7, result.A);
        Assert.Equal((byte)1, result.B);
    }
}
