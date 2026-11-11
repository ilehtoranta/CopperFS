using Amiga;

namespace CopperFS.Kickstart.Tests;

public static class NotificationQualificationProbe
{
    public static int Ffs() => Run(0);
    public static int Sfs() => Run(1);
    public static int Sfs2() => Run(2);

    private static int Run(int kind)
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
        var request = APTR.FromPointer(control.Raw + 512);
        var name = kind == 0 ? CString.FromLiteral("Data/Payload") : CString.FromLiteral("Payload");
        APTR.WriteUInt32(request, DosLayout.NotifyRequest.FullName, CString.ToUInt32(name));
        var result = DOS.DoPkt(port, (int)DosPacketAction.AddNotify, unchecked((int)request.Raw), 0, 0, 0, 0);
        var error = DOS.IoErr();
        if (result != 0) DOS.DoPkt(port, (int)DosPacketAction.RemoveNotify, unchecked((int)request.Raw), 0, 0, 0, 0);
        APTR.WriteUInt32(control, 20, unchecked((uint)result));
        APTR.WriteUInt32(control, 24, unchecked((uint)error));
        CorrectnessProbe.Report(210);
        return 0;
    }
}
