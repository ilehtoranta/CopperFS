using Copper68k;
using CopperSharp.Compiler;
using CopperFS.Cpx4.Reference;

namespace CopperCPX.InstructionTests;

public static class InodeCatalogueProbe
{
    private static void Io(GuestMemory memory, IoKind kind, Wide sector, uint buffer, uint bytes)
    {
        memory.Write32(0x8004, sector.High); memory.Write32(0x8008, sector.Low);
        memory.Write32(0x800c, buffer); memory.Write32(0x8010, bytes); memory.Write32(0x8000, (uint)kind + 1);
    }
    public static uint Entry()
    {
        var memory = new GuestMemory(); uint mode = memory.Read32(0x8304);
        if (CpxShadowMapGeometry.TryCreate(512, 512, Wide.From(8192), Wide.From(512), out var geometry, inodeCatalogue: true) != CpxError.None) return 1;
        if (CpxInodeIdentity.TryCreate(geometry, Wide.From(64), out var identity) != CpxError.None) return 2;
        if (identity.TryAssign(Wide.From(2), Wide.From(16), out var oldId, out var next) != CpxError.None || oldId.Low != 8) return 3;
        if (identity.TryAssign(next, Wide.From(16), out var reused, out _) != CpxError.None || reused.Low != 4104 ||
            identity.Matches(oldId, reused, Wide.From(16)) || !identity.Matches(reused, reused, Wide.From(16))) return 4;
        Wide proposed = Wide.From(520);
        if (mode != 2)
        {
            var map = new CpxShadowMapWriteCursor<GuestMemory>(memory, 0x70000, geometry);
            if (map.Begin(default, Wide.From(1), Wide.From(12345), 0x9000, 2, 0x40000) != CpxError.None) return 5;
            bool finished = false;
            for (uint turn = 0; turn < 512; turn++)
            {
                var step = map.Step(); if (step == StepResult.Complete) { finished = true; break; }
                if (step == StepResult.Failed) return 6; if (step != StepResult.NeedIo) continue;
                Io(memory, map.RequestKind, map.RequestSector, map.RequestBuffer, map.RequestBytes); map.AcceptIo(true);
            }
            if (!finished) return 7; proposed = map.MappingRoot;
            var commit = new CpxShadowCommit<GuestMemory>(memory, 0x70000);
            if (commit.Initialize(512, 512, Wide.From(8192), Wide.From(12345), Wide.From(1), Wide.From(2), 0, 0x40000) != CpxError.None ||
                commit.Begin(0x9000, 2, proposed) != CpxError.None) return 8;
            finished = false;
            for (uint turn = 0; turn < 512; turn++)
            {
                var step = commit.Step(); if (step == StepResult.Complete) { finished = true; break; }
                if (step == StepResult.Failed) return 9; if (step != StepResult.NeedIo) continue;
                var request = commit.Request; Io(memory, request.Kind, request.Sector, request.Buffer, request.Bytes);
                if (commit.CompleteIo(request.Token, request.Bytes, true) != CpxError.None) return 10;
            }
            if (!finished) return 11;
        }
        for (uint item = 0; item < (mode == 2 ? 1u : 2u); item++)
        {
            var reader = new CpxShadowMapReadCursor<GuestMemory>(memory, 0x70000, geometry);
            if (reader.Begin(proposed, Wide.From(2), Wide.From(12345), Wide.From(item == 0 ? 16u : 4096u), 0x40000) != CpxError.None) return 12;
            bool finished = false;
            for (uint turn = 0; turn < 32; turn++)
            {
                var step = reader.Step();
                if (step == StepResult.Failed) return mode == 2 && reader.Error == CpxError.Corrupt ? 0u : 13u;
                if (step == StepResult.Complete) { finished = true; break; }
                if (step != StepResult.NeedIo) continue;
                Io(memory, IoKind.Read, reader.ReadSector, reader.ReadBuffer, reader.ReadBytes); reader.AcceptRead(true);
            }
            if (!finished || mode == 2 || reader.SelectedCopy != 1 || reader.IsInode != (mode == 0 ? item == 0 : item == 1)) return 14;
        }
        return 0;
    }
}

