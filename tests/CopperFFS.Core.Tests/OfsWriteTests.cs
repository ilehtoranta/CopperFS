using System.Buffers.Binary;
using System.Text;
using Amiga;
using CopperSFS;
using CopperStart.Dos;

namespace CopperStart.FileSystem.Tests;

public sealed partial class VolumeGeometryTests
{
    private static AmigaFfsWriteCore.Request OfsFile(ref Memory p, Storage storage, AmigaVolumeGeometry g, string name)
    {
        Encoding.ASCII.GetBytes(name).CopyTo(storage.Ram, (int)Name.Raw);
        var w = Work(); var r = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = (uint)name.Length };
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref r, false), w.Error.ToString()); return r;
    }

    private static void OfsWrite(ref Memory p, Storage storage, ref AmigaFfsWriteCore.Request r, uint position, byte[] data)
    {
        data.CopyTo(storage.Ram, (int)Payload.Raw); r.Buffer = Payload; r.Position = position; r.Length = (uint)data.Length;
        var w = Work(); Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref r, out var actual), w.Error.ToString());
        Assert.Equal((uint)data.Length, actual);
    }

    // Independent disk-byte checker: both chains, block ownership, payload lengths,
    // checksums and allocation bits. No production traversal is used here.
    private static HashSet<uint> InspectOfs(Storage storage, AmigaVolumeGeometry g, uint header, byte[] expected)
    {
        uint Word(byte[] block, int at) => BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(at));
        byte[] Block(uint key)
        {
            Assert.InRange(key, g.ReservedBlocks, g.BlockCount - 1);
            var block = storage.Sectors[g.StartByte + key * (ulong)g.BlockBytes];
            uint sum = 0; for (var at = 0; at < block.Length; at += 4) sum = unchecked(sum + Word(block, at));
            Assert.Equal(0u, sum); return block;
        }
        var owned = new HashSet<uint>(); var file = Block(header);
        Assert.Equal((uint)expected.Length, Word(file, g.Tail + 324));
        var nextData = Word(file, 16); var table = header; var index = 0u; var copied = 0;
        while (table != 0)
        {
            Assert.True(owned.Add(table)); var meta = Block(table);
            Assert.Equal(table == header ? 2u : 16u, Word(meta, 0)); Assert.Equal(table, Word(meta, 4));
            if (table != header) Assert.Equal(header, Word(meta, g.Tail + 500));
            var remaining = expected.Length - copied;
            var count = Math.Min(g.TableEntries, (uint)((remaining + (int)g.BlockBytes - 25) / ((int)g.BlockBytes - 24)));
            Assert.Equal(count, Word(meta, 8));
            for (var i = 0; i < count; i++)
            {
                var key = Word(meta, g.Tail + 308 - i * 4); Assert.Equal(nextData, key); Assert.True(owned.Add(key));
                var data = Block(key); Assert.Equal(8u, Word(data, 0)); Assert.Equal(header, Word(data, 4));
                Assert.Equal(++index, Word(data, 8));
                var length = Math.Min(expected.Length - copied, (int)g.BlockBytes - 24);
                Assert.Equal((uint)length, Word(data, 12));
                Assert.Equal(expected.AsSpan(copied, length).ToArray(), data.AsSpan(24, length).ToArray());
                copied += length; nextData = Word(data, 16);
            }
            table = Word(meta, g.Tail + 504);
        }
        Assert.Equal(expected.Length, copied); Assert.Equal(0u, nextData);
        foreach (var key in owned)
        {
            var bit = key - g.ReservedBlocks;
            var map = Block(g.RootBlock + 1 + bit / g.BitmapBits);
            Assert.Equal(0u, Word(map, 4 + (int)(bit % g.BitmapBits / 32) * 4) & 1u << (int)(bit % 32));
        }
        Assert.Equal(uint.MaxValue, Word(Block(g.RootBlock), g.Tail + 312)); return owned;
    }

    [Theory]
    [InlineData(512u, 148)] [InlineData(1024u, 203)] [InlineData(4096u, 5)]
    public void OfsAppendOverwriteTruncateDeleteAndTrimPreserveBothChains(uint bytes, int blocks)
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(12000, bytes, 2, out var g));
        g.StartByte = 5UL * 1024 * 1024 * 1024 + 512;
        var p = new Memory(storage, g); Format(ref p, g); SetDosType(ref p, 0x444F5300); var baseline = Used(ref p);
        var r = OfsFile(ref p, storage, g, "Data"); var payload = (int)bytes - 24;
        var expected = Enumerable.Range(0, blocks * payload + 17).Select(i => (byte)(i * 37 + 11)).ToArray();
        // Append at partial and exact payload boundaries, then cross extensions.
        var position = 0;
        foreach (var take in new[] { 1, payload - 1, 37, expected.Length - payload - 37 })
        {
            OfsWrite(ref p, storage, ref r, (uint)position, expected.AsSpan(position, take).ToArray());
            position += take; InspectOfs(storage, g, r.Header, expected[..position]);
        }
        foreach (var offset in new[] { payload - 3, blocks > g.TableEntries ? (int)g.TableEntries * payload - 3 : 0 })
        {
            var patch = Enumerable.Repeat((byte)0xC9, 37).ToArray(); patch.CopyTo(expected, offset);
            OfsWrite(ref p, storage, ref r, (uint)offset, patch); InspectOfs(storage, g, r.Header, expected);
        }
        // Reopen with fresh state and use the separate reader too.
        p = new Memory(storage, g);
        Assert.Equal(expected.Length, AmigaDiskFileSystemCore.Read(ref p, r.Header, 0x444F5300, 0, Output, (uint)expected.Length, Meta, Data, out var error));
        Assert.Equal(DOS.Error.None, error); Assert.Equal(expected, storage.Ram.AsSpan((int)Output.Raw, expected.Length).ToArray());
        var w = Work(); Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, r.Header, true)); InspectOfs(storage, g, r.Header, []);
        Assert.Equal(baseline + 1, Used(ref p));
        OfsWrite(ref p, storage, ref r, 0, expected); InspectOfs(storage, g, r.Header, expected);
        var keep = OfsFile(ref p, storage, g, "Keep"); var kept = new byte[] { 1, 7, 9 };
        OfsWrite(ref p, storage, ref keep, 0, kept);
        Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, r.Header, false));
        Assert.True(AmigaFfsWriteCore.Trim(ref p, ref w, APTR.Null, 0, out _)); InspectOfs(storage, g, keep.Header, kept);
        r = OfsFile(ref p, storage, g, "Data"); OfsWrite(ref p, storage, ref r, 0, expected);
        InspectOfs(storage, g, r.Header, expected); InspectOfs(storage, g, keep.Header, kept);
        Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, r.Header, false));
        Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, keep.Header, false)); Assert.Equal(baseline, Used(ref p));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    public void OfsRejectsCorruptChainsBeforeOverwriteOrRelease(int damage)
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(3520, 512, 2, out var g));
        var p = new Memory(storage, g); Format(ref p, g); SetDosType(ref p, 0x444F5300);
        var r = OfsFile(ref p, storage, g, "Data"); OfsWrite(ref p, storage, ref r, 0, new byte[73 * 488 + 17]);
        Assert.True(p.ReadBlock(r.Header, Meta)); var first = p.ReadUInt32(Meta, 308); var extension = p.ReadUInt32(Meta, 504);
        var key = damage <= 4 ? first : damage <= 7 ? r.Header : damage <= 9 ? extension : g.RootBlock + 1;
        Assert.True(p.ReadBlock(key, Meta));
        var at = damage switch { 0 => 20, 1 => 4, 2 => 8, 3 => 12, 4 => 16, 5 => 16, 6 => 308, 7 => 504, 8 => 500, 9 => 8, _ => 0 };
        p.WriteUInt32(Meta, at, 0);
        if (damage is not (0 or 10)) Checksum(ref p, Meta, 512, 20);
        Assert.True(p.WriteBlock(key, Meta)); storage.Transfers.Clear();
        var w = Work(); r.Position = 0; r.Buffer = Payload; r.Length = 1;
        Assert.False(AmigaFfsWriteCore.Write(ref p, ref w, ref r, out var actual)); Assert.Equal(0u, actual);
        Assert.Equal(DOS.Error.DiskNotValidated, w.Error);
        Assert.False(AmigaFfsWriteCore.Remove(ref p, ref w, r.Header, false));
        Assert.False(AmigaFfsWriteCore.Remove(ref p, ref w, r.Header, true));
        Assert.DoesNotContain(storage.Transfers, t => t.Command is 3 or 25);
    }

    [Fact]
    public void OfsEveryFailedGrowthWriteLeavesTrimBlockedUnlessInvalidationNeverHappened()
    {
        int Exercise(int fail)
        {
            var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(3520, 512, 2, out var g));
            var p = new Memory(storage, g); Format(ref p, g); SetDosType(ref p, 0x444F5300);
            var r = OfsFile(ref p, storage, g, "Data"); OfsWrite(ref p, storage, ref r, 0, new byte[72 * 488]);
            storage.WriteCalls = 0; storage.FailWriteCall = fail;
            r.Position = 72 * 488; r.Buffer = Payload; r.Length = 1001; var w = Work();
            var okay = AmigaFfsWriteCore.Write(ref p, ref w, ref r, out var actual); var calls = storage.WriteCalls;
            if (fail == 0) { Assert.True(okay); Assert.Equal(1001u, actual); }
            else
            {
                Assert.False(okay); Assert.Equal(DOS.Error.DiskNotValidated, w.Error);
                storage.FailWriteCall = 0;
                Assert.True(p.ReadBlock(g.RootBlock, Meta)); Assert.Equal(fail == 1 ? uint.MaxValue : 0u, p.ReadUInt32(Meta, 312));
                if (fail > 1)
                {
                    Assert.False(AmigaFfsWriteCore.Trim(ref p, ref w, APTR.Null, 0, out _)); Assert.Empty(storage.Discards);
                }
            }
            return calls;
        }
        var count = Exercise(0); for (var fail = 1; fail <= count; fail++) Exercise(fail);
    }

    [Theory]
    [InlineData(512u, true)] [InlineData(1024u, true)] [InlineData(4096u, true)]
    [InlineData(512u, false)] [InlineData(1024u, false)] [InlineData(4096u, false)]
    public void DosMetadataMutationsHandleDirectoriesAndCollidingNamesAtEveryBlockSize(uint bytes, bool ofs)
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(3520, bytes, 2, out var g));
        var p = new Memory(storage, g); Format(ref p, g); var type = ofs ? 0x444F5300u : 0x444F5301u; SetDosType(ref p, type);
        var baseline = Used(ref p); var names = new Dictionary<uint, string>(); string first = "", second = "";
        for (var n = 0; n < 4096; n++)
        {
            var name = "N" + n; var hash = (uint)name.Length;
            foreach (var c in name) hash = (hash * 13 + c) & 2047;
            hash %= g.TableEntries;
            if (names.TryGetValue(hash, out first!)) { second = name; break; }
            names.Add(hash, name);
        }
        Assert.NotEmpty(second);
        var a = OfsFile(ref p, storage, g, first); var b = OfsFile(ref p, storage, g, second);
        OfsWrite(ref p, storage, ref a, 0, [1, 2, 3]); OfsWrite(ref p, storage, ref b, 0, [4, 5, 6]);
        var w = Work(); Encoding.ASCII.GetBytes("Dir").CopyTo(storage.Ram, (int)Name.Raw);
        var dir = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 3 };
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref dir, true));
        // Remove a non-head hash entry while moving it into another directory.
        a.Directory = dir.Header; a.Name = Name; a.Length = 3;
        Assert.True(AmigaFfsWriteCore.Rename(ref p, ref w, ref a));
        Assert.False(AmigaFfsWriteCore.Remove(ref p, ref w, dir.Header, false)); Assert.Equal(DOS.Error.DirectoryNotEmpty, w.Error);
        Assert.Equal(3, AmigaDiskFileSystemCore.Read(ref p, b.Header, type, 0, Output, 3, Meta, Data, out _));
        Assert.Equal(new byte[] { 4, 5, 6 }, storage.Ram.AsSpan((int)Output.Raw, 3).ToArray());
        a.Position = 5; Assert.True(AmigaFfsWriteCore.SetMetadata(ref p, ref w, ref a, DosPacketAction.SetProtection));
        a.Position = 0; a.Buffer = Payload; a.Length = 1; storage.Transfers.Clear();
        Assert.False(AmigaFfsWriteCore.Write(ref p, ref w, ref a, out _)); Assert.Equal(DOS.Error.WriteProtected, w.Error);
        Assert.False(AmigaFfsWriteCore.Remove(ref p, ref w, a.Header, false)); Assert.Equal(DOS.Error.DeleteProtected, w.Error);
        Assert.DoesNotContain(storage.Transfers, t => t.Command is 3 or 25);
        Assert.True(AmigaFfsWriteCore.SetMetadata(ref p, ref w, ref a, DosPacketAction.SetProtection));
        storage.Ram[(int)Payload.Raw] = (byte)'X'; a.Length = 1;
        Assert.True(AmigaFfsWriteCore.SetMetadata(ref p, ref w, ref a, DosPacketAction.SetComment));
        Assert.True(p.ReadBlock(a.Header, Meta)); Assert.Equal((byte)'X', p.ReadUInt8(Meta, g.Tail + 329));
        a.Directory = g.RootBlock; a.Name = Name; a.Length = 3;
        // A different root name avoids colliding with the directory itself.
        Encoding.ASCII.GetBytes("Out").CopyTo(storage.Ram, (int)Name.Raw);
        Assert.True(AmigaFfsWriteCore.Rename(ref p, ref w, ref a));
        Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, dir.Header, false));
        Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, a.Header, false));
        Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, b.Header, false)); Assert.Equal(baseline, Used(ref p));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void OfsRejectsReadOnlyDiskFullAndUnsupportedFormatsBeforeWriting(int failure)
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(3520, 512, 2, out var g));
        var p = new Memory(storage, g); Format(ref p, g); SetDosType(ref p, 0x444F5300);
        var r = OfsFile(ref p, storage, g, "Data"); OfsWrite(ref p, storage, ref r, 0, new byte[488]);
        if (failure == 0) storage.Protected = true;
        if (failure == 1) { p.Clear(Bitmap, 512); Assert.True(p.WriteBlock(g.RootBlock + 1, Bitmap)); }
        if (failure == 2) SetDosType(ref p, 0x444F5302);
        r.Position = 488; r.Buffer = Payload; r.Length = 1; var w = Work(); storage.Transfers.Clear();
        Assert.False(AmigaFfsWriteCore.Write(ref p, ref w, ref r, out var actual)); Assert.Equal(0u, actual);
        Assert.Equal(failure == 0 ? DOS.Error.DiskWriteProtected : failure == 1 ? DOS.Error.DiskFull : DOS.Error.ActionNotKnown, w.Error);
        Assert.DoesNotContain(storage.Transfers, t => t.Command is 3 or 25);
    }

    [Fact]
    public void OfsFailedOverwriteReportsOnlyWrittenPrefixAndBlocksFurtherMutation()
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(3520, 512, 2, out var g));
        var p = new Memory(storage, g); Format(ref p, g); SetDosType(ref p, 0x444F5300);
        var r = OfsFile(ref p, storage, g, "Data"); OfsWrite(ref p, storage, ref r, 0, new byte[976]);
        Assert.True(p.ReadBlock(r.Header, Meta)); var first = p.ReadUInt32(Meta, 308); var second = p.ReadUInt32(Meta, 304);
        storage.FailWrite = second * 512UL; storage.Ram[(int)Payload.Raw] = 0x5A;
        r.Position = 487; r.Buffer = Payload; r.Length = 2; var w = Work();
        Assert.False(AmigaFfsWriteCore.Write(ref p, ref w, ref r, out var actual)); Assert.Equal(1u, actual);
        Assert.Equal(0x5A, storage.Sectors[first * 512UL][511]);
        Assert.True(p.ReadBlock(g.RootBlock, Meta)); Assert.Equal(0u, p.ReadUInt32(Meta, 312));
        storage.FailWrite = ulong.MaxValue; storage.Transfers.Clear();
        Assert.False(AmigaFfsWriteCore.Write(ref p, ref w, ref r, out _));
        Assert.False(AmigaFfsWriteCore.Trim(ref p, ref w, APTR.Null, 0, out _));
        Assert.DoesNotContain(storage.Transfers, t => t.Command is 3 or 25); Assert.Empty(storage.Discards);
    }
}
