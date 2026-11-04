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

    internal static bool Recover(APTR state, ref AmigaSmartFileSystemWriteCore.Work classic)
    {
        var p = new NativeFileSystemPlatform(state); var work = Work(state);
        var ok = AmigaSmartFileSystem2WriteCore.Recover(ref p, ref work);
        classic.Error = work.Error; classic.JournalCursor = work.JournalCursor;
        return ok;
    }

    internal static bool Trim(APTR state, ref NativeFileSystemPlatform p,
        ref AmigaSmartFileSystemWriteCore.Work classic, APTR control, uint flags,
        out AmigaSmartFileSystemWriteCore.TrimResult result)
    {
        var work = Work(state);
        var ok = AmigaSmartFileSystem2WriteCore.Trim(ref p, ref work, control, flags, out var trimmed);
        result = new AmigaSmartFileSystemWriteCore.TrimResult
        { Limits = trimmed.Limits, Blocks = trimmed.Blocks, Ranges = trimmed.Ranges };
        classic.Error = work.Error; classic.JournalCursor = work.JournalCursor;
        return ok;
    }
    private static APTR Arena(APTR state)
    {
        var buffer = APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRangeBufferOffset);
        if (buffer == 0)
        {
            buffer = global::Amiga.Exec.AllocMem(BufferBytes, global::Amiga.Exec.MemoryFlags.Public);
            APTR.WriteUInt32(state, NativeFileSystemStateCodec.SmartRangeBufferOffset, buffer);
        }
        return APTR.FromPointer(buffer);
    }

    private static bool Mutate(APTR state, ref AmigaSmartFileSystem2WriteCore.Request request,
        AmigaSmartFileSystem2WriteCore.TrstOperation kind, out uint actual, out DOS.Error error)
    {
        var p = new NativeFileSystemPlatform(state); var work = Work(state); actual = 0;
#if COPPERSTART_SFS2_TRST_WRITES
        var arena = Arena(state);
        var ok = !arena.IsNull && AmigaSmartFileSystem2WriteCore.ExecuteTrst(ref p, ref work, ref request, kind,
            arena, BufferBytes, out actual);
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

    internal static int Read(APTR state, uint node, ulong position, APTR buffer, uint length, out DOS.Error error)
    {
        var p = new NativeFileSystemPlatform(state);
        return AmigaSmartFileSystemReadOnlyCore.Read(ref p, node, position, buffer, length,
            NativeAmigaFileSystem.Metadata(state), NativeAmigaFileSystem.Data(state), out error);
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
            case 10: ok = AmigaSmartFileSystemReadOnlyCore.Mount(ref p, NativeAmigaFileSystem.Metadata(state), out var format) && format == AmigaSmartFileSystemFormat.Sfs2; break;
            case 11:
                node = Resolve(state, APTR.ReadUInt32(scratch, 4), APTR.FromPointer(APTR.ReadUInt32(scratch, 20)), length, out error);
                APTR.WriteUInt32(scratch, 8, node); ok = node != 0; break;
            case 12:
                ok = Describe(state, node, out var size, out var directory, out var parent);
                APTR.WriteUInt32(scratch, 12, (uint)size); APTR.WriteUInt32(scratch, 40, (uint)(size >> 32));
                APTR.WriteUInt32(scratch, 16, directory ? 1u : 0u); APTR.WriteUInt32(scratch, 4, parent); break;
            case 13:
                var read = Read(state, node, position, buffer, length, out error); actual = unchecked((uint)read); ok = read >= 0; break;
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
