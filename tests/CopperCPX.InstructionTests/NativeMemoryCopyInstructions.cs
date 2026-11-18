using Copper68k;
using CopperCPX.Amiga;
using CopperSharp.Compiler;

namespace CopperCPX.InstructionTests;

public static class NativeMemoryCopyProbe
{
    public static uint Entry()
    {
        var memory = new NativeCpxMemory();
        memory.Copy(memory.Read32(0x7f04), memory.Read32(0x7f00), memory.Read32(0x7f08));
        return 0;
    }
}

public class NativeMemoryCopyInstructions
{
    [Theory]
    [InlineData(0x8000u, 0x9000u, 512u, 630)]
    [InlineData(0x8001u, 0x9001u, 257u, 624)]
    [InlineData(0x8002u, 0x9002u, 128u, 624)]
    [InlineData(0x8000u, 0x9000u, 63u, 0)]
    [InlineData(0x8000u, 0x8002u, 97u, 0)]
    [InlineData(0x8002u, 0x8000u, 97u, 0)]
    [InlineData(0x8001u, 0x8003u, 97u, 0)]
    [InlineData(0x8000u, 0x8000u, 512u, 0)]
    [InlineData(0x8000u, 0x9000u, 0u, 0)]
    public void NativeBulkCopyPreservesScalarSemanticsAndCallerBounds(uint source, uint destination, uint bytes, int expectedVector)
    {
        var compiled = Compilation.Value;
        Assert.True(compiled.FrameworkAnalysis.IsCompatible); Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        var bus = new NativeTestBus(); compiled.Code.CopyTo(bus.Bytes, 0x10000);
        foreach (var relocation in compiled.Relocations)
        { uint at = 0x10000u + (uint)relocation.Offset; bus.Put(at, bus.Get(at) + 0x10000); }
        for (int n = 0x8000; n < 0xa000; n++) bus.Bytes[n] = (byte)(n * 17 + (n >> 7));
        byte[] expected = (byte[])bus.Bytes.Clone();
        Span<byte> word = stackalloc byte[4];
        for (uint n = 0; n < bytes;)
        {
            int width = ((source | destination) & 1) == 0 && bytes - n >= 4 ? 4 : 1;
            expected.AsSpan((int)(source + n), width).CopyTo(word);
            word[..width].CopyTo(expected.AsSpan((int)(destination + n), width)); n += (uint)width;
        }
        bus.Put(4, 0x7000); bus.Put(0x7f00, source); bus.Put(0x7f04, destination); bus.Put(0x7f08, bytes);
        bus.Put(0xf0000, 0xf1000); bus.Bytes.AsSpan(0xee000, 4096).Fill(0xa5);
        using var cpu = M68kCoreFactory.Default.Create(M68kCpuModel.M68000, bus); cpu.Reset(0x10000 + compiled.EntryPoint, 0xf0000);
        int calls = 0;
        for (int step = 0; step < 100000 && cpu.State.ProgramCounter != 0xf1000; step++)
        {
            uint vector = 0x7000u - cpu.State.ProgramCounter;
            if (vector is 624 or 630)
            {
                Assert.Equal(expectedVector, (int)vector); calls++;
                Assert.Equal(source, cpu.State.A[0]); Assert.Equal(destination, cpu.State.A[1]); Assert.Equal(bytes, cpu.State.D[0]);
                bus.Bytes.AsSpan((int)source, (int)bytes).CopyTo(bus.Bytes.AsSpan((int)destination, (int)bytes));
                cpu.State.ProgramCounter = bus.Get(cpu.State.A[7]); cpu.State.A[7] += 4;
            }
            else cpu.ExecuteInstruction();
            Assert.InRange(cpu.State.A[7], 0xef000u, 0xf0004u); Assert.False(cpu.State.Halted || cpu.State.Stopped);
        }
        Assert.Equal(0xf1000u, cpu.State.ProgramCounter); Assert.Equal(0u, cpu.State.D[0]); Assert.Equal(expectedVector == 0 ? 0 : 1, calls);
        Assert.Equal(expected.AsSpan(0x8000, 8192).ToArray(), bus.Bytes.AsSpan(0x8000, 8192).ToArray());
        Assert.All(bus.Bytes.AsSpan(0xee000, 4096).ToArray(), b => Assert.Equal(0xa5, b));
    }
    private static readonly Lazy<M68kCompilationResult> Compilation = new(() => ShadowInstructionProfile.Compile(typeof(NativeMemoryCopyProbe)));
}
