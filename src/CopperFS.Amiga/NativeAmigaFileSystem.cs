using Amiga;
#if !COPPERSTART_SFS_STANDALONE
using CopperStart.Exec;
#endif
using System.Runtime.CompilerServices;

namespace CopperStart.Dos;

/// <summary>Exec-owned DOS packet handler over an Exec block device.</summary>
public static partial class NativeAmigaFileSystem
{
#if COPPERSTART_SFS_STANDALONE && COPPERSTART_SFS_TRST_WRITES && !COPPERSTART_NATIVE_SFS2
    public const uint StateBytes = 2452;
    // One bit and reference count for each hashed 64-block cache page. Hash
    // collisions only cause a normal cache scan, never a false absence result.
    internal const int CachePageMaskOffset = 2320, CachePageCountsOffset = 2324;
#else
    public const uint StateBytes = 2320;
#endif
    internal const int SmartReservationsOffset = 2052;
    internal const int SmartRedoOverlayOffset = 2048;
    internal const int SmartRangeBufferOffset = 2044;
#if COPPERSTART_SFS_TRST_WRITES || COPPERSTART_SFS2_TRST_WRITES
    // One arena per mount, allocated lazily. Exhaustion never splits a packet.
    internal const uint SmartRangeBufferBytes = 192 * 1024;
#else
    internal const uint SmartRangeBufferBytes = 65536;
#endif
    internal const int SmartWriteCombinerOffset = 2036;
#if COPPERSTART_SFS_STANDALONE && COPPERSTART_SFS_TRST_WRITES && !COPPERSTART_NATIVE_SFS2
    // TRST sends retained home images and unpublished runs immediately. Only
    // isolated marker writes use this queue; a single owned slot is enough.
    // Keep the queue so dirty tracking and flush elision retain their contract.
    internal const uint SmartWriteQueueCapacity = 1;
#else
    internal const uint SmartWriteQueueCapacity = AmigaFileSystemWriteCombiner.MaximumCapacity;
#endif
    internal const int SmartDeviceDirtyOffset = 2040;
    internal const int SmartEnumerationCacheOffset = 2032;
    internal const uint SmartEnumerationEntryBytes = 16 + FileInfoBlock.SizeInBytes;
    internal const uint SmartEnumerationCacheBytes = 4 + 32 * SmartEnumerationEntryBytes;
    internal const int SmartWriteBufferOffset = 2020, SmartTimerOffset = 2024, SmartTimerActiveOffset = 2028;
    public const int RequestOffset = 4, CacheOffset = 8, CacheTrackOffset = 12;
    public const int DosTypeOffset = 20, PortOffset = 24, VolumeOffset = 28;
    internal const int ObjectsOffset = 32, IoPortOffset = 36;
    // Zero for trackdisk mounts. A Shell RAM mount owns a sparse table of
    // sector pointers instead, using the same packet and filesystem semantics.
    public const int RamBlocksOffset = 40;
    public const int ExtraCacheHeadOffset = 44, ExtraCacheCountOffset = 48;
    internal const uint ExtraCacheHeaderBytes = 20;
    internal const int ExtraCacheHashOffset = 1760, ExtraCacheTailOffset = 2016;
    public static uint ExtraCacheBytes(APTR state) => APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockBytesOffset) + ExtraCacheHeaderBytes;
    public const int MetadataOffset = 64;
    internal const int JournalScratchOffset = 52;
    internal const int SmartRecoveryPendingOffset = 56;
    internal const int SmartCreationDateOffset = 60;
    internal const int GeometryOffset = 1600, BuffersOffset = 1648, IoBufferOffset = 1652, CacheBlocksOffset = 1656, CacheBytesOffset = 1660;
    internal const int ExtensionScratchOffset = 1664;
    internal const int InhibitedOffset = 1744;
    internal const int SmartJournalCursorOffset = 1748, SmartAllocationCursorOffset = 1752;
    internal const int SmartTransactionBufferOffset = 1756;
    public static APTR Metadata(APTR state) => APTR.ReadUInt32(state, NativeFileSystemStateCodec.BuffersOffset) == 0 ? APTR.FromPointer(state.Raw + MetadataOffset) : APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.BuffersOffset));
    public static APTR Data(APTR state) => APTR.FromPointer(Metadata(state).Raw + APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockBytesOffset));
    public static APTR Bitmap(APTR state) => APTR.FromPointer(Metadata(state).Raw + APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockBytesOffset) * 2);
    public static void CopyVolumeName(APTR state, APTR destination)
    {
        var metadata = Metadata(state);
        if (Smart(state))
        {
            var offset = NativeSfs2Bridge.IsSfs2(state) ? 51 : 49;
            var length = 0;
            while (length < 107 && APTR.ReadUInt8(metadata, offset + length) != 0)
            { APTR.WriteUInt8(destination, length, APTR.ReadUInt8(metadata, offset + length)); length++; }
            APTR.WriteUInt8(destination, length, 0);
        }
        else
        {
            var offset = ReadGeometry(state).Tail + 432;
            var length = APTR.ReadUInt8(metadata, offset);
            for (var i = 0; i < length; i++) APTR.WriteUInt8(destination, i, APTR.ReadUInt8(metadata, offset + 1 + i));
            APTR.WriteUInt8(destination, length, 0);
        }
    }
    public static void CopyVolumeDate(APTR state, APTR volume)
    {
        if (!Smart(state))
        {
            var root = Metadata(state);
            var tail = (int)APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockBytesOffset) - 512;
            for (var i = 0; i < 12; i += 4) APTR.WriteUInt32(volume, 16 + i, APTR.ReadUInt32(root, tail + 484 + i));
            return;
        }
        // The root block's creation date, unlike the root object's modification
        // date, remains stable when the volume is changed or remounted.
        var seconds = APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartCreationDateOffset);
        APTR.WriteUInt32(volume, 16, seconds / 86400);
        APTR.WriteUInt32(volume, 20, (seconds % 86400) / 60);
        APTR.WriteUInt32(volume, 24, (seconds % 60) * 50);
    }
    public static AmigaVolumeGeometry ReadGeometry(APTR state) => new NativeFileSystemPlatform(state).Geometry;
    public static void SetGeometry(APTR state, AmigaVolumeGeometry g)
    {
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.BlockCountOffset, g.BlockCount); APTR.WriteUInt32(state, NativeFileSystemStateCodec.BlockBytesOffset, g.BlockBytes);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.ReservedBlocksOffset, g.ReservedBlocks); APTR.WriteUInt32(state, NativeFileSystemStateCodec.SectorBytesOffset, g.SectorBytes);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.StartByteHighOffset, (uint)(g.StartByte >> 32)); APTR.WriteUInt32(state, NativeFileSystemStateCodec.StartByteLowOffset, (uint)g.StartByte);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.TrackBlocksOffset, g.TrackBlocks); APTR.WriteUInt32(state, NativeFileSystemStateCodec.MaxTransferOffset, g.MaxTransfer);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.BufferMemoryTypeOffset, g.BufferMemoryType); APTR.WriteUInt32(state, NativeFileSystemStateCodec.MaskOffset, g.Mask);
    }
    private const uint ObjectBytes = 216, Magic = 0x41465331;
    internal const int SmartReadCursorOffset = 56;
    private static bool Smart(APTR state) => APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset) is AmigaSmartFileSystemCore.DosType or NativeSfs2Bridge.DosType;
    private static uint RootKey(APTR state) => Smart(state) ? AmigaSmartFileSystemCore.RootNode : ReadGeometry(state).RootBlock;
    private static bool SmartMutation(DosPacketAction action) => action is
        DosPacketAction.FindOutput or DosPacketAction.FindUpdate or DosPacketAction.Write or
        DosPacketAction.SetFileSize or DosPacketAction.DeleteObject or DosPacketAction.RenameObject or
        DosPacketAction.CreateDirectory or DosPacketAction.SetProtection or
        DosPacketAction.SetComment or DosPacketAction.SetDate or (DosPacketAction)DosNativeExtendedPacketAction.SetFileSize64;
    private static bool SmartMediaAccess(DosPacketAction action) => action is not
        (DosPacketAction.End or DosPacketAction.FreeLock or DosPacketAction.RemoveNotify or
         DosPacketAction.IsFileSystem or DosPacketAction.CurrentVolume or DosPacketAction.DiskType or
         DosPacketAction.MoreCache or DosPacketAction.LockRecord or DosPacketAction.FreeRecord or
         (DosPacketAction)DosNativeExtendedPacketAction.LockRecord64 or
         (DosPacketAction)DosNativeExtendedPacketAction.FreeRecord64);

    public static APTR Create(APTR deviceName) => CreateUnit(deviceName, 0);
    public static APTR CreateUnit(APTR deviceName, uint unit) => CreateMount(deviceName, unit, 0, APTR.Null);

    // The startup message uses BPTRs for its device BSTR and environment vector.
    public static APTR CreateStartup(APTR startup)
    {
        if (startup.IsNull) return APTR.Null;
        var name = APTR.FromPointer(APTR.ReadUInt32(startup, 4) << 2);
        if (name.IsNull) return APTR.Null;
        var length = APTR.ReadUInt8(name, 0);
        var text = APTR.FromPointer(global::Amiga.Exec.AllocMem((uint)length + 1, global::Amiga.Exec.MemoryFlags.Public));
        if (text.IsNull) return APTR.Null;
        for (var i = 0; i < length; i++) APTR.WriteUInt8(text, i, APTR.ReadUInt8(name, i + 1));
        APTR.WriteUInt8(text, length, 0);
        var state = CreateMount(text, APTR.ReadUInt32(startup, 0), APTR.ReadUInt32(startup, 12),
            APTR.FromPointer(APTR.ReadUInt32(startup, 8) << 2));
        global::Amiga.Exec.FreeMem(text.Raw, (uint)length + 1);
        return state;
    }

    /// <summary>Populate an owned whole-drive DosEnvec from the device, before publishing its BootNode.</summary>
    public static bool DriveEnvironment(APTR deviceName, uint unit, APTR environment)
    {
        var port = global::Amiga.Exec.CreateMsgPort();
        if (port.IsNull) return false;
        var request = global::Amiga.Exec.CreateIORequest(port, 56);
        var probe = APTR.FromPointer(global::Amiga.Exec.AllocMem(32, global::Amiga.Exec.MemoryFlags.Public));
        var success = false;
        if (request.IsNotNull && probe.IsNotNull && global::Amiga.Exec.OpenDevice(CString.FromPointer(deviceName.Raw), unit, request, 0) == 0)
        {
            APTR.WriteUInt16(request, NativeExecFieldCodec.IoCommandOffset, 22);
            APTR.WriteUInt32(request, NativeExecFieldCodec.IoDataOffset, probe.Raw);
            APTR.WriteUInt32(request, NativeExecFieldCodec.IoLengthOffset, 32);
            global::Amiga.Exec.DoIO(request);
            if (APTR.ReadUInt8(request, NativeExecFieldCodec.IoErrorOffset) == 0)
            {
                var bytes = APTR.ReadUInt32(probe, 0); var cylinders = APTR.ReadUInt32(probe, 8);
                var heads = APTR.ReadUInt32(probe, 16); var sectors = APTR.ReadUInt32(probe, 20);
                if (bytes >= 512 && bytes % 4 == 0 && cylinders != 0 && heads != 0 && sectors != 0)
                {
                    APTR.WriteUInt32(environment, 4, bytes / 4); APTR.WriteUInt32(environment, 12, heads);
                    APTR.WriteUInt32(environment, 16, 1); APTR.WriteUInt32(environment, 20, sectors);
                    APTR.WriteUInt32(environment, 36, 0); APTR.WriteUInt32(environment, 40, cylinders - 1);
                    APTR.WriteUInt32(environment, 48, APTR.ReadUInt32(probe, 24));
                    success = true;
                }
            }
            global::Amiga.Exec.CloseDevice(request);
        }
        if (probe.IsNotNull) global::Amiga.Exec.FreeMem(probe.Raw, 32);
        if (request.IsNotNull) global::Amiga.Exec.DeleteIORequest(request);
        global::Amiga.Exec.DeleteMsgPort(port.Raw);
        return success;
    }

    public static APTR CreateMount(APTR deviceName, uint unit, uint flags, APTR environment)
    {
        var state = global::Amiga.Exec.AllocMem(StateBytes, global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear);
        var s = APTR.FromPointer(state);
        if (s.IsNull) return s;
        APTR.WriteUInt32(s, NativeFileSystemStateCodec.MagicOffset, Magic);
        APTR.WriteUInt32(s, NativeFileSystemStateCodec.CacheTrackOffset, uint.MaxValue);
        var port = global::Amiga.Exec.CreateMsgPort();
        APTR.WriteUInt32(s, NativeFileSystemStateCodec.IoPortOffset, port.Raw);
        var request = port.IsNull ? APTR.Null : global::Amiga.Exec.CreateIORequest(port, 56);
        APTR.WriteUInt32(s, NativeFileSystemStateCodec.RequestOffset, request.Raw);
        if (request.IsNull || global::Amiga.Exec.OpenDevice(CString.FromPointer(deviceName.Raw), unit, request, flags) != 0)
        { Destroy(s); return APTR.Null; }
        var p = new NativeFileSystemPlatform(s);
        var probe = APTR.FromPointer(state + MetadataOffset);
        APTR.WriteUInt16(request, NativeExecFieldCodec.IoCommandOffset, 22); // TD_GETGEOMETRY
        APTR.WriteUInt32(request, NativeExecFieldCodec.IoDataOffset, probe.Raw);
        APTR.WriteUInt32(request, NativeExecFieldCodec.IoLengthOffset, 32);
        APTR.WriteUInt32(request, NativeExecFieldCodec.IoActualOffset, 0);
        global::Amiga.Exec.DoIO(request);
        var haveDevice = APTR.ReadUInt8(request, NativeExecFieldCodec.IoErrorOffset) == 0 && APTR.ReadUInt32(request, NativeExecFieldCodec.IoActualOffset) >= 32;
        AmigaVolumeGeometry geometry;
        if (environment.IsNotNull)
        {
            if (!AmigaVolumeGeometry.FromEnvironment(ref p, environment, out geometry)) { Destroy(s); return APTR.Null; }
            var deviceBytes = (ulong)APTR.ReadUInt32(probe, 0) * APTR.ReadUInt32(probe, 4);
            // A zero TotalSectors cannot describe the extent of a large device.
            if (haveDevice && deviceBytes != 0 && (geometry.StartByte > deviceBytes ||
                (ulong)geometry.BlockCount * geometry.BlockBytes > deviceBytes - geometry.StartByte)) { Destroy(s); return APTR.Null; }
        }
        else if (!haveDevice || !AmigaVolumeGeometry.FromDevice(ref p, probe, out geometry)) { Destroy(s); return APTR.Null; }
        SetGeometry(s, geometry);
        p = new NativeFileSystemPlatform(s);
        if (geometry.BlockBytes != 512)
        {
            var buffers = global::Amiga.Exec.AllocMem(geometry.BlockBytes * 3, global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear);
            APTR.WriteUInt32(s, NativeFileSystemStateCodec.BuffersOffset, buffers);
            if (buffers == 0) { Destroy(s); return APTR.Null; }
        }
        // Cache size is a private policy; volume size and device track size are independent.
        var cacheBlocks = geometry.TrackBlocks;
        if (cacheBlocks > 65536 / geometry.BlockBytes) cacheBlocks = 65536 / geometry.BlockBytes;
        if (cacheBlocks == 0) cacheBlocks = 1;
        var cacheBytes = cacheBlocks * geometry.BlockBytes;
        var cache = global::Amiga.Exec.AllocMem(cacheBytes, (global::Amiga.Exec.MemoryFlags)geometry.BufferMemoryType);
        if (cache != 0 && !BufferFitsMask(cache, cacheBytes, geometry.Mask))
        { global::Amiga.Exec.FreeMem(cache, cacheBytes); cache = 0; }
        if (cache == 0)
        {
            cache = global::Amiga.Exec.AllocMem(cacheBytes, global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Chip);
            if (cache != 0 && !BufferFitsMask(cache, cacheBytes, geometry.Mask))
            { global::Amiga.Exec.FreeMem(cache, cacheBytes); cache = 0; }
        }
        APTR.WriteUInt32(s, NativeFileSystemStateCodec.IoBufferOffset, cache); APTR.WriteUInt32(s, NativeFileSystemStateCodec.CacheOffset, cache);
        APTR.WriteUInt32(s, NativeFileSystemStateCodec.CacheBlocksOffset, cacheBlocks); APTR.WriteUInt32(s, NativeFileSystemStateCodec.CacheBytesOffset, cacheBytes);
        if (cache == 0) { Destroy(s); return APTR.Null; }
        var type = 0u;
        if (!AmigaDiskFileSystemCore.Mount(ref p, Metadata(s), out type))
        {
#if COPPERSTART_FULL_FILESYSTEM_EXTENSION
            // A failed device read is not evidence of another filesystem.
            // Declared SFS mounts still get their secondary-root recovery path.
            if (APTR.ReadUInt8(request, NativeExecFieldCodec.IoErrorOffset) != 0 &&
                (environment.IsNull || APTR.ReadUInt32(environment, 0) < 16 ||
                 APTR.ReadUInt32(environment, 64) is not (AmigaSmartFileSystemCore.DosType or NativeSfs2Bridge.DosType)))
            { Destroy(s); return APTR.Null; }
#endif
            if (!MountSmart(s))
            {
#if COPPERSTART_FULL_FILESYSTEM_EXTENSION
                // Keep a readable, unformatted configured device addressable by Format().
                if (APTR.ReadUInt8(request, NativeExecFieldCodec.IoErrorOffset) == 0 &&
                    APTR.ReadUInt32(s, NativeFileSystemStateCodec.DosTypeOffset) == 0)
                { ReleaseSmart(s); APTR.WriteUInt32(s, NativeFileSystemStateCodec.DosTypeOffset, 0); return s; }
#endif
                Destroy(s); return APTR.Null;
            }
            type = APTR.ReadUInt32(s, NativeFileSystemStateCodec.DosTypeOffset);
        }
        else
        {
            // Retain headers and extension blocks separately from track data.
            // Sixteen blocks cover the full disk Shell's extension chain;
            // memory never grows with read or file length. Allocation failure
            // rolls back this optional cache and keeps ordinary track reads.
            MoreCache(s, 16, out _);
        }
        APTR.WriteUInt32(s, NativeFileSystemStateCodec.DosTypeOffset, type);

        return s;
    }

    internal static bool BufferFitsMask(uint address, uint bytes, uint mask)
    {
        // Low zero bits specify the start address's alignment (commonly two
        // or four bytes); they do not exclude those bytes within the transfer.
        if (mask == 0 || bytes == 0 || (address & ~mask) != 0 || bytes > uint.MaxValue - address) return false;
        // Include every address in the interval, even for masks with holes.
        // Endpoints alone can miss a forbidden bit that changes twice.
        var varying = address ^ (address + bytes - 1);
        varying |= varying >> 1; varying |= varying >> 2; varying |= varying >> 4;
        varying |= varying >> 8; varying |= varying >> 16;
        var alignmentBits = (mask & unchecked(0u - mask)) - 1;
        return ((address | varying) & ~(mask | alignmentBits)) == 0;
    }

    private static bool MountSmart(APTR s)
    {
        var geometry = ReadGeometry(s);
        var p = new NativeFileSystemPlatform(s);
            if (!NativeSfsExtension.Root(s)) { return false; }
            APTR.WriteUInt32(s, NativeFileSystemStateCodec.SmartCreationDateOffset, APTR.ReadUInt32(Metadata(s), 16));
            // Retain validated mount/recovery reads, including the transaction
            // marker, in the same cache subsequently used by DOS operations.
            if (APTR.ReadUInt32(s, NativeFileSystemStateCodec.ExtraCacheCountOffset) < 32)
                MoreCache(s, 32 - (int)APTR.ReadUInt32(s, NativeFileSystemStateCodec.ExtraCacheCountOffset), out _);
#if COPPERSTART_NATIVE_SFS_WRITES
            // Recovery mutates media and belongs to the native SFS writer profile.
            var journal = global::Amiga.Exec.AllocMem(geometry.BlockBytes,
                global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear);
            APTR.WriteUInt32(s, NativeFileSystemStateCodec.JournalScratchOffset, journal);
            if (journal == 0) { return false; }
#if !COPPERSTART_SFS_STANDALONE || !COPPERSTART_SFS_TRST_WRITES || COPPERSTART_NATIVE_SFS2
            var batch = global::Amiga.Exec.AllocMem(
                AmigaSmartFileSystemTransactionBuffer.StorageBytes(geometry.BlockBytes,
                    AmigaSmartFileSystemTransactionBuffer.MaximumCapacity),
                global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear);
            APTR.WriteUInt32(s, NativeFileSystemStateCodec.SmartTransactionBufferOffset, batch);
            // Low-memory mounts can retain the unbuffered undo protocol.
            if (batch != 0 && !AmigaSmartFileSystemTransactionBuffer.Initialize(ref p,
                APTR.FromPointer(batch), geometry.BlockBytes, AmigaSmartFileSystemTransactionBuffer.MaximumCapacity))
            { return false; }
#endif
            // Standalone classic TRST uses its redo arena for every mutation.
            // Legacy CSUN recovery already supports an absent undo batch.
            InitializeSmartWriteWork(s, out var smartWork);
            if (!NativeSfsExtension.Recover(s, ref smartWork)) { return false; }
            var combine = global::Amiga.Exec.AllocMem(
                AmigaFileSystemWriteCombiner.StorageBytes(geometry.BlockBytes, SmartWriteQueueCapacity),
                global::Amiga.Exec.MemoryFlags.Public);
            APTR.WriteUInt32(s, NativeFileSystemStateCodec.SmartWriteCombinerOffset, combine);
            // This optional physical queue never changes a transaction barrier.
            if (combine != 0 && !AmigaFileSystemWriteCombiner.Initialize(ref p, APTR.FromPointer(combine),
                geometry.BlockBytes, SmartWriteQueueCapacity)) { return false; }
#if !COPPERSTART_SFS_STRICT_WRITES && !COPPERSTART_SFS_TRST_WRITES
            APTR writeBuffer = NativeSfs2Bridge.IsSfs2(s) ? APTR.Null : global::Amiga.Exec.AllocMem(
                AmigaSmartFileSystemWriteBuffer.StorageBytes(geometry.BlockBytes, AmigaSmartFileSystemWriteBuffer.MaximumCapacity),
                global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear);
            APTR.WriteUInt32(s, NativeFileSystemStateCodec.SmartWriteBufferOffset, writeBuffer);
            // Allocation failure retains the strict engine's existing guarantees.
            if (writeBuffer != 0 && !AmigaSmartFileSystemWriteBuffer.Initialize(ref p, APTR.FromPointer(writeBuffer),
                geometry.BlockBytes, AmigaSmartFileSystemWriteBuffer.MaximumCapacity)) { return false; }
#endif
#endif
            if (!NativeSfsExtension.Mount(s)) { return false; }

        if (Smart(s))
        {
            // The SFS tree, bitmap, and undo chain are revisited between data
            // blocks. Keep them in the existing block LRU; a failed optional
            // cache allocation affects speed, not mountability.
            var enumeration = global::Amiga.Exec.AllocMem(SmartEnumerationCacheBytes,
                global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear);
            APTR.WriteUInt32(s, NativeFileSystemStateCodec.SmartEnumerationCacheOffset, enumeration);
        }
        return true;
    }

    public static void Destroy(APTR state)
    {
        if (state.IsNull) return;
        var recordPlatform = new NativeRecordLockPlatform();
        NativeRecordLockCore.Destroy(ref recordPlatform, state);
        ReleaseSmart(state);
        var cache = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.ExtraCacheHeadOffset));
        while (cache.IsNotNull)
        {
            var next = APTR.FromPointer(APTR.ReadUInt32(cache, 0));
            global::Amiga.Exec.FreeMem(cache.Raw, ExtraCacheBytes(state)); cache = next;
        }
        var request = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.RequestOffset));
        if (request.IsNotNull)
        {
            if (APTR.ReadUInt32(request, NativeExecFieldCodec.IoDeviceOffset) != 0) global::Amiga.Exec.CloseDevice(request);
            global::Amiga.Exec.DeleteIORequest(request);
        }
        var port = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.IoPortOffset));
        if (port.IsNotNull) global::Amiga.Exec.DeleteMsgPort(port);
        var io = APTR.ReadUInt32(state, NativeFileSystemStateCodec.IoBufferOffset);
        if (io != 0) global::Amiga.Exec.FreeMem(io, APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheBytesOffset));
        var buffers = APTR.ReadUInt32(state, NativeFileSystemStateCodec.BuffersOffset);
        if (buffers != 0) global::Amiga.Exec.FreeMem(buffers, ReadGeometry(state).BlockBytes * 3);
        global::Amiga.Exec.FreeMem(state.Raw, StateBytes);
    }

    private static void ReleaseSmart(APTR state)
    {
#if COPPERSTART_NATIVE_SFS_WRITES
        var timer = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartTimerOffset));
        if (timer.IsNotNull)
        {
            if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartTimerActiveOffset) != 0)
            {
                global::Amiga.Exec.AbortIO(timer);
                global::Amiga.Exec.WaitIO(timer);
            }
            global::Amiga.Exec.CloseDevice(timer);
            global::Amiga.Exec.DeleteIORequest(timer);
        }
