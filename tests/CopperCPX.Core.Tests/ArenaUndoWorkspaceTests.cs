using CopperFS.Cpx4.Reference;
using CopperCPX.Reference.Tests;

namespace CopperCPX.Tests;

public class ArenaUndoWorkspaceTests
{
    [Fact]
    public void AdmissionRequiresOwnedArenaTailAndGeometryChangeRevokesTheLease()
    {
        var ram=new Ram(new byte[1000000]); var engine=new CpxEngine<Ram>(ram,32);
        Assert.Equal(CpxError.None,engine.Initialize(512,512,Wide.From(8192),1,metadataPageBytes:512));
        uint bytes=CpxEngine<Ram>.ArenaUndoBufferedStorageBytes(true),maximum=CpxArena.Bytes(4096);
        Assert.Equal(21504u,CpxEngine<Ram>.CompactBufferedStorageBytes(512)-bytes);
        byte[] before=(byte[])ram.Bytes.Clone();
        foreach(var (address,size,owned) in new[]{(300000u,bytes,maximum-1),(300000u,bytes-1,maximum),
            (32+CpxArena.Bytes(512),bytes,maximum),(uint.MaxValue-1024,bytes,maximum)})
        {
            Assert.Equal(CpxError.InvalidArgument,engine.EnableArenaUndoBufferedWrites(address,size,owned));
            Assert.Equal(before,ram.Bytes);
        }
        Assert.Equal(CpxError.None,engine.EnableArenaUndoBufferedWrites(300000,bytes,maximum));
        Assert.Equal(CpxError.InvalidArgument,engine.EnableShadowVolume(32+CpxArena.Bytes(512),32768,5));
        Assert.Equal(CpxError.None,engine.Initialize(512,1024,Wide.From(8192),2));
        Assert.False(engine.BufferedWrites);
        Assert.Equal(CpxError.InvalidArgument,engine.EnableArenaUndoBufferedWrites(300000,bytes,maximum));
        Assert.Equal(CpxError.InvalidArgument,engine.EnableBufferedWrites(300000,bytes));
        Assert.Equal(CpxError.None,engine.Initialize(512,512,Wide.From(8192),3,metadataPageBytes:512));
        Assert.Equal(CpxError.None,engine.EnableArenaUndoBufferedWrites(300000,bytes,maximum));
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void SplitWorkspaceMatchesContiguousDurableBytesThroughCacheChurnAndGroupRetirement(bool compact)
    {
        byte[] Run(bool split)
        {
            var disk=new MemoryDevice(); Cpx4Volume<MemoryDevice>.FormatDirectInodes(disk,512,new byte[49152],12345,true,true);
            var ram=new Ram(new byte[1000000]); var engine=new CpxEngine<Ram>(ram,32);
            uint bytes=split?CpxEngine<Ram>.ArenaUndoBufferedStorageBytes(compact):
                compact?CpxEngine<Ram>.CompactBufferedStorageBytes(512):CpxEngine<Ram>.BufferedStorageBytes(512);
            uint arenaEnd=32+CpxArena.Bytes(4096),bufferEnd=300000+bytes;
            ram.Bytes.AsSpan((int)arenaEnd,32).Fill(0xa5); ram.Bytes.AsSpan((int)bufferEnd,32).Fill(0xa5);
            Assert.Equal(CpxError.None,engine.Initialize(512,512,disk.SectorCount,1,metadataPageBytes:512));
            Assert.Equal(CpxError.None,split?engine.EnableArenaUndoBufferedWrites(300000,bytes,CpxArena.Bytes(4096)):
                engine.EnableBufferedWrites(300000,bytes));
            Assert.Equal(CpxError.None,engine.EnableShadowVolume(900000,32768,5));
            engine.BeginOperation(CpxOperation.Mount); EngineTests.Drive(ref engine,ram,disk);
            Wide id=default;
            for(int n=0;n<32;n++)
            {
                var name=System.Text.Encoding.ASCII.GetBytes($"file{n:D2}"); name.CopyTo(ram.Bytes,100000);
                engine.BeginCreate(Wide.From(1),100000,(uint)name.Length); EngineTests.Drive(ref engine,ram,disk); id=engine.MutatedObject;
                for(int offset=0;offset<1024;offset++) ram.Bytes[100000+offset]=(byte)(offset*17+n);
                engine.BeginMappedWrite(id,default,100000,1024); EngineTests.Drive(ref engine,ram,disk);
            }
            engine.BeginPersistenceBarrier(); EngineTests.Drive(ref engine,ram,disk);
            engine.BeginRemount(); EngineTests.Drive(ref engine,ram,disk); Assert.True(engine.BufferedWrites);
            engine.BeginFileRead(id,default,100000,1024); EngineTests.Drive(ref engine,ram,disk);
            for(int offset=0;offset<1024;offset++) Assert.Equal((byte)(offset*17+31),ram.Bytes[100000+offset]);
            Assert.All(ram.Bytes.AsSpan((int)arenaEnd,32).ToArray(),b=>Assert.Equal(0xa5,b));
            Assert.All(ram.Bytes.AsSpan((int)bufferEnd,32).ToArray(),b=>Assert.Equal(0xa5,b));
            new Cpx4Volume<MemoryDevice>(disk,new byte[49152]).Check();
            return disk.Bytes;
        }
        Assert.Equal(Run(false),Run(true));
    }
}
