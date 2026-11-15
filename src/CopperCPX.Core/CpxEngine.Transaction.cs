namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    public uint ReadBuffer => Page(0);
    public uint TransactionImageCount => memory.Read32(Arena + (StagedCount));

    private int FindStaged(ulong at)
    {
        if (memory.Read32(Arena + (Active)) == 0)
            return -1;
        for (uint n = 0; n < memory.Read32(Arena + (StagedCount)); n++)
            if ((((ulong)(GetWide(Index + n * 32))) == ((ulong)(at))))
                return (int)n;
        return -1;
    }

    private StepResult TransactionStep()
    {
        uint phase = memory.Read32(Arena + (Phase)), p = Page(5);
        ulong tx = RW(Generation) + 1;
        switch ((CpxOperation)memory.Read32(Arena + (OperationAt)))
        {
            case CpxOperation.BeginTransaction:
                if (phase == 0)
                {
                    if (BufferedWrites)
                    {
                        uint credits = memory.Read32(Arena + (StagedLimit));
                        memory.Write32(Arena + (MetadataTaken), 0);
                        memory.Write32(Arena + (MetadataReleased), 0);
                        memory.Write32(Arena + (MetadataLimit), credits == 39 ? 20u : credits is 22 or 30 ? 10u : credits == 42 ? 18u : credits == 8 ? 2u : credits == 6 ? 1u : 0u);
                        if (memory.Read32(Arena + (Active)) == 0)
                        {
                            memory.Write32(Arena + (StagedCount), 0);
                            Clear(Index, 2048);
                        }

                        memory.Write32(Arena + (Active), 1);
                        memory.Write32(Arena + (StagedLimit), 42);
                        return Done();
                    }

                    memory.Write32(Arena + (Active), 1);
                    memory.Write32(Arena + (StagedCount), 0);
                    memory.Write32(Arena + (MetadataTaken), 0);
                    memory.Write32(Arena + (MetadataReleased), 0);
                    memory.Write32(Arena + (MetadataLimit), memory.Read32(Arena + (StagedLimit)) == 39 ? 20u : memory.Read32(Arena + (StagedLimit))is 22 or 30 ? 10u : memory.Read32(Arena + (StagedLimit)) == 42 ? 18u : memory.Read32(Arena + (StagedLimit)) == 6 ? 1u : 0u);
                    Clear(Index, 2048);
                    Clear(p, P);
                    return Issue(IoKind.Write, Commit(tx), p, P, 1);
                }

                if (phase == 1)
                    return Issue(IoKind.Flush, default, 0, 0, 2);
                return Done();
            case CpxOperation.AbortTransaction:
                if (BufferedWrites)
                {
                    DiscardBufferedOperation();
                    return Done();
                }

                memory.Write32(Arena + (Active), 0);
                memory.Write32(Arena + (AbortOnly), 0);
                memory.Write32(Arena + (StagedCount), 0);
                Clear(Index, 2048);
                return Done();
            case CpxOperation.StageMetadata:
                if (phase != 0)
                    return Done();
                return StagePage(memory.Read32(Arena + (SourcePage)), RW(Target), 1);
        }

        return CommitStep();
    }

    private StepResult StagePage(uint p, ulong target, uint nextPhase)
    {
        // Reading/validating a page and calculating its new checksum belong to
        // separate processing phases. The caller-owned page stays pinned by the
        // single active operation until this child continuation completes.
        memory.Write32(Arena + (PreparedBuffer), p);
        WW(PreparedTarget, target);
        return Call(CpxOperation.PublishMetadata, nextPhase);
    }

    private StepResult PublishMetadata(uint p, ulong target, uint nextPhase)
    {
        ulong tx = RW(Generation) + 1;
        uint type = memory.Read16(p + 4);
        ulong owner = GetWide(p + 24);
        if (memory.Read32(p) != CpxArena.Magic || memory.Read16(p + 6) != 4 || !TargetValid(target, type, owner) || !PayloadValid(p))
            return Fail(CpxError.Corrupt);
        int slot = FindStaged(target);
        if (slot < 0)
        {
            if (BufferedWrites && memory.Read32(Arena + (StagedCount)) == 42)
                return RetryBufferedOperation();
            if (memory.Read32(Arena + (StagedCount)) == memory.Read32(Arena + (StagedLimit)))
            {
                memory.Write32(Arena + (AbortOnly), 1);
                memory.Write32(Arena + (ErrorAt), (uint)CpxError.TransactionLimit);
                memory.Write32(Arena + (Finished), 1);
                return StepResult.Failed;
            }

            slot = (int)memory.Read32(Arena + (StagedCount));
            memory.Write32(Arena + (StagedCount), (uint)slot + 1);
        }

        PutWide(p + 16, target);
        PutWide(p + 32, tx);
        if (BufferedWrites)
        {
            SaveBufferedUndo((uint)slot);
            InvalidateBufferedClean(target);
        }

        memory.Write32(p + 8, BufferedWrites ? 0u : Crc(p, P, true));
        uint descriptor = Index + (uint)slot * 32;
        PutWide(descriptor, target);
        PutWide(descriptor + 8, owner);
        memory.Write32(descriptor + 16, (uint)slot);
        memory.Write32(descriptor + 20, memory.Read32(p + 8));
        memory.Write16(descriptor + 24, type);
        if (BufferedWrites)
        {
            Copy(BufferedImage((uint)slot), p, P);
            TrustBufferedPage(p, target);
            if (type == 11)
                CacheBufferedObjects(p, target);
            memory.Write32(Arena + (Phase), nextPhase);
            return StepResult.Pending;
        }

        return Issue(IoKind.Write, Image(tx, (uint)slot), p, P, nextPhase);
    }

    private StepResult CommitStep()
    {
        uint phase = memory.Read32(Arena + (Phase)), p = Page(5);
        ulong tx = RW(Generation) + 1;
        if (BufferedWrites && memory.Read32(Arena + (BufferCommitting)) == 0)
        {
            memory.Write32(Arena + (BufferSnapshot), 0);
            if (memory.Read32(Arena + (StagedCount)) == 0)
                memory.Write32(Arena + (Active), 0);
            return Done();
        }

        switch (phase)
        {
            case 0:
                if (BufferedWrites)
                {
                    memory.Write32(Arena + (Phase), 20);
                    return StepResult.Pending;
                }

                memory.Write32(Arena + (Digest), Crc(Index, memory.Read32(Arena + (StagedCount)) * 32, false));
                memory.Write32(Arena + (Phase), 1);
                return StepResult.Pending;
            case 1:
                MakePublication(p, 8, Journal(tx), tx);
                return Issue(IoKind.Write, Journal(tx), p, P, 2);
            case 2:
                memory.Write32(Arena + (Cursor), 0);
                memory.Write32(Arena + (Phase), 3);
                return StepResult.Pending;
            case 3:
                uint page = memory.Read32(Arena + (Cursor)), first = page * ((P - 64) >> 5), take = first < memory.Read32(Arena + (StagedCount)) ? memory.Read32(Arena + (StagedCount)) - first : 0;
                if (take > ((P - 64) >> 5))
                    take = (P - 64) >> 5;
                Header(p, 9, Descriptor(tx, page), tx);
                memory.Write16(p + 48, take);
                Copy(p + 64, Index + first * 32, take * 32);
                Seal(p, take * 32);
                return Issue(IoKind.Write, Descriptor(tx, page), p, P, 4);
            case 4:
                if (memory.Read32(Arena + (Cursor)) < 3)
                {
                    memory.Write32(Arena + (Cursor), memory.Read32(Arena + (Cursor)) + 1);
                    memory.Write32(Arena + (Phase), 3);
                    return StepResult.Pending;
                }

                return Issue(IoKind.Flush, default, 0, 0, 5);
            case 5:
                MakePublication(p, 10, Commit(tx), tx);
                return Issue(IoKind.Write, Commit(tx), p, P, 6);
            case 6:
                return Issue(IoKind.Flush, default, 0, 0, 7);
            case 7:
                memory.Write32(Arena + (Cursor), 0);
                memory.Write32(Arena + (Phase), 8);
                return StepResult.Pending;
            case 8:
                if (BufferedWrites)
                    return Issue(IoKind.Write, GetWide(Index + memory.Read32(Arena + (Cursor)) * 32), BufferedImage(memory.Read32(Arena + (Cursor))), P, 10);
                return Issue(IoKind.Read, Image(tx, memory.Read32(Arena + (Cursor))), p, P, 9);
            case 9:
                if (!ImageValid(p, tx, memory.Read32(Arena + (Cursor))))
                    return Fail(CpxError.Corrupt);
                return Issue(IoKind.Write, GetWide(Index + memory.Read32(Arena + (Cursor)) * 32), p, P, 10);
            case 10:
                if (memory.Read32(Arena + (Cursor)) + 1 < memory.Read32(Arena + (StagedCount)))
                {
                    memory.Write32(Arena + (Cursor), memory.Read32(Arena + (Cursor)) + 1);
                    memory.Write32(Arena + (Phase), 8);
                    return StepResult.Pending;
                }

                return Issue(IoKind.Flush, default, 0, 0, 11);
            case 11:
                // The visible generation advances only after both checkpoint barriers.
                Header(p, 2, Anchor + Q, tx);
                PutWide(p + 72, RW(Identity));
                PutWide(p + 80, tx);
                Seal(p, 24);
                return Issue(IoKind.Write, Anchor + Q, p, P, 12);
            case 12:
                return Issue(IoKind.Flush, default, 0, 0, 13);
            case 13:
                Header(p, 2, Anchor + 2 * Q, tx);
                PutWide(p + 72, RW(Identity));
                PutWide(p + 80, tx);
                Seal(p, 24);
                return Issue(IoKind.Write, Anchor + 2 * Q, p, P, 14);
            case 14:
                return Issue(IoKind.Flush, default, 0, 0, 15);
            case 15:
                WW(Generation, tx);
                memory.Write32(Arena + (Active), 0);
                memory.Write32(Arena + (StagedCount), 0);
                Clear(Index, 2048);
                if (BufferedWrites)
                {
                    memory.Write32(Arena + (BufferCommitting), 0);
                    memory.Write32(Arena + (BufferSnapshot), 0);
                    memory.Write32(Arena + (BufferFreed), 0);
                    memory.Write32(Arena + (BufferNewCount), 0);
                    InvalidateFastWrite();
                    ClearBufferedClean();
                }

                return Done();
            case 20:
                Clear(p, P);
                InvalidateBufferedPage(p);
                return Issue(IoKind.Write, Commit(tx), p, P, 21);
            case 21:
                return Issue(IoKind.Flush, default, 0, 0, 22);
            case 22:
                memory.Write32(Arena + (Cursor), 0);
                memory.Write32(Arena + (Phase), 23);
                return StepResult.Pending;
            case 23:
                uint image = BufferedImage(memory.Read32(Arena + (Cursor)));
                memory.Write32(image + 8, Crc(image, P, true));
                memory.Write32(Index + memory.Read32(Arena + (Cursor)) * 32 + 20, memory.Read32(image + 8));
                if (memory.Read32(Arena + (Cursor)) + 1 < memory.Read32(Arena + (StagedCount)))
                {
                    memory.Write32(Arena + (Cursor), memory.Read32(Arena + (Cursor)) + 1);
                    return StepResult.Pending;
                }

                memory.Write32(Arena + (Phase), 24);
                return StepResult.Pending;
            case 24:
                // Images are contiguous both on disk and in caller-owned memory.
                // Transport still splits this request for device/DMA limits.
                return Issue(IoKind.Write, Image(tx, 0), BufferedImage(0), memory.Read32(Arena + (StagedCount)) * P, 25);
            case 25:
                memory.Write32(Arena + (Digest), Crc(Index, memory.Read32(Arena + (StagedCount)) * 32, false));
                // Once retirement starts, undo images cannot be used for rollback:
                // a failed publication poisons the mount and requires recovery.
                // Borrow five of those slots, leaving every parent scratch page
                // and the capacity-retry snapshot untouched.
                MakePublication(BufferedUndo(0), 8, Journal(tx), tx);
                memory.Write32(Arena + (Cursor), 0);
                memory.Write32(Arena + (Phase), 26);
                return StepResult.Pending;
            case 26:
                uint descriptorPage = memory.Read32(Arena + (Cursor)), begin = descriptorPage * ((P - 64) >> 5);
                uint records = begin < memory.Read32(Arena + (StagedCount)) ? memory.Read32(Arena + (StagedCount)) - begin : 0;
                if (records > ((P - 64) >> 5))
                    records = (P - 64) >> 5;
                uint descriptorBuffer = BufferedUndo(descriptorPage + 1);
                Header(descriptorBuffer, 9, Descriptor(tx, descriptorPage), tx);
                memory.Write16(descriptorBuffer + 48, records);
                Copy(descriptorBuffer + 64, Index + begin * 32, records * 32);
                Seal(descriptorBuffer, records * 32);
                if (descriptorPage < 3)
                {
                    memory.Write32(Arena + (Cursor), descriptorPage + 1);
                    return StepResult.Pending;
                }

                return Issue(IoKind.Write, Journal(tx), BufferedUndo(0), 5 * P, 4);
            default:
                return Fail(CpxError.Corrupt);
        }
    }

    private void MakePublication(uint p, uint type, ulong address, ulong tx)
    {
        Header(p, type, address, tx);
        PutWide(p + 72, RW(Identity));
        PutWide(p + 80, tx);
        PutWide(p + 88, RW(Generation));
        memory.Write32(p + 96, memory.Read32(Arena + (StagedCount)));
        memory.Write32(p + 100, memory.Read32(Arena + (Digest)));
        Seal(p, 40);
    }
}
