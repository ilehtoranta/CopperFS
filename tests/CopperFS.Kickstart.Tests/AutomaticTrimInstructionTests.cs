#if COPPERFS_AUTOTRIM
using System.Buffers.Binary;
using Amiga;
using Copper68k;
using CopperSharp.Compiler;
using CopperSharp.Targets.Amiga;
using CopperSFS;
using CopperStart.Dos;

namespace CopperFS.Kickstart.Tests;

public sealed class AutomaticTrimInstructionTests
{
    [Theory]
    [InlineData(M68kCpuModel.M68000)] [InlineData(M68kCpuModel.M68020)]
    public void FreedHintsAreVolatileAndLifecycleResetClearsThem(M68kCpuModel model)
        => Run(model, typeof(AutomaticTrimInvalidationProbe));

    [Theory]
    [InlineData(M68kCpuModel.M68000)] [InlineData(M68kCpuModel.M68020)]
    public void PermanentStagingAllocationFailureDisablesOnlyMaintenance(M68kCpuModel model)
        => Run(model, typeof(AutomaticTrimAllocationFailureProbe));

    [Theory]
    [InlineData(M68kCpuModel.M68000)] [InlineData(M68kCpuModel.M68020)]
    public void DeviceTransferFailureInvalidatesCapabilitiesUntilRemount(M68kCpuModel model)
        => Run(model, typeof(AutomaticTrimTransportFailureProbe));

    [Theory]
    [InlineData(M68kCpuModel.M68000)] [InlineData(M68kCpuModel.M68020)]
    public void EightDescriptorUnmapExecutesWithCachedDiscovery(M68kCpuModel model)
    {
        var bus = Run(model, typeof(AutomaticTrimVectorProbe));
        Assert.Equal(5u, bus.Get(0x6000)); // Three discovery commands and two UNMAPs.
        Assert.Equal(136u, bus.Get(0x6004));
        Assert.Equal(134, BinaryPrimitives.ReadUInt16BigEndian(bus.Memory.AsSpan(0xC000)));
        Assert.Equal(128, BinaryPrimitives.ReadUInt16BigEndian(bus.Memory.AsSpan(0xC002)));
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(1u, bus.Get((uint)(0xC008 + i * 16)));
            Assert.Equal((uint)(100 + i * 10), bus.Get((uint)(0xC00C + i * 16)));
            Assert.Equal(3u, bus.Get((uint)(0xC010 + i * 16)));
        }
    }

    private static TrimBus Run(M68kCpuModel model, Type probe)
    {
        var compiled = AmigaM68kCompiler.Compile(new M68kCompilationRequest
        {
            AssemblyPath = probe.Assembly.Location,
            EntryPoint = probe.FullName + "::Entry",
            ManagedAssemblyPaths = [typeof(NativeAmigaFileSystem).Assembly.Location,
                typeof(APTR).Assembly.Location, typeof(IAmigaGuestMemory).Assembly.Location,
                typeof(AmigaFreedRangeQueue).Assembly.Location],
            IncludedExportNames = [], Cpu = M68kCpuTarget.M68000,
            OutputFormat = M68kOutputFormat.Assembly, RuntimeProfile = M68kRuntimeProfile.Freestanding,
            MemoryManagement = M68kMemoryManagement.None, ExceptionMode = M68kExceptionMode.Yolo
        });
        Assert.True(compiled.FrameworkAnalysis.IsCompatible);
        Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        var bus = new TrimBus(); compiled.Code.CopyTo(bus.Memory, 0x10000);
        // Exec AllocMem failure fixture. Both preferred and Chip fallback return
        // zero, while the real compiled initialization path executes unchanged.
        bus.Put(4, 0x8000);
        BinaryPrimitives.WriteUInt16BigEndian(bus.Memory.AsSpan(0x8000 - 198), 0x4EF9);
        bus.Put(0x8000 - 196, 0x9000);
        bus.Put(0x9000, 0x70004E75); // MOVEQ #0,D0; RTS
        BinaryPrimitives.WriteUInt16BigEndian(bus.Memory.AsSpan(0x8000 - 456), 0x4EF9);
        bus.Put(0x8000 - 454, 0x9100);
        bus.Put(0x9100, 0x137C0001); // MOVE.B #1,31(A1): io_Error
        bus.Put(0x9104, 0x001F70FF); // MOVEQ #-1,D0
        BinaryPrimitives.WriteUInt16BigEndian(bus.Memory.AsSpan(0x9108), 0x4E75);
        foreach (var relocation in compiled.Relocations)
        {
            var at = 0x10000u + (uint)relocation.Offset;
            bus.Put(at, bus.Get(at) + 0x10000);
        }
        bus.Put(0xF0000, 0xF1000);
        using var cpu = M68kCoreFactory.Default.Create(model, bus);
        cpu.Reset(0x10000 + compiled.EntryPoint, 0xF0000);
        for (var n = 0; n < 1_000_000 && cpu.State.ProgramCounter != 0xF1000; n++)
        { cpu.ExecuteInstruction(); Assert.False(cpu.State.Halted || cpu.State.Stopped); }
        Assert.Equal(0xF1000u, cpu.State.ProgramCounter);
        Assert.Equal(0u, cpu.State.D[0]);
        return bus;
    }

    // Same minimal bus contract as GeometryMathInstructionTests.
    private sealed class TrimBus : IM68kBus
    {
        public byte[] Memory { get; } = new byte[0x100000];
        public uint Get(uint a) => BinaryPrimitives.ReadUInt32BigEndian(Memory.AsSpan((int)a, 4));
        public void Put(uint a, uint v) => BinaryPrimitives.WriteUInt32BigEndian(Memory.AsSpan((int)a, 4), v);
        public void ResetExternalDevices(long cycle) { }
        public byte ReadByte(uint a, ref long c, M68kBusAccessKind k) => Memory[a];
        public ushort ReadWord(uint a, ref long c, M68kBusAccessKind k) => BinaryPrimitives.ReadUInt16BigEndian(Memory.AsSpan((int)a, 2));
        public uint ReadLong(uint a, ref long c, M68kBusAccessKind k) => Get(a);
        public void WriteByte(uint a, byte v, ref long c, M68kBusAccessKind k) => Memory[a] = v;
        public void WriteWord(uint a, ushort v, ref long c, M68kBusAccessKind k) => BinaryPrimitives.WriteUInt16BigEndian(Memory.AsSpan((int)a, 2), v);
        public void WriteLong(uint a, uint v, ref long c, M68kBusAccessKind k) => Put(a, v);
    }
}

