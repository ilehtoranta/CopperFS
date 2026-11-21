using CopperCPX;
namespace CopperNova.Prototype;

public partial struct NovaEngine<TMemory> where TMemory : struct,ICpxMemory
{
    private const uint DataFloor=448, FetchSector=452, FetchType=456, FetchOwner=460, FetchReturn=464,
        RawKeys=468, Bucket=484, Fence=488, BucketListCount=492, BucketIndex=496, PairCursor=500, LeafCount=504,
        NeedOverflow=508, OldTree=512, GroupQueue=62000, SeenGroups=62128, OldLeaves=62200, NewLeaves=62232,
        Rows=62464, RawLeaf=57344, RawBranch=57856, RawOverflow=58368, Output=58880, RootPage=59392, OverflowWork=59904;
    private const uint ProofMask=520,ProofImages=71680,BitmapProof=552,RootChanges=556,BitmapChanges=560,BitmapImage=70656;
    private void RememberPage(uint page,uint lane)
    {
        m.Copy(Arena+ProofImages+(lane<<9),page,512);
        W(ProofMask,R(ProofMask)|(1u<<(int)lane));
    }
    private bool CheckPageCrc(uint page,uint type,uint sector)
    {
        if(type!=30&&type!=32) return m.Read32(page+8)==Crc(page,512,true);
        uint lane=type==30?sector:2u,copy=Arena+ProofImages+(lane<<9);
        if((R(ProofMask)&(1u<<(int)lane))!=0) {
            uint src=page,match=copy,end=page+512;
            while(src<end) {
                if(m.Read32(src)!=m.Read32(match)||m.Read32(src+4)!=m.Read32(match+4)||
                    m.Read32(src+8)!=m.Read32(match+8)||m.Read32(src+12)!=m.Read32(match+12)||
                    m.Read32(src+16)!=m.Read32(match+16)||m.Read32(src+20)!=m.Read32(match+20)||
                    m.Read32(src+24)!=m.Read32(match+24)||m.Read32(src+28)!=m.Read32(match+28)) break;
                src+=32; match+=32;
            }
            if(src==end) return true;
        }
        if(m.Read32(page+8)!=Crc(page,512,true)) return false;
        RememberPage(page,lane); return true;
    }
    private uint Root()=>Arena+RootPage;
    private uint BranchBase(uint group)=>66+group*2;
    private uint LeafBase(uint group,uint fence)=>194+group*16+fence*2;
    private uint OverflowBase(uint group)=>1218+group*2;
    private bool Pair(uint address,uint first)=>address==first||address==first+1;
    private uint Alternate(uint old,uint first)=>old==first?first+1:first;
    private uint Raw(uint type)=>Arena+(type==31?RawBranch:type==32?RawLeaf:RawOverflow);
    private uint KeyAt(uint type)=>RawKeys+(type-31)*4;
    private uint Tree(uint group)=>m.Read16(Root()+160+group*2);
    private uint Extra(uint group)=>m.Read16(Root()+288+group*2);
    private uint Row(uint index)=>Arena+Rows+index*224;

    // Fold the four big-endian byte lanes with the existing reflected CRC table.
    private uint FoldWriteCrc(uint high, uint upper, uint lower, uint low)
    {
        uint table = Arena + CrcTable;
        uint crc = (high >> 8) ^ m.Read32(table + ((high & 255) << 2)) ^ upper;
        crc = (crc >> 8) ^ m.Read32(table + ((crc & 255) << 2)) ^ lower;
        return (crc >> 8) ^ m.Read32(table + ((crc & 255) << 2)) ^ low;
    }

