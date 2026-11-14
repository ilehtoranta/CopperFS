namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // Exclusive operation arena, shared with namespace mutation. Page 0 holds
    // a bitmap, 1 its group descriptor and 3 the retired metadata reserve.
    private const uint ManualMode=10800,ManualProtected=10804,ManualMaximum=10808,ManualBlocks=10812,
        ManualRanges=10816,ManualGroup=10820,ManualMap=10828,ManualPass=10832,ManualBit=10836,
        ManualGroupUnits=10840,ManualGroupFree=10844,ManualTotal=10848,ManualExpected=10856,
        ManualRoot=10864,ManualFirst=10872,ManualEnd=10876,ManualDeviceLimit=10880;
    public uint ManualTrimBlocks=>R(ManualBlocks);
    public uint ManualTrimRanges=>R(ManualRanges);
    public uint ManualTrimMaximum=>R(ManualMaximum);
    public CpxError BeginManualTrim(uint flags,bool writeProtected=false)
    {
        if(flags>2) return CpxError.InvalidArgument;
        CpxError error=StartOperation(CpxOperation.ManualTrim);
        if(error!=CpxError.None) return error;
        W(ManualMode,flags); W(ManualProtected,writeProtected||IsReadOnly?1u:0u);
        W(ManualMaximum,0); W(ManualDeviceLimit,0); W(ManualBlocks,0); W(ManualRanges,0);
        W(ManualPass,0); WW(ManualGroup,default); WW(ManualTotal,default); return CpxError.None;
    }
    private Wide ManualDescriptor=>RW(ManualGroup).IsZero?Wide.From(131072u>>(int)R(UnitShift)):RW(ManualGroup);
    private Wide ManualWindow=>RW(ManualGroup)+R(ManualMap)*4096;
    private StepResult ManualTrimStep()
    {
        uint p=Page(0),descriptor=Page(1),reserve=Page(3);
        switch(R(Phase))
        {
            case 0:
                if(!DiscardEnabled) return Reject(CpxError.Unsupported);
                return Issue(IoKind.QueryDiscard,default,Arena+ManualDeviceLimit,4,1);
            case 1:
                if(!DiscardEnabled) return Reject(CpxError.Unsupported);
                uint maximum=R(ManualDeviceLimit)>>(int)(R(UnitShift)-R(SectorShift));
                if(maximum==0) { W(TrimDisabled,1); return Reject(CpxError.Unsupported); }
                W(ManualMaximum,maximum>4096?4096:maximum);
                if(R(ManualMode)==2) return Done();
                if(R(ManualProtected)!=0) return Reject(CpxError.ReadOnly);
                return Issue(IoKind.Flush,default,0,0,2);
            case 2: return Issue(IoKind.Read,StatePageAddress,p,P,3);
            case 3:
                if(!Control(p,3,StatePageAddress)||!ReadableGeneration(p)||!ValidStateLabel(p)||!Allocatable(GetWide(p+80))) return Fail(CpxError.Corrupt);
                WW(ManualRoot,GetWide(p+80)); WW(ManualExpected,GetWide(p+96));
                return Issue(IoKind.Read,Anchor+8*Q,reserve,P,4);
            case 4:
                if(!Control(reserve,4,Anchor+8*Q)||!ReadableGeneration(reserve)||Read16(reserve+52)!=520||Read16(reserve+64)>64) return Fail(CpxError.Corrupt);
                W(Phase,5); return StepResult.Pending;
            case 5:
                for(uint n=0;n<Read16(reserve+64);n++)
                {
                    Wide held=GetWide(reserve+72+n*8);
                    if(!Allocatable(held)||Wide.Equal(held,RW(ManualRoot))) return Fail(CpxError.Corrupt);
                }
                W(Phase,6); return StepResult.Pending;
            case 6:
                Wide group=RW(ManualGroup);
                if(!Wide.Subtract(RW(Units),group,out var left)||left.IsZero||!group.IsZero&&left.High==0&&left.Low<18*Q)
                    return FinishManualPass();
                W(ManualGroupUnits,left.High!=0||left.Low>=65536?65536u:left.Low);
                W(ManualMap,0); W(ManualGroupFree,0);
                return Issue(IoKind.Read,ManualDescriptor,descriptor,P,7);
            case 7:
                if(!ValidPage(descriptor,ManualDescriptor)||!ReadableGeneration(descriptor)||Read16(descriptor+4)!=6||
                    !Wide.Equal(GetWide(descriptor+24),RW(ManualGroup))||Read16(descriptor+52)!=80||
                    memory.Read32(descriptor+64)!=R(ManualGroupUnits)) return Fail(CpxError.Corrupt);
                W(Phase,8); return StepResult.Pending;
            case 8: return Issue(IoKind.Read,Bitmap(ManualWindow),p,P,9);
            case 9:
                if(!ControlBitmap(p,ManualWindow,RW(ManualGroup))||!ReadableGeneration(p)) return Fail(CpxError.Corrupt);
                W(Phase,10); return StepResult.Pending;
            case 10: return ValidateTrimWindow();
            case 11:
                // Check every page of the reserve and the current object root
                // independently of the free-count summaries.
                for(uint n=0;n<Read16(reserve+64);n++)
                    if(!TrimPageAllocated(GetWide(reserve+72+n*8))) return Fail(CpxError.Corrupt);
                if(!TrimPageAllocated(RW(ManualRoot))) return Fail(CpxError.Corrupt);
                W(ManualBit,R(ManualFirst)); W(Phase,R(ManualPass)==0?14u:12u); return StepResult.Pending;
            case 12: return NextManualRange();
            case 13:
                if(!DiscardEnabled) return Reject(CpxError.Unsupported);
                W(Phase,12); return StepResult.Pending;
            case 14:
                if(R(ManualMap)<15) { W(ManualMap,R(ManualMap)+1); W(Phase,8); return StepResult.Pending; }
                if(R(ManualGroupFree)!=memory.Read32(descriptor+68)) return Fail(CpxError.Corrupt);
                WW(ManualTotal,RW(ManualTotal)+R(ManualGroupFree));
                WW(ManualGroup,RW(ManualGroup)+65536); W(Phase,6); return StepResult.Pending;
            default: return Fail(CpxError.Corrupt);
        }
    }
    private StepResult FinishManualPass()
    {
        if(!Wide.Equal(RW(ManualTotal),RW(ManualExpected))) return Fail(CpxError.Corrupt);
        // v1 has unsigned 32-bit result counters. Refuse an unreportable
        // volume before submitting the first irreversible command.
        if(RW(ManualTotal).High!=0) return Reject(CpxError.InvalidArgument);
        if(R(ManualPass)==1) return Done();
        WW(ManualGroup,default); WW(ManualTotal,default); W(ManualPass,1); W(Phase,6);
        return StepResult.Pending;
    }
    private bool TrimPageAllocated(Wide held)
    {
        Wide window=ManualWindow;
        if(held.High!=window.High||(held.Low&~4095u)!=window.Low) return true;
        uint bit=held.Low&4095,p=Page(0)+64;
        for(uint n=0;n<Q;n++,bit++) if((memory.Read8(p+(bit>>3))&(128>>(int)(bit&7)))==0) return false;
        return true;
    }
    private StepResult ValidateTrimWindow()
    {
        Wide window=ManualWindow;
        uint floor=RW(ManualGroup).IsZero?2097152u>>(int)R(UnitShift):17*Q;
        uint offset=R(ManualMap)*4096,first=offset>=floor?0:floor-offset,end=4096;
        if(first>4096) first=4096;
        if(!Wide.Subtract(Backup,window,out var remaining)) end=0;
        else if(remaining.High==0&&remaining.Low<4096) end=remaining.Low;
        W(ManualFirst,first); W(ManualEnd,end);
        uint free=0,p=Page(0)+64;
        for(uint i=0;i<512;i++)
        {
            uint bits=(uint)(255^memory.Read8(p+i)); if(bits==0) continue;
            if(i*8<first||(i+1)*8>end)
                for(uint b=0;b<8;b++) if((bits&(128u>>(int)b))!=0&&(i*8+b<first||i*8+b>=end)) return Fail(CpxError.Corrupt);
            bits-=((bits>>1)&0x55); bits=(bits&0x33)+((bits>>2)&0x33); free+=(bits+(bits>>4))&0x0f;
        }
        if(free!=memory.Read32(Page(1)+80+4*R(ManualMap))) return Fail(CpxError.Corrupt);
        W(ManualGroupFree,R(ManualGroupFree)+free); W(Phase,11); return StepResult.Pending;
    }
    private StepResult NextManualRange()
    {
        uint bit=R(ManualBit),end=R(ManualEnd),map=Page(0)+64;
        while(bit<end&&(memory.Read8(map+(bit>>3))&(128>>(int)(bit&7)))!=0) bit++;
        if(bit>=end) { W(Phase,14); return StepResult.Pending; }
        uint first=bit,limit=first+R(ManualMaximum); if(limit>end) limit=end;
        while(bit<limit&&(memory.Read8(map+(bit>>3))&(128>>(int)(bit&7)))==0) bit++;
        uint count=bit-first; W(ManualBit,bit); W(TrimCount,count);
        if(count>uint.MaxValue-R(ManualBlocks)||R(ManualRanges)==uint.MaxValue) return Reject(CpxError.InvalidArgument);
        if(R(ManualMode)==1) { CountManualRange(); return StepResult.Pending; }
        return Issue(IoKind.Discard,ManualWindow+first,0,count<<(int)R(UnitShift),13);
    }
    private void CountManualRange()
    { W(ManualBlocks,R(ManualBlocks)+R(TrimCount)); W(ManualRanges,R(ManualRanges)+1); }
}
