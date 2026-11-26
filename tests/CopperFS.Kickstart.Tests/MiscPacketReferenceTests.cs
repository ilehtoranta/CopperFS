using Amiga;
using CopperStart.Floppy.Native.Tests;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    // Kickstart 3.1's own filesystem is the reference for the remaining
    // packets, each sent directly: ACTION_RENAME_DISK, ACTION_SET_OWNER,
    // ACTION_WRITE_PROTECT, ACTION_CHANGE_MODE, ACTION_SERIALIZE_DISK and
    // ACTION_GET/FREE_DISK_FSSM. ACTION_DIE is not sent: it ends the handler.
    [LinkReferenceFact]
    public async System.Threading.Tasks.Task CopperFfsMatchesKickstartMiscPackets()
    {
        var handler = Environment.GetEnvironmentVariable("COPPERFS_FFS_HANDLER")!;
        var root = (Environment.GetEnvironmentVariable("COPPERFS_LINK_ARTIFACTS") is { Length: > 0 } configured
            ? configured : Path.Combine(Path.GetTempPath(), "copperfs-link-reference")) + "-misc";
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var initial = DosDiskImage.Create(true, includeLargeFile: false, blockCount: 32768,
            firstFileBlock: 2, payload: [1, 2, 3], volumeName: "Misc").Bytes;
        var results = new Dictionary<string, uint[]>();
        var images = new Dictionary<string, byte[]>();
        foreach (var arm in new[] { "Rom", "Copper" })
        {
            var label = arm.ToLowerInvariant() + "-misc";
            images[arm] = await Run(root, "ffs", label, handler, initial, true, "MiscPacketProbe::" + arm);
            results[arm] = ProbeData(root, label);
            output.WriteLine(arm + ": " + string.Join(",", results[arm].Take(30)));
        }
        Assert.True(results["Rom"].SequenceEqual(results["Copper"]),
            "Differs from Kickstart:\nrom    " + string.Join(",", results["Rom"]) + "\ncopper " + string.Join(",", results["Copper"]));
        // The new volume name and owner reached the disk in both.
        Assert.Equal(images["Rom"].AsSpan(16384 * 512 + 432, 32).ToArray(), images["Copper"].AsSpan(16384 * 512 + 432, 32).ToArray());
        Directory.Delete(root, true);
    }
}

public static class MiscPacketProbe
{
    public static int Rom() => Run(true);
    public static int Copper() => Run(false);

    private static void Record(APTR d, int index, int result)
    {
        APTR.WriteUInt32(d, index * 4, unchecked((uint)result));
        APTR.WriteUInt32(d, index * 4 + 4, result == 0 ? unchecked((uint)DOS.IoErr()) : 0);
    }

    private static bool NameIs(APTR bstr, CString expected)
    {
        var text = APTR.FromPointer(CString.ToUInt32(expected));
        var length = APTR.ReadUInt8(bstr, 0);
        for (var i = 0; i < length; i++) if (APTR.ReadUInt8(bstr, 1 + i) != APTR.ReadUInt8(text, i)) return false;
        return APTR.ReadUInt8(text, length) == 0;
    }

