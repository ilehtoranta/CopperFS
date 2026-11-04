using Amiga;

namespace CopperSFS;

/// <summary>
/// Bounded TRST overlay in caller memory. A 24-byte header and open-addressed
/// index precede the block/image slots. The index is rebuilt for each operation;
/// it is never persisted and requires no allocation outside the supplied arena.
/// </summary>
public static class AmigaSmartFileSystemRedoArena
{
    public const uint HeaderBytes = 24;
    private const uint Indexed = 0x80000000;

    public static bool Initialize<T>(ref T p, APTR arena, uint arenaBytes, uint blockBytes)
        where T : struct, IAmigaGuestMemory
    {
        if (blockBytes < 512 || blockBytes > 32768 || arenaBytes < blockBytes + HeaderBytes + 12 ||
            arena.IsNull || !p.IsMapped(arena, arenaBytes)) return false;
        var stride = blockBytes + 4;
        var maximum = (arenaBytes - HeaderBytes) / stride;
        // At most 75% occupied, even before accounting for index storage. Thus
        // every probe terminates at an empty bucket, including a full arena.
        var buckets = 2u;
        while (buckets <= maximum || buckets - (buckets >> 2) < maximum) buckets <<= 1;
        var indexBytes = buckets * 4;
        var capacity = (arenaBytes - HeaderBytes - indexBytes) / stride;
        p.Clear(arena, HeaderBytes + indexBytes);
        p.WriteUInt32(arena, 4, capacity);
        // The high bit distinguishes this private layout from SFS2's existing
        // 16-byte overlay. Valid block sizes never use it. Keep both readers.
        p.WriteUInt32(arena, 8, blockBytes | Indexed);
        p.WriteUInt32(arena, 16, buckets - 1);
        p.WriteUInt32(arena, 20, arena.Raw + HeaderBytes + indexBytes);
        return true;
    }

    private static APTR Probe<T>(ref T p, APTR arena, uint block) where T : struct, IAmigaGuestMemory
    {
        var mask = p.ReadUInt32(arena, 16);
        // Fold high block bits without a multiply/divide on MC68000. Sequential
        // data blocks spread across buckets; wide block numbers retain entropy.
        var bucket = (block ^ (block >> 9) ^ (block >> 18)) & mask;
        while (true)
        {
            var address = APTR.FromPointer(arena.Raw + HeaderBytes + bucket * 4);
            var slot = p.ReadUInt32(address);
            if (slot == 0 || p.ReadUInt32(APTR.FromPointer(slot)) == block) return address;
            bucket = (bucket + 1) & mask;
        }
    }

    public static APTR Find<T>(ref T p, APTR arena, uint block) where T : struct, IAmigaGuestMemory
    {
        var format = p.ReadUInt32(arena, 8);
        if ((format & Indexed) == 0) return FindLinear(ref p, arena, block, format);
        var slot = p.ReadUInt32(Probe(ref p, arena, block));
        return slot == 0 ? APTR.Null : APTR.FromPointer(slot + 4);
    }

    public static bool Store<T>(ref T p, APTR arena, uint block, APTR source) where T : struct, IAmigaGuestMemory
    {
        var format = p.ReadUInt32(arena, 8);
        if ((format & Indexed) == 0) return StoreLinear(ref p, arena, block, source, format);
        var bytes = format & ~Indexed;
        var bucket = Probe(ref p, arena, block);
        var slot = p.ReadUInt32(bucket);
        if (slot == 0)
        {
            var count = p.ReadUInt32(arena);
            if (count == p.ReadUInt32(arena, 4)) { p.WriteUInt32(arena, 12, 1); return false; }
            slot = p.ReadUInt32(arena, 20) + count * (bytes + 4);
            p.WriteUInt32(APTR.FromPointer(slot), 0, block);
            p.WriteUInt32(bucket, 0, slot); p.WriteUInt32(arena, 0, count + 1);
        }
        p.Copy(source, APTR.FromPointer(slot + 4), bytes); return true;
    }

    // Compatibility for the separately qualified SFS2 overlay. Its initializer
    // and slot layout remain unchanged; classic TRST always uses the index.
    private static APTR FindLinear<T>(ref T p, APTR arena, uint block, uint bytes)
        where T : struct, IAmigaGuestMemory
    {
        var count = p.ReadUInt32(arena);
        var slot = APTR.FromPointer(arena.Raw + 16);
        for (var i = 0u; i < count; i++, slot = APTR.FromPointer(slot.Raw + bytes + 4))
            if (p.ReadUInt32(slot) == block) return APTR.FromPointer(slot.Raw + 4);
        return APTR.Null;
    }

    private static bool StoreLinear<T>(ref T p, APTR arena, uint block, APTR source, uint bytes)
        where T : struct, IAmigaGuestMemory
    {
        var image = FindLinear(ref p, arena, block, bytes);
        if (image.IsNull)
        {
            var count = p.ReadUInt32(arena);
            if (count == p.ReadUInt32(arena, 4)) { p.WriteUInt32(arena, 12, 1); return false; }
            var slot = APTR.FromPointer(arena.Raw + 16 + count * (bytes + 4));
            p.WriteUInt32(slot, 0, block); p.WriteUInt32(arena, 0, count + 1);
            image = APTR.FromPointer(slot.Raw + 4);
        }
        p.Copy(source, image, bytes); return true;
    }
}
