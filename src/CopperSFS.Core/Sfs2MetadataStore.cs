using Amiga;

namespace CopperSFS;

/// <summary>Indexed, caller-owned SFS2 transaction images. Keys and hash entries
/// are relative byte offsets, so a complete accepted arena can be cloned without
/// rebasing pointers. Verification belongs to an image revision, not a block key.</summary>
public static class Sfs2MetadataStore
{
    public const uint Format = 0x20000000, HeaderBytes = 32, SlotBytes = 16;
    public const uint CleanCache = 1, Trial = 2;
    private const uint Dirty = 0x80000000;
    public struct Handle
    {
        public APTR Arena, Slot, Image, Mount;
        public uint Epoch, Revision, Block, Type, MountEpoch;
        public bool Editable;
    }

    public static bool IsStore<T>(ref T p, APTR arena) where T : struct, IAmigaGuestMemory =>
        arena.IsNotNull && (p.ReadUInt32(arena, 8) & Format) != 0;

    public static bool Initialize<T>(ref T p, APTR arena, uint budget, uint bytes, uint epoch)
        where T : struct, IAmigaGuestMemory
    {
        if (bytes < 512 || bytes > 32768 || (bytes & (bytes - 1)) != 0 || arena.IsNull || budget < bytes + HeaderBytes + SlotBytes + 16 ||
            !p.IsMapped(arena, budget)) return false;
        var maximum = (budget - HeaderBytes) / (bytes + SlotBytes);
        var buckets = 2u;
        while (buckets - (buckets >> 2) < maximum) buckets <<= 1;
        var prefix = HeaderBytes + buckets * 4;
        if (prefix >= budget) return false;
        var capacity = (budget - prefix) / (bytes + SlotBytes);
        if (capacity == 0) return false;
        p.Clear(arena, prefix); p.WriteUInt32(arena, 4, capacity);
        p.WriteUInt32(arena, 8, Format | bytes); p.WriteUInt32(arena, 16, buckets - 1);
        p.WriteUInt32(arena, 20, prefix); p.WriteUInt32(arena, 28, epoch == 0 ? 1u : epoch);
        return true;
    }

    public static APTR Slot<T>(ref T p, APTR arena, uint index) where T : struct, IAmigaGuestMemory =>
        APTR.FromPointer(arena.Raw + p.ReadUInt32(arena, 20) + index * ((p.ReadUInt32(arena, 8) & ~Format) + SlotBytes));

    private static APTR Bucket<T>(ref T p, APTR arena, uint key) where T : struct, IAmigaGuestMemory
    {
        var mask = p.ReadUInt32(arena, 16); var index = (key ^ (key >> 16)) & mask;
        for (var probes = 0u; probes <= mask; probes++)
        {
            var bucket = APTR.FromPointer(arena.Raw + HeaderBytes + index * 4);
            // These offsets are written only by this owned store, never read
            // from disk. Lookup needs no slot multiplication or second header
            // walk; cloning preserves the offsets without rebasing pointers.
            var offset = p.ReadUInt32(bucket);
            if (offset == 0 || (p.ReadUInt32(APTR.FromPointer(arena.Raw + offset)) & 0x7FFFFFFF) == key) return bucket;
            index = (index + 1) & mask;
        }
        return APTR.Null;
    }

    public static APTR Find<T>(ref T p, APTR arena, uint key) where T : struct, IAmigaGuestMemory
    {
        var bucket = Bucket(ref p, arena, key);
        if (bucket.IsNull) return APTR.Null;
        var offset = p.ReadUInt32(bucket);
        return offset == 0 ? APTR.Null : APTR.FromPointer(arena.Raw + offset + SlotBytes);
    }

    internal static bool RawView<T>(ref T p, APTR arena, uint key, out APTR image) where T : struct, IAmigaGuestMemory
    {
        image = Find(ref p, arena, key);
        if (image.IsNull) return true;
        var slot = APTR.FromPointer(image.Raw - SlotBytes);
        if ((p.ReadUInt32(slot, 4) & Dirty) == 0 && p.ReadUInt32(slot, 12) != uint.MaxValue) return true;
        image = APTR.Null; return false;
    }