#endif
        var writeBuffer = APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartWriteBufferOffset);
        if (writeBuffer != 0) global::Amiga.Exec.FreeMem(writeBuffer,
            AmigaSmartFileSystemWriteBuffer.StorageBytes(APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockBytesOffset), AmigaSmartFileSystemWriteBuffer.MaximumCapacity));
        var rangeBuffer = APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRangeBufferOffset);
#if COPPERSTART_SFS_TRST_BUFFERED
        if (rangeBuffer != 0) global::Amiga.Exec.FreeMem(rangeBuffer, APTR.ReadUInt32(APTR.FromPointer(rangeBuffer), 8));
#elif COPPERSTART_SFS_STANDALONE && COPPERSTART_SFS_TRST_WRITES && !COPPERSTART_NATIVE_SFS2
        if (rangeBuffer != 0)
        {
            var memory = new NativeFileSystemPlatform(state);
            global::Amiga.Exec.FreeMem(rangeBuffer,
                AmigaSmartFileSystemRedoArena.StorageBytes(ref memory, APTR.FromPointer(rangeBuffer)));
        }
#else
        if (rangeBuffer != 0) global::Amiga.Exec.FreeMem(rangeBuffer,
            NativeSfs2Bridge.IsSfs2(state) ? NativeSfs2Bridge.BufferBytes : SmartRangeBufferBytes);
#endif
        var enumeration = APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartEnumerationCacheOffset);
        if (enumeration != 0) global::Amiga.Exec.FreeMem(enumeration, SmartEnumerationCacheBytes);
#if COPPERSTART_NATIVE_SFS_WRITES && !COPPERSTART_FFS_ONLY
        var combine = APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartWriteCombinerOffset);
        if (combine != 0) global::Amiga.Exec.FreeMem(combine,
            AmigaFileSystemWriteCombiner.StorageBytes(APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockBytesOffset), SmartWriteQueueCapacity));
#endif
#if COPPERSTART_NATIVE_SFS_WRITES
        var journal = APTR.ReadUInt32(state, NativeFileSystemStateCodec.JournalScratchOffset);
        if (journal != 0) global::Amiga.Exec.FreeMem(journal, ReadGeometry(state).BlockBytes);
        var batch = APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartTransactionBufferOffset);
        if (batch != 0) global::Amiga.Exec.FreeMem(batch,
            AmigaSmartFileSystemTransactionBuffer.StorageBytes(ReadGeometry(state).BlockBytes,
                AmigaSmartFileSystemTransactionBuffer.MaximumCapacity));
#endif
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartTimerOffset, 0);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartTimerActiveOffset, 0);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartWriteBufferOffset, 0);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartRangeBufferOffset, 0);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartEnumerationCacheOffset, 0);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartWriteCombinerOffset, 0);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.JournalScratchOffset, 0);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartTransactionBufferOffset, 0);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartRecoveryPendingOffset, 0);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartJournalCursorOffset, 0);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartAllocationCursorOffset, 0);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartDeviceDirtyOffset, 0);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartRedoOverlayOffset, 0);
        APTR.WriteUInt32(state, SmartReservationsOffset, 0);
    }

    private static int MoreCache(APTR state, int delta, out DOS.Error error)
    {
        var head = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.ExtraCacheHeadOffset));
        var count = APTR.ReadUInt32(state, NativeFileSystemStateCodec.ExtraCacheCountOffset);
        error = DOS.Error.None;
        if (delta > 0)
        {
            if ((uint)delta > int.MaxValue - APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheBlocksOffset) - count) { error = DOS.Error.NoFreeStore; return 0; }
            var added = 0;
            var original = head;
            for (; added < delta; added++)
            {
                var block = APTR.FromPointer(global::Amiga.Exec.AllocMem(ExtraCacheBytes(state),
                    global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear));
                if (block.IsNull)
                {
                    while (head != original)
                    {
                        var next = APTR.FromPointer(APTR.ReadUInt32(head, 0));
                        global::Amiga.Exec.FreeMem(head.Raw, ExtraCacheBytes(state)); head = next;
                    }
                    error = DOS.Error.NoFreeStore; return 0;
                }
                APTR.WriteUInt32(block, 0, head.Raw); APTR.WriteUInt32(block, 4, uint.MaxValue); head = block;
            }
            count += (uint)added;
        }
        else if (delta < 0)
        {
            var remove = unchecked(0u - (uint)delta);
            while (remove != 0 && count != 0)
            {
                var next = APTR.FromPointer(APTR.ReadUInt32(head, 0));
                global::Amiga.Exec.FreeMem(head.Raw, ExtraCacheBytes(state)); head = next; count--; remove--;
            }
        }
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.ExtraCacheHeadOffset, head.Raw);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.ExtraCacheCountOffset, count);
        ReindexCache(state);
        return (int)(APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheBlocksOffset) + count);
    }

    private static void ReindexCache(APTR state)
    {
        for (var index = 0; index < 64; index++) APTR.WriteUInt32(state, NativeFileSystemStateCodec.CacheHashOffset + index * 4, 0);
#if COPPERSTART_SFS_STANDALONE && COPPERSTART_SFS_TRST_WRITES && !COPPERSTART_NATIVE_SFS2
        APTR.WriteUInt32(state, CachePageMaskOffset, 0);
        for (var index = 0; index < 32; index++) APTR.WriteUInt32(state, CachePageCountsOffset + index * 4, 0);
#endif
        var current = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.ExtraCacheHeadOffset));
        var previous = APTR.Null;
        while (current.IsNotNull)
        {
            APTR.WriteUInt32(current, 8, previous.Raw);
            var key = APTR.ReadUInt32(current, 4);
            if (key != uint.MaxValue)
            {
                var bucket = (int)(key & 63);
                APTR.WriteUInt32(current, 12, APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheHashOffset + bucket * 4));
                APTR.WriteUInt32(state, NativeFileSystemStateCodec.CacheHashOffset + bucket * 4, current.Raw);
#if COPPERSTART_SFS_STANDALONE && COPPERSTART_SFS_TRST_WRITES && !COPPERSTART_NATIVE_SFS2
                AddCachePage(state, key);
#endif
            }
            previous = current; current = APTR.FromPointer(APTR.ReadUInt32(current, 0));
        }
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.ExtraCacheTailOffset, previous.Raw);
    }

#if COPPERSTART_SFS_STANDALONE && COPPERSTART_SFS_TRST_WRITES && !COPPERSTART_NATIVE_SFS2
    internal static void AddCachePage(APTR state, uint key)
    {
        var page = (int)((key >> 6) & 31);
        var offset = CachePageCountsOffset + page * 4;
        var count = APTR.ReadUInt32(state, offset);
        APTR.WriteUInt32(state, offset, count + 1);
        if (count == 0) APTR.WriteUInt32(state, CachePageMaskOffset,
            APTR.ReadUInt32(state, CachePageMaskOffset) | (1u << page));
    }

    internal static void RemoveCachePage(APTR state, uint key)
    {
        var page = (int)((key >> 6) & 31);
        var offset = CachePageCountsOffset + page * 4;
        var count = APTR.ReadUInt32(state, offset) - 1;
        APTR.WriteUInt32(state, offset, count);
        if (count == 0) APTR.WriteUInt32(state, CachePageMaskOffset,
            APTR.ReadUInt32(state, CachePageMaskOffset) & ~(1u << page));
    }

#endif

    public static uint Entry()
    {
        var task = global::Amiga.Exec.FindTask(CString.FromPointer(0));
        var state = APTR.FromPointer(APTR.ReadUInt32(task, ExecLayout.Task.UserData));
        return Run(state);
    }

    public static bool ProcessRecordTimer(APTR state, APTR message)
    {
        var platform = new NativeRecordLockPlatform();
        return NativeRecordLockCore.TimerReply(ref platform, state, message);
    }

    public static uint Run(APTR state)
    {
        var task = global::Amiga.Exec.FindTask(CString.FromPointer(0));
        var port = DosProcessCodec.MessagePortAddress(task);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.PortOffset, port.Raw);
#if COPPERSTART_PACKED_EXTENSION
        var mediaSignal = NativeRemovableMedia.Start(state);
#endif
#if COPPERSTART_NATIVE_SFS_WRITES
        InitializeWriteTimer(state, port);
#endif
        while (true)
        {
#if COPPERSTART_PACKED_EXTENSION
            if (mediaSignal == 0) mediaSignal = NativeRemovableMedia.Start(state);
            if (mediaSignal != 0) NativeRemovableMedia.Bind(state);
            var message = APTR.FromPointer(global::Amiga.Exec.GetMsg(port.Raw));
            if (message.IsNull && mediaSignal != 0)
            {
                global::Amiga.Exec.Wait(mediaSignal | (1u << APTR.ReadUInt8(port, 15)));
                message = APTR.FromPointer(global::Amiga.Exec.GetMsg(port.Raw));
            }
            else if (message.IsNull)
            {
                global::Amiga.Exec.WaitPort(port.Raw);
                message = APTR.FromPointer(global::Amiga.Exec.GetMsg(port.Raw));
            }
            if (mediaSignal != 0) NativeRemovableMedia.Refresh(state);
#else
            global::Amiga.Exec.WaitPort(port.Raw);
            var message = APTR.FromPointer(global::Amiga.Exec.GetMsg(port.Raw));
#endif
            if (message.IsNull) continue;
            var recordPlatform = new NativeRecordLockPlatform();
            // Empty owned chains have no timer reply or retained packet to
            // match. Nonempty chains still receive the core's record checks.
            if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.RecordWaitersOffset) != 0 &&
                ProcessRecordTimer(state, message)) continue;
#if COPPERSTART_NATIVE_SFS_WRITES
            if (message.Raw == APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartTimerOffset))
            {
                APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartTimerActiveOffset, 0);
                if (HasBufferedWrites(state)) FlushSmart(state);
                ArmWriteTimer(state);
                continue;
            }
#endif
            if (NativeFileSystemNotifications.Reply(state, message)) continue;
            var packet = APTR.FromPointer(APTR.ReadUInt32(message, NativeExecFieldCodec.NodeNameOffset));
            if (packet.IsNull) continue;
            var replyPort = APTR.FromPointer(APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.PortOffset));
            Dispatch(state, packet);
#if COPPERSTART_NATIVE_SFS_WRITES
            ArmWriteTimer(state);
