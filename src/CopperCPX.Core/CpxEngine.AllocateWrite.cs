namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // Up to three edits replace one unwritten run with left/written/right
    // pieces. Promoting six inline descriptors needs at most eight records.
    private uint WriteOriginal=>Arena+14700;
    private uint WritePieces=>Arena+14724;
    private uint WriteInlineExtents=>Arena+14816;
    private const uint WriteEditCount=14800,WriteEditCursor=14804,WriteFirstMode=14808,WriteInlineCount=15008;
    private void Extent(uint p,Wide logical,Wide physical,uint count,uint flags)
    { PutWide(p,logical); PutWide(p+8,physical); memory.Write32(p+16,count); memory.Write32(p+20,flags); }

    private StepResult AllocateWriteStep()
    {
        uint record=ObjectRecord,unit=1u<<(int)R(UnitShift),p=Page(2),mode=R(AllocationMode); Wide at=RW(AllocationScan);
        switch(R(Phase))
        {
            case 150: return EnsureReserve(30,156);
            case 156: return Issue(IoKind.Read,Anchor+8*Q,Page(3),P,151);
            case 151:
                if(!Control(Page(3),4,Anchor+8*Q)||!ReadableGeneration(Page(3))||Read16(Page(3)+64)>64) return Fail(CpxError.Corrupt);
                if(Read16(Page(3)+64)<30) return Reject(CpxError.ReserveExhausted);
                if(mode==3) { W(Phase,155); return StepResult.Pending; }
                WW(AllocationScan,Wide.From(2097152u>>(int)R(UnitShift))); W(Phase,152); return StepResult.Pending;
            case 152:
                if(Wide.Compare(at,Backup)>=0) return Reject(CpxError.DiskFull);
                Wide group=new(at.High,at.Low&0xffff0000); Wide.Subtract(RW(Units),group,out var remaining);
                if(!group.IsZero&&remaining.High==0&&remaining.Low<18*Q) return Reject(CpxError.DiskFull);
                return Issue(IoKind.Read,Bitmap(at),p,P,153);
            case 153:
                if(!ControlBitmap(p,at,new(at.High,at.Low&0xffff0000))||!ReadableGeneration(p)) return Fail(CpxError.Corrupt);
                W(Phase,154); return StepResult.Pending;
            case 154:
                Wide window=new(at.High,at.Low&~4095u);
                for(uint bit=at.Low&4095;bit<4096;bit++)
                {
                    if((memory.Read8(p+64+(bit>>3))&(0x80>>(int)(bit&7)))!=0||!DataUnit(window+bit)) continue;
                    WW(NewPhysical,window+bit); W(Phase,155); return StepResult.Pending;
                }
                WW(AllocationScan,window+4096); W(Phase,152); return StepResult.Pending;
            case 155:
                if(mode==2)
                { Clear(record+96,160); memory.Write8(record+85,1); Write16(record+88,0); Write16(record+90,0); Clear(WriteOriginal,24); }
                PrepareExtentEdits();
                if(mode!=3&&!AdjustWide(record+16,1,true)) return Fail(CpxError.Corrupt);
                Clear(Page(4),unit);
                if(mode==2) Copy(Page(4),FileInput,GetWide(record+8).Low);
                else
                {
                    Copy(Page(4)+R(DataIntra),R(FileBuffer)+R(FileValue),R(DataTake));
                    Wide end=RW(FilePosition)+R(FileValue)+R(DataTake); if(Wide.Compare(end,GetWide(record+8))>0) PutWide(record+8,end);
                }
                W(Phase,160); return StepResult.Pending;
            case 160: return BeginTx(42,162);
            case 162:
                if(mode is 0 or 3) { W(Phase,170); return StepResult.Pending; }
                return ChangeAllocation(RW(NewPhysical),1,true,170);
            case 167: return SaveObject(record,168);
            case 168: return Issue(IoKind.Write,RW(NewPhysical),Page(4),unit,300);
            case 170:
                if(mode==0) { W(Phase,167); return StepResult.Pending; }
                W(WriteEditCursor,0);
                if(memory.Read8(record+85)==2) { W(Phase,174); return StepResult.Pending; }
                uint count=Read16(record+90); if(memory.Read8(record+85)!=1||count>6) return Fail(CpxError.Corrupt);
                Copy(WriteInlineExtents,record+96,count*24); W(WriteInlineCount,count); W(Phase,171); return StepResult.Pending;
            case 171:
                uint edit=R(WriteEditCursor),piece=WritePieces+edit*24,total=R(WriteInlineCount),slot=0;
                while(slot<total&&Wide.Compare(GetWide(WriteInlineExtents+slot*24),GetWide(piece))<0) slot++;
                if(edit==0&&R(WriteFirstMode)==1)
                {
                    if(slot==total||!Wide.Equal(GetWide(WriteInlineExtents+slot*24),GetWide(piece))) return Fail(CpxError.Corrupt);
                }
                else
                {
                    if(total==8||slot<total&&Wide.Equal(GetWide(WriteInlineExtents+slot*24),GetWide(piece))) return Fail(CpxError.Corrupt);
                    for(uint n=total;n>slot;n--) Copy(WriteInlineExtents+n*24,WriteInlineExtents+(n-1)*24,24);
                    W(WriteInlineCount,total+1);
                }
                Copy(WriteInlineExtents+slot*24,piece,24); W(WriteEditCursor,edit+1);
                if(edit+1<R(WriteEditCount)) return StepResult.Pending;
                if(R(WriteInlineCount)>6) return TakeMetadata(173);
                Clear(record+96,160); Copy(record+96,WriteInlineExtents,R(WriteInlineCount)*24); Write16(record+90,R(WriteInlineCount));
                W(Phase,167); return StepResult.Pending;
            case 173:
                Wide root=RW(MetadataResult); uint leaf=Page(1);
                Header(leaf,15,root,RW(Generation)+1); PutWide(leaf+24,GetWide(record)); Write16(leaf+48,R(WriteInlineCount));
                Write16(leaf+52,R(WriteInlineCount)*24); Copy(leaf+64,WriteInlineExtents,R(WriteInlineCount)*24);
                Clear(record+96,160); Write16(record+90,0); memory.Write8(record+85,2); PutWide(record+24,root);
                return StagePage(leaf,root,167);
            case 174:
                return MutateTree(CpxTreeKind.Extent,GetWide(record+24),GetWide(record),WritePieces+R(WriteEditCursor)*24,R(WriteEditCursor)==0?R(WriteFirstMode):0,175);
            case 175:
                PutWide(record+24,RW(MutationRoot)); W(WriteEditCursor,R(WriteEditCursor)+1);
                W(Phase,R(WriteEditCursor)<R(WriteEditCount)?174u:167u); return StepResult.Pending;
            default: return Fail(CpxError.Corrupt);
        }
    }
    private void PrepareExtentEdits()
    {
        Wide logical=RW(DataLogical),physical=RW(NewPhysical); uint oldCount=memory.Read32(WriteOriginal+16);
        W(WriteFirstMode,0); W(WriteEditCount,1); Extent(WritePieces,logical,physical,1,0);
        if(R(AllocationMode)==3)
        {
            Wide.Subtract(logical,GetWide(WriteOriginal),out var delta); uint left=delta.Low,right=oldCount-left-1,offset=0;
            W(WriteFirstMode,1);
            if(left!=0) { Copy(WritePieces,WriteOriginal,24); memory.Write32(WritePieces+16,left); offset=24; }
            Extent(WritePieces+offset,logical,physical,1,0); offset+=24;
            if(right!=0) { Extent(WritePieces+offset,logical+1,physical+1,right,1); offset+=24; }
            W(WriteEditCount,offset==72?3u:offset==48?2u:1u); return;
        }
        Wide oldPhysical=GetWide(WriteOriginal+8);
        if(oldCount!=0&&oldCount<65536&&memory.Read32(WriteOriginal+20)==0&&Wide.Equal(GetWide(WriteOriginal)+oldCount,logical)&&
            Wide.Equal(oldPhysical+oldCount,physical)&&oldPhysical.High==physical.High&&(oldPhysical.Low>>16)==(physical.Low>>16))
        { Copy(WritePieces,WriteOriginal,24); memory.Write32(WritePieces+16,oldCount+1); W(WriteFirstMode,1); }
    }
}
