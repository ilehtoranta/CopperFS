#if COPPERSTART_FULL_FILESYSTEM_EXTENSION
using Amiga;

namespace CopperStart.Dos;

public static partial class NativeAmigaFileSystem
{
    private static bool FormatVolume(APTR state, APTR packet, out DOS.Error error)
    {
        var p = new NativeFileSystemPlatform(state);
        var token = APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument1Offset);
        var name = APTR.Null; uint length = 0;
        var type = APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument2Offset);
        var flags = type == 0x53465300 ? 64u : 0u;
        error = DOS.Error.InvalidComponentName;
        if (APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.TypeOffset) == 0xF00067)
        {
            type = 0x53465300; flags = 64;
            var tags = APTR.FromPointer(token);
            while (tags.IsNotNull)
            {
                if (!p.IsMapped(tags, 8)) return false;
                var tag = APTR.ReadUInt32(tags, 0); var value = APTR.ReadUInt32(tags, 4);
                tags = APTR.FromPointer(tags.Raw + 8);
                if (tag == 0) break;
                if (tag == 1) continue;
                if (tag == 2) { tags = APTR.FromPointer(value); continue; }
                if (tag == 3) { tags = APTR.FromPointer(tags.Raw + value * 8); continue; }
                if (tag == 0x80000001) name = APTR.FromPointer(value);
                else if (tag == 0x80000002) flags = value != 0 ? flags & ~64u : flags | 64;
                else if (tag == 0x80000003) flags = value != 0 ? flags | 128 : flags & ~128u;
                else if (tag == 0x80000004) flags = value != 0 ? flags | 1 : flags & ~1u;
            }
            if (name.IsNull) return false;
            while (length <= 30 && p.IsMapped(APTR.FromPointer(name.Raw + length), 1) && APTR.ReadUInt8(name, (int)length) != 0) length++;
        }
        else
        {
            name = APTR.FromPointer(token << 2);
            if (token > 0x3FFFFFFF || !p.IsMapped(name, 1)) return false;
            length = APTR.ReadUInt8(name, 0); name = APTR.FromPointer(name.Raw + 1);
        }
        if (!AmigaSmartFileSystemFormatCore.ValidName(ref p, name, length)) return false;
        if (type is not (0x444F5300 or 0x444F5301 or 0x53465300 or 0x53465302))
        { error = DOS.Error.BadNumber; return false; }
        if (!(type is 0x53465300 or 0x53465302
            ? AmigaSmartFileSystemFormatCore.CanFormat(p.Geometry, type)
            : AmigaDiskFileSystemFormatCore.CanFormat(p.Geometry, type)))
        { error = DOS.Error.BadNumber; return false; }
        if (p.WriteProtected()) { error = DOS.Error.DiskWriteProtected; return false; }
        if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.ObjectsOffset) != 0 ||
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.RecordWaitersOffset) != 0)
        { error = DOS.Error.ObjectInUse; return false; }
        if (!(Smart(state) ? FlushSmart(state) : p.Flush()))
        { error = DOS.Error.DiskNotValidated; return false; }
        var library = global::Amiga.Exec.OpenLibraryRaw("dos.library", 37);
        var dos = new CopperSharpNativeDosPlatform(0);
        var dosState = library.IsNull ? APTR.Null : DosNativeLibraryCore.StateFor(ref dos, library);
        if (dosState.IsNull)
        { if (library.IsNotNull) global::Amiga.Exec.CloseLibrary(library); error = DOS.Error.DeviceNotMounted; return false; }
        var text = Bitmap(state);
        for (var i = 0; i < length; i++) APTR.WriteUInt8(text, i, APTR.ReadUInt8(name, i));
        APTR.WriteUInt8(text, (int)length, 0);
        var volume = DosListCore.MakeDosEntry(ref dos, dosState, text, (int)DosListType.Volume);
        if (volume.IsNull) { global::Amiga.Exec.CloseLibrary(library); error = DOS.Error.NoFreeStore; return false; }
        // Keep the requested name outside the block-sized formatting scratch.
        name = APTR.FromPointer((APTR.ReadUInt32(volume, 40) << 2) + 1);
        var stamp = Data(state);
        DosCore.DateStamp(ref dos, dosState, stamp);
        var days = APTR.ReadUInt32(stamp, 0); var minutes = APTR.ReadUInt32(stamp, 4); var ticks = APTR.ReadUInt32(stamp, 8);
        ReleaseSmart(state);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.DosTypeOffset, 0);
        InvalidateFormatCache(state);
        var success = type is 0x53465300 or 0x53465302
            ? AmigaSmartFileSystemFormatCore.Format(ref p, type, name, length,
                unchecked(days * 86400 + minutes * 60 + ticks / 50), Metadata(state), out error, flags)
            : AmigaDiskFileSystemFormatCore.Format(ref p, type, name, length,
                days, minutes, ticks, Metadata(state), out error);
        InvalidateFormatCache(state);
        if (success)
        {
            success = type is 0x53465300 or 0x53465302 ? MountSmart(state)
                : AmigaDiskFileSystemCore.Mount(ref p, Metadata(state), out _);
            if (success)
            {
                APTR.WriteUInt32(state, NativeFileSystemStateCodec.DosTypeOffset, type);
                InitializeWriteTimer(state, APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.PortOffset)));
                APTR.WriteUInt32(volume, 8, APTR.ReadUInt32(state, NativeFileSystemStateCodec.PortOffset));
                APTR.WriteUInt32(volume, 32, type); CopyVolumeDate(state, volume);
            }
            else { ReleaseSmart(state); error = DOS.Error.DiskNotValidated; }
        }
        const uint listFlags = (uint)(DosListLockFlags.Volumes | DosListLockFlags.Write);
        DosListCore.LockDosList(ref dos, dosState, listFlags);
        var previous = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.VolumeOffset));
        if (previous.IsNotNull)
        {
            DosListCore.RemDosEntry(ref dos, dosState, previous);
            DosListCore.FreeDosEntry(ref dos, dosState, previous);
        }
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.VolumeOffset, 0);
        if (success && DosListCore.AddDosEntry(ref dos, dosState, volume) != 0)
            APTR.WriteUInt32(state, NativeFileSystemStateCodec.VolumeOffset, volume.Raw);
        else
        {
            DosListCore.FreeDosEntry(ref dos, dosState, volume);
            if (success) { success = false; error = DOS.Error.ObjectExists; }
        }
        DosListCore.UnLockDosList(ref dos, dosState, listFlags);
        if (!success) APTR.WriteUInt32(state, NativeFileSystemStateCodec.DosTypeOffset, 0);
        global::Amiga.Exec.CloseLibrary(library);
        return success;
    }

    private static void InvalidateFormatCache(APTR state)
    {
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.CacheTrackOffset, uint.MaxValue);
        var cached = APTR.FromPointer(APTR.ReadUInt32(state, NativeFileSystemStateCodec.ExtraCacheHeadOffset));
        while (cached.IsNotNull)
        {
            APTR.WriteUInt32(cached, 4, uint.MaxValue); APTR.WriteUInt32(cached, 16, 0);
            cached = APTR.FromPointer(APTR.ReadUInt32(cached, 0));
        }
        ReindexCache(state);
    }
}
#endif
