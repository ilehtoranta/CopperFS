using Amiga;
using CopperStart.Floppy.Native.Tests;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    // Kickstart 3.1's own filesystem is the reference for device errors: the
    // results and IoErr a program sees while every write fails, after one
    // failed write, and while reads fail; and whether the volume is valid and
    // writable again once the device recovers. The hook fails requests with
    // io_Error 20, as a failing drive does.
    [LinkReferenceTheory]
    [InlineData(false)] [InlineData(true)]
    public async System.Threading.Tasks.Task CopperFfsMatchesKickstartDeviceErrors(bool ffs)
    {
        var handler = Environment.GetEnvironmentVariable("COPPERFS_FFS_HANDLER")!;
        var root = (Environment.GetEnvironmentVariable("COPPERFS_LINK_ARTIFACTS") is { Length: > 0 } configured
            ? configured : Path.Combine(Path.GetTempPath(), "copperfs-link-reference")) + "-deverr" + (ffs ? "-ffs" : "-ofs");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var initial = DosDiskImage.Create(ffs, includeLargeFile: false, blockCount: 32768,
            firstFileBlock: 2, payload: [1, 2, 3], volumeName: "Faulty").Bytes;
        var results = new Dictionary<string, uint[]>();
        foreach (var arm in new[] { "Rom", "Copper" })
        {
            var label = arm.ToLowerInvariant();
            var device = new FailingDevice { RootOffset = initial.Length / 2 };
            await Run(root, ffs ? "ffs" : "ofs", label, handler, initial, true, "DeviceErrorProbe::" + arm + (ffs ? "Ffs" : "Ofs"),
                storage: device, observe: (mark, _, _, _, _) => { if (mark is >= 227 and <= 231) device.Mode = mark; });
            results[arm] = ProbeData(root, label).Take(DeviceErrorProbe.Words).ToArray();
            output.WriteLine($"{arm} (failed {device.FailedWrites} writes, {device.FailedReads} reads): " + string.Join(",", results[arm]));
        }
        // Words 1-6: Kickstart opens the new file without writing and fails its
        // Write or Close; CopperFFS writes the header at Open and fails there.
        // Either way, every failure carries the device's own error (20).
        var rom = results["Rom"]; var copper = results["Copper"];
        Assert.True(copper[1] == 0 ? copper[2] == 20 : copper[4] is 0 or 20 && copper[6] is 0 or 20, string.Join(",", copper));
        // Words 23-27 (writes fail everywhere but the root): Kickstart reports
        // the doomed file as written and later loses a new file it also
        // reported as written (word 27: 205). CopperFFS fails the write with
        // the device error, validates the volume once the device works, and
        // keeps the new file. Only that outcome is required.
        Assert.True((copper[23] & 0xFFFF) == 20 && copper[24] == 82 && copper[25] == 1 && copper[26] == 1 && copper[27] == 1,
            string.Join(",", copper));
        Assert.True(rom.Skip(7).Take(16).SequenceEqual(copper.Skip(7).Take(16)),
            "Differs from Kickstart:\nrom    " + string.Join(",", rom) + "\ncopper " + string.Join(",", copper));
        Directory.Delete(root, true);
    }

    // 227: every write fails. 228: healthy. 229: the next write fails once.
    // 230: every read fails. 231: every write except to the root fails.
    private sealed class FailingDevice : WritableHook
    {
        public int Mode = 228, FailedWrites, FailedReads;
        public long RootOffset;
        public override void Write(FileStream backing, long offset, ReadOnlySpan<byte> source)
        {
            if (Mode == 227 || Mode == 229 || (Mode == 231 && offset != RootOffset))
            {
                FailedWrites++;
                if (Mode == 229) Mode = 228;
                throw new IOException("Injected write failure");
            }
            base.Write(backing, offset, source);
        }
        public override void Read(FileStream backing, long offset, Span<byte> destination)
        {
            if (Mode == 230) { FailedReads++; throw new IOException("Injected read failure"); }
            base.Read(backing, offset, destination);
        }
    }
}

