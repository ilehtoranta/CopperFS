using System.Runtime.CompilerServices;
using CopperCPX;

namespace CopperNova.Prototype;

// Experimental format. All mutable execution state is caller-owned.
// Production CPX engines are not used; only the existing memory/I/O value ABI is shared.
public partial struct NovaEngine<TMemory> where TMemory : struct, ICpxMemory
{
    private TMemory m;
    public uint Arena;
    public const uint ArenaBytes=115200, Magic=0x434E4D31, PackedMagic=0x434E5032;
    public const uint FirstData=1280;
    private uint DataStart=>R(DataFloor);
    public uint VolumeMagic=>PackedVolume?PackedMagic:Magic;
    private const uint State=8192, Handles=9216, Locks=11264, Path=12800, OtherPath=13056,
        RenameBuffer=13312, CacheMeta=14336, CacheImages=16384,
        Descriptors=49152, Controls=50176, Scratch=53248, CrcTable=90112,
        FlushList=54784, JournalImages=57344, Quarantine=81920;
    private const uint Phase=0, BusyAt=4, Waiting=8, Token=12, NextToken=16,
        Poison=20, Mounted=24, Generation=28, Blocks=32, IdentityHi=36, IdentityLo=40,
        Packet=44, Port=48, Volume=52, ResultAt=56, ErrorAt=60, Epoch=64,
        Loaded=68, LoadSlot=72, LoadNext=76, LoadHome=80, DirtyCount=84,
        Resume=88, Index=92, Count=96, Bank=100, NewGeneration=104,
        SelectedCommit=108, SelectedCrc=112, DescriptorDigest=116,
        Inhibited=120, Action=124, Handle=128, ReservedResource=132,
        Current=136, Parent=140, PathLength=144, PathAt=148, ComponentAt=152,
        ComponentBytes=156, Entry=160, SourceParent=164, SourceEntry=168,
        Target=172, PathMode=176, AllocationHint=180, DataProgress=184,
        DataBytes=188, DataBuffer=192, DataTake=196, DataPhysical=200,
        DataIntra=204, Inode=208, NewAllocation=212, Bitmap=216, Group=220,
        Wanted=224, Candidate=228, Searched=232, AllocationUnits=236,
        OldPosition=240, FreePending=244, ResizeLength=248, ExtentIndex=252,
        ResizeCursor=256, ResizeRemaining=260, ResizeFree=264, ResourceSerial=268,
        MountReturn=272, ClearAt=276, CancelAt=280, CrcReady=284, CrcWord=288, CacheHand=292,
        QuarantineEpoch=296, QuarantineTags=320, DirtySlots=13760;

