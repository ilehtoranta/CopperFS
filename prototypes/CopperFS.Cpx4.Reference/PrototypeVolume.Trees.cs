using static CopperFS.Cpx4.Reference.Layout;

namespace CopperFS.Cpx4.Reference;

public ref partial struct Cpx4Volume<TDevice> where TDevice : IBlockDevice
{
    private static int KeyBytes(TreeKind kind) => kind == TreeKind.Directory ? 16 : 8;
    private static int LeafBytes(TreeKind kind) => kind switch { TreeKind.Object => 256, TreeKind.Directory => 128, _ => 24 };
    private static PageType TreeType(TreeKind kind, bool branch) => kind switch
    { TreeKind.Object => branch ? PageType.ObjectBranch : PageType.ObjectLeaf,
      TreeKind.Directory => branch ? PageType.DirectoryBranch : PageType.DirectoryLeaf,
      _ => branch ? PageType.ExtentBranch : PageType.ExtentLeaf };
    private static int CompareKey(scoped ReadOnlySpan<byte> entry, TreeKind kind, ulong high, ulong low)
    {
        ulong a = U64(entry, 0); int result = a.CompareTo(high);
        return result != 0 || kind != TreeKind.Directory ? result : U64(entry, 8).CompareTo(low);
    }
    private void TreePage(ulong address, TreeKind kind, ulong owner, scoped Span<byte> page, int expectedLevel = -1)
    {
        Load(address, page); int level = U16(page, 50), count = U16(page, 48);
        int width = level == 0 ? LeafBytes(kind) : KeyBytes(kind) + 8;
        if (level >= MaxHeight || expectedLevel >= 0 && level != expectedLevel ||
            !IsPage(page, TreeType(kind, level != 0), address, owner) || U64(page, 40) != 0 ||
            count > (layout.PageBytes - 64) / width || level != 0 && count == 0)
            throw new InvalidDataException("Invalid tree page/level/count/owner.");
        for (int i = 0; i < count; i++)
        {
            var record = page.Slice(64 + i * width, width);
            if (i > 0)
            {
                var before = page.Slice(64 + (i - 1) * width, width);
                if (CompareKey(before, kind, U64(record, 0), kind == TreeKind.Directory ? U64(record, 8) : 0) >= 0)
                    throw new InvalidDataException("Unsorted/duplicate tree keys.");
            }
            if (level > 0 && !layout.AllocatablePage(U64(record, KeyBytes(kind)))) throw new InvalidDataException("Invalid child address.");
        }
    }
    // Returns a leaf in B0 and its lower-bound slot. Path stores only scalar addresses/indices.
    private int Seek(ulong root, TreeKind kind, ulong owner, ulong high, ulong low, out int depth)
    {
        depth = 0; int expected = -1;
        while (true)
        {
            if (depth == MaxHeight) throw new InvalidDataException("Tree exceeds eight levels.");
            TreePage(root, kind, owner, B(0), expected);
            int level = U16(B(0), 50), count = U16(B(0), 48), width = level == 0 ? LeafBytes(kind) : KeyBytes(kind) + 8;
            int slot = 0;
            if (level == 0)
            { while (slot < count && CompareKey(B(0).Slice(64 + slot * width, width), kind, high, low) < 0) slot++; }
            else
            { while (slot + 1 < count && CompareKey(B(0).Slice(64 + (slot + 1) * width, width), kind, high, low) <= 0) slot++; }
            Put64(Path, depth * 16, root); Put16(Path, depth * 16 + 8, (ushort)slot); Put16(Path, depth * 16 + 10, (ushort)level); depth++;
            if (level == 0) return slot;
            root = U64(B(0), 64 + slot * width + KeyBytes(kind)); expected = level - 1;
        }
    }
    private bool TreeGet(ulong root, TreeKind kind, ulong owner, ulong high, ulong low, scoped Span<byte> record)
    {
        if (root == 0) return false;
        int slot = Seek(root, kind, owner, high, low, out _), width = LeafBytes(kind);
        if (slot == U16(B(0), 48) || CompareKey(B(0).Slice(64 + slot * width, width), kind, high, low) != 0) return false;
        B(0).Slice(64 + slot * width, width).CopyTo(record); return true;
    }
    private void TreeReplace(ulong root, TreeKind kind, ulong owner, scoped ReadOnlySpan<byte> record)
    {
        int width = LeafBytes(kind), slot = Seek(root, kind, owner, U64(record, 0), kind == TreeKind.Directory ? U64(record, 8) : 0, out int depth);
        if (slot == U16(B(0), 48) || CompareKey(B(0).Slice(64 + slot * width, width), kind, U64(record, 0), kind == TreeKind.Directory ? U64(record, 8) : 0) != 0)
            throw new InvalidDataException("Record to update is absent.");
        record.CopyTo(B(0).Slice(64 + slot * width, width)); Stage(U64(Path, (depth - 1) * 16), B(0));
    }
    private void TreeDelete(ulong root, TreeKind kind, ulong owner, ulong high, ulong low)
    {
        int width = LeafBytes(kind), slot = Seek(root, kind, owner, high, low, out int depth), count = U16(B(0), 48);
        if (slot == count || CompareKey(B(0).Slice(64 + slot * width, width), kind, high, low) != 0) throw new InvalidDataException("Record to delete is absent.");
        B(0).Slice(64 + (slot + 1) * width, (count - slot - 1) * width).CopyTo(B(0).Slice(64 + slot * width));
        B(0).Slice(64 + (count - 1) * width, width).Clear(); Put16(B(0), 48, (ushort)(count - 1));
        Stage(U64(Path, (depth - 1) * 16), B(0));
    }
    private void TreeInsert(ref ulong root, TreeKind kind, ulong owner, scoped ReadOnlySpan<byte> record)
    {
        int width = LeafBytes(kind); ulong high = U64(record, 0), low = kind == TreeKind.Directory ? U64(record, 8) : 0;
        if (root == 0)
        {
            root = TakeMetadata(); Header(B(1), TreeType(kind, false), root, owner, generation + 1, 1);
            record.CopyTo(B(1)[64..]); Stage(root, B(1)); return;
        }
        int slot = Seek(root, kind, owner, high, low, out int depth);
        if (slot < U16(B(0), 48) && CompareKey(B(0).Slice(64 + slot * width, width), kind, high, low) == 0)
            throw new InvalidDataException("Duplicate tree insertion.");
        // If every page on a height-eight path is full, reject before assigning pages.
        if (depth == MaxHeight)
        {
            bool grows = true;
            for (int d = depth - 1; d >= 0; d--)
            {
                TreePage(U64(Path, d * 16), kind, owner, B(0));
                int size = d == depth - 1 ? width : KeyBytes(kind) + 8;
                if (U16(B(0), 48) < (layout.PageBytes - 64) / size) { grows = false; break; }
            }
            if (grows) throw new AdmissionException(MutationResult.TreeTooDeep);
        }
        using var recordFrame1 = RentRecords(256); Span<byte> extra = recordFrame1.Bytes.Slice(0, 256); record.CopyTo(extra);
        for (int d = depth - 1; d >= 0; d--)
        {
            ulong address = U64(Path, d * 16); TreePage(address, kind, owner, B(0));
            int level = U16(B(0), 50), count = U16(B(0), 48);
            width = level == 0 ? LeafBytes(kind) : KeyBytes(kind) + 8;
            int at = d == depth - 1 ? slot : U16(Path, d * 16 + 8) + 1;
            if (count < (layout.PageBytes - 64) / width)
            {
                B(0).CopyTo(B(1)); B(1).Slice(64 + at * width, (count - at) * width).CopyTo(B(1).Slice(64 + (at + 1) * width));
                extra[..width].CopyTo(B(1).Slice(64 + at * width)); Put16(B(1), 48, (ushort)(count + 1)); Stage(address, B(1)); return;
            }
            ulong sibling = TakeMetadata(); int left = (count + 1) / 2;
            Header(B(1), TreeType(kind, level != 0), address, owner, generation + 1, left, level);
            Header(B(2), TreeType(kind, level != 0), sibling, owner, generation + 1, count + 1 - left, level);
            for (int i = 0; i <= count; i++)
            {
                var source = i == at ? extra[..width] : B(0).Slice(64 + (i < at ? i : i - 1) * width, width);
                source.CopyTo(i < left ? B(1).Slice(64 + i * width, width) : B(2).Slice(64 + (i - left) * width, width));
            }
            B(2).Slice(64, KeyBytes(kind)).CopyTo(extra); Put64(extra, KeyBytes(kind), sibling);
            Stage(address, B(1)); Stage(sibling, B(2));
            if (d == 0)
            {
                ulong newRoot = TakeMetadata(); int branchWidth = KeyBytes(kind) + 8;
                Header(B(1), TreeType(kind, true), newRoot, owner, generation + 1, 2, level + 1);
                Put64(B(1), 64 + KeyBytes(kind), root); extra[..branchWidth].CopyTo(B(1).Slice(64 + branchWidth));
                Stage(newRoot, B(1)); root = newRoot; return;
            }
        }
    }
    // Upper/lower-bound scan across leaves, using the scalar path rather than sibling links.
    private bool TreeNext(ulong root, TreeKind kind, ulong owner, ulong high, ulong low, bool inclusive, scoped Span<byte> record)
        => WalkOne(root, kind, owner, high, low, inclusive, false, record);
    private bool TreePruneEmpty(ref ulong root, TreeKind kind, ulong owner)
    {
        if (root == 0) return false;
        // Find an empty leaf with a bounded depth-first path, including empty key intervals.
        int depth = 0; ulong address = root;
        while (true)
        {
            TreePage(address, kind, owner, B(0)); int level = U16(B(0), 50);
            if (depth == MaxHeight) throw new InvalidDataException("Tree depth overflow.");
            Put64(Path, depth * 16, address); Put16(Path, depth * 16 + 8, 0); depth++;
            if (level != 0) { address = U64(B(0), 64 + KeyBytes(kind)); continue; }
            if (U16(B(0), 48) == 0) break;
            int parent = depth - 2;
            for (; parent >= 0; parent--)
            {
                TreePage(U64(Path, parent * 16), kind, owner, B(0));
                int next = U16(Path, parent * 16 + 8) + 1;
                if (next >= U16(B(0), 48)) continue;
                Put16(Path, parent * 16 + 8, (ushort)next); address = U64(B(0), 64 + next * (KeyBytes(kind) + 8) + KeyBytes(kind));
                depth = parent + 1; break;
            }
            if (parent < 0) return false;
        }
        ReleaseMetadata(U64(Path, (depth - 1) * 16));
        for (int d = depth - 2; d >= 0; d--)
        {
            address = U64(Path, d * 16); TreePage(address, kind, owner, B(0));
            int count = U16(B(0), 48), slot = U16(Path, d * 16 + 8), width = KeyBytes(kind) + 8;
            if (count == 1) { ReleaseMetadata(address); continue; }
            ulong firstHigh = U64(B(0), 64), firstLow = kind == TreeKind.Directory ? U64(B(0), 72) : 0;
            B(0).Slice(64 + (slot + 1) * width, (count - slot - 1) * width).CopyTo(B(0).Slice(64 + slot * width));
            B(0).Slice(64 + (count - 1) * width, width).Clear(); Put16(B(0), 48, (ushort)(count - 1));
            // The inherited first fence must cover the removed first interval too.
            if (slot == 0) { Put64(B(0), 64, firstHigh); if (kind == TreeKind.Directory) Put64(B(0), 72, firstLow); }
            Stage(address, B(0)); return true;
        }
        root = 0; return true;
    }
}
