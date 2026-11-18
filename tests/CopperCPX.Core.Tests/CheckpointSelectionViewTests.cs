using CopperFS.Cpx4.Reference;
using CopperCPX.Reference.Tests;

namespace CopperCPX.Tests;

public class CheckpointSelectionViewTests
{
    [Theory]
    [InlineData(512, 0, false)] [InlineData(512, 1, false)] [InlineData(512, 2, false)]
    [InlineData(512, 0, true)] [InlineData(512, 1, true)] [InlineData(512, 2, true)]
    [InlineData(4096, 0, false)] [InlineData(4096, 1, false)] [InlineData(4096, 2, false)]
    [InlineData(4096, 0, true)] [InlineData(4096, 1, true)] [InlineData(4096, 2, true)]
    public void FinalWinnerRemainsUsableRegardlessOfCandidateOrderAndInvalidNewerRoot(int unit, int newest, bool corruptNewest)
    {
        var disk = new MemoryDevice();
        Cpx4Volume<MemoryDevice>.FormatDirectInodes(disk, unit, new byte[49152], 12345, true, true);
        int rootBytes = Math.Max(1024, unit);
        for (int slot = 0; slot < 3; slot++)
        {
            var root = disk.Bytes.AsSpan(65536 + (slot + 1) * rootBytes, rootBytes);
            ulong generation = slot == newest ? 30ul : 10ul + (ulong)slot;
            Layout.Put64(root, 32, generation); Layout.Put64(root, 80, generation);
            Layout.Put32(root, 8, Layout.Crc(root, true));
            if (slot == newest && corruptNewest) root[8] ^= 0x40;
        }
        ulong expected = corruptNewest ? (ulong)(10 + (newest == 2 ? 1 : 2)) : 30;
        var ram = new Ram(new byte[1000000]); var engine = new CpxEngine<Ram>(ram, 32);
        Assert.Equal(CpxError.None, engine.Initialize(512, (uint)unit, disk.SectorCount, 1, metadataPageBytes: (uint)unit));
        Assert.Equal(CpxError.None, engine.EnableBufferedWrites(300000, CpxEngine<Ram>.BufferedStorageBytes((uint)unit)));
        Assert.Equal(CpxError.None, engine.EnableShadowVolume(900000, 32768, 5));
        Assert.Equal(CpxError.None, engine.BeginOperation(CpxOperation.Mount)); EngineTests.Drive(ref engine, ram, disk);
        Assert.Equal(expected, Layout.U64(ram.Bytes, 32 + 32));
        Assert.Equal(CpxError.None, engine.BeginOperation(CpxOperation.ReadObject, Wide.From(1))); EngineTests.Drive(ref engine, ram, disk);
        Assert.True(engine.ObjectFound);
        "view"u8.CopyTo(ram.Bytes.AsSpan(100000));
        Assert.Equal(CpxError.None, engine.BeginCreate(Wide.From(1), 100000, 4)); EngineTests.Drive(ref engine, ram, disk);
        Assert.Equal(CpxError.None, engine.BeginPersistenceBarrier()); EngineTests.Drive(ref engine, ram, disk);
        Assert.Equal(CpxError.None, engine.BeginRemount()); EngineTests.Drive(ref engine, ram, disk);
        Assert.Equal(expected + 1, Layout.U64(ram.Bytes, 32 + 32));
        new Cpx4Volume<MemoryDevice>(disk, new byte[49152]).Check();
    }
}
