namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    public uint ReadBuffer => Page(0);
    public uint TransactionImageCount => R(StagedCount);
    private int FindStaged(Wide at)
    {
        if(R(Active)==0) return -1;
        for(uint n=0;n<R(StagedCount);n++) if(Wide.Equal(GetWide(Index+n*32),at)) return (int)n;
        return -1;
    }
    private StepResult TransactionStep()
    {
        uint phase=R(Phase),p=Page(5); Wide tx=RW(Generation)+1;
        switch((CpxOperation)R(OperationAt))
        {
            case CpxOperation.BeginTransaction:
                if(phase==0)
                {
                    W(Active,1); W(StagedCount,0); W(MetadataTaken,0); W(MetadataReleased,0);
                    W(MetadataLimit,R(StagedLimit)==39?20u:R(StagedLimit) is 22 or 30?10u:R(StagedLimit)==42?18u:R(StagedLimit)==6?1u:0u);
                    Clear(Index,2048); Clear(p,P); return Issue(IoKind.Write,Commit(tx),p,P,1);
                }
                if(phase==1) return Issue(IoKind.Flush,default,0,0,2);
                return Done();
            case CpxOperation.AbortTransaction: W(Active,0); W(AbortOnly,0); W(StagedCount,0); Clear(Index,2048); return Done();
            case CpxOperation.StageMetadata:
                if(phase!=0) return Done();
                return StagePage(R(SourcePage),RW(Target),1);
        }
        return CommitStep();
    }
    private StepResult StagePage(uint p,Wide target,uint nextPhase)
    {
        // Reading/validating a page and calculating its new checksum belong to
        // separate processing phases. The caller-owned page stays pinned by the
        // single active operation until this child continuation completes.
        W(PreparedBuffer,p); WW(PreparedTarget,target); return Call(CpxOperation.PublishMetadata,nextPhase);
    }
    private StepResult PublishMetadata(uint p,Wide target,uint nextPhase)
    {
                Wide tx=RW(Generation)+1; uint type=Read16(p+4); Wide owner=GetWide(p+24);
                if(memory.Read32(p)!=CpxArena.Magic||Read16(p+6)!=4||!TargetValid(target,type,owner)||!PayloadValid(p)) return Fail(CpxError.Corrupt);
                int slot=FindStaged(target);
                if(slot<0)
                {
                    if(R(StagedCount)==R(StagedLimit)) { W(AbortOnly,1); W(ErrorAt,(uint)CpxError.TransactionLimit); W(Finished,1); return StepResult.Failed; }
                    slot=(int)R(StagedCount); W(StagedCount,(uint)slot+1);
                }
                PutWide(p+16,target); PutWide(p+32,tx); memory.Write32(p+8,Crc(p,P,true));
                uint descriptor=Index+(uint)slot*32; PutWide(descriptor,target); PutWide(descriptor+8,owner);
                memory.Write32(descriptor+16,(uint)slot); memory.Write32(descriptor+20,memory.Read32(p+8)); Write16(descriptor+24,type);
                return Issue(IoKind.Write,Image(tx,(uint)slot),p,P,nextPhase);
    }
    private StepResult CommitStep()
    {
        uint phase=R(Phase),p=Page(5); Wide tx=RW(Generation)+1;
        switch(phase)
        {
            case 0:
                W(Digest,Crc(Index,R(StagedCount)*32,false)); W(Phase,1); return StepResult.Pending;
            case 1:
                MakePublication(p,8,Journal(tx),tx); return Issue(IoKind.Write,Journal(tx),p,P,2);
            case 2: W(Cursor,0); W(Phase,3); return StepResult.Pending;
            case 3:
                uint page=R(Cursor),first=page*((P-64)>>5),take=first<R(StagedCount)?R(StagedCount)-first:0;
                if(take>((P-64)>>5)) take=(P-64)>>5;
                Header(p,9,Descriptor(tx,page),tx); Write16(p+48,take);
                for(uint n=0;n<take*32;n++) memory.Write8(p+64+n,memory.Read8(Index+first*32+n));
                Seal(p,take*32); return Issue(IoKind.Write,Descriptor(tx,page),p,P,4);
            case 4:
                if(R(Cursor)<3) { W(Cursor,R(Cursor)+1); W(Phase,3); return StepResult.Pending; }
                return Issue(IoKind.Flush,default,0,0,5);
            case 5: MakePublication(p,10,Commit(tx),tx); return Issue(IoKind.Write,Commit(tx),p,P,6);
            case 6: return Issue(IoKind.Flush,default,0,0,7);
            case 7: W(Cursor,0); W(Phase,8); return StepResult.Pending;
            case 8: return Issue(IoKind.Read,Image(tx,R(Cursor)),p,P,9);
            case 9:
                if(!ImageValid(p,tx,R(Cursor))) return Fail(CpxError.Corrupt);
                return Issue(IoKind.Write,GetWide(Index+R(Cursor)*32),p,P,10);
            case 10:
                if(R(Cursor)+1<R(StagedCount)) { W(Cursor,R(Cursor)+1); W(Phase,8); return StepResult.Pending; }
                return Issue(IoKind.Flush,default,0,0,11);
            case 11:
                // The visible generation advances only after both checkpoint barriers.
                Header(p,2,Anchor+Q,tx); PutWide(p+72,RW(Identity)); PutWide(p+80,tx); Seal(p,24);
                return Issue(IoKind.Write,Anchor+Q,p,P,12);
            case 12: return Issue(IoKind.Flush,default,0,0,13);
            case 13:
                Header(p,2,Anchor+2*Q,tx); PutWide(p+72,RW(Identity)); PutWide(p+80,tx); Seal(p,24);
                return Issue(IoKind.Write,Anchor+2*Q,p,P,14);
            case 14: return Issue(IoKind.Flush,default,0,0,15);
            case 15: WW(Generation,tx); W(Active,0); W(StagedCount,0); Clear(Index,2048); return Done();
            default: return Fail(CpxError.Corrupt);
        }
    }
    private void MakePublication(uint p,uint type,Wide address,Wide tx)
    {
        Header(p,type,address,tx); PutWide(p+72,RW(Identity)); PutWide(p+80,tx); PutWide(p+88,RW(Generation));
        memory.Write32(p+96,R(StagedCount)); memory.Write32(p+100,R(Digest)); Seal(p,40);
    }
}
