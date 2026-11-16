using CopperFS.Cpx4.Reference;
using CopperCPX.Reference.Tests;

namespace CopperCPX.Tests;

public class DirectInodeFilesystemTests
{
    [Fact]
    public void DirectorySplitsBufferRetirementAndInodeDeletionRemainIndependentlyReachable()
    {
        var disk = new MemoryDevice(); Cpx4Volume<MemoryDevice>.FormatDirectInodes(disk, 512, new byte[49152], 12345);
        var ram = new Ram(new byte[1000000]); var engine = Mount(disk, ram); ulong previous = 1;
        for (int n = 0; n < 20; n++)
        {
            byte[] name = System.Text.Encoding.ASCII.GetBytes($"file{n:D2}"); name.CopyTo(ram.Bytes, 100000);
            Assert.Equal(CpxError.None, engine.BeginCreate(Wide.From(1), 100000, (uint)name.Length)); EngineTests.Drive(ref engine, ram, disk);
            Assert.True((ulong)engine.MutatedObject > previous); previous = engine.MutatedObject;
        }
        Assert.Equal(CpxError.None, engine.BeginPersistenceBarrier()); EngineTests.Drive(ref engine, ram, disk);
        Assert.Equal(CpxError.None, engine.BeginRemount()); EngineTests.Drive(ref engine, ram, disk);
        var check = new Cpx4Volume<MemoryDevice>(disk, new byte[49152]); check.Check(); Assert.Equal(20UL, check.FileCount);
        for (int n = 0; n < 20; n += 2)
        {
            byte[] name = System.Text.Encoding.ASCII.GetBytes($"file{n:D2}"); name.CopyTo(ram.Bytes, 100000);
            Assert.Equal(CpxError.None, engine.BeginDelete(Wide.From(1), 100000, (uint)name.Length)); EngineTests.Drive(ref engine, ram, disk);
        }
        Assert.Equal(CpxError.None, engine.BeginPersistenceBarrier()); EngineTests.Drive(ref engine, ram, disk);
        check = new Cpx4Volume<MemoryDevice>(disk, new byte[49152]); check.Check(); Assert.Equal(10UL, check.FileCount);
        for (int n = 0; n < 20; n++)
            Assert.Equal((n & 1) != 0, check.TryGetInfo(System.Text.Encoding.ASCII.GetBytes($"file{n:D2}"), out _));
    }

    [Fact]
    public void InterruptedCreateCanRetryWithADifferentNameWithoutResurrectingAbandonedInodes()
    {
        var initial = new MemoryDevice(); Cpx4Volume<MemoryDevice>.FormatDirectInodes(initial, 512, new byte[49152], 12345);
        void Change(IBlockDevice disk, string name)
        {
            var ram = new Ram(new byte[1000000]); var engine = Mount(disk, ram);
            System.Text.Encoding.ASCII.GetBytes(name).CopyTo(ram.Bytes, 100000);
            Assert.Equal(CpxError.None, engine.BeginCreate(Wide.From(1), 100000, (uint)name.Length)); EngineTests.Drive(ref engine, ram, disk);
            Assert.Equal(CpxError.None, engine.BeginPersistenceBarrier()); EngineTests.Drive(ref engine, ram, disk);
        }
        var trace = new CrashDevice(initial.Bytes); Change(trace, "old");
        for (int cut = 1; cut <= trace.Events.Count; cut++)
        for (int persistence = 0; persistence < 5; persistence++)
        {
            var fault = new CrashDevice(initial.Bytes) { Cut = cut, Persistence = persistence };
            Assert.Throws<PowerLoss>(() => Change(fault, "old"));
            var recovered = new MemoryDevice(fault.Durable);
            var before = new Cpx4Volume<MemoryDevice>(recovered, new byte[49152]); before.Check(); ulong previous = before.FileCount;
            Change(recovered, "new");
            var after = new Cpx4Volume<MemoryDevice>(recovered, new byte[49152]); after.Check(); Assert.Equal(previous + 1, after.FileCount);
            Assert.True(after.TryGetInfo("new"u8, out _));
        }
    }

