using Amiga;

namespace CopperSFS;

/// <summary>
/// Caller-owned accepted/trial indexes over a shared pool. Images are stable for
/// the duration of a packet: superseded slots are reclaimed only at its boundary.
/// Borrowed views must not escape that boundary. No heap or external pin table.
/// </summary>
public static class SfsSharedImagePool
{
    // 0x20000000 belongs to the separate SFS2 owned store.
    internal const uint Format = 0x10000000;
    private const uint FormatMask = Format | AmigaSmartFileSystemRedoArena.Deferred;
    private const uint GroupBytes = 256, ViewBytes = 32, DescriptorBytes = 16;
    // Pool fields follow the existing 220-byte group state.
    private const int Free = 220, Slots = 224, Capacity = 228;

    public static bool IsPool<T>(ref T p, APTR view) where T : struct, IAmigaGuestMemory
        => (p.ReadUInt32(view, 8) & Format) != 0;

    // Caller has mapped the complete group span. Prove the selected view and
    // pool geometry before following a retained continuation's physical indices.
    internal static bool ValidLayout<T>(ref T p, APTR group, APTR view, uint span, uint bytes)
        where T : struct, IAmigaGuestMemory
    {
        if (span < GroupBytes + 2 * ViewBytes || group.Raw > uint.MaxValue - span ||
            bytes < 512 || bytes > 32768 || p.ReadUInt32(group, 12) != 0 ||
            view.Raw < group.Raw + GroupBytes || view.Raw > group.Raw + span - ViewBytes) return false;
        var capacity = p.ReadUInt32(group, Capacity); var mask = p.ReadUInt32(view, 16);
        // This cheap lower bound keeps both table-size products in range.
        // Once their exact sizes are known, check the actual slot region below.
        if (capacity == 0 || capacity > (span - GroupBytes - 2 * ViewBytes) >> 9 ||
            mask == 0 || mask == uint.MaxValue || (mask & (mask + 1)) != 0 ||
            mask + 1 > (span - GroupBytes - 2 * ViewBytes) / 8 ||
            capacity > mask + 1 - ((mask + 1) >> 2)) return false;
        var extent = ViewBytes + (mask + 1) * 4 + capacity * 4;
        if (extent > (span - GroupBytes) / 2) return false;
        var first = group.Raw + GroupBytes; var second = first + extent;
        var slots = second + extent;
        if ((view.Raw != first && view.Raw != second) ||
            !((p.ReadUInt32(group, 16) == first && p.ReadUInt32(group, 20) == second) ||
              (p.ReadUInt32(group, 16) == second && p.ReadUInt32(group, 20) == first))) return false;
        var available = group.Raw + span - slots;
        if (bytes == 512)
        {
            // Use subtraction between the payload and descriptor checks so
            // the product cannot wrap, even for a nearly 4 GiB input span.
            if (capacity > available >> 9 || capacity > (available - (capacity << 9)) >> 4) return false;
        }
        else if (capacity > available / (bytes + DescriptorBytes)) return false;
        return p.ReadUInt32(group, Slots) == slots && p.ReadUInt32(view, 4) == capacity &&
            p.ReadUInt32(view) <= capacity && p.ReadUInt32(view, 24) == group.Raw &&
            p.ReadUInt32(view, 20) == view.Raw + ViewBytes + (mask + 1) * 4 &&
            (p.ReadUInt32(view, 8) & ~AmigaSmartFileSystemRedoArena.Deferred) == (Format | bytes);
    }

    internal static uint PhysicalIndex<T>(ref T p, APTR view, APTR image) where T : struct, IAmigaGuestMemory
        => (image.Raw - DescriptorBytes - p.ReadUInt32(APTR.FromPointer(p.ReadUInt32(view, 24)), Slots)) /
            ((p.ReadUInt32(view, 8) & ~FormatMask) + DescriptorBytes);

