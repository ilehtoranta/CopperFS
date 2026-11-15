using Amiga;
using Task = System.Threading.Tasks.Task;

namespace CopperFS.Kickstart.Tests;

public sealed class GroupedTrstTheoryAttribute : TheoryAttribute
{
    public GroupedTrstTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("COPPERFS_SFS2_GROUPED") != "1" ||
            !File.Exists(Environment.GetEnvironmentVariable("COPPERSHARP_KICKSTART31_ROM")) ||
            !File.Exists(Environment.GetEnvironmentVariable("COPPERFS_SFS2_TRST_HANDLER")) ||
            !File.Exists(Environment.GetEnvironmentVariable("COPPERFS_ORIGINAL_SFS")))
            Skip = "Supply ROM, grouped TRST handler, original SFS and COPPERFS_SFS2_GROUPED=1.";
    }
}

public sealed partial class OriginalHandlerCorrectnessTests
{
    [GroupedTrstTheory]
    [InlineData("Group")]
    public Task GroupedTrstRecoversWithOriginalSfs(string operation) => NativeSfs2RecoveryMatrix(operation);

    [GroupedTrstTheory]
    [InlineData(131072)]
    public async Task GroupedTrstPressureCommitsAcceptedViewBeforeStrictRetry(int bytes)
    {
        var candidate = Environment.GetEnvironmentVariable("COPPERFS_SFS2_TRST_HANDLER")!;
        var root = Path.Combine(Path.GetDirectoryName(candidate)!, "group-pressure");
        var start = 0; var commits = -1;
        var result = await Run(root, "sfs2-trst", "candidate", candidate, ReadMedia.Sfs2(), true, "TrstGroupProbe::Pressure",
            observe: (marker, _, _, _, flushes) =>
            { if (marker == 219) start = flushes; if (marker == 220) commits = flushes - start; });
        // Owned metadata-only inline commit (three device flushes), then the
        // strict capacity retry (four). The checked group uses four for both.
        Assert.Equal(Environment.GetEnvironmentVariable("COPPERFS_SFS2_OWNED") == "1" ? 7 : 8, commits);
        var report = CopperSFS.Check.SfsImageChecker.Check(new MemoryStream(result));
        var payload = report.Entries.Single(e => e.Path == "/Payload");
        var expected = ReadMedia.Payload.ToArray();
        for (var i = 0; i < bytes; i++) expected[i] = (byte)(i * 17 + 31);
        Assert.Equal(Hash(expected), payload.Sha256); Assert.Equal("accepted", payload.Comment);
    }

    [GroupedTrstTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task GroupedTrstPacketsWaitForDurabilityBoundary(bool timer)
    {
        var candidate = Environment.GetEnvironmentVariable("COPPERFS_SFS2_TRST_HANDLER")!;
        var root = Path.Combine(Path.GetDirectoryName(candidate)!, "group-boundary", timer ? "timer" : "inhibit");
        int writes = 0, flushes = 0, pendingWrites = -1, pendingFlushes = -1, commitFlushes = -1;
        var result = await Run(root, "sfs2-trst", "candidate", candidate, ReadMedia.Sfs2(), true,
            timer ? "TrstGroupProbe::Timer" : "TrstGroupProbe::Inhibit", observe: (marker, _, _, w, f) =>
            {
                if (marker == 219) { writes = w; flushes = f; }
                if (marker == 220) { pendingWrites = w - writes; pendingFlushes = f - flushes; }
                if (marker == 223) commitFlushes = f - flushes;
            });
        Assert.Equal(0, pendingWrites); Assert.Equal(0, pendingFlushes);
        // Four ordered stages. An inline metadata-only commit has no data to
        // flush at stage one, so the native platform issues three commands.
        Assert.Equal(Environment.GetEnvironmentVariable("COPPERFS_SFS2_OWNED") == "1" ? 3 : 4, commitFlushes);
        CopperSFS.Check.SfsImageChecker.Check(new MemoryStream(result));
    }
}

public static class TrstGroupProbe
{
    public static int Pressure()
    {
        var library = global::Amiga.Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) { CorrectnessProbe.Report(250); return 1; }
        DOS.DOSLibraryBase = library.Value;
        var control = APTR.FromPointer(global::Amiga.Exec.AllocMem(1024,
            global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear));
        var data = APTR.FromPointer(global::Amiga.Exec.AllocMem(131072, global::Amiga.Exec.MemoryFlags.Public));
        if (control.IsNull || data.IsNull) { CorrectnessProbe.Report(250); return 2; }
        CorrectnessProbe.Report(200); CorrectnessProbe.Report((int)(control.Raw >> 24));
        CorrectnessProbe.Report((int)(control.Raw >> 16)); CorrectnessProbe.Report((int)(control.Raw >> 8));
        CorrectnessProbe.Report((int)control.Raw);
        if (CorrectnessProbe.Mount(control, 3) != 0) return CorrectnessProbe.Failed(control, 3);
        for (var i = 0; i < 131072; i++) APTR.WriteUInt8(data, i, (byte)(i * 17 + 31));
        var file = DOS.Open("SEQ0:Payload", DOS.FileMode.OldFile);
        CorrectnessProbe.Report(219);
        if (!file.HasValue || DOS.SetComment("SEQ0:Payload", "accepted") == 0) return CorrectnessProbe.Failed(control, 4);
        if (DOS.Write(file.Value, data, 131072) != 131072) return CorrectnessProbe.Failed(control, 5);
        CorrectnessProbe.Report(220);
        if (DOS.Close(file.Value) == 0 || DOS.Inhibit("SEQ0:", 1) == 0)
            return CorrectnessProbe.Failed(control, 5);
        CorrectnessProbe.Report(210); return 0;
    }

    public static int Timer() => Execute(true);
    public static int Inhibit() => Execute(false);
    private static int Execute(bool timer)
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
        if (CorrectnessProbe.Mount(control, 3) != 0) return CorrectnessProbe.Failed(control, 3);
        CorrectnessProbe.Report(219);
        if (DOS.SetProtection("SEQ0:Payload", 32) == 0 || DOS.SetProtection("SEQ0:Payload", 64) == 0)
            return CorrectnessProbe.Failed(control, 4);
        CorrectnessProbe.Report(220);
        if (timer) DOS.Delay(350);
        else if (DOS.Inhibit("SEQ0:", 1) == 0) return CorrectnessProbe.Failed(control, 5);
        CorrectnessProbe.Report(223);
        if (timer && DOS.Inhibit("SEQ0:", 1) == 0) return CorrectnessProbe.Failed(control, 6);
        CorrectnessProbe.Report(210); return 0;
    }
}
