using Amiga;
using CopperSharp.Compiler;
using CopperSharp.Sdk.Amiga;

namespace CopperStart.Dos;

/// <summary>Private ABI between the fixed E0 extension ROM and the Kickstart filesystem handler.</summary>
public static partial class NativeSfsExtension
{
    // Classic CSEB is ABI version 12. The SFS2 profile uses CSEC/version 13
    // with the 2,320-byte mount state, including handler record-lock heads,
    // and a 512 KiB image; distinct signatures prevent mixing provider profiles.
    // The extension image is never a reset ROM;
    // its first longword is used as a signature instead of a stack pointer.
    public const uint Base = 0x00E0_0000;
#if COPPERSTART_FULL_FILESYSTEM_EXTENSION
    // CopperStart's Full ROM also supplies its shared filesystem task.
    public const uint Bytes = 0x0008_0000, Magic = 0x4353_4545;
#elif COPPERSTART_NATIVE_SFS2
    public const uint Bytes = 0x0008_0000, Magic = 0x4353_4543;
#else
    public const uint Bytes = 0x0004_0000, Magic = 0x4353_4542;
#endif
    public const int ClientServiceOffset = (int)Bytes - 24;
    private const int Operation = 0, Directory = 4, Node = 8, Position = 12,
        Length = 16, Name = 20, Buffer = 24, Argument = 28, Actual = 32, Error = 36, PositionHigh = 40;
    private const uint RecoverOperation = 1, CreateOperation = 2, TruncateOperation = 3,
        RemoveOperation = 4, RenameOperation = 5, MetadataOperation = 6,
        WriteOperation = 7, ResizeOperation = 8, RootOperation = 9, MountOperation = 10,
        ResolveOperation = 11, DescribeOperation = 12, ReadOperation = 13,
        ExamineOperation = 14, ExamineNextOperation = 15, CountUsedOperation = 16,
        PacketOperation = 17, FlushOperation = 18;

    [AmigaIndirectCall(M68kRegister.A3)]
    [return: M68kRegister(M68kRegister.D0)]
    private static extern uint Call([M68kRegister(M68kRegister.A3)] APTR entry,
        [M68kRegister(M68kRegister.A0)] APTR state,
        [M68kRegister(M68kRegister.A1)] APTR request);

    private static APTR Scratch(APTR state) => APTR.FromPointer(state.Raw + NativeAmigaFileSystem.ExtensionScratchOffset);

    public static bool Available()
    {
#if COPPERSTART_SFS_STANDALONE
        return true;
#else
        var header = APTR.FromPointer(Base);
        if (APTR.ReadUInt32(header, NativeSfsExtensionHeaderCodec.MagicOffset) != Magic) return false;
        var entry = APTR.ReadUInt32(header, NativeSfsExtensionHeaderCodec.EntryOffset);
        return (entry & 1) == 0 && entry >= Base + 8 && entry < Base + Bytes;
#endif
    }

    public static uint ClientServiceAddress()
    {
#if COPPERSTART_SFS_STANDALONE
        return 0;
#else
        if (!Available()) return 0;
        var entry = APTR.ReadUInt32(APTR.FromPointer(Base), 8);
        return (entry & 1) == 0 && entry >= Base + 12 && entry < Base + Bytes ? entry : 0;
#endif
    }

    private static bool Invoke(APTR state, uint operation, ref AmigaSmartFileSystemWriteCore.Work work,
        ref AmigaSmartFileSystemWriteCore.Request request, uint argument, out uint actual)
    {
        var scratch = Scratch(state);
        APTR.WriteUInt32(scratch, Directory, request.Directory);
        APTR.WriteUInt32(scratch, Node, request.Node);
        APTR.WriteUInt32(scratch, Position, request.Position);
        APTR.WriteUInt32(scratch, PositionHigh, 0);
        APTR.WriteUInt32(scratch, Length, request.Length);
        APTR.WriteUInt32(scratch, Name, request.Name.Raw);
        APTR.WriteUInt32(scratch, Buffer, request.Buffer.Raw);
        APTR.WriteUInt32(scratch, Argument, argument);
        var success = InvokeSimple(state, operation, out actual, out work.Error);
        request.Directory = APTR.ReadUInt32(scratch, Directory);
        request.Node = APTR.ReadUInt32(scratch, Node);
        request.Position = APTR.ReadUInt32(scratch, Position);
        request.Length = APTR.ReadUInt32(scratch, Length);
        return success;
    }

    private static bool InvokeSimple(APTR state, uint operation, out uint actual, out DOS.Error error)
    {
        actual = 0;
        error = DOS.Error.DiskNotValidated;
#if !COPPERSTART_SFS_STANDALONE
        if (!Available()) return false;
        var entry = APTR.ReadUInt32(APTR.FromPointer(Base), 4);
#endif
        var scratch = Scratch(state);
        APTR.WriteUInt32(scratch, Operation, operation);
        APTR.WriteUInt32(scratch, Actual, 0);
#if COPPERSTART_SFS_STANDALONE && COPPERSTART_FFS_ONLY
        // Keep the narrow standalone FFS bookkeeping path separate from the
        // extension's complete mutation dispatcher.
        var platform = new NativeFileSystemPlatform(state);
        bool success;
        if (operation == RootOperation)
        {
            success = NativeSfs2Bridge.Root(ref platform, state);
        }
        else
        {
            NativeAmigaFileSystem.InitializeSmartWriteWork(state, out var work);
            success = operation switch
            {
                MountOperation => AmigaSmartFileSystemCore.Mount(ref platform, NativeAmigaFileSystem.Metadata(state)),
                CountUsedOperation => CountUsedCore(ref platform, state, scratch),
                FlushOperation => AmigaSmartFileSystemWriteCore.Flush(ref platform, ref work),
                _ => false,
            };
            APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartJournalCursorOffset, work.JournalCursor);
            APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartAllocationCursorOffset, work.AllocationCursor);
            APTR.WriteUInt32(scratch, Error, (uint)work.Error);
        }
