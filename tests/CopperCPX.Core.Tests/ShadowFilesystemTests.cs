using CopperFS.Cpx4.Reference;
using CopperCPX.Reference.Tests;
using static CopperFS.Cpx4.Reference.Layout;

namespace CopperCPX.Tests;

public class ShadowFilesystemTests
{
    [Fact]
    public void DosFlushRetiresWritesAndLeavesOptionalDiscardToIdleMaintenance()
    {
        var disk = new MemoryDevice(); Cpx4Volume<MemoryDevice>.FormatShadow(disk, 512, new byte[49152], 12345);
        var ram = new Ram(new byte[1000000]); var engine = Mount(disk, ram);
        "file"u8.CopyTo(ram.Bytes.AsSpan(100000)); engine.BeginCreate(Wide.From(1), 100000, 4); EngineTests.Drive(ref engine, ram, disk);
        var dispatcher = new CpxDosDispatcher<Ram>(ram, 32); dispatcher.Initialize(0xabc, 0xdef);
        int discards = 0;
        void Drive(ref CpxDosDispatcher<Ram> current)
        {
            for (int turn = 0; turn < 20000; turn++)
            {
                var result = current.Step(); if (result == StepResult.Complete) return;
                Assert.NotEqual(StepResult.Failed, result); if (result != StepResult.NeedIo) continue;
                var io = current.Request; var bytes = ram.Bytes.AsSpan((int)io.Buffer, (int)io.Bytes);
                if (io.Kind == IoKind.Read) disk.Read(io.Sector, bytes);
                else if (io.Kind == IoKind.Write) disk.Write(io.Sector, bytes);
                else if (io.Kind == IoKind.Discard) { discards++; disk.Discard(io.Sector, io.Bytes / 512); }
                else disk.Flush();
                Assert.Equal(CpxError.None, current.CompleteIo(io.Token, io.Bytes, true));
            }
            Assert.Fail("DOS continuation did not finish.");
        }
        ram.Write32(200008, 27); Assert.Equal(CpxError.None, dispatcher.BeginPacket(200000)); Drive(ref dispatcher);
        Assert.Equal(0, discards); Assert.False(engine.HasBufferedChanges);
        Assert.True(dispatcher.TryBeginMaintenance()); Drive(ref dispatcher); Assert.Equal(1, discards);
        new Cpx4Volume<MemoryDevice>(disk, new byte[49152]).Check();
    }
    [Fact]
    public void ControlOnlyPublicationRecoversAtEverySectorAndBarrier()
    {
        var initial = new MemoryDevice(); Cpx4Volume<MemoryDevice>.FormatShadow(initial, 512, new byte[49152], 12345);
        void Change(IBlockDevice disk)
        {
            var ram = new Ram(new byte[1000000]); var engine = Mount(disk, ram);
            "New"u8.CopyTo(ram.Bytes.AsSpan(100000)); engine.BeginLabel(100000, 3); EngineTests.Drive(ref engine, ram, disk);
            engine.BeginPersistenceBarrier(); EngineTests.Drive(ref engine, ram, disk);
        }
        var trace = new CrashDevice(initial.Bytes); Change(trace);
        for (int cut = 1; cut <= trace.Events.Count; cut++)
        for (int persistence = 0; persistence < 5; persistence++)
        {
            var fault = new CrashDevice(initial.Bytes) { Cut = cut, Persistence = persistence };
            Assert.Throws<PowerLoss>(() => Change(fault));
            var recovered = new MemoryDevice(fault.Durable); var ram = new Ram(new byte[1000000]);
            var engine = Mount(recovered, ram, readOnly: true);
            Assert.Contains(VolumeLabelTests.Cached(engine, ram), new[] { "CopperCPX4", "New" });
            new Cpx4Volume<MemoryDevice>(recovered, new byte[49152]).Check();
        }
    }

