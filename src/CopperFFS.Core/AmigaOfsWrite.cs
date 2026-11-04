using Amiga;

namespace CopperStart.Dos;

public static partial class AmigaFfsWriteCore
{
    // OFS has two representations of a file's block order: reverse pointer
    // tables and the forward data chain. Validate both before modifying either.
    // Use the existing three scratch blocks, never a volume-sized key array.
    private static bool OfsChain<T>(ref T p, ref Work w, uint header, bool release,
        out uint lastData) where T : struct, IAmigaWritableFileSystemPlatform
    {
        lastData = 0;
        var g = p.Geometry; var payload = g.BlockBytes - 24;
        if (!Header(ref p, ref w, header) || p.ReadUInt32(w.Meta, g.Tail + 508) != 0xFFFFFFFD)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        var remaining = p.ReadUInt32(w.Meta, g.Tail + 324);
        var blocks = remaining == 0 ? 0 : AmigaGeometryMath.Divide(remaining - 1, payload) + 1;
        if (blocks > g.BlockCount - g.ReservedBlocks) return Fail(ref w, DOS.Error.DiskNotValidated);
        var expected = p.ReadUInt32(w.Meta, 16); var table = header; var index = 0u;
        for (var guard = 0u; guard < g.BlockCount; guard++)
        {
            if (!Allocated(ref p, ref w, ref g, table) || !p.ReadBlock(table, w.Meta) ||
                !AmigaDiskFileSystemCore.Checksum(ref p, w.Meta) || p.ReadUInt32(w.Meta, 4) != table ||
                p.ReadUInt32(w.Meta) != (guard == 0 ? 2u : 16u) ||
                p.ReadUInt32(w.Meta, g.Tail + 508) != 0xFFFFFFFD ||
                (guard != 0 && p.ReadUInt32(w.Meta, g.Tail + 500) != header))
                return Fail(ref w, DOS.Error.DiskNotValidated);
            var count = blocks - index; if (count > g.TableEntries) count = g.TableEntries;
            if (p.ReadUInt32(w.Meta, 8) != count) return Fail(ref w, DOS.Error.DiskNotValidated);
            for (var i = 0u; i < count; i++)
            {
                var key = p.ReadUInt32(w.Meta, g.Tail + 308 - (int)i * 4);
                var bytes = remaining > payload ? payload : remaining;
                if (key != expected || key == header || key == table ||
                    !Allocated(ref p, ref w, ref g, key) || !p.ReadBlock(key, w.Data) ||
                    !AmigaDiskFileSystemCore.Checksum(ref p, w.Data) || p.ReadUInt32(w.Data) != 8 ||
                    p.ReadUInt32(w.Data, 4) != header || p.ReadUInt32(w.Data, 8) != index + 1 ||
                    p.ReadUInt32(w.Data, 12) != bytes) return Fail(ref w, DOS.Error.DiskNotValidated);
                expected = p.ReadUInt32(w.Data, 16); lastData = key;
                index++; remaining -= bytes;
                if (release) Bit(ref p, ref w, key, true);
                if (w.Error != DOS.Error.None) return false;
            }
            var next = p.ReadUInt32(w.Meta, g.Tail + 504);
            if (guard != 0 && release) Bit(ref p, ref w, table, true);
            if (w.Error != DOS.Error.None) return false;
            if (index == blocks)
                return (next == 0 && expected == 0) || Fail(ref w, DOS.Error.DiskNotValidated);
            if (next == 0 || next == header) return Fail(ref w, DOS.Error.DiskNotValidated);
            table = next;
        }
        return Fail(ref w, DOS.Error.DiskNotValidated);
    }

