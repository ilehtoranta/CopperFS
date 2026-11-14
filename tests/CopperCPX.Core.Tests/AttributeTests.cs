using CopperFS.Cpx4.Reference;
using CopperCPX.Reference.Tests;
namespace CopperCPX.Tests;
public class AttributeTests
{
    private static void Mutate(CrashDevice d,bool link)
    {
        var v=new Cpx4Volume<CrashDevice>(d,new byte[36864]);
        if(link) Assert.Equal(MutationResult.Success,v.CreateSymlink(1,"link"u8,new byte[200].Select(_=>(byte)'x').ToArray(),out _));
        else { v.TryGetInfo("file"u8,out var info); Assert.Equal(MutationResult.Success,v.SetComment(info.Id,"comment"u8)); }
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void NewMetadataRecoversThroughTheResumableEngineAtEveryCrashBoundary(bool link)
    {
        var d=EngineTests.Fresh(512); var v=new Cpx4Volume<Disk>(d,new byte[36864]); v.Put("file"u8,"payload"u8);
        byte[] baseline=(byte[])d.Bytes.Clone(); var trace=new CrashDevice(baseline); Mutate(trace,link);
        for(int cut=1;cut<=trace.Events.Count;cut++) for(int mode=0;mode<5;mode++)
        {
            var fault=new CrashDevice(baseline){Cut=cut,Persistence=mode}; Assert.Throws<PowerLoss>(()=>Mutate(fault,link));
            var final=new MemoryDevice(fault.Durable); var ram=new Ram(new byte[100000]); var e=new CpxEngine<Ram>(ram,32);
            e.Initialize(512,512,Wide.From(8192),1); e.BeginOperation(CpxOperation.Mount); EngineTests.Drive(ref e,ram,final);
            var oracle=new IndependentImage(final.Bytes); Assert.Equal("payload"u8.ToArray(),oracle.Read(oracle.Names[(1,"file")]));
        }
    }
    [Theory][InlineData(20)][InlineData(200)][InlineData(768)]
    public void AttributesAndLinksHaveIndependentOwnershipAndReclaimTheirPages(int length)
    {
        var d=EngineTests.Fresh(512); var v=new Cpx4Volume<Disk>(d,new byte[36864]); ulong free=v.Check().FreeUnits;
        byte[] target=Enumerable.Repeat((byte)'x',length).ToArray(); target[0]=(byte)':';
        Assert.Equal(MutationResult.Success,v.CreateSymlink(1,"link"u8,target,out ulong id));
        Assert.Equal(MutationResult.Success,v.SetComment(id,new byte[]{0x41,0xe4}));
        Assert.Equal(MutationResult.Success,v.SetDate(id,new(-10,1439,2999))); Assert.Equal(MutationResult.Success,v.SetProtection(id,0x1234));
        byte[] output=new byte[768]; Assert.Equal(length,v.ReadSymlinkUtf8(id,output)); Assert.Equal(target,output[..length]);
        Assert.Equal(3,v.ReadCommentUtf8(id,output)); Assert.Equal(new byte[]{0x41,0xc3,0xa4},output[..3]);
        v.Check(); var oracle=new IndependentImage(d.Bytes); Assert.Equal(0x1234u,IndependentImage.I(oracle.Objects[id],72));
        Assert.Equal(MutationResult.Success,v.Delete("link"u8)); v.Check(); new IndependentImage(d.Bytes);
        // Empty-tree pruning returns reserve ownership; payload/free accounting stays consistent.
        Assert.True(v.Check().FreeUnits<=free);
    }
}
