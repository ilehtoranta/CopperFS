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
#if COPPERFS_NATIVE_FFS_READAHEAD
    // FFS and SFS reservation records are mutually exclusive mount state.
    // Leave the reservation count untouched and borrow one unused record.
    internal const int FfsClockRequestOffset = SmartReservationsOffset + 4;
#endif
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
    private const int FixedHardDiskOffset = GeometryOffset + 40;
    // Opt-in DosEnvec.de_Control values, scoped to the exact device name.
    // The following word was spare; cache policy adds no state allocation.
    public const uint CopperHdfPayloadBypassControl = 0x43504301;
    public const uint ScsiPayloadBypassControl = 0x43504302;
    internal const int PayloadCacheBypassOffset = GeometryOffset + 44;
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
        if ((SpecializeFormat ? FormatIsSmart : Smart(state)))
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
        if (!(SpecializeFormat ? FormatIsSmart : Smart(state)))
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
    // Only packet and post-acceptance paths use these constants. Startup can
    // mount and recover a foreign format before its final type check.
#if COPPERSTART_SFS_STANDALONE && COPPERSTART_FFS_ONLY
    private const bool SpecializeFormat = true, FormatIsSmart = false;
#elif COPPERSTART_SFS_STANDALONE && COPPERSTART_SMART_ONLY
    private const bool SpecializeFormat = true, FormatIsSmart = true;
#else
    private const bool SpecializeFormat = false, FormatIsSmart = false;
#endif
    private static bool Smart(APTR state) => APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset) is AmigaSmartFileSystemCore.DosType or NativeSfs2Bridge.DosType;
    private static uint RootKey(APTR state) => (SpecializeFormat ? FormatIsSmart : Smart(state)) ? AmigaSmartFileSystemCore.RootNode : DiskRootBlock(state);
    // Keep the by-value disk geometry out of the shared DOS open packet's stack frame.
    private static uint DiskRootBlock(APTR state)
    {
        var blocks = APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockCountOffset);
        var reserved = APTR.ReadUInt32(state, NativeFileSystemStateCodec.ReservedBlocksOffset);
        return (blocks - 1) / 2 + reserved / 2 + ((blocks - 1) % 2 + reserved % 2) / 2;
    }
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

    private static bool DeviceNamed(APTR name, CString expected)
    {
        var literal = APTR.FromPointer(expected);
        for (var at = 0; ; at++)
        {
            var ch = APTR.ReadUInt8(name, at);
            if (ch != APTR.ReadUInt8(literal, at)) return false;
            if (ch == 0) return true;
        }
    }

    private static void ConfigurePayloadCache(APTR state, APTR deviceName, APTR environment)
    {
        var policy = 0u;
        if (environment.IsNotNull && APTR.ReadUInt32(environment, 0) >= 18)
        {
            var control = APTR.ReadUInt32(environment, 72);
            if(control == CopperHdfPayloadBypassControl && DeviceNamed(deviceName, "copperhdf.device")) policy=1;
            // Bit zero bypasses payload copies; bit one retains read-ahead for
            // small sector packets on the diagnostic native controller path.
            else if(control == ScsiPayloadBypassControl && DeviceNamed(deviceName, "scsi.device")) policy=3;
        }
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.PayloadCacheBypassOffset, policy);
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
        ConfigurePayloadCache(s, deviceName, environment);
        var p = new NativeFileSystemPlatform(s);
        var probe = APTR.FromPointer(state + MetadataOffset);
        APTR.WriteUInt16(probe, 28, 0xFFFF); // A driver omitting type/flags cannot confirm fixed media.
        APTR.WriteUInt16(request, NativeExecFieldCodec.IoCommandOffset, 22); // TD_GETGEOMETRY
        APTR.WriteUInt32(request, NativeExecFieldCodec.IoDataOffset, probe.Raw);
        APTR.WriteUInt32(request, NativeExecFieldCodec.IoLengthOffset, 32);
        APTR.WriteUInt32(request, NativeExecFieldCodec.IoActualOffset, 0);
        global::Amiga.Exec.DoIO(request);
        var haveDevice = APTR.ReadUInt8(request, NativeExecFieldCodec.IoErrorOffset) == 0 && APTR.ReadUInt32(request, NativeExecFieldCodec.IoActualOffset) >= 32;
        // Direct access alone also describes floppies. Require a known disk
        // driver and affirmative fixed, direct-access geometry for this unit.
        if (haveDevice && APTR.ReadUInt32(probe, 0) != 0 && APTR.ReadUInt8(probe, 28) == 0 &&
            (APTR.ReadUInt8(probe, 29) & 1) == 0 && (DeviceNamed(deviceName, "scsi.device") ||
            DeviceNamed(deviceName, "copperhdf.device") || DeviceNamed(deviceName, "uaehf.device")))
            APTR.WriteUInt32(s, FixedHardDiskOffset, 1);
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
            if (!MountSmart(s, type))
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
#if COPPERFS_NATIVE_FFS_READAHEAD
            // Early IDE scsi.device versions do not implement TD_GETGEOMETRY.
            // Confirm fixed direct-access media through SCSI INQUIRY instead
            // of inferring it from the driver name or partition geometry.
            if (APTR.ReadUInt32(s, FixedHardDiskOffset) == 0 && DeviceNamed(deviceName, "scsi.device") &&
                ProbeFixedScsi(s, Metadata(s))) APTR.WriteUInt32(s, FixedHardDiskOffset, 1);
            // Honor a bounded part of the mount's requested metadata budget.
            // Extra pages are optional; low-memory mounts keep the first 16.
            if (environment.IsNotNull && APTR.ReadUInt32(environment, 0) >= 11)
            {
                var requested = APTR.ReadUInt32(environment, DosLayout.DosEnvec.NumberOfBuffers);
                if (requested > 32) requested = 32;
                var have = APTR.ReadUInt32(s, NativeFileSystemStateCodec.ExtraCacheCountOffset);
                if (requested > have) MoreCache(s, (int)(requested - have), out _);
            }
#endif
        }
        APTR.WriteUInt32(s, NativeFileSystemStateCodec.DosTypeOffset, type);

        return s;
    }

#if COPPERFS_NATIVE_FFS_READAHEAD
    internal static bool ProbeFixedScsi(APTR state, APTR scratch)
    {
        var p = new NativeFileSystemPlatform(state);
        p.Clear(scratch, 100);
        APTR.WriteUInt32(scratch, 0, scratch.Raw + 64); // scsi_Data
        APTR.WriteUInt32(scratch, 4, 36);             // scsi_Length
        APTR.WriteUInt32(scratch, 12, scratch.Raw + 32);
        APTR.WriteUInt16(scratch, 16, 6);            // scsi_CmdLength
        APTR.WriteUInt8(scratch, 20, 1);            // SCSIF_READ
        APTR.WriteUInt8(scratch, 21, 0xFF);         // Require returned status.
        APTR.WriteUInt8(scratch, 32, 0x12);         // Standard INQUIRY, no VPD.
        APTR.WriteUInt8(scratch, 36, 36);
        APTR.WriteUInt16(scratch, 64, 0xFFFF);       // Require returned identity.
        return p.ScsiDirect(scratch) && APTR.ReadUInt8(scratch, 21) == 0 &&
            APTR.ReadUInt16(scratch, 18) == 6 && APTR.ReadUInt32(scratch, 8) >= 36 &&
            APTR.ReadUInt8(scratch, 64) == 0 && (APTR.ReadUInt8(scratch, 65) & 0x80) == 0;
    }
