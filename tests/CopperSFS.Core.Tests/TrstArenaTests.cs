using System.Buffers.Binary;
using Amiga;

namespace CopperSFS.Tests;

public sealed class TrstArenaTests
{
    private struct Memory(int length) : IAmigaGuestMemory
    {
        public readonly byte[] Bytes = Enumerable.Repeat((byte)0xA5, length).ToArray();
        public int Reads;
        public byte ReadUInt8(APTR a, int offset = 0) => Bytes[(int)a.Raw + offset];
        public ushort ReadUInt16(APTR a, int offset = 0) => BinaryPrimitives.ReadUInt16BigEndian(Bytes.AsSpan((int)a.Raw + offset));
        public uint ReadUInt32(APTR a, int offset = 0)
        { Reads++; return BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan((int)a.Raw + offset)); }
        public void WriteUInt8(APTR a, int offset, byte value) => Bytes[(int)a.Raw + offset] = value;
        public void WriteUInt16(APTR a, int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(Bytes.AsSpan((int)a.Raw + offset), value);
        public void WriteUInt32(APTR a, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(Bytes.AsSpan((int)a.Raw + offset), value);
        public void Clear(APTR a, uint length) => Bytes.AsSpan((int)a.Raw, (int)length).Clear();
        public void Copy(APTR a, APTR b, uint length) => Bytes.AsSpan((int)a.Raw, (int)length).CopyTo(Bytes.AsSpan((int)b.Raw));
        public bool IsMapped(APTR a, uint length) => a.Raw <= Bytes.Length && length <= Bytes.Length - a.Raw;
    }

    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(4096)]
    [InlineData(32768)]
    public void FullArenaCoalescesWritesAndResetsWithoutCrossingItsBounds(int blockBytes)
    {
        const uint arenaBytes = 192 * 1024;
        var arena = APTR.FromPointer(64); var source = APTR.FromPointer(arena.Raw + arenaBytes + 64);
        var p = new Memory((int)source.Raw + blockBytes);
        Assert.True(AmigaSmartFileSystemRedoArena.Initialize(ref p, arena, arenaBytes, (uint)blockBytes));
        var capacity = p.ReadUInt32(arena, 4);
        if (blockBytes == 512) Assert.Equal(377u, capacity);
        for (var i = 0u; i < capacity; i++)
        {
            p.Bytes.AsSpan((int)source.Raw, blockBytes).Fill((byte)i);
            Assert.True(AmigaSmartFileSystemRedoArena.Store(ref p, arena, i, source));
        }
        Assert.False(AmigaSmartFileSystemRedoArena.Store(ref p, arena, uint.MaxValue, source));
        Assert.Equal(1u, p.ReadUInt32(arena, 12));
        // Existing keys must remain writable even when there are no free slots.
        p.Bytes.AsSpan((int)source.Raw, blockBytes).Fill(0xF3);
        Assert.True(AmigaSmartFileSystemRedoArena.Store(ref p, arena, 0, source));
        Assert.Equal(capacity, p.ReadUInt32(arena));
        for (var i = 0u; i < capacity; i++)
        {
            var image = AmigaSmartFileSystemRedoArena.Find(ref p, arena, i);
            Assert.True(image.IsNotNull);
            Assert.All(p.Bytes.AsSpan((int)image.Raw, blockBytes).ToArray(), b => Assert.Equal(i == 0 ? (byte)0xF3 : (byte)i, b));
        }
        Assert.All(p.Bytes.AsSpan(0, 64).ToArray(), b => Assert.Equal(0xA5, b));
        Assert.All(p.Bytes.AsSpan((int)(arena.Raw + arenaBytes), 64).ToArray(), b => Assert.Equal(0xA5, b));
        Assert.True(AmigaSmartFileSystemRedoArena.Initialize(ref p, arena, arenaBytes, (uint)blockBytes));
        Assert.Equal(0u, p.ReadUInt32(arena, 12));
        for (var i = 0u; i < capacity; i++) Assert.True(AmigaSmartFileSystemRedoArena.Find(ref p, arena, i).IsNull);
        Assert.True(AmigaSmartFileSystemRedoArena.Store(ref p, arena, uint.MaxValue, source));
        Assert.True(AmigaSmartFileSystemRedoArena.Find(ref p, arena, uint.MaxValue).IsNotNull);
    }

