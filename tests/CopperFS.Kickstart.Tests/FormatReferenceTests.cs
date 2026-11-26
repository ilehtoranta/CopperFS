using System.Buffers.Binary;
using Amiga;
using CopperSFS;
using CopperStart.Dos;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    // Kickstart 3.1's own Format is the reference for which blocks a quick
    // format writes. Both formats run on the same image, pre-filled with a
    // per-block pattern; they must change the same blocks with the same
    // contents, apart from the volume dates and checksums that cover them.
    [LinkReferenceTheory]
    [InlineData(0u)] [InlineData(1u)] [InlineData(5u)]
    public async System.Threading.Tasks.Task CopperFormatMatchesKickstartFormat(uint variant)
    {
        // The probe runs only Kickstart's Format; the handler image is unused.
        var handler = Environment.GetEnvironmentVariable("COPPERFS_FFS_HANDLER")!;
        var root = (Environment.GetEnvironmentVariable("COPPERFS_LINK_ARTIFACTS") is { Length: > 0 } configured
            ? configured : Path.Combine(Path.GetTempPath(), "copperfs-link-reference")) + "-format" + variant;
        if (Directory.Exists(root)) Directory.Delete(root, true);
        const int blocks = 32768;
        var initial = new byte[blocks * 512];
        for (var block = 0; block < blocks; block++)
            for (var at = 0; at < 512; at += 4)
                BinaryPrimitives.WriteUInt32BigEndian(initial.AsSpan(block * 512 + at), 0xC3000000u | (uint)block);
        var rom = await Run(root, "ffs", "rom-format", handler, initial, true, "FormatProbe::Dos" + variant);
        var type = 0x444F5300u + variant;
        Assert.Equal(type, BinaryPrimitives.ReadUInt32BigEndian(rom));
        var copper = (byte[])initial.Clone();
        Assert.True(AmigaVolumeGeometry.Create(blocks, 512, 2, out var g));
        var image = new FormatImage(copper, g);
        "Format"u8.ToArray().CopyTo(image.Ram, (int)FormatImage.Name);
        Assert.True(AmigaDiskFileSystemFormatCore.Format(ref image, type, APTR.FromPointer(FormatImage.Name), 6, 1, 2, 3,
            APTR.FromPointer(FormatImage.Scratch), out var error), error.ToString());
        var differences = FormatDifferences(initial, rom, copper, g, variant);
        foreach (var line in differences) output.WriteLine(line);
        Assert.True(differences.Count == 0, "Differs from Kickstart Format:\n" + string.Join("\n", differences.Take(400)));
        Directory.Delete(root, true);
    }

    // Blocks both formats changed must agree, except the root's dates and
    // checksum and the last bitmap page past the volume's end, where
    // Kickstart rounds the final word up and leaves buffer contents.
    private List<string> FormatDifferences(byte[] initial, byte[] rom, byte[] copper, AmigaVolumeGeometry g, uint variant)
    {
        var blocks = (int)g.BlockCount;
        var maps = (g.BlockCount - g.ReservedBlocks - 1) / g.BitmapBits + 1;
        var lastMap = (int)(g.RootBlock + (variant >= 4 ? 2 : 1) + maps - 1);
        var validBits = (int)(g.BlockCount - g.ReservedBlocks - (maps - 1) * g.BitmapBits);
        bool Skipped(int block, int at) =>
            block == g.RootBlock && (at == 20 || at is >= 420 and < 432 || at is >= 472 and < 496) ||
            block == lastMap && (at == 0 || (at - 4) * 8 + 32 > validBits);
        uint ValidLastWord(byte[] disk, int at)
        {
            var bits = validBits - (at - 4) * 8;
            var word = BinaryPrimitives.ReadUInt32BigEndian(disk.AsSpan(lastMap * 512 + at));
            return bits <= 0 ? 0 : bits >= 32 ? word : word & ((1u << bits) - 1);
        }
        // The partially valid word still has to agree on its valid bits.
        var partial = 4 + (validBits / 32) * 4;
        if (validBits % 32 != 0 && ValidLastWord(rom, partial) != ValidLastWord(copper, partial))
            output.WriteLine($"last bitmap word differs in valid bits: rom {ValidLastWord(rom, partial):X8} copper {ValidLastWord(copper, partial):X8}");
        Assert.True(validBits % 32 == 0 || ValidLastWord(rom, partial) == ValidLastWord(copper, partial));
        var differences = new List<string>();
        for (var block = 0; block < blocks; block++)
        {
            var changedRom = !rom.AsSpan(block * 512, 512).SequenceEqual(initial.AsSpan(block * 512, 512));
            var changedCopper = !copper.AsSpan(block * 512, 512).SequenceEqual(initial.AsSpan(block * 512, 512));
            if (changedRom != changedCopper) { differences.Add($"block {block}: rom {(changedRom ? "writes" : "keeps")}, copper {(changedCopper ? "writes" : "keeps")}"); continue; }
            for (var at = 0; at < 512 && changedRom; at += 4)
                if (!Skipped(block, at) && BinaryPrimitives.ReadUInt32BigEndian(rom.AsSpan(block * 512 + at)) !=
                    BinaryPrimitives.ReadUInt32BigEndian(copper.AsSpan(block * 512 + at)))
                {
                    differences.Add($"block {block} +{at}: rom {BinaryPrimitives.ReadUInt32BigEndian(rom.AsSpan(block * 512 + at)):X8} " +
                        $"copper {BinaryPrimitives.ReadUInt32BigEndian(copper.AsSpan(block * 512 + at)):X8}");
                    if (differences.Count > 400) break;
                }
            if (differences.Count > 400) break;
        }
        return differences;
    }

    // A minimal host platform over a disk image and a small private RAM.
    private struct FormatImage(byte[] image, AmigaVolumeGeometry geometry) : IAmigaFormatFileSystemPlatform
    {
        public const uint Name = 0x100, Scratch = 0x1000;
        public readonly byte[] Ram = new byte[0x4000];
        public AmigaVolumeGeometry Geometry => geometry;
        public byte ReadUInt8(APTR a, int o = 0) => Ram[(int)a.Raw + o];
        public ushort ReadUInt16(APTR a, int o = 0) => BinaryPrimitives.ReadUInt16BigEndian(Ram.AsSpan((int)a.Raw + o));
        public uint ReadUInt32(APTR a, int o = 0) => BinaryPrimitives.ReadUInt32BigEndian(Ram.AsSpan((int)a.Raw + o));
        public void WriteUInt8(APTR a, int o, byte v) => Ram[(int)a.Raw + o] = v;
        public void WriteUInt16(APTR a, int o, ushort v) => BinaryPrimitives.WriteUInt16BigEndian(Ram.AsSpan((int)a.Raw + o), v);
        public void WriteUInt32(APTR a, int o, uint v) => BinaryPrimitives.WriteUInt32BigEndian(Ram.AsSpan((int)a.Raw + o), v);
        public void Clear(APTR a, uint n) => Ram.AsSpan((int)a.Raw, (int)n).Clear();
        public void Copy(APTR s, APTR d, uint n) => Ram.AsSpan((int)s.Raw, (int)n).CopyTo(Ram.AsSpan((int)d.Raw));
        public bool IsMapped(APTR a, uint n) => a.Raw <= Ram.Length && n <= Ram.Length - a.Raw;
        public bool ReadBlock(uint block, APTR d) { image.AsSpan((int)block * 512, 512).CopyTo(Ram.AsSpan((int)d.Raw)); return true; }
        public bool WriteBlock(uint block, APTR s) { Ram.AsSpan((int)s.Raw, 512).CopyTo(image.AsSpan((int)block * 512)); return true; }
        public bool WriteFormatBlock(uint block, APTR s) => WriteBlock(block, s);
        public bool ReadMetadataBlock(uint block, uint id, APTR d) => ReadBlock(block, d);
        public APTR ReadMetadataView(uint block, uint id, APTR scratch) => ReadBlock(block, scratch) ? scratch : APTR.Null;
        public uint ReadDataBlocks(uint block, uint count, APTR d) => ReadDataBlocks(block, count, d, false);
        public uint ReadDataBlocks(uint block, uint count, APTR d, bool readAhead)
        {
            for (var i = 0u; i < count; i++) ReadBlock(block + i, APTR.FromPointer(d.Raw + i * 512));
            return count;
        }
        public bool WriteDataBlocks(uint block, uint count, APTR s)
        {
            for (var i = 0u; i < count; i++) WriteBlock(block + i, APTR.FromPointer(s.Raw + i * 512));
            return true;
        }
        public bool WriteProtected() => false;
        public bool Flush() => true;
    }
}

