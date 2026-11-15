using Amiga;
using Task = System.Threading.Tasks.Task;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    [Sfs2RecoveryTheory]
    [InlineData(0)]
    public async Task IdenticalTrstPacketSkipsCommitButFollowingMutationCommits(int unused)
    {
        var candidate = Environment.GetEnvironmentVariable("COPPERFS_SFS2_TRST_HANDLER")!;
        var root = Path.Combine(Path.GetDirectoryName(candidate)!, "no-op-qualification");
        int writes = 0, flushes = 0, noOpWrites = -1, noOpFlushes = -1, changedFlushes = -1;
        var result = await Run(root, "sfs2-trst", "candidate", candidate, ReadMedia.Sfs2(), true,
            "TrstNoOpProbe::Execute", observe: (marker, _, _, w, f) =>
            {
                if (marker == 220) { writes = w; flushes = f; }
                if (marker == 221) { noOpWrites = w - writes; noOpFlushes = f - flushes; flushes = f; }
                if (marker == 223) changedFlushes = f - flushes;
            });
        Assert.Equal(0, noOpWrites); Assert.Equal(0, noOpFlushes); Assert.Equal(4, changedFlushes);
        CopperSFS.Check.SfsImageChecker.Check(new MemoryStream(result));
    }
}

public static class TrstNoOpProbe
{
    public static int Execute() => ExecuteFormat(3);
    public static int ExecuteSfs() => ExecuteFormat(2);
    private static int ExecuteFormat(uint format)
    {
        var library = global::Amiga.Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) { CorrectnessProbe.Report(250); return 1; }
        DOS.DOSLibraryBase = library.Value;
        var control = APTR.FromPointer(global::Amiga.Exec.AllocMem(1024,
            global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear));
        if (control.IsNull) { CorrectnessProbe.Report(250); return 2; }
        CorrectnessProbe.Report(200); CorrectnessProbe.Report((int)(control.Raw >> 24));
        CorrectnessProbe.Report((int)(control.Raw >> 16)); CorrectnessProbe.Report((int)(control.Raw >> 8));
        CorrectnessProbe.Report((int)control.Raw);
        if (CorrectnessProbe.Mount(control, format) != 0) return CorrectnessProbe.Failed(control, 3);
        if (format == 2)
        {
            var file = DOS.Open("SEQ0:Payload", DOS.FileMode.NewFile);
            if (!file.HasValue || DOS.Close(file.Value) == 0) return CorrectnessProbe.Failed(control, 8);
        }
        if (DOS.SetProtection("SEQ0:Payload", 32) == 0) return CorrectnessProbe.Failed(control, 4);
        CorrectnessProbe.Report(220);
        if (DOS.SetProtection("SEQ0:Payload", 32) == 0) return CorrectnessProbe.Failed(control, 5);
        CorrectnessProbe.Report(221);
        if (DOS.SetProtection("SEQ0:Payload", 0) == 0) return CorrectnessProbe.Failed(control, 6);
        CorrectnessProbe.Report(223);
        if (format == 2 && DOS.DoPkt(DOS.DeviceProc("SEQ0:"), (int)DosPacketAction.Flush, 0, 0, 0, 0, 0) == 0)
            return CorrectnessProbe.Failed(control, 9);
        CorrectnessProbe.Report(224);
        if (DOS.Inhibit("SEQ0:", 1) == 0) return CorrectnessProbe.Failed(control, 7);
        CorrectnessProbe.Report(210); return 0;
    }
}
