// Adapted from CopperStart ed02a70, tests/CopperStart.Floppy.Native.Tests/NativeCacheInvalidationInstructionTests.cs. Copyright (c) 2026 Ilkka Lehtoranta. All rights reserved.
using System.Globalization;
using System.Text.RegularExpressions;
using Copper68k;
using CopperStart.Exec.Tests;

namespace CopperFS.Native.Tests;

public sealed class NativeCacheInvalidationInstructionTests
{
    private const uint Load = 0x100000, Platform = 0x1800, State = 0x2000;
    private const uint File = 0x4000, Directory = 0x4200, Enumeration = 0x5000, Entries = 0x6000;

    [CacheHandlerTheory]
    [InlineData(0, 32)] [InlineData(1, 32)] [InlineData(2, 32)] [InlineData(3, 32)]
    [InlineData(4, 32)] [InlineData(4, 96)]
    [InlineData(0, 96)] [InlineData(1, 96)] [InlineData(2, 96)] [InlineData(3, 96)]
    public void CompiledCacheInvalidationPreservesHashLruAndCursorSemantics(int pattern, int capacity)
    {
        var path = Environment.GetEnvironmentVariable("COPPERSFS_HANDLER_PATH")!;
        var entry = Entry(path);
        var summary = HasSummary(path);
        var reindex = Entry(path, "CopperStart.Dos.NativeAmigaFileSystem::ReindexCache");
        foreach (var model in new[] { M68kCpuModel.M68000, M68kCpuModel.M68020 })
        {
            var bus = NativeParityBus.LoadHunk(System.IO.File.ReadAllBytes(path), Load);
            foreach (var region in new uint[] { 4096, 0xFFFFFFC0 })
            for (var bucket = 0u; bucket < 64; bucket++)
            foreach (var count in new uint[] { 0, 1, 2, 3, 4, 7, 15, 16, 31, 32, 63, 64, 65 })
            {
                var block = region + bucket;
                var keys = Enumerable.Range(0, capacity).Select(i => pattern switch
                {
                    0 => uint.MaxValue, // Every bucket empty; all allocated LRU slots still present.
                    1 => 320u + (uint)i, // Occupied buckets with nonmatching keys.
                    2 => block - 16 + (uint)i, // Dense matching and nonmatching chains, including wrap.
                    4 => block + 2048 + (uint)i * 64, // Occupied summary aliases, no matching data.
                    _ => block + (uint)(i % 4) + (uint)(i / 4) * 64 // Long collision chains.
                }).ToArray();
                Seed(bus, keys, summary);
                Invoke(bus, reindex, model, 0, 0, reindex: true);
                if (summary) CheckSummary(bus);
                InvokeInvalidation(bus, path, entry, model, block, count);
                Check(bus, keys, block, count, summary);
                if (summary) CheckSummary(bus);
            }
        }
    }

    private static void Seed(NativeParityBus bus, uint[] keys, bool summary)
    {
        bus.Memory.AsSpan(0x1700, 0x7900).Fill(0xA5);
        bus.Memory.AsSpan((int)State, summary ? 2452 : 2320).Clear();
        bus.Memory.AsSpan((int)File, 216).Clear(); bus.Memory.AsSpan((int)Directory, 216).Clear();
        bus.WriteLong(Platform, State); bus.WriteLong(State + 32, File); bus.WriteLong(State + 2032, Enumeration);
        bus.WriteLong(State + 44, Entries); bus.WriteLong(State + 48, (uint)keys.Length);
        bus.WriteLong(State + 2016, Entries + (uint)(keys.Length - 1) * 64);
        bus.WriteLong(File, Directory); bus.WriteLong(File + 16, 1); bus.WriteLong(File + 56, 99);
        bus.WriteLong(Directory + 40, 88); bus.WriteLong(Enumeration, 7);
        for (var i = 0; i < keys.Length; i++)
        {
            var node = Entries + (uint)i * 64;
            bus.WriteLong(node, i + 1 == keys.Length ? 0 : node + 64);
            bus.WriteLong(node + 4, keys[i]); bus.WriteLong(node + 8, i == 0 ? 0 : node - 64);
            bus.WriteLong(node + 12, 0); bus.WriteLong(node + 16, 0x1234);
            if (keys[i] == uint.MaxValue) continue;
            var bucket = State + 1760 + (keys[i] & 63) * 4;
            bus.WriteLong(node + 12, bus.ReadLong(bucket)); bus.WriteLong(bucket, node);
        }
    }

