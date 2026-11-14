using CopperCPX;
using CopperCPX.Reference.Tests;
using CopperFS.Cpx4.Reference;
using System.Text;

namespace CopperCPX.Tests;

public class DispatcherMutationTests
{
    [Fact] public void MutationPacketsCheckLocksAndReserveResourcesBeforeChangingDisk()
    {
        var disk=EngineTests.Fresh(512); var ram=new Ram(new byte[100000]); FileMutationTests.Mount(disk,ram);
        var d=new CpxDosDispatcher<Ram>(ram,32); d.Initialize(0x1234,0x5678);
        const uint packet=80000,fh=81000,name=82000,other=82500,data=83000;
        uint Name(string value,uint at=name)
        { byte[] bytes=Encoding.Latin1.GetBytes(value); ram.Write8(at,(byte)bytes.Length); bytes.CopyTo(ram.Bytes,(int)at+1); return at>>2; }
        void Packet(uint action,uint a=0,uint b=0,uint c=0,uint fourth=0)
        {
            ram.Write32(packet+8,action); ram.Write32(packet+20,a); ram.Write32(packet+24,b); ram.Write32(packet+28,c); ram.Write32(packet+32,fourth);
            Assert.Equal(CpxError.None,d.BeginPacket(packet));
            for(int n=0;n<20000;n++)
            {
                d=new(ram,32); var step=d.Step(); if(step==StepResult.Complete) return; Assert.NotEqual(StepResult.Failed,step);
                if(step!=StepResult.NeedIo) continue; var io=d.Request; Assert.Equal(StepResult.NeedIo,d.Step());
                if(io.Kind==IoKind.Read) disk.Read(io.Sector.Low,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes));
                else if(io.Kind==IoKind.Write) disk.Write(io.Sector.Low,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes)); else disk.Flush();
                d.CompleteIo(io.Token,io.Bytes,true);
            }
            Assert.Fail("Packet did not terminate.");
        }
        Packet(22,0,Name("dir")); Assert.NotEqual(0u,d.Result); uint dir=d.Result; Packet(15,dir);
        Packet(1006,fh>>2,0,Name("dir/file")); Assert.Equal(uint.MaxValue,d.Result); uint handle=ram.Read32(fh+36);
        ram.Bytes.AsSpan((int)data,1024).Fill(0x5a); Packet(87,handle,data,1024); Assert.Equal(1024u,d.Result);
        Packet(16,0,Name("dir/file")); Assert.Equal(202u,d.DosError);
        Packet(17,0,Name("dir/file"),0,Name("moved",other)); Assert.Equal(202u,d.DosError);
        Packet(1022,handle,701,uint.MaxValue); Assert.Equal(701u,d.Result); Assert.Equal(701u,ram.Read32(handle+12));
        Packet(1007,handle); Packet(17,0,Name("dir/file"),0,Name("moved",other)); Assert.Equal(uint.MaxValue,d.Result);
        var decoded=new IndependentImage(disk.Bytes); Assert.Contains((1UL,"moved"),decoded.Names.Keys);
        // Exhaust shared handles. A failed replacement-open must not truncate,
        // and a failed create must not leave a namespace entry behind.
        uint[] handles=new uint[32];
        for(int n=0;n<32;n++) { Packet(1005,fh>>2,0,Name("moved")); Assert.Equal(uint.MaxValue,d.Result); handles[n]=ram.Read32(fh+36); }
        Packet(1004,fh>>2,0,Name("missing")); Assert.Equal(103u,d.DosError);
        Packet(1006,fh>>2,0,Name("moved")); Assert.Equal(202u,d.DosError);
        Assert.False(new Cpx4Volume<Disk>(disk,new byte[36864]).TryGetInfo("missing"u8,out _));
        foreach(uint h in handles) Packet(1007,h);
        Packet(1006,fh>>2,0,Name("moved")); Assert.Equal(uint.MaxValue,d.Result); Packet(1007,ram.Read32(fh+36));
        var v=new Cpx4Volume<Disk>(disk,new byte[36864]); Assert.True(v.TryGetInfo("moved"u8,out var info)); Assert.Equal(0UL,info.Length);
        uint[] locks=new uint[32]; for(int n=0;n<32;n++) { Packet(8,0,Name("dir"),0xfffffffe); Assert.NotEqual(0u,d.Result); locks[n]=d.Result; }
        Packet(22,0,Name("not-created")); Assert.Equal(103u,d.DosError);
        foreach(uint l in locks) Packet(15,l);
        Packet(16,0,Name("moved")); Assert.Equal(uint.MaxValue,d.Result); Packet(16,0,Name("dir")); Assert.Equal(uint.MaxValue,d.Result);
        Assert.False(d.HasOpenResources); new IndependentImage(disk.Bytes); new Cpx4Volume<Disk>(disk,new byte[36864]).Check();
    }
}