#endif
            // Dispatch can create a waiter: read the head afresh here.
            if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.RecordWaitersOffset) != 0 &&
                NativeRecordLockCore.IsPending(ref recordPlatform, state, packet)) continue;
            APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.PortOffset, port.Raw);
            global::Amiga.Exec.PutMsg(replyPort, message);
        }
    }

    private static APTR Object(APTR state, uint token, bool file)
    {
        var current = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.ObjectsOffset));
        for (var guard = 0; current.IsNotNull && guard < 4096; guard++)
        {
            if ((file ? current.Raw : (current.Raw + 20) >> 2) == token && APTR.ReadUInt32(current, 16) == (file ? 1u : 0u)) return current;
            current = APTR.FromPointer(APTR.ReadUInt32(current, 0));
        }
        return APTR.Null;
    }

    // File-only high words reuse space occupied by directory-lock enumeration.
    // The public FileLock at 20..39 and the classic read cursor at 56..87 stay intact.
    private const int FileSizeHigh = 88, FilePositionHigh = 92;
    private static ulong FileSize(APTR file) => ((ulong)APTR.ReadUInt32(file, FileSizeHigh) << 32) | APTR.ReadUInt32(file, 8);
    private static ulong FilePosition(APTR file) => ((ulong)APTR.ReadUInt32(file, FilePositionHigh) << 32) | APTR.ReadUInt32(file, 12);
    private static void SetFileSize(APTR file, ulong size)
    { APTR.WriteUInt32(file, 8, (uint)size); APTR.WriteUInt32(file, FileSizeHigh, (uint)(size >> 32)); }
    private static void SetFilePosition(APTR file, ulong position)
    { APTR.WriteUInt32(file, 12, (uint)position); APTR.WriteUInt32(file, FilePositionHigh, (uint)(position >> 32)); }

    private static APTR NewObject(APTR state, uint key, ulong size, bool file, int access)
    {
        var o = APTR.FromPointer(global::Amiga.Exec.AllocMem(ObjectBytes, global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear));
        if (o.IsNull) return o;
        APTR.WriteUInt32(o, 0, APTR.ReadUInt32(state, NativeFileSystemStateCodec.ObjectsOffset));
        APTR.WriteUInt32(o, 4, key);
        SetFileSize(o, size);
        APTR.WriteUInt32(o, 16, file ? 1u : 0u);
        APTR.WriteUInt32(o, 24, key);
        APTR.WriteUInt32(o, 28, unchecked((uint)access));
        APTR.WriteUInt32(o, 32, APTR.ReadUInt32(state, NativeFileSystemStateCodec.PortOffset));
        APTR.WriteUInt32(o, 36, APTR.ReadUInt32(state, NativeFileSystemStateCodec.VolumeOffset) >> 2);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.ObjectsOffset, o.Raw);
        return o;
    }

    private static void FreeObject(APTR state, APTR o)
    {
        var link = APTR.FromPointer(state.Raw + ObjectsOffset);
        for (var guard = 0; guard < 4096; guard++)
        {
            var current = APTR.FromPointer(APTR.ReadUInt32(link, 0));
            if (current.IsNull) return;
            if (current == o)
            {
                if (APTR.ReadUInt32(o, 16) == 1)
                {
                    var recordPlatform = new NativeRecordLockPlatform();
                    NativeRecordLockCore.Close(ref recordPlatform, state, o);
                }
                APTR.WriteUInt32(link, 0, APTR.ReadUInt32(o, 0)); global::Amiga.Exec.FreeMem(o.Raw, ObjectBytes); return;
            }
            link = current;
        }
    }

    // Offsets 20..39 remain the public FileLock. Directory locks use four
    // private words at 40..55; file objects retain their own dirty flag at 40.
    public static void InvalidateDirectoryCursors(APTR state)
    {
        var enumeration = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartEnumerationCacheOffset));
        if (enumeration.IsNotNull)
        {
            var generation = unchecked(APTR.ReadUInt32(enumeration, 0) + 1);
            if (generation == 0)
            {
                var p = new NativeFileSystemPlatform(state);
                p.Clear(enumeration, SmartEnumerationCacheBytes);
            }
            APTR.WriteUInt32(enumeration, 0, generation);
        }
        var item = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.ObjectsOffset));
        while (item.IsNotNull)
        {
            if (APTR.ReadUInt32(item, 16) == 0) APTR.WriteUInt32(item, 40, 0);
            else APTR.WriteUInt32(item, SmartReadCursorOffset, 0);
            item = APTR.FromPointer(APTR.ReadUInt32(item, 0));
        }
    }

    private static void UpdateFileSizes(APTR state, uint node, ulong size)
    {
        var item = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.ObjectsOffset));
        while (item.IsNotNull)
        {
            if (APTR.ReadUInt32(item, 16) == 1 && APTR.ReadUInt32(item, 4) == node)
                SetFileSize(item, size);
            item = APTR.FromPointer(APTR.ReadUInt32(item, 0));
        }
    }

    private static ulong SharedResizeMinimum(APTR state, APTR caller)
    {
        var item = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.ObjectsOffset));
        var minimum = 0UL; var node = APTR.ReadUInt32(caller, 4);
        // dos.library/SetFileSize: other handles must remain within the file;
        // only the caller's own position may be clamped by truncation.
        while (item.IsNotNull)
        {
            if (item != caller && APTR.ReadUInt32(item, 16) == 1 && APTR.ReadUInt32(item, 4) == node &&
                FilePosition(item) > minimum) minimum = FilePosition(item);
            item = APTR.FromPointer(APTR.ReadUInt32(item, 0));
        }
        return minimum;
    }

    private static DOS.Error NextDirectory(APTR state, APTR owned, uint key, APTR fib, APTR metadata, APTR table)
    {
        var p = new NativeFileSystemPlatform(state);
        var cursor = new AmigaDiskFileSystemCore.DirectoryCursor();
        if (owned.IsNotNull)
        {
            cursor.Directory = key; cursor.Ordinal = APTR.ReadUInt32(owned, 40);
            cursor.Slot = APTR.ReadUInt32(owned, 44); cursor.Entry = APTR.ReadUInt32(owned, 48);
            cursor.Visited = APTR.ReadUInt32(owned, 52);
        }
        var error = AmigaDiskFileSystemCore.ExamineNext(ref p, ref cursor, key, fib, metadata, table);
        if (error == DOS.Error.None && owned.IsNotNull)
        {
            APTR.WriteUInt32(owned, 40, cursor.Ordinal); APTR.WriteUInt32(owned, 44, cursor.Slot);
            APTR.WriteUInt32(owned, 48, cursor.Entry); APTR.WriteUInt32(owned, 52, cursor.Visited);
        }
        return error;
    }

    internal static void InitializeWriteWork(APTR state, out AmigaFfsWriteCore.Work work)
    {
        var meta = Metadata(state);
        var bytes = APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockBytesOffset);
        var data = meta.Raw + bytes;
        work = default;
        work.Meta = meta; work.Data = APTR.FromPointer(data); work.Bitmap = APTR.FromPointer(data + bytes);
    }
    internal static AmigaSmartFileSystemWriteCore.Work SmartWriteWork(APTR state)
    {
        InitializeSmartWriteWork(state, out var work);
        return work;
    }

    // Initialize packet and extension callers' work areas directly. Returning
    // this record by value adds native temporary stack storage and copies.
    internal static void InitializeSmartWriteWork(APTR state, out AmigaSmartFileSystemWriteCore.Work work)
    {
        work = default;
        work.Reservations = APTR.FromPointer(state.Raw + SmartReservationsOffset);
        work.Meta = Metadata(state); work.Data = Data(state); work.Bitmap = Bitmap(state);
        work.Journal = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.JournalScratchOffset));
        work.Batch = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartTransactionBufferOffset));
        work.Buffered = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartWriteBufferOffset));
        work.JournalCursor = APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartJournalCursorOffset);
        work.AllocationCursor = APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartAllocationCursorOffset);
    }

    private static bool HasBufferedWrites(APTR state)
    {
#if COPPERSTART_SFS_TRST_BUFFERED
        return NativeSfsExtension.TrstGroupPending(state);
#else
        var buffer = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartWriteBufferOffset));
        return buffer.IsNotNull && APTR.ReadUInt32(buffer, 0) != 0;
#endif
    }

    private static bool FlushSmart(APTR state)
    {
        InitializeSmartWriteWork(state, out var work);
        var success = NativeSfsExtension.Flush(state, ref work);
        if (!success) APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartRecoveryPendingOffset, 1);
        return success;
    }

    private static void InitializeWriteTimer(APTR state, APTR port)
    {
#if COPPERSTART_SFS_TRST_BUFFERED
        if (!Smart(state)) return;
#else
        if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartWriteBufferOffset) == 0) return;
#endif
        var request = global::Amiga.Exec.CreateIORequest(port, TimerRequest.Size);
        if (request.IsNotNull && global::Amiga.Exec.OpenDevice(CString.FromPointer(CString.ToUInt32("timer.device")),
            (uint)TimerUnit.MicroHz, request, 0) == 0)
        { APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartTimerOffset, request.Raw); return; }
        if (request.IsNotNull) global::Amiga.Exec.DeleteIORequest(request);
#if !COPPERSTART_SFS_TRST_BUFFERED
        // No timer means strict writes: never silently leave pending data forever.
        global::Amiga.Exec.FreeMem(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartWriteBufferOffset),
            AmigaSmartFileSystemWriteBuffer.StorageBytes(APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockBytesOffset), AmigaSmartFileSystemWriteBuffer.MaximumCapacity));
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartWriteBufferOffset, 0);
#endif
    }

    private static void ArmWriteTimer(APTR state)
    {
        if (!HasBufferedWrites(state) || APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartTimerActiveOffset) != 0) return;
        var request = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartTimerOffset));
        if (request.IsNull) return;
        APTR.WriteUInt16(request, NativeExecFieldCodec.IoCommandOffset, (ushort)TimerCommand.AddRequest);
        APTR.WriteUInt8(request, NativeExecFieldCodec.IoFlagsOffset, 0);
        APTR.WriteUInt8(request, NativeExecFieldCodec.IoErrorOffset, 0);
        APTR.WriteUInt32(request, NativeTimerRequestFieldCodec.SecondsOffset, 5); APTR.WriteUInt32(request, NativeTimerRequestFieldCodec.MicrosecondsOffset, 0);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartTimerActiveOffset, 1);
        global::Amiga.Exec.SendIO(request);
    }
    private static bool InUse(APTR state, uint key, bool exclusive)
    {
        var current = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.ObjectsOffset));
        while (current.IsNotNull)
        {
            if (APTR.ReadUInt32(current, 4) == key && (exclusive || APTR.ReadUInt32(current, 28) == uint.MaxValue ||
                (APTR.ReadUInt32(current, 16) == 1 && APTR.ReadUInt32(current, 28) == 1))) return true;
            current = APTR.FromPointer(APTR.ReadUInt32(current, 0));
        }
        return false;
    }
    private static bool PacketName(APTR state, uint baseToken, uint nameToken, ref AmigaFfsWriteCore.Request r)
    {
        var owned = Object(state, baseToken, false);
        r.Directory = baseToken == 0 ? RootKey(state) : owned.IsNotNull ? APTR.ReadUInt32(owned, 4) : 0;
        if (r.Directory == 0 || nameToken > 0x3FFFFFFF) return false;
        var p = new NativeFileSystemPlatform(state); var name = APTR.FromPointer(nameToken << 2);
        if (!p.IsMapped(name, 1) || !p.IsMapped(name, (uint)p.ReadUInt8(name) + 1)) return false;
        r.Name = APTR.FromPointer(name.Raw + 1); r.Length = p.ReadUInt8(name); return true;
    }
    private static int OpenWrite(APTR state, APTR packet, DosPacketAction action, out DOS.Error error)
    {
        var p = new NativeFileSystemPlatform(state); InitializeWriteWork(state, out var w); var r = new AmigaFfsWriteCore.Request();
        error = DOS.Error.None;
        var handle = APTR.FromPointer(APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument1Offset) << 2);
        if (!p.IsMapped(handle, FileHandle.Size) || !PacketName(state, APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument2Offset), APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument3Offset), ref r))
        { error = DOS.Error.InvalidLock; return 0; }
        if (p.WriteProtected()) { error = DOS.Error.DiskWriteProtected; return 0; }
        r.Header = AmigaDiskFileSystemCore.Resolve(ref p, r.Directory, r.Name, r.Length, APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset), w.Meta, out error);
        var changed = r.Header == 0 || action == DosPacketAction.FindOutput;
        if (r.Header == 0)
        {
            if (error != DOS.Error.ObjectNotFound) return 0;
            if (!AmigaFfsWriteCore.Create(ref p, ref w, ref r, false)) { error = w.Error; return 0; }
        }
        else
        {
            if (InUse(state, r.Header, true)) { error = DOS.Error.ObjectInUse; return 0; }
            if (APTR.ReadUInt32(w.Meta, ((int)p.BlockBytes - 512) + 508) != 0xFFFFFFFD) { error = DOS.Error.ObjectWrongType; return 0; }
            if (action == DosPacketAction.FindOutput && !AmigaFfsWriteCore.Remove(ref p, ref w, r.Header, true)) { error = w.Error; return 0; }
        }
        if (!AmigaDiskFileSystemCore.Header(ref p, r.Header, w.Meta)) { error = DOS.Error.DiskNotValidated; return 0; }
        var obj = NewObject(state, r.Header, p.ReadUInt32(w.Meta, ((int)p.BlockBytes - 512) + 324), true, 1);
        if (obj.IsNull) { error = DOS.Error.NoFreeStore; return 0; }
        APTR.WriteUInt32(handle, NativeDosFileHandleFieldCodec.PortOffset, APTR.ReadUInt32(state, NativeFileSystemStateCodec.PortOffset)); APTR.WriteUInt32(handle, NativeDosFileHandleFieldCodec.Argument1Offset, obj.Raw);
        if (changed) p.WriteUInt32(obj, 40, 1);
        error = DOS.Error.None; return -1;
    }
    private static int OpenSmartWrite(APTR state, APTR packet, DosPacketAction action, out DOS.Error error)
    {
        var p = new NativeFileSystemPlatform(state); InitializeSmartWriteWork(state, out var w); var name = new AmigaFfsWriteCore.Request();
        error = DOS.Error.None;
        var handle = APTR.FromPointer(APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument1Offset) << 2);
        if (!p.IsMapped(handle, FileHandle.Size) ||
            !PacketName(state, APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument2Offset), APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument3Offset), ref name))
        { error = DOS.Error.InvalidLock; return 0; }
        if (p.WriteProtected()) { error = DOS.Error.DiskWriteProtected; return 0; }
        var found = NativeSfsExtension.ResolveInformation(state, name.Directory, name.Name, name.Length,
            out var size, out var directory, out _, out error);
        var changed = found == 0 || action == DosPacketAction.FindOutput;
        if (found == 0)
        {
            if (error != DOS.Error.ObjectNotFound) return 0;
            var request = new AmigaSmartFileSystemWriteCore.Request
            { Directory = name.Directory, Name = name.Name, Length = name.Length };
            if (!NativeSfsExtension.Create(state, ref w, ref request, false)) { error = w.Error; return 0; }
            found = request.Node; size = 0;
        }
        else
        {
            if (InUse(state, found, true)) { error = DOS.Error.ObjectInUse; return 0; }
            if (directory) { error = DOS.Error.ObjectWrongType; return 0; }
            if (action == DosPacketAction.FindOutput)
            {
                if (!NativeSfsExtension.Truncate(state, ref w, found)) { error = w.Error; return 0; }
                size = 0;
            }
        }
        var obj = NewObject(state, found, size, true, 1);
        if (obj.IsNull) { error = DOS.Error.NoFreeStore; return 0; }
        APTR.WriteUInt32(handle, NativeDosFileHandleFieldCodec.PortOffset, APTR.ReadUInt32(state, NativeFileSystemStateCodec.PortOffset)); APTR.WriteUInt32(handle, NativeDosFileHandleFieldCodec.Argument1Offset, obj.Raw);
        if (changed) p.WriteUInt32(obj, 40, 1);
        error = DOS.Error.None; return -1;
    }

    private static int MutateSmart(APTR state, APTR packet, DosPacketAction action, out DOS.Error error)
    {
        var p = new NativeFileSystemPlatform(state); InitializeSmartWriteWork(state, out var w); var name = new AmigaFfsWriteCore.Request();
        error = DOS.Error.None;
        if (!PacketName(state, APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument1Offset), APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument2Offset), ref name))
        { error = DOS.Error.InvalidLock; return 0; }
        if (action == DosPacketAction.CreateDirectory)
        {
            var request = new AmigaSmartFileSystemWriteCore.Request
            { Directory = name.Directory, Name = name.Name, Length = name.Length };
            if (!NativeSfsExtension.Create(state, ref w, ref request, true)) { error = w.Error; return 0; }
            var obj = NewObject(state, request.Node, 0, false, -2);
            if (obj.IsNull) { error = DOS.Error.NoFreeStore; return 0; }
            NativeFileSystemNotifications.Changed(state, request.Node);
            return unchecked((int)((obj.Raw + 20) >> 2));
        }
        var found = NativeSfsExtension.ResolveInformation(state, name.Directory, name.Name, name.Length,
            out _, out _, out var oldParent, out error);
        if (found == 0) return 0;
        if (InUse(state, found, true)) { error = DOS.Error.ObjectInUse; return 0; }
        bool success;
        if (action == DosPacketAction.RenameObject)
        {
            if (!PacketName(state, APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument3Offset), APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument4Offset), ref name))
            { error = DOS.Error.InvalidLock; return 0; }
            var request = new AmigaSmartFileSystemWriteCore.Request
            { Node = found, Directory = name.Directory, Name = name.Name, Length = name.Length };
            success = NativeSfsExtension.Rename(state, ref w, ref request);
        }
        else success = NativeSfsExtension.Remove(state, ref w, found);
        if (!success) error = w.Error;
        else NativeFileSystemNotifications.Changed(state, found, oldParent);
        return success ? -1 : 0;
    }
    private static int Mutate(APTR state, APTR packet, DosPacketAction action, out DOS.Error error)
    {
        var p = new NativeFileSystemPlatform(state); InitializeWriteWork(state, out var w); var r = new AmigaFfsWriteCore.Request();
        error = DOS.Error.None;
        if (!PacketName(state, APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument1Offset), APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument2Offset), ref r)) { error = DOS.Error.InvalidLock; return 0; }
        if (action == DosPacketAction.CreateDirectory)
        {
            if (!AmigaFfsWriteCore.Create(ref p, ref w, ref r, true)) { error = w.Error; return 0; }
            var obj = NewObject(state, r.Header, 0, false, -2);
            if (obj.IsNull) { error = DOS.Error.NoFreeStore; return 0; }
            NativeFileSystemNotifications.Changed(state, r.Header);
            return unchecked((int)((obj.Raw + 20) >> 2));
        }
        r.Header = AmigaDiskFileSystemCore.Resolve(ref p, r.Directory, r.Name, r.Length, APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset), w.Meta, out error);
        if (r.Header == 0) return 0;
        if (InUse(state, r.Header, true)) { error = DOS.Error.ObjectInUse; return 0; }
        var oldParent = p.ReadUInt32(w.Meta, ((int)p.BlockBytes - 512) + 500);
        bool success;
        if (action == DosPacketAction.RenameObject)
        {
            if (!PacketName(state, APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument3Offset), APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument4Offset), ref r)) { error = DOS.Error.InvalidLock; return 0; }
            success = AmigaFfsWriteCore.Rename(ref p, ref w, ref r);
        }
        else success = AmigaFfsWriteCore.Remove(ref p, ref w, r.Header, false);
        if (!success) error = w.Error;
        else NativeFileSystemNotifications.Changed(state, r.Header, oldParent);
        return success ? -1 : 0;
    }

    private static int SetMetadata(APTR state, APTR packet, DosPacketAction action, out DOS.Error error)
    {
        var p = new NativeFileSystemPlatform(state); InitializeWriteWork(state, out var w); var r = new AmigaFfsWriteCore.Request();
        error = DOS.Error.None;
        if (!PacketName(state, APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument2Offset), APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument3Offset), ref r))
        { error = DOS.Error.InvalidLock; return 0; }
        r.Header = AmigaDiskFileSystemCore.Resolve(ref p, r.Directory, r.Name, r.Length,
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset), w.Meta, out error);
        if (r.Header == 0) return 0;
        // FFS refuses date changes under an exclusive lock; RAM: permits them.
        if (action == DosPacketAction.SetDate && APTR.ReadUInt32(state, NativeFileSystemStateCodec.RamBlocksOffset) == 0 && InUse(state, r.Header, false))
        { error = DOS.Error.ObjectInUse; return 0; }
        var value = APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument4Offset);
        r.Position = value;
        if (action != DosPacketAction.SetProtection)
        {
            r.Buffer = APTR.FromPointer(value << 2);
            if (action == DosPacketAction.SetComment)
            {
                r.Length = p.ReadUInt8(r.Buffer);
                r.Buffer = APTR.FromPointer(r.Buffer.Raw + 1);
            }
        }
        var success = AmigaFfsWriteCore.SetMetadata(ref p, ref w, ref r, action);
        if (!success) error = w.Error;
        else NativeFileSystemNotifications.Changed(state, r.Header);
        return success ? -1 : 0;
    }

    private static int SetSmartMetadata(APTR state, APTR packet, DosPacketAction action, out DOS.Error error)
    {
        var p = new NativeFileSystemPlatform(state); InitializeSmartWriteWork(state, out var w); var name = new AmigaFfsWriteCore.Request();
        error = DOS.Error.None;
        if (!PacketName(state, APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument2Offset), APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument3Offset), ref name))
        { error = DOS.Error.InvalidLock; return 0; }
        var found = NativeSfsExtension.Resolve(state, name.Directory, name.Name, name.Length, out error);
        if (found == 0) return 0;
        if (action == DosPacketAction.SetDate && InUse(state, found, false))
        { error = DOS.Error.ObjectInUse; return 0; }
        var value = APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument4Offset);
        var request = new AmigaSmartFileSystemWriteCore.Request { Node = found, Position = value };
        if (action != DosPacketAction.SetProtection)
        {
            request.Buffer = APTR.FromPointer(value << 2);
            // ACTION_SET_DATE is documented with a BPTR. The Kickstart 3.1
            // guest path used by the native comparison has also delivered an
            // already-expanded pointer; accept it when the BPTR form is not
            // in guest memory. The date data itself is checked by the core.
            if (action == DosPacketAction.SetDate && !p.IsMapped(request.Buffer, 12) &&
                p.IsMapped(APTR.FromPointer(value), 12))
                request.Buffer = APTR.FromPointer(value);
            if (action == DosPacketAction.SetComment)
            {
                request.Length = p.ReadUInt8(request.Buffer);
                request.Buffer = APTR.FromPointer(request.Buffer.Raw + 1);
            }
        }
        var success = NativeSfsExtension.SetMetadata(state, ref w, ref request, action);
        if (!success) error = w.Error;
        else NativeFileSystemNotifications.Changed(state, found);
        return success ? -1 : 0;
    }

    internal static int DispatchSmartMutation(APTR state, APTR packet, DosPacketAction action, out DOS.Error error)
    {
        if (action is DosPacketAction.FindOutput or DosPacketAction.FindUpdate)
            return OpenSmartWrite(state, packet, action, out error);
        if (action is DosPacketAction.DeleteObject or DosPacketAction.RenameObject or DosPacketAction.CreateDirectory)
            return MutateSmart(state, packet, action, out error);
        if ((int)action == AmigaSmartFileSystemTrimPacket.Action)
        {
            return NativeSfsTrim.Execute(state, packet, out error);
        }
        return SetSmartMetadata(state, packet, action, out error);
    }

    public static void Dispatch(APTR state, APTR packet)
    {
        // Read and enumeration packets need no mutation work area. Keep recovery
        // and inhibited-media handling in the full dispatcher below.
        var action = (DosPacketAction)APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.TypeOffset);
