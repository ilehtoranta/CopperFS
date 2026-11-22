using CopperNova.Reference;
using static CopperNova.Tests.FilesystemTests;
namespace CopperNova.Tests;

public class ExtentGrowthTests
{
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
