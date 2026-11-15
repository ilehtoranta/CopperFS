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
            typeof(APTR).Assembly.Location, typeof(IAmigaGuestMemory).Assembly.Location,
            typeof(CopperSFS.AmigaSmartFileSystemRedoArena).Assembly.Location],
        IncludedExportNames = [], Cpu = M68kCpuTarget.M68000, OutputFormat = M68kOutputFormat.Assembly,
        RuntimeProfile = M68kRuntimeProfile.Freestanding, MemoryManagement = M68kMemoryManagement.None,
        ExceptionMode = M68kExceptionMode.Yolo, PeepholeOptimization = M68kPeepholeOptimizationMode.FixedPoint
    }));

    [Theory]
    [InlineData(M68kCpuModel.M68000, false)]
    [InlineData(M68kCpuModel.M68020, false)] [InlineData(M68kCpuModel.M68020, true)]
    public void AllCopyParitiesPreserveExactRangesAndGuards(M68kCpuModel model, bool unaligned)
    {
        var compiled = Compilation.Value;
        Assert.True(compiled.FrameworkAnalysis.IsCompatible);
        Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        for (var sourceAlignment = 0u; sourceAlignment < 4; sourceAlignment++)
        for (var targetAlignment = 0u; targetAlignment < 4; targetAlignment++)
        foreach (var length in new uint[] { 0, 1, 2, 3, 4, 5, 7, 8, 9, 31, 32, 33, 511, 512, 513 })
        {
            var bus = new CopyBus { Unaligned = unaligned }; compiled.Code.CopyTo(bus.Memory, 0x10000);
            bus.Put(4, 0x2000);
            BinaryPrimitives.WriteUInt16BigEndian(bus.Memory.AsSpan(0x2000 + 296, 2), unaligned ? (ushort)2 : (ushort)0);
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

#if COPPERFS_SFS2_OWNED
    [Theory]
    [InlineData(63u, 64u)] [InlineData(64u, uint.MaxValue)]
    [InlineData(71u, uint.MaxValue)] [InlineData(72u, 64u)]
    public void OwnedWritesInvalidateOnlyIntersectingPayloadWindow(uint block, uint expectedWindow)
    {
        var compiled = Compilation.Value;
        Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        var bus = new CopyBus(); compiled.Code.CopyTo(bus.Memory, 0x10000);
        foreach (var relocation in compiled.Relocations)
        {
            var address = 0x10000u + (uint)relocation.Offset;
            bus.Put(address, bus.Get(address) + 0x10000);
        }
        const uint state = 0x3000, map = 0x6000, source = 0x9000, target = 0xA000;
        bus.Put(state + NativeFileSystemStateCodec.BlockBytesOffset, 512);
        bus.Put(state + NativeFileSystemStateCodec.BlockCountOffset, 128);
        bus.Put(state + NativeFileSystemStateCodec.ReservedBlocksOffset, 1);
        bus.Put(state + NativeFileSystemStateCodec.RamBlocksOffset, map);
        bus.Put(state + NativeFileSystemStateCodec.CacheTrackOffset, 64);
        bus.Put(state + NativeFileSystemStateCodec.CacheBlocksOffset, 8);
        bus.Put(state + NativeFileSystemStateCodec.CacheOffset, 0xC000);
        bus.Put(map + block * 4, target);
        for (var i = 0; i < 512; i++) bus.Memory[source + i] = (byte)(i * 17 + 3);
        bus.Memory.AsSpan(0xC000, 4096).Fill(0xA7);
        bus.Put(0x1000, block); bus.Put(0x1004, source); bus.Put(0x100C, 1);
        bus.Put(0xF0000, 0xF1000);
        using var cpu = M68kCoreFactory.Default.Create(M68kCpuModel.M68000, bus);
        cpu.Reset(0x10000 + compiled.EntryPoint, 0xF0000);
        for (var n = 0; n < 100000 && cpu.State.ProgramCounter != 0xF1000; n++)
        { cpu.ExecuteInstruction(); Assert.False(cpu.State.Halted || cpu.State.Stopped); }
        Assert.Equal(0xF1000u, cpu.State.ProgramCounter);
        Assert.Equal(1u, cpu.State.D[0]);
        Assert.Equal(expectedWindow, bus.Get(state + NativeFileSystemStateCodec.CacheTrackOffset));
        Assert.Equal(bus.Memory.AsSpan((int)source, 512).ToArray(), bus.Memory.AsSpan((int)target, 512).ToArray());
        Assert.All(bus.Memory.AsSpan(0xC000, 4096).ToArray(), b => Assert.Equal(0xA7, b));
    }
#endif

    private sealed class CopyBus : IM68kBus
    {
        public readonly byte[] Memory = new byte[0x100000];
        public uint Source, Target, Bytes;
        public bool Unaligned;
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
        { Range(a, 2, false); if (!Unaligned) Assert.Equal(0u, a & 1); return BinaryPrimitives.ReadUInt16BigEndian(Memory.AsSpan((int)a, 2)); }
        public uint ReadLong(uint a, ref long c, M68kBusAccessKind k)
        { Range(a, 4, false); if (!Unaligned) Assert.Equal(0u, a & 1); return Get(a); }
        public void WriteByte(uint a, byte v, ref long c, M68kBusAccessKind k)
        { Range(a, 1, true); Memory[a] = v; }
        public void WriteWord(uint a, ushort v, ref long c, M68kBusAccessKind k)
        { Range(a, 2, true); if (!Unaligned) Assert.Equal(0u, a & 1); BinaryPrimitives.WriteUInt16BigEndian(Memory.AsSpan((int)a, 2), v); }
        public void WriteLong(uint a, uint v, ref long c, M68kBusAccessKind k)
        { Range(a, 4, true); if (!Unaligned) Assert.Equal(0u, a & 1); Put(a, v); }
    }
}

public static class CopyProbe
{
    public static uint Entry()
    {
        var p = new NativeFileSystemPlatform(APTR.FromPointer(0x3000));
        var args = APTR.FromPointer(0x1000);
#if COPPERFS_SFS2_OWNED
        if (APTR.ReadUInt32(args, 12) != 0)
            return p.WriteBlock(APTR.ReadUInt32(args, 0), APTR.FromPointer(APTR.ReadUInt32(args, 4))) ? 1u : 0u;
#endif
        p.Copy(APTR.FromPointer(APTR.ReadUInt32(args, 0)), APTR.FromPointer(APTR.ReadUInt32(args, 4)), APTR.ReadUInt32(args, 8));
        return 0;
    }
}
