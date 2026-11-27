using System.Text;
using Amiga;
using CopperSFS;
using CopperStart.Dos;

namespace CopperStart.FileSystem.Tests;

// DOS\6/7 long-name (LNFS) volumes are read-only. These images are built from
// Olaf Barthel's "DCFS and LNFS Low Level Data Structures" (AmigaOS wiki),
// independently of the production readers: entry headers hold a 112-byte
// name-and-comment field at Tail+328 (two BCPL strings), a comment block key
// at Tail+440 and the date at Tail+452; a comment that does not fit beside the
// name lives in a TYPE_COMMENT (64) block. The root keeps the classic fields
// and repeats the DOS type at Tail+496. Names use the international hash.
public sealed partial class VolumeGeometryTests
{
    private static readonly APTR Scratch = APTR.FromPointer(0x60000), Fib = APTR.FromPointer(0x70000);
    private const uint DirectoryKey = 900, FileKey = 901, DataKey = 902, CommentKey = 910, LinkKey = 911, SoftKey = 912;
    private const string Folder = "A drawer whose name is far longer than the thirty characters classic FFS allows";
    private const string LongFile = "1234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890.txt";
    private const string Commented = "Commented entry with an inline comment.dat";

    private sealed class LongNameVolume
    {
        public required Storage Storage;
        public required AmigaVolumeGeometry Geometry;
        public required uint DosType;
        public byte[] Payload = [];
    }

    private static uint InternationalBucket(string name, uint tableEntries)
    {
        var bytes = Encoding.Latin1.GetBytes(name); uint hash = (uint)bytes.Length;
        foreach (var b in bytes)
        {
            var c = b is >= 97 and <= 122 || b is >= 224 and <= 254 && b != 247 ? b - 32 : b;
            hash = (hash * 13 + (uint)c) & 0x7FF;
        }
        return hash % tableEntries;
    }

    private static void MarkUsed(ref Memory p, AmigaVolumeGeometry g, uint key)
    {
        var bit = key - g.ReservedBlocks; var page = bit / g.BitmapBits; bit %= g.BitmapBits;
        Assert.True(p.ReadBlock(g.RootBlock + 1 + page, Scratch));
        var at = 4 + (int)(bit / 32) * 4;
        p.WriteUInt32(Scratch, at, p.ReadUInt32(Scratch, at) & ~(1u << (int)(bit % 32)));
        Checksum(ref p, Scratch, g.BlockBytes, 0); Assert.True(p.WriteBlock(g.RootBlock + 1 + page, Scratch));
    }

    // A long-name entry header in Scratch, linked into its parent's hash chain.
    private static void LongEntry(ref Memory p, AmigaVolumeGeometry g, uint dosType, uint key, uint parent, int secondary,
        string name, string comment = "", uint commentBlock = 0, Action<Memory>? extra = null, byte? storedLength = null)
    {
        var tail = g.Tail; var bytes = g.BlockBytes;
        p.Clear(Scratch, bytes);
        p.WriteUInt32(Scratch, 0, 2); p.WriteUInt32(Scratch, 4, key);
        p.WriteUInt32(Scratch, tail + 316, 0x00050007); p.WriteUInt32(Scratch, tail + 320, 0x10);
        var nameBytes = Encoding.Latin1.GetBytes(name); var commentBytes = Encoding.Latin1.GetBytes(comment);
        p.WriteUInt8(Scratch, tail + 328, storedLength ?? (byte)nameBytes.Length);
        for (var i = 0; i < nameBytes.Length; i++) p.WriteUInt8(Scratch, tail + 329 + i, nameBytes[i]);
        p.WriteUInt8(Scratch, tail + 329 + nameBytes.Length, (byte)commentBytes.Length);
        for (var i = 0; i < commentBytes.Length; i++) p.WriteUInt8(Scratch, tail + 330 + nameBytes.Length + i, commentBytes[i]);
        p.WriteUInt32(Scratch, tail + 440, commentBlock);
        p.WriteUInt32(Scratch, tail + 452, 16000 + key); p.WriteUInt32(Scratch, tail + 456, 720); p.WriteUInt32(Scratch, tail + 460, 250);
        p.WriteUInt32(Scratch, tail + 500, parent); p.WriteUInt32(Scratch, tail + 508, unchecked((uint)secondary));
        extra?.Invoke(p);
        // Prepend to the parent's hash chain.
        Assert.True(p.ReadBlock(parent, Meta));
        var slot = 24 + (int)InternationalBucket(name, g.TableEntries) * 4;
        p.WriteUInt32(Scratch, tail + 496, p.ReadUInt32(Meta, slot));
        Checksum(ref p, Scratch, bytes, 20); Assert.True(p.WriteBlock(key, Scratch)); MarkUsed(ref p, g, key);
        p.WriteUInt32(Meta, slot, key); Checksum(ref p, Meta, bytes, 20); Assert.True(p.WriteBlock(parent, Meta));
    }

