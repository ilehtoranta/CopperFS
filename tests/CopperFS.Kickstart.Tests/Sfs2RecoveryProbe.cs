using Amiga;

namespace CopperFS.Kickstart.Tests;

public static class Sfs2RecoveryProbe
{
    public static int Overwrite() => Run(1);
    public static int Truncate() => Run(2);
    public static int Rename() => Run(3);
    public static int Delete() => Run(4);
    public static int Create() => Run(5);
    public static int Comment() => Run(6);
    public static int Recover() => Run(0);

    private static int Run(int operation)
    {
        var dos = global::Amiga.Exec.OpenLibrary("dos.library", 37);
        if (!dos.HasValue) { CorrectnessProbe.Report(250); return 1; }
        DOS.DOSLibraryBase = dos.Value;
        var control = APTR.FromPointer(global::Amiga.Exec.AllocMem(1024,
            global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear));
        if (control.IsNull || CorrectnessProbe.Mount(control, 3) != 0) { CorrectnessProbe.Report(250); return 2; }
        var root = DOS.Lock("SEQ0:", DOS.LockMode.Shared);
        if (!root.HasValue) { CorrectnessProbe.Report(250); return 3; }
        DOS.UnLock(root.Value);
        var ok = true;
        if (operation == 1 || operation == 2 || operation == 5)
        {
            var file = operation == 5 ? DOS.Open("SEQ0:Created", DOS.FileMode.NewFile) :
                DOS.Open("SEQ0:Payload", DOS.FileMode.OldFile);
            if (!file.HasValue) ok = false;
            else
            {
                if (operation == 1)
                {
                    var data = APTR.FromPointer(control.Raw + 400);
                    for (var i = 0; i < 513; i++) APTR.WriteUInt8(data, i, (byte)(i * 29 + 7));
                    ok = DOS.Write(file.Value, data, 513) == 513;
                }
                if (operation == 2) ok = DOS.SetFileSize(file.Value, 513, -1) == 513;
                if (DOS.Close(file.Value) == 0) ok = false;
            }
        }
        else if (operation == 3) ok = DOS.Rename("SEQ0:Payload", "SEQ0:Renamed") != 0;
        else if (operation == 4) ok = DOS.DeleteFile("SEQ0:Payload") != 0;
        else if (operation == 6) ok = DOS.SetComment("SEQ0:Payload", "recovery") != 0;
        if (!ok || DOS.Inhibit("SEQ0:", 1) == 0) { CorrectnessProbe.Report(250); return 4; }
        CorrectnessProbe.Report(210);
        return 0;
    }
}
