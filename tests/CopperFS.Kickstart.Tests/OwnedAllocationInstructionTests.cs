#if COPPERFS_SFS2_OWNED
using System.Buffers.Binary;
using Amiga;
using Copper68k;
using CopperSharp.Compiler;
using CopperSharp.Targets.Amiga;
using CopperSFS;
using CopperStart.Dos;

namespace CopperFS.Kickstart.Tests;

public sealed class OwnedAllocationInstructionTests
{
    private static byte[]? code;
    private static uint entry;

    [Theory] [InlineData(M68kCpuModel.M68000)] [InlineData(M68kCpuModel.M68020)]
    public void DeferredCloseRequiresTimerAndHealthyMatchingMount(M68kCpuModel model)
    {
        var compiled = AmigaM68kCompiler.Compile(new M68kCompilationRequest
        {
            AssemblyPath = typeof(OwnedClosePolicyProbe).Assembly.Location,
            EntryPoint = typeof(OwnedClosePolicyProbe).FullName + "::Entry",
            ManagedAssemblyPaths = [typeof(NativeAmigaFileSystem).Assembly.Location, typeof(APTR).Assembly.Location,
                typeof(Sfs2MountContext).Assembly.Location, typeof(IAmigaGuestMemory).Assembly.Location],
            IncludedExportNames = [], Cpu = M68kCpuTarget.M68000, OutputFormat = M68kOutputFormat.Assembly,
            RuntimeProfile = M68kRuntimeProfile.Freestanding, MemoryManagement = M68kMemoryManagement.None,
            ExceptionMode = M68kExceptionMode.Yolo
        });
        Assert.True(compiled.FrameworkAnalysis.IsCompatible); Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        var bus = new AllocationBus(); compiled.Code.CopyTo(bus.Memory, 0x10000);
        foreach (var relocation in compiled.Relocations)
        { var at = 0x10000u + (uint)relocation.Offset; bus.Put(at, bus.Get(at) + 0x10000); }
        bus.Put(0xE0000, 0xF0000);
        using var cpu = M68kCoreFactory.Default.Create(model, bus); cpu.Reset(0x10000 + compiled.EntryPoint, 0xE0000);
        for (var step = 0; step < 100000 && cpu.State.ProgramCounter != 0xF0000; step++)
        { cpu.ExecuteInstruction(); Assert.False(cpu.State.Halted || cpu.State.Stopped); }
        Assert.Equal(0xF0000u, cpu.State.ProgramCounter); Assert.Equal(0u, cpu.State.D[0]);
    }

    [Theory] [InlineData(M68kCpuModel.M68000)] [InlineData(M68kCpuModel.M68020)]
    public void RecoveryRetriesTheFinalBarrierInCompiledCore(M68kCpuModel model)
    {
        var compiled = AmigaM68kCompiler.Compile(new M68kCompilationRequest
        {
            AssemblyPath = typeof(OwnedRecoveryBarrierProbe).Assembly.Location,
            EntryPoint = typeof(OwnedRecoveryBarrierProbe).FullName + "::Entry",
            ManagedAssemblyPaths = [typeof(APTR).Assembly.Location, typeof(Sfs2MountContext).Assembly.Location,
                typeof(AmigaVolumeGeometry).Assembly.Location, typeof(IAmigaGuestMemory).Assembly.Location],
            IncludedExportNames = [], Cpu = M68kCpuTarget.M68000, OutputFormat = M68kOutputFormat.Assembly,
            RuntimeProfile = M68kRuntimeProfile.Freestanding, MemoryManagement = M68kMemoryManagement.None,
            ExceptionMode = M68kExceptionMode.Yolo
        });
        Assert.True(compiled.FrameworkAnalysis.IsCompatible); Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        var evidence = Path.Combine(AppContext.BaseDirectory, "owned-barrier-replay"); Directory.CreateDirectory(evidence);
        File.WriteAllText(Path.Combine(evidence, "probe.asm"), compiled.Text);
        File.WriteAllText(Path.Combine(evidence, "probe.map"), compiled.Map);
        var bus = new AllocationBus(); compiled.Code.CopyTo(bus.Memory, 0x10000);
        ReadMedia.Sfs2(8).CopyTo(bus.Memory, 0x400000);
        foreach (var relocation in compiled.Relocations)
        { var at = 0x10000u + (uint)relocation.Offset; bus.Put(at, bus.Get(at) + 0x10000); }
        bus.Put(0xE0000, 0xF0000);
        using var cpu = M68kCoreFactory.Default.Create(model, bus); cpu.Reset(0x10000 + compiled.EntryPoint, 0xE0000);
        for (var step = 0; step < 10_000_000 && cpu.State.ProgramCounter != 0xF0000; step++)
        { cpu.ExecuteInstruction(); Assert.False(cpu.State.Halted || cpu.State.Stopped); }
        var symbol = compiled.Symbols.Where(s => 0x10000 + s.Address <= cpu.State.ProgramCounter).OrderBy(s => s.Address).LastOrDefault();
        Assert.True(cpu.State.ProgramCounter == 0xF0000, $"PC {cpu.State.ProgramCounter:X8} in {symbol.Name}; D0={cpu.State.D[0]:X8}, SP={cpu.State.A[7]:X8}, context={bus.Get(0x180004)}, flushes={bus.Get(0x6000)}");
        Assert.Equal(0u, cpu.State.D[0]);
        Assert.Equal(2u, bus.Get(0x6000));
    }

