namespace CopperCPX;

public partial struct CpxDosDispatcher<TMemory> where TMemory : struct, ICpxMemory
{
    // Operation-only state, within the existing 512-byte dispatcher budget.
    // Enumeration payload +40 identifies ExAll; ExNext leaves it zero.
    private const uint AllActive=400,AllControl=404,AllOutput=408,AllCapacity=412,
        AllUsed=416,AllPrevious=420,AllSize=424,AllCopy=428,AllHeader=432;

    private uint FindAll(uint control,uint key,Wide id)
    {
        for(uint n=0;n<16;n++)
        {
            uint p=Pool+3584+n*64+8;
            if(memory.Read32(p-8)==1&&memory.Read32(p+40)==1033&&
                memory.Read32(p+28)==key&&memory.Read32(p+32)==control&&Wide.Equal(G(p),id)) return p;
        }
        return 0;
    }
    private StepResult BeginExAll()
    {
        uint control=Arg(4);
        if(!Buffer(control,16)) return Finish(0,115);
        if(!LockId(Arg(0),out var id)) return Finish(0,211);
        uint key=memory.Read32(control+4),slot=key==0||key==uint.MaxValue?0:FindAll(control,key,id);
        memory.Write32(control,0);
        if(key==uint.MaxValue) return Finish(0,232);
        if(key!=0&&slot==0) return Finish(0,211);
        if(key==0)
        {
            // A client restart, even one refused below, must not strand the old
            // cursor. This scan plus LockId visits at most 48 resource slots.
            for(uint n=0;n<16;n++)
            {
                uint p=Pool+3584+n*64+8;
                if(memory.Read32(p-8)==1&&memory.Read32(p+40)==1033&&memory.Read32(p+32)==control)
                    memory.Write32(p-8,0);
            }
        }
        W(AllActive,1); W(AllControl,control); W(EnumSlot,slot);
        if(Arg(3)<1||Arg(3)>7) return Finish(0,115);
        if(memory.Read32(control+8)!=0||memory.Read32(control+12)!=0)
        {
            // dos.library supplies its own filtering emulation. Refuse before
            // taking a cursor; never execute arbitrary callbacks on our stack.
            if(key==0) { W(AllActive,0); return Finish(0,209); }
            return Finish(0,115);
        }
        uint output=Arg(1),bytes=Arg(2);
        if(bytes>int.MaxValue||!Buffer(output,bytes)||
            output<control+16&&control<output+bytes) return Finish(0,115);
        if(slot==0)
        {
            // Pool admission has its own phase, separate from lock validation.
            W(AllOutput,output); W(AllCapacity,bytes); Put(State+Object,id);
            W(Phase,66); return StepResult.Pending;
        }
        memory.Write32(slot+36,Arg(0));
        return StartAll(slot,output,bytes,id);
    }
    private StepResult StartAll(uint slot,uint output,uint bytes,Wide id)
    {
        W(EnumSlot,slot); W(AllOutput,output); W(AllCapacity,bytes);
        W(AllUsed,0); W(AllPrevious,0); W(InfoMode,1); Put(State+Object,id);
        return Start(engine.BeginOperation(CpxOperation.ReadObject,id),40);
    }
    private StepResult EndExAll()
    {
        uint control=Arg(4);
        if(!Buffer(control,16)) return Finish(0,115);
        if(!LockId(Arg(0),out var id)) return Finish(0,211);
        uint key=memory.Read32(control+4);
        if(key!=0&&key!=uint.MaxValue)
        {
            uint p=FindAll(control,key,id); if(p==0) return Finish(0,211);
            memory.Write32(p-8,0);
        }
        memory.Write32(control+4,uint.MaxValue);
        return Finish(uint.MaxValue);
    }
    private void FinishExAll(uint error)
    {
        if(R(AllActive)==0) return;
        if(error!=0)
        {
            uint p=R(EnumSlot);
            if(p!=0) { memory.Write32(p-8,0); memory.Write32(R(AllControl)+4,uint.MaxValue); }
            W(EnumSlot,0); W(EnumNew,0);
        }
        W(AllActive,0);
    }
    private StepResult PrepareAllRecord()
    { W(Phase,62); return StepResult.Pending; }
    private StepResult ExAllStep()
    {
        uint phase=R(Phase),slot=R(EnumSlot),control=R(AllControl);
        if(phase==66)
        {
            if(R(EnumSerial)>=uint.MaxValue-1||pools.Take(ResourceKind.Enumeration,out slot,out _)!=CpxError.None) return Finish(0,103);
            W(EnumSerial,R(EnumSerial)+1); Put(slot,G(State+Object)); memory.Write32(slot+28,R(EnumSerial));
            memory.Write32(slot+32,control); memory.Write32(slot+36,Arg(0)); memory.Write32(slot+40,1033);
            memory.Write32(control+4,R(EnumSerial));
            return StartAll(slot,R(AllOutput),R(AllCapacity),G(State+Object));
        }
        uint type=Arg(3),name=memory.Read8(InfoFib+8),comment=type>=6?memory.Read8(InfoFib+144):0u;
        if(phase==62)
        {
            uint header=type<=4?4+type*4:type==5?32u:type==6?36u:40u;
            uint size=(header+name+1+(comment!=0?comment+1:0)+1)&0xfffffffe;
            if(size>R(AllCapacity)-R(AllUsed))
                return memory.Read32(control)==0?Finish(0,103):Finish(uint.MaxValue);
            W(AllSize,size); W(AllHeader,header); W(AllCopy,0); W(Phase,63);
            uint target=R(AllOutput)+R(AllUsed);
            memory.Write32(target,0); memory.Write32(target+4,target+header);
            if(type>=2) memory.Write32(target+8,memory.Read32(InfoFib+4));
            if(type>=3) memory.Write32(target+12,memory.Read32(InfoFib+124));
            if(type>=4) memory.Write32(target+16,memory.Read32(InfoFib+116));
            if(type>=5) { memory.Write32(target+20,memory.Read32(InfoFib+132)); memory.Write32(target+24,memory.Read32(InfoFib+136)); memory.Write32(target+28,memory.Read32(InfoFib+140)); }
            if(type>=6) memory.Write32(target+32,comment==0?0:target+header+name+1);
            if(type==7) memory.Write32(target+36,memory.Read32(InfoFib+224));
            return StepResult.Pending;
        }
        if(phase==63)
        {
            uint at=R(AllCopy),bytes=R(AllSize)-R(AllHeader),target=R(AllOutput)+R(AllUsed)+R(AllHeader);
            for(uint work=0;work<64&&at<bytes;work++,at++)
            {
                byte value=0;
                if(at<name) value=memory.Read8(InfoFib+9+at);
                else if(at>name&&at-name-1<comment) value=memory.Read8(InfoFib+145+at-name-1);
                memory.Write8(target+at,value);
            }
            W(AllCopy,at); if(at==bytes) W(Phase,64); return StepResult.Pending;
        }
        if(phase!=64) return Finish(0,209);
        uint entry=R(AllOutput)+R(AllUsed),previous=R(AllPrevious);
        if(previous!=0) memory.Write32(previous,entry);
        W(AllPrevious,entry); W(AllUsed,R(AllUsed)+R(AllSize));
        memory.Write32(control,memory.Read32(control)+1);
        Put(slot+8,G(InfoEntry)); Put(slot+16,G(InfoEntry+8)); memory.Write32(slot+24,1);
        Put(State+InfoHigh,G(InfoEntry)); Put(State+InfoLow,G(InfoEntry+8));
        return FindEntry(false,43);
    }
}
