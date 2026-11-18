using CopperFS.Cpx4.Reference;
using CopperCPX.Reference.Tests;

namespace CopperCPX.Tests;

public class CheckpointPairWriteTests
{
    [Theory][InlineData(512,false,false)][InlineData(512,true,false)][InlineData(4096,false,false)][InlineData(512,false,true)][InlineData(4096,false,true)]
    public void AdjacentPublicationPreservesAnUntouchedRootAndRecoversFromAnInteriorSurvivor(int unit,bool arenaUndo,bool interior)
    {
        var disk=new MemoryDevice(); Cpx4Volume<MemoryDevice>.FormatDirectInodes(disk,unit,new byte[49152],12345,true,true);
        int rootBytes=Math.Max(1024,unit),first=65536+rootBytes;
        ulong initialGeneration=Layout.U64(disk.Bytes.AsSpan(first,rootBytes),80);
        if(interior)
        {
            var root=disk.Bytes.AsSpan(first+rootBytes,rootBytes);
            Layout.Put64(root,32,initialGeneration+1); Layout.Put64(root,80,initialGeneration+1); Layout.Put32(root,8,Layout.Crc(root,true));
        }
        var ram=new Ram(new byte[1000000]); var engine=new CpxEngine<Ram>(ram,32);
        engine.Initialize(512,(uint)unit,disk.SectorCount,1,metadataPageBytes:(uint)unit);
        uint capacity=arenaUndo?CpxEngine<Ram>.ArenaUndoBufferedStorageBytes():CpxEngine<Ram>.BufferedStorageBytes((uint)unit);
        Assert.Equal(CpxError.None,arenaUndo?engine.EnableArenaUndoBufferedWrites(300000,capacity,CpxArena.Bytes(4096)):engine.EnableBufferedWrites(300000,capacity));
        Assert.Equal(CpxError.None,engine.EnableShadowVolume(900000,32768,5));
        ram.Bytes.AsSpan((int)(300000+capacity),32).Fill(0xa5); ram.Bytes.AsSpan((int)(32+CpxArena.Bytes(4096)),32).Fill(0x5a);
        List<CpxIo> Drive()
        {
            var requests=new List<CpxIo>();
            for(int turn=0;turn<20000;turn++)
            {
                var step=engine.Step(); if(step==StepResult.Complete) return requests;
                Assert.NotEqual(StepResult.Failed,step); if(step!=StepResult.NeedIo) continue;
                var io=engine.Request; requests.Add(io); var bytes=ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes);
                if(io.Kind==IoKind.Read) disk.Read(io.Sector,bytes); else if(io.Kind==IoKind.Write) disk.Write(io.Sector,bytes); else disk.Flush();
                Assert.Equal(CpxError.None,engine.CompleteIo(io.Token,io.Bytes,true));
            }
            throw new InvalidOperationException("Publication did not finish.");
        }
        engine.BeginOperation(CpxOperation.Mount); Drive();
        for(int publication=0;publication<3;publication++)
        {
            byte[] before=disk.Bytes.AsSpan(first,3*rootBytes).ToArray();
            ram.Bytes[100000]=(byte)('A'+publication); engine.BeginLabel(100000,1); Drive();
            engine.BeginPersistenceBarrier(); var writes=Drive().Where(io=>io.Kind==IoKind.Write&&(ulong)io.Sector>=(uint)first/512&&(ulong)io.Sector<(uint)(first+3*rootBytes)/512).ToArray();
            Assert.Equal(interior&&publication==0?2:1,writes.Length);
            if(writes.Length==1) { Assert.Equal((uint)(2*rootBytes),writes[0].Bytes); Assert.True((ulong)writes[0].Sector==(uint)first/512||(ulong)writes[0].Sector==(uint)(first+rootBytes)/512); }
            int unchanged=0,newCopies=0;
            for(int slot=0;slot<3;slot++)
            {
                var root=disk.Bytes.AsSpan(first+slot*rootBytes,rootBytes);
                if(root.SequenceEqual(before.AsSpan(slot*rootBytes,rootBytes))) unchanged++;
                Assert.Equal(Layout.Crc(root,true),Layout.U32(root,8));
                Assert.Equal((ulong)(first+slot*rootBytes)/(uint)unit,Layout.U64(root,16));
                if(Layout.U64(root,80)==initialGeneration+(ulong)(publication+1+(interior?1:0))) newCopies++;
            }
            Assert.Equal(1,unchanged); Assert.Equal(2,newCopies);
            engine.BeginRemount(); Drive(); Assert.Equal(((char)('A'+publication)).ToString(),VolumeLabelTests.Cached(engine,ram));
            new Cpx4Volume<MemoryDevice>(disk,new byte[49152]).Check();
        }
        Assert.All(ram.Bytes.AsSpan((int)(300000+capacity),32).ToArray(),b=>Assert.Equal(0xa5,b));
        Assert.All(ram.Bytes.AsSpan((int)(32+CpxArena.Bytes(4096)),32).ToArray(),b=>Assert.Equal(0x5a,b));
    }
}
