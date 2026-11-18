using CopperCPX;
using CopperNova.Reference;
using static CopperNova.Tests.FilesystemTests;
namespace CopperNova.Tests;
public class PackedTreeTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact] public void CompareBitmapProbesWithAlignedInitialAllocationHint()
    {
        (long Bytes,long Words,string Hash) Trace(uint hint) {
            var counter=new BitmapReads(); var h=new Harness(NovaImage.FormatPacked(),counter); h.Mount(); uint handle=h.Open("stream");
            h.Memory.Write32(4096+8192+180,hint); byte[] payload=new byte[65536]; counter.Enabled=true;
            for(int i=0;i<32;i++) h.Write(handle,payload); counter.Enabled=false; h.Close(handle); h.Flush();
            return (counter.ByteReads,counter.WordReads,NovaImage.CheckPacked(h.Disk).Entries.Single().Sha256!);
        }
        var measured=Trace(1394); var aligned=Trace(1408); Assert.Equal(measured.Hash,aligned.Hash); Assert.True(measured.Bytes>aligned.Bytes);
        output.WriteLine($"Bitmap payload reads (byte/longword): unaligned hint {measured.Bytes}/{measured.Words}; aligned hint {aligned.Bytes}/{aligned.Words}");
    }
    [Theory] [InlineData(false)] [InlineData(true)] public void IdenticalWorkloadRoundTrips(bool streaming)
    {
        var h=new Harness(NovaImage.FormatPacked()); h.Mount(); h.Workload(streaming); Assert.Equal(2,h.NonemptyBarriers);
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
        uint sector=System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(h.Disk.AsSpan(512+160,2));
        h.Disk[sector*512+offset]^=1;
        Assert.Equal(0u,h.Packet(31,0)); Assert.False(h.Engine.IsMounted);
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
