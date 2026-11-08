using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amiga;
using Copper68k;
using CopperStart.Dos;
using CopperStart.Exec.Tests;
using Xunit.Abstractions;

namespace CopperFS.Native.Tests;

public sealed class SequentialReadBenchmarkFactAttribute : FactAttribute
{
    public SequentialReadBenchmarkFactAttribute()
    {
        if (!System.IO.File.Exists(Environment.GetEnvironmentVariable("COPPERFS_READ_BENCH_MANIFEST")))
            Skip = "Set COPPERFS_READ_BENCH_MANIFEST to a prepared before/after benchmark manifest.";
    }
}

public sealed class SequentialReadBenchmarks(ITestOutputHelper output)
{
    public sealed record Pair(string Name, bool Sfs2, uint CacheEntries, string Before, string After);
    public sealed record Workload(uint PacketBytes, uint ReadBytes);
    public sealed record Manifest(string BaselineCommit, Pair[] Pairs, int Trials, string[] CpuModels,
        uint FileBytes, uint CacheBlocks, uint MaxTransfer, Workload[] Workloads);
    public sealed record Measurement(string Profile, string Variant, string Cpu, uint PacketBytes, uint ReadBytes,
        int Trial, long Cycles, long FirstPacketCycles, long Instructions, int DeviceRequests,
        int MetadataRequests, int DataRequests, ulong DeviceBytes, double HostExecutionMilliseconds,
        string ContentSha256, string HandlerSha256);

    [SequentialReadBenchmarkFact]
    public void CompareActualHandlersWithIdenticalContiguousMediaAndDevicePolicy()
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_READ_BENCH_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(System.IO.File.ReadAllText(path))!;
        var results = new List<Measurement>();
        var resultPath = Path.Combine(Path.GetDirectoryName(path)!, "measurements.json");
        // Equal code paths are primed in separate fixtures; measured trials
        // always start with a fresh handle and empty filesystem caches.
        foreach (var pair in manifest.Pairs)
        foreach (var modelName in manifest.CpuModels)
        {
            var model = Enum.Parse<M68kCpuModel>(modelName);
            foreach (var variant in new[] { "before", "after" })
                _ = new Fixture(manifest, pair, variant, model).Measure(new(64, 512), -1);
            foreach (var workload in manifest.Workloads)
            for (var trial = 0; trial < manifest.Trials; trial++)
            {
                foreach (var variant in trial % 2 == 0 ? new[] { "before", "after" } : new[] { "after", "before" })
                {
                    var result = new Fixture(manifest, pair, variant, model).Measure(workload, trial);
                    results.Add(result);
                    System.IO.File.WriteAllText(resultPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                    output.WriteLine($"{pair.Name} {modelName} {workload.PacketBytes} B {variant} trial {trial}: " +
                        $"{result.Cycles} cycles; {result.DeviceRequests} requests; {result.DeviceBytes} device bytes");
                }
            }
        }
        foreach (var group in results.GroupBy(r => (r.Profile, r.Variant, r.Cpu, r.PacketBytes)))
        {
            Assert.Single(group.Select(r => r.Cycles).Distinct());
            Assert.Single(group.Select(r => r.DeviceRequests).Distinct());
            Assert.Single(group.Select(r => r.ContentSha256).Distinct());
        }
        output.WriteLine($"Saved {results.Count} checked trials to {resultPath}");
    }

    private sealed class Fixture
    {
        private const uint State = 0x2000, Handle = 0x5000, Packet = 0x6000, Request = 0x7000;
        private const uint Cache = 0x40000, Buffer = 0x60000, Load = 0x100000, Exec = 0xE00000, Entries = 0x800000;
        private const uint DataFirst = 512, VolumeBlocks = 8192, Stack = 0xF00000, Returned = 0xF10000;
        private readonly NativeParityBus bus;
        private readonly byte[] disk = new byte[VolumeBlocks * 512];
        private readonly byte[] payload;
        private readonly Manifest manifest;
        private readonly Pair pair;
        private readonly string variant, handlerHash;
        private readonly M68kCpuModel model;
        private readonly uint entry;
        private int requests, metadataRequests, dataRequests;
        private ulong deviceBytes;