#if COPPERSTART_NATIVE_SFS_WRITES
        if (action == DosPacketAction.Write && Smart(state) &&
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.InhibitedOffset) == 0 && APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRecoveryPendingOffset) == 0)
        { DispatchSmartWrite(state, packet); return; }
#endif
        if (action is not (DosPacketAction.Read or DosPacketAction.ExamineNext) || !Smart(state) ||
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.InhibitedOffset) != 0 || APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRecoveryPendingOffset) != 0)
        { DispatchPacket(state, packet); return; }
        if (action == DosPacketAction.ExamineNext)
        {
            var lockValue = APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument1Offset);
            var owned = Object(state, lockValue, false);
            var key = lockValue == 0 ? RootKey(state) : owned.IsNotNull ? APTR.ReadUInt32(owned, 4) : 0;
            var nextError = key == 0 ? DOS.Error.InvalidLock : NativeSfsExtension.ExamineNext(state,
                owned, key, APTR.FromPointer(APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument2Offset) << 2));
            APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result1Offset, nextError == DOS.Error.None ? uint.MaxValue : 0);
            APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result2Offset, (uint)nextError);
            return;
        }
        var file = Object(state, APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument1Offset), true);
        var error = DOS.Error.InvalidLock;
        var result = -1;
        if (file.IsNotNull)
        {
            var position = FilePosition(file);
            result = NativeSfsExtension.Read(state, file, APTR.ReadUInt32(file, 4), position,
                APTR.FromPointer(APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument2Offset)), APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument3Offset), out error);
            if (result > 0) SetFilePosition(file, position + (uint)result);
        }
        APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result1Offset, unchecked((uint)result));
        APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result2Offset, (uint)error);
    }

#if COPPERSTART_NATIVE_SFS_WRITES
    private static int WriteSmartFile(APTR state, APTR file, APTR buffer, uint bytes, out DOS.Error error)
    {
        if (NativeSfs2Bridge.IsSfs2(state))
        {
            var position = FilePosition(file); var node = APTR.ReadUInt32(file, 4);
            if (!NativeSfsExtension.WriteWide(state, node, position, buffer, bytes, out var count, out error)) return -1;
            SetFilePosition(file, position + count);
            if (position + count > FileSize(file)) UpdateFileSizes(state, node, position + count);
            if (count != 0) APTR.WriteUInt32(file, 40, 1);
            return (int)count;
        }
#if !COPPERSTART_SFS_TRST_BUFFERED || !COPPERSTART_SFS_STANDALONE
        InitializeSmartWriteWork(state, out var work);
#endif
        var write = default(AmigaSmartFileSystemWriteCore.Request);
        write.Node = APTR.ReadUInt32(file, 4); write.Position = APTR.ReadUInt32(file, 12);
        write.Buffer = buffer; write.Length = bytes;
#if COPPERSTART_SFS_TRST_BUFFERED && COPPERSTART_SFS_STANDALONE
        var size = APTR.ReadUInt32(file, 8);
        if (bytes != 0 && bytes <= 65536 && write.Position < size && bytes < size - write.Position &&
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartTimerOffset) != 0)
        {
            var p = new NativeFileSystemPlatform(state);
            var group = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRangeBufferOffset));
            if (!AmigaSmartFileSystemWriteCore.TryContinueTrstGroup(ref p, group, Metadata(state),
                APTR.FromPointer(state.Raw + SmartReservationsOffset), ref write, out var handled, out error, true)) return -1;
            if (handled)
            {
                APTR.WriteUInt32(file, 12, write.Position + bytes);
                APTR.WriteUInt32(file, 40, 1);
                return (int)bytes;
            }
        }
        // Keep the general 152-byte Work off the continuation entry's stack.
        // Native local initialization otherwise clears it even when the early
        // continuation above returns without calling InitializeSmartWriteWork.
        return CompleteSmartWrite(state, file, ref write, out error);
    }

    private static int CompleteSmartWrite(APTR state, APTR file,
        ref AmigaSmartFileSystemWriteCore.Request write, out DOS.Error error)
    {
        var bytes = write.Length;
        InitializeSmartWriteWork(state, out var work);
#endif
        var blockBytes = APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockBytesOffset);
        var append = blockBytes != 0 && write.Position == APTR.ReadUInt32(file, 8) &&
            write.Position % blockBytes == 0 && bytes % blockBytes == 0;
        if (!NativeSfsExtension.Write(state, ref work, ref write, out var actual, append))
        { error = work.Error; return -1; }
        APTR.WriteUInt32(file, 12, write.Position + actual);
        if (write.Position + actual > APTR.ReadUInt32(file, 8))
            UpdateFileSizes(state, write.Node, write.Position + actual);
        if (actual != 0) APTR.WriteUInt32(file, 40, 1);
        error = DOS.Error.None;
        return (int)actual;
    }

    private static void DispatchSmartWrite(APTR state, APTR packet)
    {
        InvalidateDirectoryCursors(state);
        var file = Object(state, APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument1Offset), true);
        var error = DOS.Error.InvalidLock;
        var result = file.IsNull ? -1 : WriteSmartFile(state, file,
            APTR.FromPointer(APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument2Offset)), APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument3Offset), out error);
        if (error != DOS.Error.None) RecoverSmartWriteError(state);
        APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result1Offset, unchecked((uint)result));
        APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result2Offset, (uint)error);
    }

    // Recovery owns a full Work record. Keep its native local initialization
    // on the error path instead of paying for it before every successful Write.
    private static void RecoverSmartWriteError(APTR state)
    {
        InitializeSmartWriteWork(state, out var recovery);
        var p = new NativeFileSystemPlatform(state);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartRecoveryPendingOffset,
            NativeSfsExtension.Recover(state, ref recovery) && p.Flush() ? 0u : 1u);
    }
#endif

    // OFS/FFS and SFS1 store unsigned 32-bit sizes. Only extended
    // packets project these into QUADs and apply MorphOS's signed-LONG sentinel.
    private static void ExtendFileInfo(APTR fib)
    {
        var size = APTR.ReadUInt32(fib, 124);
        APTR.WriteUInt32(fib, FileInfoBlock.Size64Offset, 0);
        APTR.WriteUInt32(fib, FileInfoBlock.Size64Offset + 4, size);
        APTR.WriteUInt32(fib, FileInfoBlock.NumBlocks64Offset, 0);
        APTR.WriteUInt32(fib, FileInfoBlock.NumBlocks64Offset + 4, APTR.ReadUInt32(fib, 128));
        if (size > int.MaxValue) APTR.WriteUInt32(fib, 124, 0);
    }

    private static int QueryAttribute(APTR state, FileSystemQueryAttribute attribute,
        APTR storage, int storageSize, out DOS.Error error)
    {
        error = DOS.Error.None;
        var required = NativeFileSystemAttributeCore.RequiredSize(attribute);
        if (required == 0 || (Smart(state) && attribute == FileSystemQueryAttribute.MaxVolumeNameLength) ||
            (NativeSfs2Bridge.IsSfs2(state) && attribute == FileSystemQueryAttribute.IsCaseSensitive))
        { error = DOS.Error.BadNumber; return 0; }
        if (storageSize < 0 || (uint)storageSize < required)
        { error = DOS.Error.LineTooLong; return 0; }
        var p = new NativeFileSystemPlatform(state);
        if (!p.IsMapped(storage, required)) { error = DOS.Error.BadNumber; return 0; }
        ulong value;
        switch (attribute)
        {
            case FileSystemQueryAttribute.MaxFileNameLength: value = NativeSfs2Bridge.IsSfs2(state) ? 107u : Smart(state) ? 100u : 30u; break;
            case FileSystemQueryAttribute.MaxVolumeNameLength:
                value = 30; break;
            case FileSystemQueryAttribute.MaxFileSize: value = NativeSfs2Bridge.IsSfs2(state) ? AmigaSmartFileSystem2WriteCore.MaxFileSize : uint.MaxValue; break;
            case FileSystemQueryAttribute.IsCaseSensitive: value = 0; break;
            case FileSystemQueryAttribute.DeviceType: value = 31; break; // DG_UNKNOWN
            case FileSystemQueryAttribute.NumBlocks: value = p.BlockCount; break;
            case FileSystemQueryAttribute.NumBlocksUsed:
                if (!(Smart(state) ? NativeSfsExtension.CountUsed(state, out var countedUsed) :
                    AmigaDiskFileSystemCore.CountUsed(ref p, Metadata(state), Data(state), out countedUsed)))
                { error = DOS.Error.DiskNotValidated; return 0; }
                value = countedUsed;
                break;
            default: error = DOS.Error.BadNumber; return 0;
        }
        if (required == 8)
        {
            APTR.WriteUInt32(storage, 0, (uint)(value >> 32));
            APTR.WriteUInt32(storage, 4, (uint)value);
        }
        else APTR.WriteUInt32(storage, 0, (uint)value);
        return -1;
    }

    private static void DispatchPacket(APTR state, APTR packet)
    {
        var p = new NativeFileSystemPlatform(state);
        var metadata = Metadata(state);
        var data = Data(state);
        var action = (DosPacketAction)APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.TypeOffset);
#if COPPERSTART_FULL_FILESYSTEM_EXTENSION
        if (action == DosPacketAction.Format || (uint)action == 0xF00067)
        {
            var formatted = FormatVolume(state, packet, out var formatError);
            APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result1Offset, formatted ? uint.MaxValue : 0);
            APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result2Offset, (uint)formatError);
            return;
        }
        if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset) == 0 &&
            action is not (DosPacketAction.IsFileSystem or DosPacketAction.CurrentVolume or
                DosPacketAction.DiskType or DosPacketAction.MoreCache or DosPacketAction.Inhibit or DosPacketAction.Flush))
        {
            APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result1Offset,
                action is DosPacketAction.Read or DosPacketAction.Write or DosPacketAction.Seek ? uint.MaxValue : 0);
            APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result2Offset, (uint)DOS.Error.NotADosDisk);
            return;
        }
#endif
        if (Smart(state) && SmartMutation(action)) InvalidateDirectoryCursors(state);
        if (Smart(state) && APTR.ReadUInt32(state, NativeFileSystemStateCodec.InhibitedOffset) != 0 &&
            SmartMediaAccess(action) && action is not
                (DosPacketAction.Inhibit or DosPacketAction.Info or DosPacketAction.DiskInfo))
        {
            APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result1Offset, action is DosPacketAction.Read or DosPacketAction.Write or
                DosPacketAction.Seek or DosPacketAction.SetFileSize ? uint.MaxValue : 0);
            APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result2Offset, (uint)DOS.Error.NoDisk);
            return;
        }
#if COPPERSTART_NATIVE_SFS_WRITES
        if (Smart(state) && APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRecoveryPendingOffset) != 0 && SmartMediaAccess(action))
        {
            InitializeSmartWriteWork(state, out var recovery);
            if (!NativeSfsExtension.Recover(state, ref recovery) || !p.Flush())
            {
                APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result1Offset, action is DosPacketAction.Read or DosPacketAction.Write or
                    DosPacketAction.Seek or
                    DosPacketAction.SetFileSize ? uint.MaxValue : 0);
                APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result2Offset, (uint)DOS.Error.DiskNotValidated);
                return;
            }
            APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartRecoveryPendingOffset, 0);
        }
#endif
        var a = APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument1Offset);
        var b = APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument2Offset);
        var c = APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument3Offset);
        int result = 0;
        var error = DOS.Error.None;
        var file = Object(state, a, true);
        var locked = Object(state, a, false);
#if COPPERSTART_PACKED_EXTENSION
        // Handles keep their original public volume identity across removal.
        // Cleanup remains legal, but an old handle must not read a new disk.
        if (!Smart(state) && APTR.ReadUInt32(state, 60) != 0 && SmartMediaAccess(action))
        {
            var volume = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.VolumeOffset));
            // FINDINPUT carries its base lock in Arg2; Arg1 is the new FH.
            var owned = action == DosPacketAction.FindInput ? Object(state, b, false) : file.IsNotNull ? file : locked;
            if (APTR.ReadUInt32(volume, 8) == 0 ||
                (owned.IsNotNull && APTR.ReadUInt32(owned, 36) != volume.Raw >> 2))
            {
                APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result1Offset,
                    action is DosPacketAction.Read or DosPacketAction.Write or DosPacketAction.Seek ? uint.MaxValue : 0);
                APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result2Offset, (uint)DOS.Error.NoDisk);
                return;
            }
        }
#endif
        var key = a == 0 ? RootKey(state) : locked.IsNotNull ? APTR.ReadUInt32(locked, 4) : 0;
        switch (action)
        {
            case DosPacketAction.LockRecord:
            case DosPacketAction.FreeRecord:
            case (DosPacketAction)DosNativeExtendedPacketAction.LockRecord64:
            case (DosPacketAction)DosNativeExtendedPacketAction.FreeRecord64:
                if (file.IsNull) { error = DOS.Error.InvalidLock; break; }
                // Classic Args 2/3 are unsigned values, while the wide packets
                // pass pointers to UQUADs. Both use the same owner and ranges.
                ulong rangeOffset = b, rangeLength = c;
                if (action is not (DosPacketAction.LockRecord or DosPacketAction.FreeRecord))
                {
                    var recordOffset = APTR.FromPointer(b);
                    var recordLength = APTR.FromPointer(c);
                    if (!p.IsMapped(recordOffset, 8) || !p.IsMapped(recordLength, 8))
                    { error = DOS.Error.BadNumber; break; }
                    rangeOffset = ((ulong)p.ReadUInt32(recordOffset) << 32) | p.ReadUInt32(recordOffset, 4);
                    rangeLength = ((ulong)p.ReadUInt32(recordLength) << 32) | p.ReadUInt32(recordLength, 4);
                }
                var recordPlatform = new NativeRecordLockPlatform();
                result = action is DosPacketAction.LockRecord or (DosPacketAction)DosNativeExtendedPacketAction.LockRecord64
                    ? NativeRecordLockCore.Lock(ref recordPlatform, state, file, APTR.ReadUInt32(file, 4), rangeOffset, rangeLength,
                        APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument4Offset),
                        APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument5Offset), packet, out error)
                    : NativeRecordLockCore.Unlock(ref recordPlatform, state, file, rangeOffset, rangeLength, out error);
                break;
            case DosPacketAction.NewReadLink:
                if (Smart(state)) { error = DOS.Error.ActionNotKnown; break; }
                if (key == 0) { result = -1; error = DOS.Error.InvalidLock; break; }
                result = AmigaFfsReadLinkCore.Read(ref p, key, APTR.FromPointer(b), APTR.FromPointer(c),
                    unchecked((int)APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument4Offset)),
                    APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset), metadata, data, out error);
                break;
            case DosPacketAction.AddNotify:
                result = NativeFileSystemNotifications.Add(state, APTR.FromPointer(a), out error); break;
            case DosPacketAction.RemoveNotify:
                result = NativeFileSystemNotifications.Remove(state, APTR.FromPointer(a)); break;
            case DosPacketAction.IsFileSystem: result = -1; break;
            case DosPacketAction.CurrentVolume: result = unchecked((int)(APTR.ReadUInt32(state, NativeFileSystemStateCodec.VolumeOffset) >> 2)); break;
            case DosPacketAction.DiskType: result = unchecked((int)APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset)); break;
            case DosPacketAction.Flush:
#if COPPERSTART_NATIVE_SFS_WRITES
                if (Smart(state)) { if (FlushSmart(state)) result = -1; else error = DOS.Error.DiskNotValidated; break; }
