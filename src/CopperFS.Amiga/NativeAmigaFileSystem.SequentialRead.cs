using Amiga;
namespace CopperStart.Dos;
#if COPPERFS_NATIVE_FFS_READAHEAD
public static partial class NativeAmigaFileSystem
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void DispatchAdaptiveReadPacket(APTR state, APTR packet)
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
#if COPPERFS_NATIVE_FFS_READAHEAD
            var adaptive = 0u;
            if (length == 4096)
                adaptive = NativeFfsSequentialRead.Begin(state, file, packet, target, length, position);
            else if (APTR.ReadUInt32(file, 104) != 0 && !Smart(state)) NativeFfsSequentialRead.Reset(file);
#endif
            if (Smart(state))
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
            if (adaptive != 0)
                NativeFfsSequentialRead.End(state, file, length, position, result, error, adaptive);
            // Metadata speculation is worthwhile on direct large reads.
            // Cached small reads retain their lean payload-window path.
            if (length >= 4096 && position == 0 && result > 0 && error == DOS.Error.None)
                p.ReadAheadFfsMetadata(file, target, length);
#endif
        }
        APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result1Offset, unchecked((uint)result));
        APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result2Offset, (uint)error);
    }

}
#endif
