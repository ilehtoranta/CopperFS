namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    public uint TreeRecord => ObjectRecord+512;
    private uint SeekPath => ObjectRecord+768;
    public bool TreeFound => R(SeekFound)!=0;
    public CpxError BeginTreeRead(CpxTreeKind kind,Wide root,Wide owner,Wide high,Wide low,CpxSeek seek)
    {
        if(kind>CpxTreeKind.Extent||seek>CpxSeek.After) return CpxError.InvalidArgument;
        CpxError error=StartOperation(CpxOperation.ReadTree);
        if(error==CpxError.None) StartSeek(kind,root,owner,high,low,seek);
        return error;
    }
    private void StartSeek(CpxTreeKind kind,Wide root,Wide owner,Wide high,Wide low,CpxSeek seek)
    {
        W(TreeKindAt,(uint)kind); W(SeekAt,(uint)seek); WW(SeekRoot,root); WW(SeekOwner,owner);
        WW(SeekHigh,high); WW(SeekLow,low); W(SeekFound,0); W(SeekDepth,0); W(SeekExpected,uint.MaxValue); W(SeekDirection,0);
        WW(SeekAddress,root);
    }
    private uint SeekKeyBytes => R(TreeKindAt)==(uint)CpxTreeKind.Directory?16u:8u;
    private uint SeekLeafBytes => R(TreeKindAt)==0?256u:R(TreeKindAt)==1?128u:24u;
    private uint SeekType(uint level) => 11+R(TreeKindAt)*2+(level!=0?1u:0u);
    private int SeekCompare(uint record,Wide high,Wide low)
    {
        int order=Wide.Compare(GetWide(record),high);
        return order!=0||SeekKeyBytes==8?order:Wide.Compare(GetWide(record+8),low);
    }
    private StepResult SeekStep()
    {
        uint p=Page(1),phase=R(Phase);
        switch(phase)
        {
            case 0:
                if(RW(SeekRoot).IsZero) return Done();
                W(Phase,1); return StepResult.Pending;
            case 1:
                if(R(SeekDepth)>=8||!Allocatable(RW(SeekAddress))) return Fail(CpxError.Corrupt);
                return ReadStaged(RW(SeekAddress),p,2);
            case 2:
                uint level=Read16(p+50),count=Read16(p+48),width=level==0?SeekLeafBytes:SeekKeyBytes+8;
                if(!ValidPage(p,RW(SeekAddress))||!ReadableGeneration(p)||Read16(p+4)!=SeekType(level)||!Wide.Equal(GetWide(p+24),RW(SeekOwner))||
                    !GetWide(p+40).IsZero||level>=8||(R(SeekExpected)!=uint.MaxValue&&R(SeekExpected)!=level)||count>(P-64)/width||
                    (level!=0&&count==0)||Read16(p+52)!=count*width) return Fail(CpxError.Corrupt);
                W(SeekCount,count); W(SeekLevel,level); W(SeekScan,0); W(SeekSlot,level==0?uint.MaxValue:0); W(Phase,3); return StepResult.Pending;
            case 3:
                uint size=R(SeekLevel)==0?SeekLeafBytes:SeekKeyBytes+8,index=R(SeekScan),end=index+64;
                if(end>R(SeekCount)) end=R(SeekCount);
                for(;index<end;index++)
                {
                    uint record=p+64+index*size; Wide high=GetWide(record),low=SeekKeyBytes==16?GetWide(record+8):default;
                    if(index!=0&&SeekCompare(record-size,high,low)>=0) return Fail(CpxError.Corrupt);
                    int order=SeekCompare(record,RW(SeekHigh),RW(SeekLow));
                    if(R(SeekLevel)!=0)
                    { if(!Allocatable(GetWide(record+SeekKeyBytes))) return Fail(CpxError.Corrupt); if(order<=0) W(SeekSlot,index); }
                    else if(R(SeekDirection)==1 || R(SeekAt)==(uint)CpxSeek.Floor&&order<=0&&R(SeekDirection)==0) W(SeekSlot,index);
                    else if(R(SeekSlot)==uint.MaxValue&&(R(SeekDirection)==2||
                        R(SeekDirection)==0&&(R(SeekAt)==(uint)CpxSeek.Exact&&order==0||R(SeekAt)==(uint)CpxSeek.Next&&order>=0||R(SeekAt)==(uint)CpxSeek.After&&order>0))) W(SeekSlot,index);
                }
                W(SeekScan,index); if(index<R(SeekCount)) return StepResult.Pending;
                uint frame=SeekPath+R(SeekDepth)*16;
                PutWide(frame,RW(SeekAddress)); memory.Write32(frame+12,R(SeekLevel));
                if(R(SeekLevel)!=0)
                {
                    uint slot=R(SeekDirection)==1?R(SeekCount)-1:R(SeekDirection)==2?0:R(SeekSlot);
                    memory.Write32(frame+8,slot); W(SeekDepth,R(SeekDepth)+1); W(SeekExpected,R(SeekLevel)-1);
                    WW(SeekAddress,GetWide(p+64+slot*size+SeekKeyBytes)); W(Phase,1); return StepResult.Pending;
                }
                if(R(SeekSlot)!=uint.MaxValue)
                {
                    memory.Write32(frame+8,R(SeekSlot));
                    for(uint n=0;n<SeekLeafBytes;n++) memory.Write8(TreeRecord+n,memory.Read8(p+64+R(SeekSlot)*SeekLeafBytes+n));
                    W(SeekFound,1); return Done();
                }
                if(R(SeekAt)==(uint)CpxSeek.Exact) return Done();
                W(SeekDirection,R(SeekAt)==(uint)CpxSeek.Floor?1u:2u); W(Phase,4); return StepResult.Pending;
            case 4:
                if(R(SeekDepth)==0) return Done();
                W(SeekDepth,R(SeekDepth)-1); WW(SeekAddress,GetWide(SeekPath+R(SeekDepth)*16));
                return ReadStaged(RW(SeekAddress),p,5);
            case 5:
                // Parent was fully validated on descent, but is reread after an I/O
                // boundary: recheck identity, checksum and shape before following it.
                uint parent=SeekPath+R(SeekDepth)*16,lev=memory.Read32(parent+12),cnt=Read16(p+48),sz=SeekKeyBytes+8;
                if(!ValidPage(p,RW(SeekAddress))||!ReadableGeneration(p)||Read16(p+4)!=SeekType(lev)||!Wide.Equal(GetWide(p+24),RW(SeekOwner))||
                    Read16(p+50)!=lev||cnt==0||cnt>(P-64)/sz||Read16(p+52)!=cnt*sz||!GetWide(p+40).IsZero) return Fail(CpxError.Corrupt);
                uint old=memory.Read32(parent+8); if(old>=cnt) return Fail(CpxError.Corrupt);
                if(R(SeekDirection)==1?old==0:old+1==cnt) { W(Phase,4); return StepResult.Pending; }
                uint next=R(SeekDirection)==1?old-1:old+1; memory.Write32(parent+8,next);
                WW(SeekAddress,GetWide(p+64+next*sz+SeekKeyBytes)); W(SeekExpected,lev-1); W(SeekDepth,R(SeekDepth)+1); W(Phase,1); return StepResult.Pending;
            default: return Fail(CpxError.Corrupt);
        }
    }
}
