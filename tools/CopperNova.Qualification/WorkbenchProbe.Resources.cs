using Amiga;

public static partial class WorkbenchProbe
{
    public const int ResourceProbeCount = 96;
    private const uint ResourceProbePayload = 0x434e5036;

    public static int ResourcesEntry()
    {
        var dos = Exec.OpenLibraryRaw("dos.library", 37);
        if (dos.IsNull) return ResourceProbeResult(240);
        DOS.DOSLibraryBase = dos;
        APTR.WriteUInt32(Exec.FindTask(CString.FromPointer(0)), 184, uint.MaxValue);
        var port = DOS.DeviceProc("CNP0:");
        var storage = APTR.FromPointer(Exec.AllocMem(1024, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear));
        if (port.IsNull || storage.IsNull) return ResourceProbeResult(241);
        var data = APTR.FromPointer(storage.Raw + 800);
        APTR.WriteUInt32(data, 0, ResourceProbePayload);
        var file = DOS.Open("CNP0:held", DOS.FileMode.NewFile);
        if (!file.HasValue || DOS.Write(file.Value, data, 4) != 4 || DOS.Close(file.Value) == 0 ||
            DOS.DoPkt(port, 27, 0, 0, 0, 0, 0) == 0) return ResourceProbeResult(242);

        for (int i = 0; i < ResourceProbeCount; i++)
        {
            var opened = DOS.Open("CNP0:held", DOS.FileMode.OldFile);
            if (!opened.HasValue) return ResourceProbeResult(243);
            APTR.WriteUInt32(storage, i * 8, opened.Value.Raw);
            var locked = DOS.Lock("CNP0:held", (DOS.LockMode)(-2));
            if (!locked.HasValue) return ResourceProbeResult(244);
            APTR.WriteUInt32(storage, i * 8 + 4, locked.Value.Raw);
        }
        if (DOS.DeleteFile("CNP0:held") != 0 || (int)DOS.IoErr() != 202)
            return ResourceProbeResult(245);

        var first = BPTR.FromRaw(APTR.ReadUInt32(storage, 0));
        uint stale = APTR.ReadUInt32(APTR.FromPointer(first.Raw << 2), 36);
        if (DOS.Close(first) == 0) return ResourceProbeResult(246);
        var reopened = DOS.Open("CNP0:held", DOS.FileMode.OldFile);
        if (!reopened.HasValue ||
            DOS.DoPkt(port, 1007, unchecked((int)stale), 0, 0, 0, 0) != 0 ||
            (int)DOS.IoErr() != 211) return ResourceProbeResult(247);
        APTR.WriteUInt32(storage, 0, reopened.Value.Raw);
        for (int i = ResourceProbeCount - 1; i >= 0; i--)
        {
            var handle = BPTR.FromRaw(APTR.ReadUInt32(storage, i * 8));
            if (DOS.Read(handle, data, 4) != 4 || APTR.ReadUInt32(data, 0) != ResourceProbePayload ||
                DOS.Close(handle) == 0) return ResourceProbeResult(248);
            uint lockToken = APTR.ReadUInt32(storage, i * 8 + 4);
            DOS.UnLock(BPTR.FromRaw(lockToken));
            if (i == ResourceProbeCount - 1 &&
                (DOS.DoPkt(port, 15, unchecked((int)lockToken), 0, 0, 0, 0) != 0 || (int)DOS.IoErr() != 211))
                return ResourceProbeResult(249);
        }

        // Refuse only the native adapter's 48-byte public, cleared resource
        // allocations. DOS packet and file-handle setup can still allocate.
        var stub = APTR.FromPointer(storage.Raw + 832);
        var hitCount = APTR.FromPointer(storage.Raw + 864);
        var execBase = APTR.FromPointer(APTR.ReadUInt32(APTR.FromPointer(4), 0));
        APTR.WriteUInt32(stub, 0, 0x0c800000); // cmpi.l #48,d0
        APTR.WriteUInt32(stub, 4, 0x00306612); // bne.s original
        APTR.WriteUInt32(stub, 8, 0x0c810001); // cmpi.l #$10001,d1
        APTR.WriteUInt32(stub, 12, 0x0001660a); // bne.s original
        APTR.WriteUInt16(stub, 16, 0x52b9); // addq.l #1,hitCount
        APTR.WriteUInt32(stub, 18, hitCount.Raw);
        APTR.WriteUInt32(stub, 22, 0x70004e75); // moveq #0,d0 / rts
        APTR.WriteUInt16(stub, 26, 0x4ef9); // original: jmp old AllocMem
        Exec.Forbid();
        var original = Exec.SetFunction(execBase, ExecLvo.AllocMem, stub);
        APTR.WriteUInt32(stub, 28, original.Raw);
        Exec.Permit();

        // Check FINDOUTPUT directly so dos.library's wrapper cannot replace
        // the packet's secondary result while disposing of its FileHandle.
        int fileHandleBptr = (int)((storage.Raw + 896) >> 2);
        int heldBptr = (int)((storage.Raw + 960) >> 2);
        int missingBptr = (int)((storage.Raw + 976) >> 2);
        APTR.WriteUInt32(storage, 960, 0x0468656c); // 4, "hel"
        APTR.WriteUInt8(storage, 964, 100); // "d"
        APTR.WriteUInt32(storage, 976, 0x076d6973); // 7, "mis"
        APTR.WriteUInt32(storage, 980, 0x73696e67); // "sing"
        int refusedOpen = DOS.DoPkt(port, 1006, fileHandleBptr, 0, heldBptr, 0, 0);
        int openError = (int)DOS.IoErr();
        int refusedCreate = DOS.DoPkt(port, 1006, fileHandleBptr, 0, missingBptr, 0, 0);
        int createError = (int)DOS.IoErr();
        var refusedLock = DOS.Lock("CNP0:held", (DOS.LockMode)(-2));
        int lockError = (int)DOS.IoErr();
        var refusedDirectory = DOS.CreateDir("CNP0:missing-dir");
        int directoryError = (int)DOS.IoErr();
        Exec.SetFunction(execBase, ExecLvo.AllocMem, original);
        if (refusedOpen != 0 || openError != 103 || refusedCreate != 0 || createError != 103)
            return ResourceProbeResult(250);
        if (refusedLock.HasValue || lockError != 103 || refusedDirectory.HasValue || directoryError != 103)
            return ResourceProbeResult(251);
        if (APTR.ReadUInt32(hitCount, 0) != 4) return ResourceProbeResult(252);

        var missing = DOS.Lock("CNP0:missing", (DOS.LockMode)(-2));
        if (missing.HasValue || (int)DOS.IoErr() != 205) return ResourceProbeResult(253);
        missing = DOS.Lock("CNP0:missing-dir", (DOS.LockMode)(-2));
        if (missing.HasValue || (int)DOS.IoErr() != 205) return ResourceProbeResult(253);
        file = DOS.Open("CNP0:held", DOS.FileMode.OldFile);
        if (!file.HasValue || DOS.Read(file.Value, data, 4) != 4 || APTR.ReadUInt32(data, 0) != ResourceProbePayload ||
            DOS.Close(file.Value) == 0 || DOS.DoPkt(port, 27, 0, 0, 0, 0, 0) == 0)
            return ResourceProbeResult(254);
        Exec.FreeMem(storage.Raw, 1024);
        Exec.CloseLibrary(dos);
        return ResourceProbeResult(210);
    }

    private static int ResourceProbeResult(byte marker)
    {
        Mark(marker);
        return marker == 210 ? 0 : 20;
    }
}
