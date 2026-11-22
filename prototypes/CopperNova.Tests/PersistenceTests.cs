using CopperCPX;
using CopperNova.Reference;
using Xunit.Abstractions;
using static CopperNova.Tests.FilesystemTests;

namespace CopperNova.Tests;

public class PersistenceTests(ITestOutputHelper output)
{
    private sealed record Event(bool Barrier,uint Sector,byte[] Bytes);
    private static Harness Prepare()
    {
        var h=new Harness(NovaImage.Format(2*1024*1024)); h.Mount();
        uint file=h.Open("keep"); h.Write(file,Enumerable.Repeat((byte)17,1024).ToArray()); h.Close(file); h.Flush();
        for(int i=0;i<2;i++) { file=h.Open("keep",1004); h.Write(file,Enumerable.Repeat((byte)17,1024).ToArray()); h.Close(file); h.Flush(); }
        return h;
    }
    private static void Add(Harness h)
    { uint file=h.Open("added"); h.Write(file,Enumerable.Repeat((byte)51,1536).ToArray()); h.Close(file); }
    private static List<Event> Capture(Harness h)
    {
        var events=new List<Event>();
        h.BeforeIo=io=>
        {
            if(io.Kind==IoKind.Flush) events.Add(new(true,0,[]));
            if(io.Kind==IoKind.Write)
                for(uint i=0;i<io.Bytes;i+=512) events.Add(new(false,io.Sector.Low+(i>>9),h.Memory.Bytes.AsSpan((int)(io.Buffer+i),512).ToArray()));
        };
        return events;
    }
    private static void Apply(byte[] disk,Event write,bool torn=false)
    {
        int offset=checked((int)write.Sector*512);
        if(torn) write.Bytes.AsSpan(0,256).CopyTo(disk.AsSpan(offset,256));
        else write.Bytes.CopyTo(disk,offset);
    }
    private static IEnumerable<(byte[] Disk,int Barriers)> Crashes(byte[] initial,List<Event> events)
    {
        byte[] durable=(byte[])initial.Clone(); var pending=new List<Event>(); int barriers=0;
        foreach(var e in events)
        {
            if(e.Barrier)
            {
                foreach(var write in pending) Apply(durable,write);
                pending.Clear(); barriers++; yield return ((byte[])durable.Clone(),barriers); continue;
            }
            pending.Add(e);
            for(int pattern=0;pattern<5;pattern++)
            {
                byte[] disk=(byte[])durable.Clone();
                for(int i=pending.Count-1;i>=0;i--)
                {
                    bool persist=pattern==1||pattern==2||pattern==3&&i%2==0||pattern==4&&i<pending.Count/2;
                    if(persist) Apply(disk,pending[i],pattern==2&&i==pending.Count-1);
                }
                yield return (disk,barriers);
            }
        }
    }
    [Fact]
    public void CoalescedPayloadIsDurableAtFirstBarrierAndRootPublishesAtSecond()
    {
        var h = new Harness(NovaImage.FormatPacked(4*1024*1024));
        h.Mount();
        uint handle = h.Open("payload");
        byte[] bytes = Enumerable.Range(0, 8192).Select(Harness.Initial).ToArray();
        h.Write(handle, bytes);
        h.Flush();
        byte[] initial = (byte[])h.Disk.Clone();
        var events = Capture(h);
        foreach (int at in new[] { 1023, 4093 })
        {
            Assert.NotEqual(uint.MaxValue, h.Packet(1008, handle, (uint)at, uint.MaxValue));
            byte[] edit = Enumerable.Repeat((byte)81, 769).ToArray();
            h.Write(handle, edit);
            edit.CopyTo(bytes, at);
        }
        Assert.Empty(events);
        h.Close(handle);
        h.Flush();
        Assert.Equal(2, events.Count(e => e.Barrier));
        string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        foreach (var cut in Crashes(initial, events))
        {
            var recovered = new Harness(cut.Disk);
            recovered.Mount();
            var view = NovaImage.CheckPacked(cut.Disk);
            Assert.Contains(view.Generation, new uint[] { 1, 2 });
            if (cut.Barriers > 0) Assert.Equal(digest, Assert.Single(view.Entries).Sha256);
            if (cut.Barriers == 2) Assert.Equal(2u, view.Generation);
        }
    }

