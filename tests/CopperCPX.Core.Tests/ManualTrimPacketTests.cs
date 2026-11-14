using CopperCPX.Qualification;
using CopperCPX.Reference.Tests;
using CopperFS.Cpx4.Reference;

namespace CopperCPX.Tests;

public class ManualTrimPacketTests
{
    private const uint Action=0x43535452,Record=90000;
    private static void Begin(DispatcherDriver d,uint mode)
    {
        d.Ram.Write32(Record,32); d.Ram.Write32(Record+4,1); d.Ram.Write32(Record+8,mode);
        InhibitLifecycleTests.Begin(d,Action,Record);
    }
    private static List<CpxIo> Drive(DispatcherDriver d,Action<CpxIo> service)
    {
        List<CpxIo> requests=[];
        for(int step=0;step<100000;step++)
        {
            d.Dispatcher=new(d.Ram,32);
            var result=d.Dispatcher.Step();
            if(result==StepResult.Complete) return requests;
            Assert.NotEqual(StepResult.Failed,result);
            if(result!=StepResult.NeedIo) continue;
            var io=d.Dispatcher.Request; requests.Add(io);
            Assert.Equal(StepResult.NeedIo,d.Dispatcher.Step()); Assert.Equal(io,d.Dispatcher.Request);
            Assert.Equal(CpxError.Busy,d.Dispatcher.BeginPacket(DispatcherDriver.PacketAddress));
            service(io);
        }
        throw new InvalidOperationException("Manual TRIM failed to finish");
    }
    private static void Service(DispatcherDriver d,CpxIo io,uint sectors=67)
    {
        if(io.Kind!=IoKind.QueryDiscard) { InhibitLifecycleTests.Service(d,io); return; }
        d.Ram.Write32(io.Buffer,sectors); Assert.Equal(CpxError.None,d.Dispatcher.CompleteIo(io.Token,4,true));
    }
    [Theory][InlineData(512)][InlineData(1024)][InlineData(2048)][InlineData(4096)]
    public void DryRunAndExecuteCoverEveryUnownedUnitAndPreserveWrittenAndUnwrittenData(int unit)
    {
        using var d=new DispatcherDriver(unit);
        var disk=new Disk{Bytes=d.Image}; var volume=new Cpx4Volume<Disk>(disk,new byte[36864]);
        Assert.Equal(MutationResult.Success,volume.Create("file"u8,out var id));
        byte[] content=Enumerable.Repeat((byte)0x5a,3000).ToArray();
        Assert.Equal(MutationResult.Success,volume.Write(id,0,content,out _));
        Assert.Equal(MutationResult.Success,volume.Preallocate(id,16384,4,out _));
        d.Execute(new(ActionKind.Remount));
        IndependentImage ownership=new(d.Image);
        byte[] expected=(byte[])d.Image.Clone(); var free=new HashSet<int>();
        for(int n=0;n<expected.Length/unit;n++) if(!ownership.IsOwned((ulong)n))
        { free.Add(n); expected.AsSpan(n*unit,unit).Fill(0xa5); }
        byte[] before=(byte[])d.Image.Clone(); Begin(d,2);
        Assert.Single(Drive(d,io=>Service(d,io)));
        uint maximum=67u/(uint)(unit/512);
        Assert.Equal(maximum,d.Ram.Read32(Record+20)); Assert.Equal(1u,d.Ram.Read32(Record+24)); Assert.Equal(0u,d.Ram.Read32(Record+28));
        Begin(d,1); var dry=Drive(d,io=>Service(d,io)); Assert.Equal(uint.MaxValue,d.Dispatcher.Result);
        Assert.DoesNotContain(dry,io=>io.Kind==IoKind.Discard); Assert.Equal(IoKind.Flush,dry[1].Kind);
        Assert.Equal((uint)free.Count,d.Ram.Read32(Record+12)); uint ranges=d.Ram.Read32(Record+16); Assert.True(ranges>1);
        Assert.Equal(before,d.Image);
        Begin(d,0); var actual=Drive(d,io=>{
            if(io.Kind==IoKind.Discard)
            {
                Assert.Equal(0u,io.Sector.High); Assert.Equal(0u,io.Bytes%(uint)unit);
                Assert.InRange(io.Bytes/(uint)unit,1u,maximum);
                int first=checked((int)(io.Sector.Low*512/(uint)unit));
                for(int n=0;n<io.Bytes/unit;n++) Assert.True(free.Remove(first+n));
            }
            Service(d,io);
        });
        Assert.Equal(0u,d.Dispatcher.DosError); Assert.Empty(free); Assert.Equal(expected,d.Image);
        Assert.Equal(ranges,(uint)actual.Count(io=>io.Kind==IoKind.Discard)); Assert.Equal(ranges,d.Ram.Read32(Record+16));
        Assert.Equal(content,new IndependentImage(d.Image).Read(id)[..content.Length]);
    }
    [Theory][InlineData(0u)][InlineData(1u)][InlineData(2u)]
    public void UnsupportedProbeIsCachedUntilFreshMount(uint mode)
    {
        using var d=new DispatcherDriver(512); byte[] before=(byte[])d.Image.Clone();
        Begin(d,mode); Assert.Single(Drive(d,io=>{
            Assert.Equal(IoKind.QueryDiscard,io.Kind); var e=new CpxEngine<Ram>(d.Ram,32);
            Assert.Equal(CpxError.StaleCompletion,e.CompleteUnsupportedDiscard(io.Token+1));
            Assert.Equal(CpxError.None,e.CompleteUnsupportedDiscard(io.Token));
        }));
        Assert.Equal(0u,d.Dispatcher.Result); Assert.Equal(209u,d.Dispatcher.DosError);
        foreach(uint retry in new uint[]{0,1,2})
        {
            Begin(d,retry); Assert.Empty(Drive(d,_=>Assert.Fail("Unsupported medium was probed again")));
            Assert.Equal(209u,d.Dispatcher.DosError);
        }
        Assert.Equal(new byte[20],d.Ram.Bytes.AsSpan((int)Record+12,20).ToArray()); Assert.Equal(before,d.Image);
        d.Execute(new(ActionKind.Remount)); Begin(d,2);
        Assert.Single(Drive(d,io=>Service(d,io))); Assert.Equal(0u,d.Dispatcher.DosError);
    }
    [Theory][InlineData("barrier")][InlineData("corrupt")][InlineData("short")][InlineData("unsupported")]
    public void FailuresExposeOnlyCompletedRanges(string fault)
    {
        using var d=new DispatcherDriver(512); Begin(d,0); int commands=0;
        var requests=Drive(d,io=>{
            if(io.Kind==IoKind.Flush&&fault=="barrier")
            { Assert.Equal(CpxError.Io,d.Dispatcher.CompleteIo(io.Token,0,false)); return; }
            if(io.Kind==IoKind.Read&&fault=="corrupt"&&io.Sector.Low==(131072+16*1024)/512)
            {
                // Last bitmap in group zero. Even the earlier free windows must be untouched.
                d.Image.AsSpan((int)io.Sector.Low*512,1)[0]^=1;
            }
            if(io.Kind==IoKind.Discard&&++commands==2)
            {
                if(fault=="unsupported") Assert.Equal(CpxError.None,new CpxEngine<Ram>(d.Ram,32).CompleteUnsupportedDiscard(io.Token));
                else Assert.Equal(CpxError.Io,d.Dispatcher.CompleteIo(io.Token,io.Bytes-512,true));
                return;
            }
            Service(d,io);
        });
        Assert.Equal(0u,d.Dispatcher.Result); Assert.Equal(fault=="unsupported"?209u:213u,d.Dispatcher.DosError);
        uint completed=fault is "short" or "unsupported"?1u:0u;
        Assert.Equal(completed==0?0:2,commands); Assert.Equal(completed,d.Ram.Read32(Record+16));
        Assert.Equal(completed*67,d.Ram.Read32(Record+12));
        if(completed==0) Assert.DoesNotContain(requests,io=>io.Kind==IoKind.Discard);
    }
    [Fact]
    public void BalancedSummariesCannotMakeTheReserveOrObjectRootDiscardable()
    {
        foreach(bool root in new[]{false,true})
        {
            using var d=new DispatcherDriver(512);
            Span<byte> desc=d.Image.AsSpan(131072,1024),map=d.Image.AsSpan(131072+2048,1024),
                state=d.Image.AsSpan(65536+3072,1024);
            int unit=checked((int)(root?Layout.U64(state,80):Layout.U64(d.Image.AsSpan(65536+8192),72)));
            int bit=unit%4096; map[64+bit/8]&=(byte)~(128>>(bit%8));
            Layout.Put32(desc,68,Layout.U32(desc,68)+1); Layout.Put32(desc,84,Layout.U32(desc,84)+1);
            Layout.Put64(state,96,Layout.U64(state,96)+1);
            Layout.Seal(desc); Layout.Seal(map); Layout.Seal(state);
            Begin(d,0); var trace=Drive(d,io=>Service(d,io));
            Assert.DoesNotContain(trace,io=>io.Kind==IoKind.Discard);
            Assert.Equal(213u,d.Dispatcher.DosError); Assert.Equal(0u,d.Ram.Read32(Record+12));
        }
    }
    [Fact]
    public void MediaRemovalPreservesAConfirmedRangeAndWaitsForTheNextCommand()
    {
        using var d=new DispatcherDriver(512); Begin(d,0); int sent=0;
        Drive(d,io=>{
            if(io.Kind==IoKind.Discard&&++sent==2)
            {
                d.Dispatcher.MediaRemoved();
                Assert.Equal(StepResult.NeedIo,d.Dispatcher.Step()); Assert.Equal(io,d.Dispatcher.Request);
                Assert.Equal(CpxError.Cancelled,d.Dispatcher.CompleteIo(io.Token,io.Bytes,true));
            }
            else Service(d,io);
        });
        Assert.Equal(2,sent); Assert.Equal(226u,d.Dispatcher.DosError);
        Assert.Equal(67u,d.Ram.Read32(Record+12)); Assert.Equal(1u,d.Ram.Read32(Record+16));
    }
    [Theory][InlineData(34)][InlineData(39)]
    public void FinalAllocationGroupExcludesHeadersAndBackup(int tailUnits)
    {
        var disk=new Disk{Bytes=new byte[(65536+tailUnits)*512]};
        Cpx4Volume<Disk>.Format(disk,512,new byte[36864],12345);
        var ram=new Ram(new byte[100000]); var engine=FileMutationTests.Mount(disk,ram);
        Assert.Equal(CpxError.None,engine.BeginManualTrim(0)); List<uint> tail=[];
        for(int step=0;step<5000;step++)
        {
            var result=engine.Step(); if(result==StepResult.Complete) break;
            Assert.NotEqual(StepResult.Failed,result); if(result!=StepResult.NeedIo) continue;
            var io=engine.Request;
            if(io.Kind==IoKind.QueryDiscard) ram.Write32(io.Buffer,4096);
            else if(io.Kind==IoKind.Read) disk.Read(io.Sector.Low,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes));
            else if(io.Kind==IoKind.Discard&&io.Sector.Low>=65536)
                for(uint n=0;n<io.Bytes/512;n++) tail.Add(io.Sector.Low+n);
            Assert.Equal(CpxError.None,engine.CompleteIo(io.Token,io.Bytes,true));
        }
        Assert.Equal(StepResult.Complete,engine.Step());
        Assert.Equal(tailUnits==34?Array.Empty<uint>():new uint[]{65570,65571},tail);
    }
    [Fact]
    public void InvalidRecordProtectionAndOfflineChecks()
    {
        using var d=new DispatcherDriver(512); d.Ram.Bytes.AsSpan((int)Record,36).Fill(0xa5);
        Assert.Equal((0u,115u),d.Packet(Action,0)); Assert.Equal((0u,115u),d.Packet(Action,Record+1));
        Assert.Equal((0u,115u),d.Packet(Action,uint.MaxValue-11));
        d.Ram.Write32(Record,31); d.Ram.Write32(Record+4,1); d.Ram.Write32(Record+8,2);
        Assert.Equal((0u,115u),d.Packet(Action,Record));
        d.Ram.Write32(Record,36); d.Ram.Write32(Record+4,2); Assert.Equal((0u,115u),d.Packet(Action,Record));
        d.Ram.Write32(Record+4,1); d.Ram.Write32(Record+8,3); Assert.Equal((0u,115u),d.Packet(Action,Record));
        Assert.All(d.Ram.Bytes.AsSpan((int)Record+12,24).ToArray(),b=>Assert.Equal(0xa5,b));
        Assert.Equal((uint.MaxValue,0u),d.Packet(1023,1));
        foreach(uint mode in new uint[]{0,1,2})
        {
            Begin(d,mode); Assert.Single(Drive(d,io=>Service(d,io))); Assert.Equal(mode==2?0u:214u,d.Dispatcher.DosError);
        }
        Assert.Equal(0xa5a5a5a5u,d.Ram.Read32(Record+32));
        Assert.Equal((uint.MaxValue,0u),d.Packet(31,1)); Assert.Equal((0u,226u),d.Packet(Action,Record));
    }
}
