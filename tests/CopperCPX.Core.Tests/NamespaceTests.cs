using CopperCPX;
using CopperFS.Cpx4.Reference;
using CopperCPX.Reference.Tests;
using System.Text;

namespace CopperCPX.Tests;

public class NamespaceTests
{
    [Fact] public void EmptyReserveAndFullDiskRejectCreationButAllowDeletionAndRefill()
    {
        var disk=new MemoryDevice(ExhaustedVolume.Create()); var ram=new Ram(new byte[100000]); var e=FileMutationTests.Mount(disk,ram);
        Wide generation=e.Transaction; e.BeginCreate(Wide.From(1),90000,Name(ram,"new"));
        for(int n=0;n<20000;n++)
        {
            var step=e.Step(); if(step==StepResult.Failed) break; Assert.NotEqual(StepResult.Complete,step);
            if(step!=StepResult.NeedIo) continue; var io=e.Request;
            if(io.Kind==IoKind.Read) disk.Read(io.Sector.Low,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes));
            else if(io.Kind==IoKind.Write) disk.Write(io.Sector.Low,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes)); else disk.Flush();
            e.CompleteIo(io.Token,io.Bytes,true);
        }
        Assert.Equal(CpxError.ReserveExhausted,e.Error); Assert.True(Wide.Equal(generation,e.Transaction)); Assert.True(e.IsMounted);
        new IndependentImage(disk.Bytes);
        Assert.Equal(CpxError.None,e.BeginDelete(Wide.From(1),90000,Name(ram,"full"))); EngineTests.Drive(ref e,ram,disk);
        Create(ref e,ram,disk,Wide.From(1),"after"); new IndependentImage(disk.Bytes); new Cpx4Volume<MemoryDevice>(disk,new byte[36864]).Check();
    }
    [Fact] public void NamespaceAdmissionRejectsCyclesProtectedObjectsAndReservedAliases()
    {
        var disk=EngineTests.Fresh(512); var ram=new Ram(new byte[100000]); var e=FileMutationTests.Mount(disk,ram);
        Wide root=Wide.From(1),a=Create(ref e,ram,disk,root,"a",true),b=Create(ref e,ram,disk,a,"b",true);
        e.BeginRename(root,90000,Name(ram,"a"),b,91000,Name(ram,"moved",91000)); FileMutationTests.DriveFailure(ref e,ram,disk,CpxError.InvalidArgument);
        e.BeginDelete(root,90000,Name(ram,"a")); FileMutationTests.DriveFailure(ref e,ram,disk,CpxError.DirectoryNotEmpty);
        Assert.Equal(CpxError.InvalidArgument,e.BeginCreate(root,90000,Name(ram,"__u0000000000000001")));
        e.BeginProtection(b,1); EngineTests.Drive(ref e,ram,disk);
        e.BeginDelete(a,90000,Name(ram,"b")); FileMutationTests.DriveFailure(ref e,ram,disk,CpxError.Protected);
        new IndependentImage(disk.Bytes);
    }
    private static uint Name(Ram ram,string name,uint at=90000)
    { byte[] bytes=Encoding.Latin1.GetBytes(name); bytes.CopyTo(ram.Bytes,(int)at); return (uint)bytes.Length; }
    private static Wide Create(ref CpxEngine<Ram> e,Ram ram,IBlockDevice disk,Wide parent,string name,bool directory=false)
    {
        Assert.Equal(CpxError.None,e.BeginCreate(parent,90000,Name(ram,name),directory)); EngineTests.Drive(ref e,ram,disk); return e.MutatedObject;
    }
    [Theory][InlineData(512)][InlineData(4096)]
    public void SplittingNamespaceTreesRenameAcrossDirectoriesAndDelete(int unit)
    {
        Disk disk=EngineTests.Fresh(unit); var ram=new Ram(new byte[100000]); var e=FileMutationTests.Mount(disk,ram,unit);
        Wide root=Wide.From(1),dir=Create(ref e,ram,disk,root,"directory",true);
        var ids=new List<Wide>();
        for(int i=0;i<150;i++) ids.Add(Create(ref e,ram,disk,root,$"entry-{i:D3}"));
        string longName=new string('é',100);
        Wide longId=Create(ref e,ram,disk,dir,longName);
        uint old=Name(ram,"entry-050"),next=Name(ram,"renamed-Å",91000);
        Assert.Equal(CpxError.None,e.BeginRename(root,90000,old,dir,91000,next)); EngineTests.Drive(ref e,ram,disk);
        Assert.Equal(CpxError.None,e.BeginRename(dir,90000,Name(ram,longName),root,91000,Name(ram,"entry-051",91000),true)); EngineTests.Drive(ref e,ram,disk);
        var decoded=new IndependentImage(disk.Bytes); Assert.Equal((ulong)ids[50].Low,decoded.Names[(dir.Low,"renamed-å")]);
        Assert.Equal((ulong)longId.Low,decoded.Names[(1,"entry-051")]); Assert.DoesNotContain((ulong)ids[51].Low,decoded.Objects.Keys);
        for(int i=0;i<150;i++) if(i!=50)
        { e.BeginDelete(root,90000,Name(ram,$"entry-{i:D3}")); EngineTests.Drive(ref e,ram,disk); }
        e.BeginDelete(dir,90000,Name(ram,"renamed-Å")); EngineTests.Drive(ref e,ram,disk);
        e.BeginDelete(root,90000,Name(ram,"directory")); EngineTests.Drive(ref e,ram,disk);
        new Cpx4Volume<Disk>(disk,new byte[36864]).Check(); decoded=new(disk.Bytes); Assert.Single(decoded.Objects); Assert.Empty(decoded.Names);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void ExternalTruncationReclaimsInlineAndTreeExtentsThenZeroExtends(bool tree)
    {
        Disk disk=EngineTests.Fresh(512); var host=new Cpx4Volume<Disk>(disk,new byte[36864]); host.Create("file"u8,out ulong id);
        byte[] data=Enumerable.Repeat((byte)0x5a,14000).ToArray();
        if(tree) for(int n=0;n<80;n++) host.Write(id,(ulong)n*1024,data.AsSpan(0,512),out _);
        else host.Write(id,0,data,out _);
        var ram=new Ram(new byte[100000]); var e=FileMutationTests.Mount(disk,ram);
        e.BeginResize(Wide.From((uint)id),Wide.From(701)); EngineTests.Drive(ref e,ram,disk);
        new IndependentImage(disk.Bytes); var check=new Cpx4Volume<Disk>(disk,new byte[36864]); check.Check();
        e.BeginResize(Wide.From((uint)id),Wide.From(5000)); EngineTests.Drive(ref e,ram,disk);
        check=new(disk,new byte[36864]); byte[] actual=new byte[5000]; Assert.Equal(5000,check.Read(id,0,actual));
        Assert.All(actual[..512],b=>Assert.Equal(0x5a,b)); Assert.All(actual[701..],b=>Assert.Equal(0,b));
        e.BeginResize(Wide.From((uint)id),default); EngineTests.Drive(ref e,ram,disk);
        ram.Bytes.AsSpan(91000,1024).Fill(0x77); e.BeginMappedWrite(Wide.From((uint)id),default,91000,1024); EngineTests.Drive(ref e,ram,disk);
        check=new(disk,new byte[36864]); actual=new byte[1024]; Assert.Equal(1024,check.Read(id,0,actual)); Assert.All(actual,b=>Assert.Equal(0x77,b));
        e.BeginDelete(Wide.From(1),90000,Name(ram,"file")); EngineTests.Drive(ref e,ram,disk);
        new IndependentImage(disk.Bytes); new Cpx4Volume<Disk>(disk,new byte[36864]).Check();
    }
    [Fact] public void InlineToSparseResizePreservesData()
    {
        Disk disk=EngineTests.Fresh(512); var host=new Cpx4Volume<Disk>(disk,new byte[36864]); host.Create("file"u8,out ulong id); host.Write(id,0,"abc"u8,out _);
        var ram=new Ram(new byte[100000]); var e=FileMutationTests.Mount(disk,ram);
        e.BeginResize(Wide.From((uint)id),new(1,123)); EngineTests.Drive(ref e,ram,disk);
        var check=new Cpx4Volume<Disk>(disk,new byte[36864]); check.Check(); new IndependentImage(disk.Bytes);
        byte[] actual=new byte[1000]; Assert.Equal(1000,check.Read(id,0,actual)); Assert.Equal("abc"u8.ToArray(),actual[..3]); Assert.All(actual[3..],b=>Assert.Equal(0,b));
    }
}
