namespace CopperCPX;

public partial struct CpxDosDispatcher<TMemory> where TMemory : struct, ICpxMemory
{
    // Union with examination scratch; link packets never run FIB phases.
    private uint LinkText=>arena+3072;
    private const uint LinkScan=204,LinkBytes=208,LinkDecoded=212,LinkPrefix=216,LinkSuffix=220,LinkAbsolute=224,LinkTotal=228,LinkTarget=232;
    private StepResult BeginLinkPacket()
    {
        bool create=R(Action)==1021;
        if(create&&Arg(3)>1) return Finish(0,115);
        if(create&&Arg(3)==0)
        {
            if(Arg(2)==0||!LockId(Arg(2),out var id)) return Finish(0,211);
            Put(State+LinkTarget,id); return StartPath(Arg(0),Arg(1));
        }
        uint input=Arg(create?2u:1u),limit=create?768u:255u;
        if(input==0||input>uint.MaxValue-limit) return Finish(0,115);
        if(!create)
        {
            if(!LockId(Arg(0),out var id)) return Finish(0,211);
            if(Arg(0)!=0) { uint context=LockContext(Arg(0)<<2); Context(context,context+8); }
            if(Arg(2)==0||Arg(3)>int.MaxValue||Arg(2)>uint.MaxValue-Arg(3)) return Finish(0,115);
            Put(State+Object,id); W(PathAt,0);
        }
        W(LinkScan,0); W(Phase,90); return StepResult.Pending;
    }
    private StepResult LinkStep()
    {
        uint phase=R(Phase);
        if(phase==90)
        {
            bool create=R(Action)==1021; uint at=R(LinkScan),limit=create?768u:255u,input=Arg(create?2u:1u),target=create?LinkText:Path;
            for(uint work=0;work<64;work++,at++)
            {
                byte c=memory.Read8(input+at);
                if(c==0)
                {
                    if(create) { if(at==0) return Finish(0,210); W(LinkBytes,at); return StartPath(Arg(0),Arg(1)); }
                    W(PathLength,at); W(Phase,10); return StepResult.Pending;
                }
                if(at==limit) return Finish(0,120);
                memory.Write8(target+at,c);
                if(!create&&c==58) { Put(State+Object,Wide.From(1)); W(PathAt,at+1); }
            }
            W(LinkScan,at); return StepResult.Pending;
        }
        if(phase==92)
        {
            StepResult step=engine.Step(); if(step is StepResult.Pending or StepResult.NeedIo) return step;
            if(step==StepResult.Failed) return Finish(0,ToDosError(engine.Error));
            W(LinkBytes,engine.TransferredBytes); W(LinkScan,0); W(LinkDecoded,0); W(LinkAbsolute,0); W(Phase,93); return StepResult.Pending;
        }
        if(phase==93)
        {
            uint at=R(LinkScan),decoded=R(LinkDecoded);
            for(uint work=0;work<64&&at<R(LinkBytes);work++)
            {
                uint c=memory.Read8(LinkText+at++);
                if(c>=128)
                {
                    if(c is not (194 or 195)||at==R(LinkBytes)) return Finish(0,210);
                    uint tail=memory.Read8(LinkText+at++); if(tail<128||tail>191) return Finish(0,213);
                    c=((c&3)<<6)|(tail&63);
                }
                if(c==0) return Finish(0,213);
                if(c==58) W(LinkAbsolute,decoded==0?1u:2u);
                memory.Write8(LinkText+decoded++,(byte)c);
            }
            W(LinkScan,at); W(LinkDecoded,decoded); if(at<R(LinkBytes)) return StepResult.Pending;
            if(R(Action)==26406) return BeginNewLinkTail(true);
            uint prefix=R(LinkAbsolute)==0?R(LinkPrefix):R(LinkAbsolute)==1?10u:0u,suffix=R(LinkSuffix);
            if(suffix<R(PathLength)&&decoded!=0&&memory.Read8(LinkText+decoded-1) is 47 or 58) suffix++;
            W(LinkPrefix,prefix); W(LinkSuffix,suffix); uint total=prefix+decoded+R(PathLength)-suffix;
            if(total>=Arg(3)) return Finish(0xfffffffe,120);
            W(LinkTotal,total); W(LinkScan,0); W(Phase,94); return StepResult.Pending;
        }
        if(phase==94)
        {
            uint at=R(LinkScan),prefix=R(LinkPrefix),decoded=R(LinkDecoded),output=Arg(2);
            for(uint work=0;work<64&&at<R(LinkTotal);work++,at++)
            {
                byte c;
                if(at<prefix) c=R(LinkAbsolute)==1?VolumeNameByte(at):memory.Read8(Path+at);
                else if(at<prefix+decoded) c=memory.Read8(LinkText+at-prefix);
                else c=memory.Read8(Path+R(LinkSuffix)+at-prefix-decoded);
                memory.Write8(output+at,c);
            }
            W(LinkScan,at); if(at<R(LinkTotal)) return StepResult.Pending;
            memory.Write8(output+at,0); return Finish(at);
        }
        return Finish(0,209);
    }
    private static byte VolumeNameByte(uint at)=>at switch { 0=>67,1=>111,2 or 3=>112,4=>101,5=>114,6=>67,7=>80,8=>88,_=>52 };
    private StepResult CreateLink()
    {
        if(engine.LookupFound) return Finish(0,203);
        uint at=R(PathAt),length=R(PathLength)-at; Wide parent=G(State+ParentId);
        return Start(Arg(3)==0?engine.BeginHardLink(G(State+LinkTarget),parent,Path+at,length):
            engine.BeginSymlink(parent,Path+at,length,LinkText,R(LinkBytes)),31);
    }
    private StepResult PathObject()
    {
        if(!engine.ObjectFound) return Finish(0,205);
        uint kind=memory.Read8(engine.ObjectRecord+84);
        if(kind==3)
        {
            if(R(Action)==4097&&R(PathAt)==R(PathLength)) return Resolved();
            if(R(Action) is not (1024 or 26406)) return Finish(0,233);
            return Start(engine.BeginReadSymlink(G(State+Object),LinkText,768),92);
        }
        if(R(Action)==26406&&kind==1&&memory.Read32(engine.ObjectRecord+76)>1) return BeginCanonicalScan();
        if(R(PathAt)==R(PathLength)) return R(Action)==26406?Finish(0,212):R(Action)==1024?Finish(0,205):Resolved();
        if(kind!=2) return Finish(0,212);
        W(Phase,10); return StepResult.Pending;
    }
}
