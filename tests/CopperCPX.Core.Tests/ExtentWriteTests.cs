using CopperCPX;
using CopperCPX.Reference.Tests;
using CopperFS.Cpx4.Reference;
using static CopperFS.Cpx4.Reference.Layout;

namespace CopperCPX.Tests;

public class ExtentWriteTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public void ReferencePrunedEmptyTreeCanBeReadAndWrittenAgain(bool sparse)
    {
        var disk=EngineTests.Fresh(512); var v=new Cpx4Volume<Disk>(disk,new byte[36864]); v.Create("file"u8,out ulong id);
        for(uint n=0;n<7;n++) v.Write(id,n*1024,new byte[512],out _);
        v.Truncate(id,0); if(sparse) v.Truncate(id,8192);
        var before=new IndependentImage(disk.Bytes); Assert.Equal(2,before.Objects[id][85]); Assert.Equal(0UL,U64(before.Objects[id],24));
        var ram=new Ram(new byte[100000]); var e=FileMutationTests.Mount(disk,ram);
        if(sparse) { e.BeginFileRead(Wide.From((uint)id),Wide.From(4000),90000,100); EngineTests.Drive(ref e,ram,disk); Assert.All(ram.Bytes[90000..90100],b=>Assert.Equal(0,b)); }
        e.BeginMappedWrite(Wide.From((uint)id),new(1,0),0,0); EngineTests.Drive(ref e,ram,disk); Assert.Equal(0u,e.TransferredBytes);
        ram.Bytes.AsSpan(90000,17).Fill(0x7c); e.BeginMappedWrite(Wide.From((uint)id),default,90000,17); EngineTests.Drive(ref e,ram,disk);
        v=new(disk,new byte[36864]); byte[] bytes=new byte[17]; Assert.Equal(17,v.Read(id,0,bytes)); Assert.All(bytes,b=>Assert.Equal(0x7c,b)); v.Check(); new IndependentImage(disk.Bytes);
    }
    [Theory][InlineData(512)][InlineData(4096)]
    public void NativeWritesPromoteSixDescriptorsSplitLeavesAndPreserveSparseContents(int unit)
    {
        var disk=EngineTests.Fresh(unit); var v=new Cpx4Volume<Disk>(disk,new byte[36864]); v.Create("file"u8,out ulong id);
        int entries=(Math.Max(1024,unit)-64)/24+3,length=entries*2*unit;
        var ram=new Ram(new byte[200000]); var e=FileMutationTests.Mount(disk,ram,unit);
        e.BeginResize(Wide.From((uint)id),Wide.From((uint)length)); EngineTests.Drive(ref e,ram,disk);
        byte[] expected=new byte[length];
        for(int n=0;n<entries;n++)
        {
            byte value=(byte)(n+1); ram.Bytes.AsSpan(100000,unit).Fill(value); expected.AsSpan(n*2*unit,unit).Fill(value);
            Assert.Equal(CpxError.None,e.BeginMappedWrite(Wide.From((uint)id),Wide.From((uint)(n*2*unit)),100000,(uint)unit)); EngineTests.Drive(ref e,ram,disk);
            Assert.Equal((uint)unit,e.TransferredBytes);
        }
        var check=new Cpx4Volume<Disk>(disk,new byte[36864]); check.Check(); var decoded=new IndependentImage(disk.Bytes);
        byte[] record=decoded.Objects[id]; Assert.Equal(2,record[85]); ulong tree=U64(record,24); Assert.Equal(1,U16(disk.Bytes,(int)(tree*(uint)unit)+50));
        byte[] actual=new byte[length]; Assert.Equal(length,check.Read(id,0,actual)); Assert.Equal(expected,actual);
        // Grow the existing extent tree at EOF as well as insert into its holes.
        e.BeginMappedWrite(Wide.From((uint)id),Wide.From((uint)length),100000,(uint)unit); EngineTests.Drive(ref e,ram,disk);
        new Cpx4Volume<Disk>(disk,new byte[36864]).Check(); new IndependentImage(disk.Bytes);
    }
    [Theory][InlineData(false,0)][InlineData(false,1)][InlineData(false,2)][InlineData(true,1)]
    public void UnwrittenSplitZeroesUnwrittenBytesAndDoesNotAllocateDataAgain(bool tree,int selected)
    {
        var disk=EngineTests.Fresh(512); var v=new Cpx4Volume<Disk>(disk,new byte[36864]); v.Create("file"u8,out ulong id);
        for(int n=0;n<(tree?40:6);n++) Assert.Equal(MutationResult.Success,v.Preallocate(id,(ulong)n*4096,3,out _));
        var before=new IndependentImage(disk.Bytes); ulong allocated=U64(before.Objects[id],16);
        // Fill every allocated payload with stale data. Unwritten reads must not
        // expose it, including bytes outside the caller's partial-unit write.
        StampUnwritten(disk,before.Objects[id],before.Metadata);
        var ram=new Ram(new byte[100000]); var e=FileMutationTests.Mount(disk,ram);
        uint offset=(uint)(selected*512+13); ram.Bytes.AsSpan(90000,17).Fill(0x77);
        e.BeginMappedWrite(Wide.From((uint)id),Wide.From(offset),90000,17); EngineTests.Drive(ref e,ram,disk);
        var after=new IndependentImage(disk.Bytes); Assert.Equal(allocated,U64(after.Objects[id],16));
        v=new(disk,new byte[36864]); v.Check(); byte[] bytes=new byte[1536]; Assert.Equal(1536,v.Read(id,0,bytes));
        byte[] expected=new byte[1536]; expected.AsSpan((int)offset,17).Fill(0x77); Assert.Equal(expected,bytes);
        Assert.Equal(2,after.Objects[id][85]); // Six descriptors plus a split promote; tree case stays a tree.
    }
    internal static void StampUnwritten(Disk disk,byte[] obj,IEnumerable<ulong> metadata)
    {
        void Stamp(ReadOnlySpan<byte> extent)
        { if(U32(extent,20)==1) disk.Bytes.AsSpan((int)(U64(extent,8)*512),checked((int)U32(extent,16)*512)).Fill(0xcc); }
        if(obj[85]==1) for(int n=0;n<U16(obj,90);n++) Stamp(obj.AsSpan(96+n*24,24));
        else foreach(ulong address in metadata)
        {
            ReadOnlySpan<byte> page=disk.Bytes.AsSpan((int)(address*512),1024);
            if(U16(page,4)!=15||U64(page,24)!=U64(obj,0)) continue;
            for(int n=0;n<U16(page,48);n++) Stamp(page.Slice(64+n*24,24));
        }
    }
    [Fact] public void ExtentTreeGrowthAndUnwrittenSplitsAboveFourGiBUseBothPositionHalves()
    {
        var disk=EngineTests.Fresh(512); var v=new Cpx4Volume<Disk>(disk,new byte[36864]); v.Create("wide"u8,out ulong id);
        for(uint n=0;n<7;n++) v.Preallocate(id,0x100000000UL+n*4096,3,out _);
        var ram=new Ram(new byte[100000]); var e=FileMutationTests.Mount(disk,ram); ram.Bytes.AsSpan(90000,19).Fill(0x83);
        e.BeginMappedWrite(Wide.From((uint)id),new(1,521),90000,19); EngineTests.Drive(ref e,ram,disk);
        e.BeginMappedWrite(Wide.From((uint)id),new(1,2048),90000,19); EngineTests.Drive(ref e,ram,disk);
        v=new(disk,new byte[36864]); byte[] actual=new byte[19]; Assert.Equal(19,v.Read(id,0x100000209UL,actual)); Assert.All(actual,b=>Assert.Equal(0x83,b));
        Assert.Equal(19,v.Read(id,0x100000800UL,actual)); Assert.All(actual,b=>Assert.Equal(0x83,b)); v.Check(); new IndependentImage(disk.Bytes);
    }
}
