namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    public bool DiscardEnabled => R(TrimDisabled)==0;
    public uint DiscardedUnits => R(FileValue);
    /// <summary>Examines at most one bitmap window and discards at most one unit.
    /// The cursor advances even on a full window, bounding maintenance latency.</summary>
    private StepResult TrimFreeStep()
    {
        uint p=Page(0); Wide at=RW(Target);
        switch(R(Phase))
        {
            case 0:
                if(R(TrimDisabled)!=0) return Done();
                Wide cursor=RW(TrimCursor),first=Wide.From(2097152u>>(int)R(UnitShift));
                if(Wide.Compare(cursor,first)<0||Wide.Compare(cursor,Backup)>=0) cursor=first;
                Wide group=new(cursor.High,cursor.Low&0xffff0000); Wide.Subtract(RW(Units),group,out var remaining);
                if(!group.IsZero&&remaining.High==0&&remaining.Low<18*Q) { WW(TrimCursor,first); return Done(); }
                WW(Target,cursor); return Issue(IoKind.Read,Anchor+8*Q,Page(3),P,1);
            case 1:
                uint reserve=Page(3),count=Read16(reserve+64);
                if(!Control(reserve,4,Anchor+8*Q)||!ReadableGeneration(reserve)||count>64||Read16(reserve+52)!=520) return Fail(CpxError.Corrupt);
                return Issue(IoKind.Read,Bitmap(at),p,P,2);
            case 2:
                if(!ControlBitmap(p,at,new(at.High,at.Low&0xffff0000))||!ReadableGeneration(p)) return Fail(CpxError.Corrupt);
                W(Phase,3); return StepResult.Pending;
            case 3:
                // Exclude reserve even if damaged free-space bits claim it is free.
                uint r=Page(3),entries=Read16(r+64);
                for(uint n=0;n<entries;n++)
                {
                    Wide held=GetWide(r+72+n*8); if(!Allocatable(held)) return Fail(CpxError.Corrupt);
                    if(held.High!=at.High||(held.Low>>12)!=(at.Low>>12)) continue;
                    for(uint q=0;q<Q;q++) { uint bit=(held.Low&4095)+q,byteAt=p+64+(bit>>3); memory.Write8(byteAt,(byte)(memory.Read8(byteAt)|(0x80>>(int)(bit&7)))); }
                }
                W(Phase,4); return StepResult.Pending;
            case 4:
                Wide window=new(at.High,at.Low&~4095u);
                for(uint bit=at.Low&4095;bit<4096;bit++)
                {
                    if((memory.Read8(p+64+(bit>>3))&(0x80>>(int)(bit&7)))!=0||!DataUnit(window+bit)) continue;
                    WW(TrimCursor,window+bit+1); return Issue(IoKind.Discard,window+bit,0,1u<<(int)R(UnitShift),5);
                }
                WW(TrimCursor,window+4096); return Done();
            case 5: if(R(TrimDisabled)==0) W(FileValue,1); return Done();
            default: return Fail(CpxError.Corrupt);
        }
    }
    /// <summary>One retired range, within one bitmap window. No mutation can be
    /// admitted until revalidation and the single discard completion have finished.</summary>
    public CpxError BeginTrim(Wide first,uint units)
    {
        if(units==0||units>4096-(first.Low&4095)||!Wide.Add(first,Wide.From(units),out var end)||Wide.Compare(end,RW(Units))>0) return CpxError.InvalidArgument;
        CpxError error=StartOperation(CpxOperation.Trim,first);
        if(error==CpxError.None) W(TrimCount,units); return error;
    }
    private bool DataUnit(Wide unit)
    {
        if(Wide.Compare(unit,Wide.From(2097152u>>(int)R(UnitShift)))<0||Wide.Compare(unit,Backup)>=0) return false;
        Wide group=new(unit.High,unit.Low&0xffff0000);
        if(group.IsZero) return true;
        if((unit.Low&65535)<17*Q) return false;
        if(!Wide.Subtract(RW(Units),group,out var remaining)) return false;
        return remaining.High!=0||remaining.Low>=65536||remaining.Low>=18*Q;
    }
    private StepResult TrimStep()
    {
        uint p=Page(0); Wide at=RW(Target);
        switch(R(Phase))
        {
            case 0:
                if(R(TrimCount)==0) return Reject(CpxError.InvalidArgument);
                if(R(TrimDisabled)!=0) return Done();
                if(!DataUnit(at)||!DataUnit(at+(R(TrimCount)-1))) return Reject(CpxError.InvalidArgument);
                return Issue(IoKind.Read,Anchor+8*Q,Page(3),P,1);
            case 1:
                uint reserve=Page(3),count=Read16(reserve+64);
                if(!Control(reserve,4,Anchor+8*Q)||!ReadableGeneration(reserve)||count>64||Read16(reserve+52)!=520) return Fail(CpxError.Corrupt);
                Wide end=at+R(TrimCount);
                for(uint n=0;n<count;n++)
                {
                    Wide reserved=GetWide(reserve+72+n*8);
                    if(!Allocatable(reserved)) return Fail(CpxError.Corrupt);
                    if(Wide.Compare(reserved,end)<0&&Wide.Compare(at,reserved+Q)<0) return Done();
                }
                return Issue(IoKind.Read,Bitmap(at),p,P,2);
            case 2:
                if(!ControlBitmap(p,at,new(at.High,at.Low&0xffff0000))||!ReadableGeneration(p)) return Fail(CpxError.Corrupt);
                // All allocated space is excluded, including unwritten extents. Do
                // not split a stale hint: a later maintenance step may retry it.
                uint bit=at.Low&4095;
                for(uint n=0;n<R(TrimCount);n++)
                    if((memory.Read8(p+64+((bit+n)>>3))&(0x80>>(int)((bit+n)&7)))!=0) return Done();
                return Issue(IoKind.Discard,at,0,R(TrimCount)<<(int)R(UnitShift),3);
            case 3:
                if(R(TrimDisabled)==0) W(FileValue,R(TrimCount)); return Done();
            default: return Fail(CpxError.Corrupt);
        }
    }
}
