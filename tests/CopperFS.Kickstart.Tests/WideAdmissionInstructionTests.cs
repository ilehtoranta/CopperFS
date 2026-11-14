using System.Buffers.Binary;
using Amiga;
using Copper68k;
using CopperSharp.Compiler;
using CopperSharp.Targets.Amiga;
using CopperSFS;
using CopperStart.Dos;

namespace CopperFS.Kickstart.Tests;

public sealed class WideAdmissionInstructionTests
{
    private static readonly Lazy<M68kCompilationResult> Compilation = new(() => AmigaM68kCompiler.Compile(new() {
        AssemblyPath=typeof(WideAdmissionProbe).Assembly.Location, EntryPoint=typeof(WideAdmissionProbe).FullName+"::Entry",
        ManagedAssemblyPaths=[typeof(AmigaFileSystemDeviceCore).Assembly.Location,typeof(AmigaVolumeGeometry).Assembly.Location,typeof(APTR).Assembly.Location],
        IncludedExportNames=[],Cpu=M68kCpuTarget.M68000,OutputFormat=M68kOutputFormat.Assembly,
        RuntimeProfile=M68kRuntimeProfile.Freestanding,MemoryManagement=M68kMemoryManagement.None,ExceptionMode=M68kExceptionMode.Yolo }));

    [Theory]
    [InlineData(M68kCpuModel.M68000)] [InlineData(M68kCpuModel.M68020)]
    public void CompiledAdmissionChecksPartitionEndAndProbesOnlyWideRanges(M68kCpuModel model)
    {
        var compiled=Compilation.Value; Assert.True(compiled.FrameworkAnalysis.IsCompatible); Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        foreach(var (start,blocks,support,expected,calls) in new (ulong,uint,bool,uint,uint)[] {
            (0,0x800000,false,1,0), (0,0x800001,false,0,1), (0,0x800001,true,1,1),
            (0xfffffe00,4,false,0,1), (0xfffffe00,4,true,1,1), (0x100000000,4,false,0,1), (0x100000000,4,true,1,1) })
        {
            var bus=new Bus(); compiled.Code.CopyTo(bus.Memory,0x10000);
            foreach(var relocation in compiled.Relocations) { uint at=0x10000u+(uint)relocation.Offset; bus.Put(at,bus.Get(at)+0x10000); }
            bus.Put(0x1000,(uint)(start>>32)); bus.Put(0x1004,(uint)start); bus.Put(0x1008,blocks); bus.Put(0x100c,support?1u:0u); bus.Put(0xe0000,0xf0000);
            using var cpu=M68kCoreFactory.Default.Create(model,bus); cpu.Reset(0x10000+compiled.EntryPoint,0xe0000);
            for(int n=0;n<2000000&&cpu.State.ProgramCounter!=0xf0000;n++) { cpu.ExecuteInstruction(); Assert.False(cpu.State.Halted||cpu.State.Stopped); }
            Assert.Equal(0xf0000u,cpu.State.ProgramCounter); Assert.Equal(expected,cpu.State.D[0]); Assert.Equal(calls,bus.Get(0x2000));
            if(calls!=0) { Assert.Equal(24u,bus.Get(0x2004)); Assert.Equal((uint)(start>>32),bus.Get(0x2008)); Assert.Equal((uint)start,bus.Get(0x200c)); Assert.Equal(512u,bus.Get(0x2010)); }
        }
    }
    private sealed class Bus : IM68kBus
    {
        public readonly byte[] Memory=new byte[0x100000];
        public uint Get(uint a)=>BinaryPrimitives.ReadUInt32BigEndian(Memory.AsSpan((int)a,4));
        public void Put(uint a,uint v)=>BinaryPrimitives.WriteUInt32BigEndian(Memory.AsSpan((int)a,4),v);
        public void ResetExternalDevices(long cycle) { }
        public byte ReadByte(uint a,ref long c,M68kBusAccessKind k)=>Memory[a];
        public ushort ReadWord(uint a,ref long c,M68kBusAccessKind k)=>BinaryPrimitives.ReadUInt16BigEndian(Memory.AsSpan((int)a,2));
        public uint ReadLong(uint a,ref long c,M68kBusAccessKind k)=>Get(a);
        public void WriteByte(uint a,byte v,ref long c,M68kBusAccessKind k)=>Memory[a]=v;
        public void WriteWord(uint a,ushort v,ref long c,M68kBusAccessKind k)=>BinaryPrimitives.WriteUInt16BigEndian(Memory.AsSpan((int)a,2),v);
        public void WriteLong(uint a,uint v,ref long c,M68kBusAccessKind k)=>Put(a,v);
    }
}

public static class WideAdmissionProbe
{
    private struct Device : IAmigaFileSystemDevicePlatform
    {
        public bool Transfer(ushort command,ulong offset,uint bytes,APTR buffer)
        {
            var receipt=APTR.FromPointer(0x2000); APTR.WriteUInt32(receipt,0,APTR.ReadUInt32(receipt,0)+1);
            APTR.WriteUInt32(receipt,4,command); APTR.WriteUInt32(receipt,8,(uint)(offset>>32)); APTR.WriteUInt32(receipt,12,(uint)offset); APTR.WriteUInt32(receipt,16,bytes);
            return APTR.ReadUInt32(APTR.FromPointer(0x1000),12)!=0;
        }
    }
    public static uint Entry()
    {
        var args=APTR.FromPointer(0x1000); var geometry=new AmigaVolumeGeometry { StartByte=((ulong)APTR.ReadUInt32(args,0)<<32)|APTR.ReadUInt32(args,4),
            BlockCount=APTR.ReadUInt32(args,8),BlockBytes=512,SectorBytes=512,MaxTransfer=512 };
        var device=new Device(); return AmigaFileSystemDeviceCore.AdmitPartition(ref device,geometry,APTR.FromPointer(0x4000))?1u:0u;
    }
}