#endif
                if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.RamBlocksOffset) != 0 || p.Transfer(4, 0, 0, APTR.Null)) result = -1;
                else error = DOS.Error.DiskNotValidated;
                break;
            case DosPacketAction.Inhibit:
                InvalidateDirectoryCursors(state);
                if (a != 0)
                {
                    if (!(Smart(state) ? FlushSmart(state) : p.Flush())) { error = DOS.Error.DiskNotValidated; break; }
                    APTR.WriteUInt32(state, NativeFileSystemStateCodec.InhibitedOffset, 1);
                }
                else if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.InhibitedOffset) != 0)
                {
                    var buffered = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartWriteBufferOffset));
                    if (buffered.IsNotNull) APTR.WriteUInt32(buffered, 24, 0);
                    APTR.WriteUInt32(state, NativeFileSystemStateCodec.CacheTrackOffset, uint.MaxValue);
                    var cachedBlock = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.ExtraCacheHeadOffset));
                    while (cachedBlock.IsNotNull)
                    {
                        APTR.WriteUInt32(cachedBlock, 4, uint.MaxValue);
                        APTR.WriteUInt32(cachedBlock, 16, 0);
                        cachedBlock = APTR.FromPointer(APTR.ReadUInt32(cachedBlock, 0));
                    }
                    ReindexCache(state);
                    if (Smart(state) ? !NativeSfsExtension.Mount(state) :
                        !AmigaDiskFileSystemCore.Mount(ref p, metadata, out _))
                    { error = DOS.Error.DiskNotValidated; break; }
                    APTR.WriteUInt32(state, NativeFileSystemStateCodec.InhibitedOffset, 0);
                }
                result = -1;
                break;
            case DosPacketAction.MoreCache: result = MoreCache(state, unchecked((int)a), out error); break;
            case DosPacketAction.LocateObject:
            case DosPacketAction.FindInput:
                var baseToken = action == DosPacketAction.FindInput ? b : a;
                var baseLock = Object(state, baseToken, false);
                var directory = baseToken == 0 ? RootKey(state) : baseLock.IsNotNull ? APTR.ReadUInt32(baseLock, 4) : 0;
                if (directory == 0) { error = DOS.Error.InvalidLock; break; }
                var nameToken = action == DosPacketAction.FindInput ? c : b;
                var name = APTR.FromPointer(nameToken << 2);
                if (nameToken > 0x3FFFFFFF || !p.IsMapped(name, 1) || !p.IsMapped(name, (uint)p.ReadUInt8(name) + 1))
                { error = DOS.Error.InvalidComponentName; break; }
                if (action == DosPacketAction.FindInput && (a > 0x3FFFFFFF || !p.IsMapped(APTR.FromPointer(a << 2), FileHandle.Size)))
                { error = DOS.Error.BadNumber; break; }
                ulong foundSize = 0;
                bool foundDirectory = false;
                var found = Smart(state)
                    ? NativeSfsExtension.ResolveInformation(state, directory, APTR.FromPointer(name.Raw + 1), p.ReadUInt8(name),
                        out foundSize, out foundDirectory, out _, out error)
                    : AmigaDiskFileSystemCore.Resolve(ref p, directory, APTR.FromPointer(name.Raw + 1), p.ReadUInt8(name),
                        APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset), metadata, out error);
                if (found == 0) break;
                if (Smart(state))
                {
                    if (action == DosPacketAction.FindInput && foundDirectory)
                    { error = DOS.Error.ObjectWrongType; break; }
                }
                else
                {
                    if (action == DosPacketAction.FindInput && unchecked((int)p.ReadUInt32(metadata, ((int)p.BlockBytes - 512) + 508)) != -3)
                    { error = DOS.Error.ObjectWrongType; break; }
                    foundSize = p.ReadUInt32(metadata, ((int)p.BlockBytes - 512) + 324);
                }
                // Original DOS accepts access zero as a shared lookup.
                if (action == DosPacketAction.LocateObject && c != 0 && unchecked((int)c) != -2 && unchecked((int)c) != -1)
                { error = DOS.Error.BadNumber; break; }
                if (InUse(state, found, action == DosPacketAction.LocateObject && unchecked((int)c) == -1))
                { error = DOS.Error.ObjectInUse; break; }
                var obj = NewObject(state, found, foundSize, action == DosPacketAction.FindInput, action == DosPacketAction.FindInput ? 0 : unchecked((int)c));
                if (obj.IsNull) { error = DOS.Error.NoFreeStore; break; }
                if (action == DosPacketAction.LocateObject) result = unchecked((int)((obj.Raw + 20) >> 2));
                else
                {
                    var handle = APTR.FromPointer(a << 2);
                    APTR.WriteUInt32(handle, NativeDosFileHandleFieldCodec.PortOffset, APTR.ReadUInt32(state, NativeFileSystemStateCodec.PortOffset));
                    APTR.WriteUInt32(handle, NativeDosFileHandleFieldCodec.Argument1Offset, obj.Raw);
                    result = -1;
                }
                break;
            case DosPacketAction.FreeLock:
            case DosPacketAction.End:
                var retired = action == DosPacketAction.End ? file : locked;
                if (retired.IsNull && a != 0) { error = DOS.Error.InvalidLock; break; }
                var changedKey = retired.IsNotNull && action == DosPacketAction.End && APTR.ReadUInt32(retired, 40) != 0 ? APTR.ReadUInt32(retired, 4) : 0;
#if COPPERSTART_SFS_TRST_BUFFERED
                if (changedKey != 0 && Smart(state) && !FlushSmart(state))
                { error = DOS.Error.DiskNotValidated; break; }
#endif
                if (retired.IsNotNull && action == DosPacketAction.End && Smart(state))
                {
                    InitializeSmartWriteWork(state, out var reservationWork);
                    AmigaSmartFileSystemWriteCore.ReleaseOverwriteReservation(ref p, ref reservationWork, APTR.ReadUInt32(retired, 4));
                }
                if (retired.IsNotNull) FreeObject(state, retired);
                if (changedKey != 0) NativeFileSystemNotifications.Changed(state, changedKey);
                result = -1;
                break;
            case DosPacketAction.Read:
                result = -1;
                if (file.IsNull) { error = DOS.Error.InvalidLock; break; }
                var position = FilePosition(file);
                result = Smart(state)
                    ? NativeSfsExtension.Read(state, file, APTR.ReadUInt32(file, 4), position, APTR.FromPointer(b), c, out error)
                    : AmigaDiskFileSystemCore.Read(ref p, APTR.ReadUInt32(file, 4), APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset), (uint)position,
                        APTR.FromPointer(b), c, metadata, data, out error);
                if (result > 0) SetFilePosition(file, position + (uint)result);
                break;
            case DosPacketAction.Seek:
                result = -1;
                if (file.IsNull) { error = DOS.Error.InvalidLock; break; }
                if (NativeSfs2Bridge.IsSfs2(state))
                {
                    var before = FilePosition(file);
                    if (before > uint.MaxValue || !AmigaFilePositionCore.Offset64(before, FileSize(file),
                        (b & 0x80000000) == 0 ? 0u : uint.MaxValue, b, unchecked((int)c), uint.MaxValue, out var after) || after > FileSize(file))
                    { error = DOS.Error.SeekError; break; }
                    SetFilePosition(file, after); result = unchecked((int)before); break;
                }
                var old = APTR.ReadUInt32(file, 12);
                var size = APTR.ReadUInt32(file, 8);
                var mode = unchecked((int)c);
                if (!AmigaFilePositionCore.Seek(old, size, (b & 0x80000000) == 0 ? 0u : uint.MaxValue,
                    b, mode, out var nextPosition))
                { error = DOS.Error.SeekError; break; }
                if (nextPosition != old && Smart(state))
                {
                    InitializeSmartWriteWork(state, out var reservationWork);
                    AmigaSmartFileSystemWriteCore.ReleaseOverwriteReservation(ref p, ref reservationWork, APTR.ReadUInt32(file, 4));
                }
                APTR.WriteUInt32(file, 12, nextPosition);
                result = unchecked((int)old);
                break;
            case (DosPacketAction)DosNativeExtendedPacketAction.Seek64:
                if (file.IsNull) { error = DOS.Error.InvalidLock; break; }
                var seekInput = APTR.FromPointer(b);
                var seekOutput = APTR.FromPointer(APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument4Offset));
                if (!p.IsMapped(seekInput, 8) || !p.IsMapped(seekOutput, 8))
                { error = DOS.Error.BadNumber; break; }
                var previousPosition = FilePosition(file);
                if (!AmigaFilePositionCore.Offset64(previousPosition, FileSize(file),
                    p.ReadUInt32(seekInput), p.ReadUInt32(seekInput, 4), unchecked((int)c), FileSize(file), out var widePosition))
                { error = DOS.Error.SeekError; break; }
                if (widePosition != previousPosition && Smart(state))
                {
                    InitializeSmartWriteWork(state, out var reservationWork);
                    AmigaSmartFileSystemWriteCore.ReleaseOverwriteReservation(ref p, ref reservationWork, APTR.ReadUInt32(file, 4));
                }
                p.WriteUInt32(seekOutput, 0, (uint)(previousPosition >> 32));
                p.WriteUInt32(seekOutput, 4, (uint)previousPosition);
                SetFilePosition(file, widePosition);
                result = -1;
                break;
            case (DosPacketAction)DosNativeExtendedPacketAction.SetFileSize64:
#if COPPERSTART_NATIVE_SFS_WRITES
                if (!Smart(state)) { error = DOS.Error.ActionNotKnown; break; }
                if (file.IsNull) { error = DOS.Error.InvalidLock; break; }
                var resizeInput = APTR.FromPointer(b);
                var resizeOutput = APTR.FromPointer(APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument4Offset));
                if (!p.IsMapped(resizeInput, 8) || !p.IsMapped(resizeOutput, 8)) { error = DOS.Error.BadNumber; break; }
                if (!AmigaFilePositionCore.Offset64(FilePosition(file), FileSize(file), p.ReadUInt32(resizeInput),
                    p.ReadUInt32(resizeInput, 4), unchecked((int)c), ulong.MaxValue, out var requestedSize))
                { error = DOS.Error.SeekError; break; }
                var otherMinimum = SharedResizeMinimum(state, file);
                if (requestedSize < otherMinimum) requestedSize = otherMinimum;
                var maximumSize = NativeSfs2Bridge.IsSfs2(state) ? AmigaSmartFileSystem2WriteCore.MaxFileSize : uint.MaxValue;
                if (requestedSize > maximumSize) { error = DOS.Error.ObjectTooLarge; break; }
                if (NativeSfs2Bridge.IsSfs2(state))
                {
                    if (!NativeSfsExtension.ResizeWide(state, APTR.ReadUInt32(file, 4), requestedSize, out error)) break;
                }
                else
                {
                    InitializeSmartWriteWork(state, out var narrowResizeWork);
                    if (!NativeSfsExtension.Resize(state, ref narrowResizeWork,
                        APTR.ReadUInt32(file, 4), (uint)requestedSize))
                    { error = narrowResizeWork.Error; break; }
                }
                UpdateFileSizes(state, APTR.ReadUInt32(file, 4), requestedSize);
                if (FilePosition(file) > requestedSize) SetFilePosition(file, requestedSize);
                APTR.WriteUInt32(file, 40, 1);
                p.WriteUInt32(resizeOutput, 0, (uint)(requestedSize >> 32)); p.WriteUInt32(resizeOutput, 4, (uint)requestedSize);
                result = -1;
                break;
#else
                error = DOS.Error.DiskWriteProtected; break;
#endif
            case DosPacketAction.CopyDirectory:
            case DosPacketAction.CopyDirectoryFromFileHandle:
            case DosPacketAction.Parent:
            case DosPacketAction.ParentFileHandle:
                if (action == DosPacketAction.CopyDirectoryFromFileHandle || action == DosPacketAction.ParentFileHandle)
                    key = file.IsNotNull ? APTR.ReadUInt32(file, 4) : 0;
                if (key == 0) { error = DOS.Error.InvalidLock; break; }
                if (action == DosPacketAction.Parent || action == DosPacketAction.ParentFileHandle)
                {
                    if (key == RootKey(state)) break;
                    if (Smart(state))
                    {
                        if (!NativeSfsExtension.Describe(state, key, out _, out _, out key))
                        { error = DOS.Error.DiskNotValidated; break; }
                    }
                    else
                    {
                        if (!AmigaDiskFileSystemCore.Header(ref p, key, metadata)) { error = DOS.Error.DiskNotValidated; break; }
                        key = p.ReadUInt32(metadata, ((int)p.BlockBytes - 512) + 500);
                    }
                }
                var duplicate = NewObject(state, key, 0, false, -2);
                if (duplicate.IsNull) error = DOS.Error.NoFreeStore;
                else result = unchecked((int)((duplicate.Raw + 20) >> 2));
                break;
            case DosPacketAction.FileHandleFromLock:
                var transferred = Object(state, b, false);
                var target = APTR.FromPointer(a << 2);
                if (transferred.IsNull || a > 0x3FFFFFFF || !p.IsMapped(target, FileHandle.Size))
                { error = DOS.Error.InvalidLock; break; }
                var transferredKey = APTR.ReadUInt32(transferred, 4);
                ulong transferredSize;
                if (Smart(state))
                {
                    if (!NativeSfsExtension.Describe(state, transferredKey, out transferredSize, out var isDirectory, out _))
                    { error = DOS.Error.DiskNotValidated; break; }
                    if (isDirectory) { error = DOS.Error.ObjectWrongType; break; }
                }
                else
                {
                    if (!AmigaDiskFileSystemCore.Header(ref p, transferredKey, metadata))
                    { error = DOS.Error.DiskNotValidated; break; }
                    if (p.ReadUInt32(metadata, ((int)p.BlockBytes - 512) + 508) != 0xFFFFFFFD)
                    { error = DOS.Error.ObjectWrongType; break; }
                    transferredSize = p.ReadUInt32(metadata, ((int)p.BlockBytes - 512) + 324);
                }
                var openedFromLock = NewObject(state, transferredKey,
                    transferredSize, true,
                    APTR.ReadUInt32(transferred, 28) == uint.MaxValue ? 1 : 0);
                if (openedFromLock.IsNull) { error = DOS.Error.NoFreeStore; break; }
                p.WriteUInt32(target, 8, APTR.ReadUInt32(state, NativeFileSystemStateCodec.PortOffset));
                p.WriteUInt32(target, 36, openedFromLock.Raw);
                FreeObject(state, transferred);
                result = -1;
                break;
            case DosPacketAction.SameLock:
                var other = Object(state, b, false);
                if (key == 0 || (b != 0 && other.IsNull)) error = DOS.Error.InvalidLock;
                else result = key == (b == 0 ? RootKey(state) : APTR.ReadUInt32(other, 4)) ? 0 : 1;
                break;
            case DosPacketAction.QueryAttribute:
                result = QueryAttribute(state, (FileSystemQueryAttribute)a,
                    APTR.FromPointer(b), unchecked((int)c), out error);
                break;
            case DosPacketAction.ExamineObject64:
            case DosPacketAction.ExamineFileHandle64:
            case DosPacketAction.ExamineObject:
            case DosPacketAction.ExamineFileHandle:
                if (action is DosPacketAction.ExamineFileHandle or DosPacketAction.ExamineFileHandle64)
                    key = file.IsNotNull ? APTR.ReadUInt32(file, 4) : 0;
                if (key == 0) { error = DOS.Error.InvalidLock; break; }
                result = (Smart(state)
                    ? NativeSfsExtension.Examine(state, key, APTR.FromPointer(b << 2))
                    : AmigaDiskFileSystemCore.Examine(ref p, key, APTR.FromPointer(b << 2), metadata)) ? -1 : 0;
                if (result == 0) error = DOS.Error.DiskNotValidated;
                else if (!NativeSfs2Bridge.IsSfs2(state) && (action is DosPacketAction.ExamineObject64 or DosPacketAction.ExamineFileHandle64))
                    ExtendFileInfo(APTR.FromPointer(b << 2));
                break;
            case DosPacketAction.ExamineNext64:
            case DosPacketAction.ExamineNext:
                if (key == 0) { error = DOS.Error.InvalidLock; break; }
                error = Smart(state)
                    ? NativeSfsExtension.ExamineNext(state, locked, key, APTR.FromPointer(b << 2))
                    : NextDirectory(state, locked, key, APTR.FromPointer(b << 2), metadata, data);
                result = error == DOS.Error.None ? -1 : 0;
                if (result != 0 && action == DosPacketAction.ExamineNext64 && !NativeSfs2Bridge.IsSfs2(state))
                    ExtendFileInfo(APTR.FromPointer(b << 2));
                break;
            case DosPacketAction.Info:
            case DosPacketAction.DiskInfo:
                var info = APTR.FromPointer((action == DosPacketAction.Info ? b : a) << 2);
                if (!p.IsMapped(info, 36)) { error = DOS.Error.BadNumber; break; }
                var counted = Smart(state)
                    ? NativeSfsExtension.CountUsed(state, out var smartUsed)
                    : AmigaDiskFileSystemCore.CountUsed(ref p, metadata, data, out smartUsed);
                var usedBlocks = smartUsed;
                if (!counted || (!Smart(state) && !AmigaDiskFileSystemCore.Header(ref p, p.Geometry.RootBlock, metadata)))
                { error = DOS.Error.DiskNotValidated; break; }
                p.Clear(info, 36);
                p.WriteUInt32(info, 8, p.WriteProtected() ? 80u : Smart(state) ? 82u : p.ReadUInt32(metadata, ((int)p.BlockBytes - 512) + 312) != uint.MaxValue ? 81u : 82u);
                p.WriteUInt32(info, 12, Smart(state) ? p.BlockCount - p.ReservedBlocks - 1 : p.BlockCount);
                p.WriteUInt32(info, 16, usedBlocks);
                p.WriteUInt32(info, 20, p.BlockBytes);
                p.WriteUInt32(info, 24, APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset));
                p.WriteUInt32(info, 28, APTR.ReadUInt32(state, NativeFileSystemStateCodec.VolumeOffset) >> 2);
                result = -1;
                break;
            case DosPacketAction.FindOutput:
            case DosPacketAction.FindUpdate:
#if COPPERSTART_NATIVE_SFS_WRITES
                result = Smart(state) ? NativeSfsExtension.Packet(state, packet, action, out error) : OpenWrite(state, packet, action, out error);
#else
                result = OpenWrite(state, packet, action, out error);
#endif
                break;
            case DosPacketAction.Write:
                result = -1;
                if (file.IsNull) { error = DOS.Error.InvalidLock; break; }
                // MODE_OLDFILE also permits writes. The private access word
                // describes sharing, not file/device write protection.
#if COPPERSTART_NATIVE_SFS_WRITES
                if (Smart(state))
                {
                    result = WriteSmartFile(state, file, APTR.FromPointer(b), c, out error); break;
                }
#endif
                InitializeWriteWork(state, out var work);
                var write = new AmigaFfsWriteCore.Request { Header = APTR.ReadUInt32(file, 4), Position = APTR.ReadUInt32(file, 12), Buffer = APTR.FromPointer(b), Length = c };
                if (!AmigaFfsWriteCore.Write(ref p, ref work, ref write, out var written)) { error = work.Error; break; }
                APTR.WriteUInt32(file, 12, write.Position + written);
                if (write.Position + written > APTR.ReadUInt32(file, 8)) APTR.WriteUInt32(file, 8, write.Position + written);
                if (written != 0) APTR.WriteUInt32(file, 40, 1);
                result = (int)written;
                break;
            case DosPacketAction.DeleteObject:
            case DosPacketAction.RenameObject:
            case DosPacketAction.CreateDirectory:
#if COPPERSTART_NATIVE_SFS_WRITES
                result = Smart(state) ? NativeSfsExtension.Packet(state, packet, action, out error) : Mutate(state, packet, action, out error);
#else
                result = Mutate(state, packet, action, out error);
#endif
                break;
            case DosPacketAction.SetProtection:
            case DosPacketAction.SetComment:
            case DosPacketAction.SetDate:
#if COPPERSTART_NATIVE_SFS_WRITES
                result = Smart(state) ? NativeSfsExtension.Packet(state, packet, action, out error) : SetMetadata(state, packet, action, out error);
#else
                result = SetMetadata(state, packet, action, out error);
#endif
                break;
            case DosPacketAction.SetFileSize:
