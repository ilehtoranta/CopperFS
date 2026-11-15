namespace CopperCPX.Tests;

public class WideArithmeticTests
{
    [Fact]
    public void AddressArithmeticMatchesIndependentUnsignedArithmeticIncludingWrappedFailures()
    {
        var random=new Random(0x43505834);
        ulong[] edges=[0,1,uint.MaxValue,(ulong)uint.MaxValue+1,1UL<<63,ulong.MaxValue];
        var values=edges.Concat(Enumerable.Range(0,2048).Select(_=>((ulong)(uint)random.NextInt64()<<32)|(uint)random.NextInt64())).ToArray();
        for(int n=0;n<values.Length;n++)
        {
            ulong a=values[n],b=values[(n*17+3)%values.Length]; var wa=Split(a); var wb=Split(b);
            UInt128 sum=(UInt128)a+b;
            Assert.Equal(sum<=ulong.MaxValue,Wide.Add(wa,wb,out var added)); Assert.Equal(unchecked((ulong)sum),Join(added));
            Assert.Equal(a>=b,Wide.Subtract(wa,wb,out var subtracted)); Assert.Equal(unchecked(a-b),Join(subtracted));
            Assert.Equal(Math.Sign(a.CompareTo(b)),Wide.Compare(wa,wb));
            uint scalar=(uint)b;
            Assert.Equal(unchecked(a+scalar),Join(wa+scalar)); Assert.Equal(unchecked(a-scalar),Join(wa-scalar));
            foreach(int shift in new[]{-1,0,1,9,12,31,32,33,63,64,65})
            {
                bool valid=shift>=0&&shift<64;
                UInt128 shifted=valid?(UInt128)a<<shift:0;
                Assert.Equal(valid&&shifted<=ulong.MaxValue,Wide.Left(wa,shift,out var left)); Assert.Equal(unchecked((ulong)shifted),Join(left));
                Assert.Equal(shift<=0?a:shift>=64?0:a>>shift,Join(Wide.Right(wa,shift)));
            }
        }
    }
    private static Wide Split(ulong n)=>new((uint)(n>>32),(uint)n);
    private static ulong Join(Wide n)=>((ulong)n.High<<32)|n.Low;
}
