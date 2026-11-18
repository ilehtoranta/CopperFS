using Copper68k;
using CopperSharp.Compiler;
using CopperSharp.Targets.Amiga;
using CopperFS.Cpx4.Reference;

namespace CopperCPX.InstructionTests;

public static class ShadowMapProbe
{
    public static uint Entry()
    {
        var memory = new GuestMemory(); uint mode = memory.Read32(0x8304);
        uint table = 0, zeros = 0;
        if (mode == 6)
        {
            var owner = new CpxEngine<GuestMemory>(memory, 0x50000);
            if (owner.Initialize(512, 512, Wide.From(8192), 1) != CpxError.None) return 8;
            if (owner.EnableBufferedWrites(0x80000, CpxEngine<GuestMemory>.BufferedStorageBytes(1024)) != CpxError.None) return 9;
            table = 0x50000 + CpxArena.Scratch + 6 * 1024 + CpxArena.IndexBytes + CpxArena.PoolBytes; zeros = 0x80000;
        }
        Wide capacity = mode == 3 ? new(2, 0) : Wide.From(32768);
        if (CpxShadowMapGeometry.TryCreate(512, 1024, capacity, Wide.From(512), out var geometry) != CpxError.None) return 1;
        Wide pair = mode == 3 ? new(1, 0) : Wide.From(mode == 1 ? 32764u : mode == 2 ? 20u : 16u);
        Wide root = mode == 3 ? default : geometry.NodePairSector(geometry.Height - 1, 0) + geometry.PageSectors;
        var cursor = new CpxShadowMapReadCursor<GuestMemory>(memory, 0x70000, geometry);
        if (cursor.Begin(root, Wide.From(2), Wide.From(12345), pair, 0xc000, table, zeros) != CpxError.None) return 2;
        for (uint turn = 0; turn < 32; turn++)
        {
            var result = cursor.Step();
            if (result == StepResult.Complete) return cursor.SelectedCopy == (mode is 0 or 1 or 6 ? 1u : 0u) ? 0u : 3u;
            if (result == StepResult.Failed) return mode == 4 && cursor.Error == CpxError.Corrupt ? 0u : 4u;
            if (result != StepResult.NeedIo) continue;
            if (mode == 5) return cursor.AcceptRead(false) == CpxError.Io && cursor.Step() == StepResult.Failed ? 0u : 5u;
            Wide sector = cursor.ReadSector;
            memory.Write32(0x8004, sector.High); memory.Write32(0x8008, sector.Low);
            memory.Write32(0x800c, cursor.ReadBuffer); memory.Write32(0x8010, cursor.ReadBytes); memory.Write32(0x8000, 1);
            if (cursor.AcceptRead(true) != CpxError.None) return 6;
        }
        return 7;
    }
}

public class ShadowMapInstructions
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void CompiledRadixMapSelectsExplicitCopiesWithinFourKiBStack(int mode)
    {
        var compiled = Compilation.Value;
        Assert.True(compiled.FrameworkAnalysis.IsCompatible); Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        var bus = new ShadowCommitInstructions.Bus(); compiled.Code.CopyTo(bus.Memory, 0x10000);
        foreach (var relocation in compiled.Relocations)
        { uint at = 0x10000u + (uint)relocation.Offset; bus.Put(at, bus.Get(at) + 0x10000); }
        bus.Put(0x8304, (uint)mode);
        Assert.Equal(CpxError.None, CpxShadowMapGeometry.TryCreate(512, 1024, Wide.From(32768), Wide.From(512), out var geometry));
        void Store(uint level, ulong index, uint copy, ulong generation, byte first, byte last = 0)
        {
            ulong pair = geometry.NodePairSector(level, index);
            var image = bus.Disk.AsSpan((int)(pair + copy * geometry.PageSectors) * 512, 1024);
            Layout.Put32(image, 0, 0x43505835); Layout.Put16(image, 4, level == 0 ? (ushort)20 : (ushort)21); Layout.Put16(image, 6, 5);
            Layout.Put64(image, 16, 0x8000000000000000ul | pair); Layout.Put64(image, 24, 12345); Layout.Put64(image, 32, generation);
            uint entries = geometry.Entries(level, index);
            Layout.Put16(image, 48, (ushort)entries); Layout.Put16(image, 50, (ushort)level);
            Layout.Put16(image, 52, (ushort)(level == 0 ? (entries + 7) >> 3 : (entries + 3) >> 2));
            image[64] = first; if (level == 0) image[64 + 511] = last;
            Layout.Put32(image, 8, Layout.Crc(image, true));
        }
        Store(1, 0, 1, 2, mode == 4 ? (byte)0x80 : (byte)0x60);
        Store(0, 0, 0, 2, 0x08); Store(0, 0, 1, 3, 0);
        Store(0, 1, 1, 1, 0, 1);
        bus.Memory.AsSpan(0xee000, 8192).Fill(0xa5); bus.Put(0xf0000, 0xf1000);
        using var cpu = M68kCoreFactory.Default.Create(M68kCpuModel.M68000, bus); cpu.Reset(0x10000 + compiled.EntryPoint, 0xf0000);
        for (int instruction = 0; cpu.State.ProgramCounter != 0xf1000 && instruction < 100_000_000; instruction++)
        { cpu.ExecuteInstruction(); Assert.InRange(cpu.State.A[7], 0xef000u, 0xf0004u); Assert.False(cpu.State.Halted || cpu.State.Stopped); }
        Assert.Equal(0xf1000u, cpu.State.ProgramCounter); Assert.Equal(0u, cpu.State.D[0]);
        Assert.Equal(mode is 3 or 5 ? 0 : 2, bus.Reads); Assert.Equal(0, bus.Writes); Assert.Equal(0, bus.Flushes);
        Assert.All(bus.Memory.AsSpan(0xee000, 4096).ToArray(), b => Assert.Equal(0xa5, b));
    }
    private static readonly Lazy<M68kCompilationResult> Compilation = new(() => ShadowInstructionProfile.Compile(typeof(ShadowMapProbe)));
}
