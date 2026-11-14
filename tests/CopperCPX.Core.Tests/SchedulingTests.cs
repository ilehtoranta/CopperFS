using System.Buffers.Binary;
using CopperCPX;
using CopperCPX.Reference.Tests;
using CopperFS.Cpx4.Reference;

namespace CopperCPX.Tests;

public class SchedulingTests
{
    [Fact] public void InformationPacketsYieldWithOneChecksumAndNoRetainedStackState()
    {
        var disk=EngineTests.Fresh(512); var v=new Cpx4Volume<Disk>(disk,new byte[36864]);
        v.Create(Enumerable.Repeat((byte)0xe9,100).ToArray(),out ulong id); v.SetComment(id,Enumerable.Repeat((byte)0xff,79).ToArray());
        var meter=new Meter(); FileMutationTests.Mount(disk,new Ram(meter.Bytes)); var memory=new MeterMemory(meter);
        var d=new CpxDosDispatcher<MeterMemory>(memory,32); d.Initialize(0x1234,0x5678);
        void Packet(uint action,uint a,uint b,uint c=0)
        {
            memory.Write32(80008,action); memory.Write32(80020,a); memory.Write32(80024,b); memory.Write32(80028,c);
            Assert.Equal(CpxError.None,d.BeginPacket(80000));
            for(int n=0;n<10000;n++)
            {
                d=new(memory,32); meter.Reset(); var result=d.Step(); Assert.InRange(meter.Checksums,0u,1u);
                if(result==StepResult.Complete) return;
                Assert.NotEqual(StepResult.Failed,result); Assert.Equal(CpxError.Busy,d.BeginPacket(80000));
                if(result!=StepResult.NeedIo) continue; var io=d.Request; Assert.Equal(IoKind.Read,io.Kind);
                for(int wait=0;wait<3;wait++) { meter.Reset(); Assert.Equal(StepResult.NeedIo,d.Step()); Assert.Equal(io,d.Request); Assert.Equal(0u,meter.Checksums); }
                disk.Read(io.Sector.Low,meter.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes)); Assert.Equal(CpxError.None,d.CompleteIo(io.Token,io.Bytes,true));
            }
            Assert.Fail("Information packet failed to terminate.");
        }
        Packet(23,0,86000>>2); Assert.Equal(0u,d.DosError);
        Packet(26409,0,86000>>2); Assert.Equal(0u,d.DosError); Assert.Equal(100,memory.Read8(86008)); Assert.Equal(79,memory.Read8(86144));
        Packet(24,0,86000>>2); Assert.Equal(232u,d.DosError);
        Packet(25,86000>>2,0); Assert.Equal(0u,d.DosError); Packet(26407,7,86000,8); Assert.Equal(0u,d.DosError);
    }
    private sealed class Meter
    {
        internal readonly byte[] Bytes=new byte[100000];
        internal uint Checksums,Expected,Base;
        internal void Reset() { Checksums=0; Expected=uint.MaxValue; }
        internal byte Read(uint address)
        {
            const uint pages=32+CpxArena.Scratch;
            if(address>=pages&&address<pages+6*1024)
            {
                uint offset=(address-pages)&1023,page=address-offset;
                if(offset==0) { Base=page; Expected=1; }
                else if(page==Base&&offset==Expected)
                {
                    // A page CRC skips the checksum field. Ordinary copying
                    // reads bytes 8..11 and cannot match this access sequence.
                    Expected=offset==7?12:offset+1;
                    if(Expected==1024) { Checksums++; Expected=uint.MaxValue; }
                }
                else Expected=uint.MaxValue;
            }
            return Bytes[address];
        }
    }
    private struct MeterMemory(Meter meter):ICpxMemory
    {
        public byte Read8(uint p)=>meter.Read(p);
        public uint Read32(uint p)=>BinaryPrimitives.ReadUInt32BigEndian(meter.Bytes.AsSpan((int)p));
        public void Write8(uint p,byte b)=>meter.Bytes[p]=b;
        public void Write32(uint p,uint n)=>BinaryPrimitives.WriteUInt32BigEndian(meter.Bytes.AsSpan((int)p),n);
    }
    [Fact] public void MutationStepsChecksumAtMostOnePageAndRetainOnlyArenaState()
    {
        var disk=EngineTests.Fresh(512); var reference=new Cpx4Volume<Disk>(disk,new byte[36864]); reference.Create("unwritten"u8,out ulong unwritten);
        for(uint n=0;n<6;n++) reference.Preallocate(unwritten,n*4096,3,out _);
        var meter=new Meter(); var memory=new MeterMemory(meter); var e=new CpxEngine<MeterMemory>(memory,32);
        e.Initialize(512,512,Wide.From(8192),1); e.BeginOperation(CpxOperation.Mount);
        uint checksums=0;
        void Drive()
        {
            for(int n=0;n<20000;n++)
            {
                e=new(memory,32); meter.Reset(); var step=e.Step(); Assert.InRange(meter.Checksums,0u,1u); checksums+=meter.Checksums;
                Assert.True(step!=StepResult.Failed,$"{e.Error}"); if(step==StepResult.Complete) return;
                Assert.Equal(CpxError.Busy,e.BeginOperation(CpxOperation.Flush)); Assert.InRange(e.TransactionImageCount,0u,42u);
                if(step!=StepResult.NeedIo) continue; var io=e.Request;
                for(int delay=0;delay<3;delay++) { meter.Reset(); Assert.Equal(StepResult.NeedIo,e.Step()); Assert.Equal(0u,meter.Checksums); Assert.Equal(io.Token,e.Request.Token); }
                if(io.Kind==IoKind.Read) disk.Read(io.Sector.Low,meter.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes));
                else if(io.Kind==IoKind.Write) disk.Write(io.Sector.Low,meter.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes)); else disk.Flush();
                Assert.Equal(CpxError.None,e.CompleteIo(io.Token,io.Bytes,true));
            }
            Assert.Fail("Unterminated continuation.");
        }
        Drive(); "dir"u8.CopyTo(meter.Bytes.AsSpan(90000)); e.BeginCreate(Wide.From(1),90000,3,true); Drive(); Wide dir=e.MutatedObject;
        Array.Fill(meter.Bytes,(byte)0xe9,90000,100); e.BeginCreate(Wide.From(1),90000,100); Drive(); Wide file=e.MutatedObject;
        Array.Fill(meter.Bytes,(byte)0x5a,91000,1024); e.BeginMappedWrite(file,default,91000,1024); Drive();
        "renamed"u8.CopyTo(meter.Bytes.AsSpan(92000)); e.BeginRename(Wide.From(1),90000,100,dir,92000,7); Drive();
        e.BeginResize(file,Wide.From(701)); Drive(); e.BeginResize(file,Wide.From(2048)); Drive();
        e.BeginResize(file,Wide.From(8192)); Drive();
        for(uint n=2;n<8;n++) { e.BeginMappedWrite(file,Wide.From(n*1024),91000,17); Drive(); }
        e.BeginMappedWrite(Wide.From((uint)unwritten),Wide.From(525),91000,17); Drive();
        e.BeginDelete(dir,92000,7); Drive(); e.BeginOperation(CpxOperation.Flush); Drive();
        Assert.True(checksums>100); new IndependentImage(disk.Bytes);
    }
}
