using CopperCPX.Reference.Tests;
using CopperFS.Cpx4.Reference;

namespace CopperCPX.Tests;

public class GeometryMatrixTests
{
    public static IEnumerable<object[]> Geometries()
    {
        for (int sector = 512; sector <= 4096; sector *= 2)
            for (int unit = sector; unit <= 4096; unit *= 2) yield return [sector, unit];
    }
    [Theory, MemberData(nameof(Geometries))]
    public void DiscoveryUsesTheLastAlignedBackupForEverySectorAndUnit(int sector,int unit)
    {
        // An extra unit leaves an unpaired trailing sector when A=512, P=1024.
        var disk=new MemoryDevice(new byte[4194304+unit],sector);
        const ulong identity=0x1122334455667788;
        Cpx4Volume<MemoryDevice>.Format(disk,unit,new byte[36864],identity);
        var ram=new Ram(new byte[120000]); var engine=new CpxEngine<Ram>(ram,32);
        uint page=(uint)Math.Max(1024,unit),capacity=(uint)disk.SectorCount;
        uint backup=((uint)disk.Bytes.Length&~(page-1))-page;
        disk.Bytes[65536+8]^=1;
        foreach(uint address in new[]{65536u,backup})
        {
            disk.Read(address/(uint)sector,ram.Bytes.AsSpan(90000,(int)page));
            var result=engine.InspectAnchor(90000,page,(uint)sector,Wide.From(capacity),
                Wide.From(address/(uint)sector),out uint selected,out var found);
            if(address==65536) { Assert.Equal(CpxError.NotFound,result); continue; }
            Assert.Equal(CpxError.None,result); Assert.Equal((uint)unit,selected);
            Assert.True(Wide.Equal(new(0x11223344,0x55667788),found));
            Assert.Equal(CpxError.None,engine.Initialize((uint)sector,selected,Wide.From(capacity),1));
            Assert.Equal(CpxError.None,engine.BeginMountFromDiscoveredAnchor(found));
            EngineTests.Drive(ref engine,ram,disk); Assert.True(engine.IsMounted);
        }
    }

    [Theory]
    [InlineData(4)][InlineData(6)][InlineData(16)][InlineData(24)][InlineData(52)][InlineData(64)]
    [InlineData(80)][InlineData(84)][InlineData(88)][InlineData(96)][InlineData(104)]
    [InlineData(108)][InlineData(112)][InlineData(120)]
    public void AuthenticatedButInconsistentAnchorIsCorruption(int field)
    {
        var disk=EngineTests.Fresh(2048); var ram=new Ram(new byte[4096]);
        var bytes=ram.Bytes.AsSpan(0,2048); disk.Read(128,bytes); bytes[field]^=1;
        Layout.Put32(bytes,8,Layout.Crc(bytes,true));
        var engine=new CpxEngine<Ram>(ram,0);
        Assert.Equal(CpxError.Corrupt,engine.InspectAnchor(0,2048,512,Wide.From(8192),Wide.From(128),out uint unit,out var identity));
        Assert.Equal(0u,unit); Assert.True(identity.IsZero);
    }
    [Theory, MemberData(nameof(Geometries))]
    public void EveryLegalSectorAndAllocationUnitSupportsMutationAndRemount(int sector, int unit)
    {
        var disk = new MemoryDevice(sector); Cpx4Volume<MemoryDevice>.Format(disk, unit, new byte[36864], 12345);
        var ram = new Ram(Enumerable.Repeat((byte)0xa5, 120000).ToArray()); var e = new CpxEngine<Ram>(ram, 32);
        Assert.Equal(CpxError.None, e.Initialize((uint)sector, (uint)unit, Wide.From((uint)disk.SectorCount), 1));
        e.BeginOperation(CpxOperation.Mount); EngineTests.Drive(ref e, ram, disk);
        "geometry"u8.CopyTo(ram.Bytes.AsSpan(90000)); e.BeginCreate(Wide.From(1), 90000, 8); EngineTests.Drive(ref e, ram, disk); Wide id = e.MutatedObject;
        ram.Bytes.AsSpan(91000, unit + 1).Fill(0x63); e.BeginMappedWrite(id, default, 91000, (uint)unit + 1); EngineTests.Drive(ref e, ram, disk);
        e.BeginResize(id, Wide.From(159)); EngineTests.Drive(ref e, ram, disk);
        e.BeginResize(id, Wide.From((uint)unit * 2)); EngineTests.Drive(ref e, ram, disk);
        e.Initialize((uint)sector, (uint)unit, Wide.From((uint)disk.SectorCount), 2);
        e.BeginOperation(CpxOperation.Mount); EngineTests.Drive(ref e, ram, disk);
        e.BeginFileRead(id, default, 100000, (uint)unit * 2); EngineTests.Drive(ref e, ram, disk);
        Assert.Equal((uint)unit * 2, e.TransferredBytes);
        Assert.All(ram.Bytes[100000..100159], b => Assert.Equal(0x63, b));
        Assert.All(ram.Bytes[100159..(100000 + unit * 2)], b => Assert.Equal(0, b));
        Assert.All(ram.Bytes[..32], b => Assert.Equal(0xa5, b));
        Assert.All(ram.Bytes[(32 + (int)CpxArena.Bytes((uint)Math.Max(1024, unit)))..90000], b => Assert.Equal(0xa5, b));
        new IndependentImage(disk.Bytes); new Cpx4Volume<MemoryDevice>(disk, new byte[36864]).Check();
    }
}
