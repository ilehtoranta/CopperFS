using CopperCPX;
namespace CopperNova.Prototype;

public partial struct NovaEngine<TMemory> where TMemory : struct,ICpxMemory
{
    private uint FreeResource(bool handle)
    {
        uint start=Ptr(handle?Handles:Locks),width=handle?64u:48u,flag=handle?0u:32u;
        for(uint n=0;n<32;n++) if(m.Read32(start+n*width+flag)==0) return start+n*width;
        return 0;
    }
    private bool HandlePointer(uint token,out uint pointer)
    { pointer=Ptr(Handles)+(token&31)*64; return token!=0&&m.Read32(pointer)==token; }
    private bool LockSlot(uint bptr,out uint slot)
    {
        slot=1; if(bptr==0) return true;
        if(bptr>0x3fffffff) return false;
        uint p=bptr<<2,start=Ptr(Locks);
        if(p<start||p>=start+1536||(p-start)%48!=0||m.Read32(p+32)==0) return false;
        slot=m.Read32(p+4); return true;
    }
    private StepResult MakeLock(uint slot)
    {
        uint p=R(ReservedResource); if(p==0) p=FreeResource(false); if(p==0) return Error(103);
        Zero(p,48); m.Write32(p+4,slot); m.Write32(p+8,0xfffffffe); m.Write32(p+12,R(Port)); m.Write32(p+16,R(Volume)>>2);
        m.Write32(p+20,1); m.Write32(p+24,slot); m.Write32(p+32,1); W(ActiveLocks,R(ActiveLocks)+1); return Finish(p>>2);
    }
    private bool Conflicts(uint slot,bool exclusive)
    {
        uint remaining=R(ActiveHandles);
        for(uint n=0;n<32&&remaining!=0;n++)
        {
            uint p=(Arena+Handles)+n*64;
            if(m.Read32(p)==0) continue; remaining--;
            if(m.Read32(p+4)==slot&&(exclusive||m.Read32(p+12)==2)) return true;
        }
        return false;
    }
    private StepResult OpenHandle(uint slot)
    {
        uint p=R(ReservedResource); if(p==0) return Error(103);
        uint serial=R(ResourceSerial)+1; if(serial>=0x08000000) return Error(103);
        W(ResourceSerial,serial); uint token=(serial<<5)|((p-Ptr(Handles))>>6);
        m.Write32(p+8,0); m.Write32(p,token); m.Write32(p+4,slot); m.Write32(p+12,R(Action)==1005?0u:R(Action)==1006?2u:1u);
        W(ActiveHandles,R(ActiveHandles)+1); uint fh=Arg(0)<<2; m.Write32(fh+8,R(Port)); m.Write32(fh+36,token);
        return Finish(uint.MaxValue);
    }
    private StepResult StartPath(uint lockBptr,uint bstr,uint mode)
    {
        uint state=Arena+State;
        if(!LockSlot(lockBptr,out uint slot)) return Error(211);
        if(bstr==0||bstr>0x3fffffff) return Error(115);
        uint p=bstr<<2; if(!ExternalRange(p,1)) return Error(115); uint length=m.Read8(p),start=0;
        if(!ExternalRange(p,length+1)) return Error(115);
        uint name=Arena+Path;
        for(uint n=0;n<length;n++)
        {
            byte c=m.Read8(p+1+n); m.Write8(name+n,c);
            // First prototype has ASCII names; reject unsupported encoding before mutation.
            if(c==0||c>=128) return Error(210);
            if(c==58) { start=n+1; slot=1; }
        }
        m.Write32(state+PathLength,length); m.Write32(state+PathMode,mode);
        return WalkPath(slot,start,length,mode,0);
    }
    // Carry the cursor through at most four resident directories. On a miss
    // the usual load continuation owns the exact directory and byte offset.
    private StepResult WalkPath(uint slot,uint at,uint length,uint mode,uint directory)
    {
        uint state=Arena+State,action=m.Read32(state+Action),names=Arena+Path;
        for(uint budget=0;budget<4;budget++) {
            if(directory==0) {
                int hit=Find(256+slot);
                if(hit<0) {
                    m.Write32(state+Current,slot); m.Write32(state+PathAt,at);
                    return Load(256+slot,21);
                }
                m.Write32(Meta((uint)hit)+12,m.Read32(state+Epoch)); directory=Image((uint)hit);
            }
            if(!ValidInode(directory,slot)||m.Read16(directory+6)!=11) return Error(212);
            if(at==length) return action==8?MakeLock(slot):Error(action==22?203u:212u);
            uint end=at,hash=0;
            while(end<length) {
                uint c=m.Read8(names+end); if(c==47) break;
                if(c is >=97 and <=122) c-=32;
                hash=unchecked((hash<<5|hash>>27)+c); end++;
            }
            if(end==at||end-at>168) return Error(210);
            uint entry=FindEntry(directory,names+at,end-at,hash);
            if(entry==uint.MaxValue) return Fatal();
            if(end<length) {
                if(entry==0) return Error(205);
                slot=m.Read32(entry+4); at=end+1; directory=0; continue;
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
            if(action==16) { W(Parent,directory); W(SourceEntry,entry-directory); W(Target,target); return Load(256+target,63); }
            if(action==17) {
                m.Write32(state+SourceParent,m.Read32(directory+20)); m.Write32(state+SourceEntry,entry-directory);
                m.Write32(Arena+RenameBuffer+4,m.Read32(entry+4)); m.Write32(Arena+RenameBuffer+8,m.Read32(entry+8)); m.Write32(Arena+RenameBuffer+12,m.Read32(entry+12));
                uint args=m.Read32(state+Packet)+20;
                return StartPath(m.Read32(args+8),m.Read32(args+12),2);
            }
            m.Write32(state+Target,target);
            if(action==28) return Load(2000+(target>>2),53);
            return Load(256+target,40);
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
            if(bytes==0||size>used-at||m.Read32(e)!=1||m.Read32(e+4)==0||m.Read32(e+4)>=1024) return uint.MaxValue;
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
                if(m.Read32(state+Poison)!=0) return Error(213);
                if(m.Read32(state+Mounted)==0) return Error(218);
                if(m.Read32(state+Inhibited)!=0&&action!=31&&action!=5) return Error(218);
                // At most eight new dirty pages per supported operation; pre-retire before admission.
                if(m.Read32(state+DirtyCount)>40||(action is 1006 or 1022)&&m.Read32(state+DirtyCount)>14||action==16&&m.Read32(state+DirtyCount)>12) return StartFlush(0);
                switch(action)
                {
                    case 27: return StartFlush(10);
                    case 31:
                        if(HasResources) return Error(202);
                        if(m.Read32(args+0)!=0) return StartFlush(11);
                        m.Write32(state+MountReturn,13); return Next(100);
                    case 5:
                        if(HasResources) return Error(202);
                        return StartFlush(12);
                    case 15:
                        if(m.Read32(args+0)==0) return Finish(uint.MaxValue);
                        if(!LockSlot(m.Read32(args+0),out _)) return Error(211);
                        m.Write32(state+ActiveLocks,m.Read32(state+ActiveLocks)-1); m.Write32((m.Read32(args+0)<<2)+32,0); return Finish(uint.MaxValue);
                    case 1007:
                        if(!HandlePointer(m.Read32(args+0),out uint close)) return Error(211);
                        m.Write32(state+ActiveHandles,m.Read32(state+ActiveHandles)-1); m.Write32(close,0); return Finish(uint.MaxValue);
                    case 1004: case 1005: case 1006:
                        if(m.Read32(args+0)>0x3fffffff||!ExternalRange(m.Read32(args+0)<<2,44)) return Error(115);
                        uint h=FreeResource(true); if(h==0||m.Read32(state+ResourceSerial)>=0x07ffffff) return Error(103);
                        m.Write32(state+ReservedResource,h); return StartPath(m.Read32(args+4),m.Read32(args+8),0);
                    case 22: case 8:
                        if(action==8&&m.Read32(args+8) is not (0xfffffffe or 0xffffffff)) return Error(115);
                        if(action==8&&m.Read32(args+8)==uint.MaxValue) return Error(209);
                        uint l=FreeResource(false); if(l==0) return Error(103);
                        m.Write32(state+ReservedResource,l); return StartPath(m.Read32(args+0),m.Read32(args+4),0);
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
                        return Load(256+child,68);
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
                        m.Write32(state+Target,examine); return Load(256+examine,action==24?60u:54u);
                    case 17: m.Write32(state+SourceParent,0); return StartPath(m.Read32(args+0),m.Read32(args+4),1);
                    case 82: case 87: case 1008: case 1022:
                        if(!HandlePointer(m.Read32(args+0),out uint handle)) return Error(211);
                        if(action is 87 or 1022&&m.Read32(handle+12)==0) return Error(223);
                        if(action is 82 or 87&&(m.Read32(args+8)>int.MaxValue||!ExternalRange(m.Read32(args+4),m.Read32(args+8)))) return Error(115);
                        uint objectId=m.Read32(handle+4); if(objectId is 0 or >=1024) return Fatal();
                        uint home=256+objectId,cached=m.Read8(Arena+Resident+home);
                        m.Write32(state+Handle,handle); m.Write32(state+Current,objectId);
                        if(cached!=0) {
                            uint slot=cached-1,meta=Arena+CacheMeta+(slot<<5);
                            if(m.Read32(meta+4)!=0) { m.Write32(meta+12,m.Read32(state+Epoch)); m.Write32(state+Loaded,Arena+CacheImages+(slot<<9)); goto handleReady; }
                        }
                        return Load(home,50);
                    case 1027: return Finish(1); // ACTION_IS_FILESYSTEM
                    default: return Error(209);
                }
            case 10: return Finish(uint.MaxValue);
            case 11: m.Write32(state+Inhibited,1); return Finish(uint.MaxValue);
            case 12: m.Write32(state+Mounted,0); return Finish(uint.MaxValue);
            case 13: m.Write32(state+Inhibited,0); m.Write32(state+Mounted,1); return Finish(uint.MaxValue);
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
                    uint head=m.Read32(group+84); if(head<2||head>1023-free) return Fatal();
                    W(Group,group); W(Target,head); return Load(256+head,61);
                }
                if(free==0) return Error(221);
                if(PackedVolume) { if(free>1022) return Fatal(); m.Write32(state+Target,1024-free); }
                return CreateAdmitted(group,false);
            }
            case 61:
            {
                uint group=R(Group),hole=R(Loaded);
                if(!ValidHole(hole,R(Target),1023-m.Read32(group+80))) return Fatal();
                return CreateAdmitted(group,true);
            }
            case 40:
            {
                uint inode=m.Read32(state+Loaded),slot=m.Read32(state+Target);
                if(!ValidInode(inode,slot)||m.Read16(inode+6)!=10) return Error(212);
                if(Conflicts(slot,action==1006)) return Error(202);
                if(action==1006&&m.Read32(inode+68)!=0)
                { m.Write32(state+Inode,inode); m.Write32(state+ResizeLength,0); m.Write32(state+Resume,41); return Load(128,400); }
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
                    return Load(256+m.Read32(Arena+RenameBuffer+4),62);
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
        int reserved=CacheSlot(256+m.Read32(state+Target)); if(reserved<0) return Error(103);
        uint inode=Image((uint)reserved),slot=m.Read32(state+Target),parent=m.Read32(state+Parent),used=m.Read32(parent+100);
        uint nextHole=reuse?m.Read32(inode+116):0;
        // All fallible admission is complete before the first edit.
        if(PackedVolume) {
            m.Write16(inode+6,action==22?11u:10u); m.Write32(inode+20,256+slot); m.Write32(inode+24,1); m.Write32(inode+28,slot);
            m.Write32(inode+64,0); m.Write32(inode+68,0); m.Write32(inode+96,0); m.Write32(inode+100,0); m.Write32(inode+116,0);
        } else { Zero(inode,512); Header(inode,action==22?11u:10u,256+slot,m.Read32(state+Generation)+1,slot); m.Write32(inode+76,1); m.Write32(inode+104,1); }
        m.Write32(inode+108,m.Read32(parent+28));
        if(!PackedVolume) { uint bitmap=m.Read32(state+Bitmap); SetBit(bitmap+128,slot,true); Dirty(bitmap); }
        uint serialLo=m.Read32(group+92)+1,serialHi=m.Read32(group+88)+(serialLo==0?1u:0u);
        m.Write32(group+88,serialHi); m.Write32(group+92,serialLo); if(reuse) m.Write32(group+84,nextHole); else m.Write32(group+80,m.Read32(group+80)-1); Dirty(group);
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
