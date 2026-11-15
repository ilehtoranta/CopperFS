using System.Text.Json;
using Amiga;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    [AutomaticTrimTheory]
    [InlineData("ffs")] [InlineData("ofs")]
    public async System.Threading.Tasks.Task UnsupportedDiscardLeavesTheIdleNamespaceGroupOpen(string profile)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_AUTOTRIM_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == profile);
        var initial = AutomaticImage(manifest, profile);
        var root = Path.Combine(Path.GetDirectoryName(path)!, "unsupported-namespace", profile);
        var io = new AutomaticDiscardHook(false, supportsDiscard: false);
        var before = -1;
        var queries = 0;
        var result = await Run(root, profile, "candidate", pair.Candidate, initial, true,
            profile == "ffs" ? "ClassicGroupProbe::FfsIdleDelete" : "ClassicGroupProbe::OfsIdleDelete", deviceHook: io,
            observe: (mark, _, _, _, _) =>
            {
                if (mark == 227) { before = io.WriteCommands; queries = io.ScsiCommands; }
                if (mark != 228) return;
                Assert.True(before >= 0 && io.ScsiCommands > queries);
                Assert.Equal(before, io.WriteCommands);
                Assert.Equal(0, io.Discards);
            });
        Assert.True(before >= 0);
        Assert.DoesNotContain(DosImageInspection.Check(result, false, false).Files, f => f.Path == "/Retire");
    }

    [PerformanceProfileTheory]
    [InlineData("ffs")] [InlineData("ofs")]
    public async System.Threading.Tasks.Task TraceClassicMixedWrites(string profile)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == profile);
        Assert.Equal(pair.OriginalSha256, Hash(File.ReadAllBytes(pair.Original)));
        var initial = AutomaticImage(manifest, profile);
        var root = Path.Combine(Path.GetDirectoryName(path)!, "mixed-write-trace", profile);
        var entry = profile == "ffs" ? "CorrectnessProbe::TraceCommitFfs" : "CorrectnessProbe::TraceCommitOfs";
        foreach (var arm in new[] { "original", "candidate" })
        {
            var handler = arm == "original" ? pair.Original : pair.Candidate;
            var trace = new CommitTrace(null);
            var image = await Run(root, profile, arm, handler, initial, true, entry,
                storage: trace, observe: (mark, cycle, _, _, _) => trace.Mark(mark, cycle));
            DosImageInspection.Check(image, arm == "candidate");
            Assert.False(trace.DirtyAtBoundaryReply[^1]);
            File.WriteAllText(Path.Combine(root, arm, "writes.json"), JsonSerializer.Serialize(new
            { HandlerSha256 = Hash(File.ReadAllBytes(handler)), trace.Flushes, trace.WriteBytes, trace.Events }, Json));
        }
    }

    private sealed class ClassicGroupTrace(byte[] initial) : DurableTrace(initial)
    {
        public readonly byte[] Visible = initial.ToArray();

        public override void Write(FileStream backing, long offset, ReadOnlySpan<byte> source)
        {
            base.Write(backing, offset, source);
            source.CopyTo(Visible.AsSpan(checked((int)offset)));
        }
    }

    private sealed class PartialSectorWrite : WritableHook
    {
        public bool Armed, Failed;
        public long Offset;
        public override void Write(FileStream backing, long offset, ReadOnlySpan<byte> source)
        {
            if (Armed && !Failed && source.Length == 512 && source[0] == 0xDA && source[1] == 0x7E)
            {
                Failed = true;
                Offset = offset;
                base.Write(backing, offset, source[..256]);
                throw new IOException("Injected a partial FFS sector write");
            }
            base.Write(backing, offset, source);
        }
    }

    private sealed class PartialInvalidRoot(long rootOffset) : WritableHook
    {
        public bool Armed, Failed;
        public override void Write(FileStream backing, long offset, ReadOnlySpan<byte> source)
        {
            if (Armed && !Failed && offset == rootOffset && source.Length == 512 &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(source[312..]) == 0)
            {
                Failed = true;
                base.Write(backing, offset, source[..384]);
                throw new IOException("Interrupted invalid-root publication");
            }
            base.Write(backing, offset, source);
        }
    }

    [CorrectnessTheory]
    [InlineData("ffs")] [InlineData("ofs")]
    public async System.Threading.Tasks.Task FirstAppendRootFailureReachesCloseAndFlush(string profile)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == profile);
        var initial = AutomaticImage(manifest, profile);
        var root = Path.Combine(Path.GetDirectoryName(path)!, "partial-append-root", profile);
        var storage = new PartialInvalidRoot(initial.Length / 2);
        await Assert.ThrowsAsync<PowerCut>(() => Run(root, profile, "candidate", pair.Candidate, initial, true,
            profile == "ffs" ? "ClassicGroupProbe::FfsAppendFailure" : "ClassicGroupProbe::OfsAppendFailure",
            storage: storage, observe: (mark, _, _, _, _) =>
            {
                if (mark == 227) storage.Armed = true;
                if (mark == 228) throw new PowerCut();
            }));
        Assert.True(storage.Failed);
        var disk = File.ReadAllBytes(Path.Combine(root, "candidate", "disk.hdf"));
        Assert.Equal(0u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(disk.AsSpan(initial.Length / 2 + 312)));
    }

    [CorrectnessTheory]
    [InlineData(false)] [InlineData(true)]
    public async System.Threading.Tasks.Task PartialFfsSectorWriteFailsTheOpenGroup(bool cached)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var pair = manifest.Profiles.Single(p => p.Name == "ffs");
        var initial = AutomaticImage(manifest, "ffs");
        var root = Path.Combine(Path.GetDirectoryName(path)!, "partial-ffs-write", cached ? "cached" : "cold");
        var storage = new PartialSectorWrite();
        await Assert.ThrowsAsync<PowerCut>(() => Run(root, "ffs", "candidate", pair.Candidate, initial, true,
            cached ? "ClassicGroupProbe::CachedPatchFailure" : "ClassicGroupProbe::ColdPatchFailure", storage: storage,
            observe: (mark, _, _, _, _) =>
            {
                if (mark == 227) storage.Armed = true;
                if (mark == 228) throw new PowerCut();
            }));
        Assert.True(storage.Failed);
        var disk = File.ReadAllBytes(Path.Combine(root, "candidate", "disk.hdf"));
        Assert.Equal(0u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(disk.AsSpan(initial.Length / 2 + 312)));
        Assert.Equal(0xDA, disk[(int)storage.Offset]);
        Assert.Equal(0x11, disk[(int)storage.Offset + 256]);
    }

    [CorrectnessTheory]
    [InlineData("ffs", false)] [InlineData("ofs", false)]
    [InlineData("ffs", true)] [InlineData("ofs", true)]
    public async System.Threading.Tasks.Task NamespaceGroupSurvivesBarrier(string profile, bool inhibit)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var pair = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!.Profiles.Single(p => p.Name == profile);
        var initial = CopperStart.Floppy.Native.Tests.DosDiskImage.Create(profile == "ffs", includeLargeFile: false,
            blockCount: 32768, firstFileBlock: 2, payload: [1, 2, 3]).Bytes;
        var root = Path.Combine(Path.GetDirectoryName(path)!, "grouped-namespace", profile, inhibit ? "inhibit" : "flush");
        var prefix = "ClassicGroupProbe::" + (profile == "ffs" ? "Ffs" : "Ofs") + "Namespace";
        var storage = new ClassicGroupTrace(initial);
        var before = -1;
        await Assert.ThrowsAsync<PowerCut>(() => Run(root, profile, "candidate", pair.Candidate, initial, true,
            prefix + (inhibit ? "Handoff" : "Flush"), storage: storage, observe: (mark, _, _, _, flushes) =>
            {
                if (mark == 227)
                {
                    before = flushes;
                    Assert.Equal(0u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
                        storage.Visible.AsSpan(initial.Length / 2 + 312, 4)));
                }
                if (mark == 228)
                {
                    Assert.True(before >= 0 && flushes > before);
                    throw new PowerCut();
                }
            }));
        var disk = File.ReadAllBytes(Path.Combine(root, "candidate", "disk.hdf"));
        var files = DosImageInspection.Check(disk, false, false).Files;
        Assert.Equal(Hash([0x35]), files.Single(file => file.Path == "/Paths/Leaf0").Sha256);
        Assert.Equal(Hash([0x72]), files.Single(file => file.Path == "/Paths/Leaf16").Sha256);
        Assert.DoesNotContain(files, file => file.Path.EndsWith("/Child", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(pair.OriginalSha256, Hash(File.ReadAllBytes(pair.Original)));
        await Run(root, profile, "original", pair.Original, disk, false, prefix + "Read");
    }

    [CorrectnessTheory]
    [InlineData("ffs", "Close")] [InlineData("ofs", "Close")]
    [InlineData("ffs", "SiblingClose")] [InlineData("ofs", "SiblingClose")]
    [InlineData("ffs", "Flush")] [InlineData("ofs", "Flush")]
    [InlineData("ffs", "Handoff")] [InlineData("ofs", "Handoff")]
    public async System.Threading.Tasks.Task GroupedWritesPublishAtCloseAndSurviveBarriers(string profile, string boundary)
    {
        var manifestPath = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var pair = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath))!.Profiles.Single(p => p.Name == profile);
        var initial = CopperStart.Floppy.Native.Tests.DosDiskImage.Create(profile == "ffs", includeLargeFile: false,
            blockCount: 32768, firstFileBlock: 2, payload: [1, 2, 3]).Bytes;
        var root = Path.Combine(Path.GetDirectoryName(manifestPath)!, "grouped-write", profile, boundary);
        var prefix = "ClassicGroupProbe::" + (profile == "ffs" ? "Ffs" : "Ofs");
        var storage = new ClassicGroupTrace(initial);
        var before = -1;
        var close = boundary is "Close" or "SiblingClose";
        // Interrupt immediately after the successful DOS reply, before inhibit,
        // closing other handles or any final emulator flush can publish more.
        await Assert.ThrowsAsync<PowerCut>(() => Run(root, profile, "candidate", pair.Candidate, initial, true,
            prefix + boundary, storage: storage, observe: (mark, _, _, _, flushes) =>
            {
                if (mark == 227) before = flushes;
                if (mark == 228)
                {
                    Assert.True(before >= 0);
                    if (close) Assert.Equal(before, flushes);
                    else Assert.True(flushes > before);
                    throw new PowerCut();
                }
            }));
        var image = close ? storage.Visible : File.ReadAllBytes(Path.Combine(root, "candidate", "disk.hdf"));
        var files = DosImageInspection.Check(image, false, false).Files;
        var file = files.Single(f => f.Path == "/Group");
        if (boundary == "SiblingClose") Assert.Equal(Hash([0xBE]), files.Single(f => f.Path == "/Side").Sha256);
        var expected = Enumerable.Range(0, 2 * 1531 + 9).Select(i => (byte)(i % 1531)).ToArray();
        expected[77] = 0xBE;
        expected[3062] = 0xBE;
        Assert.Equal(Hash(expected), file.Sha256);
        Assert.Equal(pair.OriginalSha256, Hash(File.ReadAllBytes(pair.Original)));
        await Run(root, profile, "original", pair.Original, image, false, prefix + "Read");
    }
}

