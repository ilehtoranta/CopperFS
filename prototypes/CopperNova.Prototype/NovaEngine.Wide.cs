using CopperCPX;

namespace CopperNova.Prototype;

public partial struct NovaEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // CNP2 root version 3. Fixed paired pages map each bitmap window to
    // the copy selected by its committed root. No generation-only lookup.
    private const uint ObjectIndexStart = 1394, WideTopStart = 3440, WideSelectorStart = 3442;
    private const uint WideBitmapKey = 0x80000000, WideSelectorKey = 0x40000000;
    private const uint WideTopImage = Quarantine, WideTopDirty = 592, WideMap = 596, WideReturn = 600;
    private const uint WideRetirePages = 604, WideRetireReturn = 608, WideStreaming = 612;
    private const uint WideActiveSelector = 616, WidePageSector = 620;
    private const uint WideRetireRanges = Quarantine + 512;
    private const uint WideTopType = 36, WideSelectorType = 37;

    // Wide volumes use no narrow quarantine. Reuse 128 bytes of that arena
    // region for bucket heads and the spare byte at +28 of each cache entry
    // for its next slot. Slot identities let eviction unlink an invalid slot
    // without disturbing a newer copy of the same key.
    private const uint WideIndex = Quarantine + 640, WideLink = 28;

    private uint WideBucket(uint home) => Arena + WideIndex +
        ((home ^ (home >> 7) ^ (home >> 14) ^ (home >> 24)) & 127);

    private int FindWide(uint home)
    {
        uint link=m.Read8(WideBucket(home));
        while(link!=0)
        {
            uint slot=link-1,entry=Arena+CacheMeta+(slot<<5);
            if(m.Read32(entry)==home&&m.Read32(entry+4)!=0) return (int)slot;
            link=m.Read8(entry+WideLink);
        }
        return -1;
    }

    private void LinkWide(uint home,uint slot,uint entry)
    {
        uint bucket=WideBucket(home);
        m.Write8(entry+WideLink,m.Read8(bucket));
        m.Write8(bucket,(byte)(slot+1));
    }

    private void UnlinkWide(uint home,uint slot)
    {
        uint previous=WideBucket(home),link=m.Read8(previous);
        while(link!=0&&link!=slot+1)
        {
            previous=Arena+CacheMeta+((link-1)<<5)+WideLink;
            link=m.Read8(previous);
        }
        if(link!=0) m.Write8(previous,m.Read8(Arena+CacheMeta+(slot<<5)+WideLink));
    }

    // Each byte numbers sectors from the low bit. Compare whole aligned
    // words where possible, and inspect individual bits only in a mixed byte.
    private uint WideBitmapRun(uint bits,uint first,uint count,uint fill)
    {
        uint bit=first,end=first+count;
        while(bit<end)
        {
            uint address=bits+(bit>>3);
            if((bit&31)==0&&end-bit>=32&&m.Read32(address)==fill)
            {
                bit+=32;
                continue;
            }
            uint lane=bit&7,take=8-lane,remaining=end-bit;
            if(take>remaining) take=remaining;
            uint value=((uint)m.Read8(address)^(fill&255))>>(int)lane;
            if(value!=0)
                for(uint offset=0;offset<take;offset++)
                {
                    if((value&1)!=0) return bit-first+offset;
                    value>>=1;
                }
            bit+=take;
        }
        return count;
    }

    private bool PackedVolume => R(DataFloor) >= 1394;
    private bool WideVolume => R(DataFloor) > 1394;
    private uint WindowCount => ((R(Blocks) - 1) >> 11) + 1;
    private uint SelectorCount => ((WindowCount - 1) >> 11) + 1;
    private uint WideBitmapStart => WideSelectorStart + SelectorCount * 2;
    private uint BitmapHome(uint sector) => (WideVolume ? WideBitmapKey : 130u) + (sector >> 11);

    private StepResult LoadWideBitmap(uint home, uint next)
    {
        uint map = home - WideBitmapKey;
        if (map >= WindowCount) return Fatal();
        W(WideMap, map);
        W(WideReturn, next);
        return LoadHomePage(WideSelectorKey + (map >> 11), 1120);
    }

    private uint WideSelectorSector(uint index) =>
        WideSelectorStart + index * 2 + (Bit(Ptr(WideTopImage) + 128, index) ? 1u : 0u);

    private StepResult LoadWidePage(uint phase, uint home, uint page)
    {
        bool bitmap = home >= WideBitmapKey;
        uint index = home - (bitmap ? WideBitmapKey : WideSelectorKey);
        if (index >= (bitmap ? WindowCount : SelectorCount)) return Fatal();
        uint sector;
        if(phase==1100)
        {
            sector=bitmap?WideBitmapStart+index*2+(Bit(R(WideActiveSelector)+128,index&2047)?1u:0u):
                WideSelectorSector(index);
            W(WidePageSector,sector);
            return Issue(IoKind.Read,sector,page,512,1121);
        }
        sector=R(WidePageSector);
        if (!PageValid(page, bitmap ? 34u : WideSelectorType, sector, index)) return Fatal();
        if (bitmap)
        {
            uint generation=m.Read32(page+16);
            Header(page, 4, home, generation);
            m.Write32(page + 64, index << 11);
        }
        return LoadedPacked();
    }

    private void DirtyWideBitmap(uint page, uint map, uint selector)
    {
        uint entry = Meta((page - Ptr(CacheImages)) >> 9);
        if (m.Read32(entry + 8) == 0)
        {
            uint index = map >> 11;
            uint selectorEntry = Meta((selector - Ptr(CacheImages)) >> 9);
            if (m.Read32(selectorEntry + 8) == 0)
            {
                uint sector = WideSelectorSector(index);
                if(R(StagedExtentMaps)==0 || m.Read32(selector+16)!=R(Generation)+1) sector^=1;
                m.Write32(selectorEntry + 20, sector);
                SetBit(Ptr(WideTopImage) + 128, index, (sector & 1) != 0);
                W(WideTopDirty, 1);
                Dirty(selector);
            }
            uint bit = map & 2047, bits = selector + 128;
            bool side = Bit(bits,bit);
            if(R(StagedExtentMaps)==0 || m.Read32(page+36)!=R(Generation)+1) side=!side;
            SetBit(bits, bit, side);
            m.Write32(entry + 20, WideBitmapStart + map * 2 + (side ? 1u : 0u));
        }
        Dirty(page);
    }

    private StepResult WriteWidePage(uint home, uint entry, uint page, uint generation, uint next = 1201)
    {
        bool bitmap = home >= WideBitmapKey;
        uint type = bitmap ? 34u : WideSelectorType;
        uint index = home - (bitmap ? WideBitmapKey : WideSelectorKey);
        uint sector = m.Read32(entry + 20), output = Ptr(Output);
        m.Copy(output, page, 512);
        Stamp(output, type, sector, index, generation);
        return Issue(IoKind.Write, sector, output, 512, next);
    }

    private bool AdmitWideRetirement(uint page)
    {
        uint entry = Meta((page - Ptr(CacheImages)) >> 9);
        uint selector = Meta((R(WideActiveSelector) - Ptr(CacheImages)) >> 9), epoch = R(Epoch);
        uint count = R(WideRetirePages);
        if (m.Read32(entry + 8) == 0 && m.Read32(entry + 24) != epoch)
        {
            m.Write32(entry + 24, epoch);
            count++;
        }
        if (m.Read32(selector + 8) == 0 && m.Read32(selector + 24) != epoch)
        {
            m.Write32(selector + 24, epoch);
            count++;
        }
        W(WideRetirePages, count);
        // Leave admission space for the inode, root state, parent and comment.
        return R(DirtyCount) + count <= 44;
    }

    private void ReleaseWideAllocation(uint page, uint selector)
    {
        // The caller finished using both pages. Dirty ownership still pins them
        // until commit, even if the packet crosses multiple cache generations.
        uint epoch=R(Epoch)-1;
        m.Write32(Meta((page-Ptr(CacheImages))>>9)+12,epoch);
        m.Write32(Meta((selector-Ptr(CacheImages))>>9)+12,epoch);
    }

    private StepResult BeginWideRetirement()
    {
        W(WideRetireReturn,R(Resume));
        if(R(DirtyCount)!=0) return StartFlush(420);
        return Next(420);
    }

    private StepResult StreamWideRetirement(uint phase)
    {
        uint range=Ptr(WideRetireRanges),inode=R(Inode);
        switch(phase)
        {
            case 420:
            {
                if(R(Generation)==uint.MaxValue) return Fatal();
                uint epoch=R(Epoch)-1;
                for(uint i=0;i<R(CacheUsed);i++)
                {
                    uint at=Meta(i);
                    if(m.Read32(at)>=WideSelectorKey) m.Write32(at+12,epoch);
                }
                uint ranges=0,keep=(R(ResizeLength)+511)>>9;
                for(uint i=0;i<m.Read32(inode+96);i++)
                {
                    uint extent=ExtentAt(inode,i),disk=m.Read32(extent+12),run=m.Read32(extent+16),logical=m.Read32(extent+4);
                    if(!ExtentOK(disk,run)||run>uint.MaxValue-logical) return Fatal();
                    uint retained=keep>logical?keep-logical:0;
                    if(retained>=run) continue;
                    uint first=disk+retained,end=disk+run,j=ranges;
                    // At most eight extents. Physical order lets us write each
                    // shadow bitmap once, even when it intersects several runs.
                    while(j!=0&&m.Read32(range+(j-1)*8)>first)
                    {
                        m.Copy(range+j*8,range+(j-1)*8,8);
                        j--;
                    }
                    m.Write32(range+j*8,first);
                    m.Write32(range+j*8+4,end);
                    ranges++;
                }
                for(uint i=1;i<ranges;i++)
                    if(m.Read32(range+(i-1)*8+4)>m.Read32(range+i*8)) return Fatal();
                W(WideStreaming,1);
                W(WideRetirePages,ranges);
                W(ExtentIndex,0);
                W(Resume,R(WideRetireReturn));
                return Next(421);
            }
            case 421:
            {
                uint index=R(ExtentIndex);
                if(index==R(WideRetirePages)) return Next(412);
                return Load(BitmapHome(m.Read32(range+index*8)),422);
            }
            case 422:
            {
                uint index=R(ExtentIndex),bitmap=R(Loaded),map=m.Read32(range+index*8)>>11;
                uint bits=bitmap+128,freed=R(ResizeFree);
                do
                {
                    uint at=range+index*8,first=m.Read32(at),end=m.Read32(at+4);
                    uint take=2048-(first&2047);
                    if(take>end-first) take=end-first;
                    if(WideBitmapRun(bits,first&2047,take,uint.MaxValue)!=take) return Fatal();
                    SetRange(bits,first&2047,take,false);
                    freed+=take;
                    if(first+take!=end)
                    {
                        m.Write32(at,first+take);
                        break;
                    }
                    index++;
                } while(index<R(WideRetirePages)&&(m.Read32(range+index*8)>>11)==map);
                W(ExtentIndex,index);
                W(ResizeFree,freed);
                DirtyWideBitmap(bitmap,map,R(WideActiveSelector));
                // The commit will also edit the locator for each changed object.
                // Keep those bitmaps resident so their shadow side is selected only once.
                if(DynamicIndex && (map==(GroupOf(m.Read32(inode+28))>>5) ||
                    R(Action)==16 && (map==(GroupOf(m.Read32(R(Parent)+28))>>5) ||
                                      map==(GroupOf(DirectoryId(R(Parent)))>>5)))) return Next(421);
                return WriteWidePage(WideBitmapKey+map,Meta((bitmap-Ptr(CacheImages))>>9),bitmap,R(Generation)+1,423);
            }
            case 423:
            {
                // The last dirty slot is the staged bitmap. Selector edits
                // remain resident until the ordinary root commit publishes it.
                uint count=R(DirtyCount),slot=m.Read8(Arena+DirtySlots+count-1),entry=Meta(slot);
                m.Write32(entry+4,0);
                m.Write32(entry+8,0);
                m.Write32(entry+12,R(Epoch)-1);
                W(DirtyCount,count-1);
                return Next(421);
            }
            default: return Fatal();
        }
    }

    private StepResult FinishWideRetirement(uint resume)
    {
        W(WideStreaming,0);
        return StartFlush(resume);
    }
}
