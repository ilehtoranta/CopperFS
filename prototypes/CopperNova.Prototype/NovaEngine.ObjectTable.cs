using CopperCPX;

namespace CopperNova.Prototype;

public partial struct NovaEngine<TMemory> where TMemory : struct, ICpxMemory
{
    private const uint EntriesPerIndex=60;
    private bool DynamicIndex => GrowingObjects;
    private uint GrowDataFloor => (WideBitmapStart+WindowCount*2+2047)&~2047u;
    private uint GrowCapacity => R(Blocks)/2<0x1fffffffu?R(Blocks)/2:0x1fffffffu;
    private const uint LocatorSector=668, LocatorPage=672, SavedLoadSlot=676, SavedLoadHome=680, SavedLoadNext=684, SavedSelector=728, IndexSealing=732;
    private uint PairRun => DynamicIndex && R(Target)>1 && (R(Target)-1)%EntriesPerIndex==0?4u:2u;
    private uint GroupOf(uint id) => (id-1)/60;
    private uint EntryAt(uint index,uint offset) => index+32+offset*8;
    private const uint ObjectKey=0x20000000, ObjectIndexType=39, ObjectImageType=40;
    private const uint ObjectSector=124, ObjectComment=144;
    private const uint ObjectCursor=648, ObjectGroup=652, ObjectSlot=656, ObjectPair=660, ObjectCommittedLast=664;
    // Only a validated object-table root has a nonzero committed object high-water mark.
    private bool GrowingObjects => R(ObjectCommittedLast)!=0;
    private uint LastObjectSlot => GrowingObjects?GrowCapacity:1023;
    private uint ObjectHome(uint id) => id<1024?256+id:ObjectKey+id;
    private bool IsObjectHome(uint home) => home is >=257 and <1280 || home>=ObjectKey+1024 && home<WideSelectorKey;
    private uint ObjectId(uint home) => home>=ObjectKey?home-ObjectKey:home-256;
    private uint IndexSector => R(LocatorSector);
    private bool ObjectAddress(uint id,uint sector) => id==1?Pair(sector,194):
        (sector&~1u)>=DataStart && (sector|1u)<R(Blocks);

    private StepResult ReadObjectIndex(uint home)
    {
        uint id=ObjectId(home),group=GroupOf(id);
        if(id>m.Read32(Root()+76)) return Fatal();
        W(ObjectGroup,group);
        if(DynamicIndex)
        {
            W(SavedSelector,R(WideActiveSelector)); W(SavedLoadSlot,R(LoadSlot)); W(SavedLoadHome,home); W(SavedLoadNext,R(LoadNext));
            return Load(WideBitmapKey+(group>>5),1430);
        }
        return LoadIndex(group,1400);
    }

    // Borrow the narrow-layout leaf scratch and its mount-local sector tags.
    private uint IndexBuffer(uint group) => Ptr(RawLeaf)+((group&1)<<9);
    private uint IndexTag(uint group) => RawKeys+((group&1)<<2);
    private StepResult LoadIndex(uint group,uint continuation)
    {
        uint sector=IndexSector;
        if(R(IndexTag(group))==sector) return Next(continuation);
        return Issue(IoKind.Read,sector,IndexBuffer(group),512,continuation);
    }

    private bool VerifyIndex(uint index,uint group)
    {
        uint tag=IndexTag(group),sector=IndexSector;
        if(R(tag)==sector) return m.Read32(index+20)==group;
        if(!PageValid(index,ObjectIndexType,sector,group)) return false;
        W(tag,sector);
        return true;
    }

    private StepResult AllocateObjectPair(uint group)
    {
        if(m.Read32(group+72)<PairRun) return Error(221);
        W(Group,group); W(Searched,0);
        W(Candidate,R(AllocationHint)&~1u);
        return Next(1402);
    }

