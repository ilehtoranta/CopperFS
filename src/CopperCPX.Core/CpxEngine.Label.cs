namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // Cache is a Latin-1 BSTR. Input is operation-owned, never a DOS pointer.
    // Input shares the mutually exclusive link-operation workspace.
    public uint VolumeLabelAddress=>Arena+3864;
    private uint LabelInput=>Arena+16000;
    private const uint LabelLength=16032;
    private const uint OwnershipFeature=496;
    public bool OwnershipEnabled=>R(OwnershipFeature)!=0;
    public static bool LabelCharacter(uint c)=>c>=32&&c!=127&&c!=47&&c!=58;
    public bool ValidLabelInput(uint address,uint length)
    {
        if(length==0||length>30||address==0||address>uint.MaxValue-length) return false;
        for(uint n=0;n<length;n++) if(!LabelCharacter(memory.Read8(address+n))) return false;
        return true;
    }
    private void DefaultLabel()
    {
        Clear(VolumeLabelAddress,32); memory.Write8(VolumeLabelAddress,10);
        memory.Write8(VolumeLabelAddress+1,67); memory.Write32(VolumeLabelAddress+2,0x6f707065);
        memory.Write8(VolumeLabelAddress+6,114); memory.Write8(VolumeLabelAddress+7,67); memory.Write8(VolumeLabelAddress+8,80);
        memory.Write8(VolumeLabelAddress+9,88); memory.Write8(VolumeLabelAddress+10,52);
    }
    // State payload 48 is the legacy unlabeled representation. LBL1 is a
    // strict, fixed-size extension; old readers reject payload 128.
    private bool ValidStateLabel(uint p)
    {
        if(Read16(p+52)==48) return true;
        uint payload=Read16(p+52);
        if(payload!=128&&payload!=136||memory.Read32(p+112)!=0x4c424c31||Read16(p+118)!=1) return false;
        if(payload==136&&(memory.Read32(p+192)!=0x4f574e31||memory.Read32(p+196)!=1)) return false;
        uint bytes=Read16(p+116),characters=0;
        if(bytes==0||bytes>60) return false;
        for(uint n=0;n<bytes;n++)
        {
            uint c=memory.Read8(p+120+n);
            if(c>=128)
            {
                if(c is not (0xc2 or 0xc3)||++n==bytes) return false;
                uint next=memory.Read8(p+120+n); if(next<128||next>191) return false;
                c=((c&3)<<6)|(next&63);
            }
            if(!LabelCharacter(c)||++characters>30) return false;
        }
        for(uint n=bytes;n<72;n++) if(memory.Read8(p+120+n)!=0) return false;
        return true;
    }
    private void CacheStateLabel(uint p)
    {
        W(OwnershipFeature,Read16(p+52)==136?1u:0u);
        if(Read16(p+52)==48) { DefaultLabel(); return; }
        Clear(VolumeLabelAddress,32); uint count=0,bytes=Read16(p+116);
        for(uint n=0;n<bytes;n++)
        {
            uint c=memory.Read8(p+120+n);
            if(c>=128) c=((c&3)<<6)|((uint)memory.Read8(p+120+(++n))&63);
            memory.Write8(VolumeLabelAddress+1+count++,(byte)c);
        }
        memory.Write8(VolumeLabelAddress,(byte)count);
    }
    private void EncodeStateLabel(uint p)
    {
        Clear(p+112,80); memory.Write32(p+112,0x4c424c31); Write16(p+118,1); uint bytes=0;
        for(uint n=0;n<R(LabelLength);n++)
        {
            uint c=memory.Read8(LabelInput+n);
            if(c>=128) { memory.Write8(p+120+bytes++,(byte)(0xc0|(c>>6))); c=0x80|(c&63); }
            memory.Write8(p+120+bytes++,(byte)c);
        }
        Write16(p+116,bytes); if(Read16(p+52)!=136) Write16(p+52,128);
    }
    private void CacheInputLabel()
    { Clear(VolumeLabelAddress,32); memory.Write8(VolumeLabelAddress,(byte)R(LabelLength)); Copy(VolumeLabelAddress+1,LabelInput,R(LabelLength)); }
    public CpxError BeginLabel(uint latin1,uint length)
    {
        if(!ValidLabelInput(latin1,length)) return CpxError.InvalidArgument;
        CpxError error=StartOperation(CpxOperation.SetLabel); if(error!=CpxError.None) return error;
        Copy(LabelInput,latin1,length); W(LabelLength,length); return error;
    }
    public CpxError BeginReformat(uint latin1,uint length)
    {
        if(IsReadOnly) return CpxError.ReadOnly;
        if(!Wide.Add(RW(Identity),Wide.From(1),out var identity)||R(MountEpoch)==uint.MaxValue) return CpxError.ResourceExhausted;
        return BeginFormat(1u<<(int)R(SectorShift),1u<<(int)R(UnitShift),RW(Capacity),identity,R(MountEpoch)+1,latin1,length);
    }
    private StepResult LabelStep()
    {
        uint p=Page(4);
        switch(R(Phase))
        {
            case 0: return BeginTx(1,1);
            case 1: return ReadStaged(StatePageAddress,p,2);
            case 2:
                if(!Control(p,3,StatePageAddress)||!ReadableGeneration(p)||!ValidStateLabel(p)) return Fail(CpxError.Corrupt);
                EncodeStateLabel(p); return StagePage(p,StatePageAddress,3);
            case 3: return Call(CpxOperation.CommitTransaction,4);
            case 4: CacheInputLabel(); return Done();
            default: return Fail(CpxError.Corrupt);
        }
    }
}