    [Fact]
    public void ExtendedOwnershipStateFitsAlongsideTheFullReserveList()
    {
        var disk = new MemoryDevice(); Cpx4Volume<MemoryDevice>.FormatShadow(disk, 512, new byte[49152], 12345);
        var ram = new Ram(new byte[1000000]); var engine = Mount(disk, ram);
        "file"u8.CopyTo(ram.Bytes.AsSpan(100000)); engine.BeginCreate(Wide.From(1), 100000, 4); EngineTests.Drive(ref engine, ram, disk);
        Wide id = engine.MutatedObject;
        Assert.Equal(CpxError.None, engine.BeginOwner(id, 0x12345678)); EngineTests.Drive(ref engine, ram, disk);
        engine.BeginPersistenceBarrier(); EngineTests.Drive(ref engine, ram, disk);
        engine.BeginRemount(); EngineTests.Drive(ref engine, ram, disk);
        var check = new Cpx4Volume<MemoryDevice>(disk, new byte[49152]); check.Check();
        Assert.True(check.OwnershipEnabled); Assert.Equal(0x12345678u, check.ReadOwner((ulong)id));
    }
    [Theory][InlineData(512)][InlineData(4096)]
    public void LegacyShadowFixturesRemainReadableAndWritable(int unit)
    {
        var disk = new MemoryDevice(); Cpx4Volume<MemoryDevice>.FormatShadow(disk, unit, new byte[49152], 12345, compactControl: false);
        var ram = new Ram(new byte[1000000]); var engine = Mount(disk, ram, (uint)unit);
        "legacy"u8.CopyTo(ram.Bytes.AsSpan(100000)); engine.BeginCreate(Wide.From(1), 100000, 6); EngineTests.Drive(ref engine, ram, disk);
        engine.BeginPersistenceBarrier(); EngineTests.Drive(ref engine, ram, disk);
        engine.BeginRemount(); EngineTests.Drive(ref engine, ram, disk);
        var check = new Cpx4Volume<MemoryDevice>(disk, new byte[49152]); check.Check(); Assert.Equal(1UL, check.FileCount);
    }

    [Theory][InlineData(512)][InlineData(4096)]
    public void AppendedShadowContextPreservesCompactBufferCapacityAcrossRemount(int unit)
    {
        var disk = new MemoryDevice(); Cpx4Volume<MemoryDevice>.FormatShadow(disk, unit, new byte[49152], 12345);
        var ram = new Ram(new byte[1000000]); var engine = new CpxEngine<Ram>(ram, 32);
        uint page = (uint)Math.Max(1024, unit), normal = CpxEngine<Ram>.CompactBufferedStorageBytes(page), extra = CpxEngine<Ram>.ShadowStorageBytes(page);
        engine.Initialize(512, (uint)unit, Wide.From((uint)disk.SectorCount), 1);
        Assert.Equal(CpxError.None, engine.EnableBufferedWrites(300000, normal + extra));
        Assert.Equal(CpxError.None, engine.EnableShadowVolume(300000 + normal, extra));
        engine.BeginOperation(CpxOperation.Mount); EngineTests.Drive(ref engine, ram, disk);
        "compact"u8.CopyTo(ram.Bytes.AsSpan(100000)); engine.BeginCreate(Wide.From(1), 100000, 7); EngineTests.Drive(ref engine, ram, disk);
        engine.BeginPersistenceBarrier(); EngineTests.Drive(ref engine, ram, disk);
        engine.BeginRemount(); EngineTests.Drive(ref engine, ram, disk);
        Assert.Equal(4u, ram.Read32(ram.Read32(32 + 4084) + 4));
        new Cpx4Volume<MemoryDevice>(disk, new byte[49152]).Check();
    }