    private bool ValidObjectContents(uint page,uint id,bool sealing=false)
    {
        uint kind=m.Read16(page+6),count=m.Read32(page+96),used=m.Read32(page+100);
        if(m.Read8(page+ObjectComment)>79 || m.Read32(page+108)==0 ||
            m.Read32(page+108)>LastObjectSlot || used>192) return false;
        if(DynamicIndex && (m.Read32(page+NameContainer)>LastObjectSlot || m.Read32(page+BucketNext)>LastObjectSlot ||
            m.Read32(page+BucketOwner)!=0 && (kind!=11 || m.Read32(page+BucketOwner)!=m.Read32(page+108)))) return false;
        if(kind==12) return ValidHole(page,id,DynamicIndex&&sealing?LastObjectSlot:m.Read32(Root()+76));
        if(!ValidInode(page,id)) return false;
        if(kind==10)
        {
            if(used!=0) return false;
            uint logical=m.Read32(page+PrefixEnd);
            if(!ValidPrefixPointer(page,logical) || !ValidIndexHeader(page)) return false;
            for(uint i=0;i<count;i++)
            {
                uint extent=ExtentAt(page,i),run=m.Read32(extent+16);
                if(m.Read32(extent+4)!=logical||!ExtentOK(m.Read32(extent+12),run)||run>uint.MaxValue-logical) return false;
                logical+=run;
            }
            uint size=m.Read32(page+68);
            return logical==(size>>9)+((size&511)==0?0u:1u);
        }
        uint at=0,entries=0;
        while(at<used)
        {
            uint child=page+320+at;
            if(used-at<24) return false;
            uint len=m.Read16(child+20),bytes=(24+len+7)&~7u;
            if(len==0||len>168||bytes>used-at||m.Read32(child)!=1||
                m.Read32(child+4)==0||m.Read32(child+4)>LastObjectSlot) return false;
            at+=bytes; entries++;
        }
        return entries==count&&m.Read32(page+68)==0;
    }

    // The mount exclusively owns this cache image. Restore its decoded identity
    // on a validated read or after the alternate-sector write has completed.
    private void DecodeObject(uint page,uint id)
    {
        m.Write16(page+6,m.Read16(page+32)); m.Write32(page+20,ObjectHome(id));
        m.Write32(page+24,1); m.Write32(page+28,id);
    }