#if COPPERSTART_NATIVE_SFS_WRITES
                result = -1;
                if (!Smart(state)) { error = DOS.Error.DiskWriteProtected; break; }
                if (file.IsNull) { error = DOS.Error.InvalidLock; break; }
                if (NativeSfs2Bridge.IsSfs2(state))
                {
                    if (!AmigaFilePositionCore.Offset64(FilePosition(file), FileSize(file),
                        (b & 0x80000000) == 0 ? 0u : uint.MaxValue, b, unchecked((int)c), int.MaxValue, out var targetSize))
                    { error = DOS.Error.SeekError; break; }
                    var legacyMinimum = SharedResizeMinimum(state, file);
                    if (targetSize < legacyMinimum) targetSize = legacyMinimum;
                    if (targetSize > int.MaxValue) { error = DOS.Error.SeekError; break; }
                    if (!NativeSfsExtension.ResizeWide(state, APTR.ReadUInt32(file, 4), targetSize, out error)) break;
                    UpdateFileSizes(state, APTR.ReadUInt32(file, 4), targetSize);
                    if (FilePosition(file) > targetSize) SetFilePosition(file, targetSize);
                    APTR.WriteUInt32(file, 40, 1); result = (int)targetSize; break;
                }
                var resizeMode = unchecked((int)c);
                if (resizeMode < -1 || resizeMode > 1) { error = DOS.Error.SeekError; break; }
                var resizeOrigin = resizeMode == -1 ? 0u :
                    resizeMode == 0 ? APTR.ReadUInt32(file, 12) : APTR.ReadUInt32(file, 8);
                var resizeTarget = (long)resizeOrigin + unchecked((int)b);
                if (resizeTarget < 0 || resizeTarget > int.MaxValue) { error = DOS.Error.SeekError; break; }
                var sharedMinimum = SharedResizeMinimum(state, file);
                if ((ulong)resizeTarget < sharedMinimum) resizeTarget = (long)sharedMinimum;
                InitializeSmartWriteWork(state, out var resizeWork);
                if (!NativeSfsExtension.Resize(state, ref resizeWork,
                    APTR.ReadUInt32(file, 4), (uint)resizeTarget))
                { error = resizeWork.Error; break; }
                UpdateFileSizes(state, APTR.ReadUInt32(file, 4), (uint)resizeTarget);
                if (APTR.ReadUInt32(file, 12) > (uint)resizeTarget)
                    APTR.WriteUInt32(file, 12, (uint)resizeTarget);
                APTR.WriteUInt32(file, 40, 1);
                result = (int)resizeTarget;
                break;
#else
                result = -1; error = DOS.Error.DiskWriteProtected; break;
#endif
            case DosPacketAction.Format:
                error = DOS.Error.DiskWriteProtected;
                break;
            default:
                if ((int)action == AmigaSmartFileSystemTrimPacket.Action && !Smart(state))
                {
                    if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.InhibitedOffset) != 0) error = DOS.Error.NoDisk;
                    else result = NativeSfsTrim.Execute(state, packet, out error, ffs: true);
                    break;
                }
#if COPPERSTART_NATIVE_SFS_WRITES
                // Keep the existing switch and all normal packet branches intact.
                // Only an explicit unknown/custom action reaches maintenance.
                if ((int)action == AmigaSmartFileSystemTrimPacket.Action && Smart(state))
                    result = NativeSfsExtension.Packet(state, packet, action, out error);
                else
#endif
                    error = DOS.Error.ActionNotKnown;
                break;
        }
#if COPPERSTART_NATIVE_SFS_WRITES
        if (Smart(state) && error != DOS.Error.None && SmartMutation(action))
        {
            InitializeSmartWriteWork(state, out var recovery);
            APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartRecoveryPendingOffset, NativeSfsExtension.Recover(state, ref recovery) && p.Flush() ? 0u : 1u);
        }
#endif
        APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result1Offset, unchecked((uint)result));
        APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result2Offset, (uint)error);
    }

}

public struct NativeFileSystemPlatform : IAmigaBlockRangeFileSystemPlatform,
#if COPPERSTART_FULL_FILESYSTEM_EXTENSION
    IAmigaFormatFileSystemPlatform,
#endif
    IAmigaFileSystemDevicePlatform,
    IAmigaDiscardFileSystemPlatform, IAmigaScsiDirectPlatform
{
    private APTR state;
    public NativeFileSystemPlatform(APTR state)
    {
        this.state = state;
    }
    public uint BlockCount { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockCountOffset); }
    public uint BlockBytes { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockBytesOffset); }
    public uint ReservedBlocks { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => APTR.ReadUInt32(state, NativeFileSystemStateCodec.ReservedBlocksOffset); }

    // Device I/O also needs byte offsets, DMA restrictions and transfer limits.
    // Filesystem traversal uses the scalar getters above.
    public AmigaVolumeGeometry Geometry => new()
    {
        BlockCount = APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockCountOffset),
        BlockBytes = APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockBytesOffset),
        ReservedBlocks = APTR.ReadUInt32(state, NativeFileSystemStateCodec.ReservedBlocksOffset),
        SectorBytes = APTR.ReadUInt32(state, NativeFileSystemStateCodec.SectorBytesOffset),
        StartByte = ((ulong)APTR.ReadUInt32(state, NativeFileSystemStateCodec.StartByteHighOffset) << 32) |
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.StartByteLowOffset),
        TrackBlocks = APTR.ReadUInt32(state, NativeFileSystemStateCodec.TrackBlocksOffset),
        MaxTransfer = APTR.ReadUInt32(state, NativeFileSystemStateCodec.MaxTransferOffset),
        BufferMemoryType = APTR.ReadUInt32(state, NativeFileSystemStateCodec.BufferMemoryTypeOffset),
        Mask = APTR.ReadUInt32(state, NativeFileSystemStateCodec.MaskOffset),
    };
    public bool Transfer(ushort command, ulong byteOffset, uint bytes, APTR buffer)
    {
        var request = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.RequestOffset));
        APTR.WriteUInt16(request, NativeExecFieldCodec.IoCommandOffset, command);
        APTR.WriteUInt32(request, NativeExecFieldCodec.IoDataOffset, buffer.Raw);
        APTR.WriteUInt32(request, NativeExecFieldCodec.IoLengthOffset, bytes);
        APTR.WriteUInt32(request, NativeExecFieldCodec.IoByteOffset, (uint)byteOffset);
        APTR.WriteUInt32(request, NativeExecFieldCodec.IoActualOffset, (uint)(byteOffset >> 32));
        global::Amiga.Exec.DoIO(request);
        return APTR.ReadUInt8(request, NativeExecFieldCodec.IoErrorOffset) == 0 && APTR.ReadUInt32(request, NativeExecFieldCodec.IoActualOffset) == bytes;
    }

    public bool ScsiDirect(APTR command)
    {
        var request = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.RequestOffset));
        if (request.IsNull || APTR.ReadUInt32(state, NativeFileSystemStateCodec.RamBlocksOffset) != 0) return false;
        APTR.WriteUInt16(request, NativeExecFieldCodec.IoCommandOffset, 28); // HD_SCSICMD
        APTR.WriteUInt8(request, NativeExecFieldCodec.IoErrorOffset, 0);
        APTR.WriteUInt32(request, NativeExecFieldCodec.IoDataOffset, command.Raw);
        APTR.WriteUInt32(request, NativeExecFieldCodec.IoLengthOffset, AmigaScsiUnmap.ScsiCommandBytes);
        APTR.WriteUInt32(request, NativeExecFieldCodec.IoActualOffset, 0);
        APTR.WriteUInt32(request, NativeExecFieldCodec.IoByteOffset, 0);
        global::Amiga.Exec.DoIO(request);
        return APTR.ReadUInt8(request, NativeExecFieldCodec.IoErrorOffset) == 0;
    }

    public bool QueryDiscard(APTR control, out AmigaFileSystemDiscardLimits limits) =>
        AmigaScsiUnmap.Query(ref this, Geometry, control, out limits);

    public bool DiscardBlocks(APTR control, uint block, uint count)
    {
        var success = AmigaScsiUnmap.Discard(ref this, Geometry, control, block, count);
        // Failure may follow partial deallocation, so invalidate on either result.
        InvalidateBlocks(block, count);
        return success;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadUInt8(APTR address, int offset = 0) => APTR.ReadUInt8(address, offset);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ReadUInt16(APTR address, int offset = 0) => APTR.ReadUInt16(address, offset);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadUInt32(APTR address, int offset = 0) => APTR.ReadUInt32(address, offset);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt8(APTR address, int offset, byte value) => APTR.WriteUInt8(address, offset, value);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt16(APTR address, int offset, ushort value) => APTR.WriteUInt16(address, offset, value);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt32(APTR address, int offset, uint value) => APTR.WriteUInt32(address, offset, value);
    public bool IsMapped(APTR address, uint bytes)
    {
#if COPPERSTART_SFS_STANDALONE
        if (address.Raw > uint.MaxValue - bytes) return false;
#else
        if (address.Raw < 0x400 || address.Raw > uint.MaxValue - bytes) return false;
        var chipBytes = ReadUInt32(APTR.FromPointer(0x200), BootstrapAbi.ChipSizeOffset);
        if (address.Raw < chipBytes && bytes <= chipBytes - address.Raw) return true;
#endif
        var exec = APTR.FromPointer(ReadUInt32(APTR.FromPointer(4)));
        var list = APTR.FromPointer(exec.Raw + NativeExecFieldCodec.MemoryListOffset);
        var current = APTR.ReadUInt32(list, NativeExecFieldCodec.ListHeadOffset);
#if COPPERSTART_SFS_STANDALONE
        while (current != 0 && current != (list.Raw + NativeExecFieldCodec.ListTailOffset))
#else
        for (var guard = 0; current != 0 && current != (list.Raw + NativeExecFieldCodec.ListTailOffset) && guard < 64; guard++)
#endif
        {
            var header = APTR.FromPointer(current);
            if (address.Raw >= APTR.ReadUInt32(header, NativeExecFieldCodec.MemoryLowerOffset) && address.Raw + bytes <= APTR.ReadUInt32(header, NativeExecFieldCodec.MemoryUpperOffset)) return true;
            current = APTR.ReadUInt32(header, NativeExecFieldCodec.NodeSuccessorOffset);
        }
        return false;
    }
    public bool TryMappedRegion(APTR address, uint bytes, out uint lower, out uint upper)
    {
        lower = upper = 0;
#if COPPERSTART_SFS_STANDALONE
        if (address.Raw > uint.MaxValue - bytes) return false;
#else
        if (address.Raw < 0x400 || address.Raw > uint.MaxValue - bytes) return false;
        var chipBytes = ReadUInt32(APTR.FromPointer(0x200), BootstrapAbi.ChipSizeOffset);
        if (address.Raw < chipBytes && bytes <= chipBytes - address.Raw)
        { lower = 0x400; upper = chipBytes; return true; }
#endif
        var exec = APTR.FromPointer(ReadUInt32(APTR.FromPointer(4)));
        var list = APTR.FromPointer(exec.Raw + NativeExecFieldCodec.MemoryListOffset);
        var current = APTR.ReadUInt32(list, NativeExecFieldCodec.ListHeadOffset);
#if COPPERSTART_SFS_STANDALONE
        while (current != 0 && current != (list.Raw + NativeExecFieldCodec.ListTailOffset))
#else
        for (var guard = 0; current != 0 && current != (list.Raw + NativeExecFieldCodec.ListTailOffset) && guard < 64; guard++)
#endif
        {
            var header = APTR.FromPointer(current);
            var first = APTR.ReadUInt32(header, NativeExecFieldCodec.MemoryLowerOffset);
#if !COPPERSTART_SFS_STANDALONE
            // Match IsMapped's non-standalone lower-address restriction when
            // another address later reuses this returned region.
            if (first < 0x400) first = 0x400;
#endif
            var end = APTR.ReadUInt32(header, NativeExecFieldCodec.MemoryUpperOffset);
            if (address.Raw >= first && address.Raw + bytes <= end)
            { lower = first; upper = end; return true; }
            current = APTR.ReadUInt32(header, NativeExecFieldCodec.NodeSuccessorOffset);
        }
        return false;
    }
    public void Clear(APTR address, uint bytes)
    {
        var cursor = address;
        var remaining = bytes;
        if ((cursor.Raw & 1) != 0 && remaining != 0)
        { WriteUInt8(cursor, 0, 0); cursor = APTR.FromPointer(cursor.Raw + 1); remaining--; }
        while (remaining >= 32)
        {
            WriteUInt32(cursor, 0, 0); WriteUInt32(cursor, 4, 0); WriteUInt32(cursor, 8, 0); WriteUInt32(cursor, 12, 0);
            WriteUInt32(cursor, 16, 0); WriteUInt32(cursor, 20, 0); WriteUInt32(cursor, 24, 0); WriteUInt32(cursor, 28, 0);
            cursor = APTR.FromPointer(cursor.Raw + 32); remaining -= 32;
        }
        while (remaining >= 4)
        { WriteUInt32(cursor, 0, 0); cursor = APTR.FromPointer(cursor.Raw + 4); remaining -= 4; }
        while (remaining != 0)
        { WriteUInt8(cursor, 0, 0); cursor = APTR.FromPointer(cursor.Raw + 1); remaining--; }
    }
    public void Copy(APTR source, APTR destination, uint bytes)
    {
        // Exec's public copy primitive handles arbitrary alignment. Its overlap
        // behavior is unspecified, so retain the original forward byte copy for
        // overlapping ranges supplied through the guest-memory interface.
        if (bytes == 0 || source.Raw == destination.Raw) return;
        var distance = source.Raw > destination.Raw ? source.Raw - destination.Raw : destination.Raw - source.Raw;
        if (distance >= bytes)
        { CopyBlock(source, destination, bytes); return; }
        for (var i = 0u; i < bytes; i++) WriteUInt8(destination, (int)i, ReadUInt8(source, (int)i));
    }
    // Writable SFS uses a local copy loop for its journal and data paths.
    // MC68000 longword accesses need only even addresses; matching odd
    // alignment is peeled, and opposite-parity spans use byte accesses.
    // Overlapping or wrapping ranges retain the Exec fallback.
    private static void CopyBlock(APTR source, APTR destination, uint bytes)
    {
#if !COPPERSTART_NATIVE_SFS_WRITES
        // Classic uses the installed Exec copy vectors. The public Copy wrapper
        // retains its forward-byte behavior for overlapping caller ranges.
        if (bytes == 0 || source.Raw == destination.Raw) return;
        if (((source.Raw | destination.Raw | bytes) & 3) == 0)
            global::Amiga.Exec.CopyMemQuick(source, destination, bytes);
        else global::Amiga.Exec.CopyMem(source, destination, bytes);
#else
        if (bytes == 0 || source.Raw == destination.Raw) return;
        var aligned = ((source.Raw | destination.Raw | bytes) & 3) == 0;
        var distance = source.Raw > destination.Raw ? source.Raw - destination.Raw : destination.Raw - source.Raw;
        if (distance >= bytes &&
            bytes <= uint.MaxValue - source.Raw && bytes <= uint.MaxValue - destination.Raw)
        {
            var input = source.Raw;
            var output = destination.Raw;
            var remaining = bytes;
            if (((input ^ output) & 1) == 0)
            {
                if ((input & 1) != 0 && remaining != 0)
                {
                    APTR.WriteUInt8(APTR.FromPointer(output), 0, APTR.ReadUInt8(APTR.FromPointer(input), 0));
                    input++; output++; remaining--;
                }
                while (remaining >= 32)
                {
                    // Load groups before their stores so native code can combine
                    // contiguous accesses. Only the non-overlap path reaches here.
                    var word0 = APTR.ReadUInt32(APTR.FromPointer(input), 0);
                    var word4 = APTR.ReadUInt32(APTR.FromPointer(input), 4);
                    var word8 = APTR.ReadUInt32(APTR.FromPointer(input), 8);
                    var word12 = APTR.ReadUInt32(APTR.FromPointer(input), 12);
                    APTR.WriteUInt32(APTR.FromPointer(output), 0, word0);
                    APTR.WriteUInt32(APTR.FromPointer(output), 4, word4);
                    APTR.WriteUInt32(APTR.FromPointer(output), 8, word8);
                    APTR.WriteUInt32(APTR.FromPointer(output), 12, word12);
                    var word16 = APTR.ReadUInt32(APTR.FromPointer(input), 16);
                    var word20 = APTR.ReadUInt32(APTR.FromPointer(input), 20);
                    var word24 = APTR.ReadUInt32(APTR.FromPointer(input), 24);
                    var word28 = APTR.ReadUInt32(APTR.FromPointer(input), 28);
                    APTR.WriteUInt32(APTR.FromPointer(output), 16, word16);
                    APTR.WriteUInt32(APTR.FromPointer(output), 20, word20);
                    APTR.WriteUInt32(APTR.FromPointer(output), 24, word24);
                    APTR.WriteUInt32(APTR.FromPointer(output), 28, word28);
                    input += 32; output += 32; remaining -= 32;
                }
                while (remaining >= 4)
                {
                    APTR.WriteUInt32(APTR.FromPointer(output), 0, APTR.ReadUInt32(APTR.FromPointer(input), 0));
                    input += 4; output += 4; remaining -= 4;
                }
                if (remaining >= 2)
                {
                    APTR.WriteUInt16(APTR.FromPointer(output), 0, APTR.ReadUInt16(APTR.FromPointer(input), 0));
                    input += 2; output += 2; remaining -= 2;
                }
            }
            else
            {
                while (remaining >= 8)
                {
                    APTR.WriteUInt8(APTR.FromPointer(output), 0, APTR.ReadUInt8(APTR.FromPointer(input), 0));
                    APTR.WriteUInt8(APTR.FromPointer(output), 1, APTR.ReadUInt8(APTR.FromPointer(input), 1));
                    APTR.WriteUInt8(APTR.FromPointer(output), 2, APTR.ReadUInt8(APTR.FromPointer(input), 2));
                    APTR.WriteUInt8(APTR.FromPointer(output), 3, APTR.ReadUInt8(APTR.FromPointer(input), 3));
                    APTR.WriteUInt8(APTR.FromPointer(output), 4, APTR.ReadUInt8(APTR.FromPointer(input), 4));
                    APTR.WriteUInt8(APTR.FromPointer(output), 5, APTR.ReadUInt8(APTR.FromPointer(input), 5));
                    APTR.WriteUInt8(APTR.FromPointer(output), 6, APTR.ReadUInt8(APTR.FromPointer(input), 6));
                    APTR.WriteUInt8(APTR.FromPointer(output), 7, APTR.ReadUInt8(APTR.FromPointer(input), 7));
                    input += 8; output += 8; remaining -= 8;
                }
            }
            while (remaining != 0)
            {
                APTR.WriteUInt8(APTR.FromPointer(output), 0, APTR.ReadUInt8(APTR.FromPointer(input), 0));
                input++; output++; remaining--;
            }
            return;
        }
        if (aligned)
            global::Amiga.Exec.CopyMemQuick(source, destination, bytes);
        else global::Amiga.Exec.CopyMem(source, destination, bytes);
#endif
    }

    private bool CanTransferDirect(APTR buffer, uint bytes)
    {
        // These are mapped caller ranges or Exec-owned buffers. Only mounts
        // accepting ordinary public memory can bypass their allocated DMA
        // buffer; CHIP/24BITDMA/other memory requirements keep the bounce path.
        return APTR.ReadUInt32(state, NativeFileSystemStateCodec.BufferMemoryTypeOffset) == (uint)global::Amiga.Exec.MemoryFlags.Public &&
            NativeAmigaFileSystem.BufferFitsMask(buffer.Raw, bytes,
                APTR.ReadUInt32(state, NativeFileSystemStateCodec.MaskOffset));
    }

    private bool TransferBlock(bool write, uint block, APTR buffer)
    {
        // The mount owns immutable geometry. Read its scalar fields for the
        // common 512-byte request instead of constructing/copying an aggregate
        // for every cache lookup and sector. Preserve the general TD64 path.
        var start = APTR.ReadUInt32(state, NativeFileSystemStateCodec.StartByteLowOffset);
        if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockBytesOffset) == 512 &&
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.SectorBytesOffset) == 512 &&
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.StartByteHighOffset) == 0 &&
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.MaxTransferOffset) >= 512 &&
            start <= uint.MaxValue - 511u && block <= (uint.MaxValue - 511u - start) >> 9 &&
            buffer.Raw <= uint.MaxValue - 512u)
            return Transfer((ushort)(write ? 3 : 2), start + (block << 9), 512, buffer);
        return AmigaFileSystemDeviceCore.Transfer(ref this, Geometry, write, block, 1, buffer);
    }
    public bool WriteProtected()
    {
        if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.RamBlocksOffset) != 0) return false;
        var request = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.RequestOffset));
        APTR.WriteUInt16(request, NativeExecFieldCodec.IoCommandOffset, 15);
        global::Amiga.Exec.DoIO(request);
        return APTR.ReadUInt8(request, NativeExecFieldCodec.IoErrorOffset) != 0 || APTR.ReadUInt32(request, NativeExecFieldCodec.IoActualOffset) != 0;
    }
