using System.Buffers.Binary;
using Amiga;
using CopperSFS.Check;
using Task = System.Threading.Tasks.Task;

namespace CopperFS.Kickstart.Tests;

// ReadBase and the original-handler capture come from research commit 4a902a1.
public static class OriginalSfs2RedoProbe
{
    public static int Run()
    {
        var dos = global::Amiga.Exec.OpenLibrary("dos.library", 37);
        if (!dos.HasValue) { CorrectnessProbe.Report(250); return 1; }
        DOS.DOSLibraryBase = dos.Value;
        var store = APTR.FromPointer(global::Amiga.Exec.AllocMem(4096,
            global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear));
        if (store.IsNull) { CorrectnessProbe.Report(250); return 2; }
        CorrectnessProbe.Report(200);
        CorrectnessProbe.Report((int)(store.Raw >> 24)); CorrectnessProbe.Report((int)(store.Raw >> 16));
        CorrectnessProbe.Report((int)(store.Raw >> 8)); CorrectnessProbe.Report((int)store.Raw);
        var result = CorrectnessProbe.Mount(store, 3, sfs2ReservedBlocks: 2);
        if (result == 0) result = ReadBase(store);
        if (result != 0) return CorrectnessProbe.Failed(store, (uint)result);
        if (DOS.Inhibit("SEQ0:", 1) == 0) return CorrectnessProbe.Failed(store, 90);
        CorrectnessProbe.Report(210);
        return 0;
    }
    private static int ReadBase(APTR store)
    {
        var data = APTR.FromPointer(store.Raw + 512);
        var file = DOS.Open("SEQ0:Docs/Payload", DOS.FileMode.OldFile); if (!file.HasValue) return 16;
        if (DOS.Read(file.Value, data, 1301) != 1301 || DOS.Close(file.Value) == 0) return 17;
        for (var i = 0; i < 1301; i++) if (APTR.ReadUInt8(data, i) != (byte)(i * 17 + 3)) return 18;
        var locked = DOS.Lock("SEQ0:Docs/Entry79", DOS.LockMode.Shared);
        if (!locked.HasValue) return 19;
        DOS.UnLock(locked.Value);
        locked = DOS.Lock("SEQ0:Docs/Payload", DOS.LockMode.Shared); if (!locked.HasValue) return 20;
        var ok = DOS.Examine(locked.Value, data); DOS.UnLock(locked.Value);
        if (ok == 0 || APTR.ReadUInt32(data, FileInfoBlock.SizeOffset) != 1301 || APTR.ReadUInt32(data, FileInfoBlock.DateDaysOffset) != 1234) return 21;
        locked = DOS.Lock("SEQ0:Docs", DOS.LockMode.Shared); if (!locked.HasValue) return 66;
        if (DOS.Examine(locked.Value, data) == 0) return 67;
        var count = 0;
        while (DOS.ExNext(locked.Value, data) != 0) count++;
        var end = DOS.IoErr(); DOS.UnLock(locked.Value);
        if (count != 81 || end != DOS.Error.NoMoreEntries) return 68;
        return 0;
    }
}

public sealed class OriginalSfs2RedoFactAttribute : FactAttribute
{
    public OriginalSfs2RedoFactAttribute()
    {
        foreach (var key in new[] { "COPPERSHARP_KICKSTART31_ROM", "COPPERFS_SFS2_TRST_HANDLER", "COPPERSFS_SFS2_NATIVE_REDO_IMAGE" })
            if (!File.Exists(Environment.GetEnvironmentVariable(key))) Skip = $"Supply {key} for original SFS2 redo recovery.";
    }
}

public sealed partial class OriginalHandlerCorrectnessTests
{
    [OriginalSfs2RedoFact]
    public async Task NativeCopperSfsRecoversOriginalSfs2Redo()
    {
        var handler = Environment.GetEnvironmentVariable("COPPERFS_SFS2_TRST_HANDLER")!;
        var capture = File.ReadAllBytes(Environment.GetEnvironmentVariable("COPPERSFS_SFS2_NATIVE_REDO_IMAGE")!);
        const int sector = 512;
        static uint Word(byte[] image, int offset) => BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(offset, 4));
        Assert.Equal(0x53465302u, Word(capture, 0));
        var marker = checked((int)(Word(capture, 104) + 2) * sector);
        Assert.Equal(0x54524641u, Word(capture, marker)); // TRFA
        var head = checked((int)Word(capture, marker + 12) * sector);
        Assert.Equal(0x54525354u, Word(capture, head)); // TRST

        var directory = Directory.CreateTempSubdirectory("CopperFS-OriginalSfs2Redo-").FullName;
        try
        {
            const string probe = "OriginalSfs2RedoProbe::Run";
            var recovered = await Run(directory, "sfs2-trst", "recover", handler, capture, true, probe);
            Assert.Equal(0x54524F4Bu, Word(recovered, marker)); // TROK
            Assert.True(!capture.AsSpan(0, marker).SequenceEqual(recovered.AsSpan(0, marker)) ||
                !capture.AsSpan(marker + sector).SequenceEqual(recovered.AsSpan(marker + sector)),
                "Replay must change target data outside the journal marker.");
            using var stream = new MemoryStream(recovered, writable: false);
            var report = SfsImageChecker.Check(stream, reservedBlocks: 2);
            var data = Enumerable.Range(0, 1301).Select(i => (byte)(i * 17 + 3)).ToArray();
            Assert.Equal(Hash(data), report.Entries.Single(e => e.Path == "/Docs/Payload").Sha256);
            Assert.Contains(report.Entries, e => e.Path == "/Docs/Entry79" && e.Size == 0);
            Assert.Equal(Hash(data[..513]), report.Entries.Single(e => e.Path == "/LengthProbe").Sha256);
            var reopened = await Run(directory, "sfs2-trst", "reopen", handler, recovered, false, probe);
            Assert.True(recovered.AsSpan().SequenceEqual(reopened), "The second native mount changed the recovered image.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
