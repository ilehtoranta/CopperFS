namespace CopperCPX;

public struct CpxRecordTimer { public uint Slot,Token,Ticks; public bool Cancel; }
public struct CpxPacketReply { public uint Packet,Message,ReplyPort,Result,Error; }

public partial struct CpxDosDispatcher<TMemory> where TMemory : struct, ICpxMemory
{
    private const uint Deferred=240,RecordWork=244,RecordCursor=248,RecordWaitCount=252,
        RecordReadyCount=256,RecordCount=260,RecordRetained=264,RecordTimerWork=268,RecordOffset=272,RecordLength=280,RecordMode=288,RecordTicks=292;
    private uint Records=>Pool+11776;
    private uint Waiters=>Pool+14336;
    public bool PacketDeferred=>memory.Read32(arena+512+(Deferred))!=0;
    public bool HasRecordWaits=>memory.Read32(arena+512+(RecordWaitCount))!=0;
    public bool HasRecordWork=>memory.Read32(arena+512+(RecordWork))!=0||memory.Read32(arena+512+(RecordReadyCount))!=0||memory.Read32(arena+512+(RecordTimerWork))!=0;
    public uint NextRecordWaitSlot=>Waiters+memory.Read32(arena+512+(RecordCursor))*64+8;
    private void RecordChanged() { if(HasRecordWaits) memory.Write32(arena+512+(RecordWork),16); }
    private bool RecordOwner(uint owner,uint generation)=>memory.Read32(owner-8)==1&&memory.Read32(owner-4)==generation;
    private static bool Overlap(Wide a,Wide length,Wide b,Wide otherLength)
    {
        if(length.IsZero||otherLength.IsZero) return false;
        if(Wide.Compare(a,b)<=0) { Wide.Subtract(b,a,out var distance); return Wide.Compare(distance,length)<0; }
        Wide.Subtract(a,b,out var reverse); return Wide.Compare(reverse,otherLength)<0;
    }
    // One 32-slot scan both tests conflicts and remembers an available slot.
    private bool RecordConflict(uint owner,Wide id,Wide offset,Wide length,uint mode,out uint available)
    {
        available=0;
        for(uint n=0;n<32;n++)
        {
            uint p=Records+n*80+8;
            if(memory.Read32(p-8)==0)
            { if(available==0&&memory.Read32(p-4)!=uint.MaxValue) available=p; continue; }
            if(memory.Read32(p)!=owner&&Wide.Equal(G(p+8),id)&&(memory.Read32(p+32)&mode&2)==0&&Overlap(offset,length,G(p+16),G(p+24))) return true;
        }
        return false;
    }
    private void GrantRecord(uint p,uint owner,Wide offset,Wide length,uint mode)
    {
        memory.Write32(p-4,memory.Read32(p-4)+1); memory.Write32(p-8,1);
        memory.Write32(p,owner); memory.Write32(p+4,memory.Read32(owner-4)); Put(p+8,G(owner));
        Put(p+16,offset); Put(p+24,length); memory.Write32(p+32,mode);
        memory.Write32(arena+512+(RecordCount),memory.Read32(arena+512+(RecordCount))+1); RecordChanged();
    }
    private void ReleaseRecord(uint p)
    { memory.Write32(p-8,0); memory.Write32(arena+512+(RecordCount),memory.Read32(arena+512+(RecordCount))-1); RecordChanged(); }
    private StepResult BeginRecord()
    {
        uint owner=Arg(0); if(!ValidResource(owner,ResourceKind.Handle)) return Finish(0,211);
        Wide offset=Wide.From(Arg(1)),length=Wide.From(Arg(2));
        if(memory.Read32(arena+512+(Action))>=26400)
        {
            if(!Buffer(Arg(1),8)||!Buffer(Arg(2),8)) return Finish(0,115);
            offset=G(Arg(1)); length=G(Arg(2));
        }
        if(!Wide.Add(offset,length,out _)) return Finish(0,115);
        memory.Write32(arena+512+(Handle),owner); Put(State+RecordOffset,offset); Put(State+RecordLength,length);
        memory.Write32(arena+512+(RecordMode),Arg(3)); memory.Write32(arena+512+(RecordTicks),Arg(4)); memory.Write32(arena+512+(Phase),110); return StepResult.Pending;
    }
    private StepResult RecordStep()
    {
        uint owner=memory.Read32(arena+512+(Handle)),phase=memory.Read32(arena+512+(Phase));
        if(phase==111)
        {
            CloseRecords(owner); memory.Write32(arena+512+(Phase),112); return StepResult.Pending;
        }
        if(phase==112)
        {
            if(memory.Read32(owner+40)!=0) MarkNotificationChange(G(owner),G(owner+24),default);
            pools.Release(ResourceKind.Handle,owner,memory.Read32(owner-4)); return Finish(uint.MaxValue);
        }
        Wide offset=G(State+RecordOffset),length=G(State+RecordLength); uint mode=memory.Read32(arena+512+(RecordMode));
        if(memory.Read32(arena+512+(Action)) is 2009 or 26403)
        {
            for(uint n=0;n<32;n++)
            {
                uint p=Records+n*80+8;
                if(memory.Read32(p-8)==1&&memory.Read32(p)==owner&&Wide.Equal(G(p+16),offset)&&Wide.Equal(G(p+24),length))
                { ReleaseRecord(p); return Finish(uint.MaxValue); }
            }
            return Finish(0,240);
        }
        if(mode>3) return Finish(0,115);
        if(!RecordConflict(owner,G(owner),offset,length,mode,out uint free))
        { if(free==0) return Finish(0,103); GrantRecord(free,owner,offset,length,mode); return Finish(uint.MaxValue); }
        if((mode&1)!=0) return Finish(0,241);
        if(memory.Read32(arena+512+(RecordTicks))==0) return Finish(0,242);
        if(pools.Take(ResourceKind.RecordWait,out uint wait,out _)!=CpxError.None) return Finish(0,103);
        memory.Write32(wait,owner); memory.Write32(wait+4,memory.Read32(owner-4)); Put(wait+8,offset); Put(wait+16,length);
        memory.Write32(wait+24,memory.Read32(arena+512+(Packet))); memory.Write32(wait+28,memory.Read32(memory.Read32(arena+512+(Packet))+4)); memory.Write32(wait+32,memory.Read32(memory.Read32(arena+512+(Packet))));
        memory.Write32(wait+36,mode); memory.Write32(wait+40,memory.Read32(arena+512+(RecordTicks)));
        memory.Write32(arena+512+(RecordWaitCount),memory.Read32(arena+512+(RecordWaitCount))+1); memory.Write32(arena+512+(RecordRetained),memory.Read32(arena+512+(RecordRetained))+1); memory.Write32(arena+512+(RecordTimerWork),memory.Read32(arena+512+(RecordTimerWork))+1); memory.Write32(arena+512+(Deferred),1); memory.Write32(arena+512+(BusyAt),0);
        // The caller must retain the DOS message. No packet output is published
        // until the timer has relinquished its request and a reply is taken.
        return StepResult.Complete;
    }
    private void ResolveRecordWait(uint p,uint error,uint grant=0)
    {
        memory.Write32(p+48,error); memory.Write32(p+52,grant);
        if(memory.Read32(p-8)==1)
        {
            memory.Write32(arena+512+(RecordWaitCount),memory.Read32(arena+512+(RecordWaitCount))-1); memory.Write32(p-8,2);
            if(memory.Read32(p+44)==1) memory.Write32(arena+512+(RecordTimerWork),memory.Read32(arena+512+(RecordTimerWork))+1);
        }
        uint timer=memory.Read32(p+44);
        if(timer is 0 or 3)
        {
            if(timer==0) memory.Write32(arena+512+(RecordTimerWork),memory.Read32(arena+512+(RecordTimerWork))-1);
            if(memory.Read32(p-8)!=3) memory.Write32(arena+512+(RecordReadyCount),memory.Read32(arena+512+(RecordReadyCount))+1);
            memory.Write32(p-8,3); memory.Write32(p+44,3);
        }
    }
    public bool StepRecordWaits()
    {
        if(memory.Read32(arena+512+(RecordWork))==0) return false;
        memory.Write32(arena+512+(RecordWork),memory.Read32(arena+512+(RecordWork))-1); uint index=memory.Read32(arena+512+(RecordCursor)); memory.Write32(arena+512+(RecordCursor),(index+1)&15);
        uint p=Waiters+index*64+8; if(memory.Read32(p-8)!=1) return true;
        uint owner=memory.Read32(p);
        if(!RecordOwner(owner,memory.Read32(p+4))) { ResolveRecordWait(p,211); return true; }
        if(RecordConflict(owner,G(owner),G(p+8),G(p+16),memory.Read32(p+36),out uint free)) return true;
        if(free==0) { ResolveRecordWait(p,103); return true; }
        GrantRecord(free,owner,G(p+8),G(p+16),memory.Read32(p+36)); ResolveRecordWait(p,0,free); return true;
    }
    public bool TryTakeRecordTimer(out CpxRecordTimer timer)
    {
        timer=default;
        if(memory.Read32(arena+512+(RecordTimerWork))==0) return false;
        for(uint n=0;n<16;n++)
        {
            uint p=Waiters+n*64+8,state=memory.Read32(p-8),io=memory.Read32(p+44);
            if(!(state==1&&io==0||state==2&&io==1)) continue;
            timer=new() {Slot=p,Token=memory.Read32(p-4),Ticks=memory.Read32(p+40),Cancel=state==2};
            memory.Write32(p+44,state==2?2u:1u); memory.Write32(arena+512+(RecordTimerWork),memory.Read32(arena+512+(RecordTimerWork))-1); return true;
        }
        return false;
    }
    public CpxError CompleteRecordTimer(uint slot,uint token,uint error=0)
    {
        for(uint n=0;n<16;n++)
        {
            uint p=Waiters+n*64+8; if(p!=slot) continue;
            uint state=memory.Read32(p-8),io=memory.Read32(p+44);
            if(state is not (1 or 2)||io is not (1 or 2)||memory.Read32(p-4)!=token) return CpxError.StaleCompletion;
            if(state==2&&io==1) memory.Write32(arena+512+(RecordTimerWork),memory.Read32(arena+512+(RecordTimerWork))-1);
            memory.Write32(p+44,3);
            ResolveRecordWait(p,state==1?(error==0?242u:error):memory.Read32(p+48),memory.Read32(p+52));
            return CpxError.None;
        }
        return CpxError.InvalidArgument;
    }
    public bool TryTakeRecordReply(out CpxPacketReply reply)
    {
        reply=default; if(memory.Read32(arena+512+(RecordReadyCount))==0) return false;
        for(uint n=0;n<16;n++)
        {
            uint p=Waiters+n*64+8; if(memory.Read32(p-8)!=3) continue;
            uint error=memory.Read32(p+48);
            reply=new() {Packet=memory.Read32(p+24),ReplyPort=memory.Read32(p+28),Message=memory.Read32(p+32),Result=error==0?uint.MaxValue:0,Error=error};
            memory.Write32(reply.Packet+12,reply.Result); memory.Write32(reply.Packet+16,error);
            memory.Write32(p-8,0); memory.Write32(arena+512+(RecordReadyCount),memory.Read32(arena+512+(RecordReadyCount))-1); memory.Write32(arena+512+(RecordRetained),memory.Read32(arena+512+(RecordRetained))-1); return true;
        }
        return false;
    }
    private void CancelRecordSlot(uint p,uint error)
    {
        uint grant=memory.Read32(p+52);
        if(grant!=0) { ReleaseRecord(grant); memory.Write32(p+52,0); }
        ResolveRecordWait(p,error);
    }
    public bool CancelRecordWait(uint packet,uint error=304)
    {
        for(uint n=0;n<16;n++)
        {
            uint p=Waiters+n*64+8;
            if(memory.Read32(p-8)!=0&&memory.Read32(p+24)==packet) { CancelRecordSlot(p,error); return true; }
        }
        return false;
    }
    public void CancelRecordWaits(uint error)
    { for(uint n=0;n<16;n++) { uint p=Waiters+n*64+8; if(memory.Read32(p-8)!=0) CancelRecordSlot(p,error); } }
    private void CloseRecords(uint owner)
    {
        // Detach reserved grants through their waiter first, avoiding double
        // release when a close races a grant awaiting timer cancellation.
        for(uint n=0;n<16;n++) { uint p=Waiters+n*64+8; if(memory.Read32(p-8)!=0&&memory.Read32(p)==owner) CancelRecordSlot(p,211); }
        for(uint n=0;n<32;n++) { uint p=Records+n*80+8; if(memory.Read32(p-8)!=0&&memory.Read32(p)==owner) ReleaseRecord(p); }
    }
}
