namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    private const uint CommentId=15040,CommentPage=15048,CommentLength=15056,CommentScan=15060,CommentEncoded=15064,
        CommentOwner=15068,OwnerInput=15072;
    private uint CommentInput=>Arena+15104;
    private uint CommentObject=>Arena+15264;
    public CpxError BeginOwner(Wide id,uint ownerInfo)
    {
        CpxError error=StartOperation(CpxOperation.SetOwner); if(error!=CpxError.None) return error;
        WW(CommentId,id); W(OwnerInput,ownerInfo); W(CommentLength,0); return error;
    }
    public uint AttributeOwnerInfo(uint p)=>Read16(p+64)==2?memory.Read32(p+68):0;
    public uint AttributeCommentBytes(uint p)=>Read16(p+(Read16(p+64)==2?74u:70u));
    public uint AttributeCommentData(uint p)=>p+(Read16(p+64)==2?76u:72u);
    public bool ValidAttribute(uint p,Wide id)=>Read16(p+4)==18&&Wide.Equal(GetWide(p+24),id)&&PayloadValid(p)&&
        (Read16(p+64)==1||OwnershipEnabled);
    private bool AttributePayloadValid(uint p)
    {
        uint version=Read16(p+64),length=AttributeCommentBytes(p);
        if(Read16(p+66)!=0||length>158) return false;
        if(version==1) return length!=0&&Read16(p+68)==1&&Read16(p+52)==8+length;
        return version==2&&memory.Read32(p+68)!=0&&Read16(p+72)==1&&Read16(p+52)==12+length;
    }
    public CpxError BeginComment(Wide id,uint latin1,uint length)
    {
        if(length>79||latin1>uint.MaxValue-length||(length!=0&&latin1==0)) return CpxError.InvalidArgument;
        for(uint n=0;n<length;n++) if(memory.Read8(latin1+n)==0) return CpxError.InvalidArgument;
        CpxError error=StartOperation(CpxOperation.SetComment); if(error!=CpxError.None) return error;
        WW(CommentId,id); W(CommentLength,length); Copy(CommentInput,latin1,length); return error;
    }
    private StepResult CommentStep()
    {
        uint obj=CommentObject,p=Page(4); Wide page=RW(CommentPage); bool owner=(CpxOperation)R(OperationAt)==CpxOperation.SetOwner;
        switch(R(Phase))
        {
            case 0: return LoadObject(RW(CommentId),1);
            case 1:
                if(R(SourcePage)==0||memory.Read32(ObjectRecord+76)==0) return Reject(CpxError.NotFound);
                Copy(obj,ObjectRecord,256); WW(CommentPage,GetWide(obj+48));
                W(CommentOwner,owner?R(OwnerInput):0);
                if(!GetWide(obj+48).IsZero) return ReadStaged(GetWide(obj+48),p,2);
                if(R(CommentLength)==0&&R(CommentOwner)==0) return Done();
                return EnsureReserve(13,3); // One attribute page plus the 12-page margin.
            case 2:
                if(!ValidPage(p,page)||!ReadableGeneration(p)||!ValidAttribute(p,RW(CommentId))) return Fail(CpxError.Corrupt);
                if(owner)
                {
                    if(AttributeOwnerInfo(p)==R(OwnerInput)) return Done();
                    W(CommentLength,AttributeCommentBytes(p)); Copy(CommentInput,AttributeCommentData(p),R(CommentLength));
                }
                else W(CommentOwner,AttributeOwnerInfo(p));
                W(Phase,3); return StepResult.Pending;
            case 3: return BeginTx(6,20);
            case 20:
                if(R(CommentOwner)!=0&&!OwnershipEnabled) return ReadStaged(StatePageAddress,p,21);
                W(Phase,4); return StepResult.Pending;
            case 21:
                if(!Control(p,3,StatePageAddress)||!ReadableGeneration(p)||!ValidStateLabel(p)) return Fail(CpxError.Corrupt);
                if(Read16(p+52)==48)
                {
                    W(LabelLength,memory.Read8(VolumeLabelAddress)); Copy(LabelInput,VolumeLabelAddress+1,R(LabelLength)); EncodeStateLabel(p);
                }
                Write16(p+52,136); memory.Write32(p+192,0x4f574e31); memory.Write32(p+196,1);
                return StagePage(p,StatePageAddress,4);
            case 4:
                if(R(CommentLength)==0&&R(CommentOwner)==0) { PutWide(obj+48,default); return ReleaseMetadata(page,9); }
                if(page.IsZero) return TakeMetadata(5);
                W(Phase,6); return StepResult.Pending;
            case 5: WW(CommentPage,RW(MetadataResult)); W(Phase,6); return StepResult.Pending;
            case 6:
                Header(p,18,page,RW(Generation)+1); PutWide(p+24,RW(CommentId));
                Write16(p+64,R(CommentOwner)==0?1u:2u);
                if(R(CommentOwner)!=0) memory.Write32(p+68,R(CommentOwner));
                Write16(p+(R(CommentOwner)==0?68u:72u),1); W(CommentScan,0); W(CommentEncoded,0); W(Phase,7); return StepResult.Pending;
            case 7:
                uint scan=R(CommentScan),encoded=R(CommentEncoded),end=scan+64; if(end>R(CommentLength)) end=R(CommentLength);
                uint data=AttributeCommentData(p);
                for(;scan<end;scan++)
                {
                    uint c=memory.Read8(CommentInput+scan);
                    if(!owner&&c>=128) { memory.Write8(data+encoded++,(byte)(0xc0|(c>>6))); c=0x80|(c&63); }
                    memory.Write8(data+encoded++,(byte)c);
                }
                W(CommentScan,scan); W(CommentEncoded,encoded); if(scan<R(CommentLength)) return StepResult.Pending;
                Write16(data-2,encoded); Write16(p+52,data-p-64+encoded); PutWide(obj+48,page); return StagePage(p,page,9);
            case 9: return SaveObject(obj,10);
            case 10: return Call(CpxOperation.CommitTransaction,11);
            case 11: if(R(CommentOwner)!=0) W(OwnershipFeature,1); return Done();
            default: return Fail(CpxError.Corrupt);
        }
    }
}