    private static LongNameVolume BuildLongNameVolume(uint dosType, uint blockBytes)
    {
        var storage = new Storage();
        Assert.True(AmigaVolumeGeometry.Create(1760, blockBytes, 2, out var g));
        var p = new Memory(storage, g);
        Format(ref p, g);
        SetDosType(ref p, dosType);
        Assert.True(p.ReadBlock(g.RootBlock, Meta));
        p.WriteUInt32(Meta, g.Tail + 496, dosType); Checksum(ref p, Meta, g.BlockBytes, 20); Assert.True(p.WriteBlock(g.RootBlock, Meta));
        var ofs = (dosType & 1) == 0;
        var payload = Enumerable.Range(0, (int)(ofs ? g.BlockBytes - 24 : g.BlockBytes) + 77).Select(i => (byte)(i * 7 + 3)).ToArray();
        var perBlock = ofs ? (int)g.BlockBytes - 24 : (int)g.BlockBytes;
        LongEntry(ref p, g, dosType, DirectoryKey, g.RootBlock, 2, Folder);
        LongEntry(ref p, g, dosType, FileKey, DirectoryKey, -3, LongFile, extra: m =>
        {
            m.WriteUInt32(Scratch, 8, 2); m.WriteUInt32(Scratch, 16, DataKey);
            m.WriteUInt32(Scratch, g.Tail + 308, DataKey); m.WriteUInt32(Scratch, g.Tail + 304, DataKey + 1);
            m.WriteUInt32(Scratch, g.Tail + 324, (uint)payload.Length);
        });
        for (var i = 0; i < 2; i++)
        {
            var chunk = payload.AsSpan(i * perBlock, Math.Min(perBlock, payload.Length - i * perBlock));
            p.Clear(Scratch, g.BlockBytes);
            var at = ofs ? 24 : 0;
            for (var j = 0; j < chunk.Length; j++) p.WriteUInt8(Scratch, at + j, chunk[j]);
            if (ofs)
            {
                p.WriteUInt32(Scratch, 0, 8); p.WriteUInt32(Scratch, 4, FileKey); p.WriteUInt32(Scratch, 8, (uint)i + 1);
                p.WriteUInt32(Scratch, 12, (uint)chunk.Length); p.WriteUInt32(Scratch, 16, i == 0 ? DataKey + 1 : 0);
                Checksum(ref p, Scratch, g.BlockBytes, 20);
            }
            Assert.True(p.WriteBlock(DataKey + (uint)i, Scratch)); MarkUsed(ref p, g, DataKey + (uint)i);
        }
        LongEntry(ref p, g, dosType, 905, g.RootBlock, -3, Commented, "short note");
        // 100 + 40 + 2 bytes exceed the 112-byte field: the comment moves out.
        LongEntry(ref p, g, dosType, 906, g.RootBlock, -3, new string('C', 100), commentBlock: CommentKey);
        p.Clear(Scratch, g.BlockBytes);
        p.WriteUInt32(Scratch, 0, 64); p.WriteUInt32(Scratch, 4, CommentKey); p.WriteUInt32(Scratch, 8, 906);
        var note = Encoding.Latin1.GetBytes("A comment stored in its own TYPE_COMMENT block");
        p.WriteUInt8(Scratch, 24, (byte)note.Length);
        for (var i = 0; i < note.Length; i++) p.WriteUInt8(Scratch, 25 + i, note[i]);
        Checksum(ref p, Scratch, g.BlockBytes, 20); Assert.True(p.WriteBlock(CommentKey, Scratch)); MarkUsed(ref p, g, CommentKey);
        LongEntry(ref p, g, dosType, LinkKey, g.RootBlock, -4, "Hard link to the long file in the long drawer",
            extra: m => m.WriteUInt32(Scratch, g.Tail + 468, FileKey));
        LongEntry(ref p, g, dosType, SoftKey, g.RootBlock, 3, "Soft link with a name of more than thirty characters", extra: m =>
        {
            var target = Encoding.Latin1.GetBytes(Folder + "/" + LongFile);
            for (var i = 0; i < target.Length; i++) m.WriteUInt8(Scratch, 24 + i, target[i]);
        });
        return new LongNameVolume { Storage = storage, Geometry = g, DosType = dosType, Payload = payload };
    }

