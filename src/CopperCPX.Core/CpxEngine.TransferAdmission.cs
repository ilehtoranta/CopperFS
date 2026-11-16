namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    internal bool CachedTransferAvailable(bool write)
    {
        if (memory.Read32(Arena + Signature) != CpxArena.Magic) return false;
        uint fast = memory.Read32(Arena + FastPointer);
        return fast != 0 && memory.Read32(fast) == (write ? 1u : 3u);
    }

    // Only already-authenticated read/append capabilities may enter here.
    // A decline changes no operation state; ordinary typed admission follows.
    internal bool TryBeginCachedTransfer(bool write, Wide id, Wide position, uint buffer, uint bytes)
    {
        if (buffer == 0 || bytes == 0 || buffer > uint.MaxValue - bytes) return false;
        // Each capability rejects a nonzero position high word and proves
        // offset + length within its admitted uint file range before mutation.
        return write ? TryBeginCachedAppend(id, position, buffer, bytes) :
            TryBeginCachedRead(id, position, buffer, bytes);
    }
}
