using CopperCPX;
using CopperNova.Reference;
using static CopperNova.Tests.FilesystemTests;

namespace CopperNova.Tests;

public class PartialOverwriteTests
{
    private static (Harness Host, uint Handle, byte[] Bytes, uint Start) Prepare()
    {
        var host = new Harness(NovaImage.FormatPacked(2 * 1024 * 1024, reusableObjects: true));
        host.Mount();
        uint handle = host.Open("payload");
        byte[] bytes = Enumerable.Range(0, 65536).Select(Harness.Initial).ToArray();
        uint start = 0;
        host.BeforeIo = io =>
        {
            if (io.Kind != IoKind.Write || io.Bytes != 65536) return;
            Assert.Equal(Harness.Data, io.Buffer);
            start = io.Sector.Low;
        };
        host.Write(handle, bytes);
        host.Flush();
        Assert.True(start >= 1394);
        host.BeforeIo = null;
        return (host, handle, bytes, start);
    }

    private static void Overwrite(Harness host, uint handle, byte[] expected, int offset, int bytes, byte value)
    {
        Assert.NotEqual(uint.MaxValue, host.Packet(1008, handle, (uint)offset, uint.MaxValue));
        byte[] data = new byte[bytes];
        Array.Fill(data, value);
        host.Write(handle, data);
        data.CopyTo(expected, offset);
    }

    [Fact]
    public void NearbyOverwritesReadOnceAndDrainOnceBeforeRootPublication()
    {
        var (host, handle, expected, start) = Prepare();
        var trace = new List<CpxIo>();
        host.BeforeIo = trace.Add;

        Overwrite(host, handle, expected, 1023, 1024, 93);
        Overwrite(host, handle, expected, 4093, 769, 81);
        var read = Assert.Single(trace);
        Assert.Equal((IoKind.Read, start + 1, 4608u), (read.Kind, read.Sector.Low, read.Bytes));
        Assert.Equal(Harness.Initial(1023), host.Disk[start * 512 + 1023]);

        host.Close(handle);
        host.Flush();
        var data = trace[1];
        Assert.Equal((IoKind.Write, start + 1, 4608u), (data.Kind, data.Sector.Low, data.Bytes));
        Assert.Equal(2, trace.Count(io => io.Kind == IoKind.Flush));
        int firstBarrier = trace.FindIndex(io => io.Kind == IoKind.Flush);
        Assert.True(firstBarrier > 1);
        Assert.Equal((IoKind.Write, 0u, 512u),
            (trace[firstBarrier + 1].Kind, trace[firstBarrier + 1].Sector.Low, trace[firstBarrier + 1].Bytes));
        Assert.Equal(IoKind.Flush, trace[firstBarrier + 2].Kind);
        host.InhibitAndRemount();
        handle = host.Open("payload", 1005);
        Assert.Equal((uint)expected.Length, host.Packet(82, handle, Harness.Data, (uint)expected.Length));
        Assert.Equal(expected, host.Memory.Bytes.AsSpan((int)Harness.Data, expected.Length).ToArray());
    }

    [Fact]
    public void OverlapDrainsForReadAndBulkWriteWithoutRestoringStaleGapBytes()
    {
        var (host, handle, expected, start) = Prepare();
        Overwrite(host, handle, expected, 1023, 1024, 43);
        Assert.NotEqual(uint.MaxValue, host.Packet(1008, handle, 1023, uint.MaxValue));
        Assert.Equal(1024u, host.Packet(82, handle, Harness.Data, 1024));
        Assert.All(host.Memory.Bytes.AsSpan((int)Harness.Data, 1024).ToArray(), value => Assert.Equal((byte)43, value));

        Overwrite(host, handle, expected, 1023, 1024, 64);
        var trace = new List<CpxIo>();
        host.BeforeIo = trace.Add;
        Overwrite(host, handle, expected, 3072, 512, 81);
        Assert.Collection(trace,
            io => Assert.Equal((IoKind.Write, start + 1, 1536u), (io.Kind, io.Sector.Low, io.Bytes)),
            io => Assert.Equal((IoKind.Write, start + 6, Harness.Data), (io.Kind, io.Sector.Low, io.Buffer)));
        Overwrite(host, handle, expected, 4093, 769, 96);
        host.Flush();
        Assert.Equal(expected, host.Disk.AsSpan((int)(start * 512), expected.Length).ToArray());
    }

    [Fact]
    public void ShrinkDrainsPendingDataBeforeItsExtentCanBeReused()
    {
        var (host, handle, expected, start) = Prepare();
        Overwrite(host, handle, expected, 1023, 1024, 71);
        var trace = new List<CpxIo>();
        host.BeforeIo = trace.Add;
        Assert.Equal(512u, host.Packet(1022, handle, 512, uint.MaxValue));
        var data = Assert.Single(trace, io => io.Kind == IoKind.Write);
        Assert.Equal((start + 1, 1536u), (data.Sector.Low, data.Bytes));
        host.Close(handle);
        host.Flush();
        host.InhibitAndRemount();
        Assert.Equal(512L, Assert.Single(NovaImage.CheckPacked(host.Disk).Entries).Size);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedOrShortDataDrainCannotAcknowledgeFlushOrPublishRoot(bool shortWrite)
    {
        var (host, handle, expected, start) = Prepare();
        Overwrite(host, handle, expected, 1023, 1024, 39);
        host.Memory.Write32(Harness.PacketAt + 8, 27);
        Assert.Equal(CpxError.None, host.Engine.BeginPacket(Harness.PacketAt));
        while (host.Engine.Step() == StepResult.Pending) { }
        var data = host.Engine.Request;
        Assert.Equal((IoKind.Write, start + 1), (data.Kind, data.Sector.Low));
        Assert.Equal(CpxError.Io, host.Engine.CompleteIo(data.Token, shortWrite ? 512u : 0u, shortWrite));
        Assert.Equal(StepResult.Complete, host.Engine.Step());
        Assert.Equal(0u, host.Engine.Result);
        Assert.Equal(213u, host.Engine.DosError);
        Assert.False(host.Engine.IsMounted);
        Assert.Equal(1u, NovaImage.CheckPacked(host.Disk).Generation);
    }
}
