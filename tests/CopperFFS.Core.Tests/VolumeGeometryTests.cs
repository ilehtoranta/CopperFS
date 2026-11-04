using CopperSFS;
using System.Buffers.Binary;
using System.Text;
using Amiga;
using CopperStart.Dos;

namespace CopperStart.FileSystem.Tests;

public sealed partial class VolumeGeometryTests
{
    private sealed partial class Storage
    {
        public readonly byte[] Ram = new byte[2 * 1024 * 1024];
        public readonly Dictionary<ulong, byte[]> Sectors = new();
        public readonly List<(ushort Command, ulong Offset, uint Bytes)> Transfers = new();
        public ulong FailWrite = ulong.MaxValue;
        public int WriteCalls, FailWriteCall;
        public long LongReads, GeometryReads;
    }

    private partial struct Memory(Storage storage, AmigaVolumeGeometry geometry) : IAmigaWritableFileSystemPlatform, IAmigaFileSystemDevicePlatform, IAmigaDiscardFileSystemPlatform
    {
        public uint BlockCount => Geometry.BlockCount;
        public uint BlockBytes => Geometry.BlockBytes;
        public uint ReservedBlocks => Geometry.ReservedBlocks;
        public AmigaVolumeGeometry Geometry { get { storage.GeometryReads++; return geometry; } }
        public bool WriteProtected() => storage.Protected;
        public bool Flush() { storage.Flushes++; return !storage.FailFlush; }
        public APTR ReadMetadataView(uint block, uint id, APTR scratch) => ReadMetadataBlock(block, id, scratch) ? scratch : APTR.Null;
        public bool ReadMetadataBlock(uint block, uint id, APTR target) => AmigaFileSystemBlockIo.ReadMetadata(ref this, block, id, target);
        public uint ReadDataBlocks(uint block, uint count, APTR target) => AmigaFileSystemBlockIo.Read(ref this, block, count, target);
        public bool ReadBlock(uint block, APTR target) => AmigaFileSystemDeviceCore.Transfer(ref this, geometry, false, block, 1, target);
        public bool WriteDataBlocks(uint block, uint count, APTR source) => AmigaFileSystemBlockIo.Write(ref this, block, count, source);
        public bool WriteBlock(uint block, APTR source) => AmigaFileSystemDeviceCore.Transfer(ref this, geometry, true, block, 1, source);
        public bool Transfer(ushort command, ulong offset, uint bytes, APTR buffer)
        {
            storage.Transfers.Add((command, offset, bytes));
            var write = command is 3 or 25;
            if (write && ++storage.WriteCalls == storage.FailWriteCall) return false;
            if (write && offset == storage.FailWrite) return false;
            for (var at = 0u; at < bytes; at += geometry.SectorBytes)
            {
                var address = offset + at;
                var ram = storage.Ram.AsSpan((int)(buffer.Raw + at), (int)geometry.SectorBytes);
                if (write) storage.Sectors[address] = ram.ToArray();
                else if (storage.Sectors.TryGetValue(address, out var sector)) sector.CopyTo(ram);
                else ram.Clear();
            }
            return true;
        }
        public byte ReadUInt8(APTR a, int offset = 0) => storage.Ram[(int)a.Raw + offset];
        public ushort ReadUInt16(APTR a, int offset = 0) => BinaryPrimitives.ReadUInt16BigEndian(storage.Ram.AsSpan((int)a.Raw + offset));
        public uint ReadUInt32(APTR a, int offset = 0)
        {
            storage.LongReads++;
            return BinaryPrimitives.ReadUInt32BigEndian(storage.Ram.AsSpan((int)a.Raw + offset));
        }
        public void WriteUInt8(APTR a, int offset, byte value) => storage.Ram[(int)a.Raw + offset] = value;
        public void WriteUInt16(APTR a, int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(storage.Ram.AsSpan((int)a.Raw + offset), value);
        public void WriteUInt32(APTR a, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(storage.Ram.AsSpan((int)a.Raw + offset), value);
        public bool IsMapped(APTR a, uint length) => a.Raw <= storage.Ram.Length && length <= storage.Ram.Length - a.Raw;
        public void Clear(APTR a, uint length) => storage.Ram.AsSpan((int)a.Raw, (int)length).Clear();
        public void Copy(APTR source, APTR target, uint length) => storage.Ram.AsSpan((int)source.Raw, (int)length).CopyTo(storage.Ram.AsSpan((int)target.Raw));
    }

    private static readonly APTR Meta = APTR.FromPointer(0x1000), Data = APTR.FromPointer(0x11000), Bitmap = APTR.FromPointer(0x21000);
    private static readonly APTR Name = APTR.FromPointer(0x31000), Payload = APTR.FromPointer(0x40000), Output = APTR.FromPointer(0x100000);
    private static AmigaFfsWriteCore.Work Work() => new() { Meta = Meta, Data = Data, Bitmap = Bitmap };

    [Fact]
    public void DeviceGeometryMountsAnHdVolumeAtItsActualRoot()
    {
        var storage = new Storage();
        AmigaVolumeGeometry.Create(3520, 512, 2, out var g);
        var p = new Memory(storage, g);
        p.WriteUInt32(Meta, 0, 512); p.WriteUInt32(Meta, 4, 3520); p.WriteUInt32(Meta, 20, 22);
        Assert.True(AmigaVolumeGeometry.FromDevice(ref p, Meta, out g));
        Assert.Equal(1760u, g.RootBlock); Assert.Equal(22u, g.TrackBlocks);
        Format(ref p, g);
        Assert.True(AmigaDiskFileSystemCore.Mount(ref p, Meta, out var type)); Assert.Equal(0x444F5301u, type);
        Assert.True(AmigaDiskFileSystemCore.CountUsed(ref p, Meta, Bitmap, out var used)); Assert.Equal(4u, used);
    }

    [Theory]
    [InlineData(512u, 3520u)]
    [InlineData(1024u, 10000u)]
    public void OfsReadsUseTheVolumeBlockSizeAndPreserveTheVerifiedPrefix(uint bytes, uint blocks)
    {
        var storage = new Storage(); AmigaVolumeGeometry.Create(blocks, bytes, 2, out var g);
        g.StartByte = 5UL * 1024 * 1024;
        var p = new Memory(storage, g); Format(ref p, g);
        p.Clear(Meta, bytes); p.WriteUInt32(Meta, 0, 0x444F5300); Assert.True(p.WriteBlock(0, Meta));
        var payload = bytes - 24;
        p.Clear(Meta, bytes); p.WriteUInt32(Meta, 0, 2); p.WriteUInt32(Meta, 4, 10);
        p.WriteUInt32(Meta, 8, 2); p.WriteUInt32(Meta, (int)bytes - 4, 0xFFFFFFFD);
        p.WriteUInt32(Meta, (int)bytes - 188, payload + 13);
        p.WriteUInt32(Meta, (int)bytes - 204, 11); p.WriteUInt32(Meta, (int)bytes - 208, 12);
        Checksum(ref p, Meta, bytes, 20); Assert.True(p.WriteBlock(10, Meta));
        for (var i = 0u; i < 2; i++)
        {
            p.Clear(Data, bytes); p.WriteUInt32(Data, 0, 8); p.WriteUInt32(Data, 4, 10);
            p.WriteUInt32(Data, 8, i + 1); p.WriteUInt32(Data, 12, i == 0 ? payload : 13);
            for (var j = 0u; j < (i == 0 ? payload : 13); j++) p.WriteUInt8(Data, 24 + (int)j, (byte)(j + i * 19));
            Checksum(ref p, Data, bytes, 20); Assert.True(p.WriteBlock(11 + i, Data));
        }
        Assert.True(AmigaDiskFileSystemCore.Mount(ref p, Meta, out var type));
        Assert.Equal((int)payload + 13, AmigaDiskFileSystemCore.Read(ref p, 10, type, 0, Output, payload + 13, Meta, Data, out var error));
        Assert.Equal(DOS.Error.None, error);
        Assert.Equal(Enumerable.Range(0, (int)payload).Select(i => (byte)i), storage.Ram.AsSpan((int)Output.Raw, (int)payload).ToArray());
        Assert.Equal(Enumerable.Range(0, 13).Select(i => (byte)(i + 19)), storage.Ram.AsSpan((int)(Output.Raw + payload), 13).ToArray());
        Assert.True(p.ReadBlock(12, Data)); p.WriteUInt8(Data, 24, 0xFF); Assert.True(p.WriteBlock(12, Data));
        Assert.Equal((int)payload, AmigaDiskFileSystemCore.Read(ref p, 10, type, 0, Output, payload + 13, Meta, Data, out error));
        Assert.Equal(DOS.Error.DiskNotValidated, error);
    }

    [Theory]
    [InlineData(512u, 1u, 3520u, 0u)]
    [InlineData(512u, 2u, 12000u, 7u)]
    [InlineData(1024u, 1u, 9000u, 13u)]
    [InlineData(4096u, 1u, 6000u, 4u)]
    public void EnvironmentControlsPartitionOffsetsAndLogicalBlockSize(uint sectorBytes, uint sectorsPerBlock, uint blocks, uint lowCylinder)
    {
        var storage = new Storage(); AmigaVolumeGeometry.Create(3520, 512, 2, out var initial);
        var p = new Memory(storage, initial);
        var env = APTR.FromPointer(0x800);
        p.WriteUInt32(env, 0, 14); p.WriteUInt32(env, 4, sectorBytes / 4);
        p.WriteUInt32(env, 12, 2); p.WriteUInt32(env, 16, sectorsPerBlock); p.WriteUInt32(env, 20, 10);
        p.WriteUInt32(env, 24, 2); p.WriteUInt32(env, 36, lowCylinder);
        p.WriteUInt32(env, 40, lowCylinder + blocks * sectorsPerBlock / 20 - 1);
        p.WriteUInt32(env, 52, 0x1FE00); p.WriteUInt32(env, 56, 0xFFFFFFFE);
        Assert.True(AmigaVolumeGeometry.FromEnvironment(ref p, env, out var g));
        Assert.Equal(blocks, g.BlockCount); Assert.Equal(sectorBytes * sectorsPerBlock, g.BlockBytes);
        Assert.Equal((ulong)lowCylinder * 20 * sectorBytes, g.StartByte);
        p = new Memory(storage, g); Format(ref p, g);
        RoundTrip(ref p, storage, g, 3 * (int)g.BlockBytes + 17);
        Assert.All(storage.Transfers, t => Assert.InRange(t.Offset, g.StartByte, g.StartByte + (ulong)g.BlockCount * g.BlockBytes - 1));
    }

    [Theory]
    [InlineData(512u, 10000u, 4064u)]
    [InlineData(1024u, 20000u, 8160u)]
    [InlineData(512u, 110000u, 101600u)]
    [InlineData(512u, 630000u, 617728u)]
    [InlineData(512u, 8404992u, 8388608u)]
    public void WritesCrossBitmapAndExtensionChainsAndReleaseAllAllocatedBlocks(uint bytes, uint blocks, uint freeStart)
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(blocks, bytes, 2, out var g));
        g.StartByte = 16UL * 1024 * 1024;
        var p = new Memory(storage, g); Format(ref p, g, freeStart);
        var usedBefore = Used(ref p);
        // Cross a file-extension boundary too, using a non-sector-aligned final payload.
        var key = RoundTrip(ref p, storage, g, (int)(g.TableEntries + 2) * (int)bytes + 13);
        var work = Work(); Assert.True(AmigaFfsWriteCore.Remove(ref p, ref work, key, false), work.Error.ToString());
        Assert.Equal(usedBefore, Used(ref p));
        Assert.True(AmigaDiskFileSystemCore.Header(ref p, g.RootBlock, Meta));
        Assert.Equal(uint.MaxValue, p.ReadUInt32(Meta, g.Tail + 312));
    }

