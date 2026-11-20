namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // A bounded mount turn validates up to sixteen reserve entries sharing
    // the authenticated bitmap. Crossing a bitmap boundary yields for I/O.
    private StepResult RecoverReserveBatch(bool bitmapArrived)
    {
        uint arena = Arena, bitmap = (memory.Read32(Arena + (GeometryPages + (5) * 4))), reserve = (memory.Read32(Arena + (GeometryPages + (3) * 4))), table = Index;
        uint cursor = memory.Read32(arena + Cursor), count = memory.Read32(arena + Count);
        uint pageBytes = memory.Read32(arena + PageBytes), windowShift = pageBytes == 512 ? 11u : 12u;
        uint policy = memory.Read32(arena + CheckpointPolicy), storage = policy & ~7u;
        uint span = memory.Read32(arena + GeometryQ) * ((policy & 4) != 0 ? 2u : 1u);
        uint minimum = memory.Read32(arena + GeometryFirst), firstHigh = 0, firstLow = minimum;
        if ((policy & 4) != 0)
        {
            firstLow = memory.Read32(storage + 44);
            firstHigh = memory.Read32(storage + 40);
        }
        uint backupLow = memory.Read32(arena + GeometryBackup + 4), backupHigh = memory.Read32(arena + GeometryBackup);
        uint prefix = (pageBytes == 512 ? 33u : 17u) * span;
        uint hashShift = memory.Read32(arena + GeometryShift) - memory.Read32(arena + UnitShift);
        ulong previous = ((((ulong)(memory.Read32((Arena) + ((Target))))) << 32 | ((ulong)(memory.Read32((Arena) + ((Target) + 4))))));
        if (bitmapArrived)
        {
            ulong owner = previous & 0xffffffffffff0000UL;
            if (!ControlBitmap(bitmap, previous, owner) || !ReserveBitsAllocated(bitmap, (uint)previous, span, windowShift))
                return Fail(CpxError.Corrupt);
            memory.Write32(arena + Cursor, ++cursor);
        }
        for (uint budget = 0; budget < 16; budget++)
        {
            if (cursor == count)
            {
                memory.Write32(arena + Target, (uint)(previous >> 32)); memory.Write32(arena + Target + 4, (uint)previous);
                if (ShadowVolume && (memory.Read32(Arena + (ReadOnlyAt))) == 0)
                {
                    if (!CanCacheMetadata) return Fail(CpxError.ResourceExhausted);
                    W(BufferEnabled, 1);
                    ClearBufferedObjects();
                }
                memory.Write32(arena + Mounted, 1);
                memory.Write32(arena + Poisoned, 0);
                Clear(table, CpxArena.IndexBytes);
                if (memory.Read32(arena + ReadOnlyAt) != 0) return DirectInodes ? LoadObject(1, 32) : Done();
                return Call(CpxOperation.Reclaim, 30);
            }
            uint item = reserve + 72 + cursor * 8;
            uint high = memory.Read32(item), low = memory.Read32(item + 4);
            uint endLow = unchecked(low + span), endHigh = unchecked(high + (endLow < low ? 1u : 0u));
            // The admitted geometry is invariant for this mount turn. Preserve
            // Allocatable's complete 64-bit bounds and group-prefix checks.
            if ((low & (span - 1)) != 0 || endHigh < high ||
                endHigh > backupHigh || endHigh == backupHigh && endLow > backupLow ||
                high == 0 && low < minimum ||
                high < firstHigh || high == firstHigh && low < firstLow ||
                !(high == 0 && low < 65536) && (low & 65535) < prefix)
                return Fail(CpxError.Corrupt);
            ulong address = ((ulong)high << 32) | low;
            uint hash = ((low >> (int)hashShift) ^ high) & 127;
            bool inserted = false;
            for (uint probe = 0; probe < 128; probe++)
            {
                uint entry = table + hash * 16;
                if (memory.Read32(entry + 8) == 0)
                {
                    memory.Write32(entry, high); memory.Write32(entry + 4, low);
                    memory.Write32(entry + 8, 1);
                    inserted = true;
                    break;
                }
                if (memory.Read32(entry + 4) == low && memory.Read32(entry) == high) return Fail(CpxError.Corrupt);
                hash = (hash + 1) & 127;
            }
            if (!inserted) return Fail(CpxError.Corrupt);
            bool cached = cursor != 0 && (uint)(address >> 32) == (uint)(previous >> 32) &&
                ((uint)address >> (int)windowShift) == ((uint)previous >> (int)windowShift);
            if (!cached)
            {
                memory.Write32(arena + Target, high); memory.Write32(arena + Target + 4, low);
                return ReadStaged(Bitmap(address), bitmap, 29);
            }
            if (!ReserveBitsAllocated(bitmap, low, span, windowShift)) return Fail(CpxError.Corrupt);
            previous = address;
            memory.Write32(arena + Cursor, ++cursor);
        }
        memory.Write32(arena + Target, (uint)(previous >> 32)); memory.Write32(arena + Target + 4, (uint)previous);
        memory.Write32(arena + Phase, 28);
        return StepResult.Pending;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private bool ReserveBitsAllocated(uint bitmap, uint address, uint width, uint windowShift)
    {
        // Allocatable has established power-of-two alignment. Admitted page
        // geometries have spans 1, 2, 4, 8 or 16, so no partial range crosses
        // a byte. The sixteen-bit case is naturally word aligned.
        uint bit = address & ((1u << (int)windowShift) - 1), at = bitmap + 64 + (bit >> 3);
        if (width == 16) return memory.Read16(at) == 65535;
        uint mask = (255u << (int)(8 - width)) & 255u;
        mask >>= (int)(bit & 7);
        return (memory.Read8(at) & mask) == mask;
    }
}
