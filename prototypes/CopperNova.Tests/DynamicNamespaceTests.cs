using CopperCPX;
using CopperNova.Reference;
using Xunit;

namespace CopperNova.Tests;

public sealed class DynamicNamespaceTests
{
    [Fact]
    public void FirstNewIndexIsUnreachableUntilItsRootIsDurable()
    {
        var disk=new byte[8*1024*1024];
        using(var stream=new MemoryStream(disk))
            NovaImage.FormatPackedWide(stream,0,disk.Length);
        var h=new FilesystemTests.Harness(disk); h.Mount();
        var snapshots=new List<byte[]>();
        int indexWrites=0;
        for(int i=0;i<60;i++)
        {
            string name=$"F{i:D4}";
            byte[] before=(byte[])disk.Clone();
            h.Close(h.Open(name));
            h.BeforeIo=io=>{
                if(io.Kind==IoKind.Write&&h.Memory.Read16(io.Buffer+6)==39&&h.Memory.Read32(io.Buffer+20)==1) indexWrites++;
                if(indexWrites!=0&&io.Kind==IoKind.Flush) snapshots.Add((byte[])disk.Clone());
            };
            h.Flush(); h.BeforeIo=null;
            if(indexWrites==0) continue;
            Assert.Equal(2,snapshots.Count);
            var old=NovaImage.CheckPacked(before);
            Assert.Equal(old.Entries.ToArray(),NovaImage.CheckPacked(snapshots[0]).Entries.ToArray());
            Assert.Equal(i+1,NovaImage.CheckPacked(snapshots[1]).Entries.Count);
            foreach(int failAt in new[]{1,2})
            {
                var retry=new FilesystemTests.Harness((byte[])before.Clone()); retry.Mount();
                retry.Close(retry.Open(name));
                retry.Memory.Write32(FilesystemTests.Harness.PacketAt+8,27);
                Assert.Equal(CpxError.None,retry.Engine.BeginPacket(FilesystemTests.Harness.PacketAt));
                int barriers=0;
                var durable=(byte[])before.Clone();
                while(retry.Engine.Busy)
                {
                    if(retry.Engine.Step()!=StepResult.NeedIo) continue;
                    var io=retry.Engine.Request;
                    if(io.Kind==IoKind.Flush)
                    {
                        if(++barriers==failAt) { retry.Engine.CompleteIo(io.Token,0,false); continue; }
                        Array.Copy(retry.Disk,durable,durable.Length);
                    }
                    retry.Service(io);
                }
                Assert.Equal(213u,retry.Engine.DosError);
                var recovered=new FilesystemTests.Harness(durable); recovered.Mount();
                Assert.Equal(old.Entries.ToArray(),NovaImage.CheckPacked(durable).Entries.ToArray());
                recovered.Close(recovered.Open(name)); recovered.Flush();
                Assert.Equal(i+1,NovaImage.CheckPacked(durable).Entries.Count);
            }
            return;
        }
        Assert.Fail("The workload did not allocate a second object-index leaf.");
    }

    [Fact]
    public void RenameAndEnumerationUseLogicalParentsAcrossSpillPages()
    {
        var disk=new byte[8*1024*1024];
        using(var stream=new MemoryStream(disk))
            NovaImage.FormatPackedWide(stream,0,disk.Length);
        var h=new FilesystemTests.Harness(disk); h.Mount();
        uint Name(string name){h.Bstr(FilesystemTests.Harness.NameAt,name); return FilesystemTests.Harness.NameAt>>2;}
        foreach(string name in new[]{"a","b"})
        {
            uint dir=h.Packet(22,0,Name(name)); Assert.NotEqual(0u,dir); h.Packet(15,dir);
            for(int i=0;i<64;i++) h.Close(h.Open($"{name}/F{i:D2}"));
        }
        Assert.Equal(0u,h.Packet(16,0,Name("a"))); Assert.Equal(216u,h.Engine.DosError);
        uint Move(string from,string to)
        {
            h.Bstr(FilesystemTests.Harness.SecondName,to);
            return h.Packet(17,0,Name(from),0,FilesystemTests.Harness.SecondName>>2);
        }
        Assert.Equal(0u,Move("a/F63","b/F00"));
        Assert.Equal(uint.MaxValue,Move("a/F63","b/moved"));
        Assert.Equal(uint.MaxValue,Move("b/moved","b/renamed"));
        h.Flush(); h.InhibitAndRemount();
        uint b=h.Packet(8,0,Name("b"),0xfffffffe),fib=FilesystemTests.Harness.Data>>2;
        Assert.NotEqual(0u,b);
        Assert.Equal(uint.MaxValue,h.Packet(23,b,fib));
        var seen=new HashSet<string>();
        while(h.Packet(24,b,fib)!=0)
        {
            int len=h.Memory.Read8(FilesystemTests.Harness.Data+8);
            Assert.True(seen.Add(System.Text.Encoding.ASCII.GetString(h.Memory.Bytes,(int)FilesystemTests.Harness.Data+9,len)));
        }
        Assert.Equal(232u,h.Engine.DosError); Assert.Equal(65,seen.Count); Assert.Contains("renamed",seen);
        var entries=NovaImage.CheckPacked(disk).Entries;
        Assert.Contains(entries,e=>e.Path=="/b/renamed"); Assert.DoesNotContain(entries,e=>e.Path=="/a/F63");
    }

