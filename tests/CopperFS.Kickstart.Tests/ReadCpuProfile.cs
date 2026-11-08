using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Copper68k;
using CopperMod.Amiga.Lightweight;

namespace CopperFS.Kickstart.Tests;

// Diagnostic scalar execution follows CopperStart's MIT-licensed SfsCpuProfile.
// Acceptance measurements continue to use the unmodified ExecuteFrame path.
internal sealed class ReadCpuProfile
{
    private readonly LightweightA500Machine machine;
    private readonly IM68kCore core;
    private readonly Action<long> advance;
    private readonly Func<long, long> clamp;
    private readonly Func<bool> dispatch;
    private readonly Func<long> boundary;
    private readonly (uint Offset, uint Size, string Name)[] symbols;
    private readonly Dictionary<uint, (long Cycles, long Instructions)> counts = [];
    private readonly string handler;
    private readonly ReadOnlyMemory<byte> rom;
    private uint codeBase;
    private uint codeBytes, callerBuffer, callerBytes;
    private long recognizedMoveBytes;
    private readonly List<object> ioRequests = [];
    private readonly List<object> copyEntries = [];
    private uint copyEntry;
    private long start;
    private string? phase;

    internal ReadCpuProfile(LightweightA500Machine machine, string handler, byte[] rom)
    {
        this.machine = machine; this.handler = handler; this.rom = rom;
        symbols = Regex.Matches(File.Exists(handler + ".map") ? File.ReadAllText(handler + ".map") : "", @"(?m)^([0-9A-F]{8}) +(\d+) (\S+)\r?$")
            .Where(m => uint.Parse(m.Groups[2].Value) > 0)
            .Select(m => (Convert.ToUInt32(m.Groups[1].Value, 16), uint.Parse(m.Groups[2].Value), m.Groups[3].Value.TrimEnd('\r'))).ToArray();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(LightweightA500Machine);
        core = (IM68kCore)type.GetField("_cpu", flags)!.GetValue(machine)!;
        advance = type.GetMethod("AdvanceHardwareTo", flags)!.CreateDelegate<Action<long>>(machine);
        clamp = type.GetMethod("ClampCpuBatchTarget", flags)!.CreateDelegate<Func<long, long>>(machine);
        dispatch = type.GetMethod("DispatchPendingCpuInterrupt", flags)!.CreateDelegate<Func<bool>>(machine);
        boundary = type.GetProperty("NextFrameCycle", flags)!.GetMethod!.CreateDelegate<Func<long>>(machine);
    }

    internal void SetCallerBuffer(uint address, uint bytes) { callerBuffer = address; callerBytes = bytes; }

    internal void ObserveRead(ushort command)
    {
        if (phase == null || command is not (2 or 24)) return;
        // CopperHDF's guest BeginIO gateway passes the IORequest in A1.
        var request = core.State.A[1];
        Assert.Equal(command, ReadWord(request + 0x1C));
        uint data = ReadLong(request + 0x28), length = ReadLong(request + 0x24), offset = ReadLong(request + 0x2C);
        ioRequests.Add(new { Command = command, Request = request, Data = data, Length = length, ByteOffset = offset,
            IntoCaller = data >= callerBuffer && data - callerBuffer <= callerBytes && length <= callerBytes - (data - callerBuffer),
            Payload = offset >= 8192 * 512 && offset - 8192 * 512 < 1048576 });
    }

    internal void Begin(string name, long cycle)
    {
        if (codeBase == 0) LocateCode();
        counts.Clear(); ioRequests.Clear(); copyEntries.Clear(); recognizedMoveBytes = 0; phase = name; start = cycle;
        copyEntry = codeBase + symbols.FirstOrDefault(s => s.Name.EndsWith("::CopyReadAhead", StringComparison.Ordinal)).Offset;
    }

