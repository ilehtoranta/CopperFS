using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CopperCPX;
using CopperNova.Prototype;
using CopperNova.Reference;

namespace CopperNova.CacheBench;

// Host-only experiment. The native engine still owns no managed allocations.
public sealed class CachePressureScenario
{
    private const uint Arena=4096, PacketAt=0x21000, NameAt=0x22000, Fh=0x23000, Data=0x30000;
    public byte[] Disk=NovaImage.Format();
    private readonly Ram memory=new(new byte[1048576]);
    private NovaEngine<Ram> engine;
    private uint nextResource=0xe0000;
    private readonly Stack<uint> freeResources=new();
    private int metadataReads,dataReads,writes,barriers,steps;
    public CachePressureScenario() { engine=new(memory,Arena); engine.Bind((uint)Disk.Length/512,0x800,0x900); }
    public void UseSeed(byte[] disk)
    { Disk=disk; Require(engine.BeginMount()==CpxError.None); Drive(); NovaImage.Check(Disk); }
    private static void Require(bool ok) { if(!ok) throw new InvalidDataException("Cache experiment correctness failure"); }
    private void Drive()
    {
        int bound=0;
        while(engine.Busy)
        {
            Require(++bound<2000000); steps++;
            if(engine.Step()!=StepResult.NeedIo) continue;
            var io=engine.Request; int offset=checked((int)io.Sector.Low*512);
            if(io.Kind==IoKind.Read)
            {
                if(io.Sector.Low<NovaEngine<Ram>.FirstData) metadataReads+=(int)io.Bytes/512;
                else dataReads+=(int)io.Bytes/512;
                Disk.AsSpan(offset,(int)io.Bytes).CopyTo(memory.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes));
            }
            else if(io.Kind==IoKind.Write)
            { writes++; memory.Bytes.AsSpan((int)io.Buffer,(int)io.Bytes).CopyTo(Disk.AsSpan(offset,(int)io.Bytes)); }
            else barriers++;
            Require(engine.CompleteIo(io.Token,io.Bytes,true)==CpxError.None);
        }
        Require(engine.DosError==0);
    }
    private uint Packet(uint action,uint a=0,uint b=0,uint c=0)
    {
        memory.Write32(PacketAt+8,action); memory.Write32(PacketAt+20,a); memory.Write32(PacketAt+24,b); memory.Write32(PacketAt+28,c);
        uint resource=0;
        if(NovaEngine<Ram>.NeedsPacketResource(action)&&!freeResources.TryPop(out resource))
        {
            resource=nextResource;
            nextResource+=NovaEngine<Ram>.PacketResourceBytes;
        }
        Require(engine.BeginPacket(PacketAt,resource)==CpxError.None);
        Drive();
        if(engine.ReleasedPacketResource!=0) freeResources.Push(engine.ReleasedPacketResource);
        return engine.Result;
    }
    private void Name(string value)
    { byte[] text=Encoding.ASCII.GetBytes(value); memory.Write8(NameAt,(byte)text.Length); text.CopyTo(memory.Bytes,(int)NameAt+1); }
    private void Mkdir(string value)
    { Name(value); uint id=Packet(22,0,NameAt>>2); Require(id!=0); Require(Packet(15,id)==uint.MaxValue); }
    private uint Open(string value,uint action)
    { Name(value); Require(Packet(action,Fh>>2,0,NameAt>>2)==uint.MaxValue); return memory.Read32(Fh+36); }
    private void Close(uint handle)=>Require(Packet(1007,handle)==uint.MaxValue);
    private void ReadFile(int a,int b,int c)
    {
        uint handle=Open($"d{a}/s{b}/f{c}",1005);
        Require(Packet(82,handle,Data,1)==1); Require(memory.Read8(Data)==(byte)(a*12+b*2+c)); Close(handle);
    }
    public void Seed()
    {
        Require(engine.BeginMount()==CpxError.None); Drive();
        for(int a=0;a<6;a++)
        {
            Mkdir($"d{a}");
            for(int b=0;b<6;b++)
            {
                Mkdir($"d{a}/s{b}");
                for(int c=0;c<2;c++)
                {
                    uint handle=Open($"d{a}/s{b}/f{c}",1006);
                    memory.Write8(Data,(byte)(a*12+b*2+c)); Require(Packet(87,handle,Data,1)==1); Close(handle);
                }
            }
        }
        Require(Packet(27)==uint.MaxValue);
        var check=NovaImage.Check(Disk); Require(check.AllocatedInodes==115);
        for(int a=0;a<6;a++) for(int b=0;b<6;b++) for(int c=0;c<2;c++)
        {
            var entry=check.Entries.Single(e=>e.Path==$"/d{a}/s{b}/f{c}");
            Require(entry.Size==1&&entry.Sha256==Convert.ToHexString(SHA256.HashData(new byte[] { (byte)(a*12+b*2+c) })));
        }
    }
    public object Measure()
    {
        Require(Packet(31,1)==uint.MaxValue); Require(Packet(31,0)==uint.MaxValue);
        metadataReads=dataReads=writes=barriers=steps=0;
        // Six hot paths interspersed with a scan of a working set larger than 64 pages.
        for(int round=0;round<3;round++)
            for(int a=0;a<6;a++) for(int b=0;b<6;b++) for(int c=0;c<2;c++)
            {
                ReadFile(a,b,c);
                for(int hot=0;hot<6;hot++) ReadFile(hot,0,0);
            }
        Require(writes==0&&barriers==0);
        return new { Rounds=3,FileReads=1512,MetadataSectorReads=metadataReads,MetadataValidationBytes=metadataReads*512,
            DataSectorReads=dataReads,WriteRequests=writes,Flushes=barriers,ContinuationSteps=steps,
            ArenaBytes=NovaEngine<Ram>.ArenaBytes,SeedSha256=Convert.ToHexString(SHA256.HashData(Disk)),Passed=true };
    }
    private readonly struct Ram(byte[] bytes) : ICpxMemory
    {
        public byte[] Bytes=>bytes;
        public byte Read8(uint p)=>bytes[p]; public void Write8(uint p,byte v)=>bytes[p]=v;
        public uint Read16(uint p)=>BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan((int)p,2));
        public void Write16(uint p,uint v)=>BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan((int)p,2),(ushort)v);
        public uint Read32(uint p)=>BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan((int)p,4));
        public void Write32(uint p,uint v)=>BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan((int)p,4),v);
        public void Copy(uint to,uint from,uint n)=>bytes.AsSpan((int)from,(int)n).CopyTo(bytes.AsSpan((int)to,(int)n));
    }
}
