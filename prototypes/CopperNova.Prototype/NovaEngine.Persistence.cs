using CopperCPX;
namespace CopperNova.Prototype;

public partial struct NovaEngine<TMemory> where TMemory : struct,ICpxMemory
{
    private StepResult MountStep(uint phase)
    {
        uint controls=Ptr(Controls);
        switch(phase)
        {
            case 100:
                if(R(Blocks)<4096) return Fatal();
                W(PartialDirty,0); W(WideStreaming,0); W(Poison,0); W(Mounted,0); W(Index,0); W(DirtyCount,0); W(Epoch,R(Epoch)+1);
                return Next(R(CrcReady)==Magic?102u:101u);
            case 101:
                for(uint n=0;n<32;n++)
                {
                    uint index=R(Index)+n,value=index;
                    for(int bit=0;bit<8;bit++) value=(value>>1)^((value&1)!=0?0x82F63B78u:0);
                    m.Write32(Ptr(CrcTable)+index*4,value);
                }
                W(Index,R(Index)+32);
                if(R(Index)==256) { W(Index,0); return Next(117); }
                return StepResult.Pending;
            case 102:
                // Reset only cache/quarantine ownership, preserving I/O token and DOS serials.
                uint used=R(CacheUsed);
                for(uint slot=0;slot<used;slot++) { uint meta=Arena+CacheMeta+(slot<<5),home=m.Read32(meta); if(home<ResidentCount) m.Write8(Arena+Resident+home,0); m.Write32(meta+4,0); }
                W(CacheUsed,0);
                Zero(Arena+State+QuarantineTags,128); W(QuarantineEpoch,1); W(FreePending,0);
                return Issue(IoKind.Read,0,controls,1024,103);
            case 103:
            {
                if(m.Read32(controls)==PackedMagic||m.Read32(controls+512)==PackedMagic) return SelectRoot(controls);
                if(R(Blocks)>65536) return Fatal();
                W(ProofMask,0); W(DataFloor,1280);
                uint selected=0;
                for(uint n=0;n<2;n++)
                {
                    uint p=controls+n*512;
                    if(m.Read32(p)!=Magic||m.Read16(p+4)!=1||m.Read16(p+6)!=1||m.Read32(p+16)!=0||m.Read32(p+20)!=n||m.Read32(p+8)!=Crc(p,512,true)) continue;
                    if(m.Read32(p+64)!=R(Blocks)||m.Read32(p+68)!=FirstData||m.Read32(p+72)!=256||m.Read32(p+76)!=128||m.Read32(p+80)!=130||m.Read32(p+84)!=129||m.Read32(p+88)!=48) return Fatal();
                    if(selected!=0&&(m.Read32(p+40)!=m.Read32(selected+40)||m.Read32(p+44)!=m.Read32(selected+44))) return Fatal();
                    selected=p;
                }
                if(selected==0) return Fatal();
                W(IdentityHi,m.Read32(selected+40)); W(IdentityLo,m.Read32(selected+44));
                return Issue(IoKind.Read,58,controls+1024,512,104);
            }
            case 104: return Issue(IoKind.Read,114,controls+1536,512,105);
            case 105:
            {
                uint a=controls+1024,b=controls+1536;
                bool va=Valid(a,58,20),vb=Valid(b,114,20);
                if(!va&&!vb) return Fatal();
                if(va&&vb&&m.Read32(a+36)==m.Read32(b+36)) return Fatal();
                uint bank=vb&&(!va||m.Read32(b+36)>m.Read32(a+36))?1u:0;
                uint p=controls+1024+bank*512;
                if(m.Read32(p+68)>48||m.Read32(p+72)!=bank) return Fatal();
                W(Bank,bank); W(Generation,m.Read32(p+36)); W(SelectedCommit,CommitHome(bank));
                W(SelectedCrc,m.Read32(p+8)); W(Count,m.Read32(p+68)); W(DescriptorDigest,m.Read32(p+64));
                return Issue(IoKind.Read,116,controls+2048,1024,106);
            }
            case 106:
            {
                bool clean=false;
                for(uint n=0;n<2;n++)
                {
                    uint p=controls+2048+n*512;
                    if(!Valid(p,116+n,22)) continue;
                    if(m.Read32(p+36)>R(Generation)) return Fatal();
                    if(m.Read32(p+36)==R(Generation))
                    {
                        if(m.Read32(p+64)!=R(SelectedCrc)||m.Read32(p+68)!=R(SelectedCommit)||m.Read32(p+72)!=R(DescriptorDigest)) return Fatal();
                        clean=true;
                    }
                }
                if(clean) return Next(115);
                if(R(Count)==0) return Fatal();
                return Issue(IoKind.Read,BankBase(R(Bank)),Ptr(Descriptors),1024,107);
            }
            case 107:
            {
                uint d=Ptr(Descriptors),bank=BankBase(R(Bank));
                if(!Valid(d,bank,21)||!Valid(d+512,bank+1,21)||m.Read32(d+36)!=R(Generation)||m.Read32(d+548)!=R(Generation)||
                    m.Read32(d+64)!=R(Count)||m.Read32(d+576)!=R(Count)||m.Read32(d+68)!=0||m.Read32(d+580)!=1||Crc(d,1024,false)!=R(DescriptorDigest)) return Fatal();
                W(Index,0); return Next(108);
            }
            case 108:
            {
                uint i=R(Index),entry=DescriptorEntry(i),home=m.Read32(entry+4);
                if(m.Read32(entry)!=0||!MetadataHome(home)) return Fatal();
                for(uint n=0;n<i;n++) if(m.Read32(DescriptorEntry(n)+4)==home) return Fatal();
                W(Index,i+1);
                return R(Index)==R(Count)?Issue(IoKind.Read,BankBase(R(Bank))+2,Ptr(JournalImages),R(Count)<<9,109):StepResult.Pending;
            }
            case 109: W(Index,0); return Next(110);
            case 110:
            {
                uint i=R(Index),entry=DescriptorEntry(i),p=Ptr(JournalImages)+(i<<9),home=m.Read32(entry+4);
                if(!Valid(p,home,0)||m.Read32(p+36)!=R(Generation)||m.Read32(p+8)!=m.Read32(entry+8)||!ValidHomeType(p,home)) return Fatal();
                W(Index,i+1); return R(Index)==R(Count)?Next(111):StepResult.Pending;
            }
            case 111: W(Index,0); return Next(112);
            case 112:
                if(R(Index)==R(Count)) return Issue(IoKind.Flush,0,0,0,113);
                uint at=R(Index); W(Index,at+1);
                return Issue(IoKind.Write,m.Read32(DescriptorEntry(at)+4),Ptr(JournalImages)+(at<<9),512,112);
            case 113:
                PrepareClean(R(Generation),R(Bank),R(SelectedCrc),R(DescriptorDigest));
                return Issue(IoKind.Write,116+R(Bank),Ptr(Scratch),512,114);
            case 114: return Issue(IoKind.Flush,0,0,0,115);
            case 115:
                W(AllocationHint,(DataStart+31)&~31u); W(Mounted,1);
                if(WideVolume)
                {
                    uint hint=m.Read32(Root()+100);
                    if(hint>=DataStart&&hint<R(Blocks)) W(AllocationHint,hint);
                }
                return Load(257,116);
            case 116:
                if(!ValidInode(R(Loaded),1)||m.Read16(R(Loaded)+6)!=11) return Fatal();
                return Next(R(MountReturn));
            case 117:
                for(uint n=0;n<32;n++)
                {
                    uint index=R(Index)+n,value=m.Read32(Ptr(CrcTable)+(index<<2));
                    for(uint slice=1;slice<4;slice++)
                    { value=(value>>8)^m.Read32(Ptr(CrcTable)+((value&255)<<2)); m.Write32(Ptr(CrcTable)+(slice<<10)+(index<<2),value); }
                }
                W(Index,R(Index)+32);
                if(R(Index)==256) { W(Index,0); return Next(118); }
                return StepResult.Pending;
            case 118:
                uint table=Arena+CrcTable,start=R(Index);
                for(uint index=start;index<start+8;index++) {
                    uint value=m.Read32(table+index*4);
                    for(uint advance=1;advance<32;advance++) {
                        value=(value>>8)^m.Read32(table+((value&255)<<2));
                        if(advance>=28) m.Write32(table+4096+((advance-28)<<10)+index*4,value);
                    }
                }
                W(Index,start+8);
                if(start==248) { W(Index,0); return Next(119); }
                return StepResult.Pending;
            case 119:
            {
                // Advance one word between positions. Each visit builds one
                // nibble column in one quarter of the 128-row table.
                uint cursor=R(Index),nibble=cursor>>2,quarter=cursor&3,normal=Arena+CrcTable,weights=Arena+WordTable;
                uint pos=128-quarter*32,lo,hi;
                if(quarter==0) { lo=m.Read32(normal+nibble*4); hi=m.Read32(normal+(nibble<<6)); }
                else { lo=R(CrcLow); hi=R(CrcHigh); }
                for(uint i=0;i<32;i++) {
                    pos--; uint row=weights+(pos<<7);
                    m.Write32(row+nibble*4,lo); m.Write32(row+64+nibble*4,hi);
                    if(nibble==15) {
                        uint x=lo^hi,sum=x;
                        for(uint lane=0;lane<3;lane++) { x=(x>>8)^m.Read32(normal+((x&255)<<2)); sum^=x; }
                        m.Write32(Arena+OnesTable+(pos<<2),sum);
                    }
                    for(uint lane=0;lane<4;lane++) {
                        lo=(lo>>8)^m.Read32(normal+((lo&255)<<2)); hi=(hi>>8)^m.Read32(normal+((hi&255)<<2));
                    }
                }
                if(cursor==63) { W(CrcReady,Magic); return Next(102); }
                W(CrcLow,lo); W(CrcHigh,hi); W(Index,cursor+1); return StepResult.Pending;
            }
            default: return Fatal();
        }
    }
    private bool ValidHomeType(uint p,uint home)
    {
        uint type=m.Read16(p+6);
        return home==128?type==3:home==129?type==5:home is >=130 and <=161?type==4&&m.Read32(p+64)==(home-130)*2048:
            home is >=257 and <1280&&ValidInode(p,home-256);
    }
    private uint DescriptorEntry(uint i)=>Ptr(Descriptors)+(i<24?0u:512u)+80+(i%24)*16;
    private void PrepareClean(uint generation,uint bank,uint commitCrc,uint digest)
    {
        uint p=Ptr(Scratch); Zero(p,512); Header(p,22,116+bank,generation);
        m.Write32(p+64,commitCrc); m.Write32(p+68,CommitHome(bank)); m.Write32(p+72,digest); Seal(p);
    }
    private StepResult StartFlush(uint resume)
    {
        W(Resume,resume); return Next(200);
    }
    private StepResult FlushStep(uint phase)
    {
        switch(phase)
        {
            case 200:
                if (R(PartialDirty) != 0) return DrainPartial(200);
                if(R(DirtyCount)==0) return Issue(IoKind.Flush,0,0,0,215);
                if(PackedVolume) return Next(1200);
                if(R(DirtyCount)>48||R(Generation)==uint.MaxValue) return Fatal();
                W(NewGeneration,R(Generation)+1); W(Bank,1-R(Bank)); W(Index,0); W(Count,0);
                Zero(Ptr(Descriptors),1024);
                Header(Ptr(Descriptors),21,BankBase(R(Bank)),R(NewGeneration));
                Header(Ptr(Descriptors)+512,21,BankBase(R(Bank))+1,R(NewGeneration));
                m.Write32(Ptr(Descriptors)+68,0); m.Write32(Ptr(Descriptors)+580,1);
                return Next(201);
            case 201:
            {
                // One cache-sector seal/copy per visit; no checksum during home installation.
                uint index=R(Index),entries=Arena+CacheMeta;
                while(index<64)
                {
                    uint slot=index++,meta=entries+(slot<<5);
                    if(m.Read32(meta+8)==0) continue;
                    W(Index,index);
                    uint i=R(Count),p=Arena+CacheImages+(slot<<9),home=m.Read32(meta);
                    if(i>=48||!MetadataHome(home)||!ValidHomeType(p,home)) return Fatal();
                    m.Write32(p+32,0); m.Write32(p+36,R(NewGeneration)); Seal(p);
                    m.Copy(Ptr(JournalImages)+(i<<9),p,512); m.Write32(Ptr(FlushList)+i*4,slot);
                    uint entry=DescriptorEntry(i); m.Write32(entry,0); m.Write32(entry+4,home); m.Write32(entry+8,m.Read32(p+8));
                    W(Count,i+1); return StepResult.Pending;
                }
                W(Index,index);
                if(R(Count)!=R(DirtyCount)) return Fatal();
                m.Write32(Ptr(Descriptors)+64,R(Count)); m.Write32(Ptr(Descriptors)+576,R(Count));
                Seal(Ptr(Descriptors)); return Next(202);
            }
            case 202:
                Seal(Ptr(Descriptors)+512); W(DescriptorDigest,Crc(Ptr(Descriptors),1024,false));
                return Issue(IoKind.Write,BankBase(R(Bank)),Ptr(Descriptors),1024,203);
            case 203: return Issue(IoKind.Write,BankBase(R(Bank))+2,Ptr(JournalImages),R(Count)<<9,204);
            case 204: return Issue(IoKind.Flush,0,0,0,205);
            case 205:
            {
                uint p=Ptr(Scratch); Zero(p,512); Header(p,20,CommitHome(R(Bank)),R(NewGeneration));
                m.Write32(p+64,R(DescriptorDigest)); m.Write32(p+68,R(Count)); m.Write32(p+72,R(Bank)); Seal(p);
                W(SelectedCrc,m.Read32(p+8)); W(SelectedCommit,CommitHome(R(Bank)));
                return Issue(IoKind.Write,CommitHome(R(Bank)),p,512,206);
            }
            case 206: return Issue(IoKind.Flush,0,0,0,207);
            case 207: W(Index,0); return Next(208);
            case 208:
                if(R(Index)==R(Count)) return Issue(IoKind.Flush,0,0,0,209);
                uint homeIndex=R(Index); W(Index,homeIndex+1);
                return Issue(IoKind.Write,m.Read32(DescriptorEntry(homeIndex)+4),Ptr(JournalImages)+(homeIndex<<9),512,208);
            case 209:
                PrepareClean(R(NewGeneration),R(Bank),R(SelectedCrc),R(DescriptorDigest));
                return Issue(IoKind.Write,116+R(Bank),Ptr(Scratch),512,210);
            case 210: return Issue(IoKind.Flush,0,0,0,211);
            case 211:
                uint cleanEntries=Arena+CacheMeta,dirtyCount=R(DirtyCount);
                for(uint n=0;n<dirtyCount;n++) m.Write32(cleanEntries+((uint)m.Read8(Arena+DirtySlots+n)<<5)+8,0);
                W(Generation,R(NewGeneration)); W(DirtyCount,0); return Next(212);
            case 212:
                // Reached after the final durability barrier. Old bits
                // become invisible at once; no volume-sized memory clearing.
                if(R(FreePending)!=0)
                {
                    uint epoch=R(QuarantineEpoch);
                    if(epoch==uint.MaxValue) { Zero(Arena+State+QuarantineTags,128); epoch=0; }
                    W(QuarantineEpoch,epoch+1); W(FreePending,0);
                }
                return Next(215);
            case 215: return Next(R(Resume));
            default: return Fatal();
        }
    }
}
