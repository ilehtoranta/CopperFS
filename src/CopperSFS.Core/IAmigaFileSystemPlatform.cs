using Amiga;

namespace CopperSFS;

/// <summary>Caller-owned block I/O and guest memory for the SFS core.</summary>
public interface IAmigaFileSystemPlatform : IAmigaGuestMemory
{
    /// <summary>Optional 20-byte, initially cleared SFS mount record. The sole
    /// owner invalidates it and its file cursors before external access or a
    /// media/geometry change. Root block addresses are fixed on this format.</summary>
    APTR SfsMountStorage => APTR.Null;
    /// <summary>Optional single-owner SFS2 mount context. Null retains checked stateless APIs.
    /// An attached provider invalidates the owned cache before queuing physical writes,
    /// and suspends this context before geometry/media changes or external access.</summary>
    APTR Sfs2Context { get => APTR.Null; set { } }
    AmigaVolumeGeometry Geometry { get; }
    /// <summary>Stable mounted geometry scalars, identical to Geometry's fields.
    /// Native platforms can return them without constructing the device descriptor.</summary>
    uint BlockCount => Geometry.BlockCount;
    uint BlockBytes => Geometry.BlockBytes;
    uint ReservedBlocks => Geometry.ReservedBlocks;
    /// <summary>Stable allocation-placement hint, identical to Geometry.TrackBlocks.</summary>
    uint TrackBlocks => Geometry.TrackBlocks;
    ulong StartByte => Geometry.StartByte;
    bool ReadBlock(uint block, APTR destination);
    /// <summary>Physical metadata fill after the owned store resolved transaction
    /// images. Must observe pending physical writes; never consult legacy undo
    /// or mutation overlays. Default is for checked portable adapters.</summary>
    bool ReadSfs2PhysicalMetadata(uint block, APTR destination) => ReadBlock(block, destination);
    /// <summary>Optionally supply a logically current FFS metadata image.
    /// True means the platform handled the read; view is null on I/O failure.
    /// A borrowed view expires at the next cache or block access. The default
    /// makes no platform calls and leaves other implementations on ReadBlock.</summary>
    bool TryBorrowFfsMetadataBlock(uint block, APTR scratch, APTR output, uint outputBytes, out APTR view)
    {
        view = APTR.Null;
        return false;
    }
    /// <summary>Borrow a cached classic block with a verified zero-sum checksum.
    /// The view expires at the next block access. A handled checksum failure
    /// returns a null view. False requests an ordinary read and full validation.</summary>
    bool TryBorrowCheckedClassicBlock(uint block, APTR scratch, APTR output, uint outputBytes, out APTR view)
    {
        view = APTR.Null;
        return false;
    }
    /// <summary>Optional copy of a retained, logically current SFS2 metadata block
    /// verified for type, block number and version-4 checksum. False requests the
    /// ordinary read and validation path.</summary>
    bool TryReadVerifiedSfs2Block(uint block, uint id, APTR destination) => false;
    /// <summary>Optional logically current SFS2 cache image with verified type,
    /// own-block number and checksum. Read-only until the next block/cache
    /// access. Null requests an ordinary read into the supplied scratch.</summary>
    APTR BorrowVerifiedSfs2Block(uint block, uint id, APTR scratch) => APTR.Null;
    /// <summary>Optionally read a verified SFS2 metadata view, filling the cache on a miss.
    /// True means the read was handled (a null view denotes failure). The view expires
    /// at the next block access. False selects the retained-view or ordinary read path.</summary>
    bool TryReadSfs2MetadataView(uint block, uint id, APTR scratch, out APTR view)
    {
        view = APTR.Null;
        return false;
    }
    /// <summary>True only while reads on this mount overlay the complete acknowledged
    /// set of home blocks retained after an interrupted write-back checkpoint.</summary>
    bool HasAcknowledgedCheckpoint(uint rootContainer) => false;
    /// <summary>Optional read-only image already retained by the platform, or
    /// null when unavailable. Performs no block I/O; bytes must match ReadBlock,
    /// including pending writes. No checksum/type validation is implied. Consume
    /// the image before any subsequent block I/O or cache access can expire it.</summary>
    APTR ReadRetainedBlock(uint block) => APTR.Null;
    /// <summary>Read metadata and verify its type, own-block number and SFS checksum.
    /// A cache may retain this verification only while its private image is unchanged.</summary>
    bool ReadMetadataBlock(uint block, uint id, APTR destination);
    /// <summary>Verified read-only metadata, or null on failure. A platform may
    /// return scratch or a cache image. The view expires at the next block I/O;
    /// callers must never modify it or retain it across another block access.</summary>
    APTR ReadMetadataView(uint block, uint id, APTR scratch);
    /// <summary>The bulk reader accepts arbitrary byte-aligned caller buffers, including on overlay and DMA bounce paths.</summary>
    bool SupportsUnalignedDataReads => false;
    /// <summary>Read consecutive file-data blocks, returning the completed prefix.
    /// Reads must observe staged writes. Metadata uses the verified metadata methods.</summary>
    uint ReadDataBlocks(uint block, uint count, APTR destination);
    /// <summary>Read a run from a caller's larger streaming request. False
    /// suppresses speculative payload read-ahead for short run fragments;
    /// Platforms without that policy must explicitly forward to their ordinary
    /// transfer method. A default interface body would box mutable struct
    /// platforms and discard their state updates.</summary>
    uint ReadDataBlocks(uint block, uint count, APTR destination, bool readAhead);
}

