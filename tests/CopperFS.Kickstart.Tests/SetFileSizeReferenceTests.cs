using Amiga;
using CopperStart.Floppy.Native.Tests;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    // Kickstart 3.1's own filesystem is the reference for ACTION_SET_FILE_SIZE:
    // results and IoErr for each mode, the caller's and another handle's
    // position after truncation, what a grown region reads back as (the
    // freed blocks it reuses were filled with 0xA5 first), the final size and
    // block count, and the volume's used-block count after growth and
    // truncation across pointer-table boundaries.
    [LinkReferenceTheory]
    [InlineData(false)] [InlineData(true)]
    public async System.Threading.Tasks.Task CopperFfsMatchesKickstartSetFileSize(bool ffs)
    {
        var handler = Environment.GetEnvironmentVariable("COPPERFS_FFS_HANDLER")!;
        var root = (Environment.GetEnvironmentVariable("COPPERFS_LINK_ARTIFACTS") is { Length: > 0 } configured
            ? configured : Path.Combine(Path.GetTempPath(), "copperfs-link-reference")) + "-setfilesize" + (ffs ? "-ffs" : "-ofs");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var initial = DosDiskImage.Create(ffs, includeLargeFile: false, blockCount: 32768,
            firstFileBlock: 2, payload: [1, 2, 3], volumeName: "Resize").Bytes;
        var results = new Dictionary<string, uint[]>();
        foreach (var arm in new[] { "Rom", "Copper" })
        {
            var label = arm.ToLowerInvariant();
            await Run(root, ffs ? "ffs" : "ofs", label, handler, initial, true, "SetFileSizeProbe::" + arm + (ffs ? "Ffs" : "Ofs"));
            results[arm] = ProbeData(root, label).Take(SetFileSizeProbe.Words).ToArray();
            output.WriteLine(arm + ": " + string.Join(",", results[arm]));
        }
        // Words 8-10 count what the first 5000 bytes read back after a shrink
        // to 300 and growth to 5000. Kickstart leaves stale contents in both
        // the truncated tail and the grown region, so those counts depend on
        // which free blocks each allocator picks. CopperFFS also keeps FFS
        // contents and zero-fills OFS growth (OFS blocks must be written
        // anyway); both keep the first 300 bytes.
        var rom = results["Rom"]; var copper = results["Copper"];
        bool Compared(int index) => index is < 8 or > 10;
        Assert.True(Enumerable.Range(0, SetFileSizeProbe.Words).Where(Compared).All(i => rom[i] == copper[i]),
            "Differs from Kickstart:\nrom    " + string.Join(",", rom) + "\ncopper " + string.Join(",", copper));
        Assert.True(copper[8] >= 300 && copper[8] + copper[9] + copper[10] <= 5000);
        if (!ffs) Assert.Equal(4700u, copper[10]);
        Directory.Delete(root, true);
    }
}

public static class SetFileSizeProbe
{
    internal const int Words = 34;
    public static int RomFfs() => Run(true, 1);
    public static int CopperFfs() => Run(false, 1);
    public static int RomOfs() => Run(true, 0);
    public static int CopperOfs() => Run(false, 0);

    private const int Beginning = -1, Current = 0, End = 1;

    private static uint Used(APTR info)
    {
        var root = DOS.Lock("SEQ0:", DOS.LockMode.Shared);
        if (!root.HasValue) return 0xFFFFFFFF;
        var used = DOS.Info(root.Value, info) != 0 ? APTR.ReadUInt32(info, 16) : 0xFFFFFFFE;
        DOS.UnLock(root.Value);
        return used;
    }

