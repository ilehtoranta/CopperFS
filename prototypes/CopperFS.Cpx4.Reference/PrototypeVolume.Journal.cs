using static CopperFS.Cpx4.Reference.Layout;

namespace CopperFS.Cpx4.Reference;

public ref partial struct Cpx4Volume<TDevice> where TDevice : IBlockDevice
{
    private int FindStaged(ulong address)
    { for (int i = 0; i < imageCount; i++) if (U64(Index, i * 32) == address) return i; return -1; }
    private void Begin(Operation operation)
    {
        Ready(); if (readOnly) throw new InvalidOperationException("Read-only mount.");
        if (deferredCleanup && operation is not (Operation.Reclaim or Operation.Prune) && StateValue(88) != 0)
            throw new InvalidOperationException("Complete deferred cleanup before another mutation.");
        if (generation == ulong.MaxValue) throw new InvalidOperationException("Transaction IDs exhausted.");
        imageCount = newPages = 0; releasedMetadata = false; imageLimit = Credits.Images(operation); pageLimit = Credits.Pages(operation);
        Index.Clear(); active = poisoned = true;
        B(5).Clear(); RawWrite(layout.Commit(generation + 1), B(5)); FlushDevice();
    }
    private void Abort() { active = poisoned = false; imageCount = newPages = 0; Index.Clear(); }
    private void Stage(ulong address, scoped Span<byte> page)
    {
        if (!active || page.Length != layout.PageBytes) throw new InvalidOperationException("No active transaction/page.");
        ValidateTarget(address, (PageType)U16(page, 4), U64(page, 24));
        int index = FindStaged(address);
        if (index < 0)
        {
            if (imageCount >= imageLimit || imageCount >= JournalImages) throw new InvalidOperationException("Transaction credit bound exceeded before publication.");
            index = imageCount++;
        }
        Put64(page, 16, address); Put64(page, 32, generation + 1); Seal(page);
        int offset = index * 32; Put64(Index, offset, address); Put64(Index, offset + 8, U64(page, 24));
        Put32(Index, offset + 16, (uint)index); Put32(Index, offset + 20, U32(page, 8)); Put16(Index, offset + 24, U16(page, 4));
        RawWrite(layout.Image(generation + 1, index), page);
    }
    private void ValidateTarget(ulong address, PageType type, ulong owner)
    {
        bool valid = layout.PageAddress(address) && (type switch
        {
            PageType.State => address == layout.State && owner == 0,
            PageType.Reserve => address == layout.Reserve && owner == 0,
            PageType.Group => owner % GroupUnits == 0 && layout.HasGroup(owner) && address == layout.GroupDescriptor(owner),
            PageType.Bitmap => owner % GroupUnits == 0 && layout.HasGroup(owner) && address >= layout.Bitmap(owner, 0) && address <= layout.Bitmap(owner, 15),
            PageType.ObjectLeaf or PageType.ObjectBranch => owner == 0 && layout.AllocatablePage(address),
            PageType.DirectoryLeaf or PageType.DirectoryBranch or PageType.ExtentLeaf or PageType.ExtentBranch or PageType.Name or PageType.Attribute or PageType.Symlink => owner != 0 && layout.AllocatablePage(address),
            _ => false
        });
        if (!valid) throw new InvalidDataException("Forbidden journal target/type/owner.");
    }
    private void Checkpoint(ulong at, ulong transaction)
    {
        Header(B(5), PageType.Checkpoint, at, 0, transaction); layout.Identity(B(5)); Put64(B(5), 80, transaction);
        Seal(B(5)); RawWrite(at, B(5)); FlushDevice();
    }
    private void CommitTransaction()
    {
        if (!active || imageCount == 0) throw new InvalidOperationException("Empty/inactive transaction.");
        ulong next = generation + 1; uint digest = Crc(Index[..(imageCount * 32)]);
        Header(B(5), PageType.JournalHeader, layout.Journal(next), 0, next); layout.Identity(B(5));
        Put64(B(5), 80, next); Put64(B(5), 88, generation); Put32(B(5), 96, (uint)imageCount); Put32(B(5), 100, digest);
        Seal(B(5)); RawWrite(layout.Journal(next), B(5));
        for (int page = 0; page < 4; page++)
        {
            Header(B(5), PageType.Descriptor, layout.Descriptor(next, page), 0, next);
            int first = page * layout.DescriptorsPerPage, count = Math.Clamp(imageCount - first, 0, layout.DescriptorsPerPage);
            Put16(B(5), 48, (ushort)count);
            if (count != 0) Index.Slice(first * 32, count * 32).CopyTo(B(5)[64..]);
            Seal(B(5)); RawWrite(layout.Descriptor(next, page), B(5));
        }
        FlushDevice(); // final staged images and new data precede publication
        Header(B(5), PageType.Commit, layout.Commit(next), 0, next); layout.Identity(B(5));
        Put64(B(5), 80, next); Put64(B(5), 88, generation); Put32(B(5), 96, (uint)imageCount); Put32(B(5), 100, digest);
        Seal(B(5)); RawWrite(layout.Commit(next), B(5)); FlushDevice();
        for (int i = 0; i < imageCount; i++)
        { ReadImage(next, i, B(5)); RawWrite(U64(Index, i * 32), B(5)); }
        FlushDevice();
        Checkpoint(layout.CheckpointA, next); Checkpoint(layout.CheckpointB, next);
        generation = next; LastTransactionPages = imageCount; MaximumTransactionPages = Math.Max(MaximumTransactionPages, imageCount);
        active = poisoned = false; imageCount = 0;
    }
    private bool ReadCheckpoint(ulong at, out ulong transaction)
    {
        RawRead(at, B(5)); transaction = U64(B(5), 80);
        return IsPage(B(5), PageType.Checkpoint, at, 0) && layout.IdentityMatches(B(5)) && transaction == U64(B(5), 32);
    }
    private void ReadImage(ulong transaction, int index, scoped Span<byte> page)
    {
        int offset = index * 32; ulong address = U64(Index, offset), owner = U64(Index, offset + 8);
        var type = (PageType)U16(Index, offset + 24);
        ValidateTarget(address, type, owner);
        if (U32(Index, offset + 16) != index || U16(Index, offset + 26) != 0 || U32(Index, offset + 28) != 0)
            throw new InvalidDataException("Invalid journal descriptor.");
        RawRead(layout.Image(transaction, index), page);
        if (!IsPage(page, type, address, owner) || U64(page, 32) != transaction || U32(page, 8) != U32(Index, offset + 20))
            throw new InvalidDataException("Committed journal image is corrupt.");
    }
    private void RecoverJournal(bool onlyRead)
    {
        bool a = ReadCheckpoint(layout.CheckpointA, out ulong ca), b = ReadCheckpoint(layout.CheckpointB, out ulong cb);
        if (!a && !b) throw new InvalidDataException("Both checkpoints are invalid.");
        generation = Math.Max(a ? ca : 0, b ? cb : 0);
        ulong successor = 0; int count = 0; uint digest = 0;
        for (ulong slot = 1; slot <= 2; slot++)
        {
            RawRead(layout.Commit(slot), B(5));
            if (!ValidCrc(B(5))) continue; // uncommitted/torn publication under the documented crash model
            if (!IsPage(B(5), PageType.Commit, layout.Commit(slot), 0) || !layout.IdentityMatches(B(5)))
                throw new InvalidDataException("Invalid valid-CRC commit identity.");
            ulong tx = U64(B(5), 80);
            if (tx != U64(B(5), 32) || layout.Commit(tx) != layout.Commit(slot)) throw new InvalidDataException("Commit slot/ID mismatch.");
            if (tx <= generation) continue;
            if (generation == ulong.MaxValue || tx != generation + 1 || U64(B(5), 88) != generation || successor != 0 || U32(B(5), 96) is < 1 or > JournalImages)
                throw new InvalidDataException("Journal generation gap/conflict.");
            successor = tx; count = (int)U32(B(5), 96); digest = U32(B(5), 100);
        }
        if (successor != 0)
        {
            if (onlyRead) throw new InvalidOperationException("Journal recovery requires a writable mount.");
            RawRead(layout.Journal(successor), B(5));
            if (!IsPage(B(5), PageType.JournalHeader, layout.Journal(successor), 0) || !layout.IdentityMatches(B(5)) ||
                U64(B(5), 32) != successor || U64(B(5), 80) != successor || U64(B(5), 88) != generation || U32(B(5), 96) != count || U32(B(5), 100) != digest)
                throw new InvalidDataException("Commit/header mismatch.");
            Index.Clear();
            for (int page = 0; page < 4; page++)
            {
                RawRead(layout.Descriptor(successor, page), B(5));
                int first = page * layout.DescriptorsPerPage, take = Math.Clamp(count - first, 0, layout.DescriptorsPerPage);
                if (!IsPage(B(5), PageType.Descriptor, layout.Descriptor(successor, page), 0) || U64(B(5), 32) != successor || U16(B(5), 48) != take)
                    throw new InvalidDataException("Corrupt journal descriptors.");
                if (take != 0) B(5).Slice(64, take * 32).CopyTo(Index[(first * 32)..]);
            }
            if (Crc(Index[..(count * 32)]) != digest) throw new InvalidDataException("Journal descriptor digest mismatch.");
            bool ownedAttribute=false,stateImage=false,ownership=false;
            for (int i = 0; i < count; i++)
            {
                for (int j = 0; j < i; j++) if (U64(Index, j * 32) == U64(Index, i * 32)) throw new InvalidDataException("Duplicate journal target.");
                ReadImage(successor, i, B(0));
                if(U16(B(0),4)==(ushort)PageType.Attribute&&U16(B(0),64)==2) ownedAttribute=true;
                if(U16(B(0),4)==(ushort)PageType.State) { stateImage=true; ownership=U16(B(0),52)==136; }
            }
            if(ownedAttribute&&!stateImage)
            {
                RawRead(layout.State,B(0));
                if(!IsPage(B(0),PageType.State,layout.State,0)||U64(B(0),32)>generation) throw new InvalidDataException("Invalid ownership feature state.");
                ownership=U16(B(0),52)==136;
            }
            if(ownedAttribute&&!ownership) throw new InvalidDataException("Owned attributes require the ownership feature before replay.");
            // Entire log has been checked before the first home write. Re-read streaming.
            for (int i = 0; i < count; i++) { ReadImage(successor, i, B(0)); RawWrite(U64(Index, i * 32), B(0)); }
            FlushDevice(); generation = successor;
        }
        if (!onlyRead)
        {
            if (successor != 0 || !a || ca != generation) Checkpoint(layout.CheckpointA, generation);
            if (successor != 0 || !b || cb != generation) Checkpoint(layout.CheckpointB, generation);
        }
        imageCount = 0; Index.Clear();
    }
}
