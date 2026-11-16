using CopperFS.Cpx4.Reference;

namespace CopperCPX.Tests;

public class ChecksumSetupTests
{
    private static uint Advance(uint value, int bytes)
    {
        for (int n = 0; n < bytes * 8; n++) value = (value >> 1) ^ (0x82f63b78u & (0u - (value & 1)));
        return value;
    }

    [Fact]
    public void EveryPreparedAndZeroRunTableEntryMatchesIndependentPolynomialDivision()
    {
        var ram = new Ram(new byte[1000000]); var engine = new CpxEngine<Ram>(ram, 32);
        Assert.Equal(CpxError.None, engine.PrepareAnchorChecksum(200000));
        for (uint slice = 0; slice < 4; slice++)
        for (uint value = 0; value < 256; value++)
            Assert.Equal(Advance(value, (int)slice + 1), ram.Read32(200000 + slice * 1024 + value * 4));
        Assert.Equal(CpxError.None, engine.Initialize(512, 512, Wide.From(8192), 1));
        Assert.Equal(CpxError.None, engine.EnableBufferedWrites(300000, CpxEngine<Ram>.BufferedStorageBytes(1024)));
        for (uint slice = 0; slice < 4; slice++)
        for (uint value = 0; value < 256; value++)
            Assert.Equal(Advance(value << (int)(slice * 8), 64), ram.Read32(300000 + slice * 1024 + value * 4));
    }

    [Theory][InlineData(512)][InlineData(4096)]
    public void PreparedDiscoveryPreservesGeometryAndRejectsTornAnchors(int unit)
    {
        var disk = EngineTests.Fresh(unit); var ram = new Ram(new byte[120000]); var engine = new CpxEngine<Ram>(ram, 32);
        uint page = (uint)Math.Max(unit, 1024); disk.Read(128, ram.Bytes.AsSpan(90000, (int)page));
        Assert.Equal(CpxError.None, engine.PrepareAnchorChecksum(100000));
        Assert.Equal(CpxError.None, engine.InspectAnchor(90000, page, 512, Wide.From(8192), Wide.From(128),
            out uint selected, out var id, out uint version, 100000));
        Assert.Equal((uint)unit, selected); Assert.Equal(12345UL, (ulong)id); Assert.Equal(4u, version);
        ram.Bytes[90080] ^= 1;
        Assert.Equal(CpxError.NotFound, engine.InspectAnchor(90000, page, 512, Wide.From(8192), Wide.From(128), out _, out _, out _, 100000));
        Layout.Put32(ram.Bytes.AsSpan(90000), 8, Layout.Crc(ram.Bytes.AsSpan(90000, (int)page), true));
        Assert.Equal(CpxError.Corrupt, engine.InspectAnchor(90000, page, 512, Wide.From(8192), Wide.From(128), out _, out _, out _, 100000));
    }
}