#endif

    internal static bool BufferFitsMask(uint address, uint bytes, uint mask)
    {
        // Low zero bits specify the start address's alignment (commonly two
        // or four bytes); they do not exclude those bytes within the transfer.
        if (mask == 0 || bytes == 0 || (address & ~mask) != 0 || bytes > uint.MaxValue - address) return false;
        var alignmentBits = (mask & unchecked(0u - mask)) - 1;
        if ((mask | alignmentBits) == uint.MaxValue) return true;
        // Include every address in the interval, even for masks with holes.
        // Endpoints alone can miss a forbidden bit that changes twice.
        var varying = address ^ (address + bytes - 1);
        varying |= varying >> 1; varying |= varying >> 2; varying |= varying >> 4;
        varying |= varying >> 8; varying |= varying >> 16;
        return ((address | varying) & ~(mask | alignmentBits)) == 0;
    }

    private static bool MountSmart(APTR s, uint probedType = 0)
    {
        var geometry = ReadGeometry(s);
        var p = new NativeFileSystemPlatform(s);
        var previousCacheCount = APTR.ReadUInt32(s, NativeFileSystemStateCodec.ExtraCacheCountOffset);
        var earlyCache = false;
#if COPPERSTART_SFS_STANDALONE && COPPERSTART_NATIVE_SFS2
        // The initial probe already read sector zero into private scratch.
        // Its signature is an I/O hint. Root still checks both root copies.
        earlyCache = probedType == NativeSfs2Bridge.DosType && APTR.ReadUInt32(s, FixedHardDiskOffset) != 0;
        if (earlyCache)
        {
            if (previousCacheCount < 32) MoreCache(s, 32 - (int)previousCacheCount, out _);
            p.RetainSfs2MountPrefix(Metadata(s));
        }
#endif
            if (!NativeSfsExtension.Root(s))
            {
#if COPPERSTART_SFS_STANDALONE && COPPERSTART_NATIVE_SFS2
                var added = APTR.ReadUInt32(s, NativeFileSystemStateCodec.ExtraCacheCountOffset) - previousCacheCount;
                if (added != 0) MoreCache(s, -(int)added, out _);
#endif
                return false;
            }
            APTR.WriteUInt32(s, NativeFileSystemStateCodec.SmartCreationDateOffset, APTR.ReadUInt32(Metadata(s), 16));
            // Retain validated mount/recovery reads, including the transaction
            // marker, in the same cache subsequently used by DOS operations.
            if (!earlyCache && previousCacheCount < 32) MoreCache(s, 32 - (int)previousCacheCount, out _);
#if COPPERSTART_NATIVE_SFS_WRITES
            // Recovery mutates media and belongs to the native SFS writer profile.
            var journal = global::Amiga.Exec.AllocMem(geometry.BlockBytes,
                global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear);
            APTR.WriteUInt32(s, NativeFileSystemStateCodec.JournalScratchOffset, journal);
            if (journal == 0) { return false; }
#if !COPPERSTART_SFS_STANDALONE || !COPPERSTART_SFS_TRST_WRITES || COPPERSTART_NATIVE_SFS2
            // Initialize sets the visible slot count to zero. Each staged slot
            // receives its key and a complete block before the next packet.
            var batchFlags = NativeSfs2Bridge.IsSfs2(s) ? global::Amiga.Exec.MemoryFlags.Public :
                global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear;
            var batch = global::Amiga.Exec.AllocMem(
                AmigaSmartFileSystemTransactionBuffer.StorageBytes(geometry.BlockBytes,
                    AmigaSmartFileSystemTransactionBuffer.MaximumCapacity),
                batchFlags);
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
#if COPPERSTART_SFS_STANDALONE && COPPERSTART_NATIVE_SFS2
        if (NativeSfs2Bridge.IsSfs2(s))
        {
            AllocateEnumerationCache(s);
            return MountSfs2ForInput(ref p, s);
        }
#endif
        if (!NativeSfsExtension.Mount(s)) return false;
        if (Smart(s)) AllocateEnumerationCache(s);
        return true;
    }

    private static void AllocateEnumerationCache(APTR state)
    {
        var cache = global::Amiga.Exec.AllocMem(SmartEnumerationCacheBytes,
            global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartEnumerationCacheOffset, cache);
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
#if COPPERFS_NATIVE_FFS_READAHEAD
            if (timer.Raw != state.Raw + FfsClockRequestOffset)
#endif
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
#if COPPERSTART_NATIVE_SFS_WRITES
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
#if COPPERSTART_SFS_STANDALONE && COPPERSTART_NATIVE_SFS2
            // Empty slots are not hashed. A complete block is copied or read
            // before publication; clearing the payload here repeats that work.
            const global::Amiga.Exec.MemoryFlags flags = global::Amiga.Exec.MemoryFlags.Public;
#else
            const global::Amiga.Exec.MemoryFlags flags = global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear;
#endif
            for (; added < delta; added++)
            {
                var block = APTR.FromPointer(global::Amiga.Exec.AllocMem(ExtraCacheBytes(state), flags));
                if (block.IsNull)
                {
                    while (head != original)
                    {
                        var next = APTR.FromPointer(APTR.ReadUInt32(head, 0));
                        global::Amiga.Exec.FreeMem(head.Raw, ExtraCacheBytes(state)); head = next;
                    }
                    error = DOS.Error.NoFreeStore; return 0;
                }
                APTR.WriteUInt32(block, 0, head.Raw);
                APTR.WriteUInt32(block, 4, uint.MaxValue);
#if COPPERSTART_SFS_STANDALONE && COPPERSTART_NATIVE_SFS2
                // ReindexCache sets the previous link when this batch commits.
                APTR.WriteUInt32(block, 12, 0);
                APTR.WriteUInt32(block, 16, 0);
#endif
                head = block;
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
        var o = APTR.Null;
#if COPPERSTART_SFS_STANDALONE && COPPERSTART_NATIVE_SFS2
        if (file && access == 0) o = Sfs2InputObject(state);
        if (o.IsNull || APTR.ReadUInt32(o, 16) != 0)
#endif
            o = APTR.FromPointer(global::Amiga.Exec.AllocMem(ObjectBytes, global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear));
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
                if (APTR.ReadUInt32(o, 16) == 1 && (APTR.ReadUInt32(state, NativeFileSystemStateCodec.RecordLocksOffset) != 0 ||
                    APTR.ReadUInt32(state, NativeFileSystemStateCodec.RecordWaitersOffset) != 0))
                {
                    var recordPlatform = new NativeRecordLockPlatform();
                    NativeRecordLockCore.Close(ref recordPlatform, state, o);
                }
                APTR.WriteUInt32(link, 0, APTR.ReadUInt32(o, 0));
#if COPPERSTART_SFS_STANDALONE && COPPERSTART_NATIVE_SFS2
                if (o == Sfs2InputObject(state))
                {
                    var p = new NativeFileSystemPlatform(state);
                    p.Clear(o, ObjectBytes);
                    return;
                }
#endif
                global::Amiga.Exec.FreeMem(o.Raw, ObjectBytes);
                return;
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
#if COPPERSTART_SFS_STANDALONE && COPPERSTART_NATIVE_SFS2
                // The adjacent input object may still be linked and in use.
                if (NativeSfs2Bridge.IsSfs2(state)) ClearSfs2Lookup(enumeration);
                else
#endif
                {
                    var p = new NativeFileSystemPlatform(state);
                    p.Clear(enumeration, SmartEnumerationCacheBytes);
                }
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
#if COPPERFS_NATIVE_FFS_READAHEAD
        // FFS owns a timer request for allocation-free stream cost sampling.
        // It is never armed; normal unmount cleanup closes it with the device.
        if (!(SpecializeFormat ? FormatIsSmart : Smart(state)) && (APTR.ReadUInt32(state, FixedHardDiskOffset) == 0 ||
            APTR.ReadUInt32(state, PayloadCacheBypassOffset) != 0)) return;
        // Chip-only mounts keep the payload window: probing direct transfers
        // pays controller setup without the Fast-RAM copy saving. Use Exec's
        // real allocation attributes, independent of device names or emulator.
        if (!(SpecializeFormat ? FormatIsSmart : Smart(state)) && (global::Amiga.Exec.TypeOfMem(
            APTR.FromPointer(APTR.ReadUInt32(state, CacheOffset))) & (uint)global::Amiga.Exec.MemoryFlags.Fast) == 0) return;
        if (!(SpecializeFormat ? FormatIsSmart : Smart(state)))
        {
            // This request is only an OpenDevice/CloseDevice handle. It is
            // never queued: ReadEClock uses its device base directly.
            var inline = APTR.FromPointer(state.Raw + FfsClockRequestOffset);
            for (var offset = 0; offset < TimerRequest.Size; offset += 4)
                APTR.WriteUInt32(inline, offset, 0);
            APTR.WriteUInt8(inline, 8, 5); // NT_MESSAGE
            APTR.WriteUInt32(inline, 14, port.Raw);
            APTR.WriteUInt16(inline, 18, (ushort)TimerRequest.Size);
            if (global::Amiga.Exec.OpenDevice(CString.FromPointer(CString.ToUInt32("timer.device")),
                (uint)TimerUnit.MicroHz, inline, 0) == 0)
                APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartTimerOffset, inline.Raw);
            return;
        }
#else
        if (!(SpecializeFormat ? FormatIsSmart : Smart(state))) return;
#endif
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
#if COPPERFS_NATIVE_FFS_READAHEAD
        // The optional FFS sampling request is never a deferred-write timer.
        // Avoid walking SFS transaction state after every FFS packet.
        if (!(SpecializeFormat ? FormatIsSmart : Smart(state))) return;
#endif
        if (!HasBufferedWrites(state) || APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartTimerActiveOffset) != 0) return;
        StartWriteTimer(state);
    }

    private static void StartWriteTimer(APTR state)
    {
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
                var request = new AmigaSmartFileSystemWriteCore.Request
                { Node = found, Directory = name.Directory, Name = name.Name, Length = name.Length };
                if (!NativeSfsExtension.ReplaceFile(state, ref w, ref request)) { error = w.Error; return 0; }
                found = request.Node;
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
            // Kickstart 3.1's SetFileDate path supplies an expanded address.
            // CopperStart's DOS path supplies a BPTR. Match the SFS adapter:
            // use the expanded form when only that form is in guest memory.
            if (action == DosPacketAction.SetDate && !p.IsMapped(r.Buffer, 12) &&
                p.IsMapped(APTR.FromPointer(value), 12))
                r.Buffer = APTR.FromPointer(value);
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
        // The FFS-only standalone entry reaches packet dispatch only after it
        // accepts an FFS/OFS mount; foreign recovery still occurs at startup.
        var action = (DosPacketAction)APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.TypeOffset);
#if COPPERFS_NATIVE_FFS_READAHEAD
        if (action == DosPacketAction.Read)
        {
            var cachedLength = APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument3Offset);
            if (cachedLength >= 512 && cachedLength <= 4096 &&
                NativeFileSystemPlatform.TryDispatchCachedFfsRead(state, packet)) return;
            if (cachedLength == 4096 && APTR.ReadUInt32(state, SmartTimerOffset) != 0 && !(SpecializeFormat ? FormatIsSmart : Smart(state)))
            { DispatchAdaptiveReadPacket(state, packet); return; }
            // DispatchPacket repeats the action/type checks before reaching
            // this same reader. FFS/OFS have no SFS recovery gate to traverse.
            if (!(SpecializeFormat ? FormatIsSmart : Smart(state))) { DispatchReadPacket(state, packet); return; }
        }
#endif
#if COPPERSTART_NATIVE_SFS_WRITES && !COPPERSTART_FFS_ONLY
        if (action == DosPacketAction.Write && (SpecializeFormat ? FormatIsSmart : Smart(state)) &&
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.InhibitedOffset) == 0 && APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRecoveryPendingOffset) == 0)
        { DispatchSmartWrite(state, packet); return; }
#endif
        if (action is not (DosPacketAction.Read or DosPacketAction.ExamineNext) || !(SpecializeFormat ? FormatIsSmart : Smart(state)) ||
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.InhibitedOffset) != 0 || APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRecoveryPendingOffset) != 0)
        { DispatchPacket(state, packet); return; }
#if COPPERFS_NATIVE_FFS_READAHEAD
        DispatchSmartReadOrNext(state, packet, action);
    }

    // CILica may inline geometry/work-area setup into the outer dispatcher.
    // Keep those locals off every FFS read, including cache misses and small I/O.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DispatchSmartReadOrNext(APTR state, APTR packet, DosPacketAction action)
    {
#endif
        if (action == DosPacketAction.ExamineNext)
        { DispatchSmartNext(state, packet); return; }
#if COPPERSTART_SFS_STANDALONE && COPPERSTART_NATIVE_SFS2
        // A newly opened file is the first owned object. Check that native
        // record directly; older handles still go through the bounded walk.
        var token = APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument1Offset);
        var file = APTR.FromPointer(APTR.ReadUInt32(state, ObjectsOffset));
        if (file.Raw != token) file = Object(state, token, true);
        else if (file.IsNotNull && APTR.ReadUInt32(file, 16) != 1) file = APTR.Null;
#else
        var file = Object(state, APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument1Offset), true);
#endif
        var error = DOS.Error.InvalidLock;
        var result = -1;
        if (file.IsNotNull)
        {
#if COPPERSTART_SFS_STANDALONE && COPPERSTART_NATIVE_SFS2
            var low = APTR.ReadUInt32(file, 12);
            var high = APTR.ReadUInt32(file, FilePositionHigh);
            var position = ((ulong)high << 32) | low;
#else
            var position = FilePosition(file);
#endif
            var node = APTR.ReadUInt32(file, 4);
            var destination = APTR.FromPointer(APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument2Offset));
            var length = APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument3Offset);
#if COPPERSTART_SFS_STANDALONE && COPPERSTART_NATIVE_SFS2
            if (NativeSfs2Bridge.IsSfs2(state))
            {
                var p = new NativeFileSystemPlatform(state);
                var metadata = Metadata(state);
                var data = APTR.FromPointer(metadata.Raw + p.BlockBytes);
                if (APTR.ReadUInt32(file, SmartReadCursorOffset) == 0)
                    result = ReadNewSfs2Cursor(ref p, state, file, node, position, destination, length, metadata, data, out error);
                else
                    result = NativeFileSystemReadCore.ReadSfs2(ref p, file, node, position, destination, length, metadata, data, out error);
            }
            else
