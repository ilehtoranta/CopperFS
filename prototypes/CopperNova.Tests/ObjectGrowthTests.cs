using CopperCPX;
using CopperNova.Reference;
using static CopperNova.Tests.FilesystemTests;
namespace CopperNova.Tests;

public class ObjectGrowthTests
{
    [Fact]
    public void RootPublishesObjectPairsNamesAndPayloadAtTheSecondBarrier()
    {
        var disk=new byte[4*1024*1024];
        using(var stream=new MemoryStream(disk))
            NovaImage.FormatPackedWide(stream,0,disk.Length);
        var h=new Harness(disk); h.Mount();
        uint file=h.Open("first"); h.Write(file,new byte[1000]); h.Close(file); h.Flush();
        var original=Assert.Single(NovaImage.CheckPacked(disk).Entries);
        // The device persists its write cache only at a successful barrier.
        var durable=(byte[])disk.Clone();
        var cuts=new List<byte[]>();
        h.BeforeIo=io=>{
            if(io.Kind!=IoKind.Flush) return;
            cuts.Add((byte[])durable.Clone());
            disk.CopyTo(durable,0);
        };
        h.Bstr(Harness.NameAt,"first"); Assert.Equal(uint.MaxValue,h.Packet(16,0,Harness.NameAt>>2));
        file=h.Open("second"); h.Write(file,new byte[1700]); h.Close(file); h.Flush();
        Assert.Equal("/second",Assert.Single(NovaImage.CheckPacked(durable).Entries).Path);
        Assert.Equal(4,cuts.Count); // Retirement commits before the pair or its payload can be reused.
        for(int i=0;i<cuts.Count;i++)
        {
            var cut=cuts[i];
            var before=NovaImage.CheckPacked(cut);
            if(i<2) Assert.Equal(original,Assert.Single(before.Entries));
            else Assert.Empty(before.Entries);
            var cold=new Harness(cut); cold.Mount();
            cold.Close(cold.Open("other")); cold.Flush();
            var after=NovaImage.CheckPacked(cut);
            Assert.Equal(before.Entries.ToArray(),after.Entries.Where(e=>e.Path!="/other").ToArray());
            Assert.Equal(before.Generation+1,after.Generation);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NamespacePressureAndExplicitFlushPublishCompleteGroups(bool longPath)
    {
        var disk=new byte[4*1024*1024];
        using(var stream=new MemoryStream(disk)) NovaImage.FormatPackedWide(stream,0,disk.Length);
        var h=new Harness(disk);
        h.Mount();
        void Dir(string path)
        {
            h.Bstr(Harness.NameAt,path);
            uint bptr=h.Packet(22,0,Harness.NameAt>>2);
            Assert.NotEqual(0u,bptr);
            h.Packet(15,bptr);
        }
        string deep="d";
        Dir(deep);
        for(int i=0;i<15;i++) { deep+="/d"; Dir(deep); }
        for(int i=0;i<3;i++)
        {
            Dir($"{i}");
            for(int j=0;j<6;j++) Dir($"{i}/{j}");
        }
        h.Flush();
        int created=0,barriers=0;
        h.BeforeIo=io=>{ if(io.Kind==IoKind.Flush) barriers++; };
        while(barriers==0)
        {
            Assert.InRange(created,0,107);
            h.Close(h.Open($"{created/36}/{created/6%6}/{created%6}"));
            created++;
        }
        Assert.Equal(2,barriers);
        if(longPath)
        {
            h.Close(h.Open(deep+"/z"));
        }
        h.Flush();
        Assert.Equal(4,barriers);
        Assert.Equal(created+(longPath?1:0),NovaImage.CheckPacked(disk).Entries.Count(e=>!e.Directory));
    }

    [Fact]
    public void ShortObjectWriteCannotPublishTheSealedCacheImage()
    {
        var disk=new byte[4*1024*1024];
        using(var stream=new MemoryStream(disk)) NovaImage.FormatPackedWide(stream,0,disk.Length);
        var h=new Harness(disk);
        h.Mount();
        h.Close(h.Open("keep"));
        h.Flush();
        var before=NovaImage.CheckPacked(disk);
        h.Close(h.Open("trial"));

        h.Memory.Write32(Harness.PacketAt+8,27);
        Assert.Equal(CpxError.None,h.Engine.BeginPacket(Harness.PacketAt));
        bool failed=false;
        while(h.Engine.Busy)
        {
            if(h.Engine.Step()!=StepResult.NeedIo) continue;
            var io=h.Engine.Request;
            if(io.Kind==IoKind.Write && h.Memory.Read16(io.Buffer+6)==40)
            {
                h.Memory.Bytes.AsSpan((int)io.Buffer,256).CopyTo(disk.AsSpan((int)io.Sector.Low*512));
                Assert.Equal(CpxError.Io,h.Engine.CompleteIo(io.Token,256,true));
                failed=true;
            }
            else h.Service(io);
        }
        Assert.True(failed);
        Assert.Equal(213u,h.Engine.DosError);
        Assert.Equal(0u,h.OpenRaw("trial",1005));
        Assert.Equal(213u,h.Engine.DosError);
        var after=NovaImage.CheckPacked(disk);
        Assert.Equal(before.Generation,after.Generation);
        Assert.Equal(before.Entries.ToArray(),after.Entries.ToArray());
        var cold=new Harness(disk);
        cold.Mount();
        cold.Close(cold.Open("next"));
        cold.Flush();
        Assert.Equal(new[]{"/keep","/next"},NovaImage.CheckPacked(disk).Entries.Select(e=>e.Path).Order().ToArray());
    }

    [Fact]
    public void InlineRecordsExpandForCommentsAndDataAndReuseDeletedIds()
    {
        var disk=new byte[8*1024*1024];
        using(var s=new MemoryStream(disk)) NovaImage.FormatPackedWide(s,0,disk.Length);
        var h=new Harness(disk);
        h.Mount(); h.Close(h.Open("empty")); h.Close(h.Open("neighbor")); h.Flush();
        var empty=NovaImage.CheckPacked(disk).Entries.Single(e=>e.Path=="/empty");
        h.InhibitAndRemount();
        h.Bstr(Harness.NameAt,"empty"); h.Bstr(Harness.SecondName,"preserved");
        Assert.Equal(uint.MaxValue,h.Packet(28,0,0,Harness.NameAt>>2,Harness.SecondName>>2));
        h.Flush(); h.InhibitAndRemount();
        Assert.Equal("preserved",NovaImage.CheckPacked(disk).Entries.Single(e=>e.Path=="/empty").Comment);
        uint handle=h.Open("empty",1004); h.Write(handle,new byte[]{1,2,3}); h.Close(handle);
        h.Flush(); h.InhibitAndRemount();
        var written=NovaImage.CheckPacked(disk).Entries.Single(e=>e.Path=="/empty");
        Assert.Equal(3,written.Size);
        Assert.Equal("preserved",written.Comment);
        foreach(string name in new[]{"empty","neighbor"})
        {
            h.Bstr(Harness.NameAt,name); Assert.Equal(uint.MaxValue,h.Packet(16,0,Harness.NameAt>>2));
        }
        h.Flush(); h.InhibitAndRemount();
        h.Close(h.Open("one")); h.Close(h.Open("two")); h.Flush();
        var reused=NovaImage.CheckPacked(disk).Entries;
        Assert.Equal(empty.Slot,reused.Single(e=>e.Path=="/two").Slot);
        Assert.All(reused,e=>{Assert.Equal(0,e.Size); Assert.Null(e.Comment);});
    }

    [Fact]
    public void PublishedIndexIsReusedUntilRemount()
    {
        var disk=new byte[4*1024*1024];
        using(var stream=new MemoryStream(disk)) NovaImage.FormatPackedWide(stream,0,disk.Length);
        var h=new Harness(disk);
        h.Mount();
        h.Close(h.Open("first"));
        h.Flush();

        // Both copies of the first v6 object-index page occupy this fixed pair.
        int reads=0, barriers=0;
        h.BeforeIo=io=>{
            if(io.Kind==IoKind.Read && io.Sector.Low is 1394 or 1395) reads++;
            if(io.Kind==IoKind.Flush) barriers++;
        };
        h.Close(h.Open("second"));
        h.Flush();
        Assert.Equal(0,reads);
        Assert.Equal(2,barriers);

        h.InhibitAndRemount();
        h.Close(h.Open("first",1005));
        h.Close(h.Open("second",1005));
        Assert.Equal(1,reads);
        Assert.Equal(2,NovaImage.CheckPacked(disk).Entries.Count);
    }

    [Fact]
    public void FullPartitionRejectsGrowthAndCanReuseADeletedSlot()
    {
        // The catalogue occupies 4,096 sectors. Exactly one inode pair fits.
        var disk=new byte[4098*512];
        using(var stream=new MemoryStream(disk)) NovaImage.FormatPackedWide(stream,0,disk.Length);
        var h=new Harness(disk); h.Mount();
        h.Close(h.Open("old")); h.Flush();
        Assert.Equal(0u,NovaImage.CheckPacked(disk).FreeSectors);
        Assert.Equal(0u,h.OpenRaw("new",1006)); Assert.Equal(221u,h.Engine.DosError);
        h.Bstr(Harness.NameAt,"old"); Assert.Equal(uint.MaxValue,h.Packet(16,0,Harness.NameAt>>2));
        h.Close(h.Open("new")); h.Flush();
        var check=NovaImage.CheckPacked(disk);
        Assert.Equal(0u,check.FreeSectors); Assert.Equal("/new",Assert.Single(check.Entries).Path);
    }

    [Fact]
    public void FirstObjectInANewIndexSurvivesOnlyAfterPublication()
    {
        var disk=new byte[4*1024*1024];
        using(var stream=new MemoryStream(disk)) NovaImage.FormatPackedWide(stream,0,disk.Length);
        var h=new Harness(disk); h.Mount();
        void Directory(string path) {
            h.Bstr(Harness.NameAt,path);
            uint handle=h.Packet(22,0,Harness.NameAt>>2); Assert.NotEqual(0u,handle); h.Packet(15,handle);
        }
        for(int i=0;i<4;i++) {
            Directory($"{i}");
            for(int j=0;j<4;j++) {
                Directory($"{i}/{j}");
                for(int k=0;k<6;k++) h.Close(h.Open($"{i}/{j}/{k}"));
            }
        }
        foreach(string path in new[]{"A","B","0/C"}) h.Close(h.Open(path));
        h.Flush(); var old=NovaImage.CheckPacked(disk);
        Assert.Equal(119,old.Entries.Count);
        var durable=(byte[])disk.Clone(); var cuts=new List<byte[]>();
        h.BeforeIo=io=>{ if(io.Kind==IoKind.Flush) {cuts.Add((byte[])durable.Clone()); disk.CopyTo(durable,0);} };
        h.Close(h.Open("1/trial")); h.Flush();
        Assert.Equal(121u,Assert.Single(NovaImage.CheckPacked(durable).Entries,e=>e.Path=="/1/trial").Slot);
        Assert.Equal(2,cuts.Count);
        foreach(var cut in cuts)
        {
            Assert.Equal(old.Entries.ToArray(),NovaImage.CheckPacked(cut).Entries.ToArray());
            var retry=new Harness(cut); retry.Mount(); retry.Close(retry.Open("2/retry")); retry.Flush();
            var next=NovaImage.CheckPacked(cut);
            Assert.Equal(121u,Assert.Single(next.Entries,e=>e.Path=="/2/retry").Slot);
            Assert.DoesNotContain(next.Entries,e=>e.Path=="/1/trial");
        }
    }

    [Fact]
    public void WideIdsSurviveColdMountBeyondTheFormerLastIndex()
    {
        const int last=122759, directories=20460;
        var disk=new byte[128*1024*1024];
        using(var stream=new MemoryStream(disk)) NovaImage.FormatPackedWide(stream,0,disk.Length);
        var h=new Harness(disk); h.Mount();
        // Number a six-way directory tree breadth first. A path fits inline at every level.
        static string PathOf(int index) {
            var parts=new Stack<char>();
            while(index>0) { index--; parts.Push((char)('a'+index%6)); index/=6; }
            return string.Join("/",parts);
        }
        for(int n=1;n<=directories;n++)
        {
            h.Bstr(Harness.NameAt,PathOf(n));
            uint handle=h.Packet(22,0,Harness.NameAt>>2);
            Assert.NotEqual(0u,handle); h.Packet(15,handle);
        }
        for(int n=directories+1;n<=last;n++) h.Close(h.Open(PathOf(n)));
        h.Close(h.Open(PathOf(last+1)));
        h.Flush(); h=new Harness(disk); h.Mount();
        foreach(int n in new[]{65534,65535,65536,last}) h.Close(h.Open(PathOf(n),1005));
        h.Bstr(Harness.NameAt,PathOf(last)); Assert.Equal(uint.MaxValue,h.Packet(16,0,Harness.NameAt>>2));
        h.Close(h.Open(PathOf(last+2))); h.Flush();
        var after=NovaImage.CheckPacked(disk);
        Assert.Equal(last+1,after.Entries.Count);
        Assert.Equal(122760u,Assert.Single(after.Entries,e=>e.Path=="/"+PathOf(last+2)).Slot);
    }

    [Fact]
    public void TwoThousandFilesSurviveFlushDeletionAndReuse()
    {
        var disk=new byte[8*1024*1024];
        using(var stream=new MemoryStream(disk)) NovaImage.FormatPackedWide(stream,0,disk.Length);
        var h=new Harness(disk);
        h.Mount();
        void Dir(string name) {
            h.Bstr(Harness.NameAt,name);
            uint result=h.Packet(22,0,Harness.NameAt>>2);
            Assert.True(result!=0,$"Create directory {name}: {h.Engine.DosError}");
            h.Packet(15,result);
        }
        var parents=new List<string>{""};
        for(int level=0;level<4;level++)
        {
            var next=new List<string>();
            foreach(string p in parents)
                for(int i=0;i<5;i++) { string child=p+i; Dir(child); next.Add(child+"/"); }
            parents=next;
        }
        var files=parents.SelectMany(p=>Enumerable.Range(0,4).Select(i=>p+i)).ToArray();
        Assert.Equal(2500,files.Length);
        foreach(string name in files) h.Close(h.Open(name));
        h.Flush();
        var created=NovaImage.CheckPacked(disk);
        Assert.Equal(3280,created.Entries.Count);
        string high=files[^1];
        Assert.True(created.Entries.Single(e=>e.Path=="/"+high).Slot>1023);
        h=new Harness(disk); h.Mount();
        foreach(string name in files) h.Close(h.Open(name,1005));
        uint largeId=h.Open(high,1004);
        h.Write(largeId,new byte[1200]); h.Close(largeId);
        h.Bstr(Harness.NameAt,high); h.Bstr(Harness.SecondName,"high ID");
        Assert.Equal(uint.MaxValue,h.Packet(28,0,0,Harness.NameAt>>2,Harness.SecondName>>2));
        h.Flush(); h=new Harness(disk); h.Mount();
        var highEntry=NovaImage.CheckPacked(disk).Entries.Single(e=>e.Path=="/"+high);
        Assert.Equal(1200,highEntry.Size);
        Assert.Equal("high ID",highEntry.Comment);
        foreach(string name in files) {
            h.Bstr(Harness.NameAt,name);
            Assert.Equal(uint.MaxValue,h.Packet(16,0,Harness.NameAt>>2));
        }
        h.Flush(); h=new Harness(disk); h.Mount();
        var removed=NovaImage.CheckPacked(disk);
        Assert.Equal(780,removed.Entries.Count);
        Assert.Equal(created.FreeSectors,removed.FreeSectors);
        foreach(string name in files) Assert.Equal(0u,h.OpenRaw(name,1005));
        foreach(string name in files) h.Close(h.Open(name));
        h.Flush(); var reused=NovaImage.CheckPacked(disk);
        Assert.Equal(created.FreeSectors,reused.FreeSectors);
        Assert.All(reused.Entries,e=>Assert.Null(e.Comment));
        h=new Harness(disk); h.Mount();
        foreach(string name in files) h.Close(h.Open(name,1005));
    }
}
