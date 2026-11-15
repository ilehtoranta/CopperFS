using Amiga;
#if COPPERSTART_NATIVE_SFS2

namespace CopperStart.Dos;

/// <summary>Format-specific native adapter; classic SFS keeps its existing engine.</summary>
internal static class NativeSfs2Bridge
{
    internal const uint DosType = 0x53465302;
#if COPPERSTART_SFS2_TRST_WRITES
    internal const uint BufferBytes = 192 * 1024;
#else
    internal const uint BufferBytes = 65536;
#endif
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static bool IsSfs2(APTR state) => APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset) == DosType;

    internal static bool Root(ref NativeFileSystemPlatform p, APTR state)
    {
        var metadata = NativeAmigaFileSystem.Metadata(state);
        if (AmigaSmartFileSystemCore.Root(ref p, metadata))
        { APTR.WriteUInt32(state, NativeFileSystemStateCodec.DosTypeOffset, AmigaSmartFileSystemCore.DosType); return true; }
        if (!AmigaSmartFileSystemReadOnlyCore.Root(ref p, metadata, out var format) || format != AmigaSmartFileSystemFormat.Sfs2)
            return false;
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.DosTypeOffset, DosType);
        return true;
    }

    private static AmigaSmartFileSystem2WriteCore.Work Work(APTR state) => new()
    {
        Meta = NativeAmigaFileSystem.Metadata(state), Data = NativeAmigaFileSystem.Data(state),
        Bitmap = NativeAmigaFileSystem.Bitmap(state),
        Journal = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.JournalScratchOffset)),
        JournalCursor = APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartJournalCursorOffset)
    };

#if COPPERFS_SFS2_OWNED
    internal static void Suspend(APTR state)
    {
        var p = new NativeFileSystemPlatform(state);
        Sfs2MountContext.Suspend(ref p, p.Sfs2Context);
    }

    internal static void ReleaseContext(APTR state)
    {
        Suspend(state);
        var storage = APTR.ReadUInt32(state, NativeAmigaFileSystem.Sfs2ContextStorageOffset);
        if (storage != 0) global::Amiga.Exec.FreeMem(storage, APTR.ReadUInt32(state, NativeAmigaFileSystem.Sfs2ContextBytesOffset));
        APTR.WriteUInt32(state, NativeAmigaFileSystem.Sfs2ContextOffset, 0);
        APTR.WriteUInt32(state, NativeAmigaFileSystem.Sfs2ContextStorageOffset, 0);
        APTR.WriteUInt32(state, NativeAmigaFileSystem.Sfs2ContextBytesOffset, 0);
    }

    internal static bool MountContext(APTR state)
    {
        var p = new NativeFileSystemPlatform(state);
        if (p.Sfs2Context.IsNotNull && Sfs2MountContext.Ready(ref p, p.Sfs2Context)) return true;
        // Normal operations allocate nothing. Reserve both transaction arenas
        // and the selected metadata budget before accepting the first packet.
        if (Arena(state).IsNull) return false;
        var storage = APTR.FromPointer(APTR.ReadUInt32(state, NativeAmigaFileSystem.Sfs2ContextStorageOffset));
        var bytes = APTR.ReadUInt32(state, NativeAmigaFileSystem.Sfs2ContextBytesOffset);
        if (storage.IsNull)
        {
#if COPPERFS_SFS2_EXTRA256
            var extra = 256u * 1024;
#elif COPPERFS_SFS2_EXTRA64
            var extra = 64u * 1024;
#elif COPPERFS_SFS2_EXTRA16
            var extra = 16u * 1024;
#else
            var extra = 0u;
#endif
            // Reuse the obsolete undo-buffer budget, including the twelve
            // new state bytes. Extra memory is entirely opt-in and accounted.
            var baseline = AmigaSmartFileSystemTransactionBuffer.StorageBytes(p.BlockBytes,
                AmigaSmartFileSystemTransactionBuffer.MaximumCapacity) - 12;
            while (true)
            {
                bytes = baseline + extra;
                storage = global::Amiga.Exec.AllocMem(bytes, global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear);
                if (storage.IsNotNull || extra == 0) break;
                extra = extra == 256 * 1024 ? 64u * 1024 : extra == 64 * 1024 ? 16u * 1024 : 0u;
            }
            // No optional metadata capacity: retain the checked grouped path.
            if (storage.IsNull) return true;
            APTR.WriteUInt32(state, NativeAmigaFileSystem.Sfs2ContextStorageOffset, storage.Raw);
            APTR.WriteUInt32(state, NativeAmigaFileSystem.Sfs2ContextBytesOffset, bytes);
        }
        var work = Work(state);
        return Sfs2MountContext.Mount(ref p, ref work, storage, bytes);
    }
