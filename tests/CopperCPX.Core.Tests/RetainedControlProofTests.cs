using CopperFS.Cpx4.Reference;
using CopperCPX.Reference.Tests;

namespace CopperCPX.Tests;

public class RetainedControlProofTests
{
    private const uint Storage = 900000;

    [Theory]
    [InlineData(512, 4u)] [InlineData(512, 5u)] [InlineData(4096, 4u)] [InlineData(4096, 5u)]
    public void ResettingAllocationHintDoesNotEraseProofBeforeRootValidation(int unit, uint features)
    {
        var disk = Fresh(unit, features); var ram = new Ram(new byte[1000000]);
        var engine = Mount(disk, ram, unit, features);
        uint end = Storage + CpxEngine<Ram>.ShadowStorageBytes((uint)unit);
        ram.Write32(end - 8, 0); ram.Write32(end - 4, 12345);
        AssertProof(ram, unit, features);
        Assert.Equal(CpxError.None, engine.BeginRemount());
        for (int turn = 0; turn < 20000; turn++)
        {
            var step = engine.Step();
            Assert.NotEqual(StepResult.Failed, step); Assert.NotEqual(StepResult.Complete, step);
            if (step != StepResult.NeedIo) continue;
            var io = engine.Request; Assert.Equal(IoKind.Read, io.Kind);
            if (io.Sector == Wide.From((uint)(65536 + Math.Max(unit, features == 5 ? 1024 : unit)) / 512))
            {
                AssertProof(ram, unit, features);
                Assert.Equal(0u, ram.Read32(end - 8)); Assert.Equal(0u, ram.Read32(end - 4));
                return;
            }
            disk.Read(io.Sector, ram.Bytes.AsSpan((int)io.Buffer, (int)io.Bytes));
            Assert.Equal(CpxError.None, engine.CompleteIo(io.Token, io.Bytes, true));
        }
        Assert.Fail("Recovery did not request the first checkpoint.");
    }

    [Theory]
    [InlineData(512, 4u)] [InlineData(512, 5u)] [InlineData(4096, 4u)] [InlineData(4096, 5u)]
    public void OnlySuccessfulPublicationRetainsAnIndependentlyValidRoot(int unit, uint features)
    {
        var disk = Fresh(unit, features); var ram = new Ram(new byte[1000000]);
        var engine = Mount(disk, ram, unit, features);
        AssertProof(ram, unit, features);
        for (int pass = 0; pass < 3; pass++)
        {
            ram.Bytes[100000] = (byte)('a' + pass);
            Assert.Equal(CpxError.None, engine.BeginCreate(Wide.From(1), 100000, 1)); EngineTests.Drive(ref engine, ram, disk);
            Assert.Equal(CpxError.None, engine.BeginPersistenceBarrier()); EngineTests.Drive(ref engine, ram, disk);
            AssertProof(ram, unit, features);
            Assert.Equal(CpxError.None, engine.BeginRemount()); EngineTests.Drive(ref engine, ram, disk);
            AssertProof(ram, unit, features);
        }
        new Cpx4Volume<MemoryDevice>(disk, new byte[49152]).Check();
    }

    [Theory]
    [InlineData(512, 0)] [InlineData(512, 1)] [InlineData(512, 2)] [InlineData(512, 3)] [InlineData(512, 4)]
    [InlineData(4096, 0)] [InlineData(4096, 1)] [InlineData(4096, 2)] [InlineData(4096, 3)] [InlineData(4096, 4)]
    public void ChangedMediaCannotBorrowAnUnrelatedOrInvalidRetainedProof(int unit, int damage)
    {
        var disk = Fresh(unit, 5); var ram = new Ram(new byte[1000000]);
        var engine = Mount(disk, ram, unit, 5); AssertProof(ram, unit, 5);
        int bytes = Math.Max(unit, 1024);
        if (damage is 2 or 3)
        {
            foreach (int at in new[] { 65536, (disk.Bytes.Length & ~(unit - 1)) - unit })
            {
                var anchor = disk.Bytes.AsSpan(at, unit);
                Layout.Put64(anchor, damage == 2 ? 72 : 96, damage == 2 ? 12346ul : 4ul);
                Layout.Put32(anchor, 8, Layout.Crc(anchor, true));
            }
        }
        else for (int slot = 0; slot < (damage == 4 ? 1 : 3); slot++)
        {
            var root = disk.Bytes.AsSpan(65536 + (slot + 1) * bytes, bytes);
            if (damage == 0) root[8] ^= 0x40;
            else
            {
                if (damage == 1) Layout.Put16(root, 56, 0);
                else root[96] ^= 0x40; // Valid CRC, conflicting same-generation body.
                Layout.Put32(root, 8, Layout.Crc(root, true));
            }
        }
        Assert.Equal(CpxError.None, engine.BeginRemount());
        for (int turn = 0; turn < 20000; turn++)
        {
            var step = engine.Step();
            if (step == StepResult.Failed) { Assert.Equal(CpxError.Corrupt, engine.Error); Assert.False(engine.IsMounted); return; }
            Assert.NotEqual(StepResult.Complete, step);
            if (step != StepResult.NeedIo) continue;
            var io = engine.Request; Assert.Equal(IoKind.Read, io.Kind);
            disk.Read(io.Sector, ram.Bytes.AsSpan((int)io.Buffer, (int)io.Bytes));
            Assert.Equal(CpxError.None, engine.CompleteIo(io.Token, io.Bytes, true));
        }
        Assert.Fail("Changed media was not rejected.");
    }