    private static void Check(NativeParityBus bus, uint[] keys, uint block, uint count, bool summary)
    {
        var retained = new HashSet<uint>();
        for (var i = 0; i < keys.Length; i++)
        {
            var node = Entries + (uint)i * 64;
            var invalidSlotScanned = count != 1 && !(count < 64 && count < keys.Length);
            var removed = (keys[i] != uint.MaxValue || invalidSlotScanned) && keys[i] >= block && keys[i] - block < count;
            Assert.Equal(removed ? uint.MaxValue : keys[i], bus.ReadLong(node + 4));
            Assert.Equal(removed ? 0u : 0x1234u, bus.ReadLong(node + 16));
            Assert.All(bus.Memory.AsSpan((int)node + 20, 44).ToArray(), b => Assert.Equal((byte)0xA5, b));
            if (!removed && keys[i] != uint.MaxValue) retained.Add(node);
        }
        var hashed = new HashSet<uint>();
        for (var bucket = 0u; bucket < 64; bucket++)
        {
            var node = bus.ReadLong(State + 1760 + bucket * 4);
            while (node != 0)
            {
                Assert.True(retained.Contains(node) && hashed.Add(node));
                Assert.Equal(bucket, bus.ReadLong(node + 4) & 63);
                node = bus.ReadLong(node + 12);
            }
        }
        Assert.True(retained.SetEquals(hashed));
        var lru = new HashSet<uint>(); var current = bus.ReadLong(State + 44); var previous = 0u;
        while (current != 0)
        {
            Assert.True(current >= Entries && current < Entries + keys.Length * 64 && (current - Entries) % 64 == 0 && lru.Add(current));
            Assert.Equal(previous, bus.ReadLong(current + 8)); previous = current; current = bus.ReadLong(current);
        }
        Assert.Equal(keys.Length, lru.Count); Assert.Equal(previous, bus.ReadLong(State + 2016));
        Assert.Equal(uint.MaxValue, bus.ReadLong(State + 12)); Assert.Equal(8u, bus.ReadLong(Enumeration));
        Assert.Equal(0u, bus.ReadLong(File + 56)); Assert.Equal(0u, bus.ReadLong(Directory + 40));
        Assert.Equal(0xA5A5A5A5u, bus.ReadLong(State - 4)); Assert.Equal(0xA5A5A5A5u, bus.ReadLong(State + (summary ? 2452u : 2320u)));
    }

