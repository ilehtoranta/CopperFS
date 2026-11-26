using System.Buffers.Binary;
using Amiga;
using CopperStart.Floppy.Native.Tests;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    // Kickstart 3.1's own filesystem is the reference for the FIB fields
    // Examine fills: fib_NumBlocks for OFS and FFS files of several sizes,
    // and the owner UID/GID stored in the header.
    [LinkReferenceFact]
    public async System.Threading.Tasks.Task CopperFfsMatchesKickstartFibFields()
    {
        var handler = Environment.GetEnvironmentVariable("COPPERFS_FFS_HANDLER")!;
        var root = (Environment.GetEnvironmentVariable("COPPERFS_LINK_ARTIFACTS") is { Length: > 0 } configured
            ? configured : Path.Combine(Path.GetTempPath(), "copperfs-link-reference")) + "-fib";
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var mismatches = new List<string>();
        foreach (var ffs in new[] { false, true })
        {
            var disk = DosDiskImage.Create(ffs, includeLargeFile: false, blockCount: 32768,
                firstFileBlock: 2, payload: [1, 2, 3], volumeName: "Fib");
            // Owner UID 0x1234 and GID 0xABCD on the existing payload's header.
            BinaryPrimitives.WriteUInt32BigEndian(disk.Bytes.AsSpan((int)disk.PayloadHeader * 512 + 316), 0x1234ABCD);
            disk.Checksum(disk.PayloadHeader);
            var results = new Dictionary<string, uint[]>();
            foreach (var arm in new[] { "Rom", "Copper" })
            {
                var label = arm.ToLowerInvariant() + (ffs ? "-ffs" : "-ofs");
                await Run(root, ffs ? "ffs" : "ofs", label, handler, disk.Bytes, true, "FibProbe::" + arm + (ffs ? "Ffs" : "Ofs"));
                results[arm] = ProbeData(root, label);
                output.WriteLine(label + ": " + string.Join(",", results[arm].Take(16)));
            }
            if (!results["Rom"].SequenceEqual(results["Copper"])) mismatches.Add(ffs ? "FFS" : "OFS");
        }
        Assert.True(mismatches.Count == 0, "Differs from Kickstart: " + string.Join(", ", mismatches));
        Directory.Delete(root, true);
    }
}

public static class FibProbe
{
    public static int RomOfs() => Run(true, 0);
    public static int CopperOfs() => Run(false, 0);
    public static int RomFfs() => Run(true, 1);
    public static int CopperFfs() => Run(false, 1);

    private static int Run(bool rom, uint kind)
    {
        var library = Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) { CorrectnessProbe.Report(250); return 10; }
        DOS.DOSLibraryBase = library.Value;
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)), 184, uint.MaxValue);
        var control = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var buffer = APTR.FromPointer(Exec.AllocMem(40000, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var fib = APTR.FromPointer(Exec.AllocMem(260, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var path = APTR.FromPointer(Exec.AllocMem(16, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        if (control.IsNull || buffer.IsNull || fib.IsNull || path.IsNull) { CorrectnessProbe.Report(250); return 11; }
        CorrectnessProbe.Report(200);
        for (var shift = 24; shift >= 0; shift -= 8) CorrectnessProbe.Report((int)(control.Raw >> shift));
        var setup = CorrectnessProbe.Mount(control, kind, romFileSystem: rom);
        if (setup != 0) return CorrectnessProbe.Failed(control, (uint)setup);
        var d = APTR.FromPointer(control.Raw + 512);
        // "SEQ0:S0".."SEQ0:S5" of sizes 0, 1, 488, 489, 512, 36000 (OFS needs
        // an extension block above 72 * 488 bytes).
        var prefix = APTR.FromPointer(CString.ToUInt32(CString.FromLiteral("SEQ0:S")));
        for (var i = 0; i < 6; i++) APTR.WriteUInt8(path, i, APTR.ReadUInt8(prefix, i));
        for (var n = 0; n < 6; n++)
        {
            var size = n == 0 ? 0 : n == 1 ? 1 : n == 2 ? 488 : n == 3 ? 489 : n == 4 ? 512 : 36000;
            APTR.WriteUInt8(path, 6, (byte)('0' + n)); APTR.WriteUInt8(path, 7, 0);
            var file = DOS.Open(CString.FromPointer(path.Raw), DOS.FileMode.NewFile);
            if (!file.HasValue) continue;
            if (size != 0) DOS.Write(file.Value, buffer, size);
            DOS.Close(file.Value);
            var locked = DOS.Lock(CString.FromPointer(path.Raw), DOS.LockMode.Shared);
            if (!locked.HasValue) continue;
            if (DOS.Examine(locked.Value, fib) != 0) APTR.WriteUInt32(d, n * 4, APTR.ReadUInt32(fib, 128)); // fib_NumBlocks
            DOS.UnLock(locked.Value);
        }
        var payload = DOS.Lock("SEQ0:Data/Payload", DOS.LockMode.Shared);
        if (payload.HasValue)
        {
            if (DOS.Examine(payload.Value, fib) != 0)
            {
                APTR.WriteUInt32(d, 24, APTR.ReadUInt16(fib, 224)); // fib_OwnerUID
                APTR.WriteUInt32(d, 28, APTR.ReadUInt16(fib, 226)); // fib_OwnerGID
                APTR.WriteUInt32(d, 32, APTR.ReadUInt32(fib, 128));
            }
            DOS.UnLock(payload.Value);
        }
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (DOS.Inhibit("SEQ0:", 1) != 0) { CorrectnessProbe.Report(210); return 0; }
            DOS.Delay(10);
        }
        return CorrectnessProbe.Failed(control, 90);
    }
}
