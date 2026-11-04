#if COPPERSTART_SFS_TRST_BUFFERED
using Amiga;
using CopperSFS;

namespace CopperStart.Dos;

public static partial class NativeSfsExtension
{
    internal static bool TrstGroupPending(APTR state)
    {
        var p = new NativeFileSystemPlatform(state);
        return AmigaSmartFileSystemWriteCore.TrstGroupPending(ref p,
            APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRangeBufferOffset)));
    }

    private static APTR AllocateNativeTrstGroup(APTR state, ref NativeFileSystemPlatform p, uint budget)
    {
        var bytes = AmigaSmartFileSystemWriteCore.TrstGroupBytes(budget, p.BlockBytes);
        if (bytes == 0) return APTR.Null;
        var allocation = global::Amiga.Exec.AllocMem(bytes, global::Amiga.Exec.MemoryFlags.Public);
        if (allocation == 0) return APTR.Null;
        var group = APTR.FromPointer(allocation);
        if (!AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref p, group, bytes))
        { global::Amiga.Exec.FreeMem(allocation, bytes); return APTR.Null; }
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartRangeBufferOffset, allocation);
        return group;
    }

    private static APTR NativeTrstGroup(APTR state, ref NativeFileSystemPlatform p)
    {
        var group = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRangeBufferOffset));
        if (group.IsNotNull) return group;
        var initial = AmigaSmartFileSystemWriteCore.TrstGroupHeaderBytes + 2 * (88 + 8 * (p.BlockBytes + AmigaSmartFileSystemRedoArena.SlotHeaderBytes));
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
        return success;
    }

    private static bool WriteBufferedNativeTrst(APTR state, ref NativeFileSystemPlatform p,
        ref AmigaSmartFileSystemWriteCore.Work work, ref AmigaSmartFileSystemWriteCore.Request request, out uint actual)
    {
        // Large packets retain the existing complete-request capacity. Without
        // a timer, keep strict durability rather than indefinitely pending data.
        if (request.Length > 65536 || APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartTimerOffset) == 0)
            return ExecuteStrictNativeTrstGroup(state, ref p, ref work, ref request,
                AmigaSmartFileSystemWriteCore.TrstOperation.Write, out actual);
        actual = 0;
        var group = NativeTrstGroup(state, ref p);
        if (group.IsNull) { work.Error = DOS.Error.NoFreeStore; return false; }
        if (AmigaSmartFileSystemWriteCore.WriteTrstGroup(ref p, ref work, ref request, group, out actual)) return true;
        if (work.Error != DOS.Error.NoFreeStore && work.Error != DOS.Error.DiskFull) return false;
        // The failed request left the accepted group unchanged. Make that
        // checkpoint durable before retrying the complete current request.
        return ExecuteStrictNativeTrstGroup(state, ref p, ref work, ref request,
            AmigaSmartFileSystemWriteCore.TrstOperation.Write, out actual);
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
            AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref p, group, bytes);
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
