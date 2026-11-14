using CopperCPX;
using CopperFS.Cpx4.Reference;
namespace CopperCPX.Tests;
public class FileDataTests
{
    [Fact] public void SparseGrowthPreservesThePrefixAndNullBuffersAreInvalid()
    {
        var disk=EngineTests.Fresh(512); var v=new Cpx4Volume<Disk>(disk,new byte[36864]); v.Create("file"u8,out ulong id); v.Write(id,0,"abc"u8,out _);
        var ram=new Ram(new byte[100000]); var e=FileMutationTests.Mount(disk,ram);
        Assert.Equal(CpxError.InvalidArgument,e.BeginFileRead(Wide.From((uint)id),default,0,1));
        Assert.Equal(CpxError.InvalidArgument,e.BeginOperation(CpxOperation.SetDate,Wide.From((uint)id)));
        Assert.Equal(CpxError.None,e.BeginMappedWrite(Wide.From((uint)id),Wide.From(999),90000,1));
        EngineTests.Drive(ref e,ram,disk); Assert.Equal(1u,e.TransferredBytes); Assert.True(e.IsMounted);
        v=new(disk,new byte[36864]); byte[] actual=new byte[1000]; Assert.Equal(1000,v.Read(id,0,actual));
        Assert.Equal("abc"u8.ToArray(),actual[..3]); Assert.All(actual[3..],b=>Assert.Equal(0,b)); v.Check();
    }
    [Theory][InlineData(512)][InlineData(4096)]
    public void NativeWritePromotesInlineAndGrowsCoalescedExtents(int unit)
    {
        var disk=EngineTests.Fresh(unit); var v=new Cpx4Volume<Disk>(disk,new byte[36864]); v.Create("file"u8,out ulong id);
        v.Write(id,0,"original"u8,out _);
        var ram=new Ram(new byte[200000]); var e=FileMutationTests.Mount(disk,ram,unit);
        byte[] payload=Enumerable.Range(0,12000).Select(n=>(byte)(n*31+17)).ToArray(); payload.CopyTo(ram.Bytes,100000);
        Assert.Equal(CpxError.None,e.BeginMappedWrite(Wide.From((uint)id),Wide.From(3),100000,(uint)payload.Length)); EngineTests.Drive(ref e,ram,disk);
        Assert.Equal((uint)payload.Length,e.CommittedBytes);
        var reopened=new Cpx4Volume<Disk>(disk,new byte[36864]); reopened.Check(); new CopperCPX.Reference.Tests.IndependentImage(disk.Bytes);
        byte[] actual=new byte[12003]; Assert.Equal(actual.Length,reopened.Read(id,0,actual));
        Assert.Equal("ori"u8.ToArray(),actual[..3]); Assert.Equal(payload,actual[3..]);
    }
    [Fact] public void AllocationAdmissionRefillsLowReserveBeforeWriting()
    {
        var disk=EngineTests.Fresh(512); var v=new Cpx4Volume<Disk>(disk,new byte[36864]); ulong id=0;
        for(int n=0;n<100;n++) { v.Create(System.Text.Encoding.ASCII.GetBytes(new string('x',90)+n),out id); if(v.AvailableReservePages<30) break; }
        Assert.True(v.AvailableReservePages<30); byte[] before=(byte[])disk.Bytes.Clone();
        var ram=new Ram(new byte[100000]); var e=FileMutationTests.Mount(disk,ram);
        e.BeginMappedWrite(Wide.From((uint)id),default,90000,512); EngineTests.Drive(ref e,ram,disk);
        Assert.True(e.IsMounted); Assert.Equal(512u,e.CommittedBytes);
        var reopened=new Cpx4Volume<Disk>(disk,new byte[36864]); Assert.True(reopened.AvailableReservePages>=30); reopened.Check();
    }
    [Fact] public void ReadsSparseTreeAndUnwrittenExtentsAndOverwritesMappedData()
    {
        var disk=EngineTests.Fresh(512); var v=new Cpx4Volume<Disk>(disk,new byte[36864]); v.Create("file"u8,out ulong id);
        for(uint n=0;n<80;n++) { byte[] data=Enumerable.Repeat((byte)(n+1),512).ToArray(); Assert.Equal(MutationResult.Success,v.Write(id,n*1024,data,out _)); }
        Assert.Equal(MutationResult.Success,v.Preallocate(id,80*1024,2,out _));
        byte[] expected=new byte[81*1024]; int length=v.Read(id,0,expected);
        var ram=new Ram(new byte[200000]); var e=FileMutationTests.Mount(disk,ram);
        Assert.Equal(CpxError.None,e.BeginFileRead(Wide.From((uint)id),default,100000,(uint)expected.Length)); EngineTests.Drive(ref e,ram,disk);
        Assert.Equal((uint)length,e.TransferredBytes); Assert.Equal(expected,ram.Bytes.AsSpan(100000,expected.Length).ToArray());
        ram.Bytes.AsSpan(190000,9).Fill(0xab); e.BeginMappedWrite(Wide.From((uint)id),Wide.From(3*1024+10),190000,9); EngineTests.Drive(ref e,ram,disk);
        var reopened=new Cpx4Volume<Disk>(disk,new byte[36864]); byte[] actual=new byte[20]; reopened.Read(id,3*1024,actual);
        Assert.All(actual[10..19],b=>Assert.Equal(0xab,b)); Assert.Equal(4,actual[9]); Assert.Equal(4,actual[19]); reopened.Check();
    }
    [Fact] public void LookupConvertsLatin1AndSupportsAliasesAcrossTreePages()
    {
        var disk=EngineTests.Fresh(512); var v=new Cpx4Volume<Disk>(disk,new byte[36864]); ulong expected=0;
        for(int n=0;n<70;n++) v.Create(System.Text.Encoding.ASCII.GetBytes($"file{n}"),out _);
        v.Create(new byte[]{0xc4,0xe9,0xff},out expected);
        var ram=new Ram(new byte[100000]); var e=FileMutationTests.Mount(disk,ram); new byte[]{0xe4,0xc9,0xff}.CopyTo(ram.Bytes,90000);
        Assert.Equal(CpxError.None,e.BeginLookup(Wide.From(1),90000,3)); EngineTests.Drive(ref e,ram,disk);
        Assert.True(e.LookupFound); Assert.Equal((uint)expected,e.LookupId.Low);
        "missing"u8.CopyTo(ram.Bytes.AsSpan(90000)); e.BeginLookup(Wide.From(1),90000,7); EngineTests.Drive(ref e,ram,disk); Assert.False(e.LookupFound);
    }
    [Fact] public void DispatcherOwnsBoundedHandlesAndReportsDurablePosition()
    {
        var disk=EngineTests.Fresh(512); var v=new Cpx4Volume<Disk>(disk,new byte[36864]); v.Create("file"u8,out _);
        var ram=new Ram(new byte[100000]); FileMutationTests.Mount(disk,ram); var d=new CpxDosDispatcher<Ram>(ram,32); d.Initialize(0x1234,0x5678);
        const uint packet=80000,fh=81000,name=82000,data=83000;
        ram.Write8(name,4); "file"u8.CopyTo(ram.Bytes.AsSpan((int)name+1));
        void Packet(uint action,uint a=0,uint b=0,uint c=0)
        {
            ram.Write32(packet+8,action); ram.Write32(packet+20,a); ram.Write32(packet+24,b); ram.Write32(packet+28,c);
            Assert.Equal(CpxError.None,d.BeginPacket(packet));
            for(int n=0;n<20000;n++)
            {
                var step=d.Step(); if(step==StepResult.Complete) return;
                Assert.NotEqual(StepResult.Failed,step); Assert.Equal(CpxError.Busy,d.BeginPacket(packet));
                if(step!=StepResult.NeedIo) continue; var io=d.Request;
                if(io.Kind==IoKind.Read) disk.Read(io.Sector.Low,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes));
                else if(io.Kind==IoKind.Write) disk.Write(io.Sector.Low,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes)); else disk.Flush();
                d.CompleteIo(io.Token,io.Bytes,true);
            }
            Assert.Fail("Packet did not finish.");
        }
        Packet(1004,fh>>2,0,name>>2); Assert.Equal(uint.MaxValue,d.Result); uint h=ram.Read32(fh+36); Assert.True(d.HasOpenResources);
        "hello"u8.CopyTo(ram.Bytes.AsSpan((int)data)); Packet(87,h,data,5); Assert.Equal(5u,d.Result); Assert.Equal(5u,ram.Read32(h+12));
        Packet(1008,h,0,uint.MaxValue); Assert.Equal(5u,d.Result);
        Packet(82,h,data+100,5); Assert.Equal(5u,d.Result); Assert.Equal("hello"u8.ToArray(),ram.Bytes.AsSpan((int)data+100,5).ToArray());
        ram.Write32(packet+32,data+200); ram.Write32(data+208,0); ram.Write32(data+212,2);
        Packet(26400,h,data+208,uint.MaxValue); Assert.Equal(uint.MaxValue,d.Result); Assert.Equal(5u,ram.Read32(data+204)); Assert.Equal(2u,ram.Read32(h+12));
        ram.Write32(data+208,uint.MaxValue); ram.Write32(data+212,0xfffffffd); ram.Write32(data+200,0xabcdef01);
        Packet(26400,h,data+208,0); Assert.Equal(219u,d.DosError); Assert.Equal(2u,ram.Read32(h+12)); Assert.Equal(0xabcdef01u,ram.Read32(data+200));
        Packet(1007,h); Assert.False(d.HasOpenResources); Packet(82,h,data,1); Assert.Equal(211u,d.DosError);
        for(int n=0;n<32;n++) { Packet(1005,fh>>2,0,name>>2); Assert.Equal(uint.MaxValue,d.Result); }
        Packet(1005,fh>>2,0,name>>2); Assert.Equal(103u,d.DosError);
        Packet(0x777777); Assert.Equal(209u,d.DosError);
    }
}
