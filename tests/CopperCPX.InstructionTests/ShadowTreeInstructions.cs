using Copper68k;
using CopperSharp.Compiler;
using CopperSharp.Targets.Amiga;
using CopperFS.Cpx4.Reference;

namespace CopperCPX.InstructionTests;

public static class ShadowTreeProbe
{
    private static void Io(GuestMemory memory, IoKind kind, Wide sector, uint buffer, uint bytes)
    {
        memory.Write32(0x8004, sector.High); memory.Write32(0x8008, sector.Low);
        memory.Write32(0x800c, buffer); memory.Write32(0x8010, bytes); memory.Write32(0x8000, (uint)kind + 1);
    }
    public static uint Entry()
    {
        var memory = new GuestMemory(); uint mode = memory.Read32(0x8304);
        uint generation = mode is 1 or 2 ? 2u : 1u, count = mode is 1 or 2 ? 1u : 2u;
        uint table = 0, zeros = 0;
        if (mode == 4)
        {
            var checksumOwner = new CpxEngine<GuestMemory>(memory, 0x50000);
            if (checksumOwner.Initialize(512, 512, Wide.From(8192), 1) != CpxError.None) return 11;
            if (checksumOwner.EnableBufferedWrites(0x80000, CpxEngine<GuestMemory>.BufferedStorageBytes(1024)) != CpxError.None) return 12;
            table = 0x50000 + CpxArena.Scratch + 6 * 1024 + CpxArena.IndexBytes + CpxArena.PoolBytes; zeros = 0x80000;
        }
        Wide oldRoot = mode is 1 or 2 ? Wide.From(516) : default;
        if (CpxShadowMapGeometry.TryCreate(512, 512, Wide.From(8192), Wide.From(512), out var geometry) != CpxError.None) return 1;
        var map = new CpxShadowMapWriteCursor<GuestMemory>(memory, 0x70000, geometry);
        if (map.Begin(oldRoot, Wide.From(generation), Wide.From(12345), 0x9000, count, 0x40000, table, zeros) != CpxError.None) return 2;
        bool mapped = false;
        for (uint turn = 0; turn < 512; turn++)
        {
            var step = map.Step();
            if (step == StepResult.Complete) { mapped = true; break; }
            if (step == StepResult.Failed) return mode == 3 && map.Error == CpxError.Cancelled ? 0u : 3u;
            if (step != StepResult.NeedIo) continue;
            Io(memory, map.RequestKind, map.RequestSector, map.RequestBuffer, map.RequestBytes);
            if (mode == 3) map.Cancel();
            map.AcceptIo(true);
        }
        if (!mapped) return 4;
        Wide proposedRoot = map.MappingRoot;
        // Retirement can reuse quiescent mapping state and frame storage.
        var commit = new CpxShadowCommit<GuestMemory>(memory, mode == 4 ? 0x70000u : 0x71000u);
        if (commit.Initialize(512, 512, Wide.From(8192), Wide.From(12345), Wide.From(generation), Wide.From(2), 1, mode == 4 ? 0x40000u : 0xc000u, oldRoot, table, zeros) != CpxError.None) return 5;
        if (commit.Begin(0x9000, count, proposedRoot) != CpxError.None) return 6;
        for (uint turn = 0; turn < 512; turn++)
        {
            var step = commit.Step();
            if (step == StepResult.Complete) return commit.DurableGeneration.Low == generation + 1 ? 0u : 7u;
            if (step == StepResult.Failed) return 8;
            if (step != StepResult.NeedIo) continue;
            var request = commit.Request; Io(memory, request.Kind, request.Sector, request.Buffer, request.Bytes);
            if (commit.CompleteIo(request.Token, request.Bytes, true) != CpxError.None) return 9;
        }
        return 10;
    }
}