    private uint LeafWriteCrc(uint page, uint end)
    {
        // Stamp owns this unpublished image and replaces its CRC before any I/O.
        // Zeroing the field once keeps it out of every word's inner-loop test.
        m.Write32(page + 8, 0);
        uint row = Arena + WordTable;
        // CRC-32C of 512 zero bytes supplies the affine offset. Each 128-byte
        // row stores the low- and high-nibble weights for one word position.
        uint high = 0, upper = 0, lower = 0, low = 0x30fcedc0;
        // The encoder has already cleared the suffix. The row weights still
        // advance each byte through its full position in a 512-byte sector.
        for (uint src = page; src < end; src += 4, row += 128)
        {
            uint word = m.Read32(src);
            if (word == 0) continue;
            if (word < 16)
            {
                low ^= m.Read32(row + (word << 2));
                continue;
            }
            if ((word & 255) != 0)
                low ^= m.Read32(row + ((word << 2) & 60)) ^ m.Read32(row + 64 + ((word >> 2) & 60));
            if (word <= 255) continue;
            uint second = m.Read8(src + 2);
            if (second != 0)
                lower ^= m.Read32(row + ((second << 2) & 60)) ^ m.Read32(row + 64 + ((second >> 2) & 60));
            if (word <= 65535) continue;
            uint third = m.Read8(src + 1);
            if (third != 0)
                upper ^= m.Read32(row + ((third << 2) & 60)) ^ m.Read32(row + 64 + ((third >> 2) & 60));
            uint fourth = m.Read8(src);
            if (fourth != 0)
                high ^= m.Read32(row + ((fourth << 2) & 60)) ^ m.Read32(row + 64 + ((fourth >> 2) & 60));
        }
        return FoldWriteCrc(high, upper, lower, low);
    }

    private uint PatchedCrc(uint page, uint reference, uint mask)
    {
        uint row = Arena + WordTable;
        uint high = 0, upper = 0, lower = 0, low = m.Read32(reference + 8);
        // Matching the reference checksum makes the checksum word contribute zero
        // to the delta. Stamp has exclusive ownership of the candidate page.
        m.Write32(page + 8, low);
        // Start at the CRC of the exact verified reference. Each bit covers
        // eight longwords. Advance both cursors and their table row together.
        for (mask &= 0xffff; mask != 0; mask >>= 1)
        {
            if ((mask & 1) == 0)
            {
                page += 32;
                reference += 32;
                row += 1024;
                continue;
            }
            uint end = page + 32;
            for (; page < end; page += 4, reference += 4, row += 128)
            {
                uint word = m.Read32(page) ^ m.Read32(reference);
                if (word == 0) continue;
                if (word < 16)
                {
                    low ^= m.Read32(row + (word << 2));
                    continue;
                }
                if ((word & 255) != 0)
                    low ^= m.Read32(row + ((word << 2) & 60)) ^ m.Read32(row + 64 + ((word >> 2) & 60));
                word >>= 8;
                if (word == 0) continue;
                if ((word & 255) != 0)
                    lower ^= m.Read32(row + ((word << 2) & 60)) ^ m.Read32(row + 64 + ((word >> 2) & 60));
                word >>= 8;
                if (word == 0) continue;
                upper ^= m.Read32(row + ((word << 2) & 60)) ^ m.Read32(row + 64 + ((word >> 2) & 60));
                word >>= 8;
                if (word != 0)
                    high ^= m.Read32(row + ((word << 2) & 60)) ^ m.Read32(row + 64 + ((word >> 2) & 60));
            }
        }
        return FoldWriteCrc(high, upper, lower, low);
    }

