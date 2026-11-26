using Amiga;
using CopperStart.Floppy.Native.Tests;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    // Kickstart 3.1's own filesystem is the reference for ACTION_INHIBIT:
    // what lookups, opens, reads and existing locks return while inhibited,
    // whether inhibits nest, and what survives the final uninhibit.
    [LinkReferenceFact]
    public async System.Threading.Tasks.Task CopperFfsMatchesKickstartInhibit()
    {
        var handler = Environment.GetEnvironmentVariable("COPPERFS_FFS_HANDLER")!;
        var root = (Environment.GetEnvironmentVariable("COPPERFS_LINK_ARTIFACTS") is { Length: > 0 } configured
            ? configured : Path.Combine(Path.GetTempPath(), "copperfs-link-reference")) + "-inhibit";
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var initial = DosDiskImage.Create(true, includeLargeFile: false, blockCount: 32768,
            firstFileBlock: 2, payload: [1, 2, 3], volumeName: "Inhibit").Bytes;
        var results = new Dictionary<string, uint[]>();
        var images = new Dictionary<string, byte[]>();
        foreach (var arm in new[] { "Rom", "Copper" })
        {
            var label = arm.ToLowerInvariant() + "-inhibit";
            images[arm] = await Run(root, "ffs", label, handler, initial, true, "InhibitProbe::" + arm);
            results[arm] = ProbeData(root, label);
        }
        File.WriteAllText(Path.Combine(root, "inhibit-reference.json"), System.Text.Json.JsonSerializer.Serialize(results, Json));
        Assert.True(results["Rom"].SequenceEqual(results["Copper"]),
            "Differs from Kickstart:\nrom    " + string.Join(",", results["Rom"]) + "\ncopper " + string.Join(",", results["Copper"]));
        Assert.Equal(LinkLayout(images["Rom"]), LinkLayout(images["Copper"]));
    }
}

public static class InhibitProbe
{
    public static int Rom() => Run(true);
    public static int Copper() => Run(false);

    private static void Put(APTR d, int index, uint value) => APTR.WriteUInt32(d, index * 4, value);
    private static void Error(APTR d, int index) => Put(d, index, unchecked((uint)DOS.IoErr()));

    private static void TryLock(APTR d, int index)
    {
        var locked = DOS.Lock("SEQ0:Data/Payload", DOS.LockMode.Shared);
        Put(d, index, locked.HasValue ? 1u : 0u);
        if (locked.HasValue) DOS.UnLock(locked.Value); else Error(d, index + 1);
    }

    private static int Run(bool rom)
    {
        var library = Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) { CorrectnessProbe.Report(250); return 10; }
        DOS.DOSLibraryBase = library.Value;
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)), 184, uint.MaxValue);
        var control = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var buffer = APTR.FromPointer(Exec.AllocMem(64, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var fib = APTR.FromPointer(Exec.AllocMem(260, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        if (control.IsNull || buffer.IsNull || fib.IsNull) { CorrectnessProbe.Report(250); return 11; }
        CorrectnessProbe.Report(200);
        for (var shift = 24; shift >= 0; shift -= 8) CorrectnessProbe.Report((int)(control.Raw >> shift));
        var setup = CorrectnessProbe.Mount(control, 1, romFileSystem: rom);
        if (setup != 0) return CorrectnessProbe.Failed(control, (uint)setup);
        var d = APTR.FromPointer(control.Raw + 512);
        var held = DOS.Lock("SEQ0:Data/Payload", DOS.LockMode.Shared);
        var handle = DOS.Open("SEQ0:Data/Payload", DOS.FileMode.OldFile);
        if (!held.HasValue || !handle.HasValue) return CorrectnessProbe.Failed(control, 20);
        Put(d, 0, unchecked((uint)DOS.Inhibit("SEQ0:", 1)));
        TryLock(d, 1);
        var created = DOS.Open("SEQ0:New", DOS.FileMode.NewFile);
        Put(d, 3, created.HasValue ? 1u : 0u);
        if (created.HasValue) DOS.Close(created.Value); else Error(d, 4);
        { var r = DOS.Read(handle.Value, buffer, 3); Put(d, 5, unchecked((uint)r)); if (r < 0) Error(d, 6); }
        { var r = DOS.Examine(held.Value, fib); Put(d, 7, unchecked((uint)r)); if (r == 0) Error(d, 8); }
        // A second inhibit and a single release: is the volume still inhibited?
        Put(d, 9, unchecked((uint)DOS.Inhibit("SEQ0:", 1)));
        Put(d, 10, unchecked((uint)DOS.Inhibit("SEQ0:", 0)));
        TryLock(d, 11);
        Put(d, 13, unchecked((uint)DOS.Inhibit("SEQ0:", 0)));
        TryLock(d, 14);
        // Objects from before the inhibit, on the same unchanged disk.
        { var r = DOS.Examine(held.Value, fib); Put(d, 16, unchecked((uint)r)); if (r == 0) Error(d, 17); }
        { var r = DOS.Seek(handle.Value, 0, -1); Put(d, 18, unchecked((uint)r)); }
        { var r = DOS.Read(handle.Value, buffer, 3); Put(d, 19, unchecked((uint)r)); if (r < 0) Error(d, 20); }
        Put(d, 21, APTR.ReadUInt8(buffer, 0) == 1 && APTR.ReadUInt8(buffer, 2) == 3 ? 1u : 0u);
        DOS.UnLock(held.Value); DOS.Close(handle.Value);
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (DOS.Inhibit("SEQ0:", 1) != 0) { CorrectnessProbe.Report(210); return 0; }
            DOS.Delay(10);
        }
        return CorrectnessProbe.Failed(control, 90);
    }
}
