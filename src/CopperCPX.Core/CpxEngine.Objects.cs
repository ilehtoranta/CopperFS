namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    public uint ObjectRecord => Arena+CpxArena.MountBytes;
    public bool ObjectFound => R(OperationAt)==(uint)CpxOperation.ReadObject&&R(Finished)!=0&&R(ErrorAt)==0&&R(SourcePage)!=0;
    private StepResult ReadStaged(Wide at,uint buffer,uint nextPhase)
    { int staged=FindStaged(at); return Issue(IoKind.Read,staged<0?at:Image(RW(Generation)+1,(uint)staged),buffer,P,nextPhase); }
    private StepResult ObjectStep()
    {
        uint p=Page(0);
        switch(R(Phase))
        {
            case 0: W(SourcePage,0); W(TreeDepth,0); W(ExpectedLevel,uint.MaxValue); return ReadStaged(Anchor+3*Q,p,1);
            case 1:
                if(!Control(p,3,Anchor+3*Q)||!ReadableGeneration(p)) return Fail(CpxError.Corrupt);
                WW(TreeAddress,GetWide(p+80)); W(Phase,2); return StepResult.Pending;
            case 2:
                if(R(TreeDepth)==8||!Allocatable(RW(TreeAddress))) return Fail(CpxError.Corrupt);
                W(TreeDepth,R(TreeDepth)+1); return ReadStaged(RW(TreeAddress),p,3);
            case 3:
                uint level=Read16(p+50),count=Read16(p+48),width=level==0?256u:16u;
                if(!Control(p,level==0?11u:12u,RW(TreeAddress))||!ReadableGeneration(p)||!GetWide(p+40).IsZero||level>=8||
                    (R(ExpectedLevel)!=uint.MaxValue&&level!=R(ExpectedLevel))||count>(P-64)/width||(level!=0&&count==0)||Read16(p+52)!=count*width) return Fail(CpxError.Corrupt);
                W(TreeCount,count); W(TreeLevel,level); W(ScanIndex,0); W(TreeSlot,0); W(Phase,4); return StepResult.Pending;
            case 4:
                uint size=R(TreeLevel)==0?256u:16u,index=R(ScanIndex),end=index+64;
                if(end>R(TreeCount)) end=R(TreeCount);
                for(;index<end;index++)
                {
                    Wide key=GetWide(p+64+index*size);
                    if(index>0&&Wide.Compare(GetWide(p+64+(index-1)*size),key)>=0) return Fail(CpxError.Corrupt);
                    if(R(TreeLevel)!=0)
                    { if(!Allocatable(GetWide(p+64+index*size+8))) return Fail(CpxError.Corrupt); if(Wide.Compare(key,RW(ObjectKey))<=0) W(TreeSlot,index); }
                    else if(Wide.Equal(key,RW(ObjectKey))) W(TreeSlot,index+1);
                }
                W(ScanIndex,index); if(index<R(TreeCount)) return StepResult.Pending;
                if(R(TreeLevel)!=0)
                { WW(TreeAddress,GetWide(p+64+R(TreeSlot)*size+8)); W(ExpectedLevel,R(TreeLevel)-1); W(Phase,2); return StepResult.Pending; }
                if(R(TreeSlot)!=0)
                { uint source=p+64+(R(TreeSlot)-1)*256; for(uint n=0;n<256;n++) memory.Write8(ObjectRecord+n,memory.Read8(source+n)); W(SourcePage,1); }
                return Done();
            default: return Fail(CpxError.Corrupt);
        }
    }
}
