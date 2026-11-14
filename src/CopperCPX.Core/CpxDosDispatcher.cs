namespace CopperCPX;

/// <summary>Single-owner DOS packet continuation. The OS message remains queued
/// or owned by the caller until Complete/Failed; this class never allocates.</summary>
public partial struct CpxDosDispatcher<TMemory> where TMemory : struct, ICpxMemory
{
    private TMemory memory;
    private CpxEngine<TMemory> engine;
    private FixedPools<TMemory> pools;
    private uint arena;
    private const uint Packet=0, Phase=4, BusyAt=8, Port=12, Volume=16, Handle=20,
        Object=24, PathLength=32, PathAt=36, ResultAt=40, ErrorAt=44, Action=48, PathEnd=52,
        Destination=56, ParentId=64, OldParent=72, OldLength=80, Reserved=84, ReservedKind=88, ResizeResult=92;
    private uint State => arena+512;
    private uint Path => arena+1024;
    private uint OldName => arena+1280;
    private uint Pool => arena+CpxArena.Scratch+6*engine.MetadataPageBytes+CpxArena.IndexBytes;
    public CpxDosDispatcher(TMemory memory,uint arena)
    { this.memory=memory; this.arena=arena; engine=new(memory,arena); pools=new(memory,arena+CpxArena.Scratch+6*engine.MetadataPageBytes+CpxArena.IndexBytes); }
    private uint R(uint offset)=>memory.Read32(State+offset);
    private void W(uint offset,uint value)=>memory.Write32(State+offset,value);
    private Wide G(uint p)=>new(memory.Read32(p),memory.Read32(p+4));
    private void Put(uint p,Wide v) { memory.Write32(p,v.High); memory.Write32(p+4,v.Low); }
    private uint Arg(uint index)=>memory.Read32(R(Packet)+20+index*4);
    public bool Busy=>R(BusyAt)!=0;
    public bool HasOpenResources
    {
        get { for(uint n=0;n<32;n++) if(memory.Read32(Pool+n*64)!=0||memory.Read32(Pool+2048+n*48)!=0) return true; return false; }
    }
    public uint Result=>R(ResultAt);
    public uint DosError=>R(ErrorAt);
    public CpxIo Request=>engine.Request;
    public CpxError CompleteIo(uint token,uint bytes,bool success)=>engine.CompleteIo(token,bytes,success);
    public void Initialize(uint port,uint volume)
    { InitializeOnClearedPools(port,volume); pools.Initialize(); }
    // Native startup owns fresh MEMF_CLEAR pool storage. Mount/recovery uses
    // scratch and never takes a DOS slot before publication.
    public void InitializeOnClearedPools(uint port,uint volume)
    {
        uint serial=R(180)==CpxArena.Magic?R(EnumSerial):0;
        for(uint n=0;n<512;n+=4) W(n,0);
        W(EnumSerial,serial); W(180,CpxArena.Magic); W(Port,port); W(Volume,volume);
    }
    public CpxError BeginPacket(uint packet)
    {
        if(Busy) return CpxError.Busy;
        if(packet==0||(packet&1)!=0||packet>uint.MaxValue-48) return CpxError.InvalidArgument;
        W(Packet,packet); W(Phase,0); W(BusyAt,1); W(Action,memory.Read32(packet+8)); W(ResultAt,0); W(ErrorAt,0); W(Handle,0); W(Destination,0); W(Reserved,0);
        W(EnumSlot,0); W(EnumNew,0); Put(State+ParentId,default); Put(State+EntryId,default);
        W(Deferred,0); W(NotifyInternal,0); W(NotifySlot,0); W(NotifyRemoved,64);
        W(AllActive,0);
        return CpxError.None;
    }
    private StepResult Finish(uint value,uint error=0)
    {
        if(R(Action)==TrimPacket&&R(Phase)==160) PublishTrimResult();
        if(R(Action)==1033) FinishExAll(error);
        if(R(Action)==4097) FinishNotification(ref value,ref error);
        else NotificationMutation(value,error);
        if((R(Action) is 1024 or 26406)&&error!=0&&value==0) value=uint.MaxValue;
        if(error!=0&&R(EnumNew)!=0) { uint e=R(EnumSlot); pools.Release(ResourceKind.Enumeration,e,memory.Read32(e-4)); W(EnumNew,0); }
        if(R(Reserved)!=0) { pools.Release((ResourceKind)R(ReservedKind),R(Reserved),memory.Read32(R(Reserved)-4)); W(Reserved,0); }
        W(ResultAt,value); W(ErrorAt,error); W(BusyAt,0); memory.Write32(R(Packet)+12,value); memory.Write32(R(Packet)+16,error); return StepResult.Complete;
    }
    public static uint ToDosError(CpxError error)=>error switch
    { CpxError.None=>0,CpxError.NotFound=>205,CpxError.WrongType=>212,CpxError.Protected=>223,CpxError.ReadOnly=>214,
      CpxError.ResourceExhausted=>103,CpxError.DiskFull or CpxError.ReserveExhausted=>221,CpxError.InvalidArgument=>115,CpxError.Unsupported=>209,
      CpxError.Busy=>202,CpxError.NotMounted=>218,CpxError.AlreadyExists=>203,CpxError.DirectoryNotEmpty=>216,CpxError.TreeTooDeep=>221,_=>213 };
    private StepResult Start(CpxError error,uint next)
    { if(error!=CpxError.None) return Finish(R(Action) is 82 or 87 or 1008 or 1022?uint.MaxValue:0,ToDosError(error)); W(Phase,next); return StepResult.Pending; }
    private bool ValidResource(uint p,ResourceKind kind)
    {
        uint start=Pool+(kind==ResourceKind.Handle?0u:2048u),width=kind==ResourceKind.Handle?64u:48u;
        for(uint n=0;n<32;n++) if(p==start+n*width+8) return memory.Read32(p-8)==1;
        return false;
    }
    private bool LockId(uint bptr,out Wide id)
    {
        id=Wide.From(1); if(bptr==0) return true;
        if(bptr>0x3fffffff) return false; uint p=bptr<<2;
        if(!ValidResource(p,ResourceKind.Lock)) return false; id=G(p+20); return true;
    }
    private bool Conflicts(Wide id,bool exclusive)
    {
        for(uint n=0;n<32;n++)
        {
            uint h=Pool+n*64,l=Pool+2048+n*48;
            if(memory.Read32(h)==1&&Wide.Equal(G(h+8),id)&&(exclusive||memory.Read32(h+24)==uint.MaxValue)) return true;
            if(memory.Read32(l)==1&&Wide.Equal(G(l+28),id)&&(exclusive||memory.Read32(l+16)==uint.MaxValue)) return true;
        }
        return false;
    }
    private StepResult MakeLock(Wide id,uint access)
    {
        if(access is not (0xfffffffe or 0xffffffff)) return Finish(0,115);
        Put(State+Object,id); W(LockAccess,access); W(Phase,78); return StepResult.Pending;
    }
    private StepResult StartPath(uint lockBptr,uint nameBptr)
    {
        if(!LockId(lockBptr,out var id)) return Finish(0,211);
        if(lockBptr!=0) { uint context=LockContext(lockBptr<<2); Context(context,context+8); }
        if(nameBptr==0||nameBptr>0x3fffffff) return Finish(0,115);
        uint p=nameBptr<<2,length=memory.Read8(p),start=0;
        for(uint n=0;n<length;n++) { byte c=memory.Read8(p+1+n); if(c==0) return Finish(0,210); memory.Write8(Path+n,c); if(c==58) { id=Wide.From(1); start=n+1; } }
        Put(State+Object,id); W(PathLength,length); W(PathAt,start); W(Phase,10); return StepResult.Pending;
    }
    public StepResult Step()
    {
        if(!Busy) return StepResult.Complete;
        if(StorageStopPending) return StopStorageStep();
        uint phase=R(Phase),action=R(Action);
        if(phase==0) return Dispatch();
        if(phase==160) return ManualTrimPacketStep();
        if(phase>=140) return NewReadLinkStep();
        if(phase>=130) return VolumeStep();
        if(phase>=120) return MediaStep();
        if(phase>=110) return RecordStep();
        if(phase>=100) return NotificationStep();
        if(phase>=90) return LinkStep();
        if(phase>=70) return LifecycleStep();
        if(phase>=40) return InformationStep();
        if(phase==10)
        {
            if(action==26406) return NewLinkComponent();
            uint at=R(PathAt),end=at; if(at==R(PathLength)) return Start(engine.BeginOperation(CpxOperation.ReadObject,G(State+Object)),12);
            while(end<R(PathLength)&&memory.Read8(Path+end)!=47) end++;
            if(end==at) return Start(engine.BeginOperation(CpxOperation.ReadObject,G(State+Object)),14);
            Put(State+ParentId,G(State+Object)); W(PathEnd,end); return Start(engine.BeginLookup(G(State+Object),Path+at,end-at),11);
        }
        StepResult result=engine.Step();
        if(result is StepResult.Pending or StepResult.NeedIo) return result;
        if(result==StepResult.Failed)
        {
            if(phase==20) return FinishTransfer(action==82&&engine.Error==CpxError.Protected?224u:ToDosError(engine.Error));
            return Finish(action is 1008 or 1022?uint.MaxValue:0,ToDosError(engine.Error));
        }
        switch(phase)
        {
            case 11:
                if(R(PathEnd)==R(PathLength)&&(action is 16 or 17 or 22 or 1004 or 1006 or 1021)) { W(Phase,83); return StepResult.Pending; }
                if(!engine.LookupFound) return Finish(0,205);
                Put(State+EntryId,G(engine.TreeRecord+8));
                W(LinkPrefix,R(PathAt)); W(LinkSuffix,R(PathEnd));
                Put(State+Object,engine.LookupId); W(PathAt,R(PathEnd)<R(PathLength)?R(PathEnd)+1:R(PathEnd));
                return Start(engine.BeginOperation(CpxOperation.ReadObject,engine.LookupId),13);
            case 12:
                if(!engine.ObjectFound) return Finish(0,205);
                return Resolved();
            case 13: return PathObject();
            case 14:
                if(!engine.ObjectFound) return Finish(0,205);
                Wide ancestor=memory.Read8(engine.ObjectRecord+84)==2?G(engine.ObjectRecord+40):G(State+ParentId);
                if(action==26406&&Wide.Equal(G(State+Object),Wide.From(1))) ancestor=Wide.From(1);
                else if(ancestor.IsZero||Wide.Equal(G(State+Object),Wide.From(1))) return Finish(0,205);
                Put(State+Object,ancestor); Put(State+ParentId,default); Put(State+EntryId,default);
                W(PathAt,R(PathAt)+1); W(Phase,10); return StepResult.Pending;
            case 20: return FinishTransfer(0);
            case 21:
                if(action==27&&!IsWriteProtected) return Start(engine.BeginOperation(CpxOperation.TrimFree),26);
                return Finish(uint.MaxValue);
            case 22: return Finish(Arg(1));
            case 23: return SeekFinish();
            case 24:
                if(!engine.ObjectFound) return Finish(0,205);
                Wide parent=memory.Read8(engine.ObjectRecord+84)==2?G(engine.ObjectRecord+40):G(State+ParentId);
                if(parent.IsZero) return Finish(0,213);
                return Start(engine.BeginOperation(CpxOperation.ReadObject,parent),70);
            case 25:
                memory.Write32(R(Handle)+40,1);
                Wide resized=G(State+ResizeResult); if(Wide.Compare(G(R(Handle)+8),resized)>0) Put(R(Handle)+8,resized);
                if(action==26401) { Put(Arg(3),resized); return Finish(uint.MaxValue); }
                return Finish(resized.Low);
            case 26: return Finish(uint.MaxValue);
            case 30:
                Put(State+Object,engine.MutatedObject);
                Put(State+EntryId,engine.CreatedEntryId);
                if(action==22) { MarkNotificationChange(engine.MutatedObject,G(State+ParentId),default); return MakeLock(engine.MutatedObject,0xfffffffe); }
                return OpenHandle();
            case 31: return Finish(uint.MaxValue);
            case 32: return OpenHandle();
            default: return Finish(0,209);
        }
    }
    private StepResult FinishTransfer(uint error)
    {
        uint count=engine.TransferredBytes,p=R(Handle);
        if(R(Action)==87&&count!=0) memory.Write32(p+40,1);
        if(count!=0) { Wide.Add(G(p+8),Wide.From(count),out var next); Put(p+8,next); }
        return Finish(error!=0&&count==0?uint.MaxValue:count,error);
    }
    private StepResult Resolved()
    {
        uint action=R(Action),record=engine.ObjectRecord; Wide id=G(State+Object);
        if(memory.Read8(record+84)==3&&(action is 8 or 1004 or 1005 or 1006)) return Finish(0,233);
        if(action==1024) return PathObject();
        switch(action)
        {
            case 8: return MakeLock(id,Arg(2));
            case 4097: return Finish(uint.MaxValue);
            case 1004: case 1005: case 1006:
                if(memory.Read8(record+84)!=1) return Finish(0,212);
                if((memory.Read32(record+72)&(action==1005?8u:action==1006?5u:12u))!=0) return Finish(0,action==1005?224u:223u);
                if(Arg(0)==0||Arg(0)>0x3fffffff) return Finish(0,115);
                W(Phase,81); return StepResult.Pending;
            case 21: return Start(engine.BeginProtection(id,Arg(3)),21);
            case 1036: return Start(engine.BeginOwner(id,Arg(3)),21);
            case 28:
                uint comment=Arg(3); if(comment==0||comment>0x3fffffff) return Finish(0,115);
                uint text=comment<<2; return Start(engine.BeginComment(id,text+1,memory.Read8(text)),21);
            case 34:
                uint date=Arg(3); if(date==0||(date&1)!=0) return Finish(0,115);
                return Start(engine.BeginDate(id,memory.Read32(date),memory.Read32(date+4),memory.Read32(date+8)),21);
            case 23: case 1034: case 26408: case 26410: return Examine();
            default: return Finish(0,209);
        }
    }
    private bool Reserve(ResourceKind kind)
    {
        if(pools.Take(kind,out uint address,out _)!=CpxError.None) return false;
        W(Reserved,address); W(ReservedKind,(uint)kind); return true;
    }
    private StepResult OpenHandle()
    {
        uint handle=R(Reserved); if(handle==0) return Finish(0,103); W(Reserved,0);
        Put(handle,G(State+Object)); memory.Write32(handle+16,R(Action)==1006?uint.MaxValue:0xfffffffe); memory.Write32(handle+20,R(Action)==1005?0u:1u);
        Put(handle+24,G(State+ParentId)); Put(handle+32,G(State+EntryId));
        if(R(Phase) is 30 or 32) memory.Write32(handle+40,1);
        uint fh=Arg(0)<<2; memory.Write32(fh+8,R(Port)); memory.Write32(fh+36,handle); return Finish(uint.MaxValue);
    }
    private StepResult FinalComponent()
    {
        uint action=R(Action),at=R(PathAt),length=R(PathLength)-at; Wide parent=G(State+ParentId);
        if(action==1021) return CreateLink();
        if(action==17&&R(Destination)!=0)
        {
            if(engine.LookupFound&&Conflicts(engine.LookupId,true)) return Finish(0,202);
            W(Phase,84); return StepResult.Pending;
        }
        if(action==22||!engine.LookupFound&&(action is 1004 or 1006))
        {
            if(engine.LookupFound) return Finish(0,203);
            if(action!=22&&(Arg(0)==0||Arg(0)>0x3fffffff)) return Finish(0,115);
            if(!Reserve(action==22?ResourceKind.Lock:ResourceKind.Handle)) return Finish(0,103);
            return Start(engine.BeginCreate(parent,Path+at,length,action==22),30);
        }
        if(!engine.LookupFound) return Finish(0,205);
        Put(State+EntryId,G(engine.TreeRecord+8));
        if(action is 16 or 17)
        {
            if(Conflicts(engine.LookupId,true)) return Finish(0,202);
            W(Phase,85); return StepResult.Pending;
        }
        Put(State+Object,engine.LookupId); return Start(engine.BeginOperation(CpxOperation.ReadObject,engine.LookupId),12);
    }
    private StepResult SeekFinish()
    {
        bool wide=R(Action) is 26400 or 26401,resize=R(Action) is 1022 or 26401; uint failure=wide?0u:uint.MaxValue;
        if(!engine.ObjectFound) return Finish(failure,205);
        uint h=R(Handle),mode=Arg(2); Wide old=G(h+8),basis,offset;
        if(wide)
        {
            if(Arg(1)==0||Arg(3)==0||((Arg(1)|Arg(3))&1)!=0||Arg(1)>uint.MaxValue-8||Arg(3)>uint.MaxValue-8) return Finish(0,115);
            offset=G(Arg(1));
        }
        else offset=new((Arg(1)&0x80000000)!=0?uint.MaxValue:0,Arg(1));
        if(mode==uint.MaxValue) basis=default; else if(mode==0) basis=old; else if(mode==1) basis=G(engine.ObjectRecord+8); else return Finish(failure,219);
        Wide next; bool valid;
        if((offset.High&0x80000000)==0) valid=Wide.Add(basis,offset,out next);
        else { Wide.Add(new(~offset.High,~offset.Low),Wide.From(1),out var magnitude); valid=Wide.Subtract(basis,magnitude,out next); }
        if(!valid||!wide&&(next.High!=0||next.Low>int.MaxValue||old.High!=0||old.Low>int.MaxValue)) return Finish(failure,219);
        if(resize)
        {
            if(memory.Read32(h+20)==0) return Finish(failure,223);
            for(uint n=0;n<32;n++)
            {
                uint other=Pool+n*64+8;
                if(other!=h&&memory.Read32(other-8)==1&&Wide.Equal(G(other),G(h))&&Wide.Compare(G(other+8),next)>0) next=G(other+8);
            }
            if(!wide&&(next.High!=0||next.Low>int.MaxValue)) return Finish(failure,219);
            CpxError err=engine.BeginResize(G(h),next); if(err!=CpxError.None) return Finish(failure,ToDosError(err));
            Put(State+ResizeResult,next); W(Phase,25); return StepResult.Pending;
        }
        if(Wide.Compare(next,G(engine.ObjectRecord+8))>0) return Finish(failure,219);
        if(wide) Put(Arg(3),old); Put(h+8,next); return Finish(wide?uint.MaxValue:old.Low);
    }
    private StepResult Dispatch()
    {
        uint a=Arg(0),action=R(Action); Wide id;
        if(!MediaAvailable)
        {
            if(action is 25 or 26) return OfflineInformation();
            if(action==7) return Finish(0);
            if(action==32) return Finish(uint.MaxValue);
            // Like the SFS media gate, report NoDisk before dispatching WaitChar,
            // Timer and DiskChange. On mounted media all three use the unknown-action reply.
            if(action is TrimPacket or 8 or 9 or 16 or 17 or 20 or 21 or 22 or 23 or 24 or 27 or 28 or 29 or 30 or 33 or 34 or 40 or 82 or 87 or
                1004 or 1005 or 1006 or 1008 or 1021 or 1022 or 1023 or 1024 or 1026 or 1028 or 1030 or 1031 or 1033 or 1034 or 1036 or 2008 or
                26400 or 26401 or 26402 or 26406 or 26407 or 26408 or 26409 or 26410) return Finish(action is 82 or 87 or 1008 or 1022?uint.MaxValue:0,226);
        }
        if(IsWriteProtected&&(action is 9 or 16 or 17 or 21 or 22 or 28 or 34 or 87 or 1004 or 1006 or 1020 or 1021 or 1022 or 1036 or 26401))
            return Finish(action is 87 or 1022?uint.MaxValue:0,214);
        switch(action)
        {
            case TrimPacket: return BeginManualTrimPacket();
            case 31: return BeginInhibit();
            case 9: case 1020: return BeginVolumePacket();
            case 1033: return BeginExAll();
            case 1035: return EndExAll();
            case 8: case 16: case 17: case 22: return StartPath(a,Arg(1));
            case 1004: case 1005: case 1006: return StartPath(Arg(1),Arg(2));
            case 21: case 28: case 34: case 1036: return StartPath(Arg(1),Arg(2));
            case 82: case 87:
                if(!ValidResource(a,ResourceKind.Handle)) return Finish(uint.MaxValue,211);
                if(action==87&&memory.Read32(a+20)==0) return Finish(uint.MaxValue,223);
                if(Arg(2)>int.MaxValue) return Finish(uint.MaxValue,115);
                W(Handle,a);
                return Start(action==82?engine.BeginFileRead(G(a),G(a+8),Arg(1),Arg(2)):engine.BeginMappedWrite(G(a),G(a+8),Arg(1),Arg(2)),20);
            case 1007:
                if(!ValidResource(a,ResourceKind.Handle)) return Finish(0,211);
                W(Handle,a); W(Phase,111); return StepResult.Pending;
            case 2008: case 2009: case 26402: case 26403: return BeginRecord();
            case 4097: return BeginAddNotification();
            case 4098: return RemoveNotification();
            case 1008: case 1022: case 26400: case 26401:
                if(!ValidResource(a,ResourceKind.Handle)) return Finish(action>=26400?0u:uint.MaxValue,211);
                W(Handle,a); return Start(engine.BeginOperation(CpxOperation.ReadObject,G(a)),23);
            case 15:
                if(a==0) return Finish(uint.MaxValue);
                if(a>0x3fffffff||!ValidResource(a<<2,ResourceKind.Lock)) return Finish(0,211);
                W(LifeResource,a<<2); W(Phase,80); return StepResult.Pending;
            case 19:
                if(!LockId(a,out id)) return Finish(0,211);
                if(a!=0) { uint context=LockContext(a<<2); Context(context,context+8); }
                return MakeLock(id,0xfffffffe);
            case 29:
                if(!LockId(a,out id)) return Finish(0,211);
                if(Wide.Equal(id,Wide.From(1))) return Finish(0);
                if(a!=0) { uint context=LockContext(a<<2); Context(context,context+8); }
                return Start(engine.BeginOperation(CpxOperation.ReadObject,id),24);
            case 1030: case 1031:
                if(!ValidResource(a,ResourceKind.Handle)) return Finish(0,211);
                Context(a+24,a+32); id=G(a);
                if(action==1030) return MakeLock(id,0xfffffffe);
                return Start(engine.BeginOperation(CpxOperation.ReadObject,id),24);
            case 1026: return BeginFromLock();
            case 1028: return BeginChangeMode();
            case 1023: return WriteProtection();
            case 26406: return BeginNewReadLink();
            case 1021: case 1024: return BeginLinkPacket();
            case 18: return Finish(0); // No persistent cache; all six pages are fixed operation scratch.
            case 40:
                if(!LockId(a,out id)||!LockId(Arg(1),out var second)) return Finish(0,211);
                return Finish(Wide.Equal(id,second)?uint.MaxValue:0u);
            case 23: case 1034: case 26408: case 26410:
                if(action is 23 or 26408)
                { if(!LockId(a,out id)) return Finish(0,211); if(a!=0) { uint context=LockContext(a<<2); Context(context,context+8); } }
                else { if(!ValidResource(a,ResourceKind.Handle)) return Finish(0,211); id=G(a); Context(a+24,a+32); }
                Put(State+Object,id); return Start(engine.BeginOperation(CpxOperation.ReadObject,id),12);
            case 24: case 26409: return BeginNext();
            case 25: case 26: case 26407: return BeginInformation();
            case 27: return Start(IsWriteProtected?engine.BeginPersistenceBarrier():engine.BeginOperation(CpxOperation.Flush),21);
            case 7: return Finish(R(Volume)>>2);
            case 32: return Finish(CpxArena.Magic);
            case 1027: return Finish(uint.MaxValue);
            // Until the corresponding mutation or lifetime policy is present,
            // refuse the packet explicitly instead of reporting a false success.
            default: return Finish(0,209);
        }
    }
}