        internal Fixture(Manifest manifest, Pair pair, string variant, M68kCpuModel model)
        {
            this.manifest = manifest; this.pair = pair; this.variant = variant; this.model = model;
            var path = variant == "before" ? pair.Before : pair.After;
            var hunk = System.IO.File.ReadAllBytes(path);
            handlerHash = Convert.ToHexString(SHA256.HashData(hunk));
            bus = NativeParityBus.LoadHunk(hunk, Load);
            var map = System.IO.File.ReadAllText(path + ".map");
            uint Symbol(string expression)
            {
                var match = Regex.Match(map, @"(?m)^([0-9A-Fa-f]{8})\s+\d+\s+" + expression + @"\r?$");
                Assert.True(match.Success, expression);
                return Load + uint.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            entry = Symbol(Regex.Escape("CopperStart.Dos.NativeAmigaFileSystem::Dispatch"));
            bus.WriteLong(Symbol("_ExecBase"), Exec); bus.WriteLong(4, Exec);
            var list = Exec + ExecLayout.ExecBase.MemList; const uint memory = 0xE20000;
            bus.WriteLong(list, memory); bus.WriteLong(memory, list + 4);
            bus.WriteLong(memory + ExecLayout.MemHeader.Lower, 0x400);
            bus.WriteLong(memory + ExecLayout.MemHeader.Upper, 0x1000000);
            foreach (var lvo in new uint[] { 624, 630 })
                bus.RegisterGateway(Exec - lvo, cpu => bus.Memory.AsSpan((int)cpu.A[0], (int)cpu.D[0])
                    .CopyTo(bus.Memory.AsSpan((int)cpu.A[1], (int)cpu.D[0])));
            bus.RegisterGateway(Exec - 456, cpu =>
            {
                var request = cpu.A[1];
                Assert.Equal(2, bus.ReadWord(request + ExecLayout.IORequest.Command));
                var offset = bus.ReadLong(request + ExecLayout.IOStdReq.Offset);
                var bytes = bus.ReadLong(request + ExecLayout.IOStdReq.Length);
                var target = bus.ReadLong(request + ExecLayout.IOStdReq.Data);
                Assert.InRange(bytes, 1u, manifest.MaxTransfer);
                Assert.Equal(0u, (offset | bytes) & 511);
                requests++; deviceBytes += bytes;
                if (offset < DataFirst * 512 + manifest.FileBytes && (ulong)offset + bytes > DataFirst * 512) dataRequests++;
                else metadataRequests++;
                disk.AsSpan((int)offset, (int)bytes).CopyTo(bus.Memory.AsSpan((int)target, (int)bytes));
                bus.Memory[request + ExecLayout.IORequest.Error] = 0;
                bus.WriteLong(request + ExecLayout.IOStdReq.Actual, bytes);
            });
            bus.WriteLong(State + 4, Request); bus.WriteLong(State + 8, Cache);
            bus.WriteLong(State + 12, uint.MaxValue); bus.WriteLong(State + 20, pair.Sfs2 ? 0x53465302u : 0x444F5301u);
            bus.WriteLong(State + 32, Handle); bus.WriteLong(State + 1600, VolumeBlocks);
            bus.WriteLong(State + 1604, 512); bus.WriteLong(State + 1608, 2); bus.WriteLong(State + 1612, 512);
            bus.WriteLong(State + 1624, manifest.CacheBlocks); bus.WriteLong(State + 1628, manifest.MaxTransfer);
            bus.WriteLong(State + 1632, 1); bus.WriteLong(State + 1636, 0xFFFFFFFE);
            bus.WriteLong(State + 1656, manifest.CacheBlocks); bus.WriteLong(State + 1660, manifest.CacheBlocks * 512);
            if (pair.CacheEntries != 0)
            {
                const uint stride = 544;
                bus.WriteLong(State + 44, Entries); bus.WriteLong(State + 48, pair.CacheEntries);
                bus.WriteLong(State + 2016, Entries + (pair.CacheEntries - 1) * stride);
                for (var i = 0u; i < pair.CacheEntries; i++)
                {
                    var page = Entries + i * stride;
                    bus.WriteLong(page, i + 1 == pair.CacheEntries ? 0 : page + stride);
                    bus.WriteLong(page + 4, uint.MaxValue); bus.WriteLong(page + 8, i == 0 ? 0 : page - stride);
                }
            }
            bus.WriteLong(Handle + 4, pair.Sfs2 ? 8u : 10u); bus.WriteLong(Handle + 8, manifest.FileBytes);
            bus.WriteLong(Handle + 16, 1);
            if (pair.Sfs2) Sfs2Image(); else FfsImage();
            payload = new byte[manifest.FileBytes];
            for (var i = 0u; i < manifest.FileBytes; i++) payload[i] = (byte)(i * 17 + (i >> 9) * 31 + (i >> 17) * 7 + 3);
            payload.CopyTo(disk, DataFirst * 512);
        }

        internal Measurement Measure(Workload workload, int trial)
        {
            Assert.True(workload.ReadBytes <= manifest.FileBytes && workload.ReadBytes % workload.PacketBytes == 0);
            Assert.InRange(workload.PacketBytes, 1u, 65536u);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var cpu = M68kCoreFactory.Default.Create(model, bus);
            long cycles = 0, firstCycles = 0, instructions = 0, hostTicks = 0;
            for (var position = 0u; position < workload.ReadBytes; position += workload.PacketBytes)
            {
                bus.WriteLong(Packet + 8, (uint)DosPacketAction.Read); bus.WriteLong(Packet + 20, Handle);
                bus.WriteLong(Packet + 24, Buffer); bus.WriteLong(Packet + 28, workload.PacketBytes);
                bus.Memory.AsSpan((int)Buffer, (int)workload.PacketBytes + 8).Fill(0xA7);
                bus.WriteLong(Stack, Returned); cpu.Reset(entry, Stack);
                cpu.State.A[0] = State; cpu.State.A[1] = Packet; cpu.State.A[6] = Exec;
                var initialCycles = cpu.State.Cycles;
                var start = Stopwatch.GetTimestamp(); var steps = 0;
                for (; steps < 20_000_000 && cpu.State.ProgramCounter != Returned && !cpu.State.Halted; steps++) cpu.ExecuteInstruction();
                hostTicks += Stopwatch.GetTimestamp() - start;
                var packetCycles = cpu.State.Cycles - initialCycles;
                cycles += packetCycles; instructions += steps;
                if (position == 0) firstCycles = packetCycles;
                Assert.Equal(Returned, cpu.State.ProgramCounter); Assert.Equal(Stack + 4, cpu.State.A[7]);
                Assert.Equal(workload.PacketBytes, bus.ReadLong(Packet + 12)); Assert.Equal(0u, bus.ReadLong(Packet + 16));
                Assert.Equal(position + workload.PacketBytes, bus.ReadLong(Handle + 12));
                Assert.Equal(0xA7, bus.Memory[Buffer + workload.PacketBytes]);
                digest.AppendData(bus.Memory.AsSpan((int)Buffer, (int)workload.PacketBytes));
            }
            var contentHash = Convert.ToHexString(digest.GetHashAndReset());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(payload.AsSpan(0, (int)workload.ReadBytes))), contentHash);
            Assert.True(cycles > 0);
            return new(pair.Name, variant, model.ToString(), workload.PacketBytes, workload.ReadBytes, trial,
                cycles, firstCycles, instructions, requests, metadataRequests, dataRequests, deviceBytes,
                (double)hostTicks / Stopwatch.Frequency * 1000, contentHash, handlerHash);
        }

