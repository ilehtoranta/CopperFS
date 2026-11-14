using Amiga;
using System.Buffers.Binary;
using Copper68k;
using CopperSharp.Compiler;
using CopperSharp.Targets.Amiga;
using CopperCPX;
using CopperFS.Cpx4.Reference;

namespace CopperCPX.InstructionTests;

public struct GuestMemory : ICpxMemory
{
    public uint Reserved;
    public byte Read8(uint p)=>APTR.ReadUInt8(APTR.FromPointer(p),0);
    public uint Read32(uint p)=>APTR.ReadUInt32(APTR.FromPointer(p),0);
    public void Write8(uint p,byte v)=>APTR.WriteUInt8(APTR.FromPointer(p),0,v);
    public void Write32(uint p,uint v)=>APTR.WriteUInt32(APTR.FromPointer(p),0,v);
}
public static class MountProbe
{
    public static uint Entry()
    {
        var memory=new GuestMemory(); var engine=new CpxEngine<GuestMemory>(memory,0x40000);
        if(engine.Initialize(512,512,Wide.From(8192),1)!=CpxError.None) return 1;
        if(engine.BeginOperation(CpxOperation.Mount)!=CpxError.None) return 2;
        uint phase=0;
        for(uint n=0;n<2000;n++)
        {
            var result=engine.Step(); if(result==StepResult.Failed) return 100+(uint)engine.Error;
            if(result==StepResult.Complete)
            {
                if(!engine.IsMounted) return 3;
                CpxError start;
                if(phase==0) start=engine.BeginOperation(CpxOperation.BeginTransaction);
                else if(phase==1) start=engine.BeginOperation(CpxOperation.ReadMetadata,Wide.From(4096));
                else if(phase==2) { memory.Write32(engine.ReadBuffer+64+72,0x1234); start=engine.BeginOperation(CpxOperation.StageMetadata,Wide.From(4096),engine.ReadBuffer); }
                else if(phase==3) start=engine.BeginOperation(CpxOperation.CommitTransaction);
                else return engine.Transaction.Low==1?0u:6u;
                if(start!=CpxError.None) return 7;
                phase++;
            }
            if(result==StepResult.NeedIo)
            {
                var io=engine.Request;
                memory.Write32(0x8004,io.Sector.High); memory.Write32(0x8008,io.Sector.Low);
                memory.Write32(0x800c,io.Buffer); memory.Write32(0x8010,io.Bytes);
                memory.Write32(0x8000,(uint)io.Kind+1);
                if(engine.CompleteIo(io.Token,io.Bytes,true)!=CpxError.None) return 4;
            }
        }
        return 5;
    }
}
public class EngineInstructions(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)] public void CompiledEngineAndTd64RespectFourKiBStack(int mode)
    {
        bool unmap=mode>=3;
        Type probe=unmap?typeof(UnmapProbe):mode==2?typeof(NativeIoProbe):mode==1?typeof(Td64Probe):typeof(MountProbe);
        var compiled=AmigaM68kCompiler.Compile(new M68kCompilationRequest {
            AssemblyPath=probe.Assembly.Location, EntryPoint=probe.FullName+"::Entry",
            ManagedAssemblyPaths=[typeof(CpxArena).Assembly.Location,typeof(APTR).Assembly.Location],
            IncludedExportNames=[],Cpu=M68kCpuTarget.M68000,OutputFormat=M68kOutputFormat.Assembly,
            RuntimeProfile=M68kRuntimeProfile.Freestanding,MemoryManagement=M68kMemoryManagement.None,ExceptionMode=M68kExceptionMode.Yolo });
        Assert.True(compiled.FrameworkAnalysis.IsCompatible); Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        var bus=new Bus(); Cpx4Volume<Bus>.Format(bus,512,new byte[36864],12345);
        compiled.Code.CopyTo(bus.Memory,0x10000);
        foreach(var relocation in compiled.Relocations) { uint at=0x10000u+(uint)relocation.Offset; bus.Put(at,bus.Get(at)+0x10000); }
        bus.Memory.AsSpan(0xee000,8192).Fill(0xa5); bus.Put(0xf0000,0xf1000);
        bus.Put(4,0x9000); int polls=0,submits=0,reaps=0; bool aborted=false;
        if(unmap) { bus.Put(0x8300,mode==3?512u:4096u); bus.Put(0x8304,mode==5?1u:0u); }
        using var cpu=M68kCoreFactory.Default.Create(M68kCpuModel.M68000,bus); cpu.Reset(0x10000+compiled.EntryPoint,0xf0000);
        uint minimum=0xf0000; int instructions=0;
        while(cpu.State.ProgramCounter!=0xf1000&&instructions<100_000_000)
        {
            uint pc=cpu.State.ProgramCounter;
            if((mode==2||unmap) && pc is (0x9000-462) or (0x9000-468) or (0x9000-474) or (0x9000-480))
            {
                Assert.Equal(0x8200u,cpu.State.A[1]); uint request=cpu.State.A[1];
                if(pc==0x9000-462)
                {
                    submits++; polls=0; aborted=false; Assert.Equal(1u,bus.Get(unmap?0x40800u:0x8400u));
                    Assert.Equal(0,bus.Memory[request+30]); Assert.Equal(0,bus.Memory[request+31]);
                    ushort command=BinaryPrimitives.ReadUInt16BigEndian(bus.Memory.AsSpan((int)request+28));
                    if(unmap)
                    {
                        Assert.Equal(28,command); Assert.Equal(0u,bus.Get(request+32)); Assert.Equal(30u,bus.Get(request+36)); Assert.Equal(0x40a00u,bus.Get(request+40));
                        Assert.Equal(submits<=2?0x12:submits==3?0x9e:0x42,bus.Memory[0x40a20]);
                        if(submits==4) { Assert.Equal(1u,bus.Get(0x40a58)); Assert.Equal(2u,bus.Get(0x40a5c)); Assert.Equal(1u,bus.Get(0x40a60)); }
                    }
                    else
                    {
                        Assert.Equal(submits==2?4:25,command); Assert.Equal(submits==2?0u:1u,bus.Get(request+32));
                        Assert.Equal(submits==2?0u:512u,bus.Get(request+36)); Assert.Equal(submits==2?0u:0x2000u,bus.Get(request+40));
                        Assert.Equal(submits==2?0u:512u,bus.Get(request+44));
                    }
                }
                else if(pc==0x9000-468)
                {
                    cpu.State.D[0]=++polls==1?0u:request;
                    if(polls>1)
                    {
                        bus.Put(request+32,aborted?0u:bus.Get(request+36)); bus.Memory[request+31]=aborted?(byte)0xfe:(byte)0;
                        if(unmap)
                        {
                            BinaryPrimitives.WriteUInt16BigEndian(bus.Memory.AsSpan(0x40a12),submits<=2?(ushort)6:submits==3?(ushort)16:(ushort)10);
                            bus.Put(0x40a08,bus.Get(0x40a04));
                            if(submits==1) { bus.Memory[0x40a51]=0xb2; bus.Memory[0x40a53]=4; bus.Memory[0x40a55]=128; }
                            if(submits==2) { bus.Memory[0x40a51]=0xb0; bus.Memory[0x40a53]=24; bus.Put(0x40a64,4096); bus.Put(0x40a68,1); }
                            if(submits==3) { bus.Put(0x40a50,2); bus.Put(0x40a54,0); bus.Put(0x40a58,mode==4?4096u:512u); }
                        }
                    }
                }
                else if(pc==0x9000-474) { reaps++; cpu.State.D[0]=aborted?0xfffffffeu:0; }
                else aborted=true;
                cpu.State.ProgramCounter=bus.Get(cpu.State.A[7]); cpu.State.A[7]+=4;
            }
            else cpu.ExecuteInstruction();
            instructions++; minimum=Math.Min(minimum,cpu.State.A[7]); Assert.InRange(cpu.State.A[7],0xef000u,0xf0004u); Assert.False(cpu.State.Halted||cpu.State.Stopped);
        }
        Assert.Equal(0xf1000u,cpu.State.ProgramCounter); Assert.Equal(0u,cpu.State.D[0]);
        if(mode==0) { Assert.True(bus.Requests>30); Assert.Equal(0x1234u,BinaryPrimitives.ReadUInt32BigEndian(bus.Disk.AsSpan(2097152+64+72))); }
        if(mode==2) { Assert.Equal(3,submits); Assert.Equal(3,reaps); }
        if(unmap) { Assert.Equal(mode==5?3:4,submits); Assert.Equal(submits,reaps); }
        Assert.All(bus.Memory.AsSpan(0xee000,4096).ToArray(),b=>Assert.Equal(0xa5,b));
        output.WriteLine($"{probe.Name}: {compiled.Code.Length} code bytes, {instructions} instructions, {0xf0000-minimum} stack bytes, {bus.Requests} external I/O boundaries. This is not DOS handler coverage.");
    }
    private sealed class Bus : IM68kBus,IBlockDevice
    {
        internal byte[] Memory=new byte[0x100000],Disk=new byte[4*1024*1024]; internal int Requests;
        public int SectorSize=>512; public ulong SectorCount=>8192;
        public void Read(ulong s,Span<byte> b)=>Disk.AsSpan((int)s*512,b.Length).CopyTo(b);
        public void Write(ulong s,ReadOnlySpan<byte> b)=>b.CopyTo(Disk.AsSpan((int)s*512)); public void Flush() { }
        internal uint Get(uint a)=>BinaryPrimitives.ReadUInt32BigEndian(Memory.AsSpan((int)a));
        internal void Put(uint a,uint v)=>BinaryPrimitives.WriteUInt32BigEndian(Memory.AsSpan((int)a),v);
        public void ResetExternalDevices(long c) { }
        public byte ReadByte(uint a,ref long c,M68kBusAccessKind k)=>Memory[a];
        public ushort ReadWord(uint a,ref long c,M68kBusAccessKind k)=>BinaryPrimitives.ReadUInt16BigEndian(Memory.AsSpan((int)a));
        public uint ReadLong(uint a,ref long c,M68kBusAccessKind k)=>Get(a);
        public void WriteByte(uint a,byte v,ref long c,M68kBusAccessKind k)=>Memory[a]=v;
        public void WriteWord(uint a,ushort v,ref long c,M68kBusAccessKind k)=>BinaryPrimitives.WriteUInt16BigEndian(Memory.AsSpan((int)a),v);
        public void WriteLong(uint a,uint v,ref long c,M68kBusAccessKind k)
        {
            Put(a,v); if(a!=0x8000) return; Requests++; Assert.Equal(0u,Get(0x8004));
            if(v==1) Read(Get(0x8008),Memory.AsSpan((int)Get(0x800c),(int)Get(0x8010)));
            else if(v==2) Write(Get(0x8008),Memory.AsSpan((int)Get(0x800c),(int)Get(0x8010)));
            else Assert.Equal(3u,v);
        }
    }
}

