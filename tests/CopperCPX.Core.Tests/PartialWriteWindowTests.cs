using CopperFS.Cpx4.Reference;
using CopperCPX.Reference.Tests;

namespace CopperCPX.Tests;

public class PartialWriteWindowTests
{
    private sealed class Fixture
    {
        internal readonly MemoryDevice Disk=new();
        internal readonly Ram Ram=new(new byte[1000000]);
        internal CpxEngine<Ram> Engine;
        internal readonly Wide Id;
        internal readonly byte[] Original;
        internal Fixture(int unit,bool catalogue)
        {
            Cpx4Volume<MemoryDevice>.FormatDirectInodes(Disk,unit,new byte[49152],12345,true,catalogue);
            Engine=new(Ram,32);
            Assert.Equal(CpxError.None,Engine.Initialize(512,(uint)unit,Disk.SectorCount,1,metadataPageBytes:(uint)unit));
            Assert.Equal(CpxError.None,Engine.EnableBufferedWrites(300000,CpxEngine<Ram>.BufferedStorageBytes((uint)unit)));
            Assert.Equal(CpxError.None,Engine.EnableShadowVolume(900000,32768,catalogue?5u:4u));
            Engine.BeginOperation(CpxOperation.Mount); EngineTests.Drive(ref Engine,Ram,Disk);
            "file"u8.CopyTo(Ram.Bytes.AsSpan(100000));
            Engine.BeginCreate(Wide.From(1),100000,4); EngineTests.Drive(ref Engine,Ram,Disk); Id=Engine.MutatedObject;
            Original=Enumerable.Range(0,unit*4).Select(n=>(byte)(n*17+3)).ToArray(); Original.CopyTo(Ram.Bytes,100000);
            Engine.BeginMappedWrite(Id,default,100000,(uint)Original.Length); EngineTests.Drive(ref Engine,Ram,Disk);
            Engine.BeginPersistenceBarrier(); EngineTests.Drive(ref Engine,Ram,Disk);
        }
        internal CpxIo NeedIo()
        {
            for(int turn=0;turn<20000;turn++)
            {
                var step=Engine.Step(); if(step==StepResult.NeedIo) return Engine.Request;
                Assert.Equal(StepResult.Pending,step);
            }
            throw new InvalidOperationException("No I/O request.");
        }
        internal void Complete(CpxIo io)
        {
            var bytes=Ram.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes);
            if(io.Kind==IoKind.Read) Disk.Read(io.Sector,bytes);
            else if(io.Kind==IoKind.Write) Disk.Write(io.Sector,bytes);
            else Disk.Flush();
            Assert.Equal(CpxError.None,Engine.CompleteIo(io.Token,io.Bytes,true));
        }
        internal void Verify(byte[] expected)
        {
            Engine.BeginPersistenceBarrier(); EngineTests.Drive(ref Engine,Ram,Disk);
            Engine.BeginRemount(); EngineTests.Drive(ref Engine,Ram,Disk);
            var check=new Cpx4Volume<MemoryDevice>(Disk,new byte[49152]); check.Check();
            byte[] actual=new byte[expected.Length]; Assert.Equal(actual.Length,check.Read((ulong)Id,0,actual)); Assert.Equal(expected,actual);
        }
    }

    [Theory][InlineData(512,false)][InlineData(512,true)][InlineData(4096,false)][InlineData(4096,true)]
    public void UnalignedOverwriteUsesOneWindowAndPreservesEverySurroundingByte(int unit,bool catalogue)
    {
        var f=new Fixture(unit,catalogue); int position=unit-3;
        byte[] input=Enumerable.Range(0,769).Select(n=>(byte)(n*23+9)).ToArray(),expected=(byte[])f.Original.Clone();
        input.CopyTo(expected,position); input.CopyTo(f.Ram.Bytes,100000);
        uint scratch=f.Ram.Read32(32+4048),span=(uint)((position%unit+input.Length+unit-1)/unit*unit);
        byte[] afterWindow=f.Ram.Bytes.AsSpan((int)(scratch+3*unit),32).ToArray();
        Assert.Equal(CpxError.None,f.Engine.BeginMappedWrite(f.Id,Wide.From((uint)position),100000,(uint)input.Length));
        var read=f.NeedIo(); Assert.Equal(IoKind.Read,read.Kind); Assert.Equal(span,read.Bytes); f.Complete(read);
        var write=f.NeedIo(); Assert.Equal(IoKind.Write,write.Kind); Assert.Equal(span,write.Bytes); Assert.Equal(read.Sector,write.Sector); f.Complete(write);
        Assert.Equal(StepResult.Complete,f.Engine.Step()); Assert.Equal((uint)input.Length,f.Engine.TransferredBytes);
        Assert.Equal(input,f.Ram.Bytes.AsSpan(100000,input.Length).ToArray());
        Assert.Equal(afterWindow,f.Ram.Bytes.AsSpan((int)(scratch+3*unit),32).ToArray());
        f.Verify(expected);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void FailedOrCancelledWindowReadNeverSubmitsAWrite(bool cancel)
    {
        var f=new Fixture(512,true); byte[] before=(byte[])f.Disk.Bytes.Clone();
        f.Ram.Bytes.AsSpan(100000,769).Fill(0x92);
        Assert.Equal(CpxError.None,f.Engine.BeginMappedWrite(f.Id,Wide.From(509),100000,769));
        var read=f.NeedIo(); Assert.Equal(IoKind.Read,read.Kind);
        if(cancel) f.Engine.Cancel();
        f.Disk.Read(read.Sector,f.Ram.Bytes.AsSpan((int)read.Buffer,(int)read.Bytes));
        Assert.Equal(cancel?CpxError.Cancelled:CpxError.Io,f.Engine.CompleteIo(read.Token,cancel?read.Bytes:0,cancel));
        Assert.Equal(StepResult.Failed,f.Engine.Step()); Assert.Equal(0u,f.Engine.TransferredBytes);
        Assert.Equal(before,f.Disk.Bytes);
    }

    [Fact]
    public void InputAliasingScratchKeepsTheOrdinaryPartialWriteOrdering()
    {
        var f=new Fixture(512,true); uint source=f.Ram.Read32(32+4048)+16;
        byte[] input=Enumerable.Range(0,769).Select(n=>(byte)(n*13+5)).ToArray(),expected=(byte[])f.Original.Clone();
        input.CopyTo(f.Ram.Bytes,(int)source); input.CopyTo(expected,509);
        Assert.Equal(CpxError.None,f.Engine.BeginMappedWrite(f.Id,Wide.From(509),source,769));
        var first=f.NeedIo(); Assert.Equal(IoKind.Read,first.Kind); Assert.Equal(512u,first.Bytes); f.Complete(first);
        EngineTests.Drive(ref f.Engine,f.Ram,f.Disk); f.Verify(expected);
    }
}
