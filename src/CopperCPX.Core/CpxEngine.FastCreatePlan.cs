/* Copyright (C) 2026 Ilkka Lehtoranta; SPDX-License-Identifier: MIT. */
namespace CopperCPX;
public partial struct CpxEngine<TMemory> where TMemory:struct,ICpxMemory
{
    // Create does not use the rename destination-name buffer. Keep its validated
    // plan there so values do not remain live across metadata ownership calls.
    private uint CreatePlan => NsNewName;
    private StepResult FastCreateStep()
    {
        if (DirectInodes) return FastDirectCreateStep();
        if(memory.Read32(Arena+Phase)==601)
        {
            if(!Control((memory.Read32(Arena + (GeometryPages + (3) * 4))),4,Anchor+8*Q)||!ReadableGeneration((memory.Read32(Arena + (GeometryPages + (3) * 4))))) return Fail(CpxError.Corrupt);
            memory.Write32(Arena+Phase,600); return StepResult.Pending;
        }
        if(!PrepareFastCreatePlan(out var pending)) return pending;
        ApplyFastCreatePlan();
        return Done();
    }
    private bool DeclineFastCreate()
    { memory.Write32(Arena+Phase,0); return false; }
    private bool PrepareFastCreatePlan(out StepResult pending)
    {
        pending=StepResult.Pending;
        ulong parent=((((ulong)(memory.Read32((Arena) + ((NsParent))))) << 32 | ((ulong)(memory.Read32((Arena) + ((NsParent) + 4))))));
        uint cached=CachedObject(parent),state=CachedMetadata(StateAddress),reserve=CachedMetadata(Anchor+8*Q);
        if(cached==0||state==0||memory.Read32(Arena+NsDirectory)>1||memory.Read32(Arena+StagedCount)>35||((((ulong)(memory.Read32((state+88)))) << 32 | ((ulong)(memory.Read32((state+88) + 4)))))!=0||!FastName(NsName,memory.Read32(Arena+NsLength))) return DeclineFastCreate();
        ulong root=((((ulong)(memory.Read32((state+80)))) << 32 | ((ulong)(memory.Read32((state+80) + 4))))),dir=((((ulong)(memory.Read32((cached+32)))) << 32 | ((ulong)(memory.Read32((cached+32) + 4)))));
        uint objects=CachedMetadata(root),directory=dir==0?0:CachedMetadata(dir);
        if(objects==0||memory.Read16(objects+4)!=11||memory.Read16(objects+50)!=0||((((ulong)(memory.Read32((objects+24)))) << 32 | ((ulong)(memory.Read32((objects+24) + 4)))))!=0||((((ulong)(memory.Read32((objects+40)))) << 32 | ((ulong)(memory.Read32((objects+40) + 4)))))!=0||dir!=0&&!FastDirectoryShape(directory,parent)||memory.Read8(cached+84)!=2||(memory.Read32(cached+72)&4)!=0) return DeclineFastCreate();
        uint count=memory.Read16(objects+48),capacity=(P-64)>>8,entries=directory==0?0:memory.Read16(directory+48);
        if(count==0||count>capacity||memory.Read16(objects+52)!=count*256||entries>=(P-64)>>7||((((ulong)(memory.Read32((cached+8)))) << 32 | ((ulong)(memory.Read32((cached+8) + 4)))))!=entries||directory!=0&&FastFindName(directory,NsName,memory.Read32(Arena+NsLength))!=-1||(((((ulong)(memory.Read32((state+64)))) << 32 | ((ulong)(memory.Read32((state+64) + 4)))))>>32)!=0||(((((ulong)(memory.Read32((state+72)))) << 32 | ((ulong)(memory.Read32((state+72) + 4)))))>>32)!=0||memory.Read32(state+68)==uint.MaxValue||memory.Read32(state+76)==uint.MaxValue||!Scalar64.Add(((((ulong)(memory.Read32((state+104)))) << 32 | ((ulong)(memory.Read32((state+104) + 4))))),1,out _)||!Scalar64.Add(((((ulong)(memory.Read32((Arena) + ((Generation))))) << 32 | ((ulong)(memory.Read32((Arena) + ((Generation) + 4)))))),1,out _)) return DeclineFastCreate();
        if(reserve==0){pending=ReadStaged(Anchor+8*Q,(memory.Read32(Arena + (GeometryPages + (3) * 4))),601);return false;}
        uint need=(directory==0?1u:0u)+(count==capacity?2u:0u),available=memory.Read16(reserve+64);
        if(available<=need||available>ReserveCapacity||((((ulong)(memory.Read32((state+64)))) << 32 | ((ulong)(memory.Read32((state+64) + 4)))))<=((((ulong)(memory.Read32((objects+64+(count-1)*256)))) << 32 | ((ulong)(memory.Read32((objects+64+(count-1)*256) + 4)))))) return DeclineFastCreate();
        uint parentSlot=uint.MaxValue;
        for(uint n=0;n<count;n++) if(((((ulong)(memory.Read32((objects+64+n*256)))) << 32 | ((ulong)(memory.Read32((objects+64+n*256) + 4)))))==parent) parentSlot=n;
        if(parentSlot==uint.MaxValue) return DeclineFastCreate();
        for(uint n=0;n<need;n++) if(!Allocatable(((((ulong)(memory.Read32((reserve+72+(available-1-n)*8)))) << 32 | ((ulong)(memory.Read32((reserve+72+(available-1-n)*8) + 4))))))) return DeclineFastCreate();
        uint plan=CreatePlan;
        PutWide(plan,parent);PutWide(plan+8,root);PutWide(plan+16,dir);PutWide(plan+24,((((ulong)(memory.Read32((state+64)))) << 32 | ((ulong)(memory.Read32((state+64) + 4))))));PutWide(plan+32,((((ulong)(memory.Read32((state+72)))) << 32 | ((ulong)(memory.Read32((state+72) + 4))))));
        memory.Write32(plan+40,state);memory.Write32(plan+44,objects);memory.Write32(plan+48,reserve);memory.Write32(plan+52,directory);memory.Write32(plan+56,parentSlot);memory.Write32(plan+60,count);memory.Write32(plan+64,capacity);memory.Write32(plan+68,entries);memory.Write32(plan+72,available);
        return true;
    }
    private void ApplyFastCreatePlan()
    {
        uint plan=CreatePlan;
        BeginFastGroup();
        uint state=OwnMetadata(memory.Read32(plan+40),StateAddress,3,0),objects=OwnMetadata(memory.Read32(plan+44),((((ulong)(memory.Read32((plan+8)))) << 32 | ((ulong)(memory.Read32((plan+8) + 4))))),11,0),reserve=OwnMetadata(memory.Read32(plan+48),Anchor+8*Q,4,0);
        uint directory=memory.Read32(plan+52),available=memory.Read32(plan+72);
        if(directory==0)
        {
            PutWide(plan+16,GetWide(reserve+72+(--available)*8));Clear(reserve+72+available*8,8);
            directory=OwnMetadata(0,((((ulong)(memory.Read32((plan+16)))) << 32 | ((ulong)(memory.Read32((plan+16) + 4))))),13,((((ulong)(memory.Read32((plan)))) << 32 | ((ulong)(memory.Read32((plan) + 4))))));
            PutWide(directory+24,((((ulong)(memory.Read32((plan)))) << 32 | ((ulong)(memory.Read32((plan) + 4))))));memory.Write16(directory+48,0);memory.Write16(directory+50,0);
        }
        else directory=OwnMetadata(directory,((((ulong)(memory.Read32((plan+16)))) << 32 | ((ulong)(memory.Read32((plan+16) + 4))))),13,((((ulong)(memory.Read32((plan)))) << 32 | ((ulong)(memory.Read32((plan) + 4))))));
        PutWide(NsEntry+8,((((ulong)(memory.Read32((plan+32)))) << 32 | ((ulong)(memory.Read32((plan+32) + 4))))));PutWide(NsEntry+16,((((ulong)(memory.Read32((plan+24)))) << 32 | ((ulong)(memory.Read32((plan+24) + 4))))));InsertFastEntry(directory,memory.Read32(plan+68));
        uint parentRecord=objects+64+memory.Read32(plan+56)*256;
        PutWide(parentRecord+32,((((ulong)(memory.Read32((plan+16)))) << 32 | ((ulong)(memory.Read32((plan+16) + 4))))));PutWide(parentRecord+8,memory.Read32(plan+68)+1);
        Clear(NsObject,256);PutWide(NsObject,((((ulong)(memory.Read32((plan+24)))) << 32 | ((ulong)(memory.Read32((plan+24) + 4))))));memory.Write32(NsObject+76,1);memory.Write32(NsObject+80,1);
        memory.Write8(NsObject+84,(byte)(memory.Read32(Arena+NsDirectory)!=0?2:1));
        if(memory.Read32(Arena+NsDirectory)!=0) PutWide(NsObject+40,((((ulong)(memory.Read32((plan)))) << 32 | ((ulong)(memory.Read32((plan) + 4))))));
        uint count=memory.Read32(plan+60);
        if(count<memory.Read32(plan+64))
        {
            Copy(objects+64+count*256,NsObject,256);memory.Write16(objects+48,count+1);memory.Write16(objects+52,(count+1)*256);
        }
        else available=SplitFastCreatedObject(plan,objects,state,reserve,available);
        memory.Write16(reserve+64,available);PutWide(state+64,((((ulong)(memory.Read32((plan+24)))) << 32 | ((ulong)(memory.Read32((plan+24) + 4)))))+1);PutWide(state+72,((((ulong)(memory.Read32((plan+32)))) << 32 | ((ulong)(memory.Read32((plan+32) + 4)))))+1);AdjustWide(state+104,1,true);
        CacheBufferedObjects(objects,((((ulong)(memory.Read32((plan+8)))) << 32 | ((ulong)(memory.Read32((plan+8) + 4))))));WW(NsResult,((((ulong)(memory.Read32((plan+24)))) << 32 | ((ulong)(memory.Read32((plan+24) + 4))))));WW(NsEntryId,((((ulong)(memory.Read32((plan+32)))) << 32 | ((ulong)(memory.Read32((plan+32) + 4))))));memory.Write32(Arena+NsPublished,1);
    }
    private uint SplitFastCreatedObject(uint plan,uint objects,uint state,uint reserve,uint available)
    {
        ulong sibling=GetWide(reserve+72+(--available)*8),newRoot=GetWide(reserve+72+(--available)*8);
        Clear(reserve+72+available*8,16);
        uint right=OwnMetadata(0,sibling,11,0),branch=OwnMetadata(0,newRoot,12,0),count=memory.Read32(plan+60),left=(count+1)>>1,total=count+1-left;
        for(uint n=0;n<total;n++){uint index=left+n;Copy(right+64+n*256,index==count?NsObject:objects+64+index*256,256);}
        Clear(objects+64+left*256,P-64-left*256);memory.Write16(objects+48,left);memory.Write16(objects+52,left*256);memory.Write16(right+48,total);memory.Write16(right+52,total*256);
        memory.Write16(branch+48,2);memory.Write16(branch+50,1);memory.Write16(branch+52,32);
        PutWide(branch+64,((((ulong)(memory.Read32((objects+64)))) << 32 | ((ulong)(memory.Read32((objects+64) + 4))))));PutWide(branch+72,((((ulong)(memory.Read32((plan+8)))) << 32 | ((ulong)(memory.Read32((plan+8) + 4))))));PutWide(branch+80,((((ulong)(memory.Read32((right+64)))) << 32 | ((ulong)(memory.Read32((right+64) + 4))))));PutWide(branch+88,sibling);PutWide(state+80,newRoot);CacheBufferedObjects(right,sibling);
        return available;
    }
}
