using CopperCPX;
using CopperFS.Cpx4.Reference;
namespace CopperCPX.Tests;

public class TrimTests
{
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
