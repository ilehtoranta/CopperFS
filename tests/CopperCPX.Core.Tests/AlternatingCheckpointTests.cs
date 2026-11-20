using CopperCPX;
using CopperCPX.Reference.Tests;
using CopperFS.Cpx4.Reference;

namespace CopperCPX.Tests;

public class AlternatingCheckpointTests
{
    private static CpxEngine<Ram> Mount(IBlockDevice disk, Ram ram, uint unit = 512)
    {
        var engine = new CpxEngine<Ram>(ram, 32);
        Assert.Equal(CpxError.None, engine.Initialize(512, unit, Wide.From((uint)disk.SectorCount), 1));
        Assert.Equal(CpxError.None, engine.EnableAlternatingCheckpoints());
        Assert.Equal(CpxError.None, engine.EnableCpuBatching());
        Assert.Equal(CpxError.None, engine.EnableBufferedWrites(300000, CpxEngine<Ram>.BufferedStorageBytes(Math.Max(1024u, unit))));
        Assert.Equal(CpxError.None, engine.BeginOperation(CpxOperation.Mount));
        EngineTests.Drive(ref engine, ram, disk);
        return engine;
    }

    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(4096)]
    public void RetiresWithFourBarriersAndCleanMountDoesNotRepairTheOlderCheckpoint(int unit)
    {
        var disk = new MemoryDevice(EngineTests.Fresh(unit).Bytes);
        var ram = new Ram(new byte[1000000]);
        var engine = Mount(disk, ram, (uint)unit);
        "child"u8.CopyTo(ram.Bytes.AsSpan(100000));
        Assert.Equal(CpxError.None, engine.BeginCreate(Wide.From(1), 100000, 5));
        EngineTests.Drive(ref engine, ram, disk);
        int before = disk.Flushes;
        Assert.Equal(CpxError.None, engine.BeginPersistenceBarrier());
        EngineTests.Drive(ref engine, ram, disk);
        // Four retirement barriers and the explicit final device drain.
        Assert.Equal(5, disk.Flushes - before);
        int writes = disk.Writes;
        before = disk.Flushes;
        engine = Mount(disk, new Ram(new byte[1000000]), (uint)unit);
        Assert.Equal(writes, disk.Writes);
        Assert.Equal(before, disk.Flushes);
        new IndependentImage(disk.Bytes);
        new Cpx4Volume<MemoryDevice>(disk, new byte[36864]).Check();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThreeGenerationsRecoverAcrossEverySectorAndBarrierIncludingSlotReuse(bool legacyWrongParity)
    {
        var initial = EngineTests.Fresh(512);
        var reference = new Cpx4Volume<Disk>(initial, new byte[36864]);
        reference.Create("file"u8, out ulong id);
        reference.Create("side"u8, out _);
        reference.Create("other"u8, out _);
        if (legacyWrongParity)
        {
            // A classic writer can have durable copy A at an even generation
            // and copy B one generation behind. The newer copy belongs in B for the
            // alternating protocol, so mount must repair B before admission.
            Span<byte> checkpoint = initial.Bytes.AsSpan(65536 + 2 * 1024, 1024);
            ulong generation = Layout.U64(checkpoint, 80);
            Assert.Equal(0ul, generation & 1);
            Layout.Put64(checkpoint, 32, generation - 1);
            Layout.Put64(checkpoint, 80, generation - 1);
            Layout.Put32(checkpoint, 8, Layout.Crc(checkpoint, true));
        }
        byte[] payload = Enumerable.Range(0, 3072).Select(n => (byte)(n * 19 + n / 1024)).ToArray();
        void Write(IBlockDevice disk, List<int>? acknowledgements = null)
        {
            var ram = new Ram(new byte[1000000]);
            var engine = Mount(disk, ram);
            payload.CopyTo(ram.Bytes, 100000);
            for (uint group = 0; group < 3; group++)
            {
                Assert.Equal(CpxError.None, engine.BeginMappedWrite((Wide)id, Wide.From(group * 1024), 100000 + group * 1024, 1024));
                EngineTests.Drive(ref engine, ram, disk);
                Assert.Equal(CpxError.None, engine.BeginPersistenceBarrier());
                EngineTests.Drive(ref engine, ram, disk);
                if (acknowledgements is not null) acknowledgements.Add(((CrashDevice)disk).Events.Count);
            }
        }
        var trace = new CrashDevice(initial.Bytes);
        var acknowledgedAt = new List<int>();
        Write(trace, acknowledgedAt);
        for (int cut = 1; cut <= trace.Events.Count; cut++)
        for (int persistence = 0; persistence < 5; persistence++)
        {
            var fault = new CrashDevice(initial.Bytes) { Cut = cut, Persistence = persistence };
            Assert.Throws<PowerLoss>(() => Write(fault));
            var recovered = new MemoryDevice(fault.Durable);
            var engine = Mount(recovered, new Ram(new byte[1000000]));
            var check = new Cpx4Volume<MemoryDevice>(recovered, new byte[36864]);
            check.Check();
            new IndependentImage(recovered.Bytes);
            byte[] actual = new byte[payload.Length];
            int length = check.Read(id, 0, actual);
            Assert.True(length is 0 or 1024 or 2048 or 3072, $"cut={cut}, persistence={persistence}, length={length}");
            Assert.True(length >= 1024 * acknowledgedAt.Count(at => at < cut), $"Lost acknowledged group at cut={cut}, persistence={persistence}");
            Assert.Equal(payload[..length], actual[..length]);
        }
        var durable = new MemoryDevice(trace.Durable);
        Mount(durable, new Ram(new byte[1000000]));
        var final = new Cpx4Volume<MemoryDevice>(durable, new byte[36864]);
        byte[] contents = new byte[payload.Length];
        Assert.Equal(payload.Length, final.Read(id, 0, contents));
        Assert.Equal(payload, contents);
    }
}