#endif

    internal static bool Recover(APTR state, ref AmigaSmartFileSystemWriteCore.Work classic)
    {
        var p = new NativeFileSystemPlatform(state); var work = Work(state);
#if COPPERSTART_SFS2_TRST_GROUPED
        var group = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRangeBufferOffset));
        var ok = group.IsNotNull ? AmigaSmartFileSystem2WriteCore.RecoverTrstGroup(ref p, ref work, group) :
            AmigaSmartFileSystem2WriteCore.Recover(ref p, ref work);
#else
        var ok = AmigaSmartFileSystem2WriteCore.Recover(ref p, ref work);
#endif
        classic.Error = work.Error; classic.JournalCursor = work.JournalCursor;
        return ok;
    }

#if COPPERFS_SFS2_OWNED && COPPERSTART_SFS2_TRST_GROUPED
    internal static bool CanDeferClose(APTR state)
    {
        // Accepted images belong to the mount, not the retiring file handle.
        // The existing non-resetting timer bounds their lifetime; flush,
        // inhibit and pressure still commit them. Do not defer without a timer
        // or hide a failed commit behind a successful close.
        if (!IsSfs2(state) || APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartTimerOffset) == 0 ||
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRecoveryPendingOffset) != 0) return false;
        var p = new NativeFileSystemPlatform(state);
        return Sfs2MountContext.Ready(ref p, p.Sfs2Context);
    }
#endif

    internal static bool Trim(APTR state, ref NativeFileSystemPlatform p,
        ref AmigaSmartFileSystemWriteCore.Work classic, APTR control, uint flags,
        out AmigaSmartFileSystemWriteCore.TrimResult result, uint startBlock = 0, uint blockCount = uint.MaxValue, APTR validation = default)
    {
        var work = Work(state);
#if COPPERSTART_SFS2_TRST_GROUPED
        if (flags != AmigaSmartFileSystemTrimPacket.QueryOnly && !Recover(state, ref classic))
        { result = default; return false; }
#endif
        var ok = AmigaSmartFileSystem2WriteCore.TrimWindow(ref p, ref work, control, flags, out var trimmed, startBlock, blockCount, validation);
        result = new AmigaSmartFileSystemWriteCore.TrimResult
        { Limits = trimmed.Limits, Blocks = trimmed.Blocks, Ranges = trimmed.Ranges };
        classic.Error = work.Error; classic.JournalCursor = work.JournalCursor;
        return ok;
    }
    internal static bool TrimFreed(APTR state, ref NativeFileSystemPlatform p,
        ref AmigaSmartFileSystemWriteCore.Work classic, APTR control, APTR batch, uint ranges)
    {
        var work = Work(state);
        var ok = AmigaSmartFileSystem2WriteCore.TrimFreedBatch(ref p, ref work, control, batch, ranges, out _);
        classic.Error = work.Error; classic.JournalCursor = work.JournalCursor;
        return ok;
    }

#if COPPERFS_AUTOTRIM
    internal static bool CollectAutomatic(APTR state, APTR batch, uint generation)
    {
        var p = new NativeFileSystemPlatform(state); var work = Work(state);
        var ok = AmigaSmartFileSystem2WriteCore.CollectAutomaticTrim(ref p, ref work, batch, state.Raw, generation);
        if (ok) NativeAutomaticTrim.RememberRoots(state, work.BitmapBase, work.RootContainer, work.AdminContainer, work.NodeRoot, work.ExtentRoot);
        return ok;
    }
