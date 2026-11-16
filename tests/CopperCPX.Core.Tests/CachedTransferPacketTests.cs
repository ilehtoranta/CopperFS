using System.Reflection;
using CopperFS.Cpx4.Reference;
using CopperCPX.Reference.Tests;

namespace CopperCPX.Tests;

public class CachedTransferPacketTests
{
    private sealed class Fixture
    {
        internal readonly MemoryDevice Disk = new();
        internal readonly Ram Ram = new(new byte[1000000]);
        internal CpxEngine<Ram> Engine;
        internal CpxDosDispatcher<Ram> Dispatcher;
        internal readonly Wide Id;
        internal readonly byte[] Payload = Enumerable.Range(0, 8192).Select(n => (byte)(n * 17 + (n >> 9))).ToArray();
        internal Fixture(bool catalogue, bool empty = false)
        {
            Cpx4Volume<MemoryDevice>.FormatDirectInodes(Disk, 512, new byte[49152], 12345, true, catalogue);
            Engine = new(Ram, 32); Engine.Initialize(512, 512, Disk.SectorCount, 1, metadataPageBytes: 512);
            Engine.EnableBufferedWrites(300000, CpxEngine<Ram>.BufferedStorageBytes(512)); Engine.EnableShadowVolume(900000, 32768, catalogue ? 5u : 4u);
            Engine.BeginOperation(CpxOperation.Mount); EngineTests.Drive(ref Engine, Ram, Disk);
            "file"u8.CopyTo(Ram.Bytes.AsSpan(100000)); Engine.BeginCreate(Wide.From(1), 100000, 4); EngineTests.Drive(ref Engine, Ram, Disk); Id = Engine.MutatedObject;
            if (!empty) { Payload.CopyTo(Ram.Bytes, 100000); Engine.BeginMappedWrite(Id, default, 100000, 8192); EngineTests.Drive(ref Engine, Ram, Disk); }
            Engine.BeginPersistenceBarrier(); EngineTests.Drive(ref Engine, Ram, Disk);
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
                var step = Dispatcher.Step();
                if (step == StepResult.Complete) return (Dispatcher.Result, Dispatcher.DosError);
                Assert.NotEqual(StepResult.Failed, step); if (step != StepResult.NeedIo) continue;
                var io = Dispatcher.Request; Assert.Equal(StepResult.NeedIo, Dispatcher.Step());
                Assert.Equal(CpxError.StaleCompletion, Dispatcher.CompleteIo(io.Token + 1, io.Bytes, true));
                var data = Ram.Bytes.AsSpan((int)io.Buffer, (int)io.Bytes);
                if (io.Kind == IoKind.Read) Disk.Read(io.Sector, data); else if (io.Kind == IoKind.Write) Disk.Write(io.Sector, data); else Disk.Flush();
                Assert.Equal(CpxError.None, Dispatcher.CompleteIo(io.Token, io.Bytes, true));
            }
            throw new InvalidOperationException("Packet did not finish.");
        }
        internal (uint Result, uint Error) Packet(uint action, params uint[] args) { Begin(action, args); return Drive(); }
        internal uint Open(uint action)
        { Assert.Equal((uint.MaxValue, 0u), Packet(action, 230000 >> 2, 0, 220000 >> 2)); return Ram.Read32(230036); }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void RepeatedReadPacketsPreserveAdmissionBusyTokensEofAndInvalidBufferSemantics(bool catalogue)
    {
        var f = new Fixture(catalogue); uint handle = f.Open(1005);
        for (int pass = 0; pass < 3; pass++)
        {
            f.Ram.Bytes.AsSpan(120000, 544).Fill(0xa5);
            f.Begin(82, handle, 120000, 512);
            Assert.All(f.Ram.Bytes.AsSpan(120000, 544).ToArray(), b => Assert.Equal(0xa5, b));
            byte[] before = (byte[])f.Ram.Bytes.Clone();
            Assert.Equal(CpxError.Busy, f.Dispatcher.BeginPacket(210000)); Assert.Equal(before, f.Ram.Bytes);
            Assert.Equal((512u, 0u), f.Drive());
            Assert.Equal(f.Payload.AsSpan(pass * 512, 512).ToArray(), f.Ram.Bytes.AsSpan(120000, 512).ToArray());
            Assert.All(f.Ram.Bytes.AsSpan(120512, 32).ToArray(), b => Assert.Equal(0xa5, b));
        }
        Assert.Equal((1536u, 0u), f.Packet(1008, handle, 7936, uint.MaxValue));
        f.Ram.Bytes.AsSpan(120000, 544).Fill(0xa5);
        Assert.Equal((256u, 0u), f.Packet(82, handle, 120000, 512));
        Assert.Equal(f.Payload[7936..], f.Ram.Bytes.AsSpan(120000, 256).ToArray());
        Assert.All(f.Ram.Bytes.AsSpan(120256, 288).ToArray(), b => Assert.Equal(0xa5, b));
        Assert.Equal((0u, 0u), f.Packet(82, handle, 120000, 512));
        Assert.Equal((8192u, 0u), f.Packet(1008, handle, 0, uint.MaxValue));
        Assert.Equal((uint.MaxValue, 115u), f.Packet(82, handle, uint.MaxValue - 255, 512));
        Assert.Equal(0u, f.Ram.Read32(handle + 12));
        f.Packet(1007, handle); Assert.Equal((uint.MaxValue, 211u), f.Packet(82, handle, 120000, 512));
        new Cpx4Volume<MemoryDevice>(f.Disk, new byte[49152]).Check();
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void RepeatedAppendPacketsKeepWriteProtectionAndBufferedDurability(bool catalogue)
    {
        var f = new Fixture(catalogue, true); uint handle = f.Open(1004);
        for (int pass = 0; pass < 4; pass++)
        {
            f.Payload.AsSpan(pass * 512, 512).CopyTo(f.Ram.Bytes.AsSpan(120000));
            f.Begin(87, handle, 120000, 512); Assert.Equal((512u, 0u), f.Drive());
        }
        Assert.Equal((uint.MaxValue, 0u), f.Packet(1023, 1, 7));
        Assert.Equal((uint.MaxValue, 214u), f.Packet(87, handle, 120000, 512));
        Assert.Equal((uint.MaxValue, 0u), f.Packet(1023, 0, 7));
        f.Packet(1007, handle); Assert.Equal((uint.MaxValue, 0u), f.Packet(27));
        var volume = new Cpx4Volume<MemoryDevice>(f.Disk, new byte[49152]); volume.Check();
        byte[] actual = new byte[2048]; Assert.Equal(2048, volume.Read((ulong)f.Id, 0, actual)); Assert.Equal(f.Payload[..2048], actual);
        handle = f.Open(1005); Assert.Equal((uint.MaxValue, 223u), f.Packet(87, handle, 120000, 512));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void CachedPacketAdmissionRejectsHighPositionWordsWithoutChangingState(bool catalogue)
    {
        var f = new Fixture(catalogue);
        f.Engine.BeginFileRead(f.Id, default, 120000, 512); EngineTests.Drive(ref f.Engine, f.Ram, f.Disk);
        var method = typeof(CpxEngine<Ram>).GetMethod("TryBeginCachedTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!;
        byte[] before = (byte[])f.Ram.Bytes.Clone();
        Assert.False((bool)method.Invoke(f.Engine, [false, f.Id, new Wide(1, 0), 120000u, 512u])!);
        Assert.Equal(before, f.Ram.Bytes);
        Assert.False((bool)method.Invoke(f.Engine, [false, f.Id, new Wide(0, uint.MaxValue), 120000u, 512u])!);
        Assert.Equal(before, f.Ram.Bytes);
        Assert.Equal(CpxError.None, f.Engine.BeginMappedWrite(f.Id, Wide.From(8192), 120000, 512));
        EngineTests.Drive(ref f.Engine, f.Ram, f.Disk);
        before = (byte[])f.Ram.Bytes.Clone();
        Assert.False((bool)method.Invoke(f.Engine, [true, f.Id, new Wide(1, 8704), 120000u, 512u])!);
        Assert.Equal(before, f.Ram.Bytes);
        Assert.False((bool)method.Invoke(f.Engine, [true, new Wide(f.Id.High ^ 1u, f.Id.Low), Wide.From(8704), 120000u, 512u])!);
        Assert.Equal(before, f.Ram.Bytes);
    }
}