    internal void End(string directory, long cycle)
    {
        if (phase == null) return;
        var rows = counts.Select(p => new
        {
            Pc = p.Key, Offset = unchecked(p.Key - codeBase), Opcode = ReadWord(p.Key), p.Value.Cycles, p.Value.Instructions,
            Region = p.Key >= codeBase && p.Key - codeBase < codeBytes ? "handler" : p.Key >= 0xF80000 && p.Key < 0x1000000 ? "kickstart" : "other",
            Name = p.Key >= codeBase && p.Key - codeBase < codeBytes
                ? symbols.FirstOrDefault(s => p.Key - codeBase >= s.Offset && p.Key - codeBase - s.Offset < s.Size).Name ?? "<unmapped-handler>" : "<outside-handler>"
        }).ToArray();
        File.WriteAllText(Path.Combine(directory, phase + "-profile.json"), JsonSerializer.Serialize(new
        {
            CodeBase = codeBase, CodeBytes = codeBytes, GuestCycles = cycle - start, AttributedCycles = rows.Sum(r => r.Cycles),
            Attribution = "Exclusive instruction cycles; interrupt dispatch, STOP waits and boundary markers are residual.",
            Methods = rows.GroupBy(r => r.Name).Select(g => new { Name = g.Key, Cycles = g.Sum(r => r.Cycles), Instructions = g.Sum(r => r.Instructions) }).OrderByDescending(r => r.Cycles),
            Regions = rows.GroupBy(r => r.Region).Select(g => new { Region = g.Key, Cycles = g.Sum(r => r.Cycles), Instructions = g.Sum(r => r.Instructions) }),
            RecognizedMoveBytesToCaller = recognizedMoveBytes, IoRequests = ioRequests, CopyEntries = copyEntries,
            Instructions = rows.OrderByDescending(r => r.Cycles)
        }, new JsonSerializerOptions { WriteIndented = true }));
        phase = null;
    }

    private void LocateCode()
    {
        var (code, mask) = FirstCodeImage(File.ReadAllBytes(handler));
        var length = code.Length; codeBytes = (uint)length;
        var matches = new List<uint>();
        void Search(ReadOnlyMemory<byte> memory, uint origin)
        {
            var span = memory.Span;
            for (var at = 0; at <= span.Length - length; at += 2)
            {
                var match = true;
                for (var i = 0; i < mask.Length; i++)
                    if (mask[i] && span[at + i] != code[i]) { match = false; break; }
                if (match) matches.Add(origin + (uint)at);
            }
        }
        Search(machine.ChipRam, 0);
        if (machine.FastRamBase is { } fast) Search(machine.FastRam, fast);
        codeBase = Assert.Single(matches);
    }