    [Fact] public void RetirementPublishesBeforeReuseAcrossPowerCuts()
    {
        var h=new Harness(NovaImage.FormatPacked(4*1024*1024)); h.Mount();
        uint old=h.Open("old"); h.Write(old,Enumerable.Repeat((byte)42,1024).ToArray()); h.Close(old); h.Flush();
        var initial=(byte[])h.Disk.Clone(); var events=Capture(h);
        h.Bstr(Harness.NameAt,"old"); Assert.Equal(uint.MaxValue,h.Packet(16,0,Harness.NameAt>>2));
        uint next=h.Open("new"); h.Write(next,Enumerable.Repeat((byte)69,1024).ToArray()); h.Close(next); h.Flush();
        Assert.Equal(4,events.Count(e=>e.Barrier));
        var before=NovaImage.CheckPacked(initial).Entries.Single(); var after=NovaImage.CheckPacked(h.Disk).Entries.Single();
        Assert.Equal(before.Slot,after.Slot);
        foreach(var cut in Crashes(initial,events)) {
            var fresh=new Harness(cut.Disk); fresh.Mount();
            var view=NovaImage.CheckPacked(cut.Disk);
            Assert.InRange(view.Generation,1u,3u);
            if(view.Generation==2) Assert.Empty(view.Entries);
            else Assert.Equal(view.Generation==1?before:after,Assert.Single(view.Entries));
            if(cut.Barriers==4) Assert.Equal(3u,view.Generation);
        }
    }
    [Fact] public void PhysicalSectorCutsAcrossBankReuseAndChangedRetry()
    {
        var h=Prepare(); Add(h); byte[] initial=(byte[])h.Disk.Clone(); var events=Capture(h); h.Flush();
        Assert.Equal(4,events.Count(e=>e.Barrier));
        int cases=0;
        foreach(var crash in Crashes(initial,events))
        {
            var recovered=new Harness(crash.Disk); recovered.Mount(); var check=NovaImage.Check(recovered.Disk);
            Assert.Contains(check.Generation,new uint[]{3,4});
            if(crash.Barriers>=2) Assert.Equal(4u,check.Generation);
            Assert.Equal(check.Generation==3?1:2,check.Entries.Count);
            Assert.Contains(check.Entries,e=>e.Path=="/keep"&&e.Size==1024);
            uint retry=recovered.Open("retry"); recovered.Write(retry,Enumerable.Repeat((byte)85,512).ToArray()); recovered.Close(retry); recovered.Flush();
            var after=NovaImage.Check(recovered.Disk);
            Assert.Equal(check.Entries.Count+1,after.Entries.Count);
            Assert.Equal(check.Generation+1,after.Generation);
            Assert.Equal(check.Entries.Any(e=>e.Path=="/added"),after.Entries.Any(e=>e.Path=="/added"));
            cases++;
        }
        Assert.True(cases>50); output.WriteLine($"Physical transaction/changed-retry crash states: {cases}");
    }
    [Fact] public void PackedRootPublicationSurvivesTornReorderedWritesAndReuse()
    {
        var h=new Harness(NovaImage.FormatPacked(4*1024*1024)); h.Mount();
        uint keep=h.Open("keep"); h.Write(keep,Enumerable.Repeat((byte)17,1024).ToArray()); h.Close(keep); h.Flush();
        Add(h); h.Flush(); byte[] initial=(byte[])h.Disk.Clone(); var events=Capture(h);
        keep=h.Open("keep",1004); Assert.Equal(0u,h.Packet(1022,keep,0,uint.MaxValue)); h.Close(keep);
        h.Memory.Write32(4096+8192+180,1408); uint other=h.Open("other"); h.Write(other,Enumerable.Repeat((byte)85,512).ToArray()); h.Close(other);
        h.Bstr(Harness.NameAt,"other"); h.Bstr(Harness.SecondName,"published together"); Assert.Equal(uint.MaxValue,h.Packet(28,0,0,Harness.NameAt>>2,Harness.SecondName>>2)); h.Flush();
        Assert.Equal(4,events.Count(e=>e.Barrier)); int cases=0;
        foreach(var state in Crashes(initial,events)) {
            var recovered=new Harness(state.Disk); var reads=Capture(recovered); recovered.Mount(); Assert.Empty(reads);
            var check=NovaImage.CheckPacked(recovered.Disk); Assert.Contains(check.Generation,new uint[]{2,3,4});
            if(state.Barriers==4) Assert.Equal(4u,check.Generation);
            var old=check.Entries.Single(e=>e.Path=="/keep"); Assert.Equal(check.Generation==2?1024:0,old.Size);
            if(check.Generation==2) Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Enumerable.Repeat((byte)17,1024).ToArray())),old.Sha256);
            Assert.Equal(check.Generation==4,check.Entries.Any(e=>e.Path=="/other"&&e.Comment=="published together"));
            uint retry=recovered.Open("retry"); recovered.Write(retry,new byte[1024]); recovered.Close(retry); recovered.Flush();
            Assert.Equal(check.Generation+1,NovaImage.CheckPacked(recovered.Disk).Generation); cases++;
        }
        Assert.True(cases>=20); output.WriteLine($"Packed physical-sector crash states with retry: {cases}");
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void LongNameReplacementRecoversEitherPublishedTree(bool collapse)
    {
        var h=new Harness(NovaImage.FormatPacked(4*1024*1024)); h.Mount();
        foreach(string name in new[]{"a","b"}) { h.Bstr(Harness.NameAt,name); uint dir=h.Packet(22,0,Harness.NameAt>>2); Assert.NotEqual(0u,dir); Assert.Equal(uint.MaxValue,h.Packet(15,dir)); }
        string nameA="a/"+new string('x',150),shortB="b/q",longB="b/"+new string('y',150);
        uint fd=h.Open(nameA); h.Close(fd); fd=h.Open(collapse?longB:shortB); h.Close(fd); h.Flush();
        byte[] before=(byte[])h.Disk.Clone(); var writes=Capture(h);
        h.Bstr(Harness.NameAt,collapse?longB:shortB); h.Bstr(Harness.SecondName,collapse?shortB:longB); Assert.Equal(uint.MaxValue,h.Packet(17,0,Harness.NameAt>>2,0,Harness.SecondName>>2)); h.Flush();
        Assert.Equal(2,writes.Count(e=>e.Barrier));
        int cases=0; foreach(var state in Crashes(before,writes)) { var reboot=new Harness(state.Disk); reboot.Mount(); var info=NovaImage.CheckPacked(reboot.Disk);
            bool after=info.Generation==2; Assert.Contains(info.Generation,new uint[]{1,2}); if(state.Barriers==2) Assert.True(after);
            string winner=(collapse!=after)?longB:shortB; fd=reboot.Open(winner,1005); reboot.Close(fd); Assert.Contains(info.Entries,e=>e.Path=="/"+winner);
            fd=reboot.Open("retry"); reboot.Close(fd); reboot.Flush(); Assert.Equal(info.Generation+1,NovaImage.CheckPacked(reboot.Disk).Generation); cases++;
        }
        output.WriteLine($"Packed split/collapse physical-sector states: {cases}");
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void PackedFlushRejectsEveryFailedOrShortRequest(bool shorten)
    {
        Harness NewImage() { var h=new Harness(NovaImage.FormatPacked(4*1024*1024)); h.Mount(); Add(h);
            h.Bstr(Harness.NameAt,"added"); h.Bstr(Harness.SecondName,"unpublished"); Assert.Equal(uint.MaxValue,h.Packet(28,0,0,Harness.NameAt>>2,Harness.SecondName>>2)); return h; }
        var baseline=NewImage(); int requests=0; baseline.BeforeIo=_=>requests++; baseline.Flush();
        for(int target=0;target<requests;target++) { var h=NewImage(); h.Memory.Write32(Harness.PacketAt+8,27);
            Assert.Equal(CpxError.None,h.Engine.BeginPacket(Harness.PacketAt)); int index=0; bool injected=false;
            while(h.Engine.Busy) { if(h.Engine.Step()!=StepResult.NeedIo) continue; var io=h.Engine.Request;
                if(index++!=target) { h.Service(io); continue; } injected=true;
                Assert.Equal(CpxError.Io,h.Engine.CompleteIo(io.Token,0,shorten&&io.Bytes!=0)); }
            Assert.True(injected); Assert.Equal(213u,h.Engine.DosError); Assert.False(h.Engine.IsMounted);
            var recovered=new Harness(h.Disk); recovered.Mount(); var check=NovaImage.CheckPacked(h.Disk);
            Assert.True(check.Generation==0?check.Entries.Count==0:check.Generation==1&&check.Entries.Single().Comment=="unpublished");
        }
        output.WriteLine($"Packed failed/short request positions: {requests}");
    }
    [Fact] public void InterruptedRecoveryIsIdempotent()
    {
        var h=Prepare(); Add(h); byte[] initial=(byte[])h.Disk.Clone(); var events=Capture(h); h.Flush();
        byte[] interrupted=Crashes(initial,events).First(c=>c.Barriers==2).Disk;
        var first=new Harness((byte[])interrupted.Clone()); var recoveryEvents=Capture(first); first.Mount();
        Assert.Equal(2,recoveryEvents.Count(e=>e.Barrier));
        int cases=0;
        foreach(var crash in Crashes(interrupted,recoveryEvents))
        {
            var second=new Harness(crash.Disk); second.Mount(); var check=NovaImage.Check(second.Disk);
            Assert.Equal(4u,check.Generation); Assert.Equal(2,check.Entries.Count); cases++;
        }
        Assert.True(cases>20); output.WriteLine($"Physical recovery crash states: {cases}");
    }
    [Fact] public void PhysicalCutsDuringFreeAndAllocatePreserveThePublishedOwner()
    {
        var h=Prepare(); uint handle=h.Open("keep",1004);
        Assert.Equal(0u,h.Packet(1022,handle,0,uint.MaxValue)); h.Close(handle);
        h.Memory.Write32(4096+8192+180,1280); Add(h);
        Assert.All(h.Disk.AsSpan(1280*512,1024).ToArray(),b=>Assert.Equal((byte)17,b));
        byte[] initial=(byte[])h.Disk.Clone(); var events=Capture(h); h.Flush();
        int cases=0;
        foreach(var crash in Crashes(initial,events))
        {
            var recovered=new Harness(crash.Disk); recovered.Mount(); var check=NovaImage.Check(recovered.Disk);
            Assert.Contains(check.Generation,new uint[]{3,4});
            if(crash.Barriers>=2) Assert.Equal(4u,check.Generation);
            Assert.Equal(check.Generation==3?1024:0,check.Entries.Single(e=>e.Path=="/keep").Size);
            Assert.Equal(check.Generation==4,check.Entries.Any(e=>e.Path=="/added"));
            uint retry=recovered.Open("retry"); recovered.Write(retry,new byte[512]); recovered.Close(retry); recovered.Flush();
            NovaImage.Check(recovered.Disk); cases++;
        }
        Assert.True(cases>50); output.WriteLine($"Free/allocate physical crash states: {cases}");
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void FailedAndShortTransfersPoisonUntilRecovery(bool shortTransfer)
    {
        var baseline=Prepare(); Add(baseline);
        var events=Capture(baseline); baseline.Flush();
        // Each metadata write request and every barrier, not host backing-sector callbacks.
        int requests=events.Count(e=>e.Barrier)+events.Count(e=>!e.Barrier); int exercised=0;
        for(int target=0;target<requests;target++)
        {
            // Recreate the same unpublished logical operation for independent cache state.
            var h=Prepare(); Add(h);
            h.Memory.Write32(Harness.PacketAt+8,27); Assert.Equal(CpxError.None,h.Engine.BeginPacket(Harness.PacketAt));
            int index=0; bool injected=false;
            while(h.Engine.Busy)
            {
                if(h.Engine.Step()!=StepResult.NeedIo) continue;
                var io=h.Engine.Request;
                if(index++==target)
                {
                    uint actual=shortTransfer&&io.Bytes>=512?io.Bytes-512:0;
                    if(io.Kind==IoKind.Write&&actual!=0) h.Memory.Bytes.AsSpan((int)io.Buffer,(int)actual).CopyTo(h.Disk.AsSpan((int)io.Sector.Low*512,(int)actual));
                    Assert.Equal(CpxError.Io,h.Engine.CompleteIo(io.Token,actual,shortTransfer&&io.Bytes!=0)); injected=true;
                }
                else h.Service(io);
            }
            if(!injected) break;
            Assert.Equal(213u,h.Engine.DosError); Assert.False(h.Engine.IsMounted);
            var recovered=new Harness(h.Disk); recovered.Mount(); NovaImage.Check(recovered.Disk); exercised++;
        }
        Assert.True(exercised>=10); output.WriteLine($"{(shortTransfer?"Short/failed":"Failed")} request positions: {exercised}");
    }
}
