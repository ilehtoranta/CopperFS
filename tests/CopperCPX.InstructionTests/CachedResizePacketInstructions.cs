using Copper68k;
using CopperFS.Cpx4.Reference;
using CopperSharp.Compiler;

namespace CopperCPX.InstructionTests;

public static class CachedResizePacketProbe
{
    private static void Io(ref GuestMemory memory, CpxIo io)
    {
        memory.Write32(0x8004, io.Sector.High); memory.Write32(0x8008, io.Sector.Low);
        memory.Write32(0x800c, io.Buffer); memory.Write32(0x8010, io.Bytes); memory.Write32(0x8000, (uint)io.Kind + 1);
    }
    private static bool Drive(ref CpxEngine<GuestMemory> engine, ref GuestMemory memory)
    {
        for (uint n = 0; n < 20000; n++)
        {
            var result = engine.Step(); if (result == StepResult.Complete) return true;
            if (result == StepResult.Failed) return false;
            if (result != StepResult.NeedIo) continue;
            var io = engine.Request; Io(ref memory, io);
            if (engine.CompleteIo(io.Token, io.Bytes, true) != CpxError.None) return false;
        }
        return false;
    }
    private static bool Packet(ref CpxDosDispatcher<GuestMemory> dispatcher, ref GuestMemory memory,
        uint action, uint a, uint b, uint c, uint expected, uint expectedError = 0)
    {
        for (uint n = 0; n < 48; n += 4) memory.Write32(0x8600 + n, 0);
        memory.Write32(0x8608, action); memory.Write32(0x8614, a); memory.Write32(0x8618, b); memory.Write32(0x861c, c);
        if (dispatcher.BeginPacket(0x8600) != CpxError.None) return false;
        for (uint n = 0; n < 20000; n++)
        {
            var result = dispatcher.Step();
            if (n == 0 && action == 1022 && memory.Read32(0x60000 + 60) != (uint)CpxOperation.Resize) return false;
            if (result == StepResult.Complete) return dispatcher.DosError == expectedError && dispatcher.Result == expected;
            if (result == StepResult.Failed) return false;
            if (result != StepResult.NeedIo) continue;
            var io = dispatcher.Request; Io(ref memory, io);
            if (dispatcher.CompleteIo(io.Token, io.Bytes, true) != CpxError.None) return false;
        }
        return false;
    }
    public static uint Entry()
    {
        var memory = new GuestMemory(); var engine = new CpxEngine<GuestMemory>(memory, 0x60000);
        if (engine.Initialize(512, 512, Wide.From(8192), 1, metadataPageBytes: 512) != CpxError.None) return 1;
        if (engine.EnableBufferedWrites(0x70000, CpxEngine<GuestMemory>.BufferedStorageBytes(512)) != CpxError.None) return 2;
        if (engine.EnableShadowVolume(0xe0000, 32768) != CpxError.None) return 3;
        if (engine.BeginOperation(CpxOperation.Mount) != CpxError.None || !Drive(ref engine, ref memory)) return 4;
        memory.Write32(0x9000, 0x66696c65);
        if (engine.BeginCreate(Wide.From(1), 0x9000, 4) != CpxError.None || !Drive(ref engine, ref memory)) return 5;
        Wide id = engine.MutatedObject;
        for (uint n = 0; n < 8192; n++) memory.Write8(0x9000 + n, (byte)(n * 17 + 3));
        if (engine.BeginMappedWrite(id, default, 0x9000, 8192) != CpxError.None || !Drive(ref engine, ref memory)) return 6;
        var dispatcher = new CpxDosDispatcher<GuestMemory>(memory, 0x60000); dispatcher.Initialize(0xabc, 0xdef);
        memory.Write8(0x8900, 4); memory.Write8(0x8901, 102); memory.Write8(0x8902, 105); memory.Write8(0x8903, 108); memory.Write8(0x8904, 101);
        if (!Packet(ref dispatcher, ref memory, 1004, 0x8800 >> 2, 0, 0x8900 >> 2, uint.MaxValue)) return 7;
        uint handle = memory.Read32(0x8824);
        if (!Packet(ref dispatcher, ref memory, 1022, handle, 6003, uint.MaxValue, 6003)) return 8;
        if (!Packet(ref dispatcher, ref memory, 1022, handle, 6003, uint.MaxValue, 6003)) return 9;
        if (engine.BeginProtection(id, 4) != CpxError.None || !Drive(ref engine, ref memory)) return 16;
        if (!Packet(ref dispatcher, ref memory, 1022, handle, 6003, uint.MaxValue, uint.MaxValue, 223)) return 17;
        if (engine.BeginProtection(id, 0) != CpxError.None || !Drive(ref engine, ref memory)) return 18;
        if (!Packet(ref dispatcher, ref memory, 1007, handle, 0, 0, uint.MaxValue)) return 10;
        if (!Packet(ref dispatcher, ref memory, 27, 0, 0, 0, uint.MaxValue)) return 11;
        if (engine.BeginRemount() != CpxError.None || !Drive(ref engine, ref memory)) return 12;
        if (engine.BeginFileRead(id, default, 0x9000, 8192) != CpxError.None || !Drive(ref engine, ref memory)) return 13;
        if (engine.TransferredBytes != 6003) return 14;
        for (uint n = 0; n < 6003; n++) if (memory.Read8(0x9000 + n) != (byte)(n * 17 + 3)) return 15;
        return 0;
    }
}

public class CachedResizePacketInstructions
{
    [Theory][InlineData(false)][InlineData(true)]
    public void CompiledCachedAbsoluteResizePreservesFourKiBStackAndPersistedPayload(bool catalogue)
    {
        var compiled = Compilation.Value;
        Assert.True(compiled.FrameworkAnalysis.IsCompatible); Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        Assert.True(compiled.Code.Length < 0x50000);
        var bus = new EngineInstructions.Bus(); Cpx4Volume<EngineInstructions.Bus>.FormatDirectInodes(bus, 512, new byte[49152], 12345, true, catalogue);
        compiled.Code.CopyTo(bus.Memory, 0x10000);
        foreach (var relocation in compiled.Relocations) { uint at = 0x10000u + (uint)relocation.Offset; bus.Put(at, bus.Get(at) + 0x10000); }
        bus.Memory.AsSpan(0xee000, 8192).Fill(0xa5); bus.Put(0xf0000, 0xf1000);
        using var cpu = M68kCoreFactory.Default.Create(M68kCpuModel.M68000, bus); cpu.Reset(0x10000 + compiled.EntryPoint, 0xf0000);
        for (int n = 0; cpu.State.ProgramCounter != 0xf1000 && n < 100_000_000; n++)
        { cpu.ExecuteInstruction(); Assert.InRange(cpu.State.A[7], 0xef000u, 0xf0004u); Assert.False(cpu.State.Halted || cpu.State.Stopped); }
        Assert.Equal(0xf1000u, cpu.State.ProgramCounter); Assert.Equal(0u, cpu.State.D[0]);
        Assert.All(bus.Memory.AsSpan(0xee000, 4096).ToArray(), b => Assert.Equal(0xa5, b));
        var check = new Cpx4Volume<EngineInstructions.Bus>(bus, new byte[49152]); check.Check();
        byte[] bytes = new byte[8192]; Assert.Equal(6003, check.Read("file"u8, 0, bytes));
        for (int n = 0; n < 6003; n++) Assert.Equal((byte)(n * 17 + 3), bytes[n]);
    }
    private static readonly Lazy<M68kCompilationResult> Compilation = new(() => ShadowInstructionProfile.Compile(typeof(CachedResizePacketProbe)));
}
