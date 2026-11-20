using CopperFS.Cpx4.Reference;
using CopperCPX.Reference.Tests;

namespace CopperCPX.Tests;

public class ShadowCheckpointBatchTests
{
    [Theory][InlineData(512,false,false)][InlineData(512,true,false)][InlineData(512,true,true)]
    [InlineData(1024,false,false)][InlineData(2048,false,false)][InlineData(4096,false,false)]
    public void AdjacentCheckpointBatchKeepsIndependentFallbackAndWorkspaceGuards(int unit,bool catalogue,bool arenaUndo)
    {
        var initial=new MemoryDevice(); Cpx4Volume<MemoryDevice>.FormatDirectInodes(initial,unit,new byte[49152],12345,true,catalogue);
        uint rootBytes=(uint)Math.Max(catalogue?1024:unit,unit),anchor=(uint)(65536/unit);
        for(int corrupt=-1;corrupt<3;corrupt++)
        {
            var disk=new MemoryDevice((byte[])initial.Bytes.Clone());
            if(corrupt>=0) disk.Bytes[anchor*unit+(corrupt+1)*(int)rootBytes+8]^=0x40;
            var ram=new Ram(new byte[1000000]); var engine=new CpxEngine<Ram>(ram,32);
            engine.Initialize(512,(uint)unit,disk.SectorCount,1,metadataPageBytes:(uint)unit);
            uint bytes=arenaUndo?CpxEngine<Ram>.ArenaUndoBufferedStorageBytes():CpxEngine<Ram>.BufferedStorageBytes((uint)unit);
            Assert.Equal(CpxError.None,arenaUndo?engine.EnableArenaUndoBufferedWrites(300000,bytes,CpxArena.Bytes(4096)):engine.EnableBufferedWrites(300000,bytes));
            Assert.Equal(CpxError.None,engine.EnableShadowVolume(900000,32768,catalogue?5u:4u));
            ram.Bytes.AsSpan((int)(300000+bytes),32).Fill(0xa5); ram.Bytes.AsSpan((int)(32+CpxArena.Bytes(4096)),32).Fill(0x5a);
            engine.BeginOperation(CpxOperation.Mount); int batches=0;
            for(int turn=0;turn<20000;turn++)
            {
                var step=engine.Step(); if(step==StepResult.Complete) break;
                Assert.NotEqual(StepResult.Failed,step); Assert.True(turn<19999);
                if(step!=StepResult.NeedIo) continue;
                var io=engine.Request; Assert.Equal(IoKind.Read,io.Kind);
                if(io.Bytes==3*rootBytes) { batches++; Assert.Equal((ulong)(anchor+(rootBytes/(uint)unit))*(uint)unit/512,(ulong)io.Sector); }
                disk.Read(io.Sector,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes));
                Assert.Equal(CpxError.None,engine.CompleteIo(io.Token,io.Bytes,true));
            }
            Assert.Equal(1,batches); Assert.True(engine.IsMounted);
            Assert.All(ram.Bytes.AsSpan((int)(300000+bytes),32).ToArray(),b=>Assert.Equal(0xa5,b));
            Assert.All(ram.Bytes.AsSpan((int)(32+CpxArena.Bytes(4096)),32).ToArray(),b=>Assert.Equal(0x5a,b));
            "file"u8.CopyTo(ram.Bytes.AsSpan(100000)); Assert.Equal(CpxError.None,engine.BeginCreate(Wide.From(1),100000,4)); EngineTests.Drive(ref engine,ram,disk);
            engine.BeginPersistenceBarrier(); EngineTests.Drive(ref engine,ram,disk); engine.BeginRemount(); EngineTests.Drive(ref engine,ram,disk);
            new Cpx4Volume<MemoryDevice>(disk,new byte[49152]).Check();
        }
    }
}
