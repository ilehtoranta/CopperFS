namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    private const uint NsParent=10600,NsDestination=10608,NsLength=10616,NsNewLength=10620,NsDirectory=10624,NsReplace=10628,
        NsReplacing=10632,NsResult=10640,NsEntryId=10648,NsRoot=10656,NsAncestor=10664,NsHops=10672,
        NsNameScan=10680,NsEncodedLength=10684,NsHash=10688,NsNameReturn=10692,NsPublished=10696;
    private uint NsParentRecord=>Arena+10800;
    private uint NsDestinationRecord=>Arena+11056;
    private uint NsObject=>Arena+11312;
    private uint NsVictim=>Arena+11568;
    private uint NsEntry=>Arena+11824;
    private uint NsOldEntry=>Arena+11952;
    private uint NsPrevious=>Arena+12080;
    private uint NsName=>Arena+12208;
    private uint NsNewName=>Arena+12464;
    private uint NsEncoded=>Arena+12720;
    public Wide MutatedObject=>RW(NsResult);
    public bool NamespacePublished=>R(NsPublished)!=0;
    private bool ValidComponent(uint name,uint length,bool creating)
    {
        if(name==0||length==0||length>255||name>uint.MaxValue-length) return false;
        if(length<=2&&memory.Read8(name)==46&&(length==1||memory.Read8(name+1)==46)) return false;
        for(uint n=0;n<length;n++) if(memory.Read8(name+n) is 0 or 47 or 58) return false;
        if(creating&&length==19&&memory.Read8(name)==95&&memory.Read8(name+1)==95&&FoldLatin(memory.Read8(name+2))==117)
        {
            bool alias=true; for(uint n=3;n<19;n++) { uint c=FoldLatin(memory.Read8(name+n)); if(!(c is >=48 and <=57 or >=97 and <=102)) alias=false; }
            if(alias) return false;
        }
        return true;
    }
    public CpxError BeginCreate(Wide parent,uint name,uint length,bool directory=false)
    {
        if(!ValidComponent(name,length,true)) return CpxError.InvalidArgument;
        CpxError error=StartOperation(CpxOperation.Create); if(error!=CpxError.None) return error;
        WW(NsParent,parent); W(NsLength,length); W(NsDirectory,directory?1u:0u); WW(NsResult,default); Copy(NsName,name,length); return error;
    }
    public CpxError BeginDelete(Wide parent,uint name,uint length)
    {
        if(!ValidComponent(name,length,false)) return CpxError.InvalidArgument;
        CpxError error=StartOperation(CpxOperation.Delete); if(error!=CpxError.None) return error;
        WW(NsParent,parent); W(NsLength,length); WW(NsResult,default); Copy(NsName,name,length); return error;
    }
    public CpxError BeginRename(Wide parent,uint name,uint length,Wide destination,uint newName,uint newLength,bool replace=false)
    {
        if(!ValidComponent(name,length,false)||!ValidComponent(newName,newLength,true)) return CpxError.InvalidArgument;
        CpxError error=StartOperation(CpxOperation.Rename); if(error!=CpxError.None) return error;
        WW(NsParent,parent); WW(NsDestination,destination); W(NsLength,length); W(NsNewLength,newLength);
        W(NsReplace,replace?1u:0u); W(NsReplacing,0); WW(NsResult,default); Copy(NsName,name,length); Copy(NsNewName,newName,newLength); return error;
    }
    private StepResult LookupName(Wide parent,uint name,uint length,uint next)
    {
        WW(ObjectKey,parent); W(NameLength,length); W(LookupFoundAt,0); WW(LookupTarget,default); Copy(LegacyName,name,length);
        return Call(CpxOperation.Lookup,next);
    }
    private StepResult LoadObject(Wide id,uint next)
    { WW(ObjectKey,id); return Call(CpxOperation.ReadObject,next); }
    private StepResult BuildEntry(uint next)
    {
        W(NsNameScan,0); W(NsEncodedLength,0); W(NsHash,uint.MaxValue); W(NsNameReturn,next); W(Phase,80); return StepResult.Pending;
    }
    private uint HashByte(uint crc,uint value)
    { crc^=value; for(uint n=0;n<8;n++) crc=(crc>>1)^(0x82f63b78u&(0u-(crc&1))); return crc; }
    private bool AdjustWide(uint address,uint count,bool add)
    {
        Wide result; bool ok=add?Wide.Add(GetWide(address),Wide.From(count),out result):Wide.Subtract(GetWide(address),Wide.From(count),out result);
        if(ok) PutWide(address,result); return ok;
    }
    private StepResult NamespaceStep()
    {
        uint phase=R(Phase),p=Page(3); CpxOperation op=(CpxOperation)R(OperationAt);
        switch(phase)
        {
            case 0:
                if(op==CpxOperation.Create&&R(NsDirectory)==3) return Call(CpxOperation.EncodeSymlink,100);
                return Call(CpxOperation.Reclaim,1);
            case 1: return LookupName(RW(NsParent),NsName,R(NsLength),2);
            case 2:
                Copy(NsParentRecord,ObjectRecord,256);
                if((memory.Read32(NsParentRecord+72)&4)!=0) return Reject(CpxError.Protected);
                if(op==CpxOperation.Create)
                { if(LookupFound) return Reject(CpxError.AlreadyExists); return EnsureReserve(32,10); }
                if(!LookupFound) return Reject(CpxError.NotFound);
                Copy(NsOldEntry,TreeRecord,128); WW(NsResult,LookupId); return LoadObject(LookupId,3);
            case 3:
                if(R(SourcePage)==0) return Fail(CpxError.Corrupt);
                Copy(NsObject,ObjectRecord,256);
                if((memory.Read32(NsObject+72)&1)!=0) return Reject(CpxError.Protected);
                if(op==CpxOperation.Delete)
                {
                    if(memory.Read8(NsObject+84)==2&&!GetWide(NsObject+8).IsZero) return Reject(CpxError.DirectoryNotEmpty);
                    return BeginTx(7,30);
                }
                return LookupName(RW(NsDestination),NsNewName,R(NsNewLength),4);
            case 4:
                Copy(NsDestinationRecord,ObjectRecord,256);
                if((memory.Read32(NsDestinationRecord+72)&4)!=0) return Reject(CpxError.Protected);
                if(LookupFound&&!Wide.Equal(GetWide(TreeRecord+8),GetWide(NsOldEntry+8)))
                {
                    if(R(NsReplace)==0) return Reject(CpxError.AlreadyExists);
                    if(Wide.Equal(LookupId,RW(NsResult))) return Done();
                    Copy(NsPrevious,TreeRecord,128); W(NsReplacing,1); return LoadObject(LookupId,5);
                }
                W(Phase,6); return StepResult.Pending;
            case 5:
                if(R(SourcePage)==0) return Fail(CpxError.Corrupt);
                Copy(NsVictim,ObjectRecord,256);
                if((memory.Read32(NsVictim+72)&1)!=0) return Reject(CpxError.Protected);
                if(memory.Read8(NsVictim+84)!=memory.Read8(NsObject+84)) return Reject(CpxError.WrongType);
                if(memory.Read8(NsVictim+84)==2&&!GetWide(NsVictim+8).IsZero) return Reject(CpxError.DirectoryNotEmpty);
                W(Phase,6); return StepResult.Pending;
            case 6:
                if(memory.Read8(NsObject+84)!=2) return EnsureReserve(10,40);
                WW(NsAncestor,RW(NsDestination)); WW(NsHops,default); W(Phase,7); return StepResult.Pending;
            case 7:
                if(Wide.Equal(RW(NsAncestor),RW(NsResult))) return Reject(CpxError.InvalidArgument);
                if(RW(NsAncestor).IsZero) return EnsureReserve(10,40);
                return LoadObject(RW(NsAncestor),8);
            case 8:
                if(R(SourcePage)==0||memory.Read8(ObjectRecord+84)!=2) return Fail(CpxError.Corrupt);
                WW(NsAncestor,GetWide(ObjectRecord+40)); WW(NsHops,RW(NsHops)+1);
                return ReadStaged(Anchor+3*Q,p,9);
            case 9:
                if(!Control(p,3,Anchor+3*Q)||Wide.Compare(RW(NsHops),GetWide(p+64))>=0) return Fail(CpxError.Corrupt);
                W(Phase,7); return StepResult.Pending;
            case 10: return BeginTx(39,11);
            case 11: return ReadStaged(Anchor+3*Q,p,12);
            case 12:
                if(!Control(p,3,Anchor+3*Q)||!ReadableGeneration(p)) return Fail(CpxError.Corrupt);
                WW(NsResult,GetWide(p+64)); WW(NsEntryId,GetWide(p+72));
                if(!AdjustWide(p+64,1,true)||!AdjustWide(p+72,1,true)||!AdjustWide(p+104,1,true)) return Reject(CpxError.ResourceExhausted);
                Clear(NsObject,256); PutWide(NsObject,RW(NsResult)); memory.Write32(NsObject+76,1); memory.Write32(NsObject+80,1);
                memory.Write8(NsObject+84,(byte)(R(NsDirectory)==3?3:R(NsDirectory)!=0?2:1)); if(R(NsDirectory)==1) PutWide(NsObject+40,RW(NsParent));
                return StagePage(p,Anchor+3*Q,13);
            case 13:
                if(R(NsDirectory)==3)
                {
                    PutWide(NsObject+8,Wide.From(R(LinkEncoded)));
                    if(R(LinkEncoded)>160) return TakeMetadata(102);
                    Write16(NsObject+88,R(LinkEncoded)); Copy(NsObject+96,LinkText,R(LinkEncoded));
                }
                return BuildEntry(14);
            case 14: return MutateTree(CpxTreeKind.Directory,GetWide(NsParentRecord+32),RW(NsParent),NsEntry,0,15);
            case 15:
                PutWide(NsParentRecord+32,RW(MutationRoot)); if(!AdjustWide(NsParentRecord+8,1,true)) return Fail(CpxError.Corrupt);
                return SaveObject(NsParentRecord,16);
            case 16: return ReadStaged(Anchor+3*Q,p,17);
            case 17:
                if(!Control(p,3,Anchor+3*Q)) return Fail(CpxError.Corrupt);
                return MutateTree(CpxTreeKind.Object,GetWide(p+80),default,NsObject,0,18);
            case 18: WW(NsRoot,RW(MutationRoot)); return ReadStaged(Anchor+3*Q,p,19);
            case 19:
                if(!Control(p,3,Anchor+3*Q)) return Fail(CpxError.Corrupt);
                PutWide(p+80,RW(NsRoot)); return StagePage(p,Anchor+3*Q,20);
            case 20: return Call(CpxOperation.CommitTransaction,99);
            case 30: return MutateTree(CpxTreeKind.Directory,GetWide(NsParentRecord+32),RW(NsParent),NsOldEntry,2,31);
            case 31:
                if(!AdjustWide(NsParentRecord+8,1,false)) return Fail(CpxError.Corrupt);
                return SaveObject(NsParentRecord,32);
            case 32:
                if(!GetWide(NsOldEntry+24).IsZero) return ReleaseMetadata(GetWide(NsOldEntry+24),33);
                W(Phase,33); return StepResult.Pending;
            case 33: Copy(NsVictim,NsObject,256); W(Phase,60); return StepResult.Pending;
            case 40: return BeginTx(30,41);
            case 41: WW(NsEntryId,GetWide(NsOldEntry+8)); return BuildEntry(42);
            case 42: return MutateTree(CpxTreeKind.Directory,GetWide(NsParentRecord+32),RW(NsParent),NsOldEntry,2,43);
            case 43:
                if(R(NsReplacing)!=0) return MutateTree(CpxTreeKind.Directory,GetWide(NsDestinationRecord+32),RW(NsDestination),NsPrevious,2,44);
                W(Phase,44); return StepResult.Pending;
            case 44: return MutateTree(CpxTreeKind.Directory,GetWide(NsDestinationRecord+32),RW(NsDestination),NsEntry,0,45);
            case 45:
                PutWide(NsDestinationRecord+32,RW(MutationRoot));
                if(!Wide.Equal(RW(NsParent),RW(NsDestination))) { if(!AdjustWide(NsDestinationRecord+8,1,true)) return Fail(CpxError.Corrupt); }
                if(R(NsReplacing)!=0&&!AdjustWide(NsDestinationRecord+8,1,false)) return Fail(CpxError.Corrupt);
                return SaveObject(NsDestinationRecord,46);
            case 46:
                if(Wide.Equal(RW(NsParent),RW(NsDestination))) { W(Phase,48); return StepResult.Pending; }
                if(!AdjustWide(NsParentRecord+8,1,false)) return Fail(CpxError.Corrupt);
                return SaveObject(NsParentRecord,47);
            case 47:
                if(memory.Read8(NsObject+84)==2) { PutWide(NsObject+40,RW(NsDestination)); return SaveObject(NsObject,48); }
                W(Phase,48); return StepResult.Pending;
            case 48:
                if(!GetWide(NsOldEntry+24).IsZero) return ReleaseMetadata(GetWide(NsOldEntry+24),49);
                W(Phase,49); return StepResult.Pending;
            case 49:
                if(R(NsReplacing)==0) return Call(CpxOperation.CommitTransaction,99);
                if(!GetWide(NsPrevious+24).IsZero) return ReleaseMetadata(GetWide(NsPrevious+24),60);
                W(Phase,60); return StepResult.Pending;
            case 60: return ReadStaged(Anchor+3*Q,p,61);
            case 61:
                if(!Control(p,3,Anchor+3*Q)||memory.Read32(NsVictim+76)==0||Read16(NsVictim+86)!=0) return Fail(CpxError.Corrupt);
                uint links=memory.Read32(NsVictim+76)-1; memory.Write32(NsVictim+76,links);
                if(links==0) { Write16(NsVictim+86,1); PutWide(NsVictim+40,GetWide(p+88)); PutWide(p+88,GetWide(NsVictim)); }
                if(!AdjustWide(p+104,1,false)) return Fail(CpxError.Corrupt);
                return StagePage(p,Anchor+3*Q,62);
            case 62: return SaveObject(NsVictim,63);
            case 63: return Call(CpxOperation.CommitTransaction,64);
            case 64: W(NsPublished,1); return Call(CpxOperation.Reclaim,99);
            case 80:
                uint name=op==CpxOperation.Rename?NsNewName:NsName,length=op==CpxOperation.Rename?R(NsNewLength):R(NsLength);
                uint scan=R(NsNameScan),end=scan+64,encoded=R(NsEncodedLength),hash=R(NsHash); if(end>length) end=length;
                for(;scan<end;scan++)
                {
                    uint c=memory.Read8(name+scan),fold=FoldLatin(c);
                    if(c<128) memory.Write8(NsEncoded+encoded++,(byte)c);
                    else { memory.Write8(NsEncoded+encoded++,(byte)(0xc0|(c>>6))); memory.Write8(NsEncoded+encoded++,(byte)(0x80|(c&63))); }
                    if(fold<128) hash=HashByte(hash,fold); else { hash=HashByte(hash,0xc0|(fold>>6)); hash=HashByte(hash,0x80|(fold&63)); }
                }
                W(NsNameScan,scan); W(NsEncodedLength,encoded); W(NsHash,hash); if(scan<length) return StepResult.Pending;
                Clear(NsEntry,128); PutWide(NsEntry,new(~hash,0)); PutWide(NsEntry+8,RW(NsEntryId)); PutWide(NsEntry+16,RW(NsResult));
                Write16(NsEntry+32,encoded); Write16(NsEntry+36,length);
                if(encoded<=80) { Copy(NsEntry+48,NsEncoded,encoded); W(Phase,R(NsNameReturn)); return StepResult.Pending; }
                return TakeMetadata(81);
            case 81:
                uint page=Page(4); Wide address=RW(MetadataResult); PutWide(NsEntry+24,address);
                Header(page,17,address,RW(Generation)+1); PutWide(page+24,RW(NsEntryId)); Write16(page+64,R(NsEncodedLength));
                Copy(page+68,NsEncoded,R(NsEncodedLength)); Write16(page+52,R(NsEncodedLength)+4);
                return StagePage(page,address,R(NsNameReturn));
            case 99: W(NsPublished,1); return Done();
            case 100: return Call(CpxOperation.Reclaim,1);
            case 102:
                Wide linkPage=RW(MetadataResult); uint link=Page(4);
                memory.Write8(NsObject+85,3); PutWide(NsObject+96,linkPage);
                Header(link,19,linkPage,RW(Generation)+1); PutWide(link+24,RW(NsResult)); Write16(link+64,R(LinkEncoded));
                Copy(link+68,LinkText,R(LinkEncoded)); Write16(link+52,4+R(LinkEncoded)); return StagePage(link,linkPage,103);
            case 103: return BuildEntry(14);
            default: return Fail(CpxError.Corrupt);
        }
    }
}
