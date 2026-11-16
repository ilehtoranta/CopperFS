using Copper68k;
using CopperSharp.Compiler;
using CopperFS.Cpx4.Reference;
using static CopperFS.Cpx4.Reference.Layout;

namespace CopperCPX.InstructionTests;

public static class ShadowFilesystemProbe
{
    public static uint Entry()
    {
        var memory = new GuestMemory(); uint unit = memory.Read32(0x8304); bool controlOnly = memory.Read32(0x8308) != 0;
        bool smallMetadata = memory.Read32(0x830c) != 0, longStrings = memory.Read32(0x8310) != 0;
        bool promotion = memory.Read32(0x8314) != 0;
        var engine = new CpxEngine<GuestMemory>(memory, 0x60000);
        if (engine.Initialize(512, unit, Wide.From(8192), 1, metadataPageBytes: smallMetadata ? unit : 0) != CpxError.None) return 1;
        if (engine.EnableBufferedWrites(0x70000, CpxEngine<GuestMemory>.BufferedStorageBytes(unit < 1024 ? 1024 : unit)) != CpxError.None) return 2;
        if (engine.EnableShadowVolume(0xe0000, 32768) != CpxError.None) return 3;
        if (engine.BeginOperation(CpxOperation.Mount) != CpxError.None) return 4;
        uint phase = 0; Wide id = default;
        for (uint turn = 0; turn < 40000; turn++)
        {
            var result = engine.Step();
            if (result == StepResult.Failed) return 100 + (uint)engine.Error;
            if (result == StepResult.Complete)
            {
                CpxError error;
                if (promotion)
                {
                    if (phase < 140)
                    {
                        if ((phase & 1) == 0)
                        {
                            uint n = phase >> 1;
                            memory.Write8(0x9000, 102); memory.Write8(0x9001, (byte)(48 + n / 10)); memory.Write8(0x9002, (byte)(48 + n % 10));
                            error = engine.BeginCreate(Wide.From(1), 0x9000, 3);
                        }
                        else error = engine.BeginPersistenceBarrier();
                    }
                    else if (phase == 140) error = engine.BeginRemount();
                    else return 0;
                    if (error != CpxError.None) return 200 + (uint)error;
                    phase++; continue;
                }
                if (longStrings)
                {
                    if (phase == 0)
                    {
                        for (uint n = 0; n < 255; n++) memory.Write8(0x9000 + n, 0xe4);
                        for (uint n = 0; n < 768; n++) memory.Write8(0x9200 + n, (byte)(97 + n % 26));
                        error = engine.BeginSymlink(Wide.From(1), 0x9000, 255, 0x9200, 768);
                    }
                    else if (phase == 1) { id = engine.MutatedObject; error = engine.BeginReadSymlink(id, 0x9400, 768); }
                    else if (phase == 2)
                    {
                        for (uint n = 0; n < 768; n++) if (memory.Read8(0x9400 + n) != (byte)(97 + n % 26)) return 10;
                        error = engine.BeginPersistenceBarrier();
                    }
                    else if (phase == 3) error = engine.BeginRemount();
                    else if (phase == 4) error = engine.BeginLookup(Wide.From(1), 0x9000, 255);
                    else if (phase == 5)
                    {
                        if (!engine.LookupFound || !Wide.Equal(id, engine.LookupId)) return 11;
                        error = engine.BeginDelete(Wide.From(1), 0x9000, 255);
                    }
                    else if (phase == 6) error = engine.BeginPersistenceBarrier();
                    else return 0;
                    if (error != CpxError.None) return 200 + (uint)error;
                    phase++; continue;
                }
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
                else if (phase == 6)
                {
                    for (uint n = 0; n < 8192; n++) if (memory.Read8(0x9000 + n) != (byte)(n * 17 + 3)) return 5;
                    error = engine.BeginFileRead(id, default, 0x9000, 8192);
                }
                else { for (uint n = 0; n < 8192; n++) if (memory.Read8(0x9000 + n) != (byte)(n * 17 + 3)) return 12; return 0; }
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
    [Theory][InlineData(512, false, false, false, false, false)][InlineData(4096, false, false, false, false, false)][InlineData(512, true, false, false, false, false)][InlineData(4096, true, false, false, false, false)]
    [InlineData(512, false, true, false, false, false)][InlineData(4096, false, true, false, false, false)]
    [InlineData(512, false, true, true, false, false)][InlineData(512, false, true, true, true, false)]
    [InlineData(512, false, true, true, false, true)][InlineData(4096, false, true, true, false, true)]
    [InlineData(512, false, true, true, true, true)]
    public void CompiledFilesystemWritesFlushesRemountsAndReadsWithinFourKiBStack(int unit, bool controlOnly, bool directInodes, bool smallMetadata, bool longStrings, bool inlineCatalogue)
        => Run(unit, controlOnly, directInodes, smallMetadata, longStrings, inlineCatalogue, false);

    [Fact]
    public void CompiledCataloguePromotionPreservesEveryInodeWithinFourKiBStack()
        => Run(512, false, true, true, false, true, true);

    private static void Run(int unit, bool controlOnly, bool directInodes, bool smallMetadata, bool longStrings, bool inlineCatalogue, bool promotion)
    {
        var compiled = Compilation.Value;
        Assert.True(compiled.FrameworkAnalysis.IsCompatible); Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        Assert.True(compiled.Code.Length < 0x50000);
        var bus = new EngineInstructions.Bus();
        if (directInodes) Cpx4Volume<EngineInstructions.Bus>.FormatDirectInodes(bus, unit, new byte[49152], 12345, smallMetadata, inlineCatalogue);
        else Cpx4Volume<EngineInstructions.Bus>.FormatShadow(bus, unit, new byte[49152], 12345);
        compiled.Code.CopyTo(bus.Memory, 0x10000);
        foreach (var relocation in compiled.Relocations)
        { uint at = 0x10000u + (uint)relocation.Offset; bus.Put(at, bus.Get(at) + 0x10000); }
        bus.Put(0x8304, (uint)unit);
        bus.Put(0x8308, controlOnly ? 1u : 0u);
        bus.Put(0x830c, smallMetadata ? 1u : 0u); bus.Put(0x8310, longStrings ? 1u : 0u);
        bus.Put(0x8314, promotion ? 1u : 0u);
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
        if (promotion)
        {
            Assert.Equal(70UL, check.FileCount);
            for (int n = 0; n < 70; n++) Assert.True(check.TryGetInfo(System.Text.Encoding.ASCII.GetBytes($"f{n:D2}"), out _));
            byte[] root = new byte[1024]; ulong generation = 0, mapping = 0;
            for (ulong copy = 0; copy < 3; copy++)
            {
                bus.Read(130 + copy * 2, root);
                if (U64(root, 80) >= generation) { generation = U64(root, 80); mapping = U64(root, 88); }
            }
            Assert.NotEqual(0UL, generation); Assert.NotEqual(ulong.MaxValue, mapping);
        }
        else if (longStrings) Assert.Equal(0UL, check.FileCount);
        else if (!controlOnly)
        {
            byte[] expected = Enumerable.Range(0, 8192).Select(n => (byte)(n * 17 + 3)).ToArray(), actual = new byte[8192];
            Assert.Equal(8192, check.Read("file"u8, 0, actual)); Assert.Equal(expected, actual);
        }
    }
    private static readonly Lazy<M68kCompilationResult> Compilation = new(() => ShadowInstructionProfile.Compile(typeof(ShadowFilesystemProbe)));
}
