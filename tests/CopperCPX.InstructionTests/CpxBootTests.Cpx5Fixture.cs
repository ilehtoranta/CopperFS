using System.Buffers.Binary;
using CopperFS.Cpx4.Reference;

namespace CopperCPX.InstructionTests;

public partial class CpxBootTests
{
    private struct FixtureMemory(byte[] bytes) : ICpxMemory
    {
        internal readonly byte[] Bytes=bytes;
        public byte Read8(uint at)=>Bytes[at];
        public uint Read16(uint at)=>BinaryPrimitives.ReadUInt16BigEndian(Bytes.AsSpan((int)at));
        public uint Read32(uint at)=>BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan((int)at));
        public void Write8(uint at,byte value)=>Bytes[at]=value;
        public void Write16(uint at,uint value)=>BinaryPrimitives.WriteUInt16BigEndian(Bytes.AsSpan((int)at),(ushort)value);
        public void Write32(uint at,uint value)=>BinaryPrimitives.WriteUInt32BigEndian(Bytes.AsSpan((int)at),value);
        public void Copy(uint destination,uint source,uint count)=>CpxMemoryCopy.Forward(ref this,destination,source,count);
    }

    private static void FormatCpx5Allocation(Image disk)
    {
        Cpx4Volume<Image>.FormatDirectInodes(disk,512,new byte[49152],12345,true,true);
        var memory=new FixtureMemory(new byte[1000000]);
        var engine=new CpxEngine<FixtureMemory>(memory,32);
        Assert.Equal(CpxError.None,engine.Initialize(512,512,disk.SectorCount,1,metadataPageBytes:512));
        Assert.Equal(CpxError.None,engine.EnableBufferedWrites(300000,CpxEngine<FixtureMemory>.BufferedStorageBytes(512)));
        Assert.Equal(CpxError.None,engine.EnableShadowVolume(900000,32768,5));
        void Drive(CpxError error)
        {
            Assert.Equal(CpxError.None,error);
            for(int turn=0;turn<20000;turn++)
            {
                var step=engine.Step(); if(step==StepResult.Complete) return;
                Assert.NotEqual(StepResult.Failed,step); if(step!=StepResult.NeedIo) continue;
                var io=engine.Request; var data=memory.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes);
                if(io.Kind==IoKind.Read) disk.Read(io.Sector,data);
                else if(io.Kind==IoKind.Write) disk.Write(io.Sector,data);
                else disk.Flush();
                Assert.Equal(CpxError.None,engine.CompleteIo(io.Token,io.Bytes,true));
            }
            Assert.Fail("CPX5 allocation fixture did not terminate.");
        }
        Drive(engine.BeginOperation(CpxOperation.Mount));
        // CPX5's independent reference is a decoder. Build through the portable
        // engine, then validate ownership independently before native boot.
        // Both aliases here are long Latin-1 names; UTF-8 import remains covered
        // by the separate legacy fixture and is not claimed by this ROM gate.
        byte[][] names=["Inline"u8.ToArray(),"Payload"u8.ToArray(),"Wide"u8.ToArray(),"Extents"u8.ToArray(),
            "Unwritten"u8.ToArray(),"EmptyTree"u8.ToArray(),[0xe9,0xc4],
            System.Text.Encoding.ASCII.GetBytes(new string('q',108)),System.Text.Encoding.ASCII.GetBytes(new string('r',108))];
        for(int n=0;n<names.Length;n++)
        {
            names[n].CopyTo(memory.Bytes,100000);
            Drive(engine.BeginCreate(Wide.From(1),100000,(uint)names[n].Length));
            var id=engine.MutatedObject;
            if(n==0)
            {
                "native comment"u8.CopyTo(memory.Bytes.AsSpan(100000));
                Drive(engine.BeginComment(id,100000,14));
            }
            if(n==1)
            {
                for(int offset=0;offset<4096;offset++) memory.Bytes[100000+offset]=(byte)(offset*7+11);
                Drive(engine.BeginMappedWrite(id,default,100000,4096));
            }
        }
        Drive(engine.BeginPersistenceBarrier());
        new Cpx4Volume<Image>(disk,new byte[Cpx4Volume<Image>.RequiredScratchBytes(4096,true)]).Check();
    }
}