    public static bool Store<T>(ref T p, APTR arena, uint key, APTR source, uint verified = 0)
        where T : struct, IAmigaGuestMemory
    {
        if (key >= 0x80000000 || source.IsNull || !p.IsMapped(source, p.ReadUInt32(arena, 8) & ~Format)) return false;
        var bucket = Bucket(ref p, arena, key);
        if (bucket.IsNull) return false;
        var offset = p.ReadUInt32(bucket); APTR slot;
        if (offset == 0)
        {
            var count = p.ReadUInt32(arena);
            if (count >= p.ReadUInt32(arena, 4)) { p.WriteUInt32(arena, 12, 1); return false; }
            slot = Slot(ref p, arena, count); p.Clear(slot, SlotBytes);
            p.WriteUInt32(slot, 0, key); p.WriteUInt32(bucket, 0, slot.Raw - arena.Raw); p.WriteUInt32(arena, 0, count + 1);
        }
        else slot = APTR.FromPointer(arena.Raw + offset);
        if (p.ReadUInt32(slot, 12) != 0) return false;
        var image = APTR.FromPointer(slot.Raw + SlotBytes);
        var revision = unchecked(p.ReadUInt32(slot, 8) + 1);
        if (revision == 0) return false; // Never revive a stale handle on wrap.
        p.WriteUInt32(slot, 4, 0);
        p.WriteUInt32(slot, 8, revision);
        if (image != source) p.Copy(source, image, p.ReadUInt32(arena, 8) & ~Format);
        p.WriteUInt32(slot, 4, verified);
        return true;
    }

    public static APTR View<T>(ref T p, APTR arena, uint block, uint id, out bool found)
        where T : struct, IAmigaFileSystemPlatform
    {
        var image = Find(ref p, arena, block); found = image.IsNotNull;
        if (!found) return APTR.Null;
        return VerifyView(ref p, arena, image, block, id);
    }

    private static APTR VerifyView<T>(ref T p, APTR arena, APTR image, uint block, uint id)
        where T : struct, IAmigaFileSystemPlatform
    {
        var slot = APTR.FromPointer(image.Raw - SlotBytes);
        if (p.ReadUInt32(slot, 12) == uint.MaxValue) return APTR.Null;
        if (id != 0 && p.ReadUInt32(slot, 4) != id)
        {
            if (p.ReadUInt32(arena, 24) == Trial && p.ReadUInt32(slot, 4) == (id | Dirty))
                return p.ReadUInt32(image) == id && p.ReadUInt32(image, 8) == block ? image : APTR.Null;
            if (p.ReadUInt32(image) != id || p.ReadUInt32(image, 8) != block ||
                !AmigaFileSystemBlockIo.ValidSfs2Sum(ref p, image, p.BlockBytes)) return APTR.Null;
            p.WriteUInt32(slot, 4, id);
        }
        return image;
    }

    public static bool Acquire<T>(ref T p, APTR arena, uint block, uint id, out Handle handle)
        where T : struct, IAmigaFileSystemPlatform
    {
        var image = View(ref p, arena, block, id, out _);
        return Pin(ref p, arena, image, block, id, out handle);
    }

    private static bool Pin<T>(ref T p, APTR arena, APTR image, uint block, uint id, out Handle handle)
        where T : struct, IAmigaGuestMemory
    {
        handle = default;
        if (image.IsNull) return false;
        var slot = APTR.FromPointer(image.Raw - SlotBytes); var pins = p.ReadUInt32(slot, 12);
        if (pins == uint.MaxValue) return false;
        p.WriteUInt32(slot, 12, pins + 1);
        handle = new Handle { Arena = arena, Slot = slot, Image = image, Block = block, Type = id,
            Epoch = p.ReadUInt32(arena, 28), Revision = p.ReadUInt32(slot, 8) };
        return true;
    }

    public static bool Current<T>(ref T p, in Handle h) where T : struct, IAmigaGuestMemory =>
        h.Arena.IsNotNull && h.Slot.IsNotNull && p.ReadUInt32(h.Arena, 28) == h.Epoch &&
        (h.Mount.IsNull || (p.ReadUInt32(h.Mount, 4) == 1 && p.ReadUInt32(h.Mount, 8) == h.MountEpoch)) &&
        p.ReadUInt32(h.Slot, 8) == h.Revision && p.ReadUInt32(h.Slot) == h.Block && p.ReadUInt32(h.Slot, 12) != 0;

    public static bool Release<T>(ref T p, ref Handle h) where T : struct, IAmigaGuestMemory
    {
        if (h.Arena.IsNull && h.Image.IsNotNull) { h = default; return true; }
        if (!Current(ref p, h) || h.Editable) return false;
        p.WriteUInt32(h.Slot, 12, p.ReadUInt32(h.Slot, 12) - 1); h = default; return true;
    }

