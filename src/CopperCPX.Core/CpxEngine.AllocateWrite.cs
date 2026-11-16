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
        {
            uint cpxWideAddress = (uint)(p);
            ulong cpxWideValue = logical;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        {
            uint cpxWideAddress = (uint)(p + 8);
            ulong cpxWideValue = physical;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(p + 16, count);
        memory.Write32(p + 20, flags);
    }

    private StepResult AllocateWriteStep()
    {
        uint cpxArenaBase = Arena;
        uint record = (cpxArenaBase + CpxArena.MountBytes), unit = 1u << (int)memory.Read32(cpxArenaBase + (UnitShift)), p = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(2)) * 4))), mode = memory.Read32(cpxArenaBase + (AllocationMode));
        ulong at = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(AllocationScan)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(AllocationScan)) + 4))))));
        switch (memory.Read32(cpxArenaBase + (Phase)))
        {
            case 150:
                if (((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) && memory.Read32(cpxArenaBase + (Active)) != 0 && memory.Read32(cpxArenaBase + (StagedCount)) != 0) && memory.Read32(cpxArenaBase + (BufferFreed)) != 0)
                    return RetireBufferedGroup(150);
                return EnsureReserve(30, 156);
            case 156:
                return ReadStaged((((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + 8 * (memory.Read32(cpxArenaBase + (GeometryQ))), (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(3)) * 4))), 151);
            case 151:
                if (!Control((memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(3)) * 4))), 4, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + 8 * (memory.Read32(cpxArenaBase + (GeometryQ)))) || !ReadableGeneration((memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(3)) * 4)))) || memory.Read16((memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(3)) * 4))) + 64) > ReserveCapacity)
                    return Fail(CpxError.Corrupt);
                if (memory.Read16((memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(3)) * 4))) + 64) < 30)
                    return Reject(CpxError.ReserveExhausted);
                if (mode == 3)
                {
                    memory.Write32(cpxArenaBase + (Phase), 155);
                    return StepResult.Pending;
                }

                ulong first = ((ulong)(2097152u >> (int)memory.Read32(cpxArenaBase + (UnitShift))));
                uint oldCount = memory.Read32((cpxArenaBase + 14700) + 16);
                ulong hint = ((((ulong)(memory.Read32(((uint)((cpxArenaBase + 14700) + 8))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 14700) + 8)) + 4))))) + oldCount;
                bool append = mode == 1 && oldCount != 0 && memory.Read32((cpxArenaBase + 14700) + 20) == 0 && (((ulong)(((((ulong)(memory.Read32(((uint)((cpxArenaBase + 14700)))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 14700))) + 4))))) + oldCount)) == ((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(DataLogical)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(DataLogical)) + 4))))))))) && DataUnit(hint);
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(AllocationScan);
                ulong cpxWideValue = append ? hint : first;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write32(cpxArenaBase + (WriteAllocationWrapped), append ? 0u : 1u);
                memory.Write32(cpxArenaBase + (Phase), 152);
                return StepResult.Pending;
            case 152:
                if ((at) >= ((((((ulong)(memory.Read32(cpxArenaBase + (((uint)(GeometryBackup)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(GeometryBackup)) + 4)))))))))
                    return WrapAllocationSearch();
                ulong group = (((ulong)(((uint)(((ulong)(at)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(at))) & 0xffff0000)));
                Scalar64.Subtract(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Units)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Units)) + 4)))))), group, out var remaining);
                if (!(((ulong)(group)) == 0) && ((uint)(((ulong)(remaining)) >> 32)) == 0 && ((uint)((ulong)(remaining))) < 18 * MetadataSpan)
                    return WrapAllocationSearch();
                return ReadStaged(Bitmap(at), p, 153);
            case 153:
                if (!ControlBitmap(p, at, (((ulong)(((uint)(((ulong)(at)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(at))) & 0xffff0000)))) || !ReadableGeneration(p))
                    return Fail(CpxError.Corrupt);
                memory.Write32(cpxArenaBase + (Phase), 154);
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
                    if (mode == 1 && memory.Read32(cpxArenaBase + (DataIntra)) == 0 && memory.Read8(record + 85) == 1)
                    {
                        uint limit = (memory.Read32(cpxArenaBase + (FileBytes)) - memory.Read32(cpxArenaBase + (FileValue))) >> (int)memory.Read32(cpxArenaBase + (UnitShift));
                        uint bound = 65536u >> (int)memory.Read32(cpxArenaBase + (UnitShift));
                        if (limit > bound)
                            limit = bound;
                        for (uint n = 0; n < memory.Read16(record + 90); n++)
                        {
                            ulong logical = ((((ulong)(memory.Read32(((uint)(record + 96 + n * 24))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 96 + n * 24)) + 4)))));
                            if ((logical) > (((((ulong)(memory.Read32(cpxArenaBase + (((uint)(DataLogical)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(DataLogical)) + 4))))))) && Scalar64.Subtract(logical, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(DataLogical)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(DataLogical)) + 4)))))), out var gap) && ((uint)(((ulong)(gap)) >> 32)) == 0 && limit > ((uint)((ulong)(gap))))
                                limit = ((uint)((ulong)(gap)));
                        }

                        if (limit > 4096 - bit)
                            limit = 4096 - bit;
                        // A bitmap window contains no reserved range after a
                        // valid first unit; only the final backup can bound it.
                        Scalar64.Subtract((((((ulong)(memory.Read32(cpxArenaBase + (((uint)(GeometryBackup)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(GeometryBackup)) + 4))))))), physical, out var beforeBackup);
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
                            memory.Write32(cpxArenaBase + (DataTake), run * unit);
                            memory.Write32(cpxArenaBase + (WriteDirect), 1);
                        }
                    }

                    memory.Write32(cpxArenaBase + (WriteRunUnits), run);
                    {
                        uint cpxWideAddress = cpxArenaBase + (uint)(NewPhysical);
                        ulong cpxWideValue = physical;
                        memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                        memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                    }

                    memory.Write32(cpxArenaBase + (Phase), 155);
                    return StepResult.Pending;
                }

            {
                uint cpxWideAddress = cpxArenaBase + (uint)(AllocationScan);
                ulong cpxWideValue = window + 4096;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write32(cpxArenaBase + (Phase), 152);
                return StepResult.Pending;
            case 155:
                if (mode == 2)
                {
                    Clear(record + 96, 160);
                    memory.Write8(record + 85, 1);
                    memory.Write16(record + 88, 0);
                    memory.Write16(record + 90, 0);
                    Clear((cpxArenaBase + 14700), 24);
                }

                PrepareExtentEdits();
                if (mode != 3 && !AdjustWide(record + 16, memory.Read32(cpxArenaBase + (WriteRunUnits)), true))
                    return Fail(CpxError.Corrupt);
                if (memory.Read32(cpxArenaBase + (WriteDirect)) == 0)
                    Clear((memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(4)) * 4))), unit);
                if (mode == 2)
                    Copy((memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(4)) * 4))), ((cpxArenaBase + CpxArena.MountBytes) + 256), ((uint)((ulong)(((((ulong)(memory.Read32(((uint)(record + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 8)) + 4)))))))));
                else
                {
                    if (memory.Read32(cpxArenaBase + (WriteDirect)) == 0)
                        Copy((memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(4)) * 4))) + memory.Read32(cpxArenaBase + (DataIntra)), memory.Read32(cpxArenaBase + (FileBuffer)) + memory.Read32(cpxArenaBase + (FileValue)), memory.Read32(cpxArenaBase + (DataTake)));
                    ulong end = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(FilePosition)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(FilePosition)) + 4)))))) + memory.Read32(cpxArenaBase + (FileValue)) + memory.Read32(cpxArenaBase + (DataTake));
                    if ((end) > (((((ulong)(memory.Read32(((uint)(record + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 8)) + 4)))))))
                    {
                        uint cpxWideAddress = (uint)(record + 8);
                        ulong cpxWideValue = end;
                        memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                        memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                    }
                }

                memory.Write32(cpxArenaBase + (Phase), 160);
                return StepResult.Pending;
            case 160:
                return BeginTx((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) && memory.Read8(record + 85) != 2 ? 8u : 42u, 162);
            case 162:
                if (mode is 0 or 3)
                {
                    memory.Write32(cpxArenaBase + (Phase), 170);
                    return StepResult.Pending;
                }

                return ChangeAllocation(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NewPhysical)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NewPhysical)) + 4)))))), memory.Read32(cpxArenaBase + (WriteRunUnits)), true, 170);
            case 167:
                return SaveObject(record, 168);
            case 168:
                return Issue(IoKind.Write, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NewPhysical)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NewPhysical)) + 4)))))), memory.Read32(cpxArenaBase + (WriteDirect)) != 0 ? memory.Read32(cpxArenaBase + (FileBuffer)) + memory.Read32(cpxArenaBase + (FileValue)) : (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(4)) * 4))), memory.Read32(cpxArenaBase + (WriteRunUnits)) * unit, 300);
            case 170:
                if (mode == 0)
                {
                    memory.Write32(cpxArenaBase + (Phase), 167);
                    return StepResult.Pending;
                }

                memory.Write32(cpxArenaBase + (WriteEditCursor), 0);
                if (memory.Read8(record + 85) == 2)
                {
                    memory.Write32(cpxArenaBase + (Phase), 174);
                    return StepResult.Pending;
                }

                uint count = memory.Read16(record + 90);
                if (memory.Read8(record + 85) != 1 || count > 6)
                    return Fail(CpxError.Corrupt);
                Copy((cpxArenaBase + 14816), record + 96, count * 24);
                memory.Write32(cpxArenaBase + (WriteInlineCount), count);
                memory.Write32(cpxArenaBase + (Phase), 171);
                return StepResult.Pending;
            case 171:
                uint edit = memory.Read32(cpxArenaBase + (WriteEditCursor)), piece = (cpxArenaBase + 14724) + edit * 24, total = memory.Read32(cpxArenaBase + (WriteInlineCount)), slot = 0;
                while (slot < total && (((((ulong)(memory.Read32(((uint)((cpxArenaBase + 14816) + slot * 24))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 14816) + slot * 24)) + 4)))))) < (((((ulong)(memory.Read32(((uint)(piece))))) << 32 | ((ulong)(memory.Read32(((uint)(piece)) + 4)))))))
                    slot++;
                if (edit == 0 && memory.Read32(cpxArenaBase + (WriteFirstMode)) == 1)
                {
                    if (slot == total || !(((ulong)(((((ulong)(memory.Read32(((uint)((cpxArenaBase + 14816) + slot * 24))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 14816) + slot * 24)) + 4))))))) == ((ulong)(((((ulong)(memory.Read32(((uint)(piece))))) << 32 | ((ulong)(memory.Read32(((uint)(piece)) + 4)))))))))
                        return Fail(CpxError.Corrupt);
                }
                else
                {
                    if (total == 8 || slot < total && (((ulong)(((((ulong)(memory.Read32(((uint)((cpxArenaBase + 14816) + slot * 24))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 14816) + slot * 24)) + 4))))))) == ((ulong)(((((ulong)(memory.Read32(((uint)(piece))))) << 32 | ((ulong)(memory.Read32(((uint)(piece)) + 4)))))))))
                        return Fail(CpxError.Corrupt);
                    for (uint n = total; n > slot; n--)
                        Copy((cpxArenaBase + 14816) + n * 24, (cpxArenaBase + 14816) + (n - 1) * 24, 24);
                    memory.Write32(cpxArenaBase + (WriteInlineCount), total + 1);
                }

                Copy((cpxArenaBase + 14816) + slot * 24, piece, 24);
                memory.Write32(cpxArenaBase + (WriteEditCursor), edit + 1);
                if (edit + 1 < memory.Read32(cpxArenaBase + (WriteEditCount)))
                    return StepResult.Pending;
                if (memory.Read32(cpxArenaBase + (WriteInlineCount)) > 6)
                    return TakeMetadata(173);
                Clear(record + 96, 160);
                Copy(record + 96, (cpxArenaBase + 14816), memory.Read32(cpxArenaBase + (WriteInlineCount)) * 24);
                memory.Write16(record + 90, memory.Read32(cpxArenaBase + (WriteInlineCount)));
                memory.Write32(cpxArenaBase + (Phase), 167);
                return StepResult.Pending;
            case 173:
                ulong root = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(MetadataResult)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(MetadataResult)) + 4))))));
                uint leaf = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(1)) * 4)));
                Header(leaf, 15, root, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)) + 4)))))) + 1);
            {
                uint cpxWideAddress = (uint)(leaf + 24);
                ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)(record))))) << 32 | ((ulong)(memory.Read32(((uint)(record)) + 4)))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write16(leaf + 48, memory.Read32(cpxArenaBase + (WriteInlineCount)));
                memory.Write16(leaf + 52, memory.Read32(cpxArenaBase + (WriteInlineCount)) * 24);
                Copy(leaf + 64, (cpxArenaBase + 14816), memory.Read32(cpxArenaBase + (WriteInlineCount)) * 24);
                Clear(record + 96, 160);
                memory.Write16(record + 90, 0);
                memory.Write8(record + 85, 2);
            {
                uint cpxWideAddress = (uint)(record + 24);
                ulong cpxWideValue = root;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                return StagePage(leaf, root, 167);
            case 174:
                return MutateTree(CpxTreeKind.Extent, ((((ulong)(memory.Read32(((uint)(record + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 24)) + 4))))), ((((ulong)(memory.Read32(((uint)(record))))) << 32 | ((ulong)(memory.Read32(((uint)(record)) + 4))))), (cpxArenaBase + 14724) + memory.Read32(cpxArenaBase + (WriteEditCursor)) * 24, memory.Read32(cpxArenaBase + (WriteEditCursor)) == 0 ? memory.Read32(cpxArenaBase + (WriteFirstMode)) : 0, 175);
            case 175:
            {
                uint cpxWideAddress = (uint)(record + 24);
                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(MutationRoot)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(MutationRoot)) + 4))))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write32(cpxArenaBase + (WriteEditCursor), memory.Read32(cpxArenaBase + (WriteEditCursor)) + 1);
                memory.Write32(cpxArenaBase + (Phase), memory.Read32(cpxArenaBase + (WriteEditCursor)) < memory.Read32(cpxArenaBase + (WriteEditCount)) ? 174u : 167u);
                return StepResult.Pending;
            default:
                return Fail(CpxError.Corrupt);
        }
    }

    private StepResult WrapAllocationSearch()
    {
        uint cpxArenaBase = Arena;
        if (memory.Read32(cpxArenaBase + (WriteAllocationWrapped)) != 0)
            return Reject(CpxError.DiskFull);
        memory.Write32(cpxArenaBase + (WriteAllocationWrapped), 1);
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(AllocationScan);
            ulong cpxWideValue = ((ulong)(2097152u >> (int)memory.Read32(cpxArenaBase + (UnitShift))));
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(cpxArenaBase + (Phase), 152);
        return StepResult.Pending;
    }

    private void PrepareExtentEdits()
    {
        uint cpxArenaBase = Arena;
        ulong logical = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(DataLogical)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(DataLogical)) + 4)))))), physical = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NewPhysical)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NewPhysical)) + 4))))));
        uint oldCount = memory.Read32((cpxArenaBase + 14700) + 16);
        uint run = memory.Read32(cpxArenaBase + (WriteRunUnits));
        memory.Write32(cpxArenaBase + (WriteFirstMode), 0);
        memory.Write32(cpxArenaBase + (WriteEditCount), 1);
        Extent((cpxArenaBase + 14724), logical, physical, run, 0);
        if (memory.Read32(cpxArenaBase + (AllocationMode)) == 3)
        {
            Scalar64.Subtract(logical, ((((ulong)(memory.Read32(((uint)((cpxArenaBase + 14700)))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 14700))) + 4))))), out var delta);
            uint left = ((uint)((ulong)(delta))), right = oldCount - left - 1, offset = 0;
            memory.Write32(cpxArenaBase + (WriteFirstMode), 1);
            if (left != 0)
            {
                Copy((cpxArenaBase + 14724), (cpxArenaBase + 14700), 24);
                memory.Write32((cpxArenaBase + 14724) + 16, left);
                offset = 24;
            }

            Extent((cpxArenaBase + 14724) + offset, logical, physical, 1, 0);
            offset += 24;
            if (right != 0)
            {
                Extent((cpxArenaBase + 14724) + offset, logical + 1, physical + 1, right, 1);
                offset += 24;
            }

            memory.Write32(cpxArenaBase + (WriteEditCount), offset == 72 ? 3u : offset == 48 ? 2u : 1u);
            return;
        }

        ulong oldPhysical = ((((ulong)(memory.Read32(((uint)((cpxArenaBase + 14700) + 8))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 14700) + 8)) + 4)))));
        if (oldCount != 0 && oldCount <= 65536 - run && memory.Read32((cpxArenaBase + 14700) + 20) == 0 && (((ulong)(((((ulong)(memory.Read32(((uint)((cpxArenaBase + 14700)))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 14700))) + 4))))) + oldCount)) == ((ulong)(logical))) && (((ulong)(oldPhysical + oldCount)) == ((ulong)(physical))) && ((uint)(((ulong)(oldPhysical)) >> 32)) == ((uint)(((ulong)(physical)) >> 32)) && (((uint)((ulong)(oldPhysical))) >> 16) == (((uint)((ulong)(physical))) >> 16))
        {
            Copy((cpxArenaBase + 14724), (cpxArenaBase + 14700), 24);
            memory.Write32((cpxArenaBase + 14724) + 16, oldCount + run);
            memory.Write32(cpxArenaBase + (WriteFirstMode), 1);
        }
    }
}