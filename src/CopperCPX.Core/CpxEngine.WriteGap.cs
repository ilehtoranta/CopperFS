namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    private const uint WriteGapPrepared=15520;
    // Before publishing a sparse extension, erase the old EOF unit's hidden
    // suffix. The size remains unchanged until the write's first chunk retires.
    // A crash here cannot expose the suffix and retains the original prefix.
    private StepResult WriteGapStep()
    {
        uint obj=ResizeObject,extent=ResizeExtent,unit=1u<<(int)R(UnitShift);
        Wide old=RW(OldFileSize),logical=Wide.Right(old,(int)R(UnitShift));
        switch(R(Phase))
        {
            case 0:
                Clear(extent,24);
                if(memory.Read8(obj+85)==2)
                { StartSeek(CpxTreeKind.Extent,GetWide(obj+24),GetWide(obj),logical,default,CpxSeek.Floor); return Call(CpxOperation.ReadTree,1); }
                uint count=Read16(obj+90); if(memory.Read8(obj+85)!=1||count>6) return Fail(CpxError.Corrupt);
                for(uint n=0;n<count;n++) if(Wide.Compare(GetWide(obj+96+n*24),logical)<=0) Copy(extent,obj+96+n*24,24);
                W(Phase,2); return StepResult.Pending;
            case 1: if(TreeFound) Copy(extent,TreeRecord,24); W(Phase,2); return StepResult.Pending;
            case 2:
                uint length=memory.Read32(extent+16),flags=memory.Read32(extent+20);
                if(length==0) return Done();
                if(length>65536||flags>1||!Wide.Add(GetWide(extent),Wide.From(length),out var end)||!Wide.Subtract(logical,GetWide(extent),out var delta)) return Fail(CpxError.Corrupt);
                if(Wide.Compare(logical,end)>=0||flags==1) return Done();
                if(!Wide.Add(GetWide(extent+8),delta,out var physical)||!DataUnit(physical)) return Fail(CpxError.Corrupt);
                WW(ResizePhysical,physical); return Issue(IoKind.Read,physical,Page(4),unit,3);
            case 3:
                uint tail=old.Low&(unit-1); Clear(Page(4)+tail,unit-tail);
                return Issue(IoKind.Write,RW(ResizePhysical),Page(4),unit,4);
            case 4: return Issue(IoKind.Flush,default,0,0,5);
            case 5: return Done();
            default: return Fail(CpxError.Corrupt);
        }
    }
}