    [NativeCacheSummaryTheory]
    [InlineData(512)] [InlineData(1024)]
    public void CompiledCacheSummaryTracksInsertEvictionInvalidationResizeAndReset(int bytes)
    {
        var path = Environment.GetEnvironmentVariable("COPPERSFS_HANDLER_PATH")!;
        var insert = Entry(path, "CopperStart.Dos.NativeFileSystemPlatform::CacheBlock");
        var reindex = Entry(path, "CopperStart.Dos.NativeAmigaFileSystem::ReindexCache");
        var invalidate = Entry(path);
        foreach (var model in new[] { M68kCpuModel.M68000, M68kCpuModel.M68020 })
        {
            var bus = NativeParityBus.LoadHunk(System.IO.File.ReadAllBytes(path), Load);
            bus.WriteLong(Platform, State); bus.WriteLong(State + 44, Entries); bus.WriteLong(State + 48, 8);
            var stride = (uint)bytes + 20;
            for (var i = 0u; i < 8; i++)
            {
                var node = Entries + i * stride;
                bus.WriteLong(node, i == 7 ? 0 : node + stride); bus.WriteLong(node + 4, uint.MaxValue);
            }
            Invoke(bus, reindex, model, 0, 0, reindex: true); CheckSummary(bus);
            const uint payload = 0xC000;
            for (var i = 0u; i < 64; i++)
            {
                var key = i % 4 * 2048 + i * 3 + 100;
                var reused = bus.ReadLong(State + 2016);
                bus.Memory.AsSpan((int)payload, bytes).Fill((byte)(i + 19));
                // CacheBlock private ABI: this=D0, key=D1, id=A0, source=A1, bytes=stack.
                Invoke(bus, insert, model, key, 0, source: payload, bytes: (uint)bytes);
                Assert.Equal(reused, bus.ReadLong(State + 44)); Assert.Equal(key, bus.ReadLong(reused + 4));
                Assert.Equal(bus.Memory.AsSpan((int)payload, bytes).ToArray(), bus.Memory.AsSpan((int)reused + 20, bytes).ToArray());
                CheckSummary(bus);
                if (i % 3 == 0)
                {
                    var low = key & ~15u;
                    var old = CacheKeys(bus);
                    InvokeInvalidation(bus, path, invalidate, model, low, 16);
                    foreach (var (node, value) in old)
                        Assert.Equal(value >= low && value - low < 16 ? uint.MaxValue : value, bus.ReadLong(node + 4));
                    CheckSummary(bus);
                }
            }
            // Shrink the surviving list, then reindex exactly as MoreCache does.
            var tail = bus.ReadLong(State + 44);
            for (var i = 0; i < 3; i++) tail = bus.ReadLong(tail);
            bus.WriteLong(tail, 0); bus.WriteLong(State + 48, 4);
            Invoke(bus, reindex, model, 0, 0, reindex: true); CheckSummary(bus);
            Assert.Equal(tail, bus.ReadLong(State + 2016));
            // Newly allocated slots begin invalid and must not set summary bits.
            for (var i = 0u; i < 4; i++)
            {
                var node = 0x12000u + i * stride;
                bus.WriteLong(node, bus.ReadLong(State + 44)); bus.WriteLong(node + 4, uint.MaxValue);
                bus.WriteLong(State + 44, node);
            }
            bus.WriteLong(State + 48, 8);
            Invoke(bus, reindex, model, 0, 0, reindex: true); CheckSummary(bus);
            foreach (var node in CacheKeys(bus).Keys) bus.WriteLong(node + 4, uint.MaxValue);
            Invoke(bus, reindex, model, 0, 0, reindex: true); CheckSummary(bus);
            Assert.Equal(0u, bus.ReadLong(State + 2320));
        }
    }

    private static Dictionary<uint, uint> CacheKeys(NativeParityBus bus)
    {
        var keys = new Dictionary<uint, uint>();
        var node = bus.ReadLong(State + 44);
        while (node != 0)
        {
            Assert.True(keys.TryAdd(node, bus.ReadLong(node + 4)));
            node = bus.ReadLong(node);
        }
        Assert.Equal(bus.ReadLong(State + 48), (uint)keys.Count);
        return keys;
    }

    private static void CheckSummary(NativeParityBus bus)
    {
        var counts = new uint[32]; var mask = 0u;
        foreach (var key in CacheKeys(bus).Values)
            if (key != uint.MaxValue) counts[(key / 64) % 32]++;
        for (var page = 0; page < 32; page++)
        {
            Assert.Equal(counts[page], bus.ReadLong(State + 2324 + (uint)page * 4));
            if (counts[page] != 0) mask |= 1u << page;
        }
        Assert.Equal(mask, bus.ReadLong(State + 2320));
    }

    private static bool HasSummary(string path) => System.IO.File.ReadAllText(path + ".map").Contains("CopperStart.Dos.NativeFileSystemPlatform::InvalidateBlockImages");

