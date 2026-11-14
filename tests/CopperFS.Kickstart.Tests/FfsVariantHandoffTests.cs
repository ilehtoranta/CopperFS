using System.Buffers.Binary;
using System.Text.Json;
using Amiga;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    [CorrectnessTheory]
    [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public async System.Threading.Tasks.Task IndexedVariantCanWriteAnOriginalFormattedVolume(int variant)
    {
        var path = Environment.GetEnvironmentVariable("COPPERFS_CORRECTNESS_MANIFEST")!;
        var pair = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))!.Profiles.Single(p => p.Name == "ffs");
        Assert.Equal(pair.OriginalSha256, Hash(File.ReadAllBytes(pair.Original)));
        var name = $"dos{variant}";
        var root = Path.Combine(Path.GetDirectoryName(path)!, "indexed-variants", name);
        var initial = new byte[32768 * 512];
        var prefix = "FfsVariantProbe::Dos" + variant;
        var image = await Run(root, name, "original-format", pair.Original, initial, true, prefix + "Format");
        var type = 0x444F5300u + (uint)variant;
        Assert.Equal(type, BinaryPrimitives.ReadUInt32BigEndian(image));
        var written = await Run(root, name, "indexed-write", pair.Candidate, image, true, prefix + "Write");
        Assert.Equal(type, BinaryPrimitives.ReadUInt32BigEndian(written));
        await Run(root, name, "original-read", pair.Original, written, false, prefix + "Read");
    }
}

public static class FfsVariantProbe
{
    public static int Dos2Format() => Run(2, 0);
    public static int Dos3Format() => Run(3, 0);
    public static int Dos4Format() => Run(4, 0);
    public static int Dos5Format() => Run(5, 0);
    public static int Dos2Write() => Run(2, 1);
    public static int Dos3Write() => Run(3, 1);
    public static int Dos4Write() => Run(4, 1);
    public static int Dos5Write() => Run(5, 1);
    public static int Dos2Read() => Run(2, 2);
    public static int Dos3Read() => Run(3, 2);
    public static int Dos4Read() => Run(4, 2);
    public static int Dos5Read() => Run(5, 2);

    private static int Run(uint variant, int phase)
    {
        var lib = Exec.OpenLibrary("dos.library", 37);
        if (!lib.HasValue) { CorrectnessProbe.Report(250); return 1; }
        DOS.DOSLibraryBase = lib.Value;
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)), 184, uint.MaxValue);
        var control = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var data = APTR.FromPointer(Exec.AllocMem(90130, Exec.MemoryFlags.Public));
        if (control.IsNull || data.IsNull) { CorrectnessProbe.Report(250); return 2; }
        CorrectnessProbe.Report(200);
        for (var shift = 24; shift >= 0; shift -= 8) CorrectnessProbe.Report((int)(control.Raw >> shift));
        var result = CorrectnessProbe.Mount(control, variant & 1);
        if (result != 0) return CorrectnessProbe.Failed(control, (uint)result);
        var type = 0x444F5300u + variant;
        APTR.WriteUInt32(APTR.FromPointer(control.Raw + 128), DosLayout.DosEnvec.DosType, type);
        if (phase == 0)
        {
            if (DOS.Inhibit("SEQ0:", 1) == 0) return CorrectnessProbe.Failed(control, 4);
            if (DOS.Format("SEQ0:", "Variant", type) == 0) return CorrectnessProbe.Failed(control, 3);
            if (DOS.Inhibit("SEQ0:", 0) == 0) return CorrectnessProbe.Failed(control, 5);
        }
        else
        {
            if (phase == 1) result = CorrectnessProbe.Mutate(control, data, false, false);
            if (result == 0) result = CorrectnessProbe.Verify(control, data);
            if (result != 0) return CorrectnessProbe.Failed(control, (uint)result);
        }
        if (DOS.Inhibit("SEQ0:", 1) == 0) return CorrectnessProbe.Failed(control, 90);
        CorrectnessProbe.Report(210);
        return 0;
    }
}
