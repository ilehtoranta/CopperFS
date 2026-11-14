using CopperCPX.Reference.Tests;
using CopperFS.Cpx4.Reference;

namespace CopperCPX.Tests;

public class SmallPersistenceTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static void Change(IBlockDevice disk)
    {
        var ram=new Ram(new byte[100000]); var e=FileMutationTests.Mount(disk,ram);
        Assert.Equal(CpxError.None,e.BeginProtection(Wide.From(2),4)); EngineTests.Drive(ref e,ram,disk);
    }
    private static IEnumerable<(int Cut,int Pending,int Barriers)> SmallCuts(CrashDevice trace)
    {
        int pending=0,barriers=0;
        for(int n=0;n<trace.Events.Count;n++)
        {
            if(trace.Events[n].StartsWith("write ",StringComparison.Ordinal)) pending++;
            if(trace.Events[n]=="after flush") { pending=0; barriers++; }
            if(pending<=3) yield return(n+1,pending,barriers);
        }
    }
    [Fact]
    public void EverySmallPersistenceStateRespectsTheCommitBarrierAndOwnership()
    {
        var original=EngineTests.Fresh(512); var reference=new Cpx4Volume<Disk>(original,new byte[36864]);
        reference.Create("file"u8,out _); reference.Write(2,0,"preserved"u8,out _);
        var trace=new CrashDevice(original.Bytes); Change(trace);
        Assert.Equal(6,trace.Events.Count(x=>x=="after flush")); int states=0,cuts=0;
        foreach(var cut in SmallCuts(trace))
        {
            cuts++;
            foreach(int[] order in PersistenceSchedules.Enumerate(cut.Pending))
            {
                var crash=new CrashDevice(original.Bytes) { Cut=cut.Cut,PersistenceOrder=order };
                Assert.Throws<PowerLoss>(()=>Change(crash)); var recovered=new MemoryDevice(crash.Durable);
                FileMutationTests.Mount(recovered,new Ram(new byte[100000])); var decoded=new IndependentImage(recovered.Bytes);
                uint protection=IndependentImage.I(decoded.Objects[2],72);
                if(cut.Barriers<2) Assert.Equal(0u,protection); // No committed marker can exist.
                else if(cut.Barriers>=3) Assert.Equal(4u,protection); // Durable commit must survive.
                else Assert.Contains(protection,new uint[]{0,4});
                Assert.Equal("preserved"u8.ToArray(),decoded.Read(2)); new Cpx4Volume<MemoryDevice>(recovered,new byte[36864]).Check(); states++;
            }
        }
        Assert.True(states>100); output.WriteLine($"{cuts} filesystem cuts with <=3 pending sector versions; all {states} ordered subsets recovered.");
    }
    [Fact]
    public void EverySmallInterruptedReplayStateRetainsTheCommittedUpdate()
    {
        var original=EngineTests.Fresh(512); var reference=new Cpx4Volume<Disk>(original,new byte[36864]); reference.Create("file"u8,out _);
        var trace=new CrashDevice(original.Bytes); Change(trace);
        int committedCut=trace.Events.Select((name,index)=>(name,index)).Where(x=>x.name=="after flush").ElementAt(2).index+1;
        var committed=new CrashDevice(original.Bytes) { Cut=committedCut }; Assert.Throws<PowerLoss>(()=>Change(committed));
        var replay=new CrashDevice(committed.Durable); FileMutationTests.Mount(replay,new Ram(new byte[100000])); int states=0;
        foreach(var cut in SmallCuts(replay)) foreach(int[] order in PersistenceSchedules.Enumerate(cut.Pending))
        {
            var crash=new CrashDevice(committed.Durable) { Cut=cut.Cut,PersistenceOrder=order };
            Assert.Throws<PowerLoss>(()=>FileMutationTests.Mount(crash,new Ram(new byte[100000])));
            var recovered=new MemoryDevice(crash.Durable); FileMutationTests.Mount(recovered,new Ram(new byte[100000]));
            var decoded=new IndependentImage(recovered.Bytes); Assert.Equal(4u,IndependentImage.I(decoded.Objects[2],72));
            new Cpx4Volume<MemoryDevice>(recovered,new byte[36864]).Check(); states++;
        }
        Assert.True(states>0); output.WriteLine($"{states} interrupted replay persistence states retain the committed update.");
    }
}
