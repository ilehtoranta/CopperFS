namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // This opt-in exists only on the experimental branch. All new mutable state
    // belongs to explicitly supplied storage; the CPX4 arena ABI is unchanged.
    private bool ShadowVolume => (memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0;
    internal bool PreferIdleDiscard => ((memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0);
    private uint ShadowStorage => memory.Read32(Arena + (CheckpointPolicy)) & ~7u;
    public CpxError DisableShadowVolume()
    {
        if (memory.Read32(Arena + (Waiting)) != 0 || memory.Read32(Arena + (Finished)) == 0 || memory.Read32(Arena + (Active)) != 0 || memory.Read32(Arena + (Mounted)) != 0) return CpxError.Busy;
        memory.Write32(Arena + (CheckpointPolicy), memory.Read32(Arena + (CheckpointPolicy)) & 3); return CpxError.None;
    }
    public static uint ShadowStorageBytes(uint pageBytes) => 296 + 3 * pageBytes + (pageBytes < 1024 ? 2048u : pageBytes);
    private bool ShadowRootControl => ((memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0) && memory.Read32((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 56) >= 2;
    private bool ShadowCatalogue => ((memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0) && memory.Read32((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 56) >= 5;
    private uint ShadowControl => (memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 288 + 3 * (memory.Read32(Arena + (PageBytes)));
    private uint ShadowRootBytes => (((memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0) && memory.Read32((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 56) >= 5) && (memory.Read32(Arena + (PageBytes))) < 1024 ? 1024u : (memory.Read32(Arena + (PageBytes)));
    private uint ShadowRootInput => (((memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0) && memory.Read32((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 56) >= 5) && (memory.Read32(Arena + (PageBytes))) < 1024 ? ((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 288 + 3 * (memory.Read32(Arena + (PageBytes)))) + 1024 : (memory.Read32(Arena + (GeometryPages + (5) * 4)));
    private uint ShadowRootSpan => ((((memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0) && memory.Read32((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 56) >= 5) && (memory.Read32(Arena + (PageBytes))) < 1024 ? 1024u : (memory.Read32(Arena + (PageBytes)))) >> (int)memory.Read32(Arena + (UnitShift));
    private uint ReserveCapacity => (((memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0) && memory.Read32((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 56) >= 2) ? 32u : 64u;
    private uint MediaMagic => ((memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0) ? 0x43505835u : CpxArena.Magic;
    private uint MediaVersion => ((memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0) ? 5u : 4u;
    private uint MetadataSpan => ((memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0) ? 2 * (memory.Read32(Arena + (GeometryQ))) : (memory.Read32(Arena + (GeometryQ)));
    private ulong StateAddress => (((ulong)(memory.Read32(Arena + (GeometryAnchor))))) + (((memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0) ? 4u : 3u) * (memory.Read32(Arena + (GeometryQ)));
    private ulong CharsetAddress => (((ulong)(memory.Read32(Arena + (GeometryAnchor))))) + ((((memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0) && memory.Read32((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 56) >= 5) ? 4 * (((((memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0) && memory.Read32((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 56) >= 5) && (memory.Read32(Arena + (PageBytes))) < 1024 ? 1024u : (memory.Read32(Arena + (PageBytes)))) >> (int)memory.Read32(Arena + (UnitShift))) + 2 * (memory.Read32(Arena + (GeometryQ))) : (((memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0) ? 6u : 4u) * (memory.Read32(Arena + (GeometryQ))));

    public CpxError EnableShadowVolume(uint storage, uint bytes, uint features = 0)
    {
        uint buffered = BufferedStorageAddress, usable = BufferedStorageCapacity;
        // Native startup may append this context to the same Exec allocation.
        // Count only the prefix before it when selecting four/eight cache pages.
        if (buffered != 0 && storage >= buffered && (ulong)storage < (ulong)buffered + usable) usable = storage - buffered;
        uint bufferedUsed = usable >= RequiredBufferedStorageBytes(memory.Read32(Arena + PageBytes), true) ? RequiredBufferedStorageBytes(memory.Read32(Arena + PageBytes), true) : RequiredBufferedStorageBytes(memory.Read32(Arena + PageBytes));
        if (memory.Read32(Arena + (Signature)) != CpxArena.Magic || features > 5 || ((memory.Read32(Arena + (PageBytes))) == 512) && features is not (0 or 4 or 5) || storage == 0 || (storage & 7) != 0 ||
            bytes < ShadowStorageBytes((memory.Read32(Arena + (PageBytes)))) || storage > uint.MaxValue - bytes ||
            (ulong)storage < (ulong)Arena + CpxArena.Bytes(BufferedUndoDelta != 0 ? 4096u : memory.Read32(Arena + PageBytes)) && (ulong)Arena < (ulong)storage + bytes ||
            buffered != 0 && (usable < RequiredBufferedStorageBytes(memory.Read32(Arena + PageBytes)) ||
                (ulong)storage < (ulong)buffered + bufferedUsed && (ulong)buffered < (ulong)storage + bytes))
            return CpxError.InvalidArgument;
        if (memory.Read32(Arena + (Waiting)) != 0 || memory.Read32(Arena + (Finished)) == 0 || memory.Read32(Arena + (Active)) != 0 || memory.Read32(Arena + (Mounted)) != 0) return CpxError.Busy;
        memory.Write32(Arena + (CheckpointPolicy), storage | (memory.Read32(Arena + (CheckpointPolicy)) & 3) | 4);
        Clear(storage, ShadowStorageBytes((memory.Read32(Arena + (PageBytes)))));
        CpxError error = CpxShadowMapGeometry.TryCreate(1u << (int)memory.Read32(Arena + (SectorShift)), (memory.Read32(Arena + (PageBytes))), ((((ulong)(memory.Read32((Arena) + ((Capacity))))) << 32 | ((ulong)(memory.Read32((Arena) + ((Capacity) + 4)))))),
            524288UL >> (int)memory.Read32(Arena + (SectorShift)), out var geometry, features >= 3);
        if (error != CpxError.None) { memory.Write32(Arena + (CheckpointPolicy), memory.Read32(Arena + (CheckpointPolicy)) & 3); return error; }
        ulong dataStart = 2097152UL >> (int)memory.Read32(Arena + (UnitShift));
        ulong mapEnd = (ulong)geometry.MapEndSector >> (int)(memory.Read32(Arena + (UnitShift)) - memory.Read32(Arena + (SectorShift)));
        if (mapEnd > dataStart) dataStart = ((mapEnd + 65535) >> 16) << 16;
        if (dataStart >= Backup) { memory.Write32(Arena + (CheckpointPolicy), memory.Read32(Arena + (CheckpointPolicy)) & 3); return CpxError.InvalidArgument; }
        PutWide(storage + 40, dataStart);
        geometry.Store(ref memory, storage + 64);
        memory.Write32(storage + 56, features);
        uint cachePages = usable >= RequiredBufferedStorageBytes(memory.Read32(Arena + PageBytes), true) ? 8u : 4u;
        memory.Write32(storage + 48, cachePages);
        if (CanCacheMetadata) { memory.Write32((memory.Read32(Arena + 4084)) + 4, cachePages); ClearBufferedClean(); }
        return CpxError.None;
    }

    private StepResult ReadShadowControl(ulong unit, uint buffer, uint nextPhase)
    {
        bool state = unit == ((((ulong)(memory.Read32(Arena + (GeometryAnchor))))) + (((memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0) ? 4u : 3u) * (memory.Read32(Arena + (GeometryQ))));
        Header(buffer, state ? 3u : 4u, unit, ((((ulong)(memory.Read32((Arena) + ((Generation))))) << 32 | ((ulong)(memory.Read32((Arena) + ((Generation) + 4)))))));
        uint length = state ? memory.Read16(((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 288 + 3 * (memory.Read32(Arena + (PageBytes)))) + 56) : 264;
        Copy(buffer + 64, ((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 288 + 3 * (memory.Read32(Arena + (PageBytes)))) + (state ? 96u : 232u), length);
        memory.Write16(buffer + 52, state ? length : (((memory.Read32(Arena + (PageBytes))) == 512) ? 264u : 520u));
        if (CanCacheMetadata)
        {
            TrustBufferedPage(buffer, unit);
            CacheBufferedClean(buffer, unit);
        }
        else memory.Write32(buffer + 8, Crc(buffer, (memory.Read32(Arena + (PageBytes))), true));
        memory.Write32(Arena + (Phase), nextPhase);
        return StepResult.Pending;
    }

    private StepResult BeginShadowKernel(ulong root) => BeginOwnedShadowPublication(root);

    private CpxError GetShadowGeometry(out CpxShadowMapGeometry geometry)
    {
        geometry = CpxShadowMapGeometry.Restore(ref memory, (memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 64);
        return CpxError.None;
    }

    private uint ShadowCacheEntry(uint slot) => (memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 256 + (memory.Read32(Arena + (PageBytes))) + slot * (16 + (memory.Read32(Arena + (PageBytes))));
    private void ClearShadowCache()
    {
        memory.Write32((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 20, 0); memory.Write32((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 36, 0);
        memory.Write32(ShadowCacheEntry(0) + 8, 0); memory.Write32(ShadowCacheEntry(1) + 8, 0);
    }

    private StepResult BeginShadowRead(ulong unit, uint buffer, uint nextPhase, bool requireInode = false)
    {
        uint storage = (memory.Read32(Arena + (CheckpointPolicy)) & ~7u);
        memory.Write32(storage + 52, requireInode ? 1u : 0u);
        if ((((memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0) && memory.Read32((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 56) >= 5) && ((((ulong)(memory.Read32((storage + 24)))) << 32 | ((ulong)(memory.Read32((storage + 24) + 4))))) == CpxCheckpointCatalogue<TMemory>.InlineRoot)
        {
            uint pairShift = memory.Read32(storage + 76), shift = memory.Read32(Arena + (UnitShift)) - memory.Read32(Arena + (SectorShift));
            if (!Scalar64.Left(unit, (int)shift, out var inlineSector) || ((ulong)inlineSector & ((1ul << (int)pairShift) - 1)) != 0 ||
                (ulong)inlineSector >> (int)pairShift >= ((((ulong)(memory.Read32((storage + 112)))) << 32 | ((ulong)(memory.Read32((storage + 112) + 4))))) ||
                (ulong)inlineSector >= ((((ulong)(memory.Read32((storage + 96)))) << 32 | ((ulong)(memory.Read32((storage + 96) + 4))))) && (ulong)inlineSector < ((((ulong)(memory.Read32((storage + 104)))) << 32 | ((ulong)(memory.Read32((storage + 104) + 4)))))) return Fail(CpxError.Corrupt);
            var catalogue = new CpxCheckpointCatalogue<TMemory>(memory, ((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 288 + 3 * (memory.Read32(Arena + (PageBytes)))) + 496);
            uint flags = catalogue.Lookup((ulong)inlineSector >> (int)pairShift);
            memory.Write32(storage + 4, flags & 2);
            memory.Write32(Arena + (Phase), nextPhase);
            if (requireInode && (flags & 2) == 0) return StepResult.Pending;
            return Issue(IoKind.Read, unit + (flags & 1) * (memory.Read32(Arena + (GeometryQ))), buffer, (memory.Read32(Arena + (PageBytes))), nextPhase);
        }
        if (GetShadowGeometry(out var geometry) != CpxError.None) return Fail(CpxError.Corrupt);
        if (!Scalar64.Left(unit, (int)(memory.Read32(Arena + (UnitShift)) - memory.Read32(Arena + (SectorShift))), out var sector)) return Fail(CpxError.Corrupt);
        var reader = new CpxShadowMapReadCursor<TMemory>(memory, storage + 128, geometry);
        CpxError error = reader.Begin(((((ulong)(memory.Read32((storage + 24)))) << 32 | ((ulong)(memory.Read32((storage + 24) + 4))))), ((((ulong)(memory.Read32((Arena) + ((Generation))))) << 32 | ((ulong)(memory.Read32((Arena) + ((Generation) + 4)))))), ((((ulong)(memory.Read32((Arena) + ((Identity))))) << 32 | ((ulong)(memory.Read32((Arena) + ((Identity) + 4)))))), sector, storage + 256,
            (memory.Read32(Arena + (GeometryCrc))), CanCacheMetadata ? BufferedStorageAddress : 0);
        if (error != CpxError.None) return Fail(error);
        PutWide(storage + 8, unit); memory.Write32(storage + 16, buffer);
        // The caller may translate a nested tree phase after ReadStaged returns.
        // Preserve that translated phase throughout the inserted map continuation.
        memory.Write32(Arena + (Phase), nextPhase);
        memory.Write32(storage, 1);
        return StepResult.Pending;
    }

    private StepResult ShadowReadStep()
    {
        uint storage = (memory.Read32(Arena + (CheckpointPolicy)) & ~7u);
        if (GetShadowGeometry(out var geometry) != CpxError.None) return Fail(CpxError.Corrupt);
        var reader = new CpxShadowMapReadCursor<TMemory>(memory, storage + 128, geometry);
        StepResult result = reader.Step();
        if (result == StepResult.Failed) return Fail(reader.Error);
        uint fill = memory.Read32(storage + 20);
        if (fill != 0 && result != StepResult.NeedIo)
        {
            uint entry = ShadowCacheEntry(fill - 1);
            Copy(entry + 16, reader.ReadBuffer, (memory.Read32(Arena + (PageBytes)))); memory.Write32(entry + 8, 1); memory.Write32(storage + 20, 0);
        }
        if (result == StepResult.NeedIo)
        {
            for (uint slot = 0; slot < 2; slot++)
            {
                uint entry = ShadowCacheEntry(slot);
                if (memory.Read32(entry + 8) == 0 || ((((ulong)(memory.Read32((entry)))) << 32 | ((ulong)(memory.Read32((entry) + 4))))) != (ulong)reader.ReadSector) continue;
                Copy(reader.ReadBuffer, entry + 16, (memory.Read32(Arena + (PageBytes))));
                if (reader.AcceptCachedRead() != CpxError.None) return Fail(CpxError.Corrupt);
                return StepResult.Pending;
            }
            uint victim = memory.Read32(storage + 36) & 1, next = ShadowCacheEntry(victim);
            memory.Write32(storage + 36, victim + 1); memory.Write32(next + 8, 0); PutWide(next, reader.ReadSector);
            memory.Write32(storage + 20, victim + 1);
            return Issue(IoKind.Read, (ulong)reader.ReadSector >> (int)(memory.Read32(Arena + (UnitShift)) - memory.Read32(Arena + (SectorShift))), reader.ReadBuffer, (memory.Read32(Arena + (PageBytes))), memory.Read32(Arena + (Phase)));
        }
        if (result == StepResult.Complete)
        {
            memory.Write32(storage, 0);
            memory.Write32(storage + 4, reader.IsInode ? 1u : 0u);
            if (memory.Read32(storage + 52) != 0 && !reader.IsInode) return StepResult.Pending;
            return Issue(IoKind.Read, ((((ulong)(memory.Read32((storage + 8)))) << 32 | ((ulong)(memory.Read32((storage + 8) + 4))))) + reader.SelectedCopy * (memory.Read32(Arena + (GeometryQ))),
                memory.Read32(storage + 16), (memory.Read32(Arena + (PageBytes))), memory.Read32(Arena + (Phase)));
        }
        return result;
    }

    private void CompleteShadowIo(bool success)
    {
        uint storage = (memory.Read32(Arena + (CheckpointPolicy)) & ~7u);
        uint owner = memory.Read32(storage);
        if (owner is not (1 or 2)) return;
        CompleteShadowMapIo(storage, owner, success);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void CompleteShadowMapIo(uint storage, uint owner, bool success)
    {
        if (GetShadowGeometry(out var geometry) != CpxError.None) return;
        if (owner == 1)
        {
            var reader = new CpxShadowMapReadCursor<TMemory>(memory, storage + 128, geometry);
            reader.AcceptRead(success);
        }
        else if (owner == 2)
        {
            var writer = new CpxShadowMapWriteCursor<TMemory>(memory, BufferedUndo(0), geometry);
            writer.AcceptIo(success);
        }
    }

    private StepResult ShadowCommitStep()
    {
        uint stage = memory.Read32(Arena + Phase);
        if (stage == 103) return ShadowKernelStep();
        if (stage is 104 or 105)
        {
            uint descriptors = BufferedUndo(0) + 1024;
            return stage == 104 ? ShadowCatalogueUpdate(descriptors) : ShadowCataloguePromote(descriptors);
        }
        return PrepareShadowCommitStep(stage);
    }

    // Keep repeated kernel visits out of the descriptor-preparation frame.
    // Geometry and preparation temporaries have no lifetime during I/O service.
    private StepResult PrepareShadowCommitStep(uint stage)
    {
        uint storage = (memory.Read32(Arena + (CheckpointPolicy)) & ~7u), state = BufferedUndo(0), descriptors = state + 1024;
        uint shift = memory.Read32(Arena + (UnitShift)) - memory.Read32(Arena + (SectorShift));
        if (stage == 0) { memory.Write32(Arena + (Cursor), 0); memory.Write32(storage + 60, 0); memory.Write32(Arena + (Phase), 100); return StepResult.Pending; }
        if (stage == 100)
        {
            uint slot = memory.Read32(Arena + (Cursor)), image = (memory.Read32((Arena) + (BufferAddress)) + 4096 + (slot) * (memory.Read32((Arena) + (PageBytes)))), normal = memory.Read32(storage + 60), d = descriptors + normal * 32;
            ulong logical = GetWide((memory.Read32(Arena + (GeometryIndex))) + slot * 32);
            if (memory.Read32(image) != (((memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0) ? 0x43505835u : CpxArena.Magic) || memory.Read16(image + 6) != (((memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0) ? 5u : 4u) ||
                ((((ulong)(memory.Read32((image + 16)))) << 32 | ((ulong)(memory.Read32((image + 16) + 4))))) != logical || ((((ulong)(memory.Read32((image + 32)))) << 32 | ((ulong)(memory.Read32((image + 32) + 4))))) != ((((ulong)(memory.Read32((Arena) + ((Generation))))) << 32 | ((ulong)(memory.Read32((Arena) + ((Generation) + 4)))))) + 1 ||
                !TargetValid(logical, memory.Read16(image + 4), ((((ulong)(memory.Read32((image + 24)))) << 32 | ((ulong)(memory.Read32((image + 24) + 4)))))) || !PayloadValid(image)) return Fail(CpxError.Corrupt);
            if ((((memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0) && memory.Read32((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 56) >= 2) && (logical == ((((ulong)(memory.Read32(Arena + (GeometryAnchor))))) + (((memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0) ? 4u : 3u) * (memory.Read32(Arena + (GeometryQ)))) || logical == (((ulong)(memory.Read32(Arena + (GeometryAnchor))))) + 8 * (memory.Read32(Arena + (GeometryQ)))))
            {
                bool volumeState = logical == ((((ulong)(memory.Read32(Arena + (GeometryAnchor))))) + (((memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0) ? 4u : 3u) * (memory.Read32(Arena + (GeometryQ))));
                uint length = volumeState ? memory.Read16(image + 52) : 264;
                if (!volumeState && memory.Read16(image + 64) > 32) return Fail(CpxError.Corrupt);
                Clear(((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 288 + 3 * (memory.Read32(Arena + (PageBytes)))) + (volumeState ? 96u : 232u), volumeState ? 136u : 264u);
                Copy(((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 288 + 3 * (memory.Read32(Arena + (PageBytes)))) + (volumeState ? 96u : 232u), image + 64, length);
                if (volumeState) memory.Write16(((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 288 + 3 * (memory.Read32(Arena + (PageBytes)))) + 56, length);
            }
            else
            {
                memory.Write32(image + 8, Crc(image, (memory.Read32(Arena + (PageBytes))), true));
                PutWide(d, logical); PutWide(d + 8, logical << (int)shift);
                memory.Write32(d + 16, image); memory.Write32(d + 20, 0); PutWide(d + 24, 0);
                memory.Write32(storage + 60, normal + 1);
            }
            memory.Write32(Arena + (Cursor), slot + 1);
            if (slot + 1 == memory.Read32(Arena + (StagedCount))) memory.Write32(Arena + (Phase), 101);
            return StepResult.Pending;
        }
        if (stage == 101)
        {
            if (memory.Read32(storage + 60) == 0) return BeginShadowKernel(((((ulong)(memory.Read32((storage + 24)))) << 32 | ((ulong)(memory.Read32((storage + 24) + 4))))));
            if ((((memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0) && memory.Read32((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 56) >= 5) && ((((ulong)(memory.Read32((storage + 24)))) << 32 | ((ulong)(memory.Read32((storage + 24) + 4))))) == CpxCheckpointCatalogue<TMemory>.InlineRoot)
            {
                uint temporary = ShadowCatalogueTemporary;
                Copy(temporary, ((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 288 + 3 * (memory.Read32(Arena + (PageBytes)))) + 496, CpxCheckpointCatalogue<TMemory>.Bytes);
                memory.Write32(Arena + (Cursor), 0); memory.Write32(Arena + (Phase), 104); return StepResult.Pending;
            }
            if (GetShadowGeometry(out var geometry) != CpxError.None) return Fail(CpxError.Corrupt);
            var writer = new CpxShadowMapWriteCursor<TMemory>(memory, state, geometry);
            CpxError error = writer.Begin(((((ulong)(memory.Read32((storage + 24)))) << 32 | ((ulong)(memory.Read32((storage + 24) + 4))))), ((((ulong)(memory.Read32((Arena) + ((Generation))))) << 32 | ((ulong)(memory.Read32((Arena) + ((Generation) + 4)))))), ((((ulong)(memory.Read32((Arena) + ((Identity))))) << 32 | ((ulong)(memory.Read32((Arena) + ((Identity) + 4)))))), descriptors, memory.Read32(storage + 60), state + 5120,
                (memory.Read32(Arena + (GeometryCrc))), BufferedStorageAddress);
            if (error != CpxError.None) return Fail(error);
            memory.Write32(storage + 4, 0); memory.Write32(storage, 2); memory.Write32(Arena + (Phase), 102); return StepResult.Pending;
        }
        if (stage == 102)
        {
            if (GetShadowGeometry(out var geometry) != CpxError.None) return Fail(CpxError.Corrupt);
            var writer = new CpxShadowMapWriteCursor<TMemory>(memory, state, geometry);
            StepResult result = writer.Step();
            if (result == StepResult.Failed) return Fail(writer.Error);
            if (result == StepResult.NeedIo)
                return Issue(writer.RequestKind, (ulong)writer.RequestSector >> (int)shift, writer.RequestBuffer, writer.RequestBytes, 102);
            if (result != StepResult.Complete) return result;
            ulong root = writer.MappingRoot;
            if (memory.Read32(storage + 4) != 0)
            {
                new CpxCheckpointCatalogue<TMemory>(memory, ((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 288 + 3 * (memory.Read32(Arena + (PageBytes)))) + 496).Clear();
                ClearShadowDescriptorFlags(descriptors, memory.Read32(storage + 60));
            }
            memory.Write32(storage, 0);
            return BeginShadowKernel(root);
        }
        return Fail(CpxError.Corrupt);
    }

    private StepResult ShadowKernelStep() => OwnedShadowPublicationStep();

    private StepResult ShadowRecoveryStep()
    {
        uint phase = memory.Read32(Arena + Phase);
        return phase >= 24 ? RecoveryStep() : ShadowCheckpointRecoveryStep(phase);
    }

    private StepResult ShadowCheckpointRecoveryStep(uint phase)
    {
        bool catalogue = (((memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0) && memory.Read32((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 56) >= 5), rootControl = (((memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0) && memory.Read32((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 56) >= 2);
        uint rootBytes = ((((memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0) && memory.Read32((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 56) >= 5) && (memory.Read32(Arena + (PageBytes))) < 1024 ? 1024u : (memory.Read32(Arena + (PageBytes)))), span = (((((memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0) && memory.Read32((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 56) >= 5) && (memory.Read32(Arena + (PageBytes))) < 1024 ? 1024u : (memory.Read32(Arena + (PageBytes)))) >> (int)memory.Read32(Arena + (UnitShift))), control = ((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 288 + 3 * (memory.Read32(Arena + (PageBytes))));
        uint storage = (memory.Read32(Arena + (CheckpointPolicy)) & ~7u);
        // Recovery starts only with a quiescent, unpublished-image-free mount.
        // The 42-page undo bank has no snapshot owner then; three checkpoints
        // occupy at most six of those pages. The plain workspace path retains
        // independent reads. Every checkpoint is still validated separately.
        uint batch = CanCacheMetadata ? BufferedUndo(0) : 0;
        if (phase == 0)
        {
            RememberAllocationEnd(0);
            ClearShadowCache();
            memory.Write32(Arena + (Cursor), 0); memory.Write32(Arena + (ValidOne), 0); WW(Generation, 0); PutWide(storage + 24, 0);
            return Issue(IoKind.Read, (((ulong)(memory.Read32(Arena + (GeometryAnchor))))) + span,
                batch == 0 ? ShadowRootInput : batch, batch == 0 ? rootBytes : 3 * rootBytes, 1);
        }
        uint p = batch == 0 ? ShadowRootInput : batch + memory.Read32(Arena + Cursor) * rootBytes;
        ulong at = (((ulong)(memory.Read32(Arena + (GeometryAnchor))))) + (memory.Read32(Arena + (Cursor)) + 1) * span;
        bool valid = memory.Read32(p) == (((memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0) ? 0x43505835u : CpxArena.Magic) && memory.Read16(p + 6) == (((memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0) ? 5u : 4u) && memory.Read16(p + 4) == 2 &&
            ((((ulong)(memory.Read32((p + 16)))) << 32 | ((ulong)(memory.Read32((p + 16) + 4))))) == at && ((((ulong)(memory.Read32((p + 24)))) << 32 | ((ulong)(memory.Read32((p + 24) + 4))))) == 0 && memory.Read32(p + 8) == Crc(p, rootBytes, true);
        if (valid && IdentityMatches(p) && memory.Read16(p + 52) == (catalogue ? 960 : rootControl ? 432 : 32) && ((((ulong)(memory.Read32((p + 32)))) << 32 | ((ulong)(memory.Read32((p + 32) + 4))))) == ((((ulong)(memory.Read32((p + 80)))) << 32 | ((ulong)(memory.Read32((p + 80) + 4))))))
        {
            if (rootControl && (memory.Read16(p + 56) is not (48 or 128 or 136) ||
                memory.Read16(p + 58) != 32 || memory.Read16(p + 232) > 32)) return Fail(CpxError.Corrupt);
            ulong generation = ((((ulong)(memory.Read32((p + 80)))) << 32 | ((ulong)(memory.Read32((p + 80) + 4))))), root = ((((ulong)(memory.Read32((p + 88)))) << 32 | ((ulong)(memory.Read32((p + 88) + 4)))));
            if (GetShadowGeometry(out var geometry) != CpxError.None) return Fail(CpxError.Corrupt);
            ulong pair = geometry.NodePairSector(geometry.Height - 1, 0);
            bool inline = catalogue && root == CpxCheckpointCatalogue<TMemory>.InlineRoot;
            if (!inline && root != 0 && root != pair && root != pair + geometry.PageSectors) return Fail(CpxError.Corrupt);
            if (catalogue && !new CpxCheckpointCatalogue<TMemory>(memory, p + 496).Valid(geometry.PairCount, inline)) return Fail(CpxError.Corrupt);
            if (memory.Read32(Arena + (ValidOne)) != 0 && generation == ((((ulong)(memory.Read32((Arena) + ((Generation))))) << 32 | ((ulong)(memory.Read32((Arena) + ((Generation) + 4)))))) && root != ((((ulong)(memory.Read32((storage + 24)))) << 32 | ((ulong)(memory.Read32((storage + 24) + 4)))))) return Fail(CpxError.Corrupt);
            if (rootControl && memory.Read32(Arena + (ValidOne)) != 0 && generation == ((((ulong)(memory.Read32((Arena) + ((Generation))))) << 32 | ((ulong)(memory.Read32((Arena) + ((Generation) + 4)))))))
            {
                if (memory.Read32(p + 56) != memory.Read32(control + 56)) return Fail(CpxError.Corrupt);
                for (uint offset = 96; offset < (catalogue ? 1024u : 496u); offset += 4)
                    if (memory.Read32(p + offset) != memory.Read32(control + offset)) return Fail(CpxError.Corrupt);
            }
            if (memory.Read32(Arena + (ValidOne)) == 0 || generation > ((((ulong)(memory.Read32((Arena) + ((Generation))))) << 32 | ((ulong)(memory.Read32((Arena) + ((Generation) + 4)))))))
            {
                memory.Write32(Arena + (ValidOne), 1); WW(Generation, generation); PutWide(storage + 24, root); memory.Write32(storage + 32, memory.Read32(Arena + (Cursor)));
                if (rootControl) Copy(control, p, rootBytes);
            }
        }
        memory.Write32(Arena + (Cursor), memory.Read32(Arena + (Cursor)) + 1);
        if (memory.Read32(Arena + (Cursor)) < 3)
            return batch == 0 ? Issue(IoKind.Read, (((ulong)(memory.Read32(Arena + (GeometryAnchor))))) + (memory.Read32(Arena + (Cursor)) + 1) * span, p, rootBytes, 1) : StepResult.Pending;
        if (memory.Read32(Arena + (ValidOne)) == 0) return Fail(CpxError.Corrupt);
        if ((((memory.Read32(Arena + (CheckpointPolicy)) & 4) != 0) && memory.Read32((memory.Read32(Arena + (CheckpointPolicy)) & ~7u) + 56) >= 3))
        {
            GetShadowGeometry(out var inodeGeometry);
            if (CpxInodeIdentity.TryCreate(inodeGeometry, ((((ulong)(memory.Read32((control + 112)))) << 32 | ((ulong)(memory.Read32((control + 112) + 4))))) << (int)(memory.Read32(Arena + (UnitShift)) - memory.Read32(Arena + (SectorShift))), out var codec) != CpxError.None)
                return Fail(CpxError.Corrupt);
            codec.Store(ref memory, storage + 224);
        }
        memory.Write32(Arena + (Phase), 24);
        return StepResult.Pending;
    }
}