#endif
                result = NativeSfsExtension.Read(state, file, node, position, destination, length, out error);
            if (result > 0)
            {
#if COPPERSTART_SFS_STANDALONE && COPPERSTART_NATIVE_SFS2
                // Use the position captured before I/O, including when the
                // caller's destination aliases fields in the private record.
                var next = unchecked(low + (uint)result);
                APTR.WriteUInt32(file, 12, next);
                APTR.WriteUInt32(file, FilePositionHigh, next < low ? high + 1 : high);
#else
                SetFilePosition(file, position + (uint)result);
#endif
            }
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
        if (required == 0 || ((SpecializeFormat ? FormatIsSmart : Smart(state)) && attribute == FileSystemQueryAttribute.MaxVolumeNameLength) ||
            (NativeSfs2Bridge.IsSfs2(state) && attribute == FileSystemQueryAttribute.IsCaseSensitive))
        { error = DOS.Error.BadNumber; return 0; }
        if (storageSize < 0 || (uint)storageSize < required)
        { error = DOS.Error.LineTooLong; return 0; }
        var p = new NativeFileSystemPlatform(state);
        if (!p.IsMapped(storage, required)) { error = DOS.Error.BadNumber; return 0; }
        ulong value;
        switch (attribute)
        {
            case FileSystemQueryAttribute.MaxFileNameLength: value = NativeSfs2Bridge.IsSfs2(state) ? 107u : (SpecializeFormat ? FormatIsSmart : Smart(state)) ? 100u : 30u; break;
            case FileSystemQueryAttribute.MaxVolumeNameLength:
                value = 30; break;
            case FileSystemQueryAttribute.MaxFileSize: value = NativeSfs2Bridge.IsSfs2(state) ? AmigaSmartFileSystem2WriteCore.MaxFileSize : uint.MaxValue; break;
            case FileSystemQueryAttribute.IsCaseSensitive: value = 0; break;
            case FileSystemQueryAttribute.DeviceType: value = 31; break; // DG_UNKNOWN
            case FileSystemQueryAttribute.NumBlocks: value = p.BlockCount; break;
            case FileSystemQueryAttribute.NumBlocksUsed:
                if (!((SpecializeFormat ? FormatIsSmart : Smart(state)) ? NativeSfsExtension.CountUsed(state, out var countedUsed) :
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
#if COPPERSTART_SFS_STANDALONE && !COPPERSTART_PACKED_EXTENSION
        // Keep normal reads out of the general packet decoder's large frame,
        // lock lookup and root-key setup. Exceptional media/recovery states
        // continue through the original decoder and its ordering below.
        var action = (DosPacketAction)APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.TypeOffset);
#if COPPERSTART_NATIVE_SFS2
        // END remains legal during inhibition and recovery. FINDINPUT must pass
        // the general dispatcher's media and recovery checks in those states.
        if (action is DosPacketAction.FindInput or DosPacketAction.End && NativeSfs2Bridge.IsSfs2(state) &&
            (action == DosPacketAction.End || (APTR.ReadUInt32(state, NativeFileSystemStateCodec.InhibitedOffset) == 0 &&
                APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRecoveryPendingOffset) == 0)))
        {
            DispatchObjectPacket(state, packet, action);
            return;
        }
        if (action == DosPacketAction.Inhibit && NativeSfs2Bridge.IsSfs2(state) &&
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRecoveryPendingOffset) == 0)
        {
            var p = new NativeFileSystemPlatform(state);
            var inhibit = APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument1Offset) != 0;
            var result = SetInhibited(ref p, state, inhibit, out var error);
            APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result1Offset, unchecked((uint)result));
            APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result2Offset, (uint)error);
            return;
        }
#endif
        if (action == DosPacketAction.Read &&
            (!(SpecializeFormat ? FormatIsSmart : Smart(state)) || (APTR.ReadUInt32(state, NativeFileSystemStateCodec.InhibitedOffset) == 0 &&
                APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRecoveryPendingOffset) == 0)))
        {
            DispatchReadPacket(state, packet);
            return;
        }
#endif
        DispatchGeneralPacket(state, packet);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DispatchSmartNext(APTR state, APTR packet)
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

    private static void DispatchReadPacket(APTR state, APTR packet)
    {
        var file = Object(state, APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument1Offset), true);
        var result = -1;
        var error = DOS.Error.InvalidLock;
        if (file.IsNotNull)
        {
            var p = new NativeFileSystemPlatform(state);
            var position = FilePosition(file);
            var target = APTR.FromPointer(APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument2Offset));
            var length = APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument3Offset);
            if ((SpecializeFormat ? FormatIsSmart : Smart(state)))
                result = NativeSfsExtension.Read(state, file, APTR.ReadUInt32(file, 4), position, target, length, out error);
#if !COPPERFS_NATIVE_FFS_READAHEAD
            else if (length > 512 && length <= 4096 && NativeFileSystemPlatform.TryReadFfsCached(state, file, (uint)position, target, length))
            { result = (int)length; error = DOS.Error.None; }
#endif
            else if (length != 0 && length <= 4096 && (length < 512 || (length & 511) == 0))
                result = NativeFileSystemReadCore.ReadFfsAligned(ref p, file, APTR.ReadUInt32(file, 4),
                    APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset), (uint)position,
                    target, length, Metadata(state), Data(state), out error);
            else result = NativeFileSystemReadCore.ReadFfs(ref p, file, APTR.ReadUInt32(file, 4),
                    APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset), (uint)position,
                    target, length, Metadata(state), Data(state), out error);
            if (result > 0) SetFilePosition(file, position + (uint)result);
#if COPPERFS_NATIVE_FFS_READAHEAD
            // Metadata speculation is worthwhile on direct large reads.
            // Cached small reads retain their lean payload-window path.
            if (length >= 4096 && position == 0 && result > 0 && error == DOS.Error.None)
                p.ReadAheadFfsMetadata(file, target, length);
#endif
        }
        APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result1Offset, unchecked((uint)result));
        APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result2Offset, (uint)error);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReleaseFileReservation(APTR state, uint node)
    {
        var p = new NativeFileSystemPlatform(state);
        InitializeSmartWriteWork(state, out var work);
        AmigaSmartFileSystemWriteCore.ReleaseOverwriteReservation(ref p, ref work, node);
    }

    private static void DispatchObjectPacket(APTR state, APTR packet, DosPacketAction action)
    {
        var p = new NativeFileSystemPlatform(state);
        var a = APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument1Offset);
        var result = 0;
        var error = DOS.Error.None;
        switch (action)
        {
            case DosPacketAction.LocateObject:
            case DosPacketAction.FindInput:
                var b = APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument2Offset);
                var c = APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument3Offset);
                var input = action == DosPacketAction.FindInput;
                var dosType = APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset);
                var smart = SpecializeFormat ? FormatIsSmart : dosType is AmigaSmartFileSystemCore.DosType or NativeSfs2Bridge.DosType;
                var baseToken = input ? b : a;
                var baseLock = baseToken == 0 ? APTR.Null : Object(state, baseToken, false);
                var directory = baseToken == 0 ? (smart ? AmigaSmartFileSystemCore.RootNode : DiskRootBlock(state))
                    : baseLock.IsNotNull ? APTR.ReadUInt32(baseLock, 4) : 0;
                if (directory == 0) { error = DOS.Error.InvalidLock; break; }
                var nameToken = input ? c : b;
                var name = APTR.FromPointer(nameToken << 2);
                if (nameToken > 0x3FFFFFFF || !p.TryMappedRegion(name, 1, out var lower, out var upper))
                { error = DOS.Error.InvalidComponentName; break; }
                var length = p.ReadUInt8(name);
                var nameBytes = (uint)length + 1;
                if (nameBytes > upper - name.Raw && !p.IsMapped(name, nameBytes))
                { error = DOS.Error.InvalidComponentName; break; }
                var handleAddress = a << 2;
                if (input && (a > 0x3FFFFFFF ||
                    ((handleAddress < lower || handleAddress >= upper || FileHandle.Size > upper - handleAddress) &&
                     !p.IsMapped(APTR.FromPointer(handleAddress), FileHandle.Size))))
                { error = DOS.Error.BadNumber; break; }
                ulong foundSize = 0;
                uint found;
                if (smart)
                {
                    bool foundDirectory;
#if COPPERSTART_SFS_STANDALONE && COPPERSTART_NATIVE_SFS2
                    if (input && dosType == NativeSfs2Bridge.DosType)
                        found = ResolveSfs2Input(ref p, state, directory, name, length,
                            out foundSize, out foundDirectory, out error);
                    else
#endif
                        found = NativeSfsExtension.ResolveInformation(state, directory, APTR.FromPointer(name.Raw + 1), length,
                            out foundSize, out foundDirectory, out _, out error);
                    if (found == 0) break;
                    if (input && foundDirectory)
                    { error = DOS.Error.ObjectWrongType; break; }
                }
                else
                {
                    var metadata = Metadata(state);
                    found = AmigaDiskFileSystemCore.Resolve(ref p, directory, APTR.FromPointer(name.Raw + 1), length,
                        dosType, metadata, out error);
                    if (found == 0) break;
                    if (input && unchecked((int)p.ReadUInt32(metadata, ((int)p.BlockBytes - 512) + 508)) != -3)
                    { error = DOS.Error.ObjectWrongType; break; }
                    foundSize = p.ReadUInt32(metadata, ((int)p.BlockBytes - 512) + 324);
                }
                // Original DOS accepts access zero as a shared lookup.
                if (!input && c != 0 && unchecked((int)c) != -2 && unchecked((int)c) != -1)
                { error = DOS.Error.BadNumber; break; }
                if (InUse(state, found, !input && unchecked((int)c) == -1))
                { error = DOS.Error.ObjectInUse; break; }
                var obj = NewObject(state, found, foundSize, input, input ? 0 : unchecked((int)c));
                if (obj.IsNull) { error = DOS.Error.NoFreeStore; break; }
                if (!input) result = unchecked((int)((obj.Raw + 20) >> 2));
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
                var retired = Object(state, a, action == DosPacketAction.End);
                if (retired.IsNull && a != 0) { error = DOS.Error.InvalidLock; break; }
                var changedKey = retired.IsNotNull && action == DosPacketAction.End && APTR.ReadUInt32(retired, 40) != 0 ? APTR.ReadUInt32(retired, 4) : 0;
#if COPPERSTART_SFS_TRST_BUFFERED
                if (changedKey != 0 && (SpecializeFormat ? FormatIsSmart : Smart(state)) && !FlushSmart(state))
                { error = DOS.Error.DiskNotValidated; break; }
