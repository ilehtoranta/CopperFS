using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystemWriteCore
{
    public enum TrstOperation
    {
        Write, CreateFile, CreateDirectory, Remove, Rename, Resize, Truncate,
        SetProtection, SetComment, SetDate
    }

    /// <summary>
    /// Opt-in native SFS redo transaction. Stages ALL changed blocks (including file
    /// data) in caller-owned memory, including its bounded block lookup index.
    /// Each distinct block uses BlockBytes + 4 bytes after the header and index.
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
        var bytes = p.Geometry.BlockBytes;
        w.Error = DOS.Error.None;
        // A native operation's compressed payload length is a UWORD. Bound the
        // worst case (one literal-run code per 64 longs) before doing any I/O.
        if (bytes < 512 || bytes > 32768 || operation < TrstOperation.Write || operation > TrstOperation.SetDate)
            return Fail(ref w, DOS.Error.BadNumber);
        if (stagingBuffer.IsNull || stagingBytes < bytes + 36 || !p.IsMapped(stagingBuffer, stagingBytes) ||
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
        // Switching journal policy ends CSUN streams before staging allocations.
        ReleaseOverwriteReservation(ref p, ref w);
        // Legacy CSUN and TRST recovery must reach the real device, never the overlay.
        if (!Recover(ref p, ref w)) return false;
        if (!AmigaSmartFileSystemRedoArena.Initialize(ref p, stagingBuffer, stagingBytes, bytes))
            return Fail(ref w, DOS.Error.NoFreeStore);
        var staged = new RedoPlatform<T> { Inner = p, Arena = stagingBuffer,
            Buffer = APTR.FromPointer(p.ReadUInt32(stagingBuffer, 20)), Stride = bytes + 4 };
        w.RedoStaging = true;
        uint written = 0;
        var success = operation switch
        {
            TrstOperation.Write => Write(ref staged, ref w, ref r, out written),
            TrstOperation.CreateFile => Create(ref staged, ref w, ref r, false),
            TrstOperation.CreateDirectory => Create(ref staged, ref w, ref r, true),
            TrstOperation.Remove => Remove(ref staged, ref w, r.Node),
            TrstOperation.Rename => Rename(ref staged, ref w, ref r),
            TrstOperation.Resize => Resize(ref staged, ref w, r.Node, r.Position),
            TrstOperation.Truncate => Truncate(ref staged, ref w, r.Node),
            TrstOperation.SetProtection => SetMetadata(ref staged, ref w, ref r, DosPacketAction.SetProtection),
            TrstOperation.SetComment => SetMetadata(ref staged, ref w, ref r, DosPacketAction.SetComment),
            TrstOperation.SetDate => SetMetadata(ref staged, ref w, ref r, DosPacketAction.SetDate),
            _ => false
        };
        p = staged.Inner;
        staged.Count = p.ReadUInt32(stagingBuffer);
        staged.Full = p.ReadUInt32(stagingBuffer, 12) != 0;
        w.RedoStaging = false;
        if (staged.Full) return Fail(ref w, DOS.Error.NoFreeStore);
        if (!success || !CommitTrst(ref p, ref w, ref staged)) return false;
        actual = written;
        return true;
    }

    // A bounded, coalescing block overlay. Reads observe prior staged writes;
    // neither writes nor flushes reach the underlying device while staging.
    private struct RedoPlatform<T> : IAmigaWritableFileSystemPlatform
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        internal T Inner;
        internal APTR Buffer, Arena;
        internal uint Stride, Count;
        internal bool Full;
        public AmigaVolumeGeometry Geometry => Inner.Geometry;
        internal APTR Slot(uint index) => APTR.FromPointer(Buffer.Raw + index * Stride);
        internal APTR Image(uint index) => APTR.FromPointer(Slot(index).Raw + 4);
        internal bool Find(uint block, out APTR image)
        {
            image = AmigaSmartFileSystemRedoArena.Find(ref Inner, Arena, block);
            return image.IsNotNull;
        }
        public bool ReadBlock(uint block, APTR destination)
        {
            if (!Find(block, out var image)) return Inner.ReadBlock(block, destination);
            Inner.Copy(image, destination, Stride - 4); return true;
        }
        public bool WriteBlock(uint block, APTR source)
            => AmigaSmartFileSystemRedoArena.Store(ref Inner, Arena, block, source);
        public bool Flush() => true;
        public bool WriteProtected() => Inner.WriteProtected();
        public byte ReadUInt8(APTR a, int offset = 0) => Inner.ReadUInt8(a, offset);
        public ushort ReadUInt16(APTR a, int offset = 0) => Inner.ReadUInt16(a, offset);
        public uint ReadUInt32(APTR a, int offset = 0) => Inner.ReadUInt32(a, offset);
        public void WriteUInt8(APTR a, int offset, byte value) => Inner.WriteUInt8(a, offset, value);
        public void WriteUInt16(APTR a, int offset, ushort value) => Inner.WriteUInt16(a, offset, value);
        public void WriteUInt32(APTR a, int offset, uint value) => Inner.WriteUInt32(a, offset, value);
        public void Clear(APTR a, uint length) => Inner.Clear(a, length);
        public void Copy(APTR a, APTR b, uint length) => Inner.Copy(a, b, length);
        public bool IsMapped(APTR a, uint length) => Inner.IsMapped(a, length);
    }

    private struct RedoStream
    {
        internal uint First, Block, Offset, Search;
    }

    private static bool TrstSpace<T>(ref T p, ref Work w, ref RedoPlatform<T> staged,
        ref RedoStream stream, out uint block) where T : struct, IAmigaWritableFileSystemPlatform
    {
        block = 0;
        // Journal blocks must be free in BOTH the real and final bitmap, and
        // must not be staged targets, even when a target is subsequently freed.
        // Monotonic search also keeps journal blocks distinct without a heap list.
        while (stream.Search < p.Geometry.BlockCount - 1)
        {
            var candidate = stream.Search++;
            if (OverwriteReserved(ref p, ref w, candidate)) continue;
            if (candidate == w.RootContainer + 2 || staged.Find(candidate, out _)) continue;
            if (!BitmapPosition(ref p, ref w, candidate, out var map, out var offset, out var mask) ||
                !Read(ref p, ref w, map, BitmapId, w.Meta)) return false;
            if ((p.ReadUInt32(w.Meta, offset) & mask) == 0) continue;
            if (staged.Find(map, out var finalMap))
            {
                if ((p.ReadUInt32(finalMap, offset) & mask) == 0) continue;
            }
            block = candidate; return true;
        }
        return Fail(ref w, DOS.Error.DiskFull);
    }

    private static void TrstPage<T>(ref T p, ref Work w, ref RedoStream stream, uint block)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        p.Clear(w.Journal, p.Geometry.BlockBytes);
        p.WriteUInt32(w.Journal, 0, NativeRedoId); p.WriteUInt32(w.Journal, 8, block);
        stream.Block = block; stream.Offset = 16;
    }

    private static bool TrstByte<T>(ref T p, ref Work w, ref RedoPlatform<T> staged,
        ref RedoStream stream, byte value) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (stream.Offset == p.Geometry.BlockBytes)
        {
            if (!TrstSpace(ref p, ref w, ref staged, ref stream, out var next)) return false;
            p.WriteUInt32(w.Journal, 12, next); Seal(ref p, w.Journal);
            if (!p.WriteBlock(stream.Block, w.Journal)) return Fail(ref w, DOS.Error.DiskNotValidated);
            TrstPage(ref p, ref w, ref stream, next);
        }
        p.WriteUInt8(w.Journal, (int)stream.Offset++, value); return true;
    }

    // The run plan occupies at most one byte per source longword in the
    // existing Bitmap scratch. Journal allocation uses Meta, so page crossings
    // cannot clobber the plan. Old/final words are never classified a second time.
    private static bool TrstEmit<T>(ref T p, ref Work w, ref RedoPlatform<T> staged,
        ref RedoStream stream, APTR image, uint runs)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var pointer = image.Raw;
        for (var run = 0u; run < runs; run++)
        {
            var code = p.ReadUInt8(w.Bitmap, (int)run);
            var bytes = ((uint)(code & 63) + 1) * 4;
            if (!TrstByte(ref p, ref w, ref staged, ref stream, code)) return false;
            if ((code >> 6) == 3 && !TrstLiteral(ref p, ref w, ref staged, ref stream,
                APTR.FromPointer(pointer), bytes)) return false;
            pointer += bytes;
        }
        return true;
    }

    // Copy bounded literal spans, including odd addresses and page crossings.
    private static bool TrstLiteral<T>(ref T p, ref Work w, ref RedoPlatform<T> staged,
        ref RedoStream stream, APTR source, uint bytes) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var pointer = source.Raw;
        while (bytes != 0)
        {
            if (stream.Offset == p.Geometry.BlockBytes)
            {
                if (!TrstByte(ref p, ref w, ref staged, ref stream, p.ReadUInt8(APTR.FromPointer(pointer)))) return false;
                pointer++; bytes--;
                continue;
            }
            var take = p.Geometry.BlockBytes - stream.Offset;
            if (take > bytes) take = bytes;
            p.Copy(APTR.FromPointer(pointer), APTR.FromPointer(w.Journal.Raw + stream.Offset), take);
            stream.Offset += take; pointer += take; bytes -= take;
        }
        return true;
    }

    private static bool CommitTrst<T>(ref T p, ref Work w, ref RedoPlatform<T> staged)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (staged.Count == 0) return true;
        var stream = new RedoStream { Search = p.Geometry.ReservedBlocks };
        if (!TrstSpace(ref p, ref w, ref staged, ref stream, out var first)) return false;
        stream.First = first; TrstPage(ref p, ref w, ref stream, first);
        for (var i = 0u; i < staged.Count; i++)
        {
            var target = p.ReadUInt32(staged.Slot(i)); var image = staged.Image(i);
            if (!p.ReadBlock(target, w.Data)) return Fail(ref w, DOS.Error.DiskNotValidated);
            var runs = AmigaSmartFileSystemTrstCodec.Plan(ref p, w.Data, image, staged.Stride - 4, w.Bitmap, out var length);
            if (!TrstByte(ref p, ref w, ref staged, ref stream, (byte)(length >> 8)) ||
                !TrstByte(ref p, ref w, ref staged, ref stream, (byte)length)) return false;
            for (var shift = 24; shift >= 0; shift -= 8)
                if (!TrstByte(ref p, ref w, ref staged, ref stream, (byte)(target >> shift))) return false;
            if (!TrstByte(ref p, ref w, ref staged, ref stream, 0) ||
                !TrstEmit(ref p, ref w, ref staged, ref stream, image, runs)) return false;
            // Native OperationInformation uses 68k word alignment (7-byte header).
            if ((length & 1) == 0 && !TrstByte(ref p, ref w, ref staged, ref stream, 0)) return false;
        }
        // At an exact page boundary, end-of-chain terminates the stream. Otherwise
        // the zero-filled tail supplies the zero length (records are word aligned).
        Seal(ref p, w.Journal);
        if (!p.WriteBlock(stream.Block, w.Journal) || !p.Flush()) return Fail(ref w, DOS.Error.DiskNotValidated);
        p.Clear(w.Meta, p.Geometry.BlockBytes);
        var marker = w.RootContainer + 2;
        p.WriteUInt32(w.Meta, 0, TransactionFailed); p.WriteUInt32(w.Meta, 8, marker);
        p.WriteUInt32(w.Meta, 12, stream.First); Seal(ref p, w.Meta);
        if (!p.WriteBlock(marker, w.Meta) || !p.Flush()) return Fail(ref w, DOS.Error.DiskNotValidated);
        // Publication is durable; apply the retained final images directly.
        // Mount recovery retains its full validation and replay passes.
        for (var i = 0u; i < staged.Count; i++)
            if (!p.WriteBlock(p.ReadUInt32(staged.Slot(i)), staged.Image(i)))
                return Fail(ref w, DOS.Error.DiskNotValidated);
        return CompleteNative(ref p, ref w, marker);
    }
}
