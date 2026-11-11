using Amiga;


namespace CopperStart.Dos;

/// <summary>Standard AmigaDOS ACTION_STARTUP entry for an L: filesystem segment.</summary>
public static class NativeSfsStandaloneHandler
{
    public static uint Entry() => Start(false);
    public static uint FfsEntry() => Start(true);
    private static uint Start(bool ffs)
    {
        // DOS LVOs use CopperSharp's writable _DOSLibraryBase slot in a HUNK.
        // The filesystem is started by DOS, but a new segment does not inherit
        // the caller's library-base register or the compiler's private slot.
        var dosBase = global::Amiga.Exec.OpenLibraryRaw(
            CString.FromPointer(CString.ToUInt32("dos.library")), 36);
        if (dosBase.IsNull) return 20;
        global::Amiga.DOS.DOSLibraryBase = dosBase;
        var task = global::Amiga.Exec.FindTask(CString.FromPointer(0));
        var port = DosProcessCodec.MessagePortAddress(task);
        global::Amiga.Exec.WaitPort(port.Raw);
        var message = APTR.FromPointer(global::Amiga.Exec.GetMsg(port.Raw));
        if (message.IsNull)
        { global::Amiga.Exec.CloseLibrary(dosBase); return 20; }
        var packet = APTR.FromPointer(APTR.ReadUInt32(message, NativeExecFieldCodec.NodeNameOffset));
        if (packet.IsNull)
        { global::Amiga.Exec.CloseLibrary(dosBase); return 20; }
        var startup = APTR.FromPointer(APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument2Offset) << 2); // dp_Arg2, FileSysStartupMsg BPTR
        var state = startup.IsNull ? APTR.Null : NativeAmigaFileSystem.CreateStartup(startup, ffs);
        if (state.IsNull)
        { Reply(message, packet, port, 0, DOS.Error.DiskNotValidated); global::Amiga.Exec.CloseLibrary(dosBase); return 20; }
        if (ffs ? APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset) is not (0x444F5300 or 0x444F5301) :
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset) is not (AmigaSmartFileSystemCore.DosType or NativeSfs2Bridge.DosType))
        {
            NativeAmigaFileSystem.Destroy(state);
            Reply(message, packet, port, 0, DOS.Error.ObjectWrongType);
            global::Amiga.Exec.CloseLibrary(dosBase);
            return 20;
        }

        APTR.WriteUInt32(state, NativeFileSystemStateCodec.PortOffset, port.Raw);
        var name = APTR.FromPointer(global::Amiga.Exec.AllocMem(108, global::Amiga.Exec.MemoryFlags.Public));
        if (name.IsNull)
        {
            NativeAmigaFileSystem.Destroy(state);
            Reply(message, packet, port, 0, DOS.Error.NoFreeStore);
            global::Amiga.Exec.CloseLibrary(dosBase);
            return 20;
        }
        NativeAmigaFileSystem.CopyVolumeName(state, name);
        var volume = global::Amiga.DOS.MakeDosEntry(CString.FromPointer(name.Raw), (int)DosListType.Volume);
        global::Amiga.Exec.FreeMem(name.Raw, 108);
        if (volume.IsNull)
        {
            NativeAmigaFileSystem.Destroy(state);
            Reply(message, packet, port, 0, DOS.Error.NoFreeStore);
            global::Amiga.Exec.CloseLibrary(dosBase);
            return 20;
        }
        APTR.WriteUInt32(volume, 8, port.Raw); // dol_Task
        NativeAmigaFileSystem.CopyVolumeDate(state, volume);
        APTR.WriteUInt32(volume, 32, APTR.ReadUInt32(state, NativeFileSystemStateCodec.DosTypeOffset));
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.VolumeOffset, volume.Raw);
        var device = APTR.FromPointer(APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.Argument3Offset) << 2); // dp_Arg3, DosList BPTR
        if (device.IsNotNull) APTR.WriteUInt32(device, 8, port.Raw);
        Reply(message, packet, port, -1, DOS.Error.None);
        // GetDeviceProc holds the DOS list lock until ACTION_STARTUP replies.
        if (global::Amiga.DOS.AddDosEntry(volume) == 0)
        {
            APTR.WriteUInt32(state, NativeFileSystemStateCodec.VolumeOffset, 0);
            global::Amiga.DOS.FreeDosEntry(volume);
        }
        var result = NativeAmigaFileSystem.Run(state);
        global::Amiga.Exec.CloseLibrary(dosBase);
        return result;
    }

    private static void Reply(APTR message, APTR packet, APTR port, int result, DOS.Error error)
    {
        var replyPort = APTR.FromPointer(APTR.ReadUInt32(packet, NativeDosPacketFieldCodec.PortOffset));
        APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result1Offset, unchecked((uint)result));
        APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.Result2Offset, (uint)error);
        APTR.WriteUInt32(packet, NativeDosPacketFieldCodec.PortOffset, port.Raw);
        global::Amiga.Exec.PutMsg(replyPort, message);
    }

    // Scalar wire boundary for private native records and SDK fields.
    private static class WireCodec
    {
        internal static uint Read32_ExecLayout_Node_Name(APTR address) => APTR.ReadUInt32(address, ExecLayout.Node.Name);
        internal static uint Read32_24(APTR address) => APTR.ReadUInt32(address, 24);
        internal static uint Read32_NativeAmigaFileSystem_DosTypeOffset(APTR address) => APTR.ReadUInt32(address, NativeAmigaFileSystem.DosTypeOffset);
        internal static void Write32_NativeAmigaFileSystem_PortOffset(APTR address, uint value) => APTR.WriteUInt32(address, NativeAmigaFileSystem.PortOffset, value);
        internal static void Write32_NativeAmigaFileSystem_VolumeOffset(APTR address, uint value) => APTR.WriteUInt32(address, NativeAmigaFileSystem.VolumeOffset, value);
        internal static uint Read32_28(APTR address) => APTR.ReadUInt32(address, 28);
        internal static uint Read32_4(APTR address) => APTR.ReadUInt32(address, 4);
        internal static void Write32_12(APTR address, uint value) => APTR.WriteUInt32(address, 12, value);
        internal static void Write32_16(APTR address, uint value) => APTR.WriteUInt32(address, 16, value);
        internal static void Write32_4(APTR address, uint value) => APTR.WriteUInt32(address, 4, value);
    }
}
