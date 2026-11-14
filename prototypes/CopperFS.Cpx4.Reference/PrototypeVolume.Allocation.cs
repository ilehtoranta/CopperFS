using static CopperFS.Cpx4.Reference.Layout;

namespace CopperFS.Cpx4.Reference;

public ref partial struct Cpx4Volume<TDevice> where TDevice : IBlockDevice
{
    private static bool Bit(scoped ReadOnlySpan<byte> p, int bit) => (p[64 + (bit >> 3)] & (0x80 >> (bit & 7))) != 0;
    private static void SetBit(scoped Span<byte> p, int bit, bool used)
    { int n = 64 + (bit >> 3); byte mask = (byte)(0x80 >> (bit & 7)); p[n] = used ? (byte)(p[n] | mask) : (byte)(p[n] & ~mask); }
    private void ValidateReserve()
    {
        Load(layout.Reserve, B(3)); int count = U16(B(3), 64);
        if (!IsPage(B(3), PageType.Reserve, layout.Reserve, 0) || count > ReserveCapacity) throw new InvalidDataException("Invalid reserve control.");
        for (int i = 0; i < count; i++)
        {
            ulong address = U64(B(3), 72 + i * 8);
            if (!layout.AllocatablePage(address)) throw new InvalidDataException("Invalid reserve page address.");
            for (int j = 0; j < i; j++) if (U64(B(3), 72 + j * 8) == address) throw new InvalidDataException("Duplicate reserve ownership.");
            for (ulong unit = address; unit < address + layout.Q; unit++) if (!Allocated(unit)) throw new InvalidDataException("Reserve references bitmap-free space.");
        }
    }
    private bool Allocated(ulong unit)
    {
        ulong group = layout.GroupBase(unit); int map = (int)((unit & 65535) >> 12);
        Load(layout.Bitmap(group, map), B(0));
        ValidateBitmap(B(0), group, map);
        return Bit(B(0), (int)(unit & 4095));
    }
    private void ValidateBitmap(scoped ReadOnlySpan<byte> p, ulong group, int map)
    {
        if (!IsPage(p, PageType.Bitmap, layout.Bitmap(group, map), group) || U32(p, 12) != map)
            throw new InvalidDataException("Bitmap identity mismatch.");
    }
    private bool FindFree(uint wanted, bool metadata, out ulong address, out uint count)
    {
        var search = NewFreeSearch(wanted, metadata);
        while (true)
        {
            var result = SearchFreeStep(ref search, out address, out count);
            if (result == ScanResult.Found) return true;
            if (result == ScanResult.End) return false;
            if (result == ScanResult.Invalidated) throw new InvalidOperationException("Stale allocation continuation.");
            Cooperate();
        }
    }
    private void ChangeBits(ulong address, uint count, bool used)
    {
        if (count == 0 || address >= layout.Units || count > layout.Units - address || count > BitmapUnits - (address & 4095))
            throw new InvalidDataException("Allocation change crosses a bitmap window.");
        ulong group = layout.GroupBase(address); int map = (int)((address & 65535) >> 12);
        ulong bitmap = layout.Bitmap(group, map);
        Load(bitmap, B(1)); ValidateBitmap(B(1), group, map);
        for (uint n = 0; n < count; n++)
        {
            int bit = (int)((address + n) & 4095);
            if (layout.Permanent(address + n) || Bit(B(1), bit) == used) throw new InvalidDataException("Double allocation/free or reserved region.");
            SetBit(B(1), bit, used);
        }
        Stage(bitmap, B(1));
        ulong descriptor = layout.GroupDescriptor(group); Load(descriptor, B(3));
        uint free = U32(B(3), 68), local = U32(B(3), 80 + map * 4);
        Put32(B(3), 68, used ? checked(free - count) : checked(free + count));
        Put32(B(3), 80 + map * 4, used ? checked(local - count) : checked(local + count)); Stage(descriptor, B(3));
        ulong total = StateValue(96); SetState(96, used ? checked(total - count) : checked(total + count));
    }
    private bool RefillReserve()
    {
        Begin(Operation.Refill); int added = 0; ulong firstMap = ulong.MaxValue, secondMap = ulong.MaxValue;
        while (added < ReserveTarget)
        {
            Load(layout.Reserve, B(3)); int count = U16(B(3), 64);
            if (count >= ReserveTarget) break;
            if (!FindFree((uint)layout.Q, true, out ulong address, out _)) break;
            ulong map = layout.Bitmap(layout.GroupBase(address), (int)((address & 65535) >> 12));
            if (firstMap == ulong.MaxValue) firstMap = map;
            else if (map != firstMap && secondMap == ulong.MaxValue) secondMap = map;
            else if (map != firstMap && map != secondMap) break;
            ChangeBits(address, (uint)layout.Q, true);
            Load(layout.Reserve, B(3)); count = U16(B(3), 64);
            Put64(B(3), 72 + count * 8, address); Put16(B(3), 64, (ushort)(count + 1)); Stage(layout.Reserve, B(3)); added++;
        }
        if (added == 0) { Abort(); return false; }
        CommitTransaction(); return true;
    }
    private bool EnsureReserve(Operation operation)
    {
        int needed = Credits.Pages(operation);
        if (needed == 0) return true;
        int minimum = needed + (operation == Operation.Rename ? 0 : ReserveMargin);
        while (AvailableReservePages < minimum) if (!RefillReserve()) return false;
        return true;
    }
    private ulong TakeMetadata()
    {
        if (releasedMetadata || newPages >= pageLimit) throw new InvalidOperationException("New-page admission bound exceeded or early reuse.");
        Load(layout.Reserve, B(3)); int count = U16(B(3), 64);
        if (count == 0) throw new AdmissionException(MutationResult.ReserveExhausted);
        ulong address = U64(B(3), 72 + (count - 1) * 8);
        Put64(B(3), 72 + (count - 1) * 8, 0); Put16(B(3), 64, (ushort)(count - 1)); Stage(layout.Reserve, B(3));
        newPages++; return address;
    }
    private void ReleaseMetadata(ulong address)
    {
        if (!layout.AllocatablePage(address)) throw new InvalidDataException("Cannot release reserved metadata.");
        releasedMetadata = true; Load(layout.Reserve, B(3)); int count = U16(B(3), 64);
        for (int i = 0; i < count; i++) if (U64(B(3), 72 + i * 8) == address) throw new InvalidDataException("Metadata page already belongs to reserve.");
        if (count == ReserveCapacity) { ChangeBits(address, (uint)layout.Q, false); return; }
        Put64(B(3), 72 + count * 8, address); Put16(B(3), 64, (ushort)(count + 1)); Stage(layout.Reserve, B(3));
    }
}
