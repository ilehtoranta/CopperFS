using CopperFS.Cpx4.Reference;
using CopperCPX.Reference.Tests;

namespace CopperCPX.Tests;

public class AppendCapabilityTests
{
    [Theory][InlineData(false,false)][InlineData(false,true)][InlineData(true,false)][InlineData(true,true)]
    public void OwnedAppendKeepsBusyTokenFailureAndReadOnlyAdmission(bool catalogue,bool fail)
    {
        var disk=new MemoryDevice(); Cpx4Volume<MemoryDevice>.FormatDirectInodes(disk,512,new byte[49152],12345,true,catalogue);
        var ram=new Ram(new byte[1000000]); var engine=new CpxEngine<Ram>(ram,32);
        engine.Initialize(512,512,disk.SectorCount,1,metadataPageBytes:512);
        engine.EnableBufferedWrites(300000,CpxEngine<Ram>.BufferedStorageBytes(512));
        engine.EnableShadowVolume(900000,32768,catalogue?5u:4u);
        engine.BeginOperation(CpxOperation.Mount); EngineTests.Drive(ref engine,ram,disk);
        "file"u8.CopyTo(ram.Bytes.AsSpan(100000)); engine.BeginCreate(Wide.From(1),100000,4); EngineTests.Drive(ref engine,ram,disk);
        Wide id=engine.MutatedObject; byte[] payload=Enumerable.Range(0,16384).Select(n=>(byte)(n*17+3)).ToArray(); payload.CopyTo(ram.Bytes,100000);
        engine.BeginMappedWrite(id,default,100000,8192); EngineTests.Drive(ref engine,ram,disk);
        Assert.Equal(1u,ram.Read32(ram.Read32(32+4076)));
        ram.Write32(32+80,1); byte[] readonlyState=(byte[])ram.Bytes.Clone();
        Assert.Equal(CpxError.ReadOnly,engine.BeginMappedWrite(id,Wide.From(8192),108192,8192)); Assert.Equal(readonlyState,ram.Bytes);
        ram.Write32(32+80,0);
        Assert.Equal(CpxError.None,engine.BeginMappedWrite(id,Wide.From(8192),108192,8192));
        byte[] admitted=(byte[])ram.Bytes.Clone();
        Assert.Equal(CpxError.Busy,engine.BeginMappedWrite(id,Wide.From(8192),108192,8192)); Assert.Equal(admitted,ram.Bytes);
        Assert.Equal(StepResult.NeedIo,engine.Step()); CpxIo io=engine.Request;
        Assert.Equal(IoKind.Write,io.Kind); Assert.Equal(8192u,io.Bytes); Assert.Equal(108192u,io.Buffer);
        byte[] pending=(byte[])ram.Bytes.Clone();
        Assert.Equal(CpxError.StaleCompletion,engine.CompleteIo(io.Token+1,io.Bytes,true)); Assert.Equal(pending,ram.Bytes);
        if(fail)
        {
            byte[] durable=(byte[])disk.Bytes.Clone(),owned=ram.Bytes.AsSpan(300000,(int)CpxEngine<Ram>.BufferedStorageBytes(512)).ToArray();
            Assert.Equal(CpxError.Io,engine.CompleteIo(io.Token,0,false)); Assert.Equal(StepResult.Failed,engine.Step());
            Assert.Equal(0u,engine.TransferredBytes); Assert.Equal(durable,disk.Bytes);
            Assert.Equal(owned,ram.Bytes.AsSpan(300000,owned.Length).ToArray());
        }
        else
        {
            disk.Write(io.Sector,ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes));
            Assert.Equal(CpxError.None,engine.CompleteIo(io.Token,io.Bytes,true)); Assert.Equal(StepResult.Complete,engine.Step());
            Assert.Equal(8192u,engine.TransferredBytes);
            engine.BeginPersistenceBarrier(); EngineTests.Drive(ref engine,ram,disk); engine.BeginRemount(); EngineTests.Drive(ref engine,ram,disk);
            var check=new Cpx4Volume<MemoryDevice>(disk,new byte[49152]); check.Check(); byte[] actual=new byte[16384];
            Assert.Equal(16384,check.Read((ulong)id,0,actual)); Assert.Equal(payload,actual);
        }
    }
}
