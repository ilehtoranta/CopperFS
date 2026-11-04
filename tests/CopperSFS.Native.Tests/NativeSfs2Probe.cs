using Amiga;

namespace CopperSFS.Native.Tests;

// Compiled as a guest application. All filesystem calls go through the HUNK.
public static class NativeSfs2Probe
{
    public static int RunRead() => Start(0);
    public static int RunFull() => Start(1);
    public static int RunOverwrite() => Start(2);
    public static int RunTruncate() => Start(3);
    public static int RunRename() => Start(4);
    public static int RunDelete() => Start(5);
    public static int RunCreate() => Start(6);
    public static int RunComment() => Start(7);
    public static int RunWide() => Start(8);
    public static int RunRecover() => Start(9);
    public static int RunWideRecover() => Start(10);
    public static int RunArena() => Start(11);
    public static int RunVerifyFull() => Start(12);

    private static int Start(int mode)
    {
        var library = global::Amiga.Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) return 10;
        DOS.DOSLibraryBase = library.Value;
        APTR.WriteUInt8(APTR.FromPointer(0xBFE301), 0, 255);
        APTR.WriteUInt8(APTR.FromPointer(0xBFE101), 0, 101);
        var store = global::Amiga.Exec.AllocMem(mode == 11 ? 300000u : 16384u, global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear);
        var status = store.IsNull ? 11 : Mount(store, mode);
        APTR.WriteUInt8(APTR.FromPointer(0xBFE101), 0, (byte)status);
        return status;
    }

    private static int Mount(APTR store, int mode)
    {
        var segment = DOS.LoadSeg("SFSHANDLER"); if (!segment.HasValue) return 12;
        var node = DOS.MakeDosEntry("SFS2", 0); if (node.IsNull) return 13;
        var name = APTR.FromPointer(store.Raw + 16); var env = APTR.FromPointer(store.Raw + 40);
        var device = APTR.FromPointer(CString.ToUInt32("copperhdf.device"));
        APTR.WriteUInt8(name, 0, 16);
        for (var i = 0; i < 16; i++) APTR.WriteUInt8(name, i + 1, APTR.ReadUInt8(device, i));
        APTR.WriteUInt32(store, 4, name.Raw >> 2); APTR.WriteUInt32(store, 8, env.Raw >> 2);
        APTR.WriteUInt32(env, 0, 19); APTR.WriteUInt32(env, 4, 128); APTR.WriteUInt32(env, 12, 2);
        APTR.WriteUInt32(env, 16, 1); APTR.WriteUInt32(env, 20, 32); APTR.WriteUInt32(env, 24, mode is 8 or 10 ? 1u : 2u);
        APTR.WriteUInt32(env, 40, mode is 8 or 10 ? 0x11000000 / 64 - 1 : 511u);
        APTR.WriteUInt32(env, 44, 32); APTR.WriteUInt32(env, 52, 0x1FE00); APTR.WriteUInt32(env, 56, 0x7FFFFFFF);
        APTR.WriteUInt32(env, 64, 0x53465302);
        APTR.WriteUInt32(node, 20, 32768); APTR.WriteUInt32(node, 24, 10);
        APTR.WriteUInt32(node, 28, store.Raw >> 2); APTR.WriteUInt32(node, 32, segment.Value.Raw); APTR.WriteUInt32(node, 36, uint.MaxValue);
        if (DOS.AddDosEntry(node) == 0) return 14;
        var result = mode == 12 ? VerifyFull(store) : mode == 11 ? Arena(store) : mode is 9 or 10 ? Recover(store) : mode == 8 ? Wide(store) : mode == 1 ? Full(store) : mode > 1 ? Mutate(store, mode) : ReadBase(store);
        if (result != 0) return result;
        return DOS.Inhibit("SFS2:", 1) == 0 ? 15 : 42;
    }

    private static int Recover(APTR store)
    {
        var root = DOS.Lock("SFS2:", DOS.LockMode.Shared); if (!root.HasValue) return 64;
        var ok = DOS.Examine(root.Value, APTR.FromPointer(store.Raw + 512)); DOS.UnLock(root.Value); return ok == 0 ? 65 : 0;
    }

    private static int ReadBase(APTR store)
    {
        var data = APTR.FromPointer(store.Raw + 512);
        var file = DOS.Open("SFS2:Docs/Payload", DOS.FileMode.OldFile); if (!file.HasValue) return 16;
        if (DOS.Read(file.Value, data, 1301) != 1301 || DOS.Close(file.Value) == 0) return 17;
        for (var i = 0; i < 1301; i++) if (APTR.ReadUInt8(data, i) != (byte)(i * 17 + 3)) return 18;
        var locked = DOS.Lock("SFS2:Docs/Entry79", DOS.LockMode.Shared);
        if (!locked.HasValue) return 19;
        DOS.UnLock(locked.Value);
        locked = DOS.Lock("SFS2:Docs/Payload", DOS.LockMode.Shared); if (!locked.HasValue) return 20;
        var ok = DOS.Examine(locked.Value, data); DOS.UnLock(locked.Value);
        if (ok == 0 || APTR.ReadUInt32(data, 124) != 1301 || APTR.ReadUInt32(data, 132) != 1234) return 21;
        locked = DOS.Lock("SFS2:Docs", DOS.LockMode.Shared); if (!locked.HasValue) return 66;
        if (DOS.Examine(locked.Value, data) == 0) return 67;
        var count = 0;
        while (DOS.ExNext(locked.Value, data) != 0) count++;
        var end = DOS.IoErr(); DOS.UnLock(locked.Value);
        if (count != 81 || end != DOS.Error.NoMoreEntries) return 68;
        return 0;
    }

    private static int Arena(APTR store)
    {
        var file = DOS.Open("SFS2:NativeArena", DOS.FileMode.NewFile); if (!file.HasValue) return 70;
        var data = APTR.FromPointer(store.Raw + 512); var handle = APTR.FromPointer(file.Value.Raw << 2);
        if (DOS.Write(file.Value, data, 256 * 1024) != -1 || DOS.IoErr() != DOS.Error.NoFreeStore) return 71;
        if (Quad(handle, store, 26400, 0, 0, 0) == 0 || APTR.ReadUInt32(store, 264) != 0 || APTR.ReadUInt32(store, 268) != 0) return 72;
        for (var i = 0; i < 1024; i++) APTR.WriteUInt8(data, i, (byte)(i * 17 + 3));
        if (DOS.Write(file.Value, data, 1024) != 1024 || DOS.Seek(file.Value, 0, -1) != 1024 || DOS.Read(file.Value, data, 1024) != 1024) return 73;
        for (var i = 0; i < 1024; i++) if (APTR.ReadUInt8(data, i) != (byte)(i * 17 + 3)) return 74;
        return DOS.Close(file.Value) == 0 ? 75 : 0;
    }

    private static int Mutate(APTR store, int mode)
    {
        if (mode == 4) return DOS.Rename("SFS2:LengthProbe", "SFS2:Renamed") == 0 ? 22 : 0;
        if (mode == 5) return DOS.DeleteFile("SFS2:LengthProbe") == 0 ? 23 : 0;
        if (mode == 7) return DOS.SetComment("SFS2:Docs/Payload", "Native SFS2") == 0 ? 24 : 0;
        var file = DOS.Open(mode == 6 ? "SFS2:NativeNew" : "SFS2:Docs/Payload", mode == 6 ? DOS.FileMode.NewFile : DOS.FileMode.OldFile);
        if (!file.HasValue) return 25;
        var ok = true;
        if (mode == 2)
        {
            for (var i = 0; i < 7; i++) APTR.WriteUInt8(store, 512 + i, (byte)(i * 29 + 7));
            ok = DOS.Seek(file.Value, 510, -1) != -1 && DOS.Write(file.Value, APTR.FromPointer(store.Raw + 512), 7) == 7;
        }
        if (mode == 3) ok = DOS.SetFileSize(file.Value, 513, -1) == 513;
        var closed = DOS.Close(file.Value); return ok && closed != 0 ? 0 : 26;
    }

    private static int Full(APTR store)
    {
        var error = ReadBase(store); if (error != 0) return error;
        var data = APTR.FromPointer(store.Raw + 512);
        for (var i = 0; i < 8195; i++) APTR.WriteUInt8(data, i, (byte)(i * 29 + 7));
        var file = DOS.Open("SFS2:CopperWrite", DOS.FileMode.NewFile); if (!file.HasValue) return 27;
        if (DOS.Write(file.Value, data, 8195) != 8195 || DOS.Close(file.Value) == 0) return 28;
        var dir = DOS.CreateDir("SFS2:CopperDir"); if (!dir.HasValue) return 29; DOS.UnLock(dir.Value);
        file = DOS.Open("SFS2:CopperDir/BeforeRename", DOS.FileMode.NewFile); if (!file.HasValue) return 30;
        for (var i = 0; i < 1027; i++) APTR.WriteUInt8(data, i, (byte)(i * 17 + 11));
        if (DOS.Write(file.Value, data, 1027) != 1027 || DOS.Close(file.Value) == 0) return 31;
        if (DOS.Rename("SFS2:CopperDir/BeforeRename", "SFS2:CopperDir/Renamed") == 0 ||
            DOS.SetComment("SFS2:CopperDir/Renamed", "Written by CopperSFS2") == 0 ||
            DOS.SetProtection("SFS2:CopperDir/Renamed", 128) == 0) return 32;
        APTR.WriteUInt32(store, 256, 1234); APTR.WriteUInt32(store, 260, 5); APTR.WriteUInt32(store, 264, 100);
        if (DOS.SetFileDate("SFS2:CopperDir/Renamed", APTR.FromPointer(store.Raw + 256)) == 0) return 33;
        if (Mutate(store, 2) != 0) return 34;
        file = DOS.Open("SFS2:Docs/Payload", DOS.FileMode.OldFile); if (!file.HasValue) return 35;
        if (DOS.SetFileSize(file.Value, 1601, -1) != 1601 || DOS.Close(file.Value) == 0) return 36;
        if (DOS.DeleteFile("SFS2:LengthProbe") == 0) return 37;
        file = DOS.Open("SFS2:CopperWrite", DOS.FileMode.OldFile); if (!file.HasValue) return 38;
        if (DOS.Read(file.Value, data, 8195) != 8195 || DOS.Close(file.Value) == 0) return 39;
        for (var i = 0; i < 8195; i++) if (APTR.ReadUInt8(data, i) != (byte)(i * 29 + 7)) return 40;
        return 0;
    }

    private static int Quad(APTR file, APTR store, int action, uint high, uint low, int mode)
    {
        APTR.WriteUInt32(store, 256, high); APTR.WriteUInt32(store, 260, low);
        return DOS.DoPkt(APTR.FromPointer(APTR.ReadUInt32(file, 8)), action,
            unchecked((int)APTR.ReadUInt32(file, 36)), unchecked((int)(store.Raw + 256)), mode,
            unchecked((int)(store.Raw + 264)), 0);
    }

    private static int VerifyFull(APTR store)
    {
        var data = APTR.FromPointer(store.Raw + 512);
        var file = DOS.Open("SFS2:CopperWrite", DOS.FileMode.OldFile); if (!file.HasValue) return 76;
        if (DOS.Read(file.Value, data, 8195) != 8195 || DOS.Close(file.Value) == 0) return 77;
        for (var i = 0; i < 8195; i++) if (APTR.ReadUInt8(data, i) != (byte)(i * 29 + 7)) return 78;
        file = DOS.Open("SFS2:CopperDir/Renamed", DOS.FileMode.OldFile); if (!file.HasValue) return 79;
        if (DOS.Read(file.Value, data, 1027) != 1027 || DOS.Close(file.Value) == 0) return 80;
        for (var i = 0; i < 1027; i++) if (APTR.ReadUInt8(data, i) != (byte)(i * 17 + 11)) return 81;
        var locked = DOS.Lock("SFS2:CopperDir/Renamed", DOS.LockMode.Shared); if (!locked.HasValue) return 82;
        var ok = DOS.Examine(locked.Value, data); DOS.UnLock(locked.Value);
        if (ok == 0 || APTR.ReadUInt32(data, 116) != 128 || APTR.ReadUInt32(data, 132) != 1234 ||
            APTR.ReadUInt32(data, 136) != 5 || APTR.ReadUInt32(data, 140) != 100) return 83;
        var comment = APTR.FromPointer(CString.ToUInt32("Written by CopperSFS2"));
        for (var i = 0; i <= 21; i++) if (APTR.ReadUInt8(data, 144 + i) != APTR.ReadUInt8(comment, i)) return 84;
        file = DOS.Open("SFS2:Docs/Payload", DOS.FileMode.OldFile); if (!file.HasValue) return 85;
        if (DOS.Read(file.Value, data, 1601) != 1601 || DOS.Close(file.Value) == 0) return 86;
        for (var i = 0; i < 1601; i++)
        {
            var expected = i >= 510 && i < 517 ? (byte)((i - 510) * 29 + 7) : i < 1301 ? (byte)(i * 17 + 3) : (byte)0;
            if (APTR.ReadUInt8(data, i) != expected) return 87;
        }
        locked = DOS.Lock("SFS2:LengthProbe", DOS.LockMode.Shared);
        if (locked.HasValue || DOS.IoErr() != DOS.Error.ObjectNotFound) return 88;
        return 0;
    }

    private static int Wide(APTR store)
    {
        var opened = DOS.Open("SFS2:Wide", DOS.FileMode.OldFile); if (!opened.HasValue) return 50;
        var handle = APTR.FromPointer(opened.Value.Raw << 2); var data = APTR.FromPointer(store.Raw + 512);
        if (Quad(handle, store, 26400, 0, 0xFFFFFFFD, -1) == 0 || APTR.ReadUInt32(store, 264) != 0 || APTR.ReadUInt32(store, 268) != 0) return 51;
        for (var i = 0; i < 1027; i++) APTR.WriteUInt8(data, i, (byte)(i * 29 + 7));
        if (DOS.Write(opened.Value, data, 1027) != 1027) return 52;
        if (Quad(handle, store, 26400, 0, 0, 0) == 0 || APTR.ReadUInt32(store, 264) != 1 || APTR.ReadUInt32(store, 268) != 1024) return 53;
        if (DOS.Seek(opened.Value, 0, 0) != -1 || DOS.IoErr() != DOS.Error.SeekError) return 54;
        var other = DOS.Open("SFS2:Wide", DOS.FileMode.OldFile); if (!other.HasValue) return 55;
        var otherHandle = APTR.FromPointer(other.Value.Raw << 2);
        if (Quad(otherHandle, store, 26400, 0, 0xFFFFFFFD, -1) == 0 || DOS.Read(other.Value, data, 1027) != 1027) return 56;
        for (var i = 0; i < 1027; i++) if (APTR.ReadUInt8(data, i) != (byte)(i * 29 + 7)) return 57;
        var port = APTR.FromPointer(APTR.ReadUInt32(handle, 8)); var arg = unchecked((int)APTR.ReadUInt32(handle, 36));
        if (DOS.DoPkt(port, (int)DosPacketAction.ExamineFileHandle64, arg, unchecked((int)(data.Raw >> 2)), 0, 0, 0) == 0 ||
            APTR.ReadUInt32(data, FileInfoBlock.Size64Offset) != 1 || APTR.ReadUInt32(data, FileInfoBlock.Size64Offset + 4) != 1024 || APTR.ReadUInt32(data, 124) != 0) return 58;
        // Shared-position clamping must include the other handle's high word.
        if (Quad(handle, store, 26401, 1, 509, -1) == 0 || APTR.ReadUInt32(store, 264) != 1 || APTR.ReadUInt32(store, 268) != 1024) return 59;
        if (DOS.Close(other.Value) == 0 || Quad(handle, store, 26401, 1, 509, -1) == 0 ||
            APTR.ReadUInt32(store, 264) != 1 || APTR.ReadUInt32(store, 268) != 509) return 60;
        if (Quad(handle, store, 26400, 0, 0, 0) == 0 || APTR.ReadUInt32(store, 264) != 1 || APTR.ReadUInt32(store, 268) != 509) return 61;
        if (DOS.DoPkt(port, (int)DosPacketAction.QueryAttribute, (int)FileSystemQueryAttribute.MaxFileSize,
            unchecked((int)data.Raw), 8, 0, 0) == 0 || APTR.ReadUInt32(data, 0) != 65535 || APTR.ReadUInt32(data, 4) != uint.MaxValue) return 62;
        return DOS.Close(opened.Value) == 0 ? 63 : 0;
    }
}