    public NovaEngine(TMemory memory,uint arena) { m=memory; Arena=arena; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private uint R(uint o)=>m.Read32(Arena+State+o);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private void W(uint o,uint v)=>m.Write32(Arena+State+o,v);
    private uint Ptr(uint o)=>Arena+o;
    private uint Meta(uint slot)=>Ptr(CacheMeta)+(slot<<5);
    private uint Image(uint slot)=>Ptr(CacheImages)+(slot<<9);
    private uint BankBase(uint bank)=>bank==0?8u:64u;
    private uint CommitHome(uint bank)=>BankBase(bank)+50;
    public bool Busy=>R(BusyAt)!=0;
    public bool HasOutstandingIo=>R(Waiting)!=0;
    public uint Result=>R(ResultAt);
    public uint DosError=>R(ErrorAt);
    public uint DirtyPages=>R(DirtyCount);
    public bool IsMounted=>R(Mounted)!=0&&R(Poison)==0;
    public CpxIo Request=>new() { Kind=(IoKind)m.Read32(Arena+136), Sector=new(m.Read32(Arena+140),m.Read32(Arena+144)), Buffer=m.Read32(Arena+148), Bytes=m.Read32(Arena+152), Token=m.Read32(Arena+156) };
    public void Bind(uint blocks,uint port,uint volume) => Bind(blocks,port,volume,0);
    public void Bind(uint blocks,uint port,uint volume,uint unit) { W(Blocks,blocks); W(Port,port); W(Volume,volume); W(UnitNumber,unit); }
    private const uint ActiveHandles=528,ActiveLocks=532,ComponentHash=536,CacheUsed=540,CrcLow=544,CrcHigh=548;
    public bool HasResources=>R(ActiveHandles)!=0||R(ActiveLocks)!=0;
    public CpxError BeginMount() => BeginMount(false);
    public CpxError BeginMount(bool probeBlankCnp2)
    {
        if(Busy||HasOutstandingIo||HasResources||R(DirtyCount)!=0) return CpxError.Busy;
        W(FormatState,probeBlankCnp2?1u:0u);
        W(Packet,0); W(BusyAt,1); W(Phase,100); W(MountReturn,900); return CpxError.None;
    }
    public CpxError BeginPacket(uint packet)
    {
        uint state=Arena+State;
        if(m.Read32(state+BusyAt)!=0||m.Read32(state+Waiting)!=0) return CpxError.Busy;
        if((packet&1)!=0||!ExternalRange(packet,48)) return CpxError.InvalidArgument;
        uint epoch=m.Read32(state+Epoch); if(epoch==uint.MaxValue) return CpxError.ResourceExhausted;
        m.Write32(state+Packet,packet); m.Write32(state+Action,m.Read32(packet+8)); m.Write32(state+BusyAt,1); m.Write32(state+Phase,0);
        m.Write32(state+ResultAt,0); m.Write32(state+ErrorAt,0); m.Write32(state+CancelAt,0); m.Write32(state+ReservedResource,0); m.Write32(state+DataProgress,0);
        m.Write32(state+Epoch,epoch+1); return CpxError.None;
    }
    public void Cancel() { W(CancelAt,1); }
    public CpxError CompleteIo(uint token,uint bytes,bool success)
    {
        uint state=Arena+State;
        if(m.Read32(state+Waiting)==0||token!=m.Read32(state+Token)) return CpxError.StaleCompletion;
        m.Write32(state+Waiting,0);
        if(!success||bytes!=m.Read32(Arena+152)||m.Read32(state+CancelAt)!=0)
        { m.Write32(state+Poison,1); m.Write32(state+ErrorAt,213); m.Write32(state+Phase,901); return CpxError.Io; }
        return CpxError.None;
    }
    private StepResult Issue(IoKind kind,uint sector,uint buffer,uint bytes,uint next)
    {
        uint state=Arena+State,previous=m.Read32(state+NextToken),blocks=m.Read32(state+Blocks);
        if(m.Read32(state+Waiting)!=0||previous==uint.MaxValue) return Fatal();
        if(kind!=IoKind.Flush&&(bytes==0||(bytes&511)!=0||sector>=blocks||(bytes>>9)>blocks-sector||buffer==0||bytes>uint.MaxValue-buffer)) return Fatal();
        uint token=previous+1; m.Write32(state+NextToken,token); m.Write32(state+Token,token); m.Write32(state+Phase,next);
        m.Write32(Arena+136,(uint)kind); m.Write32(Arena+140,0); m.Write32(Arena+144,sector);
        m.Write32(Arena+148,buffer); m.Write32(Arena+152,bytes); m.Write32(Arena+156,token);
        m.Write32(state+Waiting,1); return StepResult.NeedIo;
    }
    private StepResult Next(uint phase) { W(Phase,phase); return StepResult.Pending; }
    private uint Arg(uint n)=>m.Read32(R(Packet)+20+n*4);
    private bool ExternalRange(uint p,uint bytes)
    {
        if(bytes==0) return true;
        return p!=0&&bytes<=uint.MaxValue-p&&(p>=Arena+ArenaBytes||p+bytes<=Arena);
    }
    private StepResult Finish(uint result,uint error=0)
    {
        uint state=Arena+State,packet=m.Read32(state+Packet);
        m.Write32(state+ResultAt,result); m.Write32(state+ErrorAt,error); m.Write32(state+BusyAt,0);
        if(packet!=0) { m.Write32(packet+12,result); m.Write32(packet+16,error); }
        return StepResult.Complete;
    }
    private StepResult Error(uint error)=>Finish(R(Action) is 82 or 87 or 1008 or 1022?uint.MaxValue:0,error);
    private StepResult Fatal() { W(Poison,1); W(ErrorAt,213); return Next(901); }
    public StepResult Step()
    {
        uint state=Arena+State;
        if(m.Read32(state+Waiting)!=0) return StepResult.NeedIo;
        if(m.Read32(state+BusyAt)==0) return StepResult.Complete;
        uint phase=m.Read32(state+Phase);
        if(m.Read32(state+CancelAt)!=0&&phase!=901) return Fatal();
        return Dispatch(phase);
    }
    public StepResult RunBatch()
    {
        uint state=Arena+State;
        if(m.Read32(state+Waiting)!=0) return StepResult.NeedIo;
        if(m.Read32(state+BusyAt)==0) return StepResult.Complete;
        if(m.Read32(state+CancelAt)!=0&&m.Read32(state+Phase)!=901) return Fatal();
        for(uint budget=0;budget<8;budget++) {
            uint phase=m.Read32(state+Phase);
            StepResult result=phase<100?PacketStep(phase):phase is >=300 and <400?TransferStep(phase):Dispatch(phase);
            if(result!=StepResult.Pending) return result;
        }
        return StepResult.Pending;
    }
    private StepResult Dispatch(uint phase)
    {
        if(phase<100) return PacketStep(phase);
        if(phase>=1300) return FormatStep(phase);
        if(phase>=1100) return PackedStep(phase);
        if(phase>=100&&phase<200) return MountStep(phase);
        if(phase>=200&&phase<300) return FlushStep(phase);
        if(phase>=300&&phase<400) return TransferStep(phase);
        if(phase>=400&&phase<450) return ShrinkStep(phase);
        if(phase==1000)
        {
            uint slot=R(LoadSlot),p=Image(slot),home=R(LoadHome);
            if(!Valid(p,home,0)||m.Read32(p+36)>R(Generation)) return Fatal();
            m.Write32(Meta(slot)+4,1); W(Loaded,p); return Next(R(LoadNext));
        }
        if(phase==900) { W(Mounted,1); return Finish(uint.MaxValue); }
        if(phase==901) return R(Packet)==0?FailMount():Finish(R(Action) is 82 or 87&&R(DataProgress)!=0?R(DataProgress):R(Action) is 82 or 87 or 1008 or 1022?uint.MaxValue:0,213);
        return PacketStep(phase);
    }
    private StepResult FailMount() { W(BusyAt,0); W(Mounted,0); return StepResult.Failed; }
    // One byte for each possible decoded metadata address, between the flush list and journal images.
    private const uint Resident=55040, ResidentCount=2256;
    private int Find(uint home)
    {
        if(home>=ResidentCount) return home>=WideSelectorKey&&WideVolume?FindWide(home):-1;
        uint value=m.Read8(Arena+Resident+home);
        if(value==0) return -1;
        uint slot=value-1;
        return m.Read32(Arena+CacheMeta+(slot<<5)+4)!=0?(int)slot:-1;
    }
    private int CacheSlot(uint home)
    {
        int hit=Find(home); if(hit>=0) { m.Write32(Meta((uint)hit)+12,R(Epoch)); return hit; }
        return ReserveSlot(home);
    }
    private int ReserveSlot(uint home)
    {
        if(home>=ResidentCount&&(home<WideSelectorKey||!WideVolume)) return -1;
        uint used=R(CacheUsed); int chosen=-1;
        if(used<64) { chosen=(int)used; W(CacheUsed,used+1); }
        // +12 is the existing last-access packet epoch; +16 is the epoch last
        // observed by CLOCK. Their difference supplies the reference bit without
        // adding work to cache hits. Admission starts probationary (epochs equal).
        // Dirty/current-packet pages remain protected. Two revolutions suffice.
        if(chosen<0)
        {
            // Any starting hand is valid, including one retained across remount.
            uint hand=R(CacheHand)&63,epoch=R(Epoch);
            for(uint n=0;n<128;n++)
            {
                uint candidate=hand,at=Meta(candidate); hand=(hand+1)&63;
                uint touched=m.Read32(at+12);
                if(m.Read32(at+8)!=0||touched==epoch) continue;
                if(m.Read32(at+16)!=touched) { m.Write32(at+16,touched); continue; }
                chosen=(int)candidate; break;
            }
            W(CacheHand,hand);
        }
        if(chosen<0) return -1;
        uint slot=(uint)chosen,entry=Meta(slot);
        if(slot<used)
        {
            uint previous=m.Read32(entry);
            if(previous<ResidentCount) m.Write8(Arena+Resident+previous,0);
            else UnlinkWide(previous,slot);
        }
        m.Write32(entry,home); m.Write32(entry+4,0); m.Write32(entry+8,0); m.Write32(entry+12,R(Epoch)); m.Write32(entry+16,R(Epoch));
        if(home<ResidentCount) m.Write8(Arena+Resident+home,(byte)(slot+1));
        else LinkWide(home,slot,entry);
        return chosen;
    }
    private StepResult Load(uint home,uint next)
    {
        if(home>=WideBitmapKey&&WideVolume) return LoadWideBitmap(home,next);
        return LoadHomePage(home,next);
    }

    private StepResult LoadHomePage(uint home,uint next)
    {
        uint state=Arena+State;
        int hit=Find(home);
        if(hit>=0)
        {
            uint slot=(uint)hit;
            m.Write32(Arena+CacheMeta+(slot<<5)+12,m.Read32(state+Epoch));
            m.Write32(state+Loaded,Arena+CacheImages+(slot<<9)); m.Write32(state+Phase,next);
            return StepResult.Pending;
        }
        // Reserve directly after the lookup miss.
        int reserve=ReserveSlot(home);
        if(reserve<0) return R(WideStreaming)!=0?Fatal():Error(103);
        m.Write32(state+LoadSlot,(uint)reserve); m.Write32(state+LoadHome,home); m.Write32(state+LoadNext,next);
        if(PackedVolume) return Next(1100);
        return Issue(IoKind.Read,home,Arena+CacheImages+((uint)reserve<<9),512,1000);
    }
    private void Dirty(uint p)
    {
        uint slot=(p-(Arena+CacheImages))>>9,entry=Arena+CacheMeta+(slot<<5),state=Arena+State;
        if(m.Read32(entry+8)==0) { uint count=m.Read32(state+DirtyCount); m.Write8(Arena+DirtySlots+count,(byte)slot); m.Write32(entry+8,1); m.Write32(state+DirtyCount,count+1); }
        m.Write32(entry+4,1);
    }
    private void Zero(uint p,uint bytes)
    {
        while(bytes>=32) { m.Write32(p,0); m.Write32(p+4,0); m.Write32(p+8,0); m.Write32(p+12,0);
            m.Write32(p+16,0); m.Write32(p+20,0); m.Write32(p+24,0); m.Write32(p+28,0); p+=32; bytes-=32; }
        while(bytes!=0) { m.Write32(p,0); p+=4; bytes-=4; }
    }
    // A row is the zero-seeded CRC contribution of the last byte of
    // one word at its final position in a 512-byte sector. Moving that
    // byte earlier by a lane advances its CRC through one more zero.
    private const uint WordTable=98304, OnesTable=WordTable+16384;
    private uint SectorCrc(uint page)
    {
        uint end=page+512;
        // Most metadata has a zero suffix. Each discarded word is checked;
        // the position tables already include its zero-byte advancement.
        while(end-page>=32&&(m.Read32(end-4)|m.Read32(end-8)|m.Read32(end-12)|m.Read32(end-16)|
            m.Read32(end-20)|m.Read32(end-24)|m.Read32(end-28)|m.Read32(end-32))==0) end-=32;
        return PositionCrc(page,end);
    }

    // Positions are relative to a full 512-byte sector. A caller may omit a
    // suffix only after physically zeroing it or verifying it is all zero.
    private uint PositionCrc(uint page,uint end)
    {
        uint a=0,b=0,c=0,d=0,normal=Arena+CrcTable,table=Arena+WordTable,ones=Arena+OnesTable;
        uint check=page+8;
        for(uint src=page;src<end;src+=4,table+=128,ones+=4) {
            uint w=m.Read32(src);
            if(w==0||src==check) continue;
            if(w==uint.MaxValue) { d^=m.Read32(ones); continue; }
            if(w>65535) {
                uint x=m.Read8(src),y=m.Read8(src+1);
                a^=m.Read32(table+((x&15)<<2))^m.Read32(table+64+((x>>4)<<2));
                b^=m.Read32(table+((y&15)<<2))^m.Read32(table+64+((y>>4)<<2));
            }
            if(w>255) { uint x=m.Read8(src+2); c^=m.Read32(table+((x&15)<<2))^m.Read32(table+64+((x>>4)<<2)); }
            uint last=w&255;
            d^=m.Read32(table+((last&15)<<2))^m.Read32(table+64+((last>>4)<<2));
        }
        uint v=(a>>8)^m.Read32(normal+((a&255)<<2))^b;
        v=(v>>8)^m.Read32(normal+((v&255)<<2))^c;
        // Affine offset is CRC-32C of a zero-filled 512-byte sector.
        return 0x30fcedc0u^d^(v>>8)^m.Read32(normal+((v&255)<<2));
    }
    private uint Crc(uint p,uint bytes,bool metadata)
    {
        if(metadata&&bytes==512) return SectorCrc(p);
        // One bounded loop (at most 256 words). Keep both addresses live for
        // the whole checksum: no four-byte calls or repeated arena decoding.
        // The private spill avoids costly 68000 variable shifts. Input bytes
        // are never modified; the metadata checksum field is logically zero.
        uint value=uint.MaxValue,word=Arena+State+CrcWord,table=Arena+CrcTable;
        for(uint n=0;n<bytes;n+=4)
        {
            uint src=p+n;
            if((n&31)==0&&bytes-n>=32&&(m.Read32(src)|m.Read32(src+4)|m.Read32(src+8)|m.Read32(src+12)|
                m.Read32(src+16)|m.Read32(src+20)|m.Read32(src+24)|m.Read32(src+28))==0) {
                m.Write32(word,value);
                value=m.Read32(table+7168+((uint)m.Read8(word+3)<<2))^m.Read32(table+6144+((uint)m.Read8(word+2)<<2))^
                    m.Read32(table+5120+((uint)m.Read8(word+1)<<2))^m.Read32(table+4096+((uint)m.Read8(word)<<2));
                n+=28; continue;
            }
            m.Write32(word,value);
            if(metadata&&n==8)
                value=m.Read32(table+3072+((uint)m.Read8(word+3)<<2))^m.Read32(table+2048+((uint)m.Read8(word+2)<<2))^
                    m.Read32(table+1024+((uint)m.Read8(word+1)<<2))^m.Read32(table+((uint)m.Read8(word)<<2));
            else
                value=m.Read32(table+3072+(((uint)m.Read8(word+3)^m.Read8(p+n))<<2))^
                    m.Read32(table+2048+(((uint)m.Read8(word+2)^m.Read8(p+n+1))<<2))^
                    m.Read32(table+1024+(((uint)m.Read8(word+1)^m.Read8(p+n+2))<<2))^
                    m.Read32(table+(((uint)m.Read8(word)^m.Read8(p+n+3))<<2));
        }
        return ~value;
    }
    private void Header(uint p,uint type,uint home,uint generation,uint owner=0)
    {
        m.Write32(p,Magic); m.Write16(p+4,1); m.Write16(p+6,type); m.Write32(p+8,0);
        m.Write32(p+16,0); m.Write32(p+20,home); m.Write32(p+24,type is 10 or 11?1u:0); m.Write32(p+28,owner);
        m.Write32(p+32,0); m.Write32(p+36,generation); m.Write32(p+40,R(IdentityHi)); m.Write32(p+44,R(IdentityLo));
    }
    private void Seal(uint p) { m.Write32(p+8,Crc(p,512,true)); }
    private bool Valid(uint p,uint home,uint type)
    {
        return m.Read32(p)==Magic&&m.Read16(p+4)==1&&(type==0||m.Read16(p+6)==type)&&m.Read32(p+16)==0&&m.Read32(p+20)==home&&m.Read32(p+32)==0&&
            m.Read32(p+40)==R(IdentityHi)&&m.Read32(p+44)==R(IdentityLo)&&m.Read32(p+8)==Crc(p,512,true);
    }
    private bool MetadataHome(uint home)=>home is >=128 and <=161 or >=256 and <1280;
    private bool ValidInodeIdentity(uint p,uint slot)=>slot is >0 and <1024&&m.Read32(p+24)==1&&m.Read32(p+28)==slot&&m.Read32(p+64)==0;
    private bool ValidInode(uint p,uint slot)=>ValidInodeIdentity(p,slot)&&m.Read16(p+6) is 10 or 11&&m.Read32(p+96)<=8;
    private uint NameHash(uint p,uint bytes) { uint value=0,end=p+bytes; while(p<end) { uint c=m.Read8(p++); if(c is >=97 and <=122)c-=32; value=unchecked((value<<5|value>>27)+c); } return value; }
    private bool NameEqual(uint a,uint b,uint bytes) { uint end=a+bytes; while(a<end) { uint x=m.Read8(a++),y=m.Read8(b++); if(x==y)continue; if(x is >=97 and <=122)x-=32; if(y is >=97 and <=122)y-=32; if(x!=y)return false; } return true; }
    private bool Bit(uint p,uint bit)=> (m.Read8(p+(bit>>3))&(1u<<(int)(bit&7)))!=0;
    private void SetBit(uint p,uint bit,bool allocated)
    { uint at=p+(bit>>3),mask=1u<<(int)(bit&7),old=m.Read8(at); m.Write8(at,(byte)(allocated?old|mask:old&~mask)); }
    private void SetRange(uint p,uint bit,uint count,bool allocated)
    {
        uint at=p+(bit>>3),offset=bit&7;
        if(offset!=0&&count!=0) {
            uint take=8-offset; if(take>count) take=count;
            uint mask=((1u<<(int)take)-1)<<(int)offset,old=m.Read8(at);
            m.Write8(at++, (byte)(allocated?old|mask:old&~mask)); count-=take;
        }
        byte fill=allocated?(byte)255:(byte)0;
        while(count>=8&&(at&3)!=0) { m.Write8(at++,fill); count-=8; }
        while(count>=32) { m.Write32(at,allocated?uint.MaxValue:0); at+=4; count-=32; }
        while(count>=8) { m.Write8(at++,fill); count-=8; }
        if(count!=0) { uint mask=(1u<<(int)count)-1,old=m.Read8(at); m.Write8(at,(byte)(allocated?old|mask:old&~mask)); }
    }
}
