using System.Buffers.Binary;
using Amiga;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    // DOS\5 directory caches edited record by record must stay readable and
    // writable by Kickstart 3.1's own filesystem, whose ExNext lists a
    // dircache directory from its cache blocks. Kickstart formats the volume,
    // then each arm runs the same mutations (multi-block caches, a record
    // outgrowing its block, rename in place and across directories, deletes
    // that empty a block); Kickstart lists both results and then deletes
    // everything on both.
    [LinkReferenceFact]
    public async System.Threading.Tasks.Task CopperFfsMatchesKickstartDirectoryCache()
    {
        var handler = Environment.GetEnvironmentVariable("COPPERFS_FFS_HANDLER")!;
        var root = (Environment.GetEnvironmentVariable("COPPERFS_LINK_ARTIFACTS") is { Length: > 0 } configured
            ? configured : Path.Combine(Path.GetTempPath(), "copperfs-link-reference")) + "-dircache";
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var formatted = await Run(root, "ffs", "rom-format", handler, new byte[32768 * 512], true, "DirCacheProbe::RomFormat");
        Assert.Equal(0x444F5305u, BinaryPrimitives.ReadUInt32BigEndian(formatted));
        var results = new Dictionary<string, uint[]>();
        foreach (var arm in new[] { "Rom", "Copper" })
        {
            var label = arm.ToLowerInvariant();
            var written = await Run(root, "ffs", label + "-mutate", handler, formatted, true, "DirCacheProbe::" + arm + "Mutate");
            Assert.Equal(0x444F5305u, BinaryPrimitives.ReadUInt32BigEndian(written));
            await Run(root, "ffs", label + "-list", handler, written, true, "DirCacheProbe::RomList");
            results[arm] = ProbeData(root, label + "-mutate").Take(3).Concat(ProbeData(root, label + "-list").Take(17)).ToArray();
            output.WriteLine(arm + ": " + string.Join(",", results[arm]));
        }
        Assert.True(results["Rom"].SequenceEqual(results["Copper"]),
            "Differs from Kickstart:\nrom    " + string.Join(",", results["Rom"]) + "\ncopper " + string.Join(",", results["Copper"]));
        Directory.Delete(root, true);
    }
}

public static class DirCacheProbe
{
    public static int RomFormat() => Run(true, 0);
    public static int RomMutate() => Run(true, 1);
    public static int CopperMutate() => Run(false, 1);
    public static int RomList() => Run(true, 2);

    private static void Put(APTR d, int index, uint value) => APTR.WriteUInt32(d, index * 4, value);

    // "SEQ0:E/EntryNN" padded to a 30-character name: 56-byte cache records,
    // eight to a 512-byte cache block.
    private static CString Entry(APTR path, int n)
    {
        var prefix = APTR.FromPointer(CString.ToUInt32(CString.FromLiteral("SEQ0:E/Entry")));
        for (var i = 0; i < 12; i++) APTR.WriteUInt8(path, i, APTR.ReadUInt8(prefix, i));
        APTR.WriteUInt8(path, 12, (byte)('0' + n / 10)); APTR.WriteUInt8(path, 13, (byte)('0' + n % 10));
        for (var i = 14; i < 37; i++) APTR.WriteUInt8(path, i, (byte)'x');
        APTR.WriteUInt8(path, 37, 0);
        return CString.FromPointer(path.Raw);
    }

    private static void Mutate(APTR d, APTR path, APTR buffer)
    {
        uint made = 0, edited = 0, removed = 0;
        var directory = DOS.CreateDir("SEQ0:E");
        if (directory.HasValue) { DOS.UnLock(directory.Value); made++; }
        for (var n = 0; n < 40; n++)
        {
            var file = DOS.Open(Entry(path, n), DOS.FileMode.NewFile);
            if (!file.HasValue) continue;
            DOS.Write(file.Value, buffer, 3 + n);
            if (DOS.Close(file.Value) != 0) made++;
        }
        // A 79-byte comment no longer fits the record's full block.
        for (var i = 0; i < 79; i++) APTR.WriteUInt8(buffer, i, (byte)('a' + i % 26));
        APTR.WriteUInt8(buffer, 79, 0);
        if (DOS.SetComment(Entry(path, 5), CString.FromPointer(buffer.Raw)) != 0) edited++;
        if (DOS.SetProtection(Entry(path, 6), 5) != 0) edited++;
        if (DOS.Rename(Entry(path, 7), "SEQ0:E/Short") != 0) edited++;
        if (DOS.Rename(Entry(path, 8), "SEQ0:Moved") != 0) edited++;
        // The first block's other records go, so that block leaves the chain.
        for (var n = 0; n < 10; n++)
            if ((n < 5 || n == 9) && DOS.DeleteFile(Entry(path, n)) != 0) removed++;
        var sub = DOS.CreateDir("SEQ0:E/Sub");
        if (sub.HasValue) { DOS.UnLock(sub.Value); made++; }
        var inner = DOS.Open("SEQ0:E/Sub/Inner", DOS.FileMode.NewFile);
        if (inner.HasValue && DOS.Close(inner.Value) != 0) made++;
        Put(d, 0, made); Put(d, 1, edited); Put(d, 2, removed); Put(d, 3, unchecked((uint)DOS.IoErr()));
    }