public static class DeviceErrorProbe
{
    internal const int Words = 28;
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
        var buffer = APTR.FromPointer(Exec.AllocMem(4096, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var info = APTR.FromPointer(Exec.AllocMem(64, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        if (control.IsNull || buffer.IsNull || info.IsNull) { CorrectnessProbe.Report(250); return 11; }
        CorrectnessProbe.Report(200);
        for (var shift = 24; shift >= 0; shift -= 8) CorrectnessProbe.Report((int)(control.Raw >> shift));
        var setup = CorrectnessProbe.Mount(control, kind, romFileSystem: rom);
        if (setup != 0) return CorrectnessProbe.Failed(control, (uint)setup);
        var d = APTR.FromPointer(control.Raw + 512);
        void Put(int index, uint value) => APTR.WriteUInt32(d, index * 4, value);
        uint Error() => unchecked((uint)DOS.IoErr());
        uint State()
        {
            var root = DOS.Lock("SEQ0:", DOS.LockMode.Shared);
            if (!root.HasValue) return 0xFFFF0000u | Error();
            var state = DOS.Info(root.Value, info) != 0 ? APTR.ReadUInt32(info, 8) : 0xFFFE0000u | Error();
            DOS.UnLock(root.Value);
            return state;
        }
        uint Exists(CString name)
        {
            var found = DOS.Lock(name, DOS.LockMode.Shared);
            if (!found.HasValue) return Error();
            DOS.UnLock(found.Value); return 1;
        }
        uint WriteFile(CString name)
        {
            var file = DOS.Open(name, DOS.FileMode.NewFile);
            if (!file.HasValue) return 0x10000u | Error();
            var written = DOS.Write(file.Value, buffer, 100);
            var closed = DOS.Close(file.Value);
            return written == 100 && closed != 0 ? 1u : 0x20000u | Error();
        }
        for (var i = 0; i < 4096; i++) APTR.WriteUInt8(buffer, i, (byte)(i * 3));
        Put(0, WriteFile(CString.FromLiteral("SEQ0:Before")));

        // Every write fails while a file is created, written and closed.
        CorrectnessProbe.Report(227);
        var victim = DOS.Open("SEQ0:Victim", DOS.FileMode.NewFile);
        Put(1, victim.HasValue ? 1u : 0u); Put(2, Error());
        if (victim.HasValue)
        {
            Put(3, unchecked((uint)DOS.Write(victim.Value, buffer, 2000))); Put(4, Error());
            Put(5, unchecked((uint)DOS.Close(victim.Value))); Put(6, Error());
        }
        CorrectnessProbe.Report(228);
        DOS.Delay(250);
        Put(7, State()); Put(8, Exists(CString.FromLiteral("SEQ0:Victim"))); Put(9, Exists(CString.FromLiteral("SEQ0:Before")));
        Put(10, WriteFile(CString.FromLiteral("SEQ0:After"))); DOS.Delay(50); Put(11, State());

        // One failed write in the middle of a CreateDir.
        CorrectnessProbe.Report(229);
        var made = DOS.CreateDir("SEQ0:Dir");
        Put(12, made.HasValue ? 1u : 0u); Put(13, Error());
        if (made.HasValue) DOS.UnLock(made.Value);
        CorrectnessProbe.Report(228);
        DOS.Delay(250);
        Put(14, State()); Put(15, Exists(CString.FromLiteral("SEQ0:Dir"))); Put(16, WriteFile(CString.FromLiteral("SEQ0:After2")));

        // Reads fail, then recover.
        var before = DOS.Open("SEQ0:Before", DOS.FileMode.OldFile);
        Put(17, before.HasValue ? 1u : 0u);
        if (before.HasValue)
        {
            CorrectnessProbe.Report(230);
            Put(18, unchecked((uint)DOS.Read(before.Value, buffer, 100))); Put(19, Error());
            CorrectnessProbe.Report(228);
            DOS.Seek(before.Value, 0, -1);
            Put(20, unchecked((uint)DOS.Read(before.Value, buffer, 100)));
            DOS.Close(before.Value);
        }
        DOS.Delay(50);
        Put(21, State()); Put(22, WriteFile(CString.FromLiteral("SEQ0:Final")));

        // Writes fail except the root's: a mutation can mark the root invalid
        // and then fail, so the volume needs validating once the device works.
        CorrectnessProbe.Report(231);
        Put(23, WriteFile(CString.FromLiteral("SEQ0:Torn")));
        var torn = DOS.CreateDir("SEQ0:TornDir");
        if (torn.HasValue) DOS.UnLock(torn.Value);
        CorrectnessProbe.Report(228);
        DOS.Delay(250);
        Put(24, State()); Put(25, Exists(CString.FromLiteral("SEQ0:Before"))); Put(26, WriteFile(CString.FromLiteral("SEQ0:Healed")));
        Put(27, Exists(CString.FromLiteral("SEQ0:Healed")));
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (DOS.Inhibit("SEQ0:", 1) != 0) { CorrectnessProbe.Report(210); return 0; }
            DOS.Delay(10);
        }
        return CorrectnessProbe.Failed(control, 90);
    }
}
