using System.Buffers.Binary;
using System.Text;
using Amiga;

namespace CopperFS.Kickstart.Tests;

public sealed partial class OriginalHandlerCorrectnessTests
{
    // Kickstart 3.1's filesystem cannot read DOS\6/7, so there is no ROM arm:
    // Kickstart 3.1's dos.library drives CopperFFS over a long-name volume
    // built from Olaf Barthel's "DCFS and LNFS Low Level Data Structures"
    // (AmigaOS wiki), independently of CopperFFS's own code. CopperFFS mounts
    // it read-only: lookups, reads, Examine, ExNext and ExAll see the long
    // names and comments, writes fail with 214 and the image stays unchanged.
    [LinkReferenceTheory]
    [InlineData(0x444F5306u)] [InlineData(0x444F5307u)]
    public async System.Threading.Tasks.Task CopperFfsReadsLongNameVolumesReadOnly(uint dosType)
    {
        var handler = Environment.GetEnvironmentVariable("COPPERFS_FFS_HANDLER")!;
        var root = (Environment.GetEnvironmentVariable("COPPERFS_LINK_ARTIFACTS") is { Length: > 0 } configured
            ? configured : Path.Combine(Path.GetTempPath(), "copperfs-link-reference")) + "-lnfs";
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var image = LongNameImage.Build(dosType);
        var label = "copper-lnfs" + (dosType & 0xFF);
        var after = await Run(root, (dosType & 1) == 0 ? "ofs" : "ffs", label, handler, image, true, "LongNameProbe::Run");
        var data = ProbeData(root, label);
        output.WriteLine(string.Join(",", data.Take(LongNameProbe.Words)));
        uint digest = 0; foreach (var b in LongNameImage.Payload) digest = digest * 31 + b;
        var rootNames = (uint)(LongNameImage.Folder.Length + LongNameImage.Commented.Length + LongNameImage.BlockCommented.Length);
        Assert.Equal(new uint[]
        {
            1, (uint)LongNameImage.LongFile.Length, (uint)LongNameImage.Payload.Length,
            (uint)LongNameImage.Payload.Length, digest,
            3, rootNames, (uint)LongNameImage.BlockComment.Length,
            0, 214, 0, 214, 80,
            1, 3, rootNames, (uint)(LongNameImage.InlineComment.Length + LongNameImage.BlockComment.Length), 0,
        }, data.Take(LongNameProbe.Words));
        Assert.True(image.AsSpan().SequenceEqual(after), "The read-only volume changed on disk.");
        Directory.Delete(root, true);
    }
}

internal static class LongNameImage
{
    public const string Folder = "A drawer whose name is far longer than the thirty characters classic FFS allows";
    public const string LongFile = "1234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890.txt";
    public const string Commented = "Commented entry with an inline comment.dat";
    public const string InlineComment = "short note";
    public const string BlockCommented = "Entry whose comment does not fit beside its one hundred character name, so it uses a comment blk";
    public const string BlockComment = "A comment stored in its own TYPE_COMMENT block";
    public static readonly byte[] Payload = Enumerable.Range(0, 1100).Select(i => (byte)(i * 7 + 3)).ToArray();

    private const int Bytes = 512, Blocks = 32768, Reserved = 2, Root = 16384, Maps = 9;

