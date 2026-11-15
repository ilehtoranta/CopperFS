namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    // Persistent control occupies the final 64 bytes of the operation region.
    // Symlink text ends at 16320. Trusted keys occupy the 48-byte gap after
    // native record timer state and before the dispatcher information buffers.
    private const uint Buffered = 16320, BufferAddress = Buffered, BufferBytes = Buffered + 4, BufferEnabled = Buffered + 8, BufferCommitting = Buffered + 12, BufferSnapshot = Buffered + 16, BufferFullSnapshot = Buffered + 20, BufferOldCount = Buffered + 24, BufferUndoLow = Buffered + 28, BufferUndoHigh = Buffered + 32, BufferTrustedMask = Buffered + 36, BufferTrustedKeys = 3024, BufferFreed = Buffered + 40, BufferNewCount = Buffered + 44, BufferOldFreed = Buffered + 48, BufferOldNew = Buffered + 52, BufferObjectCursor = Buffered + 56, BufferFormatEnabled = Buffered + 60;
    public static uint BufferedStorageBytes(uint pageBytes) => 4096 + 84 * pageBytes + 512 + 8192 + 6 * pageBytes + CpxArena.IndexBytes + 2688 + 8 + 8 * (16 + pageBytes);
    public static uint CompactBufferedStorageBytes(uint pageBytes) => BufferedStorageBytes(pageBytes) - 4 * (16 + pageBytes);
    public uint BufferedStorageAddress => memory.Read32(Arena + (BufferAddress));
    public uint BufferedStorageCapacity => memory.Read32(Arena + (BufferBytes));
    public bool BufferedWrites => memory.Read32(Arena + (BufferEnabled)) == 1;
    public bool HasBufferedChanges => BufferedWrites && memory.Read32(Arena + (Active)) != 0 && memory.Read32(Arena + (StagedCount)) != 0;

    private uint BufferedImage(uint slot) => memory.Read32(Arena + (BufferAddress)) + 4096 + slot * P;
    private uint BufferedUndo(uint slot) => memory.Read32(Arena + (BufferAddress)) + 4096 + (42 + slot) * P;
    private uint BufferedHeader => memory.Read32(Arena + (BufferAddress)) + 4096 + 84 * P;
    private uint BufferedOperation => memory.Read32(Arena + (BufferAddress)) + 4096 + 84 * P + 512;
    private uint BufferedIndex => memory.Read32(Arena + (BufferAddress)) + 4096 + 90 * P + 8704;
    private uint BufferedTrust => memory.Read32(Arena + 4092);
    private uint BufferedNew => BufferedTrust + 64;
    private uint BufferedSavedNew => BufferedTrust + 256;
    private uint BufferedObjects => memory.Read32(Arena + 4088);
    private uint BufferedClean => memory.Read32(Arena + 4084);
    private bool CanCacheMetadata => BufferedWrites || memory.Read32(Arena + (BufferFormatEnabled)) != 0 && memory.Read32(Arena + (BufferBytes)) >= CompactBufferedStorageBytes(P);

    private void InitializeBufferedGeometry()
    {
        uint trust = memory.Read32(Arena + (BufferAddress)) + 4096 + 90 * P + 8704 + CpxArena.IndexBytes;
        memory.Write32(Arena + 4092, trust);
        memory.Write32(Arena + 4088, trust + 512);
        memory.Write32(Arena + 4084, trust + 2688);
    }

    /// <summary>Caller owns this storage for the mount lifetime. Data writes
    /// reach the device before reply; metadata publication waits for a barrier.
    /// The original strict policy remains available when buffering is disabled.</summary>
    public CpxError EnableBufferedWrites(uint address, uint bytes)
    {
        if (memory.Read32(Arena + (Signature)) != CpxArena.Magic || memory.Read32(Arena + (Waiting)) != 0 || memory.Read32(Arena + (Finished)) == 0 || memory.Read32(Arena + (Active)) != 0)
            return CpxError.Busy;
        if (address == 0 || (address & 3) != 0 || bytes < CompactBufferedStorageBytes(P) || address > uint.MaxValue - bytes)
            return CpxError.InvalidArgument;
        memory.Write32(Arena + (BufferAddress), address);
        memory.Write32(Arena + (BufferBytes), bytes);
        memory.Write32(Arena + (BufferEnabled), 1);
        memory.Write32(Arena + (BufferCommitting), 0);
        InitializeBufferedGeometry();
        memory.Write32(BufferedClean + 4, bytes >= BufferedStorageBytes(P) ? 8u : 4u);
        InitializeZeroCrcTable();
        memory.Write32(Arena + (FastPointer), BufferedTrust + 448);
        InvalidateFastWrite();
        memory.Write32(Arena + (BufferSnapshot), 0);
        memory.Write32(Arena + (BufferTrustedMask), 0);
        memory.Write32(Arena + (BufferFreed), 0);
        memory.Write32(Arena + (BufferNewCount), 0);
        ClearBufferedObjects();
        ClearBufferedClean();
        return CpxError.None;
    }

    private void ResetBufferedContext(bool previous)
    {
        if (!previous)
        {
            for (uint n = 0; n < 64; n += 4)
                memory.Write32(Arena + (Buffered + n), 0);
            for (uint n = 0; n < 48; n += 4)
                memory.Write32(Arena + (BufferTrustedKeys + n), 0);
        }

        memory.Write32(Arena + (BufferCommitting), 0);
        memory.Write32(Arena + (BufferSnapshot), 0);
        memory.Write32(Arena + (BufferTrustedMask), 0);
        memory.Write32(Arena + (BufferFreed), 0);
        memory.Write32(Arena + (BufferNewCount), 0);
        if (memory.Read32(Arena + (BufferBytes)) < CompactBufferedStorageBytes(P))
            memory.Write32(Arena + (BufferEnabled), 0);
        InitializeBufferedGeometry();
        memory.Write32(Arena + (FastPointer), BufferedWrites ? BufferedTrust + 448 : 0u);
        if (memory.Read32(Arena + (BufferAddress)) != 0 && memory.Read32(Arena + (BufferBytes)) >= CompactBufferedStorageBytes(P))
        {
            memory.Write32(BufferedClean + 4, memory.Read32(Arena + (BufferBytes)) >= BufferedStorageBytes(P) ? 8u : 4u);
            ClearBufferedObjects();
            ClearBufferedClean();
        }
    }

    private void ClearBufferedClean()
    {
        uint cache = BufferedClean, pages = memory.Read32(cache + 4);
        memory.Write32(cache, 0);
        for (uint n = 0; n < pages; n++)
            memory.Write32(cache + 8 + n * 16 + 8, 0);
    }

    private void InvalidateBufferedClean(ulong address)
    {
        uint cache = BufferedClean, pages = memory.Read32(cache + 4);
        for (uint n = 0; n < pages; n++)
        {
            uint entry = cache + 8 + n * 16;
            if (memory.Read32(entry + 8) != 0 && (((ulong)(GetWide(entry))) == ((ulong)(address))))
                memory.Write32(entry + 8, 0);
        }
    }

    private bool ReadBufferedClean(ulong address, uint buffer)
    {
        uint cache = BufferedClean, pageBytes = P, pages = memory.Read32(cache + 4);
        for (uint n = 0; n < pages; n++)
        {
            uint entry = cache + 8 + n * 16;
            if (memory.Read32(entry + 8) == 0 || !(((ulong)(GetWide(entry))) == ((ulong)(address))))
                continue;
            Copy(buffer, cache + 8 + pages * 16 + n * pageBytes, pageBytes);
            TrustBufferedPage(buffer, address);
            return true;
        }

        return false;
    }

    private void CacheBufferedClean(uint buffer, ulong address)
    {
        uint cache = BufferedClean, pageBytes = P, pages = memory.Read32(cache + 4);
        for (uint n = 0; n < pages; n++)
        {
            uint entry = cache + 8 + n * 16;
            if (memory.Read32(entry + 8) != 0 && (((ulong)(GetWide(entry))) == ((ulong)(address))))
            {
                Copy(cache + 8 + pages * 16 + n * pageBytes, buffer, pageBytes);
                return;
            }
        }

        uint slot = memory.Read32(cache) & (pages - 1);
        memory.Write32(cache, slot + 1);
        uint target = cache + 8 + slot * 16;
        PutWide(target, address);
        Copy(cache + 8 + pages * 16 + slot * pageBytes, buffer, pageBytes);
        memory.Write32(target + 8, 1);
    }

    private void InvalidateBufferedPage(uint p)
    {
        uint first = Page(0), bytes = P;
        if (!BufferedWrites || p < first || p > Page(5) || ((p - first) & (bytes - 1)) != 0)
            return;
        memory.Write32(Arena + (BufferTrustedMask), memory.Read32(Arena + (BufferTrustedMask)) & ~(1u << (int)((p - first) >> (int)memory.Read32(Arena + (GeometryShift)))));
    }

    private void TrustBufferedPage(uint p, ulong address)
    {
        uint first = Page(0), bytes = P;
        if (p < first || p > Page(5) || ((p - first) & (bytes - 1)) != 0)
            return;
        uint slot = (p - first) >> (int)memory.Read32(Arena + (GeometryShift));
        WW(BufferTrustedKeys + slot * 8, address);
        memory.Write32(Arena + (BufferTrustedMask), memory.Read32(Arena + (BufferTrustedMask)) | (1u << (int)slot));
    }

    private bool TrustedBufferedPage(uint p, ulong address)
    {
        uint first = Page(0), bytes = P;
        if (!BufferedWrites || p < first || p > Page(5) || ((p - first) & (bytes - 1)) != 0)
            return false;
        uint slot = (p - first) >> (int)memory.Read32(Arena + (GeometryShift));
        return (memory.Read32(Arena + (BufferTrustedMask)) & (1u << (int)slot)) != 0 && (((ulong)(RW(BufferTrustedKeys + slot * 8))) == ((ulong)(address)));
    }

    private void SnapshotBufferedOperation(uint credits)
    {
        uint count = memory.Read32(Arena + (StagedCount));
        memory.Write32(Arena + (BufferOldCount), count);
        memory.Write32(Arena + (BufferUndoLow), 0);
        memory.Write32(Arena + (BufferUndoHigh), 0);
        memory.Write32(Arena + (BufferOldFreed), memory.Read32(Arena + (BufferFreed)));
        memory.Write32(Arena + (BufferOldNew), memory.Read32(Arena + (BufferNewCount)));
        Copy(BufferedSavedNew, BufferedNew, 192);
        memory.Write32(Arena + (BufferSnapshot), 1);
        memory.Write32(Arena + (BufferFullSnapshot), count != 0 && count + credits > 42 ? 1u : 0u);
        // Only active descriptors belong to the rollback snapshot.
        Copy(BufferedIndex, Index, count * 32);
        if (memory.Read32(Arena + (BufferFullSnapshot)) == 0)
            return;
        Copy(BufferedHeader, Arena, 512);
        Copy(BufferedOperation, Arena + CpxArena.MountBytes, 8128);
        Copy(BufferedOperation + 8192, Page(0), 6 * P);
        Copy(BufferedTrust, Arena + BufferTrustedMask, 4);
        Copy(BufferedTrust + 4, Arena + BufferTrustedKeys, 48);
    }

    private void SaveBufferedUndo(uint slot)
    {
        if (memory.Read32(Arena + (BufferSnapshot)) == 0 || slot >= memory.Read32(Arena + (BufferOldCount)))
            return;
        uint field = slot < 32 ? BufferUndoLow : BufferUndoHigh, mask = 1u << (int)(slot & 31);
        if ((memory.Read32(Arena + (field)) & mask) != 0)
            return;
        Copy(BufferedUndo(slot), BufferedImage(slot), P);
        memory.Write32(Arena + (field), memory.Read32(Arena + (field)) | mask);
    }

    private void RestoreBufferedImages()
    {
        for (uint slot = 0; slot < memory.Read32(Arena + (BufferOldCount)); slot++)
            if ((memory.Read32(Arena + (slot < 32 ? BufferUndoLow : BufferUndoHigh)) & (1u << (int)(slot & 31))) != 0)
                Copy(BufferedImage(slot), BufferedUndo(slot), P);
        Copy(Index, BufferedIndex, memory.Read32(Arena + BufferOldCount) * 32);
        Copy(BufferedNew, BufferedSavedNew, 192);
        memory.Write32(Arena + (BufferNewCount), memory.Read32(Arena + (BufferOldNew)));
    }

    private void DiscardBufferedOperation()
    {
        if (memory.Read32(Arena + (BufferSnapshot)) == 0)
            return;
        RestoreBufferedImages();
        memory.Write32(Arena + (StagedCount), memory.Read32(Arena + (BufferOldCount)));
        memory.Write32(Arena + (Active), memory.Read32(Arena + (BufferOldCount)) != 0 ? 1u : 0u);
        memory.Write32(Arena + (BufferSnapshot), 0);
        memory.Write32(Arena + (BufferTrustedMask), 0);
        memory.Write32(Arena + (AbortOnly), 0);
        memory.Write32(Arena + (BufferFreed), memory.Read32(Arena + (BufferOldFreed)));
        ClearBufferedObjects();
    }

    private StepResult RetryBufferedOperation()
    {
        if (memory.Read32(Arena + (BufferFullSnapshot)) == 0)
            return Fail(CpxError.TransactionLimit);
        uint nextToken = memory.Read32(Arena + (NextToken));
        RestoreBufferedImages();
        Copy(Arena, BufferedHeader, 512);
        // I/O identity belongs to the mount, not the rolled-back operation.
        // Reads completed since admission must never have their tokens reused.
        memory.Write32(Arena + (NextToken), nextToken);
        Copy(Arena + CpxArena.MountBytes, BufferedOperation, 8128);
        Copy(Page(0), BufferedOperation + 8192, 6 * P);
        Copy(Arena + BufferTrustedMask, BufferedTrust, 4);
        Copy(Arena + BufferTrustedKeys, BufferedTrust + 4, 48);
        ClearBufferedObjects();
        memory.Write32(Arena + (BufferSnapshot), 0);
        memory.Write32(Arena + (BufferCommitting), 1);
        // The restored continuation is immediately before BeginTx. Retire the
        // earlier group, then retry admission with all 42 image slots free.
        return Call(CpxOperation.CommitTransaction, memory.Read32(Arena + (Phase)));
    }

    private StepResult RetireBufferedGroup(uint next)
    {
        memory.Write32(Arena + (BufferSnapshot), 0);
        memory.Write32(Arena + (BufferCommitting), 1);
        return Call(CpxOperation.CommitTransaction, next);
    }

    private void NoteBufferedAllocation(ulong first, uint count, bool used)
    {
        if (!BufferedWrites)
            return;
        for (uint n = 0; n < memory.Read32(Arena + (BufferNewCount)); n++)
        {
            uint record = BufferedNew + n * 12;
            ulong start = GetWide(record);
            uint length = memory.Read32(record + 8);
            if (used && (((ulong)(start + length)) == ((ulong)(first))) && length <= uint.MaxValue - count)
            {
                memory.Write32(record + 8, length + count);
                return;
            }

            if (!used && Scalar64.Subtract(first, start, out var delta) && ((uint)(((ulong)(delta)) >> 32)) == 0 && ((uint)((ulong)(delta))) <= length && count <= length - ((uint)((ulong)(delta))))
                return;
        }

        if (!used)
        {
            memory.Write32(Arena + (BufferFreed), 1);
            return;
        }

        uint slot = memory.Read32(Arena + (BufferNewCount));
        if (slot == 16)
            return;
        PutWide(BufferedNew + slot * 12, first);
        memory.Write32(BufferedNew + slot * 12 + 8, count);
        memory.Write32(Arena + (BufferNewCount), slot + 1);
    }

    private void ClearBufferedObjects()
    {
        InvalidateFastWrite();
        for (uint n = 0; n < 8; n++)
            memory.Write32(BufferedObjects + n * 272 + 268, 0);
        memory.Write32(Arena + (BufferObjectCursor), 0);
    }

    private bool ReadBufferedObject(ulong id)
    {
        for (uint n = 0; n < 8; n++)
        {
            uint entry = BufferedObjects + n * 272;
            if (memory.Read32(entry + 268) == 0 || !(((ulong)(GetWide(entry + 12))) == ((ulong)(id))))
                continue;
            Copy(ObjectRecord, entry + 12, 256);
            WW(TreeAddress, GetWide(entry));
            memory.Write32(Arena + (TreeSlot), memory.Read32(entry + 8));
            memory.Write32(Arena + (SourcePage), 1);
            return true;
        }

        return false;
    }

    private void CacheBufferedObjects(uint p, ulong address)
    {
        if (!BufferedWrites)
            return;
        InvalidateFastWrite();
        for (uint n = 0; n < 8; n++)
        {
            uint entry = BufferedObjects + n * 272;
            if (memory.Read32(entry + 268) != 0 && (((ulong)(GetWide(entry))) == ((ulong)(address))))
                memory.Write32(entry + 268, 0);
        }

        for (uint slot = 0; slot < memory.Read16(p + 48); slot++)
        {
            uint record = p + 64 + slot * 256, target = 8;
            for (uint n = 0; n < 8; n++)
            {
                uint entry = BufferedObjects + n * 272;
                if (memory.Read32(entry + 268) != 0 && (((ulong)(GetWide(entry + 12))) == ((ulong)(GetWide(record)))))
                {
                    target = n;
                    break;
                }
            }

            if (target == 8)
            {
                target = memory.Read32(Arena + (BufferObjectCursor)) & 7;
                memory.Write32(Arena + (BufferObjectCursor), memory.Read32(Arena + (BufferObjectCursor)) + 1);
            }

            uint dest = BufferedObjects + target * 272;
            PutWide(dest, address);
            memory.Write32(dest + 8, slot + 1);
            Copy(dest + 12, record, 256);
            memory.Write32(dest + 268, 1);
        }
    }
}