    private static uint PutName(ref Memory p, string name, bool terminate = false)
    {
        var bytes = Encoding.Latin1.GetBytes(name);
        for (var i = 0; i < bytes.Length; i++) p.WriteUInt8(Name, i, bytes[i]);
        if (terminate) p.WriteUInt8(Name, bytes.Length, 0);
        return (uint)bytes.Length;
    }

    private static string FibName(ref Memory p, APTR fib) => FibString(ref p, fib, 8);
    private static string FibComment(ref Memory p, APTR fib) => FibString(ref p, fib, 144);
    private static string FibString(ref Memory p, APTR fib, int at)
    {
        var bytes = new byte[p.ReadUInt8(fib, at)];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = p.ReadUInt8(fib, at + 1 + i);
        return Encoding.Latin1.GetString(bytes);
    }

    [Theory]
    [InlineData(0x444F5306u, 512u)]
    [InlineData(0x444F5307u, 512u)]
    [InlineData(0x444F5306u, 1024u)]
    [InlineData(0x444F5307u, 1024u)]
    public void LongNameVolumesMountResolveReadAndExamineReadOnly(uint dosType, uint blockBytes)
    {
        var v = BuildLongNameVolume(dosType, blockBytes); var g = v.Geometry;
        var p = new Memory(v.Storage, g);
        // Callers that pass no dosType would read the classic layout, so the
        // legacy Mount keeps refusing long-name volumes.
        Assert.False(AmigaDiskFileSystemCore.Mount(ref p, Meta, out _));
        Assert.True(AmigaDiskFileSystemCore.Mount(ref p, Meta, out var type, longNames: true));
        Assert.Equal(dosType, type);

        // Case-insensitive lookup of a 104-character name below a 79-character drawer.
        var path = Folder.ToUpperInvariant() + "/" + LongFile.ToLowerInvariant();
        var file = AmigaDiskFileSystemCore.Resolve(ref p, g.RootBlock, Name, PutName(ref p, path), type, Meta, out var error);
        Assert.Equal(DOS.Error.None, error); Assert.Equal(FileKey, file);
        Assert.Equal(v.Payload.Length, AmigaDiskFileSystemCore.Read(ref p, file, type, 0, Output, (uint)v.Payload.Length + 10, Meta, Data, out error));
        Assert.Equal(DOS.Error.None, error);
        Assert.Equal(v.Payload, v.Storage.Ram.AsSpan((int)Output.Raw, v.Payload.Length).ToArray());

        Assert.True(AmigaDiskFileSystemCore.Examine(ref p, file, Fib, Meta, type));
        Assert.Equal(LongFile, FibName(ref p, Fib)); Assert.Equal("", FibComment(ref p, Fib));
        Assert.Equal((uint)v.Payload.Length, p.ReadUInt32(Fib, 124));
        Assert.Equal(0x10u, p.ReadUInt32(Fib, 116));
        Assert.Equal(16000u + FileKey, p.ReadUInt32(Fib, 132)); Assert.Equal(720u, p.ReadUInt32(Fib, 136)); Assert.Equal(250u, p.ReadUInt32(Fib, 140));
        Assert.Equal((ushort)5, p.ReadUInt16(Fib, 224)); Assert.Equal((ushort)7, p.ReadUInt16(Fib, 226));

        // The root keeps its classic name field.
        Assert.True(AmigaDiskFileSystemCore.Examine(ref p, g.RootBlock, Fib, Meta, type));
        Assert.Equal("Test", FibName(ref p, Fib));

        // ExNext lists every entry with its full name and inline or block comment.
        Assert.True(AmigaDiskFileSystemCore.Examine(ref p, g.RootBlock, Fib, Meta, type));
        var cursor = new AmigaDiskFileSystemCore.DirectoryCursor(); var listed = new Dictionary<string, (int Type, string Comment)>();
        while (AmigaDiskFileSystemCore.ExamineNext(ref p, ref cursor, g.RootBlock, Fib, Meta, Data, type) == DOS.Error.None)
            listed[FibName(ref p, Fib)] = (unchecked((int)p.ReadUInt32(Fib, 4)), FibComment(ref p, Fib));
        Assert.Equal(new Dictionary<string, (int, string)>
        {
            [Folder] = (2, ""),
            [Commented] = (-3, "short note"),
            [new string('C', 100)] = (-3, "A comment stored in its own TYPE_COMMENT block"),
            ["Hard link to the long file in the long drawer"] = (-4, ""),
            ["Soft link with a name of more than thirty characters"] = (3, ""),
        }, listed);

        // Hard links resolve to a volume path built from long names.
        var linkPath = "Hard link to the long file in the long drawer";
        PutName(ref p, linkPath, terminate: true);
        var length = AmigaFfsReadLinkCore.Read(ref p, g.RootBlock, Name, Output, 1024, type, Meta, Data, out error);
        Assert.Equal(DOS.Error.None, error);
        Assert.Equal("Test:" + Folder + "/" + LongFile, Encoding.Latin1.GetString(v.Storage.Ram, (int)Output.Raw, length));
        PutName(ref p, "Soft link with a name of more than thirty characters", terminate: true);
        length = AmigaFfsReadLinkCore.Read(ref p, g.RootBlock, Name, Output, 1024, type, Meta, Data, out error, stored: true);
        Assert.Equal(DOS.Error.None, error);
        Assert.Equal(Folder + "/" + LongFile, Encoding.Latin1.GetString(v.Storage.Ram, (int)Output.Raw, length));
    }

