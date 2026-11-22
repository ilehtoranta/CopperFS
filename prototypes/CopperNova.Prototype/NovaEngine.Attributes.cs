using CopperCPX;
namespace CopperNova.Prototype;

public partial struct NovaEngine<TMemory> where TMemory : struct,ICpxMemory
{
    private uint CommentIndex(uint group,bool vacant)
    {
        uint table=Root()+416;
        for(uint i=0;i<24;i++) if(m.Read16(table+i*4)==(vacant?0:group+1)) return table+i*4;
        return 0;
    }
    private uint CommentBase(uint entry)=>1346+((entry-(Root()+416))>>1);
    private StepResult LoadCommentPage(uint home,uint dest)
    {
        uint group=home-2000,entry=CommentIndex(group,false);
        if(entry==0) { Zero(dest,512); Header(dest,35,home,R(Generation),group); return LoadedPacked(); }
        uint sector=m.Read16(entry+2); if(!Pair(sector,CommentBase(entry))) return Fatal();
        return Issue(IoKind.Read,sector,dest,512,1105);
    }
    private StepResult AcceptCommentPage(uint home,uint dest)
    {
        uint group=home-2000,entry=CommentIndex(group,false);
        if(entry==0||!PageValid(dest,35,m.Read16(entry+2),group)) return Fatal();
        for(uint i=0;i<4;i++) if(m.Read8(dest+64+i*80)>79) return Fatal();
        Header(dest,35,home,R(Generation),group); return LoadedPacked();
    }
    private StepResult WriteCommentPage(uint home,uint p,uint gen)
    {
        uint group=home-2000,entry=CommentIndex(group,false),output=Arena+Output;
        if(entry==0||m.Read16(p+6)!=35||m.Read32(p+28)!=group) return Fatal();
        uint sector=Alternate(m.Read16(entry+2),CommentBase(entry)); m.Copy(output,p,512); Stamp(output,35,sector,group,gen);
        RootPointer(entry+2,sector); return Issue(IoKind.Write,sector,output,512,1201);
    }
    private StepResult SetPackedComment()
    {
        uint id=R(Target),group=id>>2,src=Arg(3)<<2,len=m.Read8(src),entry=CommentIndex(group,false),p=R(Loaded),dst=p+64+(id&3)*80;
        if(entry==0) { if(len==0) return Finish(uint.MaxValue); entry=CommentIndex(group,true); if(entry==0) return Error(221); RootPointer(entry,group+1); }
        Zero(dst,80); m.Copy(dst,src,len+1); Dirty(p); return Finish(uint.MaxValue);
    }
    private StepResult ExaminePacked(uint phase)
    {
        uint id=R(Target),fib=Arg(1)<<2,p=R(Loaded);
        if(phase==54) {
            if(!ValidInode(p,id)) return Fatal();
            Zero(fib,260); if(R(Action)==24) m.Write32(fib,id);
            uint type=id==1?1u:m.Read16(p+6)==11?2u:0xfffffffdu,size=m.Read32(p+68);
            m.Write32(fib+4,type); m.Write32(fib+120,type); m.Write32(fib+124,size); m.Write32(fib+128,(size+511)>>9);
            if(GrowingObjects) m.Copy(fib+144,p+ObjectComment,(uint)m.Read8(p+ObjectComment)+1);
            if(id!=1) return Load(ObjectHome(DynamicIndex&&m.Read32(p+NameContainer)!=0?m.Read32(p+NameContainer):m.Read32(p+108)),55);
            uint name=m.Read32(R(Volume)+40)<<2; if(name!=0) { uint len=m.Read8(name); if(len>107) return Error(210); m.Copy(fib+8,name,len+1); }
            return Finish(uint.MaxValue);
        }
        if(phase==55) {
            if(m.Read16(p+6)!=11||m.Read32(p+100)>192) return Fatal(); uint end=p+320+m.Read32(p+100),at=p+320;
            while(at<end) { if(end-at<24) return Fatal(); uint len=m.Read16(at+20),step=(24+len+7)&~7u;
                if(step>end-at||len==0) return Fatal(); if(m.Read32(at+4)==id) { if(len>107) return Error(210);
                    m.Write8(fib+8,(byte)len); m.Copy(fib+9,at+24,len);
                    if(GrowingObjects||CommentIndex(id>>2,false)==0) return Finish(uint.MaxValue);
                    return Load(2000+(id>>2),56); } at+=step; } return Fatal();
        }
        uint src=p+64+(id&3)*80; m.Copy(fib+144,src,(uint)m.Read8(src)+1); return Finish(uint.MaxValue);
    }
}
