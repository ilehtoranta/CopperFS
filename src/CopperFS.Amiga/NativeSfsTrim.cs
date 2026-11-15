using Amiga;

namespace CopperStart.Dos;

/// <summary>Manual packets and tracked automatic batches. The handler owns the mount throughout this
/// synchronous call: no packet dispatch, timer commit or allocation can interleave
/// with a scanned range and its completed discard. No per-mount trim allocation.</summary>
internal static class NativeSfsTrim
{
#if COPPERFS_AUTOTRIM
    internal static bool CollectAutomatic(APTR state, APTR batch, uint generation, out bool complete)
    {
        var p = new NativeFileSystemPlatform(state); complete = true;
#if !COPPERSTART_SFS_ONLY && !COPPERSTART_SMART_ONLY
#if COPPERFS_FFS_HANDLER
        const bool ffs = true;
#else
        var ffs = APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset) is 0x444F5300 or 0x444F5301;
#endif
        if (ffs)
        {
            NativeAmigaFileSystem.InitializeWriteWork(state, out var work);
            return AmigaFfsWriteCore.CollectAutomaticTrim(ref p, ref work, batch, state.Raw, generation, out complete);
        }
#endif
#if !COPPERFS_FFS_HANDLER
#if COPPERSTART_NATIVE_SFS2
        if (NativeSfs2Bridge.IsSfs2(state)) return NativeSfs2Bridge.CollectAutomatic(state, batch, generation);
#endif
        NativeAmigaFileSystem.InitializeSmartWriteWork(state, out var smart);
        var ok = AmigaSmartFileSystemWriteCore.CollectAutomaticTrim(ref p, ref smart, batch, state.Raw, generation);
        if (ok) NativeAutomaticTrim.RememberRoots(state, smart.BitmapBase, smart.RootContainer, smart.AdminContainer, smart.NodeRoot, smart.ExtentRoot);
        return ok;
#endif
    }
#endif

    internal static uint AllocateControl(APTR state, uint bytes = AmigaScsiUnmap.ControlBytes)
    {
        var g = new NativeFileSystemPlatform(state).Geometry;
        var memory = global::Amiga.Exec.AllocMem(bytes,
            (global::Amiga.Exec.MemoryFlags)g.BufferMemoryType | global::Amiga.Exec.MemoryFlags.Public);
        if (memory != 0 && !NativeAmigaFileSystem.BufferFitsMask(memory, bytes, g.Mask))
        { global::Amiga.Exec.FreeMem(memory, bytes); memory = 0; }
        if (memory == 0)
        {
            memory = global::Amiga.Exec.AllocMem(bytes,
                global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Chip);
            if (memory != 0 && !NativeAmigaFileSystem.BufferFitsMask(memory, bytes, g.Mask))
            { global::Amiga.Exec.FreeMem(memory, bytes); memory = 0; }
        }
        return memory;
    }

    internal static bool TrimTracked(APTR state, APTR control, APTR batch, uint ranges)
    {
        var p = new NativeFileSystemPlatform(state);
#if !COPPERSTART_SFS_ONLY && !COPPERSTART_SMART_ONLY
#if COPPERFS_FFS_HANDLER
        const bool ffs = true;
#else
        var ffs = APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset) is 0x444F5300 or 0x444F5301;
#endif
        if (ffs)
        {
            NativeAmigaFileSystem.InitializeWriteWork(state, out var work);
            return AmigaFfsWriteCore.TrimFreedBatch(ref p, ref work, control, batch, ranges, out _);
        }
#endif
#if !COPPERFS_FFS_HANDLER
        NativeAmigaFileSystem.InitializeSmartWriteWork(state, out var smart);
        bool ok;
#if COPPERSTART_NATIVE_SFS2
        if (NativeSfs2Bridge.IsSfs2(state)) ok = NativeSfs2Bridge.TrimFreed(state, ref p, ref smart, control, batch, ranges);
        else
#endif
        ok = AmigaSmartFileSystemWriteCore.TrimFreedBatch(ref p, ref smart, control, batch, ranges, out _);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartJournalCursorOffset, smart.JournalCursor);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartAllocationCursorOffset, smart.AllocationCursor);
        if (!ok && smart.Error == DOS.Error.DiskNotValidated)
            APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartRecoveryPendingOffset, 1);
        return ok;
