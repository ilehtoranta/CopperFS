using CopperFS.Cpx4.Reference;
using CopperCPX.Reference.Tests;

namespace CopperCPX.Tests;

public class CachedResizePacketTests
{
    private sealed class Fixture
    {
        internal readonly MemoryDevice Disk = new();
        internal readonly Ram Ram = new(new byte[1000000]);
        internal CpxEngine<Ram> Engine;
        internal CpxDosDispatcher<Ram> Dispatcher;
        internal readonly Wide Id;
        internal Fixture(bool catalogue)
        {
            Cpx4Volume<MemoryDevice>.FormatDirectInodes(Disk, 512, new byte[49152], 12345, true, catalogue);
            Engine = new(Ram, 32);
            Engine.Initialize(512, 512, Disk.SectorCount, 1, metadataPageBytes: 512);
            Engine.EnableBufferedWrites(300000, CpxEngine<Ram>.BufferedStorageBytes(512));
            Engine.EnableShadowVolume(900000, 32768, catalogue ? 5u : 4u);
            Engine.BeginOperation(CpxOperation.Mount); EngineTests.Drive(ref Engine, Ram, Disk);
            "file"u8.CopyTo(Ram.Bytes.AsSpan(100000));
            Engine.BeginCreate(Wide.From(1), 100000, 4); EngineTests.Drive(ref Engine, Ram, Disk); Id = Engine.MutatedObject;
            for (int n = 0; n < 8192; n++) Ram.Bytes[100000 + n] = (byte)(n * 17 + 3);
            Engine.BeginMappedWrite(Id, default, 100000, 8192); EngineTests.Drive(ref Engine, Ram, Disk);
            Dispatcher = new(Ram, 32); Dispatcher.Initialize(0xabc, 0xdef);
            Ram.Bytes[220000] = 4; "file"u8.CopyTo(Ram.Bytes.AsSpan(220001));
        }
        internal void Begin(uint action, params uint[] args)
        {
            Ram.Bytes.AsSpan(210000, 48).Clear(); Ram.Write32(210008, action);
            for (int n = 0; n < args.Length; n++) Ram.Write32(210020 + (uint)n * 4, args[n]);
            Assert.Equal(CpxError.None, Dispatcher.BeginPacket(210000));
        }
        internal (uint Result, uint Error) Drive()
        {
            for (int turn = 0; turn < 20000; turn++)
            {
                var result = Dispatcher.Step();
                if (result == StepResult.Complete) return (Dispatcher.Result, Dispatcher.DosError);
                Assert.NotEqual(StepResult.Failed, result); if (result != StepResult.NeedIo) continue;
                var io = Dispatcher.Request; var bytes = Ram.Bytes.AsSpan((int)io.Buffer, (int)io.Bytes);
                if (io.Kind == IoKind.Read) Disk.Read(io.Sector, bytes);
                else if (io.Kind == IoKind.Write) Disk.Write(io.Sector, bytes);
                else Disk.Flush();
                Assert.Equal(CpxError.None, Dispatcher.CompleteIo(io.Token, io.Bytes, true));
            }
            throw new InvalidOperationException("Packet did not finish.");
        }
        internal (uint Result, uint Error) Packet(uint action, params uint[] args) { Begin(action, args); return Drive(); }
        internal uint Open(uint fh)
        {
            Assert.Equal((uint.MaxValue, 0u), Packet(1004, fh >> 2, 0, 220000 >> 2)); return Ram.Read32(fh + 36);
        }
        internal void Resize(uint handle, uint length, uint expected, bool cached)
        {
            Begin(1022, handle, length, uint.MaxValue);
            Assert.Equal(StepResult.Pending, Dispatcher.Step());
            Assert.Equal((uint)(cached ? CpxOperation.Resize : CpxOperation.ReadObject), Ram.Read32(32 + 60));
            Assert.Equal((expected, 0u), Drive());
        }
    }

    [Theory]
    [InlineData(false, false)][InlineData(false, true)][InlineData(true, false)][InlineData(true, true)]
    public void AbsoluteResizeUsesCachedIdentityAndRetainsOtherPositionsProtectionAndColdFallback(bool catalogue, bool warm)
    {
        var f = new Fixture(catalogue); uint a = f.Open(200000), b = f.Open(200064);
        Assert.Equal((0u, 0u), f.Packet(1008, a, 8192, uint.MaxValue));
        Assert.Equal((0u, 0u), f.Packet(1008, b, 7000, uint.MaxValue));
        if (!warm)
        {
            uint objects = f.Ram.Read32(32 + 4088);
            for (uint n = 0; n < 8; n++) f.Ram.Write32(objects + n * 272 + 268, 0);
        }
        f.Resize(a, 6003, 7000, warm); // The other owner's position is a lower bound.
        Assert.Equal((uint.MaxValue, 0u), f.Packet(1007, b));
        f.Resize(a, 6003, 6003, true); f.Resize(a, 6003, 6003, true);
        Assert.Equal((uint.MaxValue, 211u), f.Packet(1022, 123, 6003, uint.MaxValue));
        Assert.Equal(CpxError.None, f.Engine.BeginProtection(f.Id, 4)); EngineTests.Drive(ref f.Engine, f.Ram, f.Disk);
        Assert.Equal((uint.MaxValue, 223u), f.Packet(1022, a, 6003, uint.MaxValue));
        Assert.Equal(CpxError.None, f.Engine.BeginProtection(f.Id, 0)); EngineTests.Drive(ref f.Engine, f.Ram, f.Disk);
        Assert.Equal((uint.MaxValue, 0u), f.Packet(1023, 1, 7));
        Assert.Equal((uint.MaxValue, 214u), f.Packet(1022, a, 6003, uint.MaxValue));
        Assert.Equal((uint.MaxValue, 0u), f.Packet(1023, 0, 7));
        Assert.Equal((uint.MaxValue, 0u), f.Packet(1007, a));
        Assert.Equal((uint.MaxValue, 0u), f.Packet(27));
        var check = new Cpx4Volume<MemoryDevice>(f.Disk, new byte[49152]); check.Check();
        byte[] bytes = new byte[8192]; Assert.Equal(6003, check.Read((ulong)f.Id, 0, bytes));
        for (int n = 0; n < 6003; n++) Assert.Equal((byte)(n * 17 + 3), bytes[n]);
    }
}