#endif
                if (retired.IsNotNull && action == DosPacketAction.End && (SpecializeFormat ? FormatIsSmart : Smart(state)) &&
                    APTR.ReadUInt32(state, SmartReservationsOffset) != 0)
                {
                    ReleaseFileReservation(state, APTR.ReadUInt32(retired, 4));
                }
                if (retired.IsNotNull) FreeObject(state, retired);
                if (changedKey != 0) NativeFileSystemNotifications.Changed(state, changedKey);
                result = -1;
                break;
        }
        APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result1Offset, unchecked((uint)result));
        APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result2Offset, (uint)error);
    }

    private static int SetInhibited(ref NativeFileSystemPlatform p, APTR state, bool inhibit, out DOS.Error error)
    {
        error = DOS.Error.None;
        InvalidateDirectoryCursors(state);
        if (inhibit)
        {
            if (!((SpecializeFormat ? FormatIsSmart : Smart(state)) ? FlushSmart(state) : p.Flush())) { error = DOS.Error.DiskNotValidated; return 0; }
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
            bool mounted;
#if COPPERSTART_SFS_STANDALONE && COPPERSTART_NATIVE_SFS2
            if (NativeSfs2Bridge.IsSfs2(state))
            {
                // All keys are invalid. Nothing moved in the LRU, so
                // its previous links and tail still describe this list.
                p.Clear(APTR.FromPointer(state.Raw + NativeFileSystemStateCodec.CacheHashOffset), 64 * 4);
                mounted = MountSfs2ForInput(ref p, state, emptyCache: true);
            }
            else
#endif
            {
                ReindexCache(state);
                mounted = (SpecializeFormat ? FormatIsSmart : Smart(state)) ? NativeSfsExtension.Mount(state) : AmigaDiskFileSystemCore.Mount(ref p, Metadata(state), out _);
            }
            if (!mounted)
            { error = DOS.Error.DiskNotValidated; return 0; }
            APTR.WriteUInt32(state, NativeFileSystemStateCodec.InhibitedOffset, 0);
        }
        return -1;
    }

    private static void DispatchGeneralPacket(APTR state, APTR packet)
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
        if ((SpecializeFormat ? FormatIsSmart : Smart(state)) && SmartMutation(action)) InvalidateDirectoryCursors(state);
        if ((SpecializeFormat ? FormatIsSmart : Smart(state)) && APTR.ReadUInt32(state, NativeFileSystemStateCodec.InhibitedOffset) != 0 &&
            SmartMediaAccess(action) && action is not
                (DosPacketAction.Inhibit or DosPacketAction.Info or DosPacketAction.DiskInfo))
        {
            APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result1Offset, action is DosPacketAction.Read or DosPacketAction.Write or
                DosPacketAction.Seek or DosPacketAction.SetFileSize ? uint.MaxValue : 0);
            APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result2Offset, (uint)DOS.Error.NoDisk);
            return;
        }
#if COPPERSTART_NATIVE_SFS_WRITES
        if ((SpecializeFormat ? FormatIsSmart : Smart(state)) && APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRecoveryPendingOffset) != 0 && SmartMediaAccess(action))
        {
#if COPPERSTART_SFS_STANDALONE && COPPERSTART_NATIVE_SFS2
            var pathCache = APTR.FromPointer(APTR.ReadUInt32(state, SmartEnumerationCacheOffset));
            if (NativeSfs2Bridge.IsSfs2(state) && pathCache.IsNotNull) ClearSfs2Lookup(pathCache);
#endif
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
        if (!(SpecializeFormat ? FormatIsSmart : Smart(state)) && APTR.ReadUInt32(state, 60) != 0 && SmartMediaAccess(action))
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
                if ((SpecializeFormat ? FormatIsSmart : Smart(state))) { error = DOS.Error.ActionNotKnown; break; }
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
                if ((SpecializeFormat ? FormatIsSmart : Smart(state))) { if (FlushSmart(state)) result = -1; else error = DOS.Error.DiskNotValidated; break; }
#endif
                if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.RamBlocksOffset) != 0 || p.Transfer(4, 0, 0, APTR.Null)) result = -1;
                else error = DOS.Error.DiskNotValidated;
                break;
            case DosPacketAction.Inhibit:
                result = SetInhibited(ref p, state, a != 0, out error);
                break;
            case DosPacketAction.MoreCache: result = MoreCache(state, unchecked((int)a), out error); break;
            case DosPacketAction.LocateObject:
            case DosPacketAction.FindInput:
            case DosPacketAction.FreeLock:
            case DosPacketAction.End:
                DispatchObjectPacket(state, packet, action);
                return;
            case DosPacketAction.Read:
                result = -1;
                if (file.IsNull) { error = DOS.Error.InvalidLock; break; }
                var position = FilePosition(file);
                result = (SpecializeFormat ? FormatIsSmart : Smart(state))
                    ? NativeSfsExtension.Read(state, file, APTR.ReadUInt32(file, 4), position, APTR.FromPointer(b), c, out error)
                    : NativeFileSystemReadCore.ReadFfs(ref p, file, APTR.ReadUInt32(file, 4), APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset), (uint)position,
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
                if (nextPosition != old && (SpecializeFormat ? FormatIsSmart : Smart(state)))
                {
                    InitializeSmartWriteWork(state, out var reservationWork);
                    AmigaSmartFileSystemWriteCore.ReleaseOverwriteReservation(ref p, ref reservationWork, APTR.ReadUInt32(file, 4));
                }
                APTR.WriteUInt32(file, 12, nextPosition);
#if COPPERFS_NATIVE_FFS_READAHEAD
                if (!(SpecializeFormat ? FormatIsSmart : Smart(state))) NativeFfsSequentialRead.Reset(file);
#endif
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
                if (widePosition != previousPosition && (SpecializeFormat ? FormatIsSmart : Smart(state)))
                {
                    InitializeSmartWriteWork(state, out var reservationWork);
                    AmigaSmartFileSystemWriteCore.ReleaseOverwriteReservation(ref p, ref reservationWork, APTR.ReadUInt32(file, 4));
                }
#if COPPERFS_NATIVE_FFS_READAHEAD
                if (!(SpecializeFormat ? FormatIsSmart : Smart(state))) NativeFfsSequentialRead.Reset(file);
#endif
                p.WriteUInt32(seekOutput, 0, (uint)(previousPosition >> 32));
                p.WriteUInt32(seekOutput, 4, (uint)previousPosition);
                SetFilePosition(file, widePosition);
                result = -1;
                break;
            case (DosPacketAction)DosNativeExtendedPacketAction.SetFileSize64:
#if COPPERSTART_NATIVE_SFS_WRITES && !COPPERSTART_FFS_ONLY
                if (!(SpecializeFormat ? FormatIsSmart : Smart(state))) { error = DOS.Error.ActionNotKnown; break; }
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
#if COPPERSTART_FFS_ONLY
                error = DOS.Error.ActionNotKnown; break;
#else
                error = DOS.Error.DiskWriteProtected; break;
#endif
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
                    if ((SpecializeFormat ? FormatIsSmart : Smart(state)))
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
                if ((SpecializeFormat ? FormatIsSmart : Smart(state)))
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
                result = ((SpecializeFormat ? FormatIsSmart : Smart(state))
                    ? NativeSfsExtension.Examine(state, key, APTR.FromPointer(b << 2))
                    : AmigaDiskFileSystemCore.Examine(ref p, key, APTR.FromPointer(b << 2), metadata)) ? -1 : 0;
                if (result == 0) error = DOS.Error.DiskNotValidated;
                else if (!NativeSfs2Bridge.IsSfs2(state) && (action is DosPacketAction.ExamineObject64 or DosPacketAction.ExamineFileHandle64))
                    ExtendFileInfo(APTR.FromPointer(b << 2));
                break;
            case DosPacketAction.ExamineNext64:
            case DosPacketAction.ExamineNext:
                if (key == 0) { error = DOS.Error.InvalidLock; break; }
                error = (SpecializeFormat ? FormatIsSmart : Smart(state))
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
                var counted = (SpecializeFormat ? FormatIsSmart : Smart(state))
                    ? NativeSfsExtension.CountUsed(state, out var smartUsed)
                    : AmigaDiskFileSystemCore.CountUsed(ref p, metadata, data, out smartUsed);
                var usedBlocks = smartUsed;
                if (!counted || (!(SpecializeFormat ? FormatIsSmart : Smart(state)) && !AmigaDiskFileSystemCore.Header(ref p, p.Geometry.RootBlock, metadata)))
                { error = DOS.Error.DiskNotValidated; break; }
                p.Clear(info, 36);
                p.WriteUInt32(info, 8, p.WriteProtected() ? 80u : (SpecializeFormat ? FormatIsSmart : Smart(state)) ? 82u : p.ReadUInt32(metadata, ((int)p.BlockBytes - 512) + 312) != uint.MaxValue ? 81u : 82u);
                p.WriteUInt32(info, 12, (SpecializeFormat ? FormatIsSmart : Smart(state)) ? p.BlockCount - p.ReservedBlocks - 1 : p.BlockCount);
                p.WriteUInt32(info, 16, usedBlocks);
                p.WriteUInt32(info, 20, p.BlockBytes);
                p.WriteUInt32(info, 24, APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset));
                p.WriteUInt32(info, 28, APTR.ReadUInt32(state, NativeFileSystemStateCodec.VolumeOffset) >> 2);
                result = -1;
                break;
            case DosPacketAction.FindOutput:
            case DosPacketAction.FindUpdate:
#if COPPERSTART_SMART_ONLY
                result = NativeSfsExtension.Packet(state, packet, action, out error);
#elif COPPERSTART_NATIVE_SFS_WRITES && !COPPERSTART_FFS_ONLY
                result = (SpecializeFormat ? FormatIsSmart : Smart(state)) ? NativeSfsExtension.Packet(state, packet, action, out error) : OpenWrite(state, packet, action, out error);
#else
                result = OpenWrite(state, packet, action, out error);
#endif
                break;
            case DosPacketAction.Write:
                result = -1;
                if (file.IsNull) { error = DOS.Error.InvalidLock; break; }
                // MODE_OLDFILE also permits writes. The private access word
                // describes sharing, not file/device write protection.
#if COPPERSTART_SMART_ONLY
                result = WriteSmartFile(state, file, APTR.FromPointer(b), c, out error);
                break;
#else
#if COPPERSTART_NATIVE_SFS_WRITES && !COPPERSTART_FFS_ONLY
                if ((SpecializeFormat ? FormatIsSmart : Smart(state)))
                {
                    result = WriteSmartFile(state, file, APTR.FromPointer(b), c, out error); break;
                }
#endif
                InitializeWriteWork(state, out var work);
                var write = new AmigaFfsWriteCore.Request { Header = APTR.ReadUInt32(file, 4), Position = APTR.ReadUInt32(file, 12), Buffer = APTR.FromPointer(b), Length = c };
                if (!AmigaFfsWriteCore.Write(ref p, ref work, ref write, out var written,
                    APTR.ReadUInt32(state, FixedHardDiskOffset) != 0)) { error = work.Error; break; }
                APTR.WriteUInt32(file, 12, write.Position + written);
                if (write.Position + written > APTR.ReadUInt32(file, 8)) APTR.WriteUInt32(file, 8, write.Position + written);
                if (written != 0) APTR.WriteUInt32(file, 40, 1);
                result = (int)written;
                break;
#endif
            case DosPacketAction.DeleteObject:
            case DosPacketAction.RenameObject:
            case DosPacketAction.CreateDirectory:
#if COPPERSTART_SMART_ONLY
                result = NativeSfsExtension.Packet(state, packet, action, out error);
#elif COPPERSTART_NATIVE_SFS_WRITES && !COPPERSTART_FFS_ONLY
                result = (SpecializeFormat ? FormatIsSmart : Smart(state)) ? NativeSfsExtension.Packet(state, packet, action, out error) : Mutate(state, packet, action, out error);
