using CopperFS.Cpx4.Reference;
using CopperCPX.Reference.Tests;

namespace CopperCPX.Tests;

public class CheckpointProofSchemaTests
{
    [Theory][InlineData(512,4u,56)][InlineData(512,5u,56)][InlineData(4096,4u,232)]
    [InlineData(512,5u,1016)][InlineData(512,5u,96)]
    public void CorrectChecksumDoesNotAuthorizeChangedSchemaOrConflictingSameGenerationBody(int unit,uint features,int field)
    {
        var disk=new MemoryDevice(); Cpx4Volume<MemoryDevice>.FormatDirectInodes(disk,unit,new byte[49152],12345,true,features==5);
        int bytes=Math.Max(unit,features==5?1024:512);
        var checkpoint=disk.Bytes.AsSpan(65536+2*bytes,bytes);
        if(field==56) Layout.Put16(checkpoint,field,0);
        else if(field==232) Layout.Put16(checkpoint,field,33);
        else checkpoint[field]^=0x40;
        Layout.Put32(checkpoint,8,Layout.Crc(checkpoint,true));
        var ram=new Ram(new byte[1000000]); var engine=new CpxEngine<Ram>(ram,32);
        Assert.Equal(CpxError.None,engine.Initialize(512,(uint)unit,disk.SectorCount,1,true,metadataPageBytes:(uint)unit));
        Assert.Equal(CpxError.None,engine.EnableBufferedWrites(300000,CpxEngine<Ram>.BufferedStorageBytes((uint)unit)));
        Assert.Equal(CpxError.None,engine.EnableShadowVolume(900000,32768,features));
        Assert.Equal(CpxError.None,engine.BeginOperation(CpxOperation.Mount));
        for(int turn=0;turn<20000;turn++)
        {
            var result=engine.Step();
            if(result==StepResult.Failed) {Assert.Equal(CpxError.Corrupt,engine.Error);Assert.False(engine.IsMounted);return;}
            Assert.NotEqual(StepResult.Complete,result);
            if(result!=StepResult.NeedIo) continue;
            var io=engine.Request;Assert.Equal(IoKind.Read,io.Kind);
            disk.Read(io.Sector,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes));
            Assert.Equal(CpxError.None,engine.CompleteIo(io.Token,io.Bytes,true));
        }
        Assert.Fail("Recovery did not reject the changed checkpoint.");
    }
}
