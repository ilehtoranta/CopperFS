namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    private const uint ResizeId=14200,ResizeLength=14208,ResizeOld=14216,ResizePhysical=14224,ResizeScan=14232;
    private uint ResizeObject=>Arena+14300;
    private uint ResizeExtent=>Arena+14556;
    public CpxError BeginResize(Wide id,Wide size)
    {
        CpxError error=StartOperation(CpxOperation.Resize); if(error!=CpxError.None) return error;
        WW(ResizeId,id); WW(ResizeLength,size); return error;
    }
    private StepResult ResizeStep()
    {
        uint obj=ResizeObject,p=Page(3),unit=1u<<(int)R(UnitShift); Wide length=RW(ResizeLength),old=RW(ResizeOld);
        switch(R(Phase))
        {
            case 0: return Call(CpxOperation.Reclaim,1);
            case 1: return LoadObject(RW(ResizeId),2);
            case 2:
                if(R(SourcePage)==0) return Reject(CpxError.NotFound);
                Copy(obj,ObjectRecord,256); if(memory.Read8(obj+84)!=1) return Reject(CpxError.WrongType);
                if((memory.Read32(obj+72)&4)!=0) return Reject(CpxError.Protected);
                WW(ResizeOld,GetWide(obj+8)); if(Wide.Equal(length,GetWide(obj+8))) return Done();
                if(memory.Read8(obj+85)==0)
                {
                    if(GetWide(obj+8).High!=0||GetWide(obj+8).Low>160||Read16(obj+88)!=GetWide(obj+8).Low) return Fail(CpxError.Corrupt);
                    if(length.High==0&&length.Low<=160)
                    {
                        uint from=length.Low<GetWide(obj+8).Low?length.Low:GetWide(obj+8).Low; Clear(obj+96+from,160-from);
                        PutWide(obj+8,length); Write16(obj+88,length.Low); return BeginTx(6,10);
                    }
                    return EnsureReserve(30,40);
                }
                if(memory.Read8(obj+85)>2) return Fail(CpxError.Corrupt);
                if(Wide.Compare(length,GetWide(obj+8))<0) return BeginTx(6,20);
                if((GetWide(obj+8).Low&(unit-1))==0) return BeginTx(6,9);
                Wide logical=Wide.Right(GetWide(obj+8),(int)R(UnitShift));
                if(memory.Read8(obj+85)==1)
                {
                    uint count=Read16(obj+90); if(count>6) return Fail(CpxError.Corrupt);
                    Clear(ResizeExtent,24);
                    for(uint n=0;n<count;n++) if(Wide.Compare(GetWide(obj+96+n*24),logical)<=0) Copy(ResizeExtent,obj+96+n*24,24);
                    W(Phase,4); return StepResult.Pending;
                }
                StartSeek(CpxTreeKind.Extent,GetWide(obj+24),RW(ResizeId),logical,default,CpxSeek.Floor); return Call(CpxOperation.ReadTree,3);
            case 3:
                Clear(ResizeExtent,24); if(TreeFound) Copy(ResizeExtent,TreeRecord,24); W(Phase,4); return StepResult.Pending;
            case 4:
                uint countAt=memory.Read32(ResizeExtent+16); Wide logicalAt=Wide.Right(old,(int)R(UnitShift));
                if(countAt==0||Wide.Compare(logicalAt,GetWide(ResizeExtent)+countAt)>=0||memory.Read32(ResizeExtent+20)==1) return BeginTx(6,9);
                if(countAt>65536||memory.Read32(ResizeExtent+20)!=0||!Wide.Subtract(logicalAt,GetWide(ResizeExtent),out var delta)||
                    !Wide.Add(GetWide(ResizeExtent+8),delta,out var physical)||!DataUnit(physical)) return Fail(CpxError.Corrupt);
                WW(ResizePhysical,physical); return Issue(IoKind.Read,physical,Page(4),unit,5);
            case 5:
                Clear(Page(4)+(old.Low&(unit-1)),unit-(old.Low&(unit-1))); return BeginTx(6,6);
            case 6: return Issue(IoKind.Write,RW(ResizePhysical),Page(4),unit,9);
            case 9: PutWide(obj+8,length); W(Phase,10); return StepResult.Pending;
            case 10: return SaveObject(obj,11);
            case 11: return Call(CpxOperation.CommitTransaction,99);
            case 20: return ReadStaged(Anchor+3*Q,p,21);
            case 21:
                if(!Control(p,3,Anchor+3*Q)||!ReadableGeneration(p)||Read16(obj+86)!=0) return Fail(CpxError.Corrupt);
                PutWide(obj+8,length); Write16(obj+86,2); PutWide(obj+40,GetWide(p+88)); PutWide(p+88,RW(ResizeId)); return StagePage(p,Anchor+3*Q,22);
            case 22: return SaveObject(obj,23);
            case 23: return Call(CpxOperation.CommitTransaction,24);
            case 24: return Call(CpxOperation.Reclaim,99);
            case 40:
                Clear(Page(4),unit); Copy(Page(4),obj+96,old.Low); Clear(obj+96,160); Write16(obj+88,0); memory.Write8(obj+85,1);
                if(old.IsZero) return BeginTx(6,9);
                WW(ResizeScan,Wide.From(2097152u>>(int)R(UnitShift))); W(Phase,41); return StepResult.Pending;
            case 41:
                Wide at=RW(ResizeScan),group=new(at.High,at.Low&0xffff0000); Wide.Subtract(RW(Units),group,out var remaining);
                if(Wide.Compare(at,Backup)>=0||!group.IsZero&&remaining.High==0&&remaining.Low<18*Q) return Reject(CpxError.DiskFull);
                return ReadStaged(Bitmap(at),p,42);
            case 42:
                if(!ControlBitmap(p,RW(ResizeScan),new(RW(ResizeScan).High,RW(ResizeScan).Low&0xffff0000))||!ReadableGeneration(p)) return Fail(CpxError.Corrupt);
                W(Phase,43); return StepResult.Pending;
            case 43:
                Wide window=new(RW(ResizeScan).High,RW(ResizeScan).Low&~4095u);
                for(uint bit=RW(ResizeScan).Low&4095;bit<4096;bit++)
                {
                    if((memory.Read8(p+64+(bit>>3))&(0x80>>(int)(bit&7)))!=0||!DataUnit(window+bit)) continue;
                    WW(ResizePhysical,window+bit); PutWide(obj+16,Wide.From(1)); Write16(obj+90,1);
                    PutWide(obj+104,window+bit); memory.Write32(obj+112,1); return BeginTx(42,44);
                }
                WW(ResizeScan,window+4096); W(Phase,41); return StepResult.Pending;
            case 44: return ChangeAllocation(RW(ResizePhysical),1,true,6);
            case 99: return Done();
            default: return Fail(CpxError.Corrupt);
        }
    }
}
