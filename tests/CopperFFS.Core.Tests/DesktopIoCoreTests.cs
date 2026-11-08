using System.Buffers.Binary;
using System.Text;
using Amiga;
using CopperStart.Dos;
#if !COPPERSTART_DESKTOP_IO_BASELINE
using CopperSFS;
#endif

namespace CopperStart.Floppy.Native.Tests;

public sealed class DesktopIoCoreTests
{
    private struct Memory(byte[] disk) : IAmigaWritableFileSystemPlatform
    {
#if !COPPERSTART_DESKTOP_IO_BASELINE
        public uint BlockCount => Geometry.BlockCount;
        public uint BlockBytes => Geometry.BlockBytes;
        public uint ReservedBlocks => Geometry.ReservedBlocks;
        public AmigaVolumeGeometry Geometry { get { AmigaVolumeGeometry.Create(1760, 512, 2, out var g); return g; } }
#endif
        public readonly byte[] Ram = new byte[65536];
        public uint Reads, Words, Bad = uint.MaxValue;
        public bool WriteProtected() => false;
        public bool Flush() => true;
#if !COPPERSTART_DESKTOP_IO_BASELINE
        public APTR ReadMetadataView(uint block, uint id, APTR scratch) => ReadMetadataBlock(block, id, scratch) ? scratch : APTR.Null;
        public bool ReadMetadataBlock(uint block, uint id, APTR target) => AmigaFileSystemBlockIo.ReadMetadata(ref this, block, id, target);
        public uint ReadDataBlocks(uint block, uint count, APTR target, bool readAhead) =>
            ReadDataBlocks(block, count, target);
        public uint ReadDataBlocks(uint block, uint count, APTR target) => AmigaFileSystemBlockIo.Read(ref this, block, count, target);
        public bool WriteDataBlocks(uint block, uint count, APTR source) => AmigaFileSystemBlockIo.Write(ref this, block, count, source);
#endif
        public bool ReadBlock(uint key, APTR to)
        { Reads++; if (key == Bad || key >= 1760) return false; disk.AsSpan((int)key * 512, 512).CopyTo(Ram.AsSpan((int)to.Raw)); return true; }
        public bool WriteBlock(uint key, APTR from)
        { Ram.AsSpan((int)from.Raw, 512).CopyTo(disk.AsSpan((int)key * 512)); return true; }
        public byte ReadUInt8(APTR p, int at = 0) => Ram[p.Raw + at];
        public ushort ReadUInt16(APTR p, int at = 0) => BinaryPrimitives.ReadUInt16BigEndian(Ram.AsSpan((int)p.Raw + at));
        public uint ReadUInt32(APTR p, int at = 0) { Words++; return BinaryPrimitives.ReadUInt32BigEndian(Ram.AsSpan((int)p.Raw + at)); }
        public void WriteUInt8(APTR p, int at, byte value) => Ram[p.Raw + at] = value;
        public void WriteUInt16(APTR p, int at, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(Ram.AsSpan((int)p.Raw + at), value);
        public void WriteUInt32(APTR p, int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(Ram.AsSpan((int)p.Raw + at), value);
        public bool IsMapped(APTR p, uint length) => p.Raw <= Ram.Length && length <= Ram.Length - p.Raw;
        public void Clear(APTR p, uint length) => Ram.AsSpan((int)p.Raw, (int)length).Clear();
        public void Copy(APTR from, APTR to, uint length) => Ram.AsSpan((int)from.Raw, (int)length).CopyTo(Ram.AsSpan((int)to.Raw));
        public string Name(APTR fib) => Encoding.ASCII.GetString(Ram, (int)fib.Raw + 9, Ram[fib.Raw + 8]);
    }
    private static readonly APTR Fib = APTR.FromPointer(1024), Other = APTR.FromPointer(1400), Meta = APTR.FromPointer(2000), Table = APTR.FromPointer(2600);

    [Fact]
    public void DirectoryCursorHasLinearBlockReadsAndPreservesOrdinalResults()
    {
        var disk = DosDiskImage.Create(true); var directory = disk.AddDrawer("Many");
        for (var i = 0; i < 96; i++) disk.AddDrawerFile(directory, $"Item{i:00}", []);
        var p = new Memory(disk.Bytes); Assert.True(AmigaDiskFileSystemCore.Examine(ref p, directory, Fib, Meta));
        var cursor = new AmigaDiskFileSystemCore.DirectoryCursor(); var actual = new List<string>(); p.Reads = 0;
        for (var i = 0; i < 96; i++)
        {
            Assert.Equal(DOS.Error.None, AmigaDiskFileSystemCore.ExamineNext(ref p, ref cursor, directory, Fib, Meta, Table));
            Assert.Equal((uint)i + 1, p.ReadUInt32(Fib)); actual.Add(p.Name(Fib));
        }
        Assert.Equal(DOS.Error.NoMoreEntries, AmigaDiskFileSystemCore.ExamineNext(ref p, ref cursor, directory, Fib, Meta, Table));
        Assert.Equal(193u, p.Reads); Assert.Equal(96, actual.Distinct().Count());
        Assert.Equal(Enumerable.Range(0, 96).Select(i => $"Item{i:00}"), actual.Order());
        // A fresh private record can rebuild every position from the same public key.
        Assert.True(AmigaDiskFileSystemCore.Examine(ref p, directory, Other, Meta));
        for (var i = 0; i < 96; i++)
        {
            Assert.Equal(DOS.Error.None, AmigaDiskFileSystemCore.ExamineNext(ref p, directory, Other, Meta, Table));
            Assert.Equal(actual[i], p.Name(Other));
        }
    }

    [Fact]
    public void InterleavedFibRewindInvalidationAndIoRetryDoNotSkipEntries()
    {
        var disk = DosDiskImage.Create(true); var directory = disk.AddDrawer("Many");
        for (var i = 0; i < 24; i++) disk.AddDrawerFile(directory, $"Entry{i:00}", []);
        var p = new Memory(disk.Bytes); var cursor = new AmigaDiskFileSystemCore.DirectoryCursor();
        Assert.True(AmigaDiskFileSystemCore.Examine(ref p, directory, Fib, Meta));
        Assert.True(AmigaDiskFileSystemCore.Examine(ref p, directory, Other, Meta));
        var names = new List<string>();
        for (var i = 0; i < 24; i++)
        {
            Assert.Equal(DOS.Error.None, AmigaDiskFileSystemCore.ExamineNext(ref p, ref cursor, directory, Fib, Meta, Table)); names.Add(p.Name(Fib));
            Assert.Equal(DOS.Error.None, AmigaDiskFileSystemCore.ExamineNext(ref p, ref cursor, directory, Other, Meta, Table)); Assert.Equal(names[i], p.Name(Other));
            if (i == 7) cursor.Ordinal = 0; // native mutation invalidates the private position
        }
        Assert.True(AmigaDiskFileSystemCore.Examine(ref p, directory, Fib, Meta));
        Assert.Equal(DOS.Error.None, AmigaDiskFileSystemCore.ExamineNext(ref p, ref cursor, directory, Fib, Meta, Table)); Assert.Equal(names[0], p.Name(Fib));
        var before = cursor; var bytes = p.Ram.AsSpan((int)Fib.Raw, 260).ToArray(); p.Bad = directory;
        Assert.Equal(DOS.Error.InvalidLock, AmigaDiskFileSystemCore.ExamineNext(ref p, ref cursor, directory, Fib, Meta, Table));
        Assert.Equal(before, cursor); Assert.Equal(bytes, p.Ram.AsSpan((int)Fib.Raw, 260).ToArray());
        p.Bad = uint.MaxValue;
        var slot = cursor.Slot; var bad = cursor.Entry;
        while (bad == 0 && slot < 72) bad = p.ReadUInt32(Table, 24 + (int)slot++ * 4);
        Assert.NotEqual(0u, bad); p.Bad = bad;
        Assert.Equal(DOS.Error.DiskNotValidated, AmigaDiskFileSystemCore.ExamineNext(ref p, ref cursor, directory, Fib, Meta, Table));
        Assert.Equal(before, cursor); Assert.Equal(bytes, p.Ram.AsSpan((int)Fib.Raw, 260).ToArray());
        p.Bad = uint.MaxValue;
        Assert.Equal(DOS.Error.None, AmigaDiskFileSystemCore.ExamineNext(ref p, ref cursor, directory, Fib, Meta, Table)); Assert.Equal(names[1], p.Name(Fib));
    }

    [Fact]
    public void BitmapWordsMatchBitOracleIncludingPaddingAndEveryFirstSetPosition()
    {
        var p = new Memory(new byte[901120]); var bitmap = APTR.FromPointer(4000);
        var random = new Random(6820);
        for (var i = 0; i < 128; i++) p.WriteUInt32(bitmap, 4 + i * 4, (uint)random.NextInt64(0, 0x100000000L));
        foreach (var bits in new uint[] { 0, 1, 31, 32, 33, 1758, 4063, 4064 })
        {
            uint expected = 0;
            for (uint i = 0; i < bits; i++) if ((p.ReadUInt32(bitmap, 4 + (int)(i / 32) * 4) & (1u << (int)(i % 32))) != 0) expected++;
            p.Words = 0; Assert.Equal(expected, AmigaBitmapWordCore.CountFree(ref p, bitmap, bits)); Assert.Equal((bits + 31) / 32, p.Words);
        }
        for (var bit = 0; bit < 32; bit++)
        {
            Assert.Equal((uint)bit, AmigaBitmapWordCore.FirstSet(1u << bit));
            Assert.Equal((uint)bit, AmigaBitmapWordCore.FirstSet(uint.MaxValue << bit));
        }
        for (var i = 0; i < 2048; i++)
        {
            var value = (uint)random.NextInt64(0, 0x100000000L);
            Assert.Equal((uint)System.Numerics.BitOperations.PopCount(value), AmigaBitmapWordCore.Population(value));
        }
    }
}