    private static int Run(bool rom)
    {
        var library = Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) { CorrectnessProbe.Report(250); return 10; }
        DOS.DOSLibraryBase = library.Value;
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)), 184, uint.MaxValue);
        var control = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var name = APTR.FromPointer(Exec.AllocMem(64, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var fib = APTR.FromPointer(Exec.AllocMem(260, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var info = APTR.FromPointer(Exec.AllocMem(64, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        if (control.IsNull || name.IsNull || fib.IsNull || info.IsNull) { CorrectnessProbe.Report(250); return 11; }
        CorrectnessProbe.Report(200);
        for (var shift = 24; shift >= 0; shift -= 8) CorrectnessProbe.Report((int)(control.Raw >> shift));
        var setup = CorrectnessProbe.Mount(control, 1, romFileSystem: rom);
        if (setup != 0) return CorrectnessProbe.Failed(control, (uint)setup);
        var d = APTR.FromPointer(control.Raw + 512);
        var port = DOS.DeviceProc("SEQ0:");

        // RENAME_DISK: Arg1 BSTR new name. Root header and volume node both change.
        var label = APTR.FromPointer(CString.ToUInt32(CString.FromLiteral("Relabelled")));
        APTR.WriteUInt8(name, 0, 10);
        for (var i = 0; i < 10; i++) APTR.WriteUInt8(name, 1 + i, APTR.ReadUInt8(label, i));
        Record(d, 0, DOS.DoPkt(port, (int)DosPacketAction.RenameDisk, unchecked((int)(name.Raw >> 2)), 0, 0, 0, 0));
        var root = DOS.Lock("SEQ0:", DOS.LockMode.Shared);
        if (root.HasValue)
        {
            if (DOS.Examine(root.Value, fib) != 0)
            {
                var text = APTR.FromPointer(CString.ToUInt32(CString.FromLiteral("Relabelled")));
                var same = 1u;
                for (var i = 0; i < 11; i++) if (APTR.ReadUInt8(fib, 8 + i) != APTR.ReadUInt8(text, i)) same = 0;
                APTR.WriteUInt32(d, 8, same);
            }
            if (DOS.DoPkt(port, (int)DosPacketAction.Info, unchecked((int)root.Value.Raw), unchecked((int)(info.Raw >> 2)), 0, 0, 0) != 0)
            {
                var volume = APTR.FromPointer(APTR.ReadUInt32(info, 28) << 2);
                APTR.WriteUInt32(d, 12, volume.IsNotNull && NameIs(APTR.FromPointer(APTR.ReadUInt32(volume, 40) << 2), CString.FromLiteral("Relabelled")) ? 1u : 0u);
            }
            DOS.UnLock(root.Value);
        }

        // SET_OWNER: Arg2 lock (0: root), Arg3 BSTR name, Arg4 UID << 16 | GID.
        var payload = APTR.FromPointer(CString.ToUInt32(CString.FromLiteral("Data/Payload")));
        APTR.WriteUInt8(name, 0, 12);
        for (var i = 0; i < 12; i++) APTR.WriteUInt8(name, 1 + i, APTR.ReadUInt8(payload, i));
        Record(d, 4, DOS.DoPkt(port, (int)DosPacketAction.SetOwner, 0, 0, unchecked((int)(name.Raw >> 2)), 0x00050007, 0));
        var owned = DOS.Lock("SEQ0:Data/Payload", DOS.LockMode.Shared);
        if (owned.HasValue)
        {
            if (DOS.Examine(owned.Value, fib) != 0)
                APTR.WriteUInt32(d, 24, (uint)APTR.ReadUInt16(fib, 224) << 16 | APTR.ReadUInt16(fib, 226));
            DOS.UnLock(owned.Value);
        }

        // WRITE_PROTECT on, a create, off, a create.
        Record(d, 7, DOS.DoPkt(port, (int)DosPacketAction.WriteProtect, -1, 0, 0, 0, 0));
        var blocked = DOS.Open("SEQ0:Blocked", DOS.FileMode.NewFile);
        APTR.WriteUInt32(d, 36, blocked.HasValue ? 1u : unchecked((uint)DOS.IoErr()));
        if (blocked.HasValue) DOS.Close(blocked.Value);
        Record(d, 10, DOS.DoPkt(port, (int)DosPacketAction.WriteProtect, 0, 0, 0, 0, 0));
        var allowed = DOS.Open("SEQ0:Allowed", DOS.FileMode.NewFile);
        APTR.WriteUInt32(d, 48, allowed.HasValue ? 1u : unchecked((uint)DOS.IoErr()));
        if (allowed.HasValue) DOS.Close(allowed.Value);

        // CHANGE_MODE (Arg1 CHANGE_LOCK = 0): shared -> exclusive beside another shared lock, then alone.
        var first = DOS.Lock("SEQ0:Data/Payload", DOS.LockMode.Shared);
        var second = DOS.Lock("SEQ0:Data/Payload", DOS.LockMode.Shared);
        if (first.HasValue && second.HasValue)
        {
            Record(d, 13, DOS.DoPkt(port, (int)DosPacketAction.ChangeMode, 0, unchecked((int)first.Value.Raw), -1, 0, 0));
            DOS.UnLock(second.Value);
            Record(d, 15, DOS.DoPkt(port, (int)DosPacketAction.ChangeMode, 0, unchecked((int)first.Value.Raw), -1, 0, 0));
            var third = DOS.Lock("SEQ0:Data/Payload", DOS.LockMode.Shared);
            APTR.WriteUInt32(d, 68, third.HasValue ? 1u : unchecked((uint)DOS.IoErr()));
            if (third.HasValue) DOS.UnLock(third.Value);
            Record(d, 18, DOS.DoPkt(port, (int)DosPacketAction.ChangeMode, 0, unchecked((int)first.Value.Raw), -2, 0, 0));
            DOS.UnLock(first.Value);
        }

        // GET_DISK_FSSM / FREE_DISK_FSSM.
        var fssm = DOS.DoPkt(port, 4201, 0, 0, 0, 0, 0);
        APTR.WriteUInt32(d, 80, fssm != 0 ? 1u : 0u);
        APTR.WriteUInt32(d, 84, fssm == 0 ? unchecked((uint)DOS.IoErr()) : 0);
        if (fssm != 0) Record(d, 22, DOS.DoPkt(port, 4202, fssm, 0, 0, 0, 0));

        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (DOS.Inhibit("SEQ0:", 1) != 0) { CorrectnessProbe.Report(210); return 0; }
            DOS.Delay(10);
        }
        return CorrectnessProbe.Failed(control, 90);
    }
}
