using Amiga;
using CopperStart.Floppy.Native.Tests;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    // Kickstart 3.1's own filesystem is the reference for a long-lived
    // appending handle: after the writer goes quiet, the root on disk must be
    // valid again and the appended data durable, while the file stays open.
    [LinkReferenceFact]
    public async System.Threading.Tasks.Task CopperFfsMatchesKickstartIdleValidation()
    {
        var handler = Environment.GetEnvironmentVariable("COPPERFS_FFS_HANDLER")!;
        var root = (Environment.GetEnvironmentVariable("COPPERFS_LINK_ARTIFACTS") is { Length: > 0 } configured
            ? configured : Path.Combine(Path.GetTempPath(), "copperfs-link-reference")) + "-idle";
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var initial = DosDiskImage.Create(true, includeLargeFile: false, blockCount: 32768,
            firstFileBlock: 2, payload: [1, 2, 3], volumeName: "Idle").Bytes;
        var results = new Dictionary<string, uint[]>();
        foreach (var arm in new[] { "Rom", "Copper" })
        {
            var label = arm.ToLowerInvariant() + "-idle";
            await Run(root, "ffs", label, handler, initial, true, "IdleProbe::" + arm);
            results[arm] = ProbeData(root, label);
            output.WriteLine(arm + ": " + string.Join(",", results[arm].Take(5)));
        }
        Assert.True(results["Rom"].SequenceEqual(results["Copper"]),
            "Differs from Kickstart:\nrom    " + string.Join(",", results["Rom"]) + "\ncopper " + string.Join(",", results["Copper"]));
        // Written 600 bytes; root valid; header found with the size and first data block durable.
        Assert.Equal(new uint[] { 600, uint.MaxValue, 1, 600, 1 }, results["Copper"].Take(5));
        Directory.Delete(root, true);
    }
}

public static class IdleProbe
{
    public static int Rom() => Run(true);
    public static int Copper() => Run(false);

    // Read one 512-byte block straight from copperhdf.device, bypassing the filesystem.
    private static bool ReadRaw(APTR request, APTR buffer, uint block)
    {
        APTR.WriteUInt16(request, 28, 2); // CMD_READ
        APTR.WriteUInt32(request, 36, 512); APTR.WriteUInt32(request, 40, buffer.Raw); APTR.WriteUInt32(request, 44, block * 512);
        Exec.DoIO(request);
        return APTR.ReadUInt8(request, 31) == 0; // io_Error
    }

    private static int Run(bool rom)
    {
        var library = Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) { CorrectnessProbe.Report(250); return 10; }
        DOS.DOSLibraryBase = library.Value;
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)), 184, uint.MaxValue);
        var control = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var buffer = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var replies = Exec.CreateMsgPort();
        if (control.IsNull || buffer.IsNull || replies.IsNull) { CorrectnessProbe.Report(250); return 11; }
        var request = Exec.CreateIORequest(replies, 48);
        if (request.IsNull || Exec.OpenDevice(CString.FromPointer(CString.ToUInt32("copperhdf.device")), 0, request, 0) != 0)
        { CorrectnessProbe.Report(250); return 12; }
        CorrectnessProbe.Report(200);
        for (var shift = 24; shift >= 0; shift -= 8) CorrectnessProbe.Report((int)(control.Raw >> shift));
        var setup = CorrectnessProbe.Mount(control, 1, romFileSystem: rom);
        if (setup != 0) return CorrectnessProbe.Failed(control, (uint)setup);
        var d = APTR.FromPointer(control.Raw + 512);
        const uint root = 16384;
        for (var i = 0; i < 600; i++) APTR.WriteUInt8(buffer, i, (byte)(i * 5 + 1));
        var log = DOS.Open("SEQ0:Log", DOS.FileMode.NewFile);
        if (!log.HasValue) return CorrectnessProbe.Failed(control, 20);
        APTR.WriteUInt32(d, 0, unchecked((uint)DOS.Write(log.Value, buffer, 600)));
        // Quiet for ten seconds with the handle still open.
        DOS.Delay(500);
        if (ReadRaw(request, buffer, root)) APTR.WriteUInt32(d, 4, APTR.ReadUInt32(buffer, 312));
        // The file's own header, found through the root hash table on disk.
        var found = 0u;
        for (var slot = 0; slot < 72 && found == 0; slot++)
        {
            if (!ReadRaw(request, buffer, root)) break;
            for (var key = APTR.ReadUInt32(buffer, 24 + slot * 4); key != 0 && found == 0;)
            {
                if (!ReadRaw(request, buffer, key)) break;
                if (APTR.ReadUInt8(buffer, 432) == 3 && APTR.ReadUInt8(buffer, 433) == (byte)'L' &&
                    APTR.ReadUInt8(buffer, 434) == (byte)'o' && APTR.ReadUInt8(buffer, 435) == (byte)'g') found = key;
                else key = APTR.ReadUInt32(buffer, 496);
            }
        }
        APTR.WriteUInt32(d, 8, found != 0 ? 1u : 0u);
        if (found != 0)
        {
            APTR.WriteUInt32(d, 12, APTR.ReadUInt32(buffer, 324)); // byte_size on disk
            var first = APTR.ReadUInt32(buffer, 308);
            if (first != 0 && ReadRaw(request, buffer, first))
                APTR.WriteUInt32(d, 16, APTR.ReadUInt8(buffer, 0) == 1 && APTR.ReadUInt8(buffer, 1) == 6 ? 1u : 0u);
        }
        DOS.Close(log.Value);
        Exec.CloseDevice(request);
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (DOS.Inhibit("SEQ0:", 1) != 0) { CorrectnessProbe.Report(210); return 0; }
            DOS.Delay(10);
        }
        return CorrectnessProbe.Failed(control, 90);
    }
}
