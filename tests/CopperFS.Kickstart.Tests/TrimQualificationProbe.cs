using Amiga;

namespace CopperFS.Kickstart.Tests;

public static class TrimQualificationProbe
{
    public static int FfsQuery() => Run(0, 2);
    public static int FfsDryRun() => Run(0, 1);
    public static int FfsExecute() => Run(0, 0);
    public static int SfsQuery() => Run(1, 2);
    public static int SfsDryRun() => Run(1, 1);
    public static int SfsExecute() => Run(1, 0);
    public static int Sfs2Query() => Run(2, 2);
    public static int Sfs2DryRun() => Run(2, 1);
    public static int Sfs2Execute() => Run(2, 0);

    private static int Run(int kind, uint flags)
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
        var mount = SequentialReadProbe.Mount(control, kind != 0, kind == 1);
        if (mount != 0) return CorrectnessProbe.Failed(control, (uint)mount);
        var port = DOS.DeviceProc("SEQ0:");
        if (port.IsNull) return CorrectnessProbe.Failed(control, 20);
        var record = APTR.FromPointer(control.Raw + 512);
        APTR.WriteUInt32(record, 0, 32); APTR.WriteUInt32(record, 4, 1); APTR.WriteUInt32(record, 8, flags);
        var result = DOS.DoPkt(port, 0x43535452, unchecked((int)record.Raw), 0, 0, 0, 0);
        var error = DOS.IoErr();
        if (flags == 0 && result != 0)
        {
            var data = APTR.FromPointer(global::Amiga.Exec.AllocMem(4096,
                global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Fast));
            if (data.IsNull) return CorrectnessProbe.Failed(control, 21);
            var file = kind == 0 ? DOS.Open("SEQ0:Data/Payload", DOS.FileMode.OldFile)
                : DOS.Open("SEQ0:Payload", DOS.FileMode.OldFile);
            if (!file.HasValue) return CorrectnessProbe.Failed(control, 22);
            for (var at = 0; at < 1048576; at += 4096)
            {
                if (DOS.Read(file.Value, data, 4096) != 4096) return CorrectnessProbe.Failed(control, 23);
                for (var i = 0; i < 4096; i++)
                {
                    var n = at + i;
                    if (APTR.ReadUInt8(data, i) != (byte)(n * 17 + (n >> 9) * 31 + (n >> 17) * 7 + 3))
                        return CorrectnessProbe.Failed(control, 24);
                }
            }
            if (DOS.Close(file.Value) == 0) return CorrectnessProbe.Failed(control, 25);
            global::Amiga.Exec.FreeMem(data.Raw, 4096);
        }
        APTR.WriteUInt32(control, 20, unchecked((uint)result));
        APTR.WriteUInt32(control, 24, unchecked((uint)error));
        CorrectnessProbe.Report(210);
        return 0;
    }
}
