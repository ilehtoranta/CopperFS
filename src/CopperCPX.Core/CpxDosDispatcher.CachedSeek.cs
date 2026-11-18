namespace CopperCPX;

public partial struct CpxDosDispatcher<TMemory> where TMemory : struct, ICpxMemory
{
    private StepResult FinishCachedClassicSeek(uint handle,uint record)
    {
        uint old=memory.Read32(handle+12),mode=Arg(2),offset=Arg(1),basis;
        if(memory.Read32(handle+8)!=0||old>int.MaxValue) return Finish(uint.MaxValue,219);
        if(mode==uint.MaxValue) basis=0;
        else if(mode==0) basis=old;
        else if(mode==1)
        {
            // A signed classic offset cannot bring a size above 4 GiB back
            // into the classic signed position range, including INT_MIN.
            if(memory.Read32(record+8)!=0) return Finish(uint.MaxValue,219);
            basis=memory.Read32(record+12);
        }
        else return Finish(uint.MaxValue,219);
        uint next;
        if((offset&0x80000000)==0)
        {
            next=unchecked(basis+offset);
            if(next<basis) return Finish(uint.MaxValue,219);
        }
        else
        {
            uint magnitude=unchecked(0u-offset);
            if(magnitude>basis) return Finish(uint.MaxValue,219);
            next=basis-magnitude;
        }
        if(next>int.MaxValue||memory.Read32(record+8)==0&&next>memory.Read32(record+12)) return Finish(uint.MaxValue,219);
        memory.Write32(handle+12,next);
        return Finish(old);
    }
}
