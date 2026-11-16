namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // A bounded mount turn validates up to sixteen reserve entries sharing
    // the authenticated bitmap. Crossing a bitmap boundary yields for I/O.
    private StepResult RecoverReserveBatch(bool bitmapArrived)
    {
        uint arena = Arena, bitmap = Page(5), reserve = Page(3), table = Index;
        uint cursor = memory.Read32(arena + Cursor), count = memory.Read32(arena + Count);
        ulong previous = RW(Target);
        if (bitmapArrived)
        {
            ulong owner = previous & 0xffffffffffff0000UL;
            if (!ControlBitmap(bitmap, previous, owner) || !ReserveBitsAllocated(bitmap, previous))
                return Fail(CpxError.Corrupt);
            memory.Write32(arena + Cursor, ++cursor);
        }
        for (uint budget = 0; budget < 16; budget++)
        {
            if (cursor == count)
            {
                if (ShadowVolume && R(ReadOnlyAt) == 0)
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
            ulong address = GetWide(reserve + 72 + cursor * 8);
            if (!Allocatable(address)) return Fail(CpxError.Corrupt);
            uint hash = (((uint)address >> (int)(memory.Read32(arena + GeometryShift) - memory.Read32(arena + UnitShift))) ^ (uint)(address >> 32)) & 127;
            bool inserted = false;
            for (uint probe = 0; probe < 128; probe++)
            {
                uint entry = table + hash * 16;
                if (memory.Read32(entry + 8) == 0)
                {
                    PutWide(entry, address);
                    memory.Write32(entry + 8, 1);
                    inserted = true;
                    break;
                }
                if (GetWide(entry) == address) return Fail(CpxError.Corrupt);
                hash = (hash + 1) & 127;
            }
            if (!inserted) return Fail(CpxError.Corrupt);
            bool cached = cursor != 0 && Bitmap(address) == Bitmap(previous);
            WW(Target, address);
            if (!cached) return ReadStaged(Bitmap(address), bitmap, 29);
            if (!ReserveBitsAllocated(bitmap, address)) return Fail(CpxError.Corrupt);
            previous = address;
            memory.Write32(arena + Cursor, ++cursor);
        }
        memory.Write32(arena + Phase, 28);
        return StepResult.Pending;
    }

    private bool ReserveBitsAllocated(uint bitmap, ulong address)
    {
        uint bit = (uint)address & BitmapWindowMask, width = MetadataSpan;
        for (uint n = 0; n < width; n++)
            if ((memory.Read8(bitmap + 64 + ((bit + n) >> 3)) & (0x80 >> ((int)(bit + n) & 7))) == 0)
                return false;
        return true;
    }
}
