using Copper68k;
using CopperSharp.Compiler;
using CopperFS.Cpx4.Reference;

namespace CopperCPX.InstructionTests;

public static class ShadowFilesystemProbe
{
    public static uint Entry()
    {
        var memory = new GuestMemory(); uint unit = memory.Read32(0x8304); bool controlOnly = memory.Read32(0x8308) != 0;
        var engine = new CpxEngine<GuestMemory>(memory, 0x60000);
        if (engine.Initialize(512, unit, Wide.From(8192), 1) != CpxError.None) return 1;
        if (engine.EnableBufferedWrites(0x70000, CpxEngine<GuestMemory>.BufferedStorageBytes(unit < 1024 ? 1024 : unit)) != CpxError.None) return 2;
        if (engine.EnableShadowVolume(0xe0000, 32768) != CpxError.None) return 3;
        if (engine.BeginOperation(CpxOperation.Mount) != CpxError.None) return 4;
        uint phase = 0; Wide id = default;
        for (uint turn = 0; turn < 20000; turn++)
        {
            var result = engine.Step();
            if (result == StepResult.Failed) return 100 + (uint)engine.Error;
            if (result == StepResult.Complete)
            {
                CpxError error;
                if (controlOnly)
                {
                    if (phase == 0) { memory.Write32(0x9000, 0x4e657700); error = engine.BeginLabel(0x9000, 3); }
                    else if (phase == 1) error = engine.BeginPersistenceBarrier();
                    else if (phase == 2) error = engine.BeginRemount();
                    else
                    {
                        uint label = engine.VolumeLabelAddress;
                        return memory.Read8(label) == 3 && memory.Read8(label + 1) == 78 &&
                            memory.Read8(label + 2) == 101 && memory.Read8(label + 3) == 119 ? 0u : 9u;
                    }
                    if (error != CpxError.None) return 200 + (uint)error;
                    phase++; continue;
                }
                if (phase == 0) { memory.Write32(0x9000, 0x66696c65); error = engine.BeginCreate(Wide.From(1), 0x9000, 4); }
                else if (phase == 1)
                {
                    id = engine.MutatedObject;
                    for (uint n = 0; n < 8192; n++) memory.Write8(0x9000 + n, (byte)(n * 17 + 3));
                    error = engine.BeginMappedWrite(id, default, 0x9000, 8192);
                }
                else if (phase == 2) error = engine.BeginPersistenceBarrier();
                else if (phase == 3) error = engine.BeginRemount();
                else if (phase == 4) { memory.Write32(0x9000, 0x66696c65); error = engine.BeginLookup(Wide.From(1), 0x9000, 4); }
                else if (phase == 5)
                {
                    if (!engine.LookupFound || engine.LookupId.Low != id.Low) return 8;
                    error = engine.BeginFileRead(id, default, 0x9000, 8192);
                }
                else
                {
                    for (uint n = 0; n < 8192; n++) if (memory.Read8(0x9000 + n) != (byte)(n * 17 + 3)) return 5;
                    return 0;
                }
                if (error != CpxError.None) return 200 + (uint)error;
                phase++;
            }
            if (result != StepResult.NeedIo) continue;
            var io = engine.Request;
            memory.Write32(0x8004, io.Sector.High); memory.Write32(0x8008, io.Sector.Low);
            memory.Write32(0x800c, io.Buffer); memory.Write32(0x8010, io.Bytes); memory.Write32(0x8000, (uint)io.Kind + 1);
            if (engine.CompleteIo(io.Token, io.Bytes, true) != CpxError.None) return 6;
        }
        return 7;
    }
}

public class ShadowFilesystemInstructions
{
    [Theory][InlineData(512, false, false)][InlineData(4096, false, false)][InlineData(512, true, false)][InlineData(4096, true, false)]
    [InlineData(512, false, true)][InlineData(4096, false, true)]
    public void CompiledFilesystemWritesFlushesRemountsAndReadsWithinFourKiBStack(int unit, bool controlOnly, bool directInodes)
    {
        var compiled = Compilation.Value;
        Assert.True(compiled.FrameworkAnalysis.IsCompatible); Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        Assert.True(compiled.Code.Length < 0x50000);
        var bus = new EngineInstructions.Bus();
        if (directInodes) Cpx4Volume<EngineInstructions.Bus>.FormatDirectInodes(bus, unit, new byte[49152], 12345);
        else Cpx4Volume<EngineInstructions.Bus>.FormatShadow(bus, unit, new byte[49152], 12345);
        compiled.Code.CopyTo(bus.Memory, 0x10000);
        foreach (var relocation in compiled.Relocations)
        { uint at = 0x10000u + (uint)relocation.Offset; bus.Put(at, bus.Get(at) + 0x10000); }
        bus.Put(0x8304, (uint)unit);
        bus.Put(0x8308, controlOnly ? 1u : 0u);
        bus.Memory.AsSpan(0xee000, 8192).Fill(0xa5); bus.Put(0xf0000, 0xf1000);
        using var cpu = M68kCoreFactory.Default.Create(M68kCpuModel.M68000, bus);
        cpu.Reset(0x10000 + compiled.EntryPoint, 0xf0000);
        for (int count = 0; cpu.State.ProgramCounter != 0xf1000 && count < 100_000_000; count++)
        {
            cpu.ExecuteInstruction(); Assert.InRange(cpu.State.A[7], 0xef000u, 0xf0004u);
            Assert.False(cpu.State.Halted || cpu.State.Stopped);
        }
        Assert.Equal(0xf1000u, cpu.State.ProgramCounter); Assert.Equal(0u, cpu.State.D[0]);
        Assert.All(bus.Memory.AsSpan(0xee000, 4096).ToArray(), b => Assert.Equal(0xa5, b));
        var check = new Cpx4Volume<EngineInstructions.Bus>(bus, new byte[49152]); check.Check();
        if (!controlOnly)
        {
            byte[] expected = Enumerable.Range(0, 8192).Select(n => (byte)(n * 17 + 3)).ToArray(), actual = new byte[8192];
            Assert.Equal(8192, check.Read("file"u8, 0, actual)); Assert.Equal(expected, actual);
        }
    }
    private static readonly Lazy<M68kCompilationResult> Compilation = new(() => ShadowInstructionProfile.Compile(typeof(ShadowFilesystemProbe)));
}
