using Amiga;
using CopperStart.Floppy.Native.Tests;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    // Kickstart 3.1's own filesystem is the reference for lock and open-mode
    // conflicts: MODE_READWRITE sharing, DupLock and ParentDir of exclusive
    // locks, renaming locked objects and the lock CreateDir returns.
    [LinkReferenceFact]
    public async System.Threading.Tasks.Task CopperFfsMatchesKickstartLockRules()
    {
        var handler = Environment.GetEnvironmentVariable("COPPERFS_FFS_HANDLER")!;
        var root = (Environment.GetEnvironmentVariable("COPPERFS_LINK_ARTIFACTS") is { Length: > 0 } configured
            ? configured : Path.Combine(Path.GetTempPath(), "copperfs-link-reference")) + "-locks";
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var initial = DosDiskImage.Create(true, includeLargeFile: false, blockCount: 32768,
            firstFileBlock: 2, payload: [1, 2, 3], volumeName: "Locks").Bytes;
        var results = new Dictionary<string, uint[]>();
        var images = new Dictionary<string, byte[]>();
        foreach (var arm in new[] { "Rom", "Copper" })
        {
            var label = arm.ToLowerInvariant() + "-locks";
            images[arm] = await Run(root, "ffs", label, handler, initial, true, "LockProbe::" + arm);
            results[arm] = ProbeData(root, label);
        }
        File.WriteAllText(Path.Combine(root, "lock-reference.json"), System.Text.Json.JsonSerializer.Serialize(results, Json));
        Assert.True(results["Rom"].SequenceEqual(results["Copper"]),
            "Differs from Kickstart:\nrom    " + string.Join(",", results["Rom"]) + "\ncopper " + string.Join(",", results["Copper"]));
        Assert.Equal(LinkLayout(images["Rom"]), LinkLayout(images["Copper"]));
        Directory.Delete(root, true); // keep only the comparison when it passes
    }
}

public static class LockProbe
{
    public static int Rom() => Run(true);
    public static int Copper() => Run(false);

    private static void Put(APTR d, int index, uint value) => APTR.WriteUInt32(d, index * 4, value);
    private static void Error(APTR d, int index) => Put(d, index, unchecked((uint)DOS.IoErr()));

    // 1 and the BPTR on success; 0 and IoErr at index + 1 on failure.
    private static BPTR Opened(APTR d, int index, BPTR? value)
    {
        Put(d, index, value.HasValue ? 1u : 0u);
        if (!value.HasValue) { Error(d, index + 1); return BPTR.Null; }
        return value.Value;
    }

    // The packet itself, so the result reflects only the filesystem.
    private static BPTR Packet(APTR d, int index, DosPacketAction action, BPTR argument)
    {
        var r = DOS.DoPkt(DOS.DeviceProc("SEQ0:"), (int)action, unchecked((int)argument.Raw), 0, 0, 0, 0);
        Put(d, index, r != 0 ? 1u : 0u);
        if (r == 0) { Error(d, index + 1); return BPTR.Null; }
        return BPTR.FromRaw(unchecked((uint)r));
    }

    private static void Renamed(APTR d, int index, CString from, CString to)
    {
        var r = DOS.Rename(from, to);
        Put(d, index, unchecked((uint)r));
        if (r == 0) Error(d, index + 1); else DOS.Rename(to, from);
    }