    [Fact]
    public void CollidingKeysWrapAroundAndKeepDistinctFinalImages()
    {
        var p = new Memory(32768); var arena = APTR.FromPointer(64); var source = APTR.FromPointer(30000);
        Assert.True(AmigaSmartFileSystemRedoArena.Initialize(ref p, arena, 16384, 512));
        var mask = p.ReadUInt32(arena, 16);
        // Intentionally drive all entries into the last bucket: exercise both
        // collision resolution and wraparound instead of relying on random luck.
        var keys = Enumerable.Range(0, 100000).Select(i => (uint)i)
            .Where(b => ((b ^ (b >> 9) ^ (b >> 18)) & mask) == mask).Take(25).ToArray();
        for (var i = 0; i < keys.Length; i++)
        {
            p.WriteUInt32(source, 0, (uint)i);
            Assert.True(AmigaSmartFileSystemRedoArena.Store(ref p, arena, keys[i], source));
        }
        for (var i = keys.Length - 1; i >= 0; i--)
        {
            p.WriteUInt32(source, 0, keys[i] ^ 0xAA55AA55);
            Assert.True(AmigaSmartFileSystemRedoArena.Store(ref p, arena, keys[i], source));
        }
        Assert.Equal((uint)keys.Length, p.ReadUInt32(arena));
        foreach (var key in keys)
            Assert.Equal(key ^ 0xAA55AA55, p.ReadUInt32(AmigaSmartFileSystemRedoArena.Find(ref p, arena, key)));
        Assert.True(AmigaSmartFileSystemRedoArena.Find(ref p, arena, uint.MaxValue).IsNull);
    }

    [Fact]
    public void ConsecutiveBlockLookupWorkDoesNotGrowWithStagedCount()
    {
        var p = new Memory(210000); var arena = APTR.FromPointer(64); var source = APTR.FromPointer(200000);
        Assert.True(AmigaSmartFileSystemRedoArena.Initialize(ref p, arena, 192 * 1024, 512));
        for (var i = 0u; i < 370; i++) Assert.True(AmigaSmartFileSystemRedoArena.Store(ref p, arena, i, source));
        p.Reads = 0;
        for (var i = 0u; i < 370; i++) Assert.True(AmigaSmartFileSystemRedoArena.Find(ref p, arena, i).IsNotNull);
        // A linear scan takes more than 68,000 key reads here. This is an
        // algorithmic regression budget, independent of host timing noise.
        Assert.InRange(p.Reads, 370, 370 * 8);
    }

    [Fact]
    public void TinyArenaAlwaysRetainsAnEmptyBucketForAbsentKeys()
    {
        var p = new Memory(4096); var arena = APTR.FromPointer(64); var source = APTR.FromPointer(3000);
        // Exercise both sides of the one/two-slot sizing transition.
        foreach (var size in new uint[] { 548, 1063, 1064, 1072, 1588 })
        {
            Assert.True(AmigaSmartFileSystemRedoArena.Initialize(ref p, arena, size, 512));
            var capacity = p.ReadUInt32(arena, 4);
            Assert.True(capacity < p.ReadUInt32(arena, 16) + 1);
            for (var i = 0u; i < capacity; i++) Assert.True(AmigaSmartFileSystemRedoArena.Store(ref p, arena, i, source));
            Assert.True(AmigaSmartFileSystemRedoArena.Find(ref p, arena, uint.MaxValue).IsNull);
            Assert.False(AmigaSmartFileSystemRedoArena.Store(ref p, arena, uint.MaxValue, source));
        }
    }

    [Fact]
    public void ExistingSixteenByteOverlayKeepsItsLayoutAndCapacity()
    {
        var p = new Memory(4096); var arena = APTR.FromPointer(64); var source = APTR.FromPointer(3000);
        p.Clear(arena, 16); p.WriteUInt32(arena, 4, 2); p.WriteUInt32(arena, 8, 512);
        p.WriteUInt32(source, 0, 0x12345678);
        Assert.True(AmigaSmartFileSystemRedoArena.Store(ref p, arena, 0, source));
        Assert.True(AmigaSmartFileSystemRedoArena.Store(ref p, arena, uint.MaxValue, source));
        Assert.False(AmigaSmartFileSystemRedoArena.Store(ref p, arena, 99, source));
        Assert.Equal(arena.Raw + 20, AmigaSmartFileSystemRedoArena.Find(ref p, arena, 0).Raw);
        Assert.Equal(arena.Raw + 536, AmigaSmartFileSystemRedoArena.Find(ref p, arena, uint.MaxValue).Raw);
        p.WriteUInt32(source, 0, 0x87654321);
        Assert.True(AmigaSmartFileSystemRedoArena.Store(ref p, arena, 0, source));
        Assert.Equal(0x87654321u, p.ReadUInt32(AmigaSmartFileSystemRedoArena.Find(ref p, arena, 0)));
        Assert.Equal(0x12345678u, p.ReadUInt32(AmigaSmartFileSystemRedoArena.Find(ref p, arena, uint.MaxValue)));
        Assert.Equal(2u, p.ReadUInt32(arena));
        Assert.Equal(1u, p.ReadUInt32(arena, 12));
    }

    [Fact]
    public void InvalidOrTooSmallArenaDoesNotTouchMemory()
    {
        var p = new Memory(1024); var before = p.Bytes.ToArray(); var arena = APTR.FromPointer(64);
        Assert.False(AmigaSmartFileSystemRedoArena.Initialize(ref p, arena, 547, 512));
        Assert.False(AmigaSmartFileSystemRedoArena.Initialize(ref p, arena, 1000, 512));
        Assert.Equal(before, p.Bytes);
        Assert.True(AmigaSmartFileSystemRedoArena.Initialize(ref p, arena, 548, 512));
        Assert.Equal(1u, p.ReadUInt32(arena, 4));
    }
}
