using CopperCPX;
namespace CopperCPX.Tests;

public class Td64Tests
{
    [Theory]
    [InlineData(0x1000u,4096u,0x001ffffcu,true)]
    [InlineData(0x1002u,512u,0x001ffffcu,false)]
    [InlineData(0x1ff000u,4096u,0x001ffffeu,true)]
    [InlineData(0x1ffe00u,1024u,0x001ffffeu,false)]
    [InlineData(0x2000u,0x2001u,0xffffeffeu,false)] // endpoints fit, interior crosses a hole
    [InlineData(0x4000u,4096u,0x0007f000u,true)]
    [InlineData(0xfffffffcu,8u,0xfffffffcu,false)]
    public void DmaMaskChecksStartAlignmentAndEveryAddressInTheRange(uint address,uint bytes,uint mask,bool fits)
        =>Assert.Equal(fits,Td64Transfer.DmaRange(address,bytes,mask));
    [Theory]
    [InlineData(0u, 0u, 0u, 0x800000u, true)]
    [InlineData(0u, 0u, 0u, 0x800001u, false)]
    [InlineData(0u, 0xfffffe00u, 0u, 1u, true)]
    [InlineData(0u, 0xfffffe00u, 0u, 2u, false)]
    [InlineData(1u, 0u, 0u, 1u, false)]
    [InlineData(uint.MaxValue, 0xfffffe00u, 0u, 2u, false)]
    public void NarrowMountAdmissionChecksPartitionEnd(uint high, uint low, uint sectorsHigh, uint sectorsLow, bool fits)
    {
        Assert.Equal(fits, Td64Transfer.NarrowPartitionFits(new(high,low),new(sectorsHigh,sectorsLow),9));
    }
    [Theory][InlineData(false)][InlineData(true)] public void Td64OnlyCrossingAndSplitRequestsInitializeEveryField(bool write)
    {
        var t=new Td64Transfer();
        Assert.Equal(CpxError.None,t.Begin(new(0,0xfffffe00),Wide.From(100),default,9,2048,0x10000,512,0xfffffffe,0x2000,4096,write,true));
        for(uint i=0;i<4;i++)
        {
            Assert.Equal(StepResult.NeedIo,t.Next(out var r)); Assert.Equal(write?25u:24u,r.Command); Assert.Equal(512u,r.Length);
            Assert.Equal(i==0?0u:1u,r.OffsetHigh); Assert.Equal(i==0?0xfffffe00u:(i-1)*512,r.OffsetLow); Assert.Equal(r.OffsetHigh,r.ActualInput);
            Assert.Equal(CpxError.None,t.Complete(512,0));
        }
        Assert.Equal(2048u,t.CompletedBytes); Assert.Equal(StepResult.Complete,t.Next(out _));
    }
    [Fact] public void WholeRangeIsAdmittedBeforeAnyRequestAndNoTruncatedFallbackExists()
    {
        var t=new Td64Transfer();
        Assert.Equal(CpxError.Unsupported,t.Begin(new(0,0xfffffe00),Wide.From(100),default,9,1024,0x10000,512,0xfffffffe,0x2000,4096,false,false));
        Assert.Equal(CpxError.InvalidArgument,t.Begin(new(uint.MaxValue,0xfffffe00),Wide.From(100),default,9,1024,0x10000,512,0xfffffffe,0x2000,4096,false,true));
        Assert.Equal(CpxError.InvalidArgument,t.Begin(default,Wide.From(1),default,9,1024,0x10000,512,0xfffffffe,0x2000,4096,false,true));
    }
    [Fact] public void ShortCompletionPoisonsTransferWithoutAdvancingAndDmaUsesBounce()
    {
        var t=new Td64Transfer(); Assert.Equal(CpxError.None,t.Begin(default,Wide.From(100),default,9,8192,0x10001,8192,0xfffffffe,0x2000,4096,true,true));
        Assert.Equal(StepResult.NeedIo,t.Next(out var r)); Assert.True(r.Bounced); Assert.Equal(4096u,r.Length); Assert.Equal(0x2000u,r.Buffer);
        Assert.Equal(CpxError.Io,t.Complete(512,0)); Assert.Equal(0u,t.CompletedBytes); Assert.Equal(StepResult.Failed,t.Next(out _));
    }
}
