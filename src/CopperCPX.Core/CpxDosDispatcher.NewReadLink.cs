namespace CopperCPX;

public partial struct CpxDosDispatcher<TMemory> where TMemory : struct, ICpxMemory
{
    // Read-only operation union. The read engines use 8192..9535, reset the
    // namespace publication word at 10696, and use symlink scalars at 15524.
    // Input is snapshotted before lookup; the answer grows backwards so a
    // parent walk needs neither recursion nor an array of directory names.
    private uint NewPath=>arena+9600;           // 1024 bytes including NUL
    private uint NewAnswer=>arena+10800;        // 4096 bytes including NUL
    private const uint AnswerEnd=4095;
    // Offsets relative to dispatcher State (arena + 512), starting at 14912.
    private const uint AnswerAt=14400,PieceSource=14404,PieceLeft=14408,PieceNext=14412,
        ObjectIndex=14416,DirectoryId=14424,DirectoryIndex=14432,CursorHash=14440,
        CursorEntry=14448,BestEntry=14456,BestParent=14464,AncestorId=14472,
        AncestorParent=14480,RemainingLinks=14488,HardTarget=14492;
    private const uint ReadInput=140,CopyPiece=141,AddTarget=142,AddPrefix=143,
        ReadIndex=144,ScanObject=145,ScanEntry=146,ReadAncestor=147,ReadParent=148,
        FindChild=149,PublishAnswer=150,CopyAnswer=151,NextAncestor=152;

