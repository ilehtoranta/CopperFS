using Amiga;
using CopperStart.Floppy.Native.Tests;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    // Kickstart 3.1's own filesystem is the reference for which changes
    // signal a notification: a watched file that does not exist yet, its
    // directory and an unrelated absent path, across creates, writes,
    // attribute changes, renames and deletes. Each step records which of the
    // three watches signalled (bits 0-2); word 0 records which started.
    [LinkReferenceFact]
    public async System.Threading.Tasks.Task CopperFfsMatchesKickstartNotifications()
    {
        var handler = Environment.GetEnvironmentVariable("COPPERFS_FFS_HANDLER")!;
        var root = (Environment.GetEnvironmentVariable("COPPERFS_LINK_ARTIFACTS") is { Length: > 0 } configured
            ? configured : Path.Combine(Path.GetTempPath(), "copperfs-link-reference")) + "-notify";
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var initial = DosDiskImage.Create(true, includeLargeFile: false, blockCount: 32768,
            firstFileBlock: 2, payload: [1, 2, 3], volumeName: "Notify").Bytes;
        var results = new Dictionary<string, uint[]>();
        foreach (var arm in new[] { "Rom", "Copper" })
        {
            var label = arm.ToLowerInvariant() + "-notify";
            await Run(root, "ffs", label, handler, initial, true, "NotifyProbe::" + arm);
            results[arm] = ProbeData(root, label).Take(NotifyProbe.Steps + 1).ToArray();
            output.WriteLine(arm + ": " + string.Join(",", results[arm]));
        }
        // Every notification Kickstart sends must also arrive from CopperFFS.
        // CopperFFS also signals some changes Kickstart 3.1 stays silent on
        // (measured: SetProtection, SetComment, the directory of a deleted
        // file, and a watched directory being created or deleted). Extra
        // signals only make a client re-read; a missing one leaves it stale.
        var rom = results["Rom"]; var copper = results["Copper"];
        Assert.True(rom[0] == copper[0] && rom.Zip(copper).All(pair => (pair.First & ~pair.Second) == 0),
            "Kickstart notified a watch CopperFFS did not:\nrom    " + string.Join(",", rom) + "\ncopper " + string.Join(",", copper));
        Directory.Delete(root, true);
    }
}

public static class NotifyProbe
{
    internal const int Steps = 11;
    public static int Rom() => Run(true);
    public static int Copper() => Run(false);

    private static void Request(APTR request, CString name, APTR task, int signal)
    {
        for (var n = 0; n < 48; n += 4) APTR.WriteUInt32(request, n, 0);
        APTR.WriteUInt32(request, 0, CString.ToUInt32(name));
        APTR.WriteUInt32(request, 12, 2); // NRF_SEND_SIGNAL
        APTR.WriteUInt32(request, 16, task.Raw);
        APTR.WriteUInt8(request, 20, (byte)signal);
    }

    private static void Touch(CString path, int bytes, APTR buffer)
    {
        var file = DOS.Open(path, bytes < 0 ? DOS.FileMode.NewFile : DOS.FileMode.OldFile);
        if (!file.HasValue) return;
        if (bytes > 0) DOS.Write(file.Value, buffer, bytes);
        DOS.Close(file.Value);
    }

    private static int Run(bool rom)
    {
        var library = Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) { CorrectnessProbe.Report(250); return 10; }
        DOS.DOSLibraryBase = library.Value;
        var task = Exec.FindTask(CString.FromPointer(0));
        APTR.WriteUInt32(task, 184, uint.MaxValue);
        var control = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var requests = APTR.FromPointer(Exec.AllocMem(192, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        if (control.IsNull || requests.IsNull) { CorrectnessProbe.Report(250); return 11; }
        CorrectnessProbe.Report(200);
        for (var shift = 24; shift >= 0; shift -= 8) CorrectnessProbe.Report((int)(control.Raw >> shift));
        var setup = CorrectnessProbe.Mount(control, 1, romFileSystem: rom);
        if (setup != 0) return CorrectnessProbe.Failed(control, (uint)setup);
        var d = APTR.FromPointer(control.Raw + 512);
        var directory = DOS.CreateDir("SEQ0:N");
        if (directory.HasValue) DOS.UnLock(directory.Value);
        var s0 = Exec.AllocSignal(-1); var s1 = Exec.AllocSignal(-1); var s2 = Exec.AllocSignal(-1);
        if (s0 < 0 || s1 < 0 || s2 < 0) return CorrectnessProbe.Failed(control, 30);
        var mask = (1u << s0) | (1u << s1) | (1u << s2);
        Request(requests, CString.FromLiteral("SEQ0:N/F"), task, s0);
        Request(APTR.FromPointer(requests.Raw + 64), CString.FromLiteral("SEQ0:N"), task, s1);
        Request(APTR.FromPointer(requests.Raw + 128), CString.FromLiteral("SEQ0:Other"), task, s2);
        uint started = 0;
        if (DOS.StartNotify(requests) != 0) started |= 1;
        if (DOS.StartNotify(APTR.FromPointer(requests.Raw + 64)) != 0) started |= 2;
        if (DOS.StartNotify(APTR.FromPointer(requests.Raw + 128)) != 0) started |= 4;
        APTR.WriteUInt32(d, 0, started);
        Exec.SetSignal(0, mask);
        var buffer = APTR.FromPointer(control.Raw + 800);
        for (var step = 0; step < Steps; step++)
        {
            if (step == 0) Touch(CString.FromLiteral("SEQ0:N/F"), -1, buffer);
            else if (step == 1) Touch(CString.FromLiteral("SEQ0:N/F"), 10, buffer);
            else if (step == 2) DOS.SetProtection("SEQ0:N/F", 0);
            else if (step == 3) DOS.SetComment("SEQ0:N/F", "c");
            else if (step == 4) Touch(CString.FromLiteral("SEQ0:N/X"), -1, buffer);
            else if (step == 5) DOS.Rename("SEQ0:N/F", "SEQ0:N/G");
            else if (step == 6) DOS.Rename("SEQ0:N/G", "SEQ0:N/F");
            else if (step == 7) DOS.DeleteFile("SEQ0:N/X");
            else if (step == 8) DOS.DeleteFile("SEQ0:N/F");
            else if (step == 9) { var other = DOS.CreateDir("SEQ0:Other"); if (other.HasValue) DOS.UnLock(other.Value); }
            else DOS.DeleteFile("SEQ0:Other");
            DOS.Delay(5);
            var raised = Exec.SetSignal(0, mask) & mask;
            APTR.WriteUInt32(d, (step + 1) * 4, ((raised >> s0) & 1) | (((raised >> s1) & 1) << 1) | (((raised >> s2) & 1) << 2));
        }
        DOS.EndNotify(requests); DOS.EndNotify(APTR.FromPointer(requests.Raw + 64)); DOS.EndNotify(APTR.FromPointer(requests.Raw + 128));
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (DOS.Inhibit("SEQ0:", 1) != 0) { CorrectnessProbe.Report(210); return 0; }
            DOS.Delay(10);
        }
        return CorrectnessProbe.Failed(control, 90);
    }
}
