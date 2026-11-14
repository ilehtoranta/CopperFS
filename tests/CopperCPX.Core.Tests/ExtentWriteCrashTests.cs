using CopperCPX;
using CopperCPX.Reference.Tests;
using CopperFS.Cpx4.Reference;

namespace CopperCPX.Tests;

public class ExtentWriteCrashTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)]
    public void PromotionTreeSplitAndUnwrittenConversionRecoverAtEverySectorAndBarrier(int mode)
    {
        var disk=EngineTests.Fresh(512); var v=new Cpx4Volume<Disk>(disk,new byte[36864]); v.Create("file"u8,out ulong id);
        bool unwritten=mode>=2; int count=mode is 1 or 2?40:6;
        for(int n=0;n<count;n++)
        {
            if(unwritten) v.Preallocate(id,(ulong)n*4096,3,out _);
            else v.Write(id,(ulong)n*1024,Enumerable.Repeat((byte)0x5a,512).ToArray(),out _);
        }
        uint offset=unwritten?(uint)((count-1)*4096+512+13):(uint)(count*1024+13);
        if(!unwritten) v.Truncate(id,offset+1000);
        var initial=new IndependentImage(disk.Bytes); if(unwritten) ExtentWriteTests.StampUnwritten(disk,initial.Objects[id],initial.Metadata);
        void Write(IBlockDevice device)
        {
            var ram=new Ram(new byte[100000]); var e=FileMutationTests.Mount(device,ram); ram.Bytes.AsSpan(90000,17).Fill(0x7c);
            Assert.Equal(CpxError.None,e.BeginMappedWrite(Wide.From((uint)id),Wide.From(offset),90000,17)); EngineTests.Drive(ref e,ram,device);
            Assert.Equal(17u,e.TransferredBytes);
        }
        var trace=new CrashDevice(disk.Bytes); Write(trace);
        for(int cut=1;cut<=trace.Events.Count;cut++) for(int persistence=0;persistence<5;persistence++)
        {
            var fault=new CrashDevice(disk.Bytes){Cut=cut,Persistence=persistence}; Assert.Throws<PowerLoss>(()=>Write(fault));
            var final=new MemoryDevice(fault.Durable); FileMutationTests.Mount(final,new Ram(new byte[100000]));
            var decoded=new IndependentImage(final.Bytes); var check=new Cpx4Volume<MemoryDevice>(final,new byte[36864]); check.Check();
            byte[] actual=new byte[512]; Assert.Equal(512,check.Read(id,offset&~511u,actual));
            byte[] changed=new byte[512]; changed.AsSpan(13,17).Fill(0x7c);
            Assert.True(actual.AsSpan().SequenceEqual(new byte[512])||actual.AsSpan().SequenceEqual(changed));
            if(unwritten) Assert.Equal(IndependentImage.L(initial.Objects[id],16),IndependentImage.L(decoded.Objects[id],16));
        }
        output.WriteLine($"Extent mode {mode}: {trace.Events.Count} cuts x 5 = {trace.Events.Count*5} recovered images.");
    }
}
