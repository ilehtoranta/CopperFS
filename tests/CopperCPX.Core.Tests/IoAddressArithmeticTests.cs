using System.Numerics;
using System.Reflection;

namespace CopperCPX.Tests;

public class IoAddressArithmeticTests
{
    [Theory][InlineData(9,0)][InlineData(9,1)][InlineData(9,2)][InlineData(9,3)][InlineData(10,2)][InlineData(11,1)][InlineData(12,0)]
    public void PendingSectorAndExclusiveEndMatchUnboundedArithmetic(int sectorShift,int delta)
    {
        var ram=new Ram(new byte[100000]); var engine=new CpxEngine<Ram>(ram,32);
        engine.Initialize((uint)(1<<sectorShift),(uint)(1<<(sectorShift+delta)),32768,1,metadataPageBytes:(uint)(1<<(sectorShift+delta)));
        var method=typeof(CpxEngine<Ram>).GetMethod("Issue",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var random=new Random(91243+delta); byte[] bits=new byte[8];
        ulong Next() { random.NextBytes(bits); return BitConverter.ToUInt64(bits); }
        for(int turn=0;turn<1500;turn++)
        {
            ulong capacity=Next(),unit=(turn%6) switch { 0=>ulong.MaxValue,1=>ulong.MaxValue>>(delta),2=>uint.MaxValue,3=>capacity>>(delta),4=>1UL<<32,_=>Next() };
            uint bytes=(uint)random.NextInt64(0,1L<<32)&~((1u<<sectorShift)-1);
            if(turn%11==0) bytes|=1;
            IoKind kind=(turn%3) switch { 0=>IoKind.Read,1=>IoKind.Write,_=>IoKind.Discard }; var sector=(BigInteger)unit<<delta;
            bool valid=sector<=ulong.MaxValue&&bytes!=0&&(bytes&((1u<<sectorShift)-1))==0&&sector+(bytes>>sectorShift)<=capacity;
            ram.Write32(32+48,0); ram.Write32(32+56,0); ram.Write32(32+44,0); ram.Write32(32+80,0);
            ram.Write32(32+72,(uint)(capacity>>32)); ram.Write32(32+76,(uint)capacity);
            var step=(StepResult)method.Invoke(engine,[kind,unit,80000u,bytes,123u])!;
            Assert.Equal(valid?StepResult.NeedIo:StepResult.Failed,step);
            if(valid)
            {
                var io=engine.Request; Assert.Equal((ulong)sector,(ulong)io.Sector); Assert.Equal(bytes,io.Bytes);
                Assert.Equal(80000u,io.Buffer); Assert.Equal(kind,io.Kind); Assert.Equal(123u,ram.Read32(32+64));
                Assert.Equal(ram.Read32(32+156),io.Token);
            }
            else Assert.Equal(CpxError.Corrupt,engine.Error);
        }
    }
}