    // Called after Prepare has selected DOS0. FFS retains its existing writer.
    private static bool WriteOfs<T>(ref T p, ref Work w, ref Request r, out uint actual)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        actual = 0; var g = p.Geometry; var payload = g.BlockBytes - 24;
        if (!Header(ref p, ref w, r.Header)) return false;
        if (p.ReadUInt32(w.Meta, g.Tail + 508) != 0xFFFFFFFD) return Fail(ref w, DOS.Error.ObjectWrongType);
        if ((p.ReadUInt32(w.Meta, g.Tail + 320) & 4) != 0) return Fail(ref w, DOS.Error.WriteProtected);
        var size = p.ReadUInt32(w.Meta, g.Tail + 324);
        if (r.Position > size) return Fail(ref w, DOS.Error.SeekError);
        if (r.Length > uint.MaxValue - r.Position) return Fail(ref w, DOS.Error.DiskFull);
        if (r.Length == 0) return true;
        var end = r.Position + r.Length; var newSize = end > size ? end : size;
        var oldBlocks = size == 0 ? 0 : AmigaGeometryMath.Divide(size - 1, payload) + 1;
        if (!OfsChain(ref p, ref w, r.Header, false, out var previousData)) return false;
        if (end > size)
        {
            var newBlocks = AmigaGeometryMath.Divide(end - 1, payload) + 1;
            var oldExt = oldBlocks == 0 ? 0 : AmigaGeometryMath.Divide(oldBlocks - 1, g.TableEntries);
            var newExt = AmigaGeometryMath.Divide(newBlocks - 1, g.TableEntries);
            if (!Prepare(ref p, ref w, newBlocks - oldBlocks + newExt - oldExt)) return false;
        }
        if (!Begin(ref p, ref w, ref g)) return false;
        var table = r.Header; var tableIndex = 0u;
        while (actual < r.Length)
        {
            var position = r.Position + actual; var index = AmigaGeometryMath.Divide(position, payload);
            var targetTable = AmigaGeometryMath.Divide(index, g.TableEntries);
            while (tableIndex < targetTable)
            {
                if (!p.ReadBlock(table, w.Meta)) return Fail(ref w, DOS.Error.DiskNotValidated);
                var next = p.ReadUInt32(w.Meta, g.Tail + 504);
                if (next == 0)
                {
                    next = Allocate(ref p, ref w); if (next == 0) return false;
                    p.WriteUInt32(w.Meta, g.Tail + 504, next);
                    if (!Put(ref p, ref w, table, w.Meta, 20)) return false;
                    p.Clear(w.Meta, g.BlockBytes); p.WriteUInt32(w.Meta, 0, 16); p.WriteUInt32(w.Meta, 4, next);
                    p.WriteUInt32(w.Meta, g.Tail + 500, r.Header); p.WriteUInt32(w.Meta, g.Tail + 508, 0xFFFFFFFD);
                    if (!Put(ref p, ref w, next, w.Meta, 20)) return false;
                }
                table = next; tableIndex++;
            }
            if (!p.ReadBlock(table, w.Meta)) return Fail(ref w, DOS.Error.DiskNotValidated);
            var at = g.Tail + 308 - (int)AmigaGeometryMath.Remainder(index, g.TableEntries) * 4;
            var key = p.ReadUInt32(w.Meta, at); var allocate = index >= oldBlocks;
            if (allocate)
            {
                key = Allocate(ref p, ref w); if (key == 0) return false;
                p.Clear(w.Data, g.BlockBytes); p.WriteUInt32(w.Data, 0, 8);
                p.WriteUInt32(w.Data, 4, r.Header); p.WriteUInt32(w.Data, 8, index + 1);
            }
            else if (!p.ReadBlock(key, w.Data)) return Fail(ref w, DOS.Error.DiskNotValidated);
            var within = AmigaGeometryMath.Remainder(position, payload); var count = payload - within;
            if (count > r.Length - actual) count = r.Length - actual;
            for (var i = 0u; i < count; i++)
                p.WriteUInt8(w.Data, 24 + (int)(within + i), p.ReadUInt8(r.Buffer, (int)(actual + i)));
            var dataBytes = newSize - index * payload; if (dataBytes > payload) dataBytes = payload;
            p.WriteUInt32(w.Data, 12, dataBytes);
            if (!Put(ref p, ref w, key, w.Data, 20)) return false;
            if (allocate)
            {
                // Publish the new data only after its header, payload and checksum
                // have been written. Any later failure leaves the root invalid.
                if (previousData != 0)
                {
                    if (!p.ReadBlock(previousData, w.Data)) return Fail(ref w, DOS.Error.DiskNotValidated);
                    p.WriteUInt32(w.Data, 16, key);
                    if (!Put(ref p, ref w, previousData, w.Data, 20)) return false;
                }
                previousData = key;
                p.WriteUInt32(w.Meta, at, key); p.WriteUInt32(w.Meta, 8, AmigaGeometryMath.Remainder(index, g.TableEntries) + 1);
                if (index == 0) p.WriteUInt32(w.Meta, 16, key);
                if (!Put(ref p, ref w, table, w.Meta, 20)) return false;
            }
            actual += count;
        }
        if (!Header(ref p, ref w, r.Header)) return false;
        p.WriteUInt32(w.Meta, g.Tail + 324, newSize);
        return Put(ref p, ref w, r.Header, w.Meta, 20) && Finish(ref p, ref w, ref g);
    }
}
