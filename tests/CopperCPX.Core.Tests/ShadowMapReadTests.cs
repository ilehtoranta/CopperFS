using CopperCPX.Reference.Tests;
using static CopperFS.Cpx4.Reference.Layout;

namespace CopperCPX.Tests;

public class ShadowMapReadTests
{
    private const uint Identity = 12345;
    private static CpxShadowMapGeometry Geometry()
    {
        Assert.Equal(CpxError.None, CpxShadowMapGeometry.TryCreate(512, 1024, Wide.From(32768), Wide.From(512), out var geometry));
        return geometry;
    }
    private static byte[] Node(CpxShadowMapGeometry geometry, uint level, ulong index, ulong generation)
    {
        var bytes = new byte[geometry.PageBytes]; ulong pair = geometry.NodePairSector(level, index);
        Put32(bytes, 0, 0x43505835); Put16(bytes, 4, level == 0 ? (ushort)20 : (ushort)21); Put16(bytes, 6, 5);
        Put64(bytes, 16, 0x8000000000000000ul | pair); Put64(bytes, 24, Identity); Put64(bytes, 32, generation);
        uint entries = geometry.Entries(level, index);
        Put16(bytes, 48, (ushort)entries); Put16(bytes, 50, (ushort)level);
        Put16(bytes, 52, (ushort)(level == 0 ? (entries + 7) >> 3 : (entries + 3) >> 2));
        return bytes;
    }
    private static void Store(MemoryDevice disk, ulong sector, byte[] image)
    { Put32(image, 8, Crc(image, true)); disk.Write(sector, image); }

    private static (MemoryDevice Disk, CpxShadowMapGeometry Geometry, ulong Root) Fixture()
    {
        var geometry = Geometry(); var disk = new MemoryDevice(512, 16);
        ulong root = (ulong)geometry.NodePairSector(1, 0) + geometry.PageSectors;
        var branch = Node(geometry, 1, 0, 2); branch[64] = 0x60; Store(disk, root, branch);
        var unselectedBranch = Node(geometry, 1, 0, 3); Store(disk, geometry.NodePairSector(1, 0), unselectedBranch);
        var left = Node(geometry, 0, 0, 2); left[64] = 0x08; Store(disk, geometry.NodePairSector(0, 0), left);
        var futureLeft = Node(geometry, 0, 0, 3); Store(disk, (ulong)geometry.NodePairSector(0, 0) + geometry.PageSectors, futureLeft);
        var right = Node(geometry, 0, 1, 1); right[64 + 511] = 1;
        Store(disk, (ulong)geometry.NodePairSector(0, 1) + geometry.PageSectors, right);
        var futureRight = Node(geometry, 0, 1, 3); futureRight[64] = 0xff; Store(disk, geometry.NodePairSector(0, 1), futureRight);
        return (disk, geometry, root);
    }

    private static (CpxError Error, uint Copy, int Reads) Lookup(MemoryDevice disk, CpxShadowMapGeometry geometry, ulong root, ulong pair)
    {
        var ram = new Ram(Enumerable.Repeat((byte)0xa5, 8192).ToArray()); ram.Bytes.AsSpan(32, (int)CpxShadowMapReadCursor<Ram>.StateBytes).Clear();
        var cursor = new CpxShadowMapReadCursor<Ram>(ram, 32, geometry);
        Assert.Equal(CpxError.None, cursor.Begin(root, Wide.From(2), Wide.From(Identity), pair, 4096));
        Assert.Equal(CpxError.StaleCompletion, cursor.AcceptRead(true));
        int reads = 0;
        for (int turn = 0; turn < 24; turn++)
        {
            cursor = new CpxShadowMapReadCursor<Ram>(ram, 32, geometry);
            var result = cursor.Step();
            if (result is StepResult.Complete or StepResult.Failed)
            {
                Assert.All(ram.Bytes[..32], b => Assert.Equal(0xa5, b));
                Assert.All(ram.Bytes.AsSpan(32 + (int)CpxShadowMapReadCursor<Ram>.StateBytes, 128).ToArray(), b => Assert.Equal(0xa5, b));
                return (cursor.Error, cursor.SelectedCopy, reads);
            }
            if (result != StepResult.NeedIo) continue;
            Assert.Equal(StepResult.NeedIo, cursor.Step());
            disk.Read(cursor.ReadSector, ram.Bytes.AsSpan((int)cursor.ReadBuffer, (int)cursor.ReadBytes));
            reads++; Assert.Equal(CpxError.None, cursor.AcceptRead(true));
        }
        Assert.Fail("Map lookup did not terminate."); return default;
    }

    [Theory]
    [InlineData(16ul, 1u)]
    [InlineData(20ul, 0u)]
    [InlineData(16384ul, 0u)]
    [InlineData(32764ul, 1u)]
    public void ExplicitSelectorsChooseCommittedCopiesAcrossLeafBoundaries(ulong pair, uint expected)
    {
        var fixture = Fixture();
        Assert.Equal((CpxError.None, expected, 2), Lookup(fixture.Disk, fixture.Geometry, fixture.Root, pair));
    }
    [Fact]
    public void ImplicitMapAndUninitializedBranchesNeedNoUnselectedPageRead()
    {
        var fixture = Fixture();
        Assert.Equal((CpxError.None, 0u, 0), Lookup(fixture.Disk, fixture.Geometry, 0, 16));
        var root = Node(fixture.Geometry, 1, 0, 2); Store(fixture.Disk, fixture.Root, root);
        Assert.Equal((CpxError.None, 0u, 1), Lookup(fixture.Disk, fixture.Geometry, fixture.Root, 16));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void InvalidCrcSelectorOwnerAndFutureSelectedNodeAreRejected(int corruption)
    {
        var fixture = Fixture();
        var root = fixture.Disk.Bytes.AsSpan((int)fixture.Root * 512, 1024);
        if (corruption == 0) root[200] ^= 1;
        else
        {
            if (corruption == 1) root[64] = 0xc0;
            if (corruption == 2) Put64(root, 24, Identity + 1);
            if (corruption == 3) root[64] = 0x80; // selects the future, CRC-valid left copy
            Put32(root, 8, Crc(root, true));
        }
        Assert.Equal(CpxError.Corrupt, Lookup(fixture.Disk, fixture.Geometry, fixture.Root, 16).Error);
    }
    [Fact]
    public void FailedReadsClearPendingOwnershipAndCannotProduceASelection()
    {
        var fixture = Fixture(); var ram = new Ram(new byte[8192]);
        var cursor = new CpxShadowMapReadCursor<Ram>(ram, 32, fixture.Geometry);
        Assert.Equal(CpxError.None, cursor.Begin(fixture.Root, Wide.From(2), Wide.From(Identity), Wide.From(16), 4096));
        Assert.Equal(StepResult.NeedIo, cursor.Step());
        Assert.Equal(CpxError.Busy, cursor.Begin(fixture.Root, Wide.From(2), Wide.From(Identity), Wide.From(20), 4096));
        Assert.Equal(CpxError.Io, cursor.AcceptRead(false)); Assert.Equal(StepResult.Failed, cursor.Step());
        Assert.Equal(CpxError.StaleCompletion, cursor.AcceptRead(true));
    }
}
