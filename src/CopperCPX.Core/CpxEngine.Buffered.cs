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
    // At P=512, the unused tail of a maximum-size mount arena is exactly the
    // 42-page undo bank. The cursor's low three bits remain the cache cursor;
    // its otherwise unused high bit records this explicit ownership profile.
    private const uint ArenaUndoFlag = 0x80000000, ArenaUndoBytes = 42 * 512;
    private const uint ArenaUndoAt = CpxArena.Scratch + 6 * 512 + CpxArena.IndexBytes + CpxArena.PoolBytes + CpxArena.CrcTableBytes;
    private uint BufferedUndoDelta => (memory.Read32(Arena + BufferObjectCursor) & ArenaUndoFlag) != 0 ? ArenaUndoBytes : 0;
    public static uint ArenaUndoBufferedStorageBytes(bool compact = false) =>
        (compact ? CompactBufferedStorageBytes(512) : BufferedStorageBytes(512)) - ArenaUndoBytes;
    private uint RequiredBufferedStorageBytes(uint pageBytes, bool full = false) =>
        (full ? BufferedStorageBytes(pageBytes) : CompactBufferedStorageBytes(pageBytes)) - BufferedUndoDelta;
    public uint BufferedStorageAddress => memory.Read32(Arena + (BufferAddress));
    public uint BufferedStorageCapacity => memory.Read32(Arena + (BufferBytes));
    public bool BufferedWrites => memory.Read32(Arena + (BufferEnabled)) == 1;

    public bool HasBufferedChanges
    {
        get
        {
            uint cpxArenaBase = Arena;
            return (memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) && memory.Read32(cpxArenaBase + (Active)) != 0 && memory.Read32(cpxArenaBase + (StagedCount)) != 0;
        }
    }

    private uint BufferedImage(uint slot)
    {
        uint cpxArenaBase = Arena;
        return memory.Read32(cpxArenaBase + (BufferAddress)) + 4096 + slot * (memory.Read32(cpxArenaBase + (PageBytes)));
    }

    private uint BufferedUndo(uint slot)
    {
        uint cpxArenaBase = Arena;
        return BufferedUndoDelta != 0 ? Arena + ArenaUndoAt + slot * 512 :
            memory.Read32(cpxArenaBase + BufferAddress) + 4096 + (42 + slot) * memory.Read32(cpxArenaBase + PageBytes);
    }

    private uint BufferedHeader
    {
        get
        {
            uint cpxArenaBase = Arena;
            return memory.Read32(cpxArenaBase + (BufferAddress)) + 4096 + 84 * (memory.Read32(cpxArenaBase + (PageBytes))) - BufferedUndoDelta;
        }
    }

    private uint BufferedOperation
    {
        get
        {
            uint cpxArenaBase = Arena;
            return BufferedHeader + 512;
        }
    }

    private uint BufferedIndex
    {
        get
        {
            uint cpxArenaBase = Arena;
            return memory.Read32(cpxArenaBase + (BufferAddress)) + 4096 + 90 * (memory.Read32(cpxArenaBase + (PageBytes))) + 8704 - BufferedUndoDelta;
        }
    }

    private uint BufferedTrust => memory.Read32(Arena + 4092);
    private uint BufferedNew => (memory.Read32(Arena + 4092)) + 64;
    private uint BufferedSavedNew => (memory.Read32(Arena + 4092)) + 256;
    private uint BufferedObjects => memory.Read32(Arena + 4088);
    private uint BufferedClean => memory.Read32(Arena + 4084);

    private bool CanCacheMetadata
    {
        get
        {
            uint cpxArenaBase = Arena;
            return (memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) || memory.Read32(cpxArenaBase + (BufferFormatEnabled)) != 0 && memory.Read32(cpxArenaBase + (BufferBytes)) >= RequiredBufferedStorageBytes(memory.Read32(cpxArenaBase + PageBytes));
        }
    }

    private void InitializeBufferedGeometry()
    {
        uint cpxArenaBase = Arena;
        uint trust = BufferedIndex + CpxArena.IndexBytes;
        memory.Write32(cpxArenaBase + 4092, trust);
        memory.Write32(cpxArenaBase + 4088, trust + 512);
        memory.Write32(cpxArenaBase + 4084, trust + 2688);
    }

    /// <summary>Caller owns this storage for the mount lifetime. Data writes
    /// reach the device before reply; metadata publication waits for a barrier.
    /// The original strict policy remains available when buffering is disabled.</summary>
    public CpxError EnableBufferedWrites(uint address, uint bytes) => EnableBufferedWritesCore(address, bytes, false);

    /// <summary>Explicit P=512 profile. The caller also owns the full maximum-
    /// size mount arena for its lifetime; its unused tail supplies undo storage.
    /// Neither region may overlap the other. No allocation occurs here.</summary>
    public CpxError EnableArenaUndoBufferedWrites(uint address, uint bytes, uint arenaBytes)
    {
        if (memory.Read32(Arena + PageBytes) != 512 || arenaBytes < CpxArena.Bytes(4096) ||
            Arena > uint.MaxValue - arenaBytes ||
            (ulong)address < (ulong)Arena + arenaBytes && (ulong)Arena < (ulong)address + bytes)
            return CpxError.InvalidArgument;
        return EnableBufferedWritesCore(address, bytes, true);
    }

    private CpxError EnableBufferedWritesCore(uint address, uint bytes, bool arenaUndo)
    {
        uint cpxArenaBase = Arena;
        if (memory.Read32(cpxArenaBase + (Signature)) != CpxArena.Magic || memory.Read32(cpxArenaBase + (Waiting)) != 0 || memory.Read32(cpxArenaBase + (Finished)) == 0 || memory.Read32(cpxArenaBase + (Active)) != 0)
            return CpxError.Busy;
        uint required = CompactBufferedStorageBytes(memory.Read32(cpxArenaBase + PageBytes)) - (arenaUndo ? ArenaUndoBytes : 0);
        if (address == 0 || (address & 3) != 0 || bytes < required || address > uint.MaxValue - bytes)
            return CpxError.InvalidArgument;
        memory.Write32(cpxArenaBase + BufferObjectCursor, arenaUndo ? ArenaUndoFlag : 0);
        memory.Write32(cpxArenaBase + (BufferAddress), address);
        memory.Write32(cpxArenaBase + (BufferBytes), bytes);
        memory.Write32(cpxArenaBase + (BufferEnabled), 1);
        memory.Write32(cpxArenaBase + (BufferCommitting), 0);
        InitializeBufferedGeometry();
        memory.Write32((memory.Read32(cpxArenaBase + 4084)) + 4, bytes >= RequiredBufferedStorageBytes(memory.Read32(cpxArenaBase + PageBytes), true) ? 8u : 4u);
        InitializeZeroCrcTable();
        memory.Write32(cpxArenaBase + (FastPointer), (memory.Read32(cpxArenaBase + 4092)) + 448);
        InvalidateFastWrite();
        memory.Write32(cpxArenaBase + (BufferSnapshot), 0);
        memory.Write32(cpxArenaBase + (BufferTrustedMask), 0);
        memory.Write32(cpxArenaBase + (BufferFreed), 0);
        memory.Write32(cpxArenaBase + (BufferNewCount), 0);
        ClearBufferedObjects();
        ClearBufferedClean();
        return CpxError.None;
    }

    private void ResetBufferedContext(bool previous)
    {
        uint cpxArenaBase = Arena;
        if (!previous)
        {
            for (uint n = 0; n < 64; n += 4)
                memory.Write32(cpxArenaBase + (Buffered + n), 0);
            for (uint n = 0; n < 48; n += 4)
                memory.Write32(cpxArenaBase + (BufferTrustedKeys + n), 0);
        }

        memory.Write32(cpxArenaBase + (BufferCommitting), 0);
        memory.Write32(cpxArenaBase + (BufferSnapshot), 0);
        memory.Write32(cpxArenaBase + (BufferTrustedMask), 0);
        memory.Write32(cpxArenaBase + (BufferFreed), 0);
        memory.Write32(cpxArenaBase + (BufferNewCount), 0);
        if (memory.Read32(cpxArenaBase + (BufferBytes)) < RequiredBufferedStorageBytes(memory.Read32(cpxArenaBase + PageBytes)))
            memory.Write32(cpxArenaBase + (BufferEnabled), 0);
        InitializeBufferedGeometry();
        memory.Write32(cpxArenaBase + (FastPointer), (memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) ? (memory.Read32(cpxArenaBase + 4092)) + 448 : 0u);
        if (memory.Read32(cpxArenaBase + (BufferAddress)) != 0 && memory.Read32(cpxArenaBase + (BufferBytes)) >= RequiredBufferedStorageBytes(memory.Read32(cpxArenaBase + PageBytes)))
        {
            memory.Write32((memory.Read32(cpxArenaBase + 4084)) + 4, ShadowVolume ? memory.Read32(ShadowStorage + 48) : memory.Read32(cpxArenaBase + (BufferBytes)) >= RequiredBufferedStorageBytes(memory.Read32(cpxArenaBase + PageBytes), true) ? 8u : 4u);
            ClearBufferedObjects();
            ClearBufferedClean();
        }
    }

    private void ClearBufferedClean()
    {
        uint cache = (memory.Read32(Arena + 4084)), pages = memory.Read32(cache + 4);
        memory.Write32(cache, 0);
        for (uint n = 0; n < pages; n++)
            memory.Write32(cache + 8 + n * 16 + 8, 0);
    }

    private void InvalidateBufferedClean(ulong address)
    {
        uint cache = (memory.Read32(Arena + 4084)), pages = memory.Read32(cache + 4);
        for (uint n = 0; n < pages; n++)
        {
            uint entry = cache + 8 + n * 16;
            if (memory.Read32(entry + 8) != 0 && (((ulong)(((((ulong)(memory.Read32(((uint)(entry))))) << 32 | ((ulong)(memory.Read32(((uint)(entry)) + 4))))))) == ((ulong)(address))))
                memory.Write32(entry + 8, 0);
        }
    }

    private bool ReadBufferedClean(ulong address, uint buffer)
    {
        uint cpxArenaBase = Arena;
        uint cache = (memory.Read32(cpxArenaBase + 4084)), pageBytes = (memory.Read32(cpxArenaBase + (PageBytes))), pages = memory.Read32(cache + 4);
        for (uint n = 0; n < pages; n++)
        {
            uint entry = cache + 8 + n * 16;
            if (memory.Read32(entry + 8) == 0 || !(((ulong)(((((ulong)(memory.Read32(((uint)(entry))))) << 32 | ((ulong)(memory.Read32(((uint)(entry)) + 4))))))) == ((ulong)(address))))
                continue;
            Copy(buffer, cache + 8 + pages * 16 + n * pageBytes, pageBytes);
            TrustBufferedPage(buffer, address);
            return true;
        }

        return false;
    }

    private void CacheBufferedClean(uint buffer, ulong address)
    {
        uint cpxArenaBase = Arena;
        uint cache = (memory.Read32(cpxArenaBase + 4084)), pageBytes = (memory.Read32(cpxArenaBase + (PageBytes))), pages = memory.Read32(cache + 4);
        for (uint n = 0; n < pages; n++)
        {
            uint entry = cache + 8 + n * 16;
            if (memory.Read32(entry + 8) != 0 && (((ulong)(((((ulong)(memory.Read32(((uint)(entry))))) << 32 | ((ulong)(memory.Read32(((uint)(entry)) + 4))))))) == ((ulong)(address))))
            {
                Copy(cache + 8 + pages * 16 + n * pageBytes, buffer, pageBytes);
                return;
            }
        }

        uint slot = memory.Read32(cache) & (pages - 1);
        memory.Write32(cache, slot + 1);
        uint target = cache + 8 + slot * 16;
        {
            uint cpxWideAddress = (uint)(target);
            ulong cpxWideValue = address;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        Copy(cache + 8 + pages * 16 + slot * pageBytes, buffer, pageBytes);
        memory.Write32(target + 8, 1);
    }

    private void InvalidateBufferedPage(uint p)
    {
        uint cpxArenaBase = Arena;
        uint first = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(0)) * 4))), bytes = (memory.Read32(cpxArenaBase + (PageBytes)));
        if (!(memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) && !(ShadowRootControl && CanCacheMetadata) || p < first || p > (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(5)) * 4))) || ((p - first) & (bytes - 1)) != 0)
            return;
        memory.Write32(cpxArenaBase + (BufferTrustedMask), memory.Read32(cpxArenaBase + (BufferTrustedMask)) & ~(1u << (int)((p - first) >> (int)memory.Read32(cpxArenaBase + (GeometryShift)))));
    }

    private void TrustBufferedPage(uint p, ulong address)
    {
        uint cpxArenaBase = Arena;
        uint first = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(0)) * 4))), bytes = (memory.Read32(cpxArenaBase + (PageBytes)));
        if (p < first || p > (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(5)) * 4))) || ((p - first) & (bytes - 1)) != 0)
            return;
        uint slot = (p - first) >> (int)memory.Read32(cpxArenaBase + (GeometryShift));
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(BufferTrustedKeys + slot * 8);
            ulong cpxWideValue = address;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(cpxArenaBase + (BufferTrustedMask), memory.Read32(cpxArenaBase + (BufferTrustedMask)) | (1u << (int)slot));
    }

    private bool TrustedBufferedPage(uint p, ulong address)
    {
        uint cpxArenaBase = Arena;
        uint first = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(0)) * 4))), bytes = (memory.Read32(cpxArenaBase + (PageBytes)));
        if (!(memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) && !(ShadowRootControl && CanCacheMetadata) || p < first || p > (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(5)) * 4))) || ((p - first) & (bytes - 1)) != 0)
            return false;
        uint slot = (p - first) >> (int)memory.Read32(cpxArenaBase + (GeometryShift));
        return (memory.Read32(cpxArenaBase + (BufferTrustedMask)) & (1u << (int)slot)) != 0 && (((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(BufferTrustedKeys + slot * 8)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(BufferTrustedKeys + slot * 8)) + 4)))))))) == ((ulong)(address)));
    }

    private void SnapshotBufferedOperation(uint credits)
    {
        uint cpxArenaBase = Arena;
        uint count = memory.Read32(cpxArenaBase + (StagedCount));
        memory.Write32(cpxArenaBase + (BufferOldCount), count);
        memory.Write32(cpxArenaBase + (BufferUndoLow), 0);
        memory.Write32(cpxArenaBase + (BufferUndoHigh), 0);
        memory.Write32(cpxArenaBase + (BufferOldFreed), memory.Read32(cpxArenaBase + (BufferFreed)));
        memory.Write32(cpxArenaBase + (BufferOldNew), memory.Read32(cpxArenaBase + (BufferNewCount)));
        Copy(((memory.Read32(cpxArenaBase + 4092)) + 256), ((memory.Read32(cpxArenaBase + 4092)) + 64), 192);
        memory.Write32(cpxArenaBase + (BufferSnapshot), 1);
        memory.Write32(cpxArenaBase + (BufferFullSnapshot), count != 0 && count + credits > 42 ? 1u : 0u);
        // Only active descriptors belong to the rollback snapshot.
        Copy(BufferedIndex, (memory.Read32(cpxArenaBase + (GeometryIndex))), count * 32);
        if (memory.Read32(cpxArenaBase + (BufferFullSnapshot)) == 0)
            return;
        Copy(BufferedHeader, cpxArenaBase, 512);
        Copy(BufferedOperation, cpxArenaBase + CpxArena.MountBytes, 8128);
        Copy(BufferedOperation + 8192, (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(0)) * 4))), 6 * (memory.Read32(cpxArenaBase + (PageBytes))));
        Copy((memory.Read32(cpxArenaBase + 4092)), cpxArenaBase + BufferTrustedMask, 4);
        Copy((memory.Read32(cpxArenaBase + 4092)) + 4, cpxArenaBase + BufferTrustedKeys, 48);
    }

    private void SaveBufferedUndo(uint slot)
    {
        uint cpxArenaBase = Arena;
        if (memory.Read32(cpxArenaBase + (BufferSnapshot)) == 0 || slot >= memory.Read32(cpxArenaBase + (BufferOldCount)))
            return;
        uint field = slot < 32 ? BufferUndoLow : BufferUndoHigh, mask = 1u << (int)(slot & 31);
        if ((memory.Read32(cpxArenaBase + (field)) & mask) != 0)
            return;
        Copy(BufferedUndo((uint)(slot)), (memory.Read32(cpxArenaBase + (BufferAddress)) + 4096 + ((uint)(slot)) * (memory.Read32(cpxArenaBase + (PageBytes)))), (memory.Read32(cpxArenaBase + (PageBytes))));
        memory.Write32(cpxArenaBase + (field), memory.Read32(cpxArenaBase + (field)) | mask);
    }

    private void RestoreBufferedImages()
    {
        uint cpxArenaBase = Arena;
        for (uint slot = 0; slot < memory.Read32(cpxArenaBase + (BufferOldCount)); slot++)
            if ((memory.Read32(cpxArenaBase + (slot < 32 ? BufferUndoLow : BufferUndoHigh)) & (1u << (int)(slot & 31))) != 0)
                Copy((memory.Read32(cpxArenaBase + (BufferAddress)) + 4096 + ((uint)(slot)) * (memory.Read32(cpxArenaBase + (PageBytes)))), BufferedUndo((uint)(slot)), (memory.Read32(cpxArenaBase + (PageBytes))));
        Copy((memory.Read32(cpxArenaBase + (GeometryIndex))), BufferedIndex, memory.Read32(cpxArenaBase + BufferOldCount) * 32);
        Copy(((memory.Read32(cpxArenaBase + 4092)) + 64), ((memory.Read32(cpxArenaBase + 4092)) + 256), 192);
        memory.Write32(cpxArenaBase + (BufferNewCount), memory.Read32(cpxArenaBase + (BufferOldNew)));
    }

    private void DiscardBufferedOperation()
    {
        uint cpxArenaBase = Arena;
        if (memory.Read32(cpxArenaBase + (BufferSnapshot)) == 0)
            return;
        RestoreBufferedImages();
        memory.Write32(cpxArenaBase + (StagedCount), memory.Read32(cpxArenaBase + (BufferOldCount)));
        memory.Write32(cpxArenaBase + (Active), memory.Read32(cpxArenaBase + (BufferOldCount)) != 0 ? 1u : 0u);
        memory.Write32(cpxArenaBase + (BufferSnapshot), 0);
        memory.Write32(cpxArenaBase + (BufferTrustedMask), 0);
        memory.Write32(cpxArenaBase + (AbortOnly), 0);
        memory.Write32(cpxArenaBase + (BufferFreed), memory.Read32(cpxArenaBase + (BufferOldFreed)));
        ClearBufferedObjects();
    }

    private StepResult RetryBufferedOperation()
    {
        uint cpxArenaBase = Arena;
        if (memory.Read32(cpxArenaBase + (BufferFullSnapshot)) == 0)
            return Fail(CpxError.TransactionLimit);
        uint nextToken = memory.Read32(cpxArenaBase + (NextToken));
        RestoreBufferedImages();
        Copy(cpxArenaBase, BufferedHeader, 512);
        // I/O identity belongs to the mount, not the rolled-back operation.
        // Reads completed since admission must never have their tokens reused.
        memory.Write32(cpxArenaBase + (NextToken), nextToken);
        Copy(cpxArenaBase + CpxArena.MountBytes, BufferedOperation, 8128);
        Copy((memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(0)) * 4))), BufferedOperation + 8192, 6 * (memory.Read32(cpxArenaBase + (PageBytes))));
        Copy(cpxArenaBase + BufferTrustedMask, (memory.Read32(cpxArenaBase + 4092)), 4);
        Copy(cpxArenaBase + BufferTrustedKeys, (memory.Read32(cpxArenaBase + 4092)) + 4, 48);
        ClearBufferedObjects();
        memory.Write32(cpxArenaBase + (BufferSnapshot), 0);
        memory.Write32(cpxArenaBase + (BufferCommitting), 1);
        // The restored continuation is immediately before BeginTx. Retire the
        // earlier group, then retry admission with all 42 image slots free.
        return Call(CpxOperation.CommitTransaction, memory.Read32(cpxArenaBase + (Phase)));
    }

    private StepResult RetireBufferedGroup(uint next)
    {
        uint cpxArenaBase = Arena;
        memory.Write32(cpxArenaBase + (BufferSnapshot), 0);
        memory.Write32(cpxArenaBase + (BufferCommitting), 1);
        return Call(CpxOperation.CommitTransaction, next);
    }

    private void NoteBufferedAllocation(ulong first, uint count, bool used)
    {
        uint cpxArenaBase = Arena;
        if (!(memory.Read32(cpxArenaBase + (BufferEnabled)) == 1))
            return;
        for (uint n = 0; n < memory.Read32(cpxArenaBase + (BufferNewCount)); n++)
        {
            uint record = ((memory.Read32(cpxArenaBase + 4092)) + 64) + n * 12;
            ulong start = ((((ulong)(memory.Read32(((uint)(record))))) << 32 | ((ulong)(memory.Read32(((uint)(record)) + 4)))));
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
            memory.Write32(cpxArenaBase + (BufferFreed), 1);
            return;
        }

        uint slot = memory.Read32(cpxArenaBase + (BufferNewCount));
        if (slot == 16)
            return;
        {
            uint cpxWideAddress = (uint)(((memory.Read32(cpxArenaBase + 4092)) + 64) + slot * 12);
            ulong cpxWideValue = first;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(((memory.Read32(cpxArenaBase + 4092)) + 64) + slot * 12 + 8, count);
        memory.Write32(cpxArenaBase + (BufferNewCount), slot + 1);
    }

    private void ClearBufferedObjects()
    {
        uint cpxArenaBase = Arena;
        InvalidateFastWrite();
        for (uint n = 0; n < 8; n++)
            memory.Write32((memory.Read32(cpxArenaBase + 4088)) + n * 272 + 268, 0);
        memory.Write32(cpxArenaBase + BufferObjectCursor, memory.Read32(cpxArenaBase + BufferObjectCursor) & ArenaUndoFlag);
    }

    private bool ReadBufferedObject(ulong id)
    {
        uint cpxArenaBase = Arena;
        for (uint n = 0; n < 8; n++)
        {
            uint entry = (memory.Read32(cpxArenaBase + 4088)) + n * 272;
            if (memory.Read32(entry + 268) == 0 || !(((ulong)(((((ulong)(memory.Read32(((uint)(entry + 12))))) << 32 | ((ulong)(memory.Read32(((uint)(entry + 12)) + 4))))))) == ((ulong)(id))))
                continue;
            Copy((cpxArenaBase + CpxArena.MountBytes), entry + 12, 256);
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(TreeAddress);
                ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)(entry))))) << 32 | ((ulong)(memory.Read32(((uint)(entry)) + 4)))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

            memory.Write32(cpxArenaBase + (TreeSlot), memory.Read32(entry + 8));
            memory.Write32(cpxArenaBase + (SourcePage), 1);
            return true;
        }

        return false;
    }

    private void CacheBufferedObjects(uint p, ulong address)
    {
        uint cpxArenaBase = Arena;
        if (!(memory.Read32(cpxArenaBase + (BufferEnabled)) == 1))
            return;
        InvalidateFastWrite();
        for (uint n = 0; n < 8; n++)
        {
            uint entry = (memory.Read32(cpxArenaBase + 4088)) + n * 272;
            if (memory.Read32(entry + 268) != 0 && (((ulong)(((((ulong)(memory.Read32(((uint)(entry))))) << 32 | ((ulong)(memory.Read32(((uint)(entry)) + 4))))))) == ((ulong)(address))))
                memory.Write32(entry + 268, 0);
        }

        for (uint slot = 0; slot < memory.Read16(p + 48); slot++)
        {
            uint record = p + 64 + slot * 256, target = 8;
            for (uint n = 0; n < 8; n++)
            {
                uint entry = (memory.Read32(cpxArenaBase + 4088)) + n * 272;
                if (memory.Read32(entry + 268) != 0 && (((ulong)(((((ulong)(memory.Read32(((uint)(entry + 12))))) << 32 | ((ulong)(memory.Read32(((uint)(entry + 12)) + 4))))))) == ((ulong)(((((ulong)(memory.Read32(((uint)(record))))) << 32 | ((ulong)(memory.Read32(((uint)(record)) + 4)))))))))
                {
                    target = n;
                    break;
                }
            }

            if (target == 8)
            {
                target = memory.Read32(cpxArenaBase + (BufferObjectCursor)) & 7;
                uint cursor = memory.Read32(cpxArenaBase + BufferObjectCursor);
                memory.Write32(cpxArenaBase + BufferObjectCursor, (cursor & ArenaUndoFlag) | ((cursor + 1) & 7));
            }

            uint dest = (memory.Read32(cpxArenaBase + 4088)) + target * 272;
            {
                uint cpxWideAddress = (uint)(dest);
                ulong cpxWideValue = address;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

            memory.Write32(dest + 8, slot + 1);
            Copy(dest + 12, record, 256);
            memory.Write32(dest + 268, 1);
        }
    }
}
