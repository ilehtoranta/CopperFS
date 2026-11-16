namespace CopperCPX;
// The CSTR v1 wire format is shared with CopperTrim. Arg1 is an APTR.
public partial struct CpxDosDispatcher<TMemory>
    where TMemory : struct, ICpxMemory
{
    public const uint TrimPacket = 0x43535452;
    private const uint MaintenancePhase = 161;
    public bool InternalMaintenance => memory.Read32(arena + 512 + (Phase)) == MaintenancePhase;

    public bool MaintenanceAllowed
    {
        get
        {
            uint cpxArenaBase = arena;
            return (!(memory.Read32(cpxArenaBase + 512 + (Inhibited)) != 0) && memory.Read32(cpxArenaBase + 512 + (MediaReason)) == 0 && engine.IsMounted) && !(engine.IsReadOnly || memory.Read32(cpxArenaBase + 512 + (SoftwareProtected)) != 0) && (engine.DiscardEnabled || engine.BufferedWrites);
        }
    }

    public uint MediaEpoch => engine.Epoch;

    public bool TryBeginMaintenance()
    {
        uint cpxArenaBase = arena;
        if ((memory.Read32(cpxArenaBase + 512 + (BusyAt)) != 0) || !((!(memory.Read32(cpxArenaBase + 512 + (Inhibited)) != 0) && memory.Read32(cpxArenaBase + 512 + (MediaReason)) == 0 && engine.IsMounted) && !(engine.IsReadOnly || memory.Read32(cpxArenaBase + 512 + (SoftwareProtected)) != 0) && (engine.DiscardEnabled || engine.BufferedWrites)) || engine.BeginOperation(engine.HasBufferedChanges ? CpxOperation.Flush : CpxOperation.TrimFree) != CpxError.None)
            return false;
        memory.Write32(cpxArenaBase + 512 + (Phase), MaintenancePhase);
        memory.Write32(cpxArenaBase + 512 + (BusyAt), 1);
        memory.Write32(cpxArenaBase + 512 + (ErrorAt), 0);
        return true;
    }

    private StepResult MaintenanceStep()
    {
        uint cpxArenaBase = arena;
        var result = engine.Step();
        if (result is StepResult.Pending or StepResult.NeedIo)
            return result;
        uint reason = memory.Read32(cpxArenaBase + 512 + (StopReason));
        memory.Write32(cpxArenaBase + 512 + (ErrorAt), reason != 0 ? reason : result == StepResult.Failed ? ToDosError(engine.Error) : 0);
        memory.Write32(cpxArenaBase + 512 + (StopReason), 0);
        memory.Write32(cpxArenaBase + 512 + (BusyAt), 0);
        return StepResult.Complete;
    }

    private StepResult BeginManualTrimPacket()
    {
        uint cpxArenaBase = arena;
        uint record = Arg(0);
        if (!Buffer(record, 32) || memory.Read32(record) < 32 || memory.Read32(record + 4) != 1)
            return Finish(0, 115);
        uint flags = memory.Read32(record + 8);
        if (flags > 2)
            return Finish(0, 115);
        for (uint n = 12; n < 32; n += 4)
            memory.Write32(record + n, 0);
        memory.Write32(cpxArenaBase + 512 + (Destination), record);
        return Start(engine.BeginManualTrim(flags, (engine.IsReadOnly || memory.Read32(cpxArenaBase + 512 + (SoftwareProtected)) != 0)), 160);
    }

    private StepResult ManualTrimPacketStep()
    {
        StepResult step = engine.Step();
        if (step is StepResult.NeedIo or StepResult.Pending)
            return step;
        return step == StepResult.Complete ? Finish(uint.MaxValue) : Finish(0, ToDosError(engine.Error));
    }

    private void PublishTrimResult()
    {
        uint record = memory.Read32(arena + 512 + (Destination));
        memory.Write32(record + 12, engine.ManualTrimBlocks);
        memory.Write32(record + 16, engine.ManualTrimRanges);
        memory.Write32(record + 20, engine.ManualTrimMaximum);
        memory.Write32(record + 24, engine.ManualTrimMaximum == 0 ? 0u : 1u);
    }
}