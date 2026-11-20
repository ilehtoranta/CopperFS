using System.Buffers.Binary;
using Copper68k;
using CopperSharp.Compiler;
using CopperSharp.Targets.Amiga;
using CopperFS.Cpx4.Reference;

namespace CopperCPX.InstructionTests;

public static class ShadowCommitProbe
{
    public static uint Entry()
    {
        var memory = new GuestMemory();
        var commit = new CpxShadowCommit<GuestMemory>(memory, 0x70000);
        uint mode = memory.Read32(0x8304);
        if (commit.Initialize(512, 1024, Wide.From(8192), Wide.From(12345), Wide.From(1),
            Wide.From(2), memory.Read32(0x8300), 0xc000) != CpxError.None) return 1;
        if (commit.Begin(0x9000, 1, Wide.From(1u << 16)) != CpxError.None) return 2;
        for (uint turn = 0; turn < 512; turn++)
        {
            var result = commit.Step();
            if (result == StepResult.Complete) return mode == 0 && commit.DurableGeneration.Low == 2 ? 0u : 3u;
            if (result == StepResult.Failed)
                return commit.DurableGeneration.Low == 1 && commit.DurableMappingRoot.IsZero
                    && (mode == 1 && commit.Error == CpxError.Cancelled || mode == 2 && commit.Error == CpxError.Io) ? 0u : 4u;
            if (result != StepResult.NeedIo) continue;
            var io = commit.Request;
            memory.Write32(0x8004, io.Sector.High); memory.Write32(0x8008, io.Sector.Low);
            memory.Write32(0x800c, io.Buffer); memory.Write32(0x8010, io.Bytes);
            memory.Write32(0x8000, (uint)io.Kind + 1);
            if (commit.CompleteIo(io.Token + 1, io.Bytes, true) != CpxError.StaleCompletion) return 5;
            if (mode == 1) commit.Cancel();
            commit.CompleteIo(io.Token, mode == 2 ? io.Bytes - 512 : io.Bytes, true);
        }
        return 6;
    }
}

public class ShadowCommitInstructions
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    public void CompiledShadowPublicationKeepsFourKiBStackAndAllocatesNothing(int survivor, int mode)
    {
        var compiled = Compilation.Value;
        Assert.True(compiled.FrameworkAnalysis.IsCompatible);
        Assert.Empty(compiled.FrameworkAnalysis.ManagedAllocationSites);
        var bus = new Bus(); compiled.Code.CopyTo(bus.Memory, 0x10000);
        foreach (var relocation in compiled.Relocations)
        { uint at = 0x10000u + (uint)relocation.Offset; bus.Put(at, bus.Get(at) + 0x10000); }
        bus.Put(0x8300, (uint)survivor); bus.Put(0x8304, (uint)mode);
        Layout.Put64(bus.Memory.AsSpan(0x9000), 0, 16); Layout.Put64(bus.Memory.AsSpan(0x9000), 8, 16);
        bus.Put(0x9010, 0xa000); bus.Put(0x9014, 0);
        var image = bus.Memory.AsSpan(0xa000, 1024);
        Layout.Header(image, PageType.ObjectLeaf, 16, 0, 2, 0, 0);
        Layout.Put32(image, 0, 0x43505835); Layout.Put16(image, 6, 5); image[64] = 0x79;
        Layout.Put32(image, 8, Layout.Crc(image, true));
        bus.Memory.AsSpan(0xee000, 8192).Fill(0xa5); bus.Put(0xf0000, 0xf1000);
        bus.Memory.AsSpan(0x6ff00, 256).Fill(0xa5); bus.Memory.AsSpan(0x700a0, 256).Fill(0xa5);
        using var cpu = M68kCoreFactory.Default.Create(M68kCpuModel.M68000, bus);
        cpu.Reset(0x10000 + compiled.EntryPoint, 0xf0000);
        for (int instruction = 0; cpu.State.ProgramCounter != 0xf1000 && instruction < 100_000_000; instruction++)
        {
            cpu.ExecuteInstruction(); Assert.InRange(cpu.State.A[7], 0xef000u, 0xf0004u);
            Assert.False(cpu.State.Halted || cpu.State.Stopped);
        }
        Assert.Equal(0xf1000u, cpu.State.ProgramCounter); Assert.Equal(0u, cpu.State.D[0]);
        Assert.All(bus.Memory.AsSpan(0xee000, 4096).ToArray(), b => Assert.Equal(0xa5, b));
        Assert.All(bus.Memory.AsSpan(0x6ff00, 256).ToArray(), b => Assert.Equal(0xa5, b));
        Assert.All(bus.Memory.AsSpan(0x700a0, 256).ToArray(), b => Assert.Equal(0xa5, b));
        Assert.Equal(mode == 0 ? 2 : 0, bus.Flushes);
        Assert.Equal(mode == 0 ? 3 : 1, bus.Writes);
        if (mode == 0)
        {
            Assert.Equal(0x79, bus.Disk[18 * 512 + 64]);
            for (uint slot = 0; slot < 3; slot++)
            {
                if (slot == survivor) continue;
                var checkpoint = bus.Disk.AsSpan((int)(2 + slot * 2) * 512, 1024);
                Assert.Equal(0x43505835u, Layout.U32(checkpoint, 0));
                Assert.Equal(2ul, Layout.U64(checkpoint, 80));
                Assert.Equal(1ul << 16, Layout.U64(checkpoint, 88));
                Assert.Equal(Layout.Crc(checkpoint, true), Layout.U32(checkpoint, 8));
            }
        }
    }

    private static readonly Lazy<M68kCompilationResult> Compilation = new(() => ShadowInstructionProfile.Compile(typeof(ShadowCommitProbe)));

    internal sealed class Bus : IM68kBus
    {
        internal readonly byte[] Memory = new byte[0x100000], Disk = new byte[4 * 1024 * 1024];
        internal int Flushes, Writes, Reads;
        internal uint Get(uint at) => BinaryPrimitives.ReadUInt32BigEndian(Memory.AsSpan((int)at));
        internal void Put(uint at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(Memory.AsSpan((int)at), value);
        public void ResetExternalDevices(long cycle) { }
        public byte ReadByte(uint at, ref long cycle, M68kBusAccessKind kind) => Memory[at];
        public ushort ReadWord(uint at, ref long cycle, M68kBusAccessKind kind) => BinaryPrimitives.ReadUInt16BigEndian(Memory.AsSpan((int)at));
        public uint ReadLong(uint at, ref long cycle, M68kBusAccessKind kind) => Get(at);
        public void WriteByte(uint at, byte value, ref long cycle, M68kBusAccessKind kind) => Memory[at] = value;
        public void WriteWord(uint at, ushort value, ref long cycle, M68kBusAccessKind kind) => BinaryPrimitives.WriteUInt16BigEndian(Memory.AsSpan((int)at), value);
        public void WriteLong(uint at, uint value, ref long cycle, M68kBusAccessKind kind)
        {
            Put(at, value); if (at != 0x8000) return;
            Assert.Equal(0u, Get(0x8004));
            if (value == 1)
            { Reads++; Disk.AsSpan((int)Get(0x8008) * 512, (int)Get(0x8010)).CopyTo(Memory.AsSpan((int)Get(0x800c))); }
            else if (value == 2)
            { Writes++; Memory.AsSpan((int)Get(0x800c), (int)Get(0x8010)).CopyTo(Disk.AsSpan((int)Get(0x8008) * 512)); }
            else { Assert.Equal(3u, value); Flushes++; }
        }
    }
}
