using Amiga;

namespace CopperSFS;

/// <summary>Caller-owned block I/O and guest memory for the SFS core.</summary>
public interface IAmigaFileSystemPlatform : IAmigaGuestMemory
{
    AmigaVolumeGeometry Geometry { get; }
    bool ReadBlock(uint block, APTR destination);
}

public interface IAmigaWritableFileSystemPlatform : IAmigaFileSystemPlatform
{
    bool WriteProtected();
    bool WriteBlock(uint block, APTR source);
    bool Flush();
}

/// <summary>
/// Optional contiguous block transfers. Completion has the same visibility and
/// flush semantics as ReadBlock/WriteBlock; a failed write may have written any
/// prefix/subset of the range. Ranges are not assumed atomic. Adapters enforce
/// DMA/address constraints and may implement these methods using scalar I/O.
/// Successful reads fill the destination before returning; write sources may
/// be reused immediately on return, even when persistence awaits Flush.
/// </summary>
public interface IAmigaBlockRangeFileSystemPlatform : IAmigaWritableFileSystemPlatform
{
    bool ReadBlocks(uint first, uint count, APTR destination);
    bool WriteBlocks(uint first, uint count, APTR source);
}