    private static void Put(byte[] d, int block, int at, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(block * Bytes + at), value);
    private static uint Get(byte[] d, int block, int at) => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(block * Bytes + at));
    private static void Seal(byte[] d, int block, int at)
    {
        Put(d, block, at, 0); uint sum = 0;
        for (var i = 0; i < Bytes; i += 4) sum = unchecked(sum + Get(d, block, i));
        Put(d, block, at, unchecked(0u - sum));
    }
    private static void Text(byte[] d, int block, int at, string s)
    {
        var b = Encoding.Latin1.GetBytes(s); d[block * Bytes + at] = (byte)b.Length; b.CopyTo(d, block * Bytes + at + 1);
    }
    private static uint Bucket(string name)
    {
        uint hash = (uint)name.Length;
        foreach (var b in Encoding.Latin1.GetBytes(name))
            hash = (hash * 13 + (uint)(b is >= 97 and <= 122 || b is >= 224 and <= 254 && b != 247 ? b - 32 : b)) & 0x7FF;
        return hash % 72;
    }
    private static void Used(byte[] d, int key)
    {
        var bit = key - Reserved; var page = Root + 1 + bit / 4064; bit %= 4064;
        var at = 4 + bit / 32 * 4; Put(d, page, at, Get(d, page, at) & ~(1u << (bit % 32)));
    }

    private static void Entry(byte[] d, int key, int parent, int secondary, string name, string comment = "", int commentBlock = 0)
    {
        Put(d, key, 0, 2); Put(d, key, 4, (uint)key); Put(d, key, 320, 0);
        var nameBytes = Encoding.Latin1.GetBytes(name);
        Text(d, key, 328, name); Text(d, key, 329 + nameBytes.Length, comment);
        Put(d, key, 440, (uint)commentBlock);
        Put(d, key, 452, 16000); Put(d, key, 456, 720); Put(d, key, 460, 250);
        Put(d, key, 500, (uint)parent); Put(d, key, 508, unchecked((uint)secondary));
        var slot = 24 + (int)Bucket(name) * 4;
        Put(d, key, 496, Get(d, parent, slot)); Put(d, parent, slot, (uint)key);
        Used(d, key);
    }

    public static byte[] Build(uint dosType)
    {
        var d = new byte[Blocks * Bytes];
        var ofs = (dosType & 1) == 0;
        Put(d, 0, 0, dosType);
        // Root: classic fields plus the repeated signature at offset 496.
        Put(d, Root, 0, 2); Put(d, Root, 12, 72); Put(d, Root, 312, uint.MaxValue);
        for (var i = 0; i < Maps; i++) Put(d, Root, 316 + i * 4, (uint)(Root + 1 + i));
        Put(d, Root, 420, 16000); Put(d, Root, 472, 16000); Put(d, Root, 484, 16000);
        Text(d, Root, 432, "Long");
        Put(d, Root, 496, dosType); Put(d, Root, 508, 1);
        for (var page = 0; page < Maps; page++)
            for (var bit = 0; bit < 4064; bit++)
            {
                var key = Reserved + page * 4064 + bit;
                if (key >= Blocks) break;
                var at = 4 + bit / 32 * 4; Put(d, Root + 1 + page, at, Get(d, Root + 1 + page, at) | 1u << (bit % 32));
            }
        Used(d, Root);
        for (var i = 0; i < Maps; i++) Used(d, Root + 1 + i);
        const int folder = 16400, file = 16401, data = 16402, commented = 16410, blockCommented = 16411, comment = 16412;
        Entry(d, folder, Root, 2, Folder);
        Entry(d, file, folder, -3, LongFile);
        var per = ofs ? Bytes - 24 : Bytes; var count = (Payload.Length + per - 1) / per;
        Put(d, file, 8, (uint)count); Put(d, file, 16, data); Put(d, file, 324, (uint)Payload.Length);
        for (var i = 0; i < count; i++)
        {
            Put(d, file, 308 - i * 4, (uint)(data + i));
            var chunk = Payload.AsSpan(i * per, Math.Min(per, Payload.Length - i * per));
            chunk.CopyTo(d.AsSpan((data + i) * Bytes + (ofs ? 24 : 0)));
            if (ofs)
            {
                Put(d, data + i, 0, 8); Put(d, data + i, 4, file); Put(d, data + i, 8, (uint)i + 1);
                Put(d, data + i, 12, (uint)chunk.Length); Put(d, data + i, 16, i + 1 < count ? (uint)(data + i + 1) : 0);
                Seal(d, data + i, 20);
            }
            Used(d, data + i);
        }
        Entry(d, commented, Root, -3, Commented, InlineComment);
        Entry(d, blockCommented, Root, -3, BlockCommented, commentBlock: comment);
        Put(d, comment, 0, 64); Put(d, comment, 4, comment); Put(d, comment, 8, blockCommented);
        Text(d, comment, 24, BlockComment); Seal(d, comment, 20); Used(d, comment);
        foreach (var key in new[] { folder, file, commented, blockCommented, Root }) Seal(d, key, 20);
        for (var i = 0; i < Maps; i++) Seal(d, Root + 1 + i, 0);
        return d;
    }
}

public static class LongNameProbe
{
    public const int Words = 18;

    private static uint Digest(APTR buffer, int length)
    {
        uint sum = 0;
        for (var i = 0; i < length; i++) sum = sum * 31 + APTR.ReadUInt8(buffer, i);
        return sum;
    }

    // dos.library hands applications FIB names and comments as C strings.
    private static uint Length(APTR fib, int at, int limit)
    {
        uint n = 0;
        while (n < limit && APTR.ReadUInt8(fib, at + (int)n) != 0) n++;
        return n;
    }

