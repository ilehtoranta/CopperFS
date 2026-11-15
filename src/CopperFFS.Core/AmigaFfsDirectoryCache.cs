using Amiga;

namespace CopperStart.Dos;

public static partial class AmigaFfsWriteCore
{
    // DOS4/5 mirror canonical directory headers into chained T_DIRC blocks.
    // Rebuild only directories touched by the mutation, before publishing the
    // valid root. Reuse the three caller-owned blocks; no persistent index.
    private static bool CacheBlock<T>(ref T p, ref Work w, uint key, uint directory)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var g = AmigaFfsGeometry.Read(ref p);
        if (!Allocated(ref p, ref w, in g, key) || !p.ReadBlock(key, w.Meta) ||
            !AmigaDiskFileSystemCore.Checksum(ref p, w.Meta) || p.ReadUInt32(w.Meta) != 33 ||
            p.ReadUInt32(w.Meta, 4) != key || p.ReadUInt32(w.Meta, 8) != directory)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        var records = p.ReadUInt32(w.Meta, 12); var at = 24u;
        if (records > (g.BlockBytes - 24) / 26) return Fail(ref w, DOS.Error.DiskNotValidated);
        for (var i = 0u; i < records; i++)
        {
            if (at > g.BlockBytes - 26) return Fail(ref w, DOS.Error.DiskNotValidated);
            var header = p.ReadUInt32(w.Meta, (int)at);
            var name = p.ReadUInt8(w.Meta, (int)at + 23);
            if (header < g.ReservedBlocks || header >= g.BlockCount || name == 0 || name > 30 ||
                at + 25 + name > g.BlockBytes) return Fail(ref w, DOS.Error.DiskNotValidated);
            var comment = p.ReadUInt8(w.Meta, (int)at + 24 + name);
            if (comment > 79 || at + 25 + name + comment > g.BlockBytes) return Fail(ref w, DOS.Error.DiskNotValidated);
            at = (at + 26u + name + comment) & ~1u;
        }
        return true;
    }

    private static bool ReleaseCache<T>(ref T p, ref Work w, uint directory)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var tail = (int)p.BlockBytes - 512;
        if (!Header(ref p, ref w, directory)) return false;
        var first = p.ReadUInt32(w.Meta, tail + 504); var key = first;
        // Validate the complete old chain before changing allocation bits.
        for (var visited = 0u; key != 0; visited++)
        {
            if (visited >= p.BlockCount || !CacheBlock(ref p, ref w, key, directory)) return Fail(ref w, DOS.Error.DiskNotValidated);
            key = p.ReadUInt32(w.Meta, 16);
        }
        for (key = first; key != 0;)
        {
            if (!CacheBlock(ref p, ref w, key, directory)) return false;
            var next = p.ReadUInt32(w.Meta, 16); Bit(ref p, ref w, key, true); key = next;
            if (w.Error != DOS.Error.None) return false;
        }
        if (first == 0) return true;
        if (!Header(ref p, ref w, directory)) return false;
        p.WriteUInt32(w.Meta, tail + 504, 0);
        return Put(ref p, ref w, directory, w.Meta, 20);
    }

    private static void EmptyCache<T>(ref T p, ref Work w, uint key, uint directory)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        p.Clear(w.Data, p.BlockBytes); p.WriteUInt32(w.Data, 0, 33);
        p.WriteUInt32(w.Data, 4, key); p.WriteUInt32(w.Data, 8, directory);
    }

    private static bool RebuildCache<T>(ref T p, ref Work w, uint directory)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (directory == 0) return true;
        var g = AmigaFfsGeometry.Read(ref p);
        if (!Header(ref p, ref w, directory) || unchecked((int)p.ReadUInt32(w.Meta, g.Tail + 508)) <= 0)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if (!ReleaseCache(ref p, ref w, directory)) return false;
        w.NextKey = g.ReservedBlocks;
        var first = Allocate(ref p, ref w); if (first == 0) return false;
        var current = first; EmptyCache(ref p, ref w, current, directory);
        var at = 24u; var records = 0u; var visited = 0u;
        for (var slot = 0u; slot < g.TableEntries; slot++)
        {
            if (!Header(ref p, ref w, directory)) return false;
            var entry = p.ReadUInt32(w.Meta, 24 + (int)slot * 4);
            while (entry != 0)
            {
                if (visited++ >= g.BlockCount || !AmigaDiskFileSystemCore.Header(ref p, in g, entry, w.Meta, true) ||
                    p.ReadUInt32(w.Meta, g.Tail + 500) != directory) return Fail(ref w, DOS.Error.DiskNotValidated);
                var next = p.ReadUInt32(w.Meta, g.Tail + 496);
                var name = p.ReadUInt8(w.Meta, g.Tail + 432); var comment = p.ReadUInt8(w.Meta, g.Tail + 328);
                if (name == 0 || name > 30 || comment > 79) return Fail(ref w, DOS.Error.DiskNotValidated);
                var bytes = (26u + name + comment) & ~1u;
                if (bytes > g.BlockBytes - at)
                {
                    p.WriteUInt32(w.Data, 12, records);
                    if (!Put(ref p, ref w, current, w.Data, 20)) return false;
                    var following = Allocate(ref p, ref w); if (following == 0) return false;
                    // Allocation can use Data as bitmap-pointer scratch. Meta still owns the entry.
                    if (!p.ReadBlock(current, w.Data)) return Fail(ref w, DOS.Error.DiskNotValidated);
                    p.WriteUInt32(w.Data, 16, following);
                    if (!Put(ref p, ref w, current, w.Data, 20)) return false;
                    current = following; EmptyCache(ref p, ref w, current, directory); at = 24; records = 0;
                }
                var offset = (int)at; var type = unchecked((int)p.ReadUInt32(w.Meta, g.Tail + 508));
                p.WriteUInt32(w.Data, offset, entry);
                p.WriteUInt32(w.Data, offset + 4, type == -3 ? p.ReadUInt32(w.Meta, g.Tail + 324) : 0);
                p.WriteUInt32(w.Data, offset + 8, p.ReadUInt32(w.Meta, g.Tail + 320));
                p.WriteUInt32(w.Data, offset + 12, p.ReadUInt32(w.Meta, g.Tail + 316));
                p.WriteUInt16(w.Data, offset + 16, (ushort)p.ReadUInt32(w.Meta, g.Tail + 420));
                p.WriteUInt16(w.Data, offset + 18, (ushort)p.ReadUInt32(w.Meta, g.Tail + 424));
                p.WriteUInt16(w.Data, offset + 20, (ushort)p.ReadUInt32(w.Meta, g.Tail + 428));
                p.WriteUInt8(w.Data, offset + 22, unchecked((byte)type)); p.WriteUInt8(w.Data, offset + 23, name);
                p.Copy(APTR.FromPointer(w.Meta.Raw + (uint)g.Tail + 433), APTR.FromPointer(w.Data.Raw + at + 24), name);
                p.WriteUInt8(w.Data, offset + 24 + name, comment);
                p.Copy(APTR.FromPointer(w.Meta.Raw + (uint)g.Tail + 329), APTR.FromPointer(w.Data.Raw + at + 25 + name), comment);
                records++; at += bytes; entry = next;
            }
        }
        p.WriteUInt32(w.Data, 12, records);
        if (!Put(ref p, ref w, current, w.Data, 20) || !Header(ref p, ref w, directory)) return false;
        p.WriteUInt32(w.Meta, g.Tail + 504, first);
        return Put(ref p, ref w, directory, w.Meta, 20);
    }
}