    [Fact]
    public void ControlOnlyPublicationWritesJustTwoCheckpointsAndKeepsTheLabel()
    {
        var disk = new MemoryDevice(); Cpx4Volume<MemoryDevice>.FormatShadow(disk, 512, new byte[49152], 12345);
        var ram = new Ram(new byte[1000000]); var engine = Mount(disk, ram);
        "New"u8.CopyTo(ram.Bytes.AsSpan(100000)); engine.BeginLabel(100000, 3); EngineTests.Drive(ref engine, ram, disk);
        int writes = disk.Writes, flushes = disk.Flushes;
        engine.BeginPersistenceBarrier(); EngineTests.Drive(ref engine, ram, disk);
        Assert.Equal(2, disk.Writes - writes); Assert.Equal(3, disk.Flushes - flushes);
        engine.BeginRemount(); EngineTests.Drive(ref engine, ram, disk);
        Assert.Equal("New", VolumeLabelTests.Cached(engine, ram));
        new Cpx4Volume<MemoryDevice>(disk, new byte[49152]).Check();
    }
    private static CpxEngine<Ram> Mount(IBlockDevice disk, Ram ram, uint unit = 512, bool readOnly = false)
    {
        var engine = new CpxEngine<Ram>(ram, 32);
        Assert.Equal(CpxError.None, engine.Initialize(512, unit, Wide.From((uint)disk.SectorCount), 1, readOnly));
        Assert.Equal(CpxError.None, engine.EnableBufferedWrites(300000, CpxEngine<Ram>.BufferedStorageBytes(Math.Max(1024u, unit))));
        Assert.Equal(CpxError.None, engine.EnableShadowVolume(900000, 32768));
        Assert.Equal(CpxError.None, engine.BeginOperation(CpxOperation.Mount)); EngineTests.Drive(ref engine, ram, disk);
        return engine;
    }

    [Theory]
    [InlineData(2, IoKind.Read, 1)]
    [InlineData(2, IoKind.Write, 1)]
    [InlineData(3, IoKind.Write, 1)]
    [InlineData(3, IoKind.Flush, 1)]
    [InlineData(3, IoKind.Flush, 2)]
    public void CancellationReapsTheActualOwnerBeforeRemount(uint owner, IoKind kind, int occurrence)
    {
        var disk = new MemoryDevice(); Cpx4Volume<MemoryDevice>.FormatShadow(disk, 512, new byte[49152], 12345);
        var ram = new Ram(new byte[1000000]); var engine = Mount(disk, ram);
        "seed"u8.CopyTo(ram.Bytes.AsSpan(100000)); engine.BeginCreate(Wide.From(1), 100000, 4); EngineTests.Drive(ref engine, ram, disk);
        engine.BeginPersistenceBarrier(); EngineTests.Drive(ref engine, ram, disk);
        "other"u8.CopyTo(ram.Bytes.AsSpan(100000)); engine.BeginCreate(Wide.From(1), 100000, 5); EngineTests.Drive(ref engine, ram, disk);
        engine.BeginPersistenceBarrier();
        int matched = 0;
        for (int turn = 0; turn < 20000; turn++)
        {
            StepResult result = engine.Step();
            Assert.NotEqual(StepResult.Failed, result); Assert.NotEqual(StepResult.Complete, result);
            if (result != StepResult.NeedIo) continue;
            CpxIo request = engine.Request;
            bool stop = ram.Read32(900000) == owner && request.Kind == kind && ++matched == occurrence;
            if (stop)
            {
                engine.Cancel();
                Assert.True(engine.HasOutstandingIo);
                Assert.Equal(CpxError.Busy, engine.Initialize(512, 512, Wide.From((uint)disk.SectorCount), 2));
                Assert.Equal(CpxError.StaleCompletion, engine.CompleteIo(request.Token + 1, request.Bytes, true));
            }
            var buffer = ram.Bytes.AsSpan((int)request.Buffer, (int)request.Bytes);
            if (request.Kind == IoKind.Read) disk.Read(request.Sector, buffer);
            else if (request.Kind == IoKind.Write) disk.Write(request.Sector, buffer);
            else disk.Flush();
            Assert.Equal(stop ? CpxError.Cancelled : CpxError.None, engine.CompleteIo(request.Token, request.Bytes, true));
            if (!stop) continue;
            Assert.Equal(StepResult.Failed, engine.Step());
            Assert.Equal(CpxError.None, engine.BeginRemount()); EngineTests.Drive(ref engine, ram, disk);
            var check = new Cpx4Volume<MemoryDevice>(disk, new byte[49152]); check.Check();
            Assert.InRange(check.FileCount, 1UL, 2UL);
            return;
        }
        Assert.Fail("Did not encounter the requested publication I/O owner.");
    }

