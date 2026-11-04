using Amiga;

namespace CopperStart.Dos;

// Keep fixed native fields named without adding scalar wrapper register moves.
// Public structure offsets follow the SDK ABI; state/cache records are private.
internal static class NativeFileSystemStateCodec
{
    internal const int RecordLocksOffset = NativeRecordLockCore.LocksOffset;
    internal const int RecordWaitersOffset = NativeRecordLockCore.WaitersOffset;
    internal const int BlockBytesOffset = NativeAmigaFileSystem.GeometryOffset + 4;
    internal const int BlockCountOffset = NativeAmigaFileSystem.GeometryOffset;
    internal const int BufferMemoryTypeOffset = NativeAmigaFileSystem.GeometryOffset + 32;
    internal const int BuffersOffset = NativeAmigaFileSystem.BuffersOffset;
    internal const int CacheOffset = NativeAmigaFileSystem.CacheOffset;
    internal const int CacheBlocksOffset = NativeAmigaFileSystem.CacheBlocksOffset;
    internal const int CacheBytesOffset = NativeAmigaFileSystem.CacheBytesOffset;
    internal const int CacheTrackOffset = NativeAmigaFileSystem.CacheTrackOffset;
    internal const int DosTypeOffset = NativeAmigaFileSystem.DosTypeOffset;
    internal const int ExtraCacheCountOffset = NativeAmigaFileSystem.ExtraCacheCountOffset;
    internal const int ExtraCacheHeadOffset = NativeAmigaFileSystem.ExtraCacheHeadOffset;
    internal const int ExtraCacheTailOffset = NativeAmigaFileSystem.ExtraCacheTailOffset;
    internal const int InhibitedOffset = NativeAmigaFileSystem.InhibitedOffset;
    internal const int IoBufferOffset = NativeAmigaFileSystem.IoBufferOffset;
    internal const int IoPortOffset = NativeAmigaFileSystem.IoPortOffset;
    internal const int JournalScratchOffset = NativeAmigaFileSystem.JournalScratchOffset;
    internal const int MagicOffset = 0;
    internal const int MaskOffset = NativeAmigaFileSystem.GeometryOffset + 36;
    internal const int MaxTransferOffset = NativeAmigaFileSystem.GeometryOffset + 28;
    internal const int ObjectsOffset = NativeAmigaFileSystem.ObjectsOffset;
    internal const int PortOffset = NativeAmigaFileSystem.PortOffset;
    internal const int RamBlocksOffset = NativeAmigaFileSystem.RamBlocksOffset;
    internal const int RequestOffset = NativeAmigaFileSystem.RequestOffset;
    internal const int ReservedBlocksOffset = NativeAmigaFileSystem.GeometryOffset + 8;
    internal const int SectorBytesOffset = NativeAmigaFileSystem.GeometryOffset + 12;
    internal const int SmartAllocationCursorOffset = NativeAmigaFileSystem.SmartAllocationCursorOffset;
    internal const int SmartCreationDateOffset = NativeAmigaFileSystem.SmartCreationDateOffset;
    internal const int SmartDeviceDirtyOffset = NativeAmigaFileSystem.SmartDeviceDirtyOffset;
    internal const int SmartEnumerationCacheOffset = NativeAmigaFileSystem.SmartEnumerationCacheOffset;
    internal const int SmartJournalCursorOffset = NativeAmigaFileSystem.SmartJournalCursorOffset;
    internal const int SmartRecoveryPendingOffset = NativeAmigaFileSystem.SmartRecoveryPendingOffset;
    internal const int SmartRedoOverlayOffset = NativeAmigaFileSystem.SmartRedoOverlayOffset;
    internal const int SmartTimerOffset = NativeAmigaFileSystem.SmartTimerOffset;
    internal const int SmartTimerActiveOffset = NativeAmigaFileSystem.SmartTimerActiveOffset;
    internal const int SmartTransactionBufferOffset = NativeAmigaFileSystem.SmartTransactionBufferOffset;
    internal const int SmartWriteBufferOffset = NativeAmigaFileSystem.SmartWriteBufferOffset;
    internal const int SmartRangeBufferOffset = NativeAmigaFileSystem.SmartRangeBufferOffset;
    internal const int SmartWriteCombinerOffset = NativeAmigaFileSystem.SmartWriteCombinerOffset;
    internal const int StartByteHighOffset = NativeAmigaFileSystem.GeometryOffset + 16;
    internal const int StartByteLowOffset = NativeAmigaFileSystem.GeometryOffset + 20;
    internal const int TrackBlocksOffset = NativeAmigaFileSystem.GeometryOffset + 24;
    internal const int VolumeOffset = NativeAmigaFileSystem.VolumeOffset;
    internal const int CacheHashOffset = NativeAmigaFileSystem.ExtraCacheHashOffset;
}

