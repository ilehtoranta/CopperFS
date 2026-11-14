namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // This top-level-operation union overlaps formatter state. Formatting never
    // calls link operations; namespace/reclamation helpers do not use this area.
    private const uint LinkLength=15524,LinkEncoded=15528,LinkScan=15532,LinkOutput=15536,LinkBuffer=15540,LinkCapacity=15544;
    private uint LinkText=>Arena+15552;
    public CpxError BeginSymlink(Wide parent,uint name,uint nameLength,uint target,uint length)
    {
        if(target==0||length==0||length>768||target>uint.MaxValue-length) return CpxError.InvalidArgument;
        CpxError error=BeginCreate(parent,name,nameLength); if(error!=CpxError.None) return error;
        W(NsDirectory,3); W(LinkLength,length); Copy(LinkText,target,length); return error;
    }
    private StepResult EncodeSymlinkStep()
    {
        switch(R(Phase))
        {
            case 0: W(LinkScan,0); W(LinkEncoded,0); W(Phase,1); return StepResult.Pending;
            case 1:
                uint at=R(LinkScan),end=at+64,encoded=R(LinkEncoded); if(end>R(LinkLength)) end=R(LinkLength);
                for(;at<end;at++) { uint c=memory.Read8(LinkText+at); if(c==0) return Reject(CpxError.InvalidArgument); encoded+=c<128?1u:2u; }
                if(encoded>768) return Reject(CpxError.InvalidArgument); W(LinkScan,at); W(LinkEncoded,encoded);
                if(at<R(LinkLength)) return StepResult.Pending;
                W(LinkOutput,encoded); W(Phase,2); return StepResult.Pending;
            case 2:
                uint input=R(LinkScan),output=R(LinkOutput),limit=input>64?input-64:0;
                while(input>limit)
                {
                    uint c=memory.Read8(LinkText+--input);
                    if(c>=128) { memory.Write8(LinkText+--output,(byte)(0x80|(c&63))); c=0xc0|(c>>6); }
                    memory.Write8(LinkText+--output,(byte)c);
                }
                W(LinkScan,input); W(LinkOutput,output); return input==0?Done():StepResult.Pending;
            default: return Fail(CpxError.Corrupt);
        }
    }
    public CpxError BeginHardLink(Wide target,Wide parent,uint name,uint length)
    {
        if(!ValidComponent(name,length,true)) return CpxError.InvalidArgument;
        CpxError error=StartOperation(CpxOperation.CreateHardLink); if(error!=CpxError.None) return error;
        WW(NsResult,target); WW(NsParent,parent); W(NsLength,length); Copy(NsName,name,length); return error;
    }
    private StepResult HardLinkStep()
    {
        uint p=Page(3);
        if(R(Phase) is 80 or 81) return NamespaceStep();
        switch(R(Phase))
        {
            case 0: return Call(CpxOperation.Reclaim,1);
            case 1: return LookupName(RW(NsParent),NsName,R(NsLength),2);
            case 2:
                if(LookupFound) return Reject(CpxError.AlreadyExists);
                Copy(NsParentRecord,ObjectRecord,256); if((memory.Read32(NsParentRecord+72)&4)!=0) return Reject(CpxError.Protected);
                return LoadObject(RW(NsResult),3);
            case 3:
                if(R(SourcePage)==0||memory.Read32(ObjectRecord+76)==0) return Reject(CpxError.NotFound);
                if(memory.Read8(ObjectRecord+84)!=1) return Reject(CpxError.WrongType);
                if(memory.Read32(ObjectRecord+76)==uint.MaxValue) return Reject(CpxError.ResourceExhausted);
                Copy(NsObject,ObjectRecord,256); memory.Write32(NsObject+76,memory.Read32(NsObject+76)+1);
                return EnsureReserve(22,4);
            case 4: return BeginTx(22,5);
            case 5: return ReadStaged(Anchor+3*Q,p,6);
            case 6:
                if(!Control(p,3,Anchor+3*Q)||!ReadableGeneration(p)) return Fail(CpxError.Corrupt);
                WW(NsEntryId,GetWide(p+72));
                if(!AdjustWide(p+72,1,true)||!AdjustWide(p+104,1,true)) return Reject(CpxError.ResourceExhausted);
                return StagePage(p,Anchor+3*Q,7);
            case 7: return BuildEntry(8);
            case 8: return MutateTree(CpxTreeKind.Directory,GetWide(NsParentRecord+32),RW(NsParent),NsEntry,0,9);
            case 9:
                PutWide(NsParentRecord+32,RW(MutationRoot)); if(!AdjustWide(NsParentRecord+8,1,true)) return Fail(CpxError.Corrupt);
                return SaveObject(NsParentRecord,10);
            case 10: return SaveObject(NsObject,11);
            case 11: return Call(CpxOperation.CommitTransaction,12);
            case 12: W(NsPublished,1); return Done();
            default: return Fail(CpxError.Corrupt);
        }
    }
    public CpxError BeginReadSymlink(Wide id,uint destination,uint capacity)
    {
        if(destination==0||capacity>768||destination>uint.MaxValue-capacity) return CpxError.InvalidArgument;
        CpxError error=StartOperation(CpxOperation.ReadSymlink,id); if(error!=CpxError.None) return error;
        W(LinkBuffer,destination); W(LinkCapacity,capacity); return error;
    }
    private StepResult ReadSymlinkStep()
    {
        uint p=Page(4);
        switch(R(Phase))
        {
            case 0: return LoadObject(RW(ObjectKey),1);
            case 1:
                if(R(SourcePage)==0) return Reject(CpxError.NotFound);
                if(memory.Read8(ObjectRecord+84)!=3) return Reject(CpxError.WrongType);
                Wide size=GetWide(ObjectRecord+8); if(size.High!=0||size.Low==0||size.Low>768) return Fail(CpxError.Corrupt);
                if(size.Low>R(LinkCapacity)) return Reject(CpxError.InvalidArgument);
                W(LinkLength,size.Low); W(LinkScan,0);
                if(memory.Read8(ObjectRecord+85)==0)
                {
                    if(size.Low>160||Read16(ObjectRecord+88)!=size.Low) return Fail(CpxError.Corrupt);
                    W(LinkOutput,ObjectRecord+96); W(Phase,3); return StepResult.Pending;
                }
                if(memory.Read8(ObjectRecord+85)!=3||!Allocatable(GetWide(ObjectRecord+96))) return Fail(CpxError.Corrupt);
                return ReadStaged(GetWide(ObjectRecord+96),p,2);
            case 2:
                if(!ValidPage(p,GetWide(ObjectRecord+96))||!ReadableGeneration(p)||Read16(p+4)!=19||!Wide.Equal(GetWide(p+24),GetWide(ObjectRecord))||!PayloadValid(p)||Read16(p+64)!=R(LinkLength)) return Fail(CpxError.Corrupt);
                W(LinkOutput,p+68); W(Phase,3); return StepResult.Pending;
            case 3:
                uint count=R(LinkLength)-R(LinkScan); if(count>64) count=64;
                Copy(R(LinkBuffer)+R(LinkScan),R(LinkOutput)+R(LinkScan),count); W(LinkScan,R(LinkScan)+count);
                if(R(LinkScan)<R(LinkLength)) return StepResult.Pending;
                W(FileValue,R(LinkLength)); return Done();
            default: return Fail(CpxError.Corrupt);
        }
    }
}