public interface IAmigaWritableFileSystemPlatform : IAmigaFileSystemPlatform
{
    /// <summary>Sampled once per mutation, so disabled maintenance adds no
    /// per-block callbacks or range bookkeeping.</summary>
    bool TracksFreedRanges => false;
    /// <summary>Optional maintenance hint emitted when allocation metadata frees
    /// a range. Must be memory-only, non-failing and allocation-free. Deferred
    /// TRST can emit the hint on acceptance before materializing its bitmap.
    /// It is NOT a commit notification. The owner must recover, commit,
    /// flush and recheck current allocation before discarding. Losing hints is safe.</summary>
    void ObserveFreedRange(uint first, uint count) { }
    /// <summary>Optional caller-owned TRST overlay. Platforms supporting ExecuteTrst
    /// route all block reads/writes through AmigaSmartFileSystemRedoArena while set.</summary>
    APTR RedoOverlay { get => APTR.Null; set { } }
    /// <summary>Validate a range and optionally describe a larger contiguous
    /// mapped region containing it. Bounds are exclusive at the end, cannot
    /// wrap, and expire at any platform I/O or external call. The default
    /// certifies only the requested range; callers check other ranges normally.</summary>
    bool TryMappedRegion(APTR address, uint bytes, out uint lower, out uint upper)
    {
        lower = upper = address.Raw;
        if (address.Raw > uint.MaxValue - bytes || !IsMapped(address, bytes)) return false;
        upper += bytes;
        return true;
    }
    bool WriteProtected();
    /// <summary>Accept an owned copy outside the logical transaction overlays.
    /// An optional physical queue must provide read-your-writes and drain before
    /// Flush; writes from different flush intervals must never be combined.</summary>
    bool WriteBlock(uint block, APTR source);
    /// <summary>Accept consecutive blocks using the physical queue contract of
    /// WriteBlock. Does not flush or stage metadata. The caller must durably
    /// publish undo before overwriting reachable data or metadata.</summary>
    bool WriteDataBlocks(uint block, uint count, APTR source);
    /// <summary>Explicit opt-in to original SFS2-style file-data overwrites.
    /// Metadata remains journaled, but existing payload bytes are not rolled
    /// back after packet failure or a crash. The default retains full-data redo.</summary>
    bool UsesOriginalSfs2DataWrites => false;
    /// <summary>Consume a checked file-data run synchronously outside overlays,
    /// ordering overlapping physical writes or replacing an owned full-sector
    /// queue image while preserving read-your-writes. Requires the policy
    /// above; never use for metadata or privately staged replacement images.
    /// Flush still orders payload completion before metadata publication.</summary>
    bool WriteExistingFileDataBlocks(uint block, uint count, APTR source) => false;
    // A following OFS run may need to update the previous run's forward
    // link. Retain the last sector only after a complete successful write.
    bool WriteDataBlocksRetainingLast(uint block, uint count, APTR source) => WriteDataBlocks(block, count, source);
    /// <summary>Whether this platform implements direct unpublished-data writes.</summary>
    bool SupportsUnpublishedDataWrites => false;
    /// <summary>On a TRST encoding cache miss, optionally read the current
    /// original into private scratch. The following count-1 sectors are also
    /// journal targets. Success requires a complete read into count contiguous
    /// scratch blocks. The caller retries a failed prefetch one sector at a time.
    /// Decline when an overlay or queued write prevents physical access.</summary>
    bool TryReadTrstOriginal(uint block, uint count, APTR scratch, out bool success) { success = false; return false; }

