namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    // Up to three edits replace one unwritten run with left/written/right
    // pieces. Promoting six inline descriptors needs at most eight records.
    private uint WriteOriginal => Arena + 14700;
    private uint WritePieces => Arena + 14724;
    private uint WriteInlineExtents => Arena + 14816;

    private const uint WriteEditCount = 14800, WriteEditCursor = 14804, WriteFirstMode = 14808, WriteInlineCount = 15008;
    // One journal transaction owns a bounded contiguous run. The DOS caller's
    // buffer stays pinned through the data write and final retirement barrier.
    private const uint WriteRunUnits = 15012, WriteDirect = 15016, WriteAllocationWrapped = 15020;
    private void Extent(uint p, ulong logical, ulong physical, uint count, uint flags)
    {
        PutWide(p, logical);
        PutWide(p + 8, physical);
        memory.Write32(p + 16, count);
        memory.Write32(p + 20, flags);
    }

    private StepResult AllocateWriteStep()
    {
        uint record = ObjectRecord, unit = 1u << (int)memory.Read32(Arena + (UnitShift)), p = Page(2), mode = memory.Read32(Arena + (AllocationMode));
        ulong at = RW(AllocationScan);
        switch (memory.Read32(Arena + (Phase)))
        {
            case 150:
                if (HasBufferedChanges && memory.Read32(Arena + (BufferFreed)) != 0)
                    return RetireBufferedGroup(150);
                return EnsureReserve(30, 156);
            case 156:
                return ReadStaged(Anchor + 8 * Q, Page(3), 151);
            case 151:
                if (!Control(Page(3), 4, Anchor + 8 * Q) || !ReadableGeneration(Page(3)) || memory.Read16(Page(3) + 64) > 64)
                    return Fail(CpxError.Corrupt);
                if (memory.Read16(Page(3) + 64) < 30)
                    return Reject(CpxError.ReserveExhausted);
                if (mode == 3)
                {
                    memory.Write32(Arena + (Phase), 155);
                    return StepResult.Pending;
                }

                ulong first = ((ulong)(2097152u >> (int)memory.Read32(Arena + (UnitShift))));
                uint oldCount = memory.Read32(WriteOriginal + 16);
                ulong hint = GetWide(WriteOriginal + 8) + oldCount;
                bool append = mode == 1 && oldCount != 0 && memory.Read32(WriteOriginal + 20) == 0 && (((ulong)(GetWide(WriteOriginal) + oldCount)) == ((ulong)(RW(DataLogical)))) && DataUnit(hint);
                WW(AllocationScan, append ? hint : first);
                memory.Write32(Arena + (WriteAllocationWrapped), append ? 0u : 1u);
                memory.Write32(Arena + (Phase), 152);
                return StepResult.Pending;
            case 152:
                if ((at) >= (Backup))
                    return WrapAllocationSearch();
                ulong group = (((ulong)(((uint)(((ulong)(at)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(at))) & 0xffff0000)));
                Scalar64.Subtract(RW(Units), group, out var remaining);
                if (!(((ulong)(group)) == 0) && ((uint)(((ulong)(remaining)) >> 32)) == 0 && ((uint)((ulong)(remaining))) < 18 * Q)
                    return WrapAllocationSearch();
                return ReadStaged(Bitmap(at), p, 153);
            case 153:
                if (!ControlBitmap(p, at, (((ulong)(((uint)(((ulong)(at)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(at))) & 0xffff0000)))) || !ReadableGeneration(p))
                    return Fail(CpxError.Corrupt);
                memory.Write32(Arena + (Phase), 154);
                return StepResult.Pending;
            case 154:
                ulong window = (((ulong)(((uint)(((ulong)(at)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(at))) & ~4095u)));
                for (uint bit = ((uint)((ulong)(at))) & 4095; bit < 4096; bit++)
                {
                    if ((memory.Read8(p + 64 + (bit >> 3)) & (0x80 >> (int)(bit & 7))) != 0 || !DataUnit(window + bit))
                        continue;
                    ulong physical = window + bit;
                    uint run = 1;
                    // Inline extent descriptors expose the next mapped run, so
                    // a sparse write cannot accidentally replace a later extent.
                    if (mode == 1 && memory.Read32(Arena + (DataIntra)) == 0 && memory.Read8(record + 85) == 1)
                    {
                        uint limit = (memory.Read32(Arena + (FileBytes)) - memory.Read32(Arena + (FileValue))) >> (int)memory.Read32(Arena + (UnitShift));
                        uint bound = 65536u >> (int)memory.Read32(Arena + (UnitShift));
                        if (limit > bound)
                            limit = bound;
                        for (uint n = 0; n < memory.Read16(record + 90); n++)
                        {
                            ulong logical = GetWide(record + 96 + n * 24);
                            if ((logical) > (RW(DataLogical)) && Scalar64.Subtract(logical, RW(DataLogical), out var gap) && ((uint)(((ulong)(gap)) >> 32)) == 0 && limit > ((uint)((ulong)(gap))))
                                limit = ((uint)((ulong)(gap)));
                        }

                        if (limit > 4096 - bit)
                            limit = 4096 - bit;
                        // A bitmap window contains no reserved range after a
                        // valid first unit; only the final backup can bound it.
                        Scalar64.Subtract(Backup, physical, out var beforeBackup);
                        if (((uint)(((ulong)(beforeBackup)) >> 32)) == 0 && limit > ((uint)((ulong)(beforeBackup))))
                            limit = ((uint)((ulong)(beforeBackup)));
                        while (run < limit)
                        {
                            uint cursor = bit + run, value = memory.Read8(p + 64 + (cursor >> 3));
                            if ((cursor & 7) == 0 && value == 0 && limit - run >= 8)
                            {
                                run += 8;
                                continue;
                            }

                            if ((value & (0x80 >> (int)(cursor & 7))) != 0)
                                break;
                            run++;
                        }

                        if (limit != 0)
                        {
                            memory.Write32(Arena + (DataTake), run * unit);
                            memory.Write32(Arena + (WriteDirect), 1);
                        }
                    }

                    memory.Write32(Arena + (WriteRunUnits), run);
                    WW(NewPhysical, physical);
                    memory.Write32(Arena + (Phase), 155);
                    return StepResult.Pending;
                }

                WW(AllocationScan, window + 4096);
                memory.Write32(Arena + (Phase), 152);
                return StepResult.Pending;
            case 155:
                if (mode == 2)
                {
                    Clear(record + 96, 160);
                    memory.Write8(record + 85, 1);
                    memory.Write16(record + 88, 0);
                    memory.Write16(record + 90, 0);
                    Clear(WriteOriginal, 24);
                }

                PrepareExtentEdits();
                if (mode != 3 && !AdjustWide(record + 16, memory.Read32(Arena + (WriteRunUnits)), true))
                    return Fail(CpxError.Corrupt);
                if (memory.Read32(Arena + (WriteDirect)) == 0)
                    Clear(Page(4), unit);
                if (mode == 2)
                    Copy(Page(4), FileInput, ((uint)((ulong)(GetWide(record + 8)))));
                else
                {
                    if (memory.Read32(Arena + (WriteDirect)) == 0)
                        Copy(Page(4) + memory.Read32(Arena + (DataIntra)), memory.Read32(Arena + (FileBuffer)) + memory.Read32(Arena + (FileValue)), memory.Read32(Arena + (DataTake)));
                    ulong end = RW(FilePosition) + memory.Read32(Arena + (FileValue)) + memory.Read32(Arena + (DataTake));
                    if ((end) > (GetWide(record + 8)))
                        PutWide(record + 8, end);
                }

                memory.Write32(Arena + (Phase), 160);
                return StepResult.Pending;
            case 160:
                return BeginTx(BufferedWrites && memory.Read8(record + 85) != 2 ? 8u : 42u, 162);
            case 162:
                if (mode is 0 or 3)
                {
                    memory.Write32(Arena + (Phase), 170);
                    return StepResult.Pending;
                }

                return ChangeAllocation(RW(NewPhysical), memory.Read32(Arena + (WriteRunUnits)), true, 170);
            case 167:
                return SaveObject(record, 168);
            case 168:
                return Issue(IoKind.Write, RW(NewPhysical), memory.Read32(Arena + (WriteDirect)) != 0 ? memory.Read32(Arena + (FileBuffer)) + memory.Read32(Arena + (FileValue)) : Page(4), memory.Read32(Arena + (WriteRunUnits)) * unit, 300);
            case 170:
                if (mode == 0)
                {
                    memory.Write32(Arena + (Phase), 167);
                    return StepResult.Pending;
                }

                memory.Write32(Arena + (WriteEditCursor), 0);
                if (memory.Read8(record + 85) == 2)
                {
                    memory.Write32(Arena + (Phase), 174);
                    return StepResult.Pending;
                }

                uint count = memory.Read16(record + 90);
                if (memory.Read8(record + 85) != 1 || count > 6)
                    return Fail(CpxError.Corrupt);
                Copy(WriteInlineExtents, record + 96, count * 24);
                memory.Write32(Arena + (WriteInlineCount), count);
                memory.Write32(Arena + (Phase), 171);
                return StepResult.Pending;
            case 171:
                uint edit = memory.Read32(Arena + (WriteEditCursor)), piece = WritePieces + edit * 24, total = memory.Read32(Arena + (WriteInlineCount)), slot = 0;
                while (slot < total && (GetWide(WriteInlineExtents + slot * 24)) < (GetWide(piece)))
                    slot++;
                if (edit == 0 && memory.Read32(Arena + (WriteFirstMode)) == 1)
                {
                    if (slot == total || !(((ulong)(GetWide(WriteInlineExtents + slot * 24))) == ((ulong)(GetWide(piece)))))
                        return Fail(CpxError.Corrupt);
                }
                else
                {
                    if (total == 8 || slot < total && (((ulong)(GetWide(WriteInlineExtents + slot * 24))) == ((ulong)(GetWide(piece)))))
                        return Fail(CpxError.Corrupt);
                    for (uint n = total; n > slot; n--)
                        Copy(WriteInlineExtents + n * 24, WriteInlineExtents + (n - 1) * 24, 24);
                    memory.Write32(Arena + (WriteInlineCount), total + 1);
                }

                Copy(WriteInlineExtents + slot * 24, piece, 24);
                memory.Write32(Arena + (WriteEditCursor), edit + 1);
                if (edit + 1 < memory.Read32(Arena + (WriteEditCount)))
                    return StepResult.Pending;
                if (memory.Read32(Arena + (WriteInlineCount)) > 6)
                    return TakeMetadata(173);
                Clear(record + 96, 160);
                Copy(record + 96, WriteInlineExtents, memory.Read32(Arena + (WriteInlineCount)) * 24);
                memory.Write16(record + 90, memory.Read32(Arena + (WriteInlineCount)));
                memory.Write32(Arena + (Phase), 167);
                return StepResult.Pending;
            case 173:
                ulong root = RW(MetadataResult);
                uint leaf = Page(1);
                Header(leaf, 15, root, RW(Generation) + 1);
                PutWide(leaf + 24, GetWide(record));
                memory.Write16(leaf + 48, memory.Read32(Arena + (WriteInlineCount)));
                memory.Write16(leaf + 52, memory.Read32(Arena + (WriteInlineCount)) * 24);
                Copy(leaf + 64, WriteInlineExtents, memory.Read32(Arena + (WriteInlineCount)) * 24);
                Clear(record + 96, 160);
                memory.Write16(record + 90, 0);
                memory.Write8(record + 85, 2);
                PutWide(record + 24, root);
                return StagePage(leaf, root, 167);
            case 174:
                return MutateTree(CpxTreeKind.Extent, GetWide(record + 24), GetWide(record), WritePieces + memory.Read32(Arena + (WriteEditCursor)) * 24, memory.Read32(Arena + (WriteEditCursor)) == 0 ? memory.Read32(Arena + (WriteFirstMode)) : 0, 175);
            case 175:
                PutWide(record + 24, RW(MutationRoot));
                memory.Write32(Arena + (WriteEditCursor), memory.Read32(Arena + (WriteEditCursor)) + 1);
                memory.Write32(Arena + (Phase), memory.Read32(Arena + (WriteEditCursor)) < memory.Read32(Arena + (WriteEditCount)) ? 174u : 167u);
                return StepResult.Pending;
            default:
                return Fail(CpxError.Corrupt);
        }
    }

    private StepResult WrapAllocationSearch()
    {
        if (memory.Read32(Arena + (WriteAllocationWrapped)) != 0)
            return Reject(CpxError.DiskFull);
        memory.Write32(Arena + (WriteAllocationWrapped), 1);
        WW(AllocationScan, ((ulong)(2097152u >> (int)memory.Read32(Arena + (UnitShift)))));
        memory.Write32(Arena + (Phase), 152);
        return StepResult.Pending;
    }

    private void PrepareExtentEdits()
    {
        ulong logical = RW(DataLogical), physical = RW(NewPhysical);
        uint oldCount = memory.Read32(WriteOriginal + 16);
        uint run = memory.Read32(Arena + (WriteRunUnits));
        memory.Write32(Arena + (WriteFirstMode), 0);
        memory.Write32(Arena + (WriteEditCount), 1);
        Extent(WritePieces, logical, physical, run, 0);
        if (memory.Read32(Arena + (AllocationMode)) == 3)
        {
            Scalar64.Subtract(logical, GetWide(WriteOriginal), out var delta);
            uint left = ((uint)((ulong)(delta))), right = oldCount - left - 1, offset = 0;
            memory.Write32(Arena + (WriteFirstMode), 1);
            if (left != 0)
            {
                Copy(WritePieces, WriteOriginal, 24);
                memory.Write32(WritePieces + 16, left);
                offset = 24;
            }

            Extent(WritePieces + offset, logical, physical, 1, 0);
            offset += 24;
            if (right != 0)
            {
                Extent(WritePieces + offset, logical + 1, physical + 1, right, 1);
                offset += 24;
            }

            memory.Write32(Arena + (WriteEditCount), offset == 72 ? 3u : offset == 48 ? 2u : 1u);
            return;
        }

        ulong oldPhysical = GetWide(WriteOriginal + 8);
        if (oldCount != 0 && oldCount <= 65536 - run && memory.Read32(WriteOriginal + 20) == 0 && (((ulong)(GetWide(WriteOriginal) + oldCount)) == ((ulong)(logical))) && (((ulong)(oldPhysical + oldCount)) == ((ulong)(physical))) && ((uint)(((ulong)(oldPhysical)) >> 32)) == ((uint)(((ulong)(physical)) >> 32)) && (((uint)((ulong)(oldPhysical))) >> 16) == (((uint)((ulong)(physical))) >> 16))
        {
            Copy(WritePieces, WriteOriginal, 24);
            memory.Write32(WritePieces + 16, oldCount + run);
            memory.Write32(Arena + (WriteFirstMode), 1);
        }
    }
}