#endif

    private static APTR Arena(APTR state)
    {
        var buffer = APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRangeBufferOffset);
        if (buffer == 0)
        {
            buffer = global::Amiga.Exec.AllocMem(BufferBytes, global::Amiga.Exec.MemoryFlags.Public);
#if COPPERSTART_SFS2_TRST_GROUPED
            if (buffer != 0)
            {
                var p = new NativeFileSystemPlatform(state);
                if (!AmigaSmartFileSystem2WriteCore.InitializeTrstGroup(ref p, APTR.FromPointer(buffer), BufferBytes))
                { global::Amiga.Exec.FreeMem(buffer, BufferBytes); buffer = 0; }
            }
#endif
            APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartRangeBufferOffset, buffer);
        }
        return APTR.FromPointer(buffer);
    }

    private static bool Mutate(APTR state, ref AmigaSmartFileSystem2WriteCore.Request request,
        AmigaSmartFileSystem2WriteCore.TrstOperation kind, out uint actual, out DOS.Error error, APTR originalCursor = default)
    {
        var p = new NativeFileSystemPlatform(state); var work = Work(state); actual = 0;
#if COPPERSTART_SFS2_TRST_WRITES
        var arena = Arena(state);
#if COPPERSTART_SFS2_TRST_GROUPED
        var ok = false;
        if (arena.IsNotNull)
        {
            var saved = request;
            ok = AmigaSmartFileSystem2WriteCore.ExecuteTrstGroup(ref p, ref work, ref request, kind, arena, out actual, originalCursor);
            if (!ok && work.Error == DOS.Error.NoFreeStore)
            {
                // A rejected packet has not changed the accepted view. Commit
                // it first, then use the full allocation for this strict packet.
                if (AmigaSmartFileSystem2WriteCore.FlushTrstGroup(ref p, ref work, arena))
                {
                    request = saved;
#if COPPERFS_SFS2_OWNED
                    var context = p.Sfs2Context;
                    Sfs2MountContext.Suspend(ref p, context); p.Sfs2Context = APTR.Null;
#endif
                    ok = AmigaSmartFileSystem2WriteCore.ExecuteTrst(ref p, ref work, ref request, kind, arena, BufferBytes, out actual);
                    AmigaSmartFileSystem2WriteCore.InitializeTrstGroup(ref p, arena, BufferBytes);
#if COPPERFS_SFS2_OWNED
                    p.Sfs2Context = context;
                    if (ok && context.IsNotNull) ok = Sfs2MountContext.Mount(ref p, ref work, context,
                        APTR.ReadUInt32(state, NativeAmigaFileSystem.Sfs2ContextBytesOffset));
#endif
                }
            }
            if (ok && APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartTimerOffset) == 0)
                ok = AmigaSmartFileSystem2WriteCore.FlushTrstGroup(ref p, ref work, arena);
        }
#else
        var ok = !arena.IsNull && AmigaSmartFileSystem2WriteCore.ExecuteTrst(ref p, ref work, ref request, kind,
            arena, BufferBytes, out actual);
#endif
        if (arena.IsNull) work.Error = DOS.Error.NoFreeStore;
#else
        bool ok;
        if (kind == AmigaSmartFileSystem2WriteCore.TrstOperation.Write)
        {
            var buffer = request.Length >= 4096 ? Arena(state) : APTR.Null;
            ok = AmigaSmartFileSystem2WriteCore.WriteBatched(ref p, ref work, ref request,
                buffer, buffer.IsNull ? 0 : BufferBytes, out actual);
        }
        else ok = kind switch
        {
            AmigaSmartFileSystem2WriteCore.TrstOperation.CreateFile => AmigaSmartFileSystem2WriteCore.Create(ref p, ref work, ref request, false),
            AmigaSmartFileSystem2WriteCore.TrstOperation.CreateDirectory => AmigaSmartFileSystem2WriteCore.Create(ref p, ref work, ref request, true),
            AmigaSmartFileSystem2WriteCore.TrstOperation.Truncate => AmigaSmartFileSystem2WriteCore.Truncate(ref p, ref work, request.Node),
            AmigaSmartFileSystem2WriteCore.TrstOperation.Remove => AmigaSmartFileSystem2WriteCore.Remove(ref p, ref work, request.Node),
            AmigaSmartFileSystem2WriteCore.TrstOperation.Rename => AmigaSmartFileSystem2WriteCore.Rename(ref p, ref work, ref request),
            AmigaSmartFileSystem2WriteCore.TrstOperation.Resize => AmigaSmartFileSystem2WriteCore.Resize(ref p, ref work, request.Node, request.Position),
            AmigaSmartFileSystem2WriteCore.TrstOperation.SetComment => AmigaSmartFileSystem2WriteCore.SetMetadata(ref p, ref work, ref request, DosPacketAction.SetComment),
            AmigaSmartFileSystem2WriteCore.TrstOperation.SetDate => AmigaSmartFileSystem2WriteCore.SetMetadata(ref p, ref work, ref request, DosPacketAction.SetDate),
            _ => AmigaSmartFileSystem2WriteCore.SetMetadata(ref p, ref work, ref request, DosPacketAction.SetProtection)
        };
#endif
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartJournalCursorOffset, work.JournalCursor);
#if COPPERFS_AUTOTRIM
        if (ok) NativeAutomaticTrim.RememberRoots(state, work.BitmapBase, work.RootContainer, work.AdminContainer, work.NodeRoot, work.ExtentRoot);