#elif COPPERSTART_SFS_STANDALONE
        var success = Entry(state, scratch) != 0;
#else
        var success = Call(APTR.FromPointer(entry), state, scratch) != 0;
#endif
        actual = APTR.ReadUInt32(scratch, Actual);
        error = (DOS.Error)APTR.ReadUInt32(scratch, Error);
        return success;
    }

    public static bool Recover(APTR state, ref AmigaSmartFileSystemWriteCore.Work work)
    {
#if COPPERSTART_SFS_TRST_BUFFERED
        if (TrstGroupPending(state)) { var p = new NativeFileSystemPlatform(state); return FlushNativeTrstGroup(state, ref p, ref work); }
#endif
#if COPPERSTART_SFS_STANDALONE && COPPERSTART_FFS_ONLY
        // Keep recovery separate from the shared mutation-operation dispatcher.
        var scratch = Scratch(state);
        APTR.WriteUInt32(scratch, Operation, RecoverOperation);
        APTR.WriteUInt32(scratch, Actual, 0);
        var platform = new NativeFileSystemPlatform(state);
        NativeAmigaFileSystem.InitializeSmartWriteWork(state, out var recovery);
        var recovered = AmigaSmartFileSystemWriteCore.Recover(ref platform, ref recovery);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartJournalCursorOffset, recovery.JournalCursor);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartAllocationCursorOffset, recovery.AllocationCursor);
        APTR.WriteUInt32(scratch, Error, (uint)recovery.Error);
        work.Error = recovery.Error;
        return recovered;
#else
        return InvokeSimple(state, RecoverOperation, out _, out work.Error);
#endif
    }
    public static bool Flush(APTR state, ref AmigaSmartFileSystemWriteCore.Work work)
    {
#if COPPERSTART_SFS_TRST_BUFFERED
        var p = new NativeFileSystemPlatform(state);
        if (!FlushNativeTrstGroup(state, ref p, ref work)) return false;
#endif
        return InvokeSimple(state, FlushOperation, out _, out work.Error);
    }
    public static bool Create(APTR state, ref AmigaSmartFileSystemWriteCore.Work work,
        ref AmigaSmartFileSystemWriteCore.Request request, bool directory)
    {
#if COPPERSTART_SFS_STANDALONE
        var p = new NativeFileSystemPlatform(state);
        return CompleteStandalone(state, ref work, MutateNative(state, ref p, ref work, ref request, CreateOperation, directory ? 1u : 0u));
#else
        return Invoke(state, CreateOperation, ref work, ref request, directory ? 1u : 0u, out _);
#endif
    }
    public static bool Truncate(APTR state, ref AmigaSmartFileSystemWriteCore.Work work, uint node)
    {
        var request = new AmigaSmartFileSystemWriteCore.Request { Node = node };
#if COPPERSTART_SFS_STANDALONE
        var p = new NativeFileSystemPlatform(state);
        return CompleteStandalone(state, ref work, MutateNative(state, ref p, ref work, ref request, TruncateOperation, 0));
#else
        return Invoke(state, TruncateOperation, ref work, ref request, 0, out _);
#endif
    }
    public static bool Remove(APTR state, ref AmigaSmartFileSystemWriteCore.Work work, uint node)
    {
#if COPPERSTART_SFS_STANDALONE
        var p = new NativeFileSystemPlatform(state);
        var request = new AmigaSmartFileSystemWriteCore.Request { Node = node };
        return CompleteStandalone(state, ref work, MutateNative(state, ref p, ref work, ref request, RemoveOperation, 0));
#else
        var request = new AmigaSmartFileSystemWriteCore.Request { Node = node }; return Invoke(state, RemoveOperation, ref work, ref request, 0, out _);
#endif
    }
    // OpenSmartWrite executes inside the mutation provider (or standalone
    // handler). Keep this compound operation local; no extension wire opcode
    // or request layout changes are needed.
    internal static bool ReplaceFile(APTR state, ref AmigaSmartFileSystemWriteCore.Work work,
        ref AmigaSmartFileSystemWriteCore.Request request)
    {
        if (NativeSfs2Bridge.IsSfs2(state)) return Truncate(state, ref work, request.Node);
        var p = new NativeFileSystemPlatform(state);
        var success = MutateNative(state, ref p, ref work, ref request, TruncateOperation, 1);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartJournalCursorOffset, work.JournalCursor);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartAllocationCursorOffset, work.AllocationCursor);
        return success;
    }
    public static bool Rename(APTR state, ref AmigaSmartFileSystemWriteCore.Work work,
        ref AmigaSmartFileSystemWriteCore.Request request)
    {
#if COPPERSTART_SFS_STANDALONE
        var p = new NativeFileSystemPlatform(state);
        return CompleteStandalone(state, ref work, MutateNative(state, ref p, ref work, ref request, RenameOperation, 0));
#else
        return Invoke(state, RenameOperation, ref work, ref request, 0, out _);
#endif
    }
    public static bool SetMetadata(APTR state, ref AmigaSmartFileSystemWriteCore.Work work,
        ref AmigaSmartFileSystemWriteCore.Request request, DosPacketAction action)
    {
#if COPPERSTART_SFS_STANDALONE
        var p = new NativeFileSystemPlatform(state);
        return CompleteStandalone(state, ref work, MutateNative(state, ref p, ref work, ref request, MetadataOperation, (uint)action));
#else
        return Invoke(state, MetadataOperation, ref work, ref request, (uint)action, out _);
#endif
    }