#else
                result = Mutate(state, packet, action, out error);
#endif
                break;
            case DosPacketAction.SetProtection:
            case DosPacketAction.SetComment:
            case DosPacketAction.SetDate:
#if COPPERSTART_SMART_ONLY
                result = NativeSfsExtension.Packet(state, packet, action, out error);
#elif COPPERSTART_NATIVE_SFS_WRITES && !COPPERSTART_FFS_ONLY
                result = (SpecializeFormat ? FormatIsSmart : Smart(state)) ? NativeSfsExtension.Packet(state, packet, action, out error) : SetMetadata(state, packet, action, out error);
#else
                result = SetMetadata(state, packet, action, out error);
#endif
                break;
            case DosPacketAction.SetFileSize:
#if COPPERSTART_NATIVE_SFS_WRITES && !COPPERSTART_FFS_ONLY
                result = -1;
                if (!(SpecializeFormat ? FormatIsSmart : Smart(state))) { error = DOS.Error.DiskWriteProtected; break; }
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
#if !COPPERSTART_SMART_ONLY
                if ((int)action == AmigaSmartFileSystemTrimPacket.Action && !(SpecializeFormat ? FormatIsSmart : Smart(state)))
                {
                    if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.InhibitedOffset) != 0) error = DOS.Error.NoDisk;
#if COPPERSTART_NATIVE_SFS_WRITES
                    else result = NativeSfsTrim.Execute(state, packet, out error, ffs: true);
#else
                    else result = NativeSfsTrim.ExecuteFfs(state, packet, out error);
#endif
                    break;
                }
#endif
#if COPPERSTART_NATIVE_SFS_WRITES && !COPPERSTART_FFS_ONLY
                // Keep the existing switch and all normal packet branches intact.
                // Only an explicit unknown/custom action reaches maintenance.
                if ((int)action == AmigaSmartFileSystemTrimPacket.Action && (SpecializeFormat ? FormatIsSmart : Smart(state)))
                    result = NativeSfsExtension.Packet(state, packet, action, out error);
                else
#endif
                    error = DOS.Error.ActionNotKnown;
                break;
        }
#if COPPERSTART_NATIVE_SFS_WRITES
        if ((SpecializeFormat ? FormatIsSmart : Smart(state)) && error != DOS.Error.None && SmartMutation(action))
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
                var inputPointer = APTR.FromPointer(input);
                var outputPointer = APTR.FromPointer(output);
                // The admitted RAM spans do not overlap. Scalar C# copies
                // let the compiler combine each access with its pointer advance.
                while (remaining >= 32)
                {
                    var word0 = APTR.ReadUInt32(inputPointer, 0);
                    inputPointer = APTR.FromPointer(inputPointer.Raw + 4);
                    APTR.WriteUInt32(outputPointer, 0, word0);
                    outputPointer = APTR.FromPointer(outputPointer.Raw + 4);
                    var word1 = APTR.ReadUInt32(inputPointer, 0);
                    inputPointer = APTR.FromPointer(inputPointer.Raw + 4);
                    APTR.WriteUInt32(outputPointer, 0, word1);
                    outputPointer = APTR.FromPointer(outputPointer.Raw + 4);
                    var word2 = APTR.ReadUInt32(inputPointer, 0);
                    inputPointer = APTR.FromPointer(inputPointer.Raw + 4);
                    APTR.WriteUInt32(outputPointer, 0, word2);
                    outputPointer = APTR.FromPointer(outputPointer.Raw + 4);
                    var word3 = APTR.ReadUInt32(inputPointer, 0);
                    inputPointer = APTR.FromPointer(inputPointer.Raw + 4);
                    APTR.WriteUInt32(outputPointer, 0, word3);
                    outputPointer = APTR.FromPointer(outputPointer.Raw + 4);
                    var word4 = APTR.ReadUInt32(inputPointer, 0);
                    inputPointer = APTR.FromPointer(inputPointer.Raw + 4);
                    APTR.WriteUInt32(outputPointer, 0, word4);
                    outputPointer = APTR.FromPointer(outputPointer.Raw + 4);
                    var word5 = APTR.ReadUInt32(inputPointer, 0);
                    inputPointer = APTR.FromPointer(inputPointer.Raw + 4);
                    APTR.WriteUInt32(outputPointer, 0, word5);
                    outputPointer = APTR.FromPointer(outputPointer.Raw + 4);
                    var word6 = APTR.ReadUInt32(inputPointer, 0);
                    inputPointer = APTR.FromPointer(inputPointer.Raw + 4);
                    APTR.WriteUInt32(outputPointer, 0, word6);
                    outputPointer = APTR.FromPointer(outputPointer.Raw + 4);
                    var word7 = APTR.ReadUInt32(inputPointer, 0);
                    inputPointer = APTR.FromPointer(inputPointer.Raw + 4);
                    APTR.WriteUInt32(outputPointer, 0, word7);
                    outputPointer = APTR.FromPointer(outputPointer.Raw + 4);
                    remaining -= 32;
                }
                input = inputPointer.Raw; output = outputPointer.Raw;
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
    public uint ReadDataBlocks(uint block, uint count, APTR destination) =>
        ReadDataBlocks(block,count,destination,true);

    public uint ReadDataBlocks(uint block, uint count, APTR destination, bool readAhead)
    {
        if (RedoOverlay.IsNotNull) return AmigaFileSystemBlockIo.Read(ref this, block, count, destination);
#if COPPERSTART_NATIVE_SFS_WRITES
        var batch = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartTransactionBufferOffset));
        if (batch.IsNotNull && ReadUInt32(batch) != 0)
            return AmigaFileSystemBlockIo.Read(ref this, block, count, destination);
        var dirty = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartWriteBufferOffset));
        if (dirty.IsNotNull && AmigaSmartFileSystemWriteBuffer.Intersects(ref this, dirty,
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockBytesOffset), block, count))
            return AmigaFileSystemBlockIo.Read(ref this, block, count, destination);
#endif
#if COPPERSTART_NATIVE_SFS_WRITES && !COPPERSTART_FFS_ONLY
        var queue = WriteQueue;
        if (queue.IsNotNull && AmigaFileSystemWriteCombiner.Intersects(ref this, queue, block, count))
            return AmigaFileSystemBlockIo.Read(ref this, block, count, destination);
#endif
        if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.RamBlocksOffset) != 0)
            return AmigaFileSystemBlockIo.Read(ref this, block, count, destination);
        if (!readAhead)
        {
#if COPPERSTART_SFS_STANDALONE && COPPERSTART_NATIVE_SFS2
            if (NativeSfs2Bridge.IsSfs2(state)) return ReadSfs2Direct(block, count, destination);
#endif
            return TransferData(false, block, count, destination);
        }
        return ReadDataWithReadAhead(block, count, destination);
    }

    private uint ReadDataWithReadAhead(uint block, uint count, APTR destination)
    {
        // A direct transfer for every short packet discards the physical
        // read-ahead window. Keep small FFS/SFS2 reads on the cached path;
        // classic SFS retains its separate data/metadata cache policy.
        var type = APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset);
        var bytes = APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockBytesOffset);
        var policy=APTR.ReadUInt32(state,NativeFileSystemStateCodec.PayloadCacheBypassOffset);
        if ((type == NativeSfs2Bridge.DosType || type is 0x444F5301 or 0x444F5303 or 0x444F5305) &&
            bytes != 0 && bytes <= 4096 && count != 0 && count <= 4096 / bytes &&
            count < APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheBlocksOffset) &&
            (policy == 0 || policy == 3 && bytes == 512 && count == 1))
        {
            if (count == 1) return ReadAheadDataBlock(block, destination, bytes);
            return ReadAheadDataBlocks(block, count, destination, bytes);
        }
        return TransferData(false, block, count, destination);
    }

#if COPPERSTART_SFS_STANDALONE && COPPERSTART_NATIVE_SFS2
    private uint ReadSfs2Direct(uint block, uint count, APTR destination)
    {
        var cache = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartEnumerationCacheOffset));
        if (cache.IsNotNull && count != 0 && count <= APTR.ReadUInt32(cache, NativeAmigaFileSystem.Sfs2DirectReadLimitOffset) &&
            (destination.Raw & 3) == 0)
        {
            var volumeBlocks = APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockCountOffset);
            var bytes = count << 9;
            if (block >= volumeBlocks || count > volumeBlocks - block || destination.Raw > uint.MaxValue - bytes) return 0;
            APTR.WriteUInt32(state, NativeFileSystemStateCodec.CacheTrackOffset, uint.MaxValue);
            var offset = APTR.ReadUInt32(state, NativeFileSystemStateCodec.StartByteLowOffset) + (block << 9);
            return TransferDirect32(2, offset, bytes, destination) ? count : 0;
        }
        return TransferData(false, block, count, destination);
    }
#endif

    // The frequent one-block packet needs no run/window bookkeeping or the
    // larger batching frame. Retain the same physical read-ahead and fallback.
    private uint ReadAheadDataBlock(uint block, APTR destination, uint bytes)
    {
        if (block >= APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockCountOffset) ||
            bytes > uint.MaxValue - destination.Raw) return 0;
        var image = ReadPhysicalCached(block, 0, destination, false);
        if (image.IsNull) return TransferData(false, block, 1, destination);
        if (image != destination) CopyBlock(image, destination, bytes);
        return 1;
    }

#if COPPERFS_NATIVE_FFS_READAHEAD
    internal static bool TryDispatchCachedFfsRead(APTR state, APTR packet)
    {
        var length = APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument3Offset);
        if (length < 512 || length > 4096 || (length & 511) != 0) return false;
        var type = APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset);
        if (type is not (0x444F5301 or 0x444F5303 or 0x444F5305)) return false;
        var token = APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument1Offset);
        var file = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.ObjectsOffset));
        var ownershipBudget = 4096;
        while (file.IsNotNull && file.Raw != token && --ownershipBudget > 0)
            file = APTR.FromPointer(APTR.ReadUInt32(file, 0));
        if (ownershipBudget == 0 || file.IsNull || file.Raw != token || APTR.ReadUInt32(file, 16) != 1 || APTR.ReadUInt32(file, 92) != 0) return false;
        var position = APTR.ReadUInt32(file, 12);
        // No I/O or writes intervene before the mode's last use below. Sample
        // this mount/file decision once, rather than reloading the timer at
        // every guard on an already learned cached stream. Four means no clock.
        var adaptiveMode = length == 4096 && APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartTimerOffset) != 0
            ? APTR.ReadUInt32(file, 108) : 4u;
        if (adaptiveMode < 2 && APTR.ReadUInt32(state, NativeFileSystemStateCodec.FixedHardDiskOffset) != 0) return false;
        var destination = APTR.FromPointer(APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument2Offset));
        // The completion below reloads packet arguments and position after the
        // copy. Aliases retain the original decoder and cursor writeback path.
        if (!Separated(destination, length, packet, 64) ||
            !Separated(destination, length, state, NativeAmigaFileSystem.StateBytes)) return false;
        if (adaptiveMode < 3 &&
            APTR.ReadUInt32(file, 104) != 0 && APTR.ReadUInt32(file, 100) != position)
        {
            if (Separated(destination, length, file, 216)) NativeFfsSequentialRead.Reset(file);
            return false;
        }
        if (length == 4096 && (adaptiveMode == 2 ||
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.PayloadCacheBypassOffset) == 3))
            return TryDispatchDirectFfsPacket(state, packet, file, position, type);
