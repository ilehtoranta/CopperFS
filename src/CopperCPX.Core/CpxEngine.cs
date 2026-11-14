namespace CopperCPX;

/// <summary>Caller-owned continuation engine. All persistent state lives in Arena.
/// A Step never retains a CPU frame or a borrowed buffer after returning.</summary>
public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    private TMemory memory;
    public uint Arena;
    // Mount context; operation state occupies the separately budgeted 8 KiB area.
    private const uint Signature=0, SectorShift=4, UnitShift=8, PageBytes=12, Units=16, Identity=24,
        Generation=32, Mounted=40, Poisoned=44, Waiting=48, Token=52, ErrorAt=56, OperationAt=60,
        Phase=64, Finished=68, Capacity=72, ReadOnlyAt=80, Count=84, Cursor=88, Digest=92,
        Candidate=96, CheckpointOne=104, CheckpointTwo=112, ValidOne=120, ValidTwo=124,
        Target=128, PendingKind=136, PendingSector=140, PendingBuffer=148, PendingBytes=152,
        NextToken=156, IoReady=160, CancelAt=164, MountEpoch=168, ScanIndex=172,
        Active=176, StagedCount=180, StagedLimit=184, SourcePage=188, AbortOnly=192,
        ObjectKey=196, TreeAddress=204, TreeDepth=212, ExpectedLevel=216, TreeSlot=220, TreeCount=224, TreeLevel=228,
        FilePosition=232, FileBytes=240, FileValue=244, TrimDisabled=248, TrimCount=252,
        TreeKindAt=256, SeekAt=260, SeekRoot=264, SeekOwner=272, SeekHigh=280, SeekLow=288,
        SeekDepth=296, SeekExpected=300, SeekAddress=304, SeekCount=312, SeekLevel=316,
        SeekScan=320, SeekSlot=324, SeekFound=328, SeekDirection=332,
        FileBuffer=336, DataPhysical=340, DataLogical=348, DataTake=356, DataIntra=360,
        DataFlags=364, SavedLeaf=368, SavedSlot=376, DataProgress=380,
        NameLength=384, DirectoryRoot=388, LookupTarget=396, LookupFoundAt=404, NameCursorHigh=408, NameCursorLow=416, TrimCursor=424,
        AllocationScan=432, NewPhysical=440, AllocationMode=448, OldFileSize=456, CallDepth=464,
        MetadataTaken=468, MetadataLimit=472, MetadataReleased=476, PreparedBuffer=480, PreparedTarget=488;
    public CpxEngine(TMemory memory,uint arena) { this.memory=memory; Arena=arena; }
    private uint R(uint field) => memory.Read32(Arena+field);
    private void W(uint field,uint value) => memory.Write32(Arena+field,value);
    private Wide RW(uint field) => new(R(field),R(field+4));
    private void WW(uint field,Wide value) { W(field,value.High); W(field+4,value.Low); }
    private Wide GetWide(uint p) => new(memory.Read32(p),memory.Read32(p+4));
    private void PutWide(uint p,Wide value) { memory.Write32(p,value.High); memory.Write32(p+4,value.Low); }
    private uint Read16(uint p) => (uint)(memory.Read8(p)<<8)|memory.Read8(p+1);
    private void Write16(uint p,uint value) { memory.Write8(p,(byte)(value>>8)); memory.Write8(p+1,(byte)value); }
    private uint P => R(PageBytes);
    private uint Q => P>>(int)R(UnitShift);
    private uint Page(uint slot) => Arena+CpxArena.Scratch+slot*P;
    private uint Index => Arena+CpxArena.Scratch+6*P;
    private Wide Anchor => Wide.From(65536u>>(int)R(UnitShift));
    private Wide Journal(Wide tx) => Wide.From(((tx.Low&1)!=0?1048576u:1572864u)>>(int)R(UnitShift));
    private Wide Commit(Wide tx) => Journal(tx)+69*Q;
    private Wide Descriptor(Wide tx,uint index) => Journal(tx)+(1+index)*Q;
    private Wide Image(Wide tx,uint index) => Journal(tx)+(5+index)*Q;
    private Wide Backup => new Wide(R(Units),R(Units+4)&~(Q-1))-Q;
    public CpxError Error => (CpxError)R(ErrorAt);
    public bool IsMounted => R(Mounted)!=0 && R(Poisoned)==0;
    public bool IsReadOnly => R(ReadOnlyAt)!=0;
    public Wide Transaction => RW(Generation);
    public bool HasOutstandingIo => R(Waiting)!=0;
    public uint MetadataPageBytes => P;
    public CpxError BeginRemount()
    {
        if(R(Waiting)!=0||R(Finished)==0) return CpxError.Busy;
        uint epoch=R(MountEpoch)+1; if(epoch==0) return CpxError.ResourceExhausted;
        CpxError error=Initialize(1u<<(int)R(SectorShift),1u<<(int)R(UnitShift),RW(Capacity),epoch,IsReadOnly);
        return error==CpxError.None?BeginOperation(CpxOperation.Mount):error;
    }

    public CpxError Initialize(uint sectorBytes,uint allocationBytes,Wide capacitySectors,uint epoch,bool readOnly=false)
    {
        uint previousToken=0;
        if(R(Signature)==CpxArena.Magic) { if(R(Waiting)!=0) return CpxError.Busy; previousToken=R(NextToken); }
        if(!CpxArena.ValidUnit(sectorBytes)||!CpxArena.ValidUnit(allocationBytes)||sectorBytes>allocationBytes||epoch==0) return CpxError.InvalidArgument;
        int ds=CpxArena.Shift(allocationBytes)-CpxArena.Shift(sectorBytes);
        Wide units=Wide.Right(capacitySectors,ds);
        if((capacitySectors.Low&((1u<<ds)-1))!=0||Wide.Compare(units,Wide.From(4194304u>>CpxArena.Shift(allocationBytes)))<0||
            !Wide.Left(capacitySectors,CpxArena.Shift(sectorBytes),out _)) return CpxError.InvalidArgument;
        // Initialization must be called only on a quiescent, caller-owned arena.
        for(uint n=0;n<512;n+=4) W(n,0);
        W(Signature,CpxArena.Magic); W(SectorShift,(uint)CpxArena.Shift(sectorBytes)); W(UnitShift,(uint)CpxArena.Shift(allocationBytes));
        W(PageBytes,allocationBytes<1024?1024:allocationBytes); WW(Units,units); WW(Capacity,capacitySectors); W(MountEpoch,epoch); W(ReadOnlyAt,readOnly?1u:0u);
        W(NextToken,previousToken); W(Finished,1); DefaultLabel(); return CpxError.None;
    }
    public CpxError BeginOperation(CpxOperation operation,Wide argument=default,uint buffer=0,uint creditLimit=42)
    {
        // Parameterized operations must use their typed admission methods. This
        // prevents a raw operation number from reusing a previous caller's input.
        if(operation is >=CpxOperation.WriteInline and <=CpxOperation.Lookup || operation>=CpxOperation.Create&&operation!=CpxOperation.Reclaim) return CpxError.InvalidArgument;
        return StartOperation(operation,argument,buffer,creditLimit);
    }
    private CpxError StartOperation(CpxOperation operation,Wide argument=default,uint buffer=0,uint creditLimit=42)
    {
        if(R(Signature)!=CpxArena.Magic) return CpxError.InvalidArgument;
        if(R(Waiting)!=0||R(Finished)==0) return CpxError.Busy;
        if(R(AbortOnly)!=0 && operation!=CpxOperation.AbortTransaction) return CpxError.TransactionLimit;
        if(operation is not (CpxOperation.Mount or CpxOperation.Format) && !IsMounted) return CpxError.NotMounted;
        if(operation is < CpxOperation.Mount or > CpxOperation.ManualTrim) return CpxError.Unsupported;
        if(R(Active)!=0 && operation is CpxOperation.Mount or CpxOperation.Recover or CpxOperation.BeginTransaction or CpxOperation.Flush) return CpxError.Busy;
        if(operation>=CpxOperation.BeginTransaction && operation<=CpxOperation.AbortTransaction && R(ReadOnlyAt)!=0) return CpxError.ReadOnly;
        if((operation is >=CpxOperation.WriteInline and <=CpxOperation.Trim or CpxOperation.WriteMapped or CpxOperation.TrimFree) && R(ReadOnlyAt)!=0) return CpxError.ReadOnly;
        if(operation>=CpxOperation.Create && operation is not (CpxOperation.ReadSymlink or CpxOperation.ManualTrim) && R(ReadOnlyAt)!=0) return CpxError.ReadOnly;
        if(operation>=CpxOperation.WriteInline && operation!=CpxOperation.ReadTree && R(Active)!=0) return CpxError.Busy;
        if(operation==CpxOperation.BeginTransaction && (creditLimit==0||creditLimit>42||!Wide.Add(RW(Generation),Wide.From(1),out _))) return CpxError.InvalidArgument;
        if(operation>=CpxOperation.StageMetadata && operation<=CpxOperation.AbortTransaction && R(Active)==0) return CpxError.InvalidArgument;
        if(operation==CpxOperation.StageMetadata && (buffer<Page(0)||buffer>Page(5)||((buffer-Page(0))&(P-1))!=0)) return CpxError.InvalidArgument;
        if(operation==CpxOperation.CommitTransaction && R(StagedCount)==0) return CpxError.InvalidArgument;
        if(operation==CpxOperation.ReadMetadata && !PageAddress(argument)) return CpxError.InvalidArgument;
        if(operation==CpxOperation.Mount) { W(Mounted,0); W(Poisoned,0); }
        if(operation>=CpxOperation.WriteInline) { WW(FilePosition,default); W(FileBytes,0); W(FileValue,0); W(TrimCount,0); W(FileBuffer,0); W(DataProgress,0); }
        W(CallDepth,0); W(OperationAt,(uint)operation); W(Phase,0); W(ErrorAt,0); W(Finished,0); W(CancelAt,0); W(IoReady,0); W(NsPublished,0);
        WW(Target,argument); WW(ObjectKey,argument); W(SourcePage,buffer); if(operation==CpxOperation.BeginTransaction) W(StagedLimit,creditLimit); return CpxError.None;
    }
    /// <summary>A persistence barrier without cleanup or allocation. Software
    /// protected mounts may flush device caches but must not prune metadata.</summary>
    public CpxError BeginPersistenceBarrier()
    {
        CpxError error=StartOperation(CpxOperation.Flush);
        if(error==CpxError.None) W(Phase,1);
        return error;
    }
    public CpxIo Request => new() { Kind=(IoKind)R(PendingKind), Sector=RW(PendingSector), Buffer=R(PendingBuffer), Bytes=R(PendingBytes), Token=R(Token) };
    public CpxError CompleteIo(uint token,uint actual,bool success)
    {
        if(R(Waiting)==0||token!=R(Token)) return CpxError.StaleCompletion;
        W(Waiting,0); W(IoReady,1);
        if(!success || ((IoKind)R(PendingKind) is IoKind.Read or IoKind.Write or IoKind.Discard or IoKind.QueryDiscard && actual!=R(PendingBytes))) Fail(CpxError.Io);
        else if(R(CancelAt)!=0) Fail(CpxError.Cancelled);
        else if((CpxOperation)R(OperationAt)==CpxOperation.ManualTrim&&(IoKind)R(PendingKind)==IoKind.Discard&&DiscardEnabled) CountManualRange();
        return Error;
    }
    /// <summary>Only an explicit command-not-supported response disables discard.
    /// Other errors still poison the mount; transport errors are not capability discovery.</summary>
    public CpxError CompleteUnsupportedDiscard(uint token)
    {
        if(R(Waiting)==0||token!=R(Token)) return CpxError.StaleCompletion;
        if((IoKind)R(PendingKind) is not (IoKind.Discard or IoKind.QueryDiscard)) return CpxError.InvalidArgument;
        W(TrimDisabled,1); return CompleteIo(token,R(PendingBytes),true);
    }
    /// <summary>Caller must quiesce I/O and deliver its completion before reuse.</summary>
    public void Cancel() { W(CancelAt,1); if(R(Waiting)==0) Fail(CpxError.Cancelled); }
    private StepResult Fail(CpxError error) { W(ErrorAt,(uint)error); W(Poisoned,1); W(Finished,1); return StepResult.Failed; }
    private StepResult Done() { W(Finished,1); return StepResult.Complete; }
    private StepResult Reject(CpxError error) { W(ErrorAt,(uint)error); W(Finished,1); return StepResult.Failed; }
    private StepResult Issue(IoKind kind,Wide unit,uint buffer,uint bytes,uint nextPhase)
    {
        if(R(Waiting)!=0) return Fail(CpxError.Busy);
        if(!Wide.Left(unit,(int)(R(UnitShift)-R(SectorShift)),out var sector)) return Fail(CpxError.Corrupt);
        if(kind is IoKind.Read or IoKind.Write or IoKind.Discard)
        {
            if((bytes&((1u<<(int)R(SectorShift))-1))!=0||bytes==0||
                !Wide.Add(sector,Wide.From(bytes>>(int)R(SectorShift)),out var end)||Wide.Compare(end,RW(Capacity))>0) return Fail(CpxError.Corrupt);
        }
        if(kind is IoKind.Write or IoKind.Discard && R(ReadOnlyAt)!=0) return Fail(CpxError.ReadOnly);
        uint next=R(NextToken)+1; if(next==0) return Fail(CpxError.ResourceExhausted);
        W(NextToken,next); W(Token,next); W(PendingKind,(uint)kind); WW(PendingSector,sector); W(PendingBuffer,buffer); W(PendingBytes,bytes);
        W(Waiting,1); W(IoReady,0); W(Phase,nextPhase); return StepResult.NeedIo;
    }
    public StepResult Step()
    {
        if(R(Waiting)!=0) return StepResult.NeedIo;
        if(R(ErrorAt)!=0) return StepResult.Failed;
        if(R(Finished)!=0) return StepResult.Complete;
        if(R(CancelAt)!=0) return Fail(CpxError.Cancelled);
        StepResult result=DispatchStep();
        if(result==StepResult.Complete && R(CallDepth)!=0)
        {
            uint depth=R(CallDepth)-1, frame=Arena+9472+depth*8;
            W(CallDepth,depth); W(OperationAt,memory.Read32(frame)); W(Phase,memory.Read32(frame+4)); W(Finished,0);
            return StepResult.Pending;
        }
        // Rejected unpublished operations leave only unreachable journal images.
        // I/O failures remain poisoned and must recover before arena reuse.
        uint owner=R(CallDepth)!=0?memory.Read32(Arena+9472):R(OperationAt);
        if(result==StepResult.Failed && owner>=(uint)CpxOperation.WriteInline && R(Poisoned)==0)
        { W(CallDepth,0); W(Active,0); W(AbortOnly,0); W(StagedCount,0); }
        return result;
    }
    private StepResult DispatchStep()
    {
        switch((CpxOperation)R(OperationAt))
        {
            case CpxOperation.Mount: return MountStep();
            case CpxOperation.Recover: return RecoveryStep();
            case CpxOperation.Flush:
                if(R(Phase)==0&&!IsReadOnly) return Call(CpxOperation.PruneObjects,1);
                return R(Phase)<2?Issue(IoKind.Flush,default,0,0,2):Done();
            case CpxOperation.ReadMetadata:
                if(R(Phase)==0) { int staged=FindStaged(RW(Target)); return Issue(IoKind.Read,staged<0?RW(Target):Image(RW(Generation)+1,(uint)staged),Page(0),P,1); }
                return ValidPage(Page(0),RW(Target))&&ReadableGeneration(Page(0))?Done():Fail(CpxError.Corrupt);
            case CpxOperation.BeginTransaction: case CpxOperation.StageMetadata: case CpxOperation.CommitTransaction: case CpxOperation.AbortTransaction: return TransactionStep();
            case CpxOperation.ReadObject: return ObjectStep();
            case CpxOperation.WriteInline: case CpxOperation.ResizeInline: case CpxOperation.SetProtection: case CpxOperation.SetDate: return FileMutationStep();
            case CpxOperation.Trim: return TrimStep();
            case CpxOperation.ReadTree: return SeekStep();
            case CpxOperation.ReadFile: case CpxOperation.WriteMapped: return FileDataStep();
            case CpxOperation.Lookup: return LookupStep();
            case CpxOperation.TrimFree: return TrimFreeStep();
            case CpxOperation.ManualTrim: return ManualTrimStep();
            case CpxOperation.TreeMutation: return TreeMutationStep();
            case CpxOperation.MetadataAllocation: return MetadataAllocationStep();
            case CpxOperation.ChangeAllocation: return ChangeAllocationStep();
            case CpxOperation.SaveObject: return SaveObjectStep();
            case CpxOperation.Refill: return RefillStep();
            case CpxOperation.Create: case CpxOperation.Delete: case CpxOperation.Rename: return NamespaceStep();
            case CpxOperation.Resize: return ResizeStep();
            case CpxOperation.Reclaim: return ReclaimStep();
            case CpxOperation.PruneTree: return PruneTreeStep();
            case CpxOperation.PruneObjects: return PruneObjectsStep();
            case CpxOperation.PublishMetadata: return R(Phase)==0?PublishMetadata(R(PreparedBuffer),RW(PreparedTarget),1):Done();
            case CpxOperation.SetComment: case CpxOperation.SetOwner: return CommentStep();
            case CpxOperation.PrepareWriteGap: return WriteGapStep();
            case CpxOperation.Format: return FormatStep();
            case CpxOperation.CreateHardLink: return HardLinkStep();
            case CpxOperation.ReadSymlink: return ReadSymlinkStep();
            case CpxOperation.EncodeSymlink: return EncodeSymlinkStep();
            case CpxOperation.SetLabel: return LabelStep();
            default: return Fail(CpxError.Unsupported);
        }
    }
    private StepResult Call(CpxOperation operation,uint nextPhase)
    {
        uint depth=R(CallDepth); if(depth==8) return Fail(CpxError.Corrupt);
        uint frame=Arena+9472+depth*8; memory.Write32(frame,R(OperationAt)); memory.Write32(frame+4,nextPhase);
        W(CallDepth,depth+1); W(OperationAt,(uint)operation); W(Phase,0); return StepResult.Pending;
    }
    private void Copy(uint destination,uint source,uint bytes)
    { for(uint n=0;n<bytes;n++) memory.Write8(destination+n,memory.Read8(source+n)); }
    private StepResult BeginTx(uint credits,uint nextPhase)
    {
        if(!Wide.Add(RW(Generation),Wide.From(1),out _)) return Reject(CpxError.ResourceExhausted);
        W(StagedLimit,credits); return Call(CpxOperation.BeginTransaction,nextPhase);
    }
    private bool PageAddress(Wide unit) => (unit.Low&(Q-1))==0 && Wide.Add(unit,Wide.From(Q),out var end) && Wide.Compare(end,RW(Units))<=0;
    private bool Allocatable(Wide unit)
    {
        if(!PageAddress(unit)||Wide.Compare(unit,Wide.From(2097152u>>(int)R(UnitShift)))<0||Wide.Compare(unit,Backup)>=0) return false;
        if((unit.High!=0||(unit.Low&0xffff0000)!=0)&&(unit.Low&65535)<17*Q) return false;
        Wide group=new(unit.High,unit.Low&0xffff0000); Wide.Subtract(RW(Units),group,out var remaining);
        return remaining.High!=0||remaining.Low>=65536||remaining.Low>=18*Q;
    }
    private uint Crc(uint buffer,uint bytes,bool page)
    {
        uint crc=uint.MaxValue;
        for(uint n=0;n<bytes;n++)
        { crc^=page&&n>=8&&n<12?0u:memory.Read8(buffer+n); for(int bit=0;bit<8;bit++) crc=(crc>>1)^(0x82f63b78u&(0u-(crc&1))); }
        return ~crc;
    }
    private bool ValidPage(uint p,Wide address) => memory.Read32(p)==CpxArena.Magic && Read16(p+6)==4 && memory.Read32(p+8)==Crc(p,P,true) && Wide.Equal(GetWide(p+16),address);
    private bool ReadableGeneration(uint p) => Wide.Compare(GetWide(p+32),RW(Generation)+(R(Active)!=0?1u:0u))<=0;
    private bool Control(uint p,uint type,Wide address) => ValidPage(p,address)&&Read16(p+4)==type&&GetWide(p+24).IsZero;
    private bool IdentityMatches(uint p) => GetWide(p+64).IsZero&&Wide.Equal(GetWide(p+72),RW(Identity));
    private void Clear(uint p,uint count) { for(uint n=0;n<count;n++) memory.Write8(p+n,0); }
    private void Header(uint p,uint type,Wide address,Wide tx)
    { Clear(p,P); memory.Write32(p,CpxArena.Magic); Write16(p+4,type); Write16(p+6,4); PutWide(p+16,address); PutWide(p+32,tx); }
    private void Seal(uint p,uint payload) { Write16(p+52,payload); memory.Write32(p+8,Crc(p,P,true)); }
    private StepResult MountStep()
    {
        uint p=Page(0);
        switch(R(Phase))
        {
            case 0: return Issue(IoKind.Read,Anchor,p,P,1);
            case 1:
                if(!Control(p,1,Anchor)) return Issue(IoKind.Read,Backup,p,P,2);
                if(!AnchorFieldsValid(p)) return Fail(CpxError.Corrupt);
                WW(Identity,GetWide(p+72)); return Issue(IoKind.Read,Backup,p,P,3);
            case 2:
                if(!Control(p,1,Backup)||!AnchorFieldsValid(p)) return Fail(CpxError.Corrupt);
                WW(Identity,GetWide(p+72)); W(OperationAt,(uint)CpxOperation.Recover); W(Phase,0); return StepResult.Pending;
            case 3:
                if(ValidPage(p,Backup)&&(Read16(p+4)!=1||!GetWide(p+24).IsZero||!AnchorFieldsValid(p)||!Wide.Equal(GetWide(p+72),RW(Identity)))) return Fail(CpxError.Corrupt);
                W(OperationAt,(uint)CpxOperation.Recover); W(Phase,0); return StepResult.Pending;
            default: return Fail(CpxError.Corrupt);
        }
    }
    // Discovery has authenticated both possible copies at every legal page
    // size. Enter the ordinary recovery path without re-reading a bad sector.
    public CpxError BeginMountFromDiscoveredAnchor(Wide identity)
    {
        if(identity.IsZero) return CpxError.InvalidArgument;
        CpxError error=BeginOperation(CpxOperation.Mount);
        if(error==CpxError.None) { WW(Identity,identity); W(OperationAt,(uint)CpxOperation.Recover); }
        return error;
    }
    private bool AnchorFieldsValid(uint p)=>AnchorFieldsValid(p,1u<<(int)R(UnitShift),P,RW(Units),1u<<(int)R(SectorShift));
    private bool AnchorFieldsValid(uint p,uint unit,uint page,Wide units,uint sector)
    {
        int shift=CpxArena.Shift(unit); uint anchor=65536u>>shift,q=page>>shift;
        return memory.Read32(p+80)==unit&&memory.Read32(p+84)==page&&Wide.Equal(GetWide(p+88),units)&&
            Wide.Equal(GetWide(p+96),Wide.From(1))&&memory.Read32(p+104)==sector&&memory.Read32(p+108)==1&&
            GetWide(p+64).IsZero&&!GetWide(p+72).IsZero&&Wide.Equal(GetWide(p+112),Wide.From(anchor+8*q))&&Wide.Equal(GetWide(p+120),Wide.From(anchor+3*q));
    }
    // One CRC per candidate. A torn copy can be skipped. A CRC-valid CPX4
    // header with inconsistent geometry cannot authorize a different volume.
    public CpxError InspectAnchor(uint buffer,uint page,uint sector,Wide capacity,Wide location,out uint unit,out Wide identity)
    {
        unit=0; identity=default;
        if(!CpxArena.ValidUnit(page)||page<1024||!CpxArena.ValidUnit(sector)||sector>page) return CpxError.InvalidArgument;
        if(memory.Read32(buffer)!=CpxArena.Magic||memory.Read32(buffer+8)!=Crc(buffer,page,true)) return CpxError.NotFound;
        uint candidate=memory.Read32(buffer+80);
        if(!CpxArena.ValidUnit(candidate)||candidate<sector||(candidate<1024?1024:candidate)!=page||
            Read16(buffer+4)!=1||Read16(buffer+6)!=4||Read16(buffer+52)!=64||!GetWide(buffer+24).IsZero) return CpxError.Corrupt;
        int shift=CpxArena.Shift(candidate)-CpxArena.Shift(sector);
        if((capacity.Low&((1u<<shift)-1))!=0||!Wide.Equal(GetWide(buffer+16),Wide.Right(location,shift))||
            !AnchorFieldsValid(buffer,candidate,page,Wide.Right(capacity,shift),sector)) return CpxError.Corrupt;
        unit=candidate; identity=GetWide(buffer+72); return CpxError.None;
    }
}