    [Theory]
    [InlineData(M68kCpuModel.M68000, 0)] [InlineData(M68kCpuModel.M68020, 0)]
    [InlineData(M68kCpuModel.M68000, 1)] [InlineData(M68kCpuModel.M68020, 1)]
    [InlineData(M68kCpuModel.M68000, 2)] [InlineData(M68kCpuModel.M68020, 2)]
    [InlineData(M68kCpuModel.M68000, 3)] [InlineData(M68kCpuModel.M68020, 3)]
    [InlineData(M68kCpuModel.M68000, 4)] [InlineData(M68kCpuModel.M68020, 4)]
    public void FailedOptionalAllocationsSelectSmallerBudgetBeforeMount(M68kCpuModel model, int failures)
    {
        // Compile with the 256 KiB profile. Execute the real allocation loop;
        // only Exec allocation is injected. No filesystem packet is accepted.
        if (code == null)
        {
            var compiled = AmigaM68kCompiler.Compile(new M68kCompilationRequest
            {
                AssemblyPath = typeof(OwnedAllocationProbe).Assembly.Location,
                EntryPoint = typeof(OwnedAllocationProbe).FullName + "::Entry",
                ManagedAssemblyPaths = [typeof(NativeAmigaFileSystem).Assembly.Location, typeof(APTR).Assembly.Location,
                    typeof(IAmigaGuestMemory).Assembly.Location, typeof(Sfs2MountContext).Assembly.Location,
                    typeof(AmigaVolumeGeometry).Assembly.Location],
                IncludedExportNames = [], Cpu = M68kCpuTarget.M68000, OutputFormat = M68kOutputFormat.Assembly,
                RuntimeProfile = M68kRuntimeProfile.Freestanding, MemoryManagement = M68kMemoryManagement.None,
                ExceptionMode = M68kExceptionMode.Yolo
            });
            Assert.True(compiled.FrameworkAnalysis.IsCompatible); Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
            code = compiled.Code.ToArray(); entry = compiled.EntryPoint;
            foreach (var relocation in compiled.Relocations)
            {
                var value = BinaryPrimitives.ReadUInt32BigEndian(code.AsSpan(relocation.Offset));
                BinaryPrimitives.WriteUInt32BigEndian(code.AsSpan(relocation.Offset), value + 0x10000);
            }
        }
        var bus = new AllocationBus(); code.CopyTo(bus.Memory, 0x10000); bus.Put(4, 0x8000);
        bus.Word(0x8000 - 198, 0x4EF9); bus.Put(0x8000 - 196, 0x9000); bus.Word(0x9000, 0x4E75);
        bus.Put(0x3E0000, 0x3F0000);
        using var cpu = M68kCoreFactory.Default.Create(model, bus); cpu.Reset(0x10000 + entry, 0x3E0000);
        var requests = new List<uint>();
        for (var step = 0; step < 2_000_000 && cpu.State.ProgramCounter != 0x3F0000; step++)
        {
            if (cpu.State.ProgramCounter == 0x9000)
            {
                requests.Add(cpu.State.D[0]); cpu.State.D[0] = requests.Count <= failures ? 0u : 0x200000u;
            }
            cpu.ExecuteInstruction(); Assert.False(cpu.State.Halted || cpu.State.Stopped);
        }
        Assert.Equal(0x3F0000u, cpu.State.ProgramCounter); Assert.Equal(0u, cpu.State.D[0]);
        uint[] budgets = [278652, 82044, 32892, 16508];
        Assert.Equal(budgets.Take(Math.Min(failures + 1, 4)), requests);
        Assert.Equal(failures == 4 ? 0u : 0x200000u, bus.Get(0x3000 + NativeAmigaFileSystem.Sfs2ContextStorageOffset));
        Assert.Equal(failures == 4 ? 0u : budgets[failures], bus.Get(0x3000 + NativeAmigaFileSystem.Sfs2ContextBytesOffset));
    }

