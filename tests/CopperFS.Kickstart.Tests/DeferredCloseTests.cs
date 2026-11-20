using System.Text.Json;
using Amiga;
using CopperSFS.Check;
using Task = System.Threading.Tasks.Task;

namespace CopperFS.Kickstart.Tests;

public sealed class DeferredCloseTheoryAttribute : TheoryAttribute
{
    public DeferredCloseTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("COPPERFS_SFS2_DEFERRED_CLOSE") != "1" ||
            !File.Exists(Environment.GetEnvironmentVariable("COPPERSHARP_KICKSTART31_ROM")) ||
            !File.Exists(Environment.GetEnvironmentVariable("COPPERFS_SFS2_TRST_HANDLER")) ||
            !File.Exists(Environment.GetEnvironmentVariable("COPPERFS_ORIGINAL_SFS")))
            Skip = "Requires deferred-close SFS2 candidate, original SFS and licensed ROM.";
    }
}

public sealed partial class OriginalHandlerCorrectnessTests
{
    private static void AssertCrossClose(ImageReport report)
    {
        Assert.DoesNotContain(report.Entries, e => e.Path is "/Payload" or "/Scratch");
        var payload = report.Entries.Single(e => e.Path == "/Renamed");
        var expected = new byte[ReadMedia.Payload.Length + 7]; ReadMedia.Payload.CopyTo(expected, 0);
        expected[0] = expected[1] = expected[2] = 93; expected.AsSpan(ReadMedia.Payload.Length).Fill(77);
        Assert.Equal(Hash(expected), payload.Sha256); Assert.Equal("closed-group", payload.Comment);
        Assert.Equal(Hash(Enumerable.Repeat((byte)23, 1024).ToArray()),
            report.Entries.Single(e => e.Path == "/Replacement").Sha256);
    }

    [DeferredCloseTheory] [InlineData("CrossClose")]
    public Task DeferredCloseGroupRecoversWithOriginalSfs(string operation) => NativeSfs2RecoveryMatrix(operation);

    [DeferredCloseTheory] [InlineData("Trim")]
    public async Task ManualTrimCommitsClosedTransactionBeforeValidation(string boundary)
    {
        var candidate = Environment.GetEnvironmentVariable("COPPERFS_SFS2_TRST_HANDLER")!;
        var root = Path.Combine(Path.GetDirectoryName(candidate)!, "closed-manual-trim");
        var hook = new AutomaticDiscardHook(false); int started = -1, closed = -1, committed = -1;
        var result = await Run(root, "sfs2-trst", "candidate", candidate, ReadMedia.Sfs2(), true,
            "DeferredCloseProbe::" + boundary, deviceHook: hook, observe: (marker, _, _, _, _) =>
            {
                if (marker == 219) started = hook.Flushes;
                if (marker == 220) closed = hook.Flushes;
                if (marker == 223) committed = hook.Flushes;
            });
        Assert.True(started >= 0); Assert.Equal(started, closed); Assert.Equal(4, committed - closed);
        Assert.Equal(0, hook.Discards); // DryRun keeps the manual packet ABI.
        AssertCrossClose(SfsImageChecker.Check(new MemoryStream(result)));
        await Run(root, "sfs2-trst", "original", Environment.GetEnvironmentVariable("COPPERFS_ORIGINAL_SFS")!,
            result, true, "Sfs2RecoveryProbe::Recover");
    }