    // Count and digest a directory's ExNext listing, independent of order.
    private static void List(APTR d, int index, CString name, APTR fib)
    {
        uint count = 0, sum = 0, mix = 0;
        var directory = DOS.Lock(name, DOS.LockMode.Shared);
        if (directory.HasValue && DOS.Examine(directory.Value, fib) != 0)
        {
            while (DOS.ExNext(directory.Value, fib) != 0)
            {
                uint digest = 0;
                for (var i = 0; i < 108 && APTR.ReadUInt8(fib, 8 + i) != 0; i++) digest = digest * 31 + APTR.ReadUInt8(fib, 8 + i);
                for (var i = 0; i < 80 && APTR.ReadUInt8(fib, 144 + i) != 0; i++) digest = digest * 17 + APTR.ReadUInt8(fib, 144 + i);
                digest += APTR.ReadUInt32(fib, 4) * 7 + APTR.ReadUInt32(fib, 116) * 13 + APTR.ReadUInt32(fib, 124) * 11 +
                    APTR.ReadUInt32(fib, 128) * 3;
                count++; sum += digest; mix ^= digest * 2654435761u;
            }
        }
        Put(d, index, count); Put(d, index + 1, sum); Put(d, index + 2, mix);
        if (directory.HasValue) DOS.UnLock(directory.Value);
    }

    // Kickstart edits the caches it lists: delete everything it listed.
    private static void Clean(APTR d, APTR path, APTR fib)
    {
        uint deleted = 0;
        if (DOS.DeleteFile("SEQ0:E/Sub/Inner") != 0) deleted++;
        if (DOS.DeleteFile("SEQ0:E/Sub") != 0) deleted++;
        if (DOS.DeleteFile("SEQ0:Moved") != 0) deleted++;
        if (DOS.DeleteFile("SEQ0:E/Short") != 0) deleted++;
        for (var n = 0; n < 40; n++) if (DOS.DeleteFile(Entry(path, n)) != 0) deleted++;
        if (DOS.DeleteFile("SEQ0:E") != 0) deleted++;
        Put(d, 12, deleted); Put(d, 13, unchecked((uint)DOS.IoErr()));
        List(d, 14, CString.FromLiteral("SEQ0:"), fib);
    }

    private static int Run(bool rom, int phase)
    {
        var library = Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) { CorrectnessProbe.Report(250); return 10; }
        DOS.DOSLibraryBase = library.Value;
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)), 184, uint.MaxValue);
        var control = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var path = APTR.FromPointer(Exec.AllocMem(64, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var buffer = APTR.FromPointer(Exec.AllocMem(128, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var fib = APTR.FromPointer(Exec.AllocMem(260, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        if (control.IsNull || path.IsNull || buffer.IsNull || fib.IsNull) { CorrectnessProbe.Report(250); return 11; }
        CorrectnessProbe.Report(200);
        for (var shift = 24; shift >= 0; shift -= 8) CorrectnessProbe.Report((int)(control.Raw >> shift));
        var setup = CorrectnessProbe.Mount(control, 1, romFileSystem: rom);
        if (setup != 0) return CorrectnessProbe.Failed(control, (uint)setup);
        APTR.WriteUInt32(APTR.FromPointer(control.Raw + 128), DosLayout.DosEnvec.DosType, 0x444F5305u);
        var d = APTR.FromPointer(control.Raw + 512);
        if (phase == 0)
        {
            if (DOS.Inhibit("SEQ0:", 1) == 0) return CorrectnessProbe.Failed(control, 4);
            if (DOS.Format("SEQ0:", "Cached", 0x444F5305u) == 0) return CorrectnessProbe.Failed(control, 3);
            if (DOS.Inhibit("SEQ0:", 0) == 0) return CorrectnessProbe.Failed(control, 5);
        }
        else if (phase == 1) Mutate(d, path, buffer);
        else
        {
            List(d, 0, CString.FromLiteral("SEQ0:E"), fib);
            List(d, 3, CString.FromLiteral("SEQ0:"), fib);
            List(d, 6, CString.FromLiteral("SEQ0:E/Sub"), fib);
            Clean(d, path, fib);
        }
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (DOS.Inhibit("SEQ0:", 1) != 0) { CorrectnessProbe.Report(210); return 0; }
            DOS.Delay(10);
        }
        return CorrectnessProbe.Failed(control, 90);
    }
}