    // ValidLayout proved the full mapped pool. Ownership and revision bind the
    // immutable proof to this accepted image, without a hash walk or checksum scan.
    internal static APTR ProofImage<T>(ref T p, APTR view, uint index, uint revision, uint block, uint id)
        where T : struct, IAmigaGuestMemory
    {
        if (index >= p.ReadUInt32(view, 4)) return APTR.Null;
        var bytes = p.ReadUInt32(view, 8) & ~FormatMask;
        var offset = bytes == 512 ? (index << 9) + (index << 4) : index * (bytes + DescriptorBytes);
        var descriptor = APTR.FromPointer(p.ReadUInt32(APTR.FromPointer(p.ReadUInt32(view, 24)), Slots) + offset);
        return p.ReadUInt32(descriptor) == view.Raw && p.ReadUInt32(descriptor, 4) == revision &&
            p.ReadUInt32(descriptor, 8) == block && p.ReadUInt32(descriptor, 12) == id
            ? APTR.FromPointer(descriptor.Raw + DescriptorBytes) : APTR.Null;
    }

    // The caller has already chosen the old arena allocation extent. Never
    // round it upwards, even if the pool could use the original budget's padding.
    internal static bool Initialize<T>(ref T p, APTR group, uint span, uint bytes)
        where T : struct, IAmigaGuestMemory
    {
        if (bytes < 512 || bytes > 32768 || span < GroupBytes + 2 * ViewBytes ||
            !p.IsMapped(group, span) || group.Raw > uint.MaxValue - span) return false;
        var capacity = (span - GroupBytes - 2 * ViewBytes) / (bytes + DescriptorBytes + 8);
        uint buckets;
        while (true)
        {
            if (capacity == 0) return false;
            buckets = 2;
            while (buckets - (buckets >> 2) < capacity) buckets <<= 1;
            if (GroupBytes + 2 * ViewBytes + buckets * 8 + capacity * (bytes + DescriptorBytes + 8) <= span) break;
            capacity--;
        }
        var extent = ViewBytes + buckets * 4 + capacity * 4;
        var first = APTR.FromPointer(group.Raw + GroupBytes);
        var second = APTR.FromPointer(first.Raw + extent);
        var slots = second.Raw + extent;
        p.Clear(group, slots - group.Raw);
        p.WriteUInt32(group, 8, span);
        p.WriteUInt32(group, 16, first.Raw); p.WriteUInt32(group, 20, second.Raw);
        p.WriteUInt32(group, Slots, slots); p.WriteUInt32(group, Capacity, capacity);
        InitializeView(ref p, first, group, bytes, capacity, buckets);
        InitializeView(ref p, second, group, bytes, capacity, buckets);
        var slot = slots;
        for (var i = 0u; i < capacity; i++, slot += bytes + DescriptorBytes)
        {
            // owner/free-link, revision, block key, verification, then payload.
            p.WriteUInt32(APTR.FromPointer(slot), 0, 0);
            p.WriteUInt32(APTR.FromPointer(slot), 4, 0);
        }
        Finish(ref p, first);
        return true;
    }

    private static void InitializeView<T>(ref T p, APTR view, APTR group, uint bytes, uint count, uint buckets)
        where T : struct, IAmigaGuestMemory
    {
        p.WriteUInt32(view, 4, count); p.WriteUInt32(view, 8, Format | bytes);
        p.WriteUInt32(view, 16, buckets - 1);
        p.WriteUInt32(view, 20, view.Raw + ViewBytes + buckets * 4);
        p.WriteUInt32(view, 24, group.Raw);
    }

    internal static void Begin<T>(ref T p, APTR accepted, APTR trial, bool pending)
        where T : struct, IAmigaGuestMemory
    {
        var count = pending ? p.ReadUInt32(accepted) : 0;
        var indexBytes = (p.ReadUInt32(trial, 16) + 1) * 4;
        var index = APTR.FromPointer(trial.Raw + ViewBytes);
        p.WriteUInt32(trial, 0, count); p.WriteUInt32(trial, 12, 0);
        if (pending)
        {
            // Both views place the occupied order vector immediately after the index.
            p.Copy(APTR.FromPointer(accepted.Raw + ViewBytes), index, indexBytes + count * 4);
        }
        else
        {
            p.Clear(index, indexBytes);
            Finish(ref p, trial);
        }
    }

