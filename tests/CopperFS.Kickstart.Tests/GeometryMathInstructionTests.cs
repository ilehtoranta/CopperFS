using System.Buffers.Binary;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amiga;
using Copper68k;
using CopperSharp.Compiler;
using CopperSharp.Targets.Amiga;
using CopperSFS;
using CopperStart.Dos;

namespace CopperFS.Kickstart.Tests;

public sealed class GeometryMathInstructionTests
{
    private static IEnumerable<(ulong Value, uint Divisor)> Vectors(bool wide)
    {
        var random = new Random(0xD17E);
        foreach (var divisor in new uint[] { 1, 2, 72, 488, 512, 1024, 4096, 32768, 65535, 65536, 0x80000000, uint.MaxValue })
        {
            var values = new List<ulong> { 0, 1, divisor - 1, divisor, (ulong)divisor + 1,
                uint.MaxValue, 0x80000000, (ulong)divisor * 65535, (ulong)divisor * 65536 };
            if (wide) values.AddRange([1UL << 32, (1UL << 32) + 1, (1UL << 48) - 1, 1UL << 63, ulong.MaxValue]);
            for (var i = 0; i < 16; i++)
            {
                var low = (uint)random.NextInt64(0, 1L << 32);
                values.Add(wide ? ((ulong)(uint)random.NextInt64(0, 1L << 32) << 32) | low : low);
            }
            foreach (var value in values.Distinct())
                if (wide || value <= uint.MaxValue) yield return (value, divisor);
        }
        if (!wide) { yield return (0, 0); yield return (uint.MaxValue, 0); }
    }

    [Fact]
    public void HostDivRemPreservesUnsignedBoundariesAndZeroPolicy()
    {
        foreach (var (value, divisor) in Vectors(false))
        {
            var quotient = AmigaGeometryMath.DivRem((uint)value, divisor, out var remainder);
            Assert.Equal(divisor == 0 ? 0 : (uint)value / divisor, quotient);
            Assert.Equal(divisor == 0 ? 0 : (uint)value % divisor, remainder);
            Assert.Equal(AmigaGeometryMath.Divide((uint)value, divisor), quotient);
            Assert.Equal(AmigaGeometryMath.Remainder((uint)value, divisor), remainder);
        }
        foreach (var (value, divisor) in Vectors(true))
        {
            Assert.Equal(value / divisor, AmigaSfsGeometryMath.DivRem(value, divisor, out var remainder));
            Assert.Equal((uint)(value % divisor), remainder);
        }
        Assert.Throws<DivideByZeroException>(() => AmigaSfsGeometryMath.DivRem(1, 0, out _));
    }

