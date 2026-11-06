using Amiga;

namespace CopperStart.Dos;

// DOS0/DOS1 writer. Serial handler ownership; caller supplies three filesystem
// blocks of scratch. No managed allocation or host filesystem operations.
// Metadata transactions invalidate the root before mutation. A failed device
// write leaves it invalid so a host checkpoint cannot publish a partial image.
public static partial class AmigaFfsWriteCore
{
    public struct Work
    {
        public APTR Meta, Data, Bitmap;
        public uint BitmapBlock, BitmapFirst, BitmapCount, BitmapDirty, NextKey;
        public uint DosType;
        public DOS.Error Error;
    }
    public struct Request
    {
        public uint Directory, Header, Position, Length;
        public APTR Name, Buffer;
    }
    private static bool Fail(ref Work w, DOS.Error error) { w.Error = error; return false; }
    private static bool Header<T>(ref T p, ref Work w, uint key) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!AmigaDiskFileSystemCore.Header(ref p, key, w.Meta)) return Fail(ref w, DOS.Error.DiskNotValidated);
        return true;
    }
    private static bool Put<T>(ref T p, ref Work w, uint key, APTR buffer, int checksum) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (w.Error != DOS.Error.None) return false;
        if (checksum >= 0)
        {
            var blockBytes = p.BlockBytes;
            p.WriteUInt32(buffer, checksum, 0); uint sum = 0;
            for (var i = 0; i < blockBytes; i += 4) sum = unchecked(sum + p.ReadUInt32(buffer, i));
            p.WriteUInt32(buffer, checksum, unchecked(0u - sum));
        }
        return p.WriteBlock(key, buffer) || Fail(ref w, DOS.Error.DiskNotValidated);
    }
    private static bool Prepare<T>(ref T p, ref Work w, uint needed, bool scan = true) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var geometry = p.Geometry;
        w.Error = DOS.Error.None;
        var bitmapBits = geometry.BitmapBits;
        if (bitmapBits == 0) return Fail(ref w, DOS.Error.DiskNotValidated);
        if (p.WriteProtected()) return Fail(ref w, DOS.Error.DiskWriteProtected);
        if (!p.ReadBlock(0, w.Meta)) return Fail(ref w, DOS.Error.DiskNotValidated);
        var dosType = p.ReadUInt32(w.Meta);
        if (dosType is not (0x444F5300 or 0x444F5301)) return Fail(ref w, DOS.Error.ActionNotKnown);
        w.DosType = dosType;
        if (!Header(ref p, ref w, geometry.RootBlock) || p.ReadUInt32(w.Meta, geometry.Tail + 312) != uint.MaxValue)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        w.BitmapBlock = 0; w.BitmapDirty = 0; w.NextKey = geometry.ReservedBlocks;
        uint used = 0;
        if (scan && !AmigaDiskFileSystemCore.CountUsed(ref p, w.Data, w.Bitmap, out used))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if (IsFree(ref p, ref w, geometry.RootBlock)) return Fail(ref w, DOS.Error.DiskNotValidated);
        // Overwrites validate the touched pages on demand. Growth and other
        // metadata operations still inspect the whole map.
        if (!scan) return w.Error == DOS.Error.None;
        var maps = AmigaGeometryMath.Divide(geometry.BlockCount - geometry.ReservedBlocks - 1, bitmapBits) + 1;
        var cursor = new AmigaDiskFileSystemCore.BitmapCursor();
        for (var index = 0u; index < maps; index++)
        {
            if (!AmigaDiskFileSystemCore.BitmapBlock(ref p, ref cursor, index, w.Meta, out var map) || IsFree(ref p, ref w, map))
                return Fail(ref w, DOS.Error.DiskNotValidated);
        }
        // Bitmap extension blocks are themselves allocated filesystem blocks.
        if (!Header(ref p, ref w, geometry.RootBlock)) return false;
        var extension = p.ReadUInt32(w.Meta, geometry.Tail + 416);
        for (var guard = 0u; extension != 0; guard++)
        {
            if (guard >= geometry.BlockCount || extension < geometry.ReservedBlocks ||
                extension >= geometry.BlockCount || IsFree(ref p, ref w, extension) ||
                !p.ReadBlock(extension, w.Data)) return Fail(ref w, DOS.Error.DiskNotValidated);
            extension = p.ReadUInt32(w.Data, (int)geometry.BlockBytes - 4);
        }
        if (w.Error != DOS.Error.None) return false;
        if (geometry.BlockCount - used < needed) return Fail(ref w, DOS.Error.DiskFull);
        return true;
    }
    private static bool Begin<T>(ref T p, ref Work w, ref AmigaVolumeGeometry geometry, uint valid = 0) where T : struct, IAmigaWritableFileSystemPlatform
    {

        if (!Header(ref p, ref w, geometry.RootBlock)) return false;
        p.WriteUInt32(w.Meta, geometry.Tail + 312, valid);
        return Put(ref p, ref w, geometry.RootBlock, w.Meta, 20);
    }
    private static bool Finish<T>(ref T p, ref Work w, ref AmigaVolumeGeometry geometry) where T : struct, IAmigaWritableFileSystemPlatform
    {
        return FlushBitmap(ref p, ref w) && Begin(ref p, ref w, ref geometry, uint.MaxValue);
    }
    private static bool FlushBitmap<T>(ref T p, ref Work w) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (w.Error != DOS.Error.None) return false;
        if (w.BitmapDirty == 0) return true;
        if (!Put(ref p, ref w, w.BitmapBlock, w.Bitmap, 0)) return false;
        w.BitmapDirty = 0; return true;
    }
    private static bool LoadBitmap<T>(ref T p, ref Work w, uint key) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var cursor = new AmigaDiskFileSystemCore.BitmapCursor();
        return LoadBitmap(ref p, ref w, key, ref cursor);
    }
    private static bool LoadBitmap<T>(ref T p, ref Work w, uint key, ref AmigaDiskFileSystemCore.BitmapCursor cursor)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        // The mount geometry is fixed for this work record. Retain only the
        // valid part of the loaded page so hits need no geometry reconstruction
        // or division, and padding beyond the volume never becomes a hit.
        // The clamped span cannot wrap, so unsigned subtraction also rejects lower keys.
        if (w.BitmapBlock != 0 && unchecked(key - w.BitmapFirst) < w.BitmapCount)
            return w.Error == DOS.Error.None;
        var blockCount = p.BlockCount;
        var reservedBlocks = p.ReservedBlocks;
        var bitmapBits = (p.BlockBytes - 4) * 8;
        if (bitmapBits == 0) return Fail(ref w, DOS.Error.DiskNotValidated);
        if (key < reservedBlocks || key >= blockCount) return Fail(ref w, DOS.Error.DiskNotValidated);
        var index = AmigaGeometryMath.Divide(key - reservedBlocks, bitmapBits);
        if (!FlushBitmap(ref p, ref w) || !AmigaDiskFileSystemCore.BitmapBlock(ref p, ref cursor, index, w.Data, out var block) ||
            !p.ReadBlock(block, w.Bitmap) || !AmigaDiskFileSystemCore.Checksum(ref p, w.Bitmap))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        var first = reservedBlocks + index * bitmapBits;
        var count = blockCount - first;
        w.BitmapBlock = block; w.BitmapFirst = first;
        w.BitmapCount = count > bitmapBits ? bitmapBits : count;
        return true;
    }
    private static bool IsFree<T>(ref T p, ref Work w, uint key) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!LoadBitmap(ref p, ref w, key)) return false;
        var bit = key - w.BitmapFirst;
        return (p.ReadUInt32(w.Bitmap, 4 + (int)(bit / 32) * 4) & (1u << (int)(bit % 32))) != 0;
    }
    private static void Bit<T>(ref T p, ref Work w, uint key, bool free) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!LoadBitmap(ref p, ref w, key)) return;
        var bit = key - w.BitmapFirst;
        var at = 4 + (int)(bit / 32) * 4; var mask = 1u << (int)(bit % 32);
        var value = p.ReadUInt32(w.Bitmap, at);
        p.WriteUInt32(w.Bitmap, at, free ? value | mask : value & ~mask); w.BitmapDirty = 1;
    }
    private static uint Allocate<T>(ref T p, ref Work w) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (w.Error != DOS.Error.None) return 0;
        var geometry = p.Geometry;
        var key = w.NextKey;
        var cursor = new AmigaDiskFileSystemCore.BitmapCursor();
        while (key < geometry.BlockCount)
        {
            // Stay in the loaded page while scanning its words. Loading another
            // page validates it and flushes the previous page if it was dirty.
            if ((w.BitmapBlock == 0 || key < w.BitmapFirst || key - w.BitmapFirst >= geometry.BitmapBits) &&
                !LoadBitmap(ref p, ref w, key, ref cursor)) return 0;
            var bit = key - w.BitmapFirst;
            var at = 4 + (int)(bit / 32) * 4;
            var shift = (int)(bit % 32);
            var free = p.ReadUInt32(w.Bitmap, at) & (uint.MaxValue << shift);
            if (free != 0)
            {
                // Find the first free bit in this word without probing each
                // occupied block (or dividing its block number) individually.
                shift = (int)AmigaBitmapWordCore.FirstSet(free);
                var advance = (uint)shift - bit % 32;
                if (advance >= geometry.BlockCount - key) break;
                key += advance;
                if (key != geometry.RootBlock)
                {
                    p.WriteUInt32(w.Bitmap, at, p.ReadUInt32(w.Bitmap, at) & ~(1u << shift));
                    w.BitmapDirty = 1; w.NextKey = key + 1;
                    return key;
                }
                key++;
            }
            else
            {
                var advance = 32 - bit % 32;
                if (advance >= geometry.BlockCount - key) break;
                key += advance;
            }
        }
        Fail(ref w, DOS.Error.DiskFull); return 0;
    }
    // A run is only a request-local hint, not an on-disk reservation. It ends
    // before the next pointer table, where metadata may consume free space.
    // Keep NextKey independent: its first-free scan must still find earlier
    // holes after data has been placed near a file's tail.
    private static uint AllocateData<T>(ref T p, ref Work w, ref AmigaVolumeGeometry g,
        uint preferred, uint wanted, ref uint runFirst, ref uint runCount)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (w.Error != DOS.Error.None) return 0;
        if (runCount == 0 && preferred >= g.ReservedBlocks && preferred < g.BlockCount)
        {
            if (!LoadBitmap(ref p, ref w, preferred)) return 0;
            var available = w.BitmapCount - (preferred - w.BitmapFirst);
            if (wanted > available) wanted = available;
            // An adjacent prefix is useful even when it is shorter than the
            // write or crosses a track. This changes placement, not device I/O.
            while (runCount < wanted && SearchFree(ref p, ref w, preferred + runCount, g.RootBlock)) runCount++;
            runFirst = preferred;
            if (runCount == 0)
            {
                // Only inspect the rest of this track (or a table-sized window
                // for unknown geometry), in the bitmap already loaded above.
                // Never load another bitmap merely to find a longer run.
                var window = g.TrackBlocks > 1
                    ? g.TrackBlocks - AmigaGeometryMath.Remainder(preferred, g.TrackBlocks)
                    : g.TableEntries;
                if (window > available) window = available;
                uint first = 0, count = 0;
                for (var offset = 1u; offset < window; offset++)
                {
                    var key = preferred + offset;
                    if (SearchFree(ref p, ref w, key, g.RootBlock))
                    {
                        if (count == 0) first = key;
                        count++;
                        if (count > runCount) { runFirst = first; runCount = count; }
                        if (count == wanted) break;
                    }
                    else count = 0;
                }
            }
        }
        if (runCount == 0) return Allocate(ref p, ref w);
        var result = runFirst;
        // Single-owner execution and the table boundary keep the hint live.
        // Recheck the consumed bit, including after a bitmap cache replacement.
        if (!IsFree(ref p, ref w, result))
        {
            if (w.Error == DOS.Error.None) Fail(ref w, DOS.Error.DiskNotValidated);
            return 0;
        }
        Bit(ref p, ref w, result, false);
        if (w.Error != DOS.Error.None) return 0;
        runFirst++; runCount--;
        return result;
    }
    private static bool SearchFree<T>(ref T p, ref Work w, uint key, uint root)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var bit = key - w.BitmapFirst;
        return key != root && (p.ReadUInt32(w.Bitmap, 4 + (int)(bit / 32) * 4) & (1u << (int)(bit % 32))) != 0;
    }
    private static uint Hash<T>(ref T p, APTR name, uint length) where T : struct, IAmigaFileSystemPlatform
    {
        var tableEntries = p.BlockBytes / 4 - 56;
        if (tableEntries == 0) return 0;
        var hash = length;
        for (var i = 0u; i < length; i++) { var c = p.ReadUInt8(name, (int)i); hash = (hash * 13 + (c >= 97 && c <= 122 ? (uint)(c - 32) : c)) & 2047; }
        return AmigaGeometryMath.Remainder(hash, tableEntries);
    }
    // Resolve the parent and retain a slice of the final component.
    public static bool Parent<T>(ref T p, ref Work w, ref Request r) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var blockBytes = p.BlockBytes;
        var tail = (int)blockBytes - 512;
        if (r.Length == 0 || r.Length > 255) return Fail(ref w, DOS.Error.InvalidComponentName);
        uint start = 0;
        for (var i = 0u; i < r.Length; i++) if (p.ReadUInt8(r.Name, (int)i) == ':' || p.ReadUInt8(r.Name, (int)i) == '/') start = i + 1;
        if (start != 0)
        {
            var parentLength = start;
            if (p.ReadUInt8(r.Name, (int)start - 1) == '/') parentLength--;
            r.Directory = AmigaDiskFileSystemCore.Resolve(ref p, r.Directory, r.Name, parentLength, 0x444F5301, w.Meta, out var error);
            if (r.Directory == 0) return Fail(ref w, error);
        }
        r.Name = APTR.FromPointer(r.Name.Raw + start); r.Length -= start;
        if (r.Length == 0 || r.Length > 30) return Fail(ref w, DOS.Error.InvalidComponentName);
        return Header(ref p, ref w, r.Directory) && (unchecked((int)p.ReadUInt32(w.Meta, tail + 508)) > 0 || Fail(ref w, DOS.Error.ObjectWrongType));
    }
    private static bool Link<T>(ref T p, ref Work w, uint key, uint directory) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var blockBytes = p.BlockBytes;
        var tail = (int)blockBytes - 512;
        if (!Header(ref p, ref w, key)) return false;
        var hash = Hash(ref p, APTR.FromPointer(w.Meta.Raw + blockBytes - 79), p.ReadUInt8(w.Meta, tail + 432));
        if (!Header(ref p, ref w, directory)) return false;
        var next = p.ReadUInt32(w.Meta, 24 + (int)hash * 4);
        if (!Header(ref p, ref w, key)) return false;
        p.WriteUInt32(w.Meta, tail + 496, next); p.WriteUInt32(w.Meta, tail + 500, directory);
        if (!Put(ref p, ref w, key, w.Meta, 20) || !Header(ref p, ref w, directory)) return false;
        p.WriteUInt32(w.Meta, 24 + (int)hash * 4, key);
        return Put(ref p, ref w, directory, w.Meta, 20);
    }
    private static bool Unlink<T>(ref T p, ref Work w, uint key) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var blockBytes = p.BlockBytes;
        var tail = (int)blockBytes - 512;
        var blockCount = p.BlockCount;
        if (!Header(ref p, ref w, key)) return false;
        var directory = p.ReadUInt32(w.Meta, tail + 500); var successor = p.ReadUInt32(w.Meta, tail + 496);
        var hash = Hash(ref p, APTR.FromPointer(w.Meta.Raw + blockBytes - 79), p.ReadUInt8(w.Meta, tail + 432));
        if (!Header(ref p, ref w, directory)) return false;
        var at = 24 + (int)hash * 4; var current = p.ReadUInt32(w.Meta, at); var owner = directory;
        for (var guard = 0u; current != 0 && guard < blockCount; guard++)
        {
            if (current == key) { p.WriteUInt32(w.Meta, at, successor); return Put(ref p, ref w, owner, w.Meta, 20); }
            owner = current; at = tail + 496;
            if (!Header(ref p, ref w, current)) return false;
            current = p.ReadUInt32(w.Meta, at);
        }
        return Fail(ref w, DOS.Error.DiskNotValidated);
    }
    public static bool Create<T>(ref T p, ref Work w, ref Request r, bool directory) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var geometry = p.Geometry;
        if (!Parent(ref p, ref w, ref r)) return false;
        var existing = AmigaDiskFileSystemCore.Resolve(ref p, r.Directory, r.Name, r.Length, 0x444F5301, w.Meta, out var error);
        if (existing != 0) return Fail(ref w, DOS.Error.ObjectExists);
        if (error != DOS.Error.ObjectNotFound) return Fail(ref w, error);
        if (!Prepare(ref p, ref w, 1) || !Begin(ref p, ref w, ref geometry)) return false;
        var key = Allocate(ref p, ref w); if (key == 0) return false;
        p.Clear(w.Meta, geometry.BlockBytes); p.WriteUInt32(w.Meta, 0, 2); p.WriteUInt32(w.Meta, 4, key);
        p.WriteUInt32(w.Meta, 12, directory || w.DosType == 0x444F5301 ? geometry.TableEntries : 0);
        p.WriteUInt32(w.Meta, geometry.Tail + 508, directory ? 2u : 0xFFFFFFFD);
        p.WriteUInt8(w.Meta, geometry.Tail + 432, (byte)r.Length);
        for (var i = 0u; i < r.Length; i++) p.WriteUInt8(w.Meta, geometry.Tail + 433 + (int)i, p.ReadUInt8(r.Name, (int)i));
        if (!Put(ref p, ref w, key, w.Meta, 20) || !Link(ref p, ref w, key, r.Directory) || !Finish(ref p, ref w, ref geometry)) return false;
        r.Header = key; return true;
    }
    private static bool Allocated<T>(ref T p, ref Work w, ref AmigaVolumeGeometry geometry, uint key) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (key < geometry.ReservedBlocks || key >= geometry.BlockCount || key == geometry.RootBlock ||
            IsFree(ref p, ref w, key) || key == w.BitmapBlock || w.Error != DOS.Error.None)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        return true;
    }
    // An overwrite checks only the table path and data pointers in [first, limit).
    // uint.MaxValue retains the entire-chain validation used for growth/release.
    private static bool Chain<T>(ref T p, ref Work w, uint header, bool release,
        uint first = 0, uint limit = uint.MaxValue) where T : struct, IAmigaWritableFileSystemPlatform
        => Chain(ref p, ref w, header, release, out _, first, limit);
    private static bool Chain<T>(ref T p, ref Work w, uint header, bool release,
        out uint lastData, uint first = 0, uint limit = uint.MaxValue) where T : struct, IAmigaWritableFileSystemPlatform
    {
        lastData = 0;
        if (w.DosType == 0x444F5300) return OfsChain(ref p, ref w, header, release, out lastData);
        var geometry = p.Geometry;
        var table = header;
        uint logical = 0, blocks = 0;
        for (var guard = 0u; table != 0 && guard < geometry.BlockCount; guard++)
        {
            if (!Allocated(ref p, ref w, ref geometry, table) || !p.ReadBlock(table, w.Meta) || !AmigaDiskFileSystemCore.Checksum(ref p, w.Meta) ||
                p.ReadUInt32(w.Meta, 4) != table || p.ReadUInt32(w.Meta, 8) > geometry.TableEntries ||
                p.ReadUInt32(w.Meta) != (table == header ? 2u : 16u) || p.ReadUInt32(w.Meta, geometry.Tail + 508) != 0xFFFFFFFD ||
                (table != header && p.ReadUInt32(w.Meta, geometry.Tail + 500) != header)) return Fail(ref w, DOS.Error.DiskNotValidated);
            var count = p.ReadUInt32(w.Meta, 8);
            if (table == header)
            {
                var size = p.ReadUInt32(w.Meta, geometry.Tail + 324);
                blocks = size == 0 ? 0 : AmigaGeometryMath.Divide(size - 1, geometry.BlockBytes) + 1;
            }
            if (limit != uint.MaxValue && count < geometry.TableEntries && count < limit)
                return Fail(ref w, DOS.Error.DiskNotValidated);
            for (var i = first; i < count && i < limit; i++)
            {
                var key = p.ReadUInt32(w.Meta, geometry.Tail + 308 - (int)i * 4);
                if (!Allocated(ref p, ref w, ref geometry, key)) return false;
                if (blocks != 0 && logical + i == blocks - 1) lastData = key;
                if (release) Bit(ref p, ref w, key, true);
            }
            var next = p.ReadUInt32(w.Meta, geometry.Tail + 504);
            if (table != header && release) Bit(ref p, ref w, table, true);
            if (limit <= geometry.TableEntries) return w.Error == DOS.Error.None;
            if (next == 0) return limit == uint.MaxValue && w.Error == DOS.Error.None || Fail(ref w, DOS.Error.DiskNotValidated);
            first = first > geometry.TableEntries ? first - geometry.TableEntries : 0;
            if (limit != uint.MaxValue) limit -= geometry.TableEntries;
            logical += geometry.TableEntries;
            table = next;
        }
        return Fail(ref w, DOS.Error.DiskNotValidated);
    }
    public static bool Remove<T>(ref T p, ref Work w, uint key, bool truncate) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var geometry = p.Geometry;
        if (key == geometry.RootBlock) return Fail(ref w, DOS.Error.ObjectInUse);
        if (!Prepare(ref p, ref w, 0) || !Header(ref p, ref w, key)) return false;
        var type = unchecked((int)p.ReadUInt32(w.Meta, geometry.Tail + 508));
        if ((p.ReadUInt32(w.Meta, geometry.Tail + 320) & (truncate ? 4u : 1u)) != 0) return Fail(ref w, truncate ? DOS.Error.WriteProtected : DOS.Error.DeleteProtected);
        if (type > 0)
        {
            if (truncate) return Fail(ref w, DOS.Error.ObjectWrongType);
            for (var at = 24; at < geometry.Tail + 312; at += 4) if (p.ReadUInt32(w.Meta, at) != 0) return Fail(ref w, DOS.Error.DirectoryNotEmpty);
        }
        else if (!Chain(ref p, ref w, key, false)) return false;
        if (!Begin(ref p, ref w, ref geometry)) return false;
        if (type < 0 && !Chain(ref p, ref w, key, true)) return false;
        if (truncate)
        {
            if (!Header(ref p, ref w, key)) return false;
            for (var at = 24; at < geometry.Tail + 312; at += 4) p.WriteUInt32(w.Meta, at, 0);
            p.WriteUInt32(w.Meta, 8, 0); p.WriteUInt32(w.Meta, 16, 0); p.WriteUInt32(w.Meta, geometry.Tail + 324, 0); p.WriteUInt32(w.Meta, geometry.Tail + 504, 0);
            if (!Put(ref p, ref w, key, w.Meta, 20)) return false;
        }
        else { if (!Unlink(ref p, ref w, key)) return false; Bit(ref p, ref w, key, true); }
        return Finish(ref p, ref w, ref geometry);
    }
    public static bool Rename<T>(ref T p, ref Work w, ref Request r) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var geometry = p.Geometry;
        if (r.Header == geometry.RootBlock) return Fail(ref w, DOS.Error.ObjectInUse);
        if (!Parent(ref p, ref w, ref r)) return false;
        var found = AmigaDiskFileSystemCore.Resolve(ref p, r.Directory, r.Name, r.Length, 0x444F5301, w.Meta, out var error);
        if (found == r.Header) return true;
        if (found != 0) return Fail(ref w, DOS.Error.ObjectExists);
        if (error != DOS.Error.ObjectNotFound) return Fail(ref w, error);
        if (!Prepare(ref p, ref w, 0) || !Header(ref p, ref w, r.Header)) return false;
        if ((p.ReadUInt32(w.Meta, geometry.Tail + 320) & 1) != 0) return Fail(ref w, DOS.Error.DeleteProtected);
        var ancestor = r.Directory;
        for (var guard = 0u; ancestor != geometry.RootBlock && guard < geometry.BlockCount; guard++)
        {
            if (ancestor == r.Header) return Fail(ref w, DOS.Error.ObjectInUse);
            if (!Header(ref p, ref w, ancestor)) return false;
            ancestor = p.ReadUInt32(w.Meta, geometry.Tail + 500);
            if (guard + 1 == geometry.BlockCount) return Fail(ref w, DOS.Error.DiskNotValidated);
        }
        if (!Begin(ref p, ref w, ref geometry) || !Unlink(ref p, ref w, r.Header) || !Header(ref p, ref w, r.Header)) return false;
        for (var at = geometry.Tail + 432; at < geometry.Tail + 464; at++) p.WriteUInt8(w.Meta, at, 0);
        p.WriteUInt8(w.Meta, geometry.Tail + 432, (byte)r.Length);
        for (var i = 0u; i < r.Length; i++) p.WriteUInt8(w.Meta, geometry.Tail + 433 + (int)i, p.ReadUInt8(r.Name, (int)i));
        return Put(ref p, ref w, r.Header, w.Meta, 20) && Link(ref p, ref w, r.Header, r.Directory) && Finish(ref p, ref w, ref geometry);
    }
    public static bool SetMetadata<T>(ref T p, ref Work w, ref Request r,
        DosPacketAction action) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var geometry = p.Geometry;
        if (action != DosPacketAction.SetProtection && action != DosPacketAction.SetComment && action != DosPacketAction.SetDate)
            return Fail(ref w, DOS.Error.ActionNotKnown);
        // The volume root has a different layout: these file-header fields
        // hold bitmap pointers there, rather than protection/comment data.
        if (r.Header == geometry.RootBlock) return Fail(ref w, DOS.Error.ActionNotKnown);
        if (action == DosPacketAction.SetComment && r.Length > 79)
            return Fail(ref w, DOS.Error.CommentTooBig);
        if (!Prepare(ref p, ref w, 0) || !Header(ref p, ref w, r.Header)) return false;
        if (!Begin(ref p, ref w, ref geometry) || !Header(ref p, ref w, r.Header)) return false;
        if (action == DosPacketAction.SetProtection)
            p.WriteUInt32(w.Meta, geometry.Tail + 320, r.Position);
        else if (action == DosPacketAction.SetDate)
            for (var offset = 0; offset < 12; offset += 4)
                p.WriteUInt32(w.Meta, geometry.Tail + 420 + offset, p.ReadUInt32(r.Buffer, offset));
        else if (action == DosPacketAction.SetComment)
        {
            for (var offset = geometry.Tail + 328; offset < geometry.Tail + 408; offset++) p.WriteUInt8(w.Meta, offset, 0);
            p.WriteUInt8(w.Meta, geometry.Tail + 328, (byte)r.Length);
            for (var index = 0u; index < r.Length; index++)
                p.WriteUInt8(w.Meta, geometry.Tail + 329 + (int)index, p.ReadUInt8(r.Buffer, (int)index));
        }
        return Put(ref p, ref w, r.Header, w.Meta, 20) && Finish(ref p, ref w, ref geometry);
    }

    public static bool Write<T>(ref T p, ref Work w, ref Request r, out uint actual) where T : struct, IAmigaWritableFileSystemPlatform
        => Write(ref p, ref w, ref r, out actual, false);

    // Opt in only when the mount has positively identified a fixed hard disk.
    public static bool Write<T>(ref T p, ref Work w, ref Request r, out uint actual, bool fixedHardDisk) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var geometry = p.Geometry;
        actual = 0;
        var blockBytes = geometry.BlockBytes; var tableEntries = geometry.TableEntries;
        if (blockBytes == 0 || tableEntries == 0) return Fail(ref w, DOS.Error.DiskNotValidated);
        if (r.Length > int.MaxValue || !p.IsMapped(r.Buffer, r.Length)) return Fail(ref w, DOS.Error.BadNumber);
        if (!Prepare(ref p, ref w, 0, false)) return false;
        if (w.DosType == 0x444F5300) return WriteOfs(ref p, ref w, ref r, out actual, fixedHardDisk);
        if (!Header(ref p, ref w, r.Header)) return false;
        if (p.ReadUInt32(w.Meta, geometry.Tail + 508) != 0xFFFFFFFD) return Fail(ref w, DOS.Error.ObjectWrongType);
        if ((p.ReadUInt32(w.Meta, geometry.Tail + 320) & 4) != 0) return Fail(ref w, DOS.Error.WriteProtected);
        var size = p.ReadUInt32(w.Meta, geometry.Tail + 324);
        if (r.Position > size) return Fail(ref w, DOS.Error.SeekError);
        if (r.Length > uint.MaxValue - r.Position) return Fail(ref w, DOS.Error.DiskFull);
        if (r.Length == 0) return true;
        var end = r.Position + r.Length; var newSize = end > size ? end : size;
        var oldBlocks = size == 0 ? 0 : AmigaGeometryMath.Divide(size - 1, blockBytes) + 1;
        var first = 0u; var limit = uint.MaxValue;
        if (end <= size)
        {
            first = AmigaGeometryMath.Divide(r.Position, blockBytes);
            limit = AmigaGeometryMath.Divide(end - 1, blockBytes) + 1;
        }
        if (!Chain(ref p, ref w, r.Header, false, out var lastData, first, limit)) return false;
        var newBlocks = oldBlocks;
        if (end > size)
        {
            newBlocks = AmigaGeometryMath.Divide(end - 1, blockBytes) + 1;
            var oldExt = oldBlocks == 0 ? 0 : AmigaGeometryMath.Divide(oldBlocks - 1, tableEntries);
            var newExt = AmigaGeometryMath.Divide(newBlocks - 1, tableEntries);
            if (!Prepare(ref p, ref w, newBlocks - oldBlocks + newExt - oldExt)) return false;
        }
        if (!Begin(ref p, ref w, ref geometry)) return false;
        var table = r.Header; var tableIndex = 0u;
        uint runFirst = 0, runCount = 0;
        // A failed fit is final for this table. Never probe again per sector.
        var probeAt = fixedHardDisk && end <= size && ((r.Position | r.Length) & (blockBytes - 1)) == 0
            ? first : uint.MaxValue;
        while (actual < r.Length)
        {
            var position = r.Position + actual; var index = AmigaGeometryMath.Divide(position, blockBytes);
            var targetTable = AmigaGeometryMath.Divide(index, tableEntries);
            // Walk/extend pointer tables sequentially, retaining only their key.
            while (tableIndex < targetTable)
            {
                runCount = 0;
                if (!p.ReadBlock(table, w.Meta)) return Fail(ref w, DOS.Error.DiskNotValidated);
                var next = p.ReadUInt32(w.Meta, geometry.Tail + 504);
                if (next == 0)
                {
                    next = Allocate(ref p, ref w); if (next == 0) return false;
                    p.WriteUInt32(w.Meta, geometry.Tail + 504, next);
                    if (!Put(ref p, ref w, table, w.Meta, 20)) return false;
                    p.Clear(w.Meta, geometry.BlockBytes); p.WriteUInt32(w.Meta, 0, 16); p.WriteUInt32(w.Meta, 4, next);
                    p.WriteUInt32(w.Meta, 12, geometry.TableEntries); p.WriteUInt32(w.Meta, geometry.Tail + 500, r.Header); p.WriteUInt32(w.Meta, geometry.Tail + 508, 0xFFFFFFFD);
                    if (!Put(ref p, ref w, next, w.Meta, 20)) return false;
                }
                table = next; tableIndex++;
            }
            if (!p.ReadBlock(table, w.Meta)) return Fail(ref w, DOS.Error.DiskNotValidated);
            var slot = AmigaGeometryMath.Remainder(index, tableEntries);
            var at = geometry.Tail + 308 - (int)slot * 4; var key = p.ReadUInt32(w.Meta, at);
            if (index == probeAt)
            {
                var countInTable = tableEntries - slot;
                if (countInTable > limit - index) countInTable = limit - index;
                probeAt += countInTable;
                if (!RelocateOverwrite(ref p, ref w, ref geometry, ref r, table, slot, countInTable, ref actual, out var moved)) return false;
                if (moved)
                {
                    if (actual == r.Length) return Finish(ref p, ref w, ref geometry);
                    continue;
                }
            }
            if (index >= oldBlocks)
            {
                var wanted = newBlocks - index;
                var slots = tableEntries - AmigaGeometryMath.Remainder(index, tableEntries);
                if (wanted > slots) wanted = slots;
                key = AllocateData(ref p, ref w, ref geometry, (lastData == 0 ? r.Header : lastData) + 1,
                    wanted, ref runFirst, ref runCount);
                if (key == 0) return false;
                lastData = key;
                p.WriteUInt32(w.Meta, at, key); p.WriteUInt32(w.Meta, 8, AmigaGeometryMath.Remainder(index, tableEntries) + 1);
                if (index == 0) p.WriteUInt32(w.Meta, 16, key);
                if (!Put(ref p, ref w, table, w.Meta, 20)) return false;
                p.Clear(w.Data, geometry.BlockBytes);
            }
            else if (!p.ReadBlock(key, w.Data)) return Fail(ref w, DOS.Error.DiskNotValidated);
            var within = AmigaGeometryMath.Remainder(position, blockBytes); var count = blockBytes - within;
            if (count > r.Length - actual) count = r.Length - actual;
            for (var i = 0u; i < count; i++) p.WriteUInt8(w.Data, (int)(within + i), p.ReadUInt8(r.Buffer, (int)(actual + i)));
            if (!Put(ref p, ref w, key, w.Data, -1)) return false;
            actual += count;
        }
        if (!Header(ref p, ref w, r.Header)) return false;
        p.WriteUInt32(w.Meta, geometry.Tail + 324, newSize);
        return Put(ref p, ref w, r.Header, w.Meta, 20) && Finish(ref p, ref w, ref geometry);
    }

    // Limit the first hard-disk policy to a single bitmap page per table
    // segment. Reservation and release then each cost just one bitmap write.
    private static bool RelocateOverwrite<T>(ref T p, ref Work w, ref AmigaVolumeGeometry g,
        ref Request r, uint table, uint slot, uint count, ref uint actual, out bool moved)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        moved = false;
        if (count < 8) return true;
        var at = g.Tail + 308 - (int)slot * 4;
        var first = p.ReadUInt32(w.Meta, at);
        var last = first;
        var breaks = 0u;
        for (var i = 1u; i < count; i++)
        {
            var key = p.ReadUInt32(w.Meta, at - (int)i * 4);
            if (w.DosType == 0x444F5300)
            {
                if ((key > last ? key - last : last - key) >= 128) breaks++;
            }
            else if (key != last + 1) breaks++;
            last = key;
        }
        // Charge both ends for potentially severing an existing run. At a
        // table boundary the reader already starts a separate transfer.
        var entries = p.ReadUInt32(w.Meta, 8);
        var cuts = slot != 0 && p.ReadUInt32(w.Meta, at + 4) + 1 == first ? 1u : 0u;
        if (slot + count < entries && p.ReadUInt32(w.Meta, at - (int)count * 4) == last + 1) cuts++;
        // The 32-block/four-run benchmark amortized publication on the
        // overwrite itself. Keep the higher read-savings margin below it.
        var requiredBreaks = count >= 32 ? 3u : 4u;
        if (w.DosType == 0x444F5300) requiredBreaks = 7;
        if (breaks < requiredBreaks + cuts) return true;
        if (!LoadBitmap(ref p, ref w, first)) return false;
        for (var i = 1u; i < count; i++)
            if (unchecked(p.ReadUInt32(w.Meta, at - (int)i * 4) - w.BitmapFirst) >= w.BitmapCount) return true;

        // Try just past the last old block, then the beginning of the same
        // page. The second window can find space before a crowded file.
        var offset = last - w.BitmapFirst + 1;
        var destination = OverwriteRun(ref p, ref w, g.RootBlock, offset, w.BitmapCount, count);
        if (destination == 0) destination = OverwriteRun(ref p, ref w, g.RootBlock, 0, w.BitmapCount, count);
        if (destination == 0) return true;

        // Validate source ownership within this table before releasing any
        // pointers. Data is scratch until the new pointer image is assembled.
        p.Clear(w.Data, g.BlockBytes);
        var next = p.ReadUInt32(w.Meta, g.Tail + 504);
        for (var i = 0u; i < entries; i++)
        {
            var key = p.ReadUInt32(w.Meta, g.Tail + 308 - (int)i * 4);
            var bit = unchecked(key - w.BitmapFirst);
            if (bit >= w.BitmapCount) continue;
            var word = (int)(bit / 32) * 4;
            var mask = 1u << (int)(bit % 32);
            var seen = p.ReadUInt32(w.Data, word);
            if ((seen & mask) != 0 || key == table || key == r.Header || key == next ||
                key == w.BitmapBlock || key == g.RootBlock || SearchFree(ref p, ref w, key, g.RootBlock))
                return Fail(ref w, DOS.Error.DiskNotValidated);
            p.WriteUInt32(w.Data, word, seen | mask);
        }

        // Keep the invalid marker durable while the two sets of blocks coexist.
        if (!p.Flush()) return Fail(ref w, DOS.Error.DiskNotValidated);
        for (var i = 0u; i < count; i++) Bit(ref p, ref w, destination + i, false);
        if (!FlushBitmap(ref p, ref w)) return false;
        if (w.DosType == 0x444F5300)
        {
            if (!WriteReplacementOfsData(ref p, ref w, ref r, destination, count)) return false;
        }
        else if (!p.WriteDataBlocks(destination, count, APTR.FromPointer(r.Buffer.Raw + actual)))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if (!p.Flush())
            return Fail(ref w, DOS.Error.DiskNotValidated);
        // Keep the old table in Meta until the replacement table is on disk.
        p.Copy(w.Meta, w.Data, g.BlockBytes);
        for (var i = 0u; i < count; i++) p.WriteUInt32(w.Data, at - (int)i * 4, destination + i);
        if (table == r.Header && slot == 0) p.WriteUInt32(w.Data, 16, destination);
        if (!Put(ref p, ref w, table, w.Data, 20)) return false;
        actual += w.DosType == 0x444F5300 ? r.Length : count * g.BlockBytes;
        if (!p.Flush()) return Fail(ref w, DOS.Error.DiskNotValidated);
        for (var i = 0u; i < count; i++) Bit(ref p, ref w, p.ReadUInt32(w.Meta, at - (int)i * 4), true);
        moved = true;
        return FlushBitmap(ref p, ref w) && (p.Flush() || Fail(ref w, DOS.Error.DiskNotValidated));
    }

    private static uint OverwriteRun<T>(ref T p, ref Work w, uint root, uint offset, uint limit, uint count)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var window = limit - offset;
        if (window > count + 128) window = count + 128;
        uint length = 0;
        for (var i = 0u; i < window; i++)
        {
            var key = w.BitmapFirst + offset + i;
            if (key == w.BitmapBlock || !SearchFree(ref p, ref w, key, root)) length = 0;
            else if (++length == count) return key - (count - 1);
        }
        return 0;
    }
}
