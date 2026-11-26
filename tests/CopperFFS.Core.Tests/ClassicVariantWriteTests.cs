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
        // The budget preflight finds the cycle before Begin, so the root stays valid.
        Assert.True(p.ReadBlock(g.RootBlock, Meta)); Assert.Equal(uint.MaxValue, p.ReadUInt32(Meta, 312));
    }

    // A DOS4/5 mutation also allocates directory-cache blocks. Running out of
    // them after the root was marked invalid used to leave the volume
    // permanently unwritable. The whole budget must be refused up front.
    [Theory]
    [InlineData(4, true, 0)] [InlineData(5, true, 0)]
    [InlineData(4, false, 8)] [InlineData(5, false, 8)]
    public void DirectoryCacheBudgetIsRefusedBeforeTheRootIsInvalidated(int dos, bool directory, int siblings)
    {
        var storage = new Storage(); AmigaVolumeGeometry.Create(1760, 512, 2, out var g); var p = new Memory(storage, g);
        VariantFormat(ref p, storage, 0x444F5300u + (uint)dos);
        // Thirty-character names make 56-byte records; eight fill the first cache block.
        for (var i = 0; i < siblings; i++) VariantCreate(ref p, storage, g.RootBlock, $"Sibling{i}" + new string('s', 22));
        LeaveFree(ref p, g, 1);
        var before = storage.Sectors.ToDictionary(x => x.Key, x => x.Value.ToArray());
        var name = directory ? "Drawer" : "Overflow" + new string('o', 22);
        Encoding.Latin1.GetBytes(name).CopyTo(storage.Ram, (int)Name.Raw);
        var r = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = (uint)name.Length }; var w = Work();
        Assert.False(AmigaFfsWriteCore.Create(ref p, ref w, ref r, directory));
        Assert.Equal(DOS.Error.DiskFull, w.Error);
        Assert.Equal(before.Keys.Order(), storage.Sectors.Keys.Order());
        foreach (var (key, bytes) in before) Assert.Equal(bytes, storage.Sectors[key]);
        // The volume stays writable: a metadata edit needing no blocks succeeds.
        var sibling = directory ? VariantCreate(ref p, storage, g.RootBlock, "Last") : new AmigaFfsWriteCore.Request();
        if (!directory)
        {
            Encoding.Latin1.GetBytes("Sibling0" + new string('s', 22)).CopyTo(storage.Ram, (int)Name.Raw);
            sibling.Header = AmigaDiskFileSystemCore.Resolve(ref p, g.RootBlock, Name, 30, 0x444F5300u + (uint)dos, Meta, out _);
            Assert.NotEqual(0u, sibling.Header);
        }
        sibling.Position = 0x40; w = Work();
        Assert.True(AmigaFfsWriteCore.SetMetadata(ref p, ref w, ref sibling, DosPacketAction.SetProtection), w.Error.ToString());
        Assert.True(p.ReadBlock(g.RootBlock, Meta)); Assert.Equal(uint.MaxValue, p.ReadUInt32(Meta, 312));
        InspectDirectoryCache(storage, g, g.RootBlock, directory ? 1 : siblings);
    }

    // The exact budget still admits the last free block when the parent's
    // cache has room for the new record.
    [Theory]
    [InlineData(4)] [InlineData(5)]
    public void LastFreeBlockStillCreatesAFileWhenTheCacheHasRoom(int dos)
    {
        var storage = new Storage(); AmigaVolumeGeometry.Create(1760, 512, 2, out var g); var p = new Memory(storage, g);
        VariantFormat(ref p, storage, 0x444F5300u + (uint)dos);
        for (var i = 0; i < 7; i++) VariantCreate(ref p, storage, g.RootBlock, $"Sibling{i}" + new string('s', 22));
        LeaveFree(ref p, g, 1);
        VariantCreate(ref p, storage, g.RootBlock, "Fits" + new string('f', 26));
        Assert.Equal(g.BlockCount, Used(ref p));
        Assert.True(p.ReadBlock(g.RootBlock, Meta)); Assert.Equal(uint.MaxValue, p.ReadUInt32(Meta, 312));
        Assert.Equal(1, InspectDirectoryCache(storage, g, g.RootBlock, 8));
    }

    // Kickstart-made links share hash chains with ordinary entries. Build a
    // bucket of soft link -> hard link -> plain file and use every entry.
    // CopperFS.Kickstart.Tests' link reference compares the same semantics
    // with Kickstart 3.1's own filesystem.
    [Theory]
    [InlineData(1)] [InlineData(5)]
    public void LinksInAHashChainAreFollowedListedAndDeleted(int dos)
    {
        var storage = new Storage(); AmigaVolumeGeometry.Create(1760, 512, 2, out var g); var p = new Memory(storage, g);
        var type = 0x444F5300u + (uint)dos; VariantFormat(ref p, storage, type);
        var target = VariantCreate(ref p, storage, g.RootBlock, "Target");
        storage.Ram.AsSpan((int)Payload.Raw, 100).Fill(0x5A);
        target.Buffer = Payload; target.Length = 100; var w = Work();
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref target, out _), w.Error.ToString());
        static uint Bucket(string name)
        {
            var hash = (uint)name.Length;
            foreach (var c in name) hash = hash * 13 + (uint)char.ToUpperInvariant(c);
            return (hash & 2047) % 72;
        }
        var names = Enumerable.Range(0, 5000).Select(i => "N" + i).Where(n => Bucket(n) == Bucket("Plain")).Take(2).ToArray();
        var plain = VariantCreate(ref p, storage, g.RootBlock, "Plain");
        var hard = VariantCreate(ref p, storage, g.RootBlock, names[0]);
        var soft = VariantCreate(ref p, storage, g.RootBlock, names[1]);
        void Rewrite(uint key, Action<APTR> edit)
        {
            Assert.True(p.ReadBlock(key, Meta)); edit(Meta);
            p.WriteUInt32(Meta, 20, 0); uint sum = 0;
            for (var i = 0; i < 512; i += 4) sum = unchecked(sum + p.ReadUInt32(Meta, i));
            p.WriteUInt32(Meta, 20, unchecked(0u - sum)); Assert.True(p.WriteBlock(key, Meta));
        }
        Rewrite(hard.Header, b => { p.WriteUInt32(b, 508, unchecked((uint)-4)); p.WriteUInt32(b, 468, target.Header); });
        Rewrite(soft.Header, b => { p.WriteUInt32(b, 508, 3); Encoding.Latin1.GetBytes("Target\0").CopyTo(storage.Ram, (int)b.Raw + 24); });
        Rewrite(target.Header, b => p.WriteUInt32(b, 472, hard.Header));
        uint Resolve(string path, out DOS.Error error)
        {
            Encoding.Latin1.GetBytes(path).CopyTo(storage.Ram, (int)Name.Raw);
            return AmigaDiskFileSystemCore.Resolve(ref p, g.RootBlock, Name, (uint)path.Length, type, Meta, out error);
        }
        Assert.Equal(plain.Header, Resolve("Plain", out _));
        Assert.Equal(target.Header, Resolve(names[0], out _));
        Assert.Equal(0u, Resolve(names[1], out var softError)); Assert.Equal(DOS.Error.IsSoftLink, softError);
        Assert.Equal(0u, Resolve(names[1] + "/x", out softError)); Assert.Equal(DOS.Error.IsSoftLink, softError);
        // ExNext reports each entry with its own secondary type.
        var fib = APTR.FromPointer(0x50000); var cursor = new AmigaDiskFileSystemCore.DirectoryCursor();
        Assert.True(AmigaDiskFileSystemCore.Examine(ref p, g.RootBlock, fib, Meta));
        var listed = new Dictionary<string, int>();
        while (AmigaDiskFileSystemCore.ExamineNext(ref p, ref cursor, g.RootBlock, fib, Meta, Data) == DOS.Error.None)
            listed[Encoding.Latin1.GetString(storage.Ram, (int)fib.Raw + 9, storage.Ram[fib.Raw + 8])] = unchecked((int)p.ReadUInt32(fib, 4));
        Assert.Equal(new Dictionary<string, int> { ["Target"] = -3, ["Plain"] = -3, [names[0]] = -4, [names[1]] = 3 }, listed);
        // Creating and deleting through the same bucket passes both links.
        var colliding = Enumerable.Range(5000, 5000).Select(i => "N" + i).First(n => Bucket(n) == Bucket("Plain"));
        var created = VariantCreate(ref p, storage, g.RootBlock, colliding);
        w = Work(); Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, plain.Header, false), w.Error.ToString());
        w = Work(); Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, created.Header, false), w.Error.ToString());
        w = Work(); Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, soft.Header, false), w.Error.ToString());
        // Deleting the original keeps its header under the link's name.
        var used = Used(ref p);
        w = Work(); Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, target.Header, false), w.Error.ToString());
        Assert.Equal(target.Header, Resolve(names[0], out _));
        Assert.Equal(unchecked((uint)-3), p.ReadUInt32(Meta, 508)); Assert.Equal(0u, p.ReadUInt32(Meta, 472));
        Assert.Equal(100u, p.ReadUInt32(Meta, 324));
        Assert.Equal(0u, Resolve("Target", out var gone)); Assert.Equal(DOS.Error.ObjectNotFound, gone);
        Assert.Equal(used - 1, Used(ref p));
        Assert.True(p.ReadBlock(g.RootBlock, Meta)); Assert.Equal(uint.MaxValue, p.ReadUInt32(Meta, 312));
        if (dos >= 4) InspectDirectoryCache(storage, g, g.RootBlock, 1);
    }

    // Delete ALL pattern: list one entry, delete it, list the next. The native
    // owner drops its private cursor on every mutation, so each step resumes
    // from fib_DiskKey alone.
    [Theory]
    [InlineData(1)] [InlineData(3)] [InlineData(5)]
    public void ListOneDeleteItListNextRemovesEveryEntry(int dos)
    {
        var storage = new Storage(); AmigaVolumeGeometry.Create(1760, 512, 2, out var g); var p = new Memory(storage, g);
        var type = 0x444F5300u + (uint)dos; VariantFormat(ref p, storage, type);
        var drawer = VariantCreate(ref p, storage, g.RootBlock, "Drawer", true);
        for (var i = 0; i < 40; i++) VariantCreate(ref p, storage, drawer.Header, $"Entry{i:00}");
        var fib = APTR.FromPointer(0x50000);
        Assert.True(AmigaDiskFileSystemCore.Examine(ref p, drawer.Header, fib, Meta));
        Assert.Equal(drawer.Header, p.ReadUInt32(fib, 0));
        var deleted = 0; var cursor = new AmigaDiskFileSystemCore.DirectoryCursor();
        while (AmigaDiskFileSystemCore.ExamineNext(ref p, ref cursor, drawer.Header, fib, Meta, Data, type) == DOS.Error.None)
        {
            var w = Work(); Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, p.ReadUInt32(fib, 0), false), w.Error.ToString());
            cursor.Ordinal = 0; // the native owner invalidates its private position on every mutation
            deleted++;
        }
        Assert.Equal(40, deleted);
        Assert.True(p.ReadBlock(drawer.Header, Meta));
        for (var at = 24; at < 312; at += 4) Assert.Equal(0u, p.ReadUInt32(Meta, at));
    }

    // With a caller time, the blocks Kickstart FFS dates get exactly that time:
    // a new header and its directory on create, the directory (not the object)
    // on rename and delete, and the root's disk-altered date on publication.
    [Theory]
    [InlineData(1)] [InlineData(5)]
    public void MutationsDateTheBlocksKickstartDates(int dos)
    {
        var storage = new Storage(); AmigaVolumeGeometry.Create(1760, 512, 2, out var g); var p = new Memory(storage, g);
        var type = 0x444F5300u + (uint)dos; VariantFormat(ref p, storage, type);
        var drawer = VariantCreate(ref p, storage, g.RootBlock, "Drawer", true);
        uint[] Date(uint key, int at = 420)
        {
            Assert.True(p.ReadBlock(key, Meta));
            return [p.ReadUInt32(Meta, g.Tail + at), p.ReadUInt32(Meta, g.Tail + at + 4), p.ReadUInt32(Meta, g.Tail + at + 8)];
        }
        AmigaFfsWriteCore.Work Timed(uint days) => Work() with { StampTime = true, NowDays = days, NowMinutes = 7, NowTicks = 9 };
        Encoding.Latin1.GetBytes("Drawer/File").CopyTo(storage.Ram, (int)Name.Raw);
        var file = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 11 }; var w = Timed(100);
        var rootDate = Date(g.RootBlock);
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref file, false), w.Error.ToString());
        Assert.Equal(new uint[] { 100, 7, 9 }, Date(file.Header));
        Assert.Equal(new uint[] { 100, 7, 9 }, Date(drawer.Header));
        Assert.Equal(rootDate, Date(g.RootBlock));
        Assert.Equal(new uint[] { 100, 7, 9 }, Date(g.RootBlock, 472)); // disk altered
        Encoding.Latin1.GetBytes("Renamed").CopyTo(storage.Ram, (int)Name.Raw);
        file.Directory = drawer.Header; file.Name = Name; file.Length = 7; w = Timed(200);
        Assert.True(AmigaFfsWriteCore.Rename(ref p, ref w, ref file), w.Error.ToString());
        Assert.Equal(new uint[] { 100, 7, 9 }, Date(file.Header));
        Assert.Equal(new uint[] { 200, 7, 9 }, Date(drawer.Header));
        w = Timed(300);
        Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, file.Header, false), w.Error.ToString());
        Assert.Equal(new uint[] { 300, 7, 9 }, Date(drawer.Header));
        Assert.Equal(rootDate, Date(g.RootBlock));
        Assert.True(AmigaDiskFileSystemCore.CountUsed(ref p, Meta, Bitmap, out _));
        if (dos >= 4) InspectDirectoryCache(storage, g, drawer.Header, 0);
    }

    // fib_NumBlocks follows Kickstart 3.1's size / 512 + 1 on OFS and FFS, and
    // the owner longword fills fib_OwnerUID/GID (CopperFS.Kickstart.Tests'
    // FIB reference test measured both against Kickstart's filesystem).
    [Theory]
    [InlineData(0, 0u, 1u)] [InlineData(0, 512u, 2u)] [InlineData(1, 489u, 1u)] [InlineData(1, 36000u, 71u)]
    public void ExamineReportsKickstartBlockCountsAndOwner(int dos, uint size, uint blocks)
    {
        var storage = new Storage(); AmigaVolumeGeometry.Create(1760, 512, 2, out var g); var p = new Memory(storage, g);
        VariantFormat(ref p, storage, 0x444F5300u + (uint)dos);
        var file = VariantCreate(ref p, storage, g.RootBlock, "Sized");
        if (size != 0) OfsWrite(ref p, storage, ref file, 0, new byte[size]);
        Assert.True(p.ReadBlock(file.Header, Meta)); p.WriteUInt32(Meta, 316, 0x1234ABCD); Reseal(ref p, file.Header);
        var fib = APTR.FromPointer(0x50000);
        Assert.True(AmigaDiskFileSystemCore.Examine(ref p, file.Header, fib, Meta));
        Assert.Equal(blocks, p.ReadUInt32(fib, 128));
        Assert.Equal((ushort)0x1234, p.ReadUInt16(fib, 224)); Assert.Equal((ushort)0xABCD, p.ReadUInt16(fib, 226));
    }

    // Mark every free block but `keep` as allocated in the volume's bitmap pages.
    private static void LeaveFree(ref Memory p, AmigaVolumeGeometry g, uint keep)
    {
        Assert.True(p.ReadBlock(g.RootBlock, Meta));
        var maps = (g.BlockCount - g.ReservedBlocks + g.BitmapBits - 1) / g.BitmapBits;
        for (var map = 0u; map < maps; map++)
        {
            var key = p.ReadUInt32(Meta, g.Tail + 316 + (int)map * 4);
            Assert.True(p.ReadBlock(key, Bitmap));
            for (var bit = 0u; bit < g.BitmapBits && map * g.BitmapBits + bit < g.BlockCount - g.ReservedBlocks; bit++)
            {
                var at = 4 + (int)(bit / 32) * 4; var mask = 1u << (int)(bit % 32);
                if ((p.ReadUInt32(Bitmap, at) & mask) == 0) continue;
                if (keep != 0) { keep--; continue; }
                p.WriteUInt32(Bitmap, at, p.ReadUInt32(Bitmap, at) & ~mask);
            }
            uint sum = 0; for (var i = 4; i < g.BlockBytes; i += 4) sum = unchecked(sum + p.ReadUInt32(Bitmap, i));
            p.WriteUInt32(Bitmap, 0, unchecked(0u - sum));
            Assert.True(p.WriteBlock(key, Bitmap));
        }
        Assert.Equal(0u, keep);
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