public static class AutomaticTrimInvalidationProbe
{
    public static uint Entry()
    {
        var state = APTR.FromPointer(0x3000);
        if (!AmigaVolumeGeometry.Create(9003, 512, 2, out var geometry)) return 1;
        NativeAmigaFileSystem.SetGeometry(state, geometry);
        var p = new NativeFileSystemPlatform(state);
        p.ObserveFreedRange(64, 2);
        p.ObserveFreedRange(66, 3);
        var queue = APTR.FromPointer(state.Raw + (uint)NativeAmigaFileSystem.AutoTrimQueueOffset);
        if (AmigaFreedRangeQueue.Count(ref p, queue) != 1 || !NativeAutomaticTrim.Pending(state)) return 2;
        if (APTR.ReadUInt32(queue, 4) != 64 || APTR.ReadUInt32(queue, 8) != 5) return 3;
        if (NativeAutomaticTrim.Ready(state)) return 4;
        NativeAutomaticTrim.MakeEligible(state);
        if (!NativeAutomaticTrim.Ready(state)) return 5;
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.InhibitedOffset, 1);
        if (NativeAutomaticTrim.Ready(state)) return 6;
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.InhibitedOffset, 0);
        var batch = APTR.FromPointer(state.Raw + (uint)NativeAmigaFileSystem.AutoTrimBatchOffset);
        APTR.WriteUInt32(batch, AmigaAutomaticTrimBatch.Inputs, 2);
        APTR.WriteUInt32(batch, AmigaAutomaticTrimBatch.Outputs, 1);
        NativeAutomaticTrim.BeforeOperation(state);
        if (APTR.ReadUInt32(batch, AmigaAutomaticTrimBatch.Inputs) != 0 || APTR.ReadUInt32(batch, AmigaAutomaticTrimBatch.Outputs) != 0) return 7;
        APTR.WriteUInt32(batch, AmigaAutomaticTrimBatch.Outputs, 1);
        APTR.WriteUInt32(batch, AmigaAutomaticTrimBatch.Output, 100);
        APTR.WriteUInt32(batch, AmigaAutomaticTrimBatch.Output + 4, 32);
        NativeAutomaticTrim.TransportFailed(state);
        if (NativeAutomaticTrim.Ready(state) || p.TracksFreedRanges ||
            APTR.ReadUInt32(state, NativeAmigaFileSystem.AutoTrimRecordOffset + 4) != 2 ||
            APTR.ReadUInt32(batch, AmigaAutomaticTrimBatch.Output) != 100 ||
            APTR.ReadUInt32(batch, AmigaAutomaticTrimBatch.Output + 4) != 32) return 10;
        NativeAutomaticTrim.Reset(state);
        for (var i = 0u; i < 200; i++) p.ObserveFreedRange(100 + i * 3, 1);
        if (AmigaFreedRangeQueue.Count(ref p, queue) != 128) return 8; // 72 best-effort hints dropped.
        // A geometry/media reset invalidates cached positive and negative
        // discovery as well as every queued hint and prepared generation.
        APTR.WriteUInt32(state, NativeAmigaFileSystem.AutoTrimRecordOffset + 4, 2);
        NativeAmigaFileSystem.SetGeometry(state, geometry);
        if (APTR.ReadUInt32(state, NativeAmigaFileSystem.AutoTrimRecordOffset + 4) != 0) return 9;
        return AmigaFreedRangeQueue.Count(ref p, queue) |
            APTR.ReadUInt32(state, NativeAmigaFileSystem.AutoTrimPendingOffset);
    }
}

