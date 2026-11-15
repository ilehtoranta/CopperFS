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
    private readonly List<object> deviceCalls = [];
    private readonly List<object> dosCalls = [];
    private uint dosReturn, dosStack, dosBase;
    private int dosVector;
    private long dosStart, dosHandler, dosRom;
    private readonly Dictionary<uint, long> dosHandlerPcs = [];

    // Observe library-vector entry and the matching guest RTS. No guest
    // instruction, timer, DMA request or packet is added to either handler.
    private void ObserveDosCall(uint pc, long cycle)
    {
        if (dosReturn != 0)
        {
            if (pc != dosReturn || core.State.A[7] != dosStack) return;
            dosCalls.Add(new { Vector = dosVector, Start = dosStart - start, Cycles = cycle - dosStart,
                Handler = dosHandler, Kickstart = dosRom, Result = core.State.D[0], Methods = DosHandlerMethods() });
            dosReturn = 0;
        }
        var library = core.State.A[6];
        var vector = unchecked((int)(pc - library));
        if (vector is not (Amiga.DosLvo.Write or Amiga.DosLvo.Close or Amiga.DosLvo.DoPkt or Amiga.DosLvo.DeviceProc)) return;
        if (dosBase == 0)
        {
            if (TaskName(library) != "dos.library") return;
            dosBase = library;
        }
        if (library != dosBase || Memory(core.State.A[7], 4).Length != 4) return;
        dosReturn = ReadLong(core.State.A[7]); dosStack = core.State.A[7] + 4;
        dosStart = cycle; dosHandler = dosRom = 0; dosVector = vector; dosHandlerPcs.Clear();
    }
    private object DosHandlerMethods() => dosHandlerPcs.GroupBy(pair =>
    {
        var offset = pair.Key - codeBase;
        return symbols.FirstOrDefault(s => offset >= s.Offset && offset - s.Offset < s.Size).Name ?? "<unmapped-handler>";
    }).Select(group => new { Name = group.Key, Cycles = group.Sum(pair => pair.Value) })
      .OrderByDescending(method => method.Cycles).ToArray();

    private readonly List<object> copyEntries = [];
    private uint copyEntry;
    private uint blockCopyEntry, blockCopyBytes, blockCopyCaller;
    private readonly Dictionary<uint, (long Cycles, long Calls)> blockCopyCallers = [];
    private readonly List<object> transactionCalls = [];
    private readonly List<object> blockTransfers = [];
    private uint commitEntry, journalPageEntry, journalBlockEntry, pressureFlushEntry;
    private uint cacheReturn, cacheReturnStack;
    private int cacheLookups, cacheHits;
    private readonly List<object> interrupts = [];
    private readonly Dictionary<(uint Task, int Mask), long> romCycles = [];
    private long start;
    private string? phase;
    private string? callDirectory;
    private string callStage = "setup";
    private bool stopCalls;
    private uint callReturn, callReturnStack;
    private int callSequence;
    private Func<object>? callSnapshot;
    private object? snapshotStart;
    private readonly Dictionary<uint, string> watchedCalls = [];
    private readonly Dictionary<uint, string> methodEntries = [];
    private readonly Stack<(string Name, uint Return, uint Stack, long Start)> activeCalls = [];
    private readonly Dictionary<string, (long Cycles, long Calls)> inclusiveCalls = [];

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

    // Method-entry/return scopes exclude the five-second timer wait and guest
    // workload. Nested inclusive costs include OS calls and must not be summed.
    internal void ArmTrimCalls(string directory, Func<object> snapshot)
    {
        if (codeBase == 0) LocateCode();
        callDirectory = directory; callSnapshot = snapshot; stopCalls = false;
        foreach (var method in symbols) methodEntries.TryAdd(codeBase + method.Offset, method.Name);
        foreach (var (kind, name) in new[] {
            ("initialization", "NativeAutomaticTrim::Initialize"),
            ("service", "NativeAutomaticTrim::Service"),
            ("tracking", "NativeFileSystemPlatform::ObserveFreedRange"),
            ("flush", "NativeAmigaFileSystem::FlushSmart") })
        {
            var method = symbols.SingleOrDefault(s => s.Name.EndsWith(name, StringComparison.Ordinal));
            if (method.Size != 0) watchedCalls[codeBase + method.Offset] = kind;
        }
        Assert.Contains("service", watchedCalls.Values);
        Assert.Contains("tracking", watchedCalls.Values);
    }

    internal void SetCallStage(string stage) => callStage = stage;
    internal void StopTrimCalls()
    {
        watchedCalls.Clear(); stopCalls = true;
        if (phase == null) callDirectory = null;
    }

    internal void ObserveRead(ushort command)
    {
        if (phase == null || command is not (2 or 24)) return;
        // CopperHDF's guest BeginIO gateway passes the IORequest in A1.
        var request = core.State.A[1];
        Assert.Equal(command, ReadWord(request + 0x1C));
        uint data = ReadLong(request + 0x28), length = ReadLong(request + 0x24), offset = ReadLong(request + 0x2C);
        ioRequests.Add(new { Command = command, Request = request, Data = data, Length = length, ByteOffset = offset,
            IntoCaller = data >= callerBuffer && data - callerBuffer <= callerBytes && length <= callerBytes - (data - callerBuffer),
            Payload = offset >= 8192 * 512 && offset - 8192 * 512 < 1048576,
            Stack = activeCalls.Take(10).Select(c => c.Name).ToArray() });
    }

    internal void Begin(string name, long cycle)
    {
        if (codeBase == 0) LocateCode();
        foreach (var method in symbols) methodEntries.TryAdd(codeBase + method.Offset, method.Name);
        counts.Clear(); ioRequests.Clear(); copyEntries.Clear(); recognizedMoveBytes = 0; phase = name; start = cycle;
        copyEntry = codeBase + symbols.FirstOrDefault(s => s.Name.EndsWith("::CopyReadAhead", StringComparison.Ordinal)).Offset;
        var blockCopy = symbols.FirstOrDefault(s => s.Name.EndsWith("::CopyBlock", StringComparison.Ordinal));
        blockCopyEntry = codeBase + blockCopy.Offset; blockCopyBytes = blockCopy.Size;
        blockCopyCallers.Clear();
        transactionCalls.Clear(); blockTransfers.Clear(); deviceCalls.Clear(); dosCalls.Clear(); dosReturn = dosBase = 0;
        cacheReturn = cacheReturnStack = 0; cacheHits = cacheLookups = 0;
        uint Entry(string method) => codeBase + symbols.FirstOrDefault(s => s.Name.Contains("::" + method + "<") ||
            s.Name.EndsWith("::" + method, StringComparison.Ordinal)).Offset;
        commitEntry = Entry("CommitTrst"); journalPageEntry = Entry("TrstWritePage");
        journalBlockEntry = Entry("TrstWriteBlock"); pressureFlushEntry = Entry("FlushNativeTrstGroup");
        activeCalls.Clear(); inclusiveCalls.Clear(); interrupts.Clear(); romCycles.Clear();
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
            ScopeStart = snapshotStart, ScopeEnd = callSnapshot?.Invoke(),
            InclusiveMethods = inclusiveCalls.Select(p => new { Name = p.Key, p.Value.Cycles, p.Value.Calls }).OrderByDescending(p => p.Cycles),
            Methods = rows.GroupBy(r => r.Name).Select(g => new { Name = g.Key, Cycles = g.Sum(r => r.Cycles), Instructions = g.Sum(r => r.Instructions) }).OrderByDescending(r => r.Cycles),
            Regions = rows.GroupBy(r => r.Region).Select(g => new { Region = g.Key, Cycles = g.Sum(r => r.Cycles), Instructions = g.Sum(r => r.Instructions) }),
            Interrupts = interrupts,
            RomExecution = romCycles.Select(r => new { Task = r.Key.Task, Name = TaskName(r.Key.Task),
                InterruptMask = r.Key.Mask, Cycles = r.Value }),
            RecognizedMoveBytesToCaller = recognizedMoveBytes, IoRequests = ioRequests, CopyEntries = copyEntries,
            TransactionCalls = transactionCalls, BlockTransfers = blockTransfers, DeviceCalls = deviceCalls, DosCalls = dosCalls,
            CacheIndexLookups = cacheLookups, CacheIndexHits = cacheHits,
            BlockCopyCallers = blockCopyCallers.Select(p => new
            {
                Pc = p.Key, p.Value.Cycles, p.Value.Calls,
                Name = p.Key >= codeBase && p.Key - codeBase < codeBytes
                    ? symbols.FirstOrDefault(s => p.Key - codeBase >= s.Offset && p.Key - codeBase - s.Offset < s.Size).Name ?? "<unmapped-handler>" : "<outside-handler>"
            }).OrderByDescending(p => p.Cycles),
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

    private string TaskName(uint task)
    {
        if (task == 0 || Memory(task, 14).Length != 14) return "";
        var name = ReadLong(task + 10); // Exec Node.ln_Name
        var text = new System.Text.StringBuilder();
        for (var i = 0u; i < 80; i++)
        {
            var c = Memory(name + i, 1);
            if (c.IsEmpty || c[0] == 0) break;
            text.Append((char)c[0]);
        }
        return text.ToString();
    }

    private bool DispatchInterrupt()
    {
        if (!dispatch()) return false;
        if (phase != null)
            interrupts.Add(new { At = machine.Cpu.Cycles - start, Entry = machine.Cpu.ProgramCounter,
                Mask = (machine.Cpu.StatusRegister >> 8) & 7 });
        return true;
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
                advance(wake); cpu.Cycles = Math.Max(cpu.Cycles, wake); DispatchInterrupt(); continue;
            }
            if (DispatchInterrupt()) continue;
            while (cpu.Cycles < end)
            {
                if (DispatchInterrupt()) continue;
                if (cpu.Halted || cpu.Stopped) break;
                var pc = cpu.ProgramCounter; var before = cpu.Cycles;
                if (phase == "overwrite") ObserveDosCall(pc, before);
                if (callDirectory != null)
                {
                    while (activeCalls.TryPeek(out var frame) && pc == frame.Return && core.State.A[7] == frame.Stack)
                    {
                        activeCalls.Pop(); var total = inclusiveCalls.GetValueOrDefault(frame.Name);
                        inclusiveCalls[frame.Name] = (total.Cycles + before - frame.Start, total.Calls + 1);
                    }
                    if (phase != null && pc == callReturn && core.State.A[7] == callReturnStack)
                    {
                        End(callDirectory, before); callReturn = 0;
                        if (stopCalls) callDirectory = null;
                    }
                    if (phase == null && watchedCalls.TryGetValue(pc, out var kind))
                    {
                        Begin($"{callStage}-{++callSequence:D4}-{kind}", before);
                        snapshotStart = callSnapshot?.Invoke();
                        callReturn = ReadLong(core.State.A[7]); callReturnStack = core.State.A[7] + 4;
                    }
                    if (phase != null)
                    {
                        // Maps omit overload signatures. Count nested calls to
                        // the same named family once, rather than doubling its
                        // inclusive cost through a forwarding overload.
                        if (methodEntries.TryGetValue(pc, out var name) && !activeCalls.Any(f => f.Name == name))
                            activeCalls.Push((name, ReadLong(core.State.A[7]), core.State.A[7] + 4, before));
                    }
                }
                else if (phase != null)
                {
                    // Full foreground profiles need the same inclusive call
                    // accounting, so commit costs include their I/O/checksum
                    // callees instead of reporting only wrapper instructions.
                    while (activeCalls.TryPeek(out var frame) && pc == frame.Return && core.State.A[7] == frame.Stack)
                    {
                        activeCalls.Pop(); var total = inclusiveCalls.GetValueOrDefault(frame.Name);
                        inclusiveCalls[frame.Name] = (total.Cycles + before - frame.Start, total.Calls + 1);
                        if (phase == "overwrite" && (frame.Name.Contains("::RelocateOverwrite<") ||
                            activeCalls.Any(f => f.Name.Contains("::RelocateOverwrite<"))))
                            transactionCalls.Add(new { Method = frame.Name, Start = frame.Start - start, Cycles = before - frame.Start,
                                Caller = activeCalls.TryPeek(out var parentFrame) ? parentFrame.Name : null });
                        if (frame.Name == "CopperStart.Dos.NativeFileSystemPlatform::Transfer")
                            deviceCalls.Add(new { Start = frame.Start - start, Cycles = before - frame.Start,
                                Caller = activeCalls.TryPeek(out var caller) ? caller.Name : null });
                    }
                    if (methodEntries.TryGetValue(pc, out var name) && !activeCalls.Any(f => f.Name == name))
                        activeCalls.Push((name, ReadLong(core.State.A[7]), core.State.A[7] + 4, before));
                }
                var measuredPhase = phase;
                if (measuredPhase != null && cacheReturn != 0 && cacheReturn == pc && core.State.A[7] == cacheReturnStack)
                {
                    // The native APTR return register is A0.
                    if (core.State.A[0] != 0) cacheHits++;
                    cacheReturn = 0;
                }
                if (measuredPhase != null && methodEntries.TryGetValue(pc, out var cacheMethod) &&
                    cacheMethod.EndsWith("::FindCached", StringComparison.Ordinal))
                {
                    cacheLookups++; cacheReturn = ReadLong(core.State.A[7]); cacheReturnStack = core.State.A[7] + 4;
                }
                if (measuredPhase != null && pc != codeBase &&
                    (pc == commitEntry || pc == journalPageEntry || pc == journalBlockEntry || pc == pressureFlushEntry))
                    transactionCalls.Add(new { Cycle = before, Call = methodEntries[pc],
                        Stack = activeCalls.Select(f => f.Name).ToArray(), Return = ReadLong(core.State.A[7]) - codeBase });
                if (measuredPhase != null) CountMovesToCaller(pc);
                if (measuredPhase != null && pc == blockCopyEntry && blockCopyBytes != 0)
                {
                    blockCopyCaller = ReadLong(core.State.A[7]);
                    blockTransfers.Add(new { Cycle = before, Caller = blockCopyCaller - codeBase,
                        Source = core.State.A[0], Destination = core.State.A[1], Bytes = core.State.D[0],
                        Stack = activeCalls.Take(9).Select(f => f.Name).ToArray() });
                    var caller = blockCopyCallers.GetValueOrDefault(blockCopyCaller);
                    blockCopyCallers[blockCopyCaller] = (caller.Cycles, caller.Calls + 1);
                }
                if (measuredPhase != null && pc == copyEntry && copyEntries.Count < 4096)
                    copyEntries.Add(new { Cycle = before, Source = core.State.A[0], Destination = core.State.A[1], Bytes = core.State.D[0] });
                // Attribute ROM instructions to the task and interrupt mask at entry.
                // ExecBase.ThisTask is at byte 276 in the Kickstart Exec ABI.
                var measureRom = measuredPhase != null && pc >= 0x1000000u - (uint)rom.Length && pc < 0x1000000u;
                var context = measureRom ? (ReadLong(ReadLong(4) + 276), (cpu.StatusRegister >> 8) & 7) : default;
                core.ExecuteInstruction();
                if (cpu.Cycles <= before) throw new InvalidOperationException("CPU clock did not advance.");
                if (measuredPhase != null && measuredPhase == phase)
                {
                    if (measureRom) romCycles[context] = romCycles.GetValueOrDefault(context) + cpu.Cycles - before;
                    if (dosReturn != 0)
                    {
                        if (pc >= codeBase && pc - codeBase < codeBytes)
                        {
                            var elapsed = cpu.Cycles - before;
                            dosHandler += elapsed;
                            dosHandlerPcs[pc] = dosHandlerPcs.GetValueOrDefault(pc) + elapsed;
                        }
                        else if (pc >= 0xF80000 && pc < 0x1000000) dosRom += cpu.Cycles - before;
                    }
                    var value = counts.GetValueOrDefault(pc);
                    counts[pc] = (value.Cycles + cpu.Cycles - before, value.Instructions + 1);
                    if (pc >= blockCopyEntry && pc - blockCopyEntry < blockCopyBytes)
                    {
                        var caller = blockCopyCallers.GetValueOrDefault(blockCopyCaller);
                        blockCopyCallers[blockCopyCaller] = (caller.Cycles + cpu.Cycles - before, caller.Calls);
                    }
                }
                advance(cpu.Cycles);
            }
        }
    }
}