    private static int Run(bool rom, uint kind)
    {
        var library = Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) { CorrectnessProbe.Report(250); return 10; }
        DOS.DOSLibraryBase = library.Value;
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)), 184, uint.MaxValue);
        var control = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var buffer = APTR.FromPointer(Exec.AllocMem(20000, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var info = APTR.FromPointer(Exec.AllocMem(260, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        if (control.IsNull || buffer.IsNull || info.IsNull) { CorrectnessProbe.Report(250); return 11; }
        CorrectnessProbe.Report(200);
        for (var shift = 24; shift >= 0; shift -= 8) CorrectnessProbe.Report((int)(control.Raw >> shift));
        var setup = CorrectnessProbe.Mount(control, kind, romFileSystem: rom);
        if (setup != 0) return CorrectnessProbe.Failed(control, (uint)setup);
        var d = APTR.FromPointer(control.Raw + 512);
        void Put(int index, uint value) => APTR.WriteUInt32(d, index * 4, value);
        uint Error() => unchecked((uint)DOS.IoErr());

        // Leave 0xA5 in the blocks the resized file will reuse.
        for (var i = 0; i < 20000; i++) APTR.WriteUInt8(buffer, i, 0xA5);
        var fill = DOS.Open("SEQ0:Fill", DOS.FileMode.NewFile);
        if (fill.HasValue) { DOS.Write(fill.Value, buffer, 20000); DOS.Close(fill.Value); }
        Put(0, DOS.DeleteFile("SEQ0:Fill") != 0 ? 1u : 0u);

        for (var i = 0; i < 1000; i++) APTR.WriteUInt8(buffer, i, 0x11);
        var file = DOS.Open("SEQ0:Sized", DOS.FileMode.NewFile);
        if (!file.HasValue) return CorrectnessProbe.Failed(control, 20);
        var fh = file.Value;
        DOS.Write(fh, buffer, 1000);
        Put(1, unchecked((uint)DOS.SetFileSize(fh, 300, Beginning))); Put(2, Error());
        Put(3, unchecked((uint)DOS.Seek(fh, 0, Current)));
        Put(4, unchecked((uint)DOS.SetFileSize(fh, 5000, Beginning))); Put(5, Error());
        Put(6, unchecked((uint)DOS.Seek(fh, 0, Current)));
        DOS.Seek(fh, 0, Beginning);
        for (var i = 0; i < 5000; i++) APTR.WriteUInt8(buffer, i, 0x77);
        Put(7, unchecked((uint)DOS.Read(fh, buffer, 5000)));
        uint ones = 0, filled = 0, zeros = 0;
        for (var i = 0; i < 5000; i++)
        {
            var b = APTR.ReadUInt8(buffer, i);
            if (b == 0x11) ones++; else if (b == 0xA5) filled++; else if (b == 0) zeros++;
        }
        Put(8, ones); Put(9, filled); Put(10, zeros);
        Put(11, unchecked((uint)DOS.SetFileSize(fh, -100, End))); Put(12, Error());
        Put(13, unchecked((uint)DOS.Seek(fh, 0, Current)));
        DOS.Seek(fh, 2000, Beginning);
        Put(14, unchecked((uint)DOS.SetFileSize(fh, 0, Current))); Put(15, Error());
        Put(16, unchecked((uint)DOS.Seek(fh, 0, Current)));
        Put(17, unchecked((uint)DOS.SetFileSize(fh, -10000, Beginning))); Put(18, Error());
        // A second handle past the new end.
        var other = DOS.Open("SEQ0:Sized", DOS.FileMode.OldFile);
        Put(19, other.HasValue ? 1u : 0u);
        if (other.HasValue)
        {
            DOS.Seek(other.Value, 1500, Beginning);
            Put(20, unchecked((uint)DOS.SetFileSize(fh, 1000, Beginning))); Put(21, Error());
            Put(22, unchecked((uint)DOS.Seek(other.Value, 0, Current)));
            Put(23, unchecked((uint)DOS.Read(other.Value, buffer, 100)));
            DOS.Close(other.Value);
        }
        // Growth and truncation across pointer-table boundaries.
        var before = Used(info);
        Put(24, unchecked((uint)DOS.SetFileSize(fh, 80000, Beginning))); Put(25, Error());
        Put(26, Used(info) - before);
        DOS.Seek(fh, 79900, Beginning);
        Put(27, unchecked((uint)DOS.Read(fh, buffer, 200)));
        Put(28, unchecked((uint)DOS.SetFileSize(fh, 40000, Beginning))); Put(29, Error());
        Put(30, Used(info) - before);
        Put(31, DOS.Close(fh) != 0 ? 1u : 0u);
        var locked = DOS.Lock("SEQ0:Sized", DOS.LockMode.Shared);
        if (locked.HasValue)
        {
            if (DOS.Examine(locked.Value, info) != 0) { Put(32, APTR.ReadUInt32(info, 124)); Put(33, APTR.ReadUInt32(info, 128)); }
            DOS.UnLock(locked.Value);
        }
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (DOS.Inhibit("SEQ0:", 1) != 0) { CorrectnessProbe.Report(210); return 0; }
            DOS.Delay(10);
        }
        return CorrectnessProbe.Failed(control, 90);
    }
}
