namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // The existing 64-byte fast context has one owner and one tagged use at a
    // time. A read capability retains only authenticated mapping scalars, never
    // a DOS buffer. Other operations and object-cache changes invalidate it.
    private void ArmReadCapability(ulong id, ulong physical, uint size, uint length, uint shift)
    {
        uint fast = memory.Read32(Arena + FastPointer);
        if (fast == 0) return;
        uint covered = length << (int)shift;
        if (size > covered) size = covered;
        memory.Write32(fast + 4, (uint)(id >> 32)); memory.Write32(fast + 8, (uint)id);
        memory.Write32(fast + 12, size);
        memory.Write32(fast + 16, (uint)(physical >> 32)); memory.Write32(fast + 20, (uint)physical);
        memory.Write32(fast + 24, memory.Read32(Arena + MountEpoch));
        memory.Write32(fast, 3);
    }

    private bool TryBeginCachedRead(ulong id, ulong position, uint buffer, uint bytes)
    {
        uint context = Arena;
        if (memory.Read32(context + Signature) != CpxArena.Magic || memory.Read32(context + Waiting) != 0 ||
            memory.Read32(context + Finished) == 0 || memory.Read32(context + AbortOnly) != 0 ||
            memory.Read32(context + Mounted) == 0 || memory.Read32(context + Poisoned) != 0 ||
            memory.Read32(context + BufferEnabled) != 1) return false;
        uint fast = memory.Read32(context + FastPointer), shift = memory.Read32(context + UnitShift);
        if (fast == 0 || memory.Read32(fast) != 3 || bytes == 0 || bytes > 65536 || (position >> 32) != 0 ||
            (((uint)position | bytes) & ((1u << (int)shift) - 1)) != 0 ||
            memory.Read32(fast + 4) != (uint)(id >> 32) || memory.Read32(fast + 8) != (uint)id ||
            memory.Read32(fast + 24) != memory.Read32(context + MountEpoch)) return false;
        uint size = memory.Read32(fast + 12), offset = (uint)position;
        if (offset > size || bytes > size - offset) return false;
        memory.Write32(context + CallDepth, 0); memory.Write32(context + OperationAt, (uint)CpxOperation.ReadFile);
        memory.Write32(context + Phase, 530); memory.Write32(context + ErrorAt, 0); memory.Write32(context + Finished, 0);
        memory.Write32(context + CancelAt, 0); memory.Write32(context + IoReady, 0); memory.Write32(context + NsPublished, 0);
        memory.Write32(context + Target, (uint)(id >> 32)); memory.Write32(context + Target + 4, (uint)id);
        memory.Write32(context + ObjectKey, (uint)(id >> 32)); memory.Write32(context + ObjectKey + 4, (uint)id);
        memory.Write32(context + SourcePage, 0); memory.Write32(context + FilePosition, 0); memory.Write32(context + FilePosition + 4, offset);
        memory.Write32(context + FileBuffer, buffer); memory.Write32(context + FileBytes, bytes); memory.Write32(context + FileValue, 0);
        memory.Write32(context + DataProgress, 0); memory.Write32(context + TrimCount, 0);
        memory.Write32(context + WriteGapPrepared, 0); memory.Write32(context + WriteRunUnits, 1); memory.Write32(context + WriteDirect, 0);
        memory.Write32(context + NewPhysical, memory.Read32(fast + 16));
        memory.Write32(context + NewPhysical + 4, memory.Read32(fast + 20) + (offset >> (int)shift));
        memory.Write32(context + DataTake, bytes);
        return true;
    }
}