    private sealed class AllocationBus : IM68kBus
    {
        public byte[] Memory { get; } = new byte[0x1000000];
        public uint Get(uint a) => BinaryPrimitives.ReadUInt32BigEndian(Memory.AsSpan((int)a, 4));
        public void Put(uint a, uint v) => BinaryPrimitives.WriteUInt32BigEndian(Memory.AsSpan((int)a, 4), v);
        public void Word(uint a, ushort v) => BinaryPrimitives.WriteUInt16BigEndian(Memory.AsSpan((int)a, 2), v);
        public void ResetExternalDevices(long cycle) { }
        public byte ReadByte(uint a, ref long c, M68kBusAccessKind k) => Memory[a];
        public ushort ReadWord(uint a, ref long c, M68kBusAccessKind k) => BinaryPrimitives.ReadUInt16BigEndian(Memory.AsSpan((int)a, 2));
        public uint ReadLong(uint a, ref long c, M68kBusAccessKind k) => Get(a);
        public void WriteByte(uint a, byte v, ref long c, M68kBusAccessKind k) => Memory[a] = v;
        public void WriteWord(uint a, ushort v, ref long c, M68kBusAccessKind k) => Word(a, v);
        public void WriteLong(uint a, uint v, ref long c, M68kBusAccessKind k) => Put(a, v);
    }
}

public static class OwnedAllocationProbe
{
    public static uint Entry()
    {
        var state = APTR.FromPointer(0x3000);
        if (!AmigaVolumeGeometry.Create(32768, 512, 1, out var geometry)) return 1;
        NativeAmigaFileSystem.SetGeometry(state, geometry);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartRangeBufferOffset, 0x180000);
        NativeSfs2Bridge.MountContext(state);
        var p = new NativeFileSystemPlatform(state);
        return p.Sfs2Context.IsNotNull && Sfs2MountContext.Ready(ref p, p.Sfs2Context) ? 2u : 0u;
    }
}

public static class OwnedClosePolicyProbe
{
    public static uint Entry()
    {
        var state = APTR.FromPointer(0x3000); var context = APTR.FromPointer(0x200000);
        AmigaVolumeGeometry.Create(32768, 512, 1, out var geometry);
        NativeAmigaFileSystem.SetGeometry(state, geometry);
        APTR.WriteUInt32(APTR.FromPointer(4), 0, 0x8000);
        APTR.WriteUInt32(APTR.FromPointer(0x8000), NativeExecFieldCodec.MemoryListOffset, 0x9000);
        APTR.WriteUInt32(APTR.FromPointer(0x9000), NativeExecFieldCodec.MemoryLowerOffset, 0x1000);
        APTR.WriteUInt32(APTR.FromPointer(0x9000), NativeExecFieldCodec.MemoryUpperOffset, 0x300000);
        APTR.WriteUInt32(state, NativeAmigaFileSystem.Sfs2ContextOffset, context.Raw);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.DosTypeOffset, NativeSfs2Bridge.DosType);
        APTR.WriteUInt32(context, 0, 0x53324D43); APTR.WriteUInt32(context, 4, 1);
        APTR.WriteUInt32(context, 16, 512); APTR.WriteUInt32(context, 20, 32768); APTR.WriteUInt32(context, 24, 1);
        APTR.WriteUInt32(context, 52, 1); // A pending healthy transaction is eligible.
        if (NativeSfs2Bridge.CanDeferClose(state)) return 1;
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartTimerOffset, 0xA000);
        if (!NativeSfs2Bridge.CanDeferClose(state)) return 2;
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartRecoveryPendingOffset, 1);
        if (NativeSfs2Bridge.CanDeferClose(state)) return 3;
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartRecoveryPendingOffset, 0);
        APTR.WriteUInt32(context, 4, 2);
        if (NativeSfs2Bridge.CanDeferClose(state)) return 4;
        APTR.WriteUInt32(context, 4, 1);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.DosTypeOffset, 0x53465300);
        if (NativeSfs2Bridge.CanDeferClose(state)) return 5;
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.DosTypeOffset, NativeSfs2Bridge.DosType);
        APTR.WriteUInt32(context, 20, 32767);
        if (NativeSfs2Bridge.CanDeferClose(state)) return 6;
        APTR.WriteUInt32(context, 20, 32768); APTR.WriteUInt32(state, NativeAmigaFileSystem.Sfs2ContextOffset, 0);
        return NativeSfs2Bridge.CanDeferClose(state) ? 7u : 0u;
    }
}

