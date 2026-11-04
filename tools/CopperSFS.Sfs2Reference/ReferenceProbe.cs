using Amiga;

namespace CopperSFS.Sfs2Reference;

/// <summary>Runs inside native AmigaDOS with an independently supplied original handler.</summary>
public static class ReferenceProbe
{
    public static int Run() => Start(true);
    public static int RunVerify() => Start(false);
    private static int Start(bool format)
    {
        var library = global::Amiga.Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) return 10;
        DOS.DOSLibraryBase = library.Value;
        APTR.WriteUInt8(APTR.FromPointer(0xBFE301), 0, 255);
        APTR.WriteUInt8(APTR.FromPointer(0xBFE101), 0, 101);
        var store = APTR.FromPointer(global::Amiga.Exec.AllocMem(16384,
            global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear));
        var status = store.IsNull ? 11 : Create(store, format);
        APTR.WriteUInt8(APTR.FromPointer(0xBFE301), 0, 255);
        APTR.WriteUInt8(APTR.FromPointer(0xBFE101), 0, (byte)status);
        return status;
    }

    private static int Create(APTR store, bool format)
    {
        var segment = DOS.LoadSeg("SFSHANDLER");
        if (!segment.HasValue) return 12;
        var node = DOS.MakeDosEntry("SFS2", 0);
        if (node.IsNull) return 13;
        var name = APTR.FromPointer(store.Raw + 16);
        var environment = APTR.FromPointer(store.Raw + 40);
        var device = APTR.FromPointer(CString.ToUInt32("copperhdf.device"));
        APTR.WriteUInt8(name, 0, 16);
        for (var i = 0; i < 16; i++) APTR.WriteUInt8(name, i + 1, APTR.ReadUInt8(device, i));
        APTR.WriteUInt32(store, 4, name.Raw >> 2);
        APTR.WriteUInt32(store, 8, environment.Raw >> 2);
        APTR.WriteUInt32(environment, 0, 19);
        APTR.WriteUInt32(environment, 4, 128);
        APTR.WriteUInt32(environment, 12, 2);
        APTR.WriteUInt32(environment, 16, 1);
        APTR.WriteUInt32(environment, 20, 32);
        APTR.WriteUInt32(environment, 24, 2);
        APTR.WriteUInt32(environment, 40, 511);
        APTR.WriteUInt32(environment, 44, 32);
        APTR.WriteUInt32(environment, 52, 0x1FE00);
        APTR.WriteUInt32(environment, 56, 0x7FFFFFFF);
        APTR.WriteUInt32(environment, 64, 0x53465302);
        APTR.WriteUInt32(node, 20, 32768); // DeviceNode stack size
        APTR.WriteUInt32(node, 24, 10);
        APTR.WriteUInt32(node, 28, store.Raw >> 2);
        APTR.WriteUInt32(node, 32, segment.Value.Raw);
        APTR.WriteUInt32(node, 36, uint.MaxValue);
        if (DOS.AddDosEntry(node) == 0) return 14;
        if (!format) return Verify(store);
        if (DOS.Inhibit("SFS2:", 1) == 0) return 23;
        if (DOS.Format("SFS2:", "Sfs2Reference", 0x53465302) == 0) return 15;
        if (DOS.Inhibit("SFS2:", 0) == 0) return 24;
        var directory = DOS.CreateDir("SFS2:Docs");
        if (!directory.HasValue) return 16;
        DOS.UnLock(directory.Value);
        var data = APTR.FromPointer(store.Raw + 512);
        for (var i = 0; i < 1301; i++) APTR.WriteUInt8(data, i, (byte)(i * 17 + 3));
        var file = DOS.Open("SFS2:Docs/Payload", DOS.FileMode.NewFile);
        if (!file.HasValue) return 17;
        var written = DOS.Write(file.Value, data, 1301);
        var closed = DOS.Close(file.Value);
        if (written != 1301 || closed == 0) return 18;
        if (DOS.SetComment("SFS2:Docs/Payload", "Original SFS 1.279") == 0) return 19;
        // An asymmetric size exercises all six size bytes rather than just zero/one.
        file = DOS.Open("SFS2:LengthProbe", DOS.FileMode.NewFile);
        if (!file.HasValue) return 20;
        if (DOS.Write(file.Value, data, 513) != 513 || DOS.Close(file.Value) == 0) return 21;
        // Expand both the packed directory chain and object-node tree.
        var dynamicName = APTR.FromPointer(store.Raw + 4096);
        var template = APTR.FromPointer(CString.ToUInt32("SFS2:Docs/Entry00"));
        for (var i = 0; i <= 17; i++) APTR.WriteUInt8(dynamicName, i, APTR.ReadUInt8(template, i));
        for (var i = 0; i < 80; i++)
        {
            APTR.WriteUInt8(dynamicName, 15, (byte)('0' + i / 10));
            APTR.WriteUInt8(dynamicName, 16, (byte)('0' + i % 10));
            file = DOS.Open(CString.FromPointer(dynamicName.Raw), DOS.FileMode.NewFile);
            if (!file.HasValue) return 25;
            if (DOS.Close(file.Value) == 0) return 26;
        }
        var stamp = APTR.FromPointer(store.Raw + 256);
        APTR.WriteUInt32(stamp, 0, 1234); APTR.WriteUInt32(stamp, 4, 56); APTR.WriteUInt32(stamp, 8, 100);
        if (DOS.SetFileDate("SFS2:Docs/Payload", stamp) == 0 || DOS.SetProtection("SFS2:Docs/Payload", 128) == 0) return 27;
        if (DOS.Inhibit("SFS2:", 1) == 0) return 22;
        return 42;
    }

    private static int Verify(APTR store)
    {
        var data = APTR.FromPointer(store.Raw + 512);
        var file = DOS.Open("SFS2:CopperWrite", DOS.FileMode.OldFile);
        if (!file.HasValue) return 30;
        if (DOS.Read(file.Value, data, 8195) != 8195 || DOS.Close(file.Value) == 0) return 31;
        for (var i = 0; i < 8195; i++) if (APTR.ReadUInt8(data, i) != (byte)(i * 29 + 7)) return 32;
        file = DOS.Open("SFS2:CopperDir/Renamed", DOS.FileMode.OldFile);
        if (!file.HasValue) return 33;
        if (DOS.Read(file.Value, data, 1027) != 1027 || DOS.Close(file.Value) == 0) return 34;
        for (var i = 0; i < 1027; i++) if (APTR.ReadUInt8(data, i) != (byte)(i * 17 + 11)) return 35;
        var locked = DOS.Lock("SFS2:CopperDir/Renamed", DOS.LockMode.Shared);
        if (!locked.HasValue) return 36;
        var fib = APTR.FromPointer(store.Raw + 512);
        var examined = DOS.Examine(locked.Value, fib); DOS.UnLock(locked.Value);
        if (examined == 0 || APTR.ReadUInt32(fib, 116) != 128 || APTR.ReadUInt32(fib, 132) != 1234 ||
            APTR.ReadUInt32(fib, 136) != 5 || APTR.ReadUInt32(fib, 140) != 100) return 37;
        var comment = APTR.FromPointer(CString.ToUInt32("Written by CopperSFS2"));
        // dos.library converts the handler's packet BSTR to a C string.
        for (var i = 0; i <= 21; i++) if (APTR.ReadUInt8(fib, 144 + i) != APTR.ReadUInt8(comment, i)) return 39;
        file = DOS.Open("SFS2:Docs/Payload", DOS.FileMode.OldFile);
        if (!file.HasValue) return 40;
        if (DOS.Read(file.Value, data, 1601) != 1601 || DOS.Close(file.Value) == 0) return 41;
        for (var i = 0; i < 1601; i++)
        {
            var expected = i >= 510 && i < 517 ? (byte)((i - 510) * 29 + 7) : i < 1301 ? (byte)(i * 17 + 3) : (byte)0;
            if (APTR.ReadUInt8(data, i) != expected) return 43;
        }
        file = DOS.Open("SFS2:LengthProbe", DOS.FileMode.OldFile);
        if (file.HasValue || DOS.IoErr() != DOS.Error.ObjectNotFound) return 44;
        file = DOS.Open("SFS2:Docs/Entry79", DOS.FileMode.OldFile);
        if (!file.HasValue || DOS.Close(file.Value) == 0) return 45;
        return DOS.Inhibit("SFS2:", 1) != 0 ? 42 : 46;
    }
}
