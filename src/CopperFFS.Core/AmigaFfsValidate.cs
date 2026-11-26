using Amiga;

namespace CopperStart.Dos;

public static partial class AmigaFfsWriteCore
{
    /// <summary>Repair a volume whose root is marked invalid, as Kickstart's
    /// validator does: walk the directory tree from the root, rebuild the
    /// allocation bitmap from every reachable header, pointer table, data
    /// block and DOS4/5 cache block, then mark the root valid. A damaged
    /// structure (bad checksum or type, wrong parent, a block used twice, a
    /// cycle) refuses with DiskNotValidated and leaves the volume as it was.
    /// <paramref name="workspace"/> holds a used-block bitmap; a volume larger
    /// than it is checked in several passes over the tree. Meta, Data and
    /// Bitmap are scratch. The caller must hold exclusive mount ownership and
    /// discard any mount index and cached images first.</summary>
    public static bool Validate<T>(ref T p, ref Work w, APTR workspace, uint workspaceBytes)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var g = AmigaFfsGeometry.Read(ref p);
        w.Error = DOS.Error.None; w.LayoutKnown = false; w.BitmapBlock = 0; w.BitmapDirty = 0;
        if (g.BitmapBits == 0 || g.BlockCount <= g.ReservedBlocks + 1) return Fail(ref w, DOS.Error.DiskNotValidated);
        if (p.WriteProtected()) return Fail(ref w, DOS.Error.DiskWriteProtected);
        if (!LoadDosType(ref p, ref w)) return false;
        var pageBits = g.BitmapBits;
        var bits = g.BlockCount - g.ReservedBlocks;
        var maps = (bits - 1) / pageBits + 1;
        // Whole bitmap pages per pass, so every page is written once.
        var windowPages = workspaceBytes / 4 / (pageBits / 32);
        if (windowPages == 0 || !p.IsMapped(workspace, workspaceBytes)) return Fail(ref w, DOS.Error.NoFreeStore);
        if (windowPages > maps) windowPages = maps;
        var windowWords = windowPages * (pageBits / 32);
        for (var firstPage = 0u; firstPage < maps; firstPage += windowPages)
        {
            var lo = firstPage * pageBits;
            var hi = lo + windowPages * pageBits;
            p.Clear(workspace, windowWords * 4);
            if (!MarkLayout(ref p, ref w, in g, workspace, lo, hi, maps) || !MarkTree(ref p, ref w, in g, workspace, lo, hi))
                return false;
            if (!WritePages(ref p, ref w, in g, workspace, firstPage, firstPage + windowPages > maps ? maps : firstPage + windowPages, maps))
                return false;
        }
        // Publish validity only after the rebuilt bitmap is durable.
        if (!p.Flush()) return Fail(ref w, DOS.Error.DiskNotValidated);
        if (!p.ReadBlock(g.RootBlock, w.Meta)) return Fail(ref w, DOS.Error.DiskNotValidated);
        p.WriteUInt32(w.Meta, g.Tail + 312, uint.MaxValue);
        StampDate(ref p, ref w, w.Meta, g.Tail + 472);
        if (!Put(ref p, ref w, g.RootBlock, w.Meta, 20) || !p.Flush()) return Fail(ref w, DOS.Error.DiskNotValidated);
        return true;
    }

    // Record one used block; a block reached twice is a cross-link.
    private static bool MarkUsed<T>(ref T p, ref Work w, in AmigaFfsGeometry g, APTR used, uint lo, uint hi, uint key)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (key < g.ReservedBlocks || key >= g.BlockCount) return Fail(ref w, DOS.Error.DiskNotValidated);
        var bit = key - g.ReservedBlocks;
        if (bit < lo || bit >= hi) return true;
        var at = (int)((bit - lo) / 32) * 4; var mask = 1u << (int)(bit % 32);
        var word = p.ReadUInt32(used, at);
        if ((word & mask) != 0) return Fail(ref w, DOS.Error.DiskNotValidated);
        p.WriteUInt32(used, at, word | mask);
        return true;
    }

    // The root, its bitmap pages and their extension chain.
    private static bool MarkLayout<T>(ref T p, ref Work w, in AmigaFfsGeometry g, APTR used, uint lo, uint hi, uint maps)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!p.ReadBlock(g.RootBlock, w.Meta) || !AmigaDiskFileSystemCore.Checksum(ref p, w.Meta) ||
            p.ReadUInt32(w.Meta) != 2 || p.ReadUInt32(w.Meta, g.Tail + 508) != 1) return Fail(ref w, DOS.Error.DiskNotValidated);
        if (!MarkUsed(ref p, ref w, in g, used, lo, hi, g.RootBlock)) return false;
        var extension = p.ReadUInt32(w.Meta, g.Tail + 416);
        for (var i = 0u; i < maps && i < 25; i++)
            if (!MarkUsed(ref p, ref w, in g, used, lo, hi, p.ReadUInt32(w.Meta, g.Tail + 316 + (int)i * 4))) return false;
        var slots = g.BlockBytes / 4 - 1;
        for (var index = 25u; index < maps; index += slots)
        {
            if (!MarkUsed(ref p, ref w, in g, used, lo, hi, extension) || !p.ReadBlock(extension, w.Bitmap))
                return Fail(ref w, DOS.Error.DiskNotValidated);
            for (var i = 0u; i < slots && index + i < maps; i++)
                if (!MarkUsed(ref p, ref w, in g, used, lo, hi, p.ReadUInt32(w.Bitmap, (int)i * 4))) return false;
            extension = p.ReadUInt32(w.Bitmap, (int)g.BlockBytes - 4);
        }
        return true;
    }

    // Visit every directory entry without a stack: a finished directory
    // returns to its parent at the bucket its own name hashes to.
    private static bool MarkTree<T>(ref T p, ref Work w, in AmigaFfsGeometry g, APTR used, uint lo, uint hi)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var cached = w.DosType >= 0x444F5304;
        var directory = g.RootBlock;
        if (!p.ReadBlock(directory, w.Meta)) return Fail(ref w, DOS.Error.DiskNotValidated);
        if (cached && !MarkCache(ref p, ref w, in g, used, lo, hi, directory)) return false;
        uint slot = 0, entry = 0;
        for (var steps = 0u; ; steps++)
        {
            if (steps > 2 * g.BlockCount) return Fail(ref w, DOS.Error.DiskNotValidated);
            while (entry == 0 && slot < g.TableEntries) entry = p.ReadUInt32(w.Meta, 24 + (int)slot++ * 4);
            if (entry == 0)
            {
                if (directory == g.RootBlock) return true;
                // Return to the parent after this directory's own hash slot.
                var parent = p.ReadUInt32(w.Meta, g.Tail + 500);
                var successor = p.ReadUInt32(w.Meta, g.Tail + 496);
                var own = StoredNameSlot(ref p, w.Meta, in g, w.DosType);
                if (own == 0) return Fail(ref w, DOS.Error.DiskNotValidated);
                directory = parent;
                if (directory == g.RootBlock ? !p.ReadBlock(directory, w.Meta) : !AmigaDiskFileSystemCore.Header(ref p, in g, directory, w.Meta, true))
                    return Fail(ref w, DOS.Error.DiskNotValidated);
                slot = (uint)(own - 24) / 4 + 1; entry = successor;
                continue;
            }
            if (!AmigaDiskFileSystemCore.Header(ref p, in g, entry, w.Data, true) || p.ReadUInt32(w.Data, g.Tail + 500) != directory ||
                !MarkUsed(ref p, ref w, in g, used, lo, hi, entry)) return Fail(ref w, DOS.Error.DiskNotValidated);
            var type = unchecked((int)p.ReadUInt32(w.Data, g.Tail + 508));
            var next = p.ReadUInt32(w.Data, g.Tail + 496);
            if (type == 2)
            {
                // Descend: this directory's header becomes the current one.
                p.Copy(w.Data, w.Meta, g.BlockBytes);
                directory = entry; slot = 0; entry = 0;
                if (cached && !MarkCache(ref p, ref w, in g, used, lo, hi, directory)) return false;
                continue;
            }
            if (type == -3 && !MarkFile(ref p, ref w, in g, used, lo, hi, entry)) return false;
            entry = next;
        }
    }

    // A file header in Data: every data block and extension table.
    private static bool MarkFile<T>(ref T p, ref Work w, in AmigaFfsGeometry g, APTR used, uint lo, uint hi, uint header)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var payload = (w.DosType & 1) == 0 ? g.BlockBytes - 24 : g.BlockBytes;
        var size = p.ReadUInt32(w.Data, g.Tail + 324);
        var blocks = size == 0 ? 0 : AmigaGeometryMath.Divide(size - 1, payload) + 1;
        var table = w.Data; var marked = 0u;
        for (var guard = 0u; ; guard++)
        {
            var count = p.ReadUInt32(table, 8);
            if (guard > g.BlockCount || count > g.TableEntries || marked + count > blocks) return Fail(ref w, DOS.Error.DiskNotValidated);
            for (var i = 0u; i < count; i++)
                if (!MarkUsed(ref p, ref w, in g, used, lo, hi, p.ReadUInt32(table, g.Tail + 308 - (int)i * 4))) return false;
            marked += count;
            var next = p.ReadUInt32(table, g.Tail + 504);
            if (next == 0) return marked == blocks || Fail(ref w, DOS.Error.DiskNotValidated);
            if (!MarkUsed(ref p, ref w, in g, used, lo, hi, next) || !p.ReadBlock(next, w.Bitmap) ||
                !AmigaDiskFileSystemCore.Checksum(ref p, w.Bitmap) || p.ReadUInt32(w.Bitmap) != 16 ||
                p.ReadUInt32(w.Bitmap, 4) != next || p.ReadUInt32(w.Bitmap, g.Tail + 500) != header)
                return Fail(ref w, DOS.Error.DiskNotValidated);
            table = w.Bitmap;
        }
    }

    // A DOS4/5 directory's cache chain (the header is in Meta).
    private static bool MarkCache<T>(ref T p, ref Work w, in AmigaFfsGeometry g, APTR used, uint lo, uint hi, uint directory)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var key = p.ReadUInt32(w.Meta, g.Tail + 504);
        for (var guard = 0u; key != 0; guard++)
        {
            if (guard > g.BlockCount || !MarkUsed(ref p, ref w, in g, used, lo, hi, key) || !p.ReadBlock(key, w.Bitmap) ||
                !AmigaDiskFileSystemCore.Checksum(ref p, w.Bitmap) || p.ReadUInt32(w.Bitmap) != 33 ||
                p.ReadUInt32(w.Bitmap, 4) != key || p.ReadUInt32(w.Bitmap, 8) != directory)
                return Fail(ref w, DOS.Error.DiskNotValidated);
            key = p.ReadUInt32(w.Bitmap, 16);
        }
        return true;
    }

    // Write bitmap pages [first, end) from the used map; free bits are set.
    // A page whose contents already agree is left alone.
    private static bool WritePages<T>(ref T p, ref Work w, in AmigaFfsGeometry g, APTR used, uint first, uint end, uint maps)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var pageWords = g.BitmapBits / 32;
        var total = g.BlockCount - g.ReservedBlocks;
        var cursor = new AmigaDiskFileSystemCore.BitmapCursor();
        for (var page = first; page < end; page++)
        {
            if (!AmigaDiskFileSystemCore.BitmapBlock(ref p, ref cursor, page, w.Meta, out var key) || !p.ReadBlock(key, w.Data))
                return Fail(ref w, DOS.Error.DiskNotValidated);
            var changed = !AmigaDiskFileSystemCore.Checksum(ref p, w.Data);
            for (var i = 0u; i < pageWords; i++)
            {
                var start = page * g.BitmapBits + i * 32;
                uint free = 0;
                if (start < total)
                {
                    var valid = total - start >= 32 ? uint.MaxValue : (1u << (int)(total - start)) - 1;
                    free = ~p.ReadUInt32(used, (int)((page - first) * pageWords + i) * 4) & valid;
                }
                if (p.ReadUInt32(w.Data, 4 + (int)i * 4) != free) { p.WriteUInt32(w.Data, 4 + (int)i * 4, free); changed = true; }
            }
            if (changed && !Put(ref p, ref w, key, w.Data, 0)) return false;
        }
        return true;
    }
}