    [Fact]
    public void StreamedDeletionKeepsAnOverlappingIndexLocatorOnItsUnpublishedSide()
    {
        var disk=new byte[144*1024*1024];
        using(var stream=new MemoryStream(disk))
            NovaImage.FormatPackedWide(stream,0,disk.Length,241664);
        var h=new FilesystemTests.Harness(disk); h.Mount();
        for(int i=0;i<4000;i++) h.Close(h.Open($"F{i:D6}"));
        uint file=h.Open("large");
        h.Memory.Write32(4096+8192+180,4096); // Start payload at allocation window 2.
        byte[] chunk=new byte[65536];
        for(int i=0;i<1600;i++) h.Write(file,chunk);
        h.Close(file); h.Flush();
        var before=NovaImage.CheckPacked(disk);
        var entry=Assert.Single(before.Entries,e=>e.Path=="/large");
        Assert.InRange((entry.Slot-1)/1920,2u,100u); // The payload intersects its locator bitmap.
        h.BeforeIo=io=>{
            if(io.Kind==IoKind.Flush)
            {
                var cut=NovaImage.CheckPacked(disk);
                Assert.Equal(cut.Generation==before.Generation?4001:4000,cut.Entries.Count);
            }
        };
        h.Bstr(FilesystemTests.Harness.NameAt,"large");
        Assert.Equal(uint.MaxValue,h.Packet(16,0,FilesystemTests.Harness.NameAt>>2));
        h.Flush(); h.BeforeIo=null; h.InhibitAndRemount();
        Assert.Equal(4000,NovaImage.CheckPacked(disk).Entries.Count);
    }

    [Fact]
    public void PartitionCrossesTheV5HighWaterMark()
    {
        const int files=122880, perDirectory=256;
        var disk=new byte[192*1024*1024];
        using(var stream=new MemoryStream(disk))
            NovaImage.FormatPackedWide(stream,0,disk.Length);
        var h=new FilesystemTests.Harness(disk); h.Mount();
        for(int i=0;i<files;i++)
        {
            string parent=$"D{i/perDirectory:X6}";
            if(i%perDirectory==0)
            {
                h.Bstr(FilesystemTests.Harness.NameAt,parent);
                uint dir=h.Packet(22,0,FilesystemTests.Harness.NameAt>>2);
                Assert.NotEqual(0u,dir); h.Packet(15,dir);
            }
            h.Close(h.Open($"{parent}/F{i:X6}"));
        }
        h.Flush(); h.InhibitAndRemount();
        var check=NovaImage.CheckPacked(disk);
        Assert.Equal(files,check.Entries.Count(e=>!e.Directory));
        Assert.True(check.Entries.Max(e=>e.Slot)>122760);
        foreach(int i in new[]{0,65535,122759,files-1})
            h.Close(h.Open($"D{i/perDirectory:X6}/F{i:X6}",1005));
    }

    [Theory]
    [InlineData(64)]
    [InlineData(3072)]
    public void FlatDirectorySurvivesBothBarriersAndDeletion(int count)
    {
        var disk=new byte[16*1024*1024];
        using(var stream=new MemoryStream(disk))
            NovaImage.FormatPackedWide(stream,0,disk.Length);
        var h=new FilesystemTests.Harness(disk);
        h.Mount();
        h.Bstr(FilesystemTests.Harness.NameAt,"flat");
        uint directory=h.Packet(22,0,FilesystemTests.Harness.NameAt>>2);
        Assert.NotEqual(0u,directory);
        h.Packet(15,directory);
        for(int i=0;i<count;i++) h.Close(h.Open($"flat/F{i:D6}"));
        h.Flush();
        Assert.Equal(count,NovaImage.CheckPacked(disk).Entries.Count(e=>!e.Directory));
        h=new FilesystemTests.Harness(disk); h.Mount();
        for(int i=0;i<count;i++) h.Close(h.Open($"flat/F{i:D6}",1005));
        h.Close(h.Open("flat/trial"));
        int barriers=0;
        h.BeforeIo=io=>{
            if(io.Kind==IoKind.Flush)
                Assert.Equal(count+(barriers++==0?0:1),NovaImage.CheckPacked(disk).Entries.Count(e=>!e.Directory));
        };
        h.Flush(); Assert.Equal(2,barriers); h.BeforeIo=null;
        for(int i=count;i>=0;i--)
        {
            h.Bstr(FilesystemTests.Harness.NameAt,i==count?"flat/trial":$"flat/F{i:D6}");
            Assert.Equal(uint.MaxValue,h.Packet(16,0,FilesystemTests.Harness.NameAt>>2));
        }
        h.Bstr(FilesystemTests.Harness.NameAt,"flat");
        Assert.Equal(uint.MaxValue,h.Packet(16,0,FilesystemTests.Harness.NameAt>>2));
        h.Flush();
        Assert.Empty(NovaImage.CheckPacked(disk).Entries);
    }
}