#if COPPERSTART_NATIVE_SFS_WRITES && !COPPERSTART_FFS_ONLY
    public bool Flush()
    {
        if (RedoOverlay.IsNotNull) return true;
        if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.RamBlocksOffset) != 0) return true;
        if (WriteQueue.IsNotNull && APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartDeviceDirtyOffset) == 0 &&
            AmigaFileSystemWriteCombiner.Count(ref this, WriteQueue) == 0) return true;
        if (!DrainWrites() || !Transfer(4, 0, 0, APTR.Null)) return false;
        if (WriteQueue.IsNotNull) WriteUInt32(WriteQueue, 20, 0);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartDeviceDirtyOffset, 0);
        return true;
    }

    private APTR WriteQueue { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartWriteCombinerOffset)); }

    private bool DrainWrites()
    {
        var queue = WriteQueue;
        var used = AmigaFileSystemWriteCombiner.Count(ref this, queue);
        if (used == 0) return true;
        AmigaFileSystemWriteCombiner.Sort(ref this, queue);
        var ordinal = 0u;
        while (ordinal < used)
        {
            var source = AmigaFileSystemWriteCombiner.Run(ref this, queue, ordinal, out var block, out var count);
            if (TransferData(true, block, count, source) != count) return false;
            ordinal += count;
        }
        // Keep ALL images after an error, even the completed prefix. Recovery
        // replaces queued home blocks with their undo images before retrying.
        AmigaFileSystemWriteCombiner.Discard(ref this, queue);
        return true;
    }

    private bool QueueWrites(uint block, uint count, APTR source)
    {
        var queue = WriteQueue;
        var bytes = APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockBytesOffset);
        var completed = 0u;
        while (completed < count)
        {
            var length = count - completed;
            var capacity = ReadUInt32(queue, 4);
            if (length > capacity) length = capacity;
            var input = APTR.FromPointer(source.Raw + completed * bytes);
            if (!AmigaFileSystemWriteCombiner.TryStore(ref this, queue, block + completed, length, input))
            {
                // Capacity pressure sends writes without a durability barrier.
                if (!DrainWrites() || !AmigaFileSystemWriteCombiner.TryStore(ref this, queue, block + completed, length, input)) return false;
            }
            completed += length;
        }
        return count != 0;
    }
#else
    public bool Flush() => APTR.ReadUInt32(state, NativeFileSystemStateCodec.RamBlocksOffset) != 0 ||
        Transfer(4, 0, 0, APTR.Null);
#endif
    public uint ReadDataBlocks(uint block, uint count, APTR destination)
    {
        if (RedoOverlay.IsNotNull) return AmigaFileSystemBlockIo.Read(ref this, block, count, destination);
#if COPPERSTART_NATIVE_SFS_WRITES
        var batch = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartTransactionBufferOffset));
        if (batch.IsNotNull && ReadUInt32(batch) != 0)
            return AmigaFileSystemBlockIo.Read(ref this, block, count, destination);
        var dirty = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartWriteBufferOffset));
        if (AmigaSmartFileSystemWriteBuffer.Intersects(ref this, dirty,
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockBytesOffset), block, count))
            return AmigaFileSystemBlockIo.Read(ref this, block, count, destination);
#endif
#if COPPERSTART_NATIVE_SFS_WRITES && !COPPERSTART_FFS_ONLY
        if (AmigaFileSystemWriteCombiner.Intersects(ref this, WriteQueue, block, count))
            return AmigaFileSystemBlockIo.Read(ref this, block, count, destination);
#endif
        if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.RamBlocksOffset) != 0)
            return AmigaFileSystemBlockIo.Read(ref this, block, count, destination);
        return TransferData(false, block, count, destination);
    }

    public bool WriteDataBlocks(uint block, uint count, APTR source)
    {
        if (RedoOverlay.IsNotNull) return AmigaFileSystemBlockIo.Write(ref this, block, count, source);
        if (block < APTR.ReadUInt32(state, NativeFileSystemStateCodec.ReservedBlocksOffset)) return false;
        if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.RamBlocksOffset) != 0)
            return AmigaFileSystemBlockIo.Write(ref this, block, count, source);
        InvalidateBlocks(block, count);
#if COPPERSTART_NATIVE_SFS_WRITES && !COPPERSTART_FFS_ONLY
        if (WriteQueue.IsNotNull)
        {
            var total = APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockCountOffset);
            var bytes = APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockBytesOffset);
            if (block >= total || count > total - block || bytes == 0 ||
                count > (uint.MaxValue - source.Raw) / bytes) return false;
            // Mark before issuing I/O: an error can still complete a prefix.
            if (count != 0) APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartDeviceDirtyOffset, 1);
            var queue = WriteQueue;
            var bulk = ReadUInt32(queue, 20); var budget = ReadUInt32(queue, 4) >> 1;
            // Retain short writes for joining with the journal. Limit copying
            // bulk data to half a queue per barrier interval; long streams then
            // use the caller's DMA-safe buffer directly. Pending overlapping
            // blocks must still be replaced, including after an operation fails.
            if (count == 1 || (bulk < budget && count <= budget - bulk) ||
                AmigaFileSystemWriteCombiner.Intersects(ref this, queue, block, count))
            {
                if (!QueueWrites(block, count, source)) return false;
                if (count > 1 && bulk < budget) WriteUInt32(queue, 20, count > budget - bulk ? budget : bulk + count);
                return true;
            }
        }
#endif
        return count != 0 && TransferData(true, block, count, source) == count;
    }

    // Unreachable data can be sent directly even for a short run. It is not
    // useful to copy/sort it into the metadata queue merely to flush that queue
    // at this transaction's publication barrier. Keep overlapping queued writes
    // ordered, and mark a possibly completed prefix dirty before device I/O.
    public bool SupportsUnpublishedDataWrites { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => true; }

    // WriteUnpublishedDataBlocks reaches the device before acknowledging data;
    // the device enforces the same hardware protection queried by WriteProtected.
    public bool UnpublishedDataWritesEnforceProtection { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => true; }
    public bool WriteUnpublishedDataBlocks(uint block, uint count, APTR source)
    {
        if (RedoOverlay.IsNotNull || count == 0 ||
            block < APTR.ReadUInt32(state, NativeFileSystemStateCodec.ReservedBlocksOffset)) return false;
        if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.RamBlocksOffset) != 0)
            return WriteDataBlocks(block, count, source);
#if COPPERSTART_NATIVE_SFS_WRITES && !COPPERSTART_FFS_ONLY
        if (AmigaFileSystemWriteCombiner.Intersects(ref this, WriteQueue, block, count) && !DrainWrites()) return false;
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartDeviceDirtyOffset, 1);
#endif
        // This transfer changes payload bytes, not directory entries or extent
        // mappings. The logical mutation boundary invalidates those cursors.
        // Physical images still expire before I/O, including a failed prefix.
        InvalidateBlockImages(block, count);
        return TransferData(true, block, count, source) == count;
    }

    public bool SupportsRetainedBlockWrites => true;
    public bool WriteRetainedBlock(uint block, APTR source)
    {
        if (RedoOverlay.IsNotNull || block < ReservedBlocks || block >= BlockCount) return false;
        if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.RamBlocksOffset) != 0)
            return WriteBlock(block, source);
#if COPPERSTART_NATIVE_SFS_WRITES && !COPPERSTART_FFS_ONLY
        if (AmigaFileSystemWriteCombiner.Intersects(ref this, WriteQueue, block, 1) && !DrainWrites()) return false;
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartDeviceDirtyOffset, 1);
#endif
        // Retain a matching LRU slot while replacing its bytes. Removing and
        // reinserting the same key needlessly walks and rewires both lists.
        // Volume views still expire before I/O, and a failed/partial write
        // forgets the old image so it cannot masquerade as the device state.
        var cached = FindCached(block);
        NativeAmigaFileSystem.InvalidateDirectoryCursors(state);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.CacheTrackOffset, uint.MaxValue);
        var bytes = BlockBytes;
        var input = source;
        if (!CanTransferDirect(input, bytes))
        {
            input = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheOffset));
            CopyBlock(source, input, bytes);
        }
        if (!TransferBlock(true, block, input))
        {
            if (cached.IsNotNull) ForgetCached(cached);
            return false;
        }
        if (cached.IsNotNull)
        {
            CopyBlock(source, APTR.FromPointer(cached.Raw + NativeAmigaFileSystem.ExtraCacheHeaderBytes), bytes);
            APTR.WriteUInt32(cached, NativeFileSystemCacheEntryCodec.VerificationOffset, 0);
            TouchCached(cached);
        }
        else CacheBlock(block, 0, source, bytes);
        return true;
    }

    // Streaming data bypasses the metadata LRU. Preserve the DMA bounce path,
    // MaxTransfer and TD64 rules; a completed prefix is the only read result
    // exposed after a later device request fails.
    private uint TransferData(bool write, uint block, uint count, APTR caller)
    {
        var totalBlocks = APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockCountOffset);
        var blockBytes = APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockBytesOffset);
        if (blockBytes == 0 || block >= totalBlocks || count > totalBlocks - block) return 0;
        if (blockBytes == 512)
        {
            // Bound the product before shifting. Dividing the large remaining
            // address space otherwise takes the full 32-bit software path.
            if (count > (uint.MaxValue >> 9) || caller.Raw > uint.MaxValue - (count << 9)) return 0;
        }
        else if (count > (uint.MaxValue - caller.Raw) / blockBytes) return 0;
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.CacheTrackOffset, uint.MaxValue);
        var maximumBytes = APTR.ReadUInt32(state, NativeFileSystemStateCodec.MaxTransferOffset);
        if (blockBytes == 512 && count != 0 && count <= maximumBytes >> 9 &&
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.SectorBytesOffset) == 512 &&
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.StartByteHighOffset) == 0)
        {
            var length = count << 9;
            var start = APTR.ReadUInt32(state, NativeFileSystemStateCodec.StartByteLowOffset);
            if (start <= uint.MaxValue - (length - 1) &&
                block <= (uint.MaxValue - (length - 1) - start) >> 9 && CanTransferDirect(caller, length))
            {
                // These guards already prove a complete direct 32-bit request.
                // Fill its packet here instead of widening the offset through
                // the general TD64 call ABI. Preserve DoIO and exact completion.
                var request = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.RequestOffset));
                APTR.WriteUInt16(request, NativeExecFieldCodec.IoCommandOffset, (ushort)(write ? 3 : 2));
                APTR.WriteUInt32(request, NativeExecFieldCodec.IoDataOffset, caller.Raw);
                APTR.WriteUInt32(request, NativeExecFieldCodec.IoLengthOffset, length);
                APTR.WriteUInt32(request, NativeExecFieldCodec.IoByteOffset, start + (block << 9));
                APTR.WriteUInt32(request, NativeExecFieldCodec.IoActualOffset, 0);
                global::Amiga.Exec.DoIO(request);
                return APTR.ReadUInt8(request, NativeExecFieldCodec.IoErrorOffset) == 0 &&
                    APTR.ReadUInt32(request, NativeExecFieldCodec.IoActualOffset) == length ? count : 0;
            }
        }
        return TransferDataChunked(write, block, count, caller, blockBytes, maximumBytes);
    }

    // Keep the split/bounce/TD64 path outside the common request's register
    // frame. The complete caller range and volume bounds were checked above.
    private uint TransferDataChunked(bool write, uint block, uint count, APTR caller, uint blockBytes, uint maximumBytes)
    {
        var maximum = blockBytes == 512 ? maximumBytes >> 9 : maximumBytes / blockBytes;
        // A device sector may be smaller than a filesystem block. The general
        // transfer routine can split even a single block in that case.
        if (maximum == 0) maximum = 1;
        var completed = 0u;
        while (completed < count)
        {
            var blocks = count - completed;
            if (blocks > maximum) blocks = maximum;
            var target = APTR.FromPointer(caller.Raw + completed * blockBytes);
            var bytes = blocks * blockBytes;
            var direct = CanTransferDirect(target, bytes);
            var buffer = target;
            if (!direct)
            {
                var capacity = APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheBlocksOffset);
                if (capacity == 0) return completed;
                if (blocks > capacity) blocks = capacity;
                bytes = blocks * blockBytes;
                buffer = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheOffset));
                if (write) CopyBlock(target, buffer, bytes);
            }
            var start = APTR.ReadUInt32(state, NativeFileSystemStateCodec.StartByteLowOffset);
            var sector = block + completed;
            bool success;
            if (blockBytes == 512 && APTR.ReadUInt32(state, NativeFileSystemStateCodec.SectorBytesOffset) == 512 &&
                APTR.ReadUInt32(state, NativeFileSystemStateCodec.StartByteHighOffset) == 0 &&
                bytes <= APTR.ReadUInt32(state, NativeFileSystemStateCodec.MaxTransferOffset) &&
                start <= uint.MaxValue - (bytes - 1) && sector <= (uint.MaxValue - (bytes - 1) - start) >> 9)
                success = Transfer((ushort)(write ? 3 : 2), start + (sector << 9), bytes, buffer);
            else success = AmigaFileSystemDeviceCore.Transfer(ref this, Geometry, write, sector, blocks, buffer);
            if (!success) return completed;
            if (!write && !direct) CopyBlock(buffer, target, bytes);
            completed += blocks;
        }
        return completed;
    }

    private void InvalidateBlocks(uint block, uint count)
    {
        NativeAmigaFileSystem.InvalidateDirectoryCursors(state);
        InvalidateBlockImages(block, count);
    }

    internal void InvalidateMedia() => InvalidateBlocks(0, uint.MaxValue);

    private void InvalidateBlockImages(uint block, uint count)
    {
#if COPPERSTART_SFS_STANDALONE && COPPERSTART_SFS_TRST_WRITES && !COPPERSTART_NATIVE_SFS2
        // A short range occupies at most two 64-block pages. The reference
        // counts track every hashed entry, including unverified prefetches,
        // aliases, replacement, eviction and cache resizing. No I/O occurs
        // between inspecting this summary and the normal invalidation below.
        if (count != 0 && count <= 64 && count <= uint.MaxValue - block)
        {
            var pages = (1u << (int)((block >> 6) & 31)) |
                (1u << (int)(((block + count - 1) >> 6) & 31));
            if ((APTR.ReadUInt32(state, NativeAmigaFileSystem.CachePageMaskOffset) & pages) == 0)
            {
                APTR.WriteUInt32(state, NativeFileSystemStateCodec.CacheTrackOffset, uint.MaxValue);
                return;
            }
        }
#endif
        if (count == 1)
        {
            var cached = FindCached(block);
            if (cached.IsNotNull) ForgetCached(cached);
        }
        else if (count < 64 && count < APTR.ReadUInt32(state, NativeFileSystemStateCodec.ExtraCacheCountOffset))
        {
            // Small streaming ranges touch only their hash buckets. Snapshot
            // each link before removing an entry from both the hash and LRU.
            var first = state.Raw + (uint)NativeFileSystemStateCodec.CacheHashOffset;
            var end = first + 64 * 4;
            var cursor = first + (block & 63) * 4;
            var remaining = count;
            while (remaining != 0)
            {
                var cached = APTR.FromPointer(APTR.ReadUInt32(APTR.FromPointer(cursor), 0));
                while (cached.IsNotNull)
                {
                    var next = APTR.FromPointer(ReadUInt32(cached, 12));
                    var key = ReadUInt32(cached, 4);
                    if (key >= block && key - block < count) ForgetCached(cached);
                    cached = next;
                }
                cursor += 4;
                if (cursor == end) cursor = first;
                remaining--;
            }
        }
        else
        {
            var cached = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.ExtraCacheHeadOffset));
            var entries = APTR.ReadUInt32(state, NativeFileSystemStateCodec.ExtraCacheCountOffset);
            for (var index = 0u; index < entries && cached.IsNotNull; index++)
            {
                var next = APTR.FromPointer(ReadUInt32(cached, 0));
                var key = ReadUInt32(cached, 4);
                if (key >= block && key - block < count) ForgetCached(cached);
                cached = next;
            }
        }
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.CacheTrackOffset, uint.MaxValue);
    }

    public bool WriteBlock(uint block, APTR source)
    {
#if COPPERSTART_NATIVE_SFS_WRITES
        // Redo overlays exist only when this platform can stage SFS writes.
        if (RedoOverlay.IsNotNull) return AmigaSmartFileSystemRedoArena.Store(ref this, RedoOverlay, block, source);
#endif
        if (block < APTR.ReadUInt32(state, NativeFileSystemStateCodec.ReservedBlocksOffset) ||
            block >= APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockCountOffset)) return false;
        InvalidateBlocks(block, 1);
        if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.RamBlocksOffset) != 0) return WriteRamBlock(block, source);
#if COPPERSTART_NATIVE_SFS_WRITES && !COPPERSTART_FFS_ONLY
        if (WriteQueue.IsNotNull)
        {
            APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartDeviceDirtyOffset, 1);
            if (!QueueWrites(block, 1, source)) return false;
            // Keep written images in the bounded LRU across queue drains. The
            // queue remains authoritative until Flush succeeds; verification
            // is cleared because this is a new image, even for the same key.
            if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset) == AmigaSmartFileSystemCore.DosType)
                CacheBlock(block, 0, source, BlockBytes);
            return true;
        }
