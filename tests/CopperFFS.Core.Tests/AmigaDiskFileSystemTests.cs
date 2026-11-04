using CopperSFS;
using System.Buffers.Binary;
using System.Text;
using Amiga;
using CopperStart.Dos;

namespace CopperStart.Floppy.Native.Tests;

public sealed class AmigaDiskFileSystemTests
{
    private struct Memory(byte[] disk) : IAmigaWritableFileSystemPlatform
    {
        public uint BlockCount => Geometry.BlockCount;
        public uint BlockBytes => Geometry.BlockBytes;
        public uint ReservedBlocks => Geometry.ReservedBlocks;
        public AmigaVolumeGeometry Geometry { get { AmigaVolumeGeometry.Create(1760, 512, 2, out var g); return g; } }
        public readonly byte[] Ram = new byte[65536];
        public uint BadBlock = uint.MaxValue;
        public bool Protected;
        public uint FailWriteBlock = uint.MaxValue;
        public uint PointerTableReads;
        public uint BulkCopies;
        public bool WriteProtected() => Protected;
        public bool Flush() => true;
        public bool WriteDataBlocks(uint block, uint count, APTR source) => AmigaFileSystemBlockIo.Write(ref this, block, count, source);
        public bool WriteBlock(uint block, APTR source)
        { if (Protected || block == BadBlock || block == FailWriteBlock || block >= 1760) return false; Ram.AsSpan((int)source.Raw, 512).CopyTo(disk.AsSpan((int)block * 512)); return true; }
        public APTR ReadMetadataView(uint block, uint id, APTR scratch) => ReadMetadataBlock(block, id, scratch) ? scratch : APTR.Null;
        public bool ReadMetadataBlock(uint block, uint id, APTR target) => AmigaFileSystemBlockIo.ReadMetadata(ref this, block, id, target);
        public uint ReadDataBlocks(uint block, uint count, APTR target) => AmigaFileSystemBlockIo.Read(ref this, block, count, target);
        public bool ReadBlock(uint block, APTR destination)
        { if (block == BadBlock || block >= 1760) return false; var type = BinaryPrimitives.ReadUInt32BigEndian(disk.AsSpan((int)block * 512)); if (type is 2 or 16) PointerTableReads++; disk.AsSpan((int)block * 512, 512).CopyTo(Ram.AsSpan((int)destination.Raw)); return true; }
        public byte ReadUInt8(APTR a, int offset = 0) => Ram[a.Raw + offset];
        public ushort ReadUInt16(APTR a, int offset = 0) => BinaryPrimitives.ReadUInt16BigEndian(Ram.AsSpan((int)a.Raw + offset));
        public uint ReadUInt32(APTR a, int offset = 0) => BinaryPrimitives.ReadUInt32BigEndian(Ram.AsSpan((int)a.Raw + offset));
        public void WriteUInt8(APTR a, int offset, byte value) => Ram[a.Raw + offset] = value;
        public void WriteUInt16(APTR a, int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(Ram.AsSpan((int)a.Raw + offset), value);
        public void WriteUInt32(APTR a, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(Ram.AsSpan((int)a.Raw + offset), value);
        public bool IsMapped(APTR a, uint length) => a.Raw <= Ram.Length && length <= Ram.Length - a.Raw;
        public void Clear(APTR a, uint length) => Ram.AsSpan((int)a.Raw, (int)length).Clear();
        public void Copy(APTR source, APTR destination, uint length)
        { BulkCopies++; Ram.AsSpan((int)source.Raw, (int)length).CopyTo(Ram.AsSpan((int)destination.Raw)); }
        public APTR Name(string name) { Encoding.ASCII.GetBytes(name).CopyTo(Ram, 3000); return APTR.FromPointer(3000); }
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(-3, true)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(-4, false)]
    [InlineData(1, false)]
    public void OrdinaryHeaderValidationDoesNotAdmitLinkTypes(int type, bool valid)
    {
        var disk = DosDiskImage.Create(true);
        disk.Write(disk.PayloadHeader, 508, unchecked((uint)type));
        disk.Checksum(disk.PayloadHeader);
        var p = new Memory(disk.Bytes);
        Assert.Equal(valid, AmigaDiskFileSystemCore.Header(ref p, disk.PayloadHeader, APTR.FromPointer(1000)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadCopiesVerifiedSectorSpansWithExactUnalignedTails(bool ffs)
    {
        var disk = DosDiskImage.Create(ffs);
        for (var alignment = 0; alignment < 4; alignment++)
        {
            var p = new Memory(disk.Bytes);
            var target = 4000 + alignment;
            p.Ram.AsSpan(target - 8, 616).Fill(0xA5);
            Assert.Equal(600, AmigaDiskFileSystemCore.Read(ref p, disk.PayloadHeader,
                ffs ? 0x444F5301u : 0x444F5300u, 3, APTR.FromPointer((uint)target), 600,
                APTR.FromPointer(1000), APTR.FromPointer(1600), out var error));
            Assert.Equal(DOS.Error.None, error);
            Assert.Equal(DosDiskImage.Payload.AsSpan(3, 600).ToArray(), p.Ram.AsSpan(target, 600).ToArray());
            Assert.All(p.Ram.AsSpan(target - 8, 8).ToArray(), b => Assert.Equal(0xA5, b));
            Assert.All(p.Ram.AsSpan(target + 600, 8).ToArray(), b => Assert.Equal(0xA5, b));
            Assert.Equal(2u, p.BulkCopies);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadRetainsForwardByteSemanticsWhenCallerAliasesScratch(bool ffs)
    {
        var disk = DosDiskImage.Create(ffs); var p = new Memory(disk.Bytes);
        var offset = ffs ? 0 : 24;
        var expected = new byte[512];
        DosDiskImage.Payload.AsSpan(0, ffs ? 512 : 488).CopyTo(expected.AsSpan(offset));
        for (var i = 0; i < 64; i++) expected[1 + i] = expected[offset + i];
        Assert.Equal(64, AmigaDiskFileSystemCore.Read(ref p, disk.PayloadHeader,
            ffs ? 0x444F5301u : 0x444F5300u, 0, APTR.FromPointer(1601), 64,
            APTR.FromPointer(1000), APTR.FromPointer(1600), out var error));
        Assert.Equal(DOS.Error.None, error);
        Assert.Equal(expected.AsSpan(1, 64).ToArray(), p.Ram.AsSpan(1601, 64).ToArray());
        Assert.Equal(0u, p.BulkCopies);
    }

    [Fact]
    public void MetadataUpdatesPreserveFileDataAndProduceReadableChecksummedHeaders()
    {
        var disk = DosDiskImage.Create(true);
        var p = new Memory(disk.Bytes);
        var w = new AmigaFfsWriteCore.Work { Meta = APTR.FromPointer(1000), Data = APTR.FromPointer(1600), Bitmap = APTR.FromPointer(2200) };
        var r = new AmigaFfsWriteCore.Request { Header = disk.PayloadHeader, Position = 0x20, Buffer = APTR.FromPointer(4000) };
        Assert.True(AmigaFfsWriteCore.SetMetadata(ref p, ref w, ref r, DosPacketAction.SetProtection));
        p.WriteUInt32(r.Buffer, 0, 1234); p.WriteUInt32(r.Buffer, 4, 56); p.WriteUInt32(r.Buffer, 8, 78);
        Assert.True(AmigaFfsWriteCore.SetMetadata(ref p, ref w, ref r, DosPacketAction.SetDate));
        Encoding.ASCII.GetBytes("notes").CopyTo(p.Ram, 4000); r.Length = 5;
        Assert.True(AmigaFfsWriteCore.SetMetadata(ref p, ref w, ref r, DosPacketAction.SetComment));
        var fib = APTR.FromPointer(5000);
        Assert.True(AmigaDiskFileSystemCore.Examine(ref p, r.Header, fib, w.Meta));
        Assert.Equal(0x20u, p.ReadUInt32(fib, 116));
        Assert.Equal(1234u, p.ReadUInt32(fib, 132)); Assert.Equal(56u, p.ReadUInt32(fib, 136)); Assert.Equal(78u, p.ReadUInt32(fib, 140));
        Assert.Equal(5, p.ReadUInt8(fib, 144)); Assert.Equal("notes", Encoding.ASCII.GetString(p.Ram, 5145, 5));
        Assert.Equal(800, AmigaDiskFileSystemCore.Read(ref p, r.Header, 0x444F5301, 0, APTR.FromPointer(6000), 800, w.Meta, w.Data, out var error));
        Assert.Equal(DOS.Error.None, error); Assert.Equal(DosDiskImage.Payload, p.Ram.AsSpan(6000, 800).ToArray());
        r.Length = 0;
        Assert.True(AmigaFfsWriteCore.SetMetadata(ref p, ref w, ref r, DosPacketAction.SetComment));
        Assert.True(AmigaDiskFileSystemCore.Examine(ref p, r.Header, fib, w.Meta)); Assert.Equal(0, p.ReadUInt8(fib, 144));
        Assert.True(AmigaDiskFileSystemCore.Header(ref p, 880, w.Meta)); Assert.Equal(uint.MaxValue, p.ReadUInt32(w.Meta, 312));
    }

    [Fact]
    public void MetadataFailuresPreserveProtectionAndTransactionState()
    {
        var disk = DosDiskImage.Create(true); var original = disk.Bytes.ToArray();
        var p = new Memory(disk.Bytes) { Protected = true };
        var w = new AmigaFfsWriteCore.Work { Meta = APTR.FromPointer(1000), Data = APTR.FromPointer(1600), Bitmap = APTR.FromPointer(2200) };
        var r = new AmigaFfsWriteCore.Request { Header = disk.PayloadHeader, Position = 0x20, Buffer = APTR.FromPointer(4000) };
        Assert.False(AmigaFfsWriteCore.SetMetadata(ref p, ref w, ref r, DosPacketAction.SetProtection));
        Assert.Equal(DOS.Error.DiskWriteProtected, w.Error); Assert.Equal(original, disk.Bytes);
        p.Protected = false; r.Length = 80;
        Assert.False(AmigaFfsWriteCore.SetMetadata(ref p, ref w, ref r, DosPacketAction.SetComment));
        Assert.Equal(DOS.Error.CommentTooBig, w.Error); Assert.Equal(original, disk.Bytes);
        p.FailWriteBlock = disk.PayloadHeader;
        Assert.False(AmigaFfsWriteCore.SetMetadata(ref p, ref w, ref r, DosPacketAction.SetProtection));
        Assert.Equal(DOS.Error.DiskNotValidated, w.Error);
        Assert.True(AmigaDiskFileSystemCore.Header(ref p, 880, w.Meta)); Assert.Equal(0u, p.ReadUInt32(w.Meta, 312));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaseInsensitiveDirectoriesAndFileExtensionsReadStandardDisk(bool ffs)
    {
        var disk = DosDiskImage.Create(ffs);
        var p = new Memory(disk.Bytes);
        var metadata = APTR.FromPointer(1000);
        var data = APTR.FromPointer(1600);
        Assert.True(AmigaDiskFileSystemCore.Mount(ref p, metadata, out var type));
        var path = "data/laRGe";
        var file = AmigaDiskFileSystemCore.Resolve(ref p, 880, p.Name(path), (uint)path.Length, type, metadata, out var error);
        Assert.Equal(DOS.Error.None, error);
        Assert.NotEqual(0u, file);
        var count = AmigaDiskFileSystemCore.Read(ref p, file, type, 35000, APTR.FromPointer(4000), 2000, metadata, data, out error);
        Assert.Equal(2000, count);
        Assert.Equal(DOS.Error.None, error);
        Assert.Equal(Enumerable.Range(35000, 2000).Select(i => (byte)(i * 17)), p.Ram.AsSpan(4000, 2000).ToArray());
        Assert.True(AmigaDiskFileSystemCore.Examine(ref p, file, APTR.FromPointer(6500), metadata));
        Assert.Equal("Large", PacketName(p, 6500));
        Assert.Equal(42000u, p.ReadUInt32(APTR.FromPointer(6500), 124));
        Assert.Equal(-3, unchecked((int)p.ReadUInt32(APTR.FromPointer(6500), 4)));
        count = AmigaDiskFileSystemCore.Read(ref p, file, type, 41980, APTR.FromPointer(4000), 200, metadata, data, out error);
        Assert.Equal(20, count);
        Assert.Equal(DOS.Error.None, error);
        Assert.Equal(0, AmigaDiskFileSystemCore.Read(ref p, file, type, 42000, APTR.FromPointer(4000), 1, metadata, data, out error));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SequentialReadVisitsEachPointerTableOnceAndRetainsVerifiedPrefixOnBadExtension(bool ffs)
    {
        var disk = DosDiskImage.Create(ffs);
        var p = new Memory(disk.Bytes);
        var metadata = APTR.FromPointer(1000); var data = APTR.FromPointer(1600);
        var file = AmigaDiskFileSystemCore.Resolve(ref p, 880, p.Name("Data/Large"), 10,
            ffs ? 0x444F5301u : 0x444F5300u, metadata, out _);
        p.PointerTableReads = 0;
        Assert.Equal(42000, AmigaDiskFileSystemCore.Read(ref p, file, ffs ? 0x444F5301u : 0x444F5300u,
            0, APTR.FromPointer(4000), 42000, metadata, data, out var error));
        Assert.Equal(DOS.Error.None, error);
        Assert.Equal(2u, p.PointerTableReads);
        Assert.Equal(Enumerable.Range(0, 42000).Select(i => (byte)(i * 17)), p.Ram.AsSpan(4000, 42000).ToArray());
        var extension = BinaryPrimitives.ReadUInt32BigEndian(disk.Bytes.AsSpan((int)file * 512 + 504));
        disk.Bytes[extension * 512 + 100] ^= 1;
        p.Clear(APTR.FromPointer(4000), 42000);
        var prefix = (ffs ? 512 : 488) * 72;
        Assert.Equal(prefix, AmigaDiskFileSystemCore.Read(ref p, file, ffs ? 0x444F5301u : 0x444F5300u,
            0, APTR.FromPointer(4000), 42000, metadata, data, out error));
        Assert.Equal(DOS.Error.DiskNotValidated, error);
        Assert.Equal(Enumerable.Range(0, prefix).Select(i => (byte)(i * 17)), p.Ram.AsSpan(4000, prefix).ToArray());
        Assert.All(p.Ram.AsSpan(4000 + prefix, 42000 - prefix).ToArray(), b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorruptHeaderAndInvalidPointerAreRejected(bool ffs)
    {
        var disk = DosDiskImage.Create(ffs);
        var p = new Memory(disk.Bytes);
        var metadata = APTR.FromPointer(1000);
        var data = APTR.FromPointer(1600);
        Assert.True(AmigaDiskFileSystemCore.Mount(ref p, metadata, out var type));
        disk.Write(disk.PayloadHeader, 308, 1760);
        disk.Checksum(disk.PayloadHeader);
        Assert.Equal(-1, AmigaDiskFileSystemCore.Read(ref p, disk.PayloadHeader, type, 0, APTR.FromPointer(4000), 20, metadata, data, out var error));
        Assert.Equal(DOS.Error.DiskNotValidated, error);
        Assert.All(p.Ram.AsSpan(4000, 20).ToArray(), b => Assert.Equal(0, b));
        disk.Bytes[disk.PayloadHeader * 512 + 100] ^= 1;
        Assert.False(AmigaDiskFileSystemCore.Header(ref p, disk.PayloadHeader, metadata));
    }

    [Fact]
    public void OfsDataChecksumFailureCommitsOnlyVerifiedPrefix()
    {
        var disk = DosDiskImage.Create(false);
        var p = new Memory(disk.Bytes);
        var first = BinaryPrimitives.ReadUInt32BigEndian(disk.Bytes.AsSpan((int)disk.PayloadHeader * 512 + 308));
        var second = BinaryPrimitives.ReadUInt32BigEndian(disk.Bytes.AsSpan((int)disk.PayloadHeader * 512 + 304));
        disk.Bytes[second * 512 + 30] ^= 1;
        var result = AmigaDiskFileSystemCore.Read(ref p, disk.PayloadHeader, 0x444F5300, 0, APTR.FromPointer(4000), 800,
            APTR.FromPointer(1000), APTR.FromPointer(1600), out var error);
        Assert.Equal(488, result);
        Assert.Equal(DOS.Error.DiskNotValidated, error);
        Assert.Equal(DosDiskImage.Payload.AsSpan(0, 488).ToArray(), p.Ram.AsSpan(4000, 488).ToArray());
        Assert.All(p.Ram.AsSpan(4488, 312).ToArray(), b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectoryEnumerationReturnsAllEntriesAndThenNoMoreEntries(bool ffs)
    {
        var disk = DosDiskImage.Create(ffs);
        var p = new Memory(disk.Bytes);
        var metadata = APTR.FromPointer(1000);
        var table = APTR.FromPointer(1600);
        var fib = APTR.FromPointer(6500);
        Assert.True(AmigaDiskFileSystemCore.Examine(ref p, 880, fib, metadata));
        Assert.Equal("NativeBoot", PacketName(p, 6500));
        var names = new HashSet<string>();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(DOS.Error.None, AmigaDiskFileSystemCore.ExamineNext(ref p, 880, fib, metadata, table));
            Assert.True(names.Add(PacketName(p, 6500)));
        }
        Assert.Equal(new[] { "C", "Data", "S" }, names.Order());
        Assert.Equal(DOS.Error.NoMoreEntries, AmigaDiskFileSystemCore.ExamineNext(ref p, 880, fib, metadata, table));
    }


    private static AmigaFfsWriteCore.Work Scratch() => new()
    { Meta = APTR.FromPointer(1000), Data = APTR.FromPointer(1600), Bitmap = APTR.FromPointer(2200) };

    [Fact]
    public void NativeFfsMutationsReopenThroughIndependentReadCoreAndRecoverAllocation()
    {
        var disk = DosDiskImage.Create(true); var p = new Memory(disk.Bytes); var w = Scratch();
        var originalBitmap = disk.Bytes.AsSpan(881 * 512, 512).ToArray();
        var r = new AmigaFfsWriteCore.Request { Directory = 880, Name = p.Name("Data/Saved"), Length = 10 };
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref r, false)); var file = r.Header;
        for (var i = 0; i < 50000; i++) p.Ram[9000 + i] = (byte)(i * 19);
        r = new() { Header = file, Buffer = APTR.FromPointer(9000), Length = 50000 };
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref r, out var actual)); Assert.Equal(50000u, actual);
        var fresh = new Memory(disk.Bytes); var expected = p.Ram.AsSpan(9000, 50000).ToArray();
        Assert.Equal(50000, AmigaDiskFileSystemCore.Read(ref fresh, file, 0x444F5301, 0, APTR.FromPointer(9000), 50000, w.Meta, w.Data, out var error));
        Assert.Equal(DOS.Error.None, error); Assert.Equal(expected, fresh.Ram.AsSpan(9000, 50000).ToArray());
        r = new() { Directory = 880, Header = file, Name = p.Name("S/Renamed"), Length = 9 };
        Assert.True(AmigaFfsWriteCore.Rename(ref p, ref w, ref r));
        Assert.Equal(file, AmigaDiskFileSystemCore.Resolve(ref fresh, 880, fresh.Name("S/Renamed"), 9, 0x444F5301, w.Meta, out error));
        Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, file, false));
        Assert.Equal(originalBitmap, disk.Bytes.AsSpan(881 * 512, 512).ToArray());
        Assert.Equal(0u, AmigaDiskFileSystemCore.Resolve(ref fresh, 880, fresh.Name("S/Renamed"), 9, 0x444F5301, w.Meta, out error));
        Assert.Equal(DOS.Error.ObjectNotFound, error);
    }
    [Fact]
    public void ReadOnlyAndFullDiskLeaveOriginalImageUntouched()
    {
        var disk = DosDiskImage.Create(true); var p = new Memory(disk.Bytes) { Protected = true }; var w = Scratch();
        var before = disk.Bytes.ToArray();
        var r = new AmigaFfsWriteCore.Request { Directory = 880, Name = p.Name("New"), Length = 3 };
        Assert.False(AmigaFfsWriteCore.Create(ref p, ref w, ref r, false)); Assert.Equal(DOS.Error.DiskWriteProtected, w.Error); Assert.Equal(before, disk.Bytes);
        p.Protected = false;
        disk.Bytes.AsSpan(881 * 512, 512).Clear(); // Valid bitmap with no free blocks.
        before = disk.Bytes.ToArray();
        Assert.False(AmigaFfsWriteCore.Create(ref p, ref w, ref r, false)); Assert.Equal(DOS.Error.DiskFull, w.Error); Assert.Equal(before, disk.Bytes);
    }
    [Fact]
    public void NativeFfsOverwriteTruncateAndNonemptyDirectoryRulesSurviveRemount()
    {
        var disk = DosDiskImage.Create(true); var p = new Memory(disk.Bytes); var w = Scratch();
        var dir = new AmigaFfsWriteCore.Request { Directory = 880, Name = p.Name("User"), Length = 4 };
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref dir, true));
        var r = new AmigaFfsWriteCore.Request { Directory = dir.Header, Name = p.Name("prefs"), Length = 5 };
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref r, false)); var key = r.Header;
        Assert.False(AmigaFfsWriteCore.Remove(ref p, ref w, dir.Header, false)); Assert.Equal(DOS.Error.DirectoryNotEmpty, w.Error);
        Encoding.ASCII.GetBytes("settings saved").CopyTo(p.Ram, 9000);
        r = new() { Header = key, Buffer = APTR.FromPointer(9000), Length = 14 };
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref r, out _));
        r.Position = 2; r.Length = 3; p.Ram[9000] = (byte)'X';
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref r, out _));
        Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, key, true));
        Assert.True(AmigaDiskFileSystemCore.Header(ref p, key, w.Meta)); Assert.Equal(0u, p.ReadUInt32(w.Meta, 324));
        Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, key, false));
        Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, dir.Header, false));
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(disk.Bytes.AsSpan(880 * 512 + 312)));
    }
    [Fact]
    public void DeviceFailureLeavesMetadataInvalidRatherThanPublishingPartialSuccess()
    {
        var disk=DosDiskImage.Create(true);var p=new Memory(disk.Bytes){FailWriteBlock=881};var w=Scratch();
        var r=new AmigaFfsWriteCore.Request{Directory=880,Name=p.Name("Interrupted"),Length=11};
        Assert.False(AmigaFfsWriteCore.Create(ref p,ref w,ref r,false));Assert.Equal(DOS.Error.DiskNotValidated,w.Error);
        Assert.Equal(0u,BinaryPrimitives.ReadUInt32BigEndian(disk.Bytes.AsSpan(880*512+312)));
        var partial=disk.Bytes.ToArray();p.FailWriteBlock=uint.MaxValue;
        r=new(){Directory=880,Name=p.Name("RetryName"),Length=9};
        Assert.False(AmigaFfsWriteCore.Create(ref p,ref w,ref r,false));Assert.Equal(DOS.Error.DiskNotValidated,w.Error);
        Assert.Equal(partial,disk.Bytes);
    }
    // Consume the same packet FIB layout as native DOS NameFromLock/ExNext.
    // A C string here loses its first character and treats it as the length.
    private static string PacketName(Memory memory, int address)
    {
        var fib = APTR.FromPointer((uint)address);
        var count = DosFileInfoBlockCodec.ReadFileNameLength(ref memory, fib);
        return new string(Enumerable.Range(0, (int)count).Select(index =>
            (char)DosFileInfoBlockCodec.ReadFileNameByte(ref memory, fib, (uint)index)).ToArray());
    }
}
