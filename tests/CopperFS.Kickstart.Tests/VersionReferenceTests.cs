using System.Text;
using Amiga;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    // AmigaDOS Version finds a handler's "$VER:" tag in its file and in its
    // loaded segments. Kickstart 3.1's LoadSeg loads the built handler and the
    // probe searches the loaded CODE hunk for the tag.
    [LinkReferenceFact]
    public async System.Threading.Tasks.Task LoadedCopperFfsCarriesItsVersionTag()
    {
        var handler = Environment.GetEnvironmentVariable("COPPERFS_FFS_HANDLER")!;
        var root = (Environment.GetEnvironmentVariable("COPPERFS_LINK_ARTIFACTS") is { Length: > 0 } configured
            ? configured : Path.Combine(Path.GetTempPath(), "copperfs-link-reference")) + "-version";
        if (Directory.Exists(root)) Directory.Delete(root, true);
        await Run(root, "ffs", "version", handler, new byte[32768 * 512], false, "VersionProbe::Loaded");
        var words = ProbeData(root, "version");
        Assert.NotEqual(0u, words[0]);
        var text = new StringBuilder();
        for (var i = 1; i < 12 && text.Length < 44; i++)
            for (var shift = 24; shift >= 0; shift -= 8)
            {
                var c = (char)((words[i] >> shift) & 0xFF);
                if (c == 0) { i = 12; break; }
                text.Append(c);
            }
        output.WriteLine(text.ToString());
        Assert.Matches(@"^\$VER: CopperFFS \d+\.\d+ \(\d{1,2}\.\d{1,2}\.\d{2,3}\)$", text.ToString());
        Directory.Delete(root, true);
    }
}

public static class VersionProbe
{
    public static int Loaded()
    {
        var library = Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) { CorrectnessProbe.Report(250); return 10; }
        DOS.DOSLibraryBase = library.Value;
        var control = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        if (control.IsNull) { CorrectnessProbe.Report(250); return 11; }
        CorrectnessProbe.Report(200);
        for (var shift = 24; shift >= 0; shift -= 8) CorrectnessProbe.Report((int)(control.Raw >> shift));
        var d = APTR.FromPointer(control.Raw + 512);
        var segments = DOS.LoadSeg("HANDLER");
        if (!segments.HasValue) return CorrectnessProbe.Failed(control, 12);
        // Each segment: a size longword before the BPTR, then the next BPTR, then data.
        uint found = 0;
        for (var segment = segments.Value.Raw; segment != 0 && found == 0; segment = APTR.ReadUInt32(APTR.FromPointer(segment << 2), 0))
        {
            var start = APTR.FromPointer((segment << 2) + 4);
            var bytes = APTR.ReadUInt32(APTR.FromPointer((segment << 2) - 4), 0) - 8;
            for (var at = 0u; at + 5 < bytes && found == 0; at++)
                if (APTR.ReadUInt8(start, (int)at) == (byte)'$' && APTR.ReadUInt8(start, (int)at + 1) == (byte)'V' &&
                    APTR.ReadUInt8(start, (int)at + 2) == (byte)'E' && APTR.ReadUInt8(start, (int)at + 3) == (byte)'R' &&
                    APTR.ReadUInt8(start, (int)at + 4) == (byte)':')
                {
                    found = start.Raw + at;
                    for (var i = 0; i < 44; i++) APTR.WriteUInt8(d, 4 + i, APTR.ReadUInt8(APTR.FromPointer(found), i));
                }
        }
        APTR.WriteUInt32(d, 0, found);
        DOS.UnLoadSeg(segments.Value);
        CorrectnessProbe.Report(210);
        return 0;
    }
}
