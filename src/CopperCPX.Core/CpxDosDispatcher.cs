namespace CopperCPX;
/// <summary>Single-owner DOS packet continuation. The OS message remains queued
/// or owned by the caller until Complete/Failed; this class never allocates.</summary>
public partial struct CpxDosDispatcher<TMemory>
    where TMemory : struct, ICpxMemory
{
    private TMemory memory;
    private CpxEngine<TMemory> engine;
    private FixedPools<TMemory> pools;
    private uint arena;
    private const uint Packet = 0, Phase = 4, BusyAt = 8, Port = 12, Volume = 16, Handle = 20, Object = 24, PathLength = 32, PathAt = 36, ResultAt = 40, ErrorAt = 44, Action = 48, PathEnd = 52, Destination = 56, ParentId = 64, OldParent = 72, OldLength = 80, Reserved = 84, ReservedKind = 88, ResizeResult = 92;
    private uint State => arena + 512;
    private uint Path => arena + 1024;
    private uint OldName => arena + 1280;
    private uint Pool => arena + CpxArena.Scratch + 6 * engine.MetadataPageBytes + CpxArena.IndexBytes;

    public CpxDosDispatcher(TMemory memory, uint arena)
    {
        this.memory = memory;
        this.arena = arena;
        engine = new(memory, arena);
        pools = new(memory, arena + CpxArena.Scratch + 6 * engine.MetadataPageBytes + CpxArena.IndexBytes);
    }

    private uint R(uint offset) => memory.Read32((arena + 512) + offset);
    private void W(uint offset, uint value) => memory.Write32((arena + 512) + offset, value);
    private Wide G(uint p) => new(memory.Read32(p), memory.Read32(p + 4));
    private void Put(uint p, Wide v)
    {
        memory.Write32(p, v.High);
        memory.Write32(p + 4, v.Low);
    }

    private uint Arg(uint index) => memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + index * 4);
    public bool Busy => memory.Read32(arena + 512 + (BusyAt)) != 0;

    public bool HasOpenResources
    {
        get
        {
            uint pool = arena + CpxArena.Scratch + 6 * engine.MetadataPageBytes + CpxArena.IndexBytes;
            for (uint n = 0; n < 32; n++)
                if (memory.Read32(pool + n * 64) != 0 || memory.Read32(pool + 2048 + n * 48) != 0)
                    return true;
            return false;
        }
    }

    public uint Result => memory.Read32(arena + 512 + (ResultAt));
    public uint DosError => memory.Read32(arena + 512 + (ErrorAt));
    public CpxIo Request => engine.Request;

    public CpxError CompleteIo(uint token, uint bytes, bool success) => engine.CompleteIo(token, bytes, success);
    public void Initialize(uint port, uint volume)
    {
        InitializeOnClearedPools(port, volume);
        pools.Initialize();
    }

    // Native startup owns fresh MEMF_CLEAR pool storage. Mount/recovery uses
    // scratch and never takes a DOS slot before publication.
    public void InitializeOnClearedPools(uint port, uint volume)
    {
        uint cpxArenaBase = arena;
        uint serial = memory.Read32(cpxArenaBase + 512 + (180)) == CpxArena.Magic ? memory.Read32(cpxArenaBase + 512 + (EnumSerial)) : 0;
        for (uint n = 0; n < 512; n += 4)
            memory.Write32(cpxArenaBase + 512 + (n), 0);
        memory.Write32(cpxArenaBase + 512 + (EnumSerial), serial);
        memory.Write32(cpxArenaBase + 512 + (180), CpxArena.Magic);
        memory.Write32(cpxArenaBase + 512 + (Port), port);
        memory.Write32(cpxArenaBase + 512 + (Volume), volume);
    }

    public CpxError BeginPacket(uint packet)
    {
        uint cpxArenaBase = arena;
        if ((memory.Read32(cpxArenaBase + 512 + (BusyAt)) != 0))
            return CpxError.Busy;
        if (packet == 0 || (packet & 1) != 0 || packet > uint.MaxValue - 48)
            return CpxError.InvalidArgument;
        memory.Write32(cpxArenaBase + 512 + (Packet), packet);
        memory.Write32(cpxArenaBase + 512 + (Phase), 0);
        memory.Write32(cpxArenaBase + 512 + (BusyAt), 1);
        uint action = memory.Read32(packet + 8);
        memory.Write32(cpxArenaBase + 512 + (Action), action);
        memory.Write32(cpxArenaBase + 512 + (ResultAt), 0);
        memory.Write32(cpxArenaBase + 512 + (ErrorAt), 0);
        memory.Write32(cpxArenaBase + 512 + (Handle), 0);
        memory.Write32(cpxArenaBase + 512 + (Destination), 0);
        memory.Write32(cpxArenaBase + 512 + (Reserved), 0);
        memory.Write32(cpxArenaBase + 512 + (EnumSlot), 0);
        memory.Write32(cpxArenaBase + 512 + (EnumNew), 0);
        Put((cpxArenaBase + 512) + ParentId, default);
        Put((cpxArenaBase + 512) + EntryId, default);
        memory.Write32(cpxArenaBase + 512 + (Deferred), 0);
        memory.Write32(cpxArenaBase + 512 + (NotifyInternal), 0);
        memory.Write32(cpxArenaBase + 512 + (NotifySlot), 0);
        memory.Write32(cpxArenaBase + 512 + (NotifyRemoved), 64);
        memory.Write32(cpxArenaBase + 512 + (AllActive), 0);
        // Preserve the complete packet reset, but avoid a separate dispatch
        // phase when the existing capability admits this transfer outright.
        // Begin still issues no I/O; Step retains cancellation and phase bounds.
        bool transferred = action is 82 or 87 && TryBeginCachedTransferPacket(packet, action);
        if (!transferred) engine.RetireAppendLease();
        return CpxError.None;
    }

    private StepResult Finish(uint value, uint error = 0)
    {
        uint cpxArenaBase = arena;
        if (memory.Read32(cpxArenaBase + 512 + (Action)) == TrimPacket && memory.Read32(cpxArenaBase + 512 + (Phase)) == 160)
            PublishTrimResult();
        if (memory.Read32(cpxArenaBase + 512 + (Action)) == 1033)
            FinishExAll(error);
        if (memory.Read32(cpxArenaBase + 512 + (Action)) == 4097)
            FinishNotification(ref value, ref error);
        else
            NotificationMutation(value, error);
        if ((memory.Read32(cpxArenaBase + 512 + (Action))is 1024 or 26406) && error != 0 && value == 0)
            value = uint.MaxValue;
        if (error != 0 && memory.Read32(cpxArenaBase + 512 + (EnumNew)) != 0)
        {
            uint e = memory.Read32(cpxArenaBase + 512 + (EnumSlot));
            pools.Release(ResourceKind.Enumeration, e, memory.Read32(e - 4));
            memory.Write32(cpxArenaBase + 512 + (EnumNew), 0);
        }

        if (memory.Read32(cpxArenaBase + 512 + (Reserved)) != 0)
        {
            pools.Release((ResourceKind)memory.Read32(cpxArenaBase + 512 + (ReservedKind)), memory.Read32(cpxArenaBase + 512 + (Reserved)), memory.Read32(memory.Read32(cpxArenaBase + 512 + (Reserved)) - 4));
            memory.Write32(cpxArenaBase + 512 + (Reserved), 0);
        }

        memory.Write32(cpxArenaBase + 512 + (ResultAt), value);
        memory.Write32(cpxArenaBase + 512 + (ErrorAt), error);
        memory.Write32(cpxArenaBase + 512 + (BusyAt), 0);
        memory.Write32(memory.Read32(cpxArenaBase + 512 + (Packet)) + 12, value);
        memory.Write32(memory.Read32(cpxArenaBase + 512 + (Packet)) + 16, error);
        return StepResult.Complete;
    }

    public static uint ToDosError(CpxError error) => error switch
    {
        CpxError.None => 0,
        CpxError.NotFound => 205,
        CpxError.WrongType => 212,
        CpxError.Protected => 223,
        CpxError.ReadOnly => 214,
        CpxError.ResourceExhausted => 103,
        CpxError.DiskFull or CpxError.ReserveExhausted => 221,
        CpxError.InvalidArgument => 115,
        CpxError.Unsupported => 209,
        CpxError.Busy => 202,
        CpxError.NotMounted => 218,
        CpxError.AlreadyExists => 203,
        CpxError.DirectoryNotEmpty => 216,
        CpxError.TreeTooDeep => 221,
        _ => 213
    };
    private StepResult Start(CpxError error, uint next)
    {
        uint cpxArenaBase = arena;
        if (error != CpxError.None)
            return Finish(memory.Read32(cpxArenaBase + 512 + (Action))is 82 or 87 or 1008 or 1022 ? uint.MaxValue : 0, ToDosError(error));
        memory.Write32(cpxArenaBase + 512 + (Phase), next);
        return StepResult.Pending;
    }

    private bool ValidResource(uint p, ResourceKind kind)
    {
        uint start = (arena + CpxArena.Scratch + 6 * engine.MetadataPageBytes + CpxArena.IndexBytes) + (kind == ResourceKind.Handle ? 0u : 2048u), width = kind == ResourceKind.Handle ? 64u : 48u;
        for (uint n = 0; n < 32; n++)
            if (p == start + n * width + 8)
                return memory.Read32(p - 8) == 1;
        return false;
    }

    private bool LockId(uint bptr, out Wide id)
    {
        id = Wide.From(1);
        if (bptr == 0)
            return true;
        if (bptr > 0x3fffffff)
            return false;
        uint p = bptr << 2;
        if (!ValidResource(p, ResourceKind.Lock))
            return false;
        id = (new CopperCPX.Wide(memory.Read32((p + 20)), memory.Read32((p + 20) + 4)));
        return true;
    }

    private bool Conflicts(Wide id, bool exclusive)
    {
        uint cpxArenaBase = arena;
        uint pool = cpxArenaBase + CpxArena.Scratch + 6 * engine.MetadataPageBytes + CpxArena.IndexBytes;
        for (uint n = 0; n < 32; n++)
        {
            uint h = pool + n * 64, l = pool + 2048 + n * 48;
            if (memory.Read32(h) == 1 && Wide.Equal((new CopperCPX.Wide(memory.Read32((h + 8)), memory.Read32((h + 8) + 4))), id) && (exclusive || memory.Read32(h + 24) == uint.MaxValue))
                return true;
            if (memory.Read32(l) == 1 && Wide.Equal((new CopperCPX.Wide(memory.Read32((l + 28)), memory.Read32((l + 28) + 4))), id) && (exclusive || memory.Read32(l + 16) == uint.MaxValue))
                return true;
        }

        return false;
    }

    private StepResult MakeLock(Wide id, uint access)
    {
        uint cpxArenaBase = arena;
        if (access is not (0xfffffffe or 0xffffffff))
            return Finish(0, 115);
        Put((cpxArenaBase + 512) + Object, id);
        memory.Write32(cpxArenaBase + 512 + (LockAccess), access);
        memory.Write32(cpxArenaBase + 512 + (Phase), 78);
        return StepResult.Pending;
    }

    private StepResult StartPath(uint lockBptr, uint nameBptr)
    {
        uint cpxArenaBase = arena;
        if (!LockId(lockBptr, out var id))
            return Finish(0, 211);
        if (lockBptr != 0)
        {
            uint context = LockContext(lockBptr << 2);
            Context(context, context + 8);
        }

        if (nameBptr == 0 || nameBptr > 0x3fffffff)
            return Finish(0, 115);
        uint p = nameBptr << 2, length = memory.Read8(p), start = 0;
        for (uint n = 0; n < length; n++)
        {
            byte c = memory.Read8(p + 1 + n);
            if (c == 0)
                return Finish(0, 210);
            memory.Write8((cpxArenaBase + 1024) + n, c);
            if (c == 58)
            {
                id = Wide.From(1);
                start = n + 1;
            }
        }

        Put((cpxArenaBase + 512) + Object, id);
        memory.Write32(cpxArenaBase + 512 + (PathLength), length);
        memory.Write32(cpxArenaBase + 512 + (PathAt), start);
        memory.Write32(cpxArenaBase + 512 + (Phase), 10);
        return StepResult.Pending;
    }

    public StepResult Step()
    {
        uint budget = engine.CpuPhaseBudget;
        for (uint turn = 0; turn < budget; turn++)
        {
            StepResult result = StepOnce();
            if (result != StepResult.Pending) return result;
        }
        return StepResult.Pending;
    }

    private StepResult StepOnce()
    {
        uint cpxArenaBase = arena;
        if (!(memory.Read32(cpxArenaBase + 512 + (BusyAt)) != 0))
            return StepResult.Complete;
        if ((memory.Read32(cpxArenaBase + 512 + (Phase)) == MaintenancePhase))
            return MaintenanceStep();
        if ((memory.Read32(cpxArenaBase + 512 + (StopReason)) != 0))
            return StopStorageStep();
        uint phase = memory.Read32(cpxArenaBase + 512 + (Phase)), action = memory.Read32(cpxArenaBase + 512 + (Action));
        if (phase == 0)
            return Dispatch();
        if (phase == 160)
            return ManualTrimPacketStep();
        if (phase >= 140)
            return NewReadLinkStep();
        if (phase >= 130)
            return VolumeStep();
        if (phase >= 120)
            return MediaStep();
        if (phase >= 110)
            return RecordStep();
        if (phase >= 100)
            return NotificationStep();
        if (phase >= 90)
            return LinkStep();
        if (phase >= 70)
            return LifecycleStep();
        if (phase >= 40)
            return InformationStep();
        if (phase == 20)
            return TransferPacketStep(action);
        return OrdinaryPacketStep(phase, action);
    }

    private StepResult TransferPacketStep(uint action)
    {
        StepResult result = engine.Step();
        if (result is StepResult.Pending or StepResult.NeedIo) return result;
        return FinishTransfer(result == StepResult.Failed ?
            action == 82 && engine.Error == CpxError.Protected ? 224u : ToDosError(engine.Error) : 0);
    }

    private StepResult OrdinaryPacketStep(uint phase, uint action)
    {
        uint cpxArenaBase = arena;
        if (phase == 10)
        {
            if (action == 26406)
                return NewLinkComponent();
            uint at = memory.Read32(cpxArenaBase + 512 + (PathAt)), end = at;
            if (at == memory.Read32(cpxArenaBase + 512 + (PathLength)))
                return Start(engine.BeginOperation(CpxOperation.ReadObject, (new CopperCPX.Wide(memory.Read32(((cpxArenaBase + 512) + Object)), memory.Read32(((cpxArenaBase + 512) + Object) + 4)))), 12);
            while (end < memory.Read32(cpxArenaBase + 512 + (PathLength)) && memory.Read8((cpxArenaBase + 1024) + end) != 47)
                end++;
            if (end == at)
                return Start(engine.BeginOperation(CpxOperation.ReadObject, (new CopperCPX.Wide(memory.Read32(((cpxArenaBase + 512) + Object)), memory.Read32(((cpxArenaBase + 512) + Object) + 4)))), 14);
            Put((cpxArenaBase + 512) + ParentId, (new CopperCPX.Wide(memory.Read32(((cpxArenaBase + 512) + Object)), memory.Read32(((cpxArenaBase + 512) + Object) + 4))));
            memory.Write32(cpxArenaBase + 512 + (PathEnd), end);
            if (end == memory.Read32(cpxArenaBase + 512 + PathLength) && action is 16 or 17 or 22 or 1004 or 1006 or 1021)
                return Start(engine.BeginLookup((new CopperCPX.Wide(memory.Read32(((cpxArenaBase + 512) + Object)), memory.Read32(((cpxArenaBase + 512) + Object) + 4))), (cpxArenaBase + 1024) + at, end - at), 11);
            return Start(engine.BeginLookupObject((new CopperCPX.Wide(memory.Read32(((cpxArenaBase + 512) + Object)), memory.Read32(((cpxArenaBase + 512) + Object) + 4))), (cpxArenaBase + 1024) + at, end - at), 33);
        }

        StepResult result = engine.Step();
        if (result is StepResult.Pending or StepResult.NeedIo)
            return result;
        if (result == StepResult.Failed)
        {
            if (phase == 20)
                return FinishTransfer(action == 82 && engine.Error == CpxError.Protected ? 224u : ToDosError(engine.Error));
            return Finish(action is 1008 or 1022 ? uint.MaxValue : 0, ToDosError(engine.Error));
        }

        switch (phase)
        {
            case 11:
            case 33:
                if (memory.Read32(cpxArenaBase + 512 + (PathEnd)) == memory.Read32(cpxArenaBase + 512 + (PathLength)) && (action is 16 or 17 or 22 or 1004 or 1006 or 1021))
                {
                    memory.Write32(cpxArenaBase + 512 + (Phase), 83);
                    return StepResult.Pending;
                }

                if (!engine.LookupFound)
                    return Finish(0, 205);
                if (phase == 33) Put((cpxArenaBase + 512) + EntryId, engine.LookupObjectEntry);
                else Put((cpxArenaBase + 512) + EntryId, (new CopperCPX.Wide(memory.Read32((engine.TreeRecord + 8)), memory.Read32((engine.TreeRecord + 8) + 4))));
                memory.Write32(cpxArenaBase + 512 + (LinkPrefix), memory.Read32(cpxArenaBase + 512 + (PathAt)));
                memory.Write32(cpxArenaBase + 512 + (LinkSuffix), memory.Read32(cpxArenaBase + 512 + (PathEnd)));
                Put((cpxArenaBase + 512) + Object, engine.LookupId);
                memory.Write32(cpxArenaBase + 512 + (PathAt), memory.Read32(cpxArenaBase + 512 + (PathEnd)) < memory.Read32(cpxArenaBase + 512 + (PathLength)) ? memory.Read32(cpxArenaBase + 512 + (PathEnd)) + 1 : memory.Read32(cpxArenaBase + 512 + (PathEnd)));
                if (phase == 33) return PathObject();
                return Start(engine.BeginOperation(CpxOperation.ReadObject, engine.LookupId), 13);
            case 12:
                if (!engine.ObjectFound)
                    return Finish(0, 205);
                return Resolved();
            case 13:
                return PathObject();
            case 14:
                if (!engine.ObjectFound)
                    return Finish(0, 205);
                Wide ancestor = memory.Read8(engine.ObjectRecord + 84) == 2 ? (new CopperCPX.Wide(memory.Read32((engine.ObjectRecord + 40)), memory.Read32((engine.ObjectRecord + 40) + 4))) : (new CopperCPX.Wide(memory.Read32(((cpxArenaBase + 512) + ParentId)), memory.Read32(((cpxArenaBase + 512) + ParentId) + 4)));
                if (action == 26406 && Wide.Equal((new CopperCPX.Wide(memory.Read32(((cpxArenaBase + 512) + Object)), memory.Read32(((cpxArenaBase + 512) + Object) + 4))), Wide.From(1)))
                    ancestor = Wide.From(1);
                else if (ancestor.IsZero || Wide.Equal((new CopperCPX.Wide(memory.Read32(((cpxArenaBase + 512) + Object)), memory.Read32(((cpxArenaBase + 512) + Object) + 4))), Wide.From(1)))
                    return Finish(0, 205);
                Put((cpxArenaBase + 512) + Object, ancestor);
                Put((cpxArenaBase + 512) + ParentId, default);
                Put((cpxArenaBase + 512) + EntryId, default);
                memory.Write32(cpxArenaBase + 512 + (PathAt), memory.Read32(cpxArenaBase + 512 + (PathAt)) + 1);
                memory.Write32(cpxArenaBase + 512 + (Phase), 10);
                return StepResult.Pending;
            case 20:
                return FinishTransfer(0);
            case 21:
                if (action == 27 && !engine.PreferIdleDiscard && !(engine.IsReadOnly || memory.Read32(cpxArenaBase + 512 + (SoftwareProtected)) != 0))
                    return Start(engine.BeginOperation(CpxOperation.TrimFree), 26);
                return Finish(uint.MaxValue);
            case 22:
                return Finish((memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (1) * 4)));
            case 23:
                return SeekFinish();
            case 24:
                if (!engine.ObjectFound)
                    return Finish(0, 205);
                Wide parent = memory.Read8(engine.ObjectRecord + 84) == 2 ? (new CopperCPX.Wide(memory.Read32((engine.ObjectRecord + 40)), memory.Read32((engine.ObjectRecord + 40) + 4))) : (new CopperCPX.Wide(memory.Read32(((cpxArenaBase + 512) + ParentId)), memory.Read32(((cpxArenaBase + 512) + ParentId) + 4)));
                if (parent.IsZero)
                    return Finish(0, 213);
                return Start(engine.BeginOperation(CpxOperation.ReadObject, parent), 70);
            case 25:
                memory.Write32(memory.Read32(cpxArenaBase + 512 + (Handle)) + 40, 1);
                Wide resized = (new CopperCPX.Wide(memory.Read32(((cpxArenaBase + 512) + ResizeResult)), memory.Read32(((cpxArenaBase + 512) + ResizeResult) + 4)));
                if (Wide.Compare(G(memory.Read32(cpxArenaBase + 512 + (Handle)) + 8), resized) > 0)
                    Put(memory.Read32(cpxArenaBase + 512 + (Handle)) + 8, resized);
                if (action == 26401)
                {
                    Put((memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (3) * 4)), resized);
                    return Finish(uint.MaxValue);
                }

                return Finish(resized.Low);
            case 26:
                return Finish(uint.MaxValue);
            case 30:
                Put((cpxArenaBase + 512) + Object, engine.MutatedObject);
                Put((cpxArenaBase + 512) + EntryId, engine.CreatedEntryId);
                if (action == 22)
                {
                    MarkNotificationChange(engine.MutatedObject, (new CopperCPX.Wide(memory.Read32(((cpxArenaBase + 512) + ParentId)), memory.Read32(((cpxArenaBase + 512) + ParentId) + 4))), default);
                    return MakeLock(engine.MutatedObject, 0xfffffffe);
                }

                return OpenHandle();
            case 31:
                return Finish(uint.MaxValue);
            case 32:
                return OpenHandle();
            default:
                return Finish(0, 209);
        }
    }

    private StepResult FinishTransfer(uint error)
    {
        uint cpxArenaBase = arena;
        uint count = engine.TransferredBytes, p = memory.Read32(cpxArenaBase + 512 + (Handle));
        if (memory.Read32(cpxArenaBase + 512 + (Action)) == 87 && count != 0)
            memory.Write32(p + 40, 1);
        if (count != 0)
        {
            Wide.Add((new CopperCPX.Wide(memory.Read32((p + 8)), memory.Read32((p + 8) + 4))), Wide.From(count), out var next);
            Put(p + 8, next);
        }

        return Finish(error != 0 && count == 0 ? uint.MaxValue : count, error);
    }

    private StepResult Resolved()
    {
        uint cpxArenaBase = arena;
        uint action = memory.Read32(cpxArenaBase + 512 + (Action)), record = engine.ObjectRecord;
        Wide id = (new CopperCPX.Wide(memory.Read32(((cpxArenaBase + 512) + Object)), memory.Read32(((cpxArenaBase + 512) + Object) + 4)));
        if (memory.Read8(record + 84) == 3 && (action is 8 or 1004 or 1005 or 1006))
            return Finish(0, 233);
        if (action == 1024)
            return PathObject();
        switch (action)
        {
            case 8:
                return MakeLock(id, (memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (2) * 4)));
            case 4097:
                return Finish(uint.MaxValue);
            case 1004:
            case 1005:
            case 1006:
                if (memory.Read8(record + 84) != 1)
                    return Finish(0, 212);
                if ((memory.Read32(record + 72) & (action == 1005 ? 8u : action == 1006 ? 5u : 12u)) != 0)
                    return Finish(0, action == 1005 ? 224u : 223u);
                if ((memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (0) * 4)) == 0 || (memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (0) * 4)) > 0x3fffffff)
                    return Finish(0, 115);
                memory.Write32(cpxArenaBase + 512 + (Phase), 81);
                return StepResult.Pending;
            case 21:
                return Start(engine.BeginProtection(id, (memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (3) * 4))), 21);
            case 1036:
                return Start(engine.BeginOwner(id, (memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (3) * 4))), 21);
            case 28:
                uint comment = (memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (3) * 4));
                if (comment == 0 || comment > 0x3fffffff)
                    return Finish(0, 115);
                uint text = comment << 2;
                return Start(engine.BeginComment(id, text + 1, memory.Read8(text)), 21);
            case 34:
                uint date = (memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (3) * 4));
                if (date == 0 || (date & 1) != 0)
                    return Finish(0, 115);
                return Start(engine.BeginDate(id, memory.Read32(date), memory.Read32(date + 4), memory.Read32(date + 8)), 21);
            case 23:
            case 1034:
            case 26408:
            case 26410:
                return Examine();
            default:
                return Finish(0, 209);
        }
    }

    private bool Reserve(ResourceKind kind)
    {
        uint cpxArenaBase = arena;
        if (pools.Take(kind, out uint address, out _) != CpxError.None)
            return false;
        memory.Write32(cpxArenaBase + 512 + (Reserved), address);
        memory.Write32(cpxArenaBase + 512 + (ReservedKind), (uint)kind);
        return true;
    }

    private StepResult OpenHandle()
    {
        uint cpxArenaBase = arena;
        uint handle = memory.Read32(cpxArenaBase + 512 + (Reserved));
        if (handle == 0)
            return Finish(0, 103);
        memory.Write32(cpxArenaBase + 512 + (Reserved), 0);
        Put(handle, (new CopperCPX.Wide(memory.Read32(((cpxArenaBase + 512) + Object)), memory.Read32(((cpxArenaBase + 512) + Object) + 4))));
        memory.Write32(handle + 16, memory.Read32(cpxArenaBase + 512 + (Action)) == 1006 ? uint.MaxValue : 0xfffffffe);
        memory.Write32(handle + 20, memory.Read32(cpxArenaBase + 512 + (Action)) == 1005 ? 0u : 1u);
        Put(handle + 24, (new CopperCPX.Wide(memory.Read32(((cpxArenaBase + 512) + ParentId)), memory.Read32(((cpxArenaBase + 512) + ParentId) + 4))));
        Put(handle + 32, (new CopperCPX.Wide(memory.Read32(((cpxArenaBase + 512) + EntryId)), memory.Read32(((cpxArenaBase + 512) + EntryId) + 4))));
        if (memory.Read32(cpxArenaBase + 512 + (Phase))is 30 or 32)
            memory.Write32(handle + 40, 1);
        uint fh = (memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (0) * 4)) << 2;
        memory.Write32(fh + 8, memory.Read32(cpxArenaBase + 512 + (Port)));
        memory.Write32(fh + 36, handle);
        return Finish(uint.MaxValue);
    }

    private StepResult FinalComponent()
    {
        uint cpxArenaBase = arena;
        uint action = memory.Read32(cpxArenaBase + 512 + (Action)), at = memory.Read32(cpxArenaBase + 512 + (PathAt)), length = memory.Read32(cpxArenaBase + 512 + (PathLength)) - at;
        Wide parent = (new CopperCPX.Wide(memory.Read32(((cpxArenaBase + 512) + ParentId)), memory.Read32(((cpxArenaBase + 512) + ParentId) + 4)));
        if (action == 1021)
            return CreateLink();
        if (action == 17 && memory.Read32(cpxArenaBase + 512 + (Destination)) != 0)
        {
            if (engine.LookupFound && Conflicts(engine.LookupId, true))
                return Finish(0, 202);
            memory.Write32(cpxArenaBase + 512 + (Phase), 84);
            return StepResult.Pending;
        }

        if (action == 22 || !engine.LookupFound && (action is 1004 or 1006))
        {
            if (engine.LookupFound)
                return Finish(0, 203);
            if (action != 22 && ((memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (0) * 4)) == 0 || (memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (0) * 4)) > 0x3fffffff))
                return Finish(0, 115);
            if (!Reserve(action == 22 ? ResourceKind.Lock : ResourceKind.Handle))
                return Finish(0, 103);
            return Start(engine.BeginCreate(parent, (cpxArenaBase + 1024) + at, length, action == 22), 30);
        }

        if (!engine.LookupFound)
            return Finish(0, 205);
        Put((cpxArenaBase + 512) + EntryId, (new CopperCPX.Wide(memory.Read32((engine.TreeRecord + 8)), memory.Read32((engine.TreeRecord + 8) + 4))));
        if (action is 16 or 17)
        {
            if (Conflicts(engine.LookupId, true))
                return Finish(0, 202);
            memory.Write32(cpxArenaBase + 512 + (Phase), 85);
            return StepResult.Pending;
        }

        Put((cpxArenaBase + 512) + Object, engine.LookupId);
        return Start(engine.BeginOperation(CpxOperation.ReadObject, engine.LookupId), 12);
    }

    private StepResult SeekFinish(uint cachedRecord = 0)
    {
        uint cpxArenaBase = arena;
        bool wide = memory.Read32(cpxArenaBase + 512 + (Action))is 26400 or 26401, resize = memory.Read32(cpxArenaBase + 512 + (Action))is 1022 or 26401;
        uint failure = wide ? 0u : uint.MaxValue;
        if (cachedRecord == 0 && !engine.ObjectFound)
            return Finish(failure, 205);
        uint record = cachedRecord != 0 ? cachedRecord : engine.ObjectRecord;
        uint h = memory.Read32(cpxArenaBase + 512 + (Handle)), mode = (memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (2) * 4));
        Wide old = (new CopperCPX.Wide(memory.Read32((h + 8)), memory.Read32((h + 8) + 4))), basis, offset;
        if (wide)
        {
            if ((memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (1) * 4)) == 0 || (memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (3) * 4)) == 0 || (((memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (1) * 4)) | (memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (3) * 4))) & 1) != 0 || (memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (1) * 4)) > uint.MaxValue - 8 || (memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (3) * 4)) > uint.MaxValue - 8)
                return Finish(0, 115);
            offset = G((memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (1) * 4)));
        }
        else
            offset = new(((memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (1) * 4)) & 0x80000000) != 0 ? uint.MaxValue : 0, (memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (1) * 4)));
        if (mode == uint.MaxValue)
            basis = default;
        else if (mode == 0)
            basis = old;
        else if (mode == 1)
            basis = (new CopperCPX.Wide(memory.Read32((record + 8)), memory.Read32((record + 8) + 4)));
        else
            return Finish(failure, 219);
        Wide next;
        bool valid;
        if ((offset.High & 0x80000000) == 0)
            valid = Wide.Add(basis, offset, out next);
        else
        {
            Wide.Add(new(~offset.High, ~offset.Low), Wide.From(1), out var magnitude);
            valid = Wide.Subtract(basis, magnitude, out next);
        }

        if (!valid || !wide && (next.High != 0 || next.Low > int.MaxValue || old.High != 0 || old.Low > int.MaxValue))
            return Finish(failure, 219);
        if (resize)
        {
            if (memory.Read32(h + 20) == 0)
                return Finish(failure, 223);
            uint pool = cpxArenaBase + CpxArena.Scratch + 6 * engine.MetadataPageBytes + CpxArena.IndexBytes;
            for (uint n = 0; n < 32; n++)
            {
                uint other = pool + n * 64 + 8;
                if (other != h && memory.Read32(other - 8) == 1 && Wide.Equal((new CopperCPX.Wide(memory.Read32((other)), memory.Read32((other) + 4))), (new CopperCPX.Wide(memory.Read32((h)), memory.Read32((h) + 4)))) && Wide.Compare((new CopperCPX.Wide(memory.Read32((other + 8)), memory.Read32((other + 8) + 4))), next) > 0)
                    next = (new CopperCPX.Wide(memory.Read32((other + 8)), memory.Read32((other + 8) + 4)));
            }

            if (!wide && (next.High != 0 || next.Low > int.MaxValue))
                return Finish(failure, 219);
            CpxError err = engine.BeginResize((new CopperCPX.Wide(memory.Read32((h)), memory.Read32((h) + 4))), next);
            if (err != CpxError.None)
                return Finish(failure, ToDosError(err));
            Put((cpxArenaBase + 512) + ResizeResult, next);
            memory.Write32(cpxArenaBase + 512 + (Phase), 25);
            return StepResult.Pending;
        }

        if (Wide.Compare(next, (new CopperCPX.Wide(memory.Read32((record + 8)), memory.Read32((record + 8) + 4)))) > 0)
            return Finish(failure, 219);
        if (wide)
            Put((memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (3) * 4)), old);
        Put(h + 8, next);
        return Finish(wide ? uint.MaxValue : old.Low);
    }

    private StepResult Dispatch()
    {
        uint cpxArenaBase = arena;
        uint a = (memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (0) * 4)), action = memory.Read32(cpxArenaBase + 512 + (Action));
        Wide id;
        if (!(!(memory.Read32(cpxArenaBase + 512 + (Inhibited)) != 0) && memory.Read32(cpxArenaBase + 512 + (MediaReason)) == 0 && engine.IsMounted))
        {
            if (action is 25 or 26)
                return OfflineInformation();
            if (action == 7)
                return Finish(0);
            if (action == 32)
                return Finish(uint.MaxValue);
            // Like the SFS media gate, report NoDisk before dispatching WaitChar,
            // Timer and DiskChange. On mounted media all three use the unknown-action reply.
            if (action is TrimPacket or 8 or 9 or 16 or 17 or 20 or 21 or 22 or 23 or 24 or 27 or 28 or 29 or 30 or 33 or 34 or 40 or 82 or 87 or 1004 or 1005 or 1006 or 1008 or 1021 or 1022 or 1023 or 1024 or 1026 or 1028 or 1030 or 1031 or 1033 or 1034 or 1036 or 2008 or 26400 or 26401 or 26402 or 26406 or 26407 or 26408 or 26409 or 26410)
                return Finish(action is 82 or 87 or 1008 or 1022 ? uint.MaxValue : 0, 226);
        }

        if ((engine.IsReadOnly || memory.Read32(cpxArenaBase + 512 + (SoftwareProtected)) != 0) && (action is 9 or 16 or 17 or 21 or 22 or 28 or 34 or 87 or 1004 or 1006 or 1020 or 1021 or 1022 or 1036 or 26401))
            return Finish(action is 87 or 1022 ? uint.MaxValue : 0, 214);
        switch (action)
        {
            case TrimPacket:
                return BeginManualTrimPacket();
            case 31:
                return BeginInhibit();
            case 9:
            case 1020:
                return BeginVolumePacket();
            case 1033:
                return BeginExAll();
            case 1035:
                return EndExAll();
            case 8:
            case 16:
            case 17:
            case 22:
                return StartPath(a, (memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (1) * 4)));
            case 1004:
            case 1005:
            case 1006:
                return StartPath((memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (1) * 4)), (memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (2) * 4)));
            case 21:
            case 28:
            case 34:
            case 1036:
                return StartPath((memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (1) * 4)), (memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (2) * 4)));
            case 82:
            case 87:
                if (!ValidResource(a, ResourceKind.Handle))
                    return Finish(uint.MaxValue, 211);
                if (action == 87 && memory.Read32(a + 20) == 0)
                    return Finish(uint.MaxValue, 223);
                if ((memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (2) * 4)) > int.MaxValue)
                    return Finish(uint.MaxValue, 115);
                memory.Write32(cpxArenaBase + 512 + (Handle), a);
                return Start(action == 82 ? engine.BeginFileRead((new CopperCPX.Wide(memory.Read32((a)), memory.Read32((a) + 4))), (new CopperCPX.Wide(memory.Read32((a + 8)), memory.Read32((a + 8) + 4))), (memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (1) * 4)), (memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (2) * 4))) : engine.BeginMappedWrite((new CopperCPX.Wide(memory.Read32((a)), memory.Read32((a) + 4))), (new CopperCPX.Wide(memory.Read32((a + 8)), memory.Read32((a + 8) + 4))), (memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (1) * 4)), (memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (2) * 4))), 20);
            case 1007:
                if (!ValidResource(a, ResourceKind.Handle))
                    return Finish(0, 211);
                memory.Write32(cpxArenaBase + 512 + (Handle), a);
                memory.Write32(cpxArenaBase + 512 + (Phase), 111);
                return StepResult.Pending;
            case 2008:
            case 2009:
            case 26402:
            case 26403:
                return BeginRecord();
            case 4097:
                return BeginAddNotification();
            case 4098:
                return RemoveNotification();
            case 1008:
            case 1022:
            case 26400:
            case 26401:
                if (!ValidResource(a, ResourceKind.Handle))
                    return Finish(action >= 26400 ? 0u : uint.MaxValue, 211);
                memory.Write32(cpxArenaBase + 512 + (Handle), a);
                uint cachedRecord = engine.CachedRegularFileRecord(G(a));
                if (cachedRecord != 0) return action == 1008 ? FinishCachedClassicSeek(a,cachedRecord) : SeekFinish(cachedRecord);
                return Start(engine.BeginOperation(CpxOperation.ReadObject, (new CopperCPX.Wide(memory.Read32((a)), memory.Read32((a) + 4)))), 23);
            case 15:
                if (a == 0)
                    return Finish(uint.MaxValue);
                if (a > 0x3fffffff || !ValidResource(a << 2, ResourceKind.Lock))
                    return Finish(0, 211);
                memory.Write32(cpxArenaBase + 512 + (LifeResource), a << 2);
                memory.Write32(cpxArenaBase + 512 + (Phase), 80);
                return StepResult.Pending;
            case 19:
                if (!LockId(a, out id))
                    return Finish(0, 211);
                if (a != 0)
                {
                    uint context = LockContext(a << 2);
                    Context(context, context + 8);
                }

                return MakeLock(id, 0xfffffffe);
            case 29:
                if (!LockId(a, out id))
                    return Finish(0, 211);
                if (Wide.Equal(id, Wide.From(1)))
                    return Finish(0);
                if (a != 0)
                {
                    uint context = LockContext(a << 2);
                    Context(context, context + 8);
                }

                return Start(engine.BeginOperation(CpxOperation.ReadObject, id), 24);
            case 1030:
            case 1031:
                if (!ValidResource(a, ResourceKind.Handle))
                    return Finish(0, 211);
                Context(a + 24, a + 32);
                id = (new CopperCPX.Wide(memory.Read32((a)), memory.Read32((a) + 4)));
                if (action == 1030)
                    return MakeLock(id, 0xfffffffe);
                return Start(engine.BeginOperation(CpxOperation.ReadObject, id), 24);
            case 1026:
                return BeginFromLock();
            case 1028:
                return BeginChangeMode();
            case 1023:
                return WriteProtection();
            case 26406:
                return BeginNewReadLink();
            case 1021:
            case 1024:
                return BeginLinkPacket();
            case 18:
                return Finish(0); // No persistent cache; all six pages are fixed operation scratch.
            case 40:
                if (!LockId(a, out id) || !LockId((memory.Read32(memory.Read32(arena + 512 + (Packet)) + 20 + (1) * 4)), out var second))
                    return Finish(0, 211);
                return Finish(Wide.Equal(id, second) ? uint.MaxValue : 0u);
            case 23:
            case 1034:
            case 26408:
            case 26410:
                if (action is 23 or 26408)
                {
                    if (!LockId(a, out id))
                        return Finish(0, 211);
                    if (a != 0)
                    {
                        uint context = LockContext(a << 2);
                        Context(context, context + 8);
                    }
                }
                else
                {
                    if (!ValidResource(a, ResourceKind.Handle))
                        return Finish(0, 211);
                    id = (new CopperCPX.Wide(memory.Read32((a)), memory.Read32((a) + 4)));
                    Context(a + 24, a + 32);
                }

                Put((cpxArenaBase + 512) + Object, id);
                return Start(engine.BeginOperation(CpxOperation.ReadObject, id), 12);
            case 24:
            case 26409:
                return BeginNext();
            case 25:
            case 26:
            case 26407:
                return BeginInformation();
            case 27:
                return Start((engine.IsReadOnly || memory.Read32(cpxArenaBase + 512 + (SoftwareProtected)) != 0) || engine.BufferedWrites ? engine.BeginPersistenceBarrier() : engine.BeginOperation(CpxOperation.Flush), 21);
            case 7:
                return Finish(memory.Read32(cpxArenaBase + 512 + (Volume)) >> 2);
            case 32:
                return Finish(CpxArena.Magic);
            case 1027:
                return Finish(uint.MaxValue);
            // Until the corresponding mutation or lifetime policy is present,
            // refuse the packet explicitly instead of reporting a false success.
            default:
                return Finish(0, 209);
        }
    }
}