    [DeferredCloseTheory]
    [InlineData("Flush", false)] [InlineData("Timer", false)] [InlineData("Inhibit", false)]
    [InlineData("Flush", true)] [InlineData("Timer", true)] [InlineData("Inhibit", true)]
    public async Task DeferredClosesStayVisibleUntilDurabilityBoundary(string boundary, bool cutBeforeBoundary)
    {
        var candidate = Environment.GetEnvironmentVariable("COPPERFS_SFS2_TRST_HANDLER")!;
        var original = Environment.GetEnvironmentVariable("COPPERFS_ORIGINAL_SFS")!;
        var initial = ReadMedia.Sfs2(); var trace = new DurableTrace(initial);
        var root = Path.Combine(Path.GetDirectoryName(candidate)!, "closed-boundary", boundary, cutBeforeBoundary.ToString());
        var initialFlushes = -1; var closedFlushes = -1; var finalFlushes = -1;
        async Task<byte[]> Execute() => await Run(root, "sfs2-trst", "candidate", candidate, initial, true,
            "DeferredCloseProbe::" + boundary, storage: trace, observe: (marker, _, _, _, flushes) =>
            {
                if (marker == 219) initialFlushes = flushes;
                if (marker == 220)
                {
                    closedFlushes = flushes;
                    if (cutBeforeBoundary) throw new PowerCut();
                }
                if (marker == 223) finalFlushes = flushes;
            });
        byte[] image;
        if (cutBeforeBoundary)
        {
            await Assert.ThrowsAsync<PowerCut>(Execute);
            image = File.ReadAllBytes(Path.Combine(root, "candidate", "disk.hdf"));
        }
        else image = await Execute();
        Assert.True(initialFlushes >= 0); Assert.Equal(initialFlushes, closedFlushes);
        if (!cutBeforeBoundary)
        {
            Assert.Equal(4, finalFlushes - initialFlushes);
            AssertCrossClose(SfsImageChecker.Check(new MemoryStream(image)));
        }
        foreach (var handler in new[] { candidate, original })
        {
            var recovered = await Run(root, "sfs2-trst", handler == candidate ? "candidate-restart" : "original-restart",
                handler, image, true, "Sfs2RecoveryProbe::Recover");
            if (cutBeforeBoundary)
                Assert.Equal(JsonSerializer.Serialize(SfsImageChecker.Check(new MemoryStream(initial))),
                    JsonSerializer.Serialize(SfsImageChecker.Check(new MemoryStream(recovered))));
            else AssertCrossClose(SfsImageChecker.Check(new MemoryStream(recovered)));
        }
    }

    private sealed class DeferredFlushFailure(byte[] initial, int failAt) : DurableTrace(initial)
    {
        public bool Armed, ClearFailure;
        public int Attempts, Failures;
        public override void Flush(FileStream backing)
        {
            if (Armed && !ClearFailure && ++Attempts >= failAt)
            { Failures++; throw new IOException("Injected deferred commit barrier failure"); }
            base.Flush(backing);
        }
    }

    [DeferredCloseTheory]
    [InlineData(false, 1)] [InlineData(false, 2)] [InlineData(false, 3)] [InlineData(false, 4)]
    [InlineData(true, 1)] [InlineData(true, 2)] [InlineData(true, 3)] [InlineData(true, 4)]
    public async Task DeferredCommitFailureBlocksMutationUntilRecovery(bool timer, int barrier)
    {
        var candidate = Environment.GetEnvironmentVariable("COPPERFS_SFS2_TRST_HANDLER")!;
        var original = Environment.GetEnvironmentVariable("COPPERFS_ORIGINAL_SFS")!;
        var initial = ReadMedia.Sfs2(); var trace = new DeferredFlushFailure(initial, barrier);
        var root = Path.Combine(Path.GetDirectoryName(candidate)!, "closed-failure", timer.ToString(), barrier.ToString());
        var result = await Run(root, "sfs2-trst", "candidate", candidate, initial, true,
            timer ? "DeferredCloseProbe::TimerFailure" : "DeferredCloseProbe::FlushFailure", storage: trace,
            observe: (marker, _, _, _, _) =>
            {
                if (marker == 220) trace.Armed = true;
                if (marker == 225) trace.ClearFailure = true;
            });
        Assert.True(trace.Failures >= 2); // Original failure and refused mutation/recovery attempt.
        var report = SfsImageChecker.Check(new MemoryStream(result));
        if (report.Entries.Any(e => e.Path == "/Renamed")) AssertCrossClose(report);
        else
        {
            var expected = SfsImageChecker.Check(new MemoryStream(initial));
            if (Environment.GetEnvironmentVariable("COPPERFS_TEST_IN_PLACE") == "1")
            {
                // Once the fault is cleared, Flush can persist the existing
                // data written before the unpublished metadata was discarded.
                Assert.Equal(1, barrier);
                var payload = ReadMedia.Payload.ToArray();
                payload.AsSpan(0, 3).Fill(93);
                var hash = Hash(payload);
                expected = expected with
                {
                    Entries = expected.Entries.Select(entry =>
                        entry.Path == "/Payload" ? entry with { Sha256 = hash } : entry).ToArray()
                };
            }
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(report));
        }
        await Run(root, "sfs2-trst", "original-restart", original, result, true, "Sfs2RecoveryProbe::Recover");
    }
}

public static class DeferredCloseProbe
{
    public static int Flush() => Run(0, false);
    public static int Timer() => Run(1, false);
    public static int Inhibit() => Run(2, false);
    public static int Trim() => Run(3, false);
    public static int FlushFailure() => Run(0, true);
    public static int TimerFailure() => Run(1, true);