public static class OwnedRecoveryBarrierProbe
{
    public static uint Entry()
    {
        var p = new OwnedRecoveryPlatform(); var context = APTR.FromPointer(0x180000);
        var work = new AmigaSmartFileSystem2WriteCore.Work { Meta = APTR.FromPointer(0x100000),
            Data = APTR.FromPointer(0x100200), Bitmap = APTR.FromPointer(0x100400), Journal = APTR.FromPointer(0x100600) };
        if (!Sfs2MountContext.Mount(ref p, ref work, context, 16508)) return 1;
        Sfs2MountContext.Suspend(ref p, context); p.WriteUInt32(context, 52, 2);
        p.WriteUInt32(APTR.FromPointer(0x6004), 0, 1);
        if (Sfs2MountContext.Mount(ref p, ref work, context, 16508) || Sfs2MountContext.Durable(ref p, context)) return 2;
        p.WriteUInt32(APTR.FromPointer(0x6004), 0, 0);
        if (!Sfs2MountContext.Mount(ref p, ref work, context, 16508) || !Sfs2MountContext.Durable(ref p, context)) return 3;
        return 0;
    }
}

public struct OwnedRecoveryPlatform : IAmigaWritableFileSystemPlatform
{
    public APTR Sfs2Context { get; set; }
    // Native providers implement optional interface members explicitly: this
    // freestanding fixture has no managed interface-dispatch runtime.
    public APTR RedoOverlay { get => APTR.Null; set { } }
    public AmigaVolumeGeometry Geometry { get { AmigaVolumeGeometry.Create(16384, 512, 1, out var g); return g; } }
    public uint BlockBytes => 512;
    public uint BlockCount => 16384;
    public uint ReservedBlocks => 1;
    public uint TrackBlocks => 1;
    public ulong StartByte => 0;
    public bool TryReadVerifiedSfs2Block(uint block, uint id, APTR destination) => false;
    public APTR BorrowVerifiedSfs2Block(uint block, uint id, APTR scratch) => APTR.Null;
    public bool TryReadSfs2MetadataView(uint block, uint id, APTR scratch, out APTR image) { image = APTR.Null; return false; }
    public APTR ReadRetainedBlock(uint block) => APTR.Null;
    public bool HasAcknowledgedCheckpoint(uint rootContainer) => false;
    public bool ReadSfs2PhysicalMetadata(uint block, APTR destination) => ReadBlock(block, destination);
    public bool ReadBlock(uint block, APTR destination)
    { if (block >= 16384) return false; Copy(APTR.FromPointer(0x400000 + block * 512), destination, 512); return true; }
    public bool ReadMetadataBlock(uint block, uint id, APTR destination) => ReadBlock(block, destination) &&
        ReadUInt32(destination) == id && ReadUInt32(destination, 8) == block && AmigaFileSystemBlockIo.ValidSfs2Sum(ref this, destination, 512);
    public APTR ReadMetadataView(uint block, uint id, APTR scratch) => ReadMetadataBlock(block, id, scratch) ? scratch : APTR.Null;
    public uint ReadDataBlocks(uint block, uint count, APTR destination) => 0;
    public uint ReadDataBlocks(uint block, uint count, APTR destination, bool readAhead) => 0;
    public bool WriteProtected() => false;
    public bool WriteBlock(uint block, APTR source) => false;
    public bool WriteDataBlocks(uint block, uint count, APTR source) => false;
    public bool Flush()
    { var count = APTR.FromPointer(0x6000); WriteUInt32(count, 0, ReadUInt32(count) + 1); return ReadUInt32(count, 4) == 0; }
    public byte ReadUInt8(APTR a, int offset = 0) => APTR.ReadUInt8(a, offset);
    public ushort ReadUInt16(APTR a, int offset = 0) => APTR.ReadUInt16(a, offset);
    public uint ReadUInt32(APTR a, int offset = 0) => APTR.ReadUInt32(a, offset);
    public void WriteUInt8(APTR a, int offset, byte value) => APTR.WriteUInt8(a, offset, value);
    public void WriteUInt16(APTR a, int offset, ushort value) => APTR.WriteUInt16(a, offset, value);
    public void WriteUInt32(APTR a, int offset, uint value) => APTR.WriteUInt32(a, offset, value);
    public void Clear(APTR a, uint bytes)
    { var i = 0; for (; i + 4 <= bytes; i += 4) WriteUInt32(a, i, 0); for (; i < bytes; i++) WriteUInt8(a, i, 0); }
    public void Copy(APTR a, APTR b, uint bytes)
    { var i = 0; for (; i + 4 <= bytes; i += 4) WriteUInt32(b, i, ReadUInt32(a, i)); for (; i < bytes; i++) WriteUInt8(b, i, ReadUInt8(a, i)); }
    public bool IsMapped(APTR a, uint bytes) => a.IsNotNull && a.Raw < 0x1000000 && bytes <= 0x1000000 - a.Raw;
}
#endif
