using System.Buffers.Binary;
using CopperCPX;
using CopperFS.Cpx4.Reference;

namespace CopperCPX.Tests;

internal struct Ram(byte[] bytes) : ICpxMemory
{
    public byte[] Bytes=bytes;
    public byte Read8(uint p)=>Bytes[p]; public void Write8(uint p,byte value)=>Bytes[p]=value;
    public uint Read32(uint p)=>BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan((int)p));
    public void Write32(uint p,uint value)=>BinaryPrimitives.WriteUInt32BigEndian(Bytes.AsSpan((int)p),value);
}
internal class Disk : IBlockDevice
{
    public byte[] Bytes=new byte[4*1024*1024]; public int SectorSize=>512; public ulong SectorCount=>(ulong)Bytes.Length/512;
    public void Read(ulong at,Span<byte> buffer)=>Bytes.AsSpan((int)(at*512),buffer.Length).CopyTo(buffer);
    public void Write(ulong at,ReadOnlySpan<byte> buffer)=>buffer.CopyTo(Bytes.AsSpan((int)(at*512)));
    public void Flush() { }
}
public class EngineTests
{
    internal static Disk Fresh(int unit)
    { var disk=new Disk(); Cpx4Volume<Disk>.Format(disk,unit,new byte[36864],12345); return disk; }
    internal static int Drive(ref CpxEngine<Ram> engine,Ram ram,IBlockDevice disk)
    {
        int ios=0;
        for(int step=0;step<20000;step++)
        {
            // Reconstruct the value on every resume: only caller-owned arena
            // storage may retain operation state across dispatch boundaries.
            engine=new CpxEngine<Ram>(ram,engine.Arena);
            var result=engine.Step(); if(result==StepResult.Complete) return ios;
            Assert.True(result!=StepResult.Failed,$"Engine failed: {engine.Error}");
            Assert.InRange(engine.TransactionImageCount,0u,42u);
            if(result!=StepResult.NeedIo) continue;
            CpxIo io=engine.Request; Assert.Equal(StepResult.NeedIo,engine.Step()); Assert.Equal(io.Token,engine.Request.Token);
            ulong sector=((ulong)io.Sector.High<<32)|io.Sector.Low;
            if(io.Kind==IoKind.Read) disk.Read(sector,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes));
            else if(io.Kind==IoKind.Write) disk.Write(sector,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes));
            else disk.Flush();
            Assert.Equal(CpxError.None,engine.CompleteIo(io.Token,io.Bytes,true)); ios++;
        }
        Assert.Fail("Continuation did not terminate."); return 0;
    }
    [Theory][InlineData(512)][InlineData(1024)][InlineData(2048)][InlineData(4096)]
    public void MountResumesWithAllStateInCallerArena(int unit)
    {
        Disk disk=Fresh(unit); var ram=new Ram(Enumerable.Repeat((byte)0xa5,100000).ToArray()); var engine=new CpxEngine<Ram>(ram,32);
        Assert.Equal(CpxError.None,engine.Initialize(512,(uint)unit,Wide.From(8192),1));
        Assert.Equal(CpxError.None,engine.BeginOperation(CpxOperation.Mount)); Assert.True(Drive(ref engine,ram,disk)>30); Assert.True(engine.IsMounted);
        Assert.All(ram.Bytes[..32],b=>Assert.Equal(0xa5,b));
        Assert.All(ram.Bytes[(32+(int)CpxArena.Bytes((uint)Math.Max(unit,1024)))..],b=>Assert.Equal(0xa5,b));
    }
    [Fact] public void StaleCompletionAndCancellationCannotReleaseAnOutstandingBuffer()
    {
        var ram=new Ram(new byte[100000]); var engine=new CpxEngine<Ram>(ram,32); engine.Initialize(512,512,Wide.From(8192),1);
        engine.BeginOperation(CpxOperation.Mount); Assert.Equal(StepResult.NeedIo,engine.Step()); var io=engine.Request;
        Assert.Equal(CpxError.StaleCompletion,engine.CompleteIo(io.Token+1,io.Bytes,true)); Assert.True(engine.HasOutstandingIo);
        Assert.Equal(CpxError.Busy,engine.Initialize(512,512,Wide.From(8192),2));
        engine.Cancel(); Assert.Equal(StepResult.NeedIo,engine.Step()); Assert.Equal(CpxError.Busy,engine.BeginOperation(CpxOperation.Mount));
        Assert.Equal(CpxError.Cancelled,engine.CompleteIo(io.Token,io.Bytes,true)); Assert.Equal(StepResult.Failed,engine.Step());
        Assert.Equal(CpxError.None,engine.Initialize(512,512,Wide.From(8192),2)); engine.BeginOperation(CpxOperation.Mount); engine.Step();
        Assert.NotEqual(io.Token,engine.Request.Token); Assert.Equal(CpxError.StaleCompletion,engine.CompleteIo(io.Token,io.Bytes,true));
    }
    [Fact] public void ObjectLookupResumesAcrossBranchAndLeafPagesWithoutPinningThem()
    {
        Disk disk=Fresh(512); var v=new Cpx4Volume<Disk>(disk,new byte[36864]); ulong id=0;
        for(int n=0;n<100;n++) v.Create(System.Text.Encoding.ASCII.GetBytes($"item{n}"),out id);
        var ram=new Ram(new byte[100000]); var e=new CpxEngine<Ram>(ram,32); e.Initialize(512,512,Wide.From(8192),1);
        e.BeginOperation(CpxOperation.Mount); Drive(ref e,ram,disk);
        e.BeginOperation(CpxOperation.ReadObject,new(0,(uint)id)); Drive(ref e,ram,disk); Assert.True(e.ObjectFound); Assert.Equal((uint)id,ram.Read32(e.ObjectRecord+4));
        e.BeginOperation(CpxOperation.ReadObject,new(0,9999)); Drive(ref e,ram,disk); Assert.False(e.ObjectFound);
    }
    [Fact] public void OldFormatCannotBeMountedAsCpx4()
    {
        var disk=Fresh(512); disk.Bytes[65539]=0x33; disk.Bytes[^1021]=0x33;
        var ram=new Ram(new byte[100000]); var e=new CpxEngine<Ram>(ram,32); e.Initialize(512,512,Wide.From(8192),1); e.BeginOperation(CpxOperation.Mount);
        for(int n=0;n<4;n++)
        { if(e.Step()==StepResult.Failed) break; var io=e.Request; disk.Read(io.Sector.Low,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes)); e.CompleteIo(io.Token,io.Bytes,true); }
        Assert.Equal(StepResult.Failed,e.Step()); Assert.Equal(CpxError.Corrupt,e.Error);
    }
}