    private static CpxEngine<Ram> Mount(IBlockDevice disk, Ram ram, uint unit = 512)
    {
        var engine = new CpxEngine<Ram>(ram, 32);
        Assert.Equal(CpxError.None, engine.Initialize(512, unit, Wide.From((uint)disk.SectorCount), 1));
        Assert.Equal(CpxError.None, engine.EnableBufferedWrites(300000, CpxEngine<Ram>.BufferedStorageBytes(Math.Max(1024u, unit))));
        Assert.Equal(CpxError.None, engine.EnableShadowVolume(900000, 32768));
        Assert.Equal(CpxError.None, engine.BeginOperation(CpxOperation.Mount)); EngineTests.Drive(ref engine, ram, disk);
        return engine;
    }

    [Theory][InlineData(512)][InlineData(4096)]
    public void CreateWriteDeleteAndReusePreserveFullIdentityAndOwnership(int unit)
    {
        var disk = new MemoryDevice(); Cpx4Volume<MemoryDevice>.FormatDirectInodes(disk, unit, new byte[49152], 12345);
        new Cpx4Volume<MemoryDevice>(disk, new byte[49152]).Check();
        var ram = new Ram(new byte[1000000]); var engine = Mount(disk, ram, (uint)unit);
        "file"u8.CopyTo(ram.Bytes.AsSpan(100000));
        Assert.Equal(CpxError.None, engine.BeginCreate(Wide.From(1), 100000, 4)); EngineTests.Drive(ref engine, ram, disk);
        Wide old = engine.MutatedObject;
        byte[] expected = Enumerable.Range(0, 8192).Select(n => (byte)(n * 17 + 3)).ToArray(); expected.CopyTo(ram.Bytes, 100000);
        Assert.Equal(CpxError.None, engine.BeginMappedWrite(old, default, 100000, 8192)); EngineTests.Drive(ref engine, ram, disk);
        Assert.Equal(CpxError.None, engine.BeginPersistenceBarrier()); EngineTests.Drive(ref engine, ram, disk);
        Assert.Equal(CpxError.None, engine.BeginRemount()); EngineTests.Drive(ref engine, ram, disk);
        var independent = new Cpx4Volume<MemoryDevice>(disk, new byte[49152]); independent.Check();
        byte[] actual = new byte[8192]; Assert.Equal(8192, independent.Read((ulong)old, 0, actual)); Assert.Equal(expected, actual);
        "file"u8.CopyTo(ram.Bytes.AsSpan(100000));
        Assert.Equal(CpxError.None, engine.BeginDelete(Wide.From(1), 100000, 4)); EngineTests.Drive(ref engine, ram, disk);
        Assert.Equal(CpxError.None, engine.BeginPersistenceBarrier()); EngineTests.Drive(ref engine, ram, disk);
        Assert.Equal(CpxError.None, engine.BeginRemount()); EngineTests.Drive(ref engine, ram, disk);
        Assert.Equal(CpxError.None, engine.BeginCreate(Wide.From(1), 100000, 4)); EngineTests.Drive(ref engine, ram, disk);
        Wide fresh = engine.MutatedObject; Assert.NotEqual(old, fresh);
        Assert.Equal(CpxError.None, engine.BeginOperation(CpxOperation.ReadObject, old)); EngineTests.Drive(ref engine, ram, disk); Assert.False(engine.ObjectFound);
        Assert.Equal(CpxError.None, engine.BeginOperation(CpxOperation.ReadObject, fresh)); EngineTests.Drive(ref engine, ram, disk); Assert.True(engine.ObjectFound);
        Assert.Equal(CpxError.None, engine.BeginPersistenceBarrier()); EngineTests.Drive(ref engine, ram, disk);
        independent = new Cpx4Volume<MemoryDevice>(disk, new byte[49152]); independent.Check(); Assert.Equal(1UL, independent.FileCount);
    }
}
