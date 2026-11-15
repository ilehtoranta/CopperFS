#if COPPERFS_SFS_GROUPED
using System.Buffers.Binary;
using Amiga;
using Copper68k;
using CopperSharp.Compiler;
using CopperSharp.Targets.Amiga;
using CopperSFS;
using CopperStart.Dos;

namespace CopperFS.Kickstart.Tests;

public sealed class SfsCapacityInstructionTests
{
    private static byte[]? code;
    private static uint entry;

    [Theory]
    [InlineData(M68kCpuModel.M68000, 512u, 0, true)]
    [InlineData(M68kCpuModel.M68020, 512u, 0, true)]
    [InlineData(M68kCpuModel.M68000, 512u, 1, true)]
    [InlineData(M68kCpuModel.M68020, 512u, 1, true)]
    [InlineData(M68kCpuModel.M68000, 512u, 2, true)]
    [InlineData(M68kCpuModel.M68020, 512u, 2, true)]
    [InlineData(M68kCpuModel.M68020, 512u, 0, false)]
    [InlineData(M68kCpuModel.M68020, 1024u, 0, true)]
    [InlineData(M68kCpuModel.M68020, 2048u, 0, true)]
    [InlineData(M68kCpuModel.M68020, 4096u, 0, true)]
    public void ExistingBudgetAndAllocationFailurePreserveSafeRejectionPolicy(M68kCpuModel model, uint bytes, int failures, bool enumeration)
    {
        if (code == null)
        {
            var result = AmigaM68kCompiler.Compile(new M68kCompilationRequest
            {
                AssemblyPath = typeof(SfsCapacityInstructionProbe).Assembly.Location,
                EntryPoint = typeof(SfsCapacityInstructionProbe).FullName + "::Entry",
                ManagedAssemblyPaths = [typeof(NativeAmigaFileSystem).Assembly.Location, typeof(APTR).Assembly.Location,
                    typeof(IAmigaGuestMemory).Assembly.Location, typeof(AmigaVolumeGeometry).Assembly.Location],
                IncludedExportNames = [], Cpu = M68kCpuTarget.M68000, OutputFormat = M68kOutputFormat.Assembly,
                RuntimeProfile = M68kRuntimeProfile.Freestanding, MemoryManagement = M68kMemoryManagement.None,
                ExceptionMode = M68kExceptionMode.Yolo
            });
            Assert.True(result.FrameworkAnalysis.IsCompatible); Assert.Empty(result.FrameworkAnalysis.ManagedAllocationSites);
            code = result.Code.ToArray(); entry = result.EntryPoint;
            foreach (var relocation in result.Relocations)
                BinaryPrimitives.WriteUInt32BigEndian(code.AsSpan(relocation.Offset), BinaryPrimitives.ReadUInt32BigEndian(code.AsSpan(relocation.Offset)) + 0x10000);
        }
        var bus = new CapacityBus(); code.CopyTo(bus.Memory, 0x10000);
        bus.Put(4, 0x8000); bus.Word(0x8000 - 198, 0x4EF9); bus.Put(0x8000 - 196, 0x9000); bus.Word(0x9000, 0x4E75);
        bus.Put(0x6000, bytes); bus.Put(0x6004, enumeration ? 0x100000u : 0);
        bus.Put(0x3E0000, 0x3F0000);
        using var cpu = M68kCoreFactory.Default.Create(model, bus); cpu.Reset(0x10000 + entry, 0x3E0000);
        var requests = new List<uint>();
        for (var step = 0; step < 2_000_000 && cpu.State.ProgramCounter != 0x3F0000; step++)
        {
            if (cpu.State.ProgramCounter == 0x9000)
            { requests.Add(cpu.State.D[0]); cpu.State.D[0] = requests.Count <= failures ? 0u : 0x200000u; }
            cpu.ExecuteInstruction(); Assert.False(cpu.State.Halted || cpu.State.Stopped);
        }
        Assert.Equal(0x3F0000u, cpu.State.ProgramCounter); Assert.Equal(0u, cpu.State.D[0]);
        var previous = AmigaSmartFileSystemWriteCore.TrstGroupBytes(220 + 2 * (88 + 8 * (bytes + 8)), bytes);
        Assert.InRange(requests.Count, 1, enumeration ? 2 : 1);
        if (failures != 0) Assert.Equal(previous, requests[^1]);
        if (enumeration)
            Assert.True(requests[0] + NativeAmigaFileSystem.SmartEnumerationCacheBytes <= previous + 4 + 32 * NativeAmigaFileSystem.SmartEnumerationOriginalEntryBytes);
#if COPPERFS_SFS_POOL
        var previousBudget = Math.Min(192u * 1024, 220 + 2 * (88 + 8 * (bytes + 8)) + 32 *
            (NativeAmigaFileSystem.SmartEnumerationOriginalEntryBytes - 240));
        var previousPool = AmigaSmartFileSystemWriteCore.TrstGroupBytes(previousBudget, bytes);
        if (enumeration) Assert.True(requests[0] + 32 * NativeAmigaFileSystem.SmartEnumerationEntryBytes <= previousPool + 32 * 240);
#endif
        if (failures == 2) Assert.Equal(0u, bus.Get(0x3000 + NativeFileSystemStateCodec.SmartRangeBufferOffset));
        else
        {
            Assert.Equal(0x200000u, bus.Get(0x3000 + NativeFileSystemStateCodec.SmartRangeBufferOffset));
#if COPPERFS_SFS_POOL
            if (bytes == 512) Assert.Equal(enumeration && failures == 0 ? 22u : 15u, bus.Get(bus.Get(0x200000 + 16) + 4));
#else
            if (bytes == 512) Assert.Equal(enumeration && failures == 0 ? 9u : 8u, bus.Get(bus.Get(0x200000 + 16) + 4));
#endif
        }
    }