#endif
        if (!ok) actual = 0;
        error = work.Error; return ok;
    }

    internal static bool MutateClassicRequest(APTR state, ref AmigaSmartFileSystemWriteCore.Work work,
        ref AmigaSmartFileSystemWriteCore.Request request, uint operation, uint argument)
    {
        var r = new AmigaSmartFileSystem2WriteCore.Request { Node = request.Node, Directory = request.Directory,
            Position = operation == 8 ? request.Length : request.Position, Name = request.Name, Buffer = request.Buffer, Length = request.Length };
        var kind = operation switch
        {
            2 => argument != 0 ? AmigaSmartFileSystem2WriteCore.TrstOperation.CreateDirectory : AmigaSmartFileSystem2WriteCore.TrstOperation.CreateFile,
            3 => AmigaSmartFileSystem2WriteCore.TrstOperation.Truncate,
            4 => AmigaSmartFileSystem2WriteCore.TrstOperation.Remove,
            5 => AmigaSmartFileSystem2WriteCore.TrstOperation.Rename,
            8 => AmigaSmartFileSystem2WriteCore.TrstOperation.Resize,
            _ => argument == (uint)DosPacketAction.SetComment ? AmigaSmartFileSystem2WriteCore.TrstOperation.SetComment :
                argument == (uint)DosPacketAction.SetDate ? AmigaSmartFileSystem2WriteCore.TrstOperation.SetDate : AmigaSmartFileSystem2WriteCore.TrstOperation.SetProtection
        };
        var ok = Mutate(state, ref r, kind, out _, out work.Error);
        request.Node = r.Node; work.JournalCursor = APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartJournalCursorOffset);
        return ok;
    }

    internal static bool Write(APTR state, uint node, ulong position, APTR data, uint length, out uint actual, out DOS.Error error)
    {
        var r = new AmigaSmartFileSystem2WriteCore.Request { Node = node, Position = position, Buffer = data, Length = length };
        return Mutate(state, ref r, AmigaSmartFileSystem2WriteCore.TrstOperation.Write, out actual, out error);
    }

#if COPPERFS_SFS2_ORIGINAL_DATA_WRITES
    internal static bool WriteOriginalCached(APTR state, uint node, ulong position, APTR data, uint length,
        APTR cursor, out uint actual, out DOS.Error error)
    {
        var r = new AmigaSmartFileSystem2WriteCore.Request { Node = node, Position = position, Buffer = data, Length = length };
        var p = new NativeFileSystemPlatform(state); actual = 0;
        var group = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRangeBufferOffset));
        // Without the bounded timer, Mutate must supply the immediate barrier.
        if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartTimerOffset) != 0 && APTR.ReadUInt32(cursor, 0) == node)
        {
            if (!AmigaSmartFileSystem2WriteCore.TryContinueOriginalDataWrite(ref p, group, cursor, ref r, out var handled, out error)) return false;
            if (handled) { actual = length; return true; }
        }
        return Mutate(state, ref r, AmigaSmartFileSystem2WriteCore.TrstOperation.Write, out actual, out error, cursor);
    }