public static class ClassicGroupProbe
{
    public static int FfsIdleDelete() => IdleDelete(1);
    public static int OfsIdleDelete() => IdleDelete(0);

    private static int IdleDelete(uint kind)
    {
        var lib = Exec.OpenLibrary("dos.library", 37);
        if (!lib.HasValue) { CorrectnessProbe.Report(250); return 1; }
        DOS.DOSLibraryBase = lib.Value;
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)), 184, uint.MaxValue);
        var control = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        if (control.IsNull) { CorrectnessProbe.Report(250); return 2; }
        CorrectnessProbe.Report(200);
        for (var shift = 24; shift >= 0; shift -= 8) CorrectnessProbe.Report((int)(control.Raw >> shift));
        if (CorrectnessProbe.Mount(control, kind) != 0) return CorrectnessProbe.Failed(control, 3);
        var file = DOS.Open("SEQ0:Retire", DOS.FileMode.NewFile);
        if (!file.HasValue || DOS.Write(file.Value, control, 512) != 512 || DOS.Close(file.Value) == 0 ||
            DOS.DeleteFile("SEQ0:Retire") == 0) return CorrectnessProbe.Failed(control, 4);
        CorrectnessProbe.Report(227);
        DOS.Delay(350);
        CorrectnessProbe.Report(228);
        if (DOS.DoPkt(DOS.DeviceProc("SEQ0:"), (int)DosPacketAction.Flush, 0, 0, 0, 0, 0) == 0)
            return CorrectnessProbe.Failed(control, 5);
        CorrectnessProbe.Report(210);
        return 0;
    }

    public static int CachedPatchFailure() => PatchFailure(true);
    public static int ColdPatchFailure() => PatchFailure(false);

    public static int FfsAppendFailure() => AppendFailure(1);
    public static int OfsAppendFailure() => AppendFailure(0);

    private static int AppendFailure(uint kind)
    {
        var lib = Exec.OpenLibrary("dos.library", 37);
        if (!lib.HasValue) { CorrectnessProbe.Report(250); return 1; }
        DOS.DOSLibraryBase = lib.Value;
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)), 184, uint.MaxValue);
        var control = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        if (control.IsNull) { CorrectnessProbe.Report(250); return 2; }
        CorrectnessProbe.Report(200);
        for (var shift = 24; shift >= 0; shift -= 8) CorrectnessProbe.Report((int)(control.Raw >> shift));
        if (CorrectnessProbe.Mount(control, kind) != 0) return CorrectnessProbe.Failed(control, 3);
        var data = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        if (data.IsNull) return CorrectnessProbe.Failed(control, 4);
        var port = DOS.DeviceProc("SEQ0:");
        var file = DOS.Open("SEQ0:Existing", DOS.FileMode.NewFile);
        if (!file.HasValue || DOS.Write(file.Value, data, 1024) != 1024 || DOS.Close(file.Value) == 0 ||
            DOS.DoPkt(port, (int)DosPacketAction.Flush, 0, 0, 0, 0, 0) == 0) return CorrectnessProbe.Failed(control, 5);
        file = DOS.Open("SEQ0:Existing", DOS.FileMode.ReadWrite);
        if (!file.HasValue || DOS.Seek(file.Value, 0, 1) < 0) return CorrectnessProbe.Failed(control, 6);
        CorrectnessProbe.Report(227);
        if (DOS.Write(file.Value, data, 31) != -1 || DOS.IoErr() != DOS.Error.DiskNotValidated ||
            DOS.Close(file.Value) != 0 ||
            DOS.DoPkt(port, (int)DosPacketAction.Flush, 0, 0, 0, 0, 0) != 0) return CorrectnessProbe.Failed(control, 7);
        CorrectnessProbe.Report(228);
        CorrectnessProbe.Report(210);
        return 0;
    }

    private static int PatchFailure(bool cached)
    {
        var lib = Exec.OpenLibrary("dos.library", 37);
        if (!lib.HasValue) { CorrectnessProbe.Report(250); return 1; }
        DOS.DOSLibraryBase = lib.Value;
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)), 184, uint.MaxValue);
        var control = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        if (control.IsNull) { CorrectnessProbe.Report(250); return 2; }
        CorrectnessProbe.Report(200);
        for (var shift = 24; shift >= 0; shift -= 8) CorrectnessProbe.Report((int)(control.Raw >> shift));
        if (CorrectnessProbe.Mount(control, 1) != 0) return CorrectnessProbe.Failed(control, 3);
        var data = APTR.FromPointer(Exec.AllocMem(8192, Exec.MemoryFlags.Public));
        if (data.IsNull) return CorrectnessProbe.Failed(control, 4);
        for (var i = 0; i < 8192; i++) APTR.WriteUInt8(data, i, 0x11);
        var file = DOS.Open("SEQ0:Patch", DOS.FileMode.NewFile);
        if (!file.HasValue || DOS.Write(file.Value, data, 8192) != 8192 ||
            DOS.Seek(file.Value, 0, -1) < 0 || DOS.Write(file.Value, data, 511) != 511)
            return CorrectnessProbe.Failed(control, 5);
        if (DOS.Seek(file.Value, cached ? 0 : 4096, -1) < 0) return CorrectnessProbe.Failed(control, 6);
        APTR.WriteUInt8(data, 0, 0xDA); APTR.WriteUInt8(data, 1, 0x7E);
        for (var i = 2; i < 511; i++) APTR.WriteUInt8(data, i, 0xEF);
        CorrectnessProbe.Report(227);
        if (DOS.Write(file.Value, data, 511) != -1 || DOS.IoErr() != DOS.Error.DiskNotValidated ||
            DOS.Close(file.Value) != 0) return CorrectnessProbe.Failed(control, 7);
        CorrectnessProbe.Report(228);
        CorrectnessProbe.Report(210);
        return 0;
    }

    public static int FfsNamespaceFlush() => Namespace(1, 0);
    public static int OfsNamespaceFlush() => Namespace(0, 0);
    public static int FfsNamespaceHandoff() => Namespace(1, 1);
    public static int OfsNamespaceHandoff() => Namespace(0, 1);
    public static int FfsNamespaceRead() => Namespace(1, 2);
    public static int OfsNamespaceRead() => Namespace(0, 2);

    private static int Namespace(uint kind, int boundary)
    {
        var lib = Exec.OpenLibrary("dos.library", 37);
        if (!lib.HasValue) { CorrectnessProbe.Report(250); return 1; }
        DOS.DOSLibraryBase = lib.Value;
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)), 184, uint.MaxValue);
        var control = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        if (control.IsNull) { CorrectnessProbe.Report(250); return 2; }
        CorrectnessProbe.Report(200);
        for (var shift = 24; shift >= 0; shift -= 8) CorrectnessProbe.Report((int)(control.Raw >> shift));
        if (CorrectnessProbe.Mount(control, kind) != 0) return CorrectnessProbe.Failed(control, 3);
        var port = DOS.DeviceProc("SEQ0:");
        if (boundary == 2)
        {
            var visible = DOS.Lock("SEQ0:After", DOS.LockMode.Shared);
            if (!visible.HasValue) return CorrectnessProbe.Failed(control, 4);
            var fib = APTR.FromPointer(control.Raw + 400);
            if (DOS.Examine(visible.Value, fib) == 0 || APTR.ReadUInt32(fib, FileInfoBlock.ProtectionOffset) != 32)
                return CorrectnessProbe.Failed(control, 5);
            DOS.UnLock(visible.Value);
            if (DOS.Lock("SEQ0:Before", DOS.LockMode.Shared).HasValue) return CorrectnessProbe.Failed(control, 6);
        }
        else
        {
            var paths = DOS.CreateDir("SEQ0:Paths");
            if (!paths.HasValue) return CorrectnessProbe.Failed(control, 12);
            DOS.UnLock(paths.Value);
            var data = APTR.FromPointer(control.Raw + 400);
            // Leaf0 and Leaf16 share one 72-entry hash bucket. Test a terminal
            // miss there, case-folded lookup, and a miss in an intermediate directory.
            var leaf = DOS.Open("SEQ0:Paths/Leaf0", DOS.FileMode.NewFile);
            APTR.WriteUInt8(data, 0, 0x35);
            if (!leaf.HasValue || DOS.Write(leaf.Value, data, 1) != 1 || DOS.Close(leaf.Value) == 0)
                return CorrectnessProbe.Failed(control, 13);
            leaf = DOS.Open("SEQ0:Paths/Leaf16", DOS.FileMode.NewFile);
            APTR.WriteUInt8(data, 0, 0x72);
            if (!leaf.HasValue || DOS.Write(leaf.Value, data, 1) != 1 || DOS.Close(leaf.Value) == 0)
                return CorrectnessProbe.Failed(control, 14);
            leaf = DOS.Open("SEQ0:paths/leaf0", DOS.FileMode.OldFile);
            if (!leaf.HasValue || DOS.Read(leaf.Value, data, 1) != 1 || APTR.ReadUInt8(data, 0) != 0x35 ||
                DOS.Close(leaf.Value) == 0) return CorrectnessProbe.Failed(control, 15);
            if (DOS.Open("SEQ0:Paths/Missing/Child", DOS.FileMode.NewFile).HasValue ||
                (DOS.Error)DOS.IoErr() != DOS.Error.ObjectNotFound) return CorrectnessProbe.Failed(control, 16);
            var dir = DOS.CreateDir("SEQ0:Before");
            if (!dir.HasValue) return CorrectnessProbe.Failed(control, 7);
            DOS.UnLock(dir.Value);
            if (DOS.Rename("SEQ0:Before", "SEQ0:After") == 0 || DOS.SetProtection("SEQ0:After", 32) == 0)
                return CorrectnessProbe.Failed(control, 8);
            dir = DOS.Lock("SEQ0:After", DOS.LockMode.Shared);
            if (!dir.HasValue) return CorrectnessProbe.Failed(control, 10);
            var fib = APTR.FromPointer(control.Raw + 400);
            if (DOS.Examine(dir.Value, fib) == 0 || APTR.ReadUInt32(fib, FileInfoBlock.ProtectionOffset) != 32)
                return CorrectnessProbe.Failed(control, 11);
            DOS.UnLock(dir.Value);
            CorrectnessProbe.Report(227);
            if ((boundary == 1 ? DOS.Inhibit("SEQ0:", 1) :
                DOS.DoPkt(port, (int)DosPacketAction.Flush, 0, 0, 0, 0, 0)) == 0)
                return CorrectnessProbe.Failed(control, 9);
            CorrectnessProbe.Report(228);
        }
        CorrectnessProbe.Report(210);
        return 0;
    }

    public static int FfsClose() => Run(1, 0);
    public static int OfsClose() => Run(0, 0);
    public static int FfsSiblingClose() => Run(1, 4);
    public static int OfsSiblingClose() => Run(0, 4);
    public static int FfsFlush() => Run(1, 1);
    public static int OfsFlush() => Run(0, 1);
    public static int FfsHandoff() => Run(1, 3);
    public static int OfsHandoff() => Run(0, 3);
    public static int FfsRead() => Run(1, 2);
    public static int OfsRead() => Run(0, 2);

    private static int Run(uint kind, int boundary)
    {
        var lib = Exec.OpenLibrary("dos.library", 37);
        if (!lib.HasValue) { CorrectnessProbe.Report(250); return 1; }
        DOS.DOSLibraryBase = lib.Value;
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)), 184, uint.MaxValue);
        var control = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var data = APTR.FromPointer(Exec.AllocMem(3072, Exec.MemoryFlags.Public));
        if (control.IsNull || data.IsNull) { CorrectnessProbe.Report(250); return 2; }
        CorrectnessProbe.Report(200);
        for (var shift = 24; shift >= 0; shift -= 8) CorrectnessProbe.Report((int)(control.Raw >> shift));
        if (CorrectnessProbe.Mount(control, kind) != 0) return CorrectnessProbe.Failed(control, 3);
        var file = DOS.Open("SEQ0:Group", boundary == 2 ? DOS.FileMode.OldFile : DOS.FileMode.NewFile);
        if (!file.HasValue) return CorrectnessProbe.Failed(control, 4);
        if (boundary == 2)
        {
            if (DOS.Read(file.Value, data, 3072) != 3071) return CorrectnessProbe.Failed(control, 5);
            for (var i = 0; i < 3071; i++)
                if (APTR.ReadUInt8(data, i) != (i == 77 || i == 3062 ? 0xBE : (byte)(i % 1531))) return CorrectnessProbe.Failed(control, 6);
            if (DOS.Close(file.Value) == 0) return CorrectnessProbe.Failed(control, 7);
        }
        else
        {
            for (var i = 0; i < 1531; i++) APTR.WriteUInt8(data, i, (byte)i);
            if (DOS.Write(file.Value, data, 1531) != 1531 || DOS.Write(file.Value, data, 1531) != 1531 ||
                DOS.Seek(file.Value, 77, -1) != 3062) return CorrectnessProbe.Failed(control, 8);
            APTR.WriteUInt8(data, 0, 0xBE);
            if (DOS.Write(file.Value, data, 1) != 1 || DOS.Seek(file.Value, 0, 1) != 78 ||
                DOS.Write(file.Value, data, 9) != 9) return CorrectnessProbe.Failed(control, 9);
            if (boundary == 4)
            {
                file = DOS.Open("SEQ0:Side", DOS.FileMode.NewFile);
                if (!file.HasValue || DOS.Write(file.Value, data, 1) != 1)
                    return CorrectnessProbe.Failed(control, 13);
            }
            CorrectnessProbe.Report(227);
            if (boundary == 0 || boundary == 4)
            {
                if (DOS.Close(file.Value) == 0) return CorrectnessProbe.Failed(control, 10);
            }
            else if (boundary == 1)
            {
                if (DOS.DoPkt(DOS.DeviceProc("SEQ0:"), (int)DosPacketAction.Flush, 0, 0, 0, 0, 0) == 0)
                    return CorrectnessProbe.Failed(control, 11);
            }
            else if (DOS.Inhibit("SEQ0:", 1) == 0) return CorrectnessProbe.Failed(control, 12);
            CorrectnessProbe.Report(228);
        }
        CorrectnessProbe.Report(210);
        return 0;
    }
}