#if COPPERSTART_SFS_STANDALONE
    private static bool CompleteStandalone(APTR state, ref AmigaSmartFileSystemWriteCore.Work work, bool success)
    {
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartJournalCursorOffset, work.JournalCursor);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartAllocationCursorOffset, work.AllocationCursor);
        return success;
    }
#endif
    private static bool MutateNative(APTR state, ref NativeFileSystemPlatform p,
        ref AmigaSmartFileSystemWriteCore.Work work, ref AmigaSmartFileSystemWriteCore.Request request,
        uint operation, uint argument)
    {
        if (NativeSfs2Bridge.IsSfs2(state)) return NativeSfs2Bridge.MutateClassicRequest(state, ref work, ref request, operation, argument);
#if COPPERSTART_SFS_TRST_WRITES
        var kind = operation switch
        {
            CreateOperation => argument != 0 ? AmigaSmartFileSystemWriteCore.TrstOperation.CreateDirectory : AmigaSmartFileSystemWriteCore.TrstOperation.CreateFile,
            TruncateOperation => argument != 0 ? AmigaSmartFileSystemWriteCore.TrstOperation.ReplaceFile : AmigaSmartFileSystemWriteCore.TrstOperation.Truncate,
            RemoveOperation => AmigaSmartFileSystemWriteCore.TrstOperation.Remove,
            RenameOperation => AmigaSmartFileSystemWriteCore.TrstOperation.Rename,
            ResizeOperation => AmigaSmartFileSystemWriteCore.TrstOperation.Resize,
            _ => argument == (uint)DosPacketAction.SetComment ? AmigaSmartFileSystemWriteCore.TrstOperation.SetComment :
                argument == (uint)DosPacketAction.SetDate ? AmigaSmartFileSystemWriteCore.TrstOperation.SetDate :
                AmigaSmartFileSystemWriteCore.TrstOperation.SetProtection
        };
        if (operation == ResizeOperation) request.Position = request.Length;
        return ExecuteNativeTrst(state, ref p, ref work, ref request, kind, out _);
#else
        return operation switch
        {
            CreateOperation => AmigaSmartFileSystemWriteCore.Create(ref p, ref work, ref request, argument != 0),
            TruncateOperation => argument != 0 ? AmigaSmartFileSystemWriteCore.ReplaceFile(ref p, ref work, ref request) : AmigaSmartFileSystemWriteCore.Truncate(ref p, ref work, request.Node),
            RemoveOperation => AmigaSmartFileSystemWriteCore.Remove(ref p, ref work, request.Node),
            RenameOperation => AmigaSmartFileSystemWriteCore.Rename(ref p, ref work, ref request),
            ResizeOperation => AmigaSmartFileSystemWriteCore.Resize(ref p, ref work, request.Node, request.Length),
            _ => AmigaSmartFileSystemWriteCore.SetMetadata(ref p, ref work, ref request, (DosPacketAction)argument)
        };
#endif
    }

#if COPPERSTART_SFS_TRST_WRITES
    private static bool ExecuteNativeTrst(APTR state, ref NativeFileSystemPlatform p,
        ref AmigaSmartFileSystemWriteCore.Work work, ref AmigaSmartFileSystemWriteCore.Request request,
        AmigaSmartFileSystemWriteCore.TrstOperation kind, out uint actual)
    {
#if COPPERSTART_SFS_TRST_BUFFERED
        return ExecuteStrictNativeTrstGroup(state, ref p, ref work, ref request, kind, out actual);
#else
        actual = 0;
        var arena = APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRangeBufferOffset);
#if COPPERSTART_SFS_STANDALONE && !COPPERSTART_NATIVE_SFS2
        // The self-contained classic handler owns allocation and release.
        // Extension clients and combined SFS2 handlers retain their fixed-size
        // allocation contract, including mounts that can change disk format.
        var bytes = arena == 0 ? 0 : AmigaSmartFileSystemRedoArena.StorageBytes(ref p, APTR.FromPointer(arena));
        if (arena == 0)
        {
            var initial = 152 + 16 * (p.BlockBytes + AmigaSmartFileSystemRedoArena.SlotHeaderBytes); // 16 slots and 32 index buckets.
            if (initial > NativeAmigaFileSystem.SmartRangeBufferBytes) initial = NativeAmigaFileSystem.SmartRangeBufferBytes;
            bytes = AmigaSmartFileSystemRedoArena.FitBytes(initial, p.BlockBytes);
            arena = AllocateTrstArena(state, ref p, bytes);
            if (arena == 0) { work.Error = DOS.Error.NoFreeStore; return false; }
        }
        var original = request;
        var success = AmigaSmartFileSystemWriteCore.ExecuteTrst(ref p, ref work, ref request, kind,
            APTR.FromPointer(arena), bytes, out actual);
        if (success || APTR.ReadUInt32(APTR.FromPointer(arena), 12) == 0) return success;
        var maximum = AmigaSmartFileSystemRedoArena.FitBytes(NativeAmigaFileSystem.SmartRangeBufferBytes, p.BlockBytes);
        if (bytes == maximum) return false;
        // Arena exhaustion is reported before CommitTrst, hence before TRFA.
        // Drop the failed private images before allocating the full arena;
        // peak arena memory never exceeds the original budget. Unpublished
        // data remains unreachable. Re-run the entire request, never a suffix.
        request = original;
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartRangeBufferOffset, 0);
        global::Amiga.Exec.FreeMem(arena, bytes);
        arena = AllocateTrstArena(state, ref p, maximum);
        if (arena == 0) { actual = 0; work.Error = DOS.Error.NoFreeStore; return false; }
        return AmigaSmartFileSystemWriteCore.ExecuteTrst(ref p, ref work, ref request, kind,
            APTR.FromPointer(arena), maximum, out actual);
