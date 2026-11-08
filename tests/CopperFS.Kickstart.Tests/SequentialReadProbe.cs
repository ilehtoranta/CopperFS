using Amiga;

namespace CopperFS.Kickstart.Tests;

// The mount sequence follows CopperStart's MIT-licensed NativeSfsAbProbe,
// using published SDK structure offsets. This probe runs through real guest
// Kickstart Exec/DOS and the loaded filesystem handler, without service doubles.
public static class SequentialReadProbe
{
    public static int RunFfs() => Run(false);
    public static int RunSfs2() => Run(true);

    private static int Run(bool sfs2)
    {
        var library = global::Amiga.Exec.OpenLibrary("dos.library", 37);
        if (!library.HasValue) return 10;
        DOS.DOSLibraryBase = library.Value;
        // A batch probe must report DOS errors rather than wait for a desktop
        // requester. pr_WindowPtr=-1 is the native per-process mechanism.
        var process=global::Amiga.Exec.FindTask(CString.FromPointer(0));
        APTR.WriteUInt32(process,184,uint.MaxValue);
        var control = APTR.FromPointer(global::Amiga.Exec.AllocMem(512,
            global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear));
        if (control.IsNull) { Report(250); return 11; }
        Report(200); Report((int)(control.Raw >> 24)); Report((int)(control.Raw >> 16));
        Report((int)(control.Raw >> 8)); Report((int)control.Raw);
        var memory = APTR.ReadUInt32(control, 32);
        var data = APTR.FromPointer(global::Amiga.Exec.AllocMem(1048576,
            global::Amiga.Exec.MemoryFlags.Public | (memory == 2 ? global::Amiga.Exec.MemoryFlags.Chip : global::Amiga.Exec.MemoryFlags.Fast)));
        if (data.IsNull) return Failed(control, 11);
        // Capacity probing is a separate diagnostic, outside acceptance runs.
        if (APTR.ReadUInt32(control, 292) != 0 && !CaptureGeometry(control)) return Failed(control, 26);
        var outcome = Mount(control, sfs2);
        if (outcome != 0) { APTR.WriteUInt32(control, 20, (uint)outcome); Report(250); return outcome; }
        var measureMount = APTR.ReadUInt32(control, 296) != 0;
        if (measureMount)
        {
            if (DOS.DeviceProc("SEQ0:").IsNull) return Failed(control, 27);
            Report(206);
            var first = sfs2 ? DOS.Open("SEQ0:Payload", DOS.FileMode.OldFile)
                : DOS.Open("SEQ0:Data/Payload", DOS.FileMode.OldFile);
            Report(207);
            if (!first.HasValue || DOS.Close(first.Value) == 0) return Failed(control, 28);
        }
        for (var trial = 0u; trial < 3; trial++)
        for (var workload = 0u; workload < 4; workload++)
        {
            var packet = workload == 0 ? 64u : workload == 1 ? 512u : workload == 2 ? 4096u : 65536u;
            var selected = APTR.ReadUInt32(control, 44);
            if (selected != 0 && (selected != packet || trial != 0)) continue;
            var length = workload == 0 ? 65536u : workload == 1 ? 262144u : 1048576u;
            APTR.WriteUInt32(control, 0, packet); APTR.WriteUInt32(control, 4, length);
            APTR.WriteUInt32(control, 8, selected == 0 ? trial : APTR.ReadUInt32(control, 48)); APTR.WriteUInt32(control, 12, data.Raw);
            // A selected workload owns a fresh machine. Inhibit would race an
            // original SFS startup I/O task while redundantly remounting it.
            if (selected == 0)
            {
                if (DOS.Inhibit("SEQ0:", 1) == 0) return Failed(control, 20);
                if (measureMount) Report(208);
                if (DOS.Inhibit("SEQ0:", 0) == 0) return Failed(control, 20);
                if (measureMount) Report(209);
            }
            if (APTR.ReadUInt32(control, 52) != 0)
            {
                var prior = sfs2 ? DOS.Open("SEQ0:Payload", DOS.FileMode.OldFile)
                    : DOS.Open("SEQ0:Data/Payload", DOS.FileMode.OldFile);
                if (measureMount) Report(211);
                if (!prior.HasValue || DOS.Close(prior.Value) == 0) return Failed(control, 25);
            }
            Report(201); // Entire open/read/close session starts.
            var file = sfs2 ? DOS.Open("SEQ0:Payload", DOS.FileMode.OldFile)
                : DOS.Open("SEQ0:Data/Payload", DOS.FileMode.OldFile);
            if (!file.HasValue) return Failed(control, 21);
            Report(202); // Read loop starts after open.
            for (var position = 0u; position < length; position += packet)
                if (DOS.Read(file.Value, APTR.FromPointer(data.Raw + position), (int)packet) != (int)packet)
                    return Failed(control, 22);
            Report(203); // The host hashes all returned bytes outside guest time.
            if (DOS.Close(file.Value) == 0) return Failed(control, 23);
            Report(204); // Open/read/close session ends.
        }
        Report(210);
        return 0;
    }

