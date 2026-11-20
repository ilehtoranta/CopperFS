using System.Reflection;
using CopperFS.Cpx4.Reference;

namespace CopperCPX.Tests;

public class SparseChecksumTests
{
    [Theory][InlineData(512)][InlineData(1024)][InlineData(2048)][InlineData(4096)][InlineData(513)][InlineData(4093)]
    public void ZeroRunDetectionMatchesIndependentBitwiseChecksumWithoutChangingInput(int bytes)
    {
        var ram=new Ram(new byte[1000000]); var engine=new CpxEngine<Ram>(ram,32);
        engine.Initialize(512,512,32768,1,metadataPageBytes:512);
        engine.EnableBufferedWrites(300000,CpxEngine<Ram>.BufferedStorageBytes(512));
        var method=typeof(CpxEngine<Ram>).GetMethod("Crc",BindingFlags.NonPublic|BindingFlags.Instance)!;
        var random=new Random(78231+bytes);
        for(int trial=0;trial<96;trial++)
        {
            var input=new byte[bytes];
            if(trial<64) for(int at=64+trial;at<bytes;at+=64) input[at]=(byte)(1<<(trial%8));
            else if(trial%3==0) random.NextBytes(input);
            else if(trial%3==1) Array.Fill(input,(byte)0xff);
            uint address=100000u+(uint)(trial&1); bool page=(trial&2)!=0;
            input.CopyTo(ram.Bytes,(int)address); ram.Bytes[address-1]=0xa5; ram.Bytes[address+(uint)bytes]=0x5a;
            uint expected=Layout.Crc(input,page),actual=(uint)method.Invoke(engine,[address,(uint)bytes,page])!;
            Assert.Equal(expected,actual); Assert.Equal(input,ram.Bytes.AsSpan((int)address,bytes).ToArray());
            Assert.Equal(0xa5,ram.Bytes[address-1]); Assert.Equal(0x5a,ram.Bytes[address+(uint)bytes]);
        }
    }
}