    private sealed class CapacityBus : IM68kBus
    {
        public byte[] Memory { get; } = new byte[0x400000];
        public uint Get(uint a) => BinaryPrimitives.ReadUInt32BigEndian(Memory.AsSpan((int)a, 4));
        public void Put(uint a, uint value) => BinaryPrimitives.WriteUInt32BigEndian(Memory.AsSpan((int)a, 4), value);
        public void Word(uint a, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(Memory.AsSpan((int)a, 2), value);
        public void ResetExternalDevices(long cycle) { }
        public byte ReadByte(uint a, ref long c, M68kBusAccessKind k) => Memory[a];
        public ushort ReadWord(uint a, ref long c, M68kBusAccessKind k) => BinaryPrimitives.ReadUInt16BigEndian(Memory.AsSpan((int)a, 2));
        public uint ReadLong(uint a, ref long c, M68kBusAccessKind k) => Get(a);
        public void WriteByte(uint a, byte value, ref long c, M68kBusAccessKind k) => Memory[a] = value;
        public void WriteWord(uint a, ushort value, ref long c, M68kBusAccessKind k) => Word(a, value);
        public void WriteLong(uint a, uint value, ref long c, M68kBusAccessKind k) => Put(a, value);
    }
}

public static class SfsCapacityInstructionProbe
{
    public static uint Entry()
    {
        var state = APTR.FromPointer(0x3000);
        APTR.WriteUInt32(APTR.FromPointer(0x8000), NativeExecFieldCodec.MemoryListOffset, 0xA000);
        APTR.WriteUInt32(APTR.FromPointer(0xA000), NativeExecFieldCodec.MemoryLowerOffset, 0x1000);
        APTR.WriteUInt32(APTR.FromPointer(0xA000), NativeExecFieldCodec.MemoryUpperOffset, 0x300000);
        if (!AmigaVolumeGeometry.Create(32768, APTR.ReadUInt32(APTR.FromPointer(0x6000), 0), 2, out var geometry)) return 1;
        NativeAmigaFileSystem.SetGeometry(state, geometry);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartEnumerationCacheOffset, APTR.ReadUInt32(APTR.FromPointer(0x6004), 0));
        var p = new NativeFileSystemPlatform(state);
        var fib = APTR.FromPointer(0x7000); var image = APTR.FromPointer(0x7600);
        for (var i = 0; i < 228; i++) APTR.WriteUInt8(fib, i, (byte)(i * 7));
        APTR.WriteUInt32(fib, 120, APTR.ReadUInt32(fib, 4));
#if COPPERFS_SFS_POOL
        APTR.WriteUInt8(fib, 8, 32); APTR.WriteUInt8(fib, 144, 23);
        if (NativeSfsExtension.ShortEnumerationResult(fib)) return 11;
        APTR.WriteUInt8(fib, 8, 31); APTR.WriteUInt8(fib, 144, 24);
        if (NativeSfsExtension.ShortEnumerationResult(fib)) return 12;
        APTR.WriteUInt8(fib, 144, 23);
        if (!NativeSfsExtension.ShortEnumerationResult(fib)) return 13;
#endif
        APTR.WriteUInt32(APTR.FromPointer(0x75FC), 0, 0xDEADBEEF);
        var guard = APTR.FromPointer(image.Raw + NativeAmigaFileSystem.SmartEnumerationEntryBytes - 16);
        APTR.WriteUInt32(guard, 0, 0xC0FFEE00);
        NativeSfsExtension.StoreEnumerationResult(ref p, fib, image);
        for (var i = 0; i < 260; i++) APTR.WriteUInt8(fib, i, 255);
        NativeSfsExtension.LoadEnumerationResult(ref p, image, fib);
        for (var i = 0; i < 260; i++)
        {
            var expected = i >= 228 ? (byte)0 : i >= 120 && i < 124 ? (byte)((i - 116) * 7) : (byte)(i * 7);
#if COPPERFS_SFS_POOL
            if (i == 8) expected = 31;
            if (i == 144) expected = 23;
            if (i is >= 40 and < 116 or >= 168 and < 224) expected = 0;
#endif
            if (APTR.ReadUInt8(fib, i) != expected) return 9;
        }
        if (APTR.ReadUInt32(APTR.FromPointer(0x75FC), 0) != 0xDEADBEEF ||
            APTR.ReadUInt32(guard, 0) != 0xC0FFEE00) return 10;
        var group = NativeSfsExtension.NativeTrstGroup(state, ref p);
        if (group.IsNull) return 0;
        if (group != NativeSfsExtension.NativeTrstGroup(state, ref p)) return 2;
        if (!NativeSfsExtension.IsHarmlessRejection(state, DOS.Error.ObjectExists)) return 3;
        APTR.WriteUInt32(group, 4, 1);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartRedoOverlayOffset, APTR.ReadUInt32(group, 16));
        if (!NativeSfsExtension.IsHarmlessRejection(state, DOS.Error.DeleteProtected)) return 4;
        if (NativeSfsExtension.IsHarmlessRejection(state, DOS.Error.DiskNotValidated) ||
            NativeSfsExtension.IsHarmlessRejection(state, DOS.Error.NoFreeStore) ||
            NativeSfsExtension.IsHarmlessRejection(state, DOS.Error.DiskFull)) return 5;
        APTR.WriteUInt32(group, 4, 3);
        if (NativeSfsExtension.IsHarmlessRejection(state, DOS.Error.DeleteProtected)) return 6;
        APTR.WriteUInt32(group, 4, 1); APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartRecoveryPendingOffset, 1);
        if (NativeSfsExtension.IsHarmlessRejection(state, DOS.Error.ObjectExists)) return 7;
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartRecoveryPendingOffset, 0);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartRedoOverlayOffset, 0);
        return NativeSfsExtension.IsHarmlessRejection(state, DOS.Error.ObjectExists) ? 8u : 0u;
    }
}
#endif