public class ShadowTreeInstructions
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void CompiledTreePublicationAndRetryKeepFourKiBStack(int mode)
    {
        var compiled = Compilation.Value;
        Assert.True(compiled.FrameworkAnalysis.IsCompatible); Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        var bus = new ShadowCommitInstructions.Bus(); compiled.Code.CopyTo(bus.Memory, 0x10000);
        foreach (var relocation in compiled.Relocations)
        { uint at = 0x10000u + (uint)relocation.Offset; bus.Put(at, bus.Get(at) + 0x10000); }
        bus.Put(0x8304, (uint)mode);
        uint generation = mode is 1 or 2 ? 2u : 1u;
        byte[] Page(ushort type, ulong tag, ulong owner, ulong version, byte marker)
        {
            byte[] image = new byte[512]; Layout.Put32(image, 0, 0x43505835); Layout.Put16(image, 4, type); Layout.Put16(image, 6, 5);
            Layout.Put64(image, 16, tag); Layout.Put64(image, 24, owner); Layout.Put64(image, 32, version); image[64] = marker; return image;
        }
        void Store(ulong sector, byte[] image)
        { Layout.Put32(image, 8, Layout.Crc(image, true)); image.CopyTo(bus.Disk, (int)sector * 512); }
        for (uint slot = 0; slot < 3; slot++)
        {
            var root = Page(2, 2 + slot, 0, generation, 0); Layout.Put16(root, 52, 32);
            Layout.Put64(root, 72, 12345); Layout.Put64(root, 80, generation); Layout.Put64(root, 88, generation == 2 ? 516ul : 0);
            Store(2 + slot, root);
        }
        foreach (ulong pair in new ulong[] { 16, 4096 })
        for (uint copy = 0; copy < 2; copy++) Store(pair + copy, Page(11, pair, 0, generation, (byte)generation));
        void MapNode(uint level, uint index, uint copy, ulong version, byte bits)
        {
            uint pair = level == 0 ? 512 + index * 2 : 516;
            var image = Page(level == 0 ? (ushort)20 : (ushort)21, 0x8000000000000000 | pair, 12345, version, bits);
            Layout.Put16(image, 48, level == 0 ? (ushort)2048 : (ushort)2); Layout.Put16(image, 50, (ushort)level);
            Layout.Put16(image, 52, level == 0 ? (ushort)256 : (ushort)1);
            if (level == 0 && index == 0 && version == 2) image[65] = 0x80;
            Store(pair + copy, image);
        }
        if (generation == 2)
        { MapNode(1, 0, 0, 2, 0x50); MapNode(0, 0, 0, 2, 0); MapNode(0, 1, 0, 2, 0x80); }
        if (mode == 2)
        {
            // Abandoned G+1 state exists on the inactive copies. The new write
            // changes only B and must not make the abandoned A visible.
            MapNode(1, 0, 1, 3, 0x90); MapNode(0, 0, 1, 3, 0);
            Store(16, Page(11, 16, 0, 3, 9));
        }
        uint count = 0;
        void Add(ulong pair, uint buffer)
        {
            var image = Page(11, pair, 0, generation + 1, (byte)(generation + 1)); Layout.Put32(image, 8, Layout.Crc(image, true)); image.CopyTo(bus.Memory, buffer);
            var descriptor = bus.Memory.AsSpan(0x9000 + (int)count * 32, 32);
            Layout.Put64(descriptor, 0, pair); Layout.Put64(descriptor, 8, pair); Layout.Put32(descriptor, 16, buffer); count++;
        }
        if (mode == 1) Add(16, 0xa000);
        else if (mode == 2) Add(4096, 0xb000);
        else { Add(4096, 0xb000); Add(16, 0xa000); }
        bus.Memory.AsSpan(0xee000, 8192).Fill(0xa5); bus.Put(0xf0000, 0xf1000);
        using var cpu = M68kCoreFactory.Default.Create(M68kCpuModel.M68000, bus); cpu.Reset(0x10000 + compiled.EntryPoint, 0xf0000);
        for (int instruction = 0; cpu.State.ProgramCounter != 0xf1000 && instruction < 100_000_000; instruction++)
        { cpu.ExecuteInstruction(); Assert.InRange(cpu.State.A[7], 0xef000u, 0xf0004u); Assert.False(cpu.State.Halted || cpu.State.Stopped); }
        Assert.Equal(0xf1000u, cpu.State.ProgramCounter); Assert.Equal(0u, cpu.State.D[0]);
        Assert.Equal(mode == 3 ? 0 : 2, bus.Flushes);
        Assert.All(bus.Memory.AsSpan(0xee000, 4096).ToArray(), b => Assert.Equal(0xa5, b));
        if (mode == 3) return;
        var checkpoint = bus.Disk.AsSpan(2 * 512, 512);
        ulong rootAddress = Layout.U64(checkpoint, 88); Assert.Equal(generation + 1, Layout.U64(checkpoint, 80));
        uint Copy(ulong pair)
        {
            var branch = bus.Disk.AsSpan((int)rootAddress * 512, 512); uint child = pair == 16 ? 0u : 1u;
            uint selected = ((uint)branch[64] >> (int)(6 - child * 2)) & 3;
            Assert.InRange(selected, 1u, 2u);
            var leaf = bus.Disk.AsSpan((int)(512 + child * 2 + selected - 1) * 512, 512);
            uint bit = (uint)(pair >> 1) & 2047;
            return ((uint)leaf[(int)(64 + (bit >> 3))] >> (int)(7 - (bit & 7))) & 1;
        }
        Assert.Equal(mode == 1 ? 3 : 2, bus.Disk[(16 + Copy(16)) * 512 + 64]);
        Assert.Equal(mode == 2 ? 3 : 2, bus.Disk[(4096 + Copy(4096)) * 512 + 64]);
    }
    private static readonly Lazy<M68kCompilationResult> Compilation = new(() => ShadowInstructionProfile.Compile(typeof(ShadowTreeProbe)));
}