    [Theory] [InlineData(512)] [InlineData(4096)]
    public void FailedRootBarrierDoesNotRetainAnUnpublishedProof(int unit)
    {
        var disk = Fresh(unit, 5); var ram = new Ram(new byte[1000000]);
        var engine = Mount(disk, ram, unit, 5);
        "failure"u8.CopyTo(ram.Bytes.AsSpan(100000));
        Assert.Equal(CpxError.None, engine.BeginCreate(Wide.From(1), 100000, 7)); EngineTests.Drive(ref engine, ram, disk);
        Assert.Equal(CpxError.None, engine.BeginPersistenceBarrier());
        int flushes = 0; bool failed = false;
        for (int turn = 0; turn < 20000; turn++)
        {
            var step = engine.Step();
            if (step == StepResult.Failed) { failed = true; break; }
            Assert.NotEqual(StepResult.Complete, step);
            if (step != StepResult.NeedIo) continue;
            var io = engine.Request; bool success = true;
            if (io.Kind == IoKind.Read) disk.Read(io.Sector, ram.Bytes.AsSpan((int)io.Buffer, (int)io.Bytes));
            else if (io.Kind == IoKind.Write) disk.Write(io.Sector, ram.Bytes.AsSpan((int)io.Buffer, (int)io.Bytes));
            else if (io.Kind == IoKind.Flush) success = ++flushes != 2;
            engine.CompleteIo(io.Token, io.Bytes, success);
        }
        Assert.True(failed); Assert.Equal(2, flushes); Assert.Equal(CpxError.Io, engine.Error);
        Assert.Equal(0u, ram.Read32(Storage + CpxEngine<Ram>.ShadowStorageBytes((uint)unit) - 16));
        Assert.Equal(CpxError.None, engine.BeginRemount()); EngineTests.Drive(ref engine, ram, disk);
        AssertProof(ram, unit, 5);
        new Cpx4Volume<MemoryDevice>(disk, new byte[49152]).Check();
    }

    [Theory] [InlineData(512)] [InlineData(4096)]
    public void RetainedProofCanBeRevalidatedOnAReadOnlyMount(int unit)
    {
        var disk = Fresh(unit, 5); var ram = new Ram(new byte[1000000]);
        var engine = Mount(disk, ram, unit, 5);
        Assert.Equal(CpxError.None, engine.Initialize(512, (uint)unit, disk.SectorCount, 2, true, (uint)unit));
        Assert.Equal(CpxError.None, engine.BeginOperation(CpxOperation.Mount)); EngineTests.Drive(ref engine, ram, disk);
        Assert.True(engine.IsReadOnly); AssertProof(ram, unit, 5);
        Assert.Equal(CpxError.None, engine.BeginOperation(CpxOperation.ReadObject, Wide.From(1))); EngineTests.Drive(ref engine, ram, disk);
        Assert.True(engine.ObjectFound);
    }

    private static MemoryDevice Fresh(int unit, uint features)
    {
        var disk = new MemoryDevice();
        Cpx4Volume<MemoryDevice>.FormatDirectInodes(disk, unit, new byte[49152], 12345, true, features == 5);
        return disk;
    }

    private static CpxEngine<Ram> Mount(MemoryDevice disk, Ram ram, int unit, uint features)
    {
        var engine = new CpxEngine<Ram>(ram, 32);
        Assert.Equal(CpxError.None, engine.Initialize(512, (uint)unit, disk.SectorCount, 1, metadataPageBytes: (uint)unit));
        Assert.Equal(CpxError.None, engine.EnableBufferedWrites(300000, CpxEngine<Ram>.BufferedStorageBytes((uint)unit)));
        uint bytes = CpxEngine<Ram>.ShadowStorageBytes((uint)unit);
        ram.Bytes.AsSpan((int)(Storage + bytes), 32).Fill(0xa5);
        Assert.Equal(CpxError.None, engine.EnableShadowVolume(Storage, bytes, features));
        Assert.Equal(CpxError.None, engine.BeginOperation(CpxOperation.Mount)); EngineTests.Drive(ref engine, ram, disk);
        return engine;
    }

    private static void AssertProof(Ram ram, int unit, uint features)
    {
        int bytes = Math.Max(unit, features == 5 ? 1024 : unit);
        var root = ram.Bytes.AsSpan((int)Storage + 288 + 3 * unit, bytes);
        Assert.Equal(Layout.Crc(root, true), Layout.U32(root, 8));
        uint end = Storage + CpxEngine<Ram>.ShadowStorageBytes((uint)unit);
        Assert.Equal((uint)bytes, ram.Read32(end - 16)); Assert.Equal(features, ram.Read32(end - 12));
        Assert.All(ram.Bytes[(int)end..((int)end + 32)], value => Assert.Equal(0xa5, value));
    }
}
