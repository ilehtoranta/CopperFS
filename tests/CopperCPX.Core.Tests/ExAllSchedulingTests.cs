using System.Buffers.Binary;
using CopperCPX.Qualification;

namespace CopperCPX.Tests;

public class ExAllSchedulingTests
{
    private const uint Pool=32+CpxArena.Scratch+6*1024+CpxArena.IndexBytes,Control=85500,Output=100000;
    private sealed class Meter(byte[] bytes)
    {
        internal readonly byte[] Bytes=bytes;
        internal uint Headers,OutputBytes;
        internal void Touch(uint p)
        {
            if(p>=Pool&&p<Pool+2048&&(p-Pool)%64==0) Headers++;
            else if(p>=Pool+2048&&p<Pool+3584&&(p-Pool-2048)%48==0) Headers++;
            else if(p>=Pool+3584&&p<Pool+4608&&(p-Pool-3584)%64==0) Headers++;
            else if(p>=Pool+4608&&p<Pool+11776&&(p-Pool-4608)%896==0) Headers++;
        }
        internal uint Read(uint p) { Touch(p); return BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan((int)p)); }
    }
    private struct Memory(Meter meter):ICpxMemory
    {
        public byte Read8(uint p)=>meter.Bytes[p];
        public uint Read32(uint p)=>meter.Read(p);
        public void Write8(uint p,byte value) { if(p>=Output&&p<Output+512) meter.OutputBytes++; meter.Bytes[p]=value; }
        public void Write32(uint p,uint value) { meter.Touch(p); if(p>=Output&&p<Output+512) meter.OutputBytes+=4; BinaryPrimitives.WriteUInt32BigEndian(meter.Bytes.AsSpan((int)p),value); }
    }
    [Fact] public void FullEnumerationPoolUsesBoundedStepsAndCallerOwnedState()
    {
        using var d=new DispatcherDriver(512);
        string longName=new('x',107),comment=new('é',79);
        d.Execute(new(ActionKind.Create,longName)); d.Execute(new(ActionKind.Create,"another"));
        // Stored comments are limited to79 DOS bytes, and conversion stays phased.
        Assert.Equal((uint.MaxValue,0u),d.Packet(28,0,0,d.Bstr(longName),d.Bstr(comment,DispatcherDriver.TargetAddress)));
        uint owner=0; for(int n=0;n<32;n++) owner=d.Packet(19).Result;
        for(uint n=0;n<15;n++) Assert.Equal((uint.MaxValue,0u),ExAllPacketTests.All(d,120,1,85000+n*20));
        Array.Fill(d.Ram.Bytes,(byte)0x5a,0,32); uint end=32+CpxArena.Bytes(1024); Array.Fill(d.Ram.Bytes,(byte)0xa5,(int)end,32);
        var meter=new Meter(d.Ram.Bytes); var memory=new Memory(meter); var disk=new Disk{Bytes=d.Image};
        Run();
        Assert.Equal(2u,d.Ram.Read32(Control)); Assert.Equal(uint.MaxValue,d.Ram.Read32(Control+4));
        Assert.All(d.Ram.Bytes[..32],b=>Assert.Equal(0x5a,b)); Assert.All(d.Ram.Bytes.AsSpan((int)end,32).ToArray(),b=>Assert.Equal(0xa5,b));

        void Run()
        {
            uint p=DispatcherDriver.PacketAddress;
            d.Ram.Write32(p+8,1033); d.Ram.Write32(p+20,owner); d.Ram.Write32(p+24,Output);
            d.Ram.Write32(p+28,512); d.Ram.Write32(p+32,6); d.Ram.Write32(p+36,Control);
            var dispatcher=new CpxDosDispatcher<Memory>(memory,32); Assert.Equal(CpxError.None,dispatcher.BeginPacket(p));
            bool complete=false;
            for(int n=0;n<100000;n++)
            {
                meter.Headers=0; meter.OutputBytes=0;
                dispatcher=new(memory,32); StepResult step=dispatcher.Step();
                Assert.InRange(meter.Headers,0u,64u); Assert.InRange(meter.OutputBytes,0u,64u);
                if(step==StepResult.Complete) { Assert.Equal(232u,dispatcher.DosError); complete=true; break; }
                Assert.NotEqual(StepResult.Failed,step);
                if(step!=StepResult.NeedIo) continue;
                var io=dispatcher.Request;
                Assert.Equal(StepResult.NeedIo,dispatcher.Step()); Assert.Equal(io,dispatcher.Request);
                EngineDriver.Service(io,d.Ram,disk); Assert.Equal(CpxError.None,dispatcher.CompleteIo(io.Token,io.Bytes,true));
            }
            Assert.True(complete);
        }
    }
    [Fact] public void InhibitValidationRetirementAndRemountStayInSeparateBoundedPhases()
    {
        using var d=new DispatcherDriver(512); var meter=new Meter(d.Ram.Bytes); var memory=new Memory(meter);
        var disk=new Disk{Bytes=d.Image};
        Run();
        void Run()
        {
            for(int operation=0;operation<4;operation++)
            {
                var dispatcher=new CpxDosDispatcher<Memory>(memory,32);
                if(operation==2) dispatcher.MediaRemoved();
                uint packet=DispatcherDriver.PacketAddress;
                d.Ram.Write32(packet+8,31); d.Ram.Write32(packet+20,(operation&1)==0?1u:0u);
                Assert.Equal(CpxError.None,dispatcher.BeginPacket(packet)); bool complete=false;
                for(int n=0;n<100000;n++)
                {
                    meter.Headers=0;
                    dispatcher=new(memory,32); var step=dispatcher.Step();
                    Assert.InRange(meter.Headers,0u,64u);
                    if(step==StepResult.Complete) { Assert.Equal(0u,dispatcher.DosError); complete=true; break; }
                    Assert.NotEqual(StepResult.Failed,step); if(step!=StepResult.NeedIo) continue;
                    var io=dispatcher.Request; EngineDriver.Service(io,d.Ram,disk);
                    Assert.Equal(CpxError.None,dispatcher.CompleteIo(io.Token,io.Bytes,true));
                }
                Assert.True(complete); Assert.Equal((operation&1)==0,dispatcher.IsInhibited);
            }
        }
    }

}
