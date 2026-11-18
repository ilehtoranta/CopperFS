using System.Buffers.Binary;
using System.Text;
using CopperCPX;
using CopperNova.Prototype;
using CopperNova.Reference;

namespace CopperNova.Tests;

public class FilesystemTests
{
    [Fact] public void IndependentEmptyImageChecks()=>Assert.Equal(1,NovaImage.Check(NovaImage.Format()).AllocatedInodes);
    [Theory] [InlineData(false)] [InlineData(true)]
    public void WorkloadsPersistAndIndependentlyVerify(bool streaming)
    {
        var h=new Harness(); h.Mount(); h.Workload(streaming);
        var check=NovaImage.Check(h.Disk);
        var file=Assert.Single(check.Entries,e=>e.Path=="/Bench/Renamed.bin");
        Assert.Equal(streaming?2097152:6003,file.Size);
        Assert.Equal(4,check.AllocatedInodes); Assert.Equal(4,h.NonemptyBarriers);
        h.InhibitAndRemount(); h.Verify(streaming);
    }
    [Fact] public void UnsupportedGrowthAndNamesRejectWithoutMetadataMutation()
    {
        var h=new Harness(); h.Mount(); uint handle=h.Open("hello"); h.Write(handle,new byte[1024]);
        Assert.Equal(uint.MaxValue,h.Packet(1022,handle,2048,uint.MaxValue)); Assert.Equal(219u,h.Engine.DosError);
        // A rejected operation may update control scratch; flush must retain the prior file.
        h.Close(handle); h.Flush(); Assert.Equal(1024,Assert.Single(NovaImage.Check(h.Disk).Entries).Size);
        Assert.Equal(0u,h.OpenRaw("ä",1006)); Assert.Equal(210u,h.Engine.DosError);
    }
    [Fact] public void StaleCompletionAndCancellationCannotPublish()
    {
        var h=new Harness(); Assert.Equal(CpxError.None,h.Engine.BeginMount());
        while(h.Engine.Step()!=StepResult.NeedIo) { }
        CpxIo request=h.Engine.Request;
        Assert.Equal(CpxError.StaleCompletion,h.Engine.CompleteIo(request.Token+1,request.Bytes,true)); Assert.True(h.Engine.HasOutstandingIo);
        h.Engine.Cancel(); h.Service(request); h.Drive(); Assert.False(h.Engine.IsMounted);
        Assert.Equal(0u,NovaImage.Check(h.Disk).Generation);
    }
    [Fact] public void ShutdownFlushesUnpublishedTailAndRefusesLiveHandles()
    {
        var h=new Harness(); h.Mount(); uint file=h.Open("tail"); h.Write(file,Enumerable.Repeat((byte)77,1024).ToArray());
        Assert.Equal(0u,h.Packet(5)); Assert.Equal(202u,h.Engine.DosError);
        h.Close(file); Assert.Equal(uint.MaxValue,h.Packet(5));
        Assert.Equal(1024,Assert.Single(NovaImage.Check(h.Disk).Entries).Size); Assert.False(h.Engine.IsMounted);
    }
    [Fact] public void FreedPersistentBlocksAreNotReusedBeforeCleanRetirement()
    {
        var h=new Harness(); h.Mount(); uint first=h.Open("a"); h.Write(first,new byte[8192]); h.Close(first); h.Flush();
        first=h.Open("a",1004); Assert.Equal(0u,h.Packet(1022,first,0,uint.MaxValue)); h.Close(first);
        h.Memory.Write32(4096+8192+180,1280); // A valid first-fit hint back into the quarantined range.
        uint second=h.Open("b"); h.Write(second,Enumerable.Repeat((byte)99,8192).ToArray()); h.Close(second);
        // Prior clean image still owns a's original 16 sectors until the new flush.
        Assert.All(h.Disk.AsSpan(1280*512,8192).ToArray(),b=>Assert.Equal((byte)0,b));
        h.Flush(); Assert.Equal(8192,Assert.Single(NovaImage.Check(h.Disk).Entries,e=>e.Path=="/b").Size);
    }
    [Fact] public void CounterExhaustionRejectsBeginWithoutTakingPacketOwnership()
    {
        var h=new Harness(); h.Mount(); h.Memory.Write32(4096+8192+64,uint.MaxValue);
        byte[] before=(byte[])h.Memory.Bytes.Clone();
        Assert.Equal(CpxError.ResourceExhausted,h.Engine.BeginPacket(Harness.PacketAt));
        Assert.False(h.Engine.Busy); Assert.Equal(before,h.Memory.Bytes);
    }
    [Fact] public void ExtentCapacityRejectsBeforeOverwritingTheNextRun()
    {
        var h=new Harness(); h.Mount(); uint a=h.Open("a"),b=h.Open("b");
        for(int i=0;i<8;i++) { h.Write(a,Enumerable.Repeat((byte)17,512).ToArray()); h.Write(b,Enumerable.Repeat((byte)34,512).ToArray()); }
        Assert.Equal(uint.MaxValue,h.Packet(87,a,Harness.Data,512)); Assert.Equal(221u,h.Engine.DosError);
        h.Close(a); h.Close(b); h.Flush();
        Assert.All(NovaImage.Check(h.Disk).Entries,e=>Assert.Equal(4096,e.Size));
    }
    [Fact] public void PrivatePacketAliasRejectsWithoutChangingAnyMountBytes()
    {
        var h=new Harness(); h.Mount(); byte[] before=(byte[])h.Memory.Bytes.Clone();
        Assert.Equal(CpxError.InvalidArgument,h.Engine.BeginPacket(4096+8192));
        Assert.False(h.Engine.Busy); Assert.Equal(before,h.Memory.Bytes);
    }
    [Fact] public void PrivateDataAndFileHandleAliasesPreserveEarlierBufferedWrites()
    {
        var h=new Harness(); h.Mount(); uint file=h.Open("kept"); h.Write(file,new byte[512]);
        int requests=0; h.BeforeIo=_=>requests++;
        Assert.Equal(uint.MaxValue,h.Packet(87,file,4096+90112,512)); Assert.Equal(115u,h.Engine.DosError);
        Assert.Equal(uint.MaxValue,h.Packet(82,file,4096+16384,512)); Assert.Equal(115u,h.Engine.DosError);
        h.Bstr(Harness.NameAt,"bad-handle");
        Assert.Equal(0u,h.Packet(1006,(4096+90112)>>2,0,Harness.NameAt>>2)); Assert.Equal(115u,h.Engine.DosError);
        Assert.Equal(0,requests); h.Close(file); h.Flush();
        var entry=Assert.Single(NovaImage.Check(h.Disk).Entries); Assert.Equal("/kept",entry.Path); Assert.Equal(512,entry.Size);
    }
    [Theory] [InlineData(1)] [InlineData(511)] [InlineData(513)] [InlineData(65535)]
    public void PartialNewSectorsAndUnalignedOverwriteKeepUnwrittenTailsPrivate(int length)
    {
        var h=new Harness(); h.Mount(); uint file=h.Open("partial");
        byte[] bytes=Enumerable.Range(0,length).Select(i=>(byte)(i*7+1)).ToArray(); h.Write(file,bytes);
        Assert.Equal((uint)length,h.Packet(1008,file,0,uint.MaxValue));
        byte[] patch=Enumerable.Repeat((byte)199,Math.Min(length,769)).ToArray(); h.Write(file,patch); patch.CopyTo(bytes,0);
        h.Close(file); h.Flush();
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),Assert.Single(NovaImage.Check(h.Disk).Entries).Sha256);
    }

    internal sealed class BitmapReads
    {
        public bool Enabled; public long ByteReads,WordReads;
        public void Observe(byte[] memory,uint address,bool word)
        {
            const uint first=4096+16384;
            if(!Enabled||address<first||address>=first+32768) return;
            uint within=(address-first)&511;
            if(within<128||within>=384||memory[address-within+7]!=4) return;
            if(word) WordReads++; else ByteReads++;
        }
    }
    internal readonly struct Ram(byte[] bytes,BitmapReads? bitmapReads=null) : ICpxMemory
    {
        public byte[] Bytes=>bytes;
        public byte Read8(uint p) { bitmapReads?.Observe(bytes,p,false); return bytes[p]; } public void Write8(uint p,byte value)=>bytes[p]=value;
        public uint Read16(uint p)=>BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan((int)p,2));
        public void Write16(uint p,uint value)=>BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan((int)p,2),(ushort)value);
        public uint Read32(uint p) { bitmapReads?.Observe(bytes,p,true); return BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan((int)p,4)); }
        public void Write32(uint p,uint value)=>BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan((int)p,4),value);
        public void Copy(uint to,uint from,uint count)=>bytes.AsSpan((int)from,(int)count).CopyTo(bytes.AsSpan((int)to,(int)count));
    }
    internal sealed class Harness
    {
        public byte[] Disk; public Ram Memory; public NovaEngine<Ram> Engine;
        public int NonemptyBarriers;
        public Action<CpxIo>? BeforeIo;
        public const uint PacketAt=0x21000,NameAt=0x22000,SecondName=0x22400,FileHandle=0x23000,Data=0x30000;
        public Harness(byte[]? disk=null,BitmapReads? bitmapReads=null)
        { Disk=disk??NovaImage.Format(); Memory=new(new byte[1048576],bitmapReads); Engine=new(Memory,4096); Engine.Bind((uint)Disk.Length/512,0x800,0x900); }
        public void Mount() { Assert.Equal(CpxError.None,Engine.BeginMount()); Drive(); Assert.True(Engine.IsMounted); }
        public void Service(CpxIo io)
        {
            BeforeIo?.Invoke(io);
            int offset=checked((int)io.Sector.Low*512);
            if(io.Kind==IoKind.Read) Disk.AsSpan(offset,(int)io.Bytes).CopyTo(Memory.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes));
            else if(io.Kind==IoKind.Write) Memory.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes).CopyTo(Disk.AsSpan(offset,(int)io.Bytes));
            else if(io.Kind==IoKind.Flush&&Engine.DirtyPages!=0) NonemptyBarriers++;
            Engine.CompleteIo(io.Token,io.Bytes,true);
        }
        public void Drive()
        {
            int steps=0;
            while(Engine.Busy)
            {
                Assert.True(++steps<2000000,"Unbounded continuation");
                StepResult result=Engine.Step(); if(result==StepResult.NeedIo) Service(Engine.Request);
            }
        }
        public uint Packet(uint action,uint a=0,uint b=0,uint c=0,uint d=0,uint e=0)
        {
            Memory.Write32(PacketAt+8,action); Memory.Write32(PacketAt+20,a); Memory.Write32(PacketAt+24,b); Memory.Write32(PacketAt+28,c); Memory.Write32(PacketAt+32,d); Memory.Write32(PacketAt+36,e);
            Assert.Equal(CpxError.None,Engine.BeginPacket(PacketAt)); Drive(); return Engine.Result;
        }
        public void Bstr(uint p,string name) { byte[] bytes=Encoding.UTF8.GetBytes(name); Memory.Write8(p,(byte)bytes.Length); bytes.CopyTo(Memory.Bytes,(int)p+1); }
        public uint OpenRaw(string name,uint action) { Bstr(NameAt,name); return Packet(action,FileHandle>>2,0,NameAt>>2); }
        public uint Open(string name,uint action=1006) { Assert.Equal(uint.MaxValue,OpenRaw(name,action)); return Memory.Read32(FileHandle+36); }
        public void Close(uint handle)=>Assert.Equal(uint.MaxValue,Packet(1007,handle));
        public void Write(uint handle,byte[] bytes) { bytes.CopyTo(Memory.Bytes,Data); Assert.Equal((uint)bytes.Length,Packet(87,handle,Data,(uint)bytes.Length)); }
        public void Flush()=>Assert.Equal(uint.MaxValue,Packet(27));
        public void InhibitAndRemount() { Assert.Equal(uint.MaxValue,Packet(31,1)); Assert.Equal(uint.MaxValue,Packet(31,0)); }
        public static byte Initial(int i)=>(byte)(i*17+(i>>9)*31+3);
        public static byte Expected(int i,bool streaming)=>i>=4093&&i<4862?(byte)((i-4093)*13+5):i>=1023&&i<2047?(byte)((i-1023)*23+9):Initial(streaming?i&65535:i);
        public void Workload(bool streaming)
        {
            Bstr(NameAt,"CPX0:Bench"); uint dir=Packet(22,0,NameAt>>2); Assert.NotEqual(0u,dir); Assert.Equal(uint.MaxValue,Packet(15,dir));
            uint file=Open("CPX0:Bench/Data.bin");
            byte[] data=Enumerable.Range(0,streaming?65536:8192).Select(Initial).ToArray();
            for(int i=0;i<(streaming?32:1);i++) Write(file,data);
            Assert.NotEqual(uint.MaxValue,Packet(1008,file,1023,uint.MaxValue)); Write(file,Enumerable.Range(0,1024).Select(i=>(byte)(i*23+9)).ToArray());
            Assert.NotEqual(uint.MaxValue,Packet(1008,file,4093,uint.MaxValue)); Write(file,Enumerable.Range(0,769).Select(i=>(byte)(i*13+5)).ToArray());
            uint size=streaming?2097152u:6003u;
            Assert.Equal(size,Packet(1022,file,size,uint.MaxValue)); Assert.Equal(size,Packet(1022,file,size,uint.MaxValue)); Close(file);
            Bstr(NameAt,"CPX0:Bench/Data.bin"); Bstr(SecondName,"CPX0:Bench/Renamed.bin"); Assert.Equal(uint.MaxValue,Packet(17,0,NameAt>>2,0,SecondName>>2));
            file=Open("CPX0:Bench/Temp.bin"); Write(file,Enumerable.Repeat((byte)0x5a,1024).ToArray()); Close(file); Flush();
        }
        public void Verify(bool streaming)
        {
            uint file=Open("CPX0:Bench/Renamed.bin",1005); int size=streaming?2097152:6003;
            for(int offset=0;offset<size;offset+=65536)
            {
                uint count=(uint)Math.Min(65536,size-offset); Assert.Equal(count,Packet(82,file,Data,count));
                for(uint i=0;i<count;i++) Assert.Equal(Expected(offset+(int)i,streaming),Memory.Read8(Data+i));
            }
            Close(file);
        }
    }
}