#else
    internal static bool TryReadFfsCached(APTR state, APTR file, uint position, APTR destination, uint length)
    {
#endif
        // Native records and the physical window already own validated runs.
        // Complete cache hits and eligible direct packets can avoid the general
        // range reader. Tails, aliases and logical overlays keep that reader.
#if !COPPERFS_NATIVE_FFS_READAHEAD
        var type = APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset);
#endif
        var payloadPolicy = APTR.ReadUInt32(state, NativeFileSystemStateCodec.PayloadCacheBypassOffset);
        if (length == 0 || length > 4096 || ((length | position) & 511) != 0 || (destination.Raw & 3) != 0 ||
            type is not (0x444F5301 or 0x444F5303 or 0x444F5305) ||
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockBytesOffset) != 512 ||
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.BuffersOffset) != 0 ||
            // Diagnostic scsi.device bypass (3) retains one-sector read-ahead.
            // Other bypass policies still require their original direct path.
            (payloadPolicy != 0 && (length > 512 || payloadPolicy != 3)) ||
            (APTR.ReadUInt32(state, NativeFileSystemStateCodec.RamBlocksOffset) |
             APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRedoOverlayOffset) |
             APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartWriteBufferOffset) |
             APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartTransactionBufferOffset) |
             APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartWriteCombinerOffset)) != 0) return false;
        var header = APTR.ReadUInt32(file, 4);
        if (header == 0 || APTR.ReadUInt32(file, 56) != header || APTR.ReadUInt32(file, 84) != type) return false;
        var size = APTR.ReadUInt32(file, 60);
        if (position > size || length > size - position) return false;
        var index = position >> 9; var start = APTR.ReadUInt32(file, 72); var run = APTR.ReadUInt32(file, 80);
        var count = length >> 9;
        if (index < start || index - start >= run || count > run - (index - start)) return false;
        var block = APTR.ReadUInt32(file, 76) + index - start;
        var total = APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockCountOffset);
#if COPPERFS_NATIVE_FFS_READAHEAD
        if (block >= total || count > total - block) return false;
#endif
        var first = APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheTrackOffset);
        var capacity = APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheBlocksOffset);
        var cache = APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheOffset);
        if (block >= total || count > total - block || first == uint.MaxValue || block < first ||
            block - first >= capacity || count > capacity - (block - first) || cache == 0) return false;
        var p = new NativeFileSystemPlatform(state);
        if (p.FindCached(block).IsNotNull || !p.IsMapped(destination, length)) return false;
        var source = APTR.FromPointer(cache + ((block - first) << 9));
        var end = destination.Raw + length; // IsMapped has excluded wrapping spans.
        var scratch = state.Raw + NativeAmigaFileSystem.MetadataOffset;
        if (!(end <= file.Raw || destination.Raw >= file.Raw + 216) ||
            !(end <= scratch || destination.Raw >= scratch + 1024) ||
            source != destination && !(end <= source.Raw || destination.Raw >= source.Raw + length)) return false;
        if (source != destination) CopyReadAhead(source, destination, length);
#if COPPERFS_NATIVE_FFS_READAHEAD
        // Only packet and mount pointers need survive the copy call. The
        // disjoint spans above preserve these arguments and the owned file.
        var completedFile = APTR.FromPointer(APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument1Offset));
        var completed = APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument3Offset);
        if (completed == 4096 && APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartTimerOffset) != 0)
        {
            if (APTR.ReadUInt32(completedFile, 108) != 3 &&
                APTR.ReadUInt32(state, NativeFileSystemStateCodec.FixedHardDiskOffset) != 0)
                NativeFfsSequentialRead.Record(completedFile, APTR.ReadUInt32(completedFile, 12));
        }
        else if (completed != 4096 && APTR.ReadUInt32(completedFile, 104) != 0) NativeFfsSequentialRead.Reset(completedFile);
        APTR.WriteUInt32(completedFile, 12, APTR.ReadUInt32(completedFile, 12) + completed);
        APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result1Offset, completed);
        APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result2Offset, 0);
#endif
        return true;
    }

#if COPPERFS_NATIVE_FFS_READAHEAD
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryDispatchDirectFfsPacket(APTR state, APTR packet, APTR file, uint position, uint type)
    {
        const uint length = 4096, count = 8;
        var destination = APTR.FromPointer(APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument2Offset));
        var policy = APTR.ReadUInt32(state, NativeFileSystemStateCodec.PayloadCacheBypassOffset);
        if ((policy != 3 && (policy != 0 || !NativeFfsSequentialRead.UseDirect(file, position))) ||
            (position & 511) != 0 || (destination.Raw & 3) != 0 ||
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockBytesOffset) != 512 ||
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.BuffersOffset) != 0 ||
            (APTR.ReadUInt32(state, NativeFileSystemStateCodec.RamBlocksOffset) |
             APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRedoOverlayOffset) |
             APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartWriteBufferOffset) |
             APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartTransactionBufferOffset) |
             APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartWriteCombinerOffset)) != 0) return false;
        var header = APTR.ReadUInt32(file, 4);
        if (header == 0 || APTR.ReadUInt32(file, 56) != header || APTR.ReadUInt32(file, 84) != type) return false;
        var size = APTR.ReadUInt32(file, 60);
        if (position > size || length > size - position) return false;
        var index = position >> 9; var start = APTR.ReadUInt32(file, 72); var run = APTR.ReadUInt32(file, 80);
        if (index < start || index - start >= run || count > run - (index - start)) return false;
        var block = APTR.ReadUInt32(file, 76) + index - start;
        var total = APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockCountOffset);
        if (block >= total || count > total - block ||
            !Separated(destination, length, file, 216) ||
            !Separated(destination, length, APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheOffset)),
                APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheBytesOffset))) return false;
        var p = new NativeFileSystemPlatform(state);
        if (!p.IsMapped(destination, length) || p.FindCached(block).IsNotNull) return false;
        if (policy == 3) DispatchBypassFfsRead(state, packet, block, count);
        else DispatchDirectFfsRead(state, packet, block, count);
        return true;
    }

    // Explicit bypass does not sample or update the adaptive stream hints.
    // Keep its completion separate from the learned-direct reader's frame.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DispatchBypassFfsRead(APTR state, APTR packet, uint block, uint count)
    {
        var destination = APTR.FromPointer(APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument2Offset));
        var p = new NativeFileSystemPlatform(state);
        var actual = p.TransferData(false, block, count, destination);
        var file = APTR.FromPointer(APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument1Offset));
        if (actual == count)
        {
            var bytes = count << 9;
            APTR.WriteUInt32(file, 12, APTR.ReadUInt32(file, 12) + bytes);
            APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result1Offset, bytes);
            APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result2Offset, 0);
        }
        else
        {
            APTR.WriteUInt32(file, 56, 0);
            var prefix = actual == 0 || actual > count ? uint.MaxValue : actual << 9;
            if (prefix != uint.MaxValue) APTR.WriteUInt32(file, 12, APTR.ReadUInt32(file, 12) + prefix);
            APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result1Offset, prefix);
            APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result2Offset, (uint)DOS.Error.DiskNotValidated);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DispatchDirectFfsRead(APTR state, APTR packet, uint block, uint count)
    {
        var destination = APTR.FromPointer(APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument2Offset));
        var p = new NativeFileSystemPlatform(state);
        var actual = p.TransferData(false, block, count, destination);
        var file = APTR.FromPointer(APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument1Offset));
        if (actual == count)
        {
            var bytes = count << 9;
            NativeFfsSequentialRead.Record(file, APTR.ReadUInt32(file, 12));
            APTR.WriteUInt32(file, 12, APTR.ReadUInt32(file, 12) + bytes);
            APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result1Offset, bytes);
            APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result2Offset, 0);
        }
        else
        {
            APTR.WriteUInt32(file, 56, 0); NativeFfsSequentialRead.Reset(file);
            var prefix = actual == 0 || actual > count ? uint.MaxValue : actual << 9;
            if (prefix != uint.MaxValue) APTR.WriteUInt32(file, 12, APTR.ReadUInt32(file, 12) + prefix);
            APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result1Offset, prefix);
            APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result2Offset, (uint)DOS.Error.DiskNotValidated);
        }
    }

    internal void ReadAheadFfsMetadata(APTR file, APTR output, uint outputBytes)
    {
        // Diagnostic bypass also primes four-KiB streams. Normal four-KiB
        // reads retain their existing adaptive payload/cache decision.
        if (outputBytes == 4096 && APTR.ReadUInt32(state, NativeFileSystemStateCodec.PayloadCacheBypassOffset) != 3) return;
        if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.FixedHardDiskOffset) == 0 ||
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockBytesOffset) != 512 ||
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.ExtraCacheCountOffset) < 32 ||
            (APTR.ReadUInt32(state, NativeFileSystemStateCodec.RamBlocksOffset) |
             APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRedoOverlayOffset) |
             APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartWriteBufferOffset) |
             APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartTransactionBufferOffset) |
             APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartWriteCombinerOffset)) != 0) return;
        var type = APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset);
        var header = APTR.ReadUInt32(file, 4);
        if (type is not (0x444F5301 or 0x444F5303 or 0x444F5305) || header == 0 ||
            APTR.ReadUInt32(file, 56) != header || APTR.ReadUInt32(file, 84) != type) return;
        var metadata = NativeAmigaFileSystem.Metadata(state);
        if (!Separated(output, outputBytes, file, 216) ||
            !Separated(output, outputBytes, state, NativeAmigaFileSystem.StateBytes) ||
            !Separated(output, outputBytes, APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheOffset)),
                APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheBytesOffset))) return;
        var entry = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.ExtraCacheHeadOffset));
        var entries = APTR.ReadUInt32(state, NativeFileSystemStateCodec.ExtraCacheCountOffset);
        while (entry.IsNotNull && entries-- != 0)
        {
            if (!Separated(output, outputBytes, entry, NativeAmigaFileSystem.ExtraCacheHeaderBytes + 512)) return;
            entry = APTR.FromPointer(APTR.ReadUInt32(entry, 0));
        }
        var size = APTR.ReadUInt32(file, 60);
        var current = APTR.ReadUInt32(file, 68);
        if (size == 0) return;
        var last = ((size - 1) >> 9) / 72;
        if (current >= last) return;
        var budget = last - current; if (budget > 28) budget = 28;
        var next = APTR.ReadUInt32(file, 96);
        while (budget-- != 0 && next >= ReservedBlocks && next < BlockCount)
        {
            APTR view;
            // Decline speculation when the metadata slot cannot receive this
            // device's transfer. A bounce refill would evict payload read-ahead.
            if (!TryBorrowFfsMetadataBlock(next, metadata, output, outputBytes, out view)) return;
            // Speculation follows only plausible, bounded extension links.
            // The ordinary reader still checks each checksum when needed;
            // future I/O or validation errors do not change this packet.
            if (view.IsNull || ReadUInt32(view) != 16 ||
                ReadUInt32(view, 4) != next || ReadUInt32(view, 500) != header || ReadUInt32(view, 508) != uint.MaxValue - 2) return;
            next = ReadUInt32(view, 504);
        }
    }

    private static bool Separated(APTR a, uint bytes, APTR b, uint otherBytes) =>
        a.Raw <= b.Raw ? b.Raw - a.Raw >= bytes : a.Raw - b.Raw >= otherBytes;