    public static bool Unpinned<T>(ref T p, APTR arena) where T : struct, IAmigaGuestMemory
    {
        var count = p.ReadUInt32(arena); var slot = arena.Raw + p.ReadUInt32(arena, 20);
        var stride = (p.ReadUInt32(arena, 8) & ~Format) + SlotBytes;
        for (var i = 0u; i < count; i++, slot += stride)
            if (p.ReadUInt32(APTR.FromPointer(slot), 12) != 0) return false;
        return true;
    }

    public static bool Clone<T>(ref T p, APTR source, APTR destination, uint budget, uint epoch)
        where T : struct, IAmigaGuestMemory => Clone(ref p, source, destination, budget, epoch, false);

    // The group owner certifies that destination is the accepted predecessor
    // of source, with no intervening rejected trial. Equal revisions then mean
    // equal images, not merely equal block keys from unrelated transactions.
    internal static bool Clone<T>(ref T p, APTR source, APTR destination, uint budget, uint epoch, bool predecessor)
        where T : struct, IAmigaGuestMemory
    {
        if (budget < HeaderBytes || source.IsNull || destination.IsNull || !p.IsMapped(source, budget) || !p.IsMapped(destination, budget) ||
            (source.Raw <= destination.Raw ? destination.Raw - source.Raw < budget : source.Raw - destination.Raw < budget)) return false;
        var bytes = p.ReadUInt32(source, 8) & ~Format; var mask = p.ReadUInt32(source, 16);
        if (!IsStore(ref p, source) || bytes < 512 || bytes > 32768 || mask == 0 || mask == uint.MaxValue ||
            (mask & (mask + 1)) != 0 || mask + 1 > (budget - HeaderBytes) / 4) return false;
        var prefix = HeaderBytes + (mask + 1) * 4;
        var count = p.ReadUInt32(source); var capacity = p.ReadUInt32(source, 4);
        if (prefix != p.ReadUInt32(source, 20) || capacity == 0 || count > capacity ||
            capacity > (budget - prefix) / (bytes + SlotBytes) || capacity >= mask + 1 || !Unpinned(ref p, source)) return false;
        var oldCount = p.ReadUInt32(destination);
        if (predecessor && p.ReadUInt32(source, 24) == 0 && p.ReadUInt32(destination, 24) == 0 &&
            p.ReadUInt32(destination, 4) == capacity && p.ReadUInt32(destination, 8) == (Format | bytes) &&
            p.ReadUInt32(destination, 16) == mask && p.ReadUInt32(destination, 20) == prefix && oldCount <= count &&
            Unpinned(ref p, destination))
        {
            // Keys only append during a group. An unchanged count also means
            // an unchanged hash index. Keep every image physically private.
            p.Copy(source, destination, oldCount == count ? HeaderBytes : prefix);
            var stride = bytes + SlotBytes;
            for (var i = 0u; i < count; i++)
            {
                var offset = prefix + i * stride;
                var from = APTR.FromPointer(source.Raw + offset); var to = APTR.FromPointer(destination.Raw + offset);
                if (i >= oldCount || p.ReadUInt32(from) != p.ReadUInt32(to) ||
                    p.ReadUInt32(from, 8) != p.ReadUInt32(to, 8)) p.Copy(from, to, stride);
                else p.WriteUInt32(to, 4, p.ReadUInt32(from, 4)); // Retain verification of this unchanged revision.
            }
        }
        else p.Copy(source, destination, prefix + count * (bytes + SlotBytes));
        p.WriteUInt32(destination, 28, epoch == 0 ? 1u : epoch); p.WriteUInt32(destination, 24, Trial); return true;
    }

    /// <summary>Stage metadata without repeatedly sealing it. This operation is
    /// private to the disposable trial arena; accepted images are immutable.</summary>
    internal static bool Stage<T>(ref T p, APTR arena, uint block, APTR source) where T : struct, IAmigaGuestMemory =>
        p.ReadUInt32(arena, 24) == Trial && p.ReadUInt32(source, 8) == block &&
        Store(ref p, arena, block, source, p.ReadUInt32(source) | Dirty);