public static class AutomaticTrimAllocationFailureProbe
{
    public static uint Entry()
    {
        var state = APTR.FromPointer(0x3000);
        if (!AmigaVolumeGeometry.Create(9003, 512, 2, out var g)) return 1;
        NativeAmigaFileSystem.SetGeometry(state, g);
        NativeAutomaticTrim.Initialize(state);
        var p = new NativeFileSystemPlatform(state);
        if (p.TracksFreedRanges || APTR.ReadUInt32(state, NativeAmigaFileSystem.AutoTrimPendingOffset) != 2) return 2;
        p.ObserveFreedRange(100, 32);
        if (NativeAutomaticTrim.Pending(state) || p.BlockCount != 9003) return 3;
        return 0;
    }
}

public static class AutomaticTrimTransportFailureProbe
{
    public static uint Entry()
    {
        var state = APTR.FromPointer(0x3000);
        if (!AmigaVolumeGeometry.Create(9003, 512, 2, out var g)) return 1;
        NativeAmigaFileSystem.SetGeometry(state, g);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.RequestOffset, 0x7000);
        var p = new NativeFileSystemPlatform(state);
        p.ObserveFreedRange(100, 32); NativeAutomaticTrim.MakeEligible(state);
        if (p.Transfer(2, 0, 512, APTR.FromPointer(0xA000)) || p.TracksFreedRanges || NativeAutomaticTrim.Ready(state)) return 2;
        NativeAutomaticTrim.Reset(state);
        p.ObserveFreedRange(100, 32); NativeAutomaticTrim.MakeEligible(state);
        if (p.ScsiDirect(APTR.FromPointer(0xA000)) || p.TracksFreedRanges || NativeAutomaticTrim.Ready(state)) return 3;
        return APTR.ReadUInt32(state, NativeAmigaFileSystem.AutoTrimRecordOffset + 4) == 2 ? 0u : 4u;
    }
}

public static class AutomaticTrimVectorProbe
{
    public static uint Entry()
    {
        var p = new Memory(APTR.FromPointer(0x6000)); var control = APTR.FromPointer(0xA000); var ranges = APTR.FromPointer(0xB000);
        if (!AmigaVolumeGeometry.Create(10000, 512, 1, out var g)) return 1;
        g.StartByte = 0x20000000000UL;
        if (!AmigaScsiUnmap.Query(ref p, g, control, out _)) return 2;
        for (var i = 0u; i < 8; i++)
        { p.WriteUInt32(ranges, (int)i * 8, 100 + i * 10); p.WriteUInt32(ranges, (int)i * 8 + 4, 3); }
        if (!AmigaScsiUnmap.DiscardRanges(ref p, g, control, ranges, 8)) return 3;
        return AmigaScsiUnmap.DiscardRanges(ref p, g, control, ranges, 8) ? 0u : 4u;
    }

    private struct Memory(APTR state) : IAmigaScsiDirectPlatform
    {
        public bool ScsiDirect(APTR command)
        {
            var cdb = APTR.FromPointer(ReadUInt32(command, 12)); var data = APTR.FromPointer(ReadUInt32(command));
            var bytes = ReadUInt32(command, 4);
            WriteUInt32(state, 0, ReadUInt32(state) + 1);
            WriteUInt16(command, 18, ReadUInt16(command, 16)); WriteUInt32(command, 8, bytes);
            if (ReadUInt8(cdb) == 0x12)
            {
                var page = ReadUInt8(cdb, 2); WriteUInt8(data, 1, page);
                WriteUInt16(data, 2, page == 0xB2 ? (ushort)4 : (ushort)60);
                if (page == 0xB2) WriteUInt8(data, 5, 128);
                else { WriteUInt32(data, 20, 4096); WriteUInt32(data, 24, 8); }
            }
            else if (ReadUInt8(cdb) == 0x9E)
            { WriteUInt32(data, 0, 2); WriteUInt32(data, 4, uint.MaxValue); WriteUInt32(data, 8, 512); }
            else
            { WriteUInt32(state, 4, bytes); Copy(data, APTR.FromPointer(0xC000), bytes); }
            return true;
        }
        public byte ReadUInt8(APTR a, int offset = 0) => APTR.ReadUInt8(a, offset);
        public ushort ReadUInt16(APTR a, int offset = 0) => APTR.ReadUInt16(a, offset);
        public uint ReadUInt32(APTR a, int offset = 0) => APTR.ReadUInt32(a, offset);
        public void WriteUInt8(APTR a, int offset, byte value) => APTR.WriteUInt8(a, offset, value);
        public void WriteUInt16(APTR a, int offset, ushort value) => APTR.WriteUInt16(a, offset, value);
        public void WriteUInt32(APTR a, int offset, uint value) => APTR.WriteUInt32(a, offset, value);
        public bool IsMapped(APTR a, uint bytes) => a.Raw < 0xF0000 && bytes <= 0xF0000 - a.Raw;
        public void Clear(APTR a, uint bytes) { for (var i = 0u; i < bytes; i++) APTR.WriteUInt8(a, (int)i, 0); }
        public void Copy(APTR a, APTR b, uint bytes) { for (var i = 0u; i < bytes; i++) APTR.WriteUInt8(b, (int)i, APTR.ReadUInt8(a, (int)i)); }
    }
}
#endif
