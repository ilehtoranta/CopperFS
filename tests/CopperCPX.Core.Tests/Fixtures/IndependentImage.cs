using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace CopperCPX.Reference.Tests;

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
    internal string Label { get; private set; } = "CopperCPX4";
    internal bool OwnershipEnabled { get; private set; }
    internal readonly Dictionary<ulong,uint> OwnerInfo = [];
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
        Assert.Equal(0x43505834u, I(p, 0)); Assert.Equal(4, S(p, 6)); Assert.Equal(type, S(p, 4));
        Assert.Equal(at, L(p, 16)); Assert.Equal(owner, L(p, 24)); Assert.True(L(p, 32) <= generation);
        Assert.Equal(Checksum(p), I(p, 8));
        int payload = type switch
        {
            1 => 64, 2 => 24, 3 => S(p,52) is 128 or 136?S(p,52):48, 4 => 520, 5 => 4, 6 => 80, 7 => 512,
            11 => S(p, 48) * 256, 12 or 16 => S(p, 48) * 16,
            13 => S(p, 48) * 128, 14 or 15 => S(p, 48) * 24,
            17 or 19 => 4 + S(p, 64), 18 => S(p,64)==2?12+S(p,74):8+S(p,70),
            _ => throw new InvalidDataException($"Unexpected decoded page type {type}")
        };
        Assert.InRange(payload, 0, P - 64); Assert.Equal(payload, S(p, 52)); return p;
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
        this.image = image;
        int anchor = -1, pageSize = 0;
        for (int candidate = 1024; candidate <= 4096 && anchor < 0; candidate *= 2)
            foreach (int at in new[] { 65536, (image.Length & ~(candidate - 1)) - candidate })
                if (I(image, at) == 0x43505834 && I(image, at + 8) == Checksum(image.AsSpan(at, candidate)))
                { anchor = at; pageSize = candidate; break; }
        Assert.True(anchor >= 0, "No independently valid anchor remains.");
        A = (int)I(image, anchor + 80); P = (int)I(image, anchor + 84); Assert.Equal(pageSize, P);
        Assert.Contains(A, new[] { 512, 1024, 2048, 4096 }); Assert.Equal(Math.Max(1024, A), P);
        units = L(image, anchor + 88); q = (ulong)(P / A); Assert.Equal((ulong)image.Length / (uint)A, units);
        bool first = Checksum(image.AsSpan(65536 + P, P)) == I(image, 65536 + P + 8);
        bool second = Checksum(image.AsSpan(65536 + 2 * P, P)) == I(image, 65536 + 2 * P + 8);
        Assert.True(first || second, "No independently valid checkpoint remains.");
        generation = Math.Max(first ? L(image, 65536 + P + 80) : 0, second ? L(image, 65536 + 2 * P + 80) : 0);
        if (first) { var cp = Page(65536UL / (uint)A + q, 2, 0); Assert.Equal(L(cp, 32), L(cp, 80)); Assert.Equal(L(image, anchor + 72), L(cp, 72)); }
        if (second) { var cp = Page(65536UL / (uint)A + 2 * q, 2, 0); Assert.Equal(L(cp, 32), L(cp, 80)); Assert.Equal(L(image, anchor + 72), L(cp, 72)); }
        Page((ulong)anchor / (uint)A, 1, 0);
        foreach (int at in new[] { 65536, (image.Length & ~(P - 1)) - P })
            if (I(image, at) == 0x43505834 && I(image, at + 8) == Checksum(image.AsSpan(at, P)))
            { Page((ulong)at / (uint)A, 1, 0); Assert.Equal(L(image, anchor + 72), L(image, at + 72)); Assert.Equal(A, (int)I(image, at + 80)); }
        for (ulong u = 0; u < units; u++)
        {
            ulong group = u & ~65535UL; ulong remaining = units - group;
            bool has = group == 0 || remaining >= 17 * q + (remaining < 65536 ? q : 0);
            if (u < 2097152UL / (uint)A || u >= units / q * q - q || !has || group != 0 && u - group < 17 * q) owners.Add(u);
        }
        byte[] state = Page(65536UL / (uint)A + 3 * q, 3, 0), reserve = Page(65536UL / (uint)A + 8 * q, 4, 0);
        OwnershipEnabled=S(state,52)==136;
        if(OwnershipEnabled) { Assert.Equal(0x4f574e31u,I(state,192)); Assert.Equal(1u,I(state,196)); }
        if(S(state,52) is 128 or 136)
        {
            Assert.Equal(0x4c424c31u,I(state,112)); Assert.Equal(1,S(state,118));
            int length=S(state,116); Assert.InRange(length,1,60);
            Label=new UTF8Encoding(false,true).GetString(state,120,length);
            Assert.InRange(Label.Length,1,30); Assert.All(Label,c=>Assert.True(c>=32&&c<=255&&c!=127&&c!='/'&&c!=':'));
            Assert.All(state[(120+length)..192],b=>Assert.Equal(0,b));
        }
        int reserves = S(reserve, 64); Assert.InRange(reserves, 0, 64);
        for (int i = 0; i < reserves; i++) Own(L(reserve, 72 + i * 8), q);
        foreach (var obj in Tree(L(state, 80), 11, 12, 256, 8, 0)) Assert.True(Objects.TryAdd(L(obj, 0), obj));
        Assert.True(Objects.ContainsKey(1));
        Dictionary<ulong, int> links = [];
        foreach (var (id, obj) in Objects)
        {
            OwnerInfo[id]=0;
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
            else if(obj[84]==3)
            {
                int length=checked((int)L(obj,8)); Assert.InRange(length,1,768); Assert.Equal(0UL,L(obj,16));
                byte[] target;
                if(obj[85]==0) { Assert.InRange(length,1,160); Assert.Equal(length,S(obj,88)); target=obj.AsSpan(96,length).ToArray(); }
                else { Assert.Equal(3,obj[85]); ulong page=L(obj,96); Own(page,q); Metadata.Add(page); var link=Page(page,19,id); Assert.Equal(length,S(link,64)); target=link.AsSpan(68,length).ToArray(); }
                new UTF8Encoding(false,true).GetString(target);
            }
            else
            {
                Assert.Equal(1, obj[84]); Assert.InRange(obj[85], (byte)0, (byte)2); ulong allocated = 0, end = 0;
                foreach (var e in Extents(obj, true))
                {
                    ulong logical = L(e, 0), physical = L(e, 8); uint count = I(e, 16);
                    Assert.True(logical >= end); Assert.InRange(count, 1u, 65536u); Assert.InRange(I(e, 20), 0u, 1u); Assert.Equal(physical >> 16, (physical + count - 1) >> 16);
                    end = checked(logical + count); Own(physical, count); allocated += count;
                }
                Assert.Equal(L(obj, 16), allocated);
                if (obj[85] == 0) { Assert.True(L(obj, 8) <= 160); Assert.Equal(L(obj, 8), S(obj, 88)); Assert.Equal(0UL, L(obj, 24)); Assert.Equal(0, S(obj, 90)); }
            }
            if(L(obj,48)!=0)
            {
                ulong page=L(obj,48); Own(page,q); Metadata.Add(page); var attr=Page(page,18,id);
                int version=S(attr,64); Assert.Contains(version,new[]{1,2}); Assert.Equal(0,S(attr,66));
                int start=version==1?72:76,length=S(attr,start-2); Assert.Equal(1,S(attr,start-4)); Assert.InRange(length,version==1?1:0,158);
                if(version==2) { Assert.True(OwnershipEnabled); OwnerInfo[id]=I(attr,68); Assert.NotEqual(0u,OwnerInfo[id]); }
                new UTF8Encoding(false,true).GetString(attr.AsSpan(start,length));
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
    internal bool IsOwned(ulong unit) => owners.Contains(unit);
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
