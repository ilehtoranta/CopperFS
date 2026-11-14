using static CopperFS.Cpx4.Reference.Layout;

namespace CopperFS.Cpx4.Reference;

public ref partial struct Cpx4Volume<TDevice> where TDevice : IBlockDevice
{
    // Deliberately slow offline verification: reconstruct one 4096-unit bitmap window
    // at a time. Memory stays constant as both the volume and namespace grow.
    private void MarkOwned(ulong first, ulong length, ulong window)
    {
        if (length == 0 || first >= layout.Units || length > layout.Units - first) throw new InvalidDataException("Invalid ownership range.");
        ulong begin = Math.Max(first, window), end = Math.Min(first + length, window + BitmapUnits);
        for (ulong unit = begin; unit < end; unit++)
        {
            int bit = (int)(unit - window);
            if (Bit(B(2), bit)) throw new InvalidDataException("Cross-linked/reserved allocation.");
            SetBit(B(2), bit, true);
        }
    }
    private ulong MarkTree(ulong root, TreeKind kind, ulong owner, ulong window)
    {
        if (root == 0) return 0;
        int depth = 0; ulong address = root, pages = 0; int expected = -1;
        while (true)
        {
            if (depth == MaxHeight) throw new InvalidDataException("Tree depth overflow.");
            TreePage(address, kind, owner, B(0), expected); MarkOwned(address, layout.Q, window); pages++;
            Put64(Path, depth * 16, address); Put16(Path, depth * 16 + 8, 0); depth++;
            int level = U16(B(0), 50);
            if (level != 0) { expected = level - 1; address = U64(B(0), 64 + KeyBytes(kind)); continue; }
            int parent = depth - 2;
            for (; parent >= 0; parent--)
            {
                TreePage(U64(Path, parent * 16), kind, owner, B(0)); int next = U16(Path, parent * 16 + 8) + 1;
                if (next == U16(B(0), 48)) continue;
                Put16(Path, parent * 16 + 8, (ushort)next); depth = parent + 1; expected = U16(B(0), 50) - 1;
                address = U64(B(0), 64 + next * (KeyBytes(kind) + 8) + KeyBytes(kind)); break;
            }
            if (parent < 0) return pages;
        }
    }
    public CheckReport Check()
    {
        Ready();
        try { return VerifyOwnership(); }
        catch { poisoned = true; throw; }
    }
    private CheckReport VerifyOwnership()
    {
        Ready(); ValidateReserve(); bool ownership=OwnershipEnabled;
        ulong root = StateValue(80), free = 0, files = 0, bytes = 0, data = 0, metadata = 0, entries = 0;
        int reserve = AvailableReservePages; bool firstWindow = true;
        using var recordFrame1 = RentRecords(256 + 128 + 24 + 256); Span<byte> obj = recordFrame1.Bytes.Slice(0, 256); Span<byte> entry = recordFrame1.Bytes.Slice(0 + 256, 128); Span<byte> extent = recordFrame1.Bytes.Slice(0 + 256 + 128, 24); Span<byte> target = recordFrame1.Bytes.Slice(0 + 256 + 128 + 24, 256);
        for (ulong group = 0; group < layout.Units; group += GroupUnits)
        {
            if (!layout.HasGroup(group)) continue;
            uint groupFree = 0;
            for (int map = 0; map < 16; map++)
            {
                ulong window = group + (uint)(map * BitmapUnits); B(2).Clear();
                for (int bit = 0; bit < BitmapUnits; bit++) if (window + (uint)bit >= layout.Units || layout.Permanent(window + (uint)bit)) SetBit(B(2), bit, true);
                ulong pages = MarkTree(root, TreeKind.Object, 0, window);
                Load(layout.Reserve, B(3));
                for (int i = 0; i < reserve; i++) MarkOwned(U64(B(3), 72 + i * 8), layout.Q, window);
                ulong objectId = 0;
                while (TreeNext(root, TreeKind.Object, 0, objectId, 0, false, obj))
                {
                    objectId = U64(obj, 0);
                    if (obj[84] == 2)
                    {
                        pages += MarkTree(U64(obj, 32), TreeKind.Directory, objectId, window);
                        ulong high = 0, low = 0; bool firstEntry = true; ulong count = 0;
                        while (TreeNext(U64(obj, 32), TreeKind.Directory, objectId, high, low, firstEntry, entry))
                        {
                            firstEntry = false; high = U64(entry, 0); low = U64(entry, 8); count++;
                            if (!GetObject(U64(entry, 16), target) || U32(target, 76) == 0) throw new InvalidDataException("Dangling directory entry.");
                            int length = StoredName(entry, OtherNameScratch);
                            if (high != (ulong)NameHash(OtherNameScratch[..length]) << 32) throw new InvalidDataException("Incorrect name hash.");
                            if (U64(entry, 24) != 0) { MarkOwned(U64(entry, 24), layout.Q, window); pages++; }
                        }
                        if (count != U64(obj, 8)) throw new InvalidDataException("Directory count mismatch.");
                        if (firstWindow) entries += count;
                    }
                    else if (obj[84] == 1)
                    {
                        if (firstWindow) { files++; bytes += U64(obj, 8); }
                        if (obj[85] == 0 && (U64(obj, 8) > InlineBytes || U16(obj, 88) != U64(obj, 8) || U64(obj, 16) != 0)) throw new InvalidDataException("Invalid inline file.");
                        if (obj[85] == 2) pages += MarkTree(U64(obj, 24), TreeKind.Extent, objectId, window);
                        ulong key = 0, end = 0, units = 0; bool firstExtent = true;
                        while (NextExtent(obj, key, firstExtent, extent))
                        {
                            key = U64(extent, 0); uint count = U32(extent, 16); ulong physical = U64(extent, 8);
                            if (!firstExtent && key < end || count is 0 or > GroupUnits || key > ulong.MaxValue - count ||
                                layout.GroupBase(physical) != layout.GroupBase(physical + count - 1) || U32(extent, 20) > 1) throw new InvalidDataException("Invalid/overlapping extents.");
                            firstExtent = false; end = key + count; units += count; MarkOwned(physical, count, window);
                        }
                        if (units != U64(obj, 16)) throw new InvalidDataException("Allocated size mismatch.");
                        if (firstWindow) data += units;
                    }
                    else if(obj[84]==3)
                    {
                        ulong length=U64(obj,8); if(length==0||length>768||U64(obj,16)!=0) throw new InvalidDataException("Invalid symlink.");
                        if(obj[85]==0) { if(length>160||U16(obj,88)!=length) throw new InvalidDataException("Invalid inline symlink."); }
                        else { ulong page=U64(obj,96); if(obj[85]!=3||!layout.AllocatablePage(page)) throw new InvalidDataException("Invalid symlink page."); Load(page,B(4)); if(!IsPage(B(4),PageType.Symlink,page,objectId)||U16(B(4),64)!=length) throw new InvalidDataException("Invalid symlink payload."); MarkOwned(page,layout.Q,window); pages++; }
                    }
                    else throw new InvalidDataException("Unsupported object type.");
                    if(U64(obj,48)!=0)
                    { ulong page=U64(obj,48); Load(page,B(4)); if(!IsPage(B(4),PageType.Attribute,page,objectId)||U16(B(4),64)==2&&!ownership) throw new InvalidDataException("Invalid attributes."); MarkOwned(page,layout.Q,window); pages++; }
                }
                if (firstWindow) { metadata = (pages + (uint)reserve) * layout.Q; firstWindow = false; }
                Load(layout.Bitmap(group, map), B(0)); ValidateBitmap(B(0), group, map);
                if (!B(0).Slice(64, 512).SequenceEqual(B(2).Slice(64, 512))) throw new InvalidDataException("Allocation leak or bitmap-free owner.");
                uint local = 0; for (int bit = 0; bit < BitmapUnits; bit++) if (!Bit(B(0), bit)) local++;
                Load(layout.GroupDescriptor(group), B(3));
                if (U32(B(3), 80 + map * 4) != local) throw new InvalidDataException("Bitmap free count mismatch.");
                groupFree += local;
            }
            if (U32(B(3), 68) != groupFree) throw new InvalidDataException("Group free count mismatch."); free += groupFree;
        }
        if (StateValue(96) != free || StateValue(104) != entries) throw new InvalidDataException("Volume count mismatch.");
        return new(files, bytes, data, free, metadata, reserve);
    }
}
