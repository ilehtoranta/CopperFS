namespace CopperCPX;

public partial struct CpxDosDispatcher<TMemory> where TMemory : struct, ICpxMemory
{
    public uint VolumeLabelAddress=>engine.VolumeLabelAddress;
    private StepResult BeginVolumePacket()
    {
        uint name=Arg(0); if(name==0||name>0x3fffffff) return Finish(0,210);
        name<<=2; uint length=memory.Read8(name);
        if(!engine.ValidLabelInput(name+1,length)) return Finish(0,210);
        if(memory.Read32(arena+512+(Action))==1020&&Arg(1)!=CpxArena.Magic) return Finish(0,115);
        // Format cannot invalidate objects/messages owned by other DOS clients.
        // One phase scans the 64 handle/lock headers; the next scans async pools.
        if(memory.Read32(arena+512+(Action))==1020&&HasOpenResources) return Finish(0,202);
        CopyInfo(Path,name+1,length); memory.Write32(arena+512+(PathLength),length); memory.Write32(arena+512+(Phase),130); return StepResult.Pending;
    }
    private StepResult VolumeStep()
    {
        if(memory.Read32(arena+512+(Phase))==130)
        {
            if(memory.Read32(arena+512+(Action))==9) return Start(engine.BeginLabel(Path,memory.Read32(arena+512+(PathLength))),132);
            if(HasAsyncResources||memory.Read32(arena+512+(RecordRetained))!=0) return Finish(0,202);
            if(engine.HasBufferedChanges) return Start(engine.BeginOperation(CpxOperation.Flush),133);
            CpxError admission=engine.BeginReformat(Path,memory.Read32(arena+512+(PathLength)));
            if(admission==CpxError.None) { RetireMediaCursors(); memory.Write32(arena+512+(MediaReason),213); }
            return Start(admission,131);
        }
        StepResult result=engine.Step(); if(result is StepResult.Pending or StepResult.NeedIo) return result;
        if(result==StepResult.Failed) { memory.Write32(arena+512+(MediaReason),213); return Finish(0,ToDosError(engine.Error)); }
        if(memory.Read32(arena+512+(Phase))==133) { memory.Write32(arena+512+(Phase),130); return StepResult.Pending; }
        if(memory.Read32(arena+512+(Phase))==131) { memory.Write32(arena+512+(MediaReason),0); memory.Write32(arena+512+(Inhibited),0); RefreshWatches(); }
        else MarkNotificationChange(Wide.From(1),default,default);
        return Finish(uint.MaxValue);
    }
}

