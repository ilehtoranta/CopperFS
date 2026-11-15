namespace CopperCPX;
/// <summary>Caller-owned continuation engine. All persistent state lives in Arena.
/// A Step never retains a CPU frame or a borrowed buffer after returning.</summary>
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    private TMemory memory;
    public uint Arena;
    // Mount context; operation state occupies the separately budgeted 8 KiB area.
    private const uint Signature = 0, SectorShift = 4, UnitShift = 8, PageBytes = 12, Units = 16, Identity = 24, Generation = 32, Mounted = 40, Poisoned = 44, Waiting = 48, Token = 52, ErrorAt = 56, OperationAt = 60, Phase = 64, Finished = 68, Capacity = 72, ReadOnlyAt = 80, Count = 84, Cursor = 88, Digest = 92, Candidate = 96, CheckpointOne = 104, CheckpointTwo = 112, ValidOne = 120, ValidTwo = 124, Target = 128, PendingKind = 136, PendingSector = 140, PendingBuffer = 148, PendingBytes = 152, NextToken = 156, IoReady = 160, CancelAt = 164, MountEpoch = 168, ScanIndex = 172, Active = 176, StagedCount = 180, StagedLimit = 184, SourcePage = 188, AbortOnly = 192, ObjectKey = 196, TreeAddress = 204, TreeDepth = 212, ExpectedLevel = 216, TreeSlot = 220, TreeCount = 224, TreeLevel = 228, FilePosition = 232, FileBytes = 240, FileValue = 244, TrimDisabled = 248, TrimCount = 252, TreeKindAt = 256, SeekAt = 260, SeekRoot = 264, SeekOwner = 272, SeekHigh = 280, SeekLow = 288, SeekDepth = 296, SeekExpected = 300, SeekAddress = 304, SeekCount = 312, SeekLevel = 316, SeekScan = 320, SeekSlot = 324, SeekFound = 328, SeekDirection = 332, FileBuffer = 336, DataPhysical = 340, DataLogical = 348, DataTake = 356, DataIntra = 360, DataFlags = 364, SavedLeaf = 368, SavedSlot = 376, DataProgress = 380, NameLength = 384, DirectoryRoot = 388, LookupTarget = 396, LookupFoundAt = 404, NameCursorHigh = 408, NameCursorLow = 416, TrimCursor = 424, AllocationScan = 432, NewPhysical = 440, AllocationMode = 448, OldFileSize = 456, CallDepth = 464, MetadataTaken = 468, MetadataLimit = 472, MetadataReleased = 476, PreparedBuffer = 480, PreparedTarget = 488;
    public CpxEngine(TMemory memory, uint arena)
    {
        this.memory = memory;
        Arena = arena;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private uint R(uint field) => memory.Read32(Arena + field);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void W(uint field, uint value) => memory.Write32(Arena + field, value);
    private ulong RW(uint field) => (((ulong)(memory.Read32(Arena + (field)))) << 32 | ((ulong)(memory.Read32(Arena + (field + 4)))));
    private void WW(uint field, ulong value)
    {
        memory.Write32(Arena + (field), ((uint)(((ulong)(value)) >> 32)));
        memory.Write32(Arena + (field + 4), ((uint)((ulong)(value))));
    }

    private ulong GetWide(uint p) => (((ulong)(memory.Read32(p))) << 32 | ((ulong)(memory.Read32(p + 4))));
    private void PutWide(uint p, ulong value)
    {
        memory.Write32(p, ((uint)(((ulong)(value)) >> 32)));
        memory.Write32(p + 4, ((uint)((ulong)(value))));
    }

    private uint P => memory.Read32(Arena + (PageBytes));

    // Derived geometry occupies 4020..4095, after the native
    // maintenance record (4004..4015) and before the DMA page at 4096.
    private const uint GeometryShift = 4020, GeometryQ = 4024, GeometryFirst = 4028, GeometryBackup = 4032, GeometryIndex = 4040, GeometryCrc = 4044, GeometryPages = 4048, GeometryAnchor = 4072, FastPointer = 4076;
    private uint Q => memory.Read32(Arena + (GeometryQ));

    private uint Page(uint slot) => memory.Read32(Arena + (GeometryPages + slot * 4));
    private uint Index => memory.Read32(Arena + (GeometryIndex));
    private uint CrcTable => memory.Read32(Arena + (GeometryCrc));
    private ulong Anchor => ((ulong)(memory.Read32(Arena + (GeometryAnchor))));

    private ulong Journal(ulong tx) => ((ulong)(((((uint)((ulong)(tx))) & 1) != 0 ? 1048576u : 1572864u) >> (int)memory.Read32(Arena + (UnitShift))));
    private ulong Commit(ulong tx) => Journal(tx) + 69 * Q;
    private ulong Descriptor(ulong tx, uint index) => Journal(tx) + (1 + index) * Q;
    private ulong Image(ulong tx, uint index) => Journal(tx) + (5 + index) * Q;
    private ulong Backup => RW(GeometryBackup);
    public CpxError Error => (CpxError)memory.Read32(Arena + (ErrorAt));
    public bool IsMounted => memory.Read32(Arena + (Mounted)) != 0 && memory.Read32(Arena + (Poisoned)) == 0;
    public uint Epoch => memory.Read32(Arena + (MountEpoch));
    public bool IsReadOnly => memory.Read32(Arena + (ReadOnlyAt)) != 0;
    public Wide Transaction => RW(Generation);
    public bool HasOutstandingIo => memory.Read32(Arena + (Waiting)) != 0;
    public uint MetadataPageBytes => P;

    public CpxError BeginRemount()
    {
        if (HasBufferedChanges && IsMounted)
            return CpxError.Busy;
        if (memory.Read32(Arena + (Waiting)) != 0 || memory.Read32(Arena + (Finished)) == 0)
            return CpxError.Busy;
        uint epoch = memory.Read32(Arena + (MountEpoch)) + 1;
        if (epoch == 0)
            return CpxError.ResourceExhausted;
        CpxError error = Initialize(1u << (int)memory.Read32(Arena + (SectorShift)), 1u << (int)memory.Read32(Arena + (UnitShift)), RW(Capacity), epoch, IsReadOnly);
        return error == CpxError.None ? BeginOperation(CpxOperation.Mount) : error;
    }

    public CpxError Initialize(uint sectorBytes, uint allocationBytes, Wide capacitySectors, uint epoch, bool readOnly = false)
    {
        uint previousToken = 0, previousPage = P;
        bool previous = memory.Read32(Arena + (Signature)) == CpxArena.Magic;
        if (memory.Read32(Arena + (Signature)) == CpxArena.Magic)
        {
            if (memory.Read32(Arena + (Waiting)) != 0)
                return CpxError.Busy;
            previousToken = memory.Read32(Arena + (NextToken));
        }

        if (!CpxArena.ValidUnit(sectorBytes) || !CpxArena.ValidUnit(allocationBytes) || sectorBytes > allocationBytes || epoch == 0)
            return CpxError.InvalidArgument;
        int ds = CpxArena.Shift(allocationBytes) - CpxArena.Shift(sectorBytes);
        ulong units = Scalar64.Right(capacitySectors, ds);
        if ((((uint)((ulong)(capacitySectors))) & ((1u << ds) - 1)) != 0 || (units) < (((ulong)(4194304u >> CpxArena.Shift(allocationBytes)))) || !Scalar64.Left(capacitySectors, CpxArena.Shift(sectorBytes), out _))
            return CpxError.InvalidArgument;
        // Initialization must be called only on a quiescent, caller-owned arena.
        for (uint n = 0; n < 512; n += 4)
            memory.Write32(Arena + (n), 0);
        memory.Write32(Arena + (Signature), CpxArena.Magic);
        memory.Write32(Arena + (SectorShift), (uint)CpxArena.Shift(sectorBytes));
        memory.Write32(Arena + (UnitShift), (uint)CpxArena.Shift(allocationBytes));
        memory.Write32(Arena + (PageBytes), allocationBytes < 1024 ? 1024 : allocationBytes);
        WW(Units, units);
        WW(Capacity, capacitySectors);
        memory.Write32(Arena + (MountEpoch), epoch);
        memory.Write32(Arena + (ReadOnlyAt), readOnly ? 1u : 0u);
        InitializeGeometry();
        ResetBufferedContext(previous);
        if (!previous || previousPage != P)
            InitializeCrcTable();
        memory.Write32(Arena + (NextToken), previousToken);
        memory.Write32(Arena + (Finished), 1);
        DefaultLabel();
        return CpxError.None;
    }

    public CpxError BeginOperation(CpxOperation operation, Wide argument = default, uint buffer = 0, uint creditLimit = 42)
    {
        // Parameterized operations must use their typed admission methods. This
        // prevents a raw operation number from reusing a previous caller's input.
        if (operation is >= CpxOperation.WriteInline and <= CpxOperation.Lookup || operation >= CpxOperation.Create && operation != CpxOperation.Reclaim)
            return CpxError.InvalidArgument;
        return StartOperation(operation, argument, buffer, creditLimit);
    }

    private CpxError StartOperation(CpxOperation operation, ulong argument = default, uint buffer = 0, uint creditLimit = 42)
    {
        if (memory.Read32(Arena + (Signature)) != CpxArena.Magic)
            return CpxError.InvalidArgument;
        if (memory.Read32(Arena + (Waiting)) != 0 || memory.Read32(Arena + (Finished)) == 0)
            return CpxError.Busy;
        if (operation != CpxOperation.WriteMapped)
            InvalidateFastWrite();
        if (memory.Read32(Arena + (AbortOnly)) != 0 && operation != CpxOperation.AbortTransaction)
            return CpxError.TransactionLimit;
        if (operation is not (CpxOperation.Mount or CpxOperation.Format) && !IsMounted)
            return CpxError.NotMounted;
        if (operation is < CpxOperation.Mount or > CpxOperation.ManualTrim)
            return CpxError.Unsupported;
        if (memory.Read32(Arena + (Active)) != 0 && !BufferedWrites && operation is CpxOperation.Mount or CpxOperation.Recover or CpxOperation.BeginTransaction or CpxOperation.Flush)
            return CpxError.Busy;
        if (HasBufferedChanges && IsMounted && operation is CpxOperation.Mount or CpxOperation.Recover)
            return CpxError.Busy;
        if (operation >= CpxOperation.BeginTransaction && operation <= CpxOperation.AbortTransaction && memory.Read32(Arena + (ReadOnlyAt)) != 0)
            return CpxError.ReadOnly;
        if ((operation is >= CpxOperation.WriteInline and <= CpxOperation.Trim or CpxOperation.WriteMapped or CpxOperation.TrimFree) && memory.Read32(Arena + (ReadOnlyAt)) != 0)
            return CpxError.ReadOnly;
        if (operation >= CpxOperation.Create && operation is not (CpxOperation.ReadSymlink or CpxOperation.ManualTrim) && memory.Read32(Arena + (ReadOnlyAt)) != 0)
            return CpxError.ReadOnly;
        if (operation >= CpxOperation.WriteInline && operation != CpxOperation.ReadTree && memory.Read32(Arena + (Active)) != 0 && !BufferedWrites)
            return CpxError.Busy;
        if (operation == CpxOperation.BeginTransaction && (creditLimit == 0 || creditLimit > 42 || !Scalar64.Add(RW(Generation), ((ulong)(1)), out _)))
            return CpxError.InvalidArgument;
        if (operation >= CpxOperation.StageMetadata && operation <= CpxOperation.AbortTransaction && memory.Read32(Arena + (Active)) == 0)
            return CpxError.InvalidArgument;
        if (operation == CpxOperation.StageMetadata && (buffer < Page(0) || buffer > Page(5) || ((buffer - Page(0)) & (P - 1)) != 0))
            return CpxError.InvalidArgument;
        if (operation == CpxOperation.CommitTransaction && memory.Read32(Arena + (StagedCount)) == 0)
            return CpxError.InvalidArgument;
        if (operation == CpxOperation.ReadMetadata && !PageAddress(argument))
            return CpxError.InvalidArgument;
        if (operation == CpxOperation.Mount)
        {
            memory.Write32(Arena + (Mounted), 0);
            memory.Write32(Arena + (Poisoned), 0);
        }

        if (operation is CpxOperation.Mount or CpxOperation.Recover && CanCacheMetadata)
        {
            ClearBufferedObjects();
            ClearBufferedClean();
            memory.Write32(Arena + (BufferFormatEnabled), 1);
            memory.Write32(Arena + (BufferEnabled), 0);
        }

        if (operation >= CpxOperation.WriteInline)
        {
            WW(FilePosition, default);
            memory.Write32(Arena + (FileBytes), 0);
            memory.Write32(Arena + (FileValue), 0);
            memory.Write32(Arena + (TrimCount), 0);
            memory.Write32(Arena + (FileBuffer), 0);
            memory.Write32(Arena + (DataProgress), 0);
        }

        memory.Write32(Arena + (CallDepth), 0);
        memory.Write32(Arena + (OperationAt), (uint)operation);
        memory.Write32(Arena + (Phase), 0);
        memory.Write32(Arena + (ErrorAt), 0);
        memory.Write32(Arena + (Finished), 0);
        memory.Write32(Arena + (CancelAt), 0);
        memory.Write32(Arena + (IoReady), 0);
        memory.Write32(Arena + (NsPublished), 0);
        WW(Target, argument);
        WW(ObjectKey, argument);
        memory.Write32(Arena + (SourcePage), buffer);
        if (operation == CpxOperation.BeginTransaction)
            memory.Write32(Arena + (StagedLimit), creditLimit);
        return CpxError.None;
    }

    /// <summary>A persistence barrier without cleanup or allocation. Software
    /// protected mounts may flush device caches but must not prune metadata.</summary>
    public CpxError BeginPersistenceBarrier()
    {
        CpxError error = StartOperation(CpxOperation.Flush);
        if (error == CpxError.None)
            memory.Write32(Arena + (Phase), 1);
        return error;
    }

    public CpxIo Request => new()
    {
        Kind = (IoKind)memory.Read32(Arena + (PendingKind)),
        Sector = RW(PendingSector),
        Buffer = memory.Read32(Arena + (PendingBuffer)),
        Bytes = memory.Read32(Arena + (PendingBytes)),
        Token = memory.Read32(Arena + (Token))
    };

    public CpxError CompleteIo(uint token, uint actual, bool success)
    {
        if (memory.Read32(Arena + (Waiting)) == 0 || token != memory.Read32(Arena + (Token)))
            return CpxError.StaleCompletion;
        memory.Write32(Arena + (Waiting), 0);
        memory.Write32(Arena + (IoReady), 1);
        if (!success || ((IoKind)memory.Read32(Arena + (PendingKind))is IoKind.Read or IoKind.Write or IoKind.Discard or IoKind.QueryDiscard && actual != memory.Read32(Arena + (PendingBytes))))
            Fail(CpxError.Io);
        else if (memory.Read32(Arena + (CancelAt)) != 0)
            Fail(CpxError.Cancelled);
        else if ((CpxOperation)memory.Read32(Arena + (OperationAt)) == CpxOperation.ManualTrim && (IoKind)memory.Read32(Arena + (PendingKind)) == IoKind.Discard && DiscardEnabled)
            CountManualRange();
        return Error;
    }

    /// <summary>Only an explicit command-not-supported response disables discard.
    /// Other errors still poison the mount; transport errors are not capability discovery.</summary>
    public CpxError CompleteUnsupportedDiscard(uint token)
    {
        if (memory.Read32(Arena + (Waiting)) == 0 || token != memory.Read32(Arena + (Token)))
            return CpxError.StaleCompletion;
        if ((IoKind)memory.Read32(Arena + (PendingKind))is not (IoKind.Discard or IoKind.QueryDiscard))
            return CpxError.InvalidArgument;
        memory.Write32(Arena + (TrimDisabled), 1);
        return CompleteIo(token, memory.Read32(Arena + (PendingBytes)), true);
    }

    /// <summary>Caller must quiesce I/O and deliver its completion before reuse.</summary>
    public void Cancel()
    {
        memory.Write32(Arena + (CancelAt), 1);
        if (memory.Read32(Arena + (Waiting)) == 0)
            Fail(CpxError.Cancelled);
    }

    private StepResult Fail(CpxError error)
    {
        memory.Write32(Arena + (ErrorAt), (uint)error);
        memory.Write32(Arena + (Poisoned), 1);
        memory.Write32(Arena + (Finished), 1);
        return StepResult.Failed;
    }

    private StepResult Done()
    {
        if (memory.Read32(Arena + (CallDepth)) == 0 && (CpxOperation)memory.Read32(Arena + (OperationAt))is CpxOperation.Mount or CpxOperation.Recover && memory.Read32(Arena + (BufferFormatEnabled)) != 0)
        {
            memory.Write32(Arena + (BufferEnabled), memory.Read32(Arena + (BufferBytes)) >= CompactBufferedStorageBytes(P) ? 1u : 0u);
            memory.Write32(Arena + (BufferFormatEnabled), 0);
            memory.Write32(Arena + (BufferTrustedMask), 0);
            if (BufferedWrites)
                ClearBufferedObjects();
        }

        memory.Write32(Arena + (Finished), 1);
        return StepResult.Complete;
    }

    private StepResult Reject(CpxError error)
    {
        memory.Write32(Arena + (ErrorAt), (uint)error);
        memory.Write32(Arena + (Finished), 1);
        return StepResult.Failed;
    }

    private StepResult Issue(IoKind kind, ulong unit, uint buffer, uint bytes, uint nextPhase)
    {
        if (BufferedWrites && memory.Read32(Arena + (BufferCommitting)) == 0)
        {
            if (kind == IoKind.Flush && (CpxOperation)memory.Read32(Arena + (OperationAt))is not (CpxOperation.Flush or CpxOperation.ManualTrim))
            {
                memory.Write32(Arena + (Phase), nextPhase);
                return StepResult.Pending;
            }

            // Reclaim may identify space in an unpublished group. Optional
            // discard must wait until that free-space metadata is persistent.
            if (kind == IoKind.Discard && HasBufferedChanges)
            {
                memory.Write32(Arena + (Phase), nextPhase);
                return StepResult.Pending;
            }
        }

        if (kind == IoKind.Read)
            InvalidateBufferedPage(buffer);
        if (kind == IoKind.Write && !BufferedWrites && CanCacheMetadata)
            InvalidateBufferedClean(unit);
        if (memory.Read32(Arena + (Waiting)) != 0)
            return Fail(CpxError.Busy);
        if (!Scalar64.Left(unit, (int)(memory.Read32(Arena + (UnitShift)) - memory.Read32(Arena + (SectorShift))), out var sector))
            return Fail(CpxError.Corrupt);
        if (kind is IoKind.Read or IoKind.Write or IoKind.Discard)
        {
            if ((bytes & ((1u << (int)memory.Read32(Arena + (SectorShift))) - 1)) != 0 || bytes == 0 || !Scalar64.Add(sector, ((ulong)(bytes >> (int)memory.Read32(Arena + (SectorShift)))), out var end) || (end) > (RW(Capacity)))
                return Fail(CpxError.Corrupt);
        }

        if (kind is IoKind.Write or IoKind.Discard && memory.Read32(Arena + (ReadOnlyAt)) != 0)
            return Fail(CpxError.ReadOnly);
        uint next = memory.Read32(Arena + (NextToken)) + 1;
        if (next == 0)
            return Fail(CpxError.ResourceExhausted);
        memory.Write32(Arena + (NextToken), next);
        memory.Write32(Arena + (Token), next);
        memory.Write32(Arena + (PendingKind), (uint)kind);
        WW(PendingSector, sector);
        memory.Write32(Arena + (PendingBuffer), buffer);
        memory.Write32(Arena + (PendingBytes), bytes);
        memory.Write32(Arena + (Waiting), 1);
        memory.Write32(Arena + (IoReady), 0);
        memory.Write32(Arena + (Phase), nextPhase);
        return StepResult.NeedIo;
    }

    public StepResult Step()
    {
        if (memory.Read32(Arena + (Waiting)) != 0)
            return StepResult.NeedIo;
        if (memory.Read32(Arena + (ErrorAt)) != 0)
            return StepResult.Failed;
        if (memory.Read32(Arena + (Finished)) != 0)
            return StepResult.Complete;
        if (memory.Read32(Arena + (CancelAt)) != 0)
            return Fail(CpxError.Cancelled);
        StepResult result = DispatchStep();
        if (result == StepResult.Complete && memory.Read32(Arena + (CallDepth)) != 0)
        {
            uint depth = memory.Read32(Arena + (CallDepth)) - 1, frame = Arena + 9472 + depth * 8;
            memory.Write32(Arena + (CallDepth), depth);
            memory.Write32(Arena + (OperationAt), memory.Read32(frame));
            memory.Write32(Arena + (Phase), memory.Read32(frame + 4));
            memory.Write32(Arena + (Finished), 0);
            return StepResult.Pending;
        }

        // Rejected unpublished operations leave only unreachable journal images.
        // I/O failures remain poisoned and must recover before arena reuse.
        uint owner = memory.Read32(Arena + (CallDepth)) != 0 ? memory.Read32(Arena + 9472) : memory.Read32(Arena + (OperationAt));
        if (result == StepResult.Failed && owner >= (uint)CpxOperation.WriteInline && memory.Read32(Arena + (Poisoned)) == 0)
        {
            memory.Write32(Arena + (CallDepth), 0);
            if (BufferedWrites)
                DiscardBufferedOperation();
            else
            {
                memory.Write32(Arena + (Active), 0);
                memory.Write32(Arena + (AbortOnly), 0);
                memory.Write32(Arena + (StagedCount), 0);
            }
        }

        return result;
    }

    private StepResult DispatchStep()
    {
        switch ((CpxOperation)memory.Read32(Arena + (OperationAt)))
        {
            case CpxOperation.Mount:
                return MountStep();
            case CpxOperation.Recover:
                return RecoveryStep();
            case CpxOperation.Flush:
                if (memory.Read32(Arena + (Phase)) == 0 && !IsReadOnly)
                    return Call(CpxOperation.PruneObjects, 1);
                if (memory.Read32(Arena + (Phase)) < 2 && HasBufferedChanges)
                    return RetireBufferedGroup(2);
                return memory.Read32(Arena + (Phase)) < 3 ? Issue(IoKind.Flush, default, 0, 0, 3) : Done();
            case CpxOperation.ReadMetadata:
                if (memory.Read32(Arena + (Phase)) == 0)
                    return ReadStaged(RW(Target), Page(0), 1);
                return ValidPage(Page(0), RW(Target)) && ReadableGeneration(Page(0)) ? Done() : Fail(CpxError.Corrupt);
            case CpxOperation.BeginTransaction:
            case CpxOperation.StageMetadata:
            case CpxOperation.CommitTransaction:
            case CpxOperation.AbortTransaction:
                return TransactionStep();
            case CpxOperation.ReadObject:
                return ObjectStep();
            case CpxOperation.WriteInline:
            case CpxOperation.ResizeInline:
            case CpxOperation.SetProtection:
            case CpxOperation.SetDate:
                return FileMutationStep();
            case CpxOperation.Trim:
                return TrimStep();
            case CpxOperation.ReadTree:
                return SeekStep();
            case CpxOperation.ReadFile:
            case CpxOperation.WriteMapped:
                return FileDataStep();
            case CpxOperation.Lookup:
                return LookupStep();
            case CpxOperation.TrimFree:
                return TrimFreeStep();
            case CpxOperation.ManualTrim:
                return ManualTrimStep();
            case CpxOperation.TreeMutation:
                return TreeMutationStep();
            case CpxOperation.MetadataAllocation:
                return MetadataAllocationStep();
            case CpxOperation.ChangeAllocation:
                return ChangeAllocationStep();
            case CpxOperation.SaveObject:
                return SaveObjectStep();
            case CpxOperation.Refill:
                return RefillStep();
            case CpxOperation.Create:
            case CpxOperation.Delete:
            case CpxOperation.Rename:
                return NamespaceStep();
            case CpxOperation.Resize:
                return ResizeStep();
            case CpxOperation.Reclaim:
                return ReclaimStep();
            case CpxOperation.PruneTree:
                return PruneTreeStep();
            case CpxOperation.PruneObjects:
                return PruneObjectsStep();
            case CpxOperation.PublishMetadata:
                return memory.Read32(Arena + (Phase)) == 0 ? PublishMetadata(memory.Read32(Arena + (PreparedBuffer)), RW(PreparedTarget), 1) : Done();
            case CpxOperation.SetComment:
            case CpxOperation.SetOwner:
                return CommentStep();
            case CpxOperation.PrepareWriteGap:
                return WriteGapStep();
            case CpxOperation.Format:
                return FormatStep();
            case CpxOperation.CreateHardLink:
                return HardLinkStep();
            case CpxOperation.ReadSymlink:
                return ReadSymlinkStep();
            case CpxOperation.EncodeSymlink:
                return EncodeSymlinkStep();
            case CpxOperation.SetLabel:
                return LabelStep();
            default:
                return Fail(CpxError.Unsupported);
        }
    }

    private StepResult Call(CpxOperation operation, uint nextPhase)
    {
        uint depth = memory.Read32(Arena + (CallDepth));
        if (depth == 8)
            return Fail(CpxError.Corrupt);
        uint frame = Arena + 9472 + depth * 8;
        memory.Write32(frame, memory.Read32(Arena + (OperationAt)));
        memory.Write32(frame + 4, nextPhase);
        memory.Write32(Arena + (CallDepth), depth + 1);
        memory.Write32(Arena + (OperationAt), (uint)operation);
        memory.Write32(Arena + (Phase), 0);
        return StepResult.Pending;
    }

    private void Copy(uint destination, uint source, uint bytes)
    {
        uint n = 0;
        if (((destination | source) & 1) == 0)
        {
            for (; bytes - n >= 32; n += 32)
            {
                memory.Write32(destination + n, memory.Read32(source + n));
                memory.Write32(destination + n + 4, memory.Read32(source + n + 4));
                memory.Write32(destination + n + 8, memory.Read32(source + n + 8));
                memory.Write32(destination + n + 12, memory.Read32(source + n + 12));
                memory.Write32(destination + n + 16, memory.Read32(source + n + 16));
                memory.Write32(destination + n + 20, memory.Read32(source + n + 20));
                memory.Write32(destination + n + 24, memory.Read32(source + n + 24));
                memory.Write32(destination + n + 28, memory.Read32(source + n + 28));
            }

            for (; bytes - n >= 4; n += 4)
                memory.Write32(destination + n, memory.Read32(source + n));
        }

        for (; n < bytes; n++)
            memory.Write8(destination + n, memory.Read8(source + n));
    }

    private StepResult BeginTx(uint credits, uint nextPhase)
    {
        if (!Scalar64.Add(RW(Generation), ((ulong)(1)), out _))
            return Reject(CpxError.ResourceExhausted);
        if (BufferedWrites)
        {
            // Inline extent writes have already checked type, protection, free
            // data space and reserve admission. Their eight-image bound fits
            // without retry here; any later corruption or I/O error poisons the
            // mount. Keep rollback snapshots for namespace edits and capacity
            // retries, rather than copying the old group for every data run.
            if (credits == 8 && (CpxOperation)memory.Read32(Arena + (OperationAt)) == CpxOperation.WriteMapped && memory.Read32(Arena + (StagedCount)) <= 34)
            {
                memory.Write32(Arena + (BufferSnapshot), 0);
                memory.Write32(Arena + (BufferFullSnapshot), 0);
            }
            else
                SnapshotBufferedOperation(credits);
        }

        memory.Write32(Arena + (StagedLimit), credits);
        return Call(CpxOperation.BeginTransaction, nextPhase);
    }

    private void InitializeGeometry()
    {
        uint page = P, q = page >> (int)memory.Read32(Arena + (UnitShift));
        memory.Write32(Arena + (GeometryShift), (uint)CpxArena.Shift(page));
        memory.Write32(Arena + (GeometryQ), q);
        memory.Write32(Arena + (GeometryFirst), 2097152u >> (int)memory.Read32(Arena + (UnitShift)));
        memory.Write32(Arena + (GeometryAnchor), 65536u >> (int)memory.Read32(Arena + (UnitShift)));
        WW(GeometryBackup, (((ulong)(memory.Read32(Arena + (Units)))) << 32 | ((ulong)(memory.Read32(Arena + (Units + 4)) & ~(q - 1)))) - q);
        for (uint slot = 0; slot < 6; slot++)
            memory.Write32(Arena + (GeometryPages + slot * 4), Arena + CpxArena.Scratch + slot * page);
        uint index = Arena + CpxArena.Scratch + 6 * page;
        memory.Write32(Arena + (GeometryIndex), index);
        memory.Write32(Arena + (GeometryCrc), index + CpxArena.IndexBytes + CpxArena.PoolBytes);
    }

    private bool PageAddress(ulong unit)
    {
        uint q = Q;
        if ((((uint)((ulong)(unit))) & (q - 1)) != 0)
            return false;
        uint low = unchecked(((uint)((ulong)(unit))) + q), high = unchecked(((uint)(((ulong)(unit)) >> 32)) + (low < ((uint)((ulong)(unit))) ? 1u : 0u)), limitHigh = memory.Read32(Arena + (Units));
        return high >= ((uint)(((ulong)(unit)) >> 32)) && (high < limitHigh || high == limitHigh && low <= memory.Read32(Arena + (Units + 4)));
    }

    private bool Allocatable(ulong unit)
    {
        if (!PageAddress(unit) || !DataUnit(unit))
            return false;
        return true;
    }

    private uint Crc(uint buffer, uint bytes, bool page)
    {
        uint crc = uint.MaxValue, table = CrcTable;
        // On 68000, extracting the high bytes with long shifts costs more than
        // spilling the accumulator once and addressing its bytes. The private
        // word at 4080 is mount-owned and never overlaps a DMA or page buffer.
        if ((buffer & 1) == 0 && (bytes & 3) == 0)
        {
            uint word = Arena + 4080, n = 0, zeros = page && CanCacheMetadata ? memory.Read32(Arena + (BufferAddress)) : 0;
            if (page)
            {
                for (; n < 8; n += 4)
                {
                    memory.Write32(word, crc);
                    crc = memory.Read32(table + 3072 + ((uint)memory.Read8(word + 3) ^ memory.Read8(buffer + n)) * 4) ^ memory.Read32(table + 2048 + ((uint)memory.Read8(word + 2) ^ memory.Read8(buffer + n + 1)) * 4) ^ memory.Read32(table + 1024 + ((uint)memory.Read8(word + 1) ^ memory.Read8(buffer + n + 2)) * 4) ^ memory.Read32(table + ((uint)memory.Read8(word) ^ memory.Read8(buffer + n + 3)) * 4);
                }

                memory.Write32(word, crc);
                crc = memory.Read32(table + 3072 + (uint)memory.Read8(word + 3) * 4) ^ memory.Read32(table + 2048 + (uint)memory.Read8(word + 2) * 4) ^ memory.Read32(table + 1024 + (uint)memory.Read8(word + 1) * 4) ^ memory.Read32(table + (uint)memory.Read8(word) * 4);
                n = 12;
            }

            for (; n < bytes; n += 4)
            {
                if (zeros != 0 && (n & 63) == 0 && bytes - n >= 64)
                {
                    uint end = n + 64, scan = n;
                    for (; scan < end; scan += 4)
                        if (memory.Read32(buffer + scan) != 0)
                            break;
                    if (scan == end)
                    {
                        memory.Write32(word, crc);
                        crc = memory.Read32(zeros + (uint)memory.Read8(word + 3) * 4) ^ memory.Read32(zeros + 1024 + (uint)memory.Read8(word + 2) * 4) ^ memory.Read32(zeros + 2048 + (uint)memory.Read8(word + 1) * 4) ^ memory.Read32(zeros + 3072 + (uint)memory.Read8(word) * 4);
                        n += 60;
                        continue;
                    }
                }

                memory.Write32(word, crc);
                crc = memory.Read32(table + 3072 + ((uint)memory.Read8(word + 3) ^ memory.Read8(buffer + n)) * 4) ^ memory.Read32(table + 2048 + ((uint)memory.Read8(word + 2) ^ memory.Read8(buffer + n + 1)) * 4) ^ memory.Read32(table + 1024 + ((uint)memory.Read8(word + 1) ^ memory.Read8(buffer + n + 2)) * 4) ^ memory.Read32(table + ((uint)memory.Read8(word) ^ memory.Read8(buffer + n + 3)) * 4);
            }

            return ~crc;
        }

        for (uint n = 0; n < bytes; n++)
            crc = (crc >> 8) ^ memory.Read32(table + ((crc ^ (page && n >= 8 && n < 12 ? 0u : memory.Read8(buffer + n))) & 255) * 4);
        return ~crc;
    }

    // A linear CRC transform advances 64 zero bytes with four lookups. CPX
    // control pages have large zero-filled regions; the exact disk checksum is
    // unchanged. The optional table replaces eight clean cache pages and saves
    // memory overall. Enable runs only while all scratch is caller-owned.
    private void InitializeZeroCrcTable()
    {
        uint columns = Page(5), table = CrcTable, zeros = memory.Read32(Arena + (BufferAddress));
        for (uint bit = 0; bit < 32; bit++)
        {
            uint crc = 1u << (int)bit;
            for (uint n = 0; n < 64; n++)
                crc = (crc >> 8) ^ memory.Read32(table + (crc & 255) * 4);
            memory.Write32(columns + bit * 4, crc);
        }

        for (uint slice = 0; slice < 4; slice++)
        {
            uint start = zeros + slice * 1024;
            memory.Write32(start, 0);
            for (uint value = 1; value < 256; value++)
            {
                uint bit = 0, mask = value;
                while ((mask & 1) == 0)
                {
                    bit++;
                    mask >>= 1;
                }

                memory.Write32(start + value * 4, memory.Read32(start + (value & (value - 1)) * 4) ^ memory.Read32(columns + (slice * 8 + bit) * 4));
            }
        }
    }

    private void InitializeCrcTable()
    {
        uint table = CrcTable;
        for (uint n = 0; n < 256; n++)
        {
            uint crc = n;
            for (uint bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0x82f63b78u & (0u - (crc & 1)));
            memory.Write32(table + n * 4, crc);
        }

        for (uint n = 0; n < 256; n++)
        {
            uint crc = memory.Read32(table + n * 4);
            for (uint slice = 1; slice < 4; slice++)
            {
                crc = (crc >> 8) ^ memory.Read32(table + (crc & 255) * 4);
                memory.Write32(table + slice * 1024 + n * 4, crc);
            }
        }
    }

    // Anchor discovery precedes Initialize and may inspect a small standalone
    // buffer. It must not assume that a mount arena or checksum table exists.
    private uint AnchorCrc(uint buffer, uint bytes)
    {
        uint crc = uint.MaxValue;
        for (uint n = 0; n < bytes; n++)
        {
            crc ^= n >= 8 && n < 12 ? 0u : memory.Read8(buffer + n);
            for (uint bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0x82f63b78u & (0u - (crc & 1)));
        }

        return ~crc;
    }

    private bool ValidPage(uint p, ulong address)
    {
        if (memory.Read32(p) != CpxArena.Magic || memory.Read16(p + 6) != 4 || !(((ulong)(GetWide(p + 16))) == ((ulong)(address))))
            return false;
        if (TrustedBufferedPage(p, address))
            return true;
        if (memory.Read32(p + 8) != Crc(p, P, true))
            return false;
        if (CanCacheMetadata && memory.Read32(Arena + (BufferCommitting)) == 0)
        {
            CacheBufferedClean(p, address);
            if (BufferedWrites)
                TrustBufferedPage(p, address);
        }

        return true;
    }

    private bool ReadableGeneration(uint p) => (GetWide(p + 32)) <= (RW(Generation) + (memory.Read32(Arena + (Active)) != 0 ? 1u : 0u));
    private bool Control(uint p, uint type, ulong address) => ValidPage(p, address) && memory.Read16(p + 4) == type && (((ulong)(GetWide(p + 24))) == 0);
    private bool IdentityMatches(uint p) => (((ulong)(GetWide(p + 64))) == 0) && (((ulong)(GetWide(p + 72))) == ((ulong)(RW(Identity))));
    private void Clear(uint p, uint count)
    {
        uint n = 0;
        if ((p & 1) == 0)
            for (; count - n >= 4; n += 4)
                memory.Write32(p + n, 0);
        for (; n < count; n++)
            memory.Write8(p + n, 0);
    }

    private void Header(uint p, uint type, ulong address, ulong tx)
    {
        InvalidateBufferedPage(p);
        Clear(p, P);
        memory.Write32(p, CpxArena.Magic);
        memory.Write16(p + 4, type);
        memory.Write16(p + 6, 4);
        PutWide(p + 16, address);
        PutWide(p + 32, tx);
    }

    private void Seal(uint p, uint payload)
    {
        memory.Write16(p + 52, payload);
        if (BufferedWrites && memory.Read32(Arena + (BufferCommitting)) == 0)
        {
            memory.Write32(p + 8, 0);
            TrustBufferedPage(p, GetWide(p + 16));
        }
        else
            memory.Write32(p + 8, Crc(p, P, true));
    }

    private StepResult MountStep()
    {
        uint p = Page(0);
        switch (memory.Read32(Arena + (Phase)))
        {
            case 0:
                return Issue(IoKind.Read, Anchor, p, P, 1);
            case 1:
                if (!Control(p, 1, Anchor))
                    return Issue(IoKind.Read, Backup, p, P, 2);
                if (!AnchorFieldsValid(p))
                    return Fail(CpxError.Corrupt);
                WW(Identity, GetWide(p + 72));
                return Issue(IoKind.Read, Backup, p, P, 3);
            case 2:
                if (!Control(p, 1, Backup) || !AnchorFieldsValid(p))
                    return Fail(CpxError.Corrupt);
                WW(Identity, GetWide(p + 72));
                memory.Write32(Arena + (OperationAt), (uint)CpxOperation.Recover);
                memory.Write32(Arena + (Phase), 0);
                return StepResult.Pending;
            case 3:
                if (ValidPage(p, Backup) && (memory.Read16(p + 4) != 1 || !(((ulong)(GetWide(p + 24))) == 0) || !AnchorFieldsValid(p) || !(((ulong)(GetWide(p + 72))) == ((ulong)(RW(Identity))))))
                    return Fail(CpxError.Corrupt);
                memory.Write32(Arena + (OperationAt), (uint)CpxOperation.Recover);
                memory.Write32(Arena + (Phase), 0);
                return StepResult.Pending;
            default:
                return Fail(CpxError.Corrupt);
        }
    }

    // Discovery has authenticated both possible copies at every legal page
    // size. Enter the ordinary recovery path without re-reading a bad sector.
    public CpxError BeginMountFromDiscoveredAnchor(Wide identity)
    {
        if ((((ulong)(identity)) == 0))
            return CpxError.InvalidArgument;
        CpxError error = BeginOperation(CpxOperation.Mount);
        if (error == CpxError.None)
        {
            WW(Identity, identity);
            memory.Write32(Arena + (OperationAt), (uint)CpxOperation.Recover);
        }

        return error;
    }

    private bool AnchorFieldsValid(uint p) => AnchorFieldsValid(p, 1u << (int)memory.Read32(Arena + (UnitShift)), P, RW(Units), 1u << (int)memory.Read32(Arena + (SectorShift)));
    private bool AnchorFieldsValid(uint p, uint unit, uint page, ulong units, uint sector)
    {
        int shift = CpxArena.Shift(unit);
        uint anchor = 65536u >> shift, q = page >> shift;
        return memory.Read32(p + 80) == unit && memory.Read32(p + 84) == page && (((ulong)(GetWide(p + 88))) == ((ulong)(units))) && (((ulong)(GetWide(p + 96))) == ((ulong)(((ulong)(1))))) && memory.Read32(p + 104) == sector && memory.Read32(p + 108) == 1 && (((ulong)(GetWide(p + 64))) == 0) && !(((ulong)(GetWide(p + 72))) == 0) && (((ulong)(GetWide(p + 112))) == ((ulong)(((ulong)(anchor + 8 * q))))) && (((ulong)(GetWide(p + 120))) == ((ulong)(((ulong)(anchor + 3 * q)))));
    }

    // One CRC per candidate. A torn copy can be skipped. A CRC-valid CPX4
    // header with inconsistent geometry cannot authorize a different volume.
    public CpxError InspectAnchor(uint buffer, uint page, uint sector, Wide capacity, Wide location, out uint unit, out Wide identity)
    {
        unit = 0;
        identity = default;
        if (!CpxArena.ValidUnit(page) || page < 1024 || !CpxArena.ValidUnit(sector) || sector > page)
            return CpxError.InvalidArgument;
        if (memory.Read32(buffer) != CpxArena.Magic || memory.Read32(buffer + 8) != AnchorCrc(buffer, page))
            return CpxError.NotFound;
        uint candidate = memory.Read32(buffer + 80);
        if (!CpxArena.ValidUnit(candidate) || candidate < sector || (candidate < 1024 ? 1024 : candidate) != page || memory.Read16(buffer + 4) != 1 || memory.Read16(buffer + 6) != 4 || memory.Read16(buffer + 52) != 64 || !(((ulong)(GetWide(buffer + 24))) == 0))
            return CpxError.Corrupt;
        int shift = CpxArena.Shift(candidate) - CpxArena.Shift(sector);
        if ((((uint)((ulong)(capacity))) & ((1u << shift) - 1)) != 0 || !(((ulong)(GetWide(buffer + 16))) == ((ulong)(Scalar64.Right(location, shift)))) || !AnchorFieldsValid(buffer, candidate, page, Scalar64.Right(capacity, shift), sector))
            return CpxError.Corrupt;
        unit = candidate;
        identity = GetWide(buffer + 72);
        return CpxError.None;
    }
}
