using static CopperFS.Cpx4.Reference.Layout;

namespace CopperFS.Cpx4.Reference;

public ref partial struct Cpx4Volume<TDevice> where TDevice : IBlockDevice
{
    public MutationResult Create(scoped ReadOnlySpan<byte> name, out ulong id) => Create(RootDirectory, name, false, out id);
    public MutationResult Create(ulong parent, scoped ReadOnlySpan<byte> name, bool directory, out ulong id)
    {
        Ready(); id = 0; int n = LegacyName(name, NameScratch); if (n < 0) return MutationResult.InvalidName;
        return CreateEncoded(parent, n, name.Length, directory, out id);
    }
    /// <summary>Explicit host UTF-8 import; the DOS-byte API never guesses an encoding.</summary>
    public MutationResult CreateUtf8(ulong parent, scoped ReadOnlySpan<byte> utf8, bool directory, out ulong id)
    {
        Ready(); id = 0;
        if (!ValidateUtf8Name(utf8, out int legacyLength)) return MutationResult.InvalidName;
        utf8.CopyTo(NameScratch); return CreateEncoded(parent, utf8.Length, legacyLength, directory, out id);
    }
    private MutationResult CreateEncoded(ulong parent, int n, int legacyLength, bool directory, out ulong id, scoped ReadOnlySpan<byte> symlink = default)
    {
        id = 0;
        using var recordFrame1 = RentRecords(256 + 128 + 256); Span<byte> p = recordFrame1.Bytes.Slice(0, 256); Span<byte> entry = recordFrame1.Bytes.Slice(0 + 256, 128); Span<byte> obj = recordFrame1.Bytes.Slice(0 + 256 + 128, 256);
        if (!GetObject(parent, p) || p[84] != 2) return MutationResult.NotDirectory;
        if (FindName(parent, NameScratch[..n], p, entry)) return MutationResult.AlreadyExists;
        if (!EnsureReserve(Operation.Create)) return MutationResult.ReserveExhausted;
        Begin(Operation.Create);
        try
        {
            ulong objectId = StateValue(64), entryId = StateValue(72);
            if (objectId == ulong.MaxValue || entryId == ulong.MaxValue) throw new InvalidOperationException("IDs exhausted.");
            obj.Clear(); Put64(obj, 0, objectId); Put32(obj, 76, 1); Put32(obj, 80, 1); obj[84] = directory ? (byte)2 : (byte)1;
            if (directory) Put64(obj, 40, parent);
            if (!symlink.IsEmpty)
            {
                obj[84]=3; Put64(obj,8,(uint)symlink.Length);
                if(symlink.Length<=InlineBytes) { Put16(obj,88,(ushort)symlink.Length); symlink.CopyTo(obj[96..]); }
                else { ulong page=TakeMetadata(); obj[85]=3; Put64(obj,96,page); Header(B(4),PageType.Symlink,page,objectId,generation+1); Put16(B(4),64,(ushort)symlink.Length); symlink.CopyTo(B(4)[68..]); Stage(page,B(4)); }
            }
            MakeEntry(entry, entryId, objectId, NameScratch[..n], legacyLength);
            ulong root = U64(p, 32); TreeInsert(ref root, TreeKind.Directory, parent, entry);
            Put64(p, 32, root); Put64(p, 8, checked(U64(p, 8) + 1)); SaveObject(p);
            root = StateValue(80); TreeInsert(ref root, TreeKind.Object, 0, obj); SetState(80, root);
            SetState(64, objectId + 1); SetState(72, entryId + 1); SetState(104, checked(StateValue(104) + 1));
            CommitTransaction(); id = objectId; return MutationResult.Success;
        }
        catch (AdmissionException e) { Abort(); return e.Result; }
    }
    public MutationResult Link(ulong objectId, ulong parent, scoped ReadOnlySpan<byte> name)
    {
        Ready(); int n = LegacyName(name, NameScratch); if (n < 0) return MutationResult.InvalidName;
        using var recordFrame2 = RentRecords(256 + 256 + 128); Span<byte> obj = recordFrame2.Bytes.Slice(0, 256); Span<byte> p = recordFrame2.Bytes.Slice(0 + 256, 256); Span<byte> entry = recordFrame2.Bytes.Slice(0 + 256 + 256, 128);
        if (!GetObject(objectId, obj) || obj[84] != 1 || U32(obj, 76) == 0) return MutationResult.NotFound;
        if (!GetObject(parent, p) || p[84] != 2) return MutationResult.NotDirectory;
        if (FindName(parent, NameScratch[..n], p, entry)) return MutationResult.AlreadyExists;
        if (!EnsureReserve(Operation.Link)) return MutationResult.ReserveExhausted;
        Begin(Operation.Link);
        try
        {
            ulong entryId = StateValue(72); if (entryId == ulong.MaxValue) throw new InvalidOperationException("Entry IDs exhausted.");
            MakeEntry(entry, entryId, objectId, NameScratch[..n], name.Length); ulong root = U64(p, 32);
            TreeInsert(ref root, TreeKind.Directory, parent, entry); Put64(p, 32, root); Put64(p, 8, checked(U64(p, 8) + 1)); SaveObject(p);
            Put32(obj, 76, checked(U32(obj, 76) + 1)); SaveObject(obj); SetState(72, entryId + 1); SetState(104, checked(StateValue(104) + 1));
            CommitTransaction(); return MutationResult.Success;
        }
        catch (AdmissionException e) { Abort(); return e.Result; }
    }
    public MutationResult Rename(scoped ReadOnlySpan<byte> oldName, scoped ReadOnlySpan<byte> newName) => Rename(1, oldName, 1, newName);
    public MutationResult Rename(ulong oldParent, scoped ReadOnlySpan<byte> oldName, ulong newParent, scoped ReadOnlySpan<byte> newName, bool replace = false)
    {
        Ready(); using var recordFrame3 = RentRecords(256 + 256 + 256 + 256); Span<byte> p = recordFrame3.Bytes.Slice(0, 256); Span<byte> destination = recordFrame3.Bytes.Slice(0 + 256, 256); Span<byte> obj = recordFrame3.Bytes.Slice(0 + 256 + 256, 256); Span<byte> victim = recordFrame3.Bytes.Slice(0 + 256 + 256 + 256, 256);
        using var recordFrame4 = RentRecords(128 + 128 + 128); Span<byte> old = recordFrame4.Bytes.Slice(0, 128); Span<byte> entry = recordFrame4.Bytes.Slice(0 + 128, 128); Span<byte> previous = recordFrame4.Bytes.Slice(0 + 128 + 128, 128);
        int n = LookupName(oldName); if (n < 0) return MutationResult.InvalidName;
        if (!FindName(oldParent, NameScratch[..n], p, old)) return MutationResult.NotFound;
        if (!GetObject(U64(old, 16), obj)) throw new InvalidDataException("Dangling source.");
        n = LegacyName(newName, NameScratch); if (n < 0) return MutationResult.InvalidName;
        if (!GetObject(newParent, destination) || destination[84] != 2) return MutationResult.NotDirectory;
        bool exists = FindName(newParent, NameScratch[..n], destination, previous), sameEntry = exists && U64(previous, 8) == U64(old, 8);
        bool replacing = exists && !sameEntry;
        if (replacing)
        {
            if (!replace) return MutationResult.AlreadyExists;
            if (U64(previous, 16) == U64(old, 16)) return MutationResult.Success;
            if (!GetObject(U64(previous, 16), victim)) throw new InvalidDataException("Dangling destination.");
            if (victim[84] != obj[84]) return MutationResult.InvalidOperation;
            if (victim[84] == 2 && U64(victim, 8) != 0) return MutationResult.DirectoryNotEmpty;
        }
        if (obj[84] == 2)
        {
            var search = StartAncestrySearch(newParent, U64(obj, 0));
            while (true)
            {
                var result = CheckAncestryStep(ref search);
                if (result == ScanResult.Found) return MutationResult.InvalidOperation;
                if (result == ScanResult.End) break;
                if (result == ScanResult.Invalidated) throw new InvalidOperationException("Stale ancestry continuation.");
                Cooperate();
            }
        }
        if (!EnsureReserve(Operation.Rename)) return MutationResult.ReserveExhausted;
        Begin(Operation.Rename);
        try
        {
            MakeEntry(entry, U64(old, 8), U64(old, 16), NameScratch[..n], newName.Length);
            ulong root = U64(destination, 32);
            if (oldParent == newParent) TreeDelete(root, TreeKind.Directory, oldParent, U64(old, 0), U64(old, 8));
            if (replacing) TreeDelete(root, TreeKind.Directory, newParent, U64(previous, 0), U64(previous, 8));
            TreeInsert(ref root, TreeKind.Directory, newParent, entry); Put64(destination, 32, root);
            Put64(destination, 8, U64(destination, 8) + (oldParent == newParent ? 0UL : 1UL) - (replacing ? 1UL : 0UL)); SaveObject(destination);
            if (oldParent != newParent)
            {
                TreeDelete(U64(p, 32), TreeKind.Directory, oldParent, U64(old, 0), U64(old, 8)); Put64(p, 8, checked(U64(p, 8) - 1)); SaveObject(p);
                if (obj[84] == 2) { Put64(obj, 40, newParent); SaveObject(obj); }
            }
            if (U64(old, 24) != 0) ReleaseMetadata(U64(old, 24));
            if (replacing)
            {
                if (U64(previous, 24) != 0) ReleaseMetadata(U64(previous, 24));
                uint links = U32(victim, 76); Put32(victim, 76, checked(links - 1));
                if (links == 1) { Put16(victim, 86, 1); Put64(victim, 40, StateValue(88)); SetState(88, U64(victim, 0)); }
                SaveObject(victim); SetState(104, checked(StateValue(104) - 1));
            }
            CommitTransaction(); if (!deferredCleanup) DrainWork(); return MutationResult.Success;
        }
        catch (AdmissionException e) { Abort(); return e.Result; }
    }
    public MutationResult Delete(scoped ReadOnlySpan<byte> name) => Delete(RootDirectory, name);
    public MutationResult Delete(ulong parent, scoped ReadOnlySpan<byte> name)
    {
        Ready(); int n = LookupName(name); if (n < 0) return MutationResult.InvalidName;
        using var recordFrame6 = RentRecords(256 + 128 + 256); Span<byte> p = recordFrame6.Bytes.Slice(0, 256); Span<byte> entry = recordFrame6.Bytes.Slice(0 + 256, 128); Span<byte> obj = recordFrame6.Bytes.Slice(0 + 256 + 128, 256);
        if (!FindName(parent, NameScratch[..n], p, entry)) return MutationResult.NotFound;
        if (!GetObject(U64(entry, 16), obj)) throw new InvalidDataException("Dangling entry.");
        if (obj[84] == 2 && U64(obj, 8) != 0) return MutationResult.DirectoryNotEmpty;
        Begin(Operation.Unlink);
        TreeDelete(U64(p, 32), TreeKind.Directory, parent, U64(entry, 0), U64(entry, 8));
        Put64(p, 8, checked(U64(p, 8) - 1)); SaveObject(p);
        if (U64(entry, 24) != 0) ReleaseMetadata(U64(entry, 24));
        uint links = U32(obj, 76); if (links == 0) throw new InvalidDataException("Zero link count.");
        Put32(obj, 76, links - 1);
        if (links == 1) { Put16(obj, 86, 1); Put64(obj, 40, StateValue(88)); SetState(88, U64(obj, 0)); }
        SaveObject(obj); SetState(104, checked(StateValue(104) - 1)); CommitTransaction();
        if (!deferredCleanup) { DrainWork(); PruneDirectory(parent); } return MutationResult.Success;
    }
    private void PruneDirectory(ulong id)
    {
        using var recordFrame7 = RentRecords(256); Span<byte> obj = recordFrame7.Bytes.Slice(0, 256);
        while (GetObject(id, obj))
        {
            ulong root = U64(obj, 32); if (root == 0) return;
            Begin(Operation.Prune);
            if (!TreePruneEmpty(ref root, TreeKind.Directory, id)) { Abort(); return; }
            Put64(obj, 32, root); SaveObject(obj); CommitTransaction();
        }
    }
    public bool HasPendingCleanup { get { Ready(); return StateValue(88) != 0; } }
    /// <summary>One retired cleanup/prune transaction at most. Cooperate retains
    /// ownership while scanning and committing; return releases it between chunks.</summary>
    public bool ReclaimStep()
    {
        Ready(); if (readOnly) throw new InvalidOperationException("Read-only mount.");
        ulong before = generation;
        if (StateValue(88) != 0) { DrainWork(1); return generation != before; }
        ulong root = StateValue(80); Begin(Operation.Prune);
        if (!TreePruneEmpty(ref root, TreeKind.Object, 0)) { Abort(); return false; }
        if (root == 0) throw new InvalidDataException("Lost root object.");
        SetState(80, root); CommitTransaction(); return true;
    }
    public bool PruneDirectoryStep(ulong id)
    {
        Ready(); using var frame = RentRecords(256); var obj = frame.Bytes;
        if (!GetObject(id, obj) || obj[84] != 2 || U64(obj, 32) == 0) return false;
        ulong root = U64(obj, 32); Begin(Operation.Prune);
        if (!TreePruneEmpty(ref root, TreeKind.Directory, id)) { Abort(); return false; }
        Put64(obj, 32, root); SaveObject(obj); CommitTransaction(); return true;
    }
    private void DrainWork(int maximumTransactions = int.MaxValue)
    {
        using var recordFrame8 = RentRecords(256 + 24); Span<byte> obj = recordFrame8.Bytes.Slice(0, 256); Span<byte> extent = recordFrame8.Bytes.Slice(0 + 256, 24);
        while (StateValue(88) is ulong id && id != 0)
        {
            if (!GetObject(id, obj) || U16(obj, 86) is < 1 or > 2 || U16(obj, 86) == 1 && U32(obj, 76) != 0 || U16(obj, 86) == 2 && (U32(obj, 76) == 0 || obj[84] != 1)) throw new InvalidDataException("Invalid work queue.");
            bool unlink = U16(obj, 86) == 1; ulong keep = unlink ? 0 : DivideUp(U64(obj, 8), (uint)layout.UnitBytes);
            if (LastExtent(obj, extent) && (unlink || U64(extent, 0) >= keep || U32(extent, 16) > keep - U64(extent, 0)))
            {
                ulong physical = U64(extent, 8); uint count = U32(extent, 16);
                uint take = Math.Min(count, (uint)((physical + count - 1) % BitmapUnits + 1));
                if (!unlink && U64(extent, 0) < keep) take = Math.Min(take, count - (uint)(keep - U64(extent, 0)));
                Begin(Operation.Reclaim); ChangeBits(physical + count - take, take, false);
                if (take == count) RemoveExtent(obj, U64(extent, 0));
                else { Put32(extent, 16, count - take); ReplaceExtent(obj, extent); }
                Put64(obj, 16, checked(U64(obj, 16) - take)); SaveObject(obj); CommitTransaction(); if (--maximumTransactions == 0) return; continue;
            }
            ulong tree = obj[84] == 2 ? U64(obj, 32) : U64(obj, 24);
            if (tree != 0)
            {
                Begin(Operation.Prune);
                if (TreePruneEmpty(ref tree, obj[84] == 2 ? TreeKind.Directory : TreeKind.Extent, id))
                { Put64(obj, obj[84] == 2 ? 32 : 24, tree); SaveObject(obj); CommitTransaction(); if (--maximumTransactions == 0) return; continue; }
                if (unlink) throw new InvalidDataException("Nonempty detached tree.");
                Abort();
            }
            if (!unlink)
            {
                Begin(Operation.Reclaim); SetState(88, U64(obj, 40)); Put64(obj, 40, 0); Put16(obj, 86, 0); SaveObject(obj); CommitTransaction(); if (--maximumTransactions == 0) return; continue;
            }
            Begin(Operation.Reclaim);
            if(U64(obj,48)!=0) ReleaseMetadata(U64(obj,48));
            if(obj[84]==3 && obj[85]==3) ReleaseMetadata(U64(obj,96));
            TreeDelete(StateValue(80), TreeKind.Object, 0, id, 0); SetState(88, U64(obj, 40)); CommitTransaction();
            if (--maximumTransactions == 0) return;
            while (true)
            {
                ulong root = StateValue(80); Begin(Operation.Prune);
                if (!TreePruneEmpty(ref root, TreeKind.Object, 0)) { Abort(); break; }
                if (root == 0) throw new InvalidDataException("Lost root object.");
                SetState(80, root); CommitTransaction(); if (--maximumTransactions == 0) return;
            }
        }
    }
}
