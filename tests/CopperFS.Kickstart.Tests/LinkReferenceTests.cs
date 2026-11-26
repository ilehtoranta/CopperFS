using System.Buffers.Binary;
using System.Text.Json;
using Amiga;
using CopperStart.Floppy.Native.Tests;

namespace CopperFS.Kickstart.Tests;

// Kickstart 3.1's own filesystem (the boot floppy's ROM segment) is the
// reference for hard and soft links. It creates the links; both it and the
// candidate CopperFFS then run identical DOS workloads on copies of that disk.
public sealed class LinkReferenceFactAttribute : FactAttribute
{
    public LinkReferenceFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("COPPERSHARP_KICKSTART31_ROM")) ||
            !File.Exists(Environment.GetEnvironmentVariable("COPPERFS_FFS_HANDLER")))
            Skip = "Supply a licensed Kickstart 3.1 ROM and COPPERFS_FFS_HANDLER (a freshly built CopperFFS).";
    }
}

public sealed partial class OriginalHandlerCorrectnessTests
{
    private static readonly string[] LinkPhases = ["Inspect", "DeleteOriginal", "DeleteLink", "DirLink"];

    [LinkReferenceFact]
    public async System.Threading.Tasks.Task CopperFfsMatchesKickstartLinkBehaviour()
    {
        var handler = Environment.GetEnvironmentVariable("COPPERFS_FFS_HANDLER")!;
        var root = Environment.GetEnvironmentVariable("COPPERFS_LINK_ARTIFACTS") is { Length: > 0 } configured
            ? configured : Path.Combine(Path.GetTempPath(), "copperfs-link-reference");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var initial = DosDiskImage.Create(true, includeLargeFile: false, blockCount: 32768,
            firstFileBlock: 2, payload: [1, 2, 3], volumeName: "Links").Bytes;
        var created = await Run(root, "ffs", "rom-create", handler, initial, true, "LinkProbe::RomCreate");
        var createData = ProbeData(root, "rom-create");
        var report = new Dictionary<string, object> { ["create"] = new { Probe = createData, Layout = LinkLayout(created) } };
        Assert.All(createData.Take(6).Where((_, i) => i % 2 == 0), ok => Assert.NotEqual(0u, ok));
        var mismatches = new List<string>();
        foreach (var phase in LinkPhases)
        {
            var results = new Dictionary<string, (uint[] Probe, string[] Layout)>();
            foreach (var arm in new[] { "Rom", "Copper" })
            {
                var label = arm.ToLowerInvariant() + "-" + phase.ToLowerInvariant();
                var image = await Run(root, "ffs", label, handler, created, phase != "Inspect", "LinkProbe::" + arm + phase);
                results[arm] = (ProbeData(root, label), LinkLayout(image));
            }
            report[phase] = results.ToDictionary(r => r.Key, r => (object)new { r.Value.Probe, r.Value.Layout });
            if (!results["Rom"].Probe.SequenceEqual(results["Copper"].Probe)) mismatches.Add(phase + " probe");
            if (!results["Rom"].Layout.SequenceEqual(results["Copper"].Layout)) mismatches.Add(phase + " layout");
        }
        File.WriteAllText(Path.Combine(root, "link-reference.json"), JsonSerializer.Serialize(report, Json));
        Assert.True(mismatches.Count == 0, "Differs from Kickstart: " + string.Join(", ", mismatches) + "; see " + root);
    }

    private static uint[] ProbeData(string root, string label)
    {
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, label, "result.json")));
        return result.RootElement.GetProperty("ProbeData").EnumerateArray().Select(e => e.GetUInt32()).ToArray();
    }

    // Reachable namespace with keys replaced by paths, so allocation choices
    // do not matter. Records type, size, link target and link chain per entry.
    private static string[] LinkLayout(byte[] image)
    {
        const int bytes = 512; const uint root = 16384;
        uint U(uint block, int at) => BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan((int)(block * bytes) + at));
        string Name(uint block) => System.Text.Encoding.Latin1.GetString(image, (int)(block * bytes) + 433, image[block * bytes + 432]);
        var paths = new Dictionary<uint, string> { [root] = ":" };
        var pending = new Queue<uint>(); pending.Enqueue(root);
        while (pending.Count > 0)
        {
            var directory = pending.Dequeue();
            for (var slot = 0; slot < 72; slot++)
                for (var key = U(directory, 24 + slot * 4); key != 0 && !paths.ContainsKey(key); key = U(key, 496))
                {
                    paths[key] = (directory == root ? "" : paths[directory] + "/") + Name(key);
                    if (unchecked((int)U(key, 508)) == 2) pending.Enqueue(key);
                }
        }
        string P(uint key) => key == 0 ? "-" : paths.TryGetValue(key, out var path) ? path : "#unreachable";
        return paths.Where(p => p.Key != root).Select(p =>
            $"{p.Value} type={unchecked((int)U(p.Key, 508))} size={U(p.Key, 324)} real={P(U(p.Key, 468))} next={P(U(p.Key, 472))}")
            .Order().ToArray();
    }
}

