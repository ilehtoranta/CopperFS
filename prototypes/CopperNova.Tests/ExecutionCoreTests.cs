using System.Reflection;
using CopperNova.Prototype;
using static CopperNova.Tests.FilesystemTests;

namespace CopperNova.Tests;

public class ExecutionCoreTests
{
    [Theory] [InlineData(false,512)] [InlineData(false,1024)] [InlineData(true,512)] [InlineData(true,1024)]
    public void FusedCrcMatchesIndependentBitwiseReferenceWithoutModifyingInput(bool metadata,int bytes)
    {
        var h=new Harness(); h.Mount();
        var method=typeof(NovaEngine<Ram>).GetMethod("Crc",BindingFlags.NonPublic|BindingFlags.Instance)!;
        var random=new Random(0x435243);
        for(int trial=0;trial<8;trial++)
        {
            byte[] input=new byte[bytes]; random.NextBytes(input);
            if(trial==0) Array.Clear(input); else if(trial==1) Array.Fill(input,(byte)255);
            input.CopyTo(h.Memory.Bytes,(int)Harness.Data);
            uint expected=uint.MaxValue;
            for(int i=0;i<bytes;i++)
            {
                expected^=metadata&&i>=8&&i<12?(byte)0:input[i];
                for(int bit=0;bit<8;bit++) expected=(expected>>1)^((expected&1)!=0?0x82F63B78u:0);
            }
            uint actual=(uint)method.Invoke(h.Engine,new object[] { Harness.Data,(uint)bytes,metadata })!;
            Assert.Equal(~expected,actual);
            Assert.Equal(input,h.Memory.Bytes.AsSpan((int)Harness.Data,bytes).ToArray());
        }
    }

    [Fact]
    public void PublishedLeafChecksumCoversEveryLengthAndValidatesPaddingOnRead()
    {
        var h=new Harness(CopperNova.Reference.NovaImage.FormatPacked());
        h.Mount();
        var engine=typeof(NovaEngine<Ram>);
        var stamp=engine.GetMethod("Stamp",BindingFlags.NonPublic|BindingFlags.Instance)!;
        var validate=engine.GetMethod("CheckPageCrc",BindingFlags.NonPublic|BindingFlags.Instance)!;
        var random=new Random(0x434E5032);
        const uint page=Harness.Data;
        for(int used=40;used<=512;used+=4)
        {
            var bytes=h.Memory.Bytes.AsSpan((int)page,512);
            bytes.Clear();
            random.NextBytes(bytes[..used]);
            bytes.Slice(8,4).Clear();
            h.Memory.Write16(page+32,(uint)used);
            stamp.Invoke(h.Engine,new object[] { page,32u,195u,0u,1u });

            uint crc=uint.MaxValue;
            for(int i=0;i<bytes.Length;i++) {
                if(i<8||i>=12) crc^=bytes[i];
                for(int bit=0;bit<8;bit++) crc=(crc>>1)^((crc&1)!=0?0x82F63B78u:0);
            }
            Assert.Equal(~crc,h.Memory.Read32(page+8));
            if(used<512) {
                bytes[used]^=1;
                Assert.False((bool)validate.Invoke(h.Engine,new object[] { page,32u,195u })!);
            }
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void RetirementAcrossWindowsDefersReuseUntilFlushAndSurvivesEpochWrap(bool wrap)
    {
        var h=new Harness(); h.Mount();
        uint file=h.Open("old"); h.Write(file,Enumerable.Repeat((byte)17,900*512).ToArray()); h.Close(file); h.Flush();
        if(wrap) h.Memory.Write32(4096+8192+296,uint.MaxValue);
        file=h.Open("old",1004); Assert.Equal(0u,h.Packet(1022,file,0,uint.MaxValue)); h.Close(file);
        h.Memory.Write32(4096+8192+180,1280);
        file=h.Open("before"); h.Write(file,Enumerable.Repeat((byte)99,4096).ToArray()); h.Close(file);
        Assert.All(h.Disk.AsSpan(1280*512,900*512).ToArray(),b=>Assert.Equal((byte)17,b));
        h.Flush();
        h.Memory.Write32(4096+8192+180,1280);
        file=h.Open("after"); h.Write(file,Enumerable.Repeat((byte)55,512).ToArray()); h.Close(file); h.Flush();
        Assert.All(h.Disk.AsSpan(1280*512,512).ToArray(),b=>Assert.Equal((byte)55,b));
        var check=CopperNova.Reference.NovaImage.Check(h.Disk);
        Assert.Equal(3,check.Entries.Count); Assert.Equal(0,check.Entries.Single(e=>e.Path=="/old").Size);
        Assert.Equal(4096,check.Entries.Single(e=>e.Path=="/before").Size);
        Assert.Equal(512,check.Entries.Single(e=>e.Path=="/after").Size);
    }
}
