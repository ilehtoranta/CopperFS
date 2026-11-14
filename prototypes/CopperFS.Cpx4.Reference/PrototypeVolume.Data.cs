using static CopperFS.Cpx4.Reference.Layout;

namespace CopperFS.Cpx4.Reference;

public ref partial struct Cpx4Volume<TDevice> where TDevice : IBlockDevice
{
    private bool NextExtent(scoped ReadOnlySpan<byte> obj, ulong logical, bool inclusive, scoped Span<byte> extent)
    {
        if (obj[84] != 1 || obj[85] == 0) return false;
        if (obj[85] == 2) return TreeNext(U64(obj, 24), TreeKind.Extent, U64(obj, 0), logical, 0, inclusive, extent);
        int count = U16(obj, 90); if (count > 6) throw new InvalidDataException("Invalid inline extent count.");
        for (int i = 0; i < count; i++)
        {
            var e = obj.Slice(96 + i * 24, 24); ulong key = U64(e, 0);
            if (key > logical || inclusive && key == logical) { e.CopyTo(extent); return true; }
        }
        return false;
    }
    private bool LastExtent(scoped ReadOnlySpan<byte> obj, scoped Span<byte> extent)
    {
        if (obj[84] != 1 || obj[85] == 0) return false;
        if (obj[85] == 2) return WalkOne(U64(obj, 24), TreeKind.Extent, U64(obj, 0), ulong.MaxValue, 0, true, true, extent);
        int count = U16(obj, 90); if (count > 6) throw new InvalidDataException("Invalid inline extent count.");
        if (count == 0) return false; obj.Slice(96 + (count - 1) * 24, 24).CopyTo(extent); return true;
    }
    private void AddExtent(scoped Span<byte> obj, scoped ReadOnlySpan<byte> extent)
    {
        int count = U16(obj, 90);
        if (obj[85] != 2 && count < 6)
        {
            int slot = 0; while (slot < count && U64(obj, 96 + slot * 24) < U64(extent, 0)) slot++;
            obj.Slice(96 + slot * 24, (count - slot) * 24).CopyTo(obj[(96 + (slot + 1) * 24)..]);
            extent.CopyTo(obj.Slice(96 + slot * 24, 24)); obj[85] = 1;
        }
        else
        {
            ulong root = U64(obj, 24);
            if (obj[85] != 2)
            {
                root = TakeMetadata(); Header(B(1), PageType.ExtentLeaf, root, U64(obj, 0), generation + 1, count);
                obj.Slice(96, count * 24).CopyTo(B(1)[64..]); Stage(root, B(1)); obj[96..].Clear(); obj[85] = 2;
            }
            TreeInsert(ref root, TreeKind.Extent, U64(obj, 0), extent); Put64(obj, 24, root);
        }
        // This field counts inline descriptors only. Tree cardinality is not limited to ushort.
        Put16(obj, 90, obj[85] == 2 ? (ushort)0 : checked((ushort)(count + 1)));
    }
    private void ReplaceExtent(scoped Span<byte> obj, scoped ReadOnlySpan<byte> extent)
    {
        if (obj[85] == 2) { TreeReplace(U64(obj, 24), TreeKind.Extent, U64(obj, 0), extent); return; }
        for (int i = 0; i < U16(obj, 90); i++)
            if (U64(obj, 96 + i * 24) == U64(extent, 0)) { extent.CopyTo(obj.Slice(96 + i * 24, 24)); return; }
        throw new InvalidDataException("Missing extent.");
    }
    private void RemoveExtent(scoped Span<byte> obj, ulong logical)
    {
        if (obj[85] == 2) { TreeDelete(U64(obj, 24), TreeKind.Extent, U64(obj, 0), logical, 0); return; }
        int count = U16(obj, 90);
        for (int i = 0; i < count; i++) if (U64(obj, 96 + i * 24) == logical)
        {
            obj.Slice(96 + (i + 1) * 24, (count - i - 1) * 24).CopyTo(obj[(96 + i * 24)..]);
            obj.Slice(96 + (count - 1) * 24, 24).Clear(); Put16(obj, 90, (ushort)(count - 1)); return;
        }
        throw new InvalidDataException("Missing extent.");
    }
    private ulong Physical(scoped ReadOnlySpan<byte> obj, ulong logical, out bool unwritten)
    {
        using var recordFrame2 = RentRecords(24); Span<byte> e = recordFrame2.Bytes.Slice(0, 24); unwritten = false;
        if (ExtentFloor(obj, logical, e))
        {
            ulong key = U64(e, 0);
            uint count = U32(e, 16); ulong physical = U64(e, 8);
            if (count is 0 or > GroupUnits || physical >= layout.Units || count > layout.Units - physical ||
                layout.GroupBase(physical) != layout.GroupBase(physical + count - 1) || U32(e, 20) > 1) throw new InvalidDataException("Invalid extent.");
            if (logical - key < count) { unwritten = U32(e, 20) == 1; return physical + logical - key; }
        }
        return 0;
    }
    public int Read(scoped ReadOnlySpan<byte> name, ulong offset, scoped Span<byte> destination)
    { return TryGetInfo(name, out var info) ? Read(info.Id, offset, destination) : -1; }
    public int Read(ulong id, ulong offset, scoped Span<byte> destination)
    {
        Ready(); using var recordFrame3 = RentRecords(256); Span<byte> obj = recordFrame3.Bytes.Slice(0, 256); if (!GetObject(id, obj) || obj[84] != 1) return -1;
        ulong size = U64(obj, 8); if (offset >= size) return 0;
        int length = (int)Math.Min((ulong)destination.Length, size - offset);
        if (obj[85] == 0)
        {
            if (size > InlineBytes || U16(obj, 88) != size) throw new InvalidDataException("Invalid inline size.");
            obj.Slice(96 + (int)offset, length).CopyTo(destination); return length;
        }
        for (int done = 0; done < length;)
        {
            ulong at = offset + (uint)done, logical = layout.UnitOffset(at);
            int intra = (int)(layout.UnitRemainder(at)), take = Math.Min(length - done, layout.UnitBytes - intra);
            ulong physical = Physical(obj, logical, out bool unwritten);
            if (physical == 0 || unwritten) destination.Slice(done, take).Clear();
            else { RawRead(physical, B(4)[..layout.UnitBytes]); B(4).Slice(intra, take).CopyTo(destination[done..]); }
            done += take;
        }
        return length;
    }
    /// <summary>Append (possibly after a sparse gap). Reports durable progress in chunks &lt;=256 KiB.
    /// Uses the shared write path to initialise and publish retained unwritten extents.</summary>
    public MutationResult Append(ulong id, ulong offset, scoped ReadOnlySpan<byte> data, out int written)
    {
        Ready(); written = 0; using var recordFrame4 = RentRecords(256); Span<byte> obj = recordFrame4.Bytes.Slice(0, 256);
        if (!GetObject(id, obj) || obj[84] != 1) return MutationResult.NotFound;
        if (offset < U64(obj, 8) || (ulong)data.Length > ulong.MaxValue - offset) throw new ArgumentOutOfRangeException(nameof(offset));
        // Keep append's EOF constraint; Write owns zeroing, extent conversion,
        // bounded admission and publication for both public entry points.
        return Write(id, offset, data, out written);
    }
    private void ZeroExposedTail(scoped ReadOnlySpan<byte> obj, ulong oldSize, ulong writeOffset)
    {
        int tail = (int)(layout.UnitRemainder(oldSize));
        if (tail == 0 || writeOffset <= oldSize || layout.UnitOffset(writeOffset) == layout.UnitOffset(oldSize)) return;
        ulong physical = Physical(obj, layout.UnitOffset(oldSize), out bool unwritten);
        if (physical == 0 || unwritten) return;
        RawRead(physical, B(4)[..layout.UnitBytes]); B(4).Slice(tail, layout.UnitBytes - tail).Clear(); RawWrite(physical, B(4)[..layout.UnitBytes]);
    }
    /// <summary>Convenience host import. Replacement is delete/create followed by bounded appends;
    /// the whole import is not one atomic transaction.</summary>
    public MutationResult Put(scoped ReadOnlySpan<byte> name, scoped ReadOnlySpan<byte> data)
    {
        if (TryGetInfo(name, out _)) { var removed = Delete(name); if (removed != MutationResult.Success) return removed; }
        var created = Create(name, out ulong id); if (created != MutationResult.Success) return created;
        return Append(id, 0, data, out _);
    }
    public uint Trim(uint maximumUnits = 4096)
    {
        Ready(); if (readOnly || !device.SupportsDiscard || maximumUnits == 0) return 0;
        ulong start = Math.Max(trimCursor, layout.DataStart), end = start;
        ulong scanEnd = Math.Min(layout.Units, (start & ~4095UL) + 4 * BitmapUnits), cachedMap = ulong.MaxValue;
        while (start < scanEnd && !TrimFree(start, ref cachedMap)) start++;
        if (start == scanEnd) { trimCursor = scanEnd == layout.Units ? layout.DataStart : scanEnd; return 0; }
        end = start;
        uint limit = (uint)Math.Min(maximumUnits, ((ulong)device.MaximumDiscardSectors >> layout.DeviceShift));
        while (end < scanEnd && end - start < limit && TrimFree(end, ref cachedMap)) end++;
        uint alignment = device.DiscardAlignmentSectors;
        if (alignment == 0) throw new InvalidDataException("Invalid discard alignment.");
        ulong firstSector = layout.DeviceSector(start), lastSector = layout.DeviceSector(end);
        ulong remainder = AddressMath.Remainder(firstSector, alignment);
        if (remainder != 0) firstSector += alignment - remainder;
        lastSector -= AddressMath.Remainder(lastSector, alignment); trimCursor = end;
        if (lastSector <= firstSector) return 0;
        poisoned = true; IoBoundary(); device.Discard(firstSector, checked((uint)(lastSector - firstSector))); poisoned = false;
        return (uint)(AddressMath.Right(lastSector - firstSector, layout.DeviceShift));
    }
    private bool TrimFree(ulong unit, ref ulong cachedMap)
    {
        if (layout.Permanent(unit)) return false;
        ulong window = unit >> 12;
        if (window != cachedMap)
        {
            ulong group = layout.GroupBase(unit); int map = (int)((unit & 65535) >> 12);
            Load(layout.Bitmap(group, map), B(0)); ValidateBitmap(B(0), group, map); cachedMap = window;
        }
        return !Bit(B(0), (int)(unit & 4095));
    }
    // Internal qualification seam: exercises real streamed journal admission/publication.
    // Addresses must be valid existing metadata; never exposed as a public mutation API.
    internal void RestageForQualification(scoped ReadOnlySpan<ulong> addresses)
    {
        Begin(Operation.Probe);
        try { foreach (ulong address in addresses) { Load(address, B(0)); Stage(address, B(0)); } CommitTransaction(); }
        catch (InvalidOperationException) { Abort(); throw; }
    }
    internal MutationResult InsertForQualification(ref ulong root, TreeKind kind, ulong owner, scoped ReadOnlySpan<byte> record)
    {
        Begin(Operation.Data);
        try { TreeInsert(ref root, kind, owner, record); CommitTransaction(); return MutationResult.Success; }
        catch (AdmissionException e) { Abort(); return e.Result; }
    }
}
