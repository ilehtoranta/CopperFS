namespace CopperCPX;
public partial struct CpxDosDispatcher<TMemory>
    where TMemory : struct, ICpxMemory
{
    private const uint Inhibited = 324, MediaReason = 328, StopReason = 332;
    public bool IsInhibited => memory.Read32(arena + 512 + (Inhibited)) != 0;

    public bool MediaAvailable
    {
        get
        {
            uint cpxArenaBase = arena;
            return !(memory.Read32(cpxArenaBase + 512 + (Inhibited)) != 0) && memory.Read32(cpxArenaBase + 512 + (MediaReason)) == 0 && engine.IsMounted;
        }
    }

    public bool StorageStopPending => memory.Read32(arena + 512 + (StopReason)) != 0;

    // The adapter must abort/reap transport I/O before delivering CompleteIo.
    // Marking cancellation never releases a device-owned buffer or packet.
    public void RequestStorageCancellation()
    {
        uint cpxArenaBase = arena;
        uint error = memory.Read32(cpxArenaBase + 512 + (MediaReason)) == 226 ? 226u : 304u;
        CancelRecordWaits(error);
        if (!(memory.Read32(cpxArenaBase + 512 + (BusyAt)) != 0))
            return;
        memory.Write32(cpxArenaBase + 512 + (StopReason), error);
        if (error != 226)
            memory.Write32(cpxArenaBase + 512 + (MediaReason), 213);
        engine.Cancel();
    }

    public void MediaRemoved()
    {
        uint cpxArenaBase = arena;
        memory.Write32(cpxArenaBase + 512 + (MediaReason), 226);
        CancelRecordWaits(226);
        SuspendWatches();
        if ((memory.Read32(cpxArenaBase + 512 + (BusyAt)) != 0))
            memory.Write32(cpxArenaBase + 512 + (StopReason), 226);
        engine.Cancel();
    }

    private void SuspendWatches()
    {
        for (uint n = 0; n < 8; n++)
        {
            uint p = ((arena + CpxArena.Scratch + 6 * engine.MetadataPageBytes + CpxArena.IndexBytes) + 4608) + n * 896 + 8;
            if (memory.Read32(p - 8) == 0 || memory.Read32(p + 36) == 0)
                continue;
            if (!G(p + 16).IsZero)
                memory.Write32(p + 32, 1);
            Put(p + 16, default);
            memory.Write32(p + 24, 0);
            memory.Write32(p + 28, 0);
        }
    }

    private void RefreshWatches()
    {
        for (uint n = 0; n < 8; n++)
        {
            uint p = ((arena + CpxArena.Scratch + 6 * engine.MetadataPageBytes + CpxArena.IndexBytes) + 4608) + n * 896 + 8;
            if (memory.Read32(p - 8) != 0 && memory.Read32(p + 36) != 0)
                memory.Write32(p + 24, 1);
        }
    }

    private void RetireMediaCursors()
    {
        for (uint n = 0; n < 16; n++)
            memory.Write32((arena + CpxArena.Scratch + 6 * engine.MetadataPageBytes + CpxArena.IndexBytes) + 3584 + n * 64, 0);
    }

    private StepResult StopStorageStep()
    {
        uint cpxArenaBase = arena;
        if (engine.HasOutstandingIo)
            return StepResult.NeedIo;
        uint error = memory.Read32(cpxArenaBase + 512 + (StopReason));
        memory.Write32(cpxArenaBase + 512 + (StopReason), 0);
        RetireMediaCursors();
        if (memory.Read32(cpxArenaBase + 512 + (Phase)) == 20 && (memory.Read32(cpxArenaBase + 512 + (Action))is 82 or 87))
            return FinishTransfer(error);
        return Finish(memory.Read32(cpxArenaBase + 512 + (Action))is 82 or 87 or 1008 or 1022 ? uint.MaxValue : 0, error);
    }

    private StepResult BeginInhibit()
    {
        uint cpxArenaBase = arena;
        if (Arg(0) != 0 && (memory.Read32(cpxArenaBase + 512 + (Inhibited)) != 0))
            return Finish(uint.MaxValue);
        if (Arg(0) == 0 && (!(memory.Read32(cpxArenaBase + 512 + (Inhibited)) != 0) && memory.Read32(cpxArenaBase + 512 + (MediaReason)) == 0 && engine.IsMounted))
            return Finish(uint.MaxValue);
        if (HasOpenResources || memory.Read32(cpxArenaBase + 512 + (RecordRetained)) != 0)
            return Finish(0, 202);
        memory.Write32(cpxArenaBase + 512 + (Phase), 122);
        return StepResult.Pending;
    }

    private StepResult AdmitInhibit()
    {
        uint cpxArenaBase = arena;
        if (Arg(0) != 0)
        {
            if (!(!(memory.Read32(cpxArenaBase + 512 + (Inhibited)) != 0) && memory.Read32(cpxArenaBase + 512 + (MediaReason)) == 0 && engine.IsMounted))
            {
                memory.Write32(cpxArenaBase + 512 + (Inhibited), 1);
                RetireMediaCursors();
                SuspendWatches();
                return Finish(uint.MaxValue);
            }

            return Start((engine.IsReadOnly || memory.Read32(cpxArenaBase + 512 + (SoftwareProtected)) != 0) || engine.BufferedWrites ? engine.BeginPersistenceBarrier() : engine.BeginOperation(CpxOperation.Flush), 120);
        }

        // The old object IDs/cursors are never reused against replacement media.
        RetireMediaCursors();
        memory.Write32(cpxArenaBase + 512 + (MediaReason), 226);
        return Start(engine.BeginRemount(), 121);
    }

    private StepResult MediaStep()
    {
        uint cpxArenaBase = arena;
        if (memory.Read32(cpxArenaBase + 512 + (Phase)) == 122)
            return AdmitInhibit();
        StepResult result = engine.Step();
        if (result is StepResult.Pending or StepResult.NeedIo)
            return result;
        if (result == StepResult.Failed)
        {
            memory.Write32(cpxArenaBase + 512 + (MediaReason), 213);
            return Finish(0, ToDosError(engine.Error));
        }

        if (memory.Read32(cpxArenaBase + 512 + (Phase)) == 120)
        {
            memory.Write32(cpxArenaBase + 512 + (Inhibited), 1);
            RetireMediaCursors();
            SuspendWatches();
        }
        else
        {
            memory.Write32(cpxArenaBase + 512 + (Inhibited), 0);
            memory.Write32(cpxArenaBase + 512 + (MediaReason), 0);
            RefreshWatches();
        }

        return Finish(uint.MaxValue);
    }

    private StepResult OfflineInformation()
    {
        uint cpxArenaBase = arena;
        uint bptr = memory.Read32(cpxArenaBase + 512 + (Action)) == 25 ? Arg(0) : Arg(1);
        if (bptr > 0x3fffffff || !Buffer(bptr << 2, 36))
            return Finish(0, 115);
        if (memory.Read32(cpxArenaBase + 512 + (Action)) == 26 && !LockId(Arg(0), out _))
            return Finish(0, 211);
        uint p = bptr << 2;
        for (uint n = 0; n < 36; n += 4)
            memory.Write32(p + n, 0);
        memory.Write32(p + 24, uint.MaxValue);
        return Finish(uint.MaxValue);
    }
}