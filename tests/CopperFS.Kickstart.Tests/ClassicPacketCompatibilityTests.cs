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
    public async System.Threading.Tasks.Task CopperHandlersRejectClassicEventPackets(string profile, string entry)
    {
        string path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!;
        var handler = manifest.Profiles.Single(p => p.Name == profile).Candidate;
        byte[] media = profile switch
        {
            "ffs" => DosDiskImage.Create(true, includeLargeFile: false, blockCount: 32768).Bytes,
            "sfs" => File.ReadAllBytes(manifest.ClassicBase),
            _ => ReadMedia.Sfs2()
        };
        if (profile == "sfs") Assert.Equal(manifest.ClassicBaseSha256, Hash(media));
        var after = await Run(Path.Combine(Path.GetDirectoryName(path)!, "classic-event-packets"), profile,
            profile, handler, media, false, guestEntry: "ClassicPacketProbe::" + entry, cpu: M68kCpuModel.M68000);
        Assert.Equal(Hash(media), Hash(after));
    }
}

public static class ClassicPacketProbe
{
    public static int Ffs() => Run(1);
    public static int Sfs() => Run(2);
    public static int Sfs2() => Run(3);

    private static int Run(uint kind)
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
        if (port.IsNull) return CorrectnessProbe.Failed(control, 16);
        for (int phase = 0; phase < 3; phase++)
        {
            if (phase != 0 && DOS.DoPkt(port, (int)DosPacketAction.Inhibit, phase == 1 ? -1 : 0, 0, 0, 0, 0) == 0)
                return CorrectnessProbe.Failed(control, (uint)(100 + phase));
            var expected = phase == 1 && kind != 1 ? DOS.Error.NoDisk : DOS.Error.ActionNotKnown;
            for (int i = 0; i < 3; i++)
            {
                var action = i == 0 ? DosPacketAction.WaitChar : i == 1 ? DosPacketAction.Timer : DosPacketAction.DiskChange;
                if (DOS.DoPkt(port, (int)action, 0, 50000, 0, 0, 0) != 0 || (DOS.Error)DOS.IoErr() != expected)
                    return CorrectnessProbe.Failed(control, (uint)(phase * 100 + (int)action));
            }
        }
        CorrectnessProbe.Report(210);
        return 0;
    }
}
