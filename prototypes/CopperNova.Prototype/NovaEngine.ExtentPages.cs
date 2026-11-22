using CopperCPX;

namespace CopperNova.Prototype;

public partial struct NovaEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // The inode keeps the newest eight runs. Immutable overflow pages hold older
    // runs; only publishing the inode can make a newly allocated page reachable.
    private const uint PrefixLink=304, PrefixEnd=308, ExtentPageType=41;
    private const uint ExtentBuffer=Quarantine+1024;
    private const uint PrefixSector=748, PrefixLimit=752, StagedExtentMaps=756;
    private const uint RetireFirst=760, RetireLength=764, RetireBitmap=768;

    private bool ValidPrefixPointer(uint page,uint end)
    {
        uint sector=m.Read32(page+PrefixLink);
        return end==0?sector==0:end<=0x400000 && ExtentOK(sector,1);
    }

    private StepResult ReadPrefix(uint page,uint resume)
    {
        uint end=m.Read32(page+PrefixEnd),sector=m.Read32(page+PrefixLink);
        if(end==0 || !ValidPrefixPointer(page,end)) return Fatal();
        W(PrefixSector,sector); W(PrefixLimit,end);
        return Issue(IoKind.Read,sector,Ptr(ExtentBuffer),512,resume);
    }

    private bool VerifiedPrefix(uint inode,uint page)
    {
        uint logical=m.Read32(page+PrefixEnd),limit=R(PrefixLimit);
        if(!PageValid(page,ExtentPageType,R(PrefixSector),m.Read32(inode+28)) ||
            !ValidPrefixPointer(page,logical) || logical>=limit || m.Read32(page+96)!=8) return false;
        for(uint i=0;i<8;i++)
        {
            uint e=ExtentAt(page,i),run=m.Read32(e+16);
            if(m.Read32(e+4)!=logical || !ExtentOK(m.Read32(e+12),run) ||
                run>limit-logical || m.Read32(e)!=0 || m.Read32(e+8)!=0 || m.Read32(e+20)!=0) return false;
            logical+=run;
        }
        return logical==limit;
    }

    private StepResult BeginPrefixRetirement()
    {
        W(WideRetireReturn,R(Resume));
        return R(DirtyCount)!=0?StartFlush(430):Next(430);
    }

    private StepResult RetirePrefix(uint phase)
    {
        uint inode=R(Inode),keep=(R(ResizeLength)+511)>>9;
        switch(phase)
        {
            case 430:
                if(R(Generation)==uint.MaxValue) return Fatal();
                W(Resume,R(WideRetireReturn)); W(ResizeFree,0); W(RetireBitmap,0);
                W(WideStreaming,1); W(StagedExtentMaps,1);
                goto case 431;
            case 431:
            {
                uint count=m.Read32(inode+96);
                if(count!=0)
                {
                    uint e=ExtentAt(inode,count-1),logical=m.Read32(e+4),run=m.Read32(e+16);
                    uint retain=keep>logical?keep-logical:0;
                    if(retain<run)
                    {
                        W(RetireFirst,m.Read32(e+12)+retain); W(RetireLength,run-retain);
                        if(retain==0) { Zero(e,24); m.Write32(inode+96,count-1); }
                        else m.Write32(e+16,retain);
                        goto case 433;
                    }
                }
                else if(m.Read32(inode+PrefixLink)!=0) return ReadPrefix(inode,432);
                m.Write32(inode+68,R(ResizeLength)); Dirty(inode);
                uint group=R(Group);
                m.Write32(group+72,m.Read32(group+72)+R(ResizeFree)); Dirty(group);
                return R(Resume)==67?Next(67):FinishWideRetirement(R(Resume));
            }
            case 432:
            {
                uint page=Ptr(ExtentBuffer);
                if(!VerifiedPrefix(inode,page)) return Fatal();
                m.Copy(inode+320,page+320,192); m.Write32(inode+96,8);
                m.Write32(inode+PrefixLink,m.Read32(page+PrefixLink));
                m.Write32(inode+PrefixEnd,m.Read32(page+PrefixEnd));
                W(RetireFirst,R(PrefixSector)); W(RetireLength,1);
                goto case 433;
            }
            case 433:
                if(R(RetireLength)==0) return Next(431);
                if(R(RetireBitmap)!=0)
                {
                    if(m.Read32(R(RetireBitmap)+64)>>11==R(RetireFirst)>>11) goto case 434;
                    return StageRetirementPage(R(RetireBitmap),435);
                }
                return Load(BitmapHome(R(RetireFirst)),434);
            case 434:
            {
                uint bitmap=R(RetireBitmap);
                if(bitmap==0) { bitmap=R(Loaded); W(RetireBitmap,bitmap); }
                uint sector=R(RetireFirst),take=2048-(sector&2047);
                if(take>R(RetireLength)) take=R(RetireLength);
                if(WideBitmapRun(bitmap+128,sector&2047,take,uint.MaxValue)!=take) return Fatal();
                SetRange(bitmap+128,sector&2047,take,false);
                DirtyWideBitmap(bitmap,sector>>11,R(WideActiveSelector));
                W(RetireFirst,sector+take); W(RetireLength,R(RetireLength)-take);
                W(ResizeFree,R(ResizeFree)+take);
                return Next(433);
            }
            case 435:
                ReleaseStagedPage();
                return StageRetirementPage(R(WideActiveSelector),436);
            case 436:
                ReleaseStagedPage(); W(RetireBitmap,0);
                return Next(433);
            default: return Fatal();
        }
    }

    private StepResult StageRetirementPage(uint page,uint resume)
    {
        uint entry=Meta((page-Ptr(CacheImages))>>9);
        return WriteWidePage(m.Read32(entry),entry,page,R(Generation)+1,resume);
    }

    private void ReleaseStagedPage()
    {
        uint count=R(DirtyCount),entry=Meta(m.Read8(Ptr(DirtySlots)+count-1));
        m.Write32(entry+4,0); m.Write32(entry+8,0); m.Write32(entry+12,R(Epoch)-1);
        W(DirtyCount,count-1);
    }
}
