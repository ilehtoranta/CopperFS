namespace CopperCPX;

public partial struct CpxDosDispatcher<TMemory> where TMemory : struct, ICpxMemory
{
    private const uint SoftwareProtected=184,ProtectionKey=188,LockAccess=192,LifeResource=196,LifeMode=200;
    public bool IsWriteProtected=>engine.IsReadOnly||R(SoftwareProtected)!=0;

    private bool ConflictsExcept(Wide id,uint except)
    {
        // Exactly 64 slots; performed in its own dispatcher phase.
        for(uint n=0;n<32;n++)
        {
            uint h=Pool+n*64+8,l=Pool+2048+n*48+8;
            if(h!=except&&memory.Read32(h-8)==1&&Wide.Equal(G(h),id)) return true;
            if(l!=except&&memory.Read32(l-8)==1&&Wide.Equal(G(l+20),id)) return true;
        }
        return false;
    }
    private StepResult BeginFromLock()
    {
        uint fh=Arg(0),lockBptr=Arg(1);
        if(fh>0x3fffffff||!Buffer(fh<<2,44)) return Finish(0,115);
        if(lockBptr==0||!LockId(lockBptr,out var id)) return Finish(0,211);
        uint p=lockBptr<<2,context=LockContext(p); Context(context,context+8);
        W(LifeResource,p); Put(State+Object,id);
        return Start(engine.BeginOperation(CpxOperation.ReadObject,id),71);
    }
    private StepResult BeginChangeMode()
    {
        uint kind=Arg(0),bptr=Arg(1),mode=Arg(2),resource;
        if(kind>1||bptr>0x3fffffff||!Buffer(bptr<<2,kind==0?40u:44u)) return Finish(0,115);
        if(mode is 1004 or 1005) mode=0xfffffffe; else if(mode==1006) mode=uint.MaxValue;
        if(mode is not (0xfffffffe or 0xffffffff)) return Finish(0,115);
        resource=kind==0?bptr<<2:memory.Read32((bptr<<2)+36);
        if(!ValidResource(resource,kind==0?ResourceKind.Lock:ResourceKind.Handle)) return Finish(0,211);
        W(LifeResource,resource); W(LifeMode,mode); Put(State+Object,G(resource+(kind==0?20u:0u)));
        W(Phase,74); return StepResult.Pending;
    }
    private StepResult WriteProtection()
    {
        if(Arg(0)!=0)
        {
            if(IsWriteProtected) return Finish(0,214);
            // Publish the control state only after the device confirms the barrier.
            return Start(engine.BeginOperation(CpxOperation.Flush),75);
        }
        if(R(SoftwareProtected)!=0&&R(ProtectionKey)!=0&&R(ProtectionKey)!=Arg(1)) return Finish(0,210);
        W(SoftwareProtected,0); W(ProtectionKey,0); return Finish(uint.MaxValue);
    }
    private StepResult LifecycleStep()
    {
        uint phase=R(Phase),p=R(LifeResource);
        if(phase==83) return FinalComponent();
        if(phase==84) return Start(engine.BeginRename(G(State+OldParent),OldName,R(OldLength),G(State+ParentId),Path+R(PathAt),R(PathLength)-R(PathAt)),31);
        if(phase==85)
        {
            uint at=R(PathAt),length=R(PathLength)-at; Wide parent=G(State+ParentId);
            if(R(Action)==16) return Start(engine.BeginDelete(parent,Path+at,length),31);
            Put(State+OldParent,parent); W(OldLength,length);
            for(uint n=0;n<length;n++) memory.Write8(OldName+n,memory.Read8(Path+at+n));
            W(Destination,1); return StartPath(Arg(2),Arg(3));
        }
        if(phase==80)
        {
            // Validation was a separate phase. Retire at most 16 cursors and
            // scan at most 32 lock slots; no nested pool scans.
            ReleaseEnumerations(Arg(0),0,false); memory.Write32(p+12,0);
            memory.Write32(p-8,0); return Finish(uint.MaxValue);
        }
        if(phase==81)
        {
            if(Conflicts(G(State+Object),R(Action)==1006)) return Finish(0,202);
            W(Phase,82); return StepResult.Pending;
        }
        if(phase==82)
        {
            if(!Reserve(ResourceKind.Handle)) return Finish(0,103);
            if(R(Action)==1006) return Start(engine.BeginResize(G(State+Object),default),32);
            return OpenHandle();
        }
        if(phase==74)
        {
            if(R(LifeMode)==uint.MaxValue&&ConflictsExcept(G(State+Object),p)) return Finish(0,202);
            memory.Write32(p+(Arg(0)==0?8u:16u),R(LifeMode)); return Finish(uint.MaxValue);
        }
        if(phase==78)
        {
            if(Conflicts(G(State+Object),R(LockAccess)==uint.MaxValue)) return Finish(0,202);
            W(Phase,79); return StepResult.Pending;
        }
        if(phase==79)
        {
            Wide id=G(State+Object); uint locked=R(Reserved);
            if(locked==0&&pools.Take(ResourceKind.Lock,out locked,out _)!=CpxError.None) return Finish(0,103);
            W(Reserved,0); memory.Write32(locked+4,id.Low); memory.Write32(locked+8,R(LockAccess));
            memory.Write32(locked+12,R(Port)); memory.Write32(locked+16,R(Volume)>>2); Put(locked+20,id);
            uint context=LockContext(locked); Put(context,G(State+ParentId)); Put(context+8,G(State+EntryId));
            return Finish(locked>>2);
        }
        if(phase==72)
        {
            if(!Reserve(ResourceKind.Handle)) return Finish(0,103);
            W(Phase,73); return StepResult.Pending;
        }
        if(phase==73)
        {
            uint handle=R(Reserved); Put(handle,G(State+Object));
            memory.Write32(handle+16,memory.Read32(p+8)); memory.Write32(handle+20,1);
            Put(handle+24,G(State+ParentId)); Put(handle+32,G(State+EntryId));
            uint fh=Arg(0)<<2; memory.Write32(fh+8,R(Port)); memory.Write32(fh+36,handle);
            // No fallible work follows the ownership transfer. Failure before
            // this phase leaves both the caller's lock and FileHandle untouched.
            ReleaseEnumerations(Arg(1),0,false); memory.Write32(p+12,0);
            memory.Write32(p-8,0); W(Reserved,0);
            return Finish(uint.MaxValue);
        }
        StepResult step=engine.Step(); if(step is StepResult.Pending or StepResult.NeedIo) return step;
        if(step==StepResult.Failed) return Finish(0,ToDosError(engine.Error));
        if(phase==75) { W(SoftwareProtected,1); W(ProtectionKey,Arg(1)); return Finish(uint.MaxValue); }
        if(!engine.ObjectFound) return Finish(0,205);
        if(phase==70)
        {
            if(memory.Read8(engine.ObjectRecord+84)!=2) return Finish(0,213);
            Put(State+Object,G(engine.ObjectRecord)); Put(State+ParentId,G(engine.ObjectRecord+40)); Put(State+EntryId,default);
            return MakeLock(G(State+Object),0xfffffffe);
        }
        if(phase==71)
        {
            if(memory.Read8(engine.ObjectRecord+84)!=1) return Finish(0,212);
            if((memory.Read32(engine.ObjectRecord+72)&8)!=0) return Finish(0,224);
            W(Phase,72); return StepResult.Pending;
        }
        return Finish(0,209);
    }
}
