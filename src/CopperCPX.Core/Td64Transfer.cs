namespace CopperCPX;

public struct DeviceRequest
{
    public uint Command, OffsetHigh, OffsetLow, ActualInput, Length, Buffer;
    public bool Bounced;
}

/// <summary>One outstanding, fully initialized TD64/narrow request at a time.
/// The adapter performs bounce copies and SendIO/WaitIO; this cursor owns bounds.</summary>
public struct Td64Transfer
{
    public static bool NarrowPartitionFits(Wide start, Wide sectors, int sectorShift)
    {
        return sectorShift >= 9 && sectorShift <= 12 &&
            Wide.Left(sectors, sectorShift, out var bytes) && Wide.Add(start, bytes, out var end) &&
            Wide.Compare(end, new Wide(1, 0)) <= 0;
    }
    private Wide next;
    private uint remaining, caller, maximum, mask, bounce, bounceBytes, sectorBytes, pending;
    private bool write, td64, failed;
    private DeviceRequest outstanding;
    public uint CompletedBytes;
    public CpxError Begin(Wide partitionBaseBytes,Wide partitionSectors,Wide relativeSector,int sectorShift,
        uint bytes,uint buffer,uint maximumTransfer,uint dmaMask,uint bounceAddress,uint bounceLength,bool writing,bool supportsTd64,bool forceBounce=false)
    {
        if(pending!=0) return CpxError.Busy;
        this=default; failed=true;
        if(sectorShift<9||sectorShift>12||bytes==0||buffer==0||bytes>uint.MaxValue-buffer||
            ((bytes|partitionBaseBytes.Low)&((1u<<sectorShift)-1))!=0||maximumTransfer<(1u<<sectorShift)||bounceLength<(1u<<sectorShift)||
            !Wide.Add(relativeSector,Wide.From(bytes>>sectorShift),out var end)||Wide.Compare(end,partitionSectors)>0||
            !Wide.Left(relativeSector,sectorShift,out var relative)||!Wide.Left(end,sectorShift,out var relativeEnd)||
            !Wide.Add(partitionBaseBytes,relativeEnd,out var absoluteEnd)||!Wide.Add(partitionBaseBytes,relative,out next)) return CpxError.InvalidArgument;
        if(!supportsTd64 && Wide.Compare(absoluteEnd,new Wide(1,0))>0) return CpxError.Unsupported;
        if(!DmaRange(bounceAddress,bounceLength,dmaMask)) return CpxError.InvalidArgument;
        remaining=bytes; caller=buffer; sectorBytes=1u<<sectorShift; maximum=maximumTransfer&~(sectorBytes-1);
        mask=dmaMask; bounce=bounceAddress; bounceBytes=bounceLength&~(sectorBytes-1); write=writing; td64=supportsTd64;
        alwaysBounce=forceBounce; failed=false; return CpxError.None;
    }
    public StepResult Next(out DeviceRequest request)
    {
        request=default;
        if(failed) return StepResult.Failed;
        if(pending!=0) { request=outstanding; return StepResult.NeedIo; }
        if(remaining==0) return StepResult.Complete;
        uint take=remaining<maximum?remaining:maximum;
        bool bounced=alwaysBounce||!DmaRange(caller,take,mask);
        if(bounced && take>bounceBytes) take=bounceBytes;
        // Legacy requests cannot cross 4 GiB; the full range was admitted above.
        if(!td64 && (next.High!=0 || !Wide.Add(next,Wide.From(take),out var end)||Wide.Compare(end,new Wide(1,0))>0))
        { failed=true; return StepResult.Failed; }
        pending=take;
        request=new() { Command=td64?(write?25u:24u):(write?3u:2u), OffsetHigh=next.High, OffsetLow=next.Low,
            ActualInput=td64?next.High:0, Length=take, Buffer=bounced?bounce:caller, Bounced=bounced };
        outstanding=request;
        return StepResult.NeedIo;
    }
    public CpxError Complete(uint actual,int error)
    {
        if(pending==0) return CpxError.StaleCompletion;
        uint wanted=pending; pending=0;
        if(error!=0||actual!=wanted) { failed=true; return CpxError.Io; }
        next+=wanted; caller+=wanted; remaining-=wanted; CompletedBytes+=wanted; return CpxError.None;
    }
    public uint CallerBuffer => caller;
    public bool IsPending => pending!=0;
    private bool alwaysBounce;
    public static bool DmaRange(uint address,uint length,uint dmaMask)
    {
        if(address==0||dmaMask==0||(address&1)!=0||(address&~dmaMask)!=0||length==0||length-1>uint.MaxValue-address) return false;
        uint end=address+length-1, differing=address^end;
        differing|=differing>>1; differing|=differing>>2; differing|=differing>>4; differing|=differing>>8; differing|=differing>>16;
        // Consecutive zero low bits constrain the start alignment. Higher
        // holes and the address ceiling must hold throughout the transfer.
        uint alignmentBits=(dmaMask&(0u-dmaMask))-1;
        return (differing&~dmaMask&~alignmentBits)==0;
    }
}
