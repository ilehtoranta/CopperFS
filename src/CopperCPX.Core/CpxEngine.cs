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
    private ulong RW(uint field)
    {
        uint cpxArenaBase = Arena;
        return (((ulong)(memory.Read32(cpxArenaBase + (field)))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (field + 4)))));
    }

    private void WW(uint field, ulong value)
    {
        uint cpxArenaBase = Arena;
        memory.Write32(cpxArenaBase + (field), ((uint)(((ulong)(value)) >> 32)));
        memory.Write32(cpxArenaBase + (field + 4), ((uint)((ulong)(value))));
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
    private ulong Commit(ulong tx) => Journal(tx) + 69 * (memory.Read32(Arena + (GeometryQ)));
    private ulong Descriptor(ulong tx, uint index) => Journal(tx) + (1 + index) * (memory.Read32(Arena + (GeometryQ)));
    private ulong Image(ulong tx, uint index) => Journal(tx) + (5 + index) * (memory.Read32(Arena + (GeometryQ)));
    private ulong Backup
    {
        get
        {
            uint cpxArenaBase = Arena;
            return ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(GeometryBackup)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(GeometryBackup)) + 4))))));
        }
    }

    public CpxError Error => (CpxError)memory.Read32(Arena + (ErrorAt));

    public bool IsMounted
    {
        get
        {
            uint cpxArenaBase = Arena;
            return memory.Read32(cpxArenaBase + (Mounted)) != 0 && memory.Read32(cpxArenaBase + (Poisoned)) == 0;
        }
    }

    public uint Epoch => memory.Read32(Arena + (MountEpoch));
    public bool IsReadOnly => memory.Read32(Arena + (ReadOnlyAt)) != 0;

    public Wide Transaction
    {
        get
        {
            uint cpxArenaBase = Arena;
            return ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)) + 4))))));
        }
    }

    public bool HasOutstandingIo => memory.Read32(Arena + (Waiting)) != 0;
    public uint MetadataPageBytes => (memory.Read32(Arena + (PageBytes)));

    public CpxError BeginRemount()
    {
        uint cpxArenaBase = Arena;
        if (((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) && memory.Read32(cpxArenaBase + (Active)) != 0 && memory.Read32(cpxArenaBase + (StagedCount)) != 0) && (memory.Read32(cpxArenaBase + (Mounted)) != 0 && memory.Read32(cpxArenaBase + (Poisoned)) == 0))
            return CpxError.Busy;
        if (memory.Read32(cpxArenaBase + (Waiting)) != 0 || memory.Read32(cpxArenaBase + (Finished)) == 0)
            return CpxError.Busy;
        uint epoch = memory.Read32(cpxArenaBase + (MountEpoch)) + 1;
        if (epoch == 0)
            return CpxError.ResourceExhausted;
        CpxError error = Initialize(1u << (int)memory.Read32(cpxArenaBase + (SectorShift)), 1u << (int)memory.Read32(cpxArenaBase + (UnitShift)), ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Capacity)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Capacity)) + 4)))))), epoch, (memory.Read32(cpxArenaBase + (ReadOnlyAt)) != 0));
        return error == CpxError.None ? BeginOperation(CpxOperation.Mount) : error;
    }

    public CpxError Initialize(uint sectorBytes, uint allocationBytes, Wide capacitySectors, uint epoch, bool readOnly = false, uint metadataPageBytes = 0)
    {
        uint cpxArenaBase = Arena;
        uint previousToken = 0, previousPage = (memory.Read32(cpxArenaBase + (PageBytes)));
        bool previous = memory.Read32(cpxArenaBase + (Signature)) == CpxArena.Magic;
        if (memory.Read32(cpxArenaBase + (Signature)) == CpxArena.Magic)
        {
            if (memory.Read32(cpxArenaBase + (Waiting)) != 0)
                return CpxError.Busy;
            previousToken = memory.Read32(cpxArenaBase + (NextToken));
        }
        uint pageBytes = metadataPageBytes != 0 ? metadataPageBytes : previous && ShadowVolume ? previousPage : allocationBytes < 1024 ? 1024u : allocationBytes;
        if (previous && ShadowVolume && (pageBytes != previousPage || allocationBytes != (1u << (int)R(UnitShift)) ||
            sectorBytes != (1u << (int)R(SectorShift)) || (ulong)capacitySectors != RW(Capacity))) return CpxError.InvalidArgument;

        if (!CpxArena.ValidUnit(sectorBytes) || !CpxArena.ValidUnit(allocationBytes) || !CpxArena.ValidUnit(pageBytes) ||
            pageBytes < allocationBytes || sectorBytes > allocationBytes || epoch == 0)
            return CpxError.InvalidArgument;
        int ds = CpxArena.Shift(allocationBytes) - CpxArena.Shift(sectorBytes);
        ulong units = Scalar64.Right(capacitySectors, ds);
        if ((((uint)((ulong)(capacitySectors))) & ((1u << ds) - 1)) != 0 || (units) < (((ulong)(4194304u >> CpxArena.Shift(allocationBytes)))) || !Scalar64.Left(capacitySectors, CpxArena.Shift(sectorBytes), out _))
            return CpxError.InvalidArgument;
        // Initialization must be called only on a quiescent, caller-owned arena.
        for (uint n = 0; n < 512; n += 4)
            memory.Write32(cpxArenaBase + (n), 0);
        memory.Write32(cpxArenaBase + (Signature), CpxArena.Magic);
        memory.Write32(cpxArenaBase + (SectorShift), (uint)CpxArena.Shift(sectorBytes));
        memory.Write32(cpxArenaBase + (UnitShift), (uint)CpxArena.Shift(allocationBytes));
        memory.Write32(cpxArenaBase + (PageBytes), pageBytes);
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(Units);
            ulong cpxWideValue = units;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        {
            uint cpxWideAddress = cpxArenaBase + (uint)(Capacity);
            ulong cpxWideValue = capacitySectors;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(cpxArenaBase + (MountEpoch), epoch);
        memory.Write32(cpxArenaBase + (ReadOnlyAt), readOnly ? 1u : 0u);
        if (!previous) memory.Write32(Arena + CheckpointPolicy, 0);
        if (ShadowVolume) { memory.Write32(ShadowStorage, 0); memory.Write32(ShadowStorage + 56, 0); }
        InitializeGeometry();
        ResetBufferedContext(previous);
        if (!previous || previousPage != (memory.Read32(cpxArenaBase + (PageBytes))))
            InitializeCrcTable();
        memory.Write32(cpxArenaBase + (NextToken), previousToken);
        memory.Write32(cpxArenaBase + (Finished), 1);
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
        uint cpxArenaBase = Arena;
        if (memory.Read32(cpxArenaBase + (Signature)) != CpxArena.Magic)
            return CpxError.InvalidArgument;
        if (operation == CpxOperation.Format && SmallMetadata) return CpxError.Unsupported;
        if (memory.Read32(cpxArenaBase + (Waiting)) != 0 || memory.Read32(cpxArenaBase + (Finished)) == 0)
            return CpxError.Busy;
        if (operation != CpxOperation.WriteMapped)
            InvalidateFastWrite();
        if (memory.Read32(cpxArenaBase + (AbortOnly)) != 0 && operation != CpxOperation.AbortTransaction)
            return CpxError.TransactionLimit;
        if (operation is not (CpxOperation.Mount or CpxOperation.Format) && !(memory.Read32(cpxArenaBase + (Mounted)) != 0 && memory.Read32(cpxArenaBase + (Poisoned)) == 0))
            return CpxError.NotMounted;
        if (operation is < CpxOperation.Mount or > CpxOperation.ManualTrim)
            return CpxError.Unsupported;
        if (memory.Read32(cpxArenaBase + (Active)) != 0 && !(memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) && operation is CpxOperation.Mount or CpxOperation.Recover or CpxOperation.BeginTransaction or CpxOperation.Flush)
            return CpxError.Busy;
        if (((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) && memory.Read32(cpxArenaBase + (Active)) != 0 && memory.Read32(cpxArenaBase + (StagedCount)) != 0) && (memory.Read32(cpxArenaBase + (Mounted)) != 0 && memory.Read32(cpxArenaBase + (Poisoned)) == 0) && operation is CpxOperation.Mount or CpxOperation.Recover)
            return CpxError.Busy;
        if (operation >= CpxOperation.BeginTransaction && operation <= CpxOperation.AbortTransaction && memory.Read32(cpxArenaBase + (ReadOnlyAt)) != 0)
            return CpxError.ReadOnly;
        if ((operation is >= CpxOperation.WriteInline and <= CpxOperation.Trim or CpxOperation.WriteMapped or CpxOperation.TrimFree) && memory.Read32(cpxArenaBase + (ReadOnlyAt)) != 0)
            return CpxError.ReadOnly;
        if (operation >= CpxOperation.Create && operation is not (CpxOperation.ReadSymlink or CpxOperation.ManualTrim) && memory.Read32(cpxArenaBase + (ReadOnlyAt)) != 0)
            return CpxError.ReadOnly;
        if (operation >= CpxOperation.WriteInline && operation != CpxOperation.ReadTree && memory.Read32(cpxArenaBase + (Active)) != 0 && !(memory.Read32(cpxArenaBase + (BufferEnabled)) == 1))
            return CpxError.Busy;
        if (operation == CpxOperation.BeginTransaction && (creditLimit == 0 || creditLimit > 42 || !Scalar64.Add(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)) + 4)))))), ((ulong)(1)), out _)))
            return CpxError.InvalidArgument;
        if (operation >= CpxOperation.StageMetadata && operation <= CpxOperation.AbortTransaction && memory.Read32(cpxArenaBase + (Active)) == 0)
            return CpxError.InvalidArgument;
        if (operation == CpxOperation.StageMetadata && (buffer < (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(0)) * 4))) || buffer > (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(5)) * 4))) || ((buffer - (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(0)) * 4)))) & ((memory.Read32(cpxArenaBase + (PageBytes))) - 1)) != 0))
            return CpxError.InvalidArgument;
        if (operation == CpxOperation.CommitTransaction && memory.Read32(cpxArenaBase + (StagedCount)) == 0)
            return CpxError.InvalidArgument;
        if (operation == CpxOperation.ReadMetadata && !PageAddress(argument))
            return CpxError.InvalidArgument;
        if (operation == CpxOperation.Mount)
        {
            memory.Write32(cpxArenaBase + (Mounted), 0);
            memory.Write32(cpxArenaBase + (Poisoned), 0);
        }

        if (operation is CpxOperation.Mount or CpxOperation.Recover && ((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) || memory.Read32(cpxArenaBase + (BufferFormatEnabled)) != 0 && memory.Read32(cpxArenaBase + (BufferBytes)) >= CompactBufferedStorageBytes((memory.Read32(cpxArenaBase + (PageBytes))))))
        {
            ClearBufferedObjects();
            ClearBufferedClean();
            memory.Write32(cpxArenaBase + (BufferFormatEnabled), 1);
            memory.Write32(cpxArenaBase + (BufferEnabled), 0);
        }

        if (operation >= CpxOperation.WriteInline)
        {
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(FilePosition);
                ulong cpxWideValue = default;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

            memory.Write32(cpxArenaBase + (FileBytes), 0);
            memory.Write32(cpxArenaBase + (FileValue), 0);
            memory.Write32(cpxArenaBase + (TrimCount), 0);
            memory.Write32(cpxArenaBase + (FileBuffer), 0);
            memory.Write32(cpxArenaBase + (DataProgress), 0);
        }

        memory.Write32(cpxArenaBase + (CallDepth), 0);
        memory.Write32(cpxArenaBase + (OperationAt), (uint)operation);
        memory.Write32(cpxArenaBase + (Phase), 0);
        memory.Write32(cpxArenaBase + (ErrorAt), 0);
        memory.Write32(cpxArenaBase + (Finished), 0);
        memory.Write32(cpxArenaBase + (CancelAt), 0);
        memory.Write32(cpxArenaBase + (IoReady), 0);
        memory.Write32(cpxArenaBase + (NsPublished), 0);
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(Target);
            ulong cpxWideValue = argument;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        {
            uint cpxWideAddress = cpxArenaBase + (uint)(ObjectKey);
            ulong cpxWideValue = argument;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(cpxArenaBase + (SourcePage), buffer);
        if (operation == CpxOperation.BeginTransaction)
            memory.Write32(cpxArenaBase + (StagedLimit), creditLimit);
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

    public CpxIo Request
    {
        get
        {
            uint cpxArenaBase = Arena;
            return new()
            {
                Kind = (IoKind)memory.Read32(cpxArenaBase + (PendingKind)),
                Sector = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(PendingSector)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(PendingSector)) + 4)))))),
                Buffer = memory.Read32(cpxArenaBase + (PendingBuffer)),
                Bytes = memory.Read32(cpxArenaBase + (PendingBytes)),
                Token = memory.Read32(cpxArenaBase + (Token))
            };
        }
    }

    public CpxError CompleteIo(uint token, uint actual, bool success)
    {
        uint cpxArenaBase = Arena;
        if (memory.Read32(cpxArenaBase + (Waiting)) == 0 || token != memory.Read32(cpxArenaBase + (Token)))
            return CpxError.StaleCompletion;
        memory.Write32(cpxArenaBase + (Waiting), 0);
        memory.Write32(cpxArenaBase + (IoReady), 1);
        bool validCompletion = success && actual == memory.Read32(cpxArenaBase + PendingBytes) && memory.Read32(cpxArenaBase + CancelAt) == 0;
        if (ShadowVolume) CompleteShadowIo(validCompletion);
        if (!success || ((IoKind)memory.Read32(cpxArenaBase + (PendingKind))is IoKind.Read or IoKind.Write or IoKind.Discard or IoKind.QueryDiscard && actual != memory.Read32(cpxArenaBase + (PendingBytes))))
            Fail(CpxError.Io);
        else if (memory.Read32(cpxArenaBase + (CancelAt)) != 0)
            Fail(CpxError.Cancelled);
        else if ((CpxOperation)memory.Read32(cpxArenaBase + (OperationAt)) == CpxOperation.ManualTrim && (IoKind)memory.Read32(cpxArenaBase + (PendingKind)) == IoKind.Discard && (memory.Read32(cpxArenaBase + (TrimDisabled)) == 0))
            CountManualRange();
        return Error;
    }

    /// <summary>Only an explicit command-not-supported response disables discard.
    /// Other errors still poison the mount; transport errors are not capability discovery.</summary>
    public CpxError CompleteUnsupportedDiscard(uint token)
    {
        uint cpxArenaBase = Arena;
        if (memory.Read32(cpxArenaBase + (Waiting)) == 0 || token != memory.Read32(cpxArenaBase + (Token)))
            return CpxError.StaleCompletion;
        if ((IoKind)memory.Read32(cpxArenaBase + (PendingKind))is not (IoKind.Discard or IoKind.QueryDiscard))
            return CpxError.InvalidArgument;
        memory.Write32(cpxArenaBase + (TrimDisabled), 1);
        return CompleteIo(token, memory.Read32(cpxArenaBase + (PendingBytes)), true);
    }

    /// <summary>Caller must quiesce I/O and deliver its completion before reuse.</summary>
    public void Cancel()
    {
        uint cpxArenaBase = Arena;
        memory.Write32(cpxArenaBase + (CancelAt), 1);
        if (memory.Read32(cpxArenaBase + (Waiting)) == 0)
            Fail(CpxError.Cancelled);
    }

    private StepResult Fail(CpxError error)
    {
        uint cpxArenaBase = Arena;
        memory.Write32(cpxArenaBase + (ErrorAt), (uint)error);
        memory.Write32(cpxArenaBase + (Poisoned), 1);
        memory.Write32(cpxArenaBase + (Finished), 1);
        return StepResult.Failed;
    }

    private StepResult Done()
    {
        uint cpxArenaBase = Arena;
        if (memory.Read32(cpxArenaBase + (CallDepth)) == 0 && (CpxOperation)memory.Read32(cpxArenaBase + (OperationAt))is CpxOperation.Mount or CpxOperation.Recover && memory.Read32(cpxArenaBase + (BufferFormatEnabled)) != 0)
        {
            memory.Write32(cpxArenaBase + (BufferEnabled), memory.Read32(cpxArenaBase + (BufferBytes)) >= CompactBufferedStorageBytes((memory.Read32(cpxArenaBase + (PageBytes)))) ? 1u : 0u);
            memory.Write32(cpxArenaBase + (BufferFormatEnabled), 0);
            memory.Write32(cpxArenaBase + (BufferTrustedMask), 0);
            if ((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1))
                ClearBufferedObjects();
        }

        memory.Write32(cpxArenaBase + (Finished), 1);
        return StepResult.Complete;
    }

    private StepResult Reject(CpxError error)
    {
        uint cpxArenaBase = Arena;
        memory.Write32(cpxArenaBase + (ErrorAt), (uint)error);
        memory.Write32(cpxArenaBase + (Finished), 1);
        return StepResult.Failed;
    }

    private StepResult Issue(IoKind kind, ulong unit, uint buffer, uint bytes, uint nextPhase)
    {
        uint cpxArenaBase = Arena;
        if ((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) && memory.Read32(cpxArenaBase + (BufferCommitting)) == 0)
        {
            if (kind == IoKind.Flush && (CpxOperation)memory.Read32(cpxArenaBase + (OperationAt))is not (CpxOperation.Flush or CpxOperation.ManualTrim))
            {
                memory.Write32(cpxArenaBase + (Phase), nextPhase);
                return StepResult.Pending;
            }

            // Reclaim may identify space in an unpublished group. Optional
            // discard must wait until that free-space metadata is persistent.
            if (kind == IoKind.Discard && ((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) && memory.Read32(cpxArenaBase + (Active)) != 0 && memory.Read32(cpxArenaBase + (StagedCount)) != 0))
            {
                memory.Write32(cpxArenaBase + (Phase), nextPhase);
                return StepResult.Pending;
            }
        }

        if (kind == IoKind.Read)
            InvalidateBufferedPage(buffer);
        if (kind == IoKind.Write && !(memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) && ((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) || memory.Read32(cpxArenaBase + (BufferFormatEnabled)) != 0 && memory.Read32(cpxArenaBase + (BufferBytes)) >= CompactBufferedStorageBytes((memory.Read32(cpxArenaBase + (PageBytes))))))
            InvalidateBufferedClean(unit);
        if (memory.Read32(cpxArenaBase + (Waiting)) != 0)
            return Fail(CpxError.Busy);
        if (!Scalar64.Left(unit, (int)(memory.Read32(cpxArenaBase + (UnitShift)) - memory.Read32(cpxArenaBase + (SectorShift))), out var sector))
            return Fail(CpxError.Corrupt);
        if (kind is IoKind.Read or IoKind.Write or IoKind.Discard)
        {
            if ((bytes & ((1u << (int)memory.Read32(cpxArenaBase + (SectorShift))) - 1)) != 0 || bytes == 0 || !Scalar64.Add(sector, ((ulong)(bytes >> (int)memory.Read32(cpxArenaBase + (SectorShift)))), out var end) || (end) > (((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Capacity)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Capacity)) + 4))))))))
                return Fail(CpxError.Corrupt);
        }

        if (kind is IoKind.Write or IoKind.Discard && memory.Read32(cpxArenaBase + (ReadOnlyAt)) != 0)
            return Fail(CpxError.ReadOnly);
        uint next = memory.Read32(cpxArenaBase + (NextToken)) + 1;
        if (next == 0)
            return Fail(CpxError.ResourceExhausted);
        memory.Write32(cpxArenaBase + (NextToken), next);
        memory.Write32(cpxArenaBase + (Token), next);
        memory.Write32(cpxArenaBase + (PendingKind), (uint)kind);
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(PendingSector);
            ulong cpxWideValue = sector;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(cpxArenaBase + (PendingBuffer), buffer);
        memory.Write32(cpxArenaBase + (PendingBytes), bytes);
        memory.Write32(cpxArenaBase + (Waiting), 1);
        memory.Write32(cpxArenaBase + (IoReady), 0);
        memory.Write32(cpxArenaBase + (Phase), nextPhase);
        return StepResult.NeedIo;
    }

    public StepResult Step()
    {
        uint context = Arena;
        if (memory.Read32(context + Waiting) != 0) return StepResult.NeedIo;
        if (memory.Read32(context + ErrorAt) != 0) return StepResult.Failed;
        if (memory.Read32(context + Finished) != 0) return StepResult.Complete;
        uint policy = memory.Read32(context + CheckpointPolicy);
        uint budget = (policy & 2) != 0 ? 4u : 1u;
        uint shadow = (policy & 4) != 0 ? policy & ~7u : 0;
        // The single owner admits one bounded visit. A pure continuation keeps
        // its healthy/pending state; I/O, failure and completion end the visit.
        // Cancellation is still observed before each of the four phases.
        for (uint turn = 0; turn < budget; turn++)
        {
            if (memory.Read32(context + CancelAt) != 0) return Fail(CpxError.Cancelled);
            StepResult result = shadow != 0 && memory.Read32(shadow) == 1 ? ShadowReadStep() : DispatchStep();
            if (result == StepResult.Complete && memory.Read32(context + CallDepth) != 0)
            {
                uint depth = memory.Read32(context + CallDepth) - 1, frame = context + 9472 + depth * 8;
                memory.Write32(context + CallDepth, depth);
                memory.Write32(context + OperationAt, memory.Read32(frame));
                memory.Write32(context + Phase, memory.Read32(frame + 4));
                memory.Write32(context + Finished, 0);
                continue;
            }
            if (result == StepResult.Failed)
            {
                // Rejected unpublished operations can roll back. A failed I/O
                // owner stays poisoned and must quiesce before mount reuse.
                uint owner = memory.Read32(context + CallDepth) != 0 ? memory.Read32(context + 9472) : memory.Read32(context + OperationAt);
                if (owner >= (uint)CpxOperation.WriteInline && memory.Read32(context + Poisoned) == 0)
                {
                    memory.Write32(context + CallDepth, 0);
                    if (memory.Read32(context + BufferEnabled) == 1) DiscardBufferedOperation();
                    else
                    {
                        memory.Write32(context + Active, 0);
                        memory.Write32(context + AbortOnly, 0);
                        memory.Write32(context + StagedCount, 0);
                    }
                }
            }
            if (result != StepResult.Pending) return result;
        }
        return StepResult.Pending;
    }
    private StepResult DispatchStep()
    {
        uint cpxArenaBase = Arena;
        switch ((CpxOperation)memory.Read32(cpxArenaBase + (OperationAt)))
        {
            case CpxOperation.Mount:
                return MountStep();
            case CpxOperation.Recover:
                return ShadowVolume ? ShadowRecoveryStep() : RecoveryStep();
            case CpxOperation.Flush:
                if (memory.Read32(cpxArenaBase + (Phase)) == 0 && !(memory.Read32(cpxArenaBase + (ReadOnlyAt)) != 0))
                    return Call(CpxOperation.PruneObjects, 1);
                if (memory.Read32(cpxArenaBase + (Phase)) < 2 && ((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) && memory.Read32(cpxArenaBase + (Active)) != 0 && memory.Read32(cpxArenaBase + (StagedCount)) != 0))
                    return RetireBufferedGroup(2);
                return memory.Read32(cpxArenaBase + (Phase)) < 3 ? Issue(IoKind.Flush, default, 0, 0, 3) : Done();
            case CpxOperation.ReadMetadata:
                if (memory.Read32(cpxArenaBase + (Phase)) == 0)
                    return ReadStaged(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Target)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Target)) + 4)))))), (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(0)) * 4))), 1);
                return ValidPage((memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(0)) * 4))), ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Target)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Target)) + 4))))))) && ReadableGeneration((memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(0)) * 4)))) ? Done() : Fail(CpxError.Corrupt);
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
                return memory.Read32(cpxArenaBase + (Phase)) == 0 ? PublishMetadata(memory.Read32(cpxArenaBase + (PreparedBuffer)), ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(PreparedTarget)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(PreparedTarget)) + 4)))))), 1) : Done();
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
            case BlobOperation:
                return BlobStep();
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
        uint cpxArenaBase = Arena;
        uint depth = memory.Read32(cpxArenaBase + (CallDepth));
        if (depth == 8)
            return Fail(CpxError.Corrupt);
        uint frame = cpxArenaBase + 9472 + depth * 8;
        memory.Write32(frame, memory.Read32(cpxArenaBase + (OperationAt)));
        memory.Write32(frame + 4, nextPhase);
        memory.Write32(cpxArenaBase + (CallDepth), depth + 1);
        memory.Write32(cpxArenaBase + (OperationAt), (uint)operation);
        memory.Write32(cpxArenaBase + (Phase), 0);
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
        uint cpxArenaBase = Arena;
        // A direct inode removes the global object-tree split from namespace
        // admission. Account for both string fragments and an eight-level
        // directory path before selecting rollback/retry storage.
        if (SmallMetadata) credits = credits == 39 ? 25u : credits == 22 ? 23u : credits == 30 ? 34u : credits == 7 ? 18u : credits;
        if (!Scalar64.Add(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)) + 4)))))), ((ulong)(1)), out _))
            return Reject(CpxError.ResourceExhausted);
        if ((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1))
        {
            // Inline extent writes have already checked type, protection, free
            // data space and reserve admission. Their eight-image bound fits
            // without retry here; any later corruption or I/O error poisons the
            // mount. Keep rollback snapshots for namespace edits and capacity
            // retries, rather than copying the old group for every data run.
            if (credits == 8 && (CpxOperation)memory.Read32(cpxArenaBase + (OperationAt)) == CpxOperation.WriteMapped && memory.Read32(cpxArenaBase + (StagedCount)) <= 34)
            {
                memory.Write32(cpxArenaBase + (BufferSnapshot), 0);
                memory.Write32(cpxArenaBase + (BufferFullSnapshot), 0);
            }
            else
                SnapshotBufferedOperation(credits);
        }

        memory.Write32(cpxArenaBase + (StagedLimit), credits);
        return Call(CpxOperation.BeginTransaction, nextPhase);
    }

    private void InitializeGeometry()
    {
        uint cpxArenaBase = Arena;
        uint page = (memory.Read32(cpxArenaBase + (PageBytes))), q = page >> (int)memory.Read32(cpxArenaBase + (UnitShift));
        memory.Write32(cpxArenaBase + (GeometryShift), (uint)CpxArena.Shift(page));
        memory.Write32(cpxArenaBase + (GeometryQ), q);
        memory.Write32(cpxArenaBase + (GeometryFirst), 2097152u >> (int)memory.Read32(cpxArenaBase + (UnitShift)));
        memory.Write32(cpxArenaBase + (GeometryAnchor), 65536u >> (int)memory.Read32(cpxArenaBase + (UnitShift)));
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(GeometryBackup);
            ulong cpxWideValue = (((ulong)(memory.Read32(cpxArenaBase + (Units)))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (Units + 4)) & ~(q - 1)))) - q;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        for (uint slot = 0; slot < 6; slot++)
            memory.Write32(cpxArenaBase + (GeometryPages + slot * 4), cpxArenaBase + CpxArena.Scratch + slot * page);
        uint index = cpxArenaBase + CpxArena.Scratch + 6 * page;
        memory.Write32(cpxArenaBase + (GeometryIndex), index);
        memory.Write32(cpxArenaBase + (GeometryCrc), index + CpxArena.IndexBytes + CpxArena.PoolBytes);
    }

    private bool PageAddress(ulong unit)
    {
        uint cpxArenaBase = Arena;
        uint q = (memory.Read32(cpxArenaBase + (GeometryQ)));
        if ((((uint)((ulong)(unit))) & (q - 1)) != 0)
            return false;
        uint low = unchecked(((uint)((ulong)(unit))) + q), high = unchecked(((uint)(((ulong)(unit)) >> 32)) + (low < ((uint)((ulong)(unit))) ? 1u : 0u)), limitHigh = memory.Read32(cpxArenaBase + (Units));
        return high >= ((uint)(((ulong)(unit)) >> 32)) && (high < limitHigh || high == limitHigh && low <= memory.Read32(cpxArenaBase + (Units + 4)));
    }

    private bool Allocatable(ulong unit)
    {
        if (ShadowVolume && (((uint)unit & (MetadataSpan - 1)) != 0 || !PageAddress(unit + Q) || !DataUnit(unit + 2 * Q - 1))) return false;
        if (!PageAddress(unit) || !DataUnit(unit))
            return false;
        return true;
    }

    private uint Crc(uint buffer,uint bytes,bool page)
    {
        uint arena=Arena;
        var input=new CpxCrcInput { Buffer=buffer,Bytes=bytes,Page=page,Table=memory.Read32(arena+GeometryCrc),Word=arena+4080,
            Zeros=page&&(memory.Read32(arena+BufferEnabled)==1||memory.Read32(arena+BufferFormatEnabled)!=0&&memory.Read32(arena+BufferBytes)>=CompactBufferedStorageBytes(memory.Read32(arena+PageBytes)))?memory.Read32(arena+BufferAddress):0 };
        return CrcKernel(ref memory,in input);
    }

    // A linear CRC transform advances 64 zero bytes with four lookups. CPX
    // control pages have large zero-filled regions; the exact disk checksum is
    // unchanged. The optional table replaces eight clean cache pages and saves
    // memory overall. Enable runs only while all scratch is caller-owned.
    private void InitializeCrcTable() => InitializeCrcTableAt(CrcTable);

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
        uint cpxArenaBase = Arena;
        if (memory.Read32(p) != MediaMagic || memory.Read16(p + 6) != MediaVersion || !(((ulong)(((((ulong)(memory.Read32(((uint)(p + 16))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 16)) + 4))))))) == ((ulong)(address))))
            return false;
        if (TrustedBufferedPage(p, address))
            return true;
        if (memory.Read32(p + 8) != Crc(p, (memory.Read32(cpxArenaBase + (PageBytes))), true))
            return false;
        if (((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) || memory.Read32(cpxArenaBase + (BufferFormatEnabled)) != 0 && memory.Read32(cpxArenaBase + (BufferBytes)) >= CompactBufferedStorageBytes((memory.Read32(cpxArenaBase + (PageBytes))))) && memory.Read32(cpxArenaBase + (BufferCommitting)) == 0)
        {
            CacheBufferedClean(p, address);
            if ((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1))
                TrustBufferedPage(p, address);
        }

        return true;
    }

    private bool ReadableGeneration(uint p)
    {
        uint cpxArenaBase = Arena;
        return (((((ulong)(memory.Read32(((uint)(p + 32))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 32)) + 4)))))) <= (((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)) + 4)))))) + (memory.Read32(cpxArenaBase + (Active)) != 0 ? 1u : 0u));
    }

    private bool Control(uint p, uint type, ulong address) => ValidPage(p, address) && memory.Read16(p + 4) == type && (((ulong)(((((ulong)(memory.Read32(((uint)(p + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 24)) + 4))))))) == 0);
    private bool IdentityMatches(uint p)
    {
        uint cpxArenaBase = Arena;
        return (((ulong)(((((ulong)(memory.Read32(((uint)(p + 64))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 64)) + 4))))))) == 0) && (((ulong)(((((ulong)(memory.Read32(((uint)(p + 72))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 72)) + 4))))))) == ((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Identity)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Identity)) + 4)))))))));
    }

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
        Clear(p, (memory.Read32(Arena + (PageBytes))));
        memory.Write32(p, MediaMagic);
        memory.Write16(p + 4, type);
        memory.Write16(p + 6, MediaVersion);
        {
            uint cpxWideAddress = (uint)(p + 16);
            ulong cpxWideValue = address;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        {
            uint cpxWideAddress = (uint)(p + 32);
            ulong cpxWideValue = tx;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }
    }

    private void Seal(uint p, uint payload)
    {
        uint cpxArenaBase = Arena;
        memory.Write16(p + 52, payload);
        if ((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) && memory.Read32(cpxArenaBase + (BufferCommitting)) == 0)
        {
            memory.Write32(p + 8, 0);
            TrustBufferedPage(p, ((((ulong)(memory.Read32(((uint)(p + 16))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 16)) + 4))))));
        }
        else
            memory.Write32(p + 8, Crc(p, (memory.Read32(cpxArenaBase + (PageBytes))), true));
    }

    private StepResult MountStep()
    {
        uint cpxArenaBase = Arena;
        uint p = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(0)) * 4)));
        switch (memory.Read32(cpxArenaBase + (Phase)))
        {
            case 0:
                return Issue(IoKind.Read, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))), p, (memory.Read32(cpxArenaBase + (PageBytes))), 1);
            case 1:
                if (!Control(p, 1, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor)))))))
                    return Issue(IoKind.Read, (((((ulong)(memory.Read32(cpxArenaBase + (((uint)(GeometryBackup)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(GeometryBackup)) + 4))))))), p, (memory.Read32(cpxArenaBase + (PageBytes))), 2);
                if (!AnchorFieldsValid(p))
                    return Fail(CpxError.Corrupt);
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(Identity);
                ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)(p + 72))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 72)) + 4)))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                return Issue(IoKind.Read, (((((ulong)(memory.Read32(cpxArenaBase + (((uint)(GeometryBackup)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(GeometryBackup)) + 4))))))), p, (memory.Read32(cpxArenaBase + (PageBytes))), 3);
            case 2:
                if (!Control(p, 1, (((((ulong)(memory.Read32(cpxArenaBase + (((uint)(GeometryBackup)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(GeometryBackup)) + 4)))))))) || !AnchorFieldsValid(p))
                    return Fail(CpxError.Corrupt);
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(Identity);
                ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)(p + 72))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 72)) + 4)))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write32(cpxArenaBase + (OperationAt), (uint)CpxOperation.Recover);
                memory.Write32(cpxArenaBase + (Phase), 0);
                return StepResult.Pending;
            case 3:
                if (ValidPage(p, (((((ulong)(memory.Read32(cpxArenaBase + (((uint)(GeometryBackup)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(GeometryBackup)) + 4)))))))) && (memory.Read16(p + 4) != 1 || !(((ulong)(((((ulong)(memory.Read32(((uint)(p + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 24)) + 4))))))) == 0) || !AnchorFieldsValid(p) || !(((ulong)(((((ulong)(memory.Read32(((uint)(p + 72))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 72)) + 4))))))) == ((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Identity)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Identity)) + 4)))))))))))
                    return Fail(CpxError.Corrupt);
                memory.Write32(cpxArenaBase + (OperationAt), (uint)CpxOperation.Recover);
                memory.Write32(cpxArenaBase + (Phase), 0);
                return StepResult.Pending;
            default:
                return Fail(CpxError.Corrupt);
        }
    }

    // Discovery has authenticated both possible copies at every legal page
    // size. Enter the ordinary recovery path without re-reading a bad sector.
    public CpxError BeginMountFromDiscoveredAnchor(Wide identity)
    {
        uint cpxArenaBase = Arena;
        if ((((ulong)(identity)) == 0))
            return CpxError.InvalidArgument;
        CpxError error = BeginOperation(CpxOperation.Mount);
        if (error == CpxError.None)
        {
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(Identity);
                ulong cpxWideValue = identity;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

            memory.Write32(cpxArenaBase + (OperationAt), (uint)CpxOperation.Recover);
        }

        return error;
    }

    private bool AnchorFieldsValid(uint p)
    {
        uint cpxArenaBase = Arena;
        bool valid = AnchorFieldsValid(p, 1u << (int)memory.Read32(cpxArenaBase + (UnitShift)), (memory.Read32(cpxArenaBase + (PageBytes))), ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Units)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Units)) + 4)))))), 1u << (int)memory.Read32(cpxArenaBase + (SectorShift)));
        if (valid && ShadowVolume)
        {
            uint features = (uint)GetWide(p + 96), previous = memory.Read32(ShadowStorage + 56);
            if (previous != 0 && previous != features) return false;
            memory.Write32(ShadowStorage + 56, features);
            if (previous == 0 && features >= 3)
            {
                if (CpxShadowMapGeometry.TryCreate(1u << (int)R(SectorShift), P, RW(Capacity),
                    524288UL >> (int)R(SectorShift), out var geometry, true) != CpxError.None) return false;
                geometry.Store(ref memory, ShadowStorage + 64);
                ulong start = 2097152UL >> (int)R(UnitShift);
                ulong end = (ulong)geometry.MapEndSector >> (int)(R(UnitShift) - R(SectorShift));
                if (end > start) start = ((end + 65535) >> 16) << 16;
                PutWide(ShadowStorage + 40, start);
            }
        }
        return valid;
    }

    private bool AnchorFieldsValid(uint p, uint unit, uint page, ulong units, uint sector)
    {
        int shift = CpxArena.Shift(unit);
        uint anchor = 65536u >> shift, q = page >> shift;
        bool shadow = memory.Read16(p + 6) == 5;
        ulong features = GetWide(p + 96);
        return memory.Read32(p + 80) == unit && memory.Read32(p + 84) == page && GetWide(p + 88) == units &&
            (features == 1 || shadow && features is 2 or 3 or 4 or 5) && (page >= 1024 || shadow && features is 4 or 5) && memory.Read32(p + 104) == sector && memory.Read32(p + 108) == 1 &&
            GetWide(p + 64) == 0 && GetWide(p + 72) != 0 && GetWide(p + 112) == anchor + 8 * q &&
            GetWide(p + 120) == anchor + (shadow ? 4u : 3u) * q;
    }

    // One CRC per candidate. A torn copy can be skipped. A CRC-valid CPX4
    // header with inconsistent geometry cannot authorize a different volume.
    public CpxError InspectAnchor(uint buffer, uint page, uint sector, Wide capacity, Wide location, out uint unit, out Wide identity)
        => InspectAnchor(buffer, page, sector, capacity, location, out unit, out identity, out _);

    public CpxError InspectAnchor(uint buffer, uint page, uint sector, Wide capacity, Wide location, out uint unit, out Wide identity, out uint version, uint preparedChecksum = 0)
    {
        unit = 0;
        identity = default;
        version = memory.Read32(buffer) == 0x43505835u ? 5u : 4u;
        if (!CpxArena.ValidUnit(page) || !CpxArena.ValidUnit(sector) || sector > page)
            return CpxError.InvalidArgument;
        if (memory.Read32(buffer) != CpxArena.Magic && memory.Read32(buffer) != 0x43505835u)
            return CpxError.NotFound;
        if (preparedChecksum != 0 && ((preparedChecksum & 3) != 0 || preparedChecksum > uint.MaxValue - 4096)) return CpxError.InvalidArgument;
        uint checksum;
        if (preparedChecksum == 0) checksum = AnchorCrc(buffer, page);
        else
        {
            var input = new CpxCrcInput { Buffer = buffer, Bytes = page, Table = preparedChecksum, Word = Arena + 4080, Page = true };
            checksum = CrcKernel(ref memory, in input);
        }
        if (memory.Read32(buffer + 8) != checksum) return CpxError.NotFound;
        uint candidate = memory.Read32(buffer + 80);
        uint minimumPage = version == 5 && GetWide(buffer + 96) is 4 or 5 ? 512u : 1024u;
        if (!CpxArena.ValidUnit(candidate) || candidate < sector || (candidate < minimumPage ? minimumPage : candidate) != page || memory.Read16(buffer + 4) != 1 || memory.Read16(buffer + 6) != version || memory.Read16(buffer + 52) != 64 || !(((ulong)(((((ulong)(memory.Read32(((uint)(buffer + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(buffer + 24)) + 4))))))) == 0))
            return CpxError.Corrupt;
        int shift = CpxArena.Shift(candidate) - CpxArena.Shift(sector);
        if ((((uint)((ulong)(capacity))) & ((1u << shift) - 1)) != 0 || !(((ulong)(((((ulong)(memory.Read32(((uint)(buffer + 16))))) << 32 | ((ulong)(memory.Read32(((uint)(buffer + 16)) + 4))))))) == ((ulong)(Scalar64.Right(location, shift)))) || !AnchorFieldsValid(buffer, candidate, page, Scalar64.Right(capacity, shift), sector))
            return CpxError.Corrupt;
        unit = candidate;
        identity = ((((ulong)(memory.Read32(((uint)(buffer + 72))))) << 32 | ((ulong)(memory.Read32(((uint)(buffer + 72)) + 4)))));
        return CpxError.None;
    }
}