public static class LinkProbe
{
    public static int RomCreate() => Run(true, 0);
    public static int RomInspect() => Run(true, 1);
    public static int CopperInspect() => Run(false, 1);
    public static int RomDeleteOriginal() => Run(true, 2);
    public static int CopperDeleteOriginal() => Run(false, 2);
    public static int RomDeleteLink() => Run(true, 3);
    public static int CopperDeleteLink() => Run(false, 3);
    public static int RomDirLink() => Run(true, 4);
    public static int CopperDirLink() => Run(false, 4);

    private static int Run(bool rom, int phase)
    {
        var library = Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) { CorrectnessProbe.Report(250); return 10; }
        DOS.DOSLibraryBase = library.Value;
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)), 184, uint.MaxValue);
        var control = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var buffer = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var fib = APTR.FromPointer(Exec.AllocMem(260, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        if (control.IsNull || buffer.IsNull || fib.IsNull) { CorrectnessProbe.Report(250); return 11; }
        CorrectnessProbe.Report(200);
        for (var shift = 24; shift >= 0; shift -= 8) CorrectnessProbe.Report((int)(control.Raw >> shift));
        var setup = CorrectnessProbe.Mount(control, 1, romFileSystem: rom);
        if (setup != 0) return CorrectnessProbe.Failed(control, (uint)setup);
        var d = APTR.FromPointer(control.Raw + 512);
        if (phase == 0) Create(d, buffer);
        else if (phase == 1) Inspect(d, buffer, fib);
        else if (phase == 2) DeleteOriginal(d, buffer, fib);
        else if (phase == 3) DeleteLink(d, buffer, fib);
        else DirLink(d, buffer);
        // Both filesystems must have written everything before the host reads the disk.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (DOS.Inhibit("SEQ0:", 1) != 0) { CorrectnessProbe.Report(210); return 0; }
            DOS.Delay(10);
        }
        return CorrectnessProbe.Failed(control, 90);
    }

    private static void Put(APTR d, int index, uint value) => APTR.WriteUInt32(d, index * 4, value);
    private static void Error(APTR d, int index) => Put(d, index, unchecked((uint)DOS.IoErr()));
    // IoErr is defined only after a failure; a success leaves it unspecified.
    private static void Result(APTR d, int index, int value, bool failed)
    {
        Put(d, index, unchecked((uint)value));
        if (failed) Error(d, index + 1);
    }

    private static void Create(APTR d, APTR buffer)
    {
        for (var i = 0; i < 600; i++) APTR.WriteUInt8(buffer, i, (byte)(i * 7 + 3));
        var file = DOS.Open("SEQ0:A", DOS.FileMode.NewFile);
        if (file.HasValue) { DOS.Write(file.Value, buffer, 600); DOS.Close(file.Value); }
        var directory = DOS.CreateDir("SEQ0:D");
        if (directory.HasValue) DOS.UnLock(directory.Value);
        var a = DOS.Lock("SEQ0:A", DOS.LockMode.Shared);
        if (a.HasValue)
        {
            { var r = DOS.MakeLink("SEQ0:L", unchecked((int)a.Value.Raw), 0); Result(d, 0, r, r == 0); }
            DOS.UnLock(a.Value);
        }
        { var r = DOS.MakeLink("SEQ0:SL", unchecked((int)CString.ToUInt32(CString.FromLiteral("A"))), 1); Result(d, 2, r, r == 0); }
        var dir = DOS.Lock("SEQ0:D", DOS.LockMode.Shared);
        if (dir.HasValue)
        {
            { var r = DOS.MakeLink("SEQ0:DL", unchecked((int)dir.Value.Raw), 0); Result(d, 4, r, r == 0); }
            DOS.UnLock(dir.Value);
        }
    }

    private static bool Matches(APTR buffer, int length)
    {
        for (var i = 0; i < length; i++) if (APTR.ReadUInt8(buffer, i) != (byte)(i * 7 + 3)) return false;
        return true;
    }

    // 1 when the whole 600-byte pattern reads back through `name`.
    private static void ReadBack(APTR d, int index, CString name, APTR buffer)
    {
        for (var i = 0; i < 600; i++) APTR.WriteUInt8(buffer, i, 0);
        var file = DOS.Open(name, DOS.FileMode.OldFile);
        if (!file.HasValue) { Error(d, index + 1); return; }
        var read = DOS.Read(file.Value, buffer, 1000);
        DOS.Close(file.Value);
        Put(d, index, read == 600 && Matches(buffer, 600) ? 1u : 0u);
    }

    private static void Examine(APTR d, int index, CString name, APTR fib)
    {
        var locked = DOS.Lock(name, DOS.LockMode.Shared);
        if (!locked.HasValue) { Error(d, index + 1); return; }
        Put(d, index, 1);
        if (DOS.Examine(locked.Value, fib) != 0)
        {
            Put(d, index + 1, APTR.ReadUInt32(fib, 4));
            Put(d, index + 2, APTR.ReadUInt32(fib, 124));
        }
        DOS.UnLock(locked.Value);
    }

    // fib_FileName is a C string here; second is 0 for one-character names.
    private static bool Named(APTR fib, byte first, byte second) =>
        APTR.ReadUInt8(fib, 8) == first && APTR.ReadUInt8(fib, 9) == second &&
        (second == 0 || APTR.ReadUInt8(fib, 10) == 0);

    // Count, a seen-name mask and each name's entry type (A, D, L, SL, DL).
    private static void List(APTR d, int index, APTR fib)
    {
        var root = DOS.Lock("SEQ0:", DOS.LockMode.Shared);
        if (!root.HasValue || DOS.Examine(root.Value, fib) == 0) { Error(d, index); return; }
        uint count = 0, mask = 0;
        while (DOS.ExNext(root.Value, fib) != 0)
        {
            count++;
            var type = APTR.ReadUInt32(fib, 4);
            var bit = Named(fib, (byte)'A', 0) ? 0 : Named(fib, (byte)'D', 0) ? 1 : Named(fib, (byte)'L', 0) ? 2 :
                Named(fib, (byte)'S', (byte)'L') ? 3 : Named(fib, (byte)'D', (byte)'L') ? 4 : -1;
            if (bit < 0) continue;
            mask |= 1u << bit;
            Put(d, index + 3 + bit, type);
        }
        Put(d, index, count); Put(d, index + 1, mask); Error(d, index + 2);
        DOS.UnLock(root.Value);
    }

    private static void Inspect(APTR d, APTR buffer, APTR fib)
    {
        var soft = DOS.Lock("SEQ0:SL", DOS.LockMode.Shared);
        Put(d, 0, soft.HasValue ? 1u : 0u); if (!soft.HasValue) Error(d, 1);
        if (soft.HasValue) DOS.UnLock(soft.Value);
        Examine(d, 2, "SEQ0:L", fib);
        ReadBack(d, 5, "SEQ0:L", buffer);
        List(d, 7, fib);
        Examine(d, 15, "SEQ0:DL", fib);
        var root = DOS.Lock("SEQ0:", DOS.LockMode.Shared);
        var port = DOS.DeviceProc("SEQ0:");
        if (root.HasValue && port.IsNotNull)
        {
            for (var i = 0; i < 64; i++) APTR.WriteUInt8(buffer, i, 0xFF);
            { var r = DOS.ReadLink(port, root.Value, "SL", buffer, 64); Result(d, 18, r, r < 0); }
            Put(d, 20, APTR.ReadUInt32(buffer, 0));
            DOS.UnLock(root.Value);
        }
        Examine(d, 21, "SEQ0:A", fib);
        var through = DOS.Lock("SEQ0:SL/x", DOS.LockMode.Shared);
        Put(d, 24, through.HasValue ? 1u : 0u); if (!through.HasValue) Error(d, 25);
        if (through.HasValue) DOS.UnLock(through.Value);
    }

    private static void DeleteOriginal(APTR d, APTR buffer, APTR fib)
    {
        { var r = DOS.DeleteFile("SEQ0:A"); Result(d, 0, r, r == 0); }
        ReadBack(d, 2, "SEQ0:L", buffer);
        Examine(d, 4, "SEQ0:L", fib);
        Examine(d, 7, "SEQ0:A", fib);
        List(d, 10, fib);
    }

    private static void DeleteLink(APTR d, APTR buffer, APTR fib)
    {
        { var r = DOS.DeleteFile("SEQ0:L"); Result(d, 0, r, r == 0); }
        ReadBack(d, 2, "SEQ0:A", buffer);
        Examine(d, 4, "SEQ0:L", fib);
        { var r = DOS.DeleteFile("SEQ0:SL"); Result(d, 7, r, r == 0); }
        ReadBack(d, 9, "SEQ0:A", buffer);
        List(d, 11, fib);
    }

    private static void DirLink(APTR d, APTR buffer)
    {
        var file = DOS.Open("SEQ0:DL/Inner", DOS.FileMode.NewFile);
        Put(d, 0, file.HasValue ? 1u : 0u); if (!file.HasValue) Error(d, 1);
        if (file.HasValue) { DOS.Write(file.Value, buffer, 10); DOS.Close(file.Value); }
        var inner = DOS.Lock("SEQ0:D/Inner", DOS.LockMode.Shared);
        Put(d, 2, inner.HasValue ? 1u : 0u);
        if (inner.HasValue) DOS.UnLock(inner.Value);
        { var r = DOS.DeleteFile("SEQ0:DL"); Result(d, 3, r, r == 0); }
        inner = DOS.Lock("SEQ0:D/Inner", DOS.LockMode.Shared);
        Put(d, 5, inner.HasValue ? 1u : 0u);
        if (inner.HasValue) DOS.UnLock(inner.Value);
        var link = DOS.Lock("SEQ0:DL", DOS.LockMode.Shared);
        Put(d, 6, link.HasValue ? 1u : 0u); if (!link.HasValue) Error(d, 7);
        if (link.HasValue) DOS.UnLock(link.Value);
    }
}
