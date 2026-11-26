using Amiga;
using CopperStart.Floppy.Native.Tests;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    // CopperFFS does not implement ACTION_SET_FILE_SIZE or ACTION_FORMAT yet.
    // It must say so (209) rather than claim write protection (214). The
    // Kickstart 3.1 filesystem's replies are recorded for reference only:
    // it implements both packets.
    [LinkReferenceFact]
    public async System.Threading.Tasks.Task UnimplementedPacketsReportActionNotKnown()
    {
        var handler = Environment.GetEnvironmentVariable("COPPERFS_FFS_HANDLER")!;
        var root = (Environment.GetEnvironmentVariable("COPPERFS_LINK_ARTIFACTS") is { Length: > 0 } configured
            ? configured : Path.Combine(Path.GetTempPath(), "copperfs-link-reference")) + "-errors";
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var initial = DosDiskImage.Create(true, includeLargeFile: false, blockCount: 32768,
            firstFileBlock: 2, payload: [1, 2, 3], volumeName: "Errors").Bytes;
        var results = new Dictionary<string, uint[]>();
        foreach (var arm in new[] { "Rom", "Copper" })
        {
            var label = arm.ToLowerInvariant() + "-errors";
            await Run(root, "ffs", label, handler, initial, true, "ErrorCodeProbe::" + arm);
            results[arm] = ProbeData(root, label);
            output.WriteLine(arm + ": " + string.Join(",", results[arm].Take(6)));
        }
        // SetFileSize: Res1 -1, Res2 209. Format while inhibited: Res1 0, Res2 209. A NULL FIB: 115 (bad number).
        Assert.Equal(new uint[] { uint.MaxValue, 209, 0, 209, 0, 115 }, results["Copper"].Take(6));
        Directory.Delete(root, true);
    }
}

public static class ErrorCodeProbe
{
    public static int Rom() => Run(true);
    public static int Copper() => Run(false);

    private static int Run(bool rom)
    {
        var library = Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) { CorrectnessProbe.Report(250); return 10; }
        DOS.DOSLibraryBase = library.Value;
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)), 184, uint.MaxValue);
        var control = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var name = APTR.FromPointer(Exec.AllocMem(32, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        if (control.IsNull || name.IsNull) { CorrectnessProbe.Report(250); return 11; }
        CorrectnessProbe.Report(200);
        for (var shift = 24; shift >= 0; shift -= 8) CorrectnessProbe.Report((int)(control.Raw >> shift));
        var setup = CorrectnessProbe.Mount(control, 1, romFileSystem: rom);
        if (setup != 0) return CorrectnessProbe.Failed(control, (uint)setup);
        var d = APTR.FromPointer(control.Raw + 512);
        var port = DOS.DeviceProc("SEQ0:");
        var file = DOS.Open("SEQ0:Sized", DOS.FileMode.NewFile);
        if (!file.HasValue) return CorrectnessProbe.Failed(control, 20);
        var handle = file.Value.Address;
        // dp_Arg1 is the handle's fh_Arg1, the filesystem's own file token.
        var r = DOS.DoPkt(port, (int)DosPacketAction.SetFileSize, unchecked((int)APTR.ReadUInt32(handle, 36)), 100, -1, 0, 0);
        APTR.WriteUInt32(d, 0, unchecked((uint)r)); APTR.WriteUInt32(d, 4, unchecked((uint)DOS.IoErr()));
        DOS.Close(file.Value);
        APTR.WriteUInt8(name, 0, 3); APTR.WriteUInt8(name, 1, (byte)'N'); APTR.WriteUInt8(name, 2, (byte)'e'); APTR.WriteUInt8(name, 3, (byte)'w');
        // Only CopperFFS is asked to format: the reference filesystem would do it.
        if (!rom)
        {
            DOS.Inhibit("SEQ0:", 1);
            r = DOS.DoPkt(port, (int)DosPacketAction.Format, unchecked((int)(name.Raw >> 2)), 0x444F5301, 0, 0, 0);
            APTR.WriteUInt32(d, 8, unchecked((uint)r)); APTR.WriteUInt32(d, 12, unchecked((uint)DOS.IoErr()));
            DOS.Inhibit("SEQ0:", 0);
        }
        // The reference filesystem writes through a NULL FIB; ask only CopperFFS.
        var root = rom ? null : DOS.Lock("SEQ0:", DOS.LockMode.Shared);
        if (root.HasValue)
        {
            r = DOS.DoPkt(port, (int)DosPacketAction.ExamineObject, unchecked((int)root.Value.Raw), 0, 0, 0, 0);
            APTR.WriteUInt32(d, 16, unchecked((uint)r)); APTR.WriteUInt32(d, 20, unchecked((uint)DOS.IoErr()));
            DOS.UnLock(root.Value);
        }
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (DOS.Inhibit("SEQ0:", 1) != 0) { CorrectnessProbe.Report(210); return 0; }
            DOS.Delay(10);
        }
        return CorrectnessProbe.Failed(control, 90);
    }
}
