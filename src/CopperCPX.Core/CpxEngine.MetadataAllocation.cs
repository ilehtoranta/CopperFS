namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    private const uint AllocationAddress=10000, AllocationCount=10008, AllocationUsed=10012, MetadataRelease=10016,
        MetadataResult=10024, RefillScan=10400, RefillWindows=10408, RefillCount=10412, RefillFirstWindow=10416, RefillMinimum=10424;
    private StepResult EnsureReserve(uint minimum,uint next)
    { W(RefillMinimum,minimum); return Call(CpxOperation.Refill,next); }
    private StepResult TakeMetadata(uint next)
    { W(MetadataRelease,0); return Call(CpxOperation.MetadataAllocation,next); }
    private StepResult ReleaseMetadata(Wide address,uint next)
    { W(MetadataRelease,1); WW(MetadataResult,address); return Call(CpxOperation.MetadataAllocation,next); }
    private StepResult ChangeAllocation(Wide address,uint count,bool used,uint next)
    {
        WW(AllocationAddress,address); W(AllocationCount,count); W(AllocationUsed,used?1u:0u);
        return Call(CpxOperation.ChangeAllocation,next);
    }
    private StepResult MetadataAllocationStep()
    {
        uint p=Page(3),count=Read16(p+64);
        switch(R(Phase))
        {
            case 0: return ReadStaged(Anchor+8*Q,p,1);
            case 1:
                if(!Control(p,4,Anchor+8*Q)||!ReadableGeneration(p)||count>64) return Fail(CpxError.Corrupt);
                if(R(MetadataRelease)==0)
                {
                    if(R(MetadataReleased)!=0||R(MetadataTaken)>=R(MetadataLimit)) return Fail(CpxError.Corrupt);
                    if(count==0) return Reject(CpxError.ReserveExhausted);
                    Wide address=GetWide(p+72+(count-1)*8); if(!Allocatable(address)) return Fail(CpxError.Corrupt);
                    WW(MetadataResult,address); Clear(p+72+(count-1)*8,8); Write16(p+64,count-1);
                    W(MetadataTaken,R(MetadataTaken)+1);
                }
                else
                {
                    W(MetadataReleased,1);
                    if(!Allocatable(RW(MetadataResult))) return Fail(CpxError.Corrupt);
                    for(uint n=0;n<count;n++) if(Wide.Equal(GetWide(p+72+n*8),RW(MetadataResult))) return Fail(CpxError.Corrupt);
                    if(count==64) return ChangeAllocation(RW(MetadataResult),Q,false,3);
                    PutWide(p+72+count*8,RW(MetadataResult)); Write16(p+64,count+1);
                }
                return StagePage(p,Anchor+8*Q,3);
            case 3: return Done();
            default: return Fail(CpxError.Corrupt);
        }
    }
    private StepResult ChangeAllocationStep()
    {
        uint p=Page(3),count=R(AllocationCount); Wide at=RW(AllocationAddress),group=new(at.High,at.Low&0xffff0000);
        Wide descriptor=group.IsZero?Wide.From(131072u>>(int)R(UnitShift)):group;
        switch(R(Phase))
        {
            case 0:
                if(count==0||count>4096-(at.Low&4095)) return Fail(CpxError.Corrupt);
                return ReadStaged(Bitmap(at),p,1);
            case 1:
                if(!ControlBitmap(p,at,group)||!ReadableGeneration(p)) return Fail(CpxError.Corrupt);
                W(Phase,2); return StepResult.Pending;
            case 2:
                for(uint n=0;n<count;n++)
                {
                    if(!DataUnit(at+n)) return Fail(CpxError.Corrupt);
                    uint bit=(at.Low+n)&4095,byteAt=p+64+(bit>>3),mask=0x80u>>(int)(bit&7),value=memory.Read8(byteAt);
                    if(((value&mask)!=0)==(R(AllocationUsed)!=0)) return Fail(CpxError.Corrupt);
                    memory.Write8(byteAt,(byte)(R(AllocationUsed)!=0?value|mask:value&~mask));
                }
                W(Phase,3); return StepResult.Pending;
            case 3: return StagePage(p,Bitmap(at),4);
            case 4: return ReadStaged(descriptor,p,5);
            case 5:
                uint local=80+((at.Low&65535)>>12)*4,free=memory.Read32(p+68),window=memory.Read32(p+local);
                if(!ValidPage(p,descriptor)||!ReadableGeneration(p)||Read16(p+4)!=6||!Wide.Equal(GetWide(p+24),group)||Read16(p+52)!=80||
                    (R(AllocationUsed)!=0?(free<count||window<count):(free>65536-count||window>4096-count))) return Fail(CpxError.Corrupt);
                memory.Write32(p+68,R(AllocationUsed)!=0?free-count:free+count); memory.Write32(p+local,R(AllocationUsed)!=0?window-count:window+count);
                return StagePage(p,descriptor,6);
            case 6: return ReadStaged(Anchor+3*Q,p,7);
            case 7:
                if(!Control(p,3,Anchor+3*Q)||!ReadableGeneration(p)) return Fail(CpxError.Corrupt);
                Wide available;
                if(R(AllocationUsed)!=0?!Wide.Subtract(GetWide(p+96),Wide.From(count),out available):!Wide.Add(GetWide(p+96),Wide.From(count),out available)) return Fail(CpxError.Corrupt);
                PutWide(p+96,available); return StagePage(p,Anchor+3*Q,8);
            case 8: return Done();
            default: return Fail(CpxError.Corrupt);
        }
    }
    // Refill is retired separately from the foreground operation. Each refill
    // transaction touches at most two bitmap windows and six distinct images.
    private StepResult RefillStep()
    {
        uint p=Page(3); Wide at=RW(RefillScan);
        switch(R(Phase))
        {
            case 0: return ReadStaged(Anchor+8*Q,p,1);
            case 1:
                if(!Control(p,4,Anchor+8*Q)||!ReadableGeneration(p)||Read16(p+64)>64) return Fail(CpxError.Corrupt);
                if(Read16(p+64)>=R(RefillMinimum)) return Done();
                W(RefillCount,Read16(p+64)); W(RefillWindows,0); WW(RefillScan,Wide.From(2097152u>>(int)R(UnitShift)));
                return BeginTx(6,2);
            case 2:
                if(Wide.Compare(at,Backup)>=0) return R(RefillWindows)==0?Call(CpxOperation.AbortTransaction,10):Call(CpxOperation.CommitTransaction,10);
                Wide group=new(at.High,at.Low&0xffff0000); Wide.Subtract(RW(Units),group,out var remaining);
                if(!group.IsZero&&remaining.High==0&&remaining.Low<18*Q) { WW(RefillScan,Backup); return StepResult.Pending; }
                return ReadStaged(Bitmap(at),p,3);
            case 3:
                if(!ControlBitmap(p,at,new(at.High,at.Low&0xffff0000))||!ReadableGeneration(p)) return Fail(CpxError.Corrupt);
                W(Phase,4); return StepResult.Pending;
            case 4:
                Wide window=new(at.High,at.Low&~4095u);
                for(uint bit=at.Low&4095;bit<4096;bit+=Q)
                {
                    if(!Allocatable(window+bit)) continue;
                    bool free=true; for(uint n=0;n<Q;n++) if((memory.Read8(p+64+((bit+n)>>3))&(0x80>>(int)((bit+n)&7)))!=0) free=false;
                    if(!free) continue;
                    if(R(RefillWindows)==0||!Wide.Equal(window,RW(RefillFirstWindow)))
                    {
                        if(R(RefillWindows)==2) return Call(CpxOperation.CommitTransaction,0);
                        W(RefillWindows,R(RefillWindows)+1); WW(RefillFirstWindow,window);
                    }
                    WW(RefillScan,window+bit); return ChangeAllocation(window+bit,Q,true,5);
                }
                WW(RefillScan,window+4096); W(Phase,2); return StepResult.Pending;
            case 5: return ReleaseMetadata(RW(RefillScan),6);
            case 6:
                W(RefillCount,R(RefillCount)+1); WW(RefillScan,RW(RefillScan)+Q);
                if(R(RefillCount)>=32) return Call(CpxOperation.CommitTransaction,11);
                W(Phase,2); return StepResult.Pending;
            case 10: return R(RefillCount)>=R(RefillMinimum)?Done():Reject(CpxError.ReserveExhausted);
            case 11: return Done();
            default: return Fail(CpxError.Corrupt);
        }
    }
}
