#if COPPERFS_CPX4
using Amiga;
namespace CopperCPX.Amiga;

// Single owner from the first probe until publication, and from DIE admission
// until teardown. Offsets 3952..4003 are inside the existing mount arena.
internal static class NativeCpxLifecycle
{
    private enum Phase : uint
    {
        Idle, ProbeWide, ProbeNarrow, Mount, Timers, Publish, Register,
        Flush, Detach, FailedStartupDetach, CloseTimers, ReleaseVolume, Drain, Exit, Retain, MaintenanceTimer
    }
    private const int Message=4, Packet=8, Failure=12, Volume=16, Node=20, Registered=24;
    private const int ProbePage=28,BackupCopy=32,FoundUnit=36,FoundHigh=40,FoundLow=44,ReadFault=48;
    private static APTR State(uint arena)=>APTR.FromPointer(arena+3952);
    private static Phase Current(APTR state)=>(Phase)APTR.ReadUInt32(state,0);
    private static StepResult Next(APTR state,Phase phase)
    { APTR.WriteUInt32(state,0,(uint)phase); return StepResult.Pending; }
    internal static bool Active(uint arena)=>Current(State(arena))!=Phase.Idle;
    internal static uint Error(uint arena)=>APTR.ReadUInt32(State(arena),Failure);
    private static void Own(APTR state,APTR message,APTR packet,Phase phase)
    {
        APTR.WriteUInt32(state,Message,message.Raw); APTR.WriteUInt32(state,Packet,packet.Raw);
        APTR.WriteUInt32(state,Failure,0); Next(state,phase);
    }
    private static void Reply(APTR state,APTR port,uint error)
    {
        NativeCpxHandler.Reply(APTR.FromPointer(APTR.ReadUInt32(state,Message)),
            APTR.FromPointer(APTR.ReadUInt32(state,Packet)),port,error==0?uint.MaxValue:0,error);
        APTR.WriteUInt32(state,Message,0); APTR.WriteUInt32(state,Packet,0);
    }
    internal static void BeginStartup(uint arena,APTR message,APTR packet)
    {
        APTR state=State(arena); Own(state,message,packet,Phase.ProbeWide);
        uint sector=1u<<NativeCpxTransport.SectorShift(arena);
        APTR.WriteUInt32(state,ProbePage,sector);
        for(int n=BackupCopy;n<=ReadFault;n+=4) APTR.WriteUInt32(state,n,0);
        // Discovery owns the unused sixth maximum-size scratch page. Reuse a
        // prepared checksum table across anchors before final geometry exists.
        if(new CpxEngine<NativeCpxMemory>(new NativeCpxMemory(),arena).PrepareAnchorChecksum(arena+CpxArena.Scratch+5*4096)!=CpxError.None)
            APTR.WriteUInt32(state,Failure,103);
    }
    internal static void BeginShutdown(uint arena,APTR message,APTR packet,bool available,bool protectedVolume)
    {
        APTR state=State(arena); Own(state,message,packet,available?Phase.Flush:Phase.Detach);
        if(!available) return;
        var engine=new CpxEngine<NativeCpxMemory>(new NativeCpxMemory(),arena);
        if((protectedVolume?engine.BeginPersistenceBarrier():engine.BeginOperation(CpxOperation.Flush))!=CpxError.None)
            APTR.WriteUInt32(state,Failure,213);
    }
    internal static void Stop(uint arena,uint error,ref CpxDosDispatcher<NativeCpxMemory> dispatcher)
    {
        APTR state=State(arena); Phase phase=Current(state);
        // Once the volume has replied to startup, deliver the event to the
        // mounted dispatcher. After removal is submitted it must be reaped.
        if(phase==Phase.Register) { if(error==226) dispatcher.MediaRemoved(); return; }
        if(phase is Phase.Detach or Phase.Flush && error==226) dispatcher.MediaRemoved();
        if(phase is not (Phase.ProbeWide or Phase.ProbeNarrow or Phase.Mount or Phase.Timers or Phase.MaintenanceTimer or Phase.Publish or Phase.Flush)) return;
        if(Error(arena)==0||error==226) APTR.WriteUInt32(state,Failure,error);
        if(phase is Phase.Mount or Phase.Flush)
            new CpxEngine<NativeCpxMemory>(new NativeCpxMemory(),arena).Cancel();
        NativeCpxTransport.RequestStop(arena);
    }
    // Each visit performs at most one engine phase and one transport action.
    // NeedIo means a device owns a request that can wake the outer loop.
    internal static StepResult Visit(uint arena,APTR port,ref CpxDosDispatcher<NativeCpxMemory> dispatcher)
    {
        APTR state=State(arena); Phase phase=Current(state); uint failure=APTR.ReadUInt32(state,Failure);
        var engine=new CpxEngine<NativeCpxMemory>(new NativeCpxMemory(),arena);
        switch(phase)
        {
            case Phase.ProbeWide: case Phase.ProbeNarrow:
                return Probe(arena,state,port,phase,ref engine);
            case Phase.Flush: case Phase.Mount:
                StepResult step=EngineStep(arena,ref engine);
                if(step is StepResult.NeedIo or StepResult.Pending) return step;
                if(step==StepResult.Failed&&failure==0)
                {
                    if(phase==Phase.Mount&&engine.Error==CpxError.Corrupt) return Next(state,Phase.Timers);
                    failure=NativeCpxTransport.DiskChanged(arena)?226u:213u;
                    if(failure==226&&phase==Phase.Flush) dispatcher.MediaRemoved();
                }
                if(failure!=0) return Refuse(state,port,failure,phase==Phase.Mount);
                return Next(state,phase==Phase.Flush?Phase.Detach:Phase.Timers);
            case Phase.Timers:
                if(failure!=0) return Refuse(state,port,failure,true);
                StepResult opened=NativeCpxRecordTimers.OpenOne(arena);
                if(opened==StepResult.Failed) return Refuse(state,port,103,true);
                return opened==StepResult.Complete?Next(state,Phase.MaintenanceTimer):StepResult.Pending;
            case Phase.MaintenanceTimer:
                if(failure!=0) return Refuse(state,port,failure,true);
                return NativeCpxMaintenance.Open(arena)?Next(state,Phase.Publish):Refuse(state,port,103,true);
            case Phase.Publish:
                if(failure!=0) return Refuse(state,port,failure,true);
                PrepareBufferedStorage(ref engine);
                APTR volume=DOS.MakeDosEntry(CString.FromLiteral("CopperCPX4"),(int)DosListType.Volume);
                if(volume.IsNull) return Refuse(state,port,103,true);
                NativeCpxVolume.Initialize(arena,volume,engine.VolumeLabelAddress);
                APTR.WriteUInt32(volume,8,port.Raw); APTR.WriteUInt32(volume,32,CpxArena.Magic);
                APTR packet=APTR.FromPointer(APTR.ReadUInt32(state,Packet));
                APTR node=APTR.FromPointer(APTR.ReadUInt32(packet,28)<<2);
                if(node.IsNotNull) APTR.WriteUInt32(node,8,port.Raw);
                APTR.WriteUInt32(state,Node,node.Raw); APTR.WriteUInt32(state,Volume,volume.Raw);
                dispatcher=new CpxDosDispatcher<NativeCpxMemory>(new NativeCpxMemory(),arena);
                dispatcher.InitializeOnClearedPools(port.Raw,volume.Raw);
                // DOS may hold the list semaphore until startup has replied.
                Reply(state,port,0); return Next(state,Phase.Register);
            case Phase.Register:
                if(DOS.AddDosEntry(APTR.FromPointer(APTR.ReadUInt32(state,Volume)))!=0) APTR.WriteUInt32(state,Registered,1);
                return Next(state,Phase.Idle);
            case Phase.Detach: case Phase.FailedStartupDetach:
                StepResult detached=NativeCpxMedia.Detach(arena);
                if(detached==StepResult.Pending) return StepResult.NeedIo;
                if(detached==StepResult.Failed)
                    return phase==Phase.Detach?Refuse(state,port,213,false):Next(state,Phase.Retain);
                if(phase==Phase.Detach)
                {
                    APTR device=APTR.FromPointer(APTR.ReadUInt32(state,Node));
                    if(device.IsNotNull) APTR.WriteUInt32(device,8,0);
                    Reply(state,port,0);
                }
                return Next(state,Phase.CloseTimers);
            case Phase.CloseTimers:
                StepResult timer=NativeCpxMaintenance.CloseOne(arena);
                if(timer!=StepResult.Complete) return timer;
                if(NativeCpxRecordTimers.CloseOne(arena)==StepResult.Pending) return StepResult.Pending;
                return Next(state,Phase.ReleaseVolume);
            case Phase.ReleaseVolume:
                APTR entry=APTR.FromPointer(APTR.ReadUInt32(state,Volume));
                if(entry.IsNotNull)
                {
                    if(APTR.ReadUInt32(state,Registered)!=0) DOS.RemDosEntry(entry);
                    NativeCpxVolume.Restore(arena,entry); DOS.FreeDosEntry(entry);
                }
                return Next(state,Phase.Drain);
            case Phase.Drain:
                APTR queued=APTR.FromPointer(Exec.GetMsg(port));
                if(queued.IsNull) return Next(state,Phase.Exit);
                APTR queuedPacket=APTR.FromPointer(APTR.ReadUInt32(queued,10));
                if(queuedPacket.IsNotNull)
                {
                    uint action=APTR.ReadUInt32(queuedPacket,8);
                    NativeCpxHandler.Reply(queued,queuedPacket,port,action is 82 or 87 or 1008 or 1022?uint.MaxValue:0,226);
                }
                return StepResult.Pending;
            case Phase.Retain: return StepResult.NeedIo;
            default: return StepResult.Complete;
        }
    }
    private static StepResult Refuse(APTR state,APTR port,uint error,bool startup)
    {
        Reply(state,port,error); APTR.WriteUInt32(state,Failure,startup?error:0);
        return Next(state,startup?Phase.FailedStartupDetach:Phase.Idle);
    }
    private static StepResult EngineStep(uint arena,ref CpxEngine<NativeCpxMemory> engine)
    {
        StepResult step=engine.Step();
        if(step!=StepResult.NeedIo) return step;
        CpxIo io=engine.Request;
        StepResult result=NativeCpxTransport.Service(arena,io,out uint actual,out bool unsupported);
        if(result==StepResult.Pending) return NativeCpxTransport.Waiting(arena)?StepResult.NeedIo:StepResult.Pending;
        if(unsupported) engine.CompleteUnsupportedDiscard(io.Token);
        else engine.CompleteIo(io.Token,actual,result==StepResult.Complete);
        return StepResult.Pending;
    }
    private static StepResult Probe(uint arena,APTR state,APTR port,Phase phase,ref CpxEngine<NativeCpxMemory> engine)
    {
        uint page=APTR.ReadUInt32(state,ProbePage),backup=APTR.ReadUInt32(state,BackupCopy);
        int shift=NativeCpxTransport.SectorShift(arena); uint sector=1u<<shift,pageSectors=page>>shift;
        Wide capacity=NativeCpxTransport.Capacity(arena);
        Wide location=backup==0?Wide.From(65536u>>shift):new Wide(capacity.High,capacity.Low&~(pageSectors-1))-pageSectors;
        // A fresh token also resets split-transfer progress after an unreadable copy.
        var io=new CpxIo { Kind=IoKind.Read,Sector=location,Buffer=arena+CpxArena.Scratch,Bytes=page,Token=page+backup };
        StepResult result=NativeCpxTransport.Service(arena,io,out _,out _);
        if(result==StepResult.Pending) return NativeCpxTransport.Waiting(arena)?StepResult.NeedIo:StepResult.Pending;
        uint failure=APTR.ReadUInt32(state,Failure);
        if(failure!=0) return Refuse(state,port,failure,true);
        if(result==StepResult.Failed)
        {
            if(phase==Phase.ProbeWide&&backup==0&&page==(sector)&&NativeCpxTransport.TryNarrowProbe(arena))
                return Next(state,Phase.ProbeNarrow);
            if(!NativeCpxTransport.UnreadableSector(arena)) return Refuse(state,port,NativeCpxTransport.DiskChanged(arena)?226u:213u,true);
            APTR.WriteUInt32(state,ReadFault,1);
        }
        else
        {
            CpxError check=engine.InspectAnchor(io.Buffer,page,sector,capacity,location,out uint unit,out Wide identity,out uint version,arena+CpxArena.Scratch+5*4096);
            if(check!=CpxError.None&&check!=CpxError.NotFound) return Refuse(state,port,213,true);
            if(check==CpxError.None)
            {
                if(version==5) { unit|=0x80000000; uint features=APTR.ReadUInt32(APTR.FromPointer(io.Buffer),100); if(features>=2) unit|=0x40000000; if(features>=3) unit|=0x20000000; if(features>=4) unit|=0x10000000; if(features==5) unit|=0x08000000; }
                uint previous=APTR.ReadUInt32(state,FoundUnit);
                if(previous!=0&&(previous!=unit||APTR.ReadUInt32(state,FoundHigh)!=identity.High||
                    APTR.ReadUInt32(state,FoundLow)!=identity.Low)) return Refuse(state,port,213,true);
                APTR.WriteUInt32(state,FoundUnit,unit); APTR.WriteUInt32(state,FoundHigh,identity.High); APTR.WriteUInt32(state,FoundLow,identity.Low);
            }
        }
        if(backup==0) { APTR.WriteUInt32(state,BackupCopy,1); return StepResult.Pending; }
        if(page<4096) { APTR.WriteUInt32(state,ProbePage,page*2); APTR.WriteUInt32(state,BackupCopy,0); return StepResult.Pending; }
        uint chosen=APTR.ReadUInt32(state,FoundUnit);
        bool shadow=(chosen&0x80000000)!=0,compactControl=(chosen&0x40000000)!=0,directInodes=(chosen&0x20000000)!=0,smallMetadata=(chosen&0x10000000)!=0,inlineCatalogue=(chosen&0x08000000)!=0; chosen&=0x07ffffff;
        if(chosen==0&&APTR.ReadUInt32(state,ReadFault)!=0) return Refuse(state,port,213,true);
        if(engine.Initialize(sector,chosen==0?sector:chosen,capacity,1,metadataPageBytes:smallMetadata?chosen:0)!=CpxError.None) return Refuse(state,port,213,true);
        if(engine.DisableShadowVolume()!=CpxError.None) return Refuse(state,port,213,true);
        PrepareBufferedStorage(ref engine,shadow);
        if(shadow)
        {
            uint extra=CpxEngine<NativeCpxMemory>.ShadowStorageBytes(engine.MetadataPageBytes);
            if(engine.BufferedStorageAddress==0||engine.BufferedStorageCapacity<extra) return Refuse(state,port,103,true);
            uint storage=engine.BufferedStorageAddress+engine.BufferedStorageCapacity-extra;
            if(engine.EnableShadowVolume(storage,extra,inlineCatalogue?5u:smallMetadata?4u:directInodes?3u:compactControl?2u:1u)!=CpxError.None) return Refuse(state,port,103,true);
        }
        if(chosen==0) return Next(state,Phase.Timers); // readable unformatted medium, explicit DOS Format remains available
        if(engine.BeginMountFromDiscoveredAnchor(new(APTR.ReadUInt32(state,FoundHigh),APTR.ReadUInt32(state,FoundLow)))!=CpxError.None)
            return Refuse(state,port,213,true);
        return Next(state,Phase.Mount);
    }
    private static void PrepareBufferedStorage(ref CpxEngine<NativeCpxMemory> engine,bool shadow=false)
    {
        if(engine.BufferedStorageAddress!=0) return;
        uint extra=shadow?CpxEngine<NativeCpxMemory>.ShadowStorageBytes(engine.MetadataPageBytes):0;
        uint bytes=CpxEngine<NativeCpxMemory>.BufferedStorageBytes(engine.MetadataPageBytes)+extra;
        uint storage=Exec.AllocMem(bytes,Exec.MemoryFlags.Public|Exec.MemoryFlags.Clear);
        if(storage==0)
        {
            bytes=CpxEngine<NativeCpxMemory>.CompactBufferedStorageBytes(engine.MetadataPageBytes)+extra;
            storage=Exec.AllocMem(bytes,Exec.MemoryFlags.Public|Exec.MemoryFlags.Clear);
        }
        if(storage!=0&&engine.EnableBufferedWrites(storage,bytes)!=CpxError.None) Exec.FreeMem(storage,bytes);
        engine.EnableAlternatingCheckpoints();
        engine.EnableCpuBatching();
    }
}
#endif
