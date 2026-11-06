using Amiga;

namespace CopperFS.Kickstart.Tests;

// Identical DOS workload for original and Copper handlers. Guest code is compiled
// from C#; no test doubles, native patching or host filesystem dispatch is used.
public static class CorrectnessProbe
{
    internal static int Mount(APTR control, uint kind, bool floppy = false, bool unknown = false)
    {
        var format = kind % 10;
        var segment = DOS.LoadSeg("HANDLER");
        if (!segment.HasValue) return 12;
        var node = DOS.MakeDosEntry(CString.FromLiteral("SEQ0"), (int)DosListType.Device);
        if (node.IsNull) return 13;
        var startup = APTR.FromPointer(control.Raw + 64);
        var name = APTR.FromPointer(control.Raw + 96);
        var environment = APTR.FromPointer(control.Raw + 128);
        var device = floppy ? CString.FromLiteral("trackdisk.device") : CString.FromLiteral("copperhdf.device");
        if (unknown)
        {
            // Give the fixed disk an unrecognized driver name in this guest.
            var exec = APTR.ReadUInt32(APTR.FromPointer(4), 0);
            var driver = global::Amiga.Exec.FindName(APTR.FromPointer(exec + ExecLayout.ExecBase.DeviceList), device);
            if (driver.IsNull) return 15;
            device = CString.FromLiteral("opaquehdf.device");
            APTR.WriteUInt32(driver, ExecLayout.Node.Name, CString.ToUInt32(device));
        }
        APTR.WriteUInt8(name, 0, 16);
        for (var i = 0; i < 16; i++) APTR.WriteUInt8(name, i + 1, APTR.ReadUInt8(APTR.FromPointer(CString.ToUInt32(device)), i));
        APTR.WriteUInt32(startup, DosLayout.FileSysStartupMsg.Unit, floppy ? 1u : 0u);
        APTR.WriteUInt32(startup, DosLayout.FileSysStartupMsg.Device, name.Raw >> 2);
        APTR.WriteUInt32(startup, DosLayout.FileSysStartupMsg.Environment, environment.Raw >> 2);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.TableSize, 19);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.SizeBlock, 128);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.Surfaces, 2);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.SectorsPerBlock, 1);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.BlocksPerTrack, kind >= 20 ? 22u : kind >= 10 ? 11u : 128u);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.Reserved, format == 3 ? 1u : 2u);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.HighCylinder, kind >= 10 ? 79u : 127u);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.NumberOfBuffers, 32);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.BufferMemoryType, 1);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.MaximumTransfer, 65536);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.Mask, 0xFFFFFFFE);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.BootPriority, 0xFFFFFF7F);
        APTR.WriteUInt32(environment, DosLayout.DosEnvec.DosType, format == 0 ? 0x444F5300u : format == 1 ? 0x444F5301u : format == 2 ? 0x53465300u : 0x53465302u);
        APTR.WriteUInt32(node, DosLayout.DeviceNode.StackSize, 32768);
        APTR.WriteUInt32(node, DosLayout.DeviceNode.Priority, 10);
        APTR.WriteUInt32(node, DosLayout.DeviceNode.Startup, startup.Raw >> 2);
        APTR.WriteUInt32(node, DosLayout.DeviceNode.SegmentList, segment.Value.Raw);
        APTR.WriteUInt32(node, DosLayout.DeviceNode.GlobalVector, uint.MaxValue);
        return DOS.AddDosEntry(node) == 0 ? 14 : 0;
    }


    internal static void Report(int value)
    {
        APTR.WriteUInt8(APTR.FromPointer(0xBFE301), 0, 255);
        APTR.WriteUInt8(APTR.FromPointer(0xBFE101), 0, (byte)value);
    }
}