    [Theory]
    [InlineData(M68kCpuTarget.M68000, M68kCpuModel.M68000, false)]
    [InlineData(M68kCpuTarget.M68000, M68kCpuModel.M68020, false)]
    [InlineData(M68kCpuTarget.M68020, M68kCpuModel.M68020, false)]
    [InlineData(M68kCpuTarget.M68000, M68kCpuModel.M68000, true)]
    [InlineData(M68kCpuTarget.M68000, M68kCpuModel.M68020, true)]
    [InlineData(M68kCpuTarget.M68020, M68kCpuModel.M68020, true)]
    public void NativeDivRemMatchesHostAndRecordsPairedCycles(M68kCpuTarget target, M68kCpuModel model, bool wide)
    {
        var samples = new List<object>();
        foreach (var paired in new[] { false, true })
        {
            var method = (wide ? "Wide" : "Word") + (paired ? "Pair" : "Separate");
            var compiled = AmigaM68kCompiler.Compile(new M68kCompilationRequest
            {
                AssemblyPath = typeof(GeometryMathProbe).Assembly.Location,
                EntryPoint = $"{typeof(GeometryMathProbe).FullName}::{method}",
                ManagedAssemblyPaths = [typeof(APTR).Assembly.Location], IncludedExportNames = [],
                Cpu = target, OutputFormat = M68kOutputFormat.Assembly,
                RuntimeProfile = M68kRuntimeProfile.Freestanding, MemoryManagement = M68kMemoryManagement.None,
                ExceptionMode = M68kExceptionMode.Yolo, PeepholeOptimization = M68kPeepholeOptimizationMode.FixedPoint
            });
            Assert.True(compiled.FrameworkAnalysis.IsCompatible);
            Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
            if (!wide && target == M68kCpuTarget.M68000)
                Assert.Equal(paired ? 1 : 2, Regex.Matches(compiled.Text!,
                    @"^C68K_generated_003Adiv_loop[^\r\n]*:", RegexOptions.Multiline).Count);
            var bus = new ArithmeticBus();
            compiled.Code.CopyTo(bus.Memory, 0x10000);
            foreach (var relocation in compiled.Relocations)
            {
                var address = 0x10000u + (uint)relocation.Offset;
                bus.Put(address, bus.Get(address) + 0x10000);
            }
            foreach (var (value, divisor) in Vectors(wide))
            {
                bus.Put(0x4000, (uint)(value >> 32)); bus.Put(0x4004, (uint)value); bus.Put(0x4008, divisor);
                bus.Put(0xF0000, 0xF1000);
                using var cpu = M68kCoreFactory.Default.Create(model, bus);
                cpu.Reset(0x10000 + compiled.EntryPoint, 0xF0000);
                for (var step = 0; step < 100_000 && cpu.State.ProgramCounter != 0xF1000; step++)
                {
                    cpu.ExecuteInstruction();
                    Assert.False(cpu.State.Halted || cpu.State.Stopped);
                }
                Assert.Equal(0xF1000u, cpu.State.ProgramCounter);
                Assert.Equal(0xF0004u, cpu.State.A[7]);
                var expected = divisor == 0 ? 0 : value / divisor;
                var actual = ((ulong)bus.Get(0x4010) << 32) | bus.Get(0x4014);
                Assert.True(expected == actual, $"{target}/{model}/{method}: {value:X}/{divisor:X}: expected {expected:X}, got {actual:X}");
                Assert.Equal(divisor == 0 ? 0u : (uint)(value % divisor), bus.Get(0x4018));
                samples.Add(new { Paired = paired, Value = value, Divisor = divisor, Cycles = cpu.State.Cycles });
            }
            if (Environment.GetEnvironmentVariable("COPPERFS_DIVREM_ARTIFACT_ROOT") is { Length: > 0 } root)
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, $"{target}-{model}-{method}.s"), compiled.Text);
            }
        }
        if (Environment.GetEnvironmentVariable("COPPERFS_DIVREM_ARTIFACT_ROOT") is { Length: > 0 } output)
            File.WriteAllText(Path.Combine(output, $"{target}-{model}-{wide}.json"), JsonSerializer.Serialize(samples));
    }

    private sealed class ArithmeticBus : IM68kBus
    {
        public byte[] Memory { get; } = new byte[0x100000];
        public void ResetExternalDevices(long cycle) { }
        public uint Get(uint address) => BinaryPrimitives.ReadUInt32BigEndian(Memory.AsSpan((int)address, 4));
        public void Put(uint address, uint value) => BinaryPrimitives.WriteUInt32BigEndian(Memory.AsSpan((int)address, 4), value);
        public byte ReadByte(uint address, ref long cycle, M68kBusAccessKind kind) => Memory[address];
        public ushort ReadWord(uint address, ref long cycle, M68kBusAccessKind kind) => BinaryPrimitives.ReadUInt16BigEndian(Memory.AsSpan((int)address, 2));
        public uint ReadLong(uint address, ref long cycle, M68kBusAccessKind kind) => Get(address);
        public void WriteByte(uint address, byte value, ref long cycle, M68kBusAccessKind kind) => Memory[address] = value;
        public void WriteWord(uint address, ushort value, ref long cycle, M68kBusAccessKind kind) => BinaryPrimitives.WriteUInt16BigEndian(Memory.AsSpan((int)address, 2), value);
        public void WriteLong(uint address, uint value, ref long cycle, M68kBusAccessKind kind) => Put(address, value);
    }
}

public static class GeometryMathProbe
{
    private static void Store(ulong quotient, uint remainder)
    {
        var output = APTR.FromPointer(0x4010);
        APTR.WriteUInt32(output, 0, (uint)(quotient >> 32));
        APTR.WriteUInt32(output, 4, (uint)quotient); APTR.WriteUInt32(output, 8, remainder);
    }
    private static ulong Value() => ((ulong)APTR.ReadUInt32(APTR.FromPointer(0x4000), 0) << 32) | APTR.ReadUInt32(APTR.FromPointer(0x4004), 0);
    private static uint Divisor() => APTR.ReadUInt32(APTR.FromPointer(0x4008), 0);
    public static void WordPair() { var quotient = AmigaGeometryMath.DivRem((uint)Value(), Divisor(), out var remainder); Store(quotient, remainder); }
    public static void WordSeparate() { var value = (uint)Value(); var divisor = Divisor(); Store(AmigaGeometryMath.Divide(value, divisor), AmigaGeometryMath.Remainder(value, divisor)); }
    public static void WidePair() { var quotient = AmigaSfsGeometryMath.DivRem(Value(), Divisor(), out var remainder); Store(quotient, remainder); }
    public static void WideSeparate() { var value = Value(); var divisor = Divisor(); Store(value / divisor, (uint)(value % divisor)); }
}
