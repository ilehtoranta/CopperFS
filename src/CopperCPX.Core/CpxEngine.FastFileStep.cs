namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory:struct,ICpxMemory
{
    // Dispatch known file continuations before entering the general tree/file
    // frame. Each visit still consumes exactly one engine phase.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private StepResult FastFileDataStep(uint phase)
    {
        if(phase==500)
            return Issue(IoKind.Write,GetWide(Arena+NewPhysical),memory.Read32(Arena+FileBuffer),memory.Read32(Arena+FileBytes),501);
        if(phase==501) return FinishFastAppend();
        if(phase is 502 or 503) return PrepareCrossWindowAppend();
        if(phase is >=510 and <=512) return PrepareFastInitialWrite();
        if(phase is >=520 and <=532) return FastMappedStep(phase);
        if(phase is >=540 and <=543) return FastPartialMappedStep(phase);
        if(phase is >=550 and <=552) return PartialWriteWindowStep(phase);
        return Fail(CpxError.Corrupt);
    }
}
