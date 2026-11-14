using Amiga;
using Copper68k;
using CopperSharp.Compiler;
using CopperSharp.Targets.Amiga;
using CopperFS.Cpx4.Reference;
using System.Buffers.Binary;

namespace CopperCPX.InstructionTests;

public static class ExtendedCoreProbe
{
    public static uint Entry()
    {
        var m=new GuestMemory(); var e=new CpxEngine<GuestMemory>(m,0x40000);
        if(e.BeginFormat(512,512,Wide.From(8192),Wide.From(12345),1)!=CpxError.None) return 1;
        m.Write8(0x9000,102); m.Write8(0x9010,108); m.Write8(0x9020,104);
        for(uint n=0;n<200;n++) m.Write8(0x9100+n,0xe9);
        uint phase=0;
        for(uint steps=0;steps<20000;steps++)
        {
            var result=e.Step(); if(result==StepResult.Failed) return 1000+phase*32+(uint)e.Error;
            if(result==StepResult.Complete)
            {
                CpxError start;
                switch(phase)
                {
                    case 0: start=e.BeginCreate(Wide.From(1),0x9000,1); break;
                    case 1: start=e.BeginMappedWrite(Wide.From(2),new(1,13),0x9100,1); break;
                    case 2: start=e.BeginSymlink(Wide.From(1),0x9010,1,0x9100,200); break;
                    case 3: start=e.BeginHardLink(Wide.From(2),Wide.From(1),0x9020,1); break;
                    case 4: start=e.BeginComment(Wide.From(2),0x9100,79); break;
                    case 5: start=e.BeginReadSymlink(Wide.From(3),0x9200,768); break;
                    case 6:
                        if(e.TransferredBytes!=400) return 2;
                        for(uint n=0;n<400;n++) if(m.Read8(0x9200+n)!=(n%2==0?0xc3:0xa9)) return 3;
                        start=e.BeginFileRead(Wide.From(2),new(1,0),0x9200,14); break;
                    case 7:
                        if(e.TransferredBytes!=14||m.Read8(0x920d)!=0xe9) return 4;
                        for(uint n=0;n<13;n++) if(m.Read8(0x9200+n)!=0) return 5;
                        start=e.BeginDelete(Wide.From(1),0x9000,1); break;
                    case 8: start=e.BeginDelete(Wide.From(1),0x9020,1); break;
                    case 9: start=e.BeginDelete(Wide.From(1),0x9010,1); break;
                    default: return 0;
                }
                if(start!=CpxError.None) return 2000+phase*32+(uint)start;
                phase++; m.Write32(0x8020,phase);
            }
            if(result==StepResult.NeedIo)
            {
                var io=e.Request; m.Write32(0x8004,io.Sector.High); m.Write32(0x8008,io.Sector.Low);
                m.Write32(0x800c,io.Buffer); m.Write32(0x8010,io.Bytes); m.Write32(0x8000,(uint)io.Kind+1);
                if(e.CompleteIo(io.Token,io.Bytes,true)!=CpxError.None) return 6;
            }
        }
        return 7;
    }
}

public class ExtendedCoreInstructions(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact] public void CompiledFormatterSparseWritesCommentsAndLinksKeepFourKiBStack()
    {
        var compiled=AmigaM68kCompiler.Compile(new M68kCompilationRequest {
            AssemblyPath=typeof(ExtendedCoreProbe).Assembly.Location, EntryPoint=typeof(ExtendedCoreProbe).FullName+"::Entry",
            ManagedAssemblyPaths=[typeof(CpxArena).Assembly.Location,typeof(APTR).Assembly.Location],IncludedExportNames=[],
            Cpu=M68kCpuTarget.M68000,OutputFormat=M68kOutputFormat.Assembly,RuntimeProfile=M68kRuntimeProfile.Freestanding,
            MemoryManagement=M68kMemoryManagement.None,ExceptionMode=M68kExceptionMode.Yolo });
        Assert.True(compiled.FrameworkAnalysis.IsCompatible); Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        var bus=new Bus(); compiled.Code.CopyTo(bus.Memory,0x10000);
        foreach(var relocation in compiled.Relocations) { uint at=0x10000u+(uint)relocation.Offset; bus.Put(at,bus.Get(at)+0x10000); }
        bus.Memory.AsSpan(0xee000,8192).Fill(0xa5); bus.Put(0xf0000,0xf1000);
        using var cpu=M68kCoreFactory.Default.Create(M68kCpuModel.M68000,bus); cpu.Reset(0x10000+compiled.EntryPoint,0xf0000);
        uint minimum=0xf0000; int instructions=0;
        while(cpu.State.ProgramCounter!=0xf1000&&instructions<150_000_000)
        {
            cpu.ExecuteInstruction(); instructions++; minimum=Math.Min(minimum,cpu.State.A[7]);
            Assert.InRange(cpu.State.A[7],0xef000u,0xf0004u); Assert.False(cpu.State.Halted||cpu.State.Stopped);
        }
        Assert.Equal(0xf1000u,cpu.State.ProgramCounter); Assert.Equal(0u,cpu.State.D[0]);
        Assert.All(bus.Memory.AsSpan(0xee000,4096).ToArray(),b=>Assert.Equal(0xa5,b));
        var volume=new Cpx4Volume<Bus>(bus,new byte[36864]); volume.Check(); Assert.Equal(0UL,volume.FileCount);
        output.WriteLine($"Compiled core (not DOS/HUNK): {compiled.Code.Length} code bytes, {instructions} instructions, {0xf0000-minimum} stack bytes, {bus.Requests} storage completions.");
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
