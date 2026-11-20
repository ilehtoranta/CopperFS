using CopperFS.Cpx4.Reference;
using CopperCPX.Reference.Tests;
using static CopperFS.Cpx4.Reference.Layout;

namespace CopperCPX.Tests;

public class CheckpointCatalogueFilesystemTests
{
    private static CpxEngine<Ram> Mount(IBlockDevice disk, Ram ram, uint unit = 512, bool readOnly = false)
    {
        var engine = new CpxEngine<Ram>(ram, 32);
        uint page = Math.Max(512u, unit);
        Assert.Equal(CpxError.None, engine.Initialize(512, unit, disk.SectorCount, 1, readOnly, metadataPageBytes: page));
        Assert.Equal(CpxError.None, engine.EnableBufferedWrites(300000, CpxEngine<Ram>.BufferedStorageBytes(page)));
        Assert.Equal(CpxError.None, engine.EnableShadowVolume(900000, 32768, 5));
        Assert.Equal(CpxError.None, engine.BeginOperation(CpxOperation.Mount)); EngineTests.Drive(ref engine, ram, disk);
        return engine;
    }

    [Theory][InlineData(512)][InlineData(4096)]
    public void InlineSelectionsRetireAndRemountWithIndependentFullPayloadAndOwnership(int unit)
    {
        var disk = new MemoryDevice(); Cpx4Volume<MemoryDevice>.FormatDirectInodes(disk, unit, new byte[49152], 12345, true, true);
        new Cpx4Volume<MemoryDevice>(disk, new byte[49152]).Check();
        var ram = new Ram(new byte[1000000]); var engine = Mount(disk, ram, (uint)unit);
        "file"u8.CopyTo(ram.Bytes.AsSpan(100000));
        Assert.Equal(CpxError.None, engine.BeginCreate(Wide.From(1), 100000, 4)); EngineTests.Drive(ref engine, ram, disk); Wide id = engine.MutatedObject;
        byte[] expected = Enumerable.Range(0, 8192).Select(n => (byte)(n * 17 + 3)).ToArray(); expected.CopyTo(ram.Bytes, 100000);
        Assert.Equal(CpxError.None, engine.BeginMappedWrite(id, default, 100000, 8192)); EngineTests.Drive(ref engine, ram, disk);
        Assert.Equal(CpxError.None, engine.BeginPersistenceBarrier()); EngineTests.Drive(ref engine, ram, disk);
        Assert.Equal(CpxError.None, engine.BeginRemount()); EngineTests.Drive(ref engine, ram, disk);
        var independent = new Cpx4Volume<MemoryDevice>(disk, new byte[49152]); independent.Check();
        byte[] actual = new byte[8192]; Assert.Equal(actual.Length, independent.Read((ulong)id, 0, actual)); Assert.Equal(expected, actual);
        uint page = engine.MetadataPageBytes, span = page < 1024 ? 2u : page / 512;
        Assert.Equal(ulong.MaxValue, U64(disk.Bytes.AsSpan((int)(128 + span) * 512, (int)Math.Max(1024, page)), 88));
        "file"u8.CopyTo(ram.Bytes.AsSpan(100000));
        Assert.Equal(CpxError.None, engine.BeginDelete(Wide.From(1), 100000, 4)); EngineTests.Drive(ref engine, ram, disk);
        Assert.Equal(CpxError.None, engine.BeginPersistenceBarrier()); EngineTests.Drive(ref engine, ram, disk);
        independent = new Cpx4Volume<MemoryDevice>(disk, new byte[49152]); independent.Check(); Assert.Equal(0UL, independent.FileCount);
    }