    // All views expire before this call. Preserve exactly the chosen mapping;
    // failed trials and superseded accepted versions become free together.
    internal static void Finish<T>(ref T p, APTR chosen) where T : struct, IAmigaGuestMemory
    {
        var group = APTR.FromPointer(p.ReadUInt32(chosen, 24));
        var slots = p.ReadUInt32(group, Slots); var count = p.ReadUInt32(group, Capacity);
        var stride = (p.ReadUInt32(chosen, 8) & ~FormatMask) + DescriptorBytes;
        var slot = slots;
        for (var i = 0u; i < count; i++, slot += stride) p.WriteUInt32(APTR.FromPointer(slot), 0, 0);
        var order = p.ReadUInt32(chosen, 20);
        var occupied = p.ReadUInt32(chosen);
        for (var i = 0u; i < occupied; i++, order += 4)
            p.WriteUInt32(APTR.FromPointer(p.ReadUInt32(APTR.FromPointer(order)) - 8), 0, chosen.Raw);
        var free = 0u;
        slot = slots;
        for (var i = 0u; i < count; i++, slot += stride)
            if (p.ReadUInt32(APTR.FromPointer(slot)) == 0)
            { p.WriteUInt32(APTR.FromPointer(slot), 0, free); free = slot; }
        p.WriteUInt32(group, Free, free);
    }

    internal static APTR SlotAt<T>(ref T p, APTR view, uint index) where T : struct, IAmigaGuestMemory
        => APTR.FromPointer(p.ReadUInt32(APTR.FromPointer(p.ReadUInt32(view, 20) + index * 4)));

    // Native SFS reads usually hit the first pool bucket, or find it empty.
    // Deferred materialization, collisions and uncertified images take the
    // normal checked lookup, including the legacy and recovery arena formats.
    public static APTR ReadOverlay<T>(ref T p, APTR view, uint block, uint id, uint bytes, out bool found)
        where T : struct, IAmigaFileSystemPlatform
    {
        if (p.ReadUInt32(view, 8) == (Format | bytes) && p.ReadUInt32(view, 12) <= 1)
        {
            var index = Hash(block) & p.ReadUInt32(view, 16);
            var slot = APTR.FromPointer(p.ReadUInt32(view, (int)(ViewBytes + index * 4)));
            if (slot.IsNull) { found = false; return APTR.Null; }
            if (id != 0 && p.ReadUInt32(slot) == block && p.ReadUInt32(slot, 4) == id)
            {
                found = true;
                return APTR.FromPointer(slot.Raw + AmigaSmartFileSystemRedoArena.SlotHeaderBytes);
            }
        }
        return AmigaSmartFileSystemRedoArena.MetadataView(ref p, view, block, id, false, out found);
    }

    private static uint Hash(uint block) => block ^ (block >> 16);

    private static APTR Probe<T>(ref T p, APTR view, uint block) where T : struct, IAmigaGuestMemory
    {
        var mask = p.ReadUInt32(view, 16);
        // Fold the two 16-bit halves. Both adjacent blocks and large-volume
        // block numbers contribute, with a single word swap on 68k.
        var index = Hash(block) & mask;
        for (var attempts = 0u; attempts <= mask; attempts++, index = (index + 1) & mask)
        {
            var bucket = APTR.FromPointer(view.Raw + ViewBytes + index * 4);
            var slot = p.ReadUInt32(bucket);
            if (slot == 0 || p.ReadUInt32(APTR.FromPointer(slot)) == block) return bucket;
        }
        return APTR.Null;
    }

    internal static APTR Find<T>(ref T p, APTR view, uint block) where T : struct, IAmigaGuestMemory
    {
        var bucket = Probe(ref p, view, block);
        var slot = bucket.IsNull ? 0 : p.ReadUInt32(bucket);
        return slot == 0 ? APTR.Null : APTR.FromPointer(slot + 8);
    }

