using Amiga;
using CopperStart.Floppy.Native.Tests;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    // Kickstart 3.1's own filesystem is the reference for the ACTION_SAME_LOCK
    // reply, sent directly so dos.library's SameLock mapping is not involved.
    [LinkReferenceFact]
    public async System.Threading.Tasks.Task CopperFfsMatchesKickstartSameLock()
    {
        var handler = Environment.GetEnvironmentVariable("COPPERFS_FFS_HANDLER")!;
        var root = (Environment.GetEnvironmentVariable("COPPERFS_LINK_ARTIFACTS") is { Length: > 0 } configured
            ? configured : Path.Combine(Path.GetTempPath(), "copperfs-link-reference")) + "-samelock";
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var initial = DosDiskImage.Create(true, includeLargeFile: false, blockCount: 32768,
            firstFileBlock: 2, payload: [1, 2, 3], volumeName: "Same").Bytes;
        var results = new Dictionary<string, uint[]>();
        foreach (var arm in new[] { "Rom", "Copper" })
        {
            var label = arm.ToLowerInvariant() + "-samelock";
            await Run(root, "ffs", label, handler, initial, false, "SameLockProbe::" + arm);
            results[arm] = ProbeData(root, label);
        }
        Assert.True(results["Rom"].SequenceEqual(results["Copper"]),
            "Differs from Kickstart:\nrom    " + string.Join(",", results["Rom"]) + "\ncopper " + string.Join(",", results["Copper"]));
        Directory.Delete(root, true);
    }
}

public static class SameLockProbe
{
    public static int Rom() => Run(true);
    public static int Copper() => Run(false);

    private static void Compare(APTR d, int index, BPTR first, BPTR second)
    {
        var r = DOS.DoPkt(DOS.DeviceProc("SEQ0:"), (int)DosPacketAction.SameLock,
            unchecked((int)first.Raw), unchecked((int)second.Raw), 0, 0, 0);
        APTR.WriteUInt32(d, index * 4, unchecked((uint)r));
        APTR.WriteUInt32(d, index * 4 + 4, r == 0 ? unchecked((uint)DOS.IoErr()) : 0);
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
        var a = DOS.Lock("SEQ0:Data/Payload", DOS.LockMode.Shared);
        var again = DOS.Lock("SEQ0:Data/Payload", DOS.LockMode.Shared);
        var other = DOS.Lock("SEQ0:Data", DOS.LockMode.Shared);
        if (!a.HasValue || !again.HasValue || !other.HasValue) return CorrectnessProbe.Failed(control, 20);
        Compare(d, 0, a.Value, a.Value);
        Compare(d, 2, a.Value, again.Value);
        Compare(d, 4, a.Value, other.Value);
        DOS.UnLock(a.Value); DOS.UnLock(again.Value); DOS.UnLock(other.Value);
        CorrectnessProbe.Report(210);
        return 0;
    }
}