    [Fact]
    public void FullCataloguePromotesAllOldAndNewSelectionsWithoutLosingObjects()
    {
        var disk = new MemoryDevice(); Cpx4Volume<MemoryDevice>.FormatDirectInodes(disk, 512, new byte[49152], 12345, true, true);
        var ram = new Ram(new byte[1000000]); var engine = Mount(disk, ram);
        for (int n = 0; n < 70; n++)
        {
            byte[] name = System.Text.Encoding.ASCII.GetBytes($"file{n:D2}"); name.CopyTo(ram.Bytes, 100000);
            Assert.Equal(CpxError.None, engine.BeginCreate(Wide.From(1), 100000, (uint)name.Length)); EngineTests.Drive(ref engine, ram, disk);
            Assert.Equal(CpxError.None, engine.BeginPersistenceBarrier()); EngineTests.Drive(ref engine, ram, disk);
        }
        Assert.Equal(CpxError.None, engine.BeginRemount()); EngineTests.Drive(ref engine, ram, disk);
        var check = new Cpx4Volume<MemoryDevice>(disk, new byte[49152]); check.Check(); Assert.Equal(70UL, check.FileCount);
        for (int n = 0; n < 70; n++) Assert.True(check.TryGetInfo(System.Text.Encoding.ASCII.GetBytes($"file{n:D2}"), out _));
        Assert.NotEqual(ulong.MaxValue, U64(disk.Bytes.AsSpan(130 * 512, 1024), 88));
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void EverySectorAndBarrierCutPreservesPublishedSelectionsThenAcceptsAChangedWriteSet(bool promotion)
    {
        var initial = new MemoryDevice(); Cpx4Volume<MemoryDevice>.FormatDirectInodes(initial, 512, new byte[49152], 12345, true, true);
        int seedCount = 0;
        string changeName = "new";
        if (promotion)
        {
            // Find the actual boundary. Short names fit directory records, so
            // a fixed file count does not prove catalogue capacity overflow.
            for (; seedCount < 100; seedCount++)
            {
                changeName = $"seed{seedCount:D2}";
                var candidate = new MemoryDevice(initial.Bytes);
                var seedRam = new Ram(new byte[1000000]); var seedEngine = Mount(candidate, seedRam);
                CreateAndFlush(ref seedEngine, seedRam, candidate, changeName);
                if (LatestRoot(candidate.Bytes) != ulong.MaxValue) break;
                initial = candidate;
            }
            Assert.InRange(seedCount, 1, 99);
        }
        Assert.Equal(ulong.MaxValue, LatestRoot(initial.Bytes));
        void Change(IBlockDevice disk, string name, List<(int Events, int Files)>? acknowledgements = null)
        {
            var ram = new Ram(new byte[1000000]); var engine = Mount(disk, ram);
            if (promotion)
            {
                CreateAndFlush(ref engine, ram, disk, name);
                if (acknowledgements is not null) acknowledgements.Add((((CrashDevice)disk).Events.Count, seedCount + 1));
            }
            else
            {
                CreateAndFlush(ref engine, ram, disk, name);
                if (acknowledgements is not null) acknowledgements.Add((((CrashDevice)disk).Events.Count, 1));
            }
            // A later publication can fail after the first FLUSH has replied.
            // Recovery must still retain every object covered by that reply.
            "Next"u8.CopyTo(ram.Bytes.AsSpan(110000));
            Assert.Equal(CpxError.None, engine.BeginLabel(110000, 4)); EngineTests.Drive(ref engine, ram, disk);
            Assert.Equal(CpxError.None, engine.BeginPersistenceBarrier()); EngineTests.Drive(ref engine, ram, disk);
        }
        var acknowledgements = new List<(int Events, int Files)>();
        var trace = new CrashDevice(initial.Bytes); Change(trace, changeName, acknowledgements);
        if (promotion) Assert.NotEqual(ulong.MaxValue, LatestRoot(trace.Bytes));
        for (int cut = 1; cut <= trace.Events.Count; cut++)
        for (int persistence = 0; persistence < 5; persistence++)
        {
            var fault = new CrashDevice(initial.Bytes) { Cut = cut, Persistence = persistence };
            Assert.Throws<PowerLoss>(() => Change(fault, changeName));
            var recovered = new MemoryDevice(fault.Durable);
            var before = new Cpx4Volume<MemoryDevice>(recovered, new byte[49152]); before.Check();
            ulong minimum = (ulong)seedCount;
            foreach (var acknowledgement in acknowledgements) if (acknowledgement.Events < cut) minimum = (ulong)acknowledgement.Files;
            Assert.True(before.FileCount >= minimum, $"Lost an acknowledged FLUSH at cut {cut}, persistence {persistence}.");
            var ram = new Ram(new byte[1000000]); var engine = Mount(recovered, ram);
            CreateAndFlush(ref engine, ram, recovered, "retry");
            var independent = new Cpx4Volume<MemoryDevice>(recovered, new byte[49152]); independent.Check();
            Assert.True(independent.TryGetInfo("retry"u8, out _));
            for (int n = 0; n < seedCount; n++) Assert.True(independent.TryGetInfo(System.Text.Encoding.ASCII.GetBytes($"seed{n:D2}"), out _));
        }
    }

    private static ulong LatestRoot(byte[] image)
    {
        ulong generation = 0, selected = 0;
        for (int copy = 0; copy < 3; copy++)
        {
            var root = image.AsSpan((130 + copy * 2) * 512, 1024);
            if (U64(root, 80) >= generation) { generation = U64(root, 80); selected = U64(root, 88); }
        }
        return selected;
    }

    private static void CreateAndFlush(ref CpxEngine<Ram> engine, Ram ram, IBlockDevice disk, string name)
    {
        byte[] encoded = System.Text.Encoding.ASCII.GetBytes(name); encoded.CopyTo(ram.Bytes, 100000);
        Assert.Equal(CpxError.None, engine.BeginCreate(Wide.From(1), 100000, (uint)encoded.Length)); EngineTests.Drive(ref engine, ram, disk);
        Assert.Equal(CpxError.None, engine.BeginPersistenceBarrier()); EngineTests.Drive(ref engine, ram, disk);
    }

    [Theory][InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)][InlineData(6)]
    public void BothDecodersRejectAuthenticatedMalformedCatalogues(int corruption)
    {
        var disk = new MemoryDevice(); Cpx4Volume<MemoryDevice>.FormatDirectInodes(disk, 512, new byte[49152], 12345, true, true);
        for (int copy = 0; copy < 3; copy++)
        {
            var root = disk.Bytes.AsSpan((130 + copy * 2) * 512, 1024);
            switch (corruption)
            {
                case 0: Put32(root, 496, 65); break;
                case 1: Put32(root, 500, 0); break;
                case 2: Put32(root, 496, 2); Put64(root, 512, U64(root, 504)); break;
                case 3: Put64(root, 504, (disk.SectorCount << 2) | 2); break;
                case 4: root[1016] = 1; break;
                case 5: Put64(root, 88, 0); break;
                case 6: Put64(root, 504, U64(root, 504) & ~3ul); break;
            }
            Put32(root, 8, Crc(root, true));
        }
        Assert.Throws<InvalidDataException>(() => new Cpx4Volume<MemoryDevice>(disk, new byte[49152]));
        var ram = new Ram(new byte[1000000]); var engine = new CpxEngine<Ram>(ram, 32);
        Assert.Equal(CpxError.None, engine.Initialize(512, 512, disk.SectorCount, 1, true, metadataPageBytes: 512));
        Assert.Equal(CpxError.None, engine.EnableBufferedWrites(300000, CpxEngine<Ram>.BufferedStorageBytes(512)));
        Assert.Equal(CpxError.None, engine.EnableShadowVolume(900000, 32768, 5));
        Assert.Equal(CpxError.None, engine.BeginOperation(CpxOperation.Mount));
        bool failed = false;
        for (int turn = 0; turn < 10000; turn++)
        {
            var result = engine.Step();
            if (result == StepResult.Failed) { failed = true; break; }
            Assert.NotEqual(StepResult.Complete, result);
            if (result != StepResult.NeedIo) continue;
            var io = engine.Request; Assert.Equal(IoKind.Read, io.Kind);
            disk.Read(io.Sector, ram.Bytes.AsSpan((int)io.Buffer, (int)io.Bytes));
            engine.CompleteIo(io.Token, io.Bytes, true);
        }
        Assert.True(failed); Assert.Equal(CpxError.Corrupt, engine.Error);
    }
}
