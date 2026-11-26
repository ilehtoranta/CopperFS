using System.Buffers.Binary;
using Amiga;
using CopperStart.Floppy.Native.Tests;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    // A volume left with an invalid root (a crash mid-write) and a leaked
    // bitmap bit: Kickstart 3.1's filesystem validates it at mount. CopperFFS
    // must too: the volume reads as validated, Info counts the repaired
    // allocation, and it accepts a new file.
    [LinkReferenceTheory]
    [InlineData(false)] [InlineData(true)]
    public async System.Threading.Tasks.Task CopperFfsValidatesAnInvalidVolumeLikeKickstart(bool ffs)
    {
        var handler = Environment.GetEnvironmentVariable("COPPERFS_FFS_HANDLER")!;
        var root = (Environment.GetEnvironmentVariable("COPPERFS_LINK_ARTIFACTS") is { Length: > 0 } configured
            ? configured : Path.Combine(Path.GetTempPath(), "copperfs-link-reference")) + "-validator" + (ffs ? "-ffs" : "-ofs");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var image = DosDiskImage.Create(ffs, includeLargeFile: false, blockCount: 32768,
            firstFileBlock: 2, payload: [1, 2, 3], volumeName: "Crashed").Bytes;
        const int rootBlock = 16384, tail = 0;
        uint Word(int block, int at) => BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(block * 512 + at));
        void Seal(int block, int at)
        {
            BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(block * 512 + at), 0);
            uint sum = 0; for (var i = 0; i < 512; i += 4) sum = unchecked(sum + Word(block, i));
            BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(block * 512 + at), unchecked(0u - sum));
        }
        // Leak block 30000 (mark it allocated), then mark the root invalid.
        var bit = 30000 - 2;
        var map = (int)Word(rootBlock, tail + 316 + bit / 4064 * 4);
        var at = 4 + bit % 4064 / 32 * 4;
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(map * 512 + at), Word(map, at) & ~(1u << (bit % 32)));
        Seal(map, 0);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(rootBlock * 512 + tail + 312), 0);
        Seal(rootBlock, 20);
        var results = new Dictionary<string, uint[]>();
        foreach (var arm in new[] { "Rom", "Copper" })
        {
            var label = arm.ToLowerInvariant();
            await Run(root, ffs ? "ffs" : "ofs", label, handler, image, true, "ValidatorProbe::" + arm + (ffs ? "Ffs" : "Ofs"));
            results[arm] = ProbeData(root, label).Take(4).ToArray();
            output.WriteLine(arm + ": " + string.Join(",", results[arm]));
        }
        Assert.True(results["Rom"].SequenceEqual(results["Copper"]),
            "Differs from Kickstart:\nrom    " + string.Join(",", results["Rom"]) + "\ncopper " + string.Join(",", results["Copper"]));
        Directory.Delete(root, true);
    }
}

public static class ValidatorProbe
{
    public static int RomFfs() => Run(true, 1);
    public static int CopperFfs() => Run(false, 1);
    public static int RomOfs() => Run(true, 0);
    public static int CopperOfs() => Run(false, 0);

    private static int Run(bool rom, uint kind)
    {
        var library = Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) { CorrectnessProbe.Report(250); return 10; }
        DOS.DOSLibraryBase = library.Value;
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)), 184, uint.MaxValue);
        var control = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var info = APTR.FromPointer(Exec.AllocMem(64, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        if (control.IsNull || info.IsNull) { CorrectnessProbe.Report(250); return 11; }
        CorrectnessProbe.Report(200);
        for (var shift = 24; shift >= 0; shift -= 8) CorrectnessProbe.Report((int)(control.Raw >> shift));
        var setup = CorrectnessProbe.Mount(control, kind, romFileSystem: rom);
        if (setup != 0) return CorrectnessProbe.Failed(control, (uint)setup);
        var d = APTR.FromPointer(control.Raw + 512);
        DOS.Delay(250);
        var lockRoot = DOS.Lock("SEQ0:", DOS.LockMode.Shared);
        if (lockRoot.HasValue)
        {
            if (DOS.Info(lockRoot.Value, info) != 0) { APTR.WriteUInt32(d, 0, APTR.ReadUInt32(info, 8)); APTR.WriteUInt32(d, 4, APTR.ReadUInt32(info, 16)); }
            DOS.UnLock(lockRoot.Value);
        }
        var file = DOS.Open("SEQ0:Fresh", DOS.FileMode.NewFile);
        if (file.HasValue)
        {
            APTR.WriteUInt32(d, 8, unchecked((uint)DOS.Write(file.Value, info, 64)));
            DOS.Close(file.Value);
        }
        else APTR.WriteUInt32(d, 8, 0x10000u | unchecked((uint)DOS.IoErr()));
        var check = DOS.Lock("SEQ0:Fresh", DOS.LockMode.Shared);
        APTR.WriteUInt32(d, 12, check.HasValue ? 1u : unchecked((uint)DOS.IoErr()));
        if (check.HasValue) DOS.UnLock(check.Value);
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (DOS.Inhibit("SEQ0:", 1) != 0) { CorrectnessProbe.Report(210); return 0; }
            DOS.Delay(10);
        }
        return CorrectnessProbe.Failed(control, 90);
    }
}
