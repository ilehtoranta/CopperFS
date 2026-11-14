namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // The input is copied before returning to the dispatcher. ObjectRecord occupies
    // the preceding 256 bytes. Neither buffer aliases any of the six page buffers.
    private uint FileInput => ObjectRecord+256;
    public uint CommittedBytes => R(FileValue);
    public CpxError BeginInlineWrite(Wide id,Wide position,uint source,uint bytes)
    {
        if(bytes>160||!Wide.Add(position,Wide.From(bytes),out var end)||end.High!=0||end.Low>160||source>uint.MaxValue-bytes) return CpxError.InvalidArgument;
        CpxError error=StartOperation(CpxOperation.WriteInline,id); if(error!=CpxError.None) return error;
        WW(FilePosition,position); W(FileBytes,bytes);
        for(uint n=0;n<bytes;n++) memory.Write8(FileInput+n,memory.Read8(source+n));
        return CpxError.None;
    }
    public CpxError BeginInlineResize(Wide id,uint size)
    {
        if(size>160) return CpxError.InvalidArgument;
        CpxError error=StartOperation(CpxOperation.ResizeInline,id);
        if(error==CpxError.None) W(FileBytes,size); return error;
    }
    public CpxError BeginProtection(Wide id,uint protection)
    {
        CpxError error=StartOperation(CpxOperation.SetProtection,id);
        if(error==CpxError.None) memory.Write32(FileInput,protection); return error;
    }
    public CpxError BeginDate(Wide id,uint days,uint minutes,uint ticks)
    {
        if(minutes>=1440||ticks>=3000) return CpxError.InvalidArgument;
        CpxError error=StartOperation(CpxOperation.SetDate,id);
        if(error==CpxError.None) { memory.Write32(FileInput,days); Write16(FileInput+4,minutes); Write16(FileInput+6,ticks); }
        return error;
    }
    private StepResult FileMutationStep()
    {
        uint phase=R(Phase),p=Page(0),record=ObjectRecord; CpxOperation op=(CpxOperation)R(OperationAt);
        if(phase<100)
        {
            StepResult lookup=ObjectStep();
            if(lookup!=StepResult.Complete) return lookup;
            W(Finished,0);
            if(R(SourcePage)==0) return Reject(CpxError.NotFound);
            W(Phase,100); return StepResult.Pending;
        }
        if(phase>=200)
        {
            W(Phase,phase-200); StepResult commit=CommitStep(); W(Phase,R(Phase)+200);
            // Advancing a file handle is permitted only after the final retirement barrier.
            if(commit==StepResult.Complete) W(FileValue,op==CpxOperation.WriteInline?R(FileBytes):0);
            return commit;
        }
        switch(phase)
        {
            case 100:
                if(op is CpxOperation.WriteInline or CpxOperation.ResizeInline)
                {
                    if(memory.Read8(record+84)!=1) return Reject(CpxError.WrongType);
                    if((memory.Read32(record+72)&4)!=0) return Reject(CpxError.Protected);
                    if(memory.Read8(record+85)!=0) return Reject(CpxError.Unsupported);
                    Wide size=GetWide(record+8);
                    if(size.High!=0||size.Low>160||Read16(record+88)!=size.Low||!GetWide(record+16).IsZero||!GetWide(record+24).IsZero) return Fail(CpxError.Corrupt);
                    if(op==CpxOperation.WriteInline&&R(FileBytes)==0) return Done();
                    uint length=op==CpxOperation.ResizeInline?R(FileBytes):R(FilePosition+4)+R(FileBytes);
                    if(op==CpxOperation.WriteInline&&length<size.Low) length=size.Low;
                    // Clear both a newly exposed hole and a truncated tail. A later grow
                    // must never reveal bytes from the previous incarnation of the file.
                    if(length>size.Low) Clear(record+96+size.Low,length-size.Low);
                    else if(length<size.Low) Clear(record+96+length,160-length);
                    if(op==CpxOperation.WriteInline)
                        for(uint n=0;n<R(FileBytes);n++) memory.Write8(record+96+R(FilePosition+4)+n,memory.Read8(FileInput+n));
                    PutWide(record+8,Wide.From(length)); Write16(record+88,length);
                }
                else if(op==CpxOperation.SetProtection) memory.Write32(record+72,memory.Read32(FileInput));
                else for(uint n=0;n<8;n++) memory.Write8(record+56+n,memory.Read8(FileInput+n));
                // Admission precedes invalidation and every durable write.
                if(!Wide.Add(RW(Generation),Wide.From(1),out _)) return Reject(CpxError.ResourceExhausted);
                W(StagedCount,0); W(StagedLimit,6); W(Active,1); Clear(Index,2048); Clear(Page(5),P);
                return Issue(IoKind.Write,Commit(RW(Generation)+1),Page(5),P,101);
            case 101: return Issue(IoKind.Flush,default,0,0,102);
            case 102:
                uint dest=p+64+(R(TreeSlot)-1)*256;
                for(uint n=0;n<256;n++) memory.Write8(dest+n,memory.Read8(record+n));
                return StagePage(p,RW(TreeAddress),200);
            default: return Fail(CpxError.Corrupt);
        }
    }
}
