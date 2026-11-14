using static CopperFS.Cpx4.Reference.Layout;

namespace CopperFS.Cpx4.Reference;

public ref partial struct Cpx4Volume<TDevice> where TDevice : IBlockDevice
{
    public AncestrySearch StartAncestrySearch(ulong parent, ulong forbiddenDirectory)
    {
        Ready(); return new() { Current = parent, Forbidden = forbiddenDirectory, Remaining = StateValue(64),
            Walk = StartWalk(StateValue(80), TreeKind.Object, 0, parent, 0, true) };
    }
    public ScanResult CheckAncestryStep(ref AncestrySearch search)
    {
        Ready(); search.PagesRead = 0;
        if (search.Walk.Volume != layout.VolumeId || search.Walk.Generation != generation) return ScanResult.Invalidated;
        if (search.Finished) return search.Current == search.Forbidden ? ScanResult.Found : ScanResult.End;
        using var frame = RentRecords(256); int budget = 4;
        while (budget > 0)
        {
            if (search.Current == search.Forbidden) { search.Finished = true; return ScanResult.Found; }
            if (search.Current == 1) { search.Finished = true; return ScanResult.End; }
            var result = WalkStep(ref search.Walk, frame.Bytes, ref budget); search.PagesRead = 4 - budget;
            if (result == ScanResult.Pending) return result;
            if (result != ScanResult.Found || U64(frame.Bytes, 0) != search.Current || frame.Bytes[84] != 2 || search.Remaining-- == 0)
                throw new InvalidDataException("Invalid directory ancestry.");
            search.Current = U64(frame.Bytes, 40);
            search.Walk = StartWalk(search.Walk.Root, TreeKind.Object, 0, search.Current, 0, true);
        }
        return ScanResult.Pending;
    }
    public FreeSearch StartFreeSearch(uint units, bool metadataPages = false)
    {
        Ready(); if (units == 0 || units > BitmapUnits || metadataPages && units != layout.Q) throw new ArgumentOutOfRangeException(nameof(units));
        return NewFreeSearch(units, metadataPages);
    }
    private FreeSearch NewFreeSearch(uint wanted, bool metadata) => new()
    { Started = true, Volume = layout.VolumeId, Generation = generation, Wanted = wanted, Metadata = metadata, NeedGroup = true };
    public ScanResult FindFreeStep(ref FreeSearch search, out ulong address, out uint units)
    { Ready(); return SearchFreeStep(ref search, out address, out units); }
    private ScanResult SearchFreeStep(ref FreeSearch s, out ulong address, out uint count)
    {
        address = 0; count = 0; s.PagesRead = 0;
        if (!s.Started || s.Volume != layout.VolumeId || s.Generation != generation) return ScanResult.Invalidated;
        if (s.Finished) return ScanResult.End;
        while (s.PagesRead < 4)
        {
            if (s.Group >= layout.Units || !layout.HasGroup(s.Group)) { s.Finished = true; return ScanResult.End; }
            if (s.NeedGroup)
            {
                Load(layout.GroupDescriptor(s.Group), B(0)); s.PagesRead++;
                if (!IsPage(B(0), PageType.Group, layout.GroupDescriptor(s.Group), s.Group) || U32(B(0), 64) != layout.ValidUnits(s.Group)) throw new InvalidDataException("Invalid group descriptor.");
                if (U32(B(0), 68) == 0) { s.Group += GroupUnits; continue; }
                s.NeedGroup = false; s.Map = 0; if (s.PagesRead == 4) return ScanResult.Pending;
            }
            int map = s.Map; ulong group = s.Group;
            Load(layout.Bitmap(group, map), B(0)); ValidateBitmap(B(0), group, map); s.PagesRead++;
            if (++s.Map == 16) { s.Group += GroupUnits; s.NeedGroup = true; }
            ulong first = group + (uint)(map * BitmapUnits), end = Math.Min(layout.Units, first + BitmapUnits);
            for (ulong unit = first; unit < end; unit += s.Metadata ? layout.Q : 1)
            {
                if (layout.Permanent(unit) || Bit(B(0), (int)(unit - first))) continue;
                uint found = 0;
                while (found < s.Wanted && found < end - unit && !layout.Permanent(unit + found) && !Bit(B(0), (int)(unit + found - first))) found++;
                if (found == 0 || s.Metadata && found != s.Wanted) continue;
                address = unit; count = found; s.Finished = true; return ScanResult.Found;
            }
        }
        return ScanResult.Pending;
    }
    private TreeWalk StartWalk(ulong root, TreeKind kind, ulong owner, ulong high, ulong low, bool inclusive, bool reverse = false) => new()
    { Root = root, Next = root, Kind = kind, Owner = owner, High = high, Low = low, Inclusive = inclusive,
      Reverse = reverse, Seeking = true, Expected = -1, Phase = root == 0 ? 3 : 0, Volume = layout.VolumeId, Generation = generation };

    // No borrowed page survives a return. Only the eight scalar path frames persist.
    private ScanResult WalkStep(ref TreeWalk w, scoped Span<byte> record, ref int pages)
    {
        if (w.Volume != layout.VolumeId || w.Generation != generation) return ScanResult.Invalidated;
        int width = LeafBytes(w.Kind), keyBytes = KeyBytes(w.Kind);
        while (pages > 0)
        {
            if (w.Phase == 3) return ScanResult.End;
            if (w.Phase == 2)
            {
                if (w.Depth <= 1) { w.Phase = 3; return ScanResult.End; }
                w.Depth--; var frame = w.Path[w.Depth - 1];
                TreePage(frame.Address, w.Kind, w.Owner, B(0), frame.Level); pages--;
                int next = frame.Slot + (w.Reverse ? -1 : 1);
                if (next < 0 || next >= U16(B(0), 48)) continue;
                w.Path[w.Depth - 1].Slot = next;
                w.Next = U64(B(0), 64 + next * (keyBytes + 8) + keyBytes); w.Expected = frame.Level - 1;
                w.Seeking = false; w.Phase = 0; continue;
            }
            if (w.Phase == 0)
            {
                if (w.Depth == MaxHeight) throw new InvalidDataException("Tree depth overflow.");
                TreePage(w.Next, w.Kind, w.Owner, B(0), w.Expected); pages--;
                int level = U16(B(0), 50), count = U16(B(0), 48), size = level == 0 ? width : keyBytes + 8;
                int slot = w.Reverse ? count - 1 : 0;
                if (w.Seeking)
                {
                    slot = 0;
                    if (level != 0)
                        while (slot + 1 < count && CompareKey(B(0).Slice(64 + (slot + 1) * size, size), w.Kind, w.High, w.Low) <= 0) slot++;
                    else
                    {
                        while (slot < count && CompareKey(B(0).Slice(64 + slot * size, size), w.Kind, w.High, w.Low) < 0) slot++;
                        bool equal = slot < count && CompareKey(B(0).Slice(64 + slot * size, size), w.Kind, w.High, w.Low) == 0;
                        if (w.Reverse) { if (!equal || !w.Inclusive) slot--; }
                        else if (equal && !w.Inclusive) slot++;
                    }
                }
                w.Path[w.Depth++] = new() { Address = w.Next, Slot = slot, Level = level };
                if (level != 0) { w.Next = U64(B(0), 64 + slot * size + keyBytes); w.Expected = level - 1; continue; }
                w.Phase = 1;
            }
            else { TreePage(w.Path[w.Depth - 1].Address, w.Kind, w.Owner, B(0), 0); pages--; }
            int index = w.Path[w.Depth - 1].Slot;
            if (index < 0 || index >= U16(B(0), 48)) { w.Phase = 2; continue; }
            B(0).Slice(64 + index * width, width).CopyTo(record);
            w.Path[w.Depth - 1].Slot += w.Reverse ? -1 : 1;
            return ScanResult.Found;
        }
        return ScanResult.Pending;
    }
    private bool WalkOne(ulong root, TreeKind kind, ulong owner, ulong high, ulong low, bool inclusive, bool reverse, scoped Span<byte> record)
    {
        using var frame = RentRecords(System.Runtime.CompilerServices.Unsafe.SizeOf<TreeWalk>());
        ref var walk = ref System.Runtime.InteropServices.MemoryMarshal.AsRef<TreeWalk>(frame.Bytes);
        walk = StartWalk(root, kind, owner, high, low, inclusive, reverse);
        while (true)
        {
            int pages = 4; var result = WalkStep(ref walk, record, ref pages);
            if (result == ScanResult.Found) return true;
            if (result == ScanResult.End) return false;
            if (result == ScanResult.Invalidated) throw new InvalidOperationException("Stale tree continuation.");
            Cooperate();
        }
    }
    // Initialize in caller-owned storage: returning this large cursor by value
    // can introduce a second, hidden stack copy in a native calling convention.
    public void StartNameSearch(ulong parent, scoped ReadOnlySpan<byte> name, ref NameSearch search)
    {
        Ready(); int n = LookupName(name); if (n < 0) throw new ArgumentException("Invalid name.");
        using var frame = RentRecords(256); var directory = frame.Bytes;
        if (!GetObject(parent, directory) || directory[84] != 2) throw new ArgumentException("Not a directory.");
        InitializeNameSearch(ref search, parent, U64(directory, 32), NameScratch[..n]);
    }
    private void InitializeNameSearch(ref NameSearch search, ulong parent, ulong root, scoped ReadOnlySpan<byte> utf8)
    {
        bool alias = Alias(utf8, out ulong id); uint hash = NameHash(utf8);
        search = default; search.Walk = StartWalk(root, TreeKind.Directory, parent, alias ? 0 : (ulong)hash << 32, 0, true);
        search.Alias = id; search.IsAlias = alias; search.Hash = hash; search.InputCrc = Crc(utf8);
        search.InputLength = utf8.Length; utf8.CopyTo(search.Input);
    }
    public ScanResult FindNameStep(ref NameSearch search, scoped ReadOnlySpan<byte> name, out ulong objectId)
    {
        Ready(); int n = LookupName(name); objectId = 0;
        if (n < 0 || n != search.InputLength || !NameScratch[..n].SequenceEqual(((ReadOnlySpan<byte>)search.Input)[..n])) return ScanResult.Invalidated;
        return SearchNameStep(ref search, NameScratch[..n], out objectId);
    }
    private ScanResult SearchNameStep(ref NameSearch s, scoped ReadOnlySpan<byte> utf8, out ulong objectId)
    {
        objectId = 0; s.PagesRead = s.CandidatesCompared = 0;
        if (s.Walk.Volume != layout.VolumeId || s.Walk.Generation != generation) return ScanResult.Invalidated;
        if (s.Finished) { objectId = s.FoundId; return objectId == 0 ? ScanResult.End : ScanResult.Found; }
        int budget = 4;
        while (s.CandidatesCompared < 64)
        {
            if (!s.HasPending)
            {
                var result = WalkStep(ref s.Walk, s.PendingEntry, ref budget); s.PagesRead = 4 - budget;
                if (result == ScanResult.End) { s.Finished = true; return result; }
                if (result != ScanResult.Found) return result;
                s.HasPending = true;
            }
            if (!s.IsAlias && U64(s.PendingEntry, 0) != (ulong)s.Hash << 32) { s.Finished = true; return ScanResult.End; }
            bool match;
            if (s.IsAlias) match = U64(s.PendingEntry, 8) == s.Alias;
            else
            {
                if (U64(s.PendingEntry, 24) != 0) { if (budget == 0) return ScanResult.Pending; budget--; s.PagesRead++; }
                int length = StoredName(s.PendingEntry, OtherNameScratch); match = SameName(utf8, OtherNameScratch[..length]);
            }
            s.CandidatesCompared++; s.HasPending = false;
            if (match) { s.Finished = true; objectId = s.FoundId = U64(s.PendingEntry, 16); return ScanResult.Found; }
            if (budget == 0) return ScanResult.Pending;
        }
        return ScanResult.Pending;
    }
}
