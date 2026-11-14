namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    private const uint CleanupId=13400,CleanupKeep=13408,CleanupTake=13416,CleanupRoot=13424,CleanupUnlink=13432,CleanupTree=13436;
    private uint CleanupObject=>Arena+13500;
    private uint CleanupExtent=>Arena+13756;
    private StepResult ReclaimStep()
    {
        uint p=Page(3),obj=CleanupObject,extent=CleanupExtent;
        switch(R(Phase))
        {
            case 0: return ReadStaged(Anchor+3*Q,p,1);
            case 1:
                if(!Control(p,3,Anchor+3*Q)||!ReadableGeneration(p)) return Fail(CpxError.Corrupt);
                WW(CleanupId,GetWide(p+88)); if(RW(CleanupId).IsZero) return Done();
                return LoadObject(RW(CleanupId),2);
            case 2:
                if(R(SourcePage)==0) return Fail(CpxError.Corrupt);
                Copy(obj,ObjectRecord,256); uint flag=Read16(obj+86),links=memory.Read32(obj+76);
                if(flag is <1 or >2||flag==1&&links!=0||flag==2&&(links==0||memory.Read8(obj+84)!=1)) return Fail(CpxError.Corrupt);
                W(CleanupUnlink,flag==1?1u:0u);
                Wide size=GetWide(obj+8),keep=Wide.Right(size,(int)R(UnitShift)); if((size.Low&((1u<<(int)R(UnitShift))-1))!=0) keep+=1;
                WW(CleanupKeep,flag==1?default:keep);
                byte storage=memory.Read8(obj+85);
                if(memory.Read8(obj+84)!=1||storage==0) { W(Phase,20); return StepResult.Pending; }
                if(storage==1)
                {
                    uint count=Read16(obj+90); if(count>6) return Fail(CpxError.Corrupt);
                    if(count==0) { W(Phase,20); return StepResult.Pending; }
                    Copy(extent,obj+96+(count-1)*24,24); W(Phase,4); return StepResult.Pending;
                }
                if(storage!=2) return Fail(CpxError.Corrupt);
                StartSeek(CpxTreeKind.Extent,GetWide(obj+24),RW(CleanupId),new(uint.MaxValue,uint.MaxValue),default,CpxSeek.Floor);
                return Call(CpxOperation.ReadTree,3);
            case 3:
                if(!TreeFound) { W(Phase,20); return StepResult.Pending; }
                Copy(extent,TreeRecord,24); W(Phase,4); return StepResult.Pending;
            case 4:
                uint length=memory.Read32(extent+16); Wide physical=GetWide(extent+8),logical=GetWide(extent);
                if(length==0||length>65536||!Wide.Add(logical,Wide.From(length),out var end)||!Wide.Add(physical,Wide.From(length-1),out var last)||
                    physical.High!=last.High||(physical.Low>>16)!=(last.Low>>16)||memory.Read32(extent+20)>1) return Fail(CpxError.Corrupt);
                if(Wide.Compare(end,RW(CleanupKeep))<=0) { W(Phase,20); return StepResult.Pending; }
                uint take=(last.Low&4095)+1; if(take>length) take=length;
                if(Wide.Compare(logical,RW(CleanupKeep))<0)
                { Wide.Subtract(end,RW(CleanupKeep),out var excess); if(excess.High==0&&take>excess.Low) take=excess.Low; }
                W(CleanupTake,take); return BeginTx(9,5);
            case 5: return ChangeAllocation(GetWide(extent+8)+memory.Read32(extent+16)-R(CleanupTake),R(CleanupTake),false,6);
            case 6:
                uint left=memory.Read32(extent+16)-R(CleanupTake);
                if(!AdjustWide(obj+16,R(CleanupTake),false)) return Fail(CpxError.Corrupt);
                if(memory.Read8(obj+85)==1)
                {
                    uint count=Read16(obj+90);
                    if(left==0) { Clear(obj+96+(count-1)*24,24); Write16(obj+90,count-1); }
                    else memory.Write32(obj+96+(count-1)*24+16,left);
                    return SaveObject(obj,8);
                }
                memory.Write32(extent+16,left);
                return MutateTree(CpxTreeKind.Extent,GetWide(obj+24),RW(CleanupId),extent,left==0?2u:1u,7);
            case 7: return SaveObject(obj,8);
            case 8: return Call(CpxOperation.CommitTransaction,0);
            case 20:
                Wide tree=GetWide(obj+(memory.Read8(obj+84)==2?32u:24u)); WW(CleanupRoot,tree);
                if(tree.IsZero) { W(Phase,30); return StepResult.Pending; }
                W(CleanupTree,memory.Read8(obj+84)==2?1u:2u); return BeginTx(27,21);
            case 21: return PruneTree((CpxTreeKind)R(CleanupTree),RW(CleanupRoot),RW(CleanupId),22);
            case 22:
                if(R(PruneChanged)==0)
                {
                    if(R(CleanupUnlink)!=0) return Fail(CpxError.Corrupt);
                    return Call(CpxOperation.AbortTransaction,30);
                }
                PutWide(obj+(R(CleanupTree)==1?32u:24u),RW(PruneRoot)); return SaveObject(obj,23);
            case 23: return Call(CpxOperation.CommitTransaction,0);
            case 30: return BeginTx(9,31);
            case 31:
                if(R(CleanupUnlink)!=0&&!GetWide(obj+48).IsZero) return ReleaseMetadata(GetWide(obj+48),32);
                W(Phase,32); return StepResult.Pending;
            case 32:
                if(R(CleanupUnlink)!=0&&memory.Read8(obj+84)==3&&memory.Read8(obj+85)==3) return ReleaseMetadata(GetWide(obj+96),33);
                W(Phase,33); return StepResult.Pending;
            case 33: return ReadStaged(Anchor+3*Q,p,34);
            case 34:
                if(!Control(p,3,Anchor+3*Q)||!Wide.Equal(GetWide(p+88),RW(CleanupId))) return Fail(CpxError.Corrupt);
                WW(CleanupRoot,GetWide(p+80)); PutWide(p+88,GetWide(obj+40)); return StagePage(p,Anchor+3*Q,35);
            case 35:
                if(R(CleanupUnlink)!=0) return MutateTree(CpxTreeKind.Object,RW(CleanupRoot),default,obj,2,36);
                // A fully reclaimed extent tree must not strand an empty file
                // in a storage mode that requires a nonexistent tree to write.
                if(GetWide(obj+16).IsZero&&GetWide(obj+24).IsZero)
                {
                    Wide finalSize=GetWide(obj+8); bool inline=finalSize.High==0&&finalSize.Low<=160;
                    Clear(obj+96,160); memory.Write8(obj+85,inline?(byte)0:(byte)1); Write16(obj+90,0); Write16(obj+88,inline?finalSize.Low:0);
                }
                PutWide(obj+40,default); Write16(obj+86,0); return SaveObject(obj,36);
            case 36: return Call(CpxOperation.CommitTransaction,37);
            case 37:
                if(R(CleanupUnlink)!=0) return Call(CpxOperation.PruneObjects,0);
                W(Phase,0); return StepResult.Pending;
            default: return Fail(CpxError.Corrupt);
        }
    }
}
