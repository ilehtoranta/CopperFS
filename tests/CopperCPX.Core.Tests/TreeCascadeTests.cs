using CopperCPX;
using CopperCPX.Reference.Tests;
using CopperFS.Cpx4.Reference;
using static CopperFS.Cpx4.Reference.Layout;

namespace CopperCPX.Tests;

public class TreeCascadeTests
{
    [Theory][InlineData(2,false)][InlineData(3,false)][InlineData(4,false)][InlineData(5,false)]
    [InlineData(6,false)][InlineData(7,false)][InlineData(8,true)]
    public void FullSpineSplitsOrRejectsNinthLevelWithoutPublishingPrefix(int height,bool reject)
    {
        var disk=EngineTests.Fresh(512); var v=new Cpx4Volume<Disk>(disk,new byte[36864]);
        v.Create("a"u8,out _); v.Create("b"u8,out _); v.Create("c"u8,out _);
        var ram=new Ram(new byte[100000]); var e=FileMutationTests.Mount(disk,ram);
        // Install a consistent synthetic namespace while the engine is quiescent.
        // Side paths contain empty leaves; mount's empty-node maintenance is
        // intentionally not rerun before testing the actual insertion algorithm.
        InstallFullSpine(disk,height); new IndependentImage(disk.Bytes); new Cpx4Volume<Disk>(disk,new byte[36864]).Check();
        byte[] before=(byte[])disk.Bytes.Clone(); Wide generation=e.Transaction;
        "new"u8.CopyTo(ram.Bytes.AsSpan(90000)); Assert.Equal(CpxError.None,e.BeginCreate(Wide.From(1),90000,3));
        if(!reject)
        {
            EngineTests.Drive(ref e,ram,disk); var l=new Cpx4Volume<Disk>(disk,new byte[36864]).Geometry;
            ulong tx=((ulong)e.Transaction.High<<32)|e.Transaction.Low;
            uint images=U32(disk.Bytes.AsSpan((int)(l.Journal(tx)*512)),96); Assert.InRange(images,1u,42u);
            if(height==7) Assert.Equal(19u,images);
            ulong root=U64(disk.Bytes.AsSpan((int)(l.State*512)),80); Assert.Equal(height,U16(disk.Bytes.AsSpan((int)(root*512)),50));
        }
        else
        {
            for(int n=0;n<20000;n++)
            {
                var result=e.Step(); if(result==StepResult.Failed) break; Assert.NotEqual(StepResult.Complete,result);
                if(result!=StepResult.NeedIo) continue; var io=e.Request;
                if(io.Kind==IoKind.Read) disk.Read(io.Sector.Low,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes));
                else if(io.Kind==IoKind.Write) disk.Write(io.Sector.Low,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes)); else disk.Flush();
                e.CompleteIo(io.Token,io.Bytes,true);
            }
            Assert.Equal(CpxError.TreeTooDeep,e.Error); Assert.True(e.IsMounted); Assert.True(Wide.Equal(generation,e.Transaction));
            Assert.Equal(before.AsSpan(2097152).ToArray(),disk.Bytes.AsSpan(2097152).ToArray());
            Assert.False(new Cpx4Volume<Disk>(disk,new byte[36864]).TryGetInfo("new"u8,out _));
            Assert.Equal(CpxError.None,e.BeginLookup(Wide.From(1),90000,3)); EngineTests.Drive(ref e,ram,disk); Assert.False(e.LookupFound);
        }
        new IndependentImage(disk.Bytes); new Cpx4Volume<Disk>(disk,new byte[36864]).Check();
    }
    [Fact] public void FullSingleLeafPromotesToTwoLevelsWithinTheSameTransactionBound()
    {
        var disk=EngineTests.Fresh(512); var v=new Cpx4Volume<Disk>(disk,new byte[36864]);
        v.Create("a"u8,out _); v.Create("b"u8,out _); var l=v.Geometry;
        ulong root=U64(disk.Bytes.AsSpan((int)(l.State*512)),80); Assert.Equal(0,U16(disk.Bytes.AsSpan((int)(root*512)),50));
        var ram=new Ram(new byte[100000]); var e=FileMutationTests.Mount(disk,ram); "c"u8.CopyTo(ram.Bytes.AsSpan(90000));
        e.BeginCreate(Wide.From(1),90000,1); EngineTests.Drive(ref e,ram,disk);
        root=U64(disk.Bytes.AsSpan((int)(l.State*512)),80); Assert.Equal(1,U16(disk.Bytes.AsSpan((int)(root*512)),50));
        new IndependentImage(disk.Bytes); new Cpx4Volume<Disk>(disk,new byte[36864]).Check();
    }
    private static void InstallFullSpine(Disk disk,int height)
    {
        var image=disk.Bytes; var v=new Cpx4Volume<Disk>(disk,new byte[36864]); var l=v.Geometry; ulong generation=v.Generation;
        var decoded=new IndependentImage(image); byte[][] files=[decoded.Objects[2],decoded.Objects[3],decoded.Objects[4]];
        ulong cursor=l.DataStart,key=1; bool first=true;
        Span<byte> Page(ulong at)=>image.AsSpan((int)(at*512),1024);
        // Return the old object tree's allocated pages to reserve. The directory
        // tree and all object records remain reachable through the replacement.
        foreach(ulong at in decoded.Metadata)
        {
            if(U16(Page(at),4) is not (11 or 12)) continue;
            var reserve=Page(l.Reserve); int count=U16(reserve,64); Put64(reserve,72+count*8,at); Put16(reserve,64,(ushort)(count+1)); Seal(reserve);
        }
        ulong Allocate()
        {
            while(cursor<l.Backup)
            {
                ulong at=cursor; cursor+=l.Q; var bitmap=Page(l.Bitmap(0,(int)(at>>12))); int bit=(int)(at&4095);
                if((bitmap[64+(bit>>3)]&(0x80>>(bit&7)))!=0) continue;
                for(int n=0;n<2;n++) bitmap[64+((bit+n)>>3)]|=(byte)(0x80>>((bit+n)&7)); Seal(bitmap);
                var group=Page(l.GroupDescriptor(0)); Put32(group,68,U32(group,68)-2); int offset=80+(int)(at>>12)*4; Put32(group,offset,U32(group,offset)-2); Seal(group);
                var state=Page(l.State); Put64(state,96,U64(state,96)-2); Seal(state); return at;
            }
            throw new InvalidOperationException("Synthetic tree exceeds fixture.");
        }
        ulong Build(int level,bool full)
        {
            ulong address=Allocate(); int count=level==0?(full?3:first?1:0):(full?60:1);
            byte[] bytes=new byte[1024]; Header(bytes,level==0?PageType.ObjectLeaf:PageType.ObjectBranch,address,0,generation,count,level);
            if(level==0)
            {
                if(first) { decoded.Objects[1].CopyTo(bytes,64); first=false; }
                if(full) for(int i=0;i<3;i++) { Put64(files[i],0,key+(uint)i); files[i].CopyTo(bytes,64+i*256); }
                key+=4;
            }
            else for(int i=0;i<count;i++) { Put64(bytes,64+i*16,key); Put64(bytes,72+i*16,Build(level-1,full&&i==count-1)); }
            Seal(bytes); bytes.CopyTo(Page(address)); return address;
        }
        ulong root=Build(height-1,true); var statePage=Page(l.State); Put64(statePage,80,root); Put64(statePage,64,key-1); Seal(statePage);
        ulong directory=U64(decoded.Objects[1],32); var entries=Page(directory);
        for(int n=0;n<U16(entries,48);n++) { int at=64+n*128; ulong old=U64(entries,at+16); Put64(entries,at+16,U64(files[(int)old-2],0)); }
        Seal(entries);
    }
}