internal static class NativeDosPacketFieldCodec
{
    internal const int LinkOffset = 0;
    internal const int PortOffset = 4;
    internal const int TypeOffset = 8;
    internal const int Result1Offset = 12;
    internal const int Result2Offset = 16;
    internal const int Argument1Offset = 20;
    internal const int Argument2Offset = 24;
    internal const int Argument3Offset = 28;
    internal const int Argument4Offset = 32;
    internal const int Argument5Offset = 36;
}

internal static class NativeRecordLockFieldCodec
{
    internal const int PacketOffset = NativeRecordLockCore.PacketOffset;
    internal const int TimerOffset = NativeRecordLockCore.TimerOffset;
    internal const int ReplyPortOffset = NativeRecordLockCore.ReplyPortOffset;
    internal const int MessageOffset = NativeRecordLockCore.MessageOffset;
}

internal static class NativeDosFileHandleFieldCodec
{
    internal const int PortOffset = 8;
    internal const int Argument1Offset = 36;
}

internal static class NativeTimerRequestFieldCodec
{
    internal const int SecondsOffset = 32;
    internal const int MicrosecondsOffset = 36;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static void Prepare<T>(ref T memory, APTR request, TimerCommand command,
        uint seconds, uint microseconds) where T : struct, IAmigaGuestMemory
    {
        // Exec owns the message/device prefix. Only timer.device input fields
        // change; every other request byte remains in place.
        memory.WriteUInt16(request, NativeExecFieldCodec.IoCommandOffset, (ushort)command);
        memory.WriteUInt8(request, NativeExecFieldCodec.IoFlagsOffset, (byte)IOFlags.None);
        memory.WriteUInt8(request, NativeExecFieldCodec.IoErrorOffset, 0);
        memory.WriteUInt32(request, SecondsOffset, seconds);
        memory.WriteUInt32(request, MicrosecondsOffset, microseconds);
    }
}

internal static class NativeFileSystemCacheEntryCodec
{
    internal const int NextOffset = 0;
    internal const int KeyOffset = 4;
    internal const int PreviousOffset = 8;
    internal const int HashNextOffset = 12;
    internal const int VerificationOffset = 16;
}

internal static class NativeSfsExtensionHeaderCodec
{
    internal const int MagicOffset = 0;
    internal const int EntryOffset = 4;
}

internal static class NativeSfsEnumerationEntryCodec
{
    internal const int DirectoryOffset = 0;
    internal const int WantedOffset = 4;
    internal const int ErrorOffset = 8;
    internal const int GenerationOffset = 12;
}

internal static class NativeSfsTrimPacketFieldCodec
{
    internal const int SizeOffset = 0;
    internal const int VersionOffset = 4;
    internal const int FlagsOffset = 8;
    internal const int BlocksOffset = 12;
    internal const int RangesOffset = 16;
    internal const int MaximumBlocksOffset = 20;
    internal const int GranularityOffset = 24;
    internal const int AlignmentOffset = 28;
}

internal static class NativeSfsObjectFieldCodec
{
    internal const int SizeOffset = 16;
    internal const int ParentOffset = 12;
}