public static class UnmapProbe
{
    public static uint Entry()
    {
        uint sector=APTR.ReadUInt32(APTR.FromPointer(0x8300),0);
        bool mismatch=APTR.ReadUInt32(APTR.FromPointer(0x8300),4)!=0;
        CopperCPX.Amiga.NativeCpxTransport.Initialize(0x40000,APTR.FromPointer(0x8200),new(sector,0),Wide.From(8192),65536,0xfffffffe,true,sector);
        var io=new CpxIo { Kind=IoKind.Discard,Sector=Wide.From(2),Bytes=sector,Token=123 };
        for(uint n=0;n<100;n++)
        {
            var result=CopperCPX.Amiga.NativeCpxTransport.Service(0x40000,io,out uint completed,out bool unsupported);
            if(result==StepResult.Failed) return 1;
            if(result==StepResult.Complete)
                return unsupported==mismatch&&completed==(mismatch?0u:sector)?0u:2u;
        }
        return 3;
    }
}

public static class Td64Probe
{
    public static uint Entry()
    {
        var transfer=new Td64Transfer();
        if(transfer.Begin(new(0,0xfffffe00),Wide.From(100),default,9,2048,0x10000,512,0xfffffffe,0x2000,4096,true,true)!=CpxError.None) return 1;
        for(uint i=0;i<4;i++)
        {
            if(transfer.Next(out var request)!=StepResult.NeedIo||request.Command!=25||request.Length!=512) return 2;
            if(request.OffsetHigh!=(i==0?0u:1u)||request.ActualInput!=request.OffsetHigh||request.OffsetLow!=(i==0?0xfffffe00u:(i-1)*512)) return 3;
            if(transfer.Complete(512,0)!=CpxError.None) return 4;
        }
        if(transfer.CompletedBytes!=2048||transfer.Next(out _)!=StepResult.Complete) return 5;
        if(transfer.Begin(new(0,0xfffffe00),Wide.From(100),default,9,1024,0x10000,512,0xfffffffe,0x2000,4096,false,false)!=CpxError.Unsupported) return 6;
        if(transfer.Next(out _)!=StepResult.Failed) return 7;
        if(transfer.Begin(default,Wide.From(100),default,9,8192,0x10001,8192,0xfffffffe,0x2000,4096,false,true)!=CpxError.None) return 8;
        if(transfer.Next(out var bounce)!=StepResult.NeedIo||!bounce.Bounced||bounce.Length!=4096||bounce.Buffer!=0x2000) return 9;
        if(transfer.Complete(512,0)!=CpxError.Io||transfer.CompletedBytes!=0||transfer.Next(out _)!=StepResult.Failed) return 10;
        if(!Td64Transfer.NarrowPartitionFits(new(0,0xfffffe00),Wide.From(1),9)) return 11;
        if(Td64Transfer.NarrowPartitionFits(new(0,0xfffffe00),Wide.From(2),9)) return 12;
        if(Td64Transfer.NarrowPartitionFits(new(1,0),Wide.From(1),9)) return 13;
        return 0;
    }
}
