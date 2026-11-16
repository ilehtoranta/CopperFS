using System.Numerics;
using System.Reflection;

namespace CopperCPX.Tests;

public class ClassicSeekArithmeticTests
{
    [Fact]
    public void CachedClassicBoundsAndRepliesMatchUnboundedSignedArithmetic()
    {
        var ram=new Ram(new byte[1000000]); var engine=new CpxEngine<Ram>(ram,32);
        engine.Initialize(512,512,32768,1,metadataPageBytes:512);
        var dispatcher=new CpxDosDispatcher<Ram>(ram,32); dispatcher.Initialize(0xabc,0xdef);
        var method=typeof(CpxDosDispatcher<Ram>).GetMethod("FinishCachedClassicSeek",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var random=new Random(67231);
        ulong[] sizes=[0,8192,0x80000013UL,uint.MaxValue,0x100000013UL,ulong.MaxValue];
        uint[] modes=[uint.MaxValue,0,1,2];
        for(int trial=0;trial<12000;trial++)
        {
            ulong size=sizes[trial%sizes.Length],old=(trial%5) switch {0=>0UL,1=>8192UL,2=>(ulong)int.MaxValue,3=>0x100000000UL,_=>(ulong)random.NextInt64(0,1L<<32)};
            uint mode=modes[trial%modes.Length],offset=(trial%7) switch {0=>0u,1=>int.MaxValue,2=>0x80000000u,3=>uint.MaxValue,_=>(uint)random.NextInt64(0,1L<<32)};
            BigInteger basis=mode==0?old:mode==1?size:0,next=basis+unchecked((int)offset);
            bool valid=mode!=2&&old<=int.MaxValue&&next>=0&&next<=int.MaxValue&&next<=size;
            ram.Write32(100008,(uint)(size>>32)); ram.Write32(100012,(uint)size);
            ram.Write32(120008,(uint)(old>>32)); ram.Write32(120012,(uint)old);
            ram.Write32(200008,1008); ram.Write32(200024,offset); ram.Write32(200028,mode);
            Assert.Equal(CpxError.None,dispatcher.BeginPacket(200000));
            Assert.Equal(StepResult.Complete,(StepResult)method.Invoke(dispatcher,[120000u,100000u])!);
            Assert.Equal(valid?(uint)old:uint.MaxValue,dispatcher.Result); Assert.Equal(valid?0u:219u,dispatcher.DosError);
            Assert.Equal(valid?(ulong)next:old,((ulong)ram.Read32(120008)<<32)|ram.Read32(120012));
        }
    }
}
