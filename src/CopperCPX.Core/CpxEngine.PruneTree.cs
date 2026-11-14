namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    private const uint PruneRoot=13900,PruneDepth=13908,PruneLevel=13912,PruneScan=13916,PruneChanged=13920,PruneSlot=13924,PruneScanOnly=13928,PruneObjectRoot=14600;
    private uint PrunePath=>Arena+14000;
    private StepResult PruneTree(CpxTreeKind kind,Wide root,Wide owner,uint next,bool scanOnly=false)
    {
        StartSeek(kind,root,owner,default,default,CpxSeek.Exact); WW(PruneRoot,root); W(PruneChanged,0); W(PruneDepth,0); W(PruneScanOnly,scanOnly?1u:0u);
        return Call(CpxOperation.PruneTree,next);
    }
    private StepResult PruneTreeStep()
    {
        uint p=Page(1),frame=PrunePath+R(PruneDepth)*16;
        switch(R(Phase))
        {
            case 0:
                if(RW(PruneRoot).IsZero) return Done();
                PutWide(frame,RW(PruneRoot)); memory.Write32(frame+12,uint.MaxValue); W(Phase,1); return StepResult.Pending;
            case 1:
                if(R(PruneDepth)>=8||!Allocatable(GetWide(frame))) return Fail(CpxError.Corrupt);
                return ReadStaged(GetWide(frame),p,2);
            case 2:
                uint level=Read16(p+50),count=Read16(p+48),width=level==0?SeekLeafBytes:SeekKeyBytes+8,expected=memory.Read32(frame+12);
                if(!ValidPage(p,GetWide(frame))||!ReadableGeneration(p)||Read16(p+4)!=SeekType(level)||!Wide.Equal(GetWide(p+24),RW(SeekOwner))||
                    !GetWide(p+40).IsZero||level>=8||(expected!=uint.MaxValue&&level!=expected)||count>(P-64)/width||
                    (level!=0&&count==0)||Read16(p+52)!=count*width) return Fail(CpxError.Corrupt);
                W(PruneLevel,level); W(PruneScan,0); memory.Write32(frame+12,level); memory.Write32(frame+8,0); W(Phase,3); return StepResult.Pending;
            case 3:
                uint lev=R(PruneLevel),cnt=Read16(p+48),sz=lev==0?SeekLeafBytes:SeekKeyBytes+8,scan=R(PruneScan),end=scan+64; if(end>cnt) end=cnt;
                for(;scan<end;scan++)
                {
                    uint r=p+64+scan*sz;
                    if(scan!=0&&SeekCompare(r-sz,GetWide(r),SeekKeyBytes==16?GetWide(r+8):default)>=0) return Fail(CpxError.Corrupt);
                    if(lev!=0&&!Allocatable(GetWide(r+SeekKeyBytes))) return Fail(CpxError.Corrupt);
                }
                W(PruneScan,scan); if(scan<cnt) return StepResult.Pending;
                if(lev!=0)
                {
                    PutWide(frame+16,GetWide(p+64+SeekKeyBytes)); memory.Write32(frame+28,lev-1); W(PruneDepth,R(PruneDepth)+1); W(Phase,1); return StepResult.Pending;
                }
                if(cnt==0) { W(PruneChanged,1); return R(PruneScanOnly)!=0?Done():ReleaseMetadata(GetWide(frame),6); }
                W(Phase,4); return StepResult.Pending;
            case 4:
                if(R(PruneDepth)==0) return Done();
                W(PruneDepth,R(PruneDepth)-1); return ReadStaged(GetWide(PrunePath+R(PruneDepth)*16),p,5);
            case 5:
                if(!ValidPage(p,GetWide(frame))||!ReadableGeneration(p)) return Fail(CpxError.Corrupt);
                uint next=memory.Read32(frame+8)+1;
                if(next>=Read16(p+48)) { W(Phase,4); return StepResult.Pending; }
                memory.Write32(frame+8,next); PutWide(frame+16,GetWide(p+64+next*(SeekKeyBytes+8)+SeekKeyBytes)); memory.Write32(frame+28,memory.Read32(frame+12)-1);
                W(PruneDepth,R(PruneDepth)+1); W(Phase,1); return StepResult.Pending;
            case 6:
                if(R(PruneDepth)==0) { WW(PruneRoot,default); return Done(); }
                W(PruneDepth,R(PruneDepth)-1); return ReadStaged(GetWide(PrunePath+R(PruneDepth)*16),p,7);
            case 7:
                if(!ValidPage(p,GetWide(frame))||!ReadableGeneration(p)||Read16(p+48)==0) return Fail(CpxError.Corrupt);
                if(Read16(p+48)==1) return ReleaseMetadata(GetWide(frame),6);
                W(PruneSlot,memory.Read32(frame+8)); W(PruneScan,R(PruneSlot)); Copy(MutationRecord,p+64,SeekKeyBytes); W(Phase,8); return StepResult.Pending;
            case 8:
                uint cursor=R(PruneScan),limit=cursor+64,total=Read16(p+48),size=SeekKeyBytes+8; if(limit>total-1) limit=total-1;
                for(;cursor<limit;cursor++) Copy(p+64+cursor*size,p+64+(cursor+1)*size,size);
                W(PruneScan,cursor); if(cursor<total-1) return StepResult.Pending;
                Clear(p+64+(total-1)*size,size); if(R(PruneSlot)==0) Copy(p+64,MutationRecord,SeekKeyBytes);
                Write16(p+48,total-1); Write16(p+52,(total-1)*size); W(Phase,9); return StepResult.Pending;
            case 9: return StagePage(p,GetWide(frame),10);
            case 10: return Done();
            default: return Fail(CpxError.Corrupt);
        }
    }
    // Empty nodes remain reachable if power fails after the last orphan is
    // dequeued. Mount/flush also run this scan, so these pages are recoverable
    // without another durable queue record or any unbounded in-memory list.
    private StepResult PruneObjectsStep()
    {
        uint p=Page(3);
        switch(R(Phase))
        {
            case 0: return ReadStaged(Anchor+3*Q,p,1);
            case 1:
                if(!Control(p,3,Anchor+3*Q)||!ReadableGeneration(p)) return Fail(CpxError.Corrupt);
                WW(PruneObjectRoot,GetWide(p+80)); return PruneTree(CpxTreeKind.Object,RW(PruneObjectRoot),default,2,true);
            case 2:
                if(R(PruneChanged)==0) return Done();
                return BeginTx(27,3);
            case 3: return PruneTree(CpxTreeKind.Object,RW(PruneObjectRoot),default,4);
            case 4:
                if(R(PruneChanged)==0||RW(PruneRoot).IsZero) return Fail(CpxError.Corrupt);
                WW(PruneObjectRoot,RW(PruneRoot)); return ReadStaged(Anchor+3*Q,p,5);
            case 5:
                if(!Control(p,3,Anchor+3*Q)||!ReadableGeneration(p)) return Fail(CpxError.Corrupt);
                PutWide(p+80,RW(PruneObjectRoot)); return StagePage(p,Anchor+3*Q,6);
            case 6: return Call(CpxOperation.CommitTransaction,0);
            default: return Fail(CpxError.Corrupt);
        }
    }
}
