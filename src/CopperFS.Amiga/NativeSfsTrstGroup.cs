#if COPPERSTART_SFS_TRST_BUFFERED
using Amiga;
using CopperSFS;

namespace CopperStart.Dos;

public static partial class NativeSfsExtension
{
#if COPPERFS_SFS_GROUPED
    internal static bool IsHarmlessRejection(APTR state, DOS.Error error)
    {
        // These errors reject a packet before publication, or after the trial
        // has restored its accepted checkpoint. Never infer this from a device,
        // allocation, validation or commit failure.
        if (error is not (DOS.Error.ObjectExists or DOS.Error.ObjectNotFound or
            DOS.Error.ObjectWrongType or DOS.Error.DeleteProtected or DOS.Error.WriteProtected or
            DOS.Error.DirectoryNotEmpty or DOS.Error.ObjectInUse or DOS.Error.InvalidLock or DOS.Error.SeekError)) return false;
        if (NativeSfs2Bridge.IsSfs2(state) ||
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRecoveryPendingOffset) != 0) return false;
        var group = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRangeBufferOffset));
        var overlay = APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRedoOverlayOffset);
        if (group.IsNull) return overlay == 0;
        var status = APTR.ReadUInt32(group, 4);
        return status <= 1 && overlay == (status == 1 ? APTR.ReadUInt32(group, 16) : 0);
    }

    internal static bool CanDeferClose(APTR state)
    {
        if (NativeSfs2Bridge.IsSfs2(state) ||
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartTimerOffset) == 0 ||
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRecoveryPendingOffset) != 0) return false;
        var group = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRangeBufferOffset));
        return group.IsNull || (APTR.ReadUInt32(group, 4) & 2) == 0;
    }

    private static bool ExecuteBufferedNativeTrst(APTR state, ref NativeFileSystemPlatform p,
        ref AmigaSmartFileSystemWriteCore.Work work, ref AmigaSmartFileSystemWriteCore.Request request,
        AmigaSmartFileSystemWriteCore.TrstOperation kind, out uint actual)
    {
        actual = 0;
        if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartTimerOffset) == 0)
            return ExecuteStrictNativeTrstGroup(state, ref p, ref work, ref request, kind, out actual);
        var group = NativeTrstGroup(state, ref p);
        if (group.IsNull) { work.Error = DOS.Error.NoFreeStore; return false; }
        if (AmigaSmartFileSystemWriteCore.ExecuteTrstGroup(ref p, ref work, ref request, kind, group, out actual)) return true;
        if (work.Error != DOS.Error.NoFreeStore && work.Error != DOS.Error.DiskFull) return false;
        if (TrstGroupPending(state))
        {
            // Capacity belongs to the accepted group, not necessarily to this
            // packet. Reuse both emptied arenas before requiring a strict
            // complete-request transaction. The failed trial changed neither
            // the request nor the accepted checkpoint.
            if (!FlushNativeTrstGroup(state, ref p, ref work)) return false;
            if (AmigaSmartFileSystemWriteCore.ExecuteTrstGroup(ref p, ref work, ref request, kind, group, out actual)) return true;
            if (work.Error != DOS.Error.NoFreeStore && work.Error != DOS.Error.DiskFull) return false;
        }
        return ExecuteStrictNativeTrstGroup(state, ref p, ref work, ref request, kind, out actual);
    }
#endif
    internal static bool TrstGroupPending(APTR state)
    {
        var p = new NativeFileSystemPlatform(state);
        return AmigaSmartFileSystemWriteCore.TrstGroupPending(ref p,
            APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRangeBufferOffset)));
    }

    private static bool InitializeNativeTrstGroup(ref NativeFileSystemPlatform p, APTR group, uint bytes)
    {
#if COPPERFS_SFS_POOL
        return AmigaSmartFileSystemWriteCore.InitializeSharedTrstGroup(ref p, group, bytes);
#else
        return AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref p, group, bytes);
#endif
    }

    private static APTR AllocateNativeTrstGroup(APTR state, ref NativeFileSystemPlatform p, uint budget)
    {
        var bytes = AmigaSmartFileSystemWriteCore.TrstGroupBytes(budget, p.BlockBytes);
        if (bytes == 0) return APTR.Null;
        var allocation = global::Amiga.Exec.AllocMem(bytes, global::Amiga.Exec.MemoryFlags.Public);
        if (allocation == 0) return APTR.Null;
        var group = APTR.FromPointer(allocation);
        if (!InitializeNativeTrstGroup(ref p, group, bytes))
        { global::Amiga.Exec.FreeMem(allocation, bytes); return APTR.Null; }
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartRangeBufferOffset, allocation);
        return group;
    }

    internal static APTR NativeTrstGroup(APTR state, ref NativeFileSystemPlatform p)
    {
        var group = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRangeBufferOffset));
        if (group.IsNotNull) return group;
        var initial = AmigaSmartFileSystemWriteCore.TrstGroupHeaderBytes + 2 * (88 + 8 * (p.BlockBytes + AmigaSmartFileSystemRedoArena.SlotHeaderBytes));
