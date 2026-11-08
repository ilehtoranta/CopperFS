using Amiga;

namespace CopperStart.Dos;

#if !COPPERSTART_NATIVE_SFS2
// Keep the classic qualification profile independent of the optional SFS2
// integration. Unsupported formats cannot be mounted through these stubs.
internal static class NativeSfs2Bridge
{
    internal const uint DosType = 0x53465302;
    internal const uint BufferBytes = 65536;
    internal static bool IsSfs2(APTR state) => false;
    internal static bool Root(ref NativeFileSystemPlatform p, APTR state)
    {
        if (!AmigaSmartFileSystemCore.Root(ref p, NativeAmigaFileSystem.Metadata(state))) return false;
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.DosTypeOffset, AmigaSmartFileSystemCore.DosType);
        return true;
    }
    internal static bool Recover(APTR state, ref AmigaSmartFileSystemWriteCore.Work work)
    { work.Error = DOS.Error.ActionNotKnown; return false; }
    internal static bool MutateClassicRequest(APTR state, ref AmigaSmartFileSystemWriteCore.Work work,
        ref AmigaSmartFileSystemWriteCore.Request request, uint operation, uint argument)
    { work.Error = DOS.Error.ActionNotKnown; return false; }
    internal static bool Write(APTR state, uint node, ulong position, APTR data, uint length, out uint actual, out DOS.Error error)
    { actual = 0; error = DOS.Error.ActionNotKnown; return false; }
    internal static bool Resize(APTR state, uint node, ulong size, out DOS.Error error)
    { error = DOS.Error.ActionNotKnown; return false; }
    internal static uint Resolve(APTR state, uint directory, APTR name, uint length, out DOS.Error error)
    { error = DOS.Error.ActionNotKnown; return 0; }
    internal static bool Describe(APTR state, uint node, out ulong size, out bool directory, out uint parent)
    { size = 0; directory = false; parent = 0; return false; }
    internal static int Read(APTR state, APTR file, uint node, ulong position, APTR buffer, uint length, out DOS.Error error)
    { error = DOS.Error.ActionNotKnown; return -1; }
    internal static bool Examine(APTR state, uint node, APTR fib) => false;
    internal static DOS.Error Next(APTR state, uint directory, APTR fib) => DOS.Error.ActionNotKnown;
    internal static uint Entry(APTR state, APTR scratch) => 0;
}
#endif