    // FFS 40.1 has one code hunk and an empty data hunk. Copper uses one
    // combined hunk. Nonempty secondary hunks require separate attribution.
    internal static (byte[] Code, bool[] Mask) FirstCodeImage(byte[] image)
    {
        int pos = 0;
        uint Long() { var n = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(pos, 4)); pos += 4; return n; }
        Assert.Equal(1011u, Long());
        for (var names = Long(); names != 0; names = Long()) pos = checked(pos + (int)names * 4);
        var count = Long(); Assert.InRange(count, 1u, 64u); Assert.Equal(0u, Long()); Assert.Equal(count - 1, Long());
        for (var h = 0u; h < count; h++) { var size = Long() & 0x3FFFFFFF; if (h != 0) Assert.Equal(0u, size); }
        Assert.Equal(1001u, Long() & 0x3FFFFFFF);
        var length = checked((int)Long() * 4); var code = image.AsSpan(pos, length).ToArray(); pos += length;
        var mask = Enumerable.Repeat(true, Math.Min(8192, length)).ToArray();
        while (true)
        {
            var kind = Long() & 0x3FFFFFFF;
            if (kind == 1010) break;
            Assert.Equal(1004u, kind);
            for (var n = Long(); n != 0; n = Long())
            {
                Assert.True(Long() < count);
                for (var i = 0u; i < n; i++)
                {
                    var at = Long(); Assert.True(at <= length - 4);
                    for (var j = at; j < at + 4 && j < mask.Length; j++) mask[j] = false;
                }
            }
        }
        return (code, mask);
    }

    private ReadOnlySpan<byte> Memory(uint address, int bytes)
    {
        if (address < machine.ChipRam.Length && bytes <= machine.ChipRam.Length - address) return machine.ChipRam.Span.Slice((int)address, bytes);
        if (machine.FastRamBase is uint lower && address >= lower && address - lower <= machine.FastRam.Length && bytes <= machine.FastRam.Length - (address - lower))
            return machine.FastRam.Span.Slice((int)(address - lower), bytes);
        var romBase = 0x1000000u - (uint)rom.Length;
        if (address >= romBase && address - romBase <= rom.Length && bytes <= rom.Length - (address - romBase))
            return rom.Span.Slice((int)(address - romBase), bytes);
        // Device bootstrap/trap code is outside fitted RAM and Kickstart.
        return [];
    }

    private ushort ReadWord(uint address)
    {
        var bytes = Memory(address, 2);
        return bytes.Length == 2 ? BinaryPrimitives.ReadUInt16BigEndian(bytes) : (ushort)0;
    }
    private uint ReadLong(uint address) => BinaryPrimitives.ReadUInt32BigEndian(Memory(address, 4));

    private void CountMovesToCaller(uint pc)
    {
        var op = ReadWord(pc); var family = op >> 12;
        if (family is not (1 or 2 or 3)) return;
        var width = family == 1 ? 1u : family == 2 ? 4u : 2u;
        var mode = op >> 6 & 7; var register = op >> 9 & 7;
        uint address = core.State.A[register];
        if (mode == 4) address -= width;
        else if (mode == 5)
        {
            var sourceMode = op >> 3 & 7; var sourceRegister = op & 7;
            var sourceWords = sourceMode is 5 or 6 ? 1 : sourceMode == 7 ? sourceRegister == 1 ? 2 : sourceRegister == 4 && width == 4 ? 2 : 1 : 0;
            address = unchecked(address + (uint)(short)ReadWord(pc + 2 + (uint)sourceWords * 2));
        }
        else if (mode is not (2 or 3)) return;
        if (address >= callerBuffer && address - callerBuffer < callerBytes && width <= callerBytes - (address - callerBuffer)) recognizedMoveBytes += width;
    }

    internal void ExecuteFrame()
    {
        var target = machine.CompletedFrames + 1; var cpu = machine.Cpu;
        while (machine.CompletedFrames < target)
        {
            var end = boundary();
            if (cpu.Halted) { advance(end); cpu.Cycles = Math.Max(cpu.Cycles, end); continue; }
            if (cpu.Stopped)
            {
                var wake = ((cpu.StatusRegister >> 8) & 7) >= 6 ? end : clamp(end);
                advance(wake); cpu.Cycles = Math.Max(cpu.Cycles, wake); dispatch(); continue;
            }
            if (dispatch()) continue;
            while (cpu.Cycles < end)
            {
                if (dispatch()) continue;
                if (cpu.Halted || cpu.Stopped) break;
                var pc = cpu.ProgramCounter; var before = cpu.Cycles; var measuredPhase = phase;
                if (measuredPhase != null) CountMovesToCaller(pc);
                if (measuredPhase != null && pc == copyEntry && copyEntries.Count < 4096)
                    copyEntries.Add(new { Cycle = before, Source = core.State.A[0], Destination = core.State.A[1], Bytes = core.State.D[0] });
                core.ExecuteInstruction();
                if (cpu.Cycles <= before) throw new InvalidOperationException("CPU clock did not advance.");
                if (measuredPhase != null && measuredPhase == phase)
                {
                    var value = counts.GetValueOrDefault(pc);
                    counts[pc] = (value.Cycles + cpu.Cycles - before, value.Instructions + 1);
                }
                advance(cpu.Cycles);
            }
        }
    }
}
