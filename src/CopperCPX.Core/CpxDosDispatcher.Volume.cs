namespace CopperCPX;

public partial struct CpxDosDispatcher<TMemory> where TMemory : struct, ICpxMemory
{
    public uint VolumeLabelAddress=>engine.VolumeLabelAddress;
    private StepResult BeginVolumePacket()
    {
        uint name=Arg(0); if(name==0||name>0x3fffffff) return Finish(0,210);
        name<<=2; uint length=memory.Read8(name);
        if(!engine.ValidLabelInput(name+1,length)) return Finish(0,210);
        if(R(Action)==1020&&Arg(1)!=CpxArena.Magic) return Finish(0,115);
        // Format cannot invalidate objects/messages owned by other DOS clients.
        // One phase scans the 64 handle/lock headers; the next scans async pools.
        if(R(Action)==1020&&HasOpenResources) return Finish(0,202);
        CopyInfo(Path,name+1,length); W(PathLength,length); W(Phase,130); return StepResult.Pending;
    }
    private StepResult VolumeStep()
    {
        if(R(Phase)==130)
        {
            if(R(Action)==9) return Start(engine.BeginLabel(Path,R(PathLength)),132);
            if(HasAsyncResources||R(RecordRetained)!=0) return Finish(0,202);
            CpxError admission=engine.BeginReformat(Path,R(PathLength));
            if(admission==CpxError.None) { RetireMediaCursors(); W(MediaReason,213); }
            return Start(admission,131);
        }
        StepResult result=engine.Step(); if(result is StepResult.Pending or StepResult.NeedIo) return result;
        if(result==StepResult.Failed) { W(MediaReason,213); return Finish(0,ToDosError(engine.Error)); }
        if(R(Phase)==131) { W(MediaReason,0); W(Inhibited,0); RefreshWatches(); }
        else MarkNotificationChange(Wide.From(1),default,default);
        return Finish(uint.MaxValue);
    }
}
