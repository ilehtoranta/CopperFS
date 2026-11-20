using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace CopperFS.Prototype.Tests;

// Independent decoder: deliberately no production layout, CRC, tree or allocator helpers.
internal sealed class IndependentImage
{
    private readonly byte[] image;
    internal readonly int A, P;
    private readonly ulong units, q, generation;
    private readonly HashSet<ulong> owners = [];
    internal readonly Dictionary<ulong, byte[]> Objects = [];
    internal readonly Dictionary<(ulong Parent, string Name), ulong> Names = [];
    internal readonly List<ulong> Metadata = [];
    internal static ushort S(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16BigEndian(b[o..]);
    internal static uint I(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b[o..]);
    internal static ulong L(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt64BigEndian(b[o..]);
    private static uint Checksum(ReadOnlySpan<byte> page)
    {
        uint v = ~0u;
        for (int i = 0; i < page.Length; i++)
        { v ^= i >= 8 && i < 12 ? 0u : page[i]; for (int j = 0; j < 8; j++) v = (v & 1) == 0 ? v >> 1 : (v >> 1) ^ 0x82f63b78; }
        return ~v;
    }
    private byte[] Page(ulong at, int type, ulong owner)
    {
        Assert.Equal(0UL, at % q); Assert.True(at + q <= units);
        byte[] p = image.AsSpan(checked((int)(at * (uint)A)), P).ToArray();
        Assert.Equal(0x43505833u, I(p, 0)); Assert.Equal(3, S(p, 6)); Assert.Equal(type, S(p, 4));
        Assert.Equal(at, L(p, 16)); Assert.Equal(owner, L(p, 24)); Assert.True(L(p, 32) <= generation);
        Assert.Equal(Checksum(p), I(p, 8)); return p;
    }
    private void Own(ulong at, ulong count)
    { Assert.True(at < units && count <= units - at); for (ulong n = 0; n < count; n++) Assert.True(owners.Add(at + n), $"cross-link at {at + n}"); }
    private List<byte[]> Tree(ulong at, int leaf, int branch, int width, int key, ulong owner, int expected = -1)
    {
        if (at == 0) return [];
        Own(at, q); Metadata.Add(at);
        int raw = checked((int)(at * (uint)A)); int level = S(image, raw + 50);
        Assert.InRange(level, 0, 7); if (expected >= 0) Assert.Equal(expected, level);
        byte[] page = Page(at, level == 0 ? leaf : branch, owner); int count = S(page, 48), size = level == 0 ? width : key + 8;
        Assert.InRange(count, level == 0 ? 0 : 1, (P - 64) / size); Assert.Equal(0UL, L(page, 40));
        List<byte[]> result = []; byte[]? previous = null;
        for (int i = 0; i < count; i++)
        {
            byte[] entry = page.AsSpan(64 + size * i, size).ToArray();
            if (previous != null) Assert.True(previous.AsSpan(0, key).SequenceCompareTo(entry.AsSpan(0, key)) < 0);
            previous = entry;
            if (level == 0) result.Add(entry);
            else
            {
                var children = Tree(L(entry, key), leaf, branch, width, key, owner, level - 1);
                foreach (var child in children)
                {
                    Assert.True(child.AsSpan(0, key).SequenceCompareTo(entry.AsSpan(0, key)) >= 0);
                    if (i + 1 < count) Assert.True(child.AsSpan(0, key).SequenceCompareTo(page.AsSpan(64 + size * (i + 1), key)) < 0);
                }
                result.AddRange(children);
            }
        }
        return result;
    }
    internal static string Fold(string name) => string.Concat(name.Select(c => c is >= 'A' and <= 'Z' or >= '\u00c0' and <= '\u00d6' or >= '\u00d8' and <= '\u00de' ? (char)(c + 32) : c));
    internal IndependentImage(byte[] image)
    {
        this.image = image; A = (int)I(image, 65536 + 80); P = (int)I(image, 65536 + 84);
        units = L(image, 65536 + 88); q = (ulong)(P / A); Assert.Equal((ulong)image.Length / (uint)A, units);
        generation = Math.Max(L(image, 65536 + P + 80), L(image, 65536 + 2 * P + 80));
        Page(65536UL / (uint)A, 1, 0); Page(units / q * q - q, 1, 0);
        for (ulong u = 0; u < units; u++)
        {
            ulong group = u & ~65535UL; ulong remaining = units - group;
            bool has = group == 0 || remaining >= 17 * q + (remaining < 65536 ? q : 0);
            if (u < 2097152UL / (uint)A || u >= units / q * q - q || !has || group != 0 && u - group < 17 * q) owners.Add(u);
        }
        byte[] state = Page(65536UL / (uint)A + 3 * q, 3, 0), reserve = Page(65536UL / (uint)A + 8 * q, 4, 0);
        int reserves = S(reserve, 64); Assert.InRange(reserves, 0, 64);
        for (int i = 0; i < reserves; i++) Own(L(reserve, 72 + i * 8), q);
        foreach (var obj in Tree(L(state, 80), 11, 12, 256, 8, 0)) Assert.True(Objects.TryAdd(L(obj, 0), obj));
        Assert.True(Objects.ContainsKey(1));
        Dictionary<ulong, int> links = [];
        foreach (var (id, obj) in Objects)
        {
            if (obj[84] == 2)
            {
                var entries = Tree(L(obj, 32), 13, 14, 128, 16, id); Assert.Equal(L(obj, 8), (ulong)entries.Count);
                foreach (var e in entries)
                {
                    ulong target = L(e, 16); Assert.True(Objects.ContainsKey(target)); links[target] = links.GetValueOrDefault(target) + 1;
                    int n = S(e, 32); Assert.InRange(n, 1, 768); byte[] text;
                    if (L(e, 24) == 0) { Assert.True(n <= 80); text = e.AsSpan(48, n).ToArray(); }
                    else { Own(L(e, 24), q); Metadata.Add(L(e, 24)); var name = Page(L(e, 24), 17, L(e, 8)); Assert.Equal(n, S(name, 64)); text = name.AsSpan(68, n).ToArray(); }
                    string value = new UTF8Encoding(false, true).GetString(text);
                    Assert.True(Names.TryAdd((id, Fold(value)), target));
                }
            }
            else
            {
                Assert.Equal(1, obj[84]); ulong allocated = 0, end = 0;
                foreach (var e in Extents(obj, true))
                {
                    ulong logical = L(e, 0), physical = L(e, 8); uint count = I(e, 16);
                    Assert.True(logical >= end); Assert.InRange(count, 1u, 65536u); Assert.Equal(physical >> 16, (physical + count - 1) >> 16);
                    end = checked(logical + count); Own(physical, count); allocated += count;
                }
                Assert.Equal(L(obj, 16), allocated);
                if (obj[85] == 0) { Assert.True(L(obj, 8) <= 160); Assert.Equal(L(obj, 8), S(obj, 88)); }
            }
        }
        Assert.Equal(L(state, 104), (ulong)Names.Count);
        Assert.Equal(0UL, L(state, 88)); // oracle runs after writable recovery/cleanup
        foreach (var (id, obj) in Objects) Assert.Equal(id == 1 ? 1u : (uint)links.GetValueOrDefault(id), I(obj, 76));
        ulong free = 0;
        for (ulong group = 0; group < units; group += 65536)
        {
            if (group != 0 && units - group < 17 * q + (units - group < 65536 ? q : 0)) continue;
            ulong descriptor = group == 0 ? 131072UL / (uint)A : group; var gd = Page(descriptor, 6, group); uint groupFree = 0;
            for (int m = 0; m < 16; m++)
            {
                var bitmap = Page(descriptor + (ulong)(m + 1) * q, 7, group); uint local = 0;
                for (int b = 0; b < 4096; b++)
                {
                    ulong u = group + (uint)(m * 4096 + b); bool used = (bitmap[64 + b / 8] & (128 >> (b % 8))) != 0;
                    Assert.Equal(u >= units || owners.Contains(u), used); if (!used) local++;
                }
                Assert.Equal(local, I(gd, 80 + m * 4)); groupFree += local;
            }
            Assert.Equal(groupFree, I(gd, 68)); free += groupFree;
        }
        Assert.Equal(free, L(state, 96));
    }
    private List<byte[]> Extents(byte[] obj, bool own)
    {
        if (obj[85] == 0) return [];
        if (obj[85] == 2)
        {
            if (own) return Tree(L(obj, 24), 15, 16, 24, 8, L(obj, 0));
            return ReadExtentTree(L(obj, 24));
        }
        Assert.InRange(S(obj, 90), (ushort)0, (ushort)6);
        return Enumerable.Range(0, S(obj, 90)).Select(i => obj.AsSpan(96 + i * 24, 24).ToArray()).ToList();
    }
    private List<byte[]> ReadExtentTree(ulong at)
    {
        int offset = checked((int)(at * (uint)A)), count = S(image, offset + 48); List<byte[]> result = [];
        for (int i = 0; i < count; i++)
            if (S(image, offset + 50) == 0) result.Add(image.AsSpan(offset + 64 + 24 * i, 24).ToArray());
            else result.AddRange(ReadExtentTree(L(image, offset + 64 + 16 * i + 8)));
        return result;
    }
    internal byte[] Read(ulong id)
    {
        var obj = Objects[id]; byte[] result = new byte[checked((int)L(obj, 8))];
        if (obj[85] == 0) obj.AsSpan(96, result.Length).CopyTo(result);
        else foreach (var e in Extents(obj, false))
        {
            if (I(e, 20) != 0) continue;
            ulong logical = L(e, 0) * (uint)A; if (logical >= (ulong)result.Length) continue;
            int count = (int)Math.Min((ulong)result.Length - logical, (ulong)I(e, 16) * (uint)A);
            image.AsSpan(checked((int)(L(e, 8) * (uint)A)), count).CopyTo(result.AsSpan((int)logical));
        }
        return result;
    }
}
