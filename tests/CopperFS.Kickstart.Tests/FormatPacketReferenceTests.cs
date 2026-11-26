using Amiga;
using CopperSFS;
using CopperStart.Floppy.Native.Tests;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    // Kickstart 3.1's own filesystem is the reference for ACTION_FORMAT sent
    // to a mounted FFS volume: refusal while not inhibited, success while a
    // lock is held, then an inhibited DOS\1 -> DOS\5 format, and what DOS sees
    // after the volume is released (name, disk type, Info counts). Both
    // formatted images are compared block by block like the Format layout
    // test; a second boot then checks that old files are gone and the new
    // volume accepts a file.
    [LinkReferenceFact]
    public async System.Threading.Tasks.Task CopperFfsMatchesKickstartFormatPacket()
    {
        var handler = Environment.GetEnvironmentVariable("COPPERFS_FFS_HANDLER")!;
        var root = (Environment.GetEnvironmentVariable("COPPERFS_LINK_ARTIFACTS") is { Length: > 0 } configured
            ? configured : Path.Combine(Path.GetTempPath(), "copperfs-link-reference")) + "-formatpacket";
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var initial = DosDiskImage.Create(true, includeLargeFile: false, blockCount: 32768,
            firstFileBlock: 2, payload: [1, 2, 3], volumeName: "Before").Bytes;
        var results = new Dictionary<string, uint[]>();
        var images = new Dictionary<string, byte[]>();
        foreach (var arm in new[] { "Rom", "Copper" })
        {
            var label = arm.ToLowerInvariant();
            images[arm] = await Run(root, "ffs", label, handler, initial, true, "FormatPacketProbe::" + arm);
            var formatted = ProbeData(root, label).Take(FormatPacketProbe.Words);
            await Run(root, "ffs", label + "-use", handler, images[arm], true, "FormatPacketProbe::" + arm + "Use");
            results[arm] = formatted.Concat(ProbeData(root, label + "-use").Take(4)).ToArray();
            output.WriteLine(arm + ": " + string.Join(",", results[arm]));
        }
        Assert.True(results["Rom"].SequenceEqual(results["Copper"]),
            "Differs from Kickstart:\nrom    " + string.Join(",", results["Rom"]) + "\ncopper " + string.Join(",", results["Copper"]));
        Assert.True(AmigaVolumeGeometry.Create(32768, 512, 2, out var g));
        var differences = FormatDifferences(initial, images["Rom"], images["Copper"], g, 5);
        foreach (var line in differences) output.WriteLine(line);
        Assert.True(differences.Count == 0, "Image differs from Kickstart Format:\n" + string.Join("\n", differences.Take(60)));
        Directory.Delete(root, true);
    }
}

public static class FormatPacketProbe
{
    internal const int Words = 13;
    public static int Rom() => Run(true, false);
    public static int Copper() => Run(false, false);
    public static int RomUse() => Run(true, true);
    public static int CopperUse() => Run(false, true);

    private static int Run(bool rom, bool use)
    {
        var library = Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) { CorrectnessProbe.Report(250); return 10; }
        DOS.DOSLibraryBase = library.Value;
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)), 184, uint.MaxValue);
        var control = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var name = APTR.FromPointer(Exec.AllocMem(16, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var info = APTR.FromPointer(Exec.AllocMem(260, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        if (control.IsNull || name.IsNull || info.IsNull) { CorrectnessProbe.Report(250); return 11; }
        CorrectnessProbe.Report(200);
        for (var shift = 24; shift >= 0; shift -= 8) CorrectnessProbe.Report((int)(control.Raw >> shift));
        var setup = CorrectnessProbe.Mount(control, 1, romFileSystem: rom);
        if (setup != 0) return CorrectnessProbe.Failed(control, (uint)setup);
        // The reused image is DOS\5 now.
        if (use) APTR.WriteUInt32(APTR.FromPointer(control.Raw + 128), DosLayout.DosEnvec.DosType, 0x444F5305);
        var d = APTR.FromPointer(control.Raw + 512);
        void Put(int index, uint value) => APTR.WriteUInt32(d, index * 4, value);
        if (use)
        {
            // Old files are gone and the new volume accepts writes.
            var old = DOS.Lock("SEQ0:Data", DOS.LockMode.Shared);
            Put(0, old.HasValue ? 1u : 0u); Put(1, unchecked((uint)DOS.IoErr()));
            if (old.HasValue) DOS.UnLock(old.Value);
            var file = DOS.Open("SEQ0:New", DOS.FileMode.NewFile);
            Put(2, file.HasValue ? 1u : 0u);
            if (file.HasValue) { Put(3, unchecked((uint)DOS.Write(file.Value, info, 100))); DOS.Close(file.Value); }
            return Finish(control);
        }
        // BSTR "Fresh" (dp_Arg1 is a BPTR to a BCPL string).
        var text = APTR.FromPointer(CString.ToUInt32(CString.FromLiteral("Fresh")));
        APTR.WriteUInt8(name, 0, 5);
        for (var i = 0; i < 5; i++) APTR.WriteUInt8(name, 1 + i, APTR.ReadUInt8(text, i));
        var port = DOS.DeviceProc("SEQ0:");
        int Format() => DOS.DoPkt(port, (int)DosPacketAction.Format, unchecked((int)(name.Raw >> 2)), 0x444F5305, 0, 0, 0);
        // Not inhibited.
        Put(0, unchecked((uint)Format())); Put(1, unchecked((uint)DOS.IoErr()));
        // Inhibited while a lock is held.
        var held = DOS.Lock("SEQ0:Data", DOS.LockMode.Shared);
        Put(2, held.HasValue ? 1u : 0u);
        if (DOS.Inhibit("SEQ0:", 1) == 0) return CorrectnessProbe.Failed(control, 4);
        Put(3, unchecked((uint)Format())); Put(4, unchecked((uint)DOS.IoErr()));
        if (DOS.Inhibit("SEQ0:", 0) == 0) return CorrectnessProbe.Failed(control, 5);
        if (held.HasValue) DOS.UnLock(held.Value);
        // Inhibited with nothing open.
        if (DOS.Inhibit("SEQ0:", 1) == 0) return CorrectnessProbe.Failed(control, 6);
        Put(5, unchecked((uint)Format())); Put(6, unchecked((uint)DOS.IoErr()));
        if (DOS.Inhibit("SEQ0:", 0) == 0) return CorrectnessProbe.Failed(control, 7);
        var volume = DOS.Lock("Fresh:", DOS.LockMode.Shared);
        Put(7, volume.HasValue ? 1u : 0u);
        var rootLock = DOS.Lock("SEQ0:", DOS.LockMode.Shared);
        if (rootLock.HasValue)
        {
            if (DOS.Examine(rootLock.Value, info) != 0)
            {
                Put(8, APTR.ReadUInt32(info, 4));
                Put(9, APTR.ReadUInt32(info, 8)); // fib_FileName: length byte and first three characters
            }
            if (DOS.Info(rootLock.Value, info) != 0)
            { Put(10, APTR.ReadUInt32(info, 16)); Put(11, APTR.ReadUInt32(info, 24)); Put(12, APTR.ReadUInt32(info, 12)); }
            DOS.UnLock(rootLock.Value);
        }
        if (volume.HasValue) DOS.UnLock(volume.Value);
        return Finish(control);
    }

    private static int Finish(APTR control)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (DOS.Inhibit("SEQ0:", 1) != 0) { CorrectnessProbe.Report(210); return 0; }
            DOS.Delay(10);
        }
        return CorrectnessProbe.Failed(control, 90);
    }
}