#if COPPERFS_SFS_GROUPED
        // The smaller enumeration allocation pays for this increase at every
        // supported block size. If that optional allocation failed, retain the
        // original small transaction allocation as the low-memory fallback.
        if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartEnumerationCacheOffset) != 0)
        {
            var expanded = initial + 32 * (NativeAmigaFileSystem.SmartEnumerationOriginalEntryBytes - NativeAmigaFileSystem.SmartEnumerationEntryBytes);
#if COPPERFS_SFS_POOL
            // Base the transfer on bytes the previous allocation actually
            // retained. Arena rounding varies with filesystem block size.
            const uint previousEntry = 16 + 224;
            var previousBudget = initial + 32 * (NativeAmigaFileSystem.SmartEnumerationOriginalEntryBytes - previousEntry);
            if (previousBudget > NativeAmigaFileSystem.SmartRangeBufferBytes) previousBudget = NativeAmigaFileSystem.SmartRangeBufferBytes;
            expanded = AmigaSmartFileSystemWriteCore.TrstGroupBytes(previousBudget, p.BlockBytes) +
                32 * (previousEntry - NativeAmigaFileSystem.SmartEnumerationEntryBytes);
#endif
            if (expanded > NativeAmigaFileSystem.SmartRangeBufferBytes) expanded = NativeAmigaFileSystem.SmartRangeBufferBytes;
            group = AllocateNativeTrstGroup(state, ref p, expanded);
            if (group.IsNotNull) return group;
        }
#endif
        if (initial > NativeAmigaFileSystem.SmartRangeBufferBytes) initial = NativeAmigaFileSystem.SmartRangeBufferBytes;
        return AllocateNativeTrstGroup(state, ref p, initial);
    }

    private static bool FlushNativeTrstGroup(APTR state, ref NativeFileSystemPlatform p,
        ref AmigaSmartFileSystemWriteCore.Work work)
    {
        var group = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRangeBufferOffset));
        if (group.IsNull) return true;
        var success = AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref p, ref work, group);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartJournalCursorOffset, work.JournalCursor);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartAllocationCursorOffset, work.AllocationCursor);
#if COPPERFS_AUTOTRIM
        if (success) NativeAutomaticTrim.RememberRoots(state, work.BitmapBase, work.RootContainer, work.AdminContainer, work.NodeRoot, work.ExtentRoot);
#endif
        return success;
    }

    private static bool WriteBufferedNativeTrst(APTR state, ref NativeFileSystemPlatform p,
        ref AmigaSmartFileSystemWriteCore.Work work, ref AmigaSmartFileSystemWriteCore.Request request, out uint actual)
    {
#if COPPERFS_SFS_IN_PLACE
        var groupState = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRangeBufferOffset));
        if ((groupState.IsNull || APTR.ReadUInt32(groupState, 4) == 0) &&
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRecoveryPendingOffset) == 0 &&
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartTimerOffset) != 0)
        {
            var success = AmigaSmartFileSystemWriteCore.TryInPlaceOverwrite(ref p, ref work, ref request, groupState, out var handled);
            if (handled) { actual = success ? request.Length : 0; return success; }
        }
#endif
        // Large packets retain the existing complete-request capacity. Without
        // a timer, keep strict durability rather than indefinitely pending data.
        if (request.Length > 65536 || APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartTimerOffset) == 0)
            return ExecuteStrictNativeTrstGroup(state, ref p, ref work, ref request,
                AmigaSmartFileSystemWriteCore.TrstOperation.Write, out actual);
#if COPPERFS_SFS_GROUPED
        return ExecuteBufferedNativeTrst(state, ref p, ref work, ref request,
            AmigaSmartFileSystemWriteCore.TrstOperation.Write, out actual);
#else
        actual = 0;
        var group = NativeTrstGroup(state, ref p);
        if (group.IsNull) { work.Error = DOS.Error.NoFreeStore; return false; }
        if (AmigaSmartFileSystemWriteCore.WriteTrstGroup(ref p, ref work, ref request, group, out actual)) return true;
        if (work.Error != DOS.Error.NoFreeStore && work.Error != DOS.Error.DiskFull) return false;
        // The failed request left the accepted group unchanged. Make that
        // checkpoint durable before retrying the complete current request.
        return ExecuteStrictNativeTrstGroup(state, ref p, ref work, ref request,
            AmigaSmartFileSystemWriteCore.TrstOperation.Write, out actual);
#endif
    }

    private static bool ExecuteStrictNativeTrstGroup(APTR state, ref NativeFileSystemPlatform p,
        ref AmigaSmartFileSystemWriteCore.Work work, ref AmigaSmartFileSystemWriteCore.Request request,
        AmigaSmartFileSystemWriteCore.TrstOperation kind, out uint actual)
    {
        actual = 0;
        if (!FlushNativeTrstGroup(state, ref p, ref work)) return false;
        var group = NativeTrstGroup(state, ref p);
        if (group.IsNull) { work.Error = DOS.Error.NoFreeStore; return false; }
        var original = request;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var bytes = APTR.ReadUInt32(group, 8);
            // No accepted group remains. Temporarily use the entire allocation
            // for one strict operation, including payloads larger than one half.
            // ExecuteTrst can reject before initializing the arena. Give its
            // capacity flag the arena meaning even on that early-error path.
            AmigaSmartFileSystemRedoArena.Initialize(ref p, group, bytes, p.BlockBytes);
            var success = AmigaSmartFileSystemWriteCore.ExecuteTrst(ref p, ref work, ref request, kind, group, bytes, out actual);
            var full = APTR.ReadUInt32(group, 12) != 0;
            InitializeNativeTrstGroup(ref p, group, bytes);
            if (success || !full) return success;
            var maximum = AmigaSmartFileSystemWriteCore.TrstGroupBytes(NativeAmigaFileSystem.SmartRangeBufferBytes, p.BlockBytes);
            if (bytes >= maximum || attempt != 0) return false;
            request = original;
            APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartRangeBufferOffset, 0);
            global::Amiga.Exec.FreeMem(group.Raw, bytes);
            group = AllocateNativeTrstGroup(state, ref p, maximum);
            if (group.IsNull) { actual = 0; work.Error = DOS.Error.NoFreeStore; return false; }
        }
        return false;
    }
}
#endif
