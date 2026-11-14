using static CopperFS.Cpx4.Reference.Layout;

namespace CopperFS.Cpx4.Reference;

public ref partial struct Cpx4Volume<TDevice> where TDevice : IBlockDevice
{
    public MutationResult Truncate(ulong id, ulong length)
    {
        Ready(); using var recordFrame1 = RentRecords(256); Span<byte> obj = recordFrame1.Bytes.Slice(0, 256);
        if (!GetObject(id, obj) || obj[84] != 1) return MutationResult.NotFound;
        ulong old = U64(obj, 8); if (length == old) return MutationResult.Success;
        if (obj[85] == 0 && length <= InlineBytes)
        {
            Begin(Operation.Attribute);
            obj.Slice(96 + (int)Math.Min(old, length), InlineBytes - (int)Math.Min(old, length)).Clear();
            Put64(obj, 8, length); Put16(obj, 88, (ushort)length); SaveObject(obj); CommitTransaction(); return MutationResult.Success;
        }
        if (length < old)
        {
            Begin(Operation.Attribute); Put64(obj, 8, length); Put16(obj, 86, 2);
            Put64(obj, 40, StateValue(88)); SetState(88, id); SaveObject(obj); CommitTransaction(); if (!deferredCleanup) DrainWork(); return MutationResult.Success;
        }
        if (!EnsureReserve(Operation.Data)) return MutationResult.ReserveExhausted;
        Begin(Operation.Data);
        if (obj[85] == 0)
        {
            using var recordFrame2 = RentRecords(160 + 24); Span<byte> inline = recordFrame2.Bytes.Slice(0, 160); Span<byte> extent = recordFrame2.Bytes.Slice(0 + 160, 24); obj[96..].CopyTo(inline);
            obj[96..].Clear(); obj[85] = 1; Put16(obj, 88, 0);
            if (old != 0)
            {
                if (!FindFree(1, false, out ulong physical, out _)) { Abort(); return MutationResult.DiskFull; }
                ChangeBits(physical, 1, true); extent.Clear(); Put64(extent, 8, physical); Put32(extent, 16, 1); AddExtent(obj, extent); Put64(obj, 16, 1);
                B(4).Clear(); inline[..(int)old].CopyTo(B(4)); RawWrite(physical, B(4)[..layout.UnitBytes]);
            }
        }
        else if (layout.UnitRemainder(old) != 0)
        {
            ulong physical = Physical(obj, layout.UnitOffset(old), out bool unwritten);
            if (physical != 0 && !unwritten)
            {
                RawRead(physical, B(4)[..layout.UnitBytes]); int at = (int)(layout.UnitRemainder(old));
                B(4).Slice(at, layout.UnitBytes - at).Clear(); RawWrite(physical, B(4)[..layout.UnitBytes]);
            }
        }
        Put64(obj, 8, length); SaveObject(obj); CommitTransaction(); return MutationResult.Success;
    }
}
