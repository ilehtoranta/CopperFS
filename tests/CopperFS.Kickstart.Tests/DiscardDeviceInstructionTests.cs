using Amiga;
using Copper68k;
using CopperSFS;
using CopperStart.Dos;

namespace CopperFS.Kickstart.Tests;

public sealed partial class AutomaticTrimInstructionTests
{
    [Theory]
    [InlineData(M68kCpuModel.M68000)] [InlineData(M68kCpuModel.M68020)]
    public void TrackdiskAndRamRejectTrimWithoutDiscoveryOrAllocation(M68kCpuModel model)
    {
        var bus = Run(model, typeof(DiscardDeviceProbe));
        Assert.Equal(0, bus.Commands);
        Assert.Equal(0, bus.Allocations);
    }

    [Theory]
    [InlineData(M68kCpuModel.M68000)] [InlineData(M68kCpuModel.M68020)]
    public void OtherDevicesStillRequireScsiDiscovery(M68kCpuModel model)
    {
        var bus = Run(model, typeof(DiscardUnsupportedDeviceProbe));
        Assert.Equal(1, bus.Commands);
        Assert.Equal(0, bus.Allocations);
    }
}

public static class DiscardDeviceProbe
{
    internal static void Attach(APTR state, CString name)
    {
        var request = APTR.FromPointer(0x7000);
        var device = APTR.FromPointer(0x7200);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.RequestOffset, request.Raw);
        APTR.WriteUInt32(request, ExecLayout.IORequest.Device, device.Raw);
        APTR.WriteUInt32(device, ExecLayout.Node.Name, APTR.FromPointer(name).Raw);
    }

    public static uint Entry()
    {
        var state = APTR.FromPointer(0x3000);
        // Manual packet buffers are legitimate public memory. Only the discard
        // path is under test; no filesystem mount or allocation map is needed
        // for an explicitly ineligible device.
        Attach(state, "trackdisk.device");
        if (!AmigaVolumeGeometry.Create(1760, 512, 2, out var geometry)) return 1;
        NativeAmigaFileSystem.SetGeometry(state, geometry);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.DosTypeOffset, 0x444F5301);
        var p = new NativeFileSystemPlatform(state);
        if (NativeAmigaFileSystem.CanQueryDiscard(state)) return 2;
        if (!Rejected(state, ref p)) return 3;

        // The opened device's identity wins over any mount name/geometry.
        // Other names are admitted to discovery, not assumed to support UNMAP.
        Attach(state, "scsi.device");
        if (!NativeAmigaFileSystem.CanQueryDiscard(state)) return 4;
        Attach(state, "copperhdf.device");
        if (!NativeAmigaFileSystem.CanQueryDiscard(state)) return 5;
        Attach(state, "trackdisk.device.other");
        if (!NativeAmigaFileSystem.CanQueryDiscard(state)) return 6;
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.RamBlocksOffset, 0xB000);
        if (!Rejected(state, ref p)) return 7;
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.RamBlocksOffset, 0);
        APTR.WriteUInt32(state, NativeFileSystemStateCodec.RequestOffset, 0);
        if (!Rejected(state, ref p)) return 8;
        return 0;
    }

    private static bool Rejected(APTR state, ref NativeFileSystemPlatform p)
    {
        var control = APTR.FromPointer(0xA000);
        // None of these may touch a stale backend control record or issue I/O.
        if (p.QueryDiscard(control, out var limits) || limits.Valid ||
            p.DiscardBlocks(control, 100, 32) || p.DiscardRanges(control, APTR.Null, 1)) return false;
        var record = APTR.FromPointer(0xB000);
        APTR.WriteUInt32(record, 0, AmigaSmartFileSystemTrimPacket.Size);
        APTR.WriteUInt32(record, 4, AmigaSmartFileSystemTrimPacket.Version);
        for (var flags = 0u; flags <= AmigaSmartFileSystemTrimPacket.QueryOnly; flags++)
        {
            APTR.WriteUInt32(record, 8, flags);
            if (NativeSfsTrim.ExecuteRecord(state, record, out var error, 0, uint.MaxValue, true) != 0 ||
                error != DOS.Error.ActionNotKnown) return false;
            if (NativeSfsTrim.ExecuteRecord(state, record, out error, 0, uint.MaxValue, false) != 0 ||
                error != DOS.Error.ActionNotKnown) return false;
        }
#if COPPERFS_AUTOTRIM
        NativeAutomaticTrim.Reset(state);
        NativeAutomaticTrim.Initialize(state);
        if (p.TracksFreedRanges) return false;
        p.ObserveFreedRange(100, 32);
        NativeAutomaticTrim.MakeEligible(state);
        if (NativeAutomaticTrim.Pending(state) || NativeAutomaticTrim.Ready(state) ||
            NativeAutomaticTrim.DiscardControl(state).IsNotNull) return false;
        // Inhibit/remount/geometry resets must not accidentally re-enable it.
        var geometry = p.Geometry;
        NativeAmigaFileSystem.SetGeometry(state, geometry);
        if (p.TracksFreedRanges) return false;
#endif
        return true;
    }
}

public static class DiscardUnsupportedDeviceProbe
{
    public static uint Entry()
    {
        var state = APTR.FromPointer(0x3000);
        DiscardDeviceProbe.Attach(state, "copperhdf.device");
        if (!AmigaVolumeGeometry.Create(9003, 512, 2, out var geometry)) return 1;
        NativeAmigaFileSystem.SetGeometry(state, geometry);
        var p = new NativeFileSystemPlatform(state);
        // The test device rejects the first INQUIRY. Admission by name must
        // never bypass capability discovery or turn that failure into support.
        return p.QueryDiscard(APTR.FromPointer(0xA000), out var limits) || limits.Valid ? 2u : 0u;
    }
}
