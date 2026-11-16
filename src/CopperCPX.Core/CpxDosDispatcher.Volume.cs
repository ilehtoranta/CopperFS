namespace CopperCPX;
public partial struct CpxDosDispatcher<TMemory>
    where TMemory : struct, ICpxMemory
{
    public uint VolumeLabelAddress => engine.VolumeLabelAddress;

    private StepResult BeginVolumePacket()
    {
        uint cpxArenaBase = arena;
        uint name = Arg(0);
        if (name == 0 || name > 0x3fffffff)
            return Finish(0, 210);
        name <<= 2;
        uint length = memory.Read8(name);
        if (!engine.ValidLabelInput(name + 1, length))
            return Finish(0, 210);
        if (memory.Read32(cpxArenaBase + 512 + (Action)) == 1020 && Arg(1) != CpxArena.Magic)
            return Finish(0, 115);
        // Format cannot invalidate objects/messages owned by other DOS clients.
        // One phase scans the 64 handle/lock headers; the next scans async pools.
        if (memory.Read32(cpxArenaBase + 512 + (Action)) == 1020 && HasOpenResources)
            return Finish(0, 202);
        CopyInfo((cpxArenaBase + 1024), name + 1, length);
        memory.Write32(cpxArenaBase + 512 + (PathLength), length);
        memory.Write32(cpxArenaBase + 512 + (Phase), 130);
        return StepResult.Pending;
    }

    private StepResult VolumeStep()
    {
        uint cpxArenaBase = arena;
        if (memory.Read32(cpxArenaBase + 512 + (Phase)) == 130)
        {
            if (memory.Read32(cpxArenaBase + 512 + (Action)) == 9)
                return Start(engine.BeginLabel((cpxArenaBase + 1024), memory.Read32(cpxArenaBase + 512 + (PathLength))), 132);
            if ((memory.Read32(cpxArenaBase + 512 + (RecordRetained)) != 0 || memory.Read32(cpxArenaBase + 512 + (NotifyCount)) != 0) || memory.Read32(cpxArenaBase + 512 + (RecordRetained)) != 0)
                return Finish(0, 202);
            if (engine.HasBufferedChanges)
                return Start(engine.BeginOperation(CpxOperation.Flush), 133);
            CpxError admission = engine.BeginReformat((cpxArenaBase + 1024), memory.Read32(cpxArenaBase + 512 + (PathLength)));
            if (admission == CpxError.None)
            {
                RetireMediaCursors();
                memory.Write32(cpxArenaBase + 512 + (MediaReason), 213);
            }

            return Start(admission, 131);
        }

        StepResult result = engine.Step();
        if (result is StepResult.Pending or StepResult.NeedIo)
            return result;
        if (result == StepResult.Failed)
        {
            memory.Write32(cpxArenaBase + 512 + (MediaReason), 213);
            return Finish(0, ToDosError(engine.Error));
        }

        if (memory.Read32(cpxArenaBase + 512 + (Phase)) == 133)
        {
            memory.Write32(cpxArenaBase + 512 + (Phase), 130);
            return StepResult.Pending;
        }

        if (memory.Read32(cpxArenaBase + 512 + (Phase)) == 131)
        {
            memory.Write32(cpxArenaBase + 512 + (MediaReason), 0);
            memory.Write32(cpxArenaBase + 512 + (Inhibited), 0);
            RefreshWatches();
        }
        else
            MarkNotificationChange(Wide.From(1), default, default);
        return Finish(uint.MaxValue);
    }
}