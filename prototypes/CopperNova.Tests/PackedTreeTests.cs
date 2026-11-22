using CopperCPX;
using CopperNova.Reference;
using static CopperNova.Tests.FilesystemTests;
namespace CopperNova.Tests;
public class PackedTreeTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void PackedStreamChecksPayloadFromThePartitionOffset()
    {
        var h=new Harness(NovaImage.FormatPacked(4*1024*1024));
        h.Mount();
        uint file=h.Open("payload");
        var payload=new byte[20000];
        new Random(23).NextBytes(payload);
        h.Write(file,payload);
        h.Close(file);
        h.Flush();

        using var disk=new MemoryStream();
        const int partitionOffset=32768;
        disk.Position=partitionOffset;
        disk.Write(h.Disk);
        var expected=NovaImage.CheckPacked(h.Disk);
        var actual=NovaImage.CheckPacked(disk,partitionOffset,h.Disk.Length);
        Assert.Equal(expected.Generation,actual.Generation);
        Assert.Equal(expected.Entries.ToArray(),actual.Entries.ToArray());
        Assert.Throws<InvalidDataException>(()=>NovaImage.CheckPacked(disk,partitionOffset,h.Disk.Length+512L));
    }

    [Fact]
    public void WideExtentsAndRetiredBitmapsPublishWithTheRoot()
    {
        const int length=33*1024*1024;
        const uint groupBoundary=65536;
        var disk=new byte[length];
        using(var output=new MemoryStream(disk))
            NovaImage.FormatPackedWide(output,0,length,groupBoundary-4);
        var h=new Harness(disk);
        h.Mount();
        var data=new byte[65536];
        new Random(423).NextBytes(data);
        var writes=new List<uint>();
        h.BeforeIo=io=>
        {
            if(io.Kind==IoKind.Write) writes.Add(io.Sector.Low);
        };
        uint file=h.Open("keep");
        h.Write(file,data);
        uint gap=h.Open("gap");
        h.Write(gap,new byte[512]);
        h.Close(gap);
        h.Write(file,data);
        h.Close(file);
        Assert.Equal(new[]{groupBoundary-2,groupBoundary,groupBoundary+128,groupBoundary+129},writes);

        byte[]? unpublished=null;
        h.BeforeIo=io=> { if(io.Kind==IoKind.Flush&&unpublished==null) unpublished=(byte[])disk.Clone(); };
        h.Flush();
        Assert.Empty(NovaImage.CheckPacked(Assert.IsType<byte[]>(unpublished)).Entries);
        var cold=new Harness(disk);
        cold.Mount();
        file=cold.Open("keep",1005);
        for(int i=0;i<2;i++)
        {
            Assert.Equal((uint)data.Length,cold.Packet(82,file,Harness.Data,(uint)data.Length));
            Assert.Equal(data,cold.Memory.Bytes.AsSpan((int)Harness.Data,data.Length).ToArray());
        }
        cold.Close(file);

        cold.Bstr(Harness.NameAt,"keep");
        Assert.Equal(uint.MaxValue,cold.Packet(16,0,Harness.NameAt>>2));
        int retireBarriers=0;
        cold.BeforeIo=io=>
        {
            if(io.Kind==IoKind.Flush&&retireBarriers++==0)
                Assert.Equal(257u,NovaImage.CheckPacked(disk).DataSectors);
        };
        cold.Flush();
        Assert.Equal(2,retireBarriers);
        var after=NovaImage.CheckPacked(disk);
        Assert.Equal(2u,after.Generation);
        Assert.Equal("/gap",Assert.Single(after.Entries).Path);
        Assert.Equal(1u,after.DataSectors);
    }

    [Fact]
    public void WideScanAndRetirementCanExceedTheMetadataCache()
    {
        const int length=75*1024*1024, fileLength=66*1024*1024;
        const uint hint=6144;
        var disk=new byte[length];
        using(var target=new MemoryStream(disk))
            NovaImage.FormatPackedWide(target,0,length,hint);
        var h=new Harness(disk);
        h.Mount();
        uint file=h.Open("large");
        var data=new byte[65536];
        for(int offset=0;offset<fileLength;offset+=data.Length) h.Write(file,data);
        h.Close(file);
        h.Flush();

        // Model a wrapped cursor at the start of the now-full run.
        h.Memory.Write32(4096+8192+180,hint);
        file=h.Open("after");
        uint start=0;
        h.BeforeIo=io=> { if(io.Kind==IoKind.Write) start=io.Sector.Low; };
        h.Write(file,new byte[512]);
        Assert.Equal(hint+fileLength/512+4,start);
        h.Close(file);
        h.Flush();
        uint generation=NovaImage.CheckPacked(disk).Generation;

        int barriers=0;
        h.BeforeIo=io=>
        {
            if(io.Kind==IoKind.Flush&&barriers++==0)
            {
                var old=NovaImage.CheckPacked(disk);
                Assert.Equal(generation,old.Generation);
                Assert.Equal(fileLength,old.Entries.Single(e=>e.Path=="/large").Size);
            }
        };
        h.Bstr(Harness.NameAt,"large");
        Assert.Equal(uint.MaxValue,h.Packet(16,0,Harness.NameAt>>2));
        Assert.Equal(2,barriers);
        var after=NovaImage.CheckPacked(disk);
        Assert.Equal(generation+1,after.Generation);
        Assert.Equal("/after",Assert.Single(after.Entries).Path);
        Assert.Equal(1u,after.DataSectors);
    }

    [Fact]
    public void WideAllocationStopsAtAnOccupiedBitInsideAByte()
    {
        const uint first=6144+32+9,occupied=first+4;
        var disk=new byte[4*1024*1024];
        using(var target=new MemoryStream(disk))
            NovaImage.FormatPackedWide(target,0,disk.Length);
        var h=new Harness(disk);
        h.Mount();
        uint neighbor=h.Open("neighbor");
        uint file=h.Open("grown");
        h.Memory.Write32(4096+8192+180,occupied);
        var sentinel=Enumerable.Repeat((byte)0xac,512).ToArray();
        h.Write(neighbor,sentinel);
        h.Close(neighbor);
        h.Flush();

        h.Memory.Write32(4096+8192+180,first);
        var writes=new List<(uint Sector,uint Bytes)>();
        h.BeforeIo=io=> { if(io.Kind==IoKind.Write) writes.Add((io.Sector.Low,io.Bytes)); };
        h.Write(file,new byte[12*512]);
        Assert.Equal(new[]{(first,4u*512),(occupied+1,8u*512)},writes);
        h.Close(file);
        h.Flush();
        var cold=NovaImage.CheckPacked(disk);
        Assert.Equal(13u,cold.DataSectors);
        Assert.Equal(sentinel,disk.AsSpan((int)occupied*512,512).ToArray());
    }

    [Fact] public void CompareBitmapProbesWithAlignedInitialAllocationHint()
    {
        (long Bytes,long Words,string Hash) Trace(uint hint) {
            var counter=new BitmapReads(); var h=new Harness(NovaImage.FormatPacked(),counter); h.Mount(); uint handle=h.Open("stream");
            h.Memory.Write32(4096+8192+180,hint); byte[] payload=new byte[65536]; counter.Enabled=true;
            for(int i=0;i<32;i++) h.Write(handle,payload); counter.Enabled=false; h.Close(handle); h.Flush();
            return (counter.ByteReads,counter.WordReads,NovaImage.CheckPacked(h.Disk).Entries.Single().Sha256!);
        }
        var measured=Trace(4129); var aligned=Trace(4160); Assert.Equal(measured.Hash,aligned.Hash); Assert.True(measured.Bytes>aligned.Bytes);
        output.WriteLine($"Bitmap payload reads (byte/longword): unaligned hint {measured.Bytes}/{measured.Words}; aligned hint {aligned.Bytes}/{aligned.Words}");
    }
    [Theory] [InlineData(false)] [InlineData(true)] public void IdenticalWorkloadRoundTrips(bool streaming)
    {
        var h=new Harness(NovaImage.FormatPacked()); h.Mount(); h.Workload(streaming); Assert.Equal(streaming?2:4,h.NonemptyBarriers);
        h.InhibitAndRemount(); h.Verify(streaming); Assert.Equal(4,NovaImage.CheckPacked(h.Disk).AllocatedInodes);
    }
    [Fact]
    public void MultipleBitmapWindowsPublishAndReclaimAcrossChecksumRegions()
    {
        var h=new Harness(NovaImage.FormatPacked());
        h.Mount();
        uint file=h.Open("windows");
        byte[] payload=new byte[65536];
        new Random(418).NextBytes(payload);
        for(int i=0;i<17;i++) h.Write(file,payload);
        h.Close(file);
        h.Flush();
        h.InhibitAndRemount();
        Assert.Equal(17*65536,NovaImage.CheckPacked(h.Disk).Entries.Single().Size);

        file=h.Open("windows",1004);
        const uint retained=133*512+17;
        Assert.Equal(retained,h.Packet(1022,file,retained,uint.MaxValue));
        h.Close(file);
        h.Flush();
        h.InhibitAndRemount();
        var report=NovaImage.CheckPacked(h.Disk);
        Assert.Equal((long)retained,report.Entries.Single().Size);
        Assert.Equal(134u,(uint)report.DataSectors);
        file=h.Open("windows",1005);
        Assert.Equal(65536u,h.Packet(82,file,Harness.Data,65536));
        Assert.Equal(payload,h.Memory.Bytes.AsSpan((int)Harness.Data,65536).ToArray());
        h.Close(file);
    }

    [Fact] public void ColdCommentSurvivesRenameFlushAndRemoval()
    {
        var h=new Harness(NovaImage.FormatPacked()); h.Mount(); uint file=h.Open("before"); h.Close(file);
        string text=new('x',79); h.Bstr(Harness.NameAt,"before"); h.Bstr(Harness.SecondName,text);
        Assert.Equal(uint.MaxValue,h.Packet(28,0,0,Harness.NameAt>>2,Harness.SecondName>>2)); h.Bstr(Harness.SecondName,"after");
        Assert.Equal(uint.MaxValue,h.Packet(17,0,Harness.NameAt>>2,0,Harness.SecondName>>2)); h.Flush(); h.InhibitAndRemount();
        Assert.Equal(text,NovaImage.CheckPacked(h.Disk).Entries.Single(e=>e.Path=="/after").Comment);
        uint held=h.Packet(8,0,Harness.SecondName>>2,0xfffffffe); Assert.NotEqual(0u,held); Assert.Equal(uint.MaxValue,h.Packet(23,held,Harness.Data>>2));
        Assert.Equal(79,h.Memory.Read8(Harness.Data+144)); Assert.Equal(120,h.Memory.Read8(Harness.Data+223)); Assert.Equal(uint.MaxValue,h.Packet(15,held));
        h.Bstr(Harness.NameAt,""); Assert.Equal(uint.MaxValue,h.Packet(28,0,0,Harness.SecondName>>2,Harness.NameAt>>2)); h.Flush(); Assert.Null(NovaImage.CheckPacked(h.Disk).Entries.Single().Comment);
    }
    [Fact] public void SplitTreeAndExternalExtentsSurviveAnotherGeneration()
    {
        var h=new Harness(NovaImage.FormatPacked()); h.Mount(); uint first=h.Open("fragmented"),second=h.Open("interleave");
        for(int i=0;i<8;i++) { h.Write(first,Enumerable.Repeat((byte)(i+1),512).ToArray()); h.Write(second,new byte[512]); }
        h.Close(first); h.Close(second);
        string path=""; for(int i=0;i<24;i++) { path+="d/"; h.Bstr(Harness.NameAt,path.TrimEnd('/')); uint dir=h.Packet(22,0,Harness.NameAt>>2); Assert.NotEqual(0u,dir); Assert.Equal(uint.MaxValue,h.Packet(15,dir)); }
        h.Flush(); h.InhibitAndRemount(); var check=NovaImage.CheckPacked(h.Disk); Assert.Equal(27,check.AllocatedInodes);
        first=h.Open("fragmented",1004); Assert.Equal(4096u,h.Packet(82,first,Harness.Data,4096)); for(int i=0;i<4096;i++) Assert.Equal((byte)(i/512+1),h.Memory.Read8(Harness.Data+(uint)i));
        Assert.Equal(512u,h.Packet(1022,first,512,uint.MaxValue)); h.Close(first); h.Flush(); h.InhibitAndRemount(); Assert.Equal(512,NovaImage.CheckPacked(h.Disk).Entries.Single(e=>e.Path=="/fragmented").Size);
    }
    [Theory] [InlineData(0)] [InlineData(511)]
    public void RememberedLeafDoesNotHideDiskCorruption(int offset)
    {
        var h=new Harness(NovaImage.FormatPacked()); h.Mount();
        uint file=h.Open("proof"); h.Close(file); h.Flush();
        Assert.Equal(uint.MaxValue,h.Packet(31,1));
        uint sector=0;
        var probe=new Harness(h.Disk); probe.BeforeIo=io=>{ if(io.Kind==IoKind.Read && io.Sector.Low is 1394 or 1395) sector=io.Sector.Low; };
        probe.Mount(); probe.Close(probe.Open("proof",1005)); Assert.NotEqual(0u,sector);
        h.Disk[sector*512+offset]^=1;
        if(h.Packet(31,0)!=0) Assert.Equal(0u,h.OpenRaw("proof",1005));
    }
    [Theory] [InlineData(511)] [InlineData(4095)]
    public void MultiSectorUnalignedWritePreservesBothNeighbors(int offset)
    {
        var h=new Harness(NovaImage.FormatPacked()); h.Mount(); uint file=h.Open("span");
        byte[] expected=Enumerable.Range(0,16384).Select(i=>(byte)(i*31)).ToArray(); h.Write(file,expected);
        byte[] update=Enumerable.Repeat((byte)187,7169).ToArray(); Assert.Equal(16384u,h.Packet(1008,file,(uint)offset,uint.MaxValue)); h.Write(file,update); update.CopyTo(expected,offset);
        h.Close(file); h.Flush(); h.InhibitAndRemount(); file=h.Open("span",1005);
        Assert.Equal(16384u,h.Packet(82,file,Harness.Data,16384)); Assert.Equal(expected,h.Memory.Bytes.AsSpan((int)Harness.Data,16384).ToArray());
    }
}
