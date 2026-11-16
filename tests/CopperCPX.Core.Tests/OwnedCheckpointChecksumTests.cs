using CopperFS.Cpx4.Reference;

namespace CopperCPX.Tests;

public class OwnedCheckpointChecksumTests
{
    [Theory][InlineData(512)][InlineData(1024)][InlineData(2048)][InlineData(4096)]
    public void AddressTransformMatchesIndependentFullChecksumAndAllOtherBytes(int bytes)
    {
        var ram=new Ram(new byte[1000000]); var engine=new CpxEngine<Ram>(ram,32);
        Assert.Equal(CpxError.None,engine.Initialize(512,512,Wide.From(8192),1));
        Assert.Equal(CpxError.None,engine.EnableBufferedWrites(300000,CpxEngine<Ram>.BufferedStorageBytes(1024)));
        var random=new Random(0x5c0f+bytes);
        uint table=ram.Read32(32+4044),zeros=engine.BufferedStorageAddress;
        var retarget=typeof(CpxArena).Assembly.GetType("CopperCPX.CpxShadowChecksums`1",true)!.MakeGenericType(typeof(Ram))
            .GetMethod("RetargetOwned",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
        for(int trial=0;trial<128;trial++)
        {
            byte[] expected=new byte[bytes]; random.NextBytes(expected);
            // Include high-half changes, carry boundaries, repeated and zero addresses.
            ulong address=trial%8==7?Layout.U64(expected,16):
                (trial%4) switch {0=>0UL,1=>0x100000000UL,2=>ulong.MaxValue,_=>(ulong)random.NextInt64()<<1};
            expected.CopyTo(ram.Bytes,100000);
            ram.Bytes[99999]=0xa5; ram.Bytes[100000+bytes]=0xa5;
            ram.Write32(100008,Layout.Crc(expected,true));
            Layout.Put64(expected,16,address); Layout.Put32(expected,8,Layout.Crc(expected,true));
            object?[] arguments=[ram,100000u,(uint)bytes,address,table,zeros,32u+4080u];
            retarget.Invoke(null,arguments); ram=(Ram)arguments[0]!;
            Assert.Equal(expected,ram.Bytes.AsSpan(100000,bytes).ToArray());
            Assert.Equal(0xa5,ram.Bytes[99999]); Assert.Equal(0xa5,ram.Bytes[100000+bytes]);
        }
    }
}