    public static bool Publish<T>(ref T p, APTR arena) where T : struct, IAmigaFileSystemPlatform
    {
        if (!Unpinned(ref p, arena) || p.ReadUInt32(arena, 24) != Trial) return false;
        var count = p.ReadUInt32(arena); var address = arena.Raw + p.ReadUInt32(arena, 20);
        var stride = (p.ReadUInt32(arena, 8) & ~Format) + SlotBytes;
        for (var i = 0u; i < count; i++, address += stride)
        {
            var slot = APTR.FromPointer(address); var id = p.ReadUInt32(slot, 4);
            if ((id & Dirty) == 0) continue;
            var image = APTR.FromPointer(slot.Raw + SlotBytes);
            if (p.ReadUInt32(image) != (id & ~Dirty) || p.ReadUInt32(image, 8) != p.ReadUInt32(slot)) return false;
            p.WriteUInt32(image, 4, 0);
            p.WriteUInt32(image, 4, unchecked(0u - 2u - AmigaSmartFileSystemCore.BlockSum(ref p, image, p.BlockBytes)));
            p.WriteUInt32(slot, 4, id & ~Dirty);
        }
        p.WriteUInt32(arena, 24, 0); return true;
    }

    // Delete a cache key with backward-shift rehashing. Transaction keys never
    // disappear before a complete arena is retired.
    private static void Remove<T>(ref T p, APTR arena, uint key) where T : struct, IAmigaGuestMemory
    {
        var bucket = Bucket(ref p, arena, key);
        if (bucket.IsNull || p.ReadUInt32(bucket) == 0) return;
        var mask = p.ReadUInt32(arena, 16);
        var index = (bucket.Raw - arena.Raw - HeaderBytes) / 4;
        p.WriteUInt32(bucket, 0, 0);
        for (index = (index + 1) & mask; ; index = (index + 1) & mask)
        {
            bucket = APTR.FromPointer(arena.Raw + HeaderBytes + index * 4);
            var offset = p.ReadUInt32(bucket); if (offset == 0) break;
            p.WriteUInt32(bucket, 0, 0);
            var replacement = Bucket(ref p, arena, p.ReadUInt32(APTR.FromPointer(arena.Raw + offset)));
            p.WriteUInt32(replacement, 0, offset);
        }
    }

    public static bool Invalidate<T>(ref T p, APTR arena, uint first, uint count) where T : struct, IAmigaGuestMemory
    {
        if (arena.IsNull) return true;
        if (count <= 8)
        {
            for (var offset = 0u; offset < count; offset++)
            {
                var image = Find(ref p, arena, first + offset); if (image.IsNull) continue;
                var slot = APTR.FromPointer(image.Raw - SlotBytes);
                if (p.ReadUInt32(slot, 12) != 0) return false;
                Remove(ref p, arena, first + offset); p.WriteUInt32(slot, 0, uint.MaxValue); p.WriteUInt32(slot, 4, 0);
            }
            return true;
        }
        for (var i = 0u; i < p.ReadUInt32(arena); i++)
        {
            var slot = Slot(ref p, arena, i); var key = p.ReadUInt32(slot);
            if (key == uint.MaxValue || key < first || key - first >= count) continue;
            if (p.ReadUInt32(slot, 12) != 0) return false;
            Remove(ref p, arena, key); p.WriteUInt32(slot, 0, uint.MaxValue); p.WriteUInt32(slot, 4, 0);
        }
        return true;
    }

    private static APTR CacheSlot<T>(ref T p, APTR arena, uint key) where T : struct, IAmigaGuestMemory
    {
        var count = p.ReadUInt32(arena); var capacity = p.ReadUInt32(arena, 4); uint ordinal;
        if (count < capacity)
        {
            ordinal = count; var added = Slot(ref p, arena, ordinal); p.Clear(added, SlotBytes);
            p.WriteUInt32(added, 0, uint.MaxValue); p.WriteUInt32(arena, 0, count + 1);
        }
        else
        {
            ordinal = p.ReadUInt32(arena, 12);
            var attempts = 0u;
            while (attempts < capacity && (p.ReadUInt32(Slot(ref p, arena, ordinal), 12) != 0 ||
                p.ReadUInt32(Slot(ref p, arena, ordinal), 8) == uint.MaxValue))
            { ordinal++; if (ordinal == capacity) ordinal = 0; attempts++; }
            if (attempts == capacity) return APTR.Null;
        }
        var slot = Slot(ref p, arena, ordinal); var old = p.ReadUInt32(slot);
        if (old != uint.MaxValue) Remove(ref p, arena, old);
        var next = ordinal + 1; if (next == capacity) next = 0;
        p.WriteUInt32(arena, 12, next); p.WriteUInt32(slot, 0, key); p.WriteUInt32(slot, 4, 0);
        p.WriteUInt32(slot, 8, p.ReadUInt32(slot, 8) + 1);
        p.WriteUInt32(Bucket(ref p, arena, key), 0, slot.Raw - arena.Raw);
        return APTR.FromPointer(slot.Raw + SlotBytes);
    }

