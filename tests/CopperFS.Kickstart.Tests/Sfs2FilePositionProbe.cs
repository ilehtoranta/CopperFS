using Amiga;

namespace CopperFS.Kickstart.Tests;

// DOS packet sequences from CopperSFS research commit 4a902a1.
public static class Sfs2FilePositionProbe
{
    private const int Seek64 = 26400, SetFileSize64 = 26401;
    public static int TestWide() => Start(1);
    public static int ReopenWide() => Start(2);
    public static int TestArena() => Start(3);

    private static int Start(int mode)
    {
        var dos = global::Amiga.Exec.OpenLibrary("dos.library", 37);
        if (!dos.HasValue) { CorrectnessProbe.Report(250); return 1; }
        DOS.DOSLibraryBase = dos.Value;
        var store = APTR.FromPointer(global::Amiga.Exec.AllocMem(mode == 3 ? 263168u : 4096u,
            global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear));
        if (store.IsNull) { CorrectnessProbe.Report(250); return 2; }
        CorrectnessProbe.Report(200);
        CorrectnessProbe.Report((int)(store.Raw >> 24)); CorrectnessProbe.Report((int)(store.Raw >> 16));
        CorrectnessProbe.Report((int)(store.Raw >> 8)); CorrectnessProbe.Report((int)store.Raw);
        var result = CorrectnessProbe.Mount(store, 3, false, false, mode == 3 ? 127u : NativeWideFixture.Blocks / 256 - 1);
        if (result == 0) result = mode == 1 ? Wide(store) : mode == 2 ? VerifyWide(store) : Arena(store);
        if (result != 0) return CorrectnessProbe.Failed(store, (uint)result);
        if (DOS.Inhibit("SEQ0:", 1) == 0) return CorrectnessProbe.Failed(store, 90);
        CorrectnessProbe.Report(210);
        return 0;
    }

