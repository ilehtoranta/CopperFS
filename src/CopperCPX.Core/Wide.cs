using CopperFS.Prototype;

namespace CopperCPX;

/// <summary>Unsigned on-disk scalar. No native ulong arithmetic or hidden division.</summary>
public struct Wide
{
    public uint High, Low;
    public Wide(uint high, uint low) { High=high; Low=low; }
    public static Wide From(uint low) => new(0,low);
    public static int Compare(Wide a, Wide b) => WideMath32.Compare(a.High,a.Low,b.High,b.Low);
    public static bool Add(Wide a, Wide b, out Wide value)
    { bool ok=WideMath32.Add(a.High,a.Low,b.High,b.Low,out uint h,out uint l); value=new(h,l); return ok; }
    public static bool Subtract(Wide a, Wide b, out Wide value)
    { bool ok=WideMath32.Subtract(a.High,a.Low,b.High,b.Low,out uint h,out uint l); value=new(h,l); return ok; }
    public static bool Left(Wide a,int shift,out Wide value)
    { bool ok=WideMath32.ShiftLeft(a.High,a.Low,shift,out uint h,out uint l); value=new(h,l); return ok; }
    public static Wide Right(Wide a,int shift)
    { WideMath32.ShiftRight(a.High,a.Low,shift,out uint h,out uint l); return new(h,l); }
    public static Wide operator +(Wide a,uint b) { Add(a,From(b),out var v); return v; }
    public static Wide operator -(Wide a,uint b) { Subtract(a,From(b),out var v); return v; }
    public static bool Equal(Wide a,Wide b) => a.High==b.High && a.Low==b.Low;
    public static Wide Multiply(uint a,uint b)
    {
        uint low=(a&65535)*(b&65535),high=(a>>16)*(b>>16);
        uint middle1=(a>>16)*(b&65535),middle2=(a&65535)*(b>>16);
        Add(new(high,low),new(middle1>>16,middle1<<16),out var first);
        Add(first,new(middle2>>16,middle2<<16),out var value); return value;
    }
    public bool IsZero => (High|Low)==0;
}

public interface ICpxMemory
{
    byte Read8(uint address);
    void Write8(uint address,byte value);
    uint Read32(uint address);
    void Write32(uint address,uint value);
}

public enum StepResult : uint { Pending, NeedIo, Complete, Failed }
public enum CpxError : uint { None, Busy, InvalidArgument, Io, Corrupt, Unsupported, ReadOnly, NotMounted, StaleCompletion, Cancelled, ResourceExhausted, TransactionLimit, NotFound, WrongType, Protected, DiskFull, ReserveExhausted, AlreadyExists, DirectoryNotEmpty, TreeTooDeep }
public enum IoKind : uint { Read, Write, Flush, Discard, QueryDiscard }
public enum CpxOperation : uint { Mount=1, Flush=2, ReadMetadata=3, Recover=4, BeginTransaction=5, StageMetadata=6, CommitTransaction=7, AbortTransaction=8, ReadObject=9, WriteInline=10, ResizeInline=11, SetProtection=12, SetDate=13, Trim=14, ReadTree=15, ReadFile=16, WriteMapped=17, Lookup=18, TrimFree=19, Create=20, Delete=21, Rename=22, Resize=23, Reclaim=24, TreeMutation=25, MetadataAllocation=26, ChangeAllocation=27, SaveObject=28, Refill=29, PruneTree=30, PruneObjects=31, PublishMetadata=32, SetComment=33, PrepareWriteGap=34, Format=35, CreateHardLink=36, ReadSymlink=37, EncodeSymlink=38, SetLabel=39, SetOwner=40, ManualTrim=41 }
public enum CpxTreeKind : uint { Object, Directory, Extent }
public enum CpxSeek : uint { Exact, Floor, Next, After }

/// <summary>Partition-relative sector request. Buffer is caller arena address.</summary>
public struct CpxIo
{
    public IoKind Kind;
    public Wide Sector;
    public uint Buffer, Bytes, Token;
}

/// <summary>Stable byte offsets, shared by host tests and the native consumer.</summary>
public static class CpxArena
{
    public const uint MountBytes=8192, OperationBytes=8192, PoolBytes=16384;
    // Recovery and staging use 64 32-byte descriptors after the six pages.
    public const uint IndexBytes=64*32;
    public const uint Scratch=MountBytes+OperationBytes;
    public const uint Magic=0x43505834, Version=4, Images=64, TransactionImages=42, MaxHeight=8;
    public static uint Bytes(uint pageBytes) => Scratch+6*pageBytes+IndexBytes+PoolBytes;
    public static bool ValidUnit(uint n) => n>=512 && n<=4096 && (n&(n-1))==0;
    public static int Shift(uint n) { int shift=0; while(n>1) { n>>=1; shift++; } return shift; }
}