#endif
    }

    public static int Execute(APTR state, APTR packet, out DOS.Error error, bool ffs = false) =>
        ExecuteRecord(state, APTR.FromPointer(APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument1Offset)),
            out error, 0, uint.MaxValue, ffs);

    internal static int ExecuteRecord(APTR state, APTR record, out DOS.Error error,
        uint startBlock, uint blockCount, bool ffs = false, APTR validation = default)
    {
        error = DOS.Error.BadNumber;
        var p = new NativeFileSystemPlatform(state);
        if (record.IsNull || !p.IsMapped(record, AmigaSmartFileSystemTrimPacket.Size) ||
            APTR.ReadUInt32(record, NativeSfsTrimPacketFieldCodec.SizeOffset) < AmigaSmartFileSystemTrimPacket.Size ||
            APTR.ReadUInt32(record, NativeSfsTrimPacketFieldCodec.VersionOffset) != AmigaSmartFileSystemTrimPacket.Version ||
            APTR.ReadUInt32(record, NativeSfsTrimPacketFieldCodec.FlagsOffset) > AmigaSmartFileSystemTrimPacket.QueryOnly) return 0;
        p.Clear(APTR.FromPointer(record.Raw + 12), 20);
#if COPPERFS_SFS_GROUPED
        // Manual reclamation must use durable allocation state. Query-only
        // packets do not reclaim blocks and need no transaction boundary.
        if (!ffs && APTR.ReadUInt32(record, NativeSfsTrimPacketFieldCodec.FlagsOffset) != AmigaSmartFileSystemTrimPacket.QueryOnly &&
            NativeSfsExtension.TrstGroupPending(state))
        {
            NativeAmigaFileSystem.InitializeSmartWriteWork(state, out var pending);
            if (!NativeSfsExtension.Flush(state, ref pending))
            {
                APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartRecoveryPendingOffset, 1);
                error = pending.Error; return 0;
            }
        }
#endif
        var g = p.Geometry;
        var memory = global::Amiga.Exec.AllocMem(AmigaScsiUnmap.ControlBytes,
            (global::Amiga.Exec.MemoryFlags)g.BufferMemoryType | global::Amiga.Exec.MemoryFlags.Public);
        if (memory != 0 && !NativeAmigaFileSystem.BufferFitsMask(memory, AmigaScsiUnmap.ControlBytes, g.Mask))
        { global::Amiga.Exec.FreeMem(memory, AmigaScsiUnmap.ControlBytes); memory = 0; }
        if (memory == 0)
        {
            memory = global::Amiga.Exec.AllocMem(AmigaScsiUnmap.ControlBytes,
                global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Chip);
            if (memory != 0 && !NativeAmigaFileSystem.BufferFitsMask(memory, AmigaScsiUnmap.ControlBytes, g.Mask))
            { global::Amiga.Exec.FreeMem(memory, AmigaScsiUnmap.ControlBytes); memory = 0; }
        }
        if (memory == 0) { error = DOS.Error.NoFreeStore; return 0; }
        bool success;
        AmigaSmartFileSystemWriteCore.TrimResult result;
#if !COPPERSTART_SMART_ONLY
#if !COPPERSTART_FFS_ONLY
        if (ffs)
#endif
        {
            NativeAmigaFileSystem.InitializeWriteWork(state, out var ffsWork);
            success = AmigaFfsWriteCore.TrimWindow(ref p, ref ffsWork, APTR.FromPointer(memory),
                APTR.ReadUInt32(record, NativeSfsTrimPacketFieldCodec.FlagsOffset), out result, startBlock, blockCount, validation);
            error = ffsWork.Error;
        }
#if !COPPERSTART_FFS_ONLY
        else
#endif
#endif
#if !COPPERSTART_FFS_ONLY
        {
            NativeAmigaFileSystem.InitializeSmartWriteWork(state, out var work);
#if COPPERSTART_NATIVE_SFS2
            if (NativeSfs2Bridge.IsSfs2(state))
                success = NativeSfs2Bridge.Trim(state, ref p, ref work, APTR.FromPointer(memory),
                    APTR.ReadUInt32(record, NativeSfsTrimPacketFieldCodec.FlagsOffset), out result, startBlock, blockCount, validation);
            else
#endif
            success = AmigaSmartFileSystemWriteCore.TrimWindow(ref p, ref work, APTR.FromPointer(memory),
                APTR.ReadUInt32(record, NativeSfsTrimPacketFieldCodec.FlagsOffset), out result, startBlock, blockCount, validation);

            APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartJournalCursorOffset, work.JournalCursor);
            APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartAllocationCursorOffset, work.AllocationCursor);
            error = work.Error;
            if (!success && error == DOS.Error.DiskNotValidated)
                APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartRecoveryPendingOffset, 1);
        }
