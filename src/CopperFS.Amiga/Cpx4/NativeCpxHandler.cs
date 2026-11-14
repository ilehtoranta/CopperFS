#if COPPERFS_CPX4
using Amiga;
namespace CopperCPX.Amiga;

/// <summary>Experimental CPX4 segment. No CopperStart or production filesystem
/// source is linked into this entry. Packet coverage is tracked separately.</summary>
public static class NativeCpxHandler
{
    public static uint Entry()
    {
        APTR dos=Exec.OpenLibraryRaw(CString.FromLiteral("dos.library"),36);
        if(dos.IsNull) return 20; DOS.DOSLibraryBase=dos;
        APTR task=Exec.FindTask(CString.FromPointer(0)),port=APTR.FromPointer(task.Raw+92);
        Exec.WaitPort(port); APTR message=APTR.FromPointer(Exec.GetMsg(port));
        if(message.IsNull) { Exec.CloseLibrary(dos); return 20; }
        APTR packet=APTR.FromPointer(APTR.ReadUInt32(message,10));
        if(packet.IsNull) { Exec.CloseLibrary(dos); return 20; }
        uint bytes=CpxArena.Bytes(4096);
        uint arena=Exec.AllocMem(bytes,Exec.MemoryFlags.Public|Exec.MemoryFlags.Clear);
        if(arena==0) { Reply(message,packet,port,0,103); Exec.CloseLibrary(dos); return 20; }
        APTR ioPort=Exec.CreateMsgPort(),request=ioPort.IsNull?APTR.Null:Exec.CreateIORequest(ioPort,56);
        bool opened=false,ran=false; uint error=103;
        if(request.IsNotNull)
        {
            APTR startup=APTR.FromPointer(APTR.ReadUInt32(packet,24)<<2),env=APTR.Null;
            if(startup.IsNotNull) env=APTR.FromPointer(APTR.ReadUInt32(startup,8)<<2);
            if(env.IsNotNull&&Geometry(env,out var start,out var sectors,out uint sectorBytes,out uint maximum,out uint mask))
            {
                APTR name=APTR.FromPointer(APTR.ReadUInt32(startup,4)<<2),device=APTR.FromPointer(arena+1536);
                uint length=APTR.ReadUInt8(name,0);
                for(uint n=0;n<length;n++) APTR.WriteUInt8(device,(int)n,APTR.ReadUInt8(name,(int)n+1));
                APTR.WriteUInt8(device,(int)length,0);
                NativeCpxTransport.Initialize(arena,request,start,sectors,maximum,mask,true,sectorBytes);
                if(NativeCpxTransport.ReserveDma(arena,APTR.ReadUInt32(env,48)))
                    opened=Exec.OpenDevice(CString.FromPointer(device.Raw),APTR.ReadUInt32(startup,0),request,APTR.ReadUInt32(startup,12))==0;
                if(opened&&NativeCpxMedia.Initialize(arena,request,ioPort,task))
                {
                    NativeCpxLifecycle.BeginStartup(arena,message,packet);
                    var dispatcher=new CpxDosDispatcher<NativeCpxMemory>(new NativeCpxMemory(),arena);
                    ran=true; error=Run(arena,port,ioPort,ref dispatcher);
                }
            }
            else error=115;
        }
        if(!ran) Reply(message,packet,port,0,error);
        NativeCpxMedia.Destroy(arena);
        if(opened) Exec.CloseDevice(request);
        NativeCpxTransport.ReleaseDma(arena);
        if(request.IsNotNull) Exec.DeleteIORequest(request);
        if(ioPort.IsNotNull) Exec.DeleteMsgPort(ioPort);
        Exec.FreeMem(arena,bytes); Exec.CloseLibrary(dos); return error==0?0u:20u;
    }
    internal static bool Geometry(APTR env,out Wide start,out Wide sectors,out uint sectorBytes,out uint maximum,out uint mask)
    {
        start=sectors=default; maximum=mask=sectorBytes=0;
        if(APTR.ReadUInt32(env,0)<16) return false;
        uint words=APTR.ReadUInt32(env,4),group=APTR.ReadUInt32(env,16);
        if(words is not (128 or 256 or 512 or 1024)||group==0||group>1024/words||(group&(group-1))!=0) return false;
        sectorBytes=words<<2;
        uint low=APTR.ReadUInt32(env,36),high=APTR.ReadUInt32(env,40),heads=APTR.ReadUInt32(env,12),track=APTR.ReadUInt32(env,20);
        if(high<low||high-low==uint.MaxValue||heads==0||track==0) return false;
        Wide cylinder=Wide.Multiply(heads,track); if(cylinder.High!=0) return false;
        Wide first=Wide.Multiply(low,cylinder.Low); sectors=Wide.Multiply(high-low+1,cylinder.Low);
        int shift=CpxArena.Shift(sectorBytes);
        if((sectors.Low&(group-1))!=0||Wide.Compare(sectors,Wide.From(4194304u>>shift))<0||
            !Wide.Left(first,shift,out start)||!Wide.Add(first,sectors,out var end)||!Wide.Left(end,shift,out _)) return false;
        maximum=APTR.ReadUInt32(env,52); mask=APTR.ReadUInt32(env,56); return maximum>=sectorBytes;
    }
    internal static uint Run(uint arena,APTR port,APTR ioPort,ref CpxDosDispatcher<NativeCpxMemory> dispatcher)
    {
        APTR message=APTR.Null,packet=APTR.Null;
        uint wheel=0,removeAt=64,removeEnd=64,woken=0;
        uint signals=(1u<<APTR.ReadUInt8(port,15))|(1u<<APTR.ReadUInt8(ioPort,15))|NativeCpxMedia.SignalMask(arena)|4096;
        while(true)
        {
            if(NativeCpxLifecycle.Active(arena))
            {
                NativeCpxMedia.Poll(arena);
                bool changed=NativeCpxMedia.TakeChange(arena);
                uint control=woken|Exec.SetSignal(0,4096);
                woken=0;
                if(changed||(control&4096)!=0)
                    NativeCpxLifecycle.Stop(arena,changed?226u:304u,ref dispatcher);
                StepResult step=NativeCpxLifecycle.Visit(arena,port,ref dispatcher);
                if(step==StepResult.Complete) return NativeCpxLifecycle.Error(arena);
                if(step==StepResult.NeedIo&&!HasMessages(ioPort)) woken=Exec.Wait(signals);
                continue;
            }
            // One asynchronous-resource phase plus at most three filesystem
            // phases per dispatch visit. Timers keep progressing during disk I/O.
            bool auxiliary=dispatcher.HasAsyncResources||dispatcher.HasRecordWork||NativeCpxRecordTimers.Active(arena)||NativeCpxMedia.Active(arena),activity=false;
            // Wait consumes its signal bits. Retain the returned control signal
            // until the next bounded visit instead of relying on SetSignal.
            if(NativeCpxMedia.TakeChange(arena)) { dispatcher.MediaRemoved(); NativeCpxTransport.RequestStop(arena); activity=true; auxiliary=true; }
            else if(((woken|Exec.SetSignal(0,4096))&4096)!=0)
            { dispatcher.RequestStorageCancellation(); if(dispatcher.StorageStopPending) NativeCpxTransport.RequestStop(arena); activity=true; auxiliary=true; }
            else if(auxiliary) activity=wheel==5?NativeCpxMedia.Poll(arena):ServiceAsync(arena,port,wheel,ref dispatcher);
            woken=0;
            wheel=wheel==5?0:wheel+1;
            if(removeAt<removeEnd)
            {
                RemoveQueuedNotification(removeAt++,ref dispatcher);
                if(removeAt==removeEnd) Reply(message,packet,port,dispatcher.Result,dispatcher.DosError);
                continue;
            }
            if(!dispatcher.Busy)
            {
                if(!dispatcher.TryBeginNotificationRefresh())
                {
                message=APTR.FromPointer(Exec.GetMsg(port));
                if(message.IsNull)
                {
                    if(activity||dispatcher.HasRecordWork||dispatcher.HasNotificationDelivery||NativeCpxRecordTimers.HasReplies(arena)||HasMessages(ioPort)) continue;
                    woken=Exec.Wait(signals|NativeCpxRecordTimers.SignalMask(arena)); continue;
                }
                if(APTR.ReadUInt8(message,8)==7&&dispatcher.NotificationReply(message.Raw)) continue;
                packet=APTR.FromPointer(APTR.ReadUInt32(message,10));
                if(packet.IsNull) continue;
                if(APTR.ReadUInt32(packet,8)==5)
                {
                    // DIE is deliberately refused while handles or locks are live.
                    if(dispatcher.HasOpenResources||dispatcher.HasAsyncResources||NativeCpxRecordTimers.Active(arena)) { Reply(message,packet,port,0,202); continue; }
                    NativeCpxLifecycle.BeginShutdown(arena,message,packet,dispatcher.MediaAvailable,dispatcher.IsWriteProtected);
                    continue;
                }
                if(APTR.ReadUInt32(packet,8)==1020||APTR.ReadUInt32(packet,8)==31&&APTR.ReadUInt32(packet,20)==0) NativeCpxTransport.ResetMedia(arena);
                if(dispatcher.BeginPacket(packet.Raw)!=CpxError.None) { Reply(message,packet,port,0,115); continue; }
                }
            }
            // A dispatch visit performs at most four phases/requests, including
            // pure CPU phases. Conflicting packets remain in the Exec port queue.
            for(uint budget=0;budget<(auxiliary?3u:4u)&&dispatcher.Busy;budget++)
            {
                StepResult result=dispatcher.Step();
                if(result==StepResult.NeedIo)
                {
                    CpxIo io=dispatcher.Request;
                    StepResult transfer=NativeCpxTransport.Service(arena,io,out uint actual,out bool unsupported);
                    if(transfer is StepResult.Complete or StepResult.Failed)
                    {
                        APTR deviceRequest=APTR.FromPointer(APTR.ReadUInt32(APTR.FromPointer(arena+2048),16));
                        if(transfer==StepResult.Failed&&APTR.ReadUInt8(deviceRequest,31)==29) { dispatcher.MediaRemoved(); NativeCpxTransport.RequestStop(arena); }
                        if(unsupported) new CpxEngine<NativeCpxMemory>(new NativeCpxMemory(),arena).CompleteUnsupportedDiscard(io.Token);
                        else dispatcher.CompleteIo(io.Token,actual,transfer==StepResult.Complete);
                    }
                    else if(NativeCpxTransport.Waiting(arena)) break;
                }
                else if(result==StepResult.Complete)
                {
                    if(dispatcher.DosError==0&&dispatcher.MediaAvailable) NativeCpxVolume.Publish(arena,dispatcher.VolumeLabelAddress);
                    if(dispatcher.DosError is 213 or 226) dispatcher.CancelRecordWaits(dispatcher.DosError);
                    if(!dispatcher.InternalNotification&&!dispatcher.PacketDeferred)
                    {
                        if(APTR.ReadUInt32(packet,8)==4098&&dispatcher.RemovedNotificationStart<64)
                        { removeAt=dispatcher.RemovedNotificationStart; removeEnd=removeAt+8; }
                        else Reply(message,packet,port,dispatcher.Result,dispatcher.DosError);
                    }
                    break;
                }
            }
            if(dispatcher.Busy&&NativeCpxTransport.Waiting(arena)&&!activity&&!dispatcher.HasRecordWork&&
                !dispatcher.HasNotificationDelivery&&!NativeCpxRecordTimers.HasReplies(arena)&&!HasMessages(ioPort)) woken=Exec.Wait(signals|NativeCpxRecordTimers.SignalMask(arena));
        }
    }
    private static bool HasMessages(APTR port)=>APTR.ReadUInt32(port,20)!=port.Raw+24;
    private static bool ServiceAsync(uint arena,APTR port,uint phase,ref CpxDosDispatcher<NativeCpxMemory> dispatcher)
    {
        if(phase==0) return NativeCpxRecordTimers.ReapOne(arena,ref dispatcher);
        if(phase==1)
        {
            // Observe a completed timeout before considering this waiter's grant.
            bool reaped=NativeCpxRecordTimers.ReapForSlot(arena,dispatcher.NextRecordWaitSlot,ref dispatcher);
            return dispatcher.StepRecordWaits()||reaped;
        }
        if(phase==2) return NativeCpxRecordTimers.Service(arena,ref dispatcher);
        if(phase==3)
        {
            if(!dispatcher.TryTakeRecordReply(out var reply)) return false;
            APTR.WriteUInt32(APTR.FromPointer(reply.Packet),4,port.Raw);
            Exec.PutMsg(APTR.FromPointer(reply.ReplyPort),APTR.FromPointer(reply.Message)); return true;
        }
        if(!dispatcher.TryTakeNotification(out var delivery)) return false;
        if(delivery.Message==0) Exec.Signal(APTR.FromPointer(delivery.Target),delivery.SignalMask);
        else Exec.PutMsg(APTR.FromPointer(delivery.Target),APTR.FromPointer(delivery.Message));
        return true;
    }
    private static void RemoveQueuedNotification(uint index,ref CpxDosDispatcher<NativeCpxMemory> dispatcher)
    {
        if(!dispatcher.TryGetRemovedNotification(index,out uint address)) return;
        APTR message=APTR.FromPointer(address); bool queued=false;
        Exec.Forbid();
        uint next=APTR.ReadUInt32(message,0),previous=APTR.ReadUInt32(message,4);
        // NotifyMessage is read-only to its recipient except for ReplyMsg.
        // Reciprocal links distinguish still-enqueued messages from GetMsg's
        // stale links; a returned reply has a different node type. No scan of
        // the application's potentially unbounded port queue is necessary.
        if(APTR.ReadUInt8(message,8)==5&&next!=0&&previous!=0&&
            APTR.ReadUInt32(APTR.FromPointer(previous),0)==address&&APTR.ReadUInt32(APTR.FromPointer(next),4)==address)
        { Exec.Remove(message); queued=true; }
        Exec.Permit(); if(queued) dispatcher.NotificationReply(address);
    }
    internal static void Reply(APTR message,APTR packet,APTR port,uint value,uint error)
    {
        APTR reply=APTR.FromPointer(APTR.ReadUInt32(packet,4)); APTR.WriteUInt32(packet,12,value); APTR.WriteUInt32(packet,16,error);
        APTR.WriteUInt32(packet,4,port.Raw); Exec.PutMsg(reply,message);
    }
}
#endif