#endif

    private uint ReadAheadDataBlocks(uint block, uint count, APTR destination, uint bytes)
    {
        // Logical overlays and RAM were resolved by ReadDataBlocks. A complete
        // 512-byte-sector cache hit needs no refill descriptor or loop frame.
        if (bytes == 512)
        {
            var total = APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockCountOffset);
            if (block >= total || count > total - block || count > (uint.MaxValue - destination.Raw) >> 9) return 0;
            var first = APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheTrackOffset);
            var capacity = APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheBlocksOffset);
            var cache = APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheOffset);
            if (first != uint.MaxValue && block >= first && block - first < capacity &&
                count <= capacity - (block - first) && cache != 0 && FindCached(block).IsNull)
            {
                var source = APTR.FromPointer(cache + ((block - first) << 9));
                var length = count << 9;
                var distance = source.Raw > destination.Raw ? source.Raw - destination.Raw : destination.Raw - source.Raw;
                if (source == destination || distance >= length)
                {
                    if (source != destination)
                    {
                        if (((source.Raw | destination.Raw) & 1) == 0) CopyReadAhead(source, destination, length);
                        else CopyBlock(source, destination, length);
                    }
                    return count;
                }
            }
        }
        return ReadAheadDataBlocksSlow(block, count, destination, bytes);
    }

    // Caller supplies non-overlapping even RAM spans, a positive multiple
    // of 512 bytes no larger than 4 KiB.
    private static void CopyReadAhead(APTR source, APTR destination, uint bytes)
    {
        var inputPointer = source; var outputPointer = destination;
        var groups = bytes >> 5;
        do
        {
            var word0 = APTR.ReadUInt32(inputPointer, 0);
            inputPointer = APTR.FromPointer(inputPointer.Raw + 4);
            APTR.WriteUInt32(outputPointer, 0, word0);
            outputPointer = APTR.FromPointer(outputPointer.Raw + 4);
            var word1 = APTR.ReadUInt32(inputPointer, 0);
            inputPointer = APTR.FromPointer(inputPointer.Raw + 4);
            APTR.WriteUInt32(outputPointer, 0, word1);
            outputPointer = APTR.FromPointer(outputPointer.Raw + 4);
            var word2 = APTR.ReadUInt32(inputPointer, 0);
            inputPointer = APTR.FromPointer(inputPointer.Raw + 4);
            APTR.WriteUInt32(outputPointer, 0, word2);
            outputPointer = APTR.FromPointer(outputPointer.Raw + 4);
            var word3 = APTR.ReadUInt32(inputPointer, 0);
            inputPointer = APTR.FromPointer(inputPointer.Raw + 4);
            APTR.WriteUInt32(outputPointer, 0, word3);
            outputPointer = APTR.FromPointer(outputPointer.Raw + 4);
            var word4 = APTR.ReadUInt32(inputPointer, 0);
            inputPointer = APTR.FromPointer(inputPointer.Raw + 4);
            APTR.WriteUInt32(outputPointer, 0, word4);
            outputPointer = APTR.FromPointer(outputPointer.Raw + 4);
            var word5 = APTR.ReadUInt32(inputPointer, 0);
            inputPointer = APTR.FromPointer(inputPointer.Raw + 4);
            APTR.WriteUInt32(outputPointer, 0, word5);
            outputPointer = APTR.FromPointer(outputPointer.Raw + 4);
            var word6 = APTR.ReadUInt32(inputPointer, 0);
            inputPointer = APTR.FromPointer(inputPointer.Raw + 4);
            APTR.WriteUInt32(outputPointer, 0, word6);
            outputPointer = APTR.FromPointer(outputPointer.Raw + 4);
            var word7 = APTR.ReadUInt32(inputPointer, 0);
            inputPointer = APTR.FromPointer(inputPointer.Raw + 4);
            APTR.WriteUInt32(outputPointer, 0, word7);
            outputPointer = APTR.FromPointer(outputPointer.Raw + 4);
        } while (--groups != 0);
    }

    private uint ReadAheadDataBlocksSlow(uint block, uint count, APTR destination, uint bytes)
    {
        var total = APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockCountOffset);
        if (block >= total || count > total - block || count > (uint.MaxValue - destination.Raw) / bytes) return 0;
        var completed = 0u;
        while (completed < count)
        {
            var target = APTR.FromPointer(destination.Raw + completed * bytes);
            // Overlay precedence was checked once by ReadDataBlocks. File data
            // can use the track window without copying every sector into the
            // metadata LRU as well as into the caller's destination.
            var image = ReadPhysicalCached(block + completed, 0, target, false);
            if (image.IsNull)
                // A neighboring sector can fail during speculative refill.
                // Retry only the requested suffix, preserving exact completion
                // and the existing MaxTransfer/DMA/TD64 handling.
                return completed + TransferData(false, block + completed, count - completed, target);
            var blocks = 1u;
            var first = APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheTrackOffset);
            var capacity = APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheBlocksOffset);
            var cache = APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheOffset);
            var current = block + completed;
            if (first != uint.MaxValue && current >= first && current - first < capacity &&
                image.Raw == cache + (current - first) * bytes)
            {
                blocks = count - completed;
                var available = capacity - (current - first);
                if (blocks > available) blocks = available;
                var length = blocks * bytes;
                var distance = image.Raw > target.Raw ? image.Raw - target.Raw : target.Raw - image.Raw;
                // Preserve the old sector-at-a-time behavior for aliased
                // private buffers; ordinary caller buffers use one bulk copy.
                if (image != target && distance < length) blocks = 1;
            }
            if (image != target) CopyBlock(image, target, blocks * bytes);
            completed += blocks;
        }
        return completed;
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
                return TransferDirect32((ushort)(write ? 3 : 2), start + (block << 9), length, caller) ? count : 0;
            }
        }
        return TransferDataChunked(write, block, count, caller, blockBytes, maximumBytes);
    }

    // In CMD_READ/CMD_WRITE io_Actual starts at zero. Only TD64 needs a
    // high offset word. A short successful DoIO still fails the request.
    private bool TransferDirect32(ushort command, uint offset, uint bytes, APTR buffer)
    {
        var request = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.RequestOffset));
        APTR.WriteUInt16(request, NativeExecFieldCodec.IoCommandOffset, command);
        APTR.WriteUInt32(request, NativeExecFieldCodec.IoDataOffset, buffer.Raw);
        APTR.WriteUInt32(request, NativeExecFieldCodec.IoLengthOffset, bytes);
        APTR.WriteUInt32(request, NativeExecFieldCodec.IoByteOffset, offset);
        APTR.WriteUInt32(request, NativeExecFieldCodec.IoActualOffset, 0);
        global::Amiga.Exec.DoIO(request);
        return APTR.ReadUInt8(request, NativeExecFieldCodec.IoErrorOffset) == 0 &&
            APTR.ReadUInt32(request, NativeExecFieldCodec.IoActualOffset) == bytes;
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

    public bool TryBorrowFfsMetadataBlock(uint block, APTR scratch, APTR output, uint outputBytes, out APTR view)
    {
        view = APTR.Null;
        var dosType = APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset);
        if (scratch != NativeAmigaFileSystem.Metadata(state) ||
            dosType is not (0x444F5301 or 0x444F5303 or 0x444F5305) ||
            block >= BlockCount || RedoOverlay.IsNotNull ||
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.RamBlocksOffset) != 0) return false;
#if COPPERSTART_NATIVE_SFS_WRITES
        if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartWriteBufferOffset) != 0 ||
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartTransactionBufferOffset) != 0) return false;
#endif
#if COPPERSTART_NATIVE_SFS_WRITES && !COPPERSTART_FFS_ONLY
        if (WriteQueue.IsNotNull) return false;
#endif
        var entry = FindCached(block);
        if (entry.IsNotNull)
        {
            var image = APTR.FromPointer(entry.Raw + NativeAmigaFileSystem.ExtraCacheHeaderBytes);
            if (!MetadataViewDisjoint(output, outputBytes, image, BlockBytes)) return false;
            TouchCached(entry);
            view = image; return true;
        }
        var tail = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.ExtraCacheTailOffset));
        return TryFillMetadataSlot(block, tail, output, outputBytes, out view);
    }

    // Both filesystems can read into the victim's payload when DMA permits.
    // A handled I/O failure leaves the old key and verification invalidated.
    private bool TryFillMetadataSlot(uint block, APTR tail, APTR output, uint outputBytes, out APTR view)
    {
        view = APTR.Null;
        if (tail.IsNull) return false;
        var target = APTR.FromPointer(tail.Raw + NativeAmigaFileSystem.ExtraCacheHeaderBytes);
        if (!MetadataViewDisjoint(output, outputBytes, target, BlockBytes) ||
            !CanTransferDirect(target, BlockBytes)) return false;
        var first = APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheTrackOffset);
        var capacity = APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheBlocksOffset);
        var cache = APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheOffset);
        var trackHit = cache != 0 && first != uint.MaxValue && block >= first && block - first < capacity;
        // Forget the victim before a physical read can partially overwrite it.
        ForgetCached(tail);
        if (trackHit)
        {
            var source = APTR.FromPointer(cache + (block - first) * BlockBytes);
            CacheBlock(block, 0, source, BlockBytes);
        }
        else
        {
            if (!TransferBlock(false, block, target)) return true;
            CacheBlock(block, 0, target, BlockBytes);
        }
        view = target;
        return true;
    }

    private static bool MetadataViewDisjoint(APTR output, uint outputBytes, APTR image, uint imageBytes) =>
        outputBytes == 0 || (output.Raw <= image.Raw
            ? image.Raw - output.Raw >= outputBytes
            : output.Raw - image.Raw >= imageBytes);

    private bool CanReadPhysicalCache(uint block, out APTR queued)
    {
        queued = APTR.Null;
        if (block >= BlockCount || RedoOverlay.IsNotNull ||
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.RamBlocksOffset) != 0) return false;
#if COPPERSTART_NATIVE_SFS_WRITES
        // Callers can always use ReadBlock when a logical overlay is active.
        // Do not expose a physical cache image beneath acknowledged dirty data.
        var dirty = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartWriteBufferOffset));
        if (dirty.IsNotNull && ReadUInt32(dirty, AmigaSmartFileSystemWriteBuffer.HiddenOffset) == 0 &&
            AmigaSmartFileSystemWriteBuffer.Count(ref this, dirty) != 0) return false;
        var batch = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartTransactionBufferOffset));
        if (batch.IsNotNull && ReadUInt32(batch) != 0) return false;
