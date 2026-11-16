namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // BeginFastMapped has admitted one written extent and an overwrite wholly
    // inside EOF. A small unaligned write can preserve both boundary sectors
    // in one bounded scratch window, then submit one contiguous write.
    private bool TryBeginPartialWriteWindow(uint position,uint bytes,uint units)
    {
        uint context=Arena,shift=memory.Read32(context+UnitShift),unit=1u<<(int)shift;
        if(((position|bytes)&(unit-1))==0) return false;
        uint span=units<<(int)shift,scratch=memory.Read32(context+GeometryPages);
        if(span>3*memory.Read32(context+PageBytes)) return false;
        uint source=memory.Read32(context+FileBuffer);
        // Caller buffers that alias operation scratch retain the ordinary
        // path's ordering. Do not overwrite the input while reading the window.
        if(source<scratch+span&&scratch<source+bytes) return false;
        memory.Write32(context+DataIntra,position&(unit-1));
        memory.Write32(context+DataTake,span); memory.Write32(context+Phase,550);
        return true;
    }

    private StepResult PartialWriteWindowStep(uint phase)
    {
        uint context=Arena,scratch=memory.Read32(context+GeometryPages);
        if(phase==550)
        {
            // The DMA window can cover three scratch pages; retire all their
            // previous authentication leases before any bytes are borrowed.
            InvalidateBufferedPage(scratch);
            InvalidateBufferedPage(memory.Read32(context+GeometryPages+4));
            InvalidateBufferedPage(memory.Read32(context+GeometryPages+8));
            return Issue(IoKind.Read,RW(NewPhysical),scratch,memory.Read32(context+DataTake),551);
        }
        if(phase==551)
        {
            Copy(scratch+memory.Read32(context+DataIntra),memory.Read32(context+FileBuffer),memory.Read32(context+FileBytes));
            return Issue(IoKind.Write,RW(NewPhysical),scratch,memory.Read32(context+DataTake),552);
        }
        memory.Write32(context+FileValue,memory.Read32(context+FileBytes));
        return Done();
    }
}
