using CopperNova.Reference;
using static CopperNova.Tests.FilesystemTests;
namespace CopperNova.Tests;

public class ExtentGrowthTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndexedAndLegacyPrefixesSurviveTruncationAndRegrowth(bool legacy)
    {
        var h=new Harness(NovaImage.FormatPacked()); h.Mount();
        uint file=h.Open("target"),spacer=h.Open("spacer");
        var expected=new List<byte[]>();
        void Append(int limit,int salt)
        {
            while(expected.Count<limit)
            {
                byte[] data=new byte[512];
                new Random(salt+expected.Count).NextBytes(data);
                h.Write(file,data); h.Write(spacer,new byte[512]); expected.Add(data);
            }
        }
        void Reopen()
        {
            h.Close(file); h.Close(spacer); h.Flush(); h.InhibitAndRemount();
            file=h.Open("target",1004); spacer=h.Open("spacer",1004);
        }
        Append(81,1); Reopen();
        if(legacy)
        {
            h.Close(file); h.Close(spacer); h.Flush();
            // Reproduce the original v6 page encoding, including its CRC.
            for(int at=0;at<h.Disk.Length;at+=512)
            {
                var p=h.Disk.AsSpan(at,512);
                if(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(p)!=0x434e5032 ||
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(p[316..])!=0x45584931) continue;
                if(p[7]==41) p.Slice(100,128).Clear();
                else if(p[7]!=40) continue;
                p.Slice(228,64).Clear(); p.Slice(312,8).Clear(); p.Slice(8,4).Clear();
                uint crc=uint.MaxValue;
                foreach(byte b in p)
                {
                    crc^=b;
                    for(int bit=0;bit<8;bit++) crc=(crc>>1)^((crc&1)==0?0:0x82f63b78u);
                }
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(p[8..],~crc);
            }
            h=new Harness(h.Disk); h.Mount(); file=h.Open("target",1004); spacer=h.Open("spacer",1004);
        }
        void SeekEnd(uint fd) => Assert.NotEqual(uint.MaxValue,h.Packet(1008,fd,0,1));
        SeekEnd(file); SeekEnd(spacer); Append(2361,13); Reopen();
        void Verify()
        {
            var report=NovaImage.CheckPacked(h.Disk);
            Assert.True(report.Entries.Single(e=>e.Path=="/target").Extents>=expected.Count-1);
            foreach(int i in Enumerable.Range(0,expected.Count).OrderBy(i=>(i*199)%expected.Count))
            {
                Assert.NotEqual(uint.MaxValue,h.Packet(1008,file,(uint)i*512,uint.MaxValue));
                Assert.Equal(512u,h.Packet(82,file,Harness.Data,512));
                Assert.Equal(expected[i],h.Memory.Bytes.AsSpan((int)Harness.Data,512).ToArray());
            }
        }
        Verify();
        Assert.Equal(99u*512,h.Packet(1022,file,99*512,uint.MaxValue));
        expected.RemoveRange(99,expected.Count-99);
        SeekEnd(file); SeekEnd(spacer); Append(1301,37); Reopen(); Verify();
    }

    [Fact]
    public void ShortFirstOverflowWriteLeavesBothInlineFilesIntact()
    {
        var h=new Harness(NovaImage.FormatPacked()); h.Mount();
        uint a=h.Open("a"),b=h.Open("b");
        byte[] bytes=new byte[512];
        int overflowWrites=0;
        h.BeforeIo=io=>{
            if(io.Kind==CopperCPX.IoKind.Write && h.Memory.Read16(io.Buffer+6)==41) overflowWrites++;
        };
        for(int i=0;i<8;i++) { h.Write(a,bytes); h.Write(b,bytes); }
        h.Flush();
        Assert.Equal(0,overflowWrites);
        var before=NovaImage.CheckPacked(h.Disk);
        Assert.All(before.Entries,e=>Assert.Equal(8,e.Extents));

        h.Memory.Write32(Harness.PacketAt+8,87);
        h.Memory.Write32(Harness.PacketAt+20,a);
        h.Memory.Write32(Harness.PacketAt+24,Harness.Data);
        h.Memory.Write32(Harness.PacketAt+28,512);
        Assert.Equal(CopperCPX.CpxError.None,h.Engine.BeginPacket(Harness.PacketAt));
        bool failed=false;
        while(h.Engine.Busy)
        {
            if(h.Engine.Step()!=CopperCPX.StepResult.NeedIo) continue;
            var io=h.Engine.Request;
            if(io.Kind==CopperCPX.IoKind.Write && h.Memory.Read16(io.Buffer+6)==41)
            {
                h.Memory.Bytes.AsSpan((int)io.Buffer,256).CopyTo(h.Disk.AsSpan((int)io.Sector.Low*512));
                Assert.Equal(CopperCPX.CpxError.Io,h.Engine.CompleteIo(io.Token,256,true));
                failed=true;
            }
            else h.Service(io);
        }
        Assert.True(failed);
        var cold=new Harness(h.Disk); cold.Mount();
        cold.Close(cold.Open("a",1005)); cold.Close(cold.Open("b",1005));
        var after=NovaImage.CheckPacked(h.Disk);
        Assert.Equal(before.Generation,after.Generation);
        Assert.Equal(before.FreeSectors,after.FreeSectors);
        Assert.Equal(before.Entries.ToArray(),after.Entries.ToArray());
    }

    [Fact]
    public void MultiWindowRetirementKeepsTheDurableRootUntilPublication()
    {
        var h=new Harness(NovaImage.FormatPacked()); h.Mount();
        uint target=h.Open("target"),neighbor=h.Open("neighbor");
        byte[] run=new byte[32768];
        for(int i=0;i<64;i++)
        {
            Array.Fill(run,(byte)i); h.Write(target,run);
            Array.Fill(run,(byte)~i); h.Write(neighbor,run);
        }
        h.Close(target); h.Close(neighbor); h.Flush(); h.InhibitAndRemount();
        var before=NovaImage.CheckPacked(h.Disk);
        Assert.All(before.Entries,e=>Assert.True(e.Extents>=64));
        target=h.Open("target",1004);
        int barriers=0;
        h.BeforeIo=io=>
        {
            if(io.Kind!=CopperCPX.IoKind.Flush) return;
            var snapshot=NovaImage.CheckPacked(h.Disk);
            if(barriers++==0) Assert.Equal(before.Entries.ToArray(),snapshot.Entries.ToArray());
            else Assert.Equal(510,snapshot.Entries.Single(e=>e.Path=="/target").Size);
        };
        Assert.Equal(510u,h.Packet(1022,target,510,uint.MaxValue));
        Assert.Equal(2,barriers);
        h.BeforeIo=null; h.Close(target); h.InhibitAndRemount();
        var after=NovaImage.CheckPacked(h.Disk);
        Assert.Equal(1,after.Entries.Single(e=>e.Path=="/target").Extents);
        Assert.Equal(before.Entries.Single(e=>e.Path=="/neighbor"),after.Entries.Single(e=>e.Path=="/neighbor"));
    }

    [Fact]
    public void FragmentedFilesSurviveColdReadsShrinkRegrowthAndDelete()
    {
        var h=new Harness(NovaImage.FormatPacked());
        h.Mount();
        uint a=h.Open("target"),b=h.Open("neighbor");
        var data=new byte[64*512];
        new Random(91).NextBytes(data);
        for(int i=0;i<64;i++)
        {
            var sector=data.AsSpan(i*512,512).ToArray();
            h.Write(a,sector); h.Write(b,sector);
        }
        h.Close(a); h.Close(b); h.Flush(); h.InhibitAndRemount();
        var before=NovaImage.CheckPacked(h.Disk);
        Assert.All(before.Entries,file=>Assert.Equal(64,file.Extents));
        a=h.Open("target",1004);
        Assert.Equal((uint)data.Length,h.Packet(82,a,Harness.Data,(uint)data.Length));
        Assert.Equal(data,h.Memory.Bytes.AsSpan((int)Harness.Data,data.Length).ToArray());
        const uint shortened=11*512+3;
        Assert.Equal(shortened,h.Packet(1022,a,shortened,uint.MaxValue));
        h.Close(a); h.Flush(); h.InhibitAndRemount();
        Assert.Equal(12,NovaImage.CheckPacked(h.Disk).Entries.Single(e=>e.Path=="/target").Extents);
        a=h.Open("target",1004);
        Assert.Equal(0u,h.Packet(1008,a,shortened,uint.MaxValue));
        h.Write(a,new byte[4096]); h.Close(a); h.Flush();
        var after=NovaImage.CheckPacked(h.Disk);
        Assert.Equal(shortened+4096,after.Entries.Single(e=>e.Path=="/target").Size);
        h.Bstr(Harness.NameAt,"target");
        Assert.Equal(uint.MaxValue,h.Packet(16,0,Harness.NameAt>>2)); h.Flush(); h.InhibitAndRemount();
        Assert.Equal(before.Entries.Single(e=>e.Path=="/neighbor"),Assert.Single(NovaImage.CheckPacked(h.Disk).Entries));
    }
}