#endif
#if COPPERSTART_NATIVE_SFS_WRITES && !COPPERSTART_FFS_ONLY
        queued = AmigaFileSystemWriteCombiner.ReadView(ref this, WriteQueue, block);
#endif
        return queued.IsNull;
    }

    public APTR ReadRetainedBlock(uint block)
    {
        if (!CanReadPhysicalCache(block, out var queued)) return queued;
        var entry = FindCached(block);
        if (entry.IsNull)
        {
            // FFS partial reads can borrow a current payload-window image
            // without copying a whole sector into the metadata LRU/scratch.
            // Overlay and queued-write precedence was resolved above.
            var type=APTR.ReadUInt32(state,NativeFileSystemStateCodec.DosTypeOffset);
            var first=APTR.ReadUInt32(state,NativeFileSystemStateCodec.CacheTrackOffset);
            var capacity=APTR.ReadUInt32(state,NativeFileSystemStateCodec.CacheBlocksOffset);
            var cache=APTR.ReadUInt32(state,NativeFileSystemStateCodec.CacheOffset);
            if(type is 0x444F5301 or 0x444F5303 or 0x444F5305 && cache!=0 &&
                first!=uint.MaxValue && block>=first && block-first<capacity)
                return APTR.FromPointer(cache+(block-first)*BlockBytes);
            return APTR.Null;
        }
        TouchCached(entry);
        return APTR.FromPointer(entry.Raw + NativeAmigaFileSystem.ExtraCacheHeaderBytes);
    }
    public bool ReadBlocks(uint block, uint count, APTR destination) => ReadDataBlocks(block, count, destination) == count;

    public bool TryReadVerifiedSfs2Block(uint block, uint id, APTR destination)
    {
        var image = BorrowVerifiedSfs2Block(block, id, destination);
        if (image.IsNull) return false;
        CopyBlock(image, destination, BlockBytes);
        return true;
    }

    public APTR BorrowVerifiedSfs2Block(uint block, uint id, APTR scratch)
    {
        ReadSfs2MetadataView(block, id, scratch, false, out var view);
        return view;
    }

    public bool TryReadSfs2MetadataView(uint block, uint id, APTR scratch, out APTR view) =>
        ReadSfs2MetadataView(block, id, scratch, true, out view);

    private bool ReadSfs2MetadataView(uint block, uint id, APTR scratch, bool fill, out APTR view)
    {
        view = APTR.Null;
        if (scratch != NativeAmigaFileSystem.Metadata(state) || id == 0 || (id & 0x80000000) != 0 ||
            !CanReadPhysicalCache(block, out _)) return false;
        var entry = FindCached(block);
        APTR image;
        if (entry.IsNotNull)
        {
            TouchCached(entry);
            image = APTR.FromPointer(entry.Raw + NativeAmigaFileSystem.ExtraCacheHeaderBytes);
        }
        else
        {
            // Substitute the slot only for a read which would already transfer
            // one sector group into private metadata scratch. Preserve the track
            // refill path when the scratch or slot requires a bounce buffer.
            if (!fill || APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset) != NativeSfs2Bridge.DosType ||
                !CanTransferDirect(scratch, BlockBytes) || !MetadataViewDisjoint(scratch, BlockBytes,
                    APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheOffset)),
                    APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheBytesOffset))) return false;
            entry = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.ExtraCacheTailOffset));
            if (!TryFillMetadataSlot(block, entry, APTR.Null, 0, out image)) return false;
            if (image.IsNull) return true;
        }
        if (ReadUInt32(image) != id || ReadUInt32(image, 8) != block) return true;
        // Classic verification stores the actual ID. Its complement denotes
        // the SFS2 checksum on this particular immutable cache image.
        if (ReadUInt32(entry, NativeFileSystemCacheEntryCodec.VerificationOffset) != ~id)
        {
            if (!AmigaFileSystemBlockIo.ValidSfs2Sum(ref this, image, BlockBytes)) return true;
            WriteUInt32(entry, NativeFileSystemCacheEntryCodec.VerificationOffset, ~id);
        }
        view = image;
        return true;
    }
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
            if (APTR.ReadUInt32(entry, NativeFileSystemCacheEntryCodec.VerificationOffset) != id || (id & 0x80000000) != 0)
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
    private APTR ReadPhysicalCached(uint block, uint id, APTR destination, bool retain = true)
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
            if (id != 0 && (ReadUInt32(extra, 16) != id || (id & 0x80000000) != 0))
            {
                if (!AmigaFileSystemBlockIo.ValidateMetadata(ref this, block, id, source, blockBytes)) return APTR.Null;
                WriteUInt32(extra, 16, id);
            }
            TouchCached(extra);
            return source;
        }
        var cache = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheOffset));
        APTR image;
        var dosType = APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset);
        var cacheBytes=APTR.ReadUInt32(state,NativeFileSystemStateCodec.CacheBytesOffset);
        var separate=destination.Raw <= cache.Raw ? cache.Raw-destination.Raw >= blockBytes
            : destination.Raw-cache.Raw >= cacheBytes;
        if (retain && (dosType == 0 || dosType == NativeSfs2Bridge.DosType || dosType is 0x444F5301 or 0x444F5303 or 0x444F5305) &&
            (id != 0 || destination == NativeAmigaFileSystem.Metadata(state)) && separate && CanTransferDirect(destination, blockBytes))
        {
            var first = APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheTrackOffset);
            var count = APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheBlocksOffset);
            if (first != uint.MaxValue && block >= first && block - first < count)
            {
                var retained = APTR.FromPointer(cache.Raw + (block - first) * blockBytes);
                if (id != 0 && !AmigaFileSystemBlockIo.ValidateMetadata(ref this, block, id, retained, blockBytes)) return APTR.Null;
                if (retain) CacheBlock(block, id, retained, blockBytes);
                return retained;
            }
            // Metadata and mount probes do not imply a sequential payload
            // stream. Retain the requested block in the existing metadata LRU,
            // without fetching unrelated sectors or replacing the data window.
            if (!TransferBlock(false, block, destination)) return APTR.Null;
            if (id != 0 && !AmigaFileSystemBlockIo.ValidateMetadata(ref this, block, id, destination, blockBytes))
            {
#if COPPERSTART_SFS_STANDALONE && COPPERSTART_NATIVE_SFS2
                // The classic probe is tried first. Its type mismatch still
                // leaves the secondary root available for the SFS2 validator.
                if (dosType == 0 && id == AmigaSmartFileSystemCore.DosType && block == BlockCount - 1 &&
                    ReadUInt32(destination) == NativeSfs2Bridge.DosType && ReadUInt32(destination, 8) == block)
                    CacheBlock(block, 0, destination, blockBytes);
#endif
                return APTR.Null;
            }
            if (retain) CacheBlock(block, id, destination, blockBytes);
            return destination;
        }
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
            var cacheBlocks = APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheBlocksOffset);
            if (cacheBlocks == 0) return APTR.Null;
            var first = AmigaGeometryMath.Divide(block, cacheBlocks) * cacheBlocks;
            if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheTrackOffset) != first)
            {
                APTR.WriteUInt32(state, NativeFileSystemStateCodec.CacheTrackOffset, uint.MaxValue);
                var count = BlockCount - first;
                if (count > cacheBlocks) count = cacheBlocks;
                if (!AmigaFileSystemDeviceCore.Transfer(ref this, Geometry, false, first, count, cache)) return APTR.Null;
                APTR.WriteUInt32(state, NativeFileSystemStateCodec.CacheTrackOffset, first);
            }
            image = APTR.FromPointer(cache.Raw + (block - first) * blockBytes);
        }
        if (id != 0 && !AmigaFileSystemBlockIo.ValidateMetadata(ref this, block, id, image, blockBytes)) return APTR.Null;
        if (retain) CacheBlock(block, id, image, blockBytes);
        return image;
    }

#if COPPERSTART_SFS_STANDALONE && COPPERSTART_NATIVE_SFS2
    internal void RetainSfs2MountPrefix(APTR primary, bool emptyCache = false)
    {
        // SFS2 commonly places the directory, clean marker and node tree near
        // the primary root. Bound speculation to 4 KiB of block-aligned data.
        var bytes = BlockBytes;
        uint blocks = bytes == 512 ? 8u : bytes == 1024 ? 4u : 0u;
        if (blocks == 0 || blocks > BlockCount || APTR.ReadUInt32(state, NativeFileSystemStateCodec.ExtraCacheCountOffset) < 2 * blocks ||
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheBlocksOffset) < blocks ||
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheTrackOffset) != uint.MaxValue ||
            (!emptyCache && FindCached(0).IsNotNull)) return;

        var first = 0u;
        if (primary.IsNotNull)
        {
            CacheBlock(0, 0, primary, bytes);
            first = 1;
        }
        var count = blocks - first;
        var buffer = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.CacheOffset));
        if (buffer.IsNull || count * bytes > APTR.ReadUInt32(state, NativeFileSystemStateCodec.MaxTransferOffset) ||
            !CanTransferDirect(buffer, count * bytes) || TransferData(false, first, count, buffer) != count) return;

        if (emptyCache && primary.IsNull)
        {
            // The whole LRU is empty after uninhibit. Fill it from the head
            // in reverse block order, leaving populated slots ahead of empty
            // ones just as successive CacheBlock calls would. Prefix keys
            // are distinct and each has its own empty hash bucket.
            var entry = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.ExtraCacheHeadOffset));
            var block = blocks;
            while (block != 0 && entry.IsNotNull)
            {
                block--;
                var slot = APTR.FromPointer(buffer.Raw + block * bytes);
                if (!Sfs2MountMetadata(slot, block)) continue;
                var target = APTR.FromPointer(entry.Raw + NativeAmigaFileSystem.ExtraCacheHeaderBytes);
                CopyReadAhead(slot, target, bytes);
                APTR.WriteUInt32(entry, NativeFileSystemCacheEntryCodec.KeyOffset, block);
                APTR.WriteUInt32(entry, NativeFileSystemCacheEntryCodec.HashNextOffset, 0);
                APTR.WriteUInt32(state, NativeFileSystemStateCodec.CacheHashOffset + (int)block * 4, entry.Raw);
                entry = APTR.FromPointer(APTR.ReadUInt32(entry, NativeFileSystemCacheEntryCodec.NextOffset));
            }
            return;
        }

        var image = buffer;
        for (var block = first; block < blocks; block++)
        {
            if (Sfs2MountMetadata(image, block) && FindCached(block).IsNull)
                CacheBlock(block, 0, image, bytes);
            image = APTR.FromPointer(image.Raw + bytes);
        }
    }

    private static bool Sfs2MountMetadata(APTR image, uint block) =>
        APTR.ReadUInt32(image, 0) is NativeSfs2Bridge.DosType or 0x4F424A43 or 0x54524F4B or 0x4E444320 or 0x424E4443 &&
        APTR.ReadUInt32(image, 8) == block;
#endif

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
            var target = APTR.FromPointer(extra.Raw + NativeAmigaFileSystem.ExtraCacheHeaderBytes);
            if (source != target) CopyBlock(source, target, bytes);
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