#else
        if (arena == 0)
        {
            arena = global::Amiga.Exec.AllocMem(NativeAmigaFileSystem.SmartRangeBufferBytes, global::Amiga.Exec.MemoryFlags.Public);
            if (arena == 0) { work.Error = DOS.Error.NoFreeStore; return false; }
            APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartRangeBufferOffset, arena);
        }
        return AmigaSmartFileSystemWriteCore.ExecuteTrst(ref p, ref work, ref request, kind,
            APTR.FromPointer(arena), NativeAmigaFileSystem.SmartRangeBufferBytes, out actual);
#endif
#endif
    }
#if COPPERSTART_SFS_STANDALONE && !COPPERSTART_NATIVE_SFS2
    private static uint AllocateTrstArena(APTR state, ref NativeFileSystemPlatform p, uint bytes)
    {
        if (bytes == 0) return 0;
        var arena = global::Amiga.Exec.AllocMem(bytes, global::Amiga.Exec.MemoryFlags.Public);
        if (arena == 0) return 0;
        // Initialize the allocation descriptor even if ExecuteTrst rejects its
        // request before staging. Destroy can then free the exact owned span.
        if (!AmigaSmartFileSystemRedoArena.Initialize(ref p, APTR.FromPointer(arena), bytes, p.BlockBytes))
        { global::Amiga.Exec.FreeMem(arena, bytes); return 0; }
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartRangeBufferOffset, arena);
        return arena;
    }
#endif
#endif
    private static bool WriteNative(APTR state, ref NativeFileSystemPlatform platform,
        ref AmigaSmartFileSystemWriteCore.Work work, ref AmigaSmartFileSystemWriteCore.Request request,
        out uint actual, bool append)
    {
        if (NativeSfs2Bridge.IsSfs2(state))
        {
            var ok = NativeSfs2Bridge.Write(state, request.Node, request.Position, request.Buffer, request.Length, out actual, out work.Error);
            work.JournalCursor = APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartJournalCursorOffset);
            return ok;
        }
#if COPPERSTART_SFS_TRST_BUFFERED
        return WriteBufferedNativeTrst(state, ref platform, ref work, ref request, out actual);
#elif COPPERSTART_SFS_TRST_WRITES
        return ExecuteNativeTrst(state, ref platform, ref work, ref request, AmigaSmartFileSystemWriteCore.TrstOperation.Write, out actual);
#else
        // The packet handler already knows the open file's size. Avoid another
        // metadata lookup for appends; the writer still validates the request.
        if ((request.Length < 4096 && !AmigaSmartFileSystemWriteCore.HasOverwriteReservation(ref platform, ref work, request.Node)) || (append && request.Length <= 65536))
            return AmigaSmartFileSystemWriteCore.Write(ref platform, ref work, ref request, out actual);
        // Allocate once, on demand. The device adapter applies DMA-mask,
        // MaxTransfer and bounce-buffer constraints to each range transfer.
        var buffer = APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRangeBufferOffset);
        if (buffer == 0 && request.Length >= 4096)
        {
            buffer = global::Amiga.Exec.AllocMem(NativeAmigaFileSystem.SmartRangeBufferBytes,
                global::Amiga.Exec.MemoryFlags.Public);
            APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartRangeBufferOffset, buffer);
        }
        return AmigaSmartFileSystemWriteCore.WriteBatched(ref platform, ref work, ref request,
            APTR.FromPointer(buffer), buffer == 0 ? 0 : NativeAmigaFileSystem.SmartRangeBufferBytes, out actual);
#endif
    }

    public static bool Write(APTR state, ref AmigaSmartFileSystemWriteCore.Work work,
        ref AmigaSmartFileSystemWriteCore.Request request, out uint actual, bool append = false)
    {
#if COPPERSTART_SFS_STANDALONE
        var p = new NativeFileSystemPlatform(state);
        var success = WriteNative(state, ref p, ref work, ref request, out actual, append);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartJournalCursorOffset, work.JournalCursor);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartAllocationCursorOffset, work.AllocationCursor);
        return success;
#else
        var scratch = Scratch(state);
        APTR.WriteUInt32(scratch, Node, request.Node);
        APTR.WriteUInt32(scratch, Position, request.Position);
        APTR.WriteUInt32(scratch, PositionHigh, 0);
        APTR.WriteUInt32(scratch, Buffer, request.Buffer.Raw);
        APTR.WriteUInt32(scratch, Length, request.Length);
        APTR.WriteUInt32(scratch, Argument, append ? 1u : 0u);
        var success = InvokeSimple(state, WriteOperation, out actual, out work.Error);
        if (success) request.Position = APTR.ReadUInt32(scratch, Position);
        return success;
