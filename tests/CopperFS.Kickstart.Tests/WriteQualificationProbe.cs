using Amiga;

namespace CopperFS.Kickstart.Tests;

// Measured overwrite and append packets, followed by a separate cold-boot
// verifier. Both arms use the same payload and aligned guest buffer.
public static class WriteQualificationProbe
{
    public static int FfsOverwrite() => Run(0, false, false);
    public static int FfsAppend() => Run(0, true, false);
    public static int FfsVerifyOverwrite() => Run(0, false, true);
    public static int FfsVerifyAppend() => Run(0, true, true);
    public static int FfsBulkAppend() => Run(0, true, false, true);
    public static int FfsVerifyBulkAppend() => Run(0, true, true, true);
    public static int SfsOverwrite() => Run(1, false, false);
    public static int SfsAppend() => Run(1, true, false);
    public static int SfsVerifyOverwrite() => Run(1, false, true);
    public static int SfsVerifyAppend() => Run(1, true, true);
    public static int SfsBulkAppend() => Run(1, true, false, true);
    public static int SfsVerifyBulkAppend() => Run(1, true, true, true);
    public static int Sfs2Overwrite() => Run(2, false, false);
    public static int Sfs2Append() => Run(2, true, false);
    public static int Sfs2VerifyOverwrite() => Run(2, false, true);
    public static int Sfs2VerifyAppend() => Run(2, true, true);
    public static int Sfs2BulkAppend() => Run(2, true, false, true);
    public static int Sfs2VerifyBulkAppend() => Run(2, true, true, true);

    private static byte NewByte(int i) => (byte)(i * 23 + (i >> 8) * 11 + 7);
    private static byte OriginalByte(int i) => (byte)(i * 17 + (i >> 9) * 31 + (i >> 17) * 7 + 3);

    private static int Run(int kind, bool append, bool verify, bool bulk = false)
    {
        var library = global::Amiga.Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) { CorrectnessProbe.Report(250); return 10; }
        DOS.DOSLibraryBase = library.Value;
        var process = global::Amiga.Exec.FindTask(CString.FromPointer(0));
        APTR.WriteUInt32(process, 184, uint.MaxValue);
        var control = APTR.FromPointer(global::Amiga.Exec.AllocMem(1024,
            global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear));
        if (control.IsNull) { CorrectnessProbe.Report(250); return 11; }
        CorrectnessProbe.Report(200); CorrectnessProbe.Report((int)(control.Raw >> 24));
        CorrectnessProbe.Report((int)(control.Raw >> 16)); CorrectnessProbe.Report((int)(control.Raw >> 8));
        CorrectnessProbe.Report((int)control.Raw);
        var allocation = APTR.FromPointer(global::Amiga.Exec.AllocMem(65536 + 65535,
            global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Fast));
        if (allocation.IsNull) return CorrectnessProbe.Failed(control, 11);
        var data = APTR.FromPointer((allocation.Raw + 65535u) & 0xFFFF0000u);
        var mount = SequentialReadProbe.Mount(control, kind != 0, kind == 1);
        if (mount != 0) return CorrectnessProbe.Failed(control, (uint)mount);
        var file = kind == 0 ? DOS.Open("SEQ0:Data/Payload", verify ? DOS.FileMode.OldFile : DOS.FileMode.ReadWrite)
            : DOS.Open("SEQ0:Payload", verify ? DOS.FileMode.OldFile : DOS.FileMode.ReadWrite);
        if (!file.HasValue) return CorrectnessProbe.Failed(control, 20);
        if (append && DOS.Seek(file.Value, 1048576, -1) < 0) return CorrectnessProbe.Failed(control, 21);
        if (verify)
        {
            if (DOS.Read(file.Value, data, 65536) != 65536) return CorrectnessProbe.Failed(control, 22);
            for (var i = 0; i < 65536; i++)
                if (APTR.ReadUInt8(data, i) != NewByte(i)) return CorrectnessProbe.Failed(control, 23);
            if (DOS.Seek(file.Value, 131072, -1) < 0 || DOS.Read(file.Value, data, 4096) != 4096)
                return CorrectnessProbe.Failed(control, 24);
            for (var i = 0; i < 4096; i++)
                if (APTR.ReadUInt8(data, i) != OriginalByte(131072 + i)) return CorrectnessProbe.Failed(control, 25);
            if (DOS.Seek(file.Value, 0, 1) < 0 || DOS.Seek(file.Value, 0, 0) != (append ? 1114112 : 1048576))
                return CorrectnessProbe.Failed(control, 26);
            if (DOS.Close(file.Value) == 0) return CorrectnessProbe.Failed(control, 27);
        }
        else
        {
            for (var i = 0; i < 65536; i++) APTR.WriteUInt8(data, i, NewByte(i));
            // Optional phase sweep: change only the guest scheduling phase,
            // before the measured write, and apply the same delay to both arms.
            var delay = APTR.ReadUInt32(control, 400);
            for (var i = 0u; i < delay; i++) APTR.WriteUInt32(control, 404, i);
            CorrectnessProbe.Report(220);
            var packet = bulk ? 65536 : 4096;
            for (var offset = 0; offset < 65536; offset += packet)
                if (DOS.Write(file.Value, APTR.FromPointer(data.Raw + (uint)offset), packet) != packet)
                    return CorrectnessProbe.Failed(control, 28);
            if (DOS.Close(file.Value) == 0 || DOS.Inhibit("SEQ0:", 1) == 0)
                return CorrectnessProbe.Failed(control, 29);
            CorrectnessProbe.Report(221);
        }
        APTR.WriteUInt32(control, 20, data.Raw);
        CorrectnessProbe.Report(210);
        return 0;
    }
}
