namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    private const uint FormatOffset=15600,FormatGroup=15608,FormatMap=15616,FormatGroupFree=15620,FormatFree=15624;
    /// <summary>Destructive formatting of caller-owned, quiescent media. A zero
    /// label length retains the byte-identical legacy unlabeled representation.</summary>
    public CpxError BeginFormat(uint sectorBytes,uint allocationBytes,Wide capacitySectors,Wide volumeId,uint epoch,uint latin1=0,uint length=0)
    {
        if(volumeId.IsZero) return CpxError.InvalidArgument;
        if(R(Signature)==CpxArena.Magic&&(R(Waiting)!=0||R(Finished)==0||R(Active)!=0)) return CpxError.Busy;
        if(R(Signature)==CpxArena.Magic&&IsReadOnly) return CpxError.ReadOnly;
        if(length!=0&&!ValidLabelInput(latin1,length)) return CpxError.InvalidArgument;
        Copy(LabelInput,latin1,length); W(LabelLength,length);
        CpxError error=Initialize(sectorBytes,allocationBytes,capacitySectors,epoch); if(error!=CpxError.None) return error;
        WW(Identity,volumeId); return StartOperation(CpxOperation.Format);
    }
    private Wide FormatDescriptor => RW(FormatGroup).IsZero?Wide.From(131072u>>(int)R(UnitShift)):RW(FormatGroup);
    private StepResult FormatStep()
    {
        uint p=Page(0),descriptor=Page(1); Wide root=Wide.From(2097152u>>(int)R(UnitShift)),group=RW(FormatGroup);
        switch(R(Phase))
        {
            // Invalidate every possible prior primary/backup page position.
            // Until this barrier only anchor bytes are changed, so an old valid
            // backup still describes an untouched old filesystem.
            case 0: Clear(p,P); W(FormatOffset,0); W(Phase,1); return StepResult.Pending;
            case 1: return Issue(IoKind.Write,Anchor+(R(FormatOffset)>>(int)R(UnitShift)),p,P,2);
            case 2: return Issue(IoKind.Write,RW(Units)-((4096-R(FormatOffset))>>(int)R(UnitShift)),p,P,3);
            case 3:
                W(FormatOffset,R(FormatOffset)+P); if(R(FormatOffset)<4096) { W(Phase,1); return StepResult.Pending; }
                return Issue(IoKind.Flush,default,0,0,4);
            case 4: WW(FormatGroup,default); WW(FormatFree,default); W(Phase,5); return StepResult.Pending;
            case 5:
                if(Wide.Compare(group,RW(Units))>=0) { W(Phase,20); return StepResult.Pending; }
                Wide.Subtract(RW(Units),group,out var remaining);
                if(!group.IsZero&&remaining.High==0&&remaining.Low<18*Q) { W(Phase,20); return StepResult.Pending; }
                Header(descriptor,6,FormatDescriptor,default); PutWide(descriptor+24,group);
                memory.Write32(descriptor+64,remaining.High!=0||remaining.Low>=65536?65536u:remaining.Low);
                W(FormatMap,0); W(FormatGroupFree,0); W(Phase,6); return StepResult.Pending;
            case 6:
                Wide mapAddress=FormatDescriptor+(R(FormatMap)+1)*Q;
                Header(p,7,mapAddress,default); PutWide(p+24,group); memory.Write32(p+12,R(FormatMap)); W(Phase,7); return StepResult.Pending;
            case 7:
                uint free=0; Wide start=group+R(FormatMap)*4096;
                for(uint bit=0;bit<4096;bit++)
                {
                    Wide unit=start+bit; bool used=!DataUnit(unit)||(Wide.Compare(unit,root)>=0&&Wide.Compare(unit,root+33*Q)<0);
                    if(used) { uint at=p+64+(bit>>3); memory.Write8(at,(byte)(memory.Read8(at)|(0x80u>>(int)(bit&7)))); }
                    else free++;
                }
                memory.Write32(descriptor+80+R(FormatMap)*4,free); W(FormatGroupFree,R(FormatGroupFree)+free); W(Phase,8); return StepResult.Pending;
            case 8: Seal(p,512); return Issue(IoKind.Write,FormatDescriptor+(R(FormatMap)+1)*Q,p,P,9);
            case 9:
                W(FormatMap,R(FormatMap)+1); if(R(FormatMap)<16) { W(Phase,6); return StepResult.Pending; }
                memory.Write32(descriptor+68,R(FormatGroupFree)); WW(FormatFree,RW(FormatFree)+R(FormatGroupFree)); W(Phase,10); return StepResult.Pending;
            case 10: Seal(descriptor,80); return Issue(IoKind.Write,FormatDescriptor,descriptor,P,11);
            case 11: WW(FormatGroup,group+65536); W(Phase,5); return StepResult.Pending;
            case 20:
                Header(p,11,root,default); Write16(p+48,1); PutWide(p+64,Wide.From(1));
                memory.Write32(p+64+76,1); memory.Write32(p+64+80,1); memory.Write8(p+64+84,2);
                Seal(p,256); return Issue(IoKind.Write,root,p,P,21);
            case 21:
                Header(p,3,Anchor+3*Q,default); PutWide(p+64,Wide.From(2)); PutWide(p+72,Wide.From(1)); PutWide(p+80,root); PutWide(p+96,RW(FormatFree));
                if(R(LabelLength)!=0) EncodeStateLabel(p);
                Seal(p,R(LabelLength)==0?48u:128u); return Issue(IoKind.Write,Anchor+3*Q,p,P,22);
            case 22:
                Header(p,4,Anchor+8*Q,default); Write16(p+64,32);
                for(uint n=0;n<32;n++) PutWide(p+72+n*8,root+(n+1)*Q);
                Seal(p,520); return Issue(IoKind.Write,Anchor+8*Q,p,P,23);
            case 23:
                Header(p,5,Anchor+4*Q,default); memory.Write32(p+64,1); Seal(p,4); return Issue(IoKind.Write,Anchor+4*Q,p,P,24);
            case 24: Clear(p,P); return Issue(IoKind.Write,Commit(Wide.From(1)),p,P,25);
            case 25: return Issue(IoKind.Write,Commit(Wide.From(2)),p,P,26);
            case 26: MakeCheckpoint(p,Anchor+Q); return Issue(IoKind.Write,Anchor+Q,p,P,27);
            case 27: MakeCheckpoint(p,Anchor+2*Q); return Issue(IoKind.Write,Anchor+2*Q,p,P,28);
            case 28: return Issue(IoKind.Flush,default,0,0,29);
            case 29: FormatAnchor(p,Backup); return Issue(IoKind.Write,Backup,p,P,30);
            case 30: return Issue(IoKind.Flush,default,0,0,31);
            case 31: FormatAnchor(p,Anchor); return Issue(IoKind.Write,Anchor,p,P,32);
            case 32: return Issue(IoKind.Flush,default,0,0,33);
            case 33: if(R(LabelLength)!=0) CacheInputLabel(); W(Mounted,1); return Done();
            default: return Fail(CpxError.Corrupt);
        }
    }
    private void FormatAnchor(uint p,Wide at)
    {
        Header(p,1,at,default); PutWide(p+72,RW(Identity)); memory.Write32(p+80,1u<<(int)R(UnitShift)); memory.Write32(p+84,P);
        PutWide(p+88,RW(Units)); PutWide(p+96,Wide.From(1)); memory.Write32(p+104,1u<<(int)R(SectorShift)); memory.Write32(p+108,1);
        PutWide(p+112,Anchor+8*Q); PutWide(p+120,Anchor+3*Q); Seal(p,64);
    }
}
