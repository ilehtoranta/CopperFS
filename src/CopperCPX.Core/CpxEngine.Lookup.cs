namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    private uint LegacyName => ObjectRecord+1024;
    public bool LookupFound => R(LookupFoundAt)!=0;
    public Wide LookupId => RW(LookupTarget);
    public CpxError BeginLookup(Wide parent,uint name,uint length)
    {
        if(length==0||length>255||name>uint.MaxValue-length) return CpxError.InvalidArgument;
        for(uint n=0;n<length;n++) if(memory.Read8(name+n) is 0 or 47 or 58) return CpxError.InvalidArgument;
        CpxError error=StartOperation(CpxOperation.Lookup,parent); if(error!=CpxError.None) return error;
        W(NameLength,length); W(LookupFoundAt,0); WW(LookupTarget,default);
        for(uint n=0;n<length;n++) memory.Write8(LegacyName+n,memory.Read8(name+n));
        return CpxError.None;
    }
    private static uint FoldLatin(uint c) => c is >=65 and <=90 or >=192 and <=214 or >=216 and <=222?c+32:c;
    private bool IsAlias(out Wide id)
    {
        id=default;
        if(R(NameLength)!=19||memory.Read8(LegacyName)!=95||memory.Read8(LegacyName+1)!=95||FoldLatin(memory.Read8(LegacyName+2))!=117) return false;
        for(uint n=3;n<19;n++)
        {
            uint c=FoldLatin(memory.Read8(LegacyName+n)),digit=c is >=48 and <=57?c-48:c is >=97 and <=102?c-87:16;
            if(digit==16) return false;
            Wide.Left(id,4,out id); id.Low|=digit;
        }
        return true;
    }
    private bool SameLegacy(uint utf8,uint length)
    {
        uint index=0,legacy=0; bool same=true;
        while(index<length)
        {
            uint c=memory.Read8(utf8+index++),continuations=0,minimum=0;
            if(c>=128)
            {
                if(c is >=194 and <=223) { c&=31; continuations=1; minimum=128; }
                else if(c is >=224 and <=239) { c&=15; continuations=2; minimum=2048; }
                else if(c is >=240 and <=244) { c&=7; continuations=3; minimum=65536; }
                else return false;
                if(continuations>length-index) return false;
                for(uint n=0;n<continuations;n++) { uint next=memory.Read8(utf8+index++); if((next&192)!=128) return false; c=(c<<6)|(next&63); }
                if(c<minimum||c>0x10ffff||c is >=0xd800 and <=0xdfff) return false;
            }
            if(c is 0 or 47 or 58) return false;
            if(legacy>=R(NameLength)||FoldLatin(c)!=FoldLatin(memory.Read8(LegacyName+legacy))) same=false;
            legacy++;
        }
        return same&&legacy==R(NameLength);
    }
    private StepResult LookupStep()
    {
        uint phase=R(Phase);
        if(phase<100)
        {
            StepResult lookup=ObjectStep(); if(lookup!=StepResult.Complete) return lookup;
            W(Finished,0); if(R(SourcePage)==0) return Reject(CpxError.NotFound);
            if(memory.Read8(ObjectRecord+84)!=2) return Reject(CpxError.WrongType);
            WW(DirectoryRoot,GetWide(ObjectRecord+32)); WW(NameCursorHigh,default); WW(NameCursorLow,default);
            W(Phase,100); return StepResult.Pending;
        }
        if(phase>=200)
        {
            W(Phase,phase-200); StepResult seek=SeekStep(); W(Phase,R(Phase)+200);
            if(seek==StepResult.Complete) { W(Finished,0); W(Phase,102); return StepResult.Pending; } return seek;
        }
        switch(phase)
        {
            case 100: case 101:
                StartSeek(CpxTreeKind.Directory,RW(DirectoryRoot),RW(ObjectKey),RW(NameCursorHigh),RW(NameCursorLow),phase==100?CpxSeek.Next:CpxSeek.After);
                W(Phase,200); return StepResult.Pending;
            case 102:
                if(!TreeFound) return Done();
                WW(NameCursorHigh,GetWide(TreeRecord)); WW(NameCursorLow,GetWide(TreeRecord+8));
                if(IsAlias(out var alias))
                {
                    if(Wide.Equal(alias,GetWide(TreeRecord+8))) { WW(LookupTarget,GetWide(TreeRecord+16)); W(LookupFoundAt,1); return Done(); }
                    W(Phase,101); return StepResult.Pending;
                }
                uint length=Read16(TreeRecord+32); Wide external=GetWide(TreeRecord+24);
                if(length==0||length>768) return Fail(CpxError.Corrupt);
                if(external.IsZero)
                {
                    if(length>80) return Fail(CpxError.Corrupt);
                    if(SameLegacy(TreeRecord+48,length)) { WW(LookupTarget,GetWide(TreeRecord+16)); W(LookupFoundAt,1); return Done(); }
                    W(Phase,101); return StepResult.Pending;
                }
                if(!Allocatable(external)) return Fail(CpxError.Corrupt);
                return ReadStaged(external,Page(4),103);
            case 103:
                uint p=Page(4),len=Read16(TreeRecord+32);
                if(!ValidPage(p,GetWide(TreeRecord+24))||!ReadableGeneration(p)||Read16(p+4)!=17||!Wide.Equal(GetWide(p+24),GetWide(TreeRecord+8))||Read16(p+64)!=len||Read16(p+66)!=0||Read16(p+52)!=len+4) return Fail(CpxError.Corrupt);
                if(SameLegacy(p+68,len)) { WW(LookupTarget,GetWide(TreeRecord+16)); W(LookupFoundAt,1); return Done(); }
                W(Phase,101); return StepResult.Pending;
            default: return Fail(CpxError.Corrupt);
        }
    }
}