#endif
        var buffer = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheOffset));
        var bytes = APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockBytesOffset);
        if (CanTransferDirect(source, bytes)) buffer = source;
        else CopyBlock(source, buffer, bytes);
        return TransferBlock(true, block, buffer);
    }

    private APTR FindCached(uint block)
    {
        var cached = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheHashOffset + (int)(block & 63) * 4));
        while (cached.IsNotNull && ReadUInt32(cached, 4) != block)
            cached = APTR.FromPointer(ReadUInt32(cached, 12));
        return cached;
    }

    private void Unhash(APTR entry)
    {
        var key = APTR.ReadUInt32(entry, NativeFileSystemCacheEntryCodec.KeyOffset);
        if (key == uint.MaxValue) return;
        var link = APTR.FromPointer(state.Raw + (uint)NativeAmigaFileSystem.ExtraCacheHashOffset + (key & 63) * 4);
        while (ReadUInt32(link) != 0)
        {
            var current = APTR.FromPointer(ReadUInt32(link));
            if (current == entry)
            {
                WriteUInt32(link, 0, APTR.ReadUInt32(entry, NativeFileSystemCacheEntryCodec.HashNextOffset));
#if COPPERSTART_SFS_STANDALONE && COPPERSTART_SFS_TRST_WRITES && !COPPERSTART_NATIVE_SFS2
                NativeAmigaFileSystem.RemoveCachePage(state, key);
#endif
                return;
            }
            link = APTR.FromPointer(current.Raw + 12);
        }
    }

    private void TouchCached(APTR entry)
    {
        var previous = APTR.FromPointer(APTR.ReadUInt32(entry, NativeFileSystemCacheEntryCodec.PreviousOffset));
        if (previous.IsNull) return;
        var next = APTR.FromPointer(APTR.ReadUInt32(entry, NativeFileSystemCacheEntryCodec.NextOffset));
        WriteUInt32(previous, 0, next.Raw);
        if (next.IsNotNull) WriteUInt32(next, 8, previous.Raw);
        else APTR.WriteUInt32(state, NativeFileSystemStateCodec.ExtraCacheTailOffset, previous.Raw);
        var head = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.ExtraCacheHeadOffset));
        WriteUInt32(head, 8, entry.Raw); APTR.WriteUInt32(entry, NativeFileSystemCacheEntryCodec.NextOffset, head.Raw); APTR.WriteUInt32(entry, NativeFileSystemCacheEntryCodec.PreviousOffset, 0);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.ExtraCacheHeadOffset, entry.Raw);
    }

    private void ForgetCached(APTR entry)
    {
        Unhash(entry);
        APTR.WriteUInt32(entry, NativeFileSystemCacheEntryCodec.KeyOffset, uint.MaxValue); APTR.WriteUInt32(entry, NativeFileSystemCacheEntryCodec.VerificationOffset, 0);
        var next = APTR.FromPointer(APTR.ReadUInt32(entry, NativeFileSystemCacheEntryCodec.NextOffset));
        if (next.IsNull) return;
        var previous = APTR.FromPointer(APTR.ReadUInt32(entry, NativeFileSystemCacheEntryCodec.PreviousOffset));
        if (previous.IsNotNull) WriteUInt32(previous, 0, next.Raw);
        else APTR.WriteUInt32(state, NativeFileSystemStateCodec.ExtraCacheHeadOffset, next.Raw);
        WriteUInt32(next, 8, previous.Raw);
        var tail = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.ExtraCacheTailOffset));
        WriteUInt32(tail, 0, entry.Raw); APTR.WriteUInt32(entry, NativeFileSystemCacheEntryCodec.PreviousOffset, tail.Raw); APTR.WriteUInt32(entry, NativeFileSystemCacheEntryCodec.NextOffset, 0);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.ExtraCacheTailOffset, entry.Raw);
    }

    public APTR RedoOverlay
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRedoOverlayOffset));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartRedoOverlayOffset, value.Raw);
    }

    public bool ReadBlock(uint block, APTR destination) => ReadCached(block, 0, destination);
    public bool HasAcknowledgedCheckpoint(uint rootContainer)
    {
#if COPPERSTART_SFS_TRST_BUFFERED
        var group = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRangeBufferOffset));
        if (AmigaSmartFileSystemWriteCore.HasAcknowledgedTrstCheckpoint(ref this, group, rootContainer)) return true;
#endif
#if COPPERSTART_NATIVE_SFS_WRITES && (!COPPERSTART_SFS_STANDALONE || !COPPERSTART_SFS_TRST_WRITES || COPPERSTART_NATIVE_SFS2)
        var dirty = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartWriteBufferOffset));
        return AmigaSmartFileSystemWriteBuffer.HasAcknowledgedCheckpoint(ref this, dirty, rootContainer);
#else
        return false;
#endif
    }

    public APTR ReadRetainedBlock(uint block)
    {
        if (block >= BlockCount || RedoOverlay.IsNotNull ||
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.RamBlocksOffset) != 0) return APTR.Null;
#if COPPERSTART_NATIVE_SFS_WRITES
        // Callers can always use ReadBlock when a logical overlay is active.
        // Do not expose a physical cache image beneath acknowledged dirty data.
        var dirty = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartWriteBufferOffset));
        if (dirty.IsNotNull && ReadUInt32(dirty, AmigaSmartFileSystemWriteBuffer.HiddenOffset) == 0 &&
            AmigaSmartFileSystemWriteBuffer.Count(ref this, dirty) != 0) return APTR.Null;
        var batch = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartTransactionBufferOffset));
        if (batch.IsNotNull && ReadUInt32(batch) != 0) return APTR.Null;
#endif
#if COPPERSTART_NATIVE_SFS_WRITES && !COPPERSTART_FFS_ONLY
        var queued = AmigaFileSystemWriteCombiner.ReadView(ref this, WriteQueue, block);
        if (queued.IsNotNull) return queued;
#endif
        var entry = FindCached(block);
        if (entry.IsNull) return APTR.Null;
        TouchCached(entry);
        return APTR.FromPointer(entry.Raw + NativeAmigaFileSystem.ExtraCacheHeaderBytes);
    }
    public bool ReadBlocks(uint block, uint count, APTR destination) => ReadDataBlocks(block, count, destination) == count;
    public bool WriteBlocks(uint block, uint count, APTR source) => WriteDataBlocks(block, count, source);
    public bool ReadMetadataBlock(uint block, uint id, APTR destination) => ReadCached(block, id, destination);

    public APTR ReadMetadataView(uint block, uint id, APTR scratch)
    {
        if (block >= APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockCountOffset)) return APTR.Null;
        var bytes = APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockBytesOffset);
#if COPPERSTART_NATIVE_SFS_WRITES
        if (RedoOverlay.IsNotNull)
        {
            var image = AmigaSmartFileSystemRedoArena.MetadataView(ref this, RedoOverlay, block, id, false, out var found);
            if (found) return image;
        }
#endif
// Classic standalone TRST never allocates the legacy dirty or undo
        // buffers (including during CSUN recovery). Match their construction
        // guard here; other handler configurations retain both overlays.
#if COPPERSTART_NATIVE_SFS_WRITES && (!COPPERSTART_SFS_STANDALONE || !COPPERSTART_SFS_TRST_WRITES || COPPERSTART_NATIVE_SFS2)
        var dirty = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartWriteBufferOffset));
        if (dirty.IsNotNull && ReadUInt32(dirty, AmigaSmartFileSystemWriteBuffer.HiddenOffset) == 0)
        {
            // Keep the frequent owned-image lookup in the native caller. This
            // avoids a second native frame and out-parameter spills per view.
            var slot = APTR.FromPointer(ReadUInt32(dirty, (int)(AmigaSmartFileSystemWriteBuffer.HashOffset + (block & 63) * 4)));
            while (slot.IsNotNull && ReadUInt32(slot) != block)
                slot = APTR.FromPointer(ReadUInt32(slot, AmigaSmartFileSystemWriteBuffer.HashLinkOffset));
            if (slot.IsNotNull)
            {
                var source = APTR.FromPointer(slot.Raw + AmigaSmartFileSystemWriteBuffer.EntryHeader);
                if (ReadUInt32(slot, AmigaSmartFileSystemWriteBuffer.VerificationOffset) != id)
                {
                    if (!AmigaFileSystemBlockIo.ValidateMetadata(ref this, block, id, source, bytes)) return APTR.Null;
                    WriteUInt32(slot, AmigaSmartFileSystemWriteBuffer.VerificationOffset, id);
                }
                return source;
            }
        }
        var batch = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartTransactionBufferOffset));
        if (batch.IsNotNull && ReadUInt32(batch) != 0)
            return ReadCached(block, id, scratch) ? scratch : APTR.Null;
#endif
#if COPPERSTART_NATIVE_SFS_WRITES && !COPPERSTART_FFS_ONLY
        var queued = AmigaFileSystemWriteCombiner.ReadMetadataView(ref this, WriteQueue, block, id, out var queueFound);
        if (queueFound) return queued;
#endif
        var entry = FindCached(block);
        if (entry.IsNotNull)
        {
            var source = APTR.FromPointer(entry.Raw + NativeAmigaFileSystem.ExtraCacheHeaderBytes);
            if (APTR.ReadUInt32(entry, NativeFileSystemCacheEntryCodec.VerificationOffset) != id)
            {
                if (!AmigaFileSystemBlockIo.ValidateMetadata(ref this, block, id, source, bytes)) return APTR.Null;
                APTR.WriteUInt32(entry, NativeFileSystemCacheEntryCodec.VerificationOffset, id);
            }
            TouchCached(entry);
            return source;
        }
        return ReadPhysicalCached(block, id, scratch);
    }

    private bool ReadCached(uint block, uint id, APTR destination)
    {
        if (block >= APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockCountOffset)) return false;
        var blockBytes = APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockBytesOffset);
#if COPPERSTART_NATIVE_SFS_WRITES
        if (RedoOverlay.IsNotNull)
        {
            APTR redoImage;
            bool found;
            if (id == 0)
            {
                if (!AmigaSmartFileSystemRedoArena.TryFind(ref this, RedoOverlay, block, out redoImage)) return false;
                found = redoImage.IsNotNull;
            }
            else redoImage = AmigaSmartFileSystemRedoArena.MetadataView(ref this, RedoOverlay, block, id, false, out found);
            if (found)
            {
                if (redoImage.IsNull) return false;
                CopyBlock(redoImage, destination, blockBytes); return true;
            }
        }
#endif
// Classic standalone TRST never allocates the legacy dirty or undo
        // buffers (including during CSUN recovery). Match their construction
        // guard here; other handler configurations retain both overlays.
#if COPPERSTART_NATIVE_SFS_WRITES && (!COPPERSTART_SFS_STANDALONE || !COPPERSTART_SFS_TRST_WRITES || COPPERSTART_NATIVE_SFS2)
        var dirty = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartWriteBufferOffset));
        if (dirty.IsNotNull && ReadUInt32(dirty, AmigaSmartFileSystemWriteBuffer.HiddenOffset) == 0)
        {
            var slot = APTR.FromPointer(ReadUInt32(dirty, (int)(AmigaSmartFileSystemWriteBuffer.HashOffset + (block & 63) * 4)));
            while (slot.IsNotNull && ReadUInt32(slot) != block)
                slot = APTR.FromPointer(ReadUInt32(slot, AmigaSmartFileSystemWriteBuffer.HashLinkOffset));
            if (slot.IsNotNull)
            {
                var source = APTR.FromPointer(slot.Raw + AmigaSmartFileSystemWriteBuffer.EntryHeader);
                if (id != 0 && ReadUInt32(slot, AmigaSmartFileSystemWriteBuffer.VerificationOffset) != id)
                {
                    if (!AmigaFileSystemBlockIo.ValidateMetadata(ref this, block, id, source, blockBytes)) return false;
                    WriteUInt32(slot, AmigaSmartFileSystemWriteBuffer.VerificationOffset, id);
                }
                CopyBlock(source, destination, blockBytes); return true;
            }
        }
        var batch = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartTransactionBufferOffset));
        if (!AmigaSmartFileSystemTransactionBuffer.TryRead(ref this, batch, blockBytes,
            block, destination, out var staged)) return false;
        if (staged) return id == 0 || AmigaFileSystemBlockIo.ValidateMetadata(ref this, block, id, destination, blockBytes);
#endif
#if COPPERSTART_NATIVE_SFS_WRITES && !COPPERSTART_FFS_ONLY
        var queued = AmigaFileSystemWriteCombiner.ReadMetadataView(ref this, WriteQueue, block, id, out var queueFound);
        if (queueFound)
        {
            if (queued.IsNull) return false;
            CopyBlock(queued, destination, blockBytes); return true;
        }
#endif
        var image = ReadPhysicalCached(block, id, destination);
        if (image.IsNull) return false;
        if (image != destination) CopyBlock(image, destination, blockBytes);
        return true;
    }

    // Logical overlays and queued writes have already been resolved. Return a
    // borrowed physical image when possible; a copying caller consumes it
    // immediately, before any block access can expire the cache window.
    private APTR ReadPhysicalCached(uint block, uint id, APTR destination)
    {
        var blockBytes = APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockBytesOffset);
        var ram = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.RamBlocksOffset));
        if (ram.IsNotNull)
        {
            var sector = APTR.FromPointer(ReadUInt32(ram, (int)block * 4));
            if (sector.IsNull) Clear(destination, blockBytes); else CopyBlock(sector, destination, blockBytes);
            return id == 0 || AmigaFileSystemBlockIo.ValidateMetadata(ref this, block, id, destination, blockBytes) ? destination : APTR.Null;
        }
        var extra = FindCached(block);
        if (extra.IsNotNull)
        {
            var source = APTR.FromPointer(extra.Raw + NativeAmigaFileSystem.ExtraCacheHeaderBytes);
            if (id != 0 && ReadUInt32(extra, 16) != id)
            {
                if (!AmigaFileSystemBlockIo.ValidateMetadata(ref this, block, id, source, blockBytes)) return APTR.Null;
                WriteUInt32(extra, 16, id);
            }
            TouchCached(extra);
            return source;
        }
        var cache = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheOffset));
        APTR image;
        if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset) == AmigaSmartFileSystemCore.DosType)
        {
            // A small adjacent window reduces allocation/undo read requests
            // without replacing the entire metadata LRU with a track refill.
            if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheBlocksOffset) >= 8 &&
                APTR.ReadUInt32(state, NativeFileSystemStateCodec.MaxTransferOffset) / blockBytes >= 8 &&
                APTR.ReadUInt32(state, NativeFileSystemStateCodec.ExtraCacheCountOffset) >= 16)
            {
                var first = block & ~7u;
                var count = APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockCountOffset) - first;
                if (count > 8) count = 8;
                var completed = TransferData(false, first, count, cache);
                if (block - first < completed)
                {
                    var source = APTR.FromPointer(cache.Raw + (block - first) * blockBytes);
                    if (id != 0 && !AmigaFileSystemBlockIo.ValidateMetadata(ref this, block, id, source, blockBytes)) return APTR.Null;
                    for (var i = 0u; i < completed; i++)
                    {
                        var key = first + i;
                        if (key == block) continue;
                        var prefetched = APTR.FromPointer(cache.Raw + i * blockBytes);
                        // Metadata carries its own block number at offset 8.
                        // A neighbor lacking that identity is not useful to a
                        // metadata read-ahead and need not displace a cache page.
                        // This is only a retention hint: accepted neighbors stay
                        // unverified and must pass the normal ID/checksum check.
                        if (id != 0 && ReadUInt32(prefetched, 8) != key) continue;
                        // A prefetch must never reintroduce a stale physical
                        // image beneath pending writes after their queue drains.
                        if (FindCached(key).IsNotNull
#if COPPERSTART_NATIVE_SFS_WRITES && !COPPERSTART_FFS_ONLY
                            || AmigaFileSystemWriteCombiner.ReadView(ref this, WriteQueue, key).IsNotNull
#endif
                            ) continue;
                        CacheBlock(key, 0, prefetched, blockBytes);
                    }
                    CacheBlock(block, id, source, blockBytes);
                    return source;
                }
                // An unrelated neighboring sector may fail. Retry the requested
                // block alone so speculative reads cannot break a valid read.
            }
            APTR.WriteUInt32(state, NativeFileSystemStateCodec.CacheTrackOffset, uint.MaxValue);
            var input = cache;
            if (CanTransferDirect(destination, blockBytes)) input = destination;
            if (!TransferBlock(false, block, input)) return APTR.Null;
            image = input;
        }
        else
        {
            var geometry = Geometry;
            var cacheBlocks = APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheBlocksOffset);
            if (cacheBlocks == 0) return APTR.Null;
            var first = AmigaGeometryMath.Divide(block, cacheBlocks) * cacheBlocks;
            if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheTrackOffset) != first)
            {
                APTR.WriteUInt32(state, NativeFileSystemStateCodec.CacheTrackOffset, uint.MaxValue);
                var count = geometry.BlockCount - first;
                if (count > cacheBlocks) count = cacheBlocks;
                if (!AmigaFileSystemDeviceCore.Transfer(ref this, geometry, false, first, count, cache)) return APTR.Null;
                APTR.WriteUInt32(state, NativeFileSystemStateCodec.CacheTrackOffset, first);
            }
            image = APTR.FromPointer(cache.Raw + (block - first) * geometry.BlockBytes);
        }
        if (id != 0 && !AmigaFileSystemBlockIo.ValidateMetadata(ref this, block, id, image, blockBytes)) return APTR.Null;
        CacheBlock(block, id, image, blockBytes);
        return image;
    }

    private void CacheBlock(uint block, uint id, APTR source, uint bytes)
    {
        var extra = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.ExtraCacheTailOffset));
        if (extra.IsNotNull)
        {
            Unhash(extra);
            WriteUInt32(extra, 4, block); WriteUInt32(extra, 16, id);
            var bucket = (int)(block & 63);
            WriteUInt32(extra, 12, APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheHashOffset + bucket * 4)); APTR.WriteUInt32(state, NativeFileSystemStateCodec.CacheHashOffset + bucket * 4, extra.Raw);
#if COPPERSTART_SFS_STANDALONE && COPPERSTART_SFS_TRST_WRITES && !COPPERSTART_NATIVE_SFS2
            NativeAmigaFileSystem.AddCachePage(state, block);
#endif
            CopyBlock(source, APTR.FromPointer(extra.Raw + NativeAmigaFileSystem.ExtraCacheHeaderBytes), bytes);
            TouchCached(extra);
        }
    }


#if COPPERSTART_FULL_FILESYSTEM_EXTENSION
    public bool WriteFormatBlock(uint block, APTR source)
    {
        if (block >= BlockCount || WriteProtected()) return false;
        InvalidateBlocks(block, 1);
        if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.RamBlocksOffset) != 0) return WriteRamBlock(block, source);
        var buffer = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheOffset));
        if (CanTransferDirect(source, BlockBytes)) buffer = source;
        else CopyBlock(source, buffer, BlockBytes);
        return TransferBlock(true, block, buffer);
    }


#endif
    // Formatting alone may write block zero; normal filesystem writes retain
    // their reserved-block restriction in WriteBlock.
    public bool WriteRamBlock(uint block, APTR source)
    {
        var ram = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.RamBlocksOffset));
        if (ram.IsNull || block >= APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockCountOffset)) return false;
        var blockBytes = APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockBytesOffset);
        var sector = APTR.FromPointer(ReadUInt32(ram, (int)block * 4));
        if (sector.IsNull)
        {
            sector = APTR.FromPointer(global::Amiga.Exec.AllocMem(blockBytes, global::Amiga.Exec.MemoryFlags.Public));
            if (sector.IsNull) return false;
            WriteUInt32(ram, (int)block * 4, sector.Raw);
        }
        CopyBlock(source, sector, blockBytes);
        return true;
    }
}
