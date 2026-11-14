namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    private const uint RecoveryOwnership=500; // bit 0: owned attribute; bit 1: state image; bit 2: OWN1
    private StepResult RecoveryStep()
    {
        uint p=Page(5), phase=R(Phase); Wide tx=RW(Candidate);
        switch(phase)
        {
            case 0: return Issue(IoKind.Read,Anchor+Q,p,P,1);
            case 1:
                W(ValidOne,CheckpointValid(p,Anchor+Q)?1u:0u); WW(CheckpointOne,GetWide(p+80));
                return Issue(IoKind.Read,Anchor+2*Q,p,P,2);
            case 2:
                W(ValidTwo,CheckpointValid(p,Anchor+2*Q)?1u:0u); WW(CheckpointTwo,GetWide(p+80));
                if(R(ValidOne)==0&&R(ValidTwo)==0) return Fail(CpxError.Corrupt);
                Wide a=R(ValidOne)!=0?RW(CheckpointOne):default,b=R(ValidTwo)!=0?RW(CheckpointTwo):default;
                WW(Generation,Wide.Compare(a,b)>=0?a:b); WW(Candidate,default); W(Count,0);
                return Issue(IoKind.Read,Commit(Wide.From(1)),p,P,3);
            case 3:
                if(!InspectCommit(p,Wide.From(1))) return Fail(CpxError.Corrupt);
                return Issue(IoKind.Read,Commit(Wide.From(2)),p,P,4);
            case 4:
                if(!InspectCommit(p,Wide.From(2))) return Fail(CpxError.Corrupt);
                tx=RW(Candidate);
                if(tx.IsZero) { W(Phase,20); return StepResult.Pending; }
                if(R(ReadOnlyAt)!=0) return Fail(CpxError.ReadOnly);
                return Issue(IoKind.Read,Journal(tx),p,P,5);
            case 5:
                if(!Control(p,8,Journal(tx))||!IdentityMatches(p)||!Wide.Equal(GetWide(p+32),tx)||!Wide.Equal(GetWide(p+80),tx)||
                    !Wide.Equal(GetWide(p+88),RW(Generation))||memory.Read32(p+96)!=R(Count)||memory.Read32(p+100)!=R(Digest)||Read16(p+52)!=40) return Fail(CpxError.Corrupt);
                Clear(Index,2048); W(Cursor,0); return Issue(IoKind.Read,Descriptor(tx,0),p,P,6);
            case 6:
                uint page=R(Cursor), first=page*((P-64)>>5), take=first<R(Count)?R(Count)-first:0;
                if(take>((P-64)>>5)) take=(P-64)>>5;
                if(!Control(p,9,Descriptor(tx,page))||!Wide.Equal(GetWide(p+32),tx)||Read16(p+48)!=take||Read16(p+52)!=take*32) return Fail(CpxError.Corrupt);
                for(uint n=0;n<take*32;n++) memory.Write8(Index+first*32+n,memory.Read8(p+64+n));
                if(page<3) { W(Cursor,page+1); return Issue(IoKind.Read,Descriptor(tx,page+1),p,P,6); }
                W(Phase,7); return StepResult.Pending;
            case 7:
                if(Crc(Index,R(Count)*32,false)!=R(Digest)) return Fail(CpxError.Corrupt);
                W(RecoveryOwnership,0); W(Cursor,0); W(Phase,8); return StepResult.Pending;
            case 8:
                uint index=R(Cursor), d=Index+index*32; Wide home=GetWide(d);
                if(!TargetValid(home,Read16(d+24),GetWide(d+8))||memory.Read32(d+16)!=index||Read16(d+26)!=0||memory.Read32(d+28)!=0) return Fail(CpxError.Corrupt);
                for(uint n=0;n<index;n++) if(Wide.Equal(GetWide(Index+n*32),home)) return Fail(CpxError.Corrupt);
                return Issue(IoKind.Read,Image(tx,index),p,P,9);
            case 9:
                if(!ImageValid(p,tx,R(Cursor))) return Fail(CpxError.Corrupt);
                if(Read16(p+4)==18&&Read16(p+64)==2) W(RecoveryOwnership,R(RecoveryOwnership)|1);
                if(Read16(p+4)==3) W(RecoveryOwnership,R(RecoveryOwnership)|2|(Read16(p+52)==136?4u:0u));
                if(R(Cursor)+1<R(Count)) { W(Cursor,R(Cursor)+1); W(Phase,8); return StepResult.Pending; }
                if((R(RecoveryOwnership)&1)!=0)
                {
                    if((R(RecoveryOwnership)&2)==0) return Issue(IoKind.Read,StatePageAddress,p,P,14);
                    if((R(RecoveryOwnership)&4)==0) return Fail(CpxError.Corrupt);
                }
                W(Cursor,0); W(Phase,10); return StepResult.Pending;
            case 10: return Issue(IoKind.Read,Image(tx,R(Cursor)),p,P,11);
            case 11:
                if(!ImageValid(p,tx,R(Cursor))) return Fail(CpxError.Corrupt);
                return Issue(IoKind.Write,GetWide(Index+R(Cursor)*32),p,P,12);
            case 12:
                if(R(Cursor)+1<R(Count)) { W(Cursor,R(Cursor)+1); W(Phase,10); return StepResult.Pending; }
                return Issue(IoKind.Flush,default,0,0,13);
            case 13: WW(Generation,tx); W(Phase,20); return StepResult.Pending;
            case 14:
                if(!Control(p,3,StatePageAddress)||!ValidStateLabel(p)||Read16(p+52)!=136||Wide.Compare(GetWide(p+32),RW(Generation))>0) return Fail(CpxError.Corrupt);
                W(Cursor,0); W(Phase,10); return StepResult.Pending;
            case 20:
                if(R(ReadOnlyAt)!=0 || (tx.IsZero&&R(ValidOne)!=0&&Wide.Equal(RW(CheckpointOne),RW(Generation)))) { W(Phase,22); return StepResult.Pending; }
                MakeCheckpoint(p,Anchor+Q); return Issue(IoKind.Write,Anchor+Q,p,P,21);
            case 21: return Issue(IoKind.Flush,default,0,0,22);
            case 22:
                if(R(ReadOnlyAt)!=0 || (tx.IsZero&&R(ValidTwo)!=0&&Wide.Equal(RW(CheckpointTwo),RW(Generation)))) { W(Phase,24); return StepResult.Pending; }
                MakeCheckpoint(p,Anchor+2*Q); return Issue(IoKind.Write,Anchor+2*Q,p,P,23);
            case 23: return Issue(IoKind.Flush,default,0,0,24);
            case 24: return Issue(IoKind.Read,Anchor+4*Q,p,P,25);
            case 25:
                if(!Control(p,5,Anchor+4*Q)||memory.Read32(p+64)!=1||Read16(p+52)!=4) return Fail(CpxError.Corrupt);
                return Issue(IoKind.Read,Anchor+3*Q,p,P,26);
            case 26:
                if(!Control(p,3,Anchor+3*Q)||Wide.Compare(GetWide(p+64),Wide.From(2))<0||GetWide(p+72).IsZero||!Allocatable(GetWide(p+80))||!ValidStateLabel(p)) return Fail(CpxError.Corrupt);
                CacheStateLabel(p);
                return Issue(IoKind.Read,Anchor+8*Q,Page(3),P,27);
            case 27:
                uint reserve=Page(3);
                if(!Control(reserve,4,Anchor+8*Q)||Read16(reserve+64)>64||Read16(reserve+52)!=520) return Fail(CpxError.Corrupt);
                W(Count,Read16(reserve+64)); W(Cursor,0); W(Phase,28); return StepResult.Pending;
            case 28:
                if(R(Cursor)==R(Count)) { W(Mounted,1); W(Poisoned,0); Clear(Index,2048); return R(ReadOnlyAt)!=0?Done():Call(CpxOperation.Reclaim,30); }
                Wide address=GetWide(Page(3)+72+R(Cursor)*8);
                if(!Allocatable(address)) return Fail(CpxError.Corrupt);
                for(uint n=0;n<R(Cursor);n++) if(Wide.Equal(address,GetWide(Page(3)+72+n*8))) return Fail(CpxError.Corrupt);
                WW(Target,address); return Issue(IoKind.Read,Bitmap(address),p,P,29);
            case 29:
                Wide at=RW(Target), group=new(at.High,at.Low&0xffff0000);
                if(!ControlBitmap(p,at,group)) return Fail(CpxError.Corrupt);
                uint bit=at.Low&4095;
                for(uint n=0;n<Q;n++) if((memory.Read8(p+64+((bit+n)>>3))&(0x80>>((int)(bit+n)&7)))==0) return Fail(CpxError.Corrupt);
                W(Cursor,R(Cursor)+1); W(Phase,28); return StepResult.Pending;
            case 30: return Call(CpxOperation.PruneObjects,31);
            case 31: return Done();
            default: return Fail(CpxError.Corrupt);
        }
    }
    private Wide Bitmap(Wide at)
    {
        Wide group=new(at.High,at.Low&0xffff0000);
        return (group.IsZero?Wide.From(131072u>>(int)R(UnitShift)):group)+(1+((at.Low&65535)>>12))*Q;
    }
    private bool ControlBitmap(uint p,Wide at,Wide owner) => ValidPage(p,Bitmap(at))&&Read16(p+4)==7&&Wide.Equal(GetWide(p+24),owner)&&memory.Read32(p+12)==((at.Low&65535)>>12)&&Read16(p+52)==512;
    private bool CheckpointValid(uint p,Wide at) => Control(p,2,at)&&IdentityMatches(p)&&Wide.Equal(GetWide(p+32),GetWide(p+80))&&Read16(p+52)==24;
    private void MakeCheckpoint(uint p,Wide at)
    { Header(p,2,at,RW(Generation)); PutWide(p+72,RW(Identity)); PutWide(p+80,RW(Generation)); Seal(p,24); }
    private bool InspectCommit(uint p,Wide slot)
    {
        // A torn/unpublished commit is ignored; a valid-CRC inconsistent commit is fatal.
        if(memory.Read32(p+8)!=Crc(p,P,true)) return true;
        if(memory.Read32(p)!=CpxArena.Magic||Read16(p+6)!=4||!Wide.Equal(GetWide(p+16),Commit(slot))||
            Read16(p+4)!=10||!GetWide(p+24).IsZero||!IdentityMatches(p)||Read16(p+52)!=40) return false;
        Wide tx=GetWide(p+80);
        if(!Wide.Equal(tx,GetWide(p+32))||!Wide.Equal(Commit(tx),Commit(slot))) return false;
        if(Wide.Compare(tx,RW(Generation))<=0) return true;
        if(!Wide.Add(RW(Generation),Wide.From(1),out var next)||!Wide.Equal(tx,next)||!Wide.Equal(GetWide(p+88),RW(Generation))||
            !RW(Candidate).IsZero||memory.Read32(p+96)==0||memory.Read32(p+96)>64) return false;
        WW(Candidate,tx); W(Count,memory.Read32(p+96)); W(Digest,memory.Read32(p+100)); return true;
    }
    private bool ImageValid(uint p,Wide tx,uint index)
    {
        uint d=Index+index*32;
        return ValidPage(p,GetWide(d))&&Read16(p+4)==Read16(d+24)&&Wide.Equal(GetWide(p+24),GetWide(d+8))&&
            Wide.Equal(GetWide(p+32),tx)&&memory.Read32(p+8)==memory.Read32(d+20)&&PayloadValid(p);
    }
    private bool PayloadValid(uint p)
    {
        uint type=Read16(p+4), count=Read16(p+48), payload;
        switch(type)
        {
            case 3: return ValidStateLabel(p); case 4: payload=520; break; case 6: payload=80; break; case 7: payload=512; break;
            case 11: payload=count*256; break; case 12: case 16: payload=count*16; break;
            case 13: payload=count*128; break; case 14: case 15: payload=count*24; break;
            case 17: case 19:
                if(Read16(p+64)==0||Read16(p+64)>768||Read16(p+66)!=0) return false;
                payload=4+Read16(p+64); break;
            case 18:
                return AttributePayloadValid(p);
            default: return false;
        }
        return payload<=P-64 && Read16(p+52)==payload;
    }
    private bool TargetValid(Wide address,uint type,Wide owner)
    {
        if(!PageAddress(address)) return false;
        if(type==3) return owner.IsZero&&Wide.Equal(address,Anchor+3*Q);
        if(type==4) return owner.IsZero&&Wide.Equal(address,Anchor+8*Q);
        if(type==6||type==7)
        {
            if((owner.Low&65535)!=0||Wide.Compare(owner,RW(Units))>=0) return false;
            Wide.Subtract(RW(Units),owner,out var remaining);
            if(!owner.IsZero&&remaining.High==0&&remaining.Low<65536&&remaining.Low<18*Q) return false;
            Wide descriptor=owner.IsZero?Wide.From(131072u>>(int)R(UnitShift)):owner;
            if(type==6) return Wide.Equal(address,descriptor);
            for(uint n=1;n<=16;n++) if(Wide.Equal(address,descriptor+n*Q)) return true;
            return false;
        }
        return type>=11&&type<=19&&((type is 11 or 12)?owner.IsZero:!owner.IsZero)&&Allocatable(address);
    }
}
