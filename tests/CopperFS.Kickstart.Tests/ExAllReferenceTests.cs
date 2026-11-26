using Amiga;
using CopperStart.Floppy.Native.Tests;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    // Kickstart 3.1's own filesystem is the reference for ACTION_EXAMINE_ALL,
    // sent directly (dos.library would otherwise emulate it with ExNext).
    // Each scan records its packet count, total entries, final Res1/Res2 and
    // a digest of every returned record.
    [LinkReferenceFact]
    public async System.Threading.Tasks.Task CopperFfsMatchesKickstartExAll()
    {
        var handler = Environment.GetEnvironmentVariable("COPPERFS_FFS_HANDLER")!;
        var root = (Environment.GetEnvironmentVariable("COPPERFS_LINK_ARTIFACTS") is { Length: > 0 } configured
            ? configured : Path.Combine(Path.GetTempPath(), "copperfs-link-reference")) + "-exall";
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var initial = DosDiskImage.Create(true, includeLargeFile: false, blockCount: 32768,
            firstFileBlock: 2, payload: [1, 2, 3], volumeName: "ExAll").Bytes;
        var created = await Run(root, "ffs", "rom-create", handler, initial, true, "ExAllProbe::RomCreate");
        var results = new Dictionary<string, uint[]>();
        foreach (var arm in new[] { "Rom", "Copper" })
        {
            var label = arm.ToLowerInvariant() + "-exall";
            await Run(root, "ffs", label, handler, created, false, "ExAllProbe::" + arm);
            results[arm] = ProbeData(root, label);
            output.WriteLine(arm + ": " + string.Join(",", results[arm].Take(24)));
        }
        Assert.True(results["Rom"].SequenceEqual(results["Copper"]),
            "Differs from Kickstart:\nrom    " + string.Join(",", results["Rom"]) + "\ncopper " + string.Join(",", results["Copper"]));
        Directory.Delete(root, true);
    }
}

public static class ExAllProbe
{
    public static int RomCreate() => Run(true, true);
    public static int Rom() => Run(true, false);
    public static int Copper() => Run(false, false);

    // Digest one record of the given type: name, then the fields that type includes.
    private static uint Digest(APTR record, int type)
    {
        uint sum = 0;
        var name = APTR.FromPointer(APTR.ReadUInt32(record, DosLayout.ExAllData.Name));
        for (var i = 0; i < 31 && APTR.ReadUInt8(name, i) != 0; i++) sum = sum * 31 + APTR.ReadUInt8(name, i);
        if (type >= 2) sum += APTR.ReadUInt32(record, DosLayout.ExAllData.Type) * 7;
        if (type >= 3) sum += APTR.ReadUInt32(record, DosLayout.ExAllData.FileSize) * 11;
        if (type >= 4) sum += APTR.ReadUInt32(record, DosLayout.ExAllData.Protection) * 13;
        if (type >= 6)
        {
            var comment = APTR.FromPointer(APTR.ReadUInt32(record, DosLayout.ExAllData.Comment));
            for (var i = 0; i < 80 && APTR.ReadUInt8(comment, i) != 0; i++) sum = sum * 17 + APTR.ReadUInt8(comment, i);
        }
        return sum;
    }

    // One complete scan of SEQ0:X. Records calls, entries, digest, last Res1 and Res2.
    private static void Scan(APTR d, int index, APTR port, BPTR directory, APTR buffer, int size, int type, APTR control, APTR pattern)
    {
        for (var i = 0; i < 16; i += 4) APTR.WriteUInt32(control, i, 0);
        APTR.WriteUInt32(control, DosLayout.ExAllControl.MatchString, pattern.Raw);
        uint calls = 0, entries = 0, digest = 0; int r;
        do
        {
            r = DOS.DoPkt(port, (int)DosPacketAction.ExamineAll, unchecked((int)directory.Raw), unchecked((int)buffer.Raw), size, type, unchecked((int)control.Raw));
            var error = (uint)DOS.IoErr();
            calls++;
            var count = APTR.ReadUInt32(control, DosLayout.ExAllControl.Entries);
            entries += count;
            var record = buffer; uint walked = 0;
            while (count != 0 && record.IsNotNull && walked < count)
            {
                digest += Digest(record, type); walked++;
                record = APTR.FromPointer(APTR.ReadUInt32(record, DosLayout.ExAllData.Next));
            }
            if (walked != count || (count != 0 && record.IsNotNull)) digest ^= 0x80000000;
            APTR.WriteUInt32(d, (index + 4) * 4, error);
        }
        while (r != 0 && calls < 100);
        APTR.WriteUInt32(d, index * 4, calls);
        APTR.WriteUInt32(d, (index + 1) * 4, entries);
        APTR.WriteUInt32(d, (index + 2) * 4, digest);
        APTR.WriteUInt32(d, (index + 3) * 4, unchecked((uint)r));
    }

