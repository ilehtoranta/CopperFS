namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    public uint TransferredBytes => R(FileValue);
    /// <summary>The caller owns and pins this address until completion or quiesced
    /// cancellation, exactly as for a DOS read/write packet. No managed view is retained.</summary>
    public CpxError BeginFileRead(Wide id,Wide position,uint destination,uint bytes)
        => BeginFileData(CpxOperation.ReadFile,id,position,destination,bytes);
    /// <summary>Writes mappings, promotes extent lists and splits unwritten runs.
    /// Each retired allocation-unit chunk advances committed prefix progress.</summary>
    public CpxError BeginMappedWrite(Wide id,Wide position,uint source,uint bytes)
        => BeginFileData(CpxOperation.WriteMapped,id,position,source,bytes);
    private CpxError BeginFileData(CpxOperation op,Wide id,Wide position,uint buffer,uint bytes)
    {
        if((buffer==0&&bytes!=0)||buffer>uint.MaxValue-bytes||!Wide.Add(position,Wide.From(bytes),out _)) return CpxError.InvalidArgument;
        CpxError error=StartOperation(op,id); if(error!=CpxError.None) return error;
        WW(FilePosition,position); W(FileBuffer,buffer); W(FileBytes,bytes); W(WriteGapPrepared,0); return CpxError.None;
    }
    private StepResult FileDataStep()
    {
        uint phase=R(Phase),record=ObjectRecord,unit=1u<<(int)R(UnitShift); bool write=(CpxOperation)R(OperationAt)==CpxOperation.WriteMapped;
        if(phase<100)
        {
            StepResult lookup=ObjectStep(); if(lookup!=StepResult.Complete) return lookup;
            W(Finished,0); if(R(SourcePage)==0) return Reject(CpxError.NotFound);
            W(Phase,100); return StepResult.Pending;
        }
        if(phase>=200&&phase<300)
        {
            W(Phase,phase-200); StepResult seek=SeekStep(); W(Phase,R(Phase)+200);
            if(seek==StepResult.Complete) { W(Finished,0); W(Phase,103); return StepResult.Pending; }
            return seek;
        }
        if(phase>=300)
        {
            W(Phase,phase-300); StepResult commit=CommitStep(); W(Phase,R(Phase)+300);
            if(commit==StepResult.Complete)
            {
                W(Finished,0); if(R(AllocationMode)!=2) W(FileValue,R(FileValue)+R(DataTake));
                W(Phase,0); return StepResult.Pending;
            }
            return commit;
        }
        if(phase>=150) return AllocateWriteStep();
        switch(phase)
        {
            case 100:
                if(memory.Read8(record+84)!=1) return Reject(CpxError.WrongType);
                if((memory.Read32(record+72)&(write?4u:8u))!=0) return Reject(CpxError.Protected);
                if(write&&R(FileBytes)==0) return Done();
                uint storage=memory.Read8(record+85); Wide size=GetWide(record+8);
                if(storage>2) return Fail(CpxError.Corrupt);
                if(storage==0)
                {
                    if(size.High!=0||size.Low>160||Read16(record+88)!=size.Low||!GetWide(record+16).IsZero||!GetWide(record+24).IsZero) return Fail(CpxError.Corrupt);
                    if(write)
                    {
                        Wide end=RW(FilePosition)+R(FileBytes);
                        if(end.High==0&&end.Low<=160)
                        {
                            for(uint n=0;n<R(FileBytes);n++) memory.Write8(FileInput+n,memory.Read8(R(FileBuffer)+n));
                            W(OperationAt,(uint)CpxOperation.WriteInline); W(Phase,100); return StepResult.Pending;
                        }
                        if(size.Low!=0)
                        {
                            for(uint n=0;n<160;n++) memory.Write8(FileInput+n,memory.Read8(record+96+n));
                            WW(DataLogical,default); W(DataIntra,0); W(DataTake,0); W(AllocationMode,2); W(Phase,150); return StepResult.Pending;
                        }
                        Clear(record+96,160); memory.Write8(record+85,1); Write16(record+88,0);
                        storage=1;
                    }
                }
                if(R(FileBytes)==0) return Done();
                if(write&&R(WriteGapPrepared)==0&&storage!=0&&Wide.Compare(RW(FilePosition),size)>0&&(size.Low&(unit-1))!=0)
                {
                    WW(OldFileSize,size); Copy(ResizeObject,record,256);
                    return Call(CpxOperation.PrepareWriteGap,109);
                }
                if(Wide.Compare(RW(FilePosition),size)>=0&&!write) return Done();
                if(!write)
                {
                    Wide.Subtract(size,RW(FilePosition),out var remaining);
                    if(remaining.High==0&&R(FileBytes)>remaining.Low) W(FileBytes,remaining.Low);
                }
                WW(SavedLeaf,RW(TreeAddress)); W(SavedSlot,R(TreeSlot));
                if(storage==0)
                {
                    for(uint n=0;n<R(FileBytes);n++) memory.Write8(R(FileBuffer)+n,memory.Read8(record+96+R(FilePosition+4)+n));
                    W(FileValue,R(FileBytes)); return Done();
                }
                W(Phase,101); return StepResult.Pending;
            case 101:
                if(R(FileValue)==R(FileBytes)) return Done();
                Wide position=RW(FilePosition)+R(FileValue); WW(DataLogical,Wide.Right(position,(int)R(UnitShift)));
                W(DataIntra,position.Low&(unit-1)); uint take=unit-R(DataIntra);
                if(take>R(FileBytes)-R(FileValue)) take=R(FileBytes)-R(FileValue); W(DataTake,take);
                W(SeekFound,0); W(DataFlags,0); WW(DataPhysical,default);
                if(memory.Read8(record+85)==2)
                {
                    if(Read16(record+90)!=0) return Fail(CpxError.Corrupt);
                    // A fully pruned extent tree is a valid empty/sparse file
                    // in reference-created images; the first insertion recreates it.
                    if(GetWide(record+24).IsZero) { W(Phase,103); return StepResult.Pending; }
                    StartSeek(CpxTreeKind.Extent,GetWide(record+24),RW(ObjectKey),RW(DataLogical),default,CpxSeek.Floor);
                    W(Phase,200); return StepResult.Pending;
                }
                uint count=Read16(record+90); if(count>6||!GetWide(record+24).IsZero) return Fail(CpxError.Corrupt);
                for(uint n=0;n<count;n++)
                {
                    uint extent=record+96+n*24;
                    if(n!=0&&Wide.Compare(GetWide(extent-24),GetWide(extent))>=0) return Fail(CpxError.Corrupt);
                    if(Wide.Compare(GetWide(extent),RW(DataLogical))<=0)
                    { for(uint b=0;b<24;b++) memory.Write8(TreeRecord+b,memory.Read8(extent+b)); W(SeekFound,1); }
                }
                W(Phase,103); return StepResult.Pending;
            case 103:
                if(write) { Clear(WriteOriginal,24); if(R(SeekFound)!=0) Copy(WriteOriginal,TreeRecord,24); }
                if(R(SeekFound)!=0)
                {
                    Wide logical=GetWide(TreeRecord),physical=GetWide(TreeRecord+8); uint length=memory.Read32(TreeRecord+16),flags=memory.Read32(TreeRecord+20);
                    if(length==0||length>65536||flags>1||!Wide.Add(physical,Wide.From(length-1),out var last)||
                        !DataUnit(physical)||!DataUnit(last)||physical.High!=last.High||(physical.Low>>16)!=(last.Low>>16)||!Wide.Add(logical,Wide.From(length),out _)) return Fail(CpxError.Corrupt);
                    if(!Wide.Subtract(RW(DataLogical),logical,out var delta)) return Fail(CpxError.Corrupt);
                    if(delta.High==0&&delta.Low<length) { WW(DataPhysical,physical+delta.Low); W(DataFlags,flags==0?1u:2u); }
                }
                if(write&&R(DataFlags)==2) { W(AllocationMode,3); WW(NewPhysical,RW(DataPhysical)); W(Phase,150); return StepResult.Pending; }
                if(write&&R(DataFlags)==0) { W(AllocationMode,1); W(Phase,150); return StepResult.Pending; }
                if(R(DataFlags)==1) return Issue(IoKind.Read,RW(DataPhysical),Page(4),unit,104);
                Clear(Page(4),unit); W(Phase,104); return StepResult.Pending;
            case 104:
                uint source=R(FileBuffer)+R(FileValue),data=Page(4)+R(DataIntra);
                if(write)
                {
                    for(uint n=0;n<R(DataTake);n++) memory.Write8(data+n,memory.Read8(source+n));
                    Wide end=RW(FilePosition)+R(FileValue)+R(DataTake);
                    if(Wide.Compare(end,GetWide(record+8))>0)
                    {
                        PutWide(record+8,end); WW(NewPhysical,RW(DataPhysical)); W(AllocationMode,0); W(Phase,160); return StepResult.Pending;
                    }
                    return Issue(IoKind.Write,RW(DataPhysical),Page(4),unit,105);
                }
                for(uint n=0;n<R(DataTake);n++) memory.Write8(source+n,memory.Read8(data+n));
                W(FileValue,R(FileValue)+R(DataTake)); W(Phase,101); return StepResult.Pending;
            case 105: return Issue(IoKind.Flush,default,0,0,106);
            case 106: W(FileValue,R(FileValue)+R(DataTake)); W(Phase,101); return StepResult.Pending;
            case 109: W(WriteGapPrepared,1); W(Phase,0); return StepResult.Pending;
            default: return Fail(CpxError.Corrupt);
        }
    }
}