    /// <summary>The direct unpublished transfer enforces device write protection
    /// before reporting success. A failed transfer may change only unpublished
    /// payload bytes, never the accepted logical view. This permits an established
    /// deferred run to rely on the write result rather than a separate status I/O.
    /// Platforms with additional software protection must keep the default.</summary>
    bool UnpublishedDataWritesEnforceProtection => false;
    /// <summary>Write data proven unreachable in the durable filesystem, outside
    /// all logical overlays. May bypass an owned-copy queue; consumes the source
    /// before returning. Preserve prior overlapping physical writes and require
    /// a later Flush before publishing metadata that makes this data reachable.</summary>
    bool WriteUnpublishedDataBlocks(uint block, uint count, APTR source) => false;
    /// <summary>Optional immediate writes for caller-retained journal/home images.
    /// The source is consumed before return, prior overlapping queued writes are
    /// ordered first, and Flush remains the durability barrier. The caller must
    /// publish its journal before writing any reachable home image.</summary>
    bool SupportsRetainedBlockWrites => false;
    /// <summary>Optional exclusive workspace for consecutive unpublished SFS journal
    /// pages. Its contents survive reads and writes until release. Capacity is in
    /// filesystem blocks. Return null to use the caller's single-page scratch.</summary>
    APTR BorrowSfsJournalPages(uint blockBytes, out uint capacity) { capacity = 0; return APTR.Null; }
    void ReleaseSfsJournalPages(APTR pages) { }
    bool WriteRetainedBlock(uint block, APTR source) => false;
    // The caller has sealed the classic block's zero-sum checksum. Only the
    // matching retained image may inherit that proof after successful I/O.
    bool WriteSealedClassicBlock(uint block, APTR source) =>
        SupportsRetainedBlockWrites ? WriteRetainedBlock(block, source) : WriteBlock(block, source);
    // Patch an allocated FFS payload sector through a private DMA-safe cache
    // slot. False requests the caller's ordinary read/modify/write path.
    bool TryPatchClassicSector(uint key, uint offset, APTR source, uint length, out DOS.Error error)
    { error = DOS.Error.None; return false; }
    // Patch a cached OFS tail after the successor data was written. An
    // unavailable cache slot leaves the ordinary read/modify/write to the core.
    bool TryPatchClassicOfsForwardLink(uint key, uint successor, out bool written)
    { written = false; return false; }
#if COPPERFS_SFS_POOL
    /// <summary>Whether the platform can preserve verification on a sealed redo home write.</summary>
    bool SupportsTrstHomeSlotWrites => false;
    /// <summary>Install the sealed slot after durable journal publication. A false result is a write failure.</summary>
    bool WriteTrstHomeSlot(APTR slot) => false;
#endif
    /// <summary>Maximum adjacent TRST home images the platform can gather using
    /// existing storage. Each entry in slots points at a redo slot, with its
    /// image at SlotHeaderBytes. Only called after durable journal publication.</summary>
    uint RetainedRunCapacity => 1;
    bool WriteRetainedRun(uint first, uint count, APTR slots) => false;
    /// <summary>Drain all accepted physical writes, then flush the device.
    /// Return false if either step fails; retain queued images on a failed drain.</summary>
    bool Flush();
}

/// <summary>Bounded range transfers used by the separately verified SFS2 engine.</summary>
public interface IAmigaBlockRangeFileSystemPlatform : IAmigaWritableFileSystemPlatform
{
    bool ReadBlocks(uint first, uint count, APTR destination);
    bool WriteBlocks(uint first, uint count, APTR source);
}