    [Theory]
    [InlineData(0x444F5306u)]
    [InlineData(0x444F5307u)]
    public void LongNameVolumesRefuseWritesAsWriteProtected(uint dosType)
    {
        var v = BuildLongNameVolume(dosType, 512); var g = v.Geometry;
        var p = new Memory(v.Storage, g); v.Storage.Transfers.Clear();
        var r = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = PutName(ref p, "New") };
        var w = Work();
        Assert.False(AmigaFfsWriteCore.Create(ref p, ref w, ref r, false));
        Assert.Equal(DOS.Error.DiskWriteProtected, w.Error);
        Assert.DoesNotContain(v.Storage.Transfers, t => t.Command is 3 or 25);
    }

    [Fact]
    public void LongNameReadsRejectMalformedFieldsAndOverlongComponents()
    {
        const uint type = 0x444F5307;
        var v = BuildLongNameVolume(type, 512); var g = v.Geometry;
        var p = new Memory(v.Storage, g);
        // A component longer than the 107-character FIB name is refused before any lookup.
        AmigaDiskFileSystemCore.Resolve(ref p, g.RootBlock, Name, PutName(ref p, new string('x', 108)), type, Meta, out var error);
        Assert.Equal(DOS.Error.InvalidComponentName, error);
        // A name longer than the 112-byte field can hold.
        LongEntry(ref p, g, type, 920, g.RootBlock, -3, "Broken", storedLength: 111);
        Assert.False(AmigaDiskFileSystemCore.Examine(ref p, 920, Fib, Meta, type));
        // A comment block that belongs to another header.
        LongEntry(ref p, g, type, 921, g.RootBlock, -3, "Stray comment", commentBlock: CommentKey);
        Assert.False(AmigaDiskFileSystemCore.Examine(ref p, 921, Fib, Meta, type));
        // The comment block itself still examines its owner correctly.
        Assert.True(AmigaDiskFileSystemCore.Examine(ref p, 906, Fib, Meta, type));
        Assert.Equal("A comment stored in its own TYPE_COMMENT block", FibComment(ref p, Fib));
        // Examine restores the entry header after reading its comment block.
        Assert.Equal(906u, p.ReadUInt32(Meta, 4)); Assert.Equal(2u, p.ReadUInt32(Meta));
        // An LNFS boot block over a root without the repeated signature.
        Assert.True(p.ReadBlock(g.RootBlock, Meta)); p.WriteUInt32(Meta, g.Tail + 496, 0);
        Checksum(ref p, Meta, g.BlockBytes, 20); Assert.True(p.WriteBlock(g.RootBlock, Meta));
        Assert.False(AmigaDiskFileSystemCore.Mount(ref p, Meta, out _, longNames: true));
    }

    [Fact]
    public void ClassicVolumesKeepTheThirtyCharacterLimit()
    {
        var storage = new Storage();
        Assert.True(AmigaVolumeGeometry.Create(1760, 512, 2, out var g));
        var p = new Memory(storage, g); Format(ref p, g);
        Assert.True(AmigaDiskFileSystemCore.Mount(ref p, Meta, out var type, longNames: true)); Assert.Equal(0x444F5301u, type);
        AmigaDiskFileSystemCore.Resolve(ref p, g.RootBlock, Name, PutName(ref p, new string('x', 31)), type, Meta, out var error);
        Assert.Equal(DOS.Error.InvalidComponentName, error);
    }
}
