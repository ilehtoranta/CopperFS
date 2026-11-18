using CopperCPX;
namespace CopperNova.Prototype;

public partial struct NovaEngine<TMemory> where TMemory : struct,ICpxMemory
{
    private void MarkBitmapRange(uint window,uint bit,uint count)
    {
        uint state=Arena+State;
        if(m.Read32(state+DataFloor)!=1394||m.Read32(state+BitmapProof)!=window+1) return;
        uint first=4+(bit>>8),last=4+((bit+count-1)>>8);
        uint mask=((2u<<(int)(last-first))-1)<<(int)first;
        m.Write32(state+BitmapChanges,m.Read32(state+BitmapChanges)|mask);
    }
    private uint ExtentAt(uint inode,uint i)=>inode+320+i*24;
    private bool Map(uint inode,uint logical,out uint physical,out uint available)
    {
        physical=available=0; uint count=m.Read32(inode+96),blocks=R(Blocks);
        if(count>8) return false;
        for(uint n=0;n<count;n++)
        {
            uint e=ExtentAt(inode,n),start=m.Read32(e+4),run=m.Read32(e+16),disk=m.Read32(e+12);
            if(m.Read32(e)!=0||m.Read32(e+8)!=0||run==0||start>uint.MaxValue-run||disk<DataStart||disk>=blocks||run>blocks-disk||m.Read32(e+20)!=0) return false;
            if(logical>=start&&logical-start<run) { physical=disk+logical-start; available=run-(logical-start); return true; }
        }
        return false;
    }
    private StepResult TransferStep(uint phase)
    {
        uint state=Arena+State,inode=m.Read32(state+Inode),handle=m.Read32(state+Handle),progress=m.Read32(state+DataProgress),remaining=m.Read32(state+DataBytes)-progress;
        bool write=m.Read32(state+Action)==87;
        switch(phase)
        {
            case 300:
            {
                if(remaining==0) return Finish(progress);
                uint position=m.Read32(handle+8),size=m.Read32(inode+68);
                if(!write&&position>=size) return Finish(progress);
                if(write&&(position>size||remaining>0x7fffffffu-position)) return Error(219);
                uint intra=position&511;
                if(Map(inode,position>>9,out uint disk,out uint available))
                {
                    uint take=(available<<9)-intra;
                    if(take>remaining) take=remaining;
                    if(!write&&take>size-position) take=size-position;
                    if(take>65536) take=65536;
                    if(intra!=0||take<512)
                    {
                        if(m.Read32(state+DataFloor)==1394&&intra!=0&&take>512-intra) {
                            if(take>4096-intra) take=4096-intra;
                            uint bytes=(intra+take+511)&~511u;
                            m.Write32(state+DataTake,take); m.Write32(state+DataIntra,intra); m.Write32(state+DataPhysical,disk); m.Write32(state+NewAllocation,0);
                            return Issue(IoKind.Read,disk,Arena+66560,bytes,303);
                        }
                        if(take>512-intra) take=512-intra;
                        m.Write32(state+DataTake,take); m.Write32(state+DataIntra,intra); m.Write32(state+DataPhysical,disk); m.Write32(state+NewAllocation,0);
                        return Issue(IoKind.Read,disk,(Arena+Scratch),512,302);
                    }
                    take&=~511u; m.Write32(state+DataTake,take); m.Write32(state+NewAllocation,0);
                    return Issue(write?IoKind.Write:IoKind.Read,disk,m.Read32(state+DataBuffer)+progress,take,304);
                }
                if(!write||position!=size||(position&511)!=0) return Fatal();
                if(m.Read32(state+DirtyCount)>45) return StartFlush(300);
                m.Write32(state+Wanted,remaining>65536?128u:(remaining+511)>>9);
                uint count=m.Read32(inode+96),candidate=m.Read32(state+AllocationHint);
                if(count!=0) { uint last=ExtentAt(inode,count-1); candidate=m.Read32(last+12)+m.Read32(last+16); }
                if(candidate<DataStart||candidate>=m.Read32(state+Blocks)) candidate=DataStart;
                m.Write32(state+Candidate,candidate); m.Write32(state+Searched,0); return Load(128,310);
            }
            case 302:
                if(!write)
                { m.Copy(m.Read32(state+DataBuffer)+progress,(Arena+Scratch)+m.Read32(state+DataIntra),m.Read32(state+DataTake)); goto case 304; }
                m.Copy((Arena+Scratch)+m.Read32(state+DataIntra),m.Read32(state+DataBuffer)+progress,m.Read32(state+DataTake));
                return Issue(IoKind.Write,m.Read32(state+DataPhysical),(Arena+Scratch),512,304);
            case 303:
                uint span=Arena+66560,offset=m.Read32(state+DataIntra),length=m.Read32(state+DataTake),external=m.Read32(state+DataBuffer)+progress;
                if(!write) { m.Copy(external,span+offset,length); goto case 304; }
                m.Copy(span+offset,external,length);
                return Issue(IoKind.Write,m.Read32(state+DataPhysical),span,(offset+length+511)&~511u,304);
            case 304:
            {
                uint take=m.Read32(state+DataTake),position=m.Read32(handle+8),end=position+take;
                if(write)
                {
                    if(m.Read32(state+NewAllocation)!=0)
                    {
                        uint units=m.Read32(state+AllocationUnits),physical=m.Read32(state+DataPhysical),logical=position>>9;
                        uint bitmap=m.Read32(state+Bitmap),group=m.Read32(state+Group),bit=physical&2047;
                        MarkBitmapRange(physical>>11,bit,units); SetRange(bitmap+128,bit,units,true);
                        Dirty(bitmap); m.Write32(group+72,m.Read32(group+72)-units); Dirty(group);
                        uint count=m.Read32(inode+96),last=count==0?0:ExtentAt(inode,count-1);
                        if(last!=0&&m.Read32(last+4)+m.Read32(last+16)==logical&&m.Read32(last+12)+m.Read32(last+16)==physical)
                            m.Write32(last+16,m.Read32(last+16)+units);
                        else
                        {
                            uint e=ExtentAt(inode,count); Zero(e,24); m.Write32(e+4,logical); m.Write32(e+12,physical); m.Write32(e+16,units); m.Write32(inode+96,count+1);
                        }
                        m.Write32(state+AllocationHint,physical+units);
                    }
                    if(end>m.Read32(inode+68)) m.Write32(inode+68,end);
                    m.Write32(inode+116,m.Read32(inode+116)+1); Dirty(inode);
                }
                m.Write32(handle+8,end); m.Write32(state+DataProgress,progress+take);
                return take==remaining||(!write&&end>=m.Read32(inode+68))?Finish(progress+take):Next(300);
            }
            case 310:
                if(m.Read16(m.Read32(state+Loaded)+6)!=3) return Fatal();
                m.Write32(state+Group,m.Read32(state+Loaded)); goto case 311;
            case 311:
                if(m.Read32(state+Searched)>=m.Read32(state+Blocks)-DataStart||m.Read32(m.Read32(state+Group)+72)==0) return TransferError(221);
                return Load(130+(m.Read32(state+Candidate)>>11),312);
            case 312:
            {
                uint bitmap=m.Read32(state+Loaded),candidate=m.Read32(state+Candidate),window=candidate>>11,limit=(window+1)<<11;
                uint blocks=m.Read32(state+Blocks),searched=m.Read32(state+Searched),bits=bitmap+128;
                uint quarantine=m.Read32(Arena+State+QuarantineTags+window*4)==m.Read32(state+QuarantineEpoch)?Arena+Quarantine+(window<<8):0;
                if(m.Read16(bitmap+6)!=4||m.Read32(bitmap+64)!=(window<<11)) return Fatal();
                if(limit>blocks) limit=blocks;
                // At most 256 allocation bits are examined per continuation.
                uint budget=256;
                while(candidate<limit&&budget!=0&&(Bit(bits,candidate&2047)||quarantine!=0&&Bit(quarantine,candidate&2047)))
                { candidate++; searched++; budget--; }
                m.Write32(state+Searched,searched);
                if(candidate==limit||budget==0)
                {
                    m.Write32(state+Candidate,candidate>=blocks?DataStart:candidate); return Next(311);
                }
                uint units=0,wanted=m.Read32(state+Wanted);
                while(units+32<=wanted&&candidate+units+32<=limit&&((candidate+units)&31)==0&&
                    m.Read32(bits+(((candidate+units)&2047)>>3))==0&&(quarantine==0||m.Read32(quarantine+(((candidate+units)&2047)>>3))==0)) units+=32;
                while(units+8<=wanted&&candidate+units+8<=limit&&((candidate+units)&7)==0&&
                    m.Read8(bits+(((candidate+units)&2047)>>3))==0&&(quarantine==0||m.Read8(quarantine+(((candidate+units)&2047)>>3))==0)) units+=8;
                while(units<wanted&&candidate+units<limit&&!Bit(bits,(candidate+units)&2047)&&(quarantine==0||!Bit(quarantine,(candidate+units)&2047))) units++;
                if(units==0) return Fatal();
                uint count=m.Read32(inode+96),last=count==0?0:ExtentAt(inode,count-1),logical=m.Read32(handle+8)>>9;
                bool append=last!=0&&m.Read32(last+4)+m.Read32(last+16)==logical&&m.Read32(last+12)+m.Read32(last+16)==candidate;
                if(!append&&count==8) return TransferError(221);
                if(m.Read32(m.Read32(state+Group)+72)<units||m.Read32(state+DirtyCount)>45) return Fatal();
                uint take=units<<9; if(take>remaining) take=remaining;
                m.Write32(state+Bitmap,bitmap); m.Write32(state+DataPhysical,candidate); m.Write32(state+AllocationUnits,units); m.Write32(state+DataTake,take); m.Write32(state+NewAllocation,1);
                if((take&511)!=0)
                {
                    // A partial new sector is handled alone, including zeroed tail.
                    m.Write32(state+AllocationUnits,1); if(take>512) { m.Write32(state+DataTake,take&~511u); m.Write32(state+AllocationUnits,m.Read32(state+DataTake)>>9); }
                    else { Zero((Arena+Scratch),512); m.Copy((Arena+Scratch),m.Read32(state+DataBuffer)+progress,take); return Issue(IoKind.Write,candidate,(Arena+Scratch),512,304); }
                }
                return Issue(IoKind.Write,candidate,m.Read32(state+DataBuffer)+progress,m.Read32(state+DataTake),304);
            }
            default: return Fatal();
        }
    }
    private StepResult TransferError(uint error)=>R(DataProgress)!=0?Finish(R(DataProgress),error):Error(error);

