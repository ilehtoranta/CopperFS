using System.Buffers;
using System.Text;
using static CopperFS.Cpx4.Reference.Layout;

namespace CopperFS.Cpx4.Reference;

public ref partial struct Cpx4Volume<TDevice> where TDevice : IBlockDevice
{
    private static bool Alias(scoped ReadOnlySpan<byte> name, out ulong id)
    {
        id = 0; if (name.Length != 19 || name[0] != '_' || name[1] != '_' || name[2] is not ((byte)'u' or (byte)'U')) return false;
        for (int i = 3; i < 19; i++)
        {
            int c = name[i], digit = c is >= 48 and <= 57 ? c - 48 : c is >= 97 and <= 102 ? c - 87 : c is >= 65 and <= 70 ? c - 55 : -1;
            if (digit < 0) return false; id = id << 4 | (uint)digit;
        }
        return true;
    }
    private int LookupName(scoped ReadOnlySpan<byte> name)
    { if (Alias(name, out _)) { name.CopyTo(NameScratch); return name.Length; } return LegacyName(name, NameScratch); }
    private static bool ValidateUtf8Name(scoped ReadOnlySpan<byte> name, out int legacyLength)
    {
        legacyLength = 0;
        if (name.Length is < 1 or > NameBytes || name.SequenceEqual("."u8) || name.SequenceEqual(".."u8) || Alias(name, out _)) return false;
        int at = 0; bool representable = true;
        while (at < name.Length)
        {
            if (Rune.DecodeFromUtf8(name[at..], out Rune r, out int used) != OperationStatus.Done || r.Value is 0 or 47 or 58) return false;
            at += used; representable &= r.Value <= 255; legacyLength++;
        }
        if (!representable || legacyLength > 255) legacyLength = ushort.MaxValue;
        return true;
    }
    private static int LegacyName(scoped ReadOnlySpan<byte> source, scoped Span<byte> utf8)
    {
        if (source.Length is < 1 or > 255 || source.SequenceEqual("."u8) || source.SequenceEqual(".."u8)) return -1;
        // Reserve exactly the native alias shape, not an entire prefix.
        if (Alias(source, out _)) return -1;
        int n = 0;
        foreach (byte c in source)
        {
            if (c is 0 or (byte)'/' or (byte)':') return -1;
            if (c < 128) utf8[n++] = c;
            else { utf8[n++] = (byte)(0xc0 | c >> 6); utf8[n++] = (byte)(0x80 | c & 63); }
        }
        return n;
    }
    private static int Scalar(scoped ReadOnlySpan<byte> text, ref int at)
    {
        if (Rune.DecodeFromUtf8(text[at..], out Rune r, out int used) != OperationStatus.Done) throw new InvalidDataException("Invalid stored UTF-8.");
        at += used; int c = r.Value;
        return c is >= 65 and <= 90 or >= 192 and <= 214 or >= 216 and <= 222 ? c + 32 : c;
    }
    private static uint NameHash(scoped ReadOnlySpan<byte> name)
    {
        uint crc = uint.MaxValue; int at = 0; Span<byte> encoded = stackalloc byte[4];
        while (at < name.Length)
        {
            int n = new Rune(Scalar(name, ref at)).EncodeToUtf8(encoded);
            for (int i = 0; i < n; i++) { crc ^= encoded[i]; for (int bit = 0; bit < 8; bit++) crc = crc >> 1 ^ (0x82f63b78U & (uint)-(int)(crc & 1)); }
        }
        return ~crc;
    }
    private static bool SameName(scoped ReadOnlySpan<byte> a, scoped ReadOnlySpan<byte> b)
    {
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length) if (Scalar(a, ref x) != Scalar(b, ref y)) return false;
        return x == a.Length && y == b.Length;
    }
    private int StoredName(scoped ReadOnlySpan<byte> entry, scoped Span<byte> destination)
    {
        int length = U16(entry, 32); ulong address = U64(entry, 24);
        if (length is < 1 or > NameBytes) throw new InvalidDataException("Invalid name length.");
        if (address == 0)
        { if (length > 80) throw new InvalidDataException("Invalid inline name."); entry.Slice(48, length).CopyTo(destination); }
        else
        {
            Load(address, B(4));
            if (!IsPage(B(4), PageType.Name, address, U64(entry, 8)) || U16(B(4), 64) != length) throw new InvalidDataException("Invalid external name.");
            B(4).Slice(68, length).CopyTo(destination);
        }
        return length;
    }
    private bool GetObject(ulong id, scoped Span<byte> record) => TreeGet(StateValue(80), TreeKind.Object, 0, id, 0, record);
    private void SaveObject(scoped ReadOnlySpan<byte> record) => TreeReplace(StateValue(80), TreeKind.Object, 0, record);
    private bool FindName(ulong parent, scoped ReadOnlySpan<byte> name, scoped Span<byte> directory, scoped Span<byte> entry)
    {
        if (!GetObject(parent, directory) || directory[84] != 2) return false;
        using var frame = RentRecords(System.Runtime.CompilerServices.Unsafe.SizeOf<NameSearch>());
        ref var search = ref System.Runtime.InteropServices.MemoryMarshal.AsRef<NameSearch>(frame.Bytes);
        InitializeNameSearch(ref search, parent, U64(directory, 32), name);
        while (true)
        {
            var result = SearchNameStep(ref search, name, out _);
            if (result == ScanResult.Found) { ((ReadOnlySpan<byte>)search.PendingEntry).CopyTo(entry); return true; }
            if (result == ScanResult.End) return false;
            if (result == ScanResult.Invalidated) throw new InvalidOperationException("Stale name continuation.");
            Cooperate();
        }
    }
    private void MakeEntry(scoped Span<byte> entry, ulong id, ulong target, scoped ReadOnlySpan<byte> name, int legacyLength)
    {
        entry.Clear(); Put64(entry, 0, (ulong)NameHash(name) << 32); Put64(entry, 8, id); Put64(entry, 16, target);
        Put16(entry, 32, (ushort)name.Length); Put16(entry, 36, (ushort)legacyLength);
        if (name.Length <= 80) name.CopyTo(entry[48..]);
        else
        {
            ulong address = TakeMetadata(); Header(B(4), PageType.Name, address, id, generation + 1);
            Put16(B(4), 64, (ushort)name.Length); name.CopyTo(B(4)[68..]); Stage(address, B(4)); Put64(entry, 24, address);
        }
    }
    public bool TryGetInfo(scoped ReadOnlySpan<byte> name, out FileInfo info) => TryGetInfo(RootDirectory, name, out info);
    public bool TryGetInfo(ulong parent, scoped ReadOnlySpan<byte> name, out FileInfo info)
    {
        Ready(); info = default; int n;
        if (Alias(name, out _)) { name.CopyTo(NameScratch); n = name.Length; } else n = LegacyName(name, NameScratch);
        if (n < 0) return false;
        using var recordFrame1 = RentRecords(256 + 128 + 256); Span<byte> directory = recordFrame1.Bytes.Slice(0, 256); Span<byte> entry = recordFrame1.Bytes.Slice(0 + 256, 128); Span<byte> record = recordFrame1.Bytes.Slice(0 + 256 + 128, 256);
        if (!FindName(parent, NameScratch[..n], directory, entry) || !GetObject(U64(entry, 16), record)) return false;
        info = Info(record); return true;
    }
    private FileInfo Info(scoped ReadOnlySpan<byte> r)
    {
        ulong count = U16(r, 90);
        if (r[85] == 2)
        { count = 0; ulong key = 0; using var recordFrame2 = RentRecords(24); Span<byte> extent = recordFrame2.Bytes.Slice(0, 24); while (NextExtent(r, key, count == 0, extent)) { key = U64(extent, 0); count++; } }
        return new(U64(r, 0), U64(r, 8), count, r[85] == 0, r[84] == 2);
    }
    public bool TryEnumerate(ulong parent, ref DirectoryCursor cursor, scoped Span<byte> utf8Name, out int nameLength, out FileInfo info)
    {
        Ready(); nameLength = 0; info = default;
        if (utf8Name.Length < NameBytes) throw new ArgumentException("Provide 768 bytes for a stored name.");
        using var recordFrame3 = RentRecords(256 + 128); Span<byte> record = recordFrame3.Bytes.Slice(0, 256); Span<byte> entry = recordFrame3.Bytes.Slice(0 + 256, 128);
        if (!GetObject(parent, record) || record[84] != 2) return false;
        if (!TreeNext(U64(record, 32), TreeKind.Directory, parent, cursor.High, cursor.Low, !cursor.Started, entry)) return false;
        nameLength = StoredName(entry, utf8Name);
        cursor.High = U64(entry, 0); cursor.Low = U64(entry, 8); cursor.Started = true;
        if (!GetObject(U64(entry, 16), record)) throw new InvalidDataException("Dangling directory entry.");
        info = Info(record); return true;
    }
    public bool TryEnumerateLegacy(ulong parent, ref DirectoryCursor cursor, scoped Span<byte> name, out int nameLength, out FileInfo info)
    {
        if (name.Length < 108) throw new ArgumentException("Provide the 108-byte DOS name buffer.");
        nameLength = 0;
        if (!TryEnumerate(parent, ref cursor, OtherNameScratch, out int utf8Length, out info)) return false;
        int at = 0; bool alias = false;
        while (at < utf8Length)
        {
            if (Rune.DecodeFromUtf8(OtherNameScratch.Slice(at, utf8Length - at), out Rune r, out int used) != OperationStatus.Done) throw new InvalidDataException("Invalid name.");
            at += used; if (r.Value > 255 || nameLength == 107) { alias = true; break; } name[nameLength++] = (byte)r.Value;
        }
        if (alias)
        {
            "__u"u8.CopyTo(name); ulong id = cursor.Low;
            for (int i = 18; i >= 3; i--) { int digit = (int)(id & 15); name[i] = (byte)(digit < 10 ? 48 + digit : 87 + digit); id >>= 4; }
            nameLength = 19;
        }
        name[nameLength] = 0; return true;
    }
}
