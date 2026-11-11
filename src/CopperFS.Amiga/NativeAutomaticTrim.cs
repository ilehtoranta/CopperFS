#if COPPERFS_AUTOTRIM
using Amiga;

namespace CopperStart.Dos;

/// <summary>Opt-in deferred maintenance. No free-block addresses survive dispatch.
/// A failed pass disables automatic discard until this mount is recreated.</summary>
internal static class NativeAutomaticTrim
{
    internal static void Reset(APTR state)
    {
        APTR.WriteUInt32(state, NativeAmigaFileSystem.AutoTrimValidationOffset, 0);
        APTR.WriteUInt32(state, NativeAmigaFileSystem.AutoTrimPendingOffset, 0);
        APTR.WriteUInt32(state, NativeAmigaFileSystem.AutoTrimCursorOffset, 0);
    }

    internal static bool Pending(APTR state) =>
        APTR.ReadUInt32(state, NativeAmigaFileSystem.AutoTrimPendingOffset) == 1;

    internal static void CompletedPacket(APTR state, APTR packet)
    {
        if (APTR.ReadUInt32(state, NativeAmigaFileSystem.AutoTrimPendingOffset) == 2 ||
            APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.TypeOffset) != (uint)DosPacketAction.DeleteObject ||
            APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Result1Offset) == 0) return;
        APTR.WriteUInt32(state, NativeAmigaFileSystem.AutoTrimPendingOffset, 1);
        APTR.WriteUInt32(state, NativeAmigaFileSystem.AutoTrimCursorOffset, 0);
    }

    internal static void Service(APTR state)
    {
        if (!Pending(state)) return;
        if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.InhibitedOffset) != 0) return;
        var record = APTR.FromPointer(state.Raw + (uint)NativeAmigaFileSystem.AutoTrimRecordOffset);
        APTR.WriteUInt32(record, AmigaSmartFileSystemTrimPacket.SizeOffset, AmigaSmartFileSystemTrimPacket.Size);
        APTR.WriteUInt32(record, AmigaSmartFileSystemTrimPacket.VersionOffset, AmigaSmartFileSystemTrimPacket.Version);
        APTR.WriteUInt32(record, AmigaSmartFileSystemTrimPacket.FlagsOffset, 0);
        var cursor = APTR.ReadUInt32(state, NativeAmigaFileSystem.AutoTrimCursorOffset);
        const uint window = 4096;
#if COPPERFS_FFS_HANDLER
        var ok = NativeSfsTrim.ExecuteFfsRecord(state, record, out _, cursor, window, APTR.FromPointer(state.Raw + (uint)NativeAmigaFileSystem.AutoTrimValidationOffset));
#else
        var type = APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset);
        var ok = NativeSfsTrim.ExecuteRecord(state, record, out _, cursor, window,
            type is 0x444F5300 or 0x444F5301, APTR.FromPointer(state.Raw + (uint)NativeAmigaFileSystem.AutoTrimValidationOffset));
#endif
        var granularity = APTR.ReadUInt32(record, AmigaSmartFileSystemTrimPacket.GranularityOffset);
        if (ok == 0 || granularity == 0 || granularity > window)
        {
            APTR.WriteUInt32(state, NativeAmigaFileSystem.AutoTrimPendingOffset, 2);
            return;
        }
        // Overlap windows so alignment never strands a discardable boundary run.
        var step = window - granularity + 1;
        var blocks = APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockCountOffset);
        if (cursor >= blocks || blocks - cursor <= window)
            APTR.WriteUInt32(state, NativeAmigaFileSystem.AutoTrimPendingOffset, 0);
        else APTR.WriteUInt32(state, NativeAmigaFileSystem.AutoTrimCursorOffset, cursor + step);
    }
}
#endif