public class InodeCatalogueInstructions
{
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)]
    public void CatalogueAndFullIdentitiesCompileWithoutAllocationAndStayWithinFourKiBStack(int mode)
    {
        var compiled = Compilation.Value; Assert.True(compiled.FrameworkAnalysis.IsCompatible); Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        var bus = new ShadowCommitInstructions.Bus(); compiled.Code.CopyTo(bus.Memory, 0x10000);
        foreach (var relocation in compiled.Relocations) { uint at = 0x10000u + (uint)relocation.Offset; bus.Put(at, bus.Get(at) + 0x10000); }
        bus.Put(0x8304, (uint)mode);
        byte[] Image(ushort type, ulong tag, ulong owner, ulong generation)
        {
            var p = new byte[512]; Layout.Header(p, (PageType)type, tag, owner, generation, version: 5);
            if (type == 23) { Layout.Put16(p, 48, 1); Layout.Put16(p, 52, 256); Layout.Put64(p, 64, owner); }
            return p;
        }
        void Seal(byte[] page) => Layout.Put32(page, 8, Layout.Crc(page, true));
        if (mode != 2)
        {
            for (uint item = 0; item < 2; item++)
            {
                uint pair = item == 0 ? 16u : 4096u, buffer = item == 0 ? 0xa000u : 0xc000u;
                ushort type = mode == 0 ? item == 0 ? (ushort)23 : (ushort)11 : item == 0 ? (ushort)24 : (ushort)23;
                var page = Image(type, pair, type == 23 ? item == 0 ? 8UL : 2048UL : 0UL, 2); Seal(page); page.CopyTo(bus.Memory, buffer);
                var descriptor = bus.Memory.AsSpan((int)(0x9000 + item * 32), 32);
                Layout.Put64(descriptor, 0, pair); Layout.Put64(descriptor, 8, pair); Layout.Put32(descriptor, 16, buffer);
            }
        }
        else
        {
            var root = Image(21, 0x8000000000000208, 12345, 2); Layout.Put16(root, 48, 4); Layout.Put16(root, 50, 1); Layout.Put16(root, 52, 1); root[64] = 0x40; Seal(root); root.CopyTo(bus.Disk, 520 * 512);
            var leaf = Image(20, 0x8000000000000200, 12345, 2); Layout.Put16(leaf, 48, 1024); Layout.Put16(leaf, 52, 256);
            // CRC-valid selected node with the wrong authenticated leaf width.
            Layout.Put32(leaf, 12, 0); Seal(leaf); leaf.CopyTo(bus.Disk, 512 * 512);
        }
        bus.Memory.AsSpan(0xee000, 8192).Fill(0xa5); bus.Put(0xf0000, 0xf1000);
        using var cpu = M68kCoreFactory.Default.Create(M68kCpuModel.M68000, bus); cpu.Reset(0x10000 + compiled.EntryPoint, 0xf0000);
        for (int count = 0; cpu.State.ProgramCounter != 0xf1000 && count < 100_000_000; count++)
        { cpu.ExecuteInstruction(); Assert.InRange(cpu.State.A[7], 0xef000u, 0xf0004u); Assert.False(cpu.State.Halted || cpu.State.Stopped); }
        Assert.Equal(0xf1000u, cpu.State.ProgramCounter); Assert.Equal(0u, cpu.State.D[0]);
        Assert.All(bus.Memory.AsSpan(0xee000, 4096).ToArray(), b => Assert.Equal(0xa5, b));
        Assert.Equal(mode == 2 ? 0 : 2, bus.Flushes);
    }
    private static readonly Lazy<M68kCompilationResult> Compilation = new(() => ShadowInstructionProfile.Compile(typeof(InodeCatalogueProbe)));
}