    [Fact]
    public void AuthenticatedMapCacheNeverReusesSelectorsAcrossPublications()
    {
        var disk = new MemoryDevice(); Cpx4Volume<MemoryDevice>.FormatShadow(disk, 512, new byte[49152], 12345);
        var ram = new Ram(new byte[1000000]); var engine = Mount(disk, ram);
        "file"u8.CopyTo(ram.Bytes.AsSpan(100000)); engine.BeginCreate(Wide.From(1), 100000, 4); EngineTests.Drive(ref engine, ram, disk);
        Wide id = engine.MutatedObject;
        for (uint group = 0; group < 4; group++)
        {
            ram.Bytes.AsSpan(100000, 1024).Fill((byte)(group + 1));
            engine.BeginMappedWrite(id, Wide.From(group * 1024), 100000, 1024); EngineTests.Drive(ref engine, ram, disk);
            engine.BeginPersistenceBarrier(); EngineTests.Drive(ref engine, ram, disk);
            engine.BeginOperation(CpxOperation.ReadMetadata, engine.StatePageAddress); EngineTests.Drive(ref engine, ram, disk);
            Assert.Equal((ulong)engine.Transaction, U64(ram.Bytes.AsSpan((int)engine.ReadBuffer), 32));
        }
        var check = new Cpx4Volume<MemoryDevice>(disk, new byte[49152]); check.Check();
        byte[] actual = new byte[4096]; Assert.Equal(4096, check.Read((ulong)id, 0, actual));
        for (int n = 0; n < actual.Length; n++) Assert.Equal((byte)(n / 1024 + 1), actual[n]);
    }

    [Fact]
    public void FilesystemRetirementUsesTwoBarriersAndReadOnlyMountNeedsNoRepair()
    {
        var disk = new MemoryDevice(); Cpx4Volume<MemoryDevice>.FormatShadow(disk, 512, new byte[49152], 12345);
        var ram = new Ram(new byte[1000000]); var engine = Mount(disk, ram);
        "file"u8.CopyTo(ram.Bytes.AsSpan(100000)); engine.BeginCreate(Wide.From(1), 100000, 4); EngineTests.Drive(ref engine, ram, disk);
        int before = disk.Flushes;
        engine.BeginPersistenceBarrier(); EngineTests.Drive(ref engine, ram, disk);
        Assert.Equal(3, disk.Flushes - before); // two publication barriers plus explicit final drain
        before = disk.Flushes; int writes = disk.Writes;
        Mount(disk, new Ram(new byte[1000000]), readOnly: true);
        Assert.Equal(before, disk.Flushes); Assert.Equal(writes, disk.Writes);
    }

