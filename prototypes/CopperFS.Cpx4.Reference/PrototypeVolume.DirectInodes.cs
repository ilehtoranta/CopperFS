using static CopperFS.Cpx4.Reference.Layout;

namespace CopperFS.Cpx4.Reference;

public ref partial struct Cpx4Volume<TDevice> where TDevice : IBlockDevice
{
    // Deliberately independent of the production identity and map cursor code.
    private ulong DirectPair(ulong root, ulong id)
    {
        if (id == 0) return ulong.MaxValue;
        if (id == 1) return root;
        int bits = 0; for (ulong n = layout.ShadowPairs - 1; n != 0; n >>= 1) bits++;
        ulong slot = id & ((1UL << bits) - 1);
        if (slot >= layout.ShadowPairs) return ulong.MaxValue;
        ulong pair = slot << layout.ShadowPairShift;
        return pair == root ? ulong.MaxValue : pair;
    }

    private void ReadDirectInode(ulong root, ulong unit, ulong physical)
    {
        if (!layout.AllocatablePage(unit)) throw new InvalidDataException("Inode catalogue names a reserved pair.");
        RawRead(physical, B(0)); ulong owner = U64(B(0), 24);
        if (!VolumePage(B(0), PageType.Inode, unit, owner) || U64(B(0), 32) > generation ||
            U64(B(0), 64) != owner || DirectPair(root, owner) != unit || U16(B(0), 48) != 1 || U16(B(0), 50) != 0)
            throw new InvalidDataException("Invalid direct inode identity or payload.");
    }

    private bool GetDirectInode(ulong root, ulong id, scoped Span<byte> record)
    {
        ulong unit = DirectPair(root, id);
        if (unit == ulong.MaxValue || !layout.AllocatablePage(unit)) return false;
        ulong physical = ShadowSelection(unit, out bool inode);
        if (!inode) return false;
        ReadDirectInode(root, unit, physical);
        if (U64(B(0), 24) != id) return false;
        B(0).Slice(64, 256).CopyTo(record); return true;
    }

    private bool NextDirectInode(ulong root, ulong high, bool inclusive, scoped Span<byte> record, out ulong address)
    {
        address = 0; ulong best = ulong.MaxValue; bool found = false;
        if (layout.InlineCatalogue && shadowRoot == ulong.MaxValue)
        {
            for (int n = 0; n < U32(ShadowControl, 496); n++)
            {
                ulong entry = U64(ShadowControl, 504 + n * 8);
                if ((entry & 2) == 0) continue;
                ulong pair = (entry >> 2) << layout.ShadowPairShift;
                ReadDirectInode(root, pair, pair + (entry & 1) * layout.Q);
                ulong id = U64(B(0), 24);
                if (id < high || id == high && !inclusive || found && id >= best) continue;
                best = id; address = pair; found = true; B(0).Slice(64, 256).CopyTo(record);
            }
            return found;
        }
        ulong perLeaf = 1UL << layout.ShadowLeafShift;
        for (ulong first = 0; first < layout.ShadowPairs; first += perLeaf)
        {
            ShadowSelection(first << layout.ShadowPairShift, out _);
            if (!shadowLeafPresent) continue;
            ulong count = Math.Min(perLeaf, layout.ShadowPairs - first);
            for (uint n = 0; n < count; n++)
            {
                int selector = (ShadowMapPage[64 + (int)(n >> 2)] >> (int)(6 - (n & 3) * 2)) & 3;
                if ((selector & 2) == 0) continue;
                ulong pair = (first + n) << layout.ShadowPairShift;
                ReadDirectInode(root, pair, pair + (uint)(selector & 1) * layout.Q);
                ulong id = U64(B(0), 24);
                if (id < high || id == high && !inclusive || found && id >= best) continue;
                best = id; address = pair; found = true;
                B(0).Slice(64, 256).CopyTo(record);
            }
        }
        return found;
    }

    private static ulong FormatInodeCatalogue(TDevice device, Layout l, ulong inode, scoped Span<byte> page)
    {
        ulong index = inode >> l.ShadowPairShift, selected = 0;
        for (int level = 0; level < l.ShadowHeight; level++)
        {
            int bits = level == 0 ? l.ShadowLeafShift : l.ShadowBranchShift;
            ulong node = index >> (l.ShadowLeafShift + level * l.ShadowBranchShift);
            ulong entriesTotal = level == 0 ? l.ShadowPairs : l.ShadowNodes(level - 1);
            int entries = (int)Math.Min(1UL << bits, entriesTotal - (node << bits));
            ulong pair = l.ShadowNodePair(level, node);
            l.MediaHeader(page, level == 0 ? PageType.SelectorLeaf : PageType.SelectorBranch,
                0x8000000000000000UL | l.DeviceSector(pair), l.VolumeId, 1, entries, level);
            Put32(page, 12, level == 0 ? 2u : 0u);
            ulong child = level == 0 ? index & ((1UL << bits) - 1) :
                (index >> (l.ShadowLeafShift + (level - 1) * l.ShadowBranchShift)) & ((1UL << bits) - 1);
            page[64 + (int)(child >> 2)] = (byte)((level == 0 ? 2 : 1) << (int)(6 - (child & 3) * 2));
            Put16(page, 52, (ushort)((entries + 3) >> 2)); Put32(page, 8, Crc(page, true));
            device.Cooperate(); device.Write(l.DeviceSector(pair), page); selected = pair;
        }
        return selected;
    }
}
