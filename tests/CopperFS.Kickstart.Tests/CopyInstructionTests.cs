using System.Buffers.Binary;
using Amiga;
using Copper68k;
using CopperSharp.Compiler;
using CopperSharp.Targets.Amiga;
using CopperStart.Dos;

namespace CopperFS.Kickstart.Tests;

public sealed class CopyInstructionTests
{
    private static readonly Lazy<M68kCompilationResult> Compilation = new(() => AmigaM68kCompiler.Compile(new()
    {
        AssemblyPath = typeof(CopyProbe).Assembly.Location,
        EntryPoint = typeof(CopyProbe).FullName + "::Entry",
        ManagedAssemblyPaths = [typeof(NativeAmigaFileSystem).Assembly.Location,
            typeof(APTR).Assembly.Location, typeof(IAmigaGuestMemory).Assembly.Location],
        IncludedExportNames = [], Cpu = M68kCpuTarget.M68000, OutputFormat = M68kOutputFormat.Assembly,
        RuntimeProfile = M68kRuntimeProfile.Freestanding, MemoryManagement = M68kMemoryManagement.None,
        ExceptionMode = M68kExceptionMode.Yolo, PeepholeOptimization = M68kPeepholeOptimizationMode.FixedPoint
    }));

    [Theory]
    [InlineData(M68kCpuModel.M68000)] [InlineData(M68kCpuModel.M68020)]
    public void AllCopyParitiesPreserveExactRangesAndGuards(M68kCpuModel model)
    {
        var compiled = Compilation.Value;
        Assert.True(compiled.FrameworkAnalysis.IsCompatible);
        Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        for (var sourceAlignment = 0u; sourceAlignment < 4; sourceAlignment++)
        for (var targetAlignment = 0u; targetAlignment < 4; targetAlignment++)
        foreach (var length in new uint[] { 0, 1, 2, 3, 4, 5, 7, 8, 9, 31, 32, 33, 255, 256, 257, 511, 512, 513 })
        {
            var bus = new CopyBus { WideUnaligned = model == M68kCpuModel.M68020 };
            bus.Put(4, 0x2000);
            BinaryPrimitives.WriteUInt16BigEndian(bus.Memory.AsSpan(0x2000 + 296, 2), bus.WideUnaligned ? (ushort)2 : (ushort)0);
            compiled.Code.CopyTo(bus.Memory, 0x10000);
            foreach (var relocation in compiled.Relocations)
            {
                var address = 0x10000u + (uint)relocation.Offset;
                bus.Put(address, bus.Get(address) + 0x10000);
            }
            for (var i = 0; i < 4096; i++) bus.Memory[0x4000 + i] = (byte)(i * 29 + 7);
            var source = 0x4100u + sourceAlignment; var target = 0x4800u + targetAlignment;
            bus.Source = source; bus.Target = target; bus.Bytes = length;
            var expected = bus.Memory.AsSpan(0x4000, 4096).ToArray();
            expected.AsSpan((int)(source - 0x4000), (int)length).CopyTo(expected.AsSpan((int)(target - 0x4000)));
            bus.Put(0x1000, source); bus.Put(0x1004, target); bus.Put(0x1008, length);
            bus.Put(0xF0000, 0xF1000);
            using var cpu = M68kCoreFactory.Default.Create(model, bus);
            cpu.Reset(0x10000 + compiled.EntryPoint, 0xF0000);
            for (var n = 0; n < 100000 && cpu.State.ProgramCounter != 0xF1000; n++)
            { cpu.ExecuteInstruction(); Assert.False(cpu.State.Halted || cpu.State.Stopped); }
            Assert.Equal(0xF1000u, cpu.State.ProgramCounter);
            Assert.Equal(expected, bus.Memory.AsSpan(0x4000, 4096).ToArray());
        }
    }

    private sealed class CopyBus : IM68kBus
    {
        public readonly byte[] Memory = new byte[0x100000];
        public uint Source, Target, Bytes;
        public bool WideUnaligned;
        private void Range(uint address, uint bytes, bool write)
        {
            if (address < 0x4000 || address >= 0x5000) return;
            var first = write ? Target : Source;
            Assert.True(address >= first && bytes <= Bytes && address - first <= Bytes - bytes);
        }
        public uint Get(uint a) => BinaryPrimitives.ReadUInt32BigEndian(Memory.AsSpan((int)a, 4));
        public void Put(uint a, uint v) => BinaryPrimitives.WriteUInt32BigEndian(Memory.AsSpan((int)a, 4), v);
        public void ResetExternalDevices(long cycle) { }
        public byte ReadByte(uint a, ref long c, M68kBusAccessKind k)
        { Range(a, 1, false); return Memory[a]; }
        public ushort ReadWord(uint a, ref long c, M68kBusAccessKind k)
        { Range(a, 2, false); if (!WideUnaligned) Assert.Equal(0u, a & 1); return BinaryPrimitives.ReadUInt16BigEndian(Memory.AsSpan((int)a, 2)); }
        public uint ReadLong(uint a, ref long c, M68kBusAccessKind k)
        { Range(a, 4, false); if (!WideUnaligned) Assert.Equal(0u, a & 1); return Get(a); }
        public void WriteByte(uint a, byte v, ref long c, M68kBusAccessKind k)
        { Range(a, 1, true); Memory[a] = v; }
        public void WriteWord(uint a, ushort v, ref long c, M68kBusAccessKind k)
        { Range(a, 2, true); if (!WideUnaligned) Assert.Equal(0u, a & 1); BinaryPrimitives.WriteUInt16BigEndian(Memory.AsSpan((int)a, 2), v); }
        public void WriteLong(uint a, uint v, ref long c, M68kBusAccessKind k)
        { Range(a, 4, true); if (!WideUnaligned) Assert.Equal(0u, a & 1); Put(a, v); }
    }
}

public static class CopyProbe
{
    public static uint Entry()
    {
        var p = new NativeFileSystemPlatform(APTR.FromPointer(0x3000));
        var args = APTR.FromPointer(0x1000);
        p.Copy(APTR.FromPointer(APTR.ReadUInt32(args, 0)), APTR.FromPointer(APTR.ReadUInt32(args, 4)), APTR.ReadUInt32(args, 8));
        return 0;
    }
}
