using CopperCPX;

namespace CopperNova.Prototype;

public partial struct NovaEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // These packet-local pointers reference cache slots pinned for this packet's epoch.
    private const uint DeleteComment = 564, AncestorSteps = 568, UnitNumber = 572;

    private bool ReusableIds() => PackedVolume && m.Read16(Root() + 4) >= 2;
    private bool ValidNextHole(uint id, uint next, uint last) =>
        next == 0 || (next >= 2 && next <= last && next != id);
    private bool ValidHole(uint page, uint id, uint last) =>
        id >= 2 && id <= last && ValidInodeIdentity(page, id) && m.Read16(page + 6) == 12 &&
        m.Read32(page + 108) == 1 && m.Read32(page + 96) == 0 && m.Read32(page + 68) == 0 &&
        ValidNextHole(id, m.Read32(page + 116), last);

    private StepResult NextDirectoryEntry()
    {
        uint directory = R(Loaded), fib = Arg(1) << 2;
        if (!ValidInode(directory, R(Target)) || m.Read16(directory + 6) != 11) return Error(212);
        uint used = m.Read32(directory + 100), after = m.Read32(fib), best = 1024;
        if (used > 192) return Fatal();
        // Use stable IDs as an ordered enumeration key. A compacted or renamed
        // record does not change the position of other surviving objects.
        for (uint offset = 0; offset < used;)
        {
            uint entry = directory + 320 + offset;
            if (used - offset < 24) return Fatal();
            uint length = m.Read16(entry + 20), bytes = (24 + length + 7) & ~7u;
            if (length == 0 || bytes > used - offset) return Fatal();
            uint id = m.Read32(entry + 4);
            if (id > after && id < best) best = id;
            offset += bytes;
        }
        if (best == 1024) return Error(232); // ERROR_NO_MORE_ENTRIES
        W(Target, best);
        return Load(256 + best, 54);
    }

    private void RemoveName(uint directory, uint offset)
    {
        uint entry = directory + offset, size = (24 + m.Read16(entry + 20) + 7) & ~7u;
        uint used = m.Read32(directory + 100);
        m.Copy(entry, entry + size, used - (offset - 320) - size);
        ZeroTail(directory + 320 + used - size, size);
        m.Write32(directory + 100, used - size);
        m.Write32(directory + 96, m.Read32(directory + 96) - 1);
        Dirty(directory);
    }

    private StepResult MoveEntry()
    {
        uint dest = R(Parent), inode = R(Inode), id = m.Read32(inode + 28);
        int slot = Find(R(SourceParent));
        if (slot < 0) return Fatal();
        uint source = Image((uint)slot), old = source + R(SourceEntry);
        if (m.Read32(old + 4) != id || m.Read32(inode + 108) != m.Read32(source + 28)) return Fatal();
        uint used = m.Read32(dest + 100), length = R(ComponentBytes);
        WriteEntry(dest + 320 + used, id, m.Read32(old + 8), m.Read32(old + 12),
            Arena + Path + R(ComponentAt), length, R(ComponentHash));
        m.Write32(dest + 100, used + ((24 + length + 7) & ~7u));
        m.Write32(dest + 96, m.Read32(dest + 96) + 1);
        m.Write32(inode + 108, m.Read32(dest + 28));
        Dirty(dest); Dirty(inode);
        RemoveName(source, R(SourceEntry));
        return Finish(uint.MaxValue);
    }

    private StepResult NamespaceStep(uint phase)
    {
        uint page = R(Loaded), id = R(Target);
        switch (phase)
        {
            case 62:
                id = m.Read32(Arena + RenameBuffer + 4);
                if (!ValidInode(page, id)) return Fatal();
                W(Inode, page);
                if (m.Read16(page + 6) == 10) return MoveEntry();
                W(AncestorSteps, 0);
                W(Loaded, R(Parent));
                goto case 64;
            case 64:
                page = R(Loaded);
                uint ancestor = m.Read32(page + 28), parent = m.Read32(page + 108);
                if (!ValidInode(page, ancestor) || m.Read16(page + 6) != 11) return Fatal();
                if (ancestor == m.Read32(Arena + RenameBuffer + 4)) return Error(202);
                if (ancestor == 1) return MoveEntry();
                if (R(AncestorSteps) >= 1023 || parent == 0 || parent >= 1024) return Fatal();
                W(AncestorSteps, R(AncestorSteps) + 1);
                // Only the source directory, destination and moved object are needed at commit.
                if (page != R(Parent) && m.Read32(page + 20) != R(SourceParent))
                    m.Write32(Meta((page - Arena - CacheImages) >> 9) + 12, R(Epoch) - 1);
                return Load(256 + parent, 64);
            case 63:
                if (!ValidInode(page, id) || m.Read32(page + 108) != m.Read32(R(Parent) + 28)) return Fatal();
                if (Conflicts(id, true)) return Error(202);
                for (uint i = 0; i < 32; i++) {
                    uint p = Arena + Locks + i * 48;
                    if (m.Read32(p + 32) != 0 && m.Read32(p + 4) == id) return Error(202);
                }
                if (m.Read16(page + 6) == 11 && m.Read32(page + 96) != 0) return Error(216);
                W(Inode, page); W(DeleteComment, 0);
                if (CommentIndex(id >> 2, false) != 0) return Load(2000 + (id >> 2), 65);
                return Load(128, 66);
            case 65:
                W(DeleteComment, page);
                return Load(128, 66);
            case 66:
                if (m.Read16(page + 6) != 3) return Fatal();
                W(Group, page);
                if (m.Read32(R(Inode) + 68) != 0) {
                    W(ResizeLength, 0); W(Resume, 67);
                    return Next(400);
                }
                goto case 67;
            case 67:
                uint inode = R(Inode), group = R(Group), attribute = R(DeleteComment);
                if (attribute != 0) {
                    uint comment = attribute + 64 + (id & 3) * 80;
                    if (m.Read8(comment) != 0) { Zero(comment, 80); Dirty(attribute); }
                }
                RemoveName(R(Parent), R(SourceEntry));
                m.Write16(inode + 6, 12); m.Write32(inode + 108, 1);
                m.Write32(inode + 116, m.Read32(group + 84));
                m.Write32(group + 84, id);
                Dirty(inode); Dirty(group);
                return R(WideStreaming)!=0?FinishWideRetirement(10):Finish(uint.MaxValue);
            default: return Fatal();
        }
    }
}
