namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    private const uint MutationRoot=9600, MutationMode=9608, MutationDepth=9612, MutationSlot=9616,
        MutationCount=9620, MutationLevel=9624, MutationScan=9628, MutationSibling=9632, MutationLeft=9640, MutationCheckDepth=9644,
        SaveSource=10080, SaveRoot=10088;
    private uint MutationRecord => Arena+9728;
    private uint SaveRecord => Arena+10112;
    private uint MutationWidth => R(MutationLevel)==0?SeekLeafBytes:SeekKeyBytes+8;
    // mode: insert=0, replace=1, delete=2. Input is copied before yielding.
    private StepResult MutateTree(CpxTreeKind kind,Wide root,Wide owner,uint record,uint mode,uint next)
    {
        WW(MutationRoot,root); W(MutationMode,mode); Copy(MutationRecord,record,kind==CpxTreeKind.Object?256u:kind==CpxTreeKind.Directory?128u:24u);
        StartSeek(kind,root,owner,GetWide(record),kind==CpxTreeKind.Directory?GetWide(record+8):default,CpxSeek.Exact);
        return Call(CpxOperation.TreeMutation,next);
    }
    private void TreeHeader(uint page,Wide address,uint count,uint level)
    {
        Header(page,SeekType(level),address,RW(Generation)+1); PutWide(page+24,RW(SeekOwner));
        Write16(page+48,count); Write16(page+50,level); Write16(page+52,count*(level==0?SeekLeafBytes:SeekKeyBytes+8));
    }
    private StepResult TreeMutationStep()
    {
        uint p=Page(1),source=Page(0),right=Page(2),width=MutationWidth,count=R(MutationCount),slot=R(MutationSlot);
        switch(R(Phase))
        {
            case 0:
                if(RW(MutationRoot).IsZero)
                {
                    if(R(MutationMode)!=0) return Fail(CpxError.Corrupt);
                    return TakeMetadata(20);
                }
                return Call(CpxOperation.ReadTree,1);
            case 1:
                if(R(MutationMode)==0&&TreeFound) return Reject(CpxError.AlreadyExists);
                if(R(MutationMode)!=0&&!TreeFound) return Fail(CpxError.Corrupt);
                W(MutationDepth,R(SeekDepth)); W(MutationLevel,0); W(MutationCount,Read16(p+48)); W(MutationScan,0); W(MutationSlot,0);
                if(R(MutationMode)!=0) { W(MutationSlot,R(SeekSlot)); W(Phase,3); }
                else if(R(SeekDepth)==7&&Read16(p+48)==(P-64)/SeekLeafBytes) { W(MutationCheckDepth,6); W(Phase,24); }
                else W(Phase,2);
                return StepResult.Pending;
            case 2:
                uint scan=R(MutationScan),end=scan+64; if(end>count) end=count;
                for(;scan<end;scan++)
                    if(SeekCompare(p+64+scan*width,GetWide(MutationRecord),SeekKeyBytes==16?GetWide(MutationRecord+8):default)<0) W(MutationSlot,scan+1);
                W(MutationScan,scan); if(scan==count) W(Phase,3); return StepResult.Pending;
            case 3:
                if(R(MutationMode)==1)
                { Copy(p+64+slot*width,MutationRecord,width); return StagePage(p,GetWide(SeekPath+R(MutationDepth)*16),19); }
                if(R(MutationMode)==2) { W(MutationScan,slot); W(Phase,4); return StepResult.Pending; }
                Copy(source,p,P);
                if(count<(P-64)/width) { W(MutationScan,count); W(Phase,5); return StepResult.Pending; }
                if(R(MutationDepth)==0&&R(MutationLevel)==7) return Reject(CpxError.TreeTooDeep);
                return TakeMetadata(6);
            case 4:
                uint delete=R(MutationScan),limit=delete+64; if(limit>count-1) limit=count-1;
                for(;delete<limit;delete++) Copy(p+64+delete*width,p+64+(delete+1)*width,width);
                W(MutationScan,delete); if(delete<count-1) return StepResult.Pending;
                Clear(p+64+(count-1)*width,width); Write16(p+48,count-1); Write16(p+52,(count-1)*width);
                W(Phase,18); return StepResult.Pending;
            case 5:
                uint move=R(MutationScan),stop=move>64?move-64:0; if(stop<slot) stop=slot;
                for(;move>stop;move--) Copy(p+64+move*width,source+64+(move-1)*width,width);
                W(MutationScan,move); if(move>slot) return StepResult.Pending;
                Copy(p+64+slot*width,MutationRecord,width); Write16(p+48,count+1); Write16(p+52,(count+1)*width);
                W(Phase,18); return StepResult.Pending;
            case 6:
                WW(MutationSibling,RW(MetadataResult)); W(MutationLeft,(count+1)>>1); W(MutationScan,0);
                TreeHeader(p,GetWide(SeekPath+R(MutationDepth)*16),R(MutationLeft),R(MutationLevel));
                TreeHeader(right,RW(MutationSibling),count+1-R(MutationLeft),R(MutationLevel)); W(Phase,7); return StepResult.Pending;
            case 7:
                uint item=R(MutationScan),until=item+64; if(until>count+1) until=count+1;
                for(;item<until;item++)
                {
                    uint from=item==slot?MutationRecord:source+64+(item<slot?item:item-1)*width;
                    uint to=item<R(MutationLeft)?p+64+item*width:right+64+(item-R(MutationLeft))*width;
                    Copy(to,from,width);
                }
                W(MutationScan,item); if(item<count+1) return StepResult.Pending;
                Copy(MutationRecord,right+64,SeekKeyBytes); PutWide(MutationRecord+SeekKeyBytes,RW(MutationSibling));
                W(Phase,8); return StepResult.Pending;
            case 8: return StagePage(p,GetWide(SeekPath+R(MutationDepth)*16),9);
            case 9: return StagePage(right,RW(MutationSibling),10);
            case 10:
                if(R(MutationDepth)==0) return TakeMetadata(12);
                W(MutationDepth,R(MutationDepth)-1); return ReadStaged(GetWide(SeekPath+R(MutationDepth)*16),p,11);
            case 11:
                uint frame=SeekPath+R(MutationDepth)*16;
                if(!ValidPage(p,GetWide(frame))||!ReadableGeneration(p)||Read16(p+50)!=memory.Read32(frame+12)) return Fail(CpxError.Corrupt);
                W(MutationLevel,Read16(p+50)); W(MutationCount,Read16(p+48)); W(MutationSlot,memory.Read32(frame+8)+1); W(Phase,3); return StepResult.Pending;
            case 12:
                Wide root=RW(MetadataResult); TreeHeader(p,root,2,R(MutationLevel)+1);
                PutWide(p+64+SeekKeyBytes,RW(MutationRoot)); Copy(p+64+SeekKeyBytes+8,MutationRecord,SeekKeyBytes+8);
                WW(MutationRoot,root); return StagePage(p,root,19);
            case 18: return StagePage(p,GetWide(SeekPath+R(MutationDepth)*16),19);
            case 19: return Done();
            case 20:
                WW(MutationRoot,RW(MetadataResult)); TreeHeader(p,RW(MutationRoot),1,0); Copy(p+64,MutationRecord,SeekLeafBytes);
                return StagePage(p,RW(MutationRoot),19);
            case 24: return ReadStaged(GetWide(SeekPath+R(MutationCheckDepth)*16),right,25);
            case 25:
                uint checkedFrame=SeekPath+R(MutationCheckDepth)*16;
                if(!ValidPage(right,GetWide(checkedFrame))||!ReadableGeneration(right)||Read16(right+50)!=memory.Read32(checkedFrame+12)) return Fail(CpxError.Corrupt);
                if(Read16(right+48)<(P-64)/(SeekKeyBytes+8)) { W(Phase,2); return StepResult.Pending; }
                if(R(MutationCheckDepth)==0) return Reject(CpxError.TreeTooDeep);
                W(MutationCheckDepth,R(MutationCheckDepth)-1); W(Phase,24); return StepResult.Pending;
            default: return Fail(CpxError.Corrupt);
        }
    }
    private StepResult SaveObject(uint record,uint next)
    { Copy(SaveRecord,record,256); return Call(CpxOperation.SaveObject,next); }
    private StepResult SaveObjectStep()
    {
        switch(R(Phase))
        {
            case 0: return ReadStaged(Anchor+3*Q,Page(3),1);
            case 1:
                if(!Control(Page(3),3,Anchor+3*Q)||!ReadableGeneration(Page(3))) return Fail(CpxError.Corrupt);
                WW(SaveRoot,GetWide(Page(3)+80)); return MutateTree(CpxTreeKind.Object,RW(SaveRoot),default,SaveRecord,1,2);
            case 2: return Done();
            default: return Fail(CpxError.Corrupt);
        }
    }
}
