namespace CopperCPX;

// The CSTR v1 wire format is shared with CopperTrim. Arg1 is an APTR.
public partial struct CpxDosDispatcher<TMemory> where TMemory : struct, ICpxMemory
{
    public const uint TrimPacket=0x43535452;
    private StepResult BeginManualTrimPacket()
    {
        uint record=Arg(0);
        if(!Buffer(record,32)||memory.Read32(record)<32||memory.Read32(record+4)!=1) return Finish(0,115);
        uint flags=memory.Read32(record+8);
        if(flags>2) return Finish(0,115);
        for(uint n=12;n<32;n+=4) memory.Write32(record+n,0);
        W(Destination,record);
        return Start(engine.BeginManualTrim(flags,IsWriteProtected),160);
    }
    private StepResult ManualTrimPacketStep()
    {
        StepResult step=engine.Step();
        if(step is StepResult.NeedIo or StepResult.Pending) return step;
        return step==StepResult.Complete?Finish(uint.MaxValue):Finish(0,ToDosError(engine.Error));
    }
    private void PublishTrimResult()
    {
        uint record=R(Destination);
        memory.Write32(record+12,engine.ManualTrimBlocks); memory.Write32(record+16,engine.ManualTrimRanges);
        memory.Write32(record+20,engine.ManualTrimMaximum);
        memory.Write32(record+24,engine.ManualTrimMaximum==0?0u:1u);
    }
}