public static class FormatProbe
{
    public static int Dos0() => Run(0);
    public static int Dos1() => Run(1);
    public static int Dos5() => Run(5);

    private static int Run(uint variant)
    {
        var library = Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) { CorrectnessProbe.Report(250); return 10; }
        DOS.DOSLibraryBase = library.Value;
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)), 184, uint.MaxValue);
        var control = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        if (control.IsNull) { CorrectnessProbe.Report(250); return 11; }
        CorrectnessProbe.Report(200);
        for (var shift = 24; shift >= 0; shift -= 8) CorrectnessProbe.Report((int)(control.Raw >> shift));
        var setup = CorrectnessProbe.Mount(control, 1, romFileSystem: true);
        if (setup != 0) return CorrectnessProbe.Failed(control, (uint)setup);
        var type = 0x444F5300u + variant;
        APTR.WriteUInt32(APTR.FromPointer(control.Raw + 128), DosLayout.DosEnvec.DosType, type);
        if (DOS.Inhibit("SEQ0:", 1) == 0) return CorrectnessProbe.Failed(control, 4);
        if (DOS.Format("SEQ0:", "Format", type) == 0) return CorrectnessProbe.Failed(control, 3);
        if (DOS.Inhibit("SEQ0:", 0) == 0) return CorrectnessProbe.Failed(control, 5);
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (DOS.Inhibit("SEQ0:", 1) != 0) { CorrectnessProbe.Report(210); return 0; }
            DOS.Delay(10);
        }
        return CorrectnessProbe.Failed(control, 90);
    }
}
