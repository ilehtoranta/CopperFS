using static CopperFS.Cpx4.Reference.Layout;

namespace CopperFS.Cpx4.Reference;

public ref partial struct Cpx4Volume<TDevice> where TDevice : IBlockDevice
{
    private bool CoveringExtent(scoped ReadOnlySpan<byte> obj, ulong logical, scoped Span<byte> found, out ulong next)
    {
        next = ulong.MaxValue;
        if (ExtentFloor(obj, logical, found) && logical - U64(found, 0) < U32(found, 16)) return true;
        if (NextExtent(obj, logical, false, found)) next = U64(found, 0);
        return false;
    }
    private bool ExtentFloor(scoped ReadOnlySpan<byte> obj, ulong logical, scoped Span<byte> found)
    {
        if (obj[84] != 1 || obj[85] == 0) return false;
        if (obj[85] == 2) return WalkOne(U64(obj, 24), TreeKind.Extent, U64(obj, 0), logical, 0, true, true, found);
        int count = U16(obj, 90); if (count > 6) throw new InvalidDataException("Invalid inline extent count.");
        for (int i = count - 1; i >= 0; i--) if (U64(obj, 96 + i * 24) <= logical) { obj.Slice(96 + i * 24, 24).CopyTo(found); return true; }
        return false;
    }
    private MutationResult Promote(scoped Span<byte> obj)
    {
        if (obj[85] != 0) return MutationResult.Success;
        if (!EnsureReserve(Operation.Data)) return MutationResult.ReserveExhausted;
        Begin(Operation.Data); using var recordFrame1 = RentRecords(160 + 24); Span<byte> inline = recordFrame1.Bytes.Slice(0, 160); Span<byte> extent = recordFrame1.Bytes.Slice(0 + 160, 24);
        ulong size = U64(obj, 8); obj[96..].CopyTo(inline); obj[96..].Clear(); obj[85] = 1; Put16(obj, 88, 0);
        ulong physical = 0;
        if (size != 0)
        {
            if (!FindFree(1, false, out physical, out _)) { Abort(); return MutationResult.DiskFull; }
            ChangeBits(physical, 1, true); extent.Clear(); Put64(extent, 8, physical); Put32(extent, 16, 1); AddExtent(obj, extent); Put64(obj, 16, 1);
        }
        SaveObject(obj);
        if (size != 0) { B(4).Clear(); inline[..(int)size].CopyTo(B(4)); RawWrite(physical, B(4)[..layout.UnitBytes]); }
        CommitTransaction(); return MutationResult.Success;
    }
    /// <summary>Bounded writes. External in-place data overwrites are not atomic;
    /// metadata, hole allocation and unwritten-to-written mapping changes are journaled.</summary>
    public MutationResult Write(ulong id, ulong offset, scoped ReadOnlySpan<byte> data, out int written)
    {
        Ready(); written = 0;
        if ((ulong)data.Length > ulong.MaxValue - offset) throw new ArgumentOutOfRangeException(nameof(offset));
        using var recordFrame2 = RentRecords(256 + 24 + 24); Span<byte> obj = recordFrame2.Bytes.Slice(0, 256); Span<byte> existing = recordFrame2.Bytes.Slice(0 + 256, 24); Span<byte> piece = recordFrame2.Bytes.Slice(0 + 256 + 24, 24);
        if (!GetObject(id, obj) || obj[84] != 1) return MutationResult.NotFound;
        if (data.IsEmpty) return MutationResult.Success;
        if (obj[85] == 0 && offset + (uint)data.Length <= InlineBytes)
        {
            Begin(Operation.Data); ulong size = U64(obj, 8);
            if (offset > size) obj.Slice(96 + (int)size, (int)(offset - size)).Clear(); data.CopyTo(obj[(96 + (int)offset)..]);
            size = Math.Max(size, offset + (uint)data.Length); Put64(obj, 8, size); Put16(obj, 88, (ushort)size); SaveObject(obj); CommitTransaction(); written = data.Length; return MutationResult.Success;
        }
        var promoted = Promote(obj); if (promoted != MutationResult.Success) return promoted;
        while (written < data.Length)
        {
            if (!EnsureReserve(Operation.Data)) return MutationResult.ReserveExhausted;
            GetObject(id, obj); ulong at = offset + (uint)written, logical = layout.UnitOffset(at);
            int intra = (int)(layout.UnitRemainder(at)), take = Math.Min(data.Length - written, 256 * 1024);
            bool mapped = CoveringExtent(obj, logical, existing, out ulong next), fresh = !mapped;
            bool unwritten = mapped && U32(existing, 20) == 1; ulong physical = 0;
            if (mapped)
            {
                ulong remaining = U32(existing, 16) - (logical - U64(existing, 0));
                take = (int)Math.Min((ulong)take, layout.ByteOffset(remaining) - (uint)intra);
                physical = U64(existing, 8) + logical - U64(existing, 0);
            }
            else if (next != ulong.MaxValue) take = (int)Math.Min((ulong)take, layout.ByteOffset(Math.Min(next - logical, 65536)) - (uint)intra);
            Begin(Operation.Data);
            try
            {
                uint units = (uint)(layout.UnitOffset((ulong)intra + (uint)take + (uint)layout.UnitBytes - 1));
                if (fresh)
                {
                    if (!FindFree(units, false, out physical, out units)) { Abort(); return MutationResult.DiskFull; }
                    take = Math.Min(take, checked((int)units * layout.UnitBytes - intra)); ChangeBits(physical, units, true);
                    piece.Clear(); Put64(piece, 0, logical); Put64(piece, 8, physical); Put32(piece, 16, units); AddExtent(obj, piece);
                    Put64(obj, 16, checked(U64(obj, 16) + units));
                }
                else if (unwritten)
                {
                    uint left = (uint)(logical - U64(existing, 0)), right = U32(existing, 16) - left - units;
                    existing.CopyTo(piece);
                    if (left != 0) { Put32(piece, 16, left); ReplaceExtent(obj, piece); }
                    else { Put32(piece, 16, units); Put32(piece, 20, 0); ReplaceExtent(obj, piece); }
                    if (left != 0)
                    { Put64(piece, 0, logical); Put64(piece, 8, physical); Put32(piece, 16, units); Put32(piece, 20, 0); AddExtent(obj, piece); }
                    if (right != 0)
                    { Put64(piece, 0, logical + units); Put64(piece, 8, physical + units); Put32(piece, 16, right); Put32(piece, 20, 1); AddExtent(obj, piece); }
                }
                ulong oldSize = U64(obj, 8); Put64(obj, 8, Math.Max(oldSize, at + (uint)take)); SaveObject(obj);
                // Both split insertions and all target admission precede external writes.
                ZeroExposedTail(obj, oldSize, at);
                for (int done = 0; done < take;)
                {
                    int local = done == 0 ? intra : 0, part = Math.Min(take - done, layout.UnitBytes - local);
                    ulong delta = (uint)((intra + done) / layout.UnitBytes), unit = physical + delta;
                    if (fresh || unwritten) B(4).Clear(); else RawRead(unit, B(4)[..layout.UnitBytes]);
                    if (!fresh && !unwritten && layout.UnitOffset(oldSize) == logical + delta)
                    { int end = (int)(layout.UnitRemainder(oldSize)); if (end < local) B(4).Slice(end, local - end).Clear(); }
                    data.Slice(written + done, part).CopyTo(B(4)[local..]); RawWrite(unit, B(4)[..layout.UnitBytes]); done += part;
                }
                CommitTransaction(); written += take;
            }
            catch (AdmissionException e) { Abort(); return e.Result; }
        }
        return MutationResult.Success;
    }
    /// <summary>Allocate a unit-aligned hole as unwritten storage; reads remain zero.
    /// The file grows to include it. Each transaction allocates one bitmap-window run.</summary>
    public MutationResult Preallocate(ulong id, ulong offset, uint unitCount, out uint allocated)
    {
        Ready(); allocated = 0;
        if (layout.UnitRemainder(offset) != 0 || layout.ByteOffset(unitCount) > ulong.MaxValue - offset) throw new ArgumentOutOfRangeException(nameof(offset));
        using var recordFrame3 = RentRecords(256 + 24); Span<byte> obj = recordFrame3.Bytes.Slice(0, 256); Span<byte> extent = recordFrame3.Bytes.Slice(0 + 256, 24);
        if (!GetObject(id, obj) || obj[84] != 1) return MutationResult.NotFound;
        ulong logical = layout.UnitOffset(offset);
        if (unitCount == 0) return MutationResult.Success;
        // Preflight overlap across the complete caller range before promoting inline data.
        if (obj[85] == 0 && U64(obj, 8) != 0 && logical == 0) return MutationResult.InvalidOperation;
        if (CoveringExtent(obj, logical, extent, out ulong next) || next != ulong.MaxValue && next - logical < unitCount) return MutationResult.InvalidOperation;
        var result = Promote(obj); if (result != MutationResult.Success) return result;
        while (allocated < unitCount)
        {
            if (!EnsureReserve(Operation.Data)) return MutationResult.ReserveExhausted;
            GetObject(id, obj); Begin(Operation.Data);
            try
            {
                uint wanted = Math.Min(unitCount - allocated, (uint)(256 * 1024 / layout.UnitBytes));
                if (!FindFree(wanted, false, out ulong physical, out uint take)) { Abort(); return MutationResult.DiskFull; }
                ChangeBits(physical, take, true); extent.Clear(); Put64(extent, 0, logical + allocated); Put64(extent, 8, physical); Put32(extent, 16, take); Put32(extent, 20, 1);
                ulong oldSize = U64(obj, 8);
                AddExtent(obj, extent); Put64(obj, 16, U64(obj, 16) + take); Put64(obj, 8, Math.Max(oldSize, layout.ByteOffset(logical + allocated + take)));
                SaveObject(obj); ZeroExposedTail(obj, oldSize, layout.ByteOffset(logical + allocated)); CommitTransaction(); allocated += take;
            }
            catch (AdmissionException e) { Abort(); return e.Result; }
        }
        return MutationResult.Success;
    }
}
