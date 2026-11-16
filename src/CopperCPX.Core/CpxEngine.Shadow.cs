namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // This opt-in exists only on the experimental branch. All new mutable state
    // belongs to explicitly supplied storage; the CPX4 arena ABI is unchanged.
    private bool ShadowVolume => (R(CheckpointPolicy) & 4) != 0;
    private uint ShadowStorage => R(CheckpointPolicy) & ~7u;
    public CpxError DisableShadowVolume()
    {
        if (R(Waiting) != 0 || R(Finished) == 0 || R(Active) != 0 || R(Mounted) != 0) return CpxError.Busy;
        W(CheckpointPolicy, R(CheckpointPolicy) & 3); return CpxError.None;
    }
    public static uint ShadowStorageBytes(uint pageBytes) => 288 + 3 * pageBytes;
    private uint MediaMagic => ShadowVolume ? 0x43505835u : CpxArena.Magic;
    private uint MediaVersion => ShadowVolume ? 5u : 4u;
    private uint MetadataSpan => ShadowVolume ? 2 * Q : Q;
    private ulong StateAddress => Anchor + (ShadowVolume ? 4u : 3u) * Q;
    private ulong CharsetAddress => Anchor + (ShadowVolume ? 6u : 4u) * Q;

    public CpxError EnableShadowVolume(uint storage, uint bytes)
    {
        if (R(Signature) != CpxArena.Magic || storage == 0 || (storage & 7) != 0 ||
            bytes < ShadowStorageBytes(P) || storage > uint.MaxValue - bytes ||
            (ulong)storage < (ulong)Arena + CpxArena.Bytes(P) && (ulong)Arena < (ulong)storage + bytes ||
            BufferedStorageAddress != 0 && (ulong)storage < (ulong)BufferedStorageAddress +
                (BufferedStorageCapacity >= BufferedStorageBytes(P) ? BufferedStorageBytes(P) : CompactBufferedStorageBytes(P)) &&
            (ulong)BufferedStorageAddress < (ulong)storage + bytes)
            return CpxError.InvalidArgument;
        if (R(Waiting) != 0 || R(Finished) == 0 || R(Active) != 0 || R(Mounted) != 0) return CpxError.Busy;
        W(CheckpointPolicy, storage | (R(CheckpointPolicy) & 3) | 4);
        Clear(storage, ShadowStorageBytes(P));
        CpxError error = CpxShadowMapGeometry.TryCreate(1u << (int)R(SectorShift), P, RW(Capacity),
            524288UL >> (int)R(SectorShift), out var geometry);
        if (error != CpxError.None) { W(CheckpointPolicy, R(CheckpointPolicy) & 3); return error; }
        ulong dataStart = 2097152UL >> (int)R(UnitShift);
        ulong mapEnd = (ulong)geometry.MapEndSector >> (int)(R(UnitShift) - R(SectorShift));
        if (mapEnd > dataStart) dataStart = ((mapEnd + 65535) >> 16) << 16;
        if (dataStart >= Backup) { W(CheckpointPolicy, R(CheckpointPolicy) & 3); return CpxError.InvalidArgument; }
        PutWide(storage + 40, dataStart);
        geometry.Store(ref memory, storage + 64);
        return CpxError.None;
    }

    private CpxError GetShadowGeometry(out CpxShadowMapGeometry geometry)
    {
        geometry = CpxShadowMapGeometry.Restore(ref memory, ShadowStorage + 64);
        return CpxError.None;
    }

    private uint ShadowCacheEntry(uint slot) => ShadowStorage + 256 + P + slot * (16 + P);
    private void ClearShadowCache()
    {
        memory.Write32(ShadowStorage + 20, 0); memory.Write32(ShadowStorage + 36, 0);
        memory.Write32(ShadowCacheEntry(0) + 8, 0); memory.Write32(ShadowCacheEntry(1) + 8, 0);
    }

    private StepResult BeginShadowRead(ulong unit, uint buffer, uint nextPhase)
    {
        uint storage = ShadowStorage;
        if (GetShadowGeometry(out var geometry) != CpxError.None) return Fail(CpxError.Corrupt);
        if (!Scalar64.Left(unit, (int)(R(UnitShift) - R(SectorShift)), out var sector)) return Fail(CpxError.Corrupt);
        var reader = new CpxShadowMapReadCursor<TMemory>(memory, storage + 128, geometry);
        CpxError error = reader.Begin(GetWide(storage + 24), RW(Generation), RW(Identity), sector, storage + 256,
            CrcTable, CanCacheMetadata ? BufferedStorageAddress : 0);
        if (error != CpxError.None) return Fail(error);
        PutWide(storage + 8, unit); memory.Write32(storage + 16, buffer);
        // The caller may translate a nested tree phase after ReadStaged returns.
        // Preserve that translated phase throughout the inserted map continuation.
        W(Phase, nextPhase);
        memory.Write32(storage, 1);
        return StepResult.Pending;
    }

    private StepResult ShadowReadStep()
    {
        uint storage = ShadowStorage;
        if (GetShadowGeometry(out var geometry) != CpxError.None) return Fail(CpxError.Corrupt);
        var reader = new CpxShadowMapReadCursor<TMemory>(memory, storage + 128, geometry);
        StepResult result = reader.Step();
        if (result == StepResult.Failed) return Fail(reader.Error);
        uint fill = memory.Read32(storage + 20);
        if (fill != 0 && result != StepResult.NeedIo)
        {
            uint entry = ShadowCacheEntry(fill - 1);
            Copy(entry + 16, reader.ReadBuffer, P); memory.Write32(entry + 8, 1); memory.Write32(storage + 20, 0);
        }
        if (result == StepResult.NeedIo)
        {
            for (uint slot = 0; slot < 2; slot++)
            {
                uint entry = ShadowCacheEntry(slot);
                if (memory.Read32(entry + 8) == 0 || GetWide(entry) != (ulong)reader.ReadSector) continue;
                Copy(reader.ReadBuffer, entry + 16, P);
                if (reader.AcceptCachedRead() != CpxError.None) return Fail(CpxError.Corrupt);
                return StepResult.Pending;
            }
            uint victim = memory.Read32(storage + 36) & 1, next = ShadowCacheEntry(victim);
            memory.Write32(storage + 36, victim + 1); memory.Write32(next + 8, 0); PutWide(next, reader.ReadSector);
            memory.Write32(storage + 20, victim + 1);
            return Issue(IoKind.Read, (ulong)reader.ReadSector >> (int)(R(UnitShift) - R(SectorShift)), reader.ReadBuffer, P, R(Phase));
        }
        if (result == StepResult.Complete)
        {
            memory.Write32(storage, 0);
            return Issue(IoKind.Read, GetWide(storage + 8) + reader.SelectedCopy * Q,
                memory.Read32(storage + 16), P, R(Phase));
        }
        return result;
    }

    private void CompleteShadowIo(bool success)
    {
        uint storage = ShadowStorage;
        uint owner = memory.Read32(storage);
        if (owner == 0) return;
        if (owner == 3)
        {
            var commit = new CpxShadowCommit<TMemory>(memory, BufferedUndo(0));
            CpxIo request = commit.Request;
            commit.CompleteIo(request.Token, success ? request.Bytes : 0, success);
            return;
        }
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
        uint storage = ShadowStorage, state = BufferedUndo(0), descriptors = state + 1024;
        uint stage = R(Phase), shift = R(UnitShift) - R(SectorShift);
        if (stage == 0) { W(Cursor, 0); W(Phase, 100); return StepResult.Pending; }
        if (stage == 100)
        {
            uint slot = R(Cursor), image = BufferedImage(slot), d = descriptors + slot * 32;
            ulong logical = GetWide(Index + slot * 32);
            if (memory.Read32(image) != MediaMagic || memory.Read16(image + 6) != MediaVersion ||
                GetWide(image + 16) != logical || GetWide(image + 32) != RW(Generation) + 1 ||
                !TargetValid(logical, memory.Read16(image + 4), GetWide(image + 24)) || !PayloadValid(image)) return Fail(CpxError.Corrupt);
            memory.Write32(image + 8, Crc(image, P, true));
            PutWide(d, logical); PutWide(d + 8, logical << (int)shift);
            memory.Write32(d + 16, image); memory.Write32(d + 20, 0); PutWide(d + 24, 0);
            W(Cursor, slot + 1);
            if (slot + 1 == R(StagedCount)) W(Phase, 101);
            return StepResult.Pending;
        }
        if (stage == 101)
        {
            if (GetShadowGeometry(out var geometry) != CpxError.None) return Fail(CpxError.Corrupt);
            var writer = new CpxShadowMapWriteCursor<TMemory>(memory, state, geometry);
            CpxError error = writer.Begin(GetWide(storage + 24), RW(Generation), RW(Identity), descriptors, R(StagedCount), state + 3072,
                CrcTable, BufferedStorageAddress);
            if (error != CpxError.None) return Fail(error);
            memory.Write32(storage, 2); W(Phase, 102); return StepResult.Pending;
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
            memory.Write32(storage, 0);
            var commit = new CpxShadowCommit<TMemory>(memory, state);
            CpxError error = commit.Initialize(1u << (int)R(SectorShift), P, RW(Capacity), RW(Identity), RW(Generation),
                (Anchor + Q) << (int)shift, memory.Read32(storage + 32), Page(5), GetWide(storage + 24),
                CrcTable, BufferedStorageAddress, shift);
            if (error == CpxError.None) error = commit.BeginOwnedImages(descriptors, R(StagedCount), root);
            if (error != CpxError.None) return Fail(error);
            memory.Write32(storage, 3); W(Phase, 103); return StepResult.Pending;
        }
        if (stage == 103)
        {
            var commit = new CpxShadowCommit<TMemory>(memory, state);
            StepResult result = commit.Step();
            if (result == StepResult.Failed) return Fail(commit.Error);
            if (result == StepResult.NeedIo)
            {
                CpxIo request = commit.Request;
                return Issue(request.Kind, (ulong)request.Sector >> (int)shift, request.Buffer, request.Bytes, 103);
            }
            if (result != StepResult.Complete) return result;
            PutWide(storage + 24, commit.DurableMappingRoot);
            memory.Write32(storage + 32, commit.SurvivingCheckpoint); memory.Write32(storage, 0);
            ClearShadowCache();
            W(Phase, 15); return StepResult.Pending;
        }
        return Fail(CpxError.Corrupt);
    }

    private StepResult ShadowRecoveryStep()
    {
        uint p = Page(5), phase = R(Phase), storage = ShadowStorage;
        if (phase >= 24) return RecoveryStep();
        if (phase == 0)
        {
            ClearShadowCache();
            W(Cursor, 0); W(ValidOne, 0); WW(Generation, 0); PutWide(storage + 24, 0);
            return Issue(IoKind.Read, Anchor + Q, p, P, 1);
        }
        ulong at = Anchor + (R(Cursor) + 1) * Q;
        if (Control(p, 2, at) && IdentityMatches(p) && memory.Read16(p + 52) == 32 && GetWide(p + 32) == GetWide(p + 80))
        {
            ulong generation = GetWide(p + 80), root = GetWide(p + 88);
            if (GetShadowGeometry(out var geometry) != CpxError.None) return Fail(CpxError.Corrupt);
            ulong pair = geometry.NodePairSector(geometry.Height - 1, 0);
            if (root != 0 && root != pair && root != pair + geometry.PageSectors) return Fail(CpxError.Corrupt);
            if (R(ValidOne) != 0 && generation == RW(Generation) && root != GetWide(storage + 24)) return Fail(CpxError.Corrupt);
            if (R(ValidOne) == 0 || generation > RW(Generation))
            {
                W(ValidOne, 1); WW(Generation, generation); PutWide(storage + 24, root); memory.Write32(storage + 32, R(Cursor));
            }
        }
        W(Cursor, R(Cursor) + 1);
        if (R(Cursor) < 3) return Issue(IoKind.Read, Anchor + (R(Cursor) + 1) * Q, p, P, 1);
        if (R(ValidOne) == 0) return Fail(CpxError.Corrupt);
        W(Phase, 24);
        return StepResult.Pending;
    }
}
