using static CopperFS.Cpx4.Reference.Layout;

namespace CopperFS.Cpx4.Reference;

public ref partial struct Cpx4Volume<TDevice> where TDevice : IBlockDevice
{
    private Span<byte> ShadowControl => scratch.Slice(7 * layout.PageBytes + 12 * 1024, layout.CheckpointBytes);
    private void LoadRootControl(ulong unit, scoped Span<byte> page)
    {
        bool state = unit == layout.State;
        layout.MediaHeader(page, state ? PageType.State : PageType.Reserve, unit, 0, generation);
        int length = state ? U16(ShadowControl, 56) : 264;
        ShadowControl.Slice(state ? 96 : 232, length).CopyTo(page[64..]);
        if (state) Put16(page, 52, (ushort)length);
        Seal(page);
    }
    private bool VolumeCrc(scoped ReadOnlySpan<byte> page) => ValidCrc(page, layout.DiskVersion);
    private bool VolumePage(scoped ReadOnlySpan<byte> page, PageType type, ulong address, ulong owner)
        => IsPage(page, type, address, owner, layout.DiskVersion);

    private void RecoverShadow()
    {
        bool found = false;
        Span<byte> checkpoint = scratch[..layout.CheckpointBytes];
        for (int copy = 0; copy < 3; copy++)
        {
            ulong at = layout.CheckpointA + (ulong)copy * layout.CheckpointSpan;
            RawRead(at, checkpoint);
            if (!VolumePage(checkpoint, PageType.Checkpoint, at, 0) || !layout.IdentityMatches(checkpoint) ||
                U64(checkpoint, 32) != U64(checkpoint, 80) || U16(checkpoint, 52) != (layout.InlineCatalogue ? 960 : layout.RootControl ? 432 : 32)) continue;
            if (layout.RootControl && (U16(checkpoint, 56) is not (48 or 128 or 136) || U16(checkpoint, 58) != 32 || U16(checkpoint, 232) > 32))
                throw new InvalidDataException("Invalid checkpoint volume controls.");
            ulong next = U64(checkpoint, 80), rootSector = U64(checkpoint, 88);
            bool inline = layout.InlineCatalogue && rootSector == ulong.MaxValue;
            ulong root = inline ? ulong.MaxValue : rootSector >> layout.DeviceShift;
            ulong expected = layout.ShadowNodePair(layout.ShadowHeight - 1, 0);
            if (!inline && ((rootSector & (layout.DeviceUnits - 1)) != 0 ||
                root != 0 && root != expected && root != expected + layout.Q))
                throw new InvalidDataException("Invalid shadow checkpoint mapping root.");
            if (layout.InlineCatalogue) ValidateCheckpointCatalogue(checkpoint, inline);
            if (found && next == generation && shadowRoot != root)
                throw new InvalidDataException("Conflicting shadow publication roots.");
            if (layout.RootControl && found && next == generation &&
                (U32(checkpoint, 56) != U32(ShadowControl, 56) || !checkpoint.Slice(96, layout.InlineCatalogue ? 928 : 400).SequenceEqual(ShadowControl.Slice(96, layout.InlineCatalogue ? 928 : 400))))
                throw new InvalidDataException("Conflicting checkpoint volume controls.");
            if (!found || next > generation)
            {
                generation = next; shadowRoot = root; found = true;
                if (layout.RootControl) checkpoint.CopyTo(ShadowControl);
            }
        }
        if (!found) throw new InvalidDataException("No valid shadow checkpoint.");
    }

    private void ValidateCheckpointCatalogue(scoped ReadOnlySpan<byte> page, bool inline)
    {
        uint count = U32(page, 496);
        if (count > 64 || !inline && count != 0 || U32(page, 500) != 0x494e4c31)
            throw new InvalidDataException("Invalid checkpoint catalogue header.");
        for (int n = 0; n < count; n++)
        {
            ulong entry = U64(page, 504 + n * 8), pair = entry >> 2;
            if ((entry & 3) == 0 || pair >= layout.ShadowPairs) throw new InvalidDataException("Invalid checkpoint catalogue entry.");
            for (int before = 0; before < n; before++)
                if ((U64(page, 504 + before * 8) >> 2) == pair) throw new InvalidDataException("Duplicate checkpoint catalogue entry.");
        }
        if (page.Slice(504 + (int)count * 8, 520 - (int)count * 8).IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Nonzero checkpoint catalogue padding.");
    }

    private ulong ShadowAddress(ulong unit) => ShadowSelection(unit, out _);
    private Span<byte> ShadowMapPage => scratch.Slice(6 * layout.PageBytes + 12 * 1024, layout.PageBytes);
    private ulong ShadowSelection(ulong unit, out bool inode)
    {
        inode = false; shadowLeafPresent = false;
        if (!layout.Shadow || unit == layout.Anchor || unit == layout.Backup || unit == layout.Charset ||
            unit >= layout.CheckpointA && unit <= layout.CheckpointC) return unit;
        if ((unit & (layout.MetadataSpan - 1)) != 0 || unit >= layout.ShadowMapBase && unit < layout.ShadowMapEnd)
            throw new InvalidDataException("Invalid paired metadata address.");
        ulong physical = shadowRoot, node = 0, pairIndex = unit >> layout.ShadowPairShift;
        if (layout.InlineCatalogue && physical == ulong.MaxValue)
        {
            uint count = U32(ShadowControl, 496);
            for (int n = 0; n < count; n++)
            {
                ulong entry = U64(ShadowControl, 504 + n * 8);
                if ((entry >> 2) != pairIndex) continue;
                inode = (entry & 2) != 0; return unit + (entry & 1) * layout.Q;
            }
            return unit;
        }
        // A separate seventh page preserves each caller's live tree/record scratch.
        Span<byte> page = scratch.Slice(6 * layout.PageBytes + 12 * 1024, layout.PageBytes);
        for (int level = layout.ShadowHeight - 1; physical != 0; level--)
        {
            ulong pair = layout.ShadowNodePair(level, node);
            ulong total = level == 0 ? layout.ShadowPairs : layout.ShadowNodes(level - 1);
            int shift = level == 0 ? layout.ShadowLeafShift : layout.ShadowBranchShift;
            uint entries = (uint)Math.Min(1UL << shift, total - (node << shift));
            uint payload = level == 0 && !layout.DirectInodes ? (entries + 7) >> 3 : (entries + 3) >> 2;
            RawRead(physical, page);
            if (!VolumeCrc(page) || U16(page, 4) != (level == 0 ? 20 : 21) ||
                U64(page, 16) != (0x8000000000000000UL | layout.DeviceSector(pair)) ||
                U64(page, 24) != layout.VolumeId || U64(page, 32) == 0 || U64(page, 32) > generation ||
                U32(page, 12) != (level == 0 && layout.DirectInodes ? 2u : 0u) || U16(page, 48) != entries || U16(page, 50) != level || U16(page, 52) != payload)
                throw new InvalidDataException("Invalid selected shadow map node.");
            if (level == 0)
            {
                uint bit = (uint)(pairIndex & ((1UL << shift) - 1));
                if (bit >= entries) throw new InvalidDataException("Shadow leaf index overflow.");
                shadowLeafPresent = true;
                if (layout.DirectInodes)
                {
                    int value = (page[64 + (int)(bit >> 2)] >> (int)(6 - (bit & 3) * 2)) & 3;
                    inode = (value & 2) != 0; return unit + (uint)(value & 1) * layout.Q;
                }
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