    private static int Run(bool rom)
    {
        var library = Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) { CorrectnessProbe.Report(250); return 10; }
        DOS.DOSLibraryBase = library.Value;
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)), 184, uint.MaxValue);
        var control = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        if (control.IsNull) { CorrectnessProbe.Report(250); return 11; }
        CorrectnessProbe.Report(200);
        for (var shift = 24; shift >= 0; shift -= 8) CorrectnessProbe.Report((int)(control.Raw >> shift));
        var setup = CorrectnessProbe.Mount(control, 1, romFileSystem: rom);
        if (setup != 0) return CorrectnessProbe.Failed(control, (uint)setup);
        var d = APTR.FromPointer(control.Raw + 512);

        // MODE_READWRITE against a second MODE_READWRITE, MODE_OLDFILE and a shared lock.
        var rw = Opened(d, 0, DOS.Open("SEQ0:Data/Payload", DOS.FileMode.ReadWrite));
        var second = Opened(d, 2, DOS.Open("SEQ0:Data/Payload", DOS.FileMode.ReadWrite));
        var old = Opened(d, 4, DOS.Open("SEQ0:Data/Payload", DOS.FileMode.OldFile));
        var shared = Opened(d, 6, DOS.Lock("SEQ0:Data/Payload", DOS.LockMode.Shared));
        if (shared.IsNotNull) DOS.UnLock(shared);
        if (old.IsNotNull) DOS.Close(old);
        if (second.IsNotNull) DOS.Close(second);
        if (rw.IsNotNull) DOS.Close(rw);

        // DupLock of an exclusive lock, and a shared lock beside it.
        var exclusive = Opened(d, 8, DOS.Lock("SEQ0:Data/Payload", DOS.LockMode.Exclusive));
        if (exclusive.IsNotNull)
        {
            Put(d, 32, APTR.ReadUInt32(exclusive.Address, 8)); // fl_Access
            var dup = Packet(d, 10, DosPacketAction.CopyDirectory, exclusive);
            if (dup.IsNotNull) DOS.UnLock(dup);
            var beside = Opened(d, 12, DOS.Lock("SEQ0:Data/Payload", DOS.LockMode.Shared));
            if (beside.IsNotNull) DOS.UnLock(beside);
            DOS.UnLock(exclusive);
        }

        // ParentDir of a child whose directory is exclusively locked.
        var directory = Opened(d, 14, DOS.Lock("SEQ0:Data", DOS.LockMode.Exclusive));
        var child = Opened(d, 16, DOS.Lock("SEQ0:Data/Payload", DOS.LockMode.Shared));
        if (child.IsNotNull)
        {
            Put(d, 33, APTR.ReadUInt32(child.Address, 8));
            var copy = Packet(d, 35, DosPacketAction.CopyDirectory, child); // a shared lock's copy, for contrast
            if (copy.IsNotNull) DOS.UnLock(copy);
            var parent = Packet(d, 18, DosPacketAction.Parent, child);
            if (parent.IsNotNull) DOS.UnLock(parent);
            DOS.UnLock(child);
        }
        if (directory.IsNotNull) DOS.UnLock(directory);

        // Rename under a shared directory lock, an open file and an exclusive lock.
        var drawer = DOS.Lock("SEQ0:Data", DOS.LockMode.Shared);
        Renamed(d, 20, "SEQ0:Data", "SEQ0:Data2");
        if (drawer.HasValue) DOS.UnLock(drawer.Value);
        var open = DOS.Open("SEQ0:Data/Payload", DOS.FileMode.OldFile);
        Renamed(d, 22, "SEQ0:Data/Payload", "SEQ0:Data/Moved");
        if (open.HasValue) DOS.Close(open.Value);
        var held = DOS.Lock("SEQ0:S", DOS.LockMode.Exclusive);
        Renamed(d, 24, "SEQ0:S", "SEQ0:S2");
        if (held.HasValue) DOS.UnLock(held.Value);

        // Is the CreateDir lock exclusive?
        var created = Opened(d, 26, DOS.CreateDir("SEQ0:NewDir"));
        if (created.IsNotNull) Put(d, 34, APTR.ReadUInt32(created.Address, 8));
        var again = Opened(d, 28, DOS.Lock("SEQ0:NewDir", DOS.LockMode.Shared));
        if (again.IsNotNull) DOS.UnLock(again);
        if (created.IsNotNull) DOS.UnLock(created);
        DOS.DeleteFile("SEQ0:NewDir");

        // Deleting an exclusively locked file.
        var locked = DOS.Lock("SEQ0:Data/Payload", DOS.LockMode.Exclusive);
        { var r = DOS.DeleteFile("SEQ0:Data/Payload"); Put(d, 30, unchecked((uint)r)); if (r == 0) Error(d, 31); }
        if (locked.HasValue) DOS.UnLock(locked.Value);

        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (DOS.Inhibit("SEQ0:", 1) != 0) { CorrectnessProbe.Report(210); return 0; }
            DOS.Delay(10);
        }
        return CorrectnessProbe.Failed(control, 90);
    }
}