    private static int Run(bool rom, bool create)
    {
        var library = Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) { CorrectnessProbe.Report(250); return 10; }
        DOS.DOSLibraryBase = library.Value;
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)), 184, uint.MaxValue);
        var control = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var buffer = APTR.FromPointer(Exec.AllocMem(4096, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var exall = APTR.FromPointer(Exec.AllocMem(64, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var path = APTR.FromPointer(Exec.AllocMem(64, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        if (control.IsNull || buffer.IsNull || exall.IsNull || path.IsNull) { CorrectnessProbe.Report(250); return 11; }
        CorrectnessProbe.Report(200);
        for (var shift = 24; shift >= 0; shift -= 8) CorrectnessProbe.Report((int)(control.Raw >> shift));
        var setup = CorrectnessProbe.Mount(control, 1, romFileSystem: rom);
        if (setup != 0) return CorrectnessProbe.Failed(control, (uint)setup);
        var d = APTR.FromPointer(control.Raw + 512);
        if (create)
        {
            var made = DOS.CreateDir("SEQ0:X"); if (made.HasValue) DOS.UnLock(made.Value);
            var sub = DOS.CreateDir("SEQ0:X/Sub"); if (sub.HasValue) DOS.UnLock(sub.Value);
            var prefix = APTR.FromPointer(CString.ToUInt32(CString.FromLiteral("SEQ0:X/F")));
            for (var i = 0; i < 8; i++) APTR.WriteUInt8(path, i, APTR.ReadUInt8(prefix, i));
            for (var n = 0; n < 12; n++)
            {
                APTR.WriteUInt8(path, 8, (byte)('A' + n)); APTR.WriteUInt8(path, 9, 0);
                var file = DOS.Open(CString.FromPointer(path.Raw), DOS.FileMode.NewFile);
                if (!file.HasValue) continue;
                DOS.Write(file.Value, buffer, n * 37);
                DOS.Close(file.Value);
                if ((n & 1) != 0) DOS.SetComment(CString.FromPointer(path.Raw), "a comment");
                DOS.SetProtection(CString.FromPointer(path.Raw), n);
            }
        }
        else
        {
            var port = DOS.DeviceProc("SEQ0:");
            var directory = DOS.Lock("SEQ0:X", DOS.LockMode.Shared);
            if (!directory.HasValue) return CorrectnessProbe.Failed(control, 20);
            Scan(d, 0, port, directory.Value, buffer, 4000, 6, exall, APTR.Null);   // ED_COMMENT, one call
            Scan(d, 5, port, directory.Value, buffer, 70, 2, exall, APTR.Null);     // ED_TYPE, several calls
            Scan(d, 10, port, directory.Value, buffer, 4000, 8, exall, APTR.Null);  // ED_OWNER
            // ED_SIZE with a parsed "F#?[ACEG]" pattern.
            var pattern = APTR.FromPointer(path.Raw + 32);
            DOS.ParsePatternNoCase("F#?[ACEG]", pattern, 30);
            Scan(d, 15, port, directory.Value, buffer, 4000, 3, exall, pattern);
            // A buffer too small for any record.
            Scan(d, 20, port, directory.Value, buffer, 8, 6, exall, APTR.Null);
            DOS.UnLock(directory.Value);
        }
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (DOS.Inhibit("SEQ0:", 1) != 0) { CorrectnessProbe.Report(210); return 0; }
            DOS.Delay(10);
        }
        return CorrectnessProbe.Failed(control, 90);
    }
}