#endif
        global::Amiga.Exec.FreeMem(memory, AmigaScsiUnmap.ControlBytes);
        APTR.WriteUInt32(record, NativeSfsTrimPacketFieldCodec.BlocksOffset, result.Blocks); APTR.WriteUInt32(record, NativeSfsTrimPacketFieldCodec.RangesOffset, result.Ranges);
        APTR.WriteUInt32(record, NativeSfsTrimPacketFieldCodec.MaximumBlocksOffset, result.Limits.MaximumBlocks);
        APTR.WriteUInt32(record, NativeSfsTrimPacketFieldCodec.GranularityOffset, result.Limits.Granularity); APTR.WriteUInt32(record, NativeSfsTrimPacketFieldCodec.AlignmentOffset, result.Limits.Alignment);
        return success ? -1 : 0;
    }

#if !COPPERSTART_NATIVE_SFS_WRITES || COPPERFS_FFS_HANDLER
    // Classic routes only FFS/OFS maintenance here. A direct entry keeps the
    // unrelated SFS journal writer out of that ROM's closed-world call graph.
    // Retain Execute for profiles/callers that explicitly request smart trim.
    public static int ExecuteFfs(APTR state, APTR packet, out DOS.Error error) =>
        ExecuteFfsRecord(state, APTR.FromPointer(APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument1Offset)),
            out error, 0, uint.MaxValue);

    internal static int ExecuteFfsRecord(APTR state, APTR record, out DOS.Error error,
        uint startBlock, uint blockCount, APTR validation = default)
    {
        error = DOS.Error.BadNumber;
        var p = new NativeFileSystemPlatform(state);
        if (record.IsNull || !p.IsMapped(record, AmigaSmartFileSystemTrimPacket.Size) ||
            APTR.ReadUInt32(record, NativeSfsTrimPacketFieldCodec.SizeOffset) < AmigaSmartFileSystemTrimPacket.Size ||
            APTR.ReadUInt32(record, NativeSfsTrimPacketFieldCodec.VersionOffset) != AmigaSmartFileSystemTrimPacket.Version ||
            APTR.ReadUInt32(record, NativeSfsTrimPacketFieldCodec.FlagsOffset) > AmigaSmartFileSystemTrimPacket.QueryOnly) return 0;
        p.Clear(APTR.FromPointer(record.Raw + 12), 20);
        var g = p.Geometry;
        var memory = global::Amiga.Exec.AllocMem(AmigaScsiUnmap.ControlBytes,
            (global::Amiga.Exec.MemoryFlags)g.BufferMemoryType | global::Amiga.Exec.MemoryFlags.Public);
        if (memory != 0 && !NativeAmigaFileSystem.BufferFitsMask(memory, AmigaScsiUnmap.ControlBytes, g.Mask))
        { global::Amiga.Exec.FreeMem(memory, AmigaScsiUnmap.ControlBytes); memory = 0; }
        if (memory == 0)
        {
            memory = global::Amiga.Exec.AllocMem(AmigaScsiUnmap.ControlBytes,
                global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Chip);
            if (memory != 0 && !NativeAmigaFileSystem.BufferFitsMask(memory, AmigaScsiUnmap.ControlBytes, g.Mask))
            { global::Amiga.Exec.FreeMem(memory, AmigaScsiUnmap.ControlBytes); memory = 0; }
        }
        if (memory == 0) { error = DOS.Error.NoFreeStore; return 0; }
        NativeAmigaFileSystem.InitializeWriteWork(state, out var work);
        var success = AmigaFfsWriteCore.TrimWindow(ref p, ref work, APTR.FromPointer(memory),
            APTR.ReadUInt32(record, NativeSfsTrimPacketFieldCodec.FlagsOffset), out var result, startBlock, blockCount, validation);
        error = work.Error;
        global::Amiga.Exec.FreeMem(memory, AmigaScsiUnmap.ControlBytes);
        APTR.WriteUInt32(record, NativeSfsTrimPacketFieldCodec.BlocksOffset, result.Blocks); APTR.WriteUInt32(record, NativeSfsTrimPacketFieldCodec.RangesOffset, result.Ranges);
        APTR.WriteUInt32(record, NativeSfsTrimPacketFieldCodec.MaximumBlocksOffset, result.Limits.MaximumBlocks);
        APTR.WriteUInt32(record, NativeSfsTrimPacketFieldCodec.GranularityOffset, result.Limits.Granularity); APTR.WriteUInt32(record, NativeSfsTrimPacketFieldCodec.AlignmentOffset, result.Limits.Alignment);
        return success ? -1 : 0;
    }
#endif
}
