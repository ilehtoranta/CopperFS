namespace CopperCPX;

public partial struct CpxDosDispatcher<TMemory> where TMemory : struct, ICpxMemory
{
    private bool TryBeginCachedTransferPacket(uint packet, uint action)
    {
        uint bytes = memory.Read32(packet + 28);
        // Every supported allocation unit is a multiple of 512. These requests
        // cannot use either capability, so keep their general path inexpensive.
        if (bytes == 0 || bytes > 65536 || (bytes & 511) != 0) return false;
        bool write = action == 87;
        if (!engine.CachedTransferAvailable(write)) return false;
        uint state = arena + 512;
        if (memory.Read32(state + Inhibited) != 0 || memory.Read32(state + MediaReason) != 0 ||
            memory.Read32(state + StopReason) != 0) return false;
        if (write && (engine.IsReadOnly || memory.Read32(state + SoftwareProtected) != 0)) return false;
        uint handle = memory.Read32(packet + 20), buffer = memory.Read32(packet + 24);
        if (!ValidResource(handle, ResourceKind.Handle) ||
            write && memory.Read32(handle + 20) == 0) return false;
        if (!engine.TryBeginCachedTransfer(write, G(handle), G(handle + 8), buffer, bytes)) return false;
        memory.Write32(state + Handle, handle);
        memory.Write32(state + Phase, 20);
        return true;
    }
}
