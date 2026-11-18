namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // Tag 4 pins the same owned images as the append capability. One free
    // bitmap interval is authenticated once; only this owner may consume it.
    // Successful writes advance the object immediately. Allocation bitmap,
    // counters and new-range ledger are materialized together on retirement,
    // before any other operation observes metadata or publishes a checkpoint.
    // Offsets 44..60 are private to this tagged use of the existing 64 bytes.
    private void ArmAppendLease(uint hint, uint count)
    {
        uint context = Arena, low = memory.Read32(context + NewPhysical + 4);
        uint maximum = BitmapWindowUnits - (low & BitmapWindowMask);
        if (maximum > 2048) maximum = 2048;
        uint length = memory.Read32(memory.Read32(hint + 12) + 112);
        if (maximum > 65536 - length) maximum = 65536 - length;
        if (maximum <= count) return;
        uint high = memory.Read32(context + NewPhysical), group = memory.Read32(hint + 32), state = memory.Read32(hint + 36);
        uint window = 80 + ((low & 65535) >> (int)BitmapWindowShift) * 4;
        if (!DataUnit(((ulong)high << 32 | low) + maximum - 1) ||
            memory.Read32(group + 68) < maximum || memory.Read32(group + window) < maximum ||
            memory.Read32(state + 96) == 0 && memory.Read32(state + 100) < maximum ||
            !BitmapRangeMatches(memory.Read32(hint + 20), low & BitmapWindowMask, maximum, false)) return;
        memory.Write32(hint + 44, low); memory.Write32(hint + 48, 0); memory.Write32(hint + 52, maximum);
        memory.Write32(hint + 56, length);
        memory.Write32(hint + 60, memory.Read32(context + MountEpoch));
        memory.Write32(hint, 4);
    }

    private bool BeginLeasedAppend(uint hint, ulong id, ulong position, uint bytes)
    {
        uint context = Arena, record = memory.Read32(hint + 12), shift = memory.Read32(context + UnitShift);
        if (memory.Read32(context + BufferEnabled) != 1 || memory.Read32(context + Active) == 0 ||
            memory.Read32(context + StagedCount) == 0 || memory.Read32(context + BufferFreed) != 0 ||
            memory.Read32(hint + 60) != memory.Read32(context + MountEpoch) ||
            memory.Read32(hint + 4) != (uint)(id >> 32) || memory.Read32(hint + 8) != (uint)id ||
            (position >> 32) != 0 || memory.Read32(record + 12) != (uint)position ||
            bytes == 0 || bytes > 65536 || (bytes & ((1u << (int)shift) - 1)) != 0 ||
            (uint)position > uint.MaxValue - bytes) return false;
        uint actual = memory.Read32(hint + 48), maximum = memory.Read32(hint + 52), count = bytes >> (int)shift;
        if (actual > maximum || count > maximum - actual) return false;
        memory.Write32(context + NewPhysical, memory.Read32(hint + 24));
        memory.Write32(context + NewPhysical + 4, memory.Read32(hint + 44) + actual);
        memory.Write32(context + WriteRunUnits, count); memory.Write32(context + BufferSnapshot, 0); memory.Write32(context + Phase, 500);
        return true;
    }

    private StepResult FinishLeasedAppend(uint hint)
    {
        uint context = Arena, count = memory.Read32(context + WriteRunUnits), record = memory.Read32(hint + 12), leaf = memory.Read32(hint + 16);
        uint actual = memory.Read32(hint + 48) + count, length = memory.Read32(hint + 56) + actual;
        uint size = memory.Read32(record + 12) + memory.Read32(context + FileBytes);
        memory.Write32(record + 12, size); memory.Write32(record + 20, length); memory.Write32(record + 112, length);
        memory.Write32(leaf + 12, size); memory.Write32(leaf + 20, length); memory.Write32(leaf + 112, length);
        memory.Write32(hint + 48, actual);
        if (actual == memory.Read32(hint + 52)) RetireOwnedAppendLease(hint);
        memory.Write32(context + FileValue, memory.Read32(context + FileBytes));
        return Done();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal void RetireAppendLease()
    {
        uint hint = memory.Read32(Arena + FastPointer);
        if (hint == 0 || memory.Read32(hint) != 4) return;
        RetireOwnedAppendLease(hint);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void RetireOwnedAppendLease(uint hint)
    {
        uint context = Arena;
        // Reinitialization and failed/cancelled operations discard unpublished
        // state. Never dereference their former owned-image pointers.
        if (memory.Read32(context + BufferEnabled) != 1 || memory.Read32(context + Active) == 0 ||
            memory.Read32(context + StagedCount) == 0 || memory.Read32(context + Mounted) == 0 ||
            memory.Read32(context + Poisoned) != 0 || memory.Read32(hint + 60) != memory.Read32(context + MountEpoch))
        { memory.Write32(hint, 0); return; }
        uint count = memory.Read32(hint + 48);
        if (count != 0)
        {
            uint low = memory.Read32(hint + 44), high = memory.Read32(hint + 24), group = memory.Read32(hint + 32), state = memory.Read32(hint + 36);
            SetBitmapRange(memory.Read32(hint + 20), low & BitmapWindowMask, count, true);
            uint window = 80 + ((low & 65535) >> (int)BitmapWindowShift) * 4;
            memory.Write32(group + 68, memory.Read32(group + 68) - count);
            memory.Write32(group + window, memory.Read32(group + window) - count);
            uint freeLow = memory.Read32(state + 100);
            memory.Write32(state + 96, memory.Read32(state + 96) - (freeLow < count ? 1u : 0u));
            memory.Write32(state + 100, freeLow - count);
            ulong first = (ulong)high << 32 | low;
            NoteBufferedAllocation(first, count, true); RememberAllocationEnd(first + count);
        }
        memory.Write32(hint + 44, 0); memory.Write32(hint + 48, 0); memory.Write32(hint + 52, 0);
        memory.Write32(hint + 56, 0); memory.Write32(hint + 60, 0); memory.Write32(hint, 1);
    }
}