    private StepResult ShrinkStep(uint phase)
    {
        uint state=Arena+State;
        uint inode=m.Read32(state+Inode),count=m.Read32(inode+96);
        switch(phase)
        {
            case 400:
                m.Write32(state+Group,m.Read32(state+Loaded));
                if(m.Read16(m.Read32(state+Group)+6)!=3) return Fatal();
                m.Write32(state+ExtentIndex,0); m.Write32(state+ResizeFree,0); m.Write32(state+ResizeCursor,0); goto case 401;
            case 401:
            {
                if(m.Read32(state+ExtentIndex)==count) { m.Write32(state+ExtentIndex,0); m.Write32(state+ResizeCursor,0); goto case 410; }
                uint e=ExtentAt(inode,m.Read32(state+ExtentIndex)),logical=m.Read32(e+4),run=m.Read32(e+16),disk=m.Read32(e+12),keep=(m.Read32(state+ResizeLength)+511)>>9;
                if(m.Read32(e)!=0||m.Read32(e+8)!=0||run==0||disk<DataStart||disk>=m.Read32(state+Blocks)||run>m.Read32(state+Blocks)-disk||logical>uint.MaxValue-run) return Fatal();
                uint retained=keep<=logical?0:keep-logical; if(retained>run) retained=run;
                uint cursor=m.Read32(state+ResizeCursor); if(cursor<retained) cursor=retained;
                if(cursor>=run) { m.Write32(state+ExtentIndex,m.Read32(state+ExtentIndex)+1); m.Write32(state+ResizeCursor,0); return Next(401); }
                uint first=disk+cursor,take=2048-(first&2047); if(take>run-cursor) take=run-cursor;
                m.Write32(state+ResizeCursor,cursor+take);
                uint home=130+(first>>11); int cached=Find(home);
                if(cached<0) return Load(home,402);
                m.Write32(Meta((uint)cached)+12,m.Read32(state+Epoch));
                if(m.Read16(Image((uint)cached)+6)!=4) return Fatal();
                return Next(401);
            }
            case 402:
                if(m.Read16(m.Read32(state+Loaded)+6)!=4) return Fatal();
                // All pages are pinned and admitted before the first resize mutation.
                return Next(401);
            case 410:
            {
                if(m.Read32(state+ExtentIndex)==count) goto case 412;
                uint e=ExtentAt(inode,m.Read32(state+ExtentIndex)),logical=m.Read32(e+4),run=m.Read32(e+16),disk=m.Read32(e+12),keep=(m.Read32(state+ResizeLength)+511)>>9;
                uint retained=keep<=logical?0:keep-logical; if(retained>run) retained=run;
                uint cursor=m.Read32(state+ResizeCursor); if(cursor<retained) cursor=retained;
                if(cursor>=run) { m.Write32(state+ExtentIndex,m.Read32(state+ExtentIndex)+1); m.Write32(state+ResizeCursor,0); return Next(410); }
                uint first=disk+cursor,take=2048-(first&2047); if(take>run-cursor) take=run-cursor;
                int slot=Find(130+(first>>11)); if(slot<0) return Fatal();
                uint bitmap=Image((uint)slot);
                if(m.Read32(state+DirtyCount)>=48&&m.Read32(Meta((uint)slot)+8)==0) return Fatal();
                for(uint n=0;n<take;n++)
                {
                    uint block=first+n; if(!Bit(bitmap+128,block&2047)) return Fatal();
                }
                uint tag=Arena+State+QuarantineTags+(first>>11)*4,epoch=m.Read32(state+QuarantineEpoch),quarantine=Arena+Quarantine+((first>>11)<<8);
                // One bounded 256-byte window is initialized on its first free
                // in this retirement epoch. Other windows remain untouched.
                if(m.Read32(tag)!=epoch) { Zero(quarantine,256); m.Write32(tag,epoch); }
                MarkBitmapRange(first>>11,first&2047,take); SetRange(bitmap+128,first&2047,take,false); SetRange(quarantine,first&2047,take,true);
                Dirty(bitmap); m.Write32(state+ResizeFree,m.Read32(state+ResizeFree)+take); m.Write32(state+ResizeCursor,cursor+take); m.Write32(state+FreePending,1);
                if(cursor+take==run) { uint next=m.Read32(state+ExtentIndex)+1; m.Write32(state+ExtentIndex,next); m.Write32(state+ResizeCursor,0); if(next==count) goto case 412; }
                return Next(410);
            }
            case 412:
            {
                uint keep=(m.Read32(state+ResizeLength)+511)>>9,newCount=0;
                for(uint n=0;n<count;n++)
                {
                    uint e=ExtentAt(inode,n),start=m.Read32(e+4),run=m.Read32(e+16);
                    if(start>=keep) { Zero(e,24); continue; }
                    if(run>keep-start) m.Write32(e+16,keep-start);
                    newCount++;
                }
                m.Write32(inode+96,newCount); m.Write32(inode+68,m.Read32(state+ResizeLength)); Dirty(inode);
                m.Write32(m.Read32(state+Group)+72,m.Read32(m.Read32(state+Group)+72)+m.Read32(state+ResizeFree)); Dirty(m.Read32(state+Group));
                return Next(m.Read32(state+Resume));
            }
            default: return Fatal();
        }
    }
}