        private Span<byte> Block(uint block, uint type, bool sfs2 = false)
        {
            var b = disk.AsSpan((int)block * 512, 512); Put(b, 0, type);
            if (sfs2) Put(b, 8, block);
            return b;
        }
        private static void Put(Span<byte> b, int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(b[at..], value);
        private static void Seal(Span<byte> b, bool sfs2 = false)
        {
            var checksum = sfs2 ? 4 : 20; Put(b, checksum, 0); uint sum = sfs2 ? 2u : 0;
            for (var i = 0; i < 512; i += 4) sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(b[i..]));
            Put(b, checksum, unchecked(0u - sum));
        }
        private void FfsImage()
        {
            var blocks = manifest.FileBytes / 512; var tables = (blocks + 71) / 72;
            for (var table = 0u; table < tables; table++)
            {
                var key = 10 + table; var b = Block(key, table == 0 ? 2u : 16u);
                var count = Math.Min(72u, blocks - table * 72);
                Put(b, 4, key); Put(b, 8, count); Put(b, 508, unchecked((uint)-3));
                Put(b, 500, table == 0 ? VolumeBlocks / 2 : 10u);
                if (table == 0) Put(b, 324, manifest.FileBytes);
                if (table + 1 < tables) Put(b, 504, key + 1);
                for (var i = 0u; i < count; i++) Put(b, 308 - (int)i * 4, DataFirst + table * 72 + i);
                Seal(b);
            }
        }
        private void Sfs2Image()
        {
            foreach (var n in new[] { 0u, VolumeBlocks - 1 })
            {
                var b = Block(n, 0x53465302, true); b[13] = 4; Put(b, 48, VolumeBlocks); Put(b, 52, 512);
                Put(b, 104, 3); Put(b, 108, 6); Put(b, 112, 7); Seal(b, true);
            }
            var root = Block(3, 0x4F424A43, true); Put(root, 28, 1); Put(root, 36, 10); root[50] = 128; root[51] = (byte)'V'; Seal(root, true);
            var clean = Block(5, 0x54524F4B, true); Seal(clean, true);
            var file = Block(10, 0x4F424A43, true); Put(file, 12, 1); Put(file, 28, 8);
            Put(file, 36, DataFirst); Put(file, 40, manifest.FileBytes >> 16);
            BinaryPrimitives.WriteUInt16BigEndian(file[44..], (ushort)manifest.FileBytes); file[51] = (byte)'F'; Seal(file, true);
            var nodes = Block(7, 0x4E444320, true); Put(nodes, 12, 1); Put(nodes, 16, 1); Put(nodes, 20, 3); Put(nodes, 90, 10); Seal(nodes, true);
            var tree = Block(6, 0x424E4443, true); tree[13] = 1; tree[14] = 1; tree[15] = 16;
            Put(tree, 16, DataFirst); Put(tree, 24, 0x80000008); Put(tree, 28, manifest.FileBytes / 512); Seal(tree, true);
        }
    }
}