#endif
    }
    public static bool Resize(APTR state, ref AmigaSmartFileSystemWriteCore.Work work, uint node, uint bytes)
    {
#if COPPERSTART_SFS_STANDALONE
        var p = new NativeFileSystemPlatform(state);
        var request = new AmigaSmartFileSystemWriteCore.Request { Node = node, Length = bytes };
        return CompleteStandalone(state, ref work, MutateNative(state, ref p, ref work, ref request, ResizeOperation, 0));
#else
        var scratch = Scratch(state);
        APTR.WriteUInt32(scratch, Node, node);
        APTR.WriteUInt32(scratch, Length, bytes);
        APTR.WriteUInt32(scratch, Position, bytes); APTR.WriteUInt32(scratch, PositionHigh, 0);
        return InvokeSimple(state, ResizeOperation, out _, out work.Error);
#endif
    }

    internal static bool WriteWide(APTR state, uint node, ulong position, APTR buffer, uint length,
        out uint actual, out DOS.Error error)
    {
#if COPPERSTART_SFS_STANDALONE
        return NativeSfs2Bridge.Write(state, node, position, buffer, length, out actual, out error);
#else
        var scratch = Scratch(state);
        APTR.WriteUInt32(scratch, Node, node); APTR.WriteUInt32(scratch, Position, (uint)position);
        APTR.WriteUInt32(scratch, PositionHigh, (uint)(position >> 32));
        APTR.WriteUInt32(scratch, Buffer, buffer.Raw); APTR.WriteUInt32(scratch, Length, length);
        return InvokeSimple(state, WriteOperation, out actual, out error);
#endif
    }

    internal static bool ResizeWide(APTR state, uint node, ulong size, out DOS.Error error)
    {
#if COPPERSTART_SFS_STANDALONE
        return NativeSfs2Bridge.Resize(state, node, size, out error);
#else
        var scratch = Scratch(state);
        APTR.WriteUInt32(scratch, Node, node); APTR.WriteUInt32(scratch, Position, (uint)size);
        APTR.WriteUInt32(scratch, PositionHigh, (uint)(size >> 32));
        return InvokeSimple(state, ResizeOperation, out _, out error);
#endif
    }

    public static bool Root(APTR state)
        => InvokeSimple(state, RootOperation, out _, out _);
    public static bool Mount(APTR state)
        => InvokeSimple(state, MountOperation, out _, out _);
    public static uint Resolve(APTR state, uint directory, APTR name, uint length, out DOS.Error error)
    {
#if COPPERSTART_SFS_STANDALONE
        if (NativeSfs2Bridge.IsSfs2(state)) return NativeSfs2Bridge.Resolve(state, directory, name, length, out error);
        var p = new NativeFileSystemPlatform(state);
        return AmigaSmartFileSystemCore.Resolve(ref p, directory, name, length, NativeAmigaFileSystem.Metadata(state), out error);
#else
        var scratch = Scratch(state);
        APTR.WriteUInt32(scratch, Directory, directory);
        APTR.WriteUInt32(scratch, Name, name.Raw);
        APTR.WriteUInt32(scratch, Length, length);
        return InvokeSimple(state, ResolveOperation, out _, out error)
            ? APTR.ReadUInt32(scratch, Node) : 0;
#endif
    }
    public static uint ResolveInformation(APTR state, uint directory, APTR name, uint length,
        out ulong size, out bool isDirectory, out uint parent, out DOS.Error error)
    {
#if COPPERSTART_SFS_STANDALONE
        var p = new NativeFileSystemPlatform(state);
#if COPPERSTART_NATIVE_SFS2
        if (NativeSfs2Bridge.IsSfs2(state))
            return AmigaSmartFileSystemReadOnlyCore.ResolveInformation(ref p, directory, name, length,
                NativeAmigaFileSystem.Metadata(state), out size, out isDirectory, out parent, out error);
#endif
        var node = AmigaSmartFileSystemCore.Resolve(ref p, directory, name, length,
            NativeAmigaFileSystem.Metadata(state), out error, out var record, out var at);
        size = parent = 0; isDirectory = false;
        if (node != 0)
        {
            isDirectory = (p.ReadUInt8(record, at + 24) & 128) != 0;
            size = isDirectory ? 0 : APTR.ReadUInt32(record, NativeSfsObjectFieldCodec.SizeOffset + at);
            parent = APTR.ReadUInt32(record, NativeSfsObjectFieldCodec.ParentOffset);
        }
        return node;
#else
        size = parent = 0; isDirectory = false;
        var node = Resolve(state, directory, name, length, out error);
        if (node != 0 && !Describe(state, node, out size, out isDirectory, out parent))
        { error = DOS.Error.DiskNotValidated; return 0; }
        return node;
#endif
    }

    public static bool Describe(APTR state, uint node, out ulong size, out bool directory, out uint parent)
    {
#if COPPERSTART_SFS_STANDALONE
        if (NativeSfs2Bridge.IsSfs2(state)) return NativeSfs2Bridge.Describe(state, node, out size, out directory, out parent);
        var p = new NativeFileSystemPlatform(state);
        var ok = AmigaSmartFileSystemCore.Describe(ref p, node, NativeAmigaFileSystem.Metadata(state), out var narrowSize, out directory, out parent);
        size = narrowSize; return ok;
#else
        var scratch = Scratch(state);
        APTR.WriteUInt32(scratch, Node, node);
        var success = InvokeSimple(state, DescribeOperation, out _, out _);
        size = ((ulong)APTR.ReadUInt32(scratch, PositionHigh) << 32) | APTR.ReadUInt32(scratch, Position);
        directory = APTR.ReadUInt32(scratch, Length) != 0;
        parent = APTR.ReadUInt32(scratch, Directory);
        return success;
#endif
    }
    public static int Read(APTR state, APTR file, uint node, ulong position, APTR destination, uint length, out DOS.Error error)
    {
#if COPPERSTART_SFS_STANDALONE
        if (NativeSfs2Bridge.IsSfs2(state)) return NativeSfs2Bridge.Read(state, file, node, position, destination, length, out error);
        if (position > uint.MaxValue) { error = DOS.Error.SeekError; return -1; }
        var p = new NativeFileSystemPlatform(state);
        return ReadFile(ref p, state, file, node, (uint)position, destination, length, out error);
#else
        if (position > uint.MaxValue && !NativeSfs2Bridge.IsSfs2(state)) { error = DOS.Error.SeekError; return -1; }
        var scratch = Scratch(state);
        APTR.WriteUInt32(scratch, Node, node);
        APTR.WriteUInt32(scratch, Position, (uint)position);
        APTR.WriteUInt32(scratch, PositionHigh, (uint)(position >> 32));
        APTR.WriteUInt32(scratch, Buffer, destination.Raw);
        APTR.WriteUInt32(scratch, Length, length);
        APTR.WriteUInt32(scratch, Argument, file.Raw);
        return InvokeSimple(state, ReadOperation, out var actual, out error)
            ? unchecked((int)actual) : -1;
#endif
    }
    public static bool Examine(APTR state, uint node, APTR fib)
    {
#if COPPERSTART_SFS_STANDALONE
        if (NativeSfs2Bridge.IsSfs2(state)) return NativeSfs2Bridge.Examine(state, node, fib);
        var p = new NativeFileSystemPlatform(state);
        return AmigaSmartFileSystemCore.Examine(ref p, node, fib, NativeAmigaFileSystem.Metadata(state));
#else
        var scratch = Scratch(state);
        APTR.WriteUInt32(scratch, Node, node);
        APTR.WriteUInt32(scratch, Buffer, fib.Raw);
        return InvokeSimple(state, ExamineOperation, out _, out _);
#endif
    }
    public static DOS.Error ExamineNext(APTR state, APTR owned, uint directory, APTR fib)
    {
#if COPPERSTART_SFS_STANDALONE
        if (NativeSfs2Bridge.IsSfs2(state)) return NativeSfs2Bridge.Next(state, directory, fib);
        var p = new NativeFileSystemPlatform(state);
        return NextDirectory(ref p, state, owned, directory, fib);
#else
        var scratch = Scratch(state);
        APTR.WriteUInt32(scratch, Directory, directory);
        APTR.WriteUInt32(scratch, Buffer, fib.Raw);
        APTR.WriteUInt32(scratch, Argument, owned.Raw);
        InvokeSimple(state, ExamineNextOperation, out _, out var error);
        return error;
#endif
    }
    public static bool CountUsed(APTR state, out uint used)
        => InvokeSimple(state, CountUsedOperation, out used, out _);
    public static int Packet(APTR state, APTR packet, DosPacketAction action, out DOS.Error error)
    {
#if COPPERSTART_SFS_STANDALONE
        return NativeAmigaFileSystem.DispatchSmartMutation(state, packet, action, out error);
#else
        var scratch = Scratch(state);
        APTR.WriteUInt32(scratch, Buffer, packet.Raw);
        APTR.WriteUInt32(scratch, Argument, (uint)action);
        return InvokeSimple(state, PacketOperation, out var result, out error)
            ? unchecked((int)result) : 0;
#endif
    }

    /// <summary>Only the extension-ROM compiler root reaches the SFS mutation engine.</summary>
    public static uint ImageRoot() => 0;

    [M68kExport("copperstart.sfs.extension.entry")]
    [return: M68kRegister(M68kRegister.D0)]
    public static uint Entry([M68kRegister(M68kRegister.A0)] APTR state,
        [M68kRegister(M68kRegister.A1)] APTR scratch)
    {
        var p = new NativeFileSystemPlatform(state);
        if (APTR.ReadUInt32(scratch, Operation) == RootOperation)
            return NativeSfs2Bridge.Root(ref p, state) ? 1u : 0u;
        if (NativeSfs2Bridge.IsSfs2(state)) return NativeSfs2Bridge.Entry(state, scratch);
        NativeAmigaFileSystem.InitializeSmartWriteWork(state, out var work);
        var request = new AmigaSmartFileSystemWriteCore.Request
        {
            Directory = APTR.ReadUInt32(scratch, Directory), Node = APTR.ReadUInt32(scratch, Node),
            Position = APTR.ReadUInt32(scratch, Position), Length = APTR.ReadUInt32(scratch, Length),
            Name = APTR.FromPointer(APTR.ReadUInt32(scratch, Name)),
            Buffer = APTR.FromPointer(APTR.ReadUInt32(scratch, Buffer)),
        };
        // Decode scalar arguments once before dispatch.
        var argument = APTR.ReadUInt32(scratch, Argument);
        var operation = APTR.ReadUInt32(scratch, Operation);
        var success = operation switch
        {
            RecoverOperation => AmigaSmartFileSystemWriteCore.Recover(ref p, ref work),
            FlushOperation => AmigaSmartFileSystemWriteCore.Flush(ref p, ref work),
            CreateOperation => MutateNative(state, ref p, ref work, ref request, CreateOperation, argument),
            TruncateOperation => MutateNative(state, ref p, ref work, ref request, TruncateOperation, 0),
            RemoveOperation => MutateNative(state, ref p, ref work, ref request, RemoveOperation, 0),
            RenameOperation => MutateNative(state, ref p, ref work, ref request, RenameOperation, 0),
            MetadataOperation => MutateNative(state, ref p, ref work, ref request, MetadataOperation, argument),
            ResizeOperation => MutateNative(state, ref p, ref work, ref request, ResizeOperation, 0),
            WriteOperation => WriteNative(state, ref p, ref work, ref request, out var written,
                APTR.ReadUInt32(scratch, Argument) != 0)
                && StoreActual(scratch, written),
            RootOperation => AmigaSmartFileSystemCore.Root(ref p, NativeAmigaFileSystem.Metadata(state)),
            MountOperation => AmigaSmartFileSystemCore.Mount(ref p, NativeAmigaFileSystem.Metadata(state)),
            ResolveOperation => ResolveCore(ref p, state, ref work, ref request),
            DescribeOperation => DescribeCore(ref p, state, ref request),
            ReadOperation => ReadCore(ref p, state, ref work, ref request, scratch),
            ExamineOperation => AmigaSmartFileSystemCore.Examine(ref p, request.Node, request.Buffer,
                NativeAmigaFileSystem.Metadata(state)),
            ExamineNextOperation => ExamineNextCore(ref p, state, ref work, ref request, APTR.FromPointer(APTR.ReadUInt32(scratch, Argument))),
            CountUsedOperation => CountUsedCore(ref p, state, scratch),
            PacketOperation => PacketCore(state, ref work, ref request, scratch),
            _ => false,
        };
        // These are private search hints only; a reboot rebuilds them from
        // the bitmap and the journal's on-disk state.
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartJournalCursorOffset, work.JournalCursor);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartAllocationCursorOffset, work.AllocationCursor);
        APTR.WriteUInt32(scratch, Directory, request.Directory);
        APTR.WriteUInt32(scratch, Node, request.Node);
        APTR.WriteUInt32(scratch, Position, request.Position);
        APTR.WriteUInt32(scratch, Length, request.Length);
        APTR.WriteUInt32(scratch, Error, (uint)work.Error);
        return success ? 1u : 0u;
    }

    private static bool StoreActual(APTR scratch, uint actual)
    { APTR.WriteUInt32(scratch, Actual, actual); return true; }

    private static bool ResolveCore(ref NativeFileSystemPlatform p, APTR state,
        ref AmigaSmartFileSystemWriteCore.Work work, ref AmigaSmartFileSystemWriteCore.Request request)
    {
        request.Node = AmigaSmartFileSystemCore.Resolve(ref p, request.Directory, request.Name,
            request.Length, NativeAmigaFileSystem.Metadata(state), out work.Error);
        return request.Node != 0;
    }
    private static bool DescribeCore(ref NativeFileSystemPlatform p, APTR state,
        ref AmigaSmartFileSystemWriteCore.Request request)
    {
        var success = AmigaSmartFileSystemCore.Describe(ref p, request.Node,
            NativeAmigaFileSystem.Metadata(state), out var size, out var directory, out var parent);
        request.Position = size; request.Length = directory ? 1u : 0u; request.Directory = parent;
        APTR.WriteUInt32(Scratch(state), PositionHigh, 0);
        return success;
    }
    private static bool ReadCore(ref NativeFileSystemPlatform p, APTR state,
        ref AmigaSmartFileSystemWriteCore.Work work, ref AmigaSmartFileSystemWriteCore.Request request, APTR scratch)
    {
        var actual = ReadFile(ref p, state, APTR.FromPointer(APTR.ReadUInt32(scratch, Argument)), request.Node,
            request.Position, request.Buffer, request.Length, out work.Error);
        APTR.WriteUInt32(scratch, Actual, unchecked((uint)actual));
        return actual >= 0;
    }

    private static int ReadFile(ref NativeFileSystemPlatform p, APTR state, APTR file, uint node,
        uint position, APTR destination, uint length, out DOS.Error error)
    {
        var owned = APTR.FromPointer(file.Raw + NativeAmigaFileSystem.SmartReadCursorOffset);
        // The handle's cursor is cleared by every mutation and media change.
        // For an aligned packet wholly inside its current extent, read straight
        // into the caller without rebuilding or storing the traversal record.
        if (file.IsNotNull && node != 0 && length != 0 && length <= int.MaxValue &&
            p.BlockBytes == 512 && ((position | length) & 511) == 0 &&
            (destination.Raw & 3) == 0 &&
            APTR.ReadUInt32(owned, 0) == node && p.IsMapped(destination, length))
        {
            var size = APTR.ReadUInt32(owned, 4);
            if (position <= size && length <= size - position)
            {
                var first = position >> 9;
                var start = APTR.ReadUInt32(owned, 20);
                var count = APTR.ReadUInt32(owned, 24);
                var extent = APTR.ReadUInt32(owned, 16);
                if (count != 0 && first >= start && first - start < count)
                {
                    var offset = first - start;
                    var blocks = length >> 9;
                    var blockCount = p.BlockCount;
                    if (blocks <= count - offset && extent < blockCount &&
                        offset < blockCount - extent)
                    {
                        var diskBlock = extent + offset;
                        if (blocks <= blockCount - diskBlock)
                        {
                            var read = p.ReadDataBlocks(diskBlock, blocks, destination);
                            if (read == blocks) { error = DOS.Error.None; return (int)length; }
                            APTR.WriteUInt32(owned, 0, 0);
                            error = DOS.Error.DiskNotValidated;
                            return read == 0 || read > blocks ? -1 : (int)(read << 9);
                        }
                    }
                }
            }
        }
        var cursor = new AmigaSmartFileSystemCore.ReadCursor
        {
            Node = APTR.ReadUInt32(owned, 0), Size = APTR.ReadUInt32(owned, 4), First = APTR.ReadUInt32(owned, 8),
            Tree = APTR.ReadUInt32(owned, 12), Extent = APTR.ReadUInt32(owned, 16), Start = APTR.ReadUInt32(owned, 20),
            Count = APTR.ReadUInt32(owned, 24), Next = APTR.ReadUInt32(owned, 28),
            BlockCount = APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockCountOffset),
            BlockBytes = APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockBytesOffset)
        };
        var actual = AmigaSmartFileSystemCore.Read(ref p, ref cursor, node, position,
            destination, length, NativeAmigaFileSystem.Metadata(state),
            NativeAmigaFileSystem.Data(state), out error);
        APTR.WriteUInt32(owned, 0, cursor.Node); APTR.WriteUInt32(owned, 4, cursor.Size);
        APTR.WriteUInt32(owned, 8, cursor.First); APTR.WriteUInt32(owned, 12, cursor.Tree);
        APTR.WriteUInt32(owned, 16, cursor.Extent); APTR.WriteUInt32(owned, 20, cursor.Start);
        APTR.WriteUInt32(owned, 24, cursor.Count); APTR.WriteUInt32(owned, 28, cursor.Next);
        return actual;
    }
    private static bool ExamineNextCore(ref NativeFileSystemPlatform p, APTR state,
        ref AmigaSmartFileSystemWriteCore.Work work, ref AmigaSmartFileSystemWriteCore.Request request, APTR owned)
    {
        work.Error = NextDirectory(ref p, state, owned, request.Directory, request.Buffer);
        return work.Error == DOS.Error.None;
    }

    private static DOS.Error NextDirectory(ref NativeFileSystemPlatform p, APTR state, APTR owned, uint directory, APTR fib)
    {
        if (!p.IsMapped(fib, FileInfoBlock.SizeInBytes)) return DOS.Error.InvalidLock;
        var cache = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartEnumerationCacheOffset));
        if (cache.IsNull || directory == 0) return NextDirectoryUncached(ref p, state, owned, directory, fib);
        var wanted = APTR.ReadUInt32(fib, 0);
        var entry = APTR.FromPointer(cache.Raw + 4 + ((directory ^ wanted) & 31) * NativeAmigaFileSystem.SmartEnumerationEntryBytes);
        var generation = APTR.ReadUInt32(cache, 0);
        if (APTR.ReadUInt32(entry, NativeSfsEnumerationEntryCodec.DirectoryOffset) == directory && APTR.ReadUInt32(entry, NativeSfsEnumerationEntryCodec.WantedOffset) == wanted &&
            APTR.ReadUInt32(entry, NativeSfsEnumerationEntryCodec.GenerationOffset) == generation)
        {
            var cachedError = (DOS.Error)APTR.ReadUInt32(entry, NativeSfsEnumerationEntryCodec.ErrorOffset);
            if (cachedError == DOS.Error.None) p.Copy(APTR.FromPointer(entry.Raw + 16), fib, FileInfoBlock.SizeInBytes);
            // A later collision miss must rebuild its per-lock cursor from the
            // caller's token, since cached steps bypassed that cursor.
            if (owned.IsNotNull) APTR.WriteUInt32(owned, 40, 0);
            return cachedError;
        }
        var error = NextDirectoryUncached(ref p, state, owned, directory, fib);
        if (error is DOS.Error.None or DOS.Error.NoMoreEntries)
        {
            APTR.WriteUInt32(entry, NativeSfsEnumerationEntryCodec.DirectoryOffset, directory); APTR.WriteUInt32(entry, NativeSfsEnumerationEntryCodec.WantedOffset, wanted);
            APTR.WriteUInt32(entry, NativeSfsEnumerationEntryCodec.ErrorOffset, (uint)error); APTR.WriteUInt32(entry, NativeSfsEnumerationEntryCodec.GenerationOffset, generation);
            if (error == DOS.Error.None) p.Copy(fib, APTR.FromPointer(entry.Raw + 16), FileInfoBlock.SizeInBytes);
        }
        return error;
    }

    private static DOS.Error NextDirectoryUncached(ref NativeFileSystemPlatform p, APTR state, APTR owned, uint directory, APTR fib)
    {
        if (owned.IsNull) return AmigaSmartFileSystemCore.ExamineNext(ref p, directory, fib,
            NativeAmigaFileSystem.Metadata(state), NativeAmigaFileSystem.Data(state));
        var cursor = new AmigaSmartFileSystemCore.DirectoryCursor
        {
            Directory = directory, Wanted = APTR.ReadUInt32(owned, 40), Container = APTR.ReadUInt32(owned, 44),
            Remaining = APTR.ReadUInt32(owned, 48), Visited = APTR.ReadUInt32(owned, 52),
            Entries = APTR.FromPointer(owned.Raw + 88), Capacity = 32,
            BlockCount = APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockCountOffset),
            BlockBytes = APTR.ReadUInt32(state, NativeFileSystemStateCodec.BlockBytesOffset)
        };
        var error = AmigaSmartFileSystemCore.ExamineNext(ref p, ref cursor, directory, fib,
            NativeAmigaFileSystem.Metadata(state), NativeAmigaFileSystem.Data(state));
        APTR.WriteUInt32(owned, 40, cursor.Wanted); APTR.WriteUInt32(owned, 44, cursor.Container);
        APTR.WriteUInt32(owned, 48, cursor.Remaining); APTR.WriteUInt32(owned, 52, cursor.Visited);
        return error;
    }
    private static bool CountUsedCore(ref NativeFileSystemPlatform p, APTR state, APTR scratch)
    {
        var success = AmigaSmartFileSystemCore.CountUsed(ref p, NativeAmigaFileSystem.Metadata(state), out var used);
        APTR.WriteUInt32(scratch, Actual, used);
        return success;
    }
    private static bool PacketCore(APTR state, ref AmigaSmartFileSystemWriteCore.Work work,
        ref AmigaSmartFileSystemWriteCore.Request request, APTR scratch)
    {
        var result = NativeAmigaFileSystem.DispatchSmartMutation(state, request.Buffer,
            (DosPacketAction)APTR.ReadUInt32(scratch, Argument), out work.Error);
        APTR.WriteUInt32(scratch, Actual, unchecked((uint)result));
        return work.Error == DOS.Error.None;
    }
}
