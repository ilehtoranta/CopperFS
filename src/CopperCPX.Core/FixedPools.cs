namespace CopperCPX;

public enum ResourceKind : uint { Handle, Lock, Enumeration, Notification, RecordLock, RecordWait }

/// <summary>Caller-owned bounded resources. Generation tokens reject stale releases.</summary>
public struct FixedPools<TMemory> where TMemory : struct, ICpxMemory
{
    private TMemory memory;
    public uint Arena;
    // Slot headers are 8 bytes: state and generation. Total 16 KiB, including names.
    public FixedPools(TMemory memory,uint arena) { this.memory=memory; Arena=arena; }
    private static uint Count(ResourceKind kind) => kind switch { ResourceKind.Handle or ResourceKind.Lock or ResourceKind.RecordLock=>32, ResourceKind.Enumeration or ResourceKind.RecordWait=>16, ResourceKind.Notification=>8, _=>0 };
    private static uint Width(ResourceKind kind) => kind switch { ResourceKind.Handle=>64,ResourceKind.Lock=>48,ResourceKind.Enumeration=>64,ResourceKind.Notification=>896,ResourceKind.RecordLock=>80,ResourceKind.RecordWait=>64,_=>0 };
    private static uint Offset(ResourceKind kind) => kind switch { ResourceKind.Handle=>0,ResourceKind.Lock=>2048,ResourceKind.Enumeration=>3584,ResourceKind.Notification=>4608,ResourceKind.RecordLock=>11776,ResourceKind.RecordWait=>14336,_=>16384 };
    public void Initialize() { for(uint n=0;n<16384;n+=4) memory.Write32(Arena+n,0); }
    public CpxError Take(ResourceKind kind,out uint address,out uint token)
    {
        address=token=0; uint count=Count(kind),width=Width(kind),start=Arena+Offset(kind);
        for(uint n=0;n<count;n++)
        {
            uint p=start+n*width; if(memory.Read32(p)!=0) continue;
            uint generation=memory.Read32(p+4)+1; if(generation==0) continue; // permanently retire wrapped slot
            for(uint i=8;i<width;i+=4) memory.Write32(p+i,0);
            memory.Write32(p,1); memory.Write32(p+4,generation); address=p+8; token=generation; return CpxError.None;
        }
        return CpxError.ResourceExhausted;
    }
    public CpxError Release(ResourceKind kind,uint address,uint token)
    {
        uint start=Arena+Offset(kind),width=Width(kind),count=Count(kind);
        if(width==0||address<start+8||address>=start+count*width+8) return CpxError.InvalidArgument;
        // Count <=32: bounded comparison avoids division for non-power-of-two slots.
        for(uint n=0;n<count;n++)
        {
            uint p=start+n*width; if(address!=p+8) continue;
            if(memory.Read32(p)!=1||memory.Read32(p+4)!=token) return CpxError.InvalidArgument;
            memory.Write32(p,0); return CpxError.None;
        }
        return CpxError.InvalidArgument;
    }
}
