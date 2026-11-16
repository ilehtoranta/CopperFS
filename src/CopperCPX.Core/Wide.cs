using CopperFS.Prototype;

namespace CopperCPX;
/// <summary>Unsigned on-disk scalar. No native ulong arithmetic or hidden division.</summary>
public struct Wide
{
    public uint High, Low;
    public Wide(uint high, uint low)
    {
        High = high;
        Low = low;
    }

    public static Wide From(uint low) => new(0, low);
    public static int Compare(Wide a, Wide b)
    {
        if (a.High != b.High)
            return a.High < b.High ? -1 : 1;
        return a.Low == b.Low ? 0 : a.Low < b.Low ? -1 : 1;
    }

    public static bool Add(Wide a, Wide b, out Wide value)
    {
        uint low = unchecked(a.Low + b.Low), carry = low < a.Low ? 1u : 0u, sum = unchecked(a.High + b.High), high = unchecked(sum + carry);
        value = new(high, low);
        return sum >= a.High && high >= sum;
    }

    public static bool Subtract(Wide a, Wide b, out Wide value)
    {
        value = new(unchecked(a.High - b.High - (a.Low < b.Low ? 1u : 0u)), unchecked(a.Low - b.Low));
        return Compare(a, b) >= 0;
    }

    public static bool Left(Wide a, int shift, out Wide value)
    {
        if (shift < 0 || shift >= 64)
        {
            value = default;
            return false;
        }

        if (shift == 0)
        {
            value = a;
            return true;
        }

        if (shift >= 32)
        {
            int extra = shift - 32;
            value = new(a.Low << extra, 0);
            return a.High == 0 && (extra == 0 || (a.Low >> (32 - extra)) == 0);
        }

        value = new((a.High << shift) | (a.Low >> (32 - shift)), a.Low << shift);
        return (a.High >> (32 - shift)) == 0;
    }

    public static Wide Right(Wide a, int shift)
    {
        if (shift <= 0)
            return a;
        if (shift >= 64)
            return default;
        if (shift >= 32)
            return new(0, a.High >> (shift - 32));
        return new(a.High >> shift, (a.Low >> shift) | (a.High << (32 - shift)));
    }

    public static Wide operator +(Wide a, uint b)
    {
        uint low = unchecked(a.Low + b);
        return new(unchecked(a.High + (low < a.Low ? 1u : 0u)), low);
    }

    public static Wide operator -(Wide a, uint b)
    {
        return new(unchecked(a.High - (a.Low < b ? 1u : 0u)), unchecked(a.Low - b));
    }

    public static bool Equal(Wide a, Wide b) => a.High == b.High && a.Low == b.Low;
    public static Wide Multiply(uint a, uint b)
    {
        uint low = (a & 65535) * (b & 65535), high = (a >> 16) * (b >> 16);
        uint middle1 = (a >> 16) * (b & 65535), middle2 = (a & 65535) * (b >> 16);
        Add(new(high, low), new(middle1 >> 16, middle1 << 16), out var first);
        Add(first, new(middle2 >> 16, middle2 << 16), out var value);
        return value;
    }

    public static implicit operator ulong (Wide value) => ((ulong)value.High << 32) | value.Low;
    public static implicit operator Wide(ulong value) => new((uint)(value >> 32), (uint)value);
    public bool IsZero => (High | Low) == 0;
}

public interface ICpxMemory
{
    byte Read8(uint address);
    void Write8(uint address, byte value);
    uint Read16(uint address);
    void Write16(uint address, uint value);
    uint Read32(uint address);
    void Write32(uint address, uint value);
}

public enum StepResult : uint
{
    Pending,
    NeedIo,
    Complete,
    Failed
}

public enum CpxError : uint
{
    None,
    Busy,
    InvalidArgument,
    Io,
    Corrupt,
    Unsupported,
    ReadOnly,
    NotMounted,
    StaleCompletion,
    Cancelled,
    ResourceExhausted,
    TransactionLimit,
    NotFound,
    WrongType,
    Protected,
    DiskFull,
    ReserveExhausted,
    AlreadyExists,
    DirectoryNotEmpty,
    TreeTooDeep
}

public enum IoKind : uint
{
    Read,
    Write,
    Flush,
    Discard,
    QueryDiscard
}

public enum CpxOperation : uint
{
    Mount = 1,
    Flush = 2,
    ReadMetadata = 3,
    Recover = 4,
    BeginTransaction = 5,
    StageMetadata = 6,
    CommitTransaction = 7,
    AbortTransaction = 8,
    ReadObject = 9,
    WriteInline = 10,
    ResizeInline = 11,
    SetProtection = 12,
    SetDate = 13,
    Trim = 14,
    ReadTree = 15,
    ReadFile = 16,
    WriteMapped = 17,
    Lookup = 18,
    TrimFree = 19,
    Create = 20,
    Delete = 21,
    Rename = 22,
    Resize = 23,
    Reclaim = 24,
    TreeMutation = 25,
    MetadataAllocation = 26,
    ChangeAllocation = 27,
    SaveObject = 28,
    Refill = 29,
    PruneTree = 30,
    PruneObjects = 31,
    PublishMetadata = 32,
    SetComment = 33,
    PrepareWriteGap = 34,
    Format = 35,
    CreateHardLink = 36,
    ReadSymlink = 37,
    EncodeSymlink = 38,
    SetLabel = 39,
    SetOwner = 40,
    ManualTrim = 41
}

public enum CpxTreeKind : uint
{
    Object,
    Directory,
    Extent
}

public enum CpxSeek : uint
{
    Exact,
    Floor,
    Next,
    After
}

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
    public const uint MountBytes = 8192, OperationBytes = 8192, PoolBytes = 16384;
    // Recovery and staging use 64 32-byte descriptors after the six pages.
    public const uint IndexBytes = 64 * 32;
    public const uint Scratch = MountBytes + OperationBytes;
    public const uint Magic = 0x43505834, Version = 4, Images = 64, TransactionImages = 42, MaxHeight = 8;
    public const uint CrcTableBytes = 4096;
    public static uint Bytes(uint pageBytes) => Scratch + 6 * pageBytes + IndexBytes + PoolBytes + CrcTableBytes;
    public static bool ValidUnit(uint n) => n >= 512 && n <= 4096 && (n & (n - 1)) == 0;
    public static int Shift(uint n)
    {
        int shift = 0;
        while (n > 1)
        {
            n >>= 1;
            shift++;
        }

        return shift;
    }
}