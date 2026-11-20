using CopperFS.Cpx4.Reference;
using CopperCPX.Reference.Tests;

namespace CopperCPX.Tests;

public class ReadCapabilityTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public void RepeatedReadsRespectOffsetsEndOfFileProtectionResizeAndRemount(bool inlineCatalogue)
    {
        var disk = new MemoryDevice(); Cpx4Volume<MemoryDevice>.FormatDirectInodes(disk, 512, new byte[49152], 12345, true, inlineCatalogue);
        var ram = new Ram(new byte[1000000]); var engine = new CpxEngine<Ram>(ram, 32);
        Assert.Equal(CpxError.None, engine.Initialize(512, 512, disk.SectorCount, 1, metadataPageBytes:512));
        Assert.Equal(CpxError.None, engine.EnableBufferedWrites(300000, CpxEngine<Ram>.BufferedStorageBytes(512)));
        Assert.Equal(CpxError.None, engine.EnableShadowVolume(900000, 32768, inlineCatalogue ? 5u : 4u));
        Assert.Equal(CpxError.None, engine.BeginOperation(CpxOperation.Mount)); EngineTests.Drive(ref engine, ram, disk);
        "file"u8.CopyTo(ram.Bytes.AsSpan(100000));
        Assert.Equal(CpxError.None, engine.BeginCreate(Wide.From(1), 100000, 4)); EngineTests.Drive(ref engine, ram, disk); Wide id = engine.MutatedObject;
        byte[] payload = Enumerable.Range(0, 16384).Select(n => (byte)(n * 17 + (n >> 9))).ToArray(); payload.CopyTo(ram.Bytes, 100000);
        Assert.Equal(CpxError.None, engine.BeginMappedWrite(id, default, 100000, 16384)); EngineTests.Drive(ref engine, ram, disk);
        Assert.Equal(CpxError.None, engine.BeginPersistenceBarrier()); EngineTests.Drive(ref engine, ram, disk);
        Assert.Equal(CpxError.None, engine.BeginRemount()); EngineTests.Drive(ref engine, ram, disk);
        for (uint offset = 0; offset < 16384; offset += 4096)
        {
            Assert.Equal(CpxError.None, engine.BeginFileRead(id, Wide.From(offset), 120000, 4096)); EngineTests.Drive(ref engine, ram, disk);
            Assert.Equal(payload.AsSpan((int)offset, 4096).ToArray(), ram.Bytes.AsSpan(120000, 4096).ToArray());
        }
        Assert.Equal(CpxError.None, engine.BeginFileRead(id, default, 120000, 4096));
        Assert.Equal(CpxError.Busy, engine.BeginFileRead(id, default, 120000, 4096));
        Assert.Equal(StepResult.NeedIo, engine.Step()); var io = engine.Request;
        Assert.Equal(CpxError.StaleCompletion, engine.CompleteIo(io.Token + 1, io.Bytes, true));
        Assert.Equal(StepResult.NeedIo, engine.Step());
        disk.Read(io.Sector, ram.Bytes.AsSpan((int)io.Buffer, (int)io.Bytes)); engine.CompleteIo(io.Token, io.Bytes, true); EngineTests.Drive(ref engine, ram, disk);
        Assert.Equal(CpxError.None, engine.BeginProtection(id, 8)); EngineTests.Drive(ref engine, ram, disk);
        Assert.Equal(CpxError.None, engine.BeginFileRead(id, default, 120000, 4096));
        for (int turn = 0; turn < 1000 && engine.Step() != StepResult.Failed; turn++) { }
        Assert.Equal(CpxError.Protected, engine.Error);
        Assert.Equal(CpxError.None, engine.BeginProtection(id, 0)); EngineTests.Drive(ref engine, ram, disk);
        Assert.Equal(CpxError.None, engine.BeginResize(id, Wide.From(6003))); EngineTests.Drive(ref engine, ram, disk);
        ram.Bytes.AsSpan(120000, 16384).Fill(0xa5);
        Assert.Equal(CpxError.None, engine.BeginFileRead(id, default, 120000, 16384)); EngineTests.Drive(ref engine, ram, disk);
        Assert.Equal(6003u, engine.TransferredBytes); Assert.Equal(payload[..6003], ram.Bytes.AsSpan(120000, 6003).ToArray()); Assert.Equal(0xa5, ram.Bytes[126003]);
        Assert.Equal(CpxError.None, engine.BeginPersistenceBarrier()); EngineTests.Drive(ref engine, ram, disk);
        new Cpx4Volume<MemoryDevice>(disk, new byte[49152]).Check();
    }
}
