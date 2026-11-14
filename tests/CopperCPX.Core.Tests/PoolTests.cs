using CopperCPX;
namespace CopperCPX.Tests;
public class PoolTests
{
    [Theory][InlineData(ResourceKind.Handle,32)][InlineData(ResourceKind.Lock,32)][InlineData(ResourceKind.Enumeration,16)]
    [InlineData(ResourceKind.Notification,8)][InlineData(ResourceKind.RecordLock,32)][InlineData(ResourceKind.RecordWait,16)]
    public void PoolsRejectExhaustionAndStaleRelease(ResourceKind kind,int capacity)
    {
        var ram=new Ram(new byte[17000]); var pools=new FixedPools<Ram>(ram,32); pools.Initialize(); uint first=0,old=0;
        for(int n=0;n<capacity;n++) { Assert.Equal(CpxError.None,pools.Take(kind,out uint p,out uint t)); if(n==0) { first=p; old=t; } }
        Assert.Equal(CpxError.ResourceExhausted,pools.Take(kind,out _,out _));
        Assert.Equal(CpxError.None,pools.Release(kind,first,old)); Assert.Equal(CpxError.None,pools.Take(kind,out uint same,out uint next));
        Assert.Equal(first,same); Assert.NotEqual(old,next); Assert.Equal(CpxError.InvalidArgument,pools.Release(kind,first,old));
        Assert.Equal(CpxError.None,pools.Release(kind,same,next));
    }
}
