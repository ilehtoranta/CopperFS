namespace CopperCPX;

public partial struct CpxDosDispatcher<TMemory> where TMemory : struct, ICpxMemory
{
    private const uint Inhibited=324,MediaReason=328,StopReason=332;
    public bool IsInhibited=>R(Inhibited)!=0;
    public bool MediaAvailable=>!IsInhibited&&R(MediaReason)==0&&engine.IsMounted;
    public bool StorageStopPending=>R(StopReason)!=0;

    // The adapter must abort/reap transport I/O before delivering CompleteIo.
    // Marking cancellation never releases a device-owned buffer or packet.
    public void RequestStorageCancellation()
    {
        uint error=R(MediaReason)==226?226u:304u; CancelRecordWaits(error);
        if(!Busy) return;
        W(StopReason,error); if(error!=226) W(MediaReason,213); engine.Cancel();
    }
    public void MediaRemoved()
    {
        W(MediaReason,226); CancelRecordWaits(226); SuspendWatches();
        if(Busy) W(StopReason,226);
        engine.Cancel();
    }
    private void SuspendWatches()
    {
        for(uint n=0;n<8;n++)
        {
            uint p=Watches+n*896+8;
            if(memory.Read32(p-8)==0||memory.Read32(p+36)==0) continue;
            if(!G(p+16).IsZero) memory.Write32(p+32,1);
            Put(p+16,default); memory.Write32(p+24,0); memory.Write32(p+28,0);
        }
    }
    private void RefreshWatches()
    {
        for(uint n=0;n<8;n++)
        { uint p=Watches+n*896+8; if(memory.Read32(p-8)!=0&&memory.Read32(p+36)!=0) memory.Write32(p+24,1); }
    }
    private void RetireMediaCursors()
    { for(uint n=0;n<16;n++) memory.Write32(Pool+3584+n*64,0); }
    private StepResult StopStorageStep()
    {
        if(engine.HasOutstandingIo) return StepResult.NeedIo;
        uint error=R(StopReason); W(StopReason,0); RetireMediaCursors();
        if(R(Phase)==20&&(R(Action) is 82 or 87)) return FinishTransfer(error);
        return Finish(R(Action) is 82 or 87 or 1008 or 1022?uint.MaxValue:0,error);
    }
    private StepResult BeginInhibit()
    {
        if(Arg(0)!=0&&IsInhibited) return Finish(uint.MaxValue);
        if(Arg(0)==0&&MediaAvailable) return Finish(uint.MaxValue);
        if(HasOpenResources||R(RecordRetained)!=0) return Finish(0,202);
        W(Phase,122); return StepResult.Pending;
    }
    private StepResult AdmitInhibit()
    {
        if(Arg(0)!=0)
        {
            if(!MediaAvailable) { W(Inhibited,1); RetireMediaCursors(); SuspendWatches(); return Finish(uint.MaxValue); }
            return Start(IsWriteProtected?engine.BeginPersistenceBarrier():engine.BeginOperation(CpxOperation.Flush),120);
        }
        // The old object IDs/cursors are never reused against replacement media.
        RetireMediaCursors(); W(MediaReason,226);
        return Start(engine.BeginRemount(),121);
    }
    private StepResult MediaStep()
    {
        if(R(Phase)==122) return AdmitInhibit();
        StepResult result=engine.Step(); if(result is StepResult.Pending or StepResult.NeedIo) return result;
        if(result==StepResult.Failed) { W(MediaReason,213); return Finish(0,ToDosError(engine.Error)); }
        if(R(Phase)==120) { W(Inhibited,1); RetireMediaCursors(); SuspendWatches(); }
        else { W(Inhibited,0); W(MediaReason,0); RefreshWatches(); }
        return Finish(uint.MaxValue);
    }
    private StepResult OfflineInformation()
    {
        uint bptr=R(Action)==25?Arg(0):Arg(1);
        if(bptr>0x3fffffff||!Buffer(bptr<<2,36)) return Finish(0,115);
        if(R(Action)==26&&!LockId(Arg(0),out _)) return Finish(0,211);
        uint p=bptr<<2; for(uint n=0;n<36;n+=4) memory.Write32(p+n,0);
        memory.Write32(p+24,uint.MaxValue); return Finish(uint.MaxValue);
    }
}