    private StepResult ObjectTableStep(uint phase)
    {
        uint group=R(ObjectGroup),index=IndexBuffer(group);
        switch(phase)
        {
            case 1430:
            case 1431:
            {
                uint map=R(Loaded),address=map+384+(group&31)*4,sector=m.Read32(address);
                W(LocatorPage,map);
                if(phase==1431 && group*EntriesPerIndex>=R(ObjectCommittedLast))
                {
                    if(sector!=0) return Fatal();
                    int hit=Find(ObjectHome(group*EntriesPerIndex+1));
                    if(hit<0) return Fatal();
                    sector=(m.Read32(Image((uint)hit)+ObjectSector)&~1u)+2;
                    W(LocatorSector,sector|1u); Zero(index,512); return Next(1412);
                }
                if(group==0?!Pair(sector,ObjectIndexStart):((sector&~1u)<DataStart||(sector|1u)>=R(Blocks))) return Fatal();
                W(LocatorSector,sector);
                if(phase==1431) return LoadIndex(group,1411);
                W(WideActiveSelector,R(SavedSelector)); W(LoadSlot,R(SavedLoadSlot)); W(LoadHome,R(SavedLoadHome)); W(LoadNext,R(SavedLoadNext));
                return LoadIndex(group,1400);
            }
            case 1400:
            {
                if(!VerifyIndex(index,group)) return Fatal();
                uint id=ObjectId(R(LoadHome)),entry=EntryAt(index,id-1-group*EntriesPerIndex),sector=m.Read32(entry);
                if(!ObjectAddress(id,sector)) return Fatal();
                uint tag=m.Read32(entry+4);
                if(tag!=0)
                {
                    uint kind=(tag&3)+9,page=Image(R(LoadSlot)),value=tag>>2;
                    if((tag&3)==0||value>R(ObjectCommittedLast)) return Fatal();
                    Zero(page,512); m.Write16(page+32,kind);
                    m.Write32(page+108,kind==12?1u:value);
                    if(kind==12) m.Write32(page+116,value);
                    DecodeObject(page,id); m.Write32(page+ObjectSector,sector);
                    return ValidObjectContents(page,id)?LoadedPacked():Fatal();
                }
                W(ObjectPair,sector);
                return Issue(IoKind.Read,sector,Image(R(LoadSlot)),512,1401);
            }
            case 1401:
            {
                uint id=ObjectId(R(LoadHome)),page=Image(R(LoadSlot)),sector=R(ObjectPair);
                if(!PageValid(page,ObjectImageType,sector,id)) return Fatal();
                DecodeObject(page,id); m.Write32(page+ObjectSector,sector);
                return ValidObjectContents(page,id)?LoadedPacked():Fatal();
            }
            case 1402:
            {
                if(R(Searched)>=R(Blocks)-DataStart) return Error(221);
                uint at=R(Candidate);
                if(at>=R(Blocks)-1) { at=DataStart; W(Candidate,at); }
                return Load(BitmapHome(at),1403);
            }
            case 1403:
            {
                uint map=R(Loaded),at=R(Candidate),end=R(Blocks)-at,available=2048-(at&2047);
                if(end>available) end=available;
                uint offset=0;
                uint required=PairRun;
                while(offset+required<=end && WideBitmapRun(map+128,(at+offset)&2047,required,0)!=required) offset+=2;
                if(offset+required>end)
                {
                    ReleaseWideAllocation(map,R(WideActiveSelector));
                    W(Searched,R(Searched)+end); W(Candidate,end==R(Blocks)-at?DataStart:at+end);
                    return Next(1402);
                }
                uint sector=at+offset,control=R(Group);
                W(ObjectPair,sector|1u); W(Bitmap,map);
                return CreateAdmitted(control,false);
            }
            case 1410:
            {
                uint lowest=uint.MaxValue,cursor=R(ObjectCursor);
                for(uint i=0;i<R(DirtyCount);i++)
                {
                    uint home=m.Read32(Meta(m.Read8(Ptr(DirtySlots)+i)));
                    if(!IsObjectHome(home)) continue;
                    uint id=ObjectId(home);
                    if(id>cursor&&id<lowest) lowest=id;
                }
                if(lowest==uint.MaxValue && DynamicIndex) return Next(1201);
                if(lowest==uint.MaxValue) return Issue(IoKind.Flush,0,0,0,1212);
                uint next=GroupOf(lowest);
                W(ObjectGroup,next); W(ObjectCursor,(next+1)*EntriesPerIndex); W(ObjectSlot,0);
                if(DynamicIndex) return Load(WideBitmapKey+(next>>5),1431);
                if(next*EntriesPerIndex<R(ObjectCommittedLast))
                    return LoadIndex(next,1411);
                Zero(IndexBuffer(next),512); return Next(1412);
            }
            case 1411:
                return VerifyIndex(index,group)?Next(1412):Fatal();
            case 1412:
            {
                uint first=R(ObjectCursor)-EntriesPerIndex+1;
                for(uint i=R(ObjectSlot);i<R(DirtyCount);i++)
                {
                    uint slot=m.Read8(Ptr(DirtySlots)+i),home=m.Read32(Meta(slot));
                    if(!IsObjectHome(home)) continue;
                    uint id=ObjectId(home),offset=id-first;
                    if(offset>=EntriesPerIndex) continue;
                    uint page=Image(slot),sector=m.Read32(page+ObjectSector),entry=EntryAt(index,offset);
                    if(!ObjectAddress(id,sector)||!ValidObjectContents(page,id,true)||
                        (id<=R(ObjectCommittedLast)?m.Read32(entry)!=sector:m.Read32(entry)!=0)) return Fatal();
                    uint kind=m.Read16(page+6),tag=0;
                    if((!DynamicIndex || m.Read32(page+NameContainer)==0 && m.Read32(page+BucketOwner)==0 && !HasBuckets(page)) && m.Read32(page+96)==0 && m.Read32(page+68)==0 && m.Read8(page+ObjectComment)==0 &&
                       (kind==12 || m.Read32(page+116)==0))
                        tag=((kind==12?m.Read32(page+116):m.Read32(page+108))<<2)|(kind-9);
                    m.Write32(entry+4,tag);
                    if(tag!=0) { m.Write32(entry,sector); continue; }
                    sector^=1;
                    m.Write32(entry,sector); m.Write32(page+ObjectSector,sector);
                    m.Write16(page+32,m.Read16(page+6));
                    Stamp(page,ObjectImageType,sector,id,R(NewGeneration));
                    W(ObjectSlot,i+1); return Issue(IoKind.Write,sector,page,512,1413);
                }
                uint alternate=IndexSector^1u;
                if(DynamicIndex)
                {
                    uint map=R(LocatorPage);
                    m.Write32(map+384+(group&31)*4,alternate);
                    DirtyWideBitmap(map,group>>5,R(WideActiveSelector));
                }
                else SetBit(Root()+160,group,(alternate&1)!=0);
                Stamp(index,ObjectIndexType,alternate,group,R(NewGeneration));
                // Packet execution resumes only after both barriers succeed.
                W(IndexTag(group),alternate);
                return Issue(IoKind.Write,alternate,index,512,1410);
            }
            case 1413:
            {
                uint page=Image(m.Read8(Ptr(DirtySlots)+R(ObjectSlot)-1));
                DecodeObject(page,m.Read32(page+20));
                return Next(1412);
            }
            case 1421:
            {
                uint page=R(Loaded),source=Arg(3)<<2;
                if(!ValidInode(page,R(Target))) return Fatal();
                Zero(page+ObjectComment,80); m.Copy(page+ObjectComment,source,(uint)m.Read8(source)+1);
                Dirty(page); return Finish(uint.MaxValue);
            }
            default: return Fatal();
        }
    }
}