    private static int Run(int boundary, bool failure)
    {
        var library = global::Amiga.Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) { CorrectnessProbe.Report(250); return 1; }
        DOS.DOSLibraryBase = library.Value;
        // Fault qualification must return DOS errors rather than open a
        // requester which has no interactive user in the boot fixture.
        var process = global::Amiga.Exec.FindTask(CString.FromPointer(0));
        APTR.WriteUInt32(process, 184, uint.MaxValue);
        var control = APTR.FromPointer(global::Amiga.Exec.AllocMem(2048,
            global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear));
        if (control.IsNull || CorrectnessProbe.Mount(control, 3) != 0) { CorrectnessProbe.Report(250); return 2; }
        var port = DOS.DeviceProc("SEQ0:");
        if (port.IsNull) return CorrectnessProbe.Failed(control, 3);
        CorrectnessProbe.Report(219);
        if (!Mutate(control)) return CorrectnessProbe.Failed(control, 4);
        CorrectnessProbe.Report(220);
        var ok = true;
        if (boundary == 0) ok = DOS.DoPkt(port, (int)DosPacketAction.Flush, 0, 0, 0, 0, 0) != 0;
        if (boundary == 1) DOS.Delay(350);
        if (boundary == 2) ok = DOS.Inhibit("SEQ0:", 1) != 0;
        if (boundary == 3)
        {
            var record = APTR.FromPointer(control.Raw + 512);
            APTR.WriteUInt32(record, 0, 32); APTR.WriteUInt32(record, 4, 1); APTR.WriteUInt32(record, 8, 1);
            ok = DOS.DoPkt(port, 0x43535452, unchecked((int)record.Raw), 0, 0, 0, 0) != 0;
        }
        if (failure)
        {
            if (boundary == 0 && (ok || DOS.IoErr() != DOS.Error.DiskNotValidated)) return CorrectnessProbe.Failed(control, 5);
            // Failed asynchronous commits must also block the next mutation.
            if (DOS.SetProtection("SEQ0:Renamed", 32) != 0 || DOS.IoErr() != DOS.Error.DiskNotValidated)
                return CorrectnessProbe.Failed(control, 6);
            CorrectnessProbe.Report(225); // Host removes the persistent I/O fault.
            if (DOS.DoPkt(port, (int)DosPacketAction.Flush, 0, 0, 0, 0, 0) == 0) return CorrectnessProbe.Failed(control, 7);
        }
        else if (!ok) return CorrectnessProbe.Failed(control, 8);
        CorrectnessProbe.Report(223);
        if (boundary != 2 && DOS.Inhibit("SEQ0:", 1) == 0) return CorrectnessProbe.Failed(control, 9);
        CorrectnessProbe.Report(210); return 0;
    }

    internal static bool Mutate(APTR control)
    {
        var data = APTR.FromPointer(control.Raw + 1024);
        var file = DOS.Open("SEQ0:Payload", DOS.FileMode.OldFile);
        if (!file.HasValue) return false;
        for (var i = 0; i < 3; i++) APTR.WriteUInt8(data, i, 93);
        if (DOS.Write(file.Value, data, 3) != 3 || DOS.Close(file.Value) == 0) return false;
        file = DOS.Open("SEQ0:Payload", DOS.FileMode.OldFile);
        if (!file.HasValue || DOS.Read(file.Value, data, 3) != 3) return false;
        for (var i = 0; i < 3; i++) if (APTR.ReadUInt8(data, i) != 93) return false;
        for (var i = 0; i < 1024; i++) APTR.WriteUInt8(data, i, 77);
        if (DOS.Seek(file.Value, 0, 1) != 3 || DOS.Write(file.Value, data, 513) != 513 || DOS.Close(file.Value) == 0) return false;
        file = DOS.Open("SEQ0:Payload", DOS.FileMode.OldFile);
        if (!file.HasValue || DOS.SetFileSize(file.Value, 1048583, -1) != 1048583 || DOS.Close(file.Value) == 0) return false;
        file = DOS.Open("SEQ0:Scratch", DOS.FileMode.NewFile);
        if (!file.HasValue || DOS.Write(file.Value, data, 1024) != 1024 || DOS.Close(file.Value) == 0 || DOS.DeleteFile("SEQ0:Scratch") == 0) return false;
        for (var i = 0; i < 1024; i++) APTR.WriteUInt8(data, i, 23);
        file = DOS.Open("SEQ0:Replacement", DOS.FileMode.NewFile);
        if (!file.HasValue || DOS.Write(file.Value, data, 1024) != 1024 || DOS.Close(file.Value) == 0) return false;
        return DOS.Rename("SEQ0:Payload", "SEQ0:Renamed") != 0 && DOS.SetComment("SEQ0:Renamed", "closed-group") != 0;
    }
}
