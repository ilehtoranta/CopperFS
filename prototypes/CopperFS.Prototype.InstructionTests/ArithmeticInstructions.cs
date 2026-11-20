using System.Buffers.Binary;
using Copper68k;
using CopperSharp.Compiler;
using CopperSharp.Targets.Amiga;

namespace CopperFS.Prototype.InstructionTests;

public class ArithmeticInstructions(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact] public void Actual68000KernelsPreserveGuardsAndStayWithin512ByteStack()
    {
        var compiled = AmigaM68kCompiler.Compile(new M68kCompilationRequest
        {
            AssemblyPath = typeof(Probe).Assembly.Location, EntryPoint = typeof(Probe).FullName + "::Entry",
            IncludedExportNames = [], Cpu = M68kCpuTarget.M68000, OutputFormat = M68kOutputFormat.Assembly,
            RuntimeProfile = M68kRuntimeProfile.Freestanding, MemoryManagement = M68kMemoryManagement.None,
            ExceptionMode = M68kExceptionMode.Yolo
        });
        Assert.True(compiled.FrameworkAnalysis.IsCompatible); Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        var bus = new ProbeBus(); compiled.Code.CopyTo(bus.Memory, 0x10000);
        foreach (var relocation in compiled.Relocations)
        { uint at = 0x10000u + (uint)relocation.Offset; bus.Put(at, bus.Get(at) + 0x10000); }
        bus.Memory.AsSpan(0xef000, 4096).Fill(0xa5); bus.Put(0xf0000, 0xf1000);
        using var cpu = M68kCoreFactory.Default.Create(M68kCpuModel.M68000, bus);
        cpu.Reset(0x10000 + compiled.EntryPoint, 0xf0000); uint minimum = 0xf0000; int instructions = 0;
        while (cpu.State.ProgramCounter != 0xf1000 && instructions < 5_000_000)
        {
            cpu.ExecuteInstruction(); instructions++; minimum = Math.Min(minimum, cpu.State.A[7]);
            Assert.False(cpu.State.Halted || cpu.State.Stopped); Assert.InRange(cpu.State.A[7], 0xefe00u, 0xf0004u);
        }
        Assert.Equal(0xf1000u, cpu.State.ProgramCounter); Assert.Equal(0u, cpu.State.D[0]);
        Assert.All(bus.Memory.AsSpan(0xef000, 3584).ToArray(), value => Assert.Equal(0xa5, value));
        output.WriteLine($"MC68000: {instructions} instructions, {0xf0000 - minimum} bytes peak measured stack; 512-byte guard budget.");
    }
    private sealed class ProbeBus : IM68kBus
    {
        internal readonly byte[] Memory = new byte[0x100000];
        internal uint Get(uint a) => BinaryPrimitives.ReadUInt32BigEndian(Memory.AsSpan((int)a));
        internal void Put(uint a, uint v) => BinaryPrimitives.WriteUInt32BigEndian(Memory.AsSpan((int)a), v);
        public void ResetExternalDevices(long cycle) { }
        public byte ReadByte(uint a, ref long c, M68kBusAccessKind k) => Memory[a];
        public ushort ReadWord(uint a, ref long c, M68kBusAccessKind k) => BinaryPrimitives.ReadUInt16BigEndian(Memory.AsSpan((int)a));
        public uint ReadLong(uint a, ref long c, M68kBusAccessKind k) => Get(a);
        public void WriteByte(uint a, byte v, ref long c, M68kBusAccessKind k) => Memory[a] = v;
        public void WriteWord(uint a, ushort v, ref long c, M68kBusAccessKind k) => BinaryPrimitives.WriteUInt16BigEndian(Memory.AsSpan((int)a), v);
        public void WriteLong(uint a, uint v, ref long c, M68kBusAccessKind k) => Put(a, v);
    }
}

public static class Probe
{
    public static uint Entry()
    {
        uint h, l;
        if (!WideMath32.Add(7, uint.MaxValue, 0, 1, out h, out l) || h != 8 || l != 0) return 1;
        if (WideMath32.Add(uint.MaxValue, uint.MaxValue, 0, 1, out h, out l)) return 2;
        if (!WideMath32.Subtract(8, 0, 0, 1, out h, out l) || h != 7 || l != uint.MaxValue) return 3;
        if (WideMath32.Subtract(0, 0, 0, 1, out h, out l)) return 4;
        for (int shift = 0; shift < 64; shift++)
        {
            if (!WideMath32.ShiftLeft(0, 1, shift, out h, out l)) return 5;
            WideMath32.ShiftRight(h, l, shift, out h, out l); if (h != 0 || l != 1) return 6;
            if (shift != 0 && WideMath32.ShiftLeft(uint.MaxValue, uint.MaxValue, shift, out h, out l)) return 7;
        }
        for (int shift = 9; shift <= 12; shift++)
        {
            if (!WideMath32.RoundUpUnits(uint.MaxValue, uint.MaxValue, shift, out h, out l)) return 8;
            if (h != (1u << (32 - shift)) || l != 0) return 9;
        }
        if (!WideMath32.DeviceRange(0, uint.MaxValue, 2, 0, 0, 2, 3, 1, out h, out l, out uint ch, out uint cl) || h != 1 || l != 3 || ch != 0 || cl != 6) return 10;
        if (WideMath32.DeviceRange(uint.MaxValue, uint.MaxValue - 1, 1, 0, 0, 0, 2, 0, out h, out l, out ch, out cl)) return 11;
        if (!WideMath32.Remainder(uint.MaxValue, uint.MaxValue, uint.MaxValue, out uint rem) || rem != 0) return 12;
        if (!WideMath32.Remainder(1, 0, 3, out rem) || rem != 1) return 13;
        return 0;
    }
}
