using CopperCPX;
namespace CopperNova.Prototype;

public partial struct NovaEngine<TMemory> where TMemory : struct,ICpxMemory
{
    private StepResult StartPath(uint lockBptr,uint bstr,uint mode)
    {
        uint state=Arena+State;
        if(!LockSlot(lockBptr,out uint slot)) return Error(211);
        if(bstr==0||bstr>0x3fffffff) return Error(115);
        uint p=bstr<<2; if(!ExternalRange(p,1)) return Error(115); uint length=m.Read8(p),start=0;
        if(!ExternalRange(p,length+1)) return Error(115);
        // Two adjacent 256-byte buffers hold the expanded DOS path.
        uint name=Arena+Path,encoded=0;
        for(uint n=0;n<length;n++)
        {
            uint c=m.Read8(p+1+n);
            if(c==0) return Error(210);
            if(c>=128)
            {
                if(!GrowingObjects) return Error(210);
                m.Write8(name+encoded++,(byte)(0xc0|(c>>6)));
                c=0x80|(c&63);
            }
            m.Write8(name+encoded++,(byte)c);
            if(c==58) { start=encoded; slot=1; }
        }
        length=encoded;
        // High-occupancy object-table packets keep every directory visited by this path
        // pinned. Allow one slot per possible component plus the control page,
        // bitmap, selector and new object. Longer paths retry after publication.
        uint dirty=m.Read32(state+DirtyCount);
        if(dirty>40 && dirty+((length-start+1)>>1)+5>64) return StartFlush(0);
        m.Write32(state+PathLength,length); m.Write32(state+PathMode,mode);
        return WalkPath(slot,start,length,mode,0);
    }
    // Carry the cursor through at most four resident directories. On a miss
    // the usual load continuation owns the exact directory and byte offset.
    private StepResult WalkPath(uint slot,uint at,uint length,uint mode,uint directory,bool spill=false)
    {
        uint state=Arena+State,action=m.Read32(state+Action),names=Arena+Path;
        for(uint budget=0;budget<4;budget++) {
            if(directory==0) {
                int hit=Find(ObjectHome(slot));
                if(hit<0) {
                    m.Write32(state+Current,slot); m.Write32(state+PathAt,at);
                    return Load(ObjectHome(slot),21);
                }
                m.Write32(Meta((uint)hit)+12,m.Read32(state+Epoch)); directory=Image((uint)hit);
            }
            if((spill?m.Read32(directory+BucketOwner)!=slot:!ValidInode(directory,slot))||m.Read16(directory+6)!=11) return Error(212);
            if(at==length) return action==8?MakeLock(slot):Error(action==22?203u:212u);
            uint end=at,hash=0;
            while(end<length) {
                uint c=m.Read8(names+end); if(c==47) break;
                c=FoldNameByte(names+at,end-at,c);
                hash=unchecked((hash<<5|hash>>27)+c); end++;
            }
            if(end==at||end-at>168) return Error(210);
            uint entry=FindEntry(directory,names+at,end-at,hash);
            if(entry==uint.MaxValue) return Fatal();
            if(DynamicIndex)
            {
                uint required=(24+end-at+7)&~7u;
                if(!spill) { W(DirectoryRoot,directory); W(RoomPage,0); ResetChain(); }
                if(entry==0)
                {
                    if(R(RoomPage)==0 && required<=192-m.Read32(directory+100))
                        W(RoomPage,directory);
                    uint child=spill?m.Read32(directory+BucketNext):m.Read32(ChainHead(directory,hash));
                    if(child!=0)
                    {
                        if(!AdvanceChain(child)) return Fatal();
                        if(spill) ReleaseBucket(directory);
                        W(Current,slot); W(PathAt,at); return Load(ObjectHome(child),70);
                    }
                    if(spill) ReleaseBucket(directory);
                    if(end==length && (mode==2||action is 22 or 1004 or 1006))
                    {
                        if(R(RoomPage)==0)
                        {
                            W(CreateBucket,1); W(Parent,R(DirectoryRoot)); W(ComponentHash,hash);
                            return Load(128,31);
                        }
                        directory=R(RoomPage);
                    }
                }
            }
            if(end<length) {
                if(entry==0) return Error(205);
                slot=m.Read32(entry+4); at=end+1; directory=0; spill=false; continue;
            }
            if(entry==0||mode==2) {
                m.Write32(state+ComponentHash,hash); m.Write32(state+Parent,directory);
                m.Write32(state+ComponentAt,at); m.Write32(state+ComponentBytes,end-at);
                if(mode==2) { m.Write32(state+Entry,entry); return Next(45); }
                if(action is not (22 or 1004 or 1006)) return Error(205);
                uint required=(24+end-at+7)&~7u;
                if(required>192-m.Read32(directory+100)||m.Read32(directory+96)>=8) return Error(221);
                return PackedVolume?Load(128,31):Load(129,30);
            }
            uint target=m.Read32(entry+4);
            if(action==8) return MakeLock(target);
            if(action==22) return Error(203);
            if(action==16) { W(Parent,directory); W(SourceEntry,entry-directory); W(Target,target); return Load(ObjectHome(target),63); }
            if(action==17) {
                m.Write32(state+SourceParent,m.Read32(directory+20)); m.Write32(state+SourceEntry,entry-directory);
                m.Write32(Arena+RenameBuffer+4,m.Read32(entry+4)); m.Write32(Arena+RenameBuffer+8,m.Read32(entry+8)); m.Write32(Arena+RenameBuffer+12,m.Read32(entry+12));
                uint args=m.Read32(state+Packet)+20;
                return StartPath(m.Read32(args+8),m.Read32(args+12),2);
            }
            m.Write32(state+Target,target);
            if(action==28&&GrowingObjects) return Load(ObjectHome(target),1421);
            if(action==28) return Load(2000+(target>>2),53);
            return Load(ObjectHome(target),40);
        }
        m.Write32(state+Current,slot); m.Write32(state+PathAt,at); return Next(20);
    }
    private uint FindEntry(uint directory,uint name,uint length,uint hash)
    {
        uint used=m.Read32(directory+100),at=0,count=0;
        if(used>192) return uint.MaxValue;
        while(at<used&&count<8)
        {
            uint e=directory+320+at,bytes=m.Read16(e+20),size=(24+bytes+7)&~7u;
            if(bytes==0||size>used-at||m.Read32(e)!=1||m.Read32(e+4)==0||m.Read32(e+4)>LastObjectSlot) return uint.MaxValue;
            if(bytes==length&&m.Read32(e+16)==hash&&NameEqual(e+24,name,length)) return e;
            at+=size; count++;
        }
        return at==used&&count==m.Read32(directory+96)?0:uint.MaxValue;
    }
    private void WriteEntry(uint e,uint id,uint serialHi,uint serialLo,uint name,uint length,uint hash)
    {
        uint bytes=(24+length+7)&~7u; Zero(e,bytes);
        m.Write32(e,1); m.Write32(e+4,id); m.Write32(e+8,serialHi); m.Write32(e+12,serialLo);
        m.Write32(e+16,hash); m.Write16(e+20,length); m.Copy(e+24,name,length);
    }
    private StepResult PacketStep(uint phase)
    {
        uint state=Arena+State;
        uint action=m.Read32(state+Action),args=m.Read32(state+Packet)+20;
        switch(phase)
        {
            case 0:
                W(CreateBucket,0); W(RetireBucket,0); W(SourceParent,0);
                // Release client resources even after an I/O error poisons the mount.
                if(action==15||action==1007) return ReleasePacketResource(action==1007,m.Read32(args));
                if(m.Read32(state+Poison)!=0) return Error(213);
                if(m.Read32(state+Mounted)==0) return Error(218);
                if(m.Read32(state+Inhibited)!=0&&action!=31&&action!=5) return Error(218);
                if(NeedsPacketResource(action)&&action!=29&&R(ReservedResource)==0 ||
                    OpensHandle(action)&&m.Read32(Ptr(HandleSerial))==uint.MaxValue) return Error(103);
                // At most eight new dirty pages per supported operation; pre-retire before admission.
                if(GrowingObjects&&R(FreePending)!=0&&action is 22 or 1004 or 1006) return StartFlush(0);
                uint dirty=m.Read32(state+DirtyCount);
                if(DynamicIndex && dirty>16 && action is not (15 or 27 or 1007)) return StartFlush(0);
                if(dirty>40 && (!GrowingObjects ||
                    action is not (15 or 27 or 1007) && (dirty>54 || action is not (22 or 1006))))
                    return StartFlush(0);
                if((action==1022||action==1006&&!GrowingObjects)&&dirty>14||action==16&&!GrowingObjects&&dirty>12)
                    return StartFlush(0);
                switch(action)
                {
                    case 9: // ACTION_RENAME_DISK, Arg1 is a BPTR to a BSTR.
                        if(!GrowingObjects) return Error(209);
                        if(Arg(0)>0x3fffffff || !StageLabel(Arg(0)<<2)) return Error(210);
                        return Load(128,73);
                    case 27: return StartFlush(10);
                    case 31:
                        if(HasResources) return Error(202);
                        if(m.Read32(args+0)!=0) return StartFlush(11);
                        m.Write32(state+MountReturn,13); return Next(100);
                    case 5:
                        if(HasResources) return Error(202);
                        return StartFlush(12);
                    case 1004: case 1005: case 1006:
                        if(m.Read32(args+0)>0x3fffffff||!ExternalRange(m.Read32(args+0)<<2,44)) return Error(115);
                        return StartPath(m.Read32(args+4),m.Read32(args+8),0);
                    case 22: case 8:
                        if(action==8&&m.Read32(args+8) is not (0xfffffffe or 0xffffffff)) return Error(115);
                        if(action==8&&m.Read32(args+8)==uint.MaxValue) return Error(209);
                        return StartPath(m.Read32(args+0),m.Read32(args+4),0);
                    case 28:
                        if(!PackedVolume) return Error(209);
                        if(m.Read32(args+12)==0||m.Read32(args+12)>0x3fffffff||!ExternalRange(m.Read32(args+12)<<2,1)) return Error(115);
                        uint comment=m.Read8(m.Read32(args+12)<<2); if(comment>79) return Error(220);
                        if(!ExternalRange(m.Read32(args+12)<<2,comment+1)) return Error(115);
                        return StartPath(m.Read32(args+4),m.Read32(args+8),0);
                    case 16:
                        if(!ReusableIds()) return Error(209);
                        return StartPath(Arg(0),Arg(1),0);
                    case 19:
                        if(!PackedVolume) return Error(209);
                        if(!LockSlot(Arg(0),out uint duplicate)) return Error(211);
                        return MakeLock(duplicate);
                    case 29:
                        if(!PackedVolume) return Error(209);
                        if(!LockSlot(Arg(0),out uint child)) return Error(211);
                        if(child==1) return Finish(0);
                        return Load(ObjectHome(child),68);
                    case 25: case 26:
                        if(!PackedVolume) return Error(209);
                        if(action==26&&!LockSlot(Arg(0),out _)) return Error(211);
                        uint info=Arg(action==25?0u:1u);
                        if(info==0||info>0x3fffffff||!ExternalRange(info<<2,36)) return Error(115);
                        return Load(128,69);
                    case 24: case 23:
                        if(!PackedVolume) return Error(209);
                        if(!LockSlot(m.Read32(args+0),out uint examine)) return Error(211);
                        if(m.Read32(args+4)==0||m.Read32(args+4)>0x3fffffff||!ExternalRange(m.Read32(args+4)<<2,260)) return Error(115);
                        m.Write32(state+Target,examine); return Load(ObjectHome(examine),action==24?60u:54u);
                    case 17: m.Write32(state+SourceParent,0); return StartPath(m.Read32(args+0),m.Read32(args+4),1);
                    case 82: case 87: case 1008: case 1022:
                        if(!HandlePointer(m.Read32(args+0),out uint handle)) return Error(211);
                        if(action is 87 or 1022&&m.Read32(handle+12)==0) return Error(223);
                        if(action is 82 or 87&&(m.Read32(args+8)>int.MaxValue||!ExternalRange(m.Read32(args+4),m.Read32(args+8)))) return Error(115);
                        uint objectId=m.Read32(handle+4); if(objectId==0||objectId>LastObjectSlot) return Fatal();
                        uint home=ObjectHome(objectId);
                        m.Write32(state+Handle,handle); m.Write32(state+Current,objectId);
                        int resident=Find(home);
                        if(resident>=0) {
                            uint slot=(uint)resident;
                            m.Write32(Meta(slot)+12,m.Read32(state+Epoch));
                            m.Write32(state+Loaded,Image(slot));
                            goto handleReady;
                        }
                        return Load(home,50);
                    case 1027: return Finish(1); // ACTION_IS_FILESYSTEM
                    default: return Error(209);
                }
            case 73:
                Zero(Root()+RootLabel,64);
                EncodeText(Arena+State+LabelScratch,Root()+RootLabel);
                Dirty(R(Loaded)); // Publish the label with the group's normal shadow root.
                return StartFlush(10);
            case 10: return Finish(uint.MaxValue);
            case 11: m.Write32(state+Inhibited,1); return Finish(uint.MaxValue);
            case 12: m.Write32(state+Mounted,0); return Finish(uint.MaxValue);
            case 13: m.Write32(state+Inhibited,0); m.Write32(state+Mounted,1); return Finish(uint.MaxValue);
            case 71: return ExamineBucket(R(Loaded),R(Target));
            case 72: return NamespaceStep(phase);
            case 70: return WalkPath(R(Current),R(PathAt),R(PathLength),R(PathMode),R(Loaded),true);
            case 20: case 21:
                return WalkPath(m.Read32(state+Current),m.Read32(state+PathAt),m.Read32(state+PathLength),m.Read32(state+PathMode),
                    phase==21?m.Read32(state+Loaded):0);
            case 30:
            {
                uint parent=m.Read32(state+Parent),used=m.Read32(parent+100),required=(24+m.Read32(state+ComponentBytes)+7)&~7u;
                if(required>192-used||m.Read32(parent+96)>=8) return Error(221);
                uint bitmap=m.Read32(state+Loaded),slot=0;
                if(m.Read16(bitmap+6)!=5) return Fatal();
                for(uint n=2;n<1024;n++) if(!Bit(bitmap+128,n)) { slot=n; break; }
                if(slot==0) return Error(221);
                m.Write32(state+Bitmap,bitmap); m.Write32(state+Target,slot); return Load(128,31);
            }
            case 31:
            {
                uint group=m.Read32(state+Loaded); if(m.Read16(group+6)!=3) return Fatal();
                uint free=m.Read32(group+80);
                if(ReusableIds()&&m.Read32(group+84)!=0) {
                    uint head=m.Read32(group+84); if(head<2||head>LastObjectSlot-free) return Fatal();
                    W(Group,group); W(Target,head); return Load(ObjectHome(head),61);
                }
                if(free==0) return Error(221);
                if(PackedVolume) { if(free>=LastObjectSlot) return Fatal(); W(Target,LastObjectSlot+1-free); }
                if(GrowingObjects) return AllocateObjectPair(group);
                return CreateAdmitted(group,false);
            }
            case 61:
            {
                uint group=R(Group),hole=R(Loaded);
                if(!ValidHole(hole,R(Target),LastObjectSlot-m.Read32(group+80))) return Fatal();
                return CreateAdmitted(group,true);
            }
            case 40:
            {
                uint inode=m.Read32(state+Loaded),slot=m.Read32(state+Target);
                if(!ValidInode(inode,slot)||m.Read16(inode+6)!=10) return Error(212);
                if(Conflicts(slot,action==1006)) return Error(202);
                if(action==1006&&m.Read32(inode+68)!=0)
                {
                    if(GrowingObjects&&R(DirtyCount)>14) return StartFlush(0);
                    m.Write32(state+Inode,inode); m.Write32(state+ResizeLength,0); m.Write32(state+Resume,41); return Load(128,400); }
                return OpenHandle(slot);
            }
            case 41: return OpenHandle(m.Read32(state+Target));
            case 45:
            {
                uint parent=m.Read32(state+Parent),home=m.Read32(parent+20),entry=m.Read32(state+Entry);
                if(home!=m.Read32(state+SourceParent)) {
                    if(!PackedVolume) return Error(209);
                    if(entry!=0) return Error(203);
                    uint size=(24+R(ComponentBytes)+7)&~7u;
                    if(size>192-m.Read32(parent+100)) return Error(221);
                    return Load(ObjectHome(m.Read32(Arena+RenameBuffer+4)),62);
                }
                if(entry!=0) return Error(203);
                uint old=parent+m.Read32(state+SourceEntry),oldBytes=(24+m.Read16(old+20)+7)&~7u,newBytes=(24+m.Read32(state+ComponentBytes)+7)&~7u;
                uint used=m.Read32(parent+100),offset=m.Read32(state+SourceEntry)-320;
                if(newBytes>192-used+oldBytes) return Error(221);
                m.Copy(old,old+oldBytes,used-offset-oldBytes);
                WriteEntry(parent+320+used-oldBytes,m.Read32((Arena+RenameBuffer)+4),m.Read32((Arena+RenameBuffer)+8),m.Read32((Arena+RenameBuffer)+12),(Arena+Path)+m.Read32(state+ComponentAt),m.Read32(state+ComponentBytes),m.Read32(state+ComponentHash));
                uint final=used-oldBytes+newBytes;
                if(final<used) ZeroTail(parent+320+final,used-final);
                m.Write32(parent+100,final); Dirty(parent); return Finish(uint.MaxValue);
            }
            case 50:
            handleReady:

            {
                uint inode=m.Read32(state+Loaded),handle=m.Read32(state+Handle),size=m.Read32(inode+68),old=m.Read32(handle+8);
                if(!ValidInode(inode,m.Read32(state+Current))||m.Read16(inode+6)!=10) return Fatal();
                if(action is 1008 or 1022)
                {
                    if(!Position(old,size,m.Read32(args+4),m.Read32(args+8),out uint next)||next>size) return Error(219);
                    if(action==1008) { m.Write32(handle+8,next); return Finish(old); }
                    if(next==size) return Finish(next);
                    m.Write32(state+Inode,inode); m.Write32(state+ResizeLength,next); m.Write32(state+Resume,51); return Load(128,400);
                }
                m.Write32(state+Inode,inode); m.Write32(state+DataBytes,m.Read32(args+8)); m.Write32(state+DataBuffer,m.Read32(args+4));
                return TransferStep(300);
            }
            case 68: return MakeLock(m.Read32(R(Loaded)+108));
            case 69:
            {
                uint info=Arg(action==25?0u:1u)<<2,blocks=R(Blocks);
                Zero(info,36); m.Write32(info+4,R(UnitNumber)); m.Write32(info+8,82); m.Write32(info+12,blocks);
                m.Write32(info+16,blocks-m.Read32(R(Loaded)+72)); m.Write32(info+20,512);
                m.Write32(info+24,VolumeMagic); m.Write32(info+28,R(Volume)>>2);
                m.Write32(info+32,HasResources?uint.MaxValue:0); return Finish(uint.MaxValue);
            }
            case 60: return NextDirectoryEntry();
            case 62: case 63: case 64: case 65: case 66: case 67: return NamespaceStep(phase);
            case 53: return SetPackedComment();
            case 54: case 55: case 56: return ExaminePacked(phase);
            case 51:
                if(m.Read32(m.Read32(state+Handle)+8)>m.Read32(state+ResizeLength)) m.Write32(m.Read32(state+Handle)+8,m.Read32(state+ResizeLength));
                return Finish(m.Read32(state+ResizeLength));
            default: return Fatal();
        }
    }
    private StepResult CreateAdmitted(uint group,bool reuse)
    {
        uint state=Arena+State, action=R(Action);
        if(m.Read32(group+88)==uint.MaxValue&&m.Read32(group+92)==uint.MaxValue) return Error(221);
        int reserved=CacheSlot(ObjectHome(m.Read32(state+Target))); if(reserved<0) return Error(103);
        uint inode=Image((uint)reserved),slot=m.Read32(state+Target),parent=m.Read32(state+Parent),used=m.Read32(parent+100);
        if(GrowingObjects&&!reuse) {
            uint sector=R(ObjectPair)&~1u,map=R(Bitmap);
            uint take=PairRun;
            SetRange(map+128,sector&2047,take,true); DirtyWideBitmap(map,sector>>11,R(WideActiveSelector));
            m.Write32(group+72,m.Read32(group+72)-take); W(AllocationHint,sector+take);
        }
        uint nextHole=reuse?m.Read32(inode+116):0;
        // All fallible admission is complete before the first edit.
        if(PackedVolume) {
            if(GrowingObjects) { uint sector=reuse?m.Read32(inode+ObjectSector):R(ObjectPair); Zero(inode,512); m.Write32(inode+ObjectSector,sector); }
            m.Write16(inode+6,action==22?11u:10u); m.Write32(inode+20,ObjectHome(slot)); m.Write32(inode+24,1); m.Write32(inode+28,slot);
            m.Write32(inode+64,0); m.Write32(inode+68,0); m.Write32(inode+96,0); m.Write32(inode+100,0); m.Write32(inode+116,0);
        } else { Zero(inode,512); Header(inode,action==22?11u:10u,ObjectHome(slot),m.Read32(state+Generation)+1,slot); m.Write32(inode+76,1); m.Write32(inode+104,1); }
        m.Write32(inode+108,DynamicIndex?DirectoryId(parent):m.Read32(parent+28));
        if(DynamicIndex&&m.Read32(parent+BucketOwner)!=0) m.Write32(inode+NameContainer,m.Read32(parent+28));
        if(!PackedVolume) { uint bitmap=m.Read32(state+Bitmap); SetBit(bitmap+128,slot,true); Dirty(bitmap); }
        uint serialLo=m.Read32(group+92)+1,serialHi=m.Read32(group+88)+(serialLo==0?1u:0u);
        m.Write32(group+88,serialHi); m.Write32(group+92,serialLo); if(reuse) m.Write32(group+84,nextHole); else m.Write32(group+80,m.Read32(group+80)-1); Dirty(group);
        if(DynamicIndex&&R(CreateBucket)!=0)
        {
            uint link=ChainHead(parent,R(ComponentHash));
            m.Write16(inode+6,11); m.Write32(inode+BucketNext,m.Read32(link)); m.Write32(inode+BucketOwner,m.Read32(parent+28));
            m.Write32(link,slot); Dirty(inode); Dirty(parent); return Next(0);
        }
        CountSpill(parent,true);
        WriteEntry(parent+320+used,slot,serialHi,serialLo,(Arena+Path)+m.Read32(state+ComponentAt),m.Read32(state+ComponentBytes),m.Read32(state+ComponentHash));
        m.Write32(parent+100,used+((24+m.Read32(state+ComponentBytes)+7)&~7u)); m.Write32(parent+96,m.Read32(parent+96)+1);
        Dirty(parent); Dirty(inode);
        return action==22?MakeLock(slot):OpenHandle(slot);
    }
    private void ZeroTail(uint p,uint bytes) { for(uint i=0;i<bytes;i++) m.Write8(p+i,0); }
    private bool Position(uint old,uint size,uint offset,uint origin,out uint value)
    {
        value=0; uint basis=origin==uint.MaxValue?0:origin==0?old:origin==1?size:uint.MaxValue;
        if(basis==uint.MaxValue) return false;
        if((offset&0x80000000)!=0) { uint magnitude=unchecked(0u-offset); if(magnitude>basis) return false; value=basis-magnitude; }
        else { if(offset>uint.MaxValue-basis) return false; value=basis+offset; }
        return true;
    }
}