    private static uint Entry(string path, string symbol = "CopperStart.Dos.NativeFileSystemPlatform::InvalidateBlockImages")
    {
        var match = Regex.Match(System.IO.File.ReadAllText(path + ".map"), @"(?m)^([0-9A-Fa-f]{8})\s+\d+\s+" + Regex.Escape(symbol) + @"\r?$");
        Assert.True(match.Success, symbol);
        return uint.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    // InvalidateBlocks is inlined: execute its two calls in source order.
    private static void InvokeInvalidation(NativeParityBus bus, string path, uint images, M68kCpuModel model, uint block, uint count)
    {
        var cursors = Entry(path, "CopperStart.Dos.NativeAmigaFileSystem::InvalidateDirectoryCursors");
        const uint stack = 0xF00000, returned = 0xF10000;
        bus.WriteLong(stack, returned);
        using var cpu = M68kCoreFactory.Default.Create(model, bus);
        cpu.Reset(Load + cursors, stack);
        cpu.State.A[0] = State;
        for (var step = 0; step < 100000 && cpu.State.ProgramCounter != returned; step++)
        { cpu.ExecuteInstruction(); Assert.False(cpu.State.Halted); }
        Assert.Equal(returned, cpu.State.ProgramCounter);
        Invoke(bus, images, model, block, count);
    }
    private static void Invoke(NativeParityBus bus, uint entry, M68kCpuModel model, uint block, uint count, bool reindex = false, uint source = 0, uint bytes = 0)
    {
        const uint stack = 0xF00000, returned = 0xF10000;
        bus.WriteLong(stack, returned); bus.WriteLong(stack + 4, source == 0 ? 0xC7C7C7C7 : bytes);
        using var cpu = M68kCoreFactory.Default.Create(model, bus);
        cpu.Reset(Load + entry, stack);
        // Private instance ABI: ref this=D0, block=D1, count=A0.
        cpu.State.D[0] = Platform; cpu.State.D[1] = block; cpu.State.A[0] = reindex ? State : count; cpu.State.A[1] = source;
        for (var i = 2; i < 8; i++) cpu.State.D[i] = 0xCAFE0000u + (uint)i;
        for (var i = 2; i < 7; i++) cpu.State.A[i] = 0xBAD000u + (uint)i * 4;
        for (var step = 0; step < 100000 && cpu.State.ProgramCounter != returned; step++)
        { cpu.ExecuteInstruction(); Assert.False(cpu.State.Halted); }
        Assert.Equal(returned, cpu.State.ProgramCounter); Assert.Equal(stack + 4, cpu.State.A[7]);
        Assert.Equal(source == 0 ? 0xC7C7C7C7u : bytes, bus.ReadLong(stack + 4));
        for (var i = 2; i < 8; i++) Assert.Equal(0xCAFE0000u + (uint)i, cpu.State.D[i]);
        for (var i = 2; i < 7; i++) Assert.Equal(0xBAD000u + (uint)i * 4, cpu.State.A[i]);
        Assert.Equal(0, bus.HostGatewayCount);
    }
}

public sealed class NativeCacheSummaryTheoryAttribute : Xunit.TheoryAttribute
{
    public NativeCacheSummaryTheoryAttribute()
    {
        var path = Environment.GetEnvironmentVariable("COPPERSFS_HANDLER_PATH");
        if (string.IsNullOrWhiteSpace(path)) Skip = "Set COPPERSFS_HANDLER_PATH to qualify the compiled handler.";
        else if (!System.IO.File.ReadAllText(path + ".map").Contains("CopperStart.Dos.NativeFileSystemPlatform::InvalidateBlockImages"))
            Skip = "This profile retains the original cache layout and does not use a page summary.";
    }
}

public sealed class CacheHandlerTheoryAttribute : Xunit.TheoryAttribute
{
    public CacheHandlerTheoryAttribute()
    {
        var path = Environment.GetEnvironmentVariable("COPPERSFS_HANDLER_PATH");
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            Skip = "Set COPPERSFS_HANDLER_PATH to the freshly built classic SFS handler.";
    }
}
