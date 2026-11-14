using static CopperFS.Cpx4.Reference.Layout;

namespace CopperFS.Cpx4.Reference;

/// <summary>CPX4 host research core. Six caller-owned pages, bounded indexes, single owner.
/// I/O failure poisons the mount. Reopen only after the device has quiesced.</summary>
public ref partial struct Cpx4Volume<TDevice> where TDevice : IBlockDevice
{
    private TDevice device;
    private Layout layout;
    private Span<byte> scratch;
    private ulong generation;
    private int imageCount, imageLimit, pageLimit, newPages;
    private bool active, poisoned, releasedMetadata;
    private ulong trimCursor;
    private int recordUsed, recordPeak;
    private int requestsSinceCooperate;
    private bool inCooperate;
    private bool deferredCleanup;
    public const int IoRequestsPerSlice = 4;
    public const int RecordScratchBytes = 4096;
    public int PeakRecordScratchBytes => recordPeak;
    internal int UsedRecordScratchBytes => recordUsed;
    [System.Diagnostics.CodeAnalysis.UnscopedRef]
    private ScratchFrame RentRecords(int length) => new(scratch.Slice(6 * layout.PageBytes + 6144, RecordScratchBytes), ref recordUsed, ref recordPeak, length);
    public const ulong RootDirectory = 1;
    public int LastTransactionPages { get; private set; }
    public int MaximumTransactionPages { get; private set; }
    public int MetadataPageBytes => layout.PageBytes;
    public int AllocationUnitBytes => layout.UnitBytes;
    public int InlineCapacity => InlineBytes;
    public ulong Generation => generation;
    public ulong VolumeId => layout.VolumeId;
    public ulong FirstDataUnit => layout.DataStart;
    public ulong FileCount { get { Ready(); return StateValue(104); } }
    public int AvailableReservePages { get { Ready(); Load(layout.Reserve, B(3)); return U16(B(3), 64); } }
    internal Layout Geometry => layout;
    internal Span<byte> B(int slot) => scratch.Slice(slot * layout.PageBytes, layout.PageBytes);
    private Span<byte> Index => scratch.Slice(6 * layout.PageBytes, JournalImages * 32);
    private Span<byte> Path => scratch.Slice(6 * layout.PageBytes + 2048, MaxHeight * 16);
    private Span<byte> NameScratch => scratch.Slice(6 * layout.PageBytes + 4096, NameBytes);
    private Span<byte> OtherNameScratch => scratch.Slice(6 * layout.PageBytes + 4096 + NameBytes, NameBytes);

    public static int RequiredScratchBytes(int pageBytes)
    {
        if (pageBytes is < 1024 or > 4096 || (pageBytes & (pageBytes - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(pageBytes));
        return 6 * pageBytes + 12 * 1024;
    }
    private void Ready() { if (poisoned || active || inCooperate) throw new InvalidOperationException("Mount needs recovery or is already owned by an operation."); }
    private void IoBoundary()
    {
        if (requestsSinceCooperate == IoRequestsPerSlice) Cooperate();
        requestsSinceCooperate++;
    }
    private void Cooperate()
    {
        requestsSinceCooperate = 0; inCooperate = true;
        try { device.Cooperate(); } catch { poisoned = true; throw; } finally { inCooperate = false; }
    }
    private void FlushDevice() { IoBoundary(); device.Flush(); }
    private void RawRead(ulong unit, scoped Span<byte> bytes)
    {
        try { IoBoundary(); device.Read(layout.DeviceSector(unit), bytes); }
        catch { poisoned = true; throw; }
    }
    private void RawWrite(ulong unit, scoped ReadOnlySpan<byte> bytes)
    { IoBoundary(); device.Write(layout.DeviceSector(unit), bytes); }
    private void Load(ulong unit, scoped Span<byte> page)
    {
        if (!layout.PageAddress(unit)) { poisoned = true; throw new InvalidDataException("Unaligned/out-of-range metadata address."); }
        int found = active ? FindStaged(unit) : -1;
        RawRead(found < 0 ? unit : layout.Image(generation + 1, found), page);
        if (!ValidCrc(page) || U64(page, 16) != unit || U64(page, 32) > generation + (active ? 1UL : 0UL))
        { poisoned = true; throw new InvalidDataException("Invalid metadata identity/checksum/generation."); }
    }
    private ulong StateValue(int offset) { Load(layout.State, B(3)); return U64(B(3), offset); }
    private void SetState(int offset, ulong value) { Load(layout.State, B(3)); Put64(B(3), offset, value); Stage(layout.State, B(3)); }

    public Cpx4Volume(TDevice device, Span<byte> scratch, bool readOnly = false, bool deferCleanup = false)
    {
        this = default; this.device = device; deferredCleanup = deferCleanup;
        if (device.SectorSize is < 512 or > 4096 || (device.SectorSize & (device.SectorSize - 1)) != 0 ||
            scratch.Length < 4096 || device.SectorCount > AddressMath.Right(ulong.MaxValue, AddressMath.PowerShift((uint)device.SectorSize)) ||
            AddressMath.Left(device.SectorCount, AddressMath.PowerShift((uint)device.SectorSize)) < 4194304) throw new ArgumentException("Invalid device/scratch.");
        ulong bytes = AddressMath.Left(device.SectorCount, AddressMath.PowerShift((uint)device.SectorSize));
        bool found = false;
        for (int pageBytes = 1024; pageBytes <= 4096; pageBytes *= 2)
        {
            if (pageBytes < device.SectorSize) continue;
            for (int copy = 0; copy < 2; copy++)
            {
                ulong at = copy == 0 ? 65536UL : (bytes & ~((ulong)pageBytes - 1)) - (uint)pageBytes;
                var p = scratch[..pageBytes]; IoBoundary(); device.Read(AddressMath.Right(at, AddressMath.PowerShift((uint)device.SectorSize)), p);
                if (U32(p, 0) != Magic || U32(p, 8) != Crc(p, true)) continue;
                if (U16(p, 6) != Layout.Version || U16(p, 4) != (ushort)PageType.Anchor || U64(p, 96) != 1)
                    throw new InvalidDataException("Unsupported CPX format/features; old prototypes require reformatting.");
                var candidate = new Layout((int)U32(p, 80), device.SectorSize, U64(p, 88), U64(p, 72));
                try { candidate.Validate(); } catch (ArgumentException e) { throw new InvalidDataException("Invalid anchor geometry.", e); }
                if (candidate.PageBytes != pageBytes || candidate.ByteOffset(candidate.Units) != bytes ||
                    U32(p, 84) != pageBytes || U32(p, 104) != device.SectorSize || U32(p, 108) != 1 ||
                    U64(p, 64) != 0 || U64(p, 112) != candidate.Reserve || U64(p, 120) != candidate.State ||
                    U64(p, 16) != candidate.UnitOffset(at) || U64(p, 24) != 0)
                    throw new InvalidDataException("Anchor fields disagree with device geometry.");
                if (found && candidate != layout) throw new InvalidDataException("Conflicting immutable anchors.");
                layout = candidate; found = true;
            }
        }
        if (!found) throw new InvalidDataException("No CPX4 anchor; CPX1/CPX2 are not converted implicitly.");
        if (scratch.Length < RequiredScratchBytes(layout.PageBytes)) throw new ArgumentException("Insufficient scratch.");
        this.scratch = scratch[..RequiredScratchBytes(layout.PageBytes)]; Index.Clear();
        RecoverJournal(readOnly);
        Load(layout.Charset, B(3));
        if (!IsPage(B(3), PageType.Charset, layout.Charset, 0) || U32(B(3), 64) != 1) throw new InvalidDataException("Invalid immutable charset profile.");
        Load(layout.State, B(3));
        if (!IsPage(B(3), PageType.State, layout.State, 0) || U64(B(3), 64) < 2 || U64(B(3), 72) == 0 ||
            !layout.AllocatablePage(U64(B(3), 80))) throw new InvalidDataException("Invalid volume state.");
        ValidateReserve();
        if (!readOnly && !deferCleanup) DrainWork();
        this.readOnly = readOnly;
    }
    private bool readOnly;

    /// <summary>Destructive disposable-image formatter. A is explicit; P=max(1024,A).</summary>
    public static void Format(TDevice device, int allocationUnitBytes, Span<byte> scratch, ulong volumeId)
    {
        if (device.SectorSize <= 0 || device.SectorCount > AddressMath.Right(ulong.MaxValue, AddressMath.PowerShift((uint)device.SectorSize)) || allocationUnitBytes <= 0)
            throw new ArgumentException("Invalid geometry.");
        ulong bytes = AddressMath.Left(device.SectorCount, AddressMath.PowerShift((uint)device.SectorSize));
        if ((bytes & ((uint)allocationUnitBytes - 1)) != 0) throw new ArgumentException("Partial allocation unit.");
        var l = new Layout(allocationUnitBytes, device.SectorSize, AddressMath.Right(bytes, AddressMath.PowerShift((uint)allocationUnitBytes)), volumeId); l.Validate();
        if (scratch.Length < RequiredScratchBytes(l.PageBytes)) throw new ArgumentException("Insufficient scratch.");
        Span<byte> p = scratch[..l.PageBytes], descriptor = scratch.Slice(l.PageBytes, l.PageBytes);
        p.Clear();
        // Cover every supported old backup-page position before destructive reformat.
        for (int offset = 0; offset < 4096; offset += l.PageBytes)
        { device.Cooperate(); device.Write(AddressMath.Right(65536UL + (uint)offset, AddressMath.PowerShift((uint)l.SectorBytes)), p); device.Cooperate(); device.Write(AddressMath.Right(bytes - 4096 + (uint)offset, AddressMath.PowerShift((uint)l.SectorBytes)), p); }
        device.Cooperate(); device.Flush();
        ulong root = l.DataStart, reservedEnd = root + 33 * l.Q, free = 0;
        if (reservedEnd >= l.Backup) throw new ArgumentException("No bootstrap metadata space.");
        for (ulong group = 0; group < l.Units; group += GroupUnits)
        {
            if (!l.HasGroup(group)) continue;
            Header(descriptor, PageType.Group, l.GroupDescriptor(group), group, 0);
            Put32(descriptor, 64, l.ValidUnits(group)); uint groupFree = 0;
            for (int map = 0; map < 16; map++)
            {
                Header(p, PageType.Bitmap, l.Bitmap(group, map), group, 0); Put32(p, 12, (uint)map);
                uint count = 0;
                for (int bit = 0; bit < BitmapUnits; bit++)
                {
                    ulong unit = group + (uint)(map * BitmapUnits + bit);
                    bool used = unit >= l.Units || l.Permanent(unit) || (unit >= root && unit < reservedEnd);
                    SetBit(p, bit, used); if (!used) count++;
                }
                Put32(descriptor, 80 + map * 4, count); groupFree += count;
                Seal(p); device.Cooperate(); device.Write(l.DeviceSector(l.Bitmap(group, map)), p);
            }
            Put32(descriptor, 68, groupFree); free += groupFree; Seal(descriptor);
            device.Cooperate(); device.Write(l.DeviceSector(l.GroupDescriptor(group)), descriptor);
        }
        Header(p, PageType.ObjectLeaf, root, 0, 0, 1);
        Put64(p, 64, RootDirectory); Put32(p, 64 + 76, 1); Put32(p, 64 + 80, 1); p[64 + 84] = 2;
        Seal(p); device.Cooperate(); device.Write(l.DeviceSector(root), p);
        Header(p, PageType.State, l.State, 0, 0);
        Put64(p, 64, 2); Put64(p, 72, 1); Put64(p, 80, root); Put64(p, 96, free);
        Seal(p); device.Cooperate(); device.Write(l.DeviceSector(l.State), p);
        Header(p, PageType.Reserve, l.Reserve, 0, 0); Put16(p, 64, ReserveTarget);
        for (int i = 0; i < ReserveTarget; i++) Put64(p, 72 + i * 8, root + (ulong)(i + 1) * l.Q);
        Seal(p); device.Cooperate(); device.Write(l.DeviceSector(l.Reserve), p);
        // Profile 1 is normative Latin-1 plus the fixed fold table, with no mutable locale.
        Header(p, PageType.Charset, l.Charset, 0, 0); Put32(p, 64, 1); Seal(p); device.Cooperate(); device.Write(l.DeviceSector(l.Charset), p);
        p.Clear(); device.Cooperate(); device.Write(l.DeviceSector(l.Commit(1)), p); device.Cooperate(); device.Write(l.DeviceSector(l.Commit(2)), p);
        for (int copy = 0; copy < 2; copy++)
        { ulong at = copy == 0 ? l.CheckpointA : l.CheckpointB; Header(p, PageType.Checkpoint, at, 0, 0); l.Identity(p); Put64(p, 80, 0); Seal(p); device.Cooperate(); device.Write(l.DeviceSector(at), p); }
        device.Cooperate(); device.Flush();
        for (int copy = 0; copy < 2; copy++)
        {
            ulong at = copy == 0 ? l.Backup : l.Anchor;
            Header(p, PageType.Anchor, at, 0, 0); l.Identity(p); Put32(p, 80, (uint)l.UnitBytes);
            Put32(p, 84, (uint)l.PageBytes); Put64(p, 88, l.Units); Put64(p, 96, 1);
            Put32(p, 104, (uint)l.SectorBytes); Put32(p, 108, 1); Put64(p, 112, l.Reserve); Put64(p, 120, l.State);
            Seal(p); device.Cooperate(); device.Write(l.DeviceSector(at), p); device.Cooperate(); device.Flush();
        }
    }
}
