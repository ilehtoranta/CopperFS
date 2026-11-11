using Amiga;
using CopperStart.Floppy.Native.Tests;
using System.Buffers.Binary;
using System.Text.Json;

namespace CopperFS.Kickstart.Tests;

public static class FfsOverwriteProbe
{
    public static int Overwrite() => Run(true);
    public static int ColdRead() => Run(false);
    public static int WriteOfsDisk() => Run(true, 0);
    public static int ReadOfsDisk() => Run(false, 0);
    public static int WriteUnknownOfs() => Run(true, 0, true);
    public static int ReadUnknownOfs() => Run(false, 0, true);
    public static int WriteUnknown() => Run(true, 1, true);
    public static int ReadUnknown() => Run(false, 1, true);
    public static int WriteFfsDd() => Run(true, 11);
    public static int ReadFfsDd() => Run(false, 11);
    public static int WriteOfsDd() => Run(true, 10);
    public static int ReadOfsDd() => Run(false, 10);
    public static int WriteFfsHd() => Run(true, 21);
    public static int ReadFfsHd() => Run(false, 21);
    public static int WriteOfsHd() => Run(true, 20);
    public static int ReadOfsHd() => Run(false, 20);

    private static int Run(bool write, uint kind = 1, bool unknown = false)
    {
        var lib = global::Amiga.Exec.OpenLibrary("dos.library", 37);
        if (!lib.HasValue) { CorrectnessProbe.Report(250); return 1; }
        DOS.DOSLibraryBase = lib.Value;
        var control = APTR.FromPointer(global::Amiga.Exec.AllocMem(1024, global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear));
        var buffer = APTR.FromPointer(global::Amiga.Exec.AllocMem(65536, global::Amiga.Exec.MemoryFlags.Public));
        if (control.IsNull || buffer.IsNull || CorrectnessProbe.Mount(control, kind, kind >= 10, unknown) != 0) { CorrectnessProbe.Report(250); return 2; }
        var file = DOS.Open("SEQ0:Data/Payload", write ? DOS.FileMode.ReadWrite : DOS.FileMode.OldFile);
        if (!file.HasValue || DOS.Seek(file.Value, 0, 1) < 0) { CorrectnessProbe.Report(250); return 3; }
        var bytes = DOS.Seek(file.Value, 0, -1);
        if (bytes <= 0 || bytes > 65536) { CorrectnessProbe.Report(250); return 4; }
        if (write)
            for (var i = 0; i < bytes; i++) APTR.WriteUInt8(buffer, i, (byte)(i * 13 + (i >> 9) + 37));
        CorrectnessProbe.Report(220);
        var result = write ? DOS.Write(file.Value, buffer, bytes) : DOS.Read(file.Value, buffer, bytes);
        CorrectnessProbe.Report(221);
        if (result != bytes) { CorrectnessProbe.Report(250); return 5; }
        if (!write)
            for (var i = 0; i < bytes; i++)
                if (APTR.ReadUInt8(buffer, i) != (byte)(i * 13 + (i >> 9) + 37)) { CorrectnessProbe.Report(250); return 6; }
        if (DOS.Close(file.Value) == 0 || DOS.Inhibit("SEQ0:", 1) == 0) { CorrectnessProbe.Report(250); return 7; }
        CorrectnessProbe.Report(210); return 0;
    }
}

public sealed class OverwriteBenchmarkTheoryAttribute : TheoryAttribute
{
    public OverwriteBenchmarkTheoryAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("COPPERSHARP_KICKSTART31_ROM")) ||
            !File.Exists(Environment.GetEnvironmentVariable("COPPERFS_FFS_HANDLER")) ||
            !File.Exists(Environment.GetEnvironmentVariable("COPPERFS_FFS_BASELINE")) ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COPPERFS_OVERWRITE_RESULTS")))
            Skip = "Requires the ROM, both CopperFFS handlers and an output directory.";
    }
}

