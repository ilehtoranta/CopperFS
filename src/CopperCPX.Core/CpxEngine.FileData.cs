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
        uint cpxArenaBase = Arena;
        if ((buffer == 0 && bytes != 0) || buffer > uint.MaxValue - bytes || !Scalar64.Add(position, ((ulong)(bytes)), out _))
            return CpxError.InvalidArgument;
        CpxError error = StartOperation(op, id);
        if (error != CpxError.None)
            return error;
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(FilePosition);
            ulong cpxWideValue = position;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(cpxArenaBase + (FileBuffer), buffer);
        memory.Write32(cpxArenaBase + (FileBytes), bytes);
        memory.Write32(cpxArenaBase + (WriteGapPrepared), 0);
        memory.Write32(cpxArenaBase + (WriteRunUnits), 1);
        memory.Write32(cpxArenaBase + (WriteDirect), 0);
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
        uint cpxArenaBase = Arena;
        uint phase = memory.Read32(cpxArenaBase + (Phase));
        if (phase == 500)
            return Issue(IoKind.Write, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NewPhysical)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NewPhysical)) + 4)))))), memory.Read32(cpxArenaBase + (FileBuffer)), memory.Read32(cpxArenaBase + (FileBytes)), 501);
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
        uint record = (cpxArenaBase + CpxArena.MountBytes), unit = 1u << (int)memory.Read32(cpxArenaBase + (UnitShift));
        bool write = (CpxOperation)memory.Read32(cpxArenaBase + (OperationAt)) == CpxOperation.WriteMapped;
        if (phase < 100)
        {
            StepResult lookup = ObjectStep();
            if (lookup != StepResult.Complete)
                return lookup;
            memory.Write32(cpxArenaBase + (Finished), 0);
            if (memory.Read32(cpxArenaBase + (SourcePage)) == 0)
                return Reject(CpxError.NotFound);
            memory.Write32(cpxArenaBase + (Phase), 100);
            return StepResult.Pending;
        }

        if (phase >= 200 && phase < 300)
        {
            memory.Write32(cpxArenaBase + (Phase), phase - 200);
            StepResult seek = SeekStep();
            memory.Write32(cpxArenaBase + (Phase), memory.Read32(cpxArenaBase + (Phase)) + 200);
            if (seek == StepResult.Complete)
            {
                memory.Write32(cpxArenaBase + (Finished), 0);
                memory.Write32(cpxArenaBase + (Phase), 103);
                return StepResult.Pending;
            }

            return seek;
        }

        if (phase >= 300)
        {
            memory.Write32(cpxArenaBase + (Phase), phase - 300);
            StepResult commit = CommitStep();
            memory.Write32(cpxArenaBase + (Phase), memory.Read32(cpxArenaBase + (Phase)) + 300);
            if (commit == StepResult.Complete)
            {
                memory.Write32(cpxArenaBase + (Finished), 0);
                if (memory.Read32(cpxArenaBase + (AllocationMode)) != 2)
                    memory.Write32(cpxArenaBase + (FileValue), memory.Read32(cpxArenaBase + (FileValue)) + memory.Read32(cpxArenaBase + (DataTake)));
                memory.Write32(cpxArenaBase + (Phase), 0);
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
                if (write && memory.Read32(cpxArenaBase + (FileBytes)) == 0)
                    return Done();
                uint storage = memory.Read8(record + 85);
                ulong size = ((((ulong)(memory.Read32(((uint)(record + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 8)) + 4)))));
                if (storage > 2)
                    return Fail(CpxError.Corrupt);
                if (storage == 0)
                {
                    if (((uint)(((ulong)(size)) >> 32)) != 0 || ((uint)((ulong)(size))) > 160 || memory.Read16(record + 88) != ((uint)((ulong)(size))) || !(((ulong)(((((ulong)(memory.Read32(((uint)(record + 16))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 16)) + 4))))))) == 0) || !(((ulong)(((((ulong)(memory.Read32(((uint)(record + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 24)) + 4))))))) == 0))
                        return Fail(CpxError.Corrupt);
                    if (write)
                    {
                        ulong end = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(FilePosition)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(FilePosition)) + 4)))))) + memory.Read32(cpxArenaBase + (FileBytes));
                        if (((uint)(((ulong)(end)) >> 32)) == 0 && ((uint)((ulong)(end))) <= 160)
                        {
                            Copy(((cpxArenaBase + CpxArena.MountBytes) + 256), memory.Read32(cpxArenaBase + (FileBuffer)), memory.Read32(cpxArenaBase + (FileBytes)));
                            memory.Write32(cpxArenaBase + (OperationAt), (uint)CpxOperation.WriteInline);
                            memory.Write32(cpxArenaBase + (Phase), (memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) ? 0u : 100u);
                            return StepResult.Pending;
                        }

                        if (((uint)((ulong)(size))) != 0)
                        {
                            Copy(((cpxArenaBase + CpxArena.MountBytes) + 256), record + 96, 160);
                            {
                                uint cpxWideAddress = cpxArenaBase + (uint)(DataLogical);
                                ulong cpxWideValue = default;
                                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                            }

                            memory.Write32(cpxArenaBase + (DataIntra), 0);
                            memory.Write32(cpxArenaBase + (DataTake), 0);
                            memory.Write32(cpxArenaBase + (AllocationMode), 2);
                            memory.Write32(cpxArenaBase + (Phase), 150);
                            return StepResult.Pending;
                        }

                        Clear(record + 96, 160);
                        memory.Write8(record + 85, 1);
                        memory.Write16(record + 88, 0);
                        storage = 1;
                    }
                }

                if (memory.Read32(cpxArenaBase + (FileBytes)) == 0)
                    return Done();
                if (write && memory.Read32(cpxArenaBase + (WriteGapPrepared)) == 0 && storage != 0 && (((((ulong)(memory.Read32(cpxArenaBase + (((uint)(FilePosition)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(FilePosition)) + 4))))))) > (size) && (((uint)((ulong)(size))) & (unit - 1)) != 0)
                {
                    {
                        uint cpxWideAddress = cpxArenaBase + (uint)(OldFileSize);
                        ulong cpxWideValue = size;
                        memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                        memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                    }

                    Copy((cpxArenaBase + 14300), record, 256);
                    return Call(CpxOperation.PrepareWriteGap, 109);
                }

                if ((((((ulong)(memory.Read32(cpxArenaBase + (((uint)(FilePosition)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(FilePosition)) + 4))))))) >= (size) && !write)
                    return Done();
                if (!write)
                {
                    Scalar64.Subtract(size, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(FilePosition)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(FilePosition)) + 4)))))), out var remaining);
                    if (((uint)(((ulong)(remaining)) >> 32)) == 0 && memory.Read32(cpxArenaBase + (FileBytes)) > ((uint)((ulong)(remaining))))
                        memory.Write32(cpxArenaBase + (FileBytes), ((uint)((ulong)(remaining))));
                }

            {
                uint cpxWideAddress = cpxArenaBase + (uint)(SavedLeaf);
                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(TreeAddress)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(TreeAddress)) + 4))))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write32(cpxArenaBase + (SavedSlot), memory.Read32(cpxArenaBase + (TreeSlot)));
                if (storage == 0)
                {
                    Copy(memory.Read32(cpxArenaBase + (FileBuffer)), record + 96 + memory.Read32(cpxArenaBase + (FilePosition + 4)), memory.Read32(cpxArenaBase + (FileBytes)));
                    memory.Write32(cpxArenaBase + (FileValue), memory.Read32(cpxArenaBase + (FileBytes)));
                    return Done();
                }

                memory.Write32(cpxArenaBase + (Phase), 101);
                return StepResult.Pending;
            case 101:
                if (memory.Read32(cpxArenaBase + (FileValue)) == memory.Read32(cpxArenaBase + (FileBytes)))
                {
                    if (write)
                        ArmFastAppend();
                    return Done();
                }

                ulong position = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(FilePosition)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(FilePosition)) + 4)))))) + memory.Read32(cpxArenaBase + (FileValue));
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(DataLogical);
                ulong cpxWideValue = Scalar64.Right(position, (int)memory.Read32(cpxArenaBase + (UnitShift)));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write32(cpxArenaBase + (DataIntra), ((uint)((ulong)(position))) & (unit - 1));
                uint take = unit - memory.Read32(cpxArenaBase + (DataIntra));
                if (take > memory.Read32(cpxArenaBase + (FileBytes)) - memory.Read32(cpxArenaBase + (FileValue)))
                    take = memory.Read32(cpxArenaBase + (FileBytes)) - memory.Read32(cpxArenaBase + (FileValue));
                memory.Write32(cpxArenaBase + (DataTake), take);
                memory.Write32(cpxArenaBase + (SeekFound), 0);
                memory.Write32(cpxArenaBase + (DataFlags), 0);
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(DataPhysical);
                ulong cpxWideValue = default;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write32(cpxArenaBase + (WriteRunUnits), 1);
                memory.Write32(cpxArenaBase + (WriteDirect), 0);
                if (memory.Read8(record + 85) == 2)
                {
                    if (memory.Read16(record + 90) != 0)
                        return Fail(CpxError.Corrupt);
                    // A fully pruned extent tree is a valid empty/sparse file
                    // in reference-created images; the first insertion recreates it.
                    if ((((ulong)(((((ulong)(memory.Read32(((uint)(record + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 24)) + 4))))))) == 0))
                    {
                        memory.Write32(cpxArenaBase + (Phase), 103);
                        return StepResult.Pending;
                    }

                    StartSeek(CpxTreeKind.Extent, ((((ulong)(memory.Read32(((uint)(record + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 24)) + 4))))), ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ObjectKey)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ObjectKey)) + 4)))))), ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(DataLogical)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(DataLogical)) + 4)))))), default, CpxSeek.Floor);
                    memory.Write32(cpxArenaBase + (Phase), 200);
                    return StepResult.Pending;
                }

                uint count = memory.Read16(record + 90);
                if (count > 6 || !(((ulong)(((((ulong)(memory.Read32(((uint)(record + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 24)) + 4))))))) == 0))
                    return Fail(CpxError.Corrupt);
                for (uint n = 0; n < count; n++)
                {
                    uint extent = record + 96 + n * 24;
                    if (n != 0 && (((((ulong)(memory.Read32(((uint)(extent - 24))))) << 32 | ((ulong)(memory.Read32(((uint)(extent - 24)) + 4)))))) >= (((((ulong)(memory.Read32(((uint)(extent))))) << 32 | ((ulong)(memory.Read32(((uint)(extent)) + 4)))))))
                        return Fail(CpxError.Corrupt);
                    if ((((((ulong)(memory.Read32(((uint)(extent))))) << 32 | ((ulong)(memory.Read32(((uint)(extent)) + 4)))))) <= (((((ulong)(memory.Read32(cpxArenaBase + (((uint)(DataLogical)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(DataLogical)) + 4))))))))
                    {
                        Copy(((cpxArenaBase + CpxArena.MountBytes) + 512), extent, 24);
                        memory.Write32(cpxArenaBase + (SeekFound), 1);
                    }
                }

                memory.Write32(cpxArenaBase + (Phase), 103);
                return StepResult.Pending;
            case 103:
                if (write)
                {
                    Clear((cpxArenaBase + 14700), 24);
                    if (memory.Read32(cpxArenaBase + (SeekFound)) != 0)
                        Copy((cpxArenaBase + 14700), ((cpxArenaBase + CpxArena.MountBytes) + 512), 24);
                }

                if (memory.Read32(cpxArenaBase + (SeekFound)) != 0)
                {
                    ulong logical = ((((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 512)))))) << 32 | ((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 512))) + 4))))), physical = ((((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 512) + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 512) + 8)) + 4)))));
                    uint length = memory.Read32(((cpxArenaBase + CpxArena.MountBytes) + 512) + 16), flags = memory.Read32(((cpxArenaBase + CpxArena.MountBytes) + 512) + 20);
                    if (length == 0 || length > 65536 || flags > 1 || !Scalar64.Add(physical, ((ulong)(length - 1)), out var last) || !DataUnit(physical) || !DataUnit(last) || ((uint)(((ulong)(physical)) >> 32)) != ((uint)(((ulong)(last)) >> 32)) || (((uint)((ulong)(physical))) >> 16) != (((uint)((ulong)(last))) >> 16) || !Scalar64.Add(logical, ((ulong)(length)), out _))
                        return Fail(CpxError.Corrupt);
                    if (!Scalar64.Subtract(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(DataLogical)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(DataLogical)) + 4)))))), logical, out var delta))
                        return Fail(CpxError.Corrupt);
                    if (((uint)(((ulong)(delta)) >> 32)) == 0 && ((uint)((ulong)(delta))) < length)
                    {
                        {
                            uint cpxWideAddress = cpxArenaBase + (uint)(DataPhysical);
                            ulong cpxWideValue = physical + ((uint)((ulong)(delta)));
                            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                        }

                        memory.Write32(cpxArenaBase + (DataFlags), flags == 0 ? 1u : 2u);
                    }
                }

                if (write && memory.Read32(cpxArenaBase + (DataFlags)) == 2)
                {
                    memory.Write32(cpxArenaBase + (AllocationMode), 3);
                    {
                        uint cpxWideAddress = cpxArenaBase + (uint)(NewPhysical);
                        ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(DataPhysical)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(DataPhysical)) + 4))))));
                        memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                        memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                    }

                    memory.Write32(cpxArenaBase + (Phase), 150);
                    return StepResult.Pending;
                }

                if (write && memory.Read32(cpxArenaBase + (DataFlags)) == 0)
                {
                    memory.Write32(cpxArenaBase + (AllocationMode), 1);
                    memory.Write32(cpxArenaBase + (Phase), 150);
                    return StepResult.Pending;
                }

                if (memory.Read32(cpxArenaBase + (DataFlags)) == 1)
                {
                    // Full mapped units transfer directly to/from the pinned
                    // caller buffer. Partial units retain read/modify/write.
                    uint available = (memory.Read32(cpxArenaBase + (FileBytes)) - memory.Read32(cpxArenaBase + (FileValue))) >> (int)memory.Read32(cpxArenaBase + (UnitShift));
                    if (memory.Read32(cpxArenaBase + (DataIntra)) == 0 && available != 0)
                    {
                        Scalar64.Subtract(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(DataLogical)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(DataLogical)) + 4)))))), ((((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 512)))))) << 32 | ((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 512))) + 4))))), out var delta);
                        uint run = memory.Read32(((cpxArenaBase + CpxArena.MountBytes) + 512) + 16) - ((uint)((ulong)(delta)));
                        if (run > available)
                            run = available;
                        uint bound = 65536u >> (int)memory.Read32(cpxArenaBase + (UnitShift));
                        if (run > bound)
                            run = bound;
                        memory.Write32(cpxArenaBase + (WriteRunUnits), run);
                        memory.Write32(cpxArenaBase + (DataTake), run * unit);
                        memory.Write32(cpxArenaBase + (WriteDirect), 1);
                        if (!write)
                            return Issue(IoKind.Read, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(DataPhysical)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(DataPhysical)) + 4)))))), memory.Read32(cpxArenaBase + (FileBuffer)) + memory.Read32(cpxArenaBase + (FileValue)), run * unit, 107);
                        ulong end = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(FilePosition)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(FilePosition)) + 4)))))) + memory.Read32(cpxArenaBase + (FileValue)) + memory.Read32(cpxArenaBase + (DataTake));
                        if ((end) > (((((ulong)(memory.Read32(((uint)(record + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 8)) + 4)))))))
                        {
                            {
                                uint cpxWideAddress = (uint)(record + 8);
                                ulong cpxWideValue = end;
                                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                            }

                            {
                                uint cpxWideAddress = cpxArenaBase + (uint)(NewPhysical);
                                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(DataPhysical)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(DataPhysical)) + 4))))));
                                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                            }

                            memory.Write32(cpxArenaBase + (AllocationMode), 0);
                            memory.Write32(cpxArenaBase + (Phase), 160);
                            return StepResult.Pending;
                        }

                        return Issue(IoKind.Write, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(DataPhysical)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(DataPhysical)) + 4)))))), memory.Read32(cpxArenaBase + (FileBuffer)) + memory.Read32(cpxArenaBase + (FileValue)), run * unit, 105);
                    }

                    return Issue(IoKind.Read, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(DataPhysical)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(DataPhysical)) + 4)))))), (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(4)) * 4))), unit, 104);
                }

                Clear((memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(4)) * 4))), unit);
                memory.Write32(cpxArenaBase + (Phase), 104);
                return StepResult.Pending;
            case 104:
                uint source = memory.Read32(cpxArenaBase + (FileBuffer)) + memory.Read32(cpxArenaBase + (FileValue)), data = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(4)) * 4))) + memory.Read32(cpxArenaBase + (DataIntra));
                if (write)
                {
                    Copy(data, source, memory.Read32(cpxArenaBase + (DataTake)));
                    ulong end = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(FilePosition)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(FilePosition)) + 4)))))) + memory.Read32(cpxArenaBase + (FileValue)) + memory.Read32(cpxArenaBase + (DataTake));
                    if ((end) > (((((ulong)(memory.Read32(((uint)(record + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 8)) + 4)))))))
                    {
                        {
                            uint cpxWideAddress = (uint)(record + 8);
                            ulong cpxWideValue = end;
                            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                        }

                        {
                            uint cpxWideAddress = cpxArenaBase + (uint)(NewPhysical);
                            ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(DataPhysical)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(DataPhysical)) + 4))))));
                            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                        }

                        memory.Write32(cpxArenaBase + (AllocationMode), 0);
                        memory.Write32(cpxArenaBase + (Phase), 160);
                        return StepResult.Pending;
                    }

                    return Issue(IoKind.Write, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(DataPhysical)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(DataPhysical)) + 4)))))), (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(4)) * 4))), unit, 105);
                }

                Copy(source, data, memory.Read32(cpxArenaBase + (DataTake)));
                memory.Write32(cpxArenaBase + (FileValue), memory.Read32(cpxArenaBase + (FileValue)) + memory.Read32(cpxArenaBase + (DataTake)));
                memory.Write32(cpxArenaBase + (Phase), 101);
                return StepResult.Pending;
            case 105:
                return Issue(IoKind.Flush, default, 0, 0, 106);
            case 106:
                memory.Write32(cpxArenaBase + (FileValue), memory.Read32(cpxArenaBase + (FileValue)) + memory.Read32(cpxArenaBase + (DataTake)));
                memory.Write32(cpxArenaBase + (Phase), 101);
                return StepResult.Pending;
            case 107:
                memory.Write32(cpxArenaBase + (FileValue), memory.Read32(cpxArenaBase + (FileValue)) + memory.Read32(cpxArenaBase + (DataTake)));
                memory.Write32(cpxArenaBase + (Phase), 101);
                return StepResult.Pending;
            case 109:
                memory.Write32(cpxArenaBase + (WriteGapPrepared), 1);
                memory.Write32(cpxArenaBase + (Phase), 0);
                return StepResult.Pending;
            default:
                return Fail(CpxError.Corrupt);
        }
    }
}