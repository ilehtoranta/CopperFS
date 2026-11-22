using System.Buffers.Binary;
using CopperCPX;
using CopperNova.Reference;
using Xunit;

namespace CopperNova.Tests;

public sealed class FormatTests
{
    [Fact]
    public void DefaultHostImageUsesPackedV6()
    {
        var disk = NovaImage.FormatPacked();
        Assert.Equal(6, BinaryPrimitives.ReadUInt16BigEndian(disk.AsSpan(4)));
        Assert.Empty(NovaImage.CheckPacked(disk).Entries);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RootIsPublishedOnlyAfterMetadataAndSuccessRequiresBothBarriers(int failedBarrier)
    {
        var h = new FilesystemTests.Harness(new byte[8193 * 512]);
        Assert.Equal(CpxError.None, h.Engine.BeginMount(probeBlankCnp2: true));
        h.Drive();
        Assert.True(h.Engine.CanFormat);
        Assert.Equal(CpxError.None, h.Engine.BeginFormat(1, 2));

        var pending = new List<(uint Sector, byte[] Data)>();
        int barriers = 0;
        while (h.Engine.Busy)
        {
            if (h.Engine.Step() != StepResult.NeedIo) continue;
            CpxIo io = h.Engine.Request;
            if (io.Kind == IoKind.Read)
            {
                h.Service(io);
                continue;
            }
            if (io.Kind == IoKind.Write)
            {
                Assert.Equal(io.Sector.Low == 0 ? 1 : 0, barriers);
                pending.Add((io.Sector.Low, h.Memory.Bytes.AsSpan((int)io.Buffer, 512).ToArray()));
            }
            if (io.Kind == IoKind.Flush)
            {
                Assert.Equal(0, h.Disk[0]);
                if (++barriers == failedBarrier)
                {
                    h.Engine.CompleteIo(io.Token, 0, false);
                    continue;
                }
                foreach (var entry in pending)
                    entry.Data.CopyTo(h.Disk, (int)entry.Sector * 512);
                pending.Clear();
            }
            h.Engine.CompleteIo(io.Token, io.Bytes, true);
        }

        Assert.False(h.Engine.CanFormat);
        Assert.Equal(CpxError.InvalidArgument, h.Engine.BeginFormat(1, 3));
        if (failedBarrier != 0)
        {
            Assert.False(h.Engine.IsMounted);
            Assert.All(h.Disk[..1024], value => Assert.Equal(0, value));
            return;
        }
        Assert.Equal(2, barriers);
        Assert.True(h.Engine.IsMounted);
        Assert.Equal(6, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(h.Disk.AsSpan(4)));
        Assert.Empty(NovaImage.CheckPacked(h.Disk).Entries);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void RetiredCnp2RevisionsAreRefusedWithoutWrites(ushort version)
    {
        var disk = new byte[8192 * 512];
        using (var stream = new MemoryStream(disk))
            NovaImage.FormatPackedWide(stream, 0, disk.Length);
        var root = disk.AsSpan(0, 512);
        BinaryPrimitives.WriteUInt16BigEndian(root[4..], version);
        BinaryPrimitives.WriteUInt32BigEndian(root[68..], 4096);
        BinaryPrimitives.WriteUInt32BigEndian(root[72..], 4096);
        if (version == 3)
            BinaryPrimitives.WriteUInt16BigEndian(root[160..], 194);
        root.Slice(8, 4).Clear();
        uint crc = uint.MaxValue;
        foreach (byte b in root)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0x82f63b78u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(root[8..], ~crc);
        var h = new FilesystemTests.Harness(disk);
        h.BeforeIo = io => Assert.Equal(IoKind.Read, io.Kind);
        Assert.Equal(CpxError.None, h.Engine.BeginMount(probeBlankCnp2: true));
        h.Drive();
        Assert.False(h.Engine.IsMounted);
        Assert.False(h.Engine.CanFormat);
        Assert.Equal(CpxError.InvalidArgument, h.Engine.BeginFormat(1, 2));
        Assert.Throws<InvalidDataException>(() => NovaImage.CheckPacked(disk));
    }

    [Fact]
    public void NonzeroRootContentsAndFailedReadsCannotAdmitFormatting()
    {
        foreach (int offset in new[] { 0, 1023 })
        {
            var disk = new byte[8192 * 512];
            disk[offset] = 1;
            var h = new FilesystemTests.Harness(disk);
            Assert.Equal(CpxError.None, h.Engine.BeginMount(probeBlankCnp2: true));
            h.Drive();
            Assert.Equal(CpxError.InvalidArgument, h.Engine.BeginFormat(1, 2));
        }

        var blank = new FilesystemTests.Harness(new byte[8192 * 512]);
        Assert.Equal(CpxError.None, blank.Engine.BeginMount(probeBlankCnp2: true));
        while (blank.Engine.Step() != StepResult.NeedIo) { }
        blank.Engine.CompleteIo(blank.Engine.Request.Token, 0, false);
        blank.Drive();
        Assert.Equal(CpxError.InvalidArgument, blank.Engine.BeginFormat(1, 2));
    }
}