    /// <summary>The only contextual metadata read path. A temporary view expires
    /// on the next store access; Acquire pins the same owned image for traversal.</summary>
    public static APTR ReadView<T>(ref T p, uint block, uint id, APTR scratch) where T : struct, IAmigaFileSystemPlatform
        => ReadView(ref p, block, id, scratch, out _);

    private static APTR ReadView<T>(ref T p, uint block, uint id, APTR scratch, out APTR owner)
        where T : struct, IAmigaFileSystemPlatform
    {
        owner = APTR.Null;
        var context = p.Sfs2Context;
        if (!Sfs2MountContext.Active(ref p, context) || block >= p.BlockCount || id == 0) return APTR.Null;
        var overlay = Sfs2MountContext.Overlay(ref p, context);
        if (overlay.IsNotNull)
        {
            var current = View(ref p, overlay, block, id, out var found);
            if (found) { owner = overlay; return current; }
        }
        var cache = Sfs2MountContext.Cache(ref p, context);
        if (cache.IsNotNull)
        {
            var current = View(ref p, cache, block, id, out var found);
            if (found) { owner = cache; return current; }
            var image = CacheSlot(ref p, cache, block);
            if (image.IsNotNull)
            {
                if (p.ReadSfs2PhysicalMetadata(block, image) && VerifyView(ref p, cache, image, block, id).IsNotNull)
                { owner = cache; return image; }
                Invalidate(ref p, cache, block, 1); return APTR.Null;
            }
        }
        return p.ReadSfs2PhysicalMetadata(block, scratch) && p.ReadUInt32(scratch) == id && p.ReadUInt32(scratch, 8) == block &&
            AmigaFileSystemBlockIo.ValidSfs2Sum(ref p, scratch, p.BlockBytes) ? scratch : APTR.Null;
    }

    public static bool Acquire<T>(ref T p, uint block, uint id, APTR scratch, out Handle handle)
        where T : struct, IAmigaFileSystemPlatform
    {
        handle = default;
        var image = ReadView(ref p, block, id, scratch, out var arena); if (image.IsNull) return false;
        if (arena.IsNull) { handle.Image = image; handle.Block = block; handle.Type = id; return true; }
        var context = p.Sfs2Context;
        if (!Pin(ref p, arena, image, block, id, out handle)) return false;
        handle.Mount = context; handle.MountEpoch = p.ReadUInt32(context, 8); return true;
    }

    /// <summary>Copy on first edit into the private trial. No cache or accepted
    /// image is writable through this API. ReleaseEdit retains an unsealed edit
    /// until Publish seals the packet's changed images once.</summary>
    public static bool Edit<T>(ref T p, uint block, uint id, APTR scratch, out Handle handle)
        where T : struct, IAmigaFileSystemPlatform
    {
        handle = default; var arena = Sfs2MountContext.Overlay(ref p, p.Sfs2Context);
        if (arena.IsNull || p.ReadUInt32(arena, 24) != Trial) return false;
        var image = ReadView(ref p, block, id, scratch, out var owner); if (image.IsNull) return false;
        if (owner != arena)
        {
            if (!Store(ref p, arena, block, image, id)) return false;
            image = Find(ref p, arena, block);
        }
        if (!Pin(ref p, arena, image, block, id, out handle)) return false;
        if (p.ReadUInt32(handle.Slot, 12) != 1) { Release(ref p, ref handle); return false; }
        var revision = unchecked(p.ReadUInt32(handle.Slot, 8) + 1);
        if (revision == 0) { Release(ref p, ref handle); return false; }
        p.WriteUInt32(handle.Slot, 8, revision); handle.Revision = revision;
        p.WriteUInt32(handle.Slot, 12, uint.MaxValue);
        p.WriteUInt32(handle.Slot, 4, id | Dirty); handle.Editable = true;
        handle.Mount = p.Sfs2Context; handle.MountEpoch = p.ReadUInt32(handle.Mount, 8); return true;
    }

    public static bool ReleaseEdit<T>(ref T p, ref Handle handle) where T : struct, IAmigaGuestMemory
    {
        if (!handle.Editable || !Current(ref p, handle)) return false;
        p.WriteUInt32(handle.Slot, 12, 1);
        handle.Editable = false; return Release(ref p, ref handle);
    }
}
