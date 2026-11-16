namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    public uint ReadBuffer => (memory.Read32(Arena + (GeometryPages + ((uint)(0)) * 4)));
    public uint TransactionImageCount => memory.Read32(Arena + (StagedCount));

    private int FindStaged(ulong at)
    {
        uint cpxArenaBase = Arena;
        if (memory.Read32(cpxArenaBase + (Active)) == 0)
            return -1;
        uint count = memory.Read32(cpxArenaBase + StagedCount), table = memory.Read32(cpxArenaBase + GeometryIndex);
        uint high = (uint)(at >> 32), low = (uint)at;
        for (uint n = 0; n < count; n++)
            if (memory.Read32(table + n * 32) == high && memory.Read32(table + n * 32 + 4) == low)
                return (int)n;
        return -1;
    }

    private StepResult TransactionStep()
    {
        uint cpxArenaBase = Arena;
        uint phase = memory.Read32(cpxArenaBase + (Phase)), p = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(5)) * 4)));
        ulong tx = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)) + 4)))))) + 1;
        switch ((CpxOperation)memory.Read32(cpxArenaBase + (OperationAt)))
        {
            case CpxOperation.BeginTransaction:
                if (phase == 0)
                {
                    if ((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1))
                    {
                        uint credits = memory.Read32(cpxArenaBase + (StagedLimit));
                        memory.Write32(cpxArenaBase + (MetadataTaken), 0);
                        memory.Write32(cpxArenaBase + (MetadataReleased), 0);
                        memory.Write32(cpxArenaBase + (MetadataLimit), credits == 25 ? 14u : credits is 23 or 34 ? 11u : credits == 39 ? 20u : credits is 22 or 30 ? 10u : credits == 42 ? 18u : credits == 8 ? 2u : credits == 6 ? 1u : 0u);
                        if (memory.Read32(cpxArenaBase + (Active)) == 0)
                        {
                            memory.Write32(cpxArenaBase + (StagedCount), 0);
                            Clear((memory.Read32(cpxArenaBase + (GeometryIndex))), 2048);
                        }

                        memory.Write32(cpxArenaBase + (Active), 1);
                        memory.Write32(cpxArenaBase + (StagedLimit), 42);
                        return Done();
                    }

                    memory.Write32(cpxArenaBase + (Active), 1);
                    memory.Write32(cpxArenaBase + (StagedCount), 0);
                    memory.Write32(cpxArenaBase + (MetadataTaken), 0);
                    memory.Write32(cpxArenaBase + (MetadataReleased), 0);
                    memory.Write32(cpxArenaBase + (MetadataLimit), memory.Read32(cpxArenaBase + (StagedLimit)) == 39 ? (SmallMetadata ? 22u : 20u) : memory.Read32(cpxArenaBase + (StagedLimit))is 22 or 30 ? 10u : memory.Read32(cpxArenaBase + (StagedLimit)) == 42 ? 18u : memory.Read32(cpxArenaBase + (StagedLimit)) == 6 ? 1u : 0u);
                    Clear((memory.Read32(cpxArenaBase + (GeometryIndex))), 2048);
                    Clear(p, (memory.Read32(cpxArenaBase + (PageBytes))));
                    return Issue(IoKind.Write, Commit(tx), p, (memory.Read32(cpxArenaBase + (PageBytes))), 1);
                }

                if (phase == 1)
                    return Issue(IoKind.Flush, default, 0, 0, 2);
                return Done();
            case CpxOperation.AbortTransaction:
                if ((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1))
                {
                    DiscardBufferedOperation();
                    return Done();
                }

                memory.Write32(cpxArenaBase + (Active), 0);
                memory.Write32(cpxArenaBase + (AbortOnly), 0);
                memory.Write32(cpxArenaBase + (StagedCount), 0);
                Clear((memory.Read32(cpxArenaBase + (GeometryIndex))), 2048);
                return Done();
            case CpxOperation.StageMetadata:
                if (phase != 0)
                    return Done();
                return StagePage(memory.Read32(cpxArenaBase + (SourcePage)), ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Target)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Target)) + 4)))))), 1);
        }

        return CommitStep();
    }

    private StepResult StagePage(uint p, ulong target, uint nextPhase)
    {
        uint cpxArenaBase = Arena;
        // Reading/validating a page and calculating its new checksum belong to
        // separate processing phases. The caller-owned page stays pinned by the
        // single active operation until this child continuation completes.
        memory.Write32(cpxArenaBase + (PreparedBuffer), p);
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(PreparedTarget);
            ulong cpxWideValue = target;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        return Call(CpxOperation.PublishMetadata, nextPhase);
    }

    private StepResult PublishMetadata(uint p, ulong target, uint nextPhase)
    {
        uint cpxArenaBase = Arena;
        ulong tx = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)) + 4)))))) + 1;
        uint type = memory.Read16(p + 4);
        ulong owner = ((((ulong)(memory.Read32(((uint)(p + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 24)) + 4)))));
        if (memory.Read32(p) != MediaMagic || memory.Read16(p + 6) != MediaVersion || !TargetValid(target, type, owner) || !PayloadValid(p))
            return Fail(CpxError.Corrupt);
        int slot = FindStaged(target);
        if (slot < 0)
        {
            if ((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) && memory.Read32(cpxArenaBase + (StagedCount)) == 42)
                return RetryBufferedOperation();
            if (memory.Read32(cpxArenaBase + (StagedCount)) == memory.Read32(cpxArenaBase + (StagedLimit)))
            {
                memory.Write32(cpxArenaBase + (AbortOnly), 1);
                memory.Write32(cpxArenaBase + (ErrorAt), (uint)CpxError.TransactionLimit);
                memory.Write32(cpxArenaBase + (Finished), 1);
                return StepResult.Failed;
            }

            slot = (int)memory.Read32(cpxArenaBase + (StagedCount));
            memory.Write32(cpxArenaBase + (StagedCount), (uint)slot + 1);
        }

        {
            uint cpxWideAddress = (uint)(p + 16);
            ulong cpxWideValue = target;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        {
            uint cpxWideAddress = (uint)(p + 32);
            ulong cpxWideValue = tx;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        if ((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1))
        {
            SaveBufferedUndo((uint)slot);
            InvalidateBufferedClean(target);
        }

        memory.Write32(p + 8, (memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) ? 0u : Crc(p, (memory.Read32(cpxArenaBase + (PageBytes))), true));
        uint descriptor = (memory.Read32(cpxArenaBase + (GeometryIndex))) + (uint)slot * 32;
        {
            uint cpxWideAddress = (uint)(descriptor);
            ulong cpxWideValue = target;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        {
            uint cpxWideAddress = (uint)(descriptor + 8);
            ulong cpxWideValue = owner;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(descriptor + 16, (uint)slot);
        memory.Write32(descriptor + 20, memory.Read32(p + 8));
        memory.Write16(descriptor + 24, type);
        if ((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1))
        {
            Copy((memory.Read32(cpxArenaBase + (BufferAddress)) + 4096 + ((uint)((uint)slot)) * (memory.Read32(cpxArenaBase + (PageBytes)))), p, (memory.Read32(cpxArenaBase + (PageBytes))));
            TrustBufferedPage(p, target);
            if (type == 11 || DirectInodes && type is 23 or 24)
                CacheBufferedObjects(p, target);
            memory.Write32(cpxArenaBase + (Phase), nextPhase);
            return StepResult.Pending;
        }

        return Issue(IoKind.Write, Image(tx, (uint)slot), p, (memory.Read32(cpxArenaBase + (PageBytes))), nextPhase);
    }

    private StepResult CommitStep()
    {
        uint cpxArenaBase = Arena;
        uint phase = memory.Read32(cpxArenaBase + (Phase)), p = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(5)) * 4)));
        ulong tx = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)) + 4)))))) + 1;
        if ((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) && memory.Read32(cpxArenaBase + (BufferCommitting)) == 0)
        {
            memory.Write32(cpxArenaBase + (BufferSnapshot), 0);
            if (memory.Read32(cpxArenaBase + (StagedCount)) == 0)
                memory.Write32(cpxArenaBase + (Active), 0);
            return Done();
        }

        if (ShadowVolume && phase != 15) return ShadowCommitStep();
        switch (phase)
        {
            case 0:
                if ((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1))
                {
                    memory.Write32(cpxArenaBase + (Phase), AlternatingCheckpoints ? 23u : 20u);
                    if (AlternatingCheckpoints) memory.Write32(cpxArenaBase + Cursor, 0);
                    return StepResult.Pending;
                }

                memory.Write32(cpxArenaBase + (Digest), Crc((memory.Read32(cpxArenaBase + (GeometryIndex))), memory.Read32(cpxArenaBase + (StagedCount)) * 32, false));
                memory.Write32(cpxArenaBase + (Phase), 1);
                return StepResult.Pending;
            case 1:
                MakePublication(p, 8, Journal(tx), tx);
                return Issue(IoKind.Write, Journal(tx), p, (memory.Read32(cpxArenaBase + (PageBytes))), 2);
            case 2:
                memory.Write32(cpxArenaBase + (Cursor), 0);
                memory.Write32(cpxArenaBase + (Phase), 3);
                return StepResult.Pending;
            case 3:
                uint page = memory.Read32(cpxArenaBase + (Cursor)), first = page * (((memory.Read32(cpxArenaBase + (PageBytes))) - 64) >> 5), take = first < memory.Read32(cpxArenaBase + (StagedCount)) ? memory.Read32(cpxArenaBase + (StagedCount)) - first : 0;
                if (take > (((memory.Read32(cpxArenaBase + (PageBytes))) - 64) >> 5))
                    take = ((memory.Read32(cpxArenaBase + (PageBytes))) - 64) >> 5;
                Header(p, 9, Descriptor(tx, page), tx);
                memory.Write16(p + 48, take);
                Copy(p + 64, (memory.Read32(cpxArenaBase + (GeometryIndex))) + first * 32, take * 32);
                Seal(p, take * 32);
                return Issue(IoKind.Write, Descriptor(tx, page), p, (memory.Read32(cpxArenaBase + (PageBytes))), 4);
            case 4:
                if (memory.Read32(cpxArenaBase + (Cursor)) < 3)
                {
                    memory.Write32(cpxArenaBase + (Cursor), memory.Read32(cpxArenaBase + (Cursor)) + 1);
                    memory.Write32(cpxArenaBase + (Phase), 3);
                    return StepResult.Pending;
                }

                return Issue(IoKind.Flush, default, 0, 0, 5);
            case 5:
                MakePublication(p, 10, Commit(tx), tx);
                return Issue(IoKind.Write, Commit(tx), p, (memory.Read32(cpxArenaBase + (PageBytes))), 6);
            case 6:
                return Issue(IoKind.Flush, default, 0, 0, 7);
            case 7:
                memory.Write32(cpxArenaBase + (Cursor), 0);
                memory.Write32(cpxArenaBase + (Phase), 8);
                return StepResult.Pending;
            case 8:
                if ((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1))
                    return Issue(IoKind.Write, ((((ulong)(memory.Read32(((uint)((memory.Read32(cpxArenaBase + (GeometryIndex))) + memory.Read32(cpxArenaBase + (Cursor)) * 32))))) << 32 | ((ulong)(memory.Read32(((uint)((memory.Read32(cpxArenaBase + (GeometryIndex))) + memory.Read32(cpxArenaBase + (Cursor)) * 32)) + 4))))), (memory.Read32(cpxArenaBase + (BufferAddress)) + 4096 + ((uint)(memory.Read32(cpxArenaBase + (Cursor)))) * (memory.Read32(cpxArenaBase + (PageBytes)))), (memory.Read32(cpxArenaBase + (PageBytes))), 10);
                return Issue(IoKind.Read, Image(tx, memory.Read32(cpxArenaBase + (Cursor))), p, (memory.Read32(cpxArenaBase + (PageBytes))), 9);
            case 9:
                if (!ImageValid(p, tx, memory.Read32(cpxArenaBase + (Cursor))))
                    return Fail(CpxError.Corrupt);
                return Issue(IoKind.Write, ((((ulong)(memory.Read32(((uint)((memory.Read32(cpxArenaBase + (GeometryIndex))) + memory.Read32(cpxArenaBase + (Cursor)) * 32))))) << 32 | ((ulong)(memory.Read32(((uint)((memory.Read32(cpxArenaBase + (GeometryIndex))) + memory.Read32(cpxArenaBase + (Cursor)) * 32)) + 4))))), p, (memory.Read32(cpxArenaBase + (PageBytes))), 10);
            case 10:
                if (memory.Read32(cpxArenaBase + (Cursor)) + 1 < memory.Read32(cpxArenaBase + (StagedCount)))
                {
                    memory.Write32(cpxArenaBase + (Cursor), memory.Read32(cpxArenaBase + (Cursor)) + 1);
                    memory.Write32(cpxArenaBase + (Phase), 8);
                    return StepResult.Pending;
                }

                return Issue(IoKind.Flush, default, 0, 0, 11);
            case 11:
                if (BufferedWrites && AlternatingCheckpoints) return PublishAlternatingCheckpoint(p, tx);
                // The visible generation advances only after both checkpoint barriers.
                Header(p, 2, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + (memory.Read32(cpxArenaBase + (GeometryQ))), tx);
            {
                uint cpxWideAddress = (uint)(p + 72);
                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Identity)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Identity)) + 4))))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

            {
                uint cpxWideAddress = (uint)(p + 80);
                ulong cpxWideValue = tx;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                Seal(p, 24);
                return Issue(IoKind.Write, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + (memory.Read32(cpxArenaBase + (GeometryQ))), p, (memory.Read32(cpxArenaBase + (PageBytes))), 12);
            case 12:
                return Issue(IoKind.Flush, default, 0, 0, 13);
            case 13:
                Header(p, 2, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + 2 * (memory.Read32(cpxArenaBase + (GeometryQ))), tx);
            {
                uint cpxWideAddress = (uint)(p + 72);
                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Identity)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Identity)) + 4))))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

            {
                uint cpxWideAddress = (uint)(p + 80);
                ulong cpxWideValue = tx;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                Seal(p, 24);
                return Issue(IoKind.Write, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + 2 * (memory.Read32(cpxArenaBase + (GeometryQ))), p, (memory.Read32(cpxArenaBase + (PageBytes))), 14);
            case 14:
                return Issue(IoKind.Flush, default, 0, 0, 15);
            case 40:
                return Issue(IoKind.Flush, default, 0, 0, 41);
            case 41:
            case 15:
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(Generation);
                ulong cpxWideValue = tx;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write32(cpxArenaBase + (Active), 0);
                memory.Write32(cpxArenaBase + (StagedCount), 0);
                Clear((memory.Read32(cpxArenaBase + (GeometryIndex))), 2048);
                if ((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1))
                {
                    memory.Write32(cpxArenaBase + (BufferCommitting), 0);
                    memory.Write32(cpxArenaBase + (BufferSnapshot), 0);
                    memory.Write32(cpxArenaBase + (BufferFreed), 0);
                    memory.Write32(cpxArenaBase + (BufferNewCount), 0);
                    InvalidateFastWrite();
                    ClearBufferedClean();
                }

                return Done();
            case 20:
                Clear(p, (memory.Read32(cpxArenaBase + (PageBytes))));
                InvalidateBufferedPage(p);
                return Issue(IoKind.Write, Commit(tx), p, (memory.Read32(cpxArenaBase + (PageBytes))), 21);
            case 21:
                return Issue(IoKind.Flush, default, 0, 0, 22);
            case 22:
                memory.Write32(cpxArenaBase + (Cursor), 0);
                memory.Write32(cpxArenaBase + (Phase), 23);
                return StepResult.Pending;
            case 23:
                uint image = (memory.Read32(cpxArenaBase + (BufferAddress)) + 4096 + ((uint)(memory.Read32(cpxArenaBase + (Cursor)))) * (memory.Read32(cpxArenaBase + (PageBytes))));
                memory.Write32(image + 8, Crc(image, (memory.Read32(cpxArenaBase + (PageBytes))), true));
                memory.Write32((memory.Read32(cpxArenaBase + (GeometryIndex))) + memory.Read32(cpxArenaBase + (Cursor)) * 32 + 20, memory.Read32(image + 8));
                if (memory.Read32(cpxArenaBase + (Cursor)) + 1 < memory.Read32(cpxArenaBase + (StagedCount)))
                {
                    memory.Write32(cpxArenaBase + (Cursor), memory.Read32(cpxArenaBase + (Cursor)) + 1);
                    return StepResult.Pending;
                }

                memory.Write32(cpxArenaBase + (Phase), 24);
                return StepResult.Pending;
            case 24:
                // Images are contiguous both on disk and in caller-owned memory.
                // Transport still splits this request for device/DMA limits.
                return Issue(IoKind.Write, Image(tx, 0), (memory.Read32(cpxArenaBase + (BufferAddress)) + 4096 + ((uint)(0)) * (memory.Read32(cpxArenaBase + (PageBytes)))), memory.Read32(cpxArenaBase + (StagedCount)) * (memory.Read32(cpxArenaBase + (PageBytes))), 25);
            case 25:
                memory.Write32(cpxArenaBase + (Digest), Crc((memory.Read32(cpxArenaBase + (GeometryIndex))), memory.Read32(cpxArenaBase + (StagedCount)) * 32, false));
                // Once retirement starts, undo images cannot be used for rollback:
                // a failed publication poisons the mount and requires recovery.
                // Borrow five of those slots, leaving every parent scratch page
                // and the capacity-retry snapshot untouched.
                MakePublication((memory.Read32(cpxArenaBase + (BufferAddress)) + 4096 + (42 + ((uint)(0))) * (memory.Read32(cpxArenaBase + (PageBytes)))), 8, Journal(tx), tx);
                memory.Write32(cpxArenaBase + (Cursor), 0);
                memory.Write32(cpxArenaBase + (Phase), 26);
                return StepResult.Pending;
            case 26:
                uint descriptorPage = memory.Read32(cpxArenaBase + (Cursor)), begin = descriptorPage * (((memory.Read32(cpxArenaBase + (PageBytes))) - 64) >> 5);
                uint records = begin < memory.Read32(cpxArenaBase + (StagedCount)) ? memory.Read32(cpxArenaBase + (StagedCount)) - begin : 0;
                if (records > (((memory.Read32(cpxArenaBase + (PageBytes))) - 64) >> 5))
                    records = ((memory.Read32(cpxArenaBase + (PageBytes))) - 64) >> 5;
                uint descriptorBuffer = (memory.Read32(cpxArenaBase + (BufferAddress)) + 4096 + (42 + ((uint)(descriptorPage + 1))) * (memory.Read32(cpxArenaBase + (PageBytes))));
                Header(descriptorBuffer, 9, Descriptor(tx, descriptorPage), tx);
                memory.Write16(descriptorBuffer + 48, records);
                Copy(descriptorBuffer + 64, (memory.Read32(cpxArenaBase + (GeometryIndex))) + begin * 32, records * 32);
                Seal(descriptorBuffer, records * 32);
                if (descriptorPage < 3)
                {
                    memory.Write32(cpxArenaBase + (Cursor), descriptorPage + 1);
                    return StepResult.Pending;
                }

                return Issue(IoKind.Write, Journal(tx), (memory.Read32(cpxArenaBase + (BufferAddress)) + 4096 + (42 + ((uint)(0))) * (memory.Read32(cpxArenaBase + (PageBytes)))), 5 * (memory.Read32(cpxArenaBase + (PageBytes))), 4);
            default:
                return Fail(CpxError.Corrupt);
        }
    }

    private void MakePublication(uint p, uint type, ulong address, ulong tx)
    {
        uint cpxArenaBase = Arena;
        Header(p, type, address, tx);
        {
            uint cpxWideAddress = (uint)(p + 72);
            ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Identity)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Identity)) + 4))))));
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        {
            uint cpxWideAddress = (uint)(p + 80);
            ulong cpxWideValue = tx;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        {
            uint cpxWideAddress = (uint)(p + 88);
            ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)) + 4))))));
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(p + 96, memory.Read32(cpxArenaBase + (StagedCount)));
        memory.Write32(p + 100, memory.Read32(cpxArenaBase + (Digest)));
        Seal(p, 40);
    }
}
