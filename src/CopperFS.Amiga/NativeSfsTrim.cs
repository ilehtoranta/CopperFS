using Amiga;

namespace CopperStart.Dos;

/// <summary>Explicit manual packet. The handler owns the mount throughout this
/// synchronous call: no packet dispatch, timer commit or allocation can interleave
/// with a scanned range and its completed discard. No per-mount trim allocation.</summary>
internal static class NativeSfsTrim
{
    public static int Execute(APTR state, APTR packet, out DOS.Error error, bool ffs = false)
    {
        error = DOS.Error.BadNumber;
        var p = new NativeFileSystemPlatform(state);
        var record = APTR.FromPointer(APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument1Offset));
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
        bool success;
        AmigaSmartFileSystemWriteCore.TrimResult result;
        if (ffs)
        {
            NativeAmigaFileSystem.InitializeWriteWork(state, out var ffsWork);
            success = AmigaFfsWriteCore.Trim(ref p, ref ffsWork, APTR.FromPointer(memory),
                APTR.ReadUInt32(record, NativeSfsTrimPacketFieldCodec.FlagsOffset), out result);
            error = ffsWork.Error;
        }
        else
        {
            NativeAmigaFileSystem.InitializeSmartWriteWork(state, out var work);
#if COPPERSTART_NATIVE_SFS2
            if (NativeSfs2Bridge.IsSfs2(state))
                success = NativeSfs2Bridge.Trim(state, ref p, ref work, APTR.FromPointer(memory),
                    APTR.ReadUInt32(record, NativeSfsTrimPacketFieldCodec.FlagsOffset), out result);
            else
#endif
            success = AmigaSmartFileSystemWriteCore.Trim(ref p, ref work, APTR.FromPointer(memory),
                APTR.ReadUInt32(record, NativeSfsTrimPacketFieldCodec.FlagsOffset), out result);

            APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartJournalCursorOffset, work.JournalCursor);
            APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartAllocationCursorOffset, work.AllocationCursor);
            error = work.Error;
            if (!success && error == DOS.Error.DiskNotValidated)
                APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartRecoveryPendingOffset, 1);
        }
        global::Amiga.Exec.FreeMem(memory, AmigaScsiUnmap.ControlBytes);
        APTR.WriteUInt32(record, NativeSfsTrimPacketFieldCodec.BlocksOffset, result.Blocks); APTR.WriteUInt32(record, NativeSfsTrimPacketFieldCodec.RangesOffset, result.Ranges);
        APTR.WriteUInt32(record, NativeSfsTrimPacketFieldCodec.MaximumBlocksOffset, result.Limits.MaximumBlocks);
        APTR.WriteUInt32(record, NativeSfsTrimPacketFieldCodec.GranularityOffset, result.Limits.Granularity); APTR.WriteUInt32(record, NativeSfsTrimPacketFieldCodec.AlignmentOffset, result.Limits.Alignment);
        return success ? -1 : 0;
    }
}