#endif

    internal static bool Resize(APTR state, uint node, ulong size, out DOS.Error error)
    {
        var r = new AmigaSmartFileSystem2WriteCore.Request { Node = node, Position = size };
        return Mutate(state, ref r, AmigaSmartFileSystem2WriteCore.TrstOperation.Resize, out _, out error);
    }

    internal static uint Resolve(APTR state, uint directory, APTR name, uint length, out DOS.Error error)
    {
        var p = new NativeFileSystemPlatform(state);
        return AmigaSmartFileSystemReadOnlyCore.Resolve(ref p, directory, name, length, NativeAmigaFileSystem.Metadata(state), out error);
    }

    internal static bool Describe(APTR state, uint node, out ulong size, out bool directory, out uint parent)
    {
        var p = new NativeFileSystemPlatform(state);
        return AmigaSmartFileSystemReadOnlyCore.DescribeInformation(ref p, node, NativeAmigaFileSystem.Metadata(state), out size, out directory, out parent);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int Read(APTR state, APTR file, uint node, ulong position, APTR buffer, uint length, out DOS.Error error)
    {
        var p = new NativeFileSystemPlatform(state);
        var metadata = NativeAmigaFileSystem.Metadata(state);
        var data = APTR.FromPointer(metadata.Raw + p.BlockBytes);
        return NativeFileSystemReadCore.ReadSfs2(ref p, file, node, position, buffer, length, metadata, data, out error);
    }

    internal static bool Examine(APTR state, uint node, APTR fib)
    {
        var p = new NativeFileSystemPlatform(state);
        return AmigaSmartFileSystemReadOnlyCore.Examine(ref p, node, fib, NativeAmigaFileSystem.Metadata(state));
    }
    internal static DOS.Error Next(APTR state, uint directory, APTR fib)
    {
        var p = new NativeFileSystemPlatform(state);
        return AmigaSmartFileSystemReadOnlyCore.ExamineNext(ref p, directory, fib, NativeAmigaFileSystem.Metadata(state));
    }

    // CSE7 preserves low-word offsets and adds PositionHigh at byte 40.
    internal static uint Entry(APTR state, APTR scratch)
    {
        var p = new NativeFileSystemPlatform(state); NativeAmigaFileSystem.InitializeSmartWriteWork(state, out var work);
        var node = APTR.ReadUInt32(scratch, 8); var length = APTR.ReadUInt32(scratch, 16);
        var position = ((ulong)APTR.ReadUInt32(scratch, 40) << 32) | APTR.ReadUInt32(scratch, 12);
        var buffer = APTR.FromPointer(APTR.ReadUInt32(scratch, 24));
        var operation = APTR.ReadUInt32(scratch, 0); var error = DOS.Error.None; bool ok; uint actual = 0;
        switch (operation)
        {
            case 1: ok = Recover(state, ref work); error = work.Error; break;
            case 9: ok = Root(ref p, state); break;
#if COPPERSTART_SFS_STANDALONE
            case 10: ok = NativeAmigaFileSystem.MountSfs2ForInput(ref p, state); break;
#else
            case 10: ok = AmigaSmartFileSystemReadOnlyCore.Mount(ref p, NativeAmigaFileSystem.Metadata(state), out var format) && format == AmigaSmartFileSystemFormat.Sfs2; break;
#endif
            case 11:
                node = Resolve(state, APTR.ReadUInt32(scratch, 4), APTR.FromPointer(APTR.ReadUInt32(scratch, 20)), length, out error);
                APTR.WriteUInt32(scratch, 8, node); ok = node != 0; break;
            case 12:
                ok = Describe(state, node, out var size, out var directory, out var parent);
                APTR.WriteUInt32(scratch, 12, (uint)size); APTR.WriteUInt32(scratch, 40, (uint)(size >> 32));
                APTR.WriteUInt32(scratch, 16, directory ? 1u : 0u); APTR.WriteUInt32(scratch, 4, parent); break;
            case 13:
                var read = Read(state, APTR.FromPointer(APTR.ReadUInt32(scratch, 28)), node, position, buffer, length, out error);
                actual = unchecked((uint)read); ok = read >= 0; break;
            case 14: ok = Examine(state, node, buffer); break;
            case 15: error = Next(state, APTR.ReadUInt32(scratch, 4), buffer); ok = error == DOS.Error.None; break;
            case 16: ok = AmigaSmartFileSystemReadOnlyCore.CountUsed(ref p, NativeAmigaFileSystem.Metadata(state), out actual); break;
            case 17:
                actual = unchecked((uint)NativeAmigaFileSystem.DispatchSmartMutation(state, buffer, (DosPacketAction)APTR.ReadUInt32(scratch, 28), out error));
                ok = error == DOS.Error.None; break;
            case 18: ok = Recover(state, ref work) && p.Flush(); error = work.Error; break;
            case 7: ok = Write(state, node, position, buffer, length, out actual, out error); break;
            case 8: ok = Resize(state, node, position, out error); break;
            case 2: case 3: case 4: case 5: case 6:
                var request = new AmigaSmartFileSystemWriteCore.Request { Node = node, Directory = APTR.ReadUInt32(scratch, 4),
                    Position = (uint)position, Length = length, Name = APTR.FromPointer(APTR.ReadUInt32(scratch, 20)), Buffer = buffer };
                ok = MutateClassicRequest(state, ref work, ref request, operation, APTR.ReadUInt32(scratch, 28)); error = work.Error;
                APTR.WriteUInt32(scratch, 8, request.Node); break;
            default: ok = false; error = DOS.Error.ActionNotKnown; break;
        }
        if (!ok && error == DOS.Error.None) error = DOS.Error.DiskNotValidated;
        APTR.WriteUInt32(scratch, 32, actual); APTR.WriteUInt32(scratch, 36, (uint)error);
        return ok ? 1u : 0u;
    }
}
#endif
