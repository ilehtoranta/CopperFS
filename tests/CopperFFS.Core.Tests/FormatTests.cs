using Amiga;
using CopperSFS;
using CopperStart.Dos;
using System.Buffers.Binary;
using System.Text;

namespace CopperStart.FileSystem.Tests;

public sealed partial class VolumeGeometryTests
{
    private partial struct Memory : IAmigaFormatFileSystemPlatform
    {
        public bool WriteFormatBlock(uint block, APTR source) => WriteBlock(block, source);
    }

    [Theory]
    [InlineData(0x444F5300u, 512u, 1760u)]
    [InlineData(0x444F5301u, 512u, 3520u)]
    [InlineData(0x444F5301u, 512u, 120000u)]
    [InlineData(0x444F5300u, 1024u, 12000u)]
    [InlineData(0x444F5301u, 4096u, 12000u)]
    public void QuickFormatOfsFfsMountsWritesAndReclaimsAcrossBitmapChains(uint type, uint bytes, uint blocks)
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(blocks, bytes, 2, out var geometry));
        geometry.StartByte = 4096; var p = new Memory(storage, geometry);
        Encoding.ASCII.GetBytes("Fresh").CopyTo(storage.Ram, (int)Name.Raw);
        Assert.True(AmigaDiskFileSystemFormatCore.Format(ref p, type, Name, 5, 123, 45, 67, Meta, out var error), error.ToString());
        Assert.True(AmigaDiskFileSystemCore.Mount(ref p, Meta, out var mounted)); Assert.Equal(type, mounted);
        Assert.True(AmigaDiskFileSystemCore.CountUsed(ref p, Meta, Bitmap, out var before));
        var key = RoundTrip(ref p, storage, geometry, (int)bytes * 3 + 11);
        var work = Work(); Assert.True(AmigaFfsWriteCore.Remove(ref p, ref work, key, false), work.Error.ToString());
        Assert.True(AmigaDiskFileSystemCore.CountUsed(ref p, Meta, Bitmap, out var after)); Assert.Equal(before, after);
        Assert.All(storage.Sectors.Keys, offset => Assert.InRange(offset, geometry.StartByte, geometry.StartByte + (ulong)blocks * bytes - bytes));
    }

    [Theory]
    [InlineData(0x53465300u, 512u)] [InlineData(0x53465302u, 512u)]
    [InlineData(0x53465300u, 1024u)] [InlineData(0x53465302u, 4096u)]
    public void QuickFormatSmartMountsAndWritesWithBothRootCopies(uint type, uint bytes)
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(12000, bytes, 2, out var geometry));
        var p = new Memory(storage, geometry); Encoding.ASCII.GetBytes("Fresh").CopyTo(storage.Ram, (int)Name.Raw);
        Assert.True(AmigaSmartFileSystemFormatCore.Format(ref p, type, Name, 5, 4567, Meta, out var error), error.ToString());
        Assert.True(AmigaSmartFileSystemReadOnlyCore.Mount(ref p, Meta, out _));
        if (type == 0x53465300)
        {
            Encoding.ASCII.GetBytes(".recycled").CopyTo(storage.Ram, (int)Name.Raw);
            Assert.Equal(2u, AmigaSmartFileSystemCore.Resolve(ref p, 1, Name, 9, Meta, out _));
        }
        Encoding.ASCII.GetBytes("File").CopyTo(storage.Ram, (int)Name.Raw);
        if (type == 0x53465302)
        {
            var work = new AmigaSmartFileSystem2WriteCore.Work { Meta = Meta, Data = Data, Bitmap = Bitmap, Journal = Output };
            var request = new AmigaSmartFileSystem2WriteCore.Request { Directory = 1, Name = Name, Length = 4 };
            Assert.True(AmigaSmartFileSystem2WriteCore.Create(ref p, ref work, ref request, false), work.Error.ToString());
            request.Length = bytes + 11; request.Buffer = Payload;
            Assert.True(AmigaSmartFileSystem2WriteCore.Write(ref p, ref work, ref request, out var actual), work.Error.ToString());
            Assert.Equal(request.Length, actual);
        }
        else
        {
            var work = new AmigaSmartFileSystemWriteCore.Work { Meta = Meta, Data = Data, Bitmap = Bitmap, Journal = Output };
            var request = new AmigaSmartFileSystemWriteCore.Request { Directory = 1, Name = Name, Length = 4 };
            Assert.True(AmigaSmartFileSystemWriteCore.Create(ref p, ref work, ref request, false), work.Error.ToString());
            request.Length = bytes + 11; request.Buffer = Payload;
            Assert.True(AmigaSmartFileSystemWriteCore.Write(ref p, ref work, ref request, out var actual), work.Error.ToString());
            Assert.Equal(request.Length, actual);
        }
        Assert.True(AmigaSmartFileSystemReadOnlyCore.Mount(ref p, Meta, out _));
        Assert.NotEqual(0u, AmigaSmartFileSystemReadOnlyCore.Resolve(ref p, 1, Name, 4, Meta, out _));
        var image = new byte[checked((int)(geometry.BlockCount * bytes))];
        foreach (var (offset, sector) in storage.Sectors) sector.CopyTo(image, checked((int)offset));
        var report = CopperSFS.Check.SfsImageChecker.Check(new MemoryStream(image, writable: false), (int)bytes, 2);
        Assert.Contains(report.Entries, entry => entry.Path == "/File" && entry.Size == bytes + 11);
        if (type == 0x53465300)
        {
            var node = AmigaSmartFileSystemCore.Resolve(ref p, 1, Name, 4, Meta, out _);
            var work = new AmigaSmartFileSystemWriteCore.Work { Meta = Meta, Data = Data, Bitmap = Bitmap, Journal = Output };
            Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref p, ref work, node), work.Error.ToString());
            foreach (var (offset, sector) in storage.Sectors) sector.CopyTo(image, checked((int)offset));
            var recycled = CopperSFS.Check.SfsImageChecker.Check(new MemoryStream(image, false), (int)bytes, 2);
            Assert.Contains(recycled.Entries, entry => entry.Path == "/.recycled/File" && entry.Size == bytes + 11);
            var rootNumber = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(104));
            var damagedRoot = image.AsSpan((int)(rootNumber * bytes), (int)bytes);
            damagedRoot[(int)bytes - 32 + 3] ^= 1; BinaryPrimitives.WriteUInt32BigEndian(damagedRoot[4..], 0); uint sum = 1;
            for (var at = 0; at < damagedRoot.Length; at += 4) sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(damagedRoot[at..]));
            BinaryPrimitives.WriteUInt32BigEndian(damagedRoot[4..], unchecked(0u - sum));
            Assert.Contains("accounting", Assert.Throws<InvalidDataException>(() =>
                CopperSFS.Check.SfsImageChecker.Check(new MemoryStream(image, false), (int)bytes, 2)).Message);
        }
        if (type == 0x53465302)
        {
            // A node-2 sentinel is legal only without a recycled directory.
            var corrupted = image.ToArray();
            foreach (var number in new[] { 0u, geometry.BlockCount - 1 })
            {
                var block = corrupted.AsSpan((int)(number * bytes), (int)bytes); block[20] |= 64;
                BinaryPrimitives.WriteUInt32BigEndian(block[4..], 0); uint sum = 2;
                for (var at = 0; at < block.Length; at += 4) sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(block[at..]));
                BinaryPrimitives.WriteUInt32BigEndian(block[4..], unchecked(0u - sum));
            }
            Assert.Contains("reserved object-node", Assert.Throws<InvalidDataException>(() =>
                CopperSFS.Check.SfsImageChecker.Check(new MemoryStream(corrupted, false), (int)bytes, 2)).Message);
        }
        // Independent checksum, root identity and allocation accounting checks.
        foreach (var block in new[] { 0u, geometry.BlockCount - 1 })
        {
            var data = storage.Sectors[(ulong)block * bytes]; uint sum = type == 0x53465302 ? 2u : 1u;
            for (var at = 0; at < data.Length; at += 4) sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at)));
            Assert.Equal(0u, sum); Assert.Equal(type, BinaryPrimitives.ReadUInt32BigEndian(data));
        }
    }

    [Theory]
    [InlineData(0x444F5300u)] [InlineData(0x444F5301u)] [InlineData(0x53465300u)] [InlineData(0x53465302u)]
    public void QuickFormatRejectsProtectionNamesAndPropagatesIoFailure(uint type)
    {
        var storage = new Storage(); AmigaVolumeGeometry.Create(1760, 512, 2, out var geometry); var p = new Memory(storage, geometry);
        Encoding.ASCII.GetBytes("Fresh").CopyTo(storage.Ram, (int)Name.Raw);
        bool FormatNow(uint length, out DOS.Error error) => type is 0x53465300 or 0x53465302
            ? AmigaSmartFileSystemFormatCore.Format(ref p, type, Name, length, 0, Meta, out error)
            : AmigaDiskFileSystemFormatCore.Format(ref p, type, Name, length, 0, 0, 0, Meta, out error);
        storage.Protected = true; Assert.False(FormatNow(5, out var error)); Assert.Equal(DOS.Error.DiskWriteProtected, error); Assert.Equal(0, storage.WriteCalls);
        storage.Protected = false; Assert.False(FormatNow(0, out error)); Assert.Equal(DOS.Error.InvalidComponentName, error); Assert.Equal(0, storage.WriteCalls);
        storage.FailWriteCall = 4; Assert.False(FormatNow(5, out error)); Assert.Equal(DOS.Error.DiskNotValidated, error);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(storage.Sectors[0]));
    }
    [Theory]
    [InlineData(0x53465300u, 0u)] [InlineData(0x53465300u, 64u)]
    [InlineData(0x53465300u, 128u)] [InlineData(0x53465300u, 192u)]
    [InlineData(0x53465302u, 0u)] [InlineData(0x53465302u, 128u)]
    public void QuickFormatSmartOptionsControlLookupAndRecycledDirectory(uint type, uint flags)
    {
        var storage = new Storage(); AmigaVolumeGeometry.Create(128, 512, 2, out var g); var p = new Memory(storage, g);
        Encoding.ASCII.GetBytes("Fresh").CopyTo(storage.Ram, (int)Name.Raw);
        Assert.True(AmigaSmartFileSystemFormatCore.Format(ref p, type, Name, 5, 0, Meta, out var error, flags), error.ToString());
        Encoding.ASCII.GetBytes("Mixed").CopyTo(storage.Ram, (int)Name.Raw);
        if (type == 0x53465300)
        {
            var work = new AmigaSmartFileSystemWriteCore.Work { Meta = Meta, Data = Data, Bitmap = Bitmap, Journal = Output };
            var request = new AmigaSmartFileSystemWriteCore.Request { Directory = 1, Name = Name, Length = 5 };
            Assert.True(AmigaSmartFileSystemWriteCore.Create(ref p, ref work, ref request, false), work.Error.ToString());
        }
        else
        {
            var work = new AmigaSmartFileSystem2WriteCore.Work { Meta = Meta, Data = Data, Bitmap = Bitmap, Journal = Output };
            var request = new AmigaSmartFileSystem2WriteCore.Request { Directory = 1, Name = Name, Length = 5 };
            Assert.True(AmigaSmartFileSystem2WriteCore.Create(ref p, ref work, ref request, false), work.Error.ToString());
        }
        Encoding.ASCII.GetBytes("mixed").CopyTo(storage.Ram, (int)Name.Raw);
        var resolved = AmigaSmartFileSystemReadOnlyCore.Resolve(ref p, 1, Name, 5, Meta, out _);
        Assert.Equal((flags & 128) == 0, resolved != 0);
        Encoding.ASCII.GetBytes(".recycled").CopyTo(storage.Ram, (int)Name.Raw);
        Assert.Equal((flags & 64) != 0, AmigaSmartFileSystemReadOnlyCore.Resolve(ref p, 1, Name, 9, Meta, out _) != 0);
        var image = new byte[128 * 512]; foreach (var (offset, sector) in storage.Sectors) sector.CopyTo(image, checked((int)offset));
        CopperSFS.Check.SfsImageChecker.Check(new MemoryStream(image, false), 512, 2);
    }

}
