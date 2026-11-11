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
    public void WriteAttemptsAndLifecycleResetInvalidateCertificate(M68kCpuModel model)
    {
        var compiled = AmigaM68kCompiler.Compile(new M68kCompilationRequest
        {
            AssemblyPath = typeof(AutomaticTrimInvalidationProbe).Assembly.Location,
            EntryPoint = typeof(AutomaticTrimInvalidationProbe).FullName + "::Entry",
            ManagedAssemblyPaths = [typeof(NativeAmigaFileSystem).Assembly.Location,
                typeof(APTR).Assembly.Location, typeof(AmigaTrimValidation).Assembly.Location],
            IncludedExportNames = [], Cpu = M68kCpuTarget.M68000,
            OutputFormat = M68kOutputFormat.Assembly, RuntimeProfile = M68kRuntimeProfile.Freestanding,
            MemoryManagement = M68kMemoryManagement.None, ExceptionMode = M68kExceptionMode.Yolo
        });
        Assert.True(compiled.FrameworkAnalysis.IsCompatible);
        Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        var bus = new TrimBus(); compiled.Code.CopyTo(bus.Memory, 0x10000);
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
        for (var operation = 0; operation < 4; operation++)
        {
            APTR.WriteUInt32(state, NativeAmigaFileSystem.AutoTrimValidationOffset, 1);
            var ok = operation == 0 ? p.WriteBlock(uint.MaxValue, APTR.Null)
                : operation == 1 ? p.WriteDataBlocks(0, 0, APTR.Null)
                : operation == 2 ? p.WriteUnpublishedDataBlocks(0, 0, APTR.Null)
                : p.WriteRetainedBlock(0, APTR.Null);
            if (ok || APTR.ReadUInt32(state, NativeAmigaFileSystem.AutoTrimValidationOffset) != 0) return 2;
        }
        APTR.WriteUInt32(state, NativeAmigaFileSystem.AutoTrimValidationOffset, 1);
        APTR.WriteUInt32(state, NativeAmigaFileSystem.AutoTrimPendingOffset, 2);
        NativeAutomaticTrim.Reset(state);
        return APTR.ReadUInt32(state, NativeAmigaFileSystem.AutoTrimValidationOffset) |
            APTR.ReadUInt32(state, NativeAmigaFileSystem.AutoTrimPendingOffset);
    }
}
#endif