    private static int Mount(APTR control, bool sfs2)
    {
        var segment = DOS.LoadSeg("HANDLER");
        if (!segment.HasValue) return 12;
        var node = DOS.MakeDosEntry(CString.FromLiteral("SEQ0"), (int)DosListType.Device);
        if (node.IsNull) return 13;
        var startup = APTR.FromPointer(control.Raw + 64);
        var name = APTR.FromPointer(control.Raw + 96);
        var environment = APTR.FromPointer(control.Raw + 128);
        var ide = APTR.ReadUInt32(control, 36) != 0;
        var device = ide ? CString.FromLiteral("scsi.device") : CString.FromLiteral("copperhdf.device");
        var deviceLength = ide ? 11 : 16;
        APTR.WriteUInt8(name, 0, (byte)deviceLength);
        for (var i = 0; i < deviceLength; i++) APTR.WriteUInt8(name, i + 1, APTR.ReadUInt8(APTR.FromPointer(CString.ToUInt32(device)), i));
        APTR.WriteUInt32(startup, DosLayout.FileSysStartupMsg.Unit, APTR.ReadUInt32(control, 40));
        APTR.WriteUInt32(startup, DosLayout.FileSysStartupMsg.Device, name.Raw >> 2);
        APTR.WriteUInt32(startup, DosLayout.FileSysStartupMsg.Environment, environment.Raw >> 2);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.TableSize, 19);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.SizeBlock, 128);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.Surfaces, 2);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.SectorsPerBlock, 1);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.BlocksPerTrack, 128);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.Reserved, sfs2 ? 1u : 2u);
        var lowCylinder=APTR.ReadUInt32(control,288);
        // LowCylinder=0 selects original SFS's whole-device capacity path.
        // IDE partition fixtures reserve a cylinder and supply its real range.
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.LowCylinder, lowCylinder);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.HighCylinder, 127+lowCylinder);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.NumberOfBuffers, 32);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.BufferMemoryType, 1);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.MaximumTransfer, 65536);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.Mask, 0xFFFFFFFE);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.BootPriority, 0xFFFFFF7F);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.DosType, sfs2 ? 0x53465302u : 0x444F5301u);
        // The host selects an explicit handler policy before Mount runs. The
        // same probe image is used for cached/direct and original-handler arms.
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.Control, APTR.ReadUInt32(control, 28));
        APTR.WriteUInt32(node, DosLayout.DeviceNode.StackSize, 32768);
        APTR.WriteUInt32(node, DosLayout.DeviceNode.Priority, 10);
        APTR.WriteUInt32(node, DosLayout.DeviceNode.Startup, startup.Raw >> 2);
        APTR.WriteUInt32(node, DosLayout.DeviceNode.SegmentList, segment.Value.Raw);
        APTR.WriteUInt32(node, DosLayout.DeviceNode.GlobalVector, uint.MaxValue);
        if (APTR.ReadUInt32(control, 296) != 0) Report(205);
        return DOS.AddDosEntry(node) == 0 ? 14 : 0;
    }

    private static bool CaptureGeometry(APTR control)
    {
        var port=global::Amiga.Exec.CreateMsgPort();
        if (port.IsNull) return false;
        var request=global::Amiga.Exec.CreateIORequest(port,48);
        if (request.IsNull) { global::Amiga.Exec.DeleteMsgPort(port); return false; }
        var opened=global::Amiga.Exec.OpenDevice(CString.FromLiteral("scsi.device"),APTR.ReadUInt32(control,40),request,0)==0;
        if (opened)
        {
            APTR.WriteUInt16(request,28,22);
            APTR.WriteUInt32(request,36,32);
            APTR.WriteUInt32(request,40,control.Raw+256);
            global::Amiga.Exec.DoIO(request);
            APTR.WriteUInt32(control,56,APTR.ReadUInt8(request,31));
            APTR.WriteUInt32(control,60,APTR.ReadUInt32(control,260));
            global::Amiga.Exec.CloseDevice(request);
        }
        global::Amiga.Exec.DeleteIORequest(request);
        global::Amiga.Exec.DeleteMsgPort(port);
        return opened;
    }

    private static int Failed(APTR control, uint stage)
    {
        APTR.WriteUInt32(control, 20, stage); APTR.WriteUInt32(control, 24, (uint)DOS.IoErr());
        Report(250); return (int)stage;
    }

    private static void Report(int value)
    {
        APTR.WriteUInt8(APTR.FromPointer(0xBFE301), 0, 255);
        APTR.WriteUInt8(APTR.FromPointer(0xBFE101), 0, (byte)value);
    }
}
