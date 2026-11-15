namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    public uint TransferredBytes => memory.Read32(Arena + (FileValue));

    /// <summary>The caller owns and pins this address until completion or quiesced
    /// cancellation, exactly as for a DOS read/write packet. No managed view is retained.</summary>
    public CpxError BeginFileRead(Wide id, Wide position, uint destination, uint bytes) => BeginFileData(CpxOperation.ReadFile, id, position, destination, bytes);
    /// <summary>Writes mappings, promotes extent lists and splits unwritten runs.
    /// Each retired contiguous run advances committed prefix progress.</summary>
    public CpxError BeginMappedWrite(Wide id, Wide position, uint source, uint bytes) => BeginFileData(CpxOperation.WriteMapped, id, position, source, bytes);
    private CpxError BeginFileData(CpxOperation op, ulong id, ulong position, uint buffer, uint bytes)
    {
        if ((buffer == 0 && bytes != 0) || buffer > uint.MaxValue - bytes || !Scalar64.Add(position, ((ulong)(bytes)), out _))
            return CpxError.InvalidArgument;
        CpxError error = StartOperation(op, id);
        if (error != CpxError.None)
            return error;
        WW(FilePosition, position);
        memory.Write32(Arena + (FileBuffer), buffer);
        memory.Write32(Arena + (FileBytes), bytes);
        memory.Write32(Arena + (WriteGapPrepared), 0);
        memory.Write32(Arena + (WriteRunUnits), 1);
        memory.Write32(Arena + (WriteDirect), 0);
        if (op == CpxOperation.WriteMapped)
        {
            if (!BeginFastAppend(id, position, bytes) && !BeginFastInitialWrite(id, position, bytes) && !BeginFastMapped(id, position, bytes, true))
                InvalidateFastWrite();
        }
        else
            BeginFastMapped(id, position, bytes, false);
        return CpxError.None;
    }

    private StepResult FileDataStep()
    {
        uint phase = memory.Read32(Arena + (Phase));
        if (phase == 500)
            return Issue(IoKind.Write, RW(NewPhysical), memory.Read32(Arena + (FileBuffer)), memory.Read32(Arena + (FileBytes)), 501);
        if (phase == 501)
            return FinishFastAppend();
        if (phase is 502 or 503)
            return PrepareCrossWindowAppend();
        if (phase is >= 510 and <= 512)
            return PrepareFastInitialWrite();
        if (phase is >= 520 and <= 532)
            return FastMappedStep(phase);
        if (phase is >= 540 and <= 543)
            return FastPartialMappedStep(phase);
        uint record = ObjectRecord, unit = 1u << (int)memory.Read32(Arena + (UnitShift));
        bool write = (CpxOperation)memory.Read32(Arena + (OperationAt)) == CpxOperation.WriteMapped;
        if (phase < 100)
        {
            StepResult lookup = ObjectStep();
            if (lookup != StepResult.Complete)
                return lookup;
            memory.Write32(Arena + (Finished), 0);
            if (memory.Read32(Arena + (SourcePage)) == 0)
                return Reject(CpxError.NotFound);
            memory.Write32(Arena + (Phase), 100);
            return StepResult.Pending;
        }

        if (phase >= 200 && phase < 300)
        {
            memory.Write32(Arena + (Phase), phase - 200);
            StepResult seek = SeekStep();
            memory.Write32(Arena + (Phase), memory.Read32(Arena + (Phase)) + 200);
            if (seek == StepResult.Complete)
            {
                memory.Write32(Arena + (Finished), 0);
                memory.Write32(Arena + (Phase), 103);
                return StepResult.Pending;
            }

            return seek;
        }

        if (phase >= 300)
        {
            memory.Write32(Arena + (Phase), phase - 300);
            StepResult commit = CommitStep();
            memory.Write32(Arena + (Phase), memory.Read32(Arena + (Phase)) + 300);
            if (commit == StepResult.Complete)
            {
                memory.Write32(Arena + (Finished), 0);
                if (memory.Read32(Arena + (AllocationMode)) != 2)
                    memory.Write32(Arena + (FileValue), memory.Read32(Arena + (FileValue)) + memory.Read32(Arena + (DataTake)));
                memory.Write32(Arena + (Phase), 0);
                return StepResult.Pending;
            }

            return commit;
        }

        if (phase >= 150)
            return AllocateWriteStep();
        switch (phase)
        {
            case 100:
                if (memory.Read8(record + 84) != 1)
                    return Reject(CpxError.WrongType);
                if ((memory.Read32(record + 72) & (write ? 4u : 8u)) != 0)
                    return Reject(CpxError.Protected);
                if (write && memory.Read32(Arena + (FileBytes)) == 0)
                    return Done();
                uint storage = memory.Read8(record + 85);
                ulong size = GetWide(record + 8);
                if (storage > 2)
                    return Fail(CpxError.Corrupt);
                if (storage == 0)
                {
                    if (((uint)(((ulong)(size)) >> 32)) != 0 || ((uint)((ulong)(size))) > 160 || memory.Read16(record + 88) != ((uint)((ulong)(size))) || !(((ulong)(GetWide(record + 16))) == 0) || !(((ulong)(GetWide(record + 24))) == 0))
                        return Fail(CpxError.Corrupt);
                    if (write)
                    {
                        ulong end = RW(FilePosition) + memory.Read32(Arena + (FileBytes));
                        if (((uint)(((ulong)(end)) >> 32)) == 0 && ((uint)((ulong)(end))) <= 160)
                        {
                            Copy(FileInput, memory.Read32(Arena + (FileBuffer)), memory.Read32(Arena + (FileBytes)));
                            memory.Write32(Arena + (OperationAt), (uint)CpxOperation.WriteInline);
                            memory.Write32(Arena + (Phase), BufferedWrites ? 0u : 100u);
                            return StepResult.Pending;
                        }

                        if (((uint)((ulong)(size))) != 0)
                        {
                            Copy(FileInput, record + 96, 160);
                            WW(DataLogical, default);
                            memory.Write32(Arena + (DataIntra), 0);
                            memory.Write32(Arena + (DataTake), 0);
                            memory.Write32(Arena + (AllocationMode), 2);
                            memory.Write32(Arena + (Phase), 150);
                            return StepResult.Pending;
                        }

                        Clear(record + 96, 160);
                        memory.Write8(record + 85, 1);
                        memory.Write16(record + 88, 0);
                        storage = 1;
                    }
                }

                if (memory.Read32(Arena + (FileBytes)) == 0)
                    return Done();
                if (write && memory.Read32(Arena + (WriteGapPrepared)) == 0 && storage != 0 && (RW(FilePosition)) > (size) && (((uint)((ulong)(size))) & (unit - 1)) != 0)
                {
                    WW(OldFileSize, size);
                    Copy(ResizeObject, record, 256);
                    return Call(CpxOperation.PrepareWriteGap, 109);
                }

                if ((RW(FilePosition)) >= (size) && !write)
                    return Done();
                if (!write)
                {
                    Scalar64.Subtract(size, RW(FilePosition), out var remaining);
                    if (((uint)(((ulong)(remaining)) >> 32)) == 0 && memory.Read32(Arena + (FileBytes)) > ((uint)((ulong)(remaining))))
                        memory.Write32(Arena + (FileBytes), ((uint)((ulong)(remaining))));
                }

                WW(SavedLeaf, RW(TreeAddress));
                memory.Write32(Arena + (SavedSlot), memory.Read32(Arena + (TreeSlot)));
                if (storage == 0)
                {
                    Copy(memory.Read32(Arena + (FileBuffer)), record + 96 + memory.Read32(Arena + (FilePosition + 4)), memory.Read32(Arena + (FileBytes)));
                    memory.Write32(Arena + (FileValue), memory.Read32(Arena + (FileBytes)));
                    return Done();
                }

                memory.Write32(Arena + (Phase), 101);
                return StepResult.Pending;
            case 101:
                if (memory.Read32(Arena + (FileValue)) == memory.Read32(Arena + (FileBytes)))
                {
                    if (write)
                        ArmFastAppend();
                    return Done();
                }

                ulong position = RW(FilePosition) + memory.Read32(Arena + (FileValue));
                WW(DataLogical, Scalar64.Right(position, (int)memory.Read32(Arena + (UnitShift))));
                memory.Write32(Arena + (DataIntra), ((uint)((ulong)(position))) & (unit - 1));
                uint take = unit - memory.Read32(Arena + (DataIntra));
                if (take > memory.Read32(Arena + (FileBytes)) - memory.Read32(Arena + (FileValue)))
                    take = memory.Read32(Arena + (FileBytes)) - memory.Read32(Arena + (FileValue));
                memory.Write32(Arena + (DataTake), take);
                memory.Write32(Arena + (SeekFound), 0);
                memory.Write32(Arena + (DataFlags), 0);
                WW(DataPhysical, default);
                memory.Write32(Arena + (WriteRunUnits), 1);
                memory.Write32(Arena + (WriteDirect), 0);
                if (memory.Read8(record + 85) == 2)
                {
                    if (memory.Read16(record + 90) != 0)
                        return Fail(CpxError.Corrupt);
                    // A fully pruned extent tree is a valid empty/sparse file
                    // in reference-created images; the first insertion recreates it.
                    if ((((ulong)(GetWide(record + 24))) == 0))
                    {
                        memory.Write32(Arena + (Phase), 103);
                        return StepResult.Pending;
                    }

                    StartSeek(CpxTreeKind.Extent, GetWide(record + 24), RW(ObjectKey), RW(DataLogical), default, CpxSeek.Floor);
                    memory.Write32(Arena + (Phase), 200);
                    return StepResult.Pending;
                }

                uint count = memory.Read16(record + 90);
                if (count > 6 || !(((ulong)(GetWide(record + 24))) == 0))
                    return Fail(CpxError.Corrupt);
                for (uint n = 0; n < count; n++)
                {
                    uint extent = record + 96 + n * 24;
                    if (n != 0 && (GetWide(extent - 24)) >= (GetWide(extent)))
                        return Fail(CpxError.Corrupt);
                    if ((GetWide(extent)) <= (RW(DataLogical)))
                    {
                        Copy(TreeRecord, extent, 24);
                        memory.Write32(Arena + (SeekFound), 1);
                    }
                }

                memory.Write32(Arena + (Phase), 103);
                return StepResult.Pending;
            case 103:
                if (write)
                {
                    Clear(WriteOriginal, 24);
                    if (memory.Read32(Arena + (SeekFound)) != 0)
                        Copy(WriteOriginal, TreeRecord, 24);
                }

                if (memory.Read32(Arena + (SeekFound)) != 0)
                {
                    ulong logical = GetWide(TreeRecord), physical = GetWide(TreeRecord + 8);
                    uint length = memory.Read32(TreeRecord + 16), flags = memory.Read32(TreeRecord + 20);
                    if (length == 0 || length > 65536 || flags > 1 || !Scalar64.Add(physical, ((ulong)(length - 1)), out var last) || !DataUnit(physical) || !DataUnit(last) || ((uint)(((ulong)(physical)) >> 32)) != ((uint)(((ulong)(last)) >> 32)) || (((uint)((ulong)(physical))) >> 16) != (((uint)((ulong)(last))) >> 16) || !Scalar64.Add(logical, ((ulong)(length)), out _))
                        return Fail(CpxError.Corrupt);
                    if (!Scalar64.Subtract(RW(DataLogical), logical, out var delta))
                        return Fail(CpxError.Corrupt);
                    if (((uint)(((ulong)(delta)) >> 32)) == 0 && ((uint)((ulong)(delta))) < length)
                    {
                        WW(DataPhysical, physical + ((uint)((ulong)(delta))));
                        memory.Write32(Arena + (DataFlags), flags == 0 ? 1u : 2u);
                    }
                }

                if (write && memory.Read32(Arena + (DataFlags)) == 2)
                {
                    memory.Write32(Arena + (AllocationMode), 3);
                    WW(NewPhysical, RW(DataPhysical));
                    memory.Write32(Arena + (Phase), 150);
                    return StepResult.Pending;
                }

                if (write && memory.Read32(Arena + (DataFlags)) == 0)
                {
                    memory.Write32(Arena + (AllocationMode), 1);
                    memory.Write32(Arena + (Phase), 150);
                    return StepResult.Pending;
                }

                if (memory.Read32(Arena + (DataFlags)) == 1)
                {
                    // Full mapped units transfer directly to/from the pinned
                    // caller buffer. Partial units retain read/modify/write.
                    uint available = (memory.Read32(Arena + (FileBytes)) - memory.Read32(Arena + (FileValue))) >> (int)memory.Read32(Arena + (UnitShift));
                    if (memory.Read32(Arena + (DataIntra)) == 0 && available != 0)
                    {
                        Scalar64.Subtract(RW(DataLogical), GetWide(TreeRecord), out var delta);
                        uint run = memory.Read32(TreeRecord + 16) - ((uint)((ulong)(delta)));
                        if (run > available)
                            run = available;
                        uint bound = 65536u >> (int)memory.Read32(Arena + (UnitShift));
                        if (run > bound)
                            run = bound;
                        memory.Write32(Arena + (WriteRunUnits), run);
                        memory.Write32(Arena + (DataTake), run * unit);
                        memory.Write32(Arena + (WriteDirect), 1);
                        if (!write)
                            return Issue(IoKind.Read, RW(DataPhysical), memory.Read32(Arena + (FileBuffer)) + memory.Read32(Arena + (FileValue)), run * unit, 107);
                        ulong end = RW(FilePosition) + memory.Read32(Arena + (FileValue)) + memory.Read32(Arena + (DataTake));
                        if ((end) > (GetWide(record + 8)))
                        {
                            PutWide(record + 8, end);
                            WW(NewPhysical, RW(DataPhysical));
                            memory.Write32(Arena + (AllocationMode), 0);
                            memory.Write32(Arena + (Phase), 160);
                            return StepResult.Pending;
                        }

                        return Issue(IoKind.Write, RW(DataPhysical), memory.Read32(Arena + (FileBuffer)) + memory.Read32(Arena + (FileValue)), run * unit, 105);
                    }

                    return Issue(IoKind.Read, RW(DataPhysical), Page(4), unit, 104);
                }

                Clear(Page(4), unit);
                memory.Write32(Arena + (Phase), 104);
                return StepResult.Pending;
            case 104:
                uint source = memory.Read32(Arena + (FileBuffer)) + memory.Read32(Arena + (FileValue)), data = Page(4) + memory.Read32(Arena + (DataIntra));
                if (write)
                {
                    Copy(data, source, memory.Read32(Arena + (DataTake)));
                    ulong end = RW(FilePosition) + memory.Read32(Arena + (FileValue)) + memory.Read32(Arena + (DataTake));
                    if ((end) > (GetWide(record + 8)))
                    {
                        PutWide(record + 8, end);
                        WW(NewPhysical, RW(DataPhysical));
                        memory.Write32(Arena + (AllocationMode), 0);
                        memory.Write32(Arena + (Phase), 160);
                        return StepResult.Pending;
                    }

                    return Issue(IoKind.Write, RW(DataPhysical), Page(4), unit, 105);
                }

                Copy(source, data, memory.Read32(Arena + (DataTake)));
                memory.Write32(Arena + (FileValue), memory.Read32(Arena + (FileValue)) + memory.Read32(Arena + (DataTake)));
                memory.Write32(Arena + (Phase), 101);
                return StepResult.Pending;
            case 105:
                return Issue(IoKind.Flush, default, 0, 0, 106);
            case 106:
                memory.Write32(Arena + (FileValue), memory.Read32(Arena + (FileValue)) + memory.Read32(Arena + (DataTake)));
                memory.Write32(Arena + (Phase), 101);
                return StepResult.Pending;
            case 107:
                memory.Write32(Arena + (FileValue), memory.Read32(Arena + (FileValue)) + memory.Read32(Arena + (DataTake)));
                memory.Write32(Arena + (Phase), 101);
                return StepResult.Pending;
            case 109:
                memory.Write32(Arena + (WriteGapPrepared), 1);
                memory.Write32(Arena + (Phase), 0);
                return StepResult.Pending;
            default:
                return Fail(CpxError.Corrupt);
        }
    }
}
