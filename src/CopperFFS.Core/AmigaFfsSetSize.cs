using Amiga;

namespace CopperStart.Dos;

public static partial class AmigaFfsWriteCore
{
    /// <summary>ACTION_SET_FILE_SIZE on an OFS/FFS file header. Growth allocates
    /// and links data blocks like an append, but on FFS writes no payload: the
    /// new region reads back whatever the blocks held, as with Kickstart 3.1's
    /// FastFileSystem. OFS data blocks carry headers and checksums, so growth
    /// writes them with a zeroed payload. Shrinking frees the blocks and
    /// pointer tables past the new end. The FFS format has no holes: every
    /// block below the size is allocated.</summary>
    public static bool SetSize<T>(ref T p, ref Work w, uint header, uint size) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var g = AmigaFfsGeometry.Read(ref p);
        w.Error = DOS.Error.None;
        if (header == g.RootBlock) return Fail(ref w, DOS.Error.ObjectWrongType);
        if (MountIndexEnabled && HasFileWriteGroup(ref p, w.Index) && !FinishFileWriteGroup(ref p, ref w)) return false;
        if (!Prepare(ref p, ref w, in g, 0, attributeHeader: header)) return false;
        var image = InspectHeader(ref p, ref w, in g, header);
        if (image.IsNull) return false;
        if (p.ReadUInt32(image, g.Tail + 508) != 0xFFFFFFFD) return Fail(ref w, DOS.Error.ObjectWrongType);
        if ((p.ReadUInt32(image, g.Tail + 320) & 4) != 0) return Fail(ref w, DOS.Error.WriteProtected);
        var old = p.ReadUInt32(image, g.Tail + 324);
        var parent = p.ReadUInt32(image, g.Tail + 500);
        if (size == old) return true;
        if (size > old)
        {
            var r = new Request { Header = header, Position = old, Length = size - old, Buffer = w.Data };
            w.SkipData = true; w.BufferSmallAppends = false; w.GroupFileWrites = false;
            var grown = WriteThrough(ref p, ref w, ref r, out var actual, false);
            w.SkipData = false;
            if (!grown) return false;
            return actual == r.Length || Fail(ref w, DOS.Error.DiskFull);
        }
        if (size == 0) return Remove(ref p, ref w, header, true);
        return Shrink(ref p, ref w, in g, header, parent, size, old);
    }

    // Free every data block and extension table past the block that holds
    // byte size-1. The header's size is published with its own table write.
    private static bool Shrink<T>(ref T p, ref Work w, in AmigaFfsGeometry g, uint header, uint parent, uint size, uint old)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var ofs = (w.DosType & 1) == 0;
        var payload = ofs ? g.BlockBytes - 24 : g.BlockBytes;
        var keep = AmigaGeometryMath.Divide(size - 1, payload) + 1;
        var entries = g.TableEntries;
        var keepTables = AmigaGeometryMath.Divide(keep - 1, entries) + 1;
        // Prove the whole old layout before releasing any of it.
        if (!(MountIndexEnabled && w.LayoutKnown && AmigaFfsMountIndex.Matches(ref p, w.Index, header, old)) &&
            !Chain(ref p, ref w, header, false)) return false;
        w.CacheParent = parent; w.CacheKey = header;
        if (!Begin(ref p, ref w, in g)) return false;
        uint table = header, lastKey = 0;
        for (var t = 0u; table != 0; t++)
        {
            if (t >= g.BlockCount) return Fail(ref w, DOS.Error.DiskNotValidated);
            if (t == 0 ? !Header(ref p, ref w, header) : !p.ReadBlock(table, w.Meta)) return Fail(ref w, DOS.Error.DiskNotValidated);
            var count = p.ReadUInt32(w.Meta, 8);
            var next = p.ReadUInt32(w.Meta, g.Tail + 504);
            var first = t * entries;
            var kept = keep > first ? (keep - first < count ? keep - first : count) : 0;
            if (kept != 0) lastKey = p.ReadUInt32(w.Meta, g.Tail + 308 - (int)(kept - 1) * 4);
            // Bitmap updates may use Data as scratch; the table stays in Meta.
            for (var i = kept; i < count; i++)
            {
                var at = g.Tail + 308 - (int)i * 4;
                Bit(ref p, ref w, p.ReadUInt32(w.Meta, at), true);
                if (w.Error != DOS.Error.None) return false;
                p.WriteUInt32(w.Meta, at, 0);
            }
            if (t < keepTables)
            {
                var last = t + 1 == keepTables;
                if (t == 0 || kept != count || (last && next != 0))
                {
                    p.WriteUInt32(w.Meta, 8, kept);
                    if (last) p.WriteUInt32(w.Meta, g.Tail + 504, 0);
                    if (t == 0) p.WriteUInt32(w.Meta, g.Tail + 324, size);
                    if (!Put(ref p, ref w, table, w.Meta, 20)) return false;
                }
            }
            else
            {
                Bit(ref p, ref w, table, true);
                if (w.Error != DOS.Error.None) return false;
            }
            table = next;
        }
        // An OFS chain ends at its new last block, which records its length.
        if (ofs)
        {
            if (lastKey == 0 || !p.ReadBlock(lastKey, w.Data)) return Fail(ref w, DOS.Error.DiskNotValidated);
            p.WriteUInt32(w.Data, 12, size - (keep - 1) * payload);
            p.WriteUInt32(w.Data, 16, 0);
            if (!Put(ref p, ref w, lastKey, w.Data, 20)) return false;
        }
        return Finish(ref p, ref w, in g);
    }
}