    private StepResult BeginNewReadLink()
    {
        if(!LockId(Arg(0),out var id)) return Finish(0,211);
        if(Arg(1)==0) return Finish(0,115);
        Put(State+Object,id); W(LinkScan,0); W(PathAt,0); W(HardTarget,0);
        W(AnswerAt,AnswerEnd); W(Phase,ReadInput); return StepResult.Pending;
    }
    private StepResult ReadNewLinkInput()
    {
        uint at=R(LinkScan),input=Arg(1);
        for(uint n=0;n<64;n++,at++)
        {
            if(input>uint.MaxValue-at) return Finish(0,115);
            byte c=memory.Read8(input+at);
            if(c==0) { W(PathLength,at); W(LinkScan,0); W(Phase,10); return StepResult.Pending; }
            if(at==1023) return Finish(0,120);
            memory.Write8(NewPath+at,c);
            if(c==58) { Put(State+Object,Wide.From(1)); W(PathAt,at+1); }
        }
        W(LinkScan,at); return StepResult.Pending;
    }
    private StepResult NewLinkComponent()
    {
        uint start=R(PathAt),length=R(PathLength),end=start+R(LinkScan);
        if(start==length) return Finish(0,212);
        for(uint n=0;n<64&&end<length&&memory.Read8(NewPath+end)!=47;n++) end++;
        if(end-start>255) return Finish(0,210);
        if(end<length&&memory.Read8(NewPath+end)!=47) { W(LinkScan,end-start); return StepResult.Pending; }
        W(LinkScan,0);
        if(end==start) return Start(engine.BeginOperation(CpxOperation.ReadObject,G(State+Object)),14);
        Put(State+ParentId,G(State+Object)); W(PathEnd,end);
        return Start(engine.BeginLookup(G(State+Object),NewPath+start,end-start),11);
    }
    private StepResult BeginCanonicalScan()
    {
        W(HardTarget,1); W(RemainingLinks,memory.Read32(engine.ObjectRecord+76));
        Put(State+BestEntry,default); Put(State+DirectoryId,default);
        return Start(engine.BeginOperation(CpxOperation.ReadMetadata,engine.StatePageAddress),ReadIndex);
    }
    private StepResult NextDirectory(bool first)
        =>Start(engine.BeginTreeRead(CpxTreeKind.Object,G(State+ObjectIndex),default,G(State+DirectoryId),default,
            first?CpxSeek.Next:CpxSeek.After),ScanObject);
    private StepResult NextEntry(bool first,uint phase)
    {
        if(first) { Put(State+CursorHash,default); Put(State+CursorEntry,default); }
        return Start(engine.BeginTreeRead(CpxTreeKind.Directory,G(State+DirectoryIndex),G(State+DirectoryId),
            G(State+CursorHash),G(State+CursorEntry),first?CpxSeek.Next:CpxSeek.After),phase);
    }
    private StepResult ScanLinkEntry(uint phase)
    {
        if(!engine.TreeFound) return phase==FindChild?Finish(0,213):NextDirectory(false);
        uint rec=engine.TreeRecord; Wide hash=G(rec),entry=G(rec+8);
        int order=Wide.Compare(hash,G(State+CursorHash));
        if(entry.IsZero||order<0||order==0&&Wide.Compare(entry,G(State+CursorEntry))<=0) return Finish(0,213);
        Put(State+CursorHash,hash); Put(State+CursorEntry,entry);
        if(phase==FindChild)
        {
            if(!Wide.Equal(G(rec+16),G(State+AncestorId))) return NextEntry(false,phase);
            CopyInfo(InfoEntry,rec,128); return EntryName();
        }
        if(!Wide.Equal(G(rec+16),G(State+Object))) return NextEntry(false,phase);
        Wide best=G(State+BestEntry);
        if(best.IsZero||Wide.Compare(entry,best)<0)
        {
            Put(State+BestEntry,entry); Put(State+BestParent,G(State+DirectoryId)); CopyInfo(InfoEntry,rec,128);
        }
        W(RemainingLinks,R(RemainingLinks)-1);
        if(R(RemainingLinks)!=0) return NextEntry(false,phase);
        if(Wide.Equal(G(State+BestEntry),G(State+EntryId))) return Finish(0,212);
        return BeginNewLinkTail(false);
    }
    private StepResult PrependPiece(uint source,uint length,uint next)
    {
        if(length>R(AnswerAt)) return Finish(0xfffffffe,120);
        W(PieceSource,source); W(PieceLeft,length); W(PieceNext,next);
        W(Phase,length==0?next:CopyPiece); return StepResult.Pending;
    }
    private StepResult BeginNewLinkTail(bool soft)
    {
        uint suffix=R(LinkSuffix);
        if(soft&&suffix<R(PathLength)&&R(LinkDecoded)!=0&&memory.Read8(LinkText+R(LinkDecoded)-1) is 47 or 58) suffix++;
        return PrependPiece(NewPath+suffix,R(PathLength)-suffix,AddTarget);
    }
    private bool PrependSeparator(byte separator)
    {
        uint at=R(AnswerAt); if(at==0) return false;
        memory.Write8(NewAnswer+at-1,separator); W(AnswerAt,at-1); return true;
    }
    private StepResult PrependVolume(bool colon)
    {
        if(colon&&!PrependSeparator(58)) return Finish(0xfffffffe,120);
        return PrependPiece(engine.VolumeLabelAddress+1,memory.Read8(engine.VolumeLabelAddress),PublishAnswer);
    }
    private StepResult WalkNewLinkParent(Wide id)
    {
        if(Wide.Equal(id,Wide.From(1))) return PrependVolume(true);
        if(id.IsZero) return Finish(0,213);
        Put(State+AncestorId,id);
        return Start(engine.BeginOperation(CpxOperation.ReadObject,id),ReadAncestor);
    }
    private StepResult PrependNewLinkName()
    {
        // HardTarget changes from 1 to 0 after the canonical filename. Every
        // subsequent component comes from the link's actual directory ancestry.
        bool file=R(HardTarget)!=0;
        if(!file&&!PrependSeparator(47)) return Finish(0xfffffffe,120);
        W(HardTarget,0);
        return PrependPiece(InfoFib+9,memory.Read8(InfoFib+8),NextAncestor);
    }
    private StepResult NewReadLinkStep()
    {
        uint phase=R(Phase);
        switch(phase)
        {
            case ReadInput: return ReadNewLinkInput();
            case CopyPiece:
                uint left=R(PieceLeft),count=left>64?64:left,pos=R(AnswerAt)-count;
                CopyInfo(NewAnswer+pos,R(PieceSource)+left-count,count);
                W(AnswerAt,pos); W(PieceLeft,left-count); if(count==left) W(Phase,R(PieceNext)); return StepResult.Pending;
            case AddTarget:
                if(R(HardTarget)!=0) { Put(State+AncestorParent,G(State+BestParent)); return EntryName(); }
                return PrependPiece(LinkText,R(LinkDecoded),AddPrefix);
            case AddPrefix:
                if(R(LinkAbsolute)==0) return WalkNewLinkParent(G(State+ParentId));
                if(R(LinkAbsolute)==1) return PrependVolume(false);
                W(Phase,PublishAnswer); return StepResult.Pending;
            case NextAncestor: return WalkNewLinkParent(G(State+AncestorParent));
            case PublishAnswer:
                uint required=AnswerEnd-R(AnswerAt)+1,output=Arg(2);
                if(output==0) return Finish(required);
                if(Arg(3)>int.MaxValue||Arg(3)<required) return Finish(0xfffffffe,120);
                if(output>uint.MaxValue-(required-1)) return Finish(0,115);
                W(LinkScan,0); W(Phase,CopyAnswer); return StepResult.Pending;
            case CopyAnswer:
                uint at=R(LinkScan),length=AnswerEnd-R(AnswerAt),remaining=length-at,take=remaining>64?64:remaining;
                CopyInfo(Arg(2)+at,NewAnswer+R(AnswerAt)+at,take); W(LinkScan,at+take);
                if(take<remaining) return StepResult.Pending;
                memory.Write8(Arg(2)+length,0); return Finish(length);
        }
        StepResult step=engine.Step(); if(step is StepResult.Pending or StepResult.NeedIo) return step;
        if(step==StepResult.Failed) return Finish(0,ToDosError(engine.Error));
        switch(phase)
        {
            case ReadIndex:
                if(U16(engine.MetadataBuffer+4)!=3||!G(engine.MetadataBuffer+24).IsZero) return Finish(0,213);
                Put(State+ObjectIndex,G(engine.MetadataBuffer+80)); return NextDirectory(true);
            case ScanObject:
                if(!engine.TreeFound||Wide.Compare(G(engine.TreeRecord),G(State+DirectoryId))<=0) return Finish(0,213);
                Put(State+DirectoryId,G(engine.TreeRecord));
                if(memory.Read8(engine.TreeRecord+84)!=2||memory.Read32(engine.TreeRecord+76)==0) return NextDirectory(false);
                Put(State+DirectoryIndex,G(engine.TreeRecord+32)); return NextEntry(true,ScanEntry);
            case ScanEntry: case FindChild: return ScanLinkEntry(phase);
            case ReadAncestor:
                if(!engine.ObjectFound||memory.Read8(engine.ObjectRecord+84)!=2) return Finish(0,213);
                Wide parent=G(engine.ObjectRecord+40);
                if(parent.IsZero||Wide.Equal(parent,G(State+AncestorId))) return Finish(0,213);
                Put(State+AncestorParent,parent);
                return Start(engine.BeginOperation(CpxOperation.ReadObject,parent),ReadParent);
            case ReadParent:
                if(!engine.ObjectFound||memory.Read8(engine.ObjectRecord+84)!=2) return Finish(0,213);
                Put(State+DirectoryId,G(State+AncestorParent)); Put(State+DirectoryIndex,G(engine.ObjectRecord+32));
                return NextEntry(true,FindChild);
            default: return Finish(0,213);
        }
    }
}
