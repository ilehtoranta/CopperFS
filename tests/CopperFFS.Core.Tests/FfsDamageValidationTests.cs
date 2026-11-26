using System.Text;
using Amiga;
using CopperSFS;
using CopperStart.Dos;

namespace CopperStart.FileSystem.Tests;

// Damaged structures must be refused with DiskNotValidated, bounded work and
// no write that spreads the damage. Each case builds the damage by hand.
public sealed partial class VolumeGeometryTests
{
    private static void Reseal(ref Memory p, uint key)
    {
        p.WriteUInt32(Meta, 20, 0); uint sum = 0;
        for (var i = 0; i < 512; i += 4) sum = unchecked(sum + p.ReadUInt32(Meta, i));
        p.WriteUInt32(Meta, 20, unchecked(0u - sum)); Assert.True(p.WriteBlock(key, Meta));
    }

    private static (Storage Storage, Memory P, AmigaVolumeGeometry G, AmigaFfsWriteCore.Request File) DamageFixture(int blocks)
    {
        var storage = new Storage(); AmigaVolumeGeometry.Create(1760, 512, 2, out var g); var p = new Memory(storage, g);
        VariantFormat(ref p, storage, 0x444F5301);
        var file = VariantCreate(ref p, storage, g.RootBlock, "Victim");
        storage.Ram.AsSpan((int)Payload.Raw, blocks * 512).Fill(0x3C);
        file.Buffer = Payload; file.Length = (uint)(blocks * 512); var w = Work();
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _), w.Error.ToString());
        return (storage, p, g, file);
    }

    // One data block listed twice: freeing it the second time is a double free.
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DeletingAFileThatListsABlockTwiceIsRefused(bool indexed)
    {
        var (storage, p, g, file) = DamageFixture(3);
        Assert.True(p.ReadBlock(file.Header, Meta));
        p.WriteUInt32(Meta, 304, p.ReadUInt32(Meta, 308)); Reseal(ref p, file.Header);
        var w = Work();
        if (indexed) { w.Index = APTR.FromPointer(0x100000); AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536); }
        var used = Used(ref p);
        Assert.False(AmigaFfsWriteCore.Remove(ref p, ref w, file.Header, false));
        Assert.Equal(DOS.Error.DiskNotValidated, w.Error);
        Assert.True(Used(ref p) >= used - 1); // never more than the first copy's bit
    }

    // A data pointer aimed at the volume's bitmap page must not let an
    // overwrite land on allocation metadata.
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OverwriteThroughAPointerToTheBitmapIsRefused(bool indexed)
    {
        var (storage, p, g, file) = DamageFixture(2);
        Assert.True(p.ReadBlock(g.RootBlock, Meta)); var bitmap = p.ReadUInt32(Meta, g.Tail + 316);
        Assert.True(p.ReadBlock(file.Header, Meta)); p.WriteUInt32(Meta, 304, bitmap); Reseal(ref p, file.Header);
        Assert.True(p.ReadBlock(bitmap, Bitmap)); var before = storage.Ram.AsSpan((int)Bitmap.Raw, 512).ToArray();
        var w = Work();
        if (indexed) { w.Index = APTR.FromPointer(0x100000); AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536); }
        storage.Ram.AsSpan((int)Payload.Raw, 1024).Fill(0xEE);
        file.Buffer = Payload; file.Position = 0; file.Length = 1024;
        Assert.False(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _));
        Assert.Equal(DOS.Error.DiskNotValidated, w.Error);
        Assert.True(p.ReadBlock(bitmap, Bitmap));
        Assert.Equal(before, storage.Ram.AsSpan((int)Bitmap.Raw, 512).ToArray());
    }

    // A -> B -> A in one bucket: a lookup for a third name in that bucket must
    // stop after a few reads, not BlockCount checksummed reads.
    [Fact]
    public void CyclicHashChainIsRefusedAfterBoundedReads()
    {
        var storage = new Storage(); AmigaVolumeGeometry.Create(1760, 512, 2, out var g); var p = new Memory(storage, g);
        VariantFormat(ref p, storage, 0x444F5301);
        static uint Bucket(string name)
        {
            var hash = (uint)name.Length;
            foreach (var c in name) hash = hash * 13 + (uint)char.ToUpperInvariant(c);
            return (hash & 2047) % 72;
        }
        var names = Enumerable.Range(0, 5000).Select(i => "N" + i).Where(n => Bucket(n) == Bucket("A0")).Take(3).ToArray();
        var first = VariantCreate(ref p, storage, g.RootBlock, names[0]);
        var second = VariantCreate(ref p, storage, g.RootBlock, names[1]); // head: second -> first
        Assert.True(p.ReadBlock(first.Header, Meta)); p.WriteUInt32(Meta, 496, second.Header); Reseal(ref p, first.Header);
        Encoding.Latin1.GetBytes(names[2]).CopyTo(storage.Ram, (int)Name.Raw);
        storage.Transfers.Clear();
        Assert.Equal(0u, AmigaDiskFileSystemCore.Resolve(ref p, g.RootBlock, Name, (uint)names[2].Length, 0x444F5301, Meta, out var error));
        Assert.Equal(DOS.Error.DiskNotValidated, error);
        Assert.InRange(storage.Transfers.Count, 1, 16);
        // Creating in that bucket walks the same chain.
        var r = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = (uint)names[2].Length }; var w = Work();
        storage.Transfers.Clear();
        Assert.False(AmigaFfsWriteCore.Create(ref p, ref w, ref r, false));
        Assert.Equal(DOS.Error.DiskNotValidated, w.Error);
        Assert.InRange(storage.Transfers.Count, 1, 40);
        Assert.DoesNotContain(storage.Transfers, t => t.Command is 3 or 25);
    }

    // Block numbers at and beyond 2^31 in a hash chain and a pointer table.
    [Theory]
    [InlineData(0x7FFFFFFFu)] [InlineData(0x80000000u)] [InlineData(0xFFFFFFFFu)]
    public void BlockNumbersNear2To31AreRejected(uint key)
    {
        var (storage, p, g, file) = DamageFixture(2);
        Assert.True(p.ReadBlock(file.Header, Meta)); p.WriteUInt32(Meta, 304, key); Reseal(ref p, file.Header);
        var w = Work(); storage.Ram.AsSpan((int)Payload.Raw, 1024).Fill(0x11);
        file.Buffer = Payload; file.Position = 0; file.Length = 1024;
        Assert.False(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out _)); Assert.Equal(DOS.Error.DiskNotValidated, w.Error);
        Assert.InRange(AmigaDiskFileSystemCore.Read(ref p, file.Header, 0x444F5301, 0, Output, 1024, Meta, Data, out var readError), -1, 512);
        Assert.Equal(DOS.Error.DiskNotValidated, readError);
        // The same key as a hash successor ends a lookup the same way.
        Assert.True(p.ReadBlock(file.Header, Meta)); p.WriteUInt32(Meta, 304, p.ReadUInt32(Meta, 308) + 1);
        p.WriteUInt32(Meta, 496, key); Reseal(ref p, file.Header);
        static uint Bucket(string name)
        {
            var hash = (uint)name.Length;
            foreach (var c in name) hash = hash * 13 + (uint)char.ToUpperInvariant(c);
            return (hash & 2047) % 72;
        }
        var other = Enumerable.Range(0, 5000).Select(i => "N" + i).First(n => Bucket(n) == Bucket("Victim"));
        Encoding.Latin1.GetBytes(other).CopyTo(storage.Ram, (int)Name.Raw);
        Assert.Equal(0u, AmigaDiskFileSystemCore.Resolve(ref p, g.RootBlock, Name, (uint)other.Length, 0x444F5301, Meta, out var lookupError));
        Assert.Equal(DOS.Error.DiskNotValidated, lookupError);
    }
}