    private static APTR WritableSlot<T>(ref T p, APTR view, APTR bucket, uint block, bool copy)
        where T : struct, IAmigaGuestMemory
    {
        if (bucket.IsNull) { p.WriteUInt32(view, 12, 1); return APTR.Null; }
        var old = p.ReadUInt32(bucket);
        if (old != 0 && p.ReadUInt32(APTR.FromPointer(old - 8)) == view.Raw) return APTR.FromPointer(old);
        var group = APTR.FromPointer(p.ReadUInt32(view, 24));
        var free = p.ReadUInt32(group, Free);
        var count = p.ReadUInt32(view);
        if (free == 0 || (old == 0 && count == p.ReadUInt32(view, 4)))
        { p.WriteUInt32(view, 12, 1); return APTR.Null; }
        var descriptor = APTR.FromPointer(free);
        var slot = APTR.FromPointer(free + 8);
        p.WriteUInt32(group, Free, p.ReadUInt32(descriptor));
        p.WriteUInt32(descriptor, 0, view.Raw);
        p.WriteUInt32(descriptor, 4, unchecked(p.ReadUInt32(descriptor, 4) + 1));
        p.WriteUInt32(slot, 0, block); p.WriteUInt32(slot, 4, 0);
        var order = APTR.FromPointer(p.ReadUInt32(view, 20));
        if (old == 0)
        { p.WriteUInt32(order, (int)(count * 4), slot.Raw); p.WriteUInt32(view, 0, count + 1); }
        else
        {
            for (var i = 0u; i < count; i++)
                if (p.ReadUInt32(order, (int)(i * 4)) == old)
                { p.WriteUInt32(order, (int)(i * 4), slot.Raw); break; }
            if (copy) p.Copy(APTR.FromPointer(old + 8), APTR.FromPointer(slot.Raw + 8), p.ReadUInt32(view, 8) & ~FormatMask);
        }
        p.WriteUInt32(bucket, 0, slot.Raw);
        return slot;
    }

    internal static bool Store<T>(ref T p, APTR view, uint block, APTR source, uint verified)
        where T : struct, IAmigaGuestMemory
    {
        var slot = WritableSlot(ref p, view, Probe(ref p, view, block), block, false);
        if (slot.IsNull) return false;
        var image = APTR.FromPointer(slot.Raw + 8);
        p.WriteUInt32(slot, 4, 0);
        if (image != source) p.Copy(source, image, p.ReadUInt32(view, 8) & ~FormatMask);
        p.WriteUInt32(slot, 4, verified);
        return true;
    }

    internal static APTR View<T>(ref T p, APTR view, uint block, uint id, bool editable, out bool found)
        where T : struct, IAmigaFileSystemPlatform
    {
        var bucket = Probe(ref p, view, block);
        var slot = bucket.IsNull ? APTR.Null : APTR.FromPointer(p.ReadUInt32(bucket));
        found = slot.IsNotNull;
        if (!found) return APTR.Null;
        var image = APTR.FromPointer(slot.Raw + 8);
        if (id == 0 || p.ReadUInt32(slot, 4) != id)
        {
            if (!AmigaFileSystemBlockIo.ValidateMetadata(ref p, block, id, image, p.ReadUInt32(view, 8) & ~FormatMask)) return APTR.Null;
            p.WriteUInt32(slot, 4, id);
        }
        if (editable)
        {
            slot = WritableSlot(ref p, view, bucket, block, true);
            if (slot.IsNull) return APTR.Null;
            p.WriteUInt32(slot, 4, 0); image = APTR.FromPointer(slot.Raw + 8);
        }
        return image;
    }

    // Single-owner packet. A read of the lower layer cannot change this bucket.
    // Delay allocating the private slot until the source image is certified.
    internal static APTR Edit<T>(ref T p, APTR view, uint block, uint id, APTR scratch, out bool full)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        full = false;
        var bucket = Probe(ref p, view, block);
        var old = bucket.IsNull ? 0 : p.ReadUInt32(bucket);
        var bytes = p.ReadUInt32(view, 8) & ~FormatMask;
        APTR source;
        if (old == 0)
        {
            source = p.ReadMetadataView(block, id, scratch);
            if (source.IsNull) return APTR.Null;
        }
        else
        {
            var slot = APTR.FromPointer(old);
            source = APTR.FromPointer(old + 8);
            if ((id == 0 || p.ReadUInt32(slot, 4) != id) &&
                !AmigaFileSystemBlockIo.ValidateMetadata(ref p, block, id, source, bytes)) return APTR.Null;
            if (p.ReadUInt32(APTR.FromPointer(old - 8)) == view.Raw)
            {
                p.WriteUInt32(slot, 4, 0);
                return source;
            }
        }
        var owned = WritableSlot(ref p, view, bucket, block, false);
        if (owned.IsNull) { full = true; return APTR.Null; }
        var destination = APTR.FromPointer(owned.Raw + 8);
        p.Copy(source, destination, bytes);
        return destination;
    }
}
