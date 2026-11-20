using System.Reflection;
using CopperFS.Cpx4.Reference;

namespace CopperCPX.Tests;

public class MatchingCheckpointChecksumTests
{
    [Theory][InlineData(512)][InlineData(1024)][InlineData(2048)][InlineData(4096)]
    public void MatchingAndDifferingCopiesMatchFullChecksumAtEveryChangedByte(int bytes)
    {
        var ram=new Ram(new byte[1000000]); var engine=new CpxEngine<Ram>(ram,32);
        engine.Initialize(512,512,32768,1,metadataPageBytes:512);
        engine.EnableBufferedWrites(300000,CpxEngine<Ram>.BufferedStorageBytes(512));
        ram.Write32(32+120,1);
        byte[] validated=new byte[bytes]; new Random(14327+bytes).NextBytes(validated);
        Layout.Put64(validated,16,0xffffffffUL); Layout.Put32(validated,8,Layout.Crc(validated,true));
        validated.CopyTo(ram.Bytes,150000);
        byte[] incoming=(byte[])validated.Clone(); Layout.Put64(incoming,16,0x100000001UL); Layout.Put32(incoming,8,Layout.Crc(incoming,true));
        incoming.CopyTo(ram.Bytes,100000);
        ram.Bytes[99999]=0xa5; ram.Bytes[100000+bytes]=0x5a;
        ram.Bytes[149999]=0x5a; ram.Bytes[150000+bytes]=0xa5;
        var method=typeof(CpxEngine<Ram>).GetMethod("ShadowCheckpointChecksum",BindingFlags.NonPublic|BindingFlags.Instance)!;
        for(int changed=-1;changed<bytes;changed++)
        {
            if(changed>=0) { incoming[changed]^=0x40; ram.Bytes[100000+changed]^=0x40; }
            uint expected=Layout.Crc(incoming,true);
            uint actual=(uint)method.Invoke(engine,[100000u,(uint)bytes,Layout.U64(incoming,16),150000u,true])!;
            Assert.Equal(expected,actual); Assert.Equal(incoming,ram.Bytes.AsSpan(100000,bytes).ToArray());
            if(changed>=0) { incoming[changed]^=0x40; ram.Bytes[100000+changed]^=0x40; }
        }
        Assert.Equal(validated,ram.Bytes.AsSpan(150000,bytes).ToArray());
        Assert.Equal(0xa5,ram.Bytes[99999]); Assert.Equal(0x5a,ram.Bytes[100000+bytes]);
        Assert.Equal(0x5a,ram.Bytes[149999]); Assert.Equal(0xa5,ram.Bytes[150000+bytes]);
    }
}