    public static int Run()
    {
        var library = Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) { CorrectnessProbe.Report(250); return 10; }
        DOS.DOSLibraryBase = library.Value;
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)), 184, uint.MaxValue);
        var control = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var buffer = APTR.FromPointer(Exec.AllocMem(4096, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var fib = APTR.FromPointer(Exec.AllocMem(260, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        var exall = APTR.FromPointer(Exec.AllocMem(64, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        if (control.IsNull || buffer.IsNull || fib.IsNull || exall.IsNull) { CorrectnessProbe.Report(250); return 11; }
        CorrectnessProbe.Report(200);
        for (var shift = 24; shift >= 0; shift -= 8) CorrectnessProbe.Report((int)(control.Raw >> shift));
        var setup = CorrectnessProbe.Mount(control, 1);
        if (setup != 0) return CorrectnessProbe.Failed(control, (uint)setup);
        var d = APTR.FromPointer(control.Raw + 512);
        // Case-insensitive lookup of a 104-character name in a 79-character drawer.
        var locked = DOS.Lock("SEQ0:a drawer whose name is far longer than the thirty characters classic ffs allows/" +
            "1234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890.TXT", DOS.LockMode.Shared);
        if (locked.HasValue)
        {
            APTR.WriteUInt32(d, 0, 1);
            if (DOS.Examine(locked.Value, fib) != 0)
            { APTR.WriteUInt32(d, 4, Length(fib, 8, 108)); APTR.WriteUInt32(d, 8, APTR.ReadUInt32(fib, 124)); }
            DOS.UnLock(locked.Value);
        }
        var file = DOS.Open("SEQ0:A drawer whose name is far longer than the thirty characters classic FFS allows/" +
            "1234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890.txt", DOS.FileMode.OldFile);
        if (file.HasValue)
        {
            var read = DOS.Read(file.Value, buffer, 4096);
            APTR.WriteUInt32(d, 12, unchecked((uint)read));
            if (read > 0) APTR.WriteUInt32(d, 16, Digest(buffer, read));
            DOS.Close(file.Value);
        }
        var root = DOS.Lock("SEQ0:", DOS.LockMode.Shared);
        if (!root.HasValue) return CorrectnessProbe.Failed(control, 20);
        if (DOS.Examine(root.Value, fib) != 0)
        {
            uint count = 0, names = 0;
            while (DOS.ExNext(root.Value, fib) != 0) { count++; names += Length(fib, 8, 108); }
            APTR.WriteUInt32(d, 20, count); APTR.WriteUInt32(d, 24, names);
        }
        var commented = DOS.Lock("SEQ0:Entry whose comment does not fit beside its one hundred character name, so it uses a comment blk", DOS.LockMode.Shared);
        if (commented.HasValue)
        {
            if (DOS.Examine(commented.Value, fib) != 0) APTR.WriteUInt32(d, 28, Length(fib, 144, 80));
            DOS.UnLock(commented.Value);
        }
        // Writes are refused as write-protected.
        var created = DOS.Open("SEQ0:New", DOS.FileMode.NewFile);
        APTR.WriteUInt32(d, 32, created.HasValue ? 1u : 0u); APTR.WriteUInt32(d, 36, unchecked((uint)DOS.IoErr()));
        if (created.HasValue) DOS.Close(created.Value);
        APTR.WriteUInt32(d, 40, unchecked((uint)DOS.DeleteFile("SEQ0:Commented entry with an inline comment.dat")));
        APTR.WriteUInt32(d, 44, unchecked((uint)DOS.IoErr()));
        if (DOS.Info(root.Value, buffer) != 0) APTR.WriteUInt32(d, 48, APTR.ReadUInt32(buffer, 8));
        // ExAll (ED_COMMENT) through the handler's own packet.
        var port = DOS.DeviceProc("SEQ0:");
        int r; uint calls = 0, entries = 0, nameBytes = 0, commentBytes = 0;
        do
        {
            r = DOS.DoPkt(port, (int)DosPacketAction.ExamineAll, unchecked((int)root.Value.Raw), unchecked((int)buffer.Raw), 4000, 6, unchecked((int)exall.Raw));
            calls++;
            var count = APTR.ReadUInt32(exall, DosLayout.ExAllControl.Entries);
            var record = buffer;
            for (uint n = 0; n < count && record.IsNotNull; n++)
            {
                entries++;
                var name = APTR.FromPointer(APTR.ReadUInt32(record, DosLayout.ExAllData.Name));
                for (var i = 0; i < 108 && APTR.ReadUInt8(name, i) != 0; i++) nameBytes++;
                var comment = APTR.FromPointer(APTR.ReadUInt32(record, DosLayout.ExAllData.Comment));
                for (var i = 0; i < 80 && APTR.ReadUInt8(comment, i) != 0; i++) commentBytes++;
                record = APTR.FromPointer(APTR.ReadUInt32(record, DosLayout.ExAllData.Next));
            }
        }
        while (r != 0 && calls < 20);
        APTR.WriteUInt32(d, 52, calls); APTR.WriteUInt32(d, 56, entries);
        APTR.WriteUInt32(d, 60, nameBytes); APTR.WriteUInt32(d, 64, commentBytes); APTR.WriteUInt32(d, 68, unchecked((uint)r));
        DOS.UnLock(root.Value);
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (DOS.Inhibit("SEQ0:", 1) != 0) { CorrectnessProbe.Report(210); return 0; }
            DOS.Delay(10);
        }
        return CorrectnessProbe.Failed(control, 90);
    }
}