    private static int VerifyWide(APTR store)
    {
        var opened = DOS.Open("SEQ0:Wide", DOS.FileMode.OldFile);
        if (!opened.HasValue) return 80;
        var handle = APTR.FromPointer(opened.Value.Raw << 2);
        var data = APTR.FromPointer(store.Raw + 512);
        if (Quad(handle, store, Seek64, 0, 0xFFFFFFFD, -1) == 0 || DOS.Read(opened.Value, data, 513) != 512) return 81;
        for (var i = 0; i < 512; i++) if (APTR.ReadUInt8(data, i) != (byte)(i * 29 + 7)) return 82;
        return DOS.Close(opened.Value) == 0 ? 83 : 0;
    }
    private static int Quad(APTR file, APTR store, int action, uint high, uint low, int mode)
    {
        APTR.WriteUInt32(store, 256, high); APTR.WriteUInt32(store, 260, low);
        return DOS.DoPkt(APTR.FromPointer(APTR.ReadUInt32(file, 8)), action,
            unchecked((int)APTR.ReadUInt32(file, 36)), unchecked((int)(store.Raw + 256)), mode,
            unchecked((int)(store.Raw + 264)), 0);
    }
    private static int Arena(APTR store)
    {
        var file = DOS.Open("SEQ0:NativeArena", DOS.FileMode.NewFile); if (!file.HasValue) return 70;
        var data = APTR.FromPointer(store.Raw + 512); var handle = APTR.FromPointer(file.Value.Raw << 2);
        // A nonzero position distinguishes preserving the cursor from resetting it.
        for (var i = 0; i < 17; i++) APTR.WriteUInt8(data, i, (byte)(i * 17 + 3));
        if (DOS.Write(file.Value, data, 17) != 17) return 69;
        if (DOS.Write(file.Value, data, 256 * 1024) != -1 || DOS.IoErr() != DOS.Error.NoFreeStore) return 71;
        if (Quad(handle, store, Seek64, 0, 0, 0) == 0 || APTR.ReadUInt32(store, 264) != 0 || APTR.ReadUInt32(store, 268) != 17) return 72;
        for (var i = 0; i < 1024; i++) APTR.WriteUInt8(data, i, (byte)((17 + i) * 17 + 3));
        if (DOS.Write(file.Value, data, 1024) != 1024 || DOS.Seek(file.Value, 0, -1) != 1041 ||
            DOS.Read(file.Value, data, 1042) != 1041) return 73;
        for (var i = 0; i < 1041; i++) if (APTR.ReadUInt8(data, i) != (byte)(i * 17 + 3)) return 74;
        return DOS.Close(file.Value) == 0 ? 75 : 0;
    }
    private static int Wide(APTR store)
    {
        var opened = DOS.Open("SEQ0:Wide", DOS.FileMode.OldFile); if (!opened.HasValue) return 50;
        var handle = APTR.FromPointer(opened.Value.Raw << 2); var data = APTR.FromPointer(store.Raw + 512);
        if (Quad(handle, store, Seek64, 0, 0xFFFFFFFD, -1) == 0 || APTR.ReadUInt32(store, 264) != 0 || APTR.ReadUInt32(store, 268) != 0) return 51;
        for (var i = 0; i < 1027; i++) APTR.WriteUInt8(data, i, (byte)(i * 29 + 7));
        if (DOS.Write(opened.Value, data, 1027) != 1027) return 52;
        if (Quad(handle, store, Seek64, 0, 0, 0) == 0 || APTR.ReadUInt32(store, 264) != 1 || APTR.ReadUInt32(store, 268) != 1024) return 53;
        if (DOS.Seek(opened.Value, 0, 0) != -1 || DOS.IoErr() != DOS.Error.SeekError) return 54;
        var other = DOS.Open("SEQ0:Wide", DOS.FileMode.OldFile); if (!other.HasValue) return 55;
        var otherHandle = APTR.FromPointer(other.Value.Raw << 2);
        if (Quad(otherHandle, store, Seek64, 0, 0xFFFFFFFD, -1) == 0 || DOS.Read(other.Value, data, 1027) != 1027) return 56;
        for (var i = 0; i < 1027; i++) if (APTR.ReadUInt8(data, i) != (byte)(i * 29 + 7)) return 57;
        var port = APTR.FromPointer(APTR.ReadUInt32(handle, 8)); var arg = unchecked((int)APTR.ReadUInt32(handle, 36));
        if (DOS.DoPkt(port, (int)DosPacketAction.ExamineFileHandle64, arg, unchecked((int)(data.Raw >> 2)), 0, 0, 0) == 0 ||
            APTR.ReadUInt32(data, FileInfoBlock.Size64Offset) != 1 || APTR.ReadUInt32(data, FileInfoBlock.Size64Offset + 4) != 1024 || APTR.ReadUInt32(data, 124) != 0) return 58;
        // Shared-position clamping must include the other handle's high word.
        if (Quad(handle, store, SetFileSize64, 1, 509, -1) == 0 || APTR.ReadUInt32(store, 264) != 1 || APTR.ReadUInt32(store, 268) != 1024) return 59;
        if (DOS.Close(other.Value) == 0 || Quad(handle, store, SetFileSize64, 1, 509, -1) == 0 ||
            APTR.ReadUInt32(store, 264) != 1 || APTR.ReadUInt32(store, 268) != 509) return 60;
        if (Quad(handle, store, Seek64, 0, 0, 0) == 0 || APTR.ReadUInt32(store, 264) != 1 || APTR.ReadUInt32(store, 268) != 509) return 61;
        if (DOS.DoPkt(port, (int)DosPacketAction.QueryAttribute, (int)FileSystemQueryAttribute.MaxFileSize,
            unchecked((int)data.Raw), 8, 0, 0) == 0 || APTR.ReadUInt32(data, 0) != 65535 || APTR.ReadUInt32(data, 4) != uint.MaxValue) return 62;
        return DOS.Close(opened.Value) == 0 ? 63 : 0;
    }
}