    [Fact]
    public void FailedBitmapWriteLeavesTheVolumeInvalid()
    {
        var storage = new Storage(); AmigaVolumeGeometry.Create(12000, 512, 2, out var g);
        var p = new Memory(storage, g); Format(ref p, g, 4064);
        Assert.True(AmigaDiskFileSystemCore.BitmapBlock(ref p, 1, Meta, out var map));
        storage.FailWrite = (ulong)map * 512;
        Encoding.ASCII.GetBytes("Failure").CopyTo(storage.Ram, (int)Name.Raw);
        var work = Work(); var request = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 7 };
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref work, ref request, false));
        request.Buffer = Payload; request.Length = 4096;
        Assert.False(AmigaFfsWriteCore.Write(ref p, ref work, ref request, out _));
        Assert.Equal(DOS.Error.DiskNotValidated, work.Error);
        Assert.True(AmigaDiskFileSystemCore.Header(ref p, g.RootBlock, Meta)); Assert.Equal(0u, p.ReadUInt32(Meta, 312));
    }

    [Fact]
    public void DeviceTransfersSplitAtMaxTransferAndUseTd64BeyondFourGiB()
    {
        var storage = new Storage(); AmigaVolumeGeometry.Create(200, 4096, 2, out var g);
        g.SectorBytes = 512; g.MaxTransfer = 1537; g.StartByte = 0x100000000UL - 4096;
        var p = new Memory(storage, g);
        Assert.True(AmigaFileSystemDeviceCore.Transfer(ref p, g, true, 0, 2, Payload));
        Assert.Equal(new ushort[] { 3, 3, 25, 25, 25, 25 }, storage.Transfers.Select(t => t.Command));
        Assert.All(storage.Transfers, t => Assert.True(t.Bytes <= 1536 && t.Bytes % 512 == 0));
        Assert.Equal(8192u, storage.Transfers.Aggregate(0u, (sum, t) => sum + t.Bytes));
        var before = storage.Transfers.Count;
        Assert.False(AmigaFileSystemDeviceCore.Transfer(ref p, g, true, 199, 2, Payload));
        Assert.Equal(before, storage.Transfers.Count);
    }

    [Fact]
    public void MinimumEnvironmentDoesNotRequireOptionalFieldsAndRejectsOverflow()
    {
        var storage = new Storage(); AmigaVolumeGeometry.Create(3520, 512, 2, out var g);
        var p = new Memory(storage, g); var env = APTR.FromPointer(0x800);
        p.WriteUInt32(env, 0, 10); p.WriteUInt32(env, 4, 128); p.WriteUInt32(env, 12, 2);
        p.WriteUInt32(env, 16, 1); p.WriteUInt32(env, 20, 22); p.WriteUInt32(env, 24, 2); p.WriteUInt32(env, 40, 79);
        Assert.True(AmigaVolumeGeometry.FromEnvironment(ref p, env, out g)); Assert.Equal(3520u, g.BlockCount);
        p.WriteUInt32(env, 12, uint.MaxValue); p.WriteUInt32(env, 20, uint.MaxValue); p.WriteUInt32(env, 40, uint.MaxValue);
        Assert.False(AmigaVolumeGeometry.FromEnvironment(ref p, env, out _));
    }

    [Fact]
    public void ReservedAreaAndOddVolumeSizeDetermineRootAndBitmapOrigin()
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(3521, 512, 5, out var g));
        Assert.Equal(1762u, g.RootBlock);
        var p = new Memory(storage, g); Format(ref p, g);
        Assert.Equal(7u, Used(ref p));
        RoundTrip(ref p, storage, g, 3 * 512 + 17);
        Assert.Equal(12u, Used(ref p));
        Assert.All(storage.Transfers.Where(t => t.Command is 3 or 25 && t.Offset != 0),
            t => Assert.True(t.Offset >= 5 * 512));
    }

    private static uint RoundTrip(ref Memory p, Storage storage, AmigaVolumeGeometry g, int length)
    {
        Assert.True(AmigaDiskFileSystemCore.Mount(ref p, Meta, out var type));
        Encoding.ASCII.GetBytes("File").CopyTo(storage.Ram, (int)Name.Raw);
        var work = Work(); var r = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 4 };
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref work, ref r, false), work.Error.ToString());
        var expected = Enumerable.Range(0, length).Select(i => (byte)(i * 37 + 11)).ToArray();
        expected.CopyTo(storage.Ram, (int)Payload.Raw); r.Buffer = Payload; r.Length = (uint)length;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref work, ref r, out var actual), work.Error.ToString());
        Assert.Equal((uint)length, actual);
        Assert.Equal(r.Header, AmigaDiskFileSystemCore.Resolve(ref p, g.RootBlock, Name, 4, type, Meta, out _));
        Assert.Equal(length, AmigaDiskFileSystemCore.Read(ref p, r.Header, type, 0, Output, (uint)length, Meta, Data, out var error));
        Assert.Equal(DOS.Error.None, error); Assert.Equal(expected, storage.Ram.AsSpan((int)Output.Raw, length).ToArray());
        var fib = APTR.FromPointer(0x32000);
        Assert.True(AmigaDiskFileSystemCore.Examine(ref p, g.RootBlock, fib, Meta));
        Assert.Equal(DOS.Error.None, AmigaDiskFileSystemCore.ExamineNext(ref p, g.RootBlock, fib, Meta, Data));
        Assert.Equal((uint)length, p.ReadUInt32(fib, 124));
        return r.Header;
    }

    private static uint Used(ref Memory p)
    {
        Assert.True(AmigaDiskFileSystemCore.CountUsed(ref p, Meta, Bitmap, out var used)); return used;
    }

    [Fact]
    public void LargeVolumeCountReadsEachBitmapTableOnlyOnce()
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(8650752, 512, 2, out var g));
        var p = new Memory(storage, g); Format(ref p, g);
        storage.Transfers.Clear();
        var maps = (g.BlockCount - g.ReservedBlocks - 1) / g.BitmapBits + 1;
        var extensions = (maps - 26) / 127 + 1;
        Assert.Equal(g.ReservedBlocks + 1 + maps + extensions, Used(ref p));
        Assert.Equal(1 + maps + extensions, (uint)storage.Transfers.Count);
        Assert.All(storage.Transfers.GroupBy(t => t.Offset), group => Assert.Single(group));
    }

    [Fact]
    public void AllocationSkipsFourGiBOfOccupiedBlocksByBitmapWord()
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(8650752, 512, 2, out var g));
        var p = new Memory(storage, g); Format(ref p, g, 0x800003);
        Encoding.ASCII.GetBytes("Upper").CopyTo(storage.Ram, (int)Name.Raw);
        var work = Work(); var r = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 5 };
        storage.LongReads = 0; storage.Transfers.Clear();
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref work, ref r, false), work.Error.ToString());
        Assert.Equal(0x800003u, r.Header);
        // A regression to probing each occupied block exceeds eight million reads.
        Assert.InRange(storage.LongReads, 1, 2_000_000);
        // Includes the full preflight scan: allocation must also retain its
        // pointer-table position rather than restart the extension chain.
        Assert.InRange(storage.Transfers.Count, 1, 5_000);
    }

    [Fact]
    public void ValidatingMoreDataBlocksInOneBitmapDoesNotReloadGeometryPerBlock()
    {
        static long Overwrite(int blocks)
        {
            var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(3520, 512, 2, out var g));
            var p = new Memory(storage, g); Format(ref p, g);
            var length = blocks * 512;
            var header = RoundTrip(ref p, storage, g, length);
            var before = Used(ref p); var work = Work();
            p.WriteUInt8(Payload, 0, 0x5A);
            var request = new AmigaFfsWriteCore.Request { Header = header, Position = 1, Buffer = Payload, Length = 1 };
            storage.GeometryReads = 0;
            Assert.True(AmigaFfsWriteCore.Write(ref p, ref work, ref request, out var actual), work.Error.ToString());
            var geometryReads = storage.GeometryReads;
            Assert.Equal(1u, actual); Assert.Equal(before, Used(ref p));
            Assert.Equal(length, AmigaDiskFileSystemCore.Read(ref p, header, 0x444F5301, 0, Output, (uint)length, Meta, Data, out var error));
            Assert.Equal(DOS.Error.None, error);
            for (var i = 0; i < length; i++)
                Assert.Equal(i == 1 ? (byte)0x5A : (byte)(i * 37 + 11), storage.Ram[(int)Output.Raw + i]);
            return geometryReads;
        }
        var one = Overwrite(1); var many = Overwrite(64);
        Assert.True(many <= one + 8, $"Geometry reads grew from {one} to {many} within one pointer table and bitmap page.");
    }

    [Fact]
    public void AllocationIgnoresFreePaddingBitsInTheLastBitmapWord()
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(10003, 512, 5, out var g));
        var p = new Memory(storage, g); Format(ref p, g, g.BlockCount - 1);
        var index = (g.BlockCount - g.ReservedBlocks - 1) / g.BitmapBits;
        Assert.True(AmigaDiskFileSystemCore.BitmapBlock(ref p, index, Meta, out var map));
        Assert.True(p.ReadBlock(map, Bitmap));
        var bit = (g.BlockCount - g.ReservedBlocks - 1) % g.BitmapBits;
        p.WriteUInt32(Bitmap, 4 + (int)(bit / 32) * 4, uint.MaxValue << (int)(bit % 32));
        Checksum(ref p, Bitmap, g.BlockBytes, 0); Assert.True(p.WriteBlock(map, Bitmap));
        Assert.Equal(g.BlockCount - 1, Used(ref p));
        Encoding.ASCII.GetBytes("Last").CopyTo(storage.Ram, (int)Name.Raw);
        var work = Work(); var r = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 4 };
        Assert.True(AmigaFfsWriteCore.Create(ref p, ref work, ref r, false), work.Error.ToString());
        Assert.Equal(g.BlockCount - 1, r.Header);
        Assert.Equal(g.BlockCount, Used(ref p));
        p.WriteUInt8(Name, 0, (byte)'X');
        Assert.False(AmigaFfsWriteCore.Create(ref p, ref work, ref r, false));
        Assert.Equal(DOS.Error.DiskFull, work.Error);
    }

    [Theory]
    [InlineData(0)] // root marked free
    [InlineData(1)] // bitmap marked free
    [InlineData(2)] // extension marked free
    [InlineData(3)] // checksum damage in the final bitmap
    public void BitmapScanStillRejectsDamagedAllocationMetadataBeforeMutation(int damage)
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(630000, 512, 2, out var g));
        var p = new Memory(storage, g); Format(ref p, g);
        var maps = (g.BlockCount - g.ReservedBlocks - 1) / g.BitmapBits + 1;
        var key = g.RootBlock + (damage == 0 ? 0 : damage == 1 ? 1u : 1 + maps);
        var index = damage == 3 ? maps - 1 : (key - g.ReservedBlocks) / g.BitmapBits;
        Assert.True(AmigaDiskFileSystemCore.BitmapBlock(ref p, index, Meta, out var map));
        Assert.True(p.ReadBlock(map, Bitmap));
        if (damage == 3) p.WriteUInt32(Bitmap, 0, p.ReadUInt32(Bitmap) ^ 1);
        else
        {
            var bit = (key - g.ReservedBlocks) % g.BitmapBits;
            var at = 4 + (int)(bit / 32) * 4;
            p.WriteUInt32(Bitmap, at, p.ReadUInt32(Bitmap, at) | 1u << (int)(bit % 32));
            Checksum(ref p, Bitmap, g.BlockBytes, 0);
        }
        Assert.True(p.WriteBlock(map, Bitmap));
        Encoding.ASCII.GetBytes("Check").CopyTo(storage.Ram, (int)Name.Raw);
        var work = Work(); var r = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 5 };
        storage.Transfers.Clear();
        Assert.False(AmigaFfsWriteCore.Create(ref p, ref work, ref r, false));
        Assert.Equal(DOS.Error.DiskNotValidated, work.Error);
        Assert.DoesNotContain(storage.Transfers, t => t.Command is 3 or 25);
    }

    // Construct format records independently from the production mutation code.
    private static void Format(ref Memory p, AmigaVolumeGeometry g, uint freeStart = 2)
    {
        p.Clear(Meta, g.BlockBytes); p.WriteUInt32(Meta, 0, 0x444F5301); Assert.True(p.WriteBlock(0, Meta));
        var maps = (g.BlockCount - g.ReservedBlocks - 1) / ((g.BlockBytes - 4) * 8) + 1;
        var extensionCount = maps <= 25 ? 0 : (maps - 26) / (g.BlockBytes / 4 - 1) + 1;
        var mapStart = g.RootBlock + 1; var extensionStart = mapStart + maps;
        p.Clear(Meta, g.BlockBytes); p.WriteUInt32(Meta, 0, 2); p.WriteUInt32(Meta, 12, g.BlockBytes / 4 - 56);
        p.WriteUInt32(Meta, (int)g.BlockBytes - 200, uint.MaxValue);
        p.WriteUInt8(Meta, (int)g.BlockBytes - 80, 4);
        p.WriteUInt32(Meta, (int)g.BlockBytes - 79, 0x54657374);
        p.WriteUInt32(Meta, (int)g.BlockBytes - 4, 1);
        for (var i = 0u; i < Math.Min(maps, 25); i++) p.WriteUInt32(Meta, (int)g.BlockBytes - 196 + (int)i * 4, mapStart + i);
        if (extensionCount != 0) p.WriteUInt32(Meta, (int)g.BlockBytes - 96, extensionStart);
        Checksum(ref p, Meta, g.BlockBytes, 20); Assert.True(p.WriteBlock(g.RootBlock, Meta));
        for (var i = 0u; i < extensionCount; i++)
        {
            p.Clear(Meta, g.BlockBytes);
            var entries = g.BlockBytes / 4 - 1;
            for (var j = 0u; j < entries && 25 + i * entries + j < maps; j++)
                p.WriteUInt32(Meta, (int)j * 4, mapStart + 25 + i * entries + j);
            if (i + 1 < extensionCount) p.WriteUInt32(Meta, (int)g.BlockBytes - 4, extensionStart + i + 1);
            Assert.True(p.WriteBlock(extensionStart + i, Meta));
        }
        for (var i = 0u; i < maps; i++)
        {
            p.Clear(Bitmap, g.BlockBytes);
            for (var bit = 0u; bit < (g.BlockBytes - 4) * 8; bit++)
            {
                var key = g.ReservedBlocks + i * (g.BlockBytes - 4) * 8 + bit;
                if (key >= g.BlockCount || key < freeStart || key == g.RootBlock || key >= mapStart && key < extensionStart + extensionCount) continue;
                var at = 4 + (int)(bit / 32) * 4;
                p.WriteUInt32(Bitmap, at, p.ReadUInt32(Bitmap, at) | 1u << (int)(bit % 32));
            }
            Checksum(ref p, Bitmap, g.BlockBytes, 0); Assert.True(p.WriteBlock(mapStart + i, Bitmap));
        }
    }

    private static void Checksum(ref Memory p, APTR buffer, uint bytes, int checksum)
    {
        p.WriteUInt32(buffer, checksum, 0); uint sum = 0;
        for (var i = 0; i < bytes; i += 4) sum = unchecked(sum + p.ReadUInt32(buffer, i));
        p.WriteUInt32(buffer, checksum, unchecked(0u - sum));
    }
}
