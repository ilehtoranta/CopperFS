using System.Buffers.Binary;
using CopperCPX.Qualification;

namespace CopperCPX.Tests;

public class LifecycleSchedulingTests
{
    private sealed class Meter(byte[] bytes,uint pool)
    {
        internal readonly byte[] Bytes=bytes;
        internal uint ControlReads;
        internal uint Read(uint p)
        {
            if(p>=pool&&p<pool+4608)
            {
                uint offset=p-pool;
                if(offset<2048?(offset&63)<8:offset<3584?(offset-2048)%48<8:(offset-3584)%64<8) ControlReads++;
            }
            return BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan((int)p));
        }
    }
    private struct Memory(Meter meter):ICpxMemory
    {
        public byte Read8(uint p)=>meter.Bytes[p];
        public uint Read32(uint p)=>meter.Read(p);
        public void Write8(uint p,byte value)=>meter.Bytes[p]=value;
        public void Write32(uint p,uint value)=>BinaryPrimitives.WriteUInt32BigEndian(meter.Bytes.AsSpan((int)p),value);
    }
    [Theory][InlineData(1004u)][InlineData(1005u)][InlineData(1006u)]
    public void OpenSeparatesConflictScanFromLastHandleReservation(uint action)
    {
        using var d=new DispatcherDriver(512); d.Execute(new(ActionKind.Create,"file"));
        d.Execute(new(ActionKind.Create,"other"));
        uint[] handles=new uint[31];
        for(int n=0;n<31;n++) { Assert.Equal((uint.MaxValue,0u),d.Packet(1005,81000>>2,0,d.Bstr("other"))); handles[n]=d.Ram.Read32(81036); }
        const uint pool=32+CpxArena.Scratch+6*1024+CpxArena.IndexBytes;
        var meter=new Meter(d.Ram.Bytes,pool); var memory=new Memory(meter); var dispatcher=new CpxDosDispatcher<Memory>(memory,32);
        d.Ram.Write32(80008,action); d.Ram.Write32(80020,81000>>2); d.Ram.Write32(80024,0); d.Ram.Write32(80028,d.Bstr("file"));
        Assert.Equal(CpxError.None,dispatcher.BeginPacket(80000)); bool complete=false,fullScan=false;
        for(int n=0;n<100000;n++)
        {
            dispatcher=new(memory,32); meter.ControlReads=0; StepResult step=dispatcher.Step();
            Assert.InRange(meter.ControlReads,0u,64u); fullScan|=meter.ControlReads==64;
            if(step==StepResult.Complete) { complete=true; break; }
            Assert.NotEqual(StepResult.Failed,step);
            if(step==StepResult.NeedIo)
            {
                var io=dispatcher.Request; Assert.Equal(StepResult.NeedIo,dispatcher.Step());
                Assert.Equal(io,dispatcher.Request); EngineDriver.Service(io,d.Ram,new Disk { Bytes=d.Image });
                Assert.Equal(CpxError.None,dispatcher.CompleteIo(io.Token,io.Bytes,true));
            }
        }
        Assert.True(complete); Assert.True(fullScan); Assert.Equal(0u,dispatcher.DosError);
        Assert.Equal(pool+31*64+8,d.Ram.Read32(81036)); d.Packet(1007,d.Ram.Read32(81036));
        foreach(uint h in handles) d.Packet(1007,h); Assert.False(d.Dispatcher.HasOpenResources);
    }
    [Fact] public void UnlockWithEveryEnumerationSlotOccupiedYieldsBeforeRetiringResources()
    {
        using var d=new DispatcherDriver(512); d.Execute(new(ActionKind.Create,"file"));
        uint[] locks=new uint[32]; for(int n=0;n<32;n++) { var r=d.Packet(19); Assert.Equal(0u,r.Error); locks[n]=r.Result; }
        uint owner=locks[31];
        for(uint n=0;n<16;n++)
        {
            uint fib=86000+n*300; Assert.Equal((uint.MaxValue,0u),d.Packet(23,owner,fib>>2));
            Assert.Equal((uint.MaxValue,0u),d.Packet(24,owner,fib>>2));
        }
        const uint pool=32+CpxArena.Scratch+6*1024+CpxArena.IndexBytes;
        uint[] generations=Enumerable.Range(0,16).Select(n=>d.Ram.Read32(pool+3584+(uint)n*64+4)).ToArray();
        var meter=new Meter(d.Ram.Bytes,pool); var memory=new Memory(meter); var dispatcher=new CpxDosDispatcher<Memory>(memory,32);
        d.Ram.Write32(80008,15); d.Ram.Write32(80020,owner); Assert.Equal(CpxError.None,dispatcher.BeginPacket(80000));
        meter.ControlReads=0; Assert.Equal(StepResult.Pending,dispatcher.Step()); Assert.InRange(meter.ControlReads,0u,64u);
        Assert.Equal(1u,d.Ram.Read32((owner<<2)-8));
        for(uint n=0;n<16;n++) Assert.Equal(1u,d.Ram.Read32(pool+3584+n*64));
        dispatcher=new(memory,32); meter.ControlReads=0; Assert.Equal(StepResult.Complete,dispatcher.Step());
        Assert.InRange(meter.ControlReads,0u,64u); Assert.Equal(0u,dispatcher.DosError);
        for(uint n=0;n<16;n++) { Assert.Equal(0u,d.Ram.Read32(pool+3584+n*64)); Assert.Equal(generations[n],d.Ram.Read32(pool+3584+n*64+4)); }
        Assert.Equal(0u,d.Ram.Read32((owner<<2)-8));
        for(int n=0;n<31;n++) Assert.Equal((uint.MaxValue,0u),d.Packet(15,locks[n]));
        Assert.False(d.Dispatcher.HasOpenResources);
        // Every retired enumeration slot is reusable with a fresh serial.
        for(uint n=0;n<16;n++) { uint fib=86000+n*300; d.Packet(23,0,fib>>2); Assert.Equal((uint.MaxValue,0u),d.Packet(24,0,fib>>2)); }
    }
    [Fact] public void ParentPoolExhaustionPreservesFilePositionAndSourceOwners()
    {
        using var d=new DispatcherDriver(512); d.Execute(new(ActionKind.Create,"file")); d.Execute(new(ActionKind.Write,"file",Data:[1,2,3]));
        Assert.Equal((uint.MaxValue,0u),d.Packet(1004,81000>>2,0,d.Bstr("file"))); uint h=d.Ram.Read32(81036);
        Assert.Equal((0u,0u),d.Packet(1008,h,2,uint.MaxValue));
        uint[] locks=new uint[32]; for(int n=0;n<32;n++) locks[n]=d.Packet(8,0,d.Bstr("file"),0xfffffffe).Result;
        Assert.Equal((0u,103u),d.Packet(1031,h)); Assert.Equal((0u,103u),d.Packet(29,locks[0]));
        Assert.Equal(2u,d.Ram.Read32(h+12)); Assert.Equal((1u,0u),d.Packet(82,h,90000,1)); Assert.Equal(3,d.Ram.Read8(90000));
        foreach(uint l in locks) Assert.Equal((uint.MaxValue,0u),d.Packet(15,l));
        uint parent=d.Packet(1031,h).Result; Assert.NotEqual(0u,parent); d.Packet(15,parent); d.Packet(1007,h);
        Assert.False(d.Dispatcher.HasOpenResources);
    }
    [Theory][InlineData(0u)][InlineData(1u)]
    public void ChangeModeRoundTripKeepsPositionContentsAndEntryContext(uint kind)
    {
        using var d=new DispatcherDriver(512); uint dir=d.Packet(22,0,d.Bstr("dir")).Result; d.Packet(15,dir);
        Assert.Equal((uint.MaxValue,0u),d.Packet(1004,81000>>2,0,d.Bstr("dir/file"))); uint h=d.Ram.Read32(81036);
        d.Ram.Write8(90000,37); Assert.Equal((1u,0u),d.Packet(87,h,90000,1));
        uint l=0;
        if(kind==0) { d.Packet(1007,h); l=d.Packet(8,0,d.Bstr("dir/file"),0xfffffffe).Result; }
        uint bptr=kind==0?l:81000>>2;
        Assert.Equal((uint.MaxValue,0u),d.Packet(1028,kind,bptr,uint.MaxValue));
        Assert.Equal((uint.MaxValue,0u),d.Packet(1028,kind,bptr,0xfffffffe));
        uint parent=d.Packet(kind==0?29u:1031u,kind==0?l:h).Result; Assert.NotEqual(0u,parent);
        Assert.Equal((uint.MaxValue,0u),d.Packet(23,parent,88000>>2)); Assert.Equal("dir",System.Text.Encoding.Latin1.GetString(d.Ram.Bytes,88009,d.Ram.Read8(88008))); d.Packet(15,parent);
        if(kind==0) { Assert.Equal((uint.MaxValue,0u),d.Packet(1026,81000>>2,l)); h=d.Ram.Read32(81036); Assert.Equal(0u,d.Ram.Read32(h+12)); }
        else Assert.Equal(1u,d.Ram.Read32(h+12));
        d.Packet(1008,h,0,uint.MaxValue); Assert.Equal((1u,0u),d.Packet(82,h,90000,1)); Assert.Equal(37,d.Ram.Read8(90000));
        d.Packet(1007,h); Assert.False(d.Dispatcher.HasOpenResources);
    }
}
