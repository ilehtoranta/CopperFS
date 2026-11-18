using CopperFS.Cpx4.Reference;
using CopperCPX.Reference.Tests;

namespace CopperCPX.Tests;

public class CoalescedShadowBarrierTests
{
    private sealed class Fixture
    {
        internal readonly CrashDevice Disk;
        internal readonly Ram Ram=new(new byte[1000000]);
        internal CpxEngine<Ram> Engine;
        internal readonly Wide Id;
        internal Fixture(int unit,bool catalogue)
        {
            var initial=new MemoryDevice(); Cpx4Volume<MemoryDevice>.FormatDirectInodes(initial,unit,new byte[49152],12345,true,catalogue);
            Disk=new(initial.Bytes); Engine=new(Ram,32); Engine.Initialize(512,(uint)unit,Disk.SectorCount,1,metadataPageBytes:(uint)unit);
            Engine.EnableBufferedWrites(300000,CpxEngine<Ram>.BufferedStorageBytes((uint)unit)); Engine.EnableShadowVolume(900000,32768,catalogue?5u:4u);
            Engine.BeginOperation(CpxOperation.Mount); Drive();
            "file"u8.CopyTo(Ram.Bytes.AsSpan(100000)); Engine.BeginCreate(Wide.From(1),100000,4); Drive(); Id=Engine.MutatedObject;
            Ram.Bytes.AsSpan(100000,8192).Fill(0x6a); Engine.BeginMappedWrite(Id,default,100000,8192); Drive();
        }
        private void Transfer(CpxIo io)
        {
            var bytes=Ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes);
            if(io.Kind==IoKind.Read) Disk.Read(io.Sector,bytes);
            else if(io.Kind==IoKind.Write) Disk.Write(io.Sector,bytes);
            else if(io.Kind==IoKind.Flush) Disk.Flush();
            else throw new InvalidOperationException("Unexpected maintenance request.");
        }
        internal List<IoKind> Drive()
        {
            var requests=new List<IoKind>();
            for(int turn=0;turn<20000;turn++)
            {
                var step=Engine.Step(); if(step==StepResult.Complete) return requests;
                Assert.NotEqual(StepResult.Failed,step); if(step!=StepResult.NeedIo) continue;
                var io=Engine.Request; requests.Add(io.Kind); Transfer(io); Assert.Equal(CpxError.None,Engine.CompleteIo(io.Token,io.Bytes,true));
            }
            throw new InvalidOperationException("Engine did not finish.");
        }
        internal List<IoKind> Barrier()
        { Assert.Equal(CpxError.None,Engine.BeginPersistenceBarrier()); return Drive(); }
        internal void Overwrite()
        {
            Ram.Bytes.AsSpan(100000,769).Fill(0xa9);
            Assert.Equal(CpxError.None,Engine.BeginMappedWrite(Id,Wide.From(13),100000,769)); Drive();
            Assert.False(Engine.HasBufferedChanges); Assert.True(Disk.PendingVersions>0);
        }
        internal void VerifyDurable(bool overwritten)
        {
            Assert.Equal(0,Disk.PendingVersions);
            var durable=new MemoryDevice(Disk.Durable); var check=new Cpx4Volume<MemoryDevice>(durable,new byte[49152]); check.Check();
            var actual=new byte[8192]; Assert.Equal(actual.Length,check.Read((ulong)Id,0,actual));
            byte[] expected=Enumerable.Repeat((byte)0x6a,8192).ToArray(); if(overwritten) expected.AsSpan(13,769).Fill(0xa9);
            Assert.Equal(expected,actual);
        }
        internal uint Packet(uint action)
        {
            var dispatcher=new CpxDosDispatcher<Ram>(Ram,32); dispatcher.Initialize(0xabc,0xdef);
            Ram.Bytes.AsSpan(200000,48).Clear(); Ram.Write32(200008,action); if(action==31) Ram.Write32(200020,1);
            Assert.Equal(CpxError.None,dispatcher.BeginPacket(200000)); uint flushes=0;
            for(int turn=0;turn<20000;turn++)
            {
                var step=dispatcher.Step();
                if(step==StepResult.Complete) { Assert.Equal(uint.MaxValue,dispatcher.Result); Assert.Equal(0u,dispatcher.DosError); return flushes; }
                Assert.NotEqual(StepResult.Failed,step); if(step!=StepResult.NeedIo) continue;
                var io=dispatcher.Request; if(io.Kind==IoKind.Flush) flushes++;
                Transfer(io); Assert.Equal(CpxError.None,dispatcher.CompleteIo(io.Token,io.Bytes,true));
            }
            throw new InvalidOperationException("Packet did not finish.");
        }
    }

    [Theory][InlineData(512,false)][InlineData(512,true)][InlineData(4096,false)][InlineData(4096,true)]
    public void SuccessfulPublicationBarrierCanBeReusedButADataWriteRequiresAnother(int unit,bool catalogue)
    {
        var f=new Fixture(unit,catalogue);
        Assert.Equal(2,f.Barrier().Count(k=>k==IoKind.Flush)); f.VerifyDurable(false);
        Assert.Empty(f.Barrier()); f.VerifyDurable(false);
        f.Overwrite(); Assert.Single(f.Barrier(),k=>k==IoKind.Flush); f.VerifyDurable(true);
        Assert.Empty(f.Barrier());
        f.Engine.BeginFileRead(f.Id,default,120000,8192); f.Drive();
        Assert.Single(f.Barrier(),k=>k==IoKind.Flush); f.VerifyDurable(true);
        "New"u8.CopyTo(f.Ram.Bytes.AsSpan(100000)); f.Engine.BeginLabel(100000,3); f.Drive();
        Assert.Single(f.Barrier(),k=>k==IoKind.Flush); f.VerifyDurable(true);
        var durable=new MemoryDevice(f.Disk.Durable); var independentRam=new Ram(new byte[1000000]); var independent=new CpxEngine<Ram>(independentRam,32);
        independent.Initialize(512,(uint)unit,durable.SectorCount,2,readOnly:true,metadataPageBytes:(uint)unit);
        independent.EnableBufferedWrites(300000,CpxEngine<Ram>.BufferedStorageBytes((uint)unit)); independent.EnableShadowVolume(900000,32768,catalogue?5u:4u);
        independent.BeginOperation(CpxOperation.Mount); EngineTests.Drive(ref independent,independentRam,durable);
        Assert.Equal("New",VolumeLabelTests.Cached(independent,independentRam));
    }

    [Theory][InlineData(27u)][InlineData(31u)]
    public void PublicFlushAndInhibitPersistAnUnpublishedInPlaceOverwrite(uint action)
    {
        var f=new Fixture(512,true); f.Barrier(); f.Overwrite();
        Assert.Equal(1u,f.Packet(action)); f.VerifyDurable(true);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void BothEngineEntriesUsedByNativeShutdownPersistBufferedInPlaceWrites(bool protectedVolume)
    {
        var f=new Fixture(512,true); f.Barrier(); f.Overwrite();
        // NativeCpxLifecycle.BeginShutdown chooses these two public entries.
        Assert.Equal(CpxError.None,protectedVolume?f.Engine.BeginPersistenceBarrier():f.Engine.BeginOperation(CpxOperation.Flush));
        Assert.Single(f.Drive(),k=>k==IoKind.Flush); f.VerifyDurable(true);
    }

    [Fact]
    public void FailedFlushCannotAuthorizeReuse()
    {
        var f=new Fixture(512,true); f.Engine.BeginPersistenceBarrier();
        for(int turn=0;turn<20000;turn++)
        {
            var step=f.Engine.Step(); Assert.NotEqual(StepResult.Failed,step); Assert.NotEqual(StepResult.Complete,step);
            if(step!=StepResult.NeedIo) continue;
            var io=f.Engine.Request;
            if(io.Kind==IoKind.Flush)
            {
                Assert.Equal(CpxError.Io,f.Engine.CompleteIo(io.Token,0,false)); Assert.Equal(StepResult.Failed,f.Engine.Step());
                Assert.Equal(CpxError.NotMounted,f.Engine.BeginPersistenceBarrier()); return;
            }
            if(io.Kind==IoKind.Read) f.Disk.Read(io.Sector,f.Ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes));
            else f.Disk.Write(io.Sector,f.Ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes));
            f.Engine.CompleteIo(io.Token,io.Bytes,true);
        }
        Assert.Fail("No persistence barrier encountered.");
    }
}
