using CopperFS.Cpx4.Reference;
using CopperCPX.Reference.Tests;

namespace CopperCPX.Tests;

public class CachedObjectAdmissionTests
{
    private sealed class Fixture
    {
        internal readonly MemoryDevice Disk=new();
        internal readonly Ram Ram=new(new byte[1000000]);
        internal CpxEngine<Ram> Engine;
        internal readonly Wide Id;
        internal Fixture(bool catalogue,bool arenaUndo)
        {
            Cpx4Volume<MemoryDevice>.FormatDirectInodes(Disk,512,new byte[49152],12345,true,catalogue);
            Engine=new(Ram,32); Engine.Initialize(512,512,Disk.SectorCount,1,metadataPageBytes:512);
            Assert.Equal(CpxError.None,arenaUndo?Engine.EnableArenaUndoBufferedWrites(300000,CpxEngine<Ram>.ArenaUndoBufferedStorageBytes(),CpxArena.Bytes(4096)):
                Engine.EnableBufferedWrites(300000,CpxEngine<Ram>.BufferedStorageBytes(512)));
            Engine.EnableShadowVolume(900000,32768,catalogue?5u:4u);
            Engine.BeginOperation(CpxOperation.Mount); EngineTests.Drive(ref Engine,Ram,Disk);
            "file"u8.CopyTo(Ram.Bytes.AsSpan(100000)); Engine.BeginCreate(Wide.From(1),100000,4); EngineTests.Drive(ref Engine,Ram,Disk); Id=Engine.MutatedObject;
            Ram.Bytes.AsSpan(100000,8192).Fill(0x6a); Engine.BeginMappedWrite(Id,default,100000,8192); EngineTests.Drive(ref Engine,Ram,Disk);
        }
    }

    [Theory][InlineData(false,false)][InlineData(false,true)][InlineData(true,false)][InlineData(true,true)]
    public void CachedReadRetainsBusyIdentityEvictionAndDeletedObjectSemantics(bool catalogue,bool arenaUndo)
    {
        var f=new Fixture(catalogue,arenaUndo);
        Assert.Equal(CpxError.None,f.Engine.BeginOperation(CpxOperation.ReadObject,f.Id)); Assert.False(f.Engine.ObjectFound);
        byte[] admitted=(byte[])f.Ram.Bytes.Clone();
        Assert.Equal(CpxError.Busy,f.Engine.BeginOperation(CpxOperation.ReadObject,Wide.From(1))); Assert.Equal(admitted,f.Ram.Bytes);
        Assert.Equal(CpxError.StaleCompletion,f.Engine.CompleteIo(1,0,true)); Assert.Equal(admitted,f.Ram.Bytes);
        Assert.Equal(StepResult.Complete,f.Engine.Step()); Assert.True(f.Engine.ObjectFound);
        byte[] record=f.Ram.Bytes.AsSpan((int)f.Engine.ObjectRecord,256).ToArray();
        Assert.Equal((ulong)f.Id,Layout.U64(record,0)); Assert.Equal(8192UL,Layout.U64(record,8));
        uint objects=f.Ram.Read32(32+4088); for(uint n=0;n<8;n++) f.Ram.Write32(objects+n*272+268,0);
        Assert.Equal(CpxError.None,f.Engine.BeginOperation(CpxOperation.ReadObject,f.Id)); Assert.False(f.Engine.ObjectFound);
        Assert.Equal(StepResult.Pending,f.Engine.Step()); EngineTests.Drive(ref f.Engine,f.Ram,f.Disk);
        Assert.True(f.Engine.ObjectFound); Assert.Equal(record,f.Ram.Bytes.AsSpan((int)f.Engine.ObjectRecord,256).ToArray());
        "file"u8.CopyTo(f.Ram.Bytes.AsSpan(100000)); f.Engine.BeginDelete(Wide.From(1),100000,4); EngineTests.Drive(ref f.Engine,f.Ram,f.Disk);
        "other"u8.CopyTo(f.Ram.Bytes.AsSpan(100000)); f.Engine.BeginCreate(Wide.From(1),100000,5); EngineTests.Drive(ref f.Engine,f.Ram,f.Disk);
        Assert.NotEqual(f.Id,f.Engine.MutatedObject);
        f.Engine.BeginOperation(CpxOperation.ReadObject,f.Id); EngineTests.Drive(ref f.Engine,f.Ram,f.Disk); Assert.False(f.Engine.ObjectFound);
        f.Engine.BeginPersistenceBarrier(); EngineTests.Drive(ref f.Engine,f.Ram,f.Disk); f.Engine.BeginRemount(); EngineTests.Drive(ref f.Engine,f.Ram,f.Disk);
        new Cpx4Volume<MemoryDevice>(f.Disk,new byte[49152]).Check();
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void CancellationBeforeCachedCompletionNeverPublishesAnObject(bool catalogue)
    {
        var f=new Fixture(catalogue,true); int writes=f.Disk.Writes;
        Assert.Equal(CpxError.None,f.Engine.BeginOperation(CpxOperation.ReadObject,f.Id));
        f.Engine.Cancel(); Assert.Equal(StepResult.Failed,f.Engine.Step()); Assert.Equal(CpxError.Cancelled,f.Engine.Error);
        Assert.False(f.Engine.ObjectFound); Assert.Equal(writes,f.Disk.Writes);
    }

    [Fact]
    public void LegacyCachedReadResetsTraversalStateBeforeReturningItsRecord()
    {
        var disk=EngineTests.Fresh(512); var volume=new Cpx4Volume<Disk>(disk,new byte[36864]);
        volume.Create("file"u8,out ulong id); var ram=new Ram(new byte[1000000]);
        var engine=FileMutationTests.Mount(disk,ram);
        Assert.Equal(CpxError.None,engine.EnableBufferedWrites(300000,CpxEngine<Ram>.BufferedStorageBytes(1024)));
        engine.BeginOperation(CpxOperation.ReadObject,(Wide)id); EngineTests.Drive(ref engine,ram,disk); Assert.True(engine.ObjectFound);
        ram.Write32(32+212,7); ram.Write32(32+216,3);
        Assert.Equal(CpxError.None,engine.BeginOperation(CpxOperation.ReadObject,(Wide)id));
        Assert.Equal(StepResult.Complete,engine.Step()); Assert.True(engine.ObjectFound);
        Assert.Equal(0u,ram.Read32(32+212)); Assert.Equal(uint.MaxValue,ram.Read32(32+216));
        Assert.Equal(id,Layout.U64(ram.Bytes.AsSpan((int)engine.ObjectRecord,256),0));
    }
}
