using System.Text.Json;
using Amiga;
using Copper68k;
using CopperStart.Floppy.Native.Tests;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    [CorrectnessTheory]
    [InlineData("ffs", "Ffs")]
    [InlineData("sfs", "Sfs")]
    [InlineData("sfs2-trst", "Sfs2")]
    public async System.Threading.Tasks.Task OldestResourcesRemainReachableBeyond4096(string profile, string entry)
    {
        string path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var handler = manifest.Profiles.Single(p => p.Name == profile).Candidate;
        byte[] media = profile switch
        {
            "ffs" => DosDiskImage.Create(true, includeLargeFile: false, blockCount: 32768).Bytes,
            "sfs" => ReadMedia.Sfs(),
            _ => ReadMedia.Sfs2()
        };

        await Run(Path.Combine(Path.GetDirectoryName(path)!, "live-resources"), profile, profile,
            handler, media, false, guestEntry: "LiveResourceProbe::" + entry, cpu: M68kCpuModel.M68000);
    }
}

public static class LiveResourceProbe
{
    private const int NewerLocks = 4096;

    public static int Ffs() => Run(1, "SEQ0:Data/Payload");
    public static int Sfs() => Run(2, "SEQ0:Payload");
    public static int Sfs2() => Run(3, "SEQ0:Payload");

    private static int Run(uint kind, CString path)
    {
        var library = Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) { CorrectnessProbe.Report(250); return 10; }
        DOS.DOSLibraryBase = library.Value;
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)), 184, uint.MaxValue);
        var control = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        if (control.IsNull) { CorrectnessProbe.Report(250); return 11; }
        CorrectnessProbe.Report(200);
        for (int shift = 24; shift >= 0; shift -= 8) CorrectnessProbe.Report((int)(control.Raw >> shift));
        int setup = CorrectnessProbe.Mount(control, kind);
        if (setup != 0) return CorrectnessProbe.Failed(control, (uint)setup);
        var port = DOS.DeviceProc("SEQ0:");
        var locks = APTR.FromPointer(Exec.AllocMem(NewerLocks * 4, Exec.MemoryFlags.Public));
        if (port.IsNull || locks.IsNull) return CorrectnessProbe.Failed(control, 16);

        var file = DOS.Open(path, DOS.FileMode.OldFile);
        var oldestLock = DOS.Lock("SEQ0:", (DOS.LockMode)(-2));
        if (!file.HasValue || !oldestLock.HasValue) return CorrectnessProbe.Failed(control, 17);
        for (int i = 0; i < NewerLocks; i++)
        {
            var locked = DOS.Lock("SEQ0:", (DOS.LockMode)(-2));
            if (!locked.HasValue) return CorrectnessProbe.Failed(control, 18);
            APTR.WriteUInt32(locks, i * 4, locked.Value.Raw);
        }

        // Both original records are still beyond the old lookup boundary.
        var buffer = APTR.FromPointer(control.Raw + 512);
        if (DOS.Read(file.Value, buffer, 1) != 1 || DOS.Close(file.Value) == 0)
            return CorrectnessProbe.Failed(control, 19);
        if (DOS.Examine(oldestLock.Value, buffer) == 0) return CorrectnessProbe.Failed(control, 20);
        int token = unchecked((int)oldestLock.Value.Raw);
        DOS.UnLock(oldestLock.Value);
        if (DOS.DoPkt(port, (int)DosPacketAction.FreeLock, token, 0, 0, 0, 0) != 0 ||
            (DOS.Error)DOS.IoErr() != DOS.Error.InvalidLock)
            return CorrectnessProbe.Failed(control, 21);

        for (int i = NewerLocks - 1; i >= 0; i--)
            DOS.UnLock(BPTR.FromRaw(APTR.ReadUInt32(locks, i * 4)));
        Exec.FreeMem(locks.Raw, NewerLocks * 4);
        if (DOS.Inhibit("SEQ0:", 1) == 0 || DOS.Inhibit("SEQ0:", 0) == 0)
            return CorrectnessProbe.Failed(control, 22);
        CorrectnessProbe.Report(210);
        return 0;
    }
}
