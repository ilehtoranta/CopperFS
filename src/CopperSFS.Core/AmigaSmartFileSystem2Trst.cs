using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystem2WriteCore
{
    public enum TrstOperation
    {
        Write, CreateFile, CreateDirectory, Remove, Rename, Resize, Truncate,
        SetProtection, SetComment, SetDate
    }

    /// <summary>
    /// Native SFS redo transaction. Stages changed reachable blocks, including
    /// overwritten file data, in caller-owned memory. Contiguous data proven free
    /// in the original bitmap may be written unpublished; the journal barrier
    /// makes it durable before TRFA publication. A 16-byte header precedes slots
    /// of BlockBytes + 4 bytes.
    /// Buffer exhaustion fails before publication without changing live blocks.
    /// Request fields match the ordinary APIs; Resize takes its size in Position.
    /// After a failed commit, Recover is required; a published transaction rolls
    /// forward even if this call reports failure and zero actual bytes.
    /// The caller serializes all volume access until completion/recovery.
    /// </summary>
    public static bool ExecuteTrst<T>(ref T p, ref Work w, ref Request r,
        TrstOperation operation, APTR stagingBuffer, uint stagingBytes, out uint actual)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        actual = 0;
        var bytes = p.BlockBytes;
        w.Error = DOS.Error.None;
        // A native operation's compressed payload length is a UWORD. Bound the
        // worst case (one literal-run code per 64 longs) before doing any I/O.
        if (bytes < 512 || bytes > 32768 || operation < TrstOperation.Write || operation > TrstOperation.SetDate)
            return Fail(ref w, DOS.Error.BadNumber);
        if (stagingBuffer.IsNull || stagingBytes < bytes + 20 || !p.IsMapped(stagingBuffer, stagingBytes) ||
            w.Meta.IsNull || !p.IsMapped(w.Meta, bytes) || w.Data.IsNull || !p.IsMapped(w.Data, bytes) ||
            w.Bitmap.IsNull || !p.IsMapped(w.Bitmap, bytes) || w.Journal.IsNull || !p.IsMapped(w.Journal, bytes) ||
            Overlap(stagingBuffer, stagingBytes, r.Buffer, r.Length) ||
            Overlap(stagingBuffer, stagingBytes, r.Name, r.Length) ||
            Overlap(stagingBuffer, stagingBytes, w.Meta, bytes) ||
            Overlap(stagingBuffer, stagingBytes, w.Data, bytes) ||
            Overlap(stagingBuffer, stagingBytes, w.Bitmap, bytes) ||
            Overlap(stagingBuffer, stagingBytes, w.Journal, bytes) ||
            // SetDate reads twelve bytes regardless of Request.Length.
            (operation == TrstOperation.SetDate && Overlap(stagingBuffer, stagingBytes, r.Buffer, 12)))
            return Fail(ref w, DOS.Error.NoFreeStore);
        if (p.WriteProtected()) return Fail(ref w, DOS.Error.DiskWriteProtected);
        // Legacy CSUN and TRST recovery must reach the real device, never the overlay.
        if (!Recover(ref p, ref w)) return false;
        if (p.RedoOverlay.IsNotNull) return Fail(ref w, DOS.Error.NoFreeStore);
        var staged = new RedoPlatform<T> { Inner = p, Buffer = APTR.FromPointer(stagingBuffer.Raw + 16), Capacity = (stagingBytes - 16) / (bytes + 4) };
        p.WriteUInt32(stagingBuffer, 0, 0); p.WriteUInt32(stagingBuffer, 4, staged.Capacity);
        p.WriteUInt32(stagingBuffer, 8, bytes); p.WriteUInt32(stagingBuffer, 12, 0);
        p.RedoOverlay = stagingBuffer;
        if (p.RedoOverlay != stagingBuffer) return Fail(ref w, DOS.Error.ActionNotKnown);
        w.RedoStaging = true;
        var success = StageTrst(ref p, ref w, ref r, operation, out var written);
        p.RedoOverlay = APTR.Null;
        staged.Inner = p;
        staged.Count = p.ReadUInt32(stagingBuffer, 0);
        staged.Full = p.ReadUInt32(stagingBuffer, 12) != 0;
        w.RedoStaging = false;
        if (staged.Full) return Fail(ref w, DOS.Error.NoFreeStore);
        if (!success || !CommitTrst(ref p, ref w, ref staged)) return false;
        actual = written;
        return true;
    }

    private static bool StageTrst<T>(ref T p, ref Work w, ref Request r, TrstOperation operation, out uint written)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        written = 0;
        return operation switch
        {
            TrstOperation.Write => Write(ref p, ref w, ref r, out written),
            TrstOperation.CreateFile => Create(ref p, ref w, ref r, false),
            TrstOperation.CreateDirectory => Create(ref p, ref w, ref r, true),
            TrstOperation.Remove => Remove(ref p, ref w, r.Node),
            TrstOperation.Rename => Rename(ref p, ref w, ref r),
            TrstOperation.Resize => Resize(ref p, ref w, r.Node, r.Position),
            TrstOperation.Truncate => Truncate(ref p, ref w, r.Node),
            TrstOperation.SetProtection => SetMetadata(ref p, ref w, ref r, DosPacketAction.SetProtection),
            TrstOperation.SetComment => SetMetadata(ref p, ref w, ref r, DosPacketAction.SetComment),
            TrstOperation.SetDate => SetMetadata(ref p, ref w, ref r, DosPacketAction.SetDate),
            _ => false
        };
    }

    // Native platforms own the overlay; this descriptor never becomes a
    // generic platform argument (the native compiler uses four-byte platforms).
    private struct RedoPlatform<T> where T : struct, IAmigaWritableFileSystemPlatform
    {
        internal T Inner;
        internal APTR Buffer, OwnedArena;
        internal uint Capacity, Count;
        internal bool Full;
        internal APTR Slot(uint index) => OwnedArena.IsNotNull ? Sfs2MetadataStore.Slot(ref Inner, OwnedArena, index) : APTR.FromPointer(Buffer.Raw + index * (Inner.BlockBytes + 4));
        internal APTR Image(uint index) => APTR.FromPointer(Slot(index).Raw + (OwnedArena.IsNotNull ? Sfs2MetadataStore.SlotBytes : 4));
        internal bool Find(uint block, out APTR image)
        {
            if (OwnedArena.IsNotNull) { image = Sfs2MetadataStore.Find(ref Inner, OwnedArena, block); return image.IsNotNull; }
            for (var i = 0u; i < Count; i++)
                if ((Inner.ReadUInt32(Slot(i)) & 0x7FFFFFFF) == block) { image = Image(i); return true; }
            image = APTR.Null; return false;
        }
    }

    private struct RedoStream
    {
        internal uint First, Block, Offset, Search, Bitmap;
    }

    private static bool TrstSpace<T>(ref T p, ref Work w, ref RedoPlatform<T> staged,
        ref RedoStream stream, out uint block) where T : struct, IAmigaWritableFileSystemPlatform
    {
        block = 0;
        // Journal blocks must be free in BOTH the real and final bitmap, and
        // must not be staged targets, even when a target is subsequently freed.
        // Monotonic search also keeps journal blocks distinct without a heap list.
        while (stream.Search < p.BlockCount - 1)
        {
            var candidate = stream.Search++;
            if (!BitmapPosition(ref p, ref w, candidate, out var map, out var offset, out var mask)) return false;
            // Only journal allocation uses Meta during emission. Intersect the
            // original/final map once; neither image can change under this owner.
            if (stream.Bitmap != map)
            {
                if (!Read(ref p, ref w, map, BitmapId, w.Meta)) return false;
                if (staged.Find(map, out var finalMap))
                    for (var at = 12; at < p.BlockBytes; at += 4)
                        p.WriteUInt32(w.Meta, at, p.ReadUInt32(w.Meta, at) & p.ReadUInt32(finalMap, at));
                stream.Bitmap = map;
            }
            // Skip occupied words, and inspect their remaining bits locally.
            // Bitmap pages contain a whole number of words, so this cannot
            // cross the validated map. Avoid geometry/division for every bit.
            var free = p.ReadUInt32(w.Meta, offset) & (uint.MaxValue >> (int)(candidate & 31));
            if (free == 0)
            {
                stream.Search = candidate + 32 - (candidate & 31);
                continue;
            }
            while ((free & mask) == 0) { candidate++; mask >>= 1; }
            stream.Search = candidate + 1;
            if (candidate >= p.BlockCount - 1) break;
            if (candidate == w.RootContainer + 2 || staged.Find(candidate, out _)) continue;
            block = candidate; return true;
        }
        return Fail(ref w, DOS.Error.DiskFull);
    }

    private static void TrstPage<T>(ref T p, ref Work w, ref RedoStream stream, uint block)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        p.Clear(w.Journal, p.BlockBytes);
        p.WriteUInt32(w.Journal, 0, NativeRedoId); p.WriteUInt32(w.Journal, 8, block);
        stream.Block = block; stream.Offset = 16;
    }

    private static bool TrstByte<T>(ref T p, ref Work w, ref RedoPlatform<T> staged,
        ref RedoStream stream, byte value) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (stream.Offset == p.BlockBytes)
        {
            // A tentative marker page has not reached the device. Promote it
            // before emitting anything if the complete log does not fit.
            if (stream.First == w.RootContainer + 2)
            {
                if (!TrstSpace(ref p, ref w, ref staged, ref stream, out var first)) return false;
                stream.First = stream.Block = first;
                p.WriteUInt32(w.Journal, 8, first);
            }
            if (!TrstSpace(ref p, ref w, ref staged, ref stream, out var next)) return false;
            p.WriteUInt32(w.Journal, 12, next); Seal(ref p, w.Journal);
            if (!p.WriteBlock(stream.Block, w.Journal)) return Fail(ref w, DOS.Error.DiskNotValidated);
            TrstPage(ref p, ref w, ref stream, next);
        }
        p.WriteUInt8(w.Journal, (int)stream.Offset++, value); return true;
    }

    // The shared codec classifies each long once into caller-owned Bitmap
    // scratch. Allocation uses Meta and cannot overwrite this emission plan.
    private static bool TrstEmit<T>(ref T p, ref Work w, ref RedoPlatform<T> staged,
        ref RedoStream stream, APTR image, uint runs, uint length)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var bytes = p.BlockBytes;
        if (stream.Offset <= bytes && length <= bytes - stream.Offset)
        {
            var source = image.Raw;
            var destination = w.Journal.Raw + stream.Offset;
            for (var run = 0u; run < runs; run++)
            {
                var code = p.ReadUInt8(w.Bitmap, (int)run);
                var span = ((uint)(code & 63) + 1) * 4;
                p.WriteUInt8(APTR.FromPointer(destination++), 0, code);
                if ((code >> 6) == 3)
                {
                    p.Copy(APTR.FromPointer(source), APTR.FromPointer(destination), span);
                    destination += span;
                }
                source += span;
            }
            stream.Offset += length;
            return true;
        }
        var pointer = image.Raw;
        for (var run = 0u; run < runs; run++)
        {
            var code = p.ReadUInt8(w.Bitmap, (int)run);
            var span = ((uint)(code & 63) + 1) * 4;
            if (!TrstByte(ref p, ref w, ref staged, ref stream, code)) return false;
            if ((code >> 6) == 3)
            {
                var remaining = span;
                while (remaining != 0)
                {
                    if (stream.Offset == bytes)
                    {
                        if (!TrstByte(ref p, ref w, ref staged, ref stream, p.ReadUInt8(APTR.FromPointer(pointer)))) return false;
                        pointer++; remaining--; continue;
                    }
                    var take = bytes - stream.Offset;
                    if (take > remaining) take = remaining;
                    p.Copy(APTR.FromPointer(pointer), APTR.FromPointer(w.Journal.Raw + stream.Offset), take);
                    stream.Offset += take; pointer += take; remaining -= take;
                }
            }
            else pointer += span;
        }
        return true;
    }

    private static bool CommitTrst<T>(ref T p, ref Work w, ref RedoPlatform<T> staged)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (staged.Count == 0) return true;
        var stream = new RedoStream { Search = p.ReservedBlocks, Bitmap = uint.MaxValue };
        for (var i = 0u; i < staged.Count; i++)
        {
            var target = p.ReadUInt32(staged.Slot(i)); var image = staged.Image(i);
            // The owned clean cache still holds the physical before-image.
            // Providers invalidate its key before queueing any replacement, so
            // this cannot bypass a newer pending physical write. Plan consumes
            // it before emission can replace or invalidate a cache image.
            var cache = staged.OwnedArena.IsNotNull ? Sfs2MountContext.Cache(ref p, p.Sfs2Context) : APTR.Null;
            var original = cache.IsNotNull ? Sfs2MetadataStore.Find(ref p, cache, target) : APTR.Null;
            if (original.IsNull) original = p.ReadRetainedBlock(target);
            if (original.IsNull)
            {
                if (!p.ReadBlock(target, w.Data)) return Fail(ref w, DOS.Error.DiskNotValidated);
                original = w.Data;
            }
            var runs = AmigaSmartFileSystemTrstCodec.Plan(ref p, original, image, p.BlockBytes, w.Bitmap, out var length);
            var unchanged = true;
            for (var run = 0u; run < runs; run++)
                if ((p.ReadUInt8(w.Bitmap, (int)run) & 0xC0) != 0x40) { unchanged = false; break; }
            if (unchanged)
            {
                // The high bit is outside the supported block-number domain.
                // Keep the target reserved during journal allocation, but omit
                // an identical image from both the journal and home writes.
                p.WriteUInt32(staged.Slot(i), 0, target | 0x80000000);
                continue;
            }
            if (stream.First == 0)
            {
                var first = w.RootContainer + 2;
                // Keep the checked writer as the external-journal reference.
                // The owned profile can use original SFS's atomic single-sector
                // publication; multi-sector blocks retain TRFA publication.
                if ((staged.OwnedArena.IsNull || p.BlockBytes != 512 || p.Geometry.SectorBytes != 512) &&
                    !TrstSpace(ref p, ref w, ref staged, ref stream, out first)) return false;
                stream.First = first; TrstPage(ref p, ref w, ref stream, first);
            }
            // Record starts are word aligned. Emit an in-page header directly;
            // retain byte streaming for a header crossing a page boundary.
            if (stream.Offset <= p.BlockBytes - 7)
            {
                var at = (int)stream.Offset;
                p.WriteUInt16(w.Journal, at, (ushort)length);
                p.WriteUInt32(w.Journal, at + 2, target); p.WriteUInt8(w.Journal, at + 6, 0);
                stream.Offset += 7;
            }
            else
            {
                if (!TrstByte(ref p, ref w, ref staged, ref stream, (byte)(length >> 8)) ||
                    !TrstByte(ref p, ref w, ref staged, ref stream, (byte)length)) return false;
                for (var shift = 24; shift >= 0; shift -= 8)
                    if (!TrstByte(ref p, ref w, ref staged, ref stream, (byte)(target >> shift))) return false;
                if (!TrstByte(ref p, ref w, ref staged, ref stream, 0)) return false;
            }
            if (!TrstEmit(ref p, ref w, ref staged, ref stream, image, runs, length)) return false;
            // Native OperationInformation uses 68k word alignment (7-byte header).
            if ((length & 1) == 0 && !TrstByte(ref p, ref w, ref staged, ref stream, 0)) return false;
        }
        if (stream.First == 0) return true;
        // At an exact page boundary, end-of-chain terminates the stream. Otherwise
        // the zero-filled tail supplies the zero length (records are word aligned).
        Seal(ref p, w.Journal);
        var marker = w.RootContainer + 2;
        if (stream.First == marker)
        {
            // This write publishes the log. Flush unpublished data first, even
            // though the native platform can elide an already-clean flush.
            // Together with the home and clean barriers below all four stages
            // remain ordered. No additional staging or persistent state.
            if (!p.Flush() || !p.WriteBlock(marker, w.Journal) || !p.Flush())
                return Fail(ref w, DOS.Error.DiskNotValidated);
        }
        else
        {
            if (!p.WriteBlock(stream.Block, w.Journal) || !p.Flush()) return Fail(ref w, DOS.Error.DiskNotValidated);
            p.Clear(w.Meta, p.BlockBytes);
            p.WriteUInt32(w.Meta, 0, TransactionFailed); p.WriteUInt32(w.Meta, 8, marker);
            p.WriteUInt32(w.Meta, 12, stream.First); Seal(ref p, w.Meta);
            if (!p.WriteBlock(marker, w.Meta) || !p.Flush()) return Fail(ref w, DOS.Error.DiskNotValidated);
        }
        // These owned final images are exactly what the durable journal encodes.
        // Apply them directly on a healthy commit; interrupted mounts still use
        // the complete validation/decode path in ReplayNative. All barriers and
        // marker transitions retain the existing TRST recovery protocol.
        for (var i = 0u; i < staged.Count; i++)
        {
            var target = p.ReadUInt32(staged.Slot(i));
            if ((target & 0x80000000) != 0) continue;
            if (!p.WriteBlock(target, staged.Image(i))) return Fail(ref w, DOS.Error.DiskNotValidated);
        }
        if (!p.Flush()) return Fail(ref w, DOS.Error.DiskNotValidated);
        p.Clear(w.Meta, p.BlockBytes);
        p.WriteUInt32(w.Meta, 0, TransactionOk); p.WriteUInt32(w.Meta, 8, marker);
        Seal(ref p, w.Meta);
        return (p.WriteBlock(marker, w.Meta) && p.Flush()) || Fail(ref w, DOS.Error.DiskNotValidated);
    }
}
