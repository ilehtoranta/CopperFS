using CopperCPX;
using CopperFS.Cpx4.Reference;
using CopperCPX.Reference.Tests;
namespace CopperCPX.Tests;

public class TrimTests
{
    [Fact] public void TimerScanRechecksAReleasedUnitAfterItIsAllocatedToAnotherFile()
    {
        var disk=EngineTests.Fresh(512); var volume=new Cpx4Volume<Disk>(disk,new byte[36864]);
        volume.Create("old"u8,out ulong oldId); volume.Create("kept"u8,out ulong keptId);
        byte[] expected=Enumerable.Repeat((byte)0x57,512).ToArray();
        volume.Write(oldId,0,expected,out _);
        var before=new IndependentImage(disk.Bytes);
        ulong released=IndependentImage.L(before.Objects[oldId],104);
        var ram=new Ram(new byte[110000]); var engine=FileMutationTests.Mount(disk,ram);
        Assert.Equal(CpxError.None,engine.BeginResize(Wide.From((uint)oldId),default)); EngineTests.Drive(ref engine,ram,disk);
        Assert.False(new IndependentImage(disk.Bytes).IsOwned(released));
        expected.CopyTo(ram.Bytes,90000);
        Assert.Equal(CpxError.None,engine.BeginMappedWrite(Wide.From((uint)keptId),default,90000,512)); EngineTests.Drive(ref engine,ram,disk);
        var current=new IndependentImage(disk.Bytes); Assert.Equal(released,IndependentImage.L(current.Objects[keptId],104));
        // The saved cursor is deliberately stale. A tick must verify today's bitmap.
        ram.Write32(engine.Arena+424,0); ram.Write32(engine.Arena+428,(uint)released);
        var dispatcher=new CpxDosDispatcher<Ram>(ram,engine.Arena); dispatcher.Initialize(1,2);
        Assert.True(dispatcher.TryBeginMaintenance()); int discards=0;
        for(int step=0;dispatcher.Busy&&step<30;step++)
        {
            Assert.Equal(CpxError.Busy,dispatcher.BeginPacket(95000));
            if(dispatcher.Step()!=StepResult.NeedIo) continue;
            var io=dispatcher.Request;
            if(io.Kind==IoKind.Read) disk.Read(io.Sector.Low,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes));
            else
            {
                Assert.Equal(IoKind.Discard,io.Kind); Assert.False(current.IsOwned(io.Sector.Low));
                Assert.Equal(512u,io.Bytes); discards++; disk.Bytes.AsSpan((int)io.Sector.Low*512,512).Fill(0xe1);
                Assert.Equal(CpxError.Busy,engine.BeginResize(Wide.From((uint)keptId),default));
            }
            Assert.Equal(CpxError.None,dispatcher.CompleteIo(io.Token,io.Bytes,true));
        }
        Assert.False(dispatcher.Busy); Assert.Equal(1,discards); Assert.Equal(expected,new IndependentImage(disk.Bytes).Read(keptId));
    }
    [Fact] public void MaintenanceVisitsOneWindowAndIssuesOneDiscard()
    {
        var disk=EngineTests.Fresh(512); var ram=new Ram(new byte[100000]); var e=FileMutationTests.Mount(disk,ram);
        uint previous=0;
        for(int maintenance=0;maintenance<3;maintenance++)
        {
            Assert.Equal(CpxError.None,e.BeginOperation(CpxOperation.TrimFree)); int reads=0,discards=0;
            for(int n=0;n<30;n++)
            {
                var result=e.Step(); if(result==StepResult.Complete) break; Assert.NotEqual(StepResult.Failed,result);
                if(result!=StepResult.NeedIo) continue; var io=e.Request;
                if(io.Kind==IoKind.Read) { reads++; disk.Read(io.Sector.Low,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes)); }
                else { Assert.Equal(IoKind.Discard,io.Kind); Assert.True(io.Sector.Low>previous); previous=io.Sector.Low; discards++; }
                e.CompleteIo(io.Token,io.Bytes,true);
            }
            Assert.Equal(2,reads); Assert.Equal(1,discards); Assert.Equal(1u,e.DiscardedUnits);
        }
    }
    [Fact] public void RevalidatesRetiredBitmapAndReserveAndSerializesWithAllocation()
    {
        var disk=EngineTests.Fresh(512); var ram=new Ram(new byte[100000]); var e=FileMutationTests.Mount(disk,ram);
        foreach(uint at in new uint[]{4096,4100,6000})
        {
            Assert.Equal(CpxError.None,e.BeginTrim(Wide.From(at),2)); int discards=0;
            for(int n=0;n<100;n++)
            {
                var step=e.Step(); if(step==StepResult.Complete) break; Assert.NotEqual(StepResult.Failed,step);
                Assert.Equal(CpxError.Busy,e.BeginOperation(CpxOperation.BeginTransaction));
                if(step!=StepResult.NeedIo) continue; var io=e.Request; Assert.Equal(StepResult.NeedIo,e.Step());
                if(io.Kind==IoKind.Read) disk.Read(io.Sector.Low,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes));
                else { Assert.Equal(IoKind.Discard,io.Kind); discards++; Assert.Equal(6000u,io.Sector.Low); Assert.Equal(1024u,io.Bytes); }
                e.CompleteIo(io.Token,io.Bytes,true);
            }
            Assert.Equal(at==6000?1:0,discards); Assert.Equal(at==6000?2u:0u,e.DiscardedUnits);
        }
        Assert.Equal(CpxError.InvalidArgument,e.BeginTrim(Wide.From(8191),2));
        e.BeginTrim(Wide.From(128),2); Assert.Equal(StepResult.Failed,e.Step()); Assert.True(e.IsMounted);
    }
    [Fact] public void OnlyExplicitUnsupportedCompletionDisablesDiscard()
    {
        var disk=EngineTests.Fresh(512); var ram=new Ram(new byte[100000]); var e=FileMutationTests.Mount(disk,ram);
        e.BeginTrim(Wide.From(6000),1);
        while(true)
        {
            var step=e.Step(); Assert.NotEqual(StepResult.Failed,step); if(step!=StepResult.NeedIo) continue;
            var io=e.Request;
            if(io.Kind==IoKind.Discard)
            {
                Assert.Equal(CpxError.StaleCompletion,e.CompleteUnsupportedDiscard(io.Token+1)); Assert.True(e.DiscardEnabled);
                Assert.Equal(CpxError.None,e.CompleteUnsupportedDiscard(io.Token)); break;
            }
            Assert.Equal(CpxError.InvalidArgument,e.CompleteUnsupportedDiscard(io.Token)); Assert.True(e.HasOutstandingIo);
            disk.Read(io.Sector.Low,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes)); e.CompleteIo(io.Token,io.Bytes,true);
        }
        Assert.Equal(StepResult.Complete,e.Step()); Assert.False(e.DiscardEnabled); Assert.Equal(0u,e.DiscardedUnits);
        e.BeginTrim(Wide.From(6001),1); Assert.Equal(StepResult.Complete,e.Step()); Assert.True(e.IsMounted);
    }
}
