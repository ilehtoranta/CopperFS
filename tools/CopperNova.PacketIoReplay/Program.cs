using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amiga;
using Copper68k;
using CopperStart.Exec.Tests;

if (args.Length != 2)
    throw new ArgumentException("Usage: CopperNova.PacketIoReplay <handler> <receipt.json>");

byte[] image = File.ReadAllBytes(args[0]);
string[] map = File.ReadAllLines(args[0] + ".map");
uint Method(string name) => Convert.ToUInt32(map.Single(line =>
    Regex.IsMatch(line, "^[0-9A-F]{8} +[0-9]+ ") && line.EndsWith("::" + name))[..8], 16);

const uint code = 0x10000, arena = 0x20000, request = 0x140000;
const uint exec = 0xe00000, device = 0xd00000, stack = 0xf00000, returned = 0xf10000;
const uint tokenAt = 0x820, actualAt = 0x824;

// Replay the private transport entry with its emitted D0/A0/A1 calling convention.
// The mounted geometry describes a public DMA range and an 8192-byte partition offset.
string[] cases = [
    "quick-read", "quick-write", "quick-flush", "short", "device-error", "deferred",
    "deferred-short", "deferred-error", "deferred-cancel", "stale-token", "dma-bounce", "max-transfer-split",
    "wide-low-write", "wide-high-write", "wide-cross-write", "wide-carry-write", "wide-base-high-write",
    "wide-deferred-read", "wide-deferred-short", "wide-deferred-cancel", "wide-flush", "wide-generic-high-write"
];
foreach (string scenario in cases)
{
    var bus = NativeParityBus.LoadHunk(image, code);
    using var cpu = M68kCoreFactory.Default.Create(M68kCpuModel.M68000, bus);
    bool flush = scenario.EndsWith("-flush"), bounce = scenario == "dma-bounce", split = scenario == "max-transfer-split";
    bool deferred = scenario.Contains("deferred") || scenario == "stale-token";
    bool wide = scenario.StartsWith("wide-");
    bool generic = bounce || split || scenario == "wide-generic-high-write";
    ulong partitionStart = scenario == "wide-base-high-write" ? 0x2ffffe000UL : scenario == "wide-flush" ? 0x200002000UL : 8192;
    uint sector = scenario switch
    {
        "wide-cross-write" => 0x7fffee,
        "wide-carry-write" => 0x7ffff0,
        "wide-base-high-write" => 32,
        "wide-low-write" => 0x3000,
        _ => wide ? 0x800020u : 10u
    };
    uint kind = flush ? 2u : scenario.EndsWith("-write") ? 1u : 0u;
    uint bytes = flush ? 0u : wide ? 65536u : split ? 1024u : 512u;
    uint buffer = flush ? 0u : bounce ? 0x300001u : 0x300000u;
    uint maximum = split ? 512u : 65536u;
    uint submissions = 0, waits = 0, aborts = 0;

    void Check(bool condition, string detail)
    {
        if (!condition) throw new InvalidDataException(scenario + " failed at " + detail);
    }

    uint Invoke(string method)
    {
        bus.WriteLong(stack, returned);
        cpu.Reset(code + Method(method), stack);
        cpu.State.D[0] = arena;
        cpu.State.A[0] = tokenAt;
        cpu.State.A[1] = actualAt;
        for (int i = 0; i < 50000 && cpu.State.ProgramCounter != returned; i++)
        {
            cpu.ExecuteInstruction();
            Check(!cpu.State.Halted, "CPU halt");
        }
        Check(cpu.State.ProgramCounter == returned, "instruction bound");
        return cpu.State.D[0];
    }

    bus.WriteLong(4, exec);
    cpu.Reset(code, stack);
    cpu.ExecuteInstruction(); // The HUNK startup installs ExecBase for SDK calls.
    bus.WriteLong(request + ExecLayout.IORequest.Device, device);

    const uint direct = arena + 3072, transport = arena + 2048, io = arena + 136;
    bus.WriteLong(direct, scenario == "wide-generic-high-write" ? 0 : request);
    bus.WriteLong(direct + 4, 0x7fffffff);
    bus.WriteLong(direct + 8, 0x80000001);
    bus.WriteLong(direct + 12, maximum);
    bus.WriteLong(direct + 16, unchecked((uint)partitionStart));
    bus.WriteLong(direct + 20, (uint)(partitionStart >> 32));
    bus.WriteLong(transport + 16, request);
    bus.WriteLong(transport + 20, (uint)(partitionStart >> 32));
    bus.WriteLong(transport + 24, unchecked((uint)partitionStart));
    bus.WriteLong(transport + 32, wide ? 0x820000u : 4096u);
    bus.WriteLong(transport + 36, maximum);
    bus.WriteLong(transport + 40, 0x7ffffffe);
    bus.WriteLong(transport + 44, 1);
    bus.WriteLong(transport + 68, 9);
    bus.WriteLong(transport + 76, arena + 4096);
    bus.WriteLong(transport + 80, 4096);
    bus.WriteLong(transport + 100, 1);
    bus.WriteLong(io, kind);
    bus.WriteLong(io + 8, flush ? 0u : sector);
    bus.WriteLong(io + 12, buffer);
    bus.WriteLong(io + 16, bytes);
    bus.WriteLong(io + 20, 37);

    bus.RegisterGateway(device - 30, registers =>
    {
        Check(registers.A[1] == request, "device request address");
        Check(bus.ReadWord(request + ExecLayout.IORequest.Command) == (flush ? 4 : kind == 1 ? 25 : 24), "command");
        Check(bus.ReadLong(transport) == (generic ? 1u : 0u), "fast path selection");
        ulong offset = flush ? 0 : partitionStart + (ulong)sector * 512 + submissions * 512;
        Check(bus.ReadLong(request + ExecLayout.IOStdReq.Offset) == unchecked((uint)offset), "partition offset");
        Check(bus.ReadLong(request + ExecLayout.IOStdReq.Actual) == (uint)(offset >> 32), "TD64 upper offset");
        Check(bus.ReadLong(request + ExecLayout.IOStdReq.Length) == (split ? 512 : bytes), "command length");
        Check(bus.Memory[request + ExecLayout.IORequest.Flags] == 1, "quick eligibility");
        Check(bus.ReadLong(request + ExecLayout.IOStdReq.Data) == (bounce ? arena + 4096 : buffer + submissions * 512), "DMA buffer");
        if (bounce) Array.Fill(bus.Memory, (byte)0x64, (int)(arena + 4096), 512);
        bus.WriteLong(request + ExecLayout.IOStdReq.Actual, scenario.Contains("short") ? 256u : split ? 512u : bytes);
        bus.Memory[request + ExecLayout.IORequest.Error] = scenario.EndsWith("error") ? (byte)20 : (byte)0;
        if (deferred) bus.Memory[request + ExecLayout.IORequest.Flags] = 0;
        submissions++;
    });
    bus.RegisterGateway(exec - 468, registers => registers.D[0] = request); // CheckIO
    bus.RegisterGateway(exec - 474, registers => { waits++; registers.D[0] = 0; }); // WaitIO
    bus.RegisterGateway(exec - 480, registers => { aborts++; registers.D[0] = 0; }); // AbortIO

    uint result = Invoke("ServiceIo");
    if (deferred || split)
    {
        Check(result == 0, "pending continuation");
        if (scenario.EndsWith("deferred-cancel")) Invoke("RequestStop");
        if (scenario == "stale-token") bus.WriteLong(transport + 8, 99);
        result = Invoke("ServiceIo");
    }
    bool failure = scenario.Contains("short") || scenario.EndsWith("error") || scenario.EndsWith("deferred-cancel") || scenario == "stale-token";
    Check(result == (failure ? 3 : 2), "completion");
    Check(bus.ReadLong(tokenAt) == 37, "engine token");
    if (!failure) Check(bus.ReadLong(actualAt) == bytes, "completed bytes");
    Check(submissions == (split ? 2 : 1) && waits == (deferred ? 1 : 0) && aborts == (scenario.EndsWith("deferred-cancel") ? 1 : 0), "device lifecycle");
    Check(bus.ReadLong(transport) == 0, "retired request");
    if (bounce) Check(bus.Memory.AsSpan((int)buffer, 512).ToArray().All(b => b == 0x64), "bounce readback");
}

File.WriteAllText(args[1], JsonSerializer.Serialize(new {
    HandlerSha256 = Convert.ToHexString(SHA256.HashData(image)), Cases = cases
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Passed {cases.Length} MC68000 I/O cases.");
