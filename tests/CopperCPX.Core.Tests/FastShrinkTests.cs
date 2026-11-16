using CopperFS.Cpx4.Reference;
using CopperCPX.Reference.Tests;
using static CopperFS.Cpx4.Reference.Layout;

namespace CopperCPX.Tests;

public class FastShrinkTests
{
    private static CpxEngine<Ram> Mount(IBlockDevice disk, Ram ram, bool catalogue)
    {
        var engine = new CpxEngine<Ram>(ram, 32);
        Assert.Equal(CpxError.None, engine.Initialize(512, 512, disk.SectorCount, 1, metadataPageBytes: 512));
        Assert.Equal(CpxError.None, engine.EnableBufferedWrites(300000, CpxEngine<Ram>.BufferedStorageBytes(512)));
        Assert.Equal(CpxError.None, engine.EnableShadowVolume(900000, 32768, catalogue ? 5u : 4u));
        engine.BeginOperation(CpxOperation.Mount); EngineTests.Drive(ref engine, ram, disk);
        return engine;
    }

    private static Wide Seed(ref CpxEngine<Ram> engine, Ram ram, IBlockDevice disk)
    {
        "file"u8.CopyTo(ram.Bytes.AsSpan(100000));
        engine.BeginCreate(Wide.From(1), 100000, 4); EngineTests.Drive(ref engine, ram, disk);
        Wide id = engine.MutatedObject;
        for (int n = 0; n < 8192; n++) ram.Bytes[100000 + n] = (byte)(n * 17 + 3);
        engine.BeginMappedWrite(id, default, 100000, 8192); EngineTests.Drive(ref engine, ram, disk);
        return id;
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void ShrinkIsOneBoundedStepAndLaterGrowthZerosThePartialTail(bool catalogue)
    {
        var disk = new MemoryDevice(); Cpx4Volume<MemoryDevice>.FormatDirectInodes(disk, 512, new byte[49152], 12345, true, catalogue);
        var ram = new Ram(new byte[1000000]); var engine = Mount(disk, ram, catalogue);
        Wide id = Seed(ref engine, ram, disk);
        Assert.Equal(CpxError.None, engine.BeginResize(id, Wide.From(6003)));
        Assert.Equal(StepResult.Complete, engine.Step());
        Assert.True(engine.HasBufferedChanges);
        Assert.Equal(CpxError.None, engine.BeginResize(id, Wide.From(8192))); EngineTests.Drive(ref engine, ram, disk);
        engine.BeginPersistenceBarrier(); EngineTests.Drive(ref engine, ram, disk);
        engine.BeginRemount(); EngineTests.Drive(ref engine, ram, disk);
        var check = new Cpx4Volume<MemoryDevice>(disk, new byte[49152]); check.Check();
        byte[] bytes = new byte[8192]; Assert.Equal(8192, check.Read((ulong)id, 0, bytes));
        for (int n = 0; n < bytes.Length; n++) Assert.Equal(n < 6003 ? (byte)(n * 17 + 3) : (byte)0, bytes[n]);
    }

    // Load the exact four authenticated images after a fresh mount. This also
    // exercises shrinking OLD data, whose released suffix cannot be reused by
    // a subsequent write before the new root becomes durable.
    private static void Warm(ref CpxEngine<Ram> engine, Ram ram, IBlockDevice disk, Wide id)
    {
        engine.BeginOperation(CpxOperation.ReadObject, id); EngineTests.Drive(ref engine, ram, disk);
        ulong physical = U64(ram.Bytes.AsSpan((int)engine.ObjectRecord), 104);
        uint objects = ram.Read32(32 + 4088); ulong inode = 0;
        for (uint n = 0; n < 8; n++)
            if (ram.Read32(objects + n * 272 + 268) != 0 && U64(ram.Bytes.AsSpan((int)(objects + n * 272 + 12)), 0) == (ulong)id)
                inode = U64(ram.Bytes.AsSpan((int)(objects + n * 272)), 0);
        Assert.NotEqual(0UL, inode);
        foreach (ulong at in new[] { inode, (ulong)engine.StatePageAddress, 256UL, 258UL + (physical >> 11) * 2 })
        {
            engine.BeginOperation(CpxOperation.ReadMetadata, at); EngineTests.Drive(ref engine, ram, disk);
        }
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void EverySectorCutPreservesOldPayloadAndAcknowledgedShrinkThenAllowsChangedRetry(bool catalogue)
    {
        var initial = new MemoryDevice(); Cpx4Volume<MemoryDevice>.FormatDirectInodes(initial, 512, new byte[49152], 12345, true, catalogue);
        var seedRam = new Ram(new byte[1000000]); var seed = Mount(initial, seedRam, catalogue);
        Wide id = Seed(ref seed, seedRam, initial);
        seed.BeginPersistenceBarrier(); EngineTests.Drive(ref seed, seedRam, initial);
        void Change(IBlockDevice disk, List<int>? acknowledged = null)
        {
            var ram = new Ram(new byte[1000000]); var engine = Mount(disk, ram, catalogue);
            Warm(ref engine, ram, disk, id);
            engine.BeginResize(id, Wide.From(6003)); Assert.Equal(StepResult.Complete, engine.Step());
            "temp"u8.CopyTo(ram.Bytes.AsSpan(100000)); engine.BeginCreate(Wide.From(1), 100000, 4); EngineTests.Drive(ref engine, ram, disk);
            Wide temp = engine.MutatedObject; ram.Bytes.AsSpan(110000, 2048).Fill(0xd7);
            engine.BeginMappedWrite(temp, default, 110000, 2048); EngineTests.Drive(ref engine, ram, disk);
            engine.BeginPersistenceBarrier(); EngineTests.Drive(ref engine, ram, disk);
            acknowledged?.Add(((CrashDevice)disk).Events.Count);
            "Later"u8.CopyTo(ram.Bytes.AsSpan(100000)); engine.BeginLabel(100000, 5); EngineTests.Drive(ref engine, ram, disk);
            engine.BeginPersistenceBarrier(); EngineTests.Drive(ref engine, ram, disk);
        }
        var trace = new CrashDevice(initial.Bytes); var acknowledged = new List<int>(); Change(trace, acknowledged);
        for (int cut = 1; cut <= trace.Events.Count; cut++)
        for (int persistence = 0; persistence < 5; persistence++)
        {
            var fault = new CrashDevice(initial.Bytes) { Cut = cut, Persistence = persistence };
            Assert.Throws<PowerLoss>(() => Change(fault));
            var recovered = new MemoryDevice(fault.Durable);
            var check = new Cpx4Volume<MemoryDevice>(recovered, new byte[49152]); check.Check();
            byte[] bytes = new byte[8192]; int length = check.Read((ulong)id, 0, bytes);
            Assert.True(length is 6003 or 8192);
            if (acknowledged[0] < cut) Assert.Equal(6003, length);
            for (int n = 0; n < length; n++) Assert.Equal((byte)(n * 17 + 3), bytes[n]);
            var ram = new Ram(new byte[1000000]); var engine = Mount(recovered, ram, catalogue);
            ram.Bytes[100000] = 0x92; engine.BeginMappedWrite(id, Wide.From(44), 100000, 1); EngineTests.Drive(ref engine, ram, recovered);
            engine.BeginPersistenceBarrier(); EngineTests.Drive(ref engine, ram, recovered);
            check = new Cpx4Volume<MemoryDevice>(recovered, new byte[49152]); check.Check();
            Assert.Equal(1, check.Read((ulong)id, 44, bytes.AsSpan(0, 1))); Assert.Equal(0x92, bytes[0]);
        }
    }
}
