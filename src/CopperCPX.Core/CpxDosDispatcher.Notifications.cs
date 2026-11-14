namespace CopperCPX;

public struct CpxNotification { public uint Message,Target,SignalMask; }

public partial struct CpxDosDispatcher<TMemory> where TMemory : struct, ICpxMemory
{
    private const uint NotifyInternal=304,NotifySlot=308,NotifyScan=312,NotifyCount=316,NotifyRemoved=320;
    private uint Watches=>Pool+4608;
    private uint NotifyPacket=>arena+1856;
    public bool InternalNotification=>R(NotifyInternal)!=0;
    public bool HasAsyncResources=>R(RecordRetained)!=0||R(NotifyCount)!=0;
    public uint RemovedNotificationStart=>R(NotifyRemoved);
    public bool HasNotificationDelivery
    {
        get
        {
            if(R(NotifyCount)==0) return false;
            for(uint n=0;n<8;n++)
            {
                uint p=Watches+n*896+8;
                if(memory.Read32(p-8)==0||memory.Read32(p+36)==0||memory.Read32(p+32)==0||memory.Read32(p+24)!=0) continue;
                uint flags=memory.Read32(p+4),count=memory.Read32(p+48);
                if((flags&2)!=0||count<8&&((flags&8)==0||count==0)) return true;
            }
            return false;
        }
    }
    // Watch payload: request/flags/target/signal, object ID, dirty/hint/pending,
    // active, path length, spare, outstanding, then 256 name bytes at +64.
    // Eight 64-byte message slots at +320 contain state + generation + Exec msg.
    private StepResult BeginAddNotification()
    {
        uint request=Arg(0); if(!Buffer(request,48)) return Finish(0,115);
        uint flags=memory.Read32(request+12),target=memory.Read32(request+16),name=memory.Read32(request+4);
        if((flags&~27u)!=0||(flags&3) is not (1 or 2)||!Buffer(target,8)||name==0||name>uint.MaxValue-256||
            (flags&2)!=0&&memory.Read8(request+20)>31) return Finish(0,115);
        for(uint n=0;n<8;n++) { uint p=Watches+n*896+8; if(memory.Read32(p-8)!=0&&memory.Read32(p)==request) return Finish(0,202); }
        if(pools.Take(ResourceKind.Notification,out uint watch,out _)!=CpxError.None) return Finish(0,103);
        W(NotifyCount,R(NotifyCount)+1); W(NotifySlot,watch); W(NotifyScan,0);
        memory.Write32(watch,request); memory.Write32(watch+4,flags); memory.Write32(watch+8,target); memory.Write32(watch+12,memory.Read8(request+20));
        W(PathAt,0); W(Phase,100); return StepResult.Pending;
    }
    private StepResult NotificationStep()
    {
        uint watch=R(NotifySlot),at=R(NotifyScan); bool adding=!InternalNotification;
        uint source=adding?memory.Read32(memory.Read32(watch)+4):watch+64;
        for(uint work=0;work<64;work++,at++)
        {
            byte c=adding?memory.Read8(source+at):at==memory.Read32(watch+40)?(byte)0:memory.Read8(source+at);
            if(c==0)
            {
                memory.Write32(watch+40,at); W(PathLength,at); Put(State+Object,Wide.From(1));
                W(Phase,10); return StepResult.Pending;
            }
            if(at==255) return Finish(0,120);
            if(adding) memory.Write8(watch+64+at,c);
            memory.Write8(Path+at,c); if(c==58) W(PathAt,at+1);
        }
        W(NotifyScan,at); return StepResult.Pending;
    }
    private void RetireWatch(uint watch)
    { memory.Write32(watch-8,0); W(NotifyCount,R(NotifyCount)-1); }
    private void FinishNotification(ref uint value,ref uint error)
    {
        uint watch=R(NotifySlot); if(watch==0) return;
        bool found=error==0&&value!=0,missing=error is 204 or 205;
        if(InternalNotification)
        {
            if(found||missing)
            {
                Wide current=found?G(State+Object):default;
                if(!Wide.Equal(current,G(watch+16))||memory.Read32(watch+28)!=0) memory.Write32(watch+32,1);
                Put(watch+16,current); memory.Write32(watch+28,0);
            }
            else if(memory.Read32(watch+28)!=0) memory.Write32(watch+32,1); // known retired change, even on poisoned media
            memory.Write32(watch+24,0); // no retry spin on a poisoned mount
        }
        else if(found||missing)
        {
            Put(watch+16,found?G(State+Object):default); memory.Write32(watch+36,1);
            memory.Write32(memory.Read32(watch)+40,0); memory.Write32(memory.Read32(watch)+44,R(Port));
            if(found&&(memory.Read32(watch+4)&16)!=0) memory.Write32(watch+32,1);
            value=uint.MaxValue; error=0;
        }
        else RetireWatch(watch);
        W(NotifySlot,0);
    }
    private StepResult RemoveNotification()
    {
        uint request=Arg(0); if(!Buffer(request,48)) return Finish(0,115);
        for(uint n=0;n<8;n++)
        {
            uint p=Watches+n*896+8;
            if(memory.Read32(p-8)==0||memory.Read32(p)!=request) continue;
            memory.Write32(p,0); memory.Write32(p+24,0); memory.Write32(p+28,0); memory.Write32(p+32,0); memory.Write32(p+36,0);
            memory.Write32(request+40,0);
            W(NotifyRemoved,n*8);
            if(memory.Read32(p+48)==0) RetireWatch(p);
            break;
        }
        return Finish(uint.MaxValue);
    }
    private void MarkNotificationChange(Wide changed,Wide parent,Wide oldParent)
    {
        if(R(NotifyCount)==0) return;
        for(uint n=0;n<8;n++)
        {
            uint p=Watches+n*896+8;
            if(memory.Read32(p-8)==0||memory.Read32(p+36)==0) continue;
            Wide key=G(p+16); memory.Write32(p+24,1);
            if(!key.IsZero&&(Wide.Equal(key,changed)||Wide.Equal(key,parent)||Wide.Equal(key,oldParent))) memory.Write32(p+28,1);
        }
    }
    private void NotificationMutation(uint value,uint error)
    {
        uint action=R(Action);
        if((action is 16 or 17 or 1021)&&R(Phase)==31)
        {
            if(engine.NamespacePublished) MarkNotificationChange(engine.MutatedObject,G(State+ParentId),action==17?G(State+OldParent):default);
        }
        else if(error==0&&value!=0&&(action is 21 or 28 or 34 or 1036)) MarkNotificationChange(G(State+Object),G(State+ParentId),default);
    }
    // The adapter drains these read-only refresh operations before admitting
    // another packet, so create/delete transitions cannot disappear between sweeps.
    public bool TryBeginNotificationRefresh()
    {
        if(Busy||R(NotifyCount)==0||!MediaAvailable) return false;
        for(uint n=0;n<8;n++)
        {
            uint p=Watches+n*896+8;
            if(memory.Read32(p-8)==0||memory.Read32(p+36)==0||memory.Read32(p+24)==0) continue;
            memory.Write32(NotifyPacket+8,4097); memory.Write32(NotifyPacket+20,memory.Read32(p));
            if(BeginPacket(NotifyPacket)!=CpxError.None) return false;
            W(NotifyInternal,1); W(NotifySlot,p); W(NotifyScan,0); W(PathAt,0); W(Phase,100); return true;
        }
        return false;
    }
    public bool TryTakeNotification(out CpxNotification delivery)
    {
        delivery=default;
        for(uint n=0;n<8;n++)
        {
            uint p=Watches+n*896+8;
            if(memory.Read32(p-8)==0||memory.Read32(p+36)==0||memory.Read32(p+32)==0||memory.Read32(p+24)!=0) continue;
            uint flags=memory.Read32(p+4),count=memory.Read32(p+48);
            if((flags&2)!=0)
            { delivery.Target=memory.Read32(p+8); delivery.SignalMask=1u<<(int)memory.Read32(p+12); memory.Write32(p+32,0); return true; }
            if(count>=8||(flags&8)!=0&&count!=0) continue;
            // Only scan this watch's eight messages on this call. A full pool
            // coalesces pending changes until a message comes back.
            for(uint i=0;i<8;i++)
            {
                uint cell=p+320+i*64; if(memory.Read32(cell)!=0) continue;
                uint message=cell+8; for(uint b=0;b<40;b+=4) memory.Write32(message+b,0);
                memory.Write32(cell,1); memory.Write32(cell+4,memory.Read32(p-4));
                memory.Write8(message+8,5); memory.Write32(message+14,R(Port)); memory.Write8(message+19,38);
                memory.Write32(message+20,0x40000000); memory.Write8(message+24,0x12); memory.Write8(message+25,0x34);
                memory.Write32(message+26,memory.Read32(p)); memory.Write32(message+30,p);
                memory.Write32(p+48,count+1); memory.Write32(memory.Read32(p)+40,count+1); memory.Write32(p+32,0);
                delivery.Message=message; delivery.Target=memory.Read32(p+8); return true;
            }
            return false;
        }
        return false;
    }
    public bool NotificationReply(uint message)
    {
        // Address membership, state and generation are owned here. Never trust
        // the application's private fields to identify or free an allocation.
        for(uint n=0;n<8;n++)
        {
            uint p=Watches+n*896+8;
            if(message<p+328||message>p+328+7*64) continue;
            for(uint i=0;i<8;i++)
            {
            uint cell=p+320+i*64;
            if(message!=cell+8) continue;
            if(memory.Read32(p-8)==0||memory.Read32(cell)!=1||memory.Read32(cell+4)!=memory.Read32(p-4)) return false;
            memory.Write32(cell,0); uint count=memory.Read32(p+48)-1; memory.Write32(p+48,count);
            uint request=memory.Read32(p);
            if(request!=0) memory.Write32(request+40,count);
            else if(count==0) RetireWatch(p);
            return true;
            }
        }
        return false;
    }
    public bool TryGetRemovedNotification(uint index,out uint message)
    {
        message=0; if(index>=64) return false;
        uint p=Watches+(index>>3)*896+8,cell=p+320+(index&7)*64;
        if(memory.Read32(p-8)==0||memory.Read32(p+36)!=0||memory.Read32(cell)!=1) return false;
        message=cell+8; return true;
    }
}