    private void RootPointer(uint at,uint value)
    {
        m.Write16(at,value);
        uint state=Arena+State;
        m.Write32(state+RootChanges,m.Read32(state+RootChanges)|(1u<<(int)((at-(Arena+RootPage))>>5)));
    }
    private void Stamp(uint p,uint type,uint sector,uint owner,uint gen)
    {
        m.Write32(p,PackedMagic); if(type!=30) m.Write16(p+4,1); m.Write16(p+6,type); m.Write32(p+12,sector); m.Write32(p+16,gen); m.Write32(p+20,owner);
        m.Write32(p+24,R(IdentityHi)); m.Write32(p+28,R(IdentityLo));
        // Root stamping touches the header and counts. Bitmap decoding also
        // rewrites the virtual header and window base in the first 68 bytes.
        uint prior=0,mask=0;
        if(!WideVolume&&type==30&&(R(ProofMask)&(1u<<(int)(1-sector)))!=0) { prior=Arena+ProofImages+((1-sector)<<9); mask=5|R(RootChanges); }
        if(!WideVolume&&type==34&&R(BitmapProof)==owner+1) { prior=Arena+BitmapImage; mask=7|R(BitmapChanges); }
        if(prior!=0) m.Write32(p+8,PatchedCrc(p,prior,mask));
        else if(type==32) m.Write32(p+8,LeafWriteCrc(p,p+m.Read16(p+32)));
        else Seal(p);
        if(type==30) W(RootChanges,0);
        if(type==34) { W(BitmapChanges,0); m.Copy(Arena+BitmapImage,p,512); W(BitmapProof,owner+1); }
        if(type==30||type==32) RememberPage(p,type==30?sector:2u);
    }
    private bool RootValid(uint p,uint bank)
    {
        uint version=m.Read16(p+4),blocks=R(Blocks),floor=version==3?WideDataFloor:1394;
        return m.Read32(p)==PackedMagic&&version is >=1 and <=3&&m.Read16(p+6)==30&&m.Read32(p+12)==bank&&
            (m.Read32(p+16)&1)==bank&&m.Read32(p+20)==0&&m.Read32(p+64)==blocks&&m.Read32(p+68)==floor&&
            floor<blocks&&(version==3?Pair(m.Read16(p+96),WideTopStart):blocks<=65536)&&
            m.Read32(p+72)<=blocks-floor&&m.Read32(p+76) is >=1 and <=1023&&m.Read32(p+88)<=m.Read32(p+76)&&
            m.Read32(p+88)!=1&&(version>=2||m.Read32(p+88)==0)&&CheckPageCrc(p,30,bank);
    }
    private StepResult SelectRoot(uint p)
    {
        bool first=RootValid(p,0),second=RootValid(p+512,1);
        if(!first&&!second||first&&second&&(m.Read16(p+4)!=m.Read16(p+516)||m.Read32(p+24)!=m.Read32(p+536)||m.Read32(p+28)!=m.Read32(p+540))) return Fatal();
        uint bank=second&&(!first||m.Read32(p+528)>m.Read32(p+16))?1u:0;
        p+=bank*512; W(BitmapProof,0); W(RootChanges,0); m.Copy(Root(),p,512); W(DataFloor,m.Read32(p+68)); W(Bank,bank); W(Generation,m.Read32(p+16));
        W(IdentityHi,m.Read32(p+24)); W(IdentityLo,m.Read32(p+28)); Zero(Arena+State+RawKeys,12);
        W(WideTopDirty,0);
        if(WideVolume)
        {
            Zero(Ptr(WideIndex),128);
            return Issue(IoKind.Read,m.Read16(p+96),Ptr(WideTopImage),512,1122);
        }
        return Next(115);
    }
    private StepResult Fetch(uint sector,uint type,uint owner,uint next)
    {
        uint basis=type==31?BranchBase(owner):type==32?194+owner*2:OverflowBase(owner);
        if(!Pair(sector,basis)) return Fatal();
        if(R(KeyAt(type))==sector) return Next(next);
        W(FetchSector,sector); W(FetchType,type); W(FetchOwner,owner); W(FetchReturn,next);
        return Issue(IoKind.Read,sector,Raw(type),512,1110);
    }
    private bool PageValid(uint p,uint type,uint sector,uint owner)=>m.Read32(p)==PackedMagic&&m.Read16(p+4)==1&&m.Read16(p+6)==type&&
        m.Read32(p+12)==sector&&m.Read32(p+16)<=R(Generation)&&m.Read32(p+20)==owner&&m.Read32(p+24)==R(IdentityHi)&&
        m.Read32(p+28)==R(IdentityLo)&&CheckPageCrc(p,type,sector);
    private bool RecordOK(uint rec,uint left,uint group,uint previous)
    {
        if(left<24) return false;
        uint id=m.Read16(rec),kind=m.Read8(rec+2),len=m.Read16(rec+4),count=m.Read8(rec+3),size=m.Read32(rec+8);
        if(id<=previous||id>=1024||(id>>4)!=group||count>8||len<24||len>left||len>216||(len&3)!=0||m.Read16(rec+6) is 0 or >=1024) return false;
        if(kind==12) return ReusableIds()&&id!=1&&len==24&&count==0&&size==0&&m.Read16(rec+6)==1&&ValidNextHole(id,m.Read32(rec+12),m.Read32(Root()+76));
        if(kind==10)
        {
            uint disk=WideVolume?m.Read32(rec+16):m.Read16(rec+16);
            uint run=WideVolume?m.Read32(rec+20)&0x7fffffffu:m.Read16(rec+18);
            if(WideVolume&&count<=1&&(m.Read32(rec+20)&0x80000000u)!=0) return false;
            return len==24&&(count==0?size==0&&run==0:size!=0&&ExtentOK(disk,run));
        }
        if(kind!=11||size!=0) return false;
        uint at=24,entries=0;
        while(at<len) { if(len-at<24) return false; uint name=m.Read16(rec+at+20),step=(24+name+7)&~7u;
            if(name==0||name>168||step>len-at||m.Read32(rec+at)!=1||m.Read32(rec+at+4) is 0 or >=1024) return false;
            at+=step; entries++; }
        return entries==count;
    }
    private bool ExtentOK(uint disk,uint run)=>disk>=DataStart&&disk<R(Blocks)&&run!=0&&run<=R(Blocks)-disk;
    private StepResult Fetched()
    {
        uint state=Arena+State;
        uint type=m.Read32(state+FetchType),sector=m.Read32(state+FetchSector),owner=m.Read32(state+FetchOwner),p=Raw(type);
        if(!PageValid(p,type,sector,owner)) return Fatal();
        if(type==31) { if(m.Read16(p+32)==0) return Fatal(); for(uint i=0;i<8;i++){ uint child=m.Read16(p+32+i*2); if(child!=0&&!Pair(child,LeafBase(owner,i))) return Fatal(); } }
        if(type==32) { uint end=m.Read16(p+32),at=40,previous=0; if(end<=40||end>512) return Fatal();
            while(at<end) { if(!RecordOK(p+at,end-at,owner>>3,previous)) return Fatal(); previous=m.Read16(p+at); if((previous>>1)<owner) return Fatal(); at+=m.Read16(p+at+4); } }
        W(KeyAt(type),sector); return Next(m.Read32(state+FetchReturn));
    }
    private StepResult LoadedPacked()
    { uint slot=R(LoadSlot); m.Write32(Meta(slot)+4,1); W(Loaded,Image(slot)); return Next(R(LoadNext)); }
    private StepResult PackedLoad(uint phase)
    {
        uint state=Arena+State;
        uint home=m.Read32(state+LoadHome),p=Image(m.Read32(state+LoadSlot)),root=Root(),id=home-256,group=id>>4;
        switch(phase)
        {
            case 1120:
                W(WideActiveSelector,R(Loaded));
                return LoadHomePage(WideBitmapKey+R(WideMap),R(WideReturn));
            case 1121: return LoadWidePage(phase,home,p);
            case 1122: return PageValid(Ptr(WideTopImage),WideTopType,m.Read16(root+96),0)?Next(115):Fatal();
            case 1123:
            {
                uint extra=Ptr(RawOverflow),sector=WideExtentBase(id)+m.Read32(p+WideExtentBank);
                if(!PageValid(extra,WideExtentType,sector,id)) return Fatal();
                uint wideLogical=m.Read32(p+336),wideCount=m.Read32(p+96);
                for(uint i=1;i<wideCount;i++)
                {
                    uint at=extra+32+(i-1)*8,disk=m.Read32(at),run=m.Read32(at+4);
                    if(!ExtentOK(disk,run)||run>uint.MaxValue-wideLogical) return Fatal();
                    uint extent=ExtentAt(p,i); m.Write32(extent+4,wideLogical); m.Write32(extent+12,disk); m.Write32(extent+16,run);
                    wideLogical+=run;
                }
                return wideLogical==(m.Read32(p+68)+511)>>9?LoadedPacked():Fatal();
            }
            case 1100:
                if(home>=WideSelectorKey&&WideVolume) return LoadWidePage(phase,home,p);
                if(home is >=2000 and <2256) return LoadCommentPage(home,p);
                if(home==128||home==129) {
                    if(home==129) Zero(p,512); Header(p,home==128?3u:5u,home,m.Read32(state+Generation)); uint last=m.Read32(root+76);
                    if(home==129) SetRange(p+128,0,last+1,true);
                    else { m.Write32(p+64,m.Read32(state+Blocks)); m.Write32(p+68,DataStart); m.Write32(p+72,m.Read32(root+72)); m.Write32(p+80,1023-last); m.Write32(p+84,m.Read32(root+88)); m.Write32(p+88,m.Read32(root+80)); m.Write32(p+92,m.Read32(root+84)); }
                    return LoadedPacked(); }
                if(home is >=130 and <=161) { uint index=home-130,sector=m.Read16(root+96+index*2); if(!Pair(sector,2+index*2)) return Fatal(); return Issue(IoKind.Read,sector,p,512,1101); }
                if(id==0||id>m.Read32(root+76)) return Fatal();
                uint tree=Tree(group); if(Pair(tree,BranchBase(group))) return Fetch(tree,31,group,1102);
                return Fetch(tree,32,group*8,1103);
            case 1105: return AcceptCommentPage(home,p);
            case 1101:
                uint map=home-130,physical=m.Read16(root+96+map*2);
                if(!PageValid(p,34,physical,map)) return Fatal();
                m.Copy(Arena+BitmapImage,p,512); m.Write32(state+BitmapProof,map+1); m.Write32(state+BitmapChanges,0);
                Header(p,4,home,m.Read32(state+Generation)); m.Write32(p+64,map*2048); return LoadedPacked();
            case 1102:
                uint fence=(id&15)>>1; while(fence!=0&&m.Read16(Raw(31)+32+fence*2)==0) fence--;
                return Fetch(m.Read16(Raw(31)+32+fence*2),32,group*8+fence,1103);
            case 1103:
                uint leaf=Raw(32),end=m.Read16(leaf+32),record=leaf+40;
                while(record<leaf+end&&m.Read16(record)!=id) record+=m.Read16(record+4);
                if(record>=leaf+end) return Fatal();
                uint kind=m.Read8(record+2),count=m.Read8(record+3),bytes=m.Read16(record+4)-24u;
                m.Write32(p+64,0); m.Write32(p+100,0); if(kind==10) Zero(p+320,count*24); m.Write16(p+6,kind); m.Write32(p+20,home); m.Write32(p+24,1); m.Write32(p+28,id); m.Write32(p+108,m.Read16(record+6));
                m.Write32(p+68,m.Read32(record+8)); m.Write32(p+116,m.Read32(record+12)); m.Write32(p+96,count);
                m.Write32(p+WideExtentBank,0);
                if(kind==11) { m.Write32(p+100,bytes); m.Copy(p+320,record+24,bytes); }
                else if(count!=0&&WideVolume)
                {
                    uint runAndSide=m.Read32(record+20);
                    m.Write32(p+332,m.Read32(record+16)); m.Write32(p+336,runAndSide&0x7fffffffu);
                    if(count>1)
                    {
                        uint side=runAndSide>>31; m.Write32(p+WideExtentBank,side);
                        return Issue(IoKind.Read,WideExtentBase(id)+side,Ptr(RawOverflow),512,1123);
                    }
                    if(m.Read32(p+336)!=(m.Read32(p+68)+511)>>9) return Fatal();
                }
                else if(count!=0) { m.Write32(p+332,m.Read16(record+16)); m.Write32(p+336,m.Read16(record+18));
                    if(count>1) return Fetch(Extra(group),33,group,1104);
                    if(m.Read32(p+336)!=(m.Read32(p+68)+511)>>9) return Fatal(); }
                return LoadedPacked();
            case 1104:
                uint logical=m.Read32(p+336),overflow=Raw(33)+64+(id&15)*28;
                for(uint i=1;i<m.Read32(p+96);i++) { uint disk=m.Read16(overflow),run=m.Read16(overflow+2); if(!ExtentOK(disk,run)) return Fatal();
                    uint extent=p+320+i*24; m.Write32(extent+4,logical); m.Write32(extent+12,disk); m.Write32(extent+16,run); logical+=run; overflow+=4; }
                if(logical!=(m.Read32(p+68)+511)>>9) return Fatal(); return LoadedPacked();
            default: return Fatal();
        }
    }
    private void EncodeRow(uint rec,uint p,uint id)
    {
        uint kind=m.Read16(p+6),bytes=kind==11?m.Read32(p+100):0,count=m.Read32(p+96);
        EncodeRow(rec,p,id,kind,count,bytes);
    }
    private void EncodeRow(uint rec,uint p,uint id,uint kind,uint count,uint bytes)
    {
        m.Write16(rec,id); m.Write16(rec+2,(kind<<8)|(byte)count); m.Write16(rec+4,24+bytes); m.Write16(rec+6,m.Read16(p+110));
        m.Write32(rec+8,m.Read32(p+68)); m.Write32(rec+12,m.Read32(p+116)); m.Write32(rec+20,0);
        if(kind==10&&count!=0&&WideVolume)
        {
            m.Write32(rec+16,m.Read32(p+332));
            m.Write32(rec+20,m.Read32(p+336)|(count>1?m.Read32(p+WideExtentBank)<<31:0));
        }
        else if(kind==10&&count!=0) { m.Write16(rec+16,m.Read16(p+334)); m.Write16(rec+18,m.Read16(p+338)); }
        else m.Write32(rec+16,0);
        if(kind==11) m.Copy(rec+24,p+320,bytes);
    }
    private bool EncodeResidentLeaf(uint group,uint output)
    {
        uint end=m.Read32(Root()+76),first=group<<4,at=40;
        if(first==0) first=1;
        if(end>group*16+15) end=group*16+15;
        if(end<first) return false;
        for(uint id=first;id<=end;id++) {
            int slot=Find(256+id); if(slot<0) return false;
            uint inode=Image((uint)slot),type=m.Read16(inode+6),count=m.Read32(inode+96);
            if(!ValidInodeIdentity(inode,id)||type is not (10 or 11)||count>8||(!WideVolume&&type==10&&count>1)) return false;
            uint bytes=type==11?m.Read32(inode+100):0;
            if(bytes>192||24+bytes>512-at) return false;
            EncodeRow(output+at,inode,id,type,count,bytes); at+=24+bytes;
        }
        Zero(output,40); Zero(output+at,512-at); m.Write16(output+32,at); return true;
    }
    private StepResult PackedStep(uint phase)
    {
        uint state=Arena+State;
        if(phase==1110) return Fetched();
        if(phase<1200) return PackedLoad(phase);
        uint root=Root(),output=Arena+Output,oldLeaves=Arena+OldLeaves,newLeaves=Arena+NewLeaves,group=m.Read32(state+Bucket),gen=m.Read32(state+NewGeneration);
        switch(phase)
        {
            case 1200:
                if(m.Read32(state+Generation)==uint.MaxValue||m.Read32(state+DirtyCount)>48) return Fatal();
                m.Write32(state+NewGeneration,m.Read32(state+Generation)+1); m.Write32(state+Index,0); m.Write32(state+BucketIndex,0); m.Write32(state+BucketListCount,0); Zero(Arena+SeenGroups,64); return Next(1201);
            case 1201:
                uint index=m.Read32(state+Index);
                if(index<m.Read32(state+DirtyCount)) {
                    uint slot=m.Read8(Arena+DirtySlots+index),entry=Arena+CacheMeta+(slot<<5);
                    uint home=m.Read32(entry),p=Image(slot); m.Write32(state+Index,index+1);
                    if(home is >=2000 and <2256) return WriteCommentPage(home,p,gen);
                    if(home>=WideSelectorKey&&WideVolume) return WriteWidePage(home,entry,p,gen);
                    if(!ValidHomeType(p,home)&&!(ReusableIds()&&home>=258&&home<1280&&ValidHole(p,home-256,1023))) return Fatal();
                    if(home==128) { m.Write32(root+72,m.Read32(p+72)); m.Write32(root+76,1023-m.Read32(p+80)); m.Write32(root+80,m.Read32(p+88)); m.Write32(root+84,m.Read32(p+92)); m.Write32(root+88,m.Read32(p+84)); }
                    else if(home is >=130 and <=161) { uint map=home-130,pointer=root+96+map*2,sector=Alternate(m.Read16(pointer),2+map*2);
                        m.Copy(output,p,512); Stamp(output,34,sector,map,gen); RootPointer(pointer,sector); return Issue(IoKind.Write,sector,output,512,1201); }
                    else if(home>256) { uint key=(home-256)>>4; if(m.Read8(Arena+SeenGroups+key)==0) { uint n=m.Read32(state+BucketListCount); m.Write8(Arena+SeenGroups+key,1); m.Write8(Arena+GroupQueue+n,(byte)key); m.Write32(state+BucketListCount,n+1); }
                        if(WideVolume&&m.Read16(p+6)==10&&m.Read32(p+96)>1) return WriteWideExtentPage(home-256,p,gen); }
                    return StepResult.Pending;
                }
                if(WideVolume&&R(WideTopDirty)!=0)
                {
                    uint top=Ptr(WideTopImage),sector=Alternate(m.Read16(root+96),WideTopStart);
                    RootPointer(root+96,sector); W(WideTopDirty,0);
                    Stamp(top,WideTopType,sector,0,gen);
                    return Issue(IoKind.Write,sector,top,512,1202);
                }
                return Next(1202);
            case 1202:
                uint curr=m.Read32(state+BucketIndex); if(curr==m.Read32(state+BucketListCount)) return Issue(IoKind.Flush,0,0,0,1212);
                group=m.Read8(Arena+GroupQueue+curr); m.Write32(state+Bucket,group); m.Write32(state+BucketIndex,curr+1); m.Write32(state+Fence,0); m.Write32(state+LeafCount,0); m.Write32(state+PairCursor,0); m.Write32(state+NeedOverflow,0);
                uint prior=Tree(group);
                if((prior==0||Pair(prior,LeafBase(group,0)))&&Extra(group)==0&&EncodeResidentLeaf(group,output)) {
                    uint sector=Alternate(prior,LeafBase(group,0)); Stamp(output,32,sector,group*8,gen);
                    RootPointer(root+160+group*2,sector); return Issue(IoKind.Write,sector,output,512,1202);
                }
                Zero(oldLeaves,16); Zero(newLeaves,16); for(uint i=0;i<16;i++) m.Write16(Row(i)+4,0);
                uint tree=Tree(group); m.Write32(state+OldTree,tree);
                if(tree==0) return Next(1206);
                if(Pair(tree,BranchBase(group))) return Fetch(tree,31,group,1203);
                if(!Pair(tree,LeafBase(group,0))) return Fatal(); m.Write16(oldLeaves,tree); return Next(1204);
            case 1203: m.Copy(oldLeaves,Raw(31)+32,16); return Next(1204);
            case 1204:
                for(uint i=m.Read32(state+Fence);i<8;i++) { uint child=m.Read16(oldLeaves+i*2); if(child!=0) { m.Write32(state+Fence,i+1); return Fetch(child,32,group*8+i,1205); } }
                return Next(1206);
            case 1205:
                uint leaf=Raw(32),end=m.Read16(leaf+32);
                for(uint off=40;off<end;) { uint rec=leaf+off,idx=m.Read16(rec)&15,len=m.Read16(rec+4),dest=Row(idx);
                    if(m.Read16(dest+4)!=0) return Fatal(); m.Copy(dest,rec,len); off+=len; }
                return Next(1204);
            case 1206:
                for(uint i=0;i<16;i++) { uint id=group*16+i; int slot=Find(256+id); uint row=Row(i);
                    if(slot>=0&&m.Read32(Meta((uint)slot)+8)!=0) { uint p=Image((uint)slot); if(m.Read32(p+100)>192) return Fatal(); EncodeRow(row,p,id); }
                    if(!WideVolume&&m.Read16(row+4)!=0&&m.Read8(row+2)==10&&m.Read8(row+3)>1) m.Write32(state+NeedOverflow,1); }
                if(m.Read32(state+NeedOverflow)!=0&&Extra(group)!=0) return Fetch(Extra(group),33,group,1207);
                if(m.Read32(state+NeedOverflow)!=0) Zero(Arena+OverflowWork,512);
                return Next(1208);
            case 1207: m.Copy(Arena+OverflowWork,Raw(33),512); return Next(1208);
            case 1208:
                if(m.Read32(state+NeedOverflow)==0) { RootPointer(root+288+group*2,0); return Next(1209); }
                uint extra=Arena+OverflowWork;
                for(uint i=0;i<16;i++) { int slot=Find(256+group*16+i); if(slot<0||m.Read32(Meta((uint)slot)+8)==0) continue;
                    uint p=Image((uint)slot),dst=extra+64+i*28; Zero(dst,28);
                    if(m.Read16(p+6)==10) for(uint j=1;j<m.Read32(p+96);j++) { uint e=p+320+j*24; m.Write16(dst,m.Read32(e+12)); m.Write16(dst+2,m.Read32(e+16)); dst+=4; } }
                uint spill=Alternate(Extra(group),OverflowBase(group)); Stamp(extra,33,spill,group,gen); RootPointer(root+288+group*2,spill);
                return Issue(IoKind.Write,spill,extra,512,1209);
            case 1209:
                uint first=m.Read32(state+PairCursor); if(first==8) return Next(1210);
                Zero(output,512); uint at=40,pair=first;
                for(;pair<8;pair++) { uint left=Row(pair*2),right=Row(pair*2+1),a=m.Read16(left+4),b=m.Read16(right+4);
                    if(a+b>512-at) break; if(a!=0) { m.Copy(output+at,left,a); at+=a; } if(b!=0) { m.Copy(output+at,right,b); at+=b; } }
                if(at==40||pair==first) return Fatal();
                uint phys=Alternate(m.Read16(oldLeaves+first*2),LeafBase(group,first)); m.Write16(newLeaves+first*2,phys); m.Write16(output+32,at);
                Stamp(output,32,phys,group*8+first,gen); m.Write32(state+LeafCount,m.Read32(state+LeafCount)+1); m.Write32(state+PairCursor,pair); return Issue(IoKind.Write,phys,output,512,1209);
            case 1210:
                if(m.Read32(state+LeafCount)==1) { RootPointer(root+160+group*2,m.Read16(newLeaves)); return Next(1202); }
                uint branch=Alternate(m.Read32(state+OldTree),BranchBase(group)); Zero(output,512); m.Copy(output+32,newLeaves,16);
                Stamp(output,31,branch,group,gen); RootPointer(root+160+group*2,branch); return Issue(IoKind.Write,branch,output,512,1202);
            case 1212:
                if(WideVolume) m.Write32(root+100,R(AllocationHint));
                uint bank=1-m.Read32(state+Bank); Stamp(root,30,bank,0,gen); m.Write32(state+Bank,bank); return Issue(IoKind.Write,bank,root,512,1213);
            case 1213: return Issue(IoKind.Flush,0,0,0,1214);
            case 1214: Zero(Arena+State+RawKeys,12); return Next(211);
            default: return Fatal();
        }
    }
}
