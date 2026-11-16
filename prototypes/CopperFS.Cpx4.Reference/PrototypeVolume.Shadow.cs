using static CopperFS.Cpx4.Reference.Layout;

namespace CopperFS.Cpx4.Reference;

public ref partial struct Cpx4Volume<TDevice> where TDevice : IBlockDevice
{
    private bool VolumeCrc(scoped ReadOnlySpan<byte> page) => ValidCrc(page, layout.DiskVersion);
    private bool VolumePage(scoped ReadOnlySpan<byte> page, PageType type, ulong address, ulong owner)
        => IsPage(page, type, address, owner, layout.DiskVersion);

    private void RecoverShadow()
    {
        bool found = false;
        for (int copy = 0; copy < 3; copy++)
        {
            ulong at = layout.CheckpointA + (ulong)copy * layout.Q;
            RawRead(at, B(5));
            if (!VolumePage(B(5), PageType.Checkpoint, at, 0) || !layout.IdentityMatches(B(5)) ||
                U64(B(5), 32) != U64(B(5), 80)) continue;
            ulong next = U64(B(5), 80), rootSector = U64(B(5), 88);
            ulong root = rootSector >> layout.DeviceShift;
            ulong expected = layout.ShadowNodePair(layout.ShadowHeight - 1, 0);
            if ((rootSector & (layout.DeviceUnits - 1)) != 0 ||
                root != 0 && root != expected && root != expected + layout.Q)
                throw new InvalidDataException("Invalid shadow checkpoint mapping root.");
            if (found && next == generation && shadowRoot != root)
                throw new InvalidDataException("Conflicting shadow publication roots.");
            if (!found || next > generation) { generation = next; shadowRoot = root; found = true; }
        }
        if (!found) throw new InvalidDataException("No valid shadow checkpoint.");
    }

    private ulong ShadowAddress(ulong unit)
    {
        if (!layout.Shadow || unit == layout.Anchor || unit == layout.Backup || unit == layout.Charset ||
            unit >= layout.CheckpointA && unit <= layout.CheckpointC) return unit;
        if ((unit & (layout.MetadataSpan - 1)) != 0 || unit >= layout.ShadowMapBase && unit < layout.ShadowMapEnd)
            throw new InvalidDataException("Invalid paired metadata address.");
        ulong physical = shadowRoot, node = 0, pairIndex = unit >> layout.ShadowPairShift;
        // A separate seventh page preserves each caller's live tree/record scratch.
        Span<byte> page = scratch.Slice(6 * layout.PageBytes + 12 * 1024, layout.PageBytes);
        for (int level = layout.ShadowHeight - 1; physical != 0; level--)
        {
            ulong pair = layout.ShadowNodePair(level, node);
            ulong total = level == 0 ? layout.ShadowPairs : layout.ShadowNodes(level - 1);
            int shift = level == 0 ? layout.ShadowLeafShift : layout.ShadowBranchShift;
            uint entries = (uint)Math.Min(1UL << shift, total - (node << shift));
            uint payload = level == 0 ? (entries + 7) >> 3 : (entries + 3) >> 2;
            RawRead(physical, page);
            if (!VolumeCrc(page) || U16(page, 4) != (level == 0 ? 20 : 21) ||
                U64(page, 16) != (0x8000000000000000UL | layout.DeviceSector(pair)) ||
                U64(page, 24) != layout.VolumeId || U64(page, 32) == 0 || U64(page, 32) > generation ||
                U16(page, 48) != entries || U16(page, 50) != level || U16(page, 52) != payload)
                throw new InvalidDataException("Invalid selected shadow map node.");
            if (level == 0)
            {
                uint bit = (uint)(pairIndex & ((1UL << shift) - 1));
                if (bit >= entries) throw new InvalidDataException("Shadow leaf index overflow.");
                return unit + (ulong)((page[64 + (int)(bit >> 3)] >> (int)(7 - (bit & 7))) & 1) * layout.Q;
            }
            uint child = (uint)((pairIndex >> (layout.ShadowLeafShift + (level - 1) * layout.ShadowBranchShift)) & ((1UL << shift) - 1));
            if (child >= entries) throw new InvalidDataException("Shadow branch index overflow.");
            int selector = (page[64 + (int)(child >> 2)] >> (int)(6 - (child & 3) * 2)) & 3;
            if (selector == 3) throw new InvalidDataException("Invalid shadow branch selector.");
            if (selector == 0) return unit;
            node = (node << shift) + child;
            physical = layout.ShadowNodePair(level - 1, node) + (uint)(selector - 1) * layout.Q;
        }
        return unit;
    }
}
