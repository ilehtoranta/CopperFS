using System.Buffers.Binary;
using System.Text;
using Amiga;
using CopperSFS;
using CopperStart.Dos;

namespace CopperStart.FileSystem.Tests;

public sealed partial class VolumeGeometryTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void CaseOnlyRenameUpdatesTheStoredNameAndDirectoryCache(int dos)
    {
        var storage = new Storage(); AmigaVolumeGeometry.Create(1760, 512, 2, out var geometry);
        var p = new Memory(storage, geometry); VariantFormat(ref p, storage, 0x444F5300u + (uint)dos);
        var file = VariantCreate(ref p, storage, geometry.RootBlock, "Original");
        Encoding.Latin1.GetBytes("ORIGINAL").CopyTo(storage.Ram, (int)Name.Raw);
        file.Name = Name; file.Length = 8; var work = Work();
        Assert.True(AmigaFfsWriteCore.Rename(ref p, ref work, ref file), work.Error.ToString());
        Assert.True(p.ReadBlock(file.Header, Meta));
        Assert.Equal("ORIGINAL", Encoding.Latin1.GetString(storage.Ram, (int)Meta.Raw + 433, 8));
        if (dos >= 4) InspectDirectoryCache(storage, geometry, geometry.RootBlock, 1);
    }

    private static AmigaFfsWriteCore.Request VariantCreate(ref Memory p, Storage storage, uint parent, string name, bool directory = false)
    {
        Encoding.Latin1.GetBytes(name).CopyTo(storage.Ram, (int)Name.Raw);
        var r = new AmigaFfsWriteCore.Request { Directory = parent, Name = Name, Length = (uint)name.Length }; var w = Work();
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref r, directory), w.Error.ToString()); return r;
    }
    private static void VariantFormat(ref Memory p, Storage storage, uint type)
    {
        storage.Ram[(int)Name.Raw] = (byte)'V';
        Assert.True(AmigaDiskFileSystemFormatCore.Format(ref p, type, Name, 1, 100, 2, 3, Meta, out var error), error.ToString());
    }
    [Theory]
    [InlineData(2, 512u)] [InlineData(3, 512u)] [InlineData(4, 512u)] [InlineData(5, 512u)]
    [InlineData(2, 1024u)] [InlineData(3, 1024u)] [InlineData(4, 1024u)] [InlineData(5, 1024u)]
    public void ClassicVariantsMaintainInternationalHashesDataAndDirectoryCaches(int dos, uint bytes)
    {
        var storage = new Storage(); AmigaVolumeGeometry.Create(12000, bytes, 2, out var g);
        var p = new Memory(storage, g); var type = 0x444F5300u + (uint)dos; VariantFormat(ref p, storage, type); var baseline = Used(ref p);
        var dir = VariantCreate(ref p, storage, g.RootBlock, "répertoire", true);
        var file = VariantCreate(ref p, storage, dir.Header, "été");
        var expected = Enumerable.Range(0, 130013).Select(i => (byte)(i * 11 + i / 17)).ToArray();
        OfsWrite(ref p, storage, ref file, 0, expected);
        if (dos >= 4) InspectDirectoryCache(storage, g, dir.Header, 1);
        if ((dos & 1) == 0) InspectOfs(storage, g, file.Header, expected);
        Encoding.Latin1.GetBytes("RÉPERTOIRE/ÉTÉ").CopyTo(storage.Ram, (int)Name.Raw);
        Assert.Equal(file.Header, AmigaDiskFileSystemCore.Resolve(ref p, g.RootBlock, Name, 14, type, Meta, out _));
        Encoding.Latin1.GetBytes("ÉTÉ").CopyTo(storage.Ram, (int)Name.Raw);
        var duplicate = new AmigaFfsWriteCore.Request { Directory = dir.Header, Name = Name, Length = 3 }; var w = Work();
        storage.Transfers.Clear(); Assert.False(AmigaFfsWriteCore.Create(ref p, ref w, ref duplicate, false)); Assert.Equal(DOS.Error.ObjectExists, w.Error);
        Assert.DoesNotContain(storage.Transfers, t => t.Command is 3 or 25);
        // Latin-1 0xFF has no uppercase partner in this character set; it must not alias 0xDF.
        var y = VariantCreate(ref p, storage, dir.Header, "ÿ"); var sharp = VariantCreate(ref p, storage, dir.Header, "ß");
        Assert.NotEqual(y.Header, sharp.Header);
        Encoding.Latin1.GetBytes(new string('c', 79)).CopyTo(storage.Ram, (int)Payload.Raw);
        file.Buffer = Payload; file.Length = 79; w = Work(); Assert.True(AmigaFfsWriteCore.SetMetadata(ref p, ref w, ref file, DosPacketAction.SetComment), w.Error.ToString());
        p.WriteUInt32(Payload, 0, 12345); p.WriteUInt32(Payload, 4, 678); p.WriteUInt32(Payload, 8, 2999);
        w = Work(); Assert.True(AmigaFfsWriteCore.SetMetadata(ref p, ref w, ref file, DosPacketAction.SetDate), w.Error.ToString());
        file.Position = 0x40; w = Work(); Assert.True(AmigaFfsWriteCore.SetMetadata(ref p, ref w, ref file, DosPacketAction.SetProtection), w.Error.ToString());
        if (dos >= 4) { InspectDirectoryCache(storage, g, g.RootBlock, 1); InspectDirectoryCache(storage, g, dir.Header, 3); }
        Encoding.Latin1.GetBytes("Déplacé").CopyTo(storage.Ram, (int)Name.Raw);
        file.Name = Name; file.Length = 7; file.Directory = g.RootBlock; w = Work();
        Assert.True(AmigaFfsWriteCore.Rename(ref p, ref w, ref file), w.Error.ToString());
        if (dos >= 4) { InspectDirectoryCache(storage, g, g.RootBlock, 2); InspectDirectoryCache(storage, g, dir.Header, 2); }
        w = Work(); Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, file.Header, true), w.Error.ToString());
        if (dos >= 4) InspectDirectoryCache(storage, g, g.RootBlock, 2);
        foreach (var key in new[] { file.Header, y.Header, sharp.Header, dir.Header })
        { w = Work(); Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, key, false), w.Error.ToString()); }
        Assert.Equal(baseline, Used(ref p)); if (dos >= 4) InspectDirectoryCache(storage, g, g.RootBlock, 0);
    }

    [Theory]
    [InlineData(4)] [InlineData(5)]
    public void DirectoryCacheSpansBlocksAndRejectsCorruptChains(int dos)
    {
        var storage = new Storage(); AmigaVolumeGeometry.Create(1760, 512, 2, out var g); var p = new Memory(storage, g);
        VariantFormat(ref p, storage, 0x444F5300u + (uint)dos);
        for (var i = 0; i < 40; i++)
        {
            var file = VariantCreate(ref p, storage, g.RootBlock, $"Entry{i:D3}" + new string('x', 22));
            OfsWrite(ref p, storage, ref file, 0, [1, 3, 5]);
        }
        Assert.True(InspectDirectoryCache(storage, g, g.RootBlock, 40) >= 4);
        Assert.True(p.ReadBlock(g.RootBlock, Meta)); var cache = p.ReadUInt32(Meta, 504);
        Assert.True(p.ReadBlock(cache, Data)); p.WriteUInt32(Data, 16, cache); p.WriteUInt32(Data, 20, 0);
        uint sum = 0; for (var i = 0; i < 512; i += 4) sum = unchecked(sum + p.ReadUInt32(Data, i)); p.WriteUInt32(Data, 20, unchecked(0u - sum));
        Assert.True(p.WriteBlock(cache, Data));
        Encoding.Latin1.GetBytes("Fail").CopyTo(storage.Ram, (int)Name.Raw);
        var r = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 4 }; var w = Work();
        Assert.False(AmigaFfsWriteCore.Create(ref p, ref w, ref r, false)); Assert.Equal(DOS.Error.DiskNotValidated, w.Error);
        Assert.True(p.ReadBlock(g.RootBlock, Meta)); Assert.Equal(0u, p.ReadUInt32(Meta, 312));
    }

    // Independent on-disk cache/canonical comparison, including bitmap ownership.
    // The production reader deliberately enumerates hash chains, so using it as
    // the cache oracle would miss stale T_DIRC entries.
    private static int InspectDirectoryCache(Storage storage, AmigaVolumeGeometry g, uint directory, int count)
    {
        byte[] Read(uint key)
        {
            var data = new byte[g.BlockBytes];
            for (var offset = 0u; offset < g.BlockBytes; offset += g.SectorBytes)
                storage.Sectors[g.StartByte + (ulong)key * g.BlockBytes + offset].CopyTo(data, (int)offset);
            return data;
        }
        static uint U(byte[] b, int at) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
        var parent = Read(directory); var expected = new Dictionary<uint, byte[]>();
        for (var i = 0u; i < g.TableEntries; i++)
            for (var key = U(parent, 24 + (int)i * 4); key != 0;)
            {
                var entry = Read(key); Assert.True(expected.TryAdd(key, entry));
                Assert.Equal(directory, U(entry, g.Tail + 500)); key = U(entry, g.Tail + 496);
            }
        Assert.Equal(count, expected.Count); var caches = new HashSet<uint>(); var actual = new HashSet<uint>();
        for (var key = U(parent, g.Tail + 504); key != 0;)
        {
            Assert.True(caches.Add(key)); var b = Read(key); Assert.Equal(33u, U(b, 0)); Assert.Equal(key, U(b, 4)); Assert.Equal(directory, U(b, 8));
            uint sum = 0; for (var i = 0; i < b.Length; i += 4) sum = unchecked(sum + U(b, i)); Assert.Equal(0u, sum);
            var bit = key - g.ReservedBlocks; var map = Read(U(Read(g.RootBlock), g.Tail + 316 + (int)(bit / g.BitmapBits) * 4));
            Assert.Equal(0u, U(map, 4 + (int)(bit % g.BitmapBits / 32) * 4) & (1u << (int)(bit % 32)));
            var at = 24;
            for (var i = 0u; i < U(b, 12); i++)
            {
                var header = U(b, at); Assert.True(actual.Add(header)); var entry = expected[header];
                Assert.Equal(U(entry, g.Tail + 508) == 0xFFFFFFFD ? U(entry, g.Tail + 324) : 0u, U(b, at + 4));
                Assert.Equal(U(entry, g.Tail + 320), U(b, at + 8)); Assert.Equal(U(entry, g.Tail + 316), U(b, at + 12));
                for (var d = 0; d < 3; d++) Assert.Equal((ushort)U(entry, g.Tail + 420 + d * 4), BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at + 16 + d * 2)));
                Assert.Equal(entry[g.Tail + 511], b[at + 22]); var name = b[at + 23]; var comment = b[at + 24 + name];
                Assert.Equal(entry[g.Tail + 432], name); Assert.Equal(entry.AsSpan(g.Tail + 433, name).ToArray(), b.AsSpan(at + 24, name).ToArray());
                Assert.Equal(entry[g.Tail + 328], comment); Assert.Equal(entry.AsSpan(g.Tail + 329, comment).ToArray(), b.AsSpan(at + 25 + name, comment).ToArray());
                at = (at + 26 + name + comment) & ~1;
            }
            key = U(b, 16);
        }
        Assert.NotEmpty(caches); Assert.Equal(expected.Keys.Order(), actual.Order()); return caches.Count;
    }
}
