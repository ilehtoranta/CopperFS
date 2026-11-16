using CopperFS.Cpx4.Reference;
using CopperCPX.Reference.Tests;

namespace CopperCPX.Tests;

public class AppendLeaseTests
{
    private sealed class Fixture
    {
        internal readonly MemoryDevice Disk = new(new byte[16 * 1024 * 1024]);
        internal readonly Ram Ram = new(new byte[1000000]);
        internal CpxEngine<Ram> Engine;
        internal readonly Wide Id;
        internal readonly List<byte> Expected = [];
        internal uint Hint => Ram.Read32(32 + 4076);
        internal Fixture(bool catalogue, uint unit = 512)
        {
            Cpx4Volume<MemoryDevice>.FormatDirectInodes(Disk, (int)unit, new byte[49152], 12345, true, catalogue);
            Engine = new(Ram, 32); Engine.Initialize(512, unit, Disk.SectorCount, 1, metadataPageBytes: unit);
            Engine.EnableBufferedWrites(300000, CpxEngine<Ram>.BufferedStorageBytes(unit));
            Engine.EnableShadowVolume(900000, 32768, catalogue ? 5u : 4u);
            Engine.BeginOperation(CpxOperation.Mount); EngineTests.Drive(ref Engine, Ram, Disk);
            "file"u8.CopyTo(Ram.Bytes.AsSpan(100000)); Engine.BeginCreate(Wide.From(1), 100000, 4); EngineTests.Drive(ref Engine, Ram, Disk); Id = Engine.MutatedObject;
            Append(3);
        }
        internal void Append(byte seed)
        {
            byte[] bytes = Enumerable.Range(0, 8192).Select(n => (byte)(n * 17 + seed)).ToArray(); bytes.CopyTo(Ram.Bytes, 100000);
            Assert.Equal(CpxError.None, Engine.BeginMappedWrite(Id, Wide.From((uint)Expected.Count), 100000, 8192));
            EngineTests.Drive(ref Engine, Ram, Disk); Assert.Equal(8192u, Engine.TransferredBytes); Expected.AddRange(bytes);
        }
        internal void Flush() { Assert.Equal(CpxError.None, Engine.BeginPersistenceBarrier()); EngineTests.Drive(ref Engine, Ram, Disk); }
        internal void Check(int? durableBytes = null)
        {
            var volume = new Cpx4Volume<MemoryDevice>(Disk, new byte[49152]); volume.Check();
            int size = durableBytes ?? Expected.Count; byte[] actual = new byte[size + 1];
            Assert.Equal(size, volume.Read((ulong)Id, 0, actual)); Assert.Equal(Expected.Take(size), actual.Take(size));
        }
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)] [InlineData(false, 3)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)] [InlineData(true, 3)]
    public void LeaseRetiresBeforeFlushReadsOtherAllocationAndDosFallback(bool catalogue, int boundary)
    {
        var f = new Fixture(catalogue); f.Append(9); f.Append(23);
        Assert.Equal(4u, f.Ram.Read32(f.Hint));
        if (boundary == 0) f.Flush();
        if (boundary == 1)
        {
            Assert.Equal(CpxError.None, f.Engine.BeginFileRead(f.Id, default, 120000, 8192)); EngineTests.Drive(ref f.Engine, f.Ram, f.Disk);
            Assert.Equal(f.Expected.Take(8192), f.Ram.Bytes.AsSpan(120000, 8192).ToArray());
        }
        if (boundary == 3)
        {
            var dispatcher = new CpxDosDispatcher<Ram>(f.Ram, 32); dispatcher.Initialize(0xabc, 0xdef);
            f.Ram.Bytes.AsSpan(210000, 48).Clear(); f.Ram.Write32(210008, 0x7fff);
            Assert.Equal(CpxError.None, dispatcher.BeginPacket(210000)); Assert.Equal(StepResult.Complete, dispatcher.Step()); Assert.NotEqual(0u, dispatcher.DosError);
        }
        "other"u8.CopyTo(f.Ram.Bytes.AsSpan(100000));
        Assert.Equal(CpxError.None, f.Engine.BeginCreate(Wide.From(1), 100000, 5)); EngineTests.Drive(ref f.Engine, f.Ram, f.Disk); Wide other = f.Engine.MutatedObject;
        f.Ram.Bytes.AsSpan(100000, 8192).Fill(0x67);
        Assert.Equal(CpxError.None, f.Engine.BeginMappedWrite(other, default, 100000, 8192)); EngineTests.Drive(ref f.Engine, f.Ram, f.Disk);
        f.Flush(); Assert.Equal(CpxError.None, f.Engine.BeginRemount()); EngineTests.Drive(ref f.Engine, f.Ram, f.Disk); f.Check();
        var check = new Cpx4Volume<MemoryDevice>(f.Disk, new byte[49152]); byte[] data = new byte[8192]; Assert.Equal(8192, check.Read((ulong)other, 0, data)); Assert.All(data, b => Assert.Equal(0x67, b));
    }

    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void PendingLeaseKeepsBusyStaleAndFailureProgressAndRecoversDurablePrefix(bool catalogue, bool cancel)
    {
        var f = new Fixture(catalogue); f.Flush(); f.Append(9); f.Append(23); Assert.Equal(4u, f.Ram.Read32(f.Hint));
        byte[] beforeInvalid = (byte[])f.Ram.Bytes.Clone();
        Assert.Equal(CpxError.InvalidArgument, f.Engine.BeginMappedWrite(f.Id, Wide.From(24576), 0, 8192)); Assert.Equal(beforeInvalid, f.Ram.Bytes);
        Assert.Equal(CpxError.None, f.Engine.BeginMappedWrite(f.Id, Wide.From(24576), 100000, 8192));
        byte[] admitted = (byte[])f.Ram.Bytes.Clone(); Assert.Equal(CpxError.Busy, f.Engine.BeginMappedWrite(f.Id, Wide.From(24576), 100000, 8192)); Assert.Equal(admitted, f.Ram.Bytes);
        Assert.Equal(StepResult.NeedIo, f.Engine.Step()); CpxIo io = f.Engine.Request;
        byte[] waiting = (byte[])f.Ram.Bytes.Clone(); Assert.Equal(CpxError.StaleCompletion, f.Engine.CompleteIo(io.Token + 1, io.Bytes, true)); Assert.Equal(waiting, f.Ram.Bytes);
        if (cancel) { f.Engine.Cancel(); Assert.Equal(StepResult.NeedIo, f.Engine.Step()); }
        // An uncommitted append may reach the device; recovery must still expose only the previous durable file.
        f.Disk.Write(io.Sector, f.Ram.Bytes.AsSpan((int)io.Buffer, (int)io.Bytes));
        Assert.Equal(cancel ? CpxError.Cancelled : CpxError.Io, f.Engine.CompleteIo(io.Token, cancel ? io.Bytes : io.Bytes - 1, true));
        Assert.Equal(StepResult.Failed, f.Engine.Step()); Assert.Equal(0u, f.Engine.TransferredBytes);
        Assert.Equal(CpxError.None, f.Engine.BeginRemount()); EngineTests.Drive(ref f.Engine, f.Ram, f.Disk); f.Check(8192);
    }

    [Theory] [InlineData(false, 512u)] [InlineData(true, 512u)]
    [InlineData(false, 1024u)] [InlineData(true, 1024u)]
    [InlineData(false, 2048u)] [InlineData(true, 2048u)]
    [InlineData(false, 4096u)] [InlineData(true, 4096u)]
    public void MultipleBitmapWindowsRetireBeforePartialOverwriteAndPersistEveryByte(bool catalogue, uint unit)
    {
        var f = new Fixture(catalogue, unit); int leased = 0;
        for (int chunk = 1; chunk <= 256; chunk++)
        {
            f.Append((byte)chunk);
            if (f.Ram.Read32(f.Hint) == 4) leased++;
        }
        Assert.True(leased > 200);
        f.Ram.Bytes.AsSpan(100000, 1024).Fill(0xd1);
        Assert.Equal(CpxError.None, f.Engine.BeginMappedWrite(f.Id, Wide.From(513), 100000, 1024)); EngineTests.Drive(ref f.Engine, f.Ram, f.Disk);
        for (int n = 513; n < 1537; n++) f.Expected[n] = 0xd1;
        f.Flush(); Assert.Equal(CpxError.None, f.Engine.BeginRemount()); EngineTests.Drive(ref f.Engine, f.Ram, f.Disk); f.Check();
    }
}