    [Fact]
    public void SuccessiveFilesystemPublicationsRecoverAtEverySectorAndBarrier()
    {
        var initial = new MemoryDevice(); Cpx4Volume<MemoryDevice>.FormatShadow(initial, 512, new byte[49152], 12345);
        var seedRam = new Ram(new byte[1000000]); var seed = Mount(initial, seedRam);
        "file"u8.CopyTo(seedRam.Bytes.AsSpan(100000)); seed.BeginCreate(Wide.From(1), 100000, 4); EngineTests.Drive(ref seed, seedRam, initial);
        Wide id = seed.MutatedObject; seed.BeginPersistenceBarrier(); EngineTests.Drive(ref seed, seedRam, initial);
        byte[] payload = Enumerable.Range(0, 3072).Select(n => (byte)(n * 19 + n / 1024)).ToArray();
        void Write(IBlockDevice disk, List<int>? acknowledgements = null)
        {
            var ram = new Ram(new byte[1000000]); var engine = Mount(disk, ram);
            payload.CopyTo(ram.Bytes, 100000);
            for (uint group = 0; group < 3; group++)
            {
                Assert.Equal(CpxError.None, engine.BeginMappedWrite(id, Wide.From(group * 1024), 100000 + group * 1024, 1024));
                EngineTests.Drive(ref engine, ram, disk);
                Assert.Equal(CpxError.None, engine.BeginPersistenceBarrier()); EngineTests.Drive(ref engine, ram, disk);
                if (acknowledgements is not null) acknowledgements.Add(((CrashDevice)disk).Events.Count);
            }
        }
        var trace = new CrashDevice(initial.Bytes); var acknowledgedAt = new List<int>(); Write(trace, acknowledgedAt);
        for (int cut = 1; cut <= trace.Events.Count; cut++)
        for (int persistence = 0; persistence < 5; persistence++)
        {
            var fault = new CrashDevice(initial.Bytes) { Cut = cut, Persistence = persistence };
            Assert.Throws<PowerLoss>(() => Write(fault));
            var recovered = new MemoryDevice(fault.Durable);
            Mount(recovered, new Ram(new byte[1000000]), readOnly: true);
            var check = new Cpx4Volume<MemoryDevice>(recovered, new byte[49152]); check.Check();
            byte[] actual = new byte[payload.Length]; int length = check.Read((ulong)id, 0, actual);
            Assert.True(length is 0 or 1024 or 2048 or 3072, $"cut={cut}, persistence={persistence}, length={length}");
            Assert.True(length >= 1024 * acknowledgedAt.Count(at => at < cut), $"Lost acknowledged group at cut={cut}, persistence={persistence}");
            Assert.Equal(payload[..length], actual[..length]);
        }
    }
    [Theory][InlineData(512)][InlineData(1024)][InlineData(2048)][InlineData(4096)]
    public void BufferedFilesystemPublishesNamespaceAndPayloadAcrossSuccessiveGenerations(int unit)
    {
        var disk = new Disk { Bytes = new byte[16 * 1024 * 1024] };
        Cpx4Volume<Disk>.FormatShadow(disk, unit, new byte[49152], 12345);
        var ram = new Ram(new byte[1000000]); var engine = new CpxEngine<Ram>(ram, 32);
        Assert.Equal(CpxError.None, engine.Initialize(512, (uint)unit, Wide.From((uint)disk.SectorCount), 1));
        Assert.Equal(CpxError.None, engine.EnableBufferedWrites(300000, CpxEngine<Ram>.BufferedStorageBytes((uint)Math.Max(1024, unit))));
        Assert.Equal(CpxError.None, engine.EnableShadowVolume(900000, 32768));
        Assert.Equal(CpxError.None, engine.BeginOperation(CpxOperation.Mount)); EngineTests.Drive(ref engine, ram, disk);
        "file"u8.CopyTo(ram.Bytes.AsSpan(100000));
        Assert.Equal(CpxError.None, engine.BeginCreate(Wide.From(1), 100000, 4)); EngineTests.Drive(ref engine, ram, disk);
        Wide id = engine.MutatedObject;
        byte[] expected = Enumerable.Range(0, 8192).Select(n => (byte)(n * 17 + 3)).ToArray();
        expected.CopyTo(ram.Bytes, 100000);
        Assert.Equal(CpxError.None, engine.BeginMappedWrite(id, default, 100000, (uint)expected.Length)); EngineTests.Drive(ref engine, ram, disk);
        Assert.True(engine.HasBufferedChanges);
        Assert.Equal(CpxError.None, engine.BeginPersistenceBarrier()); EngineTests.Drive(ref engine, ram, disk);
        Assert.False(engine.HasBufferedChanges);
        Assert.Equal(CpxError.None, engine.BeginRemount()); EngineTests.Drive(ref engine, ram, disk);
        "file"u8.CopyTo(ram.Bytes.AsSpan(100000));
        Assert.Equal(CpxError.None, engine.BeginLookup(Wide.From(1), 100000, 4)); EngineTests.Drive(ref engine, ram, disk);
        Assert.True(engine.LookupFound); Assert.Equal(id, engine.LookupId);
        Assert.Equal(CpxError.None, engine.BeginFileRead(id, default, 100000, (uint)expected.Length)); EngineTests.Drive(ref engine, ram, disk);
        Assert.Equal(expected, ram.Bytes.AsSpan(100000, expected.Length).ToArray());
        expected[77] = 91; ram.Bytes[100000] = 91;
        Assert.Equal(CpxError.None, engine.BeginMappedWrite(id, Wide.From(77), 100000, 1)); EngineTests.Drive(ref engine, ram, disk);
        Assert.Equal(CpxError.None, engine.BeginPersistenceBarrier()); EngineTests.Drive(ref engine, ram, disk);
        var independent = new Cpx4Volume<Disk>(disk, new byte[49152]); independent.Check();
        Assert.Equal(1UL, independent.FileCount);
        byte[] actual = new byte[expected.Length]; Assert.Equal(expected.Length, independent.Read((ulong)id, 0, actual)); Assert.Equal(expected, actual);
    }
    [Theory][InlineData(512)][InlineData(1024)][InlineData(2048)][InlineData(4096)]
    public void CoreMountReadsIndependentShadowFixtureUsingCallerOwnedStorage(int unit)
    {
        var disk = new Disk { Bytes = new byte[16 * 1024 * 1024] };
        Cpx4Volume<Disk>.FormatShadow(disk, unit, new byte[49152], 12345);
        var ram = new Ram(new byte[1000000]); var engine = new CpxEngine<Ram>(ram, 32);
        Assert.Equal(CpxError.None, engine.Initialize(512, (uint)unit, Wide.From((uint)disk.SectorCount), 1, true));
        Assert.Equal(CpxError.None, engine.EnableShadowVolume(900000, 32768));
        Assert.Equal(CpxError.None, engine.BeginOperation(CpxOperation.Mount));
        EngineTests.Drive(ref engine, ram, disk);
        Assert.True(engine.IsMounted);
        Assert.Equal(CpxError.None, engine.BeginOperation(CpxOperation.ReadObject, Wide.From(1)));
        EngineTests.Drive(ref engine, ram, disk);
        Assert.True(engine.ObjectFound);
        Assert.Equal(2, ram.Read8(engine.ObjectRecord + 84));
    }
    [Theory][InlineData(512)][InlineData(1024)][InlineData(2048)][InlineData(4096)]
    public void IndependentFormatterAndOwnershipCheckerReserveBothMetadataCopies(int unit)
    {
        var disk = new Disk { Bytes = new byte[16 * 1024 * 1024] };
        Cpx4Volume<Disk>.FormatShadow(disk, unit, new byte[49152], 12345);
        var volume = new Cpx4Volume<Disk>(disk, new byte[49152]);
        var report = volume.Check();
        Assert.Equal(0UL, report.Files);
        Assert.Equal(32, volume.AvailableReservePages);
        Assert.Equal((ulong)(66 * Math.Max(1024, unit) / unit), report.MetadataUnits);
        Assert.Equal(0UL, volume.Generation);
        bool rejected = false;
        try { volume.Create("unsupported"u8, out _); } catch (InvalidOperationException) { rejected = true; }
        Assert.True(rejected);
        Assert.Equal(0x43505835u, U32(disk.Bytes.AsSpan(65536), 0));
    }
}