public sealed partial class OriginalHandlerCorrectnessTests
{
    [OverwriteBenchmarkTheory]
    [InlineData(8, 1)] [InlineData(32, 4)] [InlineData(64, 8)] [InlineData(32, 4, 5)]
    public async System.Threading.Tasks.Task OfsHardDiskPlacement(int blocks, int run, int stride = 257)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("COPPERFS_OVERWRITE_RESULTS")!, $"ofs-{blocks}-{run}-{stride}");
        var rows = new List<object>();
        foreach (var arm in new[] { "BASELINE", "HANDLER" })
        foreach (var packed in new[] { false, true })
        {
            var handler = Environment.GetEnvironmentVariable("COPPERFS_FFS_" + arm)!;
            var disk = DosDiskImage.Create(false, blockCount: 32768, firstFileBlock: 2, includeLargeFile: false,
                payload: new byte[blocks * 488]);
            ScatterPayload(disk, blocks, packed ? blocks : run, true, stride);
            var image = disk.Bytes;
            foreach (var write in new[] { true, false })
            {
                long start = -1; int reads = 0, writes = 0, flushes = 0;
                var phase = write ? "WriteOfsDisk" : "ReadOfsDisk";
                image = await Run(root, "ofs", $"{arm}-{packed}-{phase}", handler, image, write, "FfsOverwriteProbe::" + phase,
                    (marker, cycle, r, w, f) =>
                    {
                        if (marker == 220) { start = cycle; reads = r; writes = w; flushes = f; }
                        if (marker == 221) { Assert.True(start >= 0); rows.Add(new { arm, packed, phase, cycles = cycle-start, reads = r-reads, writes = w-writes, flushes = f-flushes }); }
                    });
                uint key = DiskWord(image, disk.PayloadHeader, 16);
                if (Environment.GetEnvironmentVariable("COPPERFS_OFS_EXPERIMENT") != "1")
                    Assert.Equal(arm == "HANDLER" && !packed && blocks == 64 && stride == 257, key != DiskWord(disk.Bytes, disk.PayloadHeader, 16));
                for (var i = 0; i < blocks; i++)
                {
                    Assert.Equal(DiskWord(image, disk.PayloadHeader, 308 - 4 * i), key);
                    Assert.Equal(8u, DiskWord(image, key, 0));
                    Assert.Equal(disk.PayloadHeader, DiskWord(image, key, 4));
                    Assert.Equal((uint)i + 1, DiskWord(image, key, 8));
                    Assert.Equal(488u, DiskWord(image, key, 12));
                    uint sum = 0;
                    for (var at = 0; at < 512; at += 4) sum = unchecked(sum + DiskWord(image, key, at));
                    Assert.Equal(0u, sum);
                    key = DiskWord(image, key, 16);
                }
                Assert.Equal(0u, key);
            }
        }
        Assert.Equal(8, rows.Count);
        File.WriteAllText(Path.Combine(root, "placement.json"), JsonSerializer.Serialize(rows));
    }
    private static uint DiskWord(byte[] image, uint key, int at) =>
        BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan((int)key * 512 + at));

    private static void ScatterPayload(DosDiskImage disk, int blocks, int run, bool ofs = false, int stride = 257)
    {
        void Bit(uint key, bool free)
        {
            var bit = key - 2;
            var map = disk.BitmapBlock + bit / 4064;
            var at = 4 + (int)(bit % 4064 / 32) * 4;
            var mask = 1u << (int)(bit % 32);
            var word = DiskWord(disk.Bytes, map, at);
            Assert.Equal(!free, (word & mask) != 0);
            disk.Write(map, at, free ? word | mask : word & ~mask);
        }
        uint Key(int i) => (uint)(600 + i / run * stride + i % run);
        for (var i = 0; i < blocks; i++)
        {
            var at = 308 - i * 4;
            var key = Key(i);
            var old = DiskWord(disk.Bytes, disk.PayloadHeader, at);
            Bit(old, true); Bit(key, false);
            Array.Copy(disk.Bytes, (int)old * 512, disk.Bytes, (int)key * 512, 512);
            if (ofs) { disk.Write(key, 16, i + 1 == blocks ? 0 : Key(i + 1)); disk.Checksum(key); }
            disk.Write(disk.PayloadHeader, at, key);
            if (i == 0) disk.Write(disk.PayloadHeader, 16, key);
        }
        disk.Checksum(disk.PayloadHeader);
        for (var i = 0u; i < disk.BitmapBlocks; i++) disk.Checksum(disk.BitmapBlock + i, 0);
    }

    [OverwriteBenchmarkTheory]
    [InlineData(32, false)] [InlineData(64, true)]
    public async System.Threading.Tasks.Task UnknownFixedDeviceRelocatesOverwrite(int blocks, bool ofs)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("COPPERFS_OVERWRITE_RESULTS")!, ofs ? "unknown-ofs" : "unknown");
        var handler = Environment.GetEnvironmentVariable("COPPERFS_FFS_HANDLER")!;
        var disk = DosDiskImage.Create(!ofs, blockCount: 32768, firstFileBlock: 2, includeLargeFile: false,
            payload: new byte[blocks * (ofs ? 488 : 512)]);
        ScatterPayload(disk, blocks, ofs ? 8 : 4, ofs);
        var suffix = ofs ? "UnknownOfs" : "Unknown";
        var written = await Run(root, "disk", "write", handler, disk.Bytes, true, "FfsOverwriteProbe::Write" + suffix);
        var first = DiskWord(written, disk.PayloadHeader, 308);
        for (var i = 0; i < blocks; i++)
        {
            var key = DiskWord(written, disk.PayloadHeader, 308 - i * 4);
            Assert.NotEqual(DiskWord(disk.Bytes, disk.PayloadHeader, 308 - i * 4), key);
            Assert.Equal(first + (uint)i, key);
        }
        await Run(root, "disk", "read", handler, written, false, "FfsOverwriteProbe::Read" + suffix);
    }

    [OverwriteBenchmarkTheory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async System.Threading.Tasks.Task TrackdiskOverwritePreservesPointersAcrossFreshBoot(bool ofs, bool hd)
    {
        var name = (ofs ? "Ofs" : "Ffs") + (hd ? "Hd" : "Dd");
        var root = Path.Combine(Environment.GetEnvironmentVariable("COPPERFS_OVERWRITE_RESULTS")!, name);
        var handler = Environment.GetEnvironmentVariable("COPPERFS_FFS_HANDLER")!;
        var blocks = ofs ? 64 : 16;
        var disk = DosDiskImage.Create(!ofs, highDensity: hd, firstFileBlock: 2, includeLargeFile: false,
            payload: new byte[blocks * (ofs ? 488 : 512)]);
        ScatterPayload(disk, blocks, ofs ? 8 : 2, ofs, ofs ? 145 : 32);
        var written = await Run(root, "floppy", "written", handler, disk.Bytes, true, "FfsOverwriteProbe::Write" + name, floppy: true);
        for (var i = 0; i < blocks; i++) Assert.Equal(DiskWord(disk.Bytes, disk.PayloadHeader, 308 - i * 4),
            DiskWord(written, disk.PayloadHeader, 308 - i * 4));
        await Run(root, "floppy", "read", handler, written, false, "FfsOverwriteProbe::Read" + name, floppy: true);
    }

    [OverwriteBenchmarkTheory]
    [InlineData(8, 1)] [InlineData(16, 2)] [InlineData(32, 4)] [InlineData(32, 8)]
    [InlineData(64, 8)] [InlineData(64, 64)] [InlineData(8, 2)] [InlineData(4, 2)]
    [InlineData(32, 4, 5)]
    public async System.Threading.Tasks.Task FragmentedOverwriteThenFreshBootSequentialRead(int blocks, int run, int stride = 257)
    {
        var directory = Path.Combine(Environment.GetEnvironmentVariable("COPPERFS_OVERWRITE_RESULTS")!, $"{blocks}-{run}-{stride}");
        var disk = DosDiskImage.Create(true, blockCount: 32768, firstFileBlock: 2, includeLargeFile: false,
            payload: new byte[blocks * 512]);
        ScatterPayload(disk, blocks, run, stride: stride);

        var measurements = new List<object>();
        var arms = new List<string> { "BASELINE", "HANDLER" };
        if (File.Exists(Environment.GetEnvironmentVariable("COPPERFS_FFS_TRIAL")) && (blocks + run - 1) / run < 5 && run < blocks)
            arms.Add("TRIAL");
        foreach (var arm in arms)
        {
            var image = disk.Bytes;
            var handler = Environment.GetEnvironmentVariable("COPPERFS_FFS_" + arm)!;
            foreach (var write in new[] { true, false })
            {
                long start = -1; int reads = 0, writes = 0, flushes = 0; bool measured = false;
                var phase = write ? "Overwrite" : "ColdRead";
                image = await Run(directory, "ffs", arm + "-" + phase, handler, image, write,
                    "FfsOverwriteProbe::" + phase, (marker, cycles, r, w, f) =>
                    {
                        if (marker == 220) { start = cycles; reads = r; writes = w; flushes = f; }
                        if (marker == 221)
                        {
                            Assert.True(start >= 0);
                            measurements.Add(new { arm, phase, BusCycles = cycles - start, ReadCommands = r - reads,
                                WriteCommands = w - writes, Flushes = f - flushes });
                            measured = true;
                        }
                    });
                Assert.True(measured);
            }
            var keys = Enumerable.Range(0, blocks).Select(i => DiskWord(image, disk.PayloadHeader, 308 - i * 4)).ToArray();
            var runs = 1 + keys.Skip(1).Where((k, i) => k != keys[i] + 1).Count();
            measurements.Add(new { arm, runs });
            var originalRuns = (blocks + run - 1) / run;
            var profitable = blocks >= 8 && originalRuns >= (blocks >= 32 ? 4 : 5);
            Assert.Equal(arm == "TRIAL" || arm == "HANDLER" && profitable ? 1 : originalRuns, runs);
        }
        File.WriteAllText(Path.Combine(directory, "measurements.json"), JsonSerializer.Serialize(measurements));
    }
}
