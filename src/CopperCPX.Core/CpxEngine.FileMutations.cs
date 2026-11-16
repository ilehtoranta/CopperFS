namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    // The input is copied before returning to the dispatcher. ObjectRecord occupies
    // the preceding 256 bytes. Neither buffer aliases any of the six page buffers.
    private uint FileInput => (Arena + CpxArena.MountBytes) + 256;
    public uint CommittedBytes => memory.Read32(Arena + (FileValue));

    public CpxError BeginInlineWrite(Wide id, Wide position, uint source, uint bytes)
    {
        uint cpxArenaBase = Arena;
        if (bytes > 160 || !Scalar64.Add(position, ((ulong)(bytes)), out var end) || ((uint)(((ulong)(end)) >> 32)) != 0 || ((uint)((ulong)(end))) > 160 || source > uint.MaxValue - bytes)
            return CpxError.InvalidArgument;
        CpxError error = StartOperation(CpxOperation.WriteInline, id);
        if (error != CpxError.None)
            return error;
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(FilePosition);
            ulong cpxWideValue = position;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(cpxArenaBase + (FileBytes), bytes);
        for (uint n = 0; n < bytes; n++)
            memory.Write8(((cpxArenaBase + CpxArena.MountBytes) + 256) + n, memory.Read8(source + n));
        return CpxError.None;
    }

    public CpxError BeginInlineResize(Wide id, uint size)
    {
        if (size > 160)
            return CpxError.InvalidArgument;
        CpxError error = StartOperation(CpxOperation.ResizeInline, id);
        if (error == CpxError.None)
            memory.Write32(Arena + (FileBytes), size);
        return error;
    }

    public CpxError BeginProtection(Wide id, uint protection)
    {
        CpxError error = StartOperation(CpxOperation.SetProtection, id);
        if (error == CpxError.None)
            memory.Write32(((Arena + CpxArena.MountBytes) + 256), protection);
        return error;
    }

    public CpxError BeginDate(Wide id, uint days, uint minutes, uint ticks)
    {
        uint cpxArenaBase = Arena;
        if (minutes >= 1440 || ticks >= 3000)
            return CpxError.InvalidArgument;
        CpxError error = StartOperation(CpxOperation.SetDate, id);
        if (error == CpxError.None)
        {
            memory.Write32(((cpxArenaBase + CpxArena.MountBytes) + 256), days);
            memory.Write16(((cpxArenaBase + CpxArena.MountBytes) + 256) + 4, minutes);
            memory.Write16(((cpxArenaBase + CpxArena.MountBytes) + 256) + 6, ticks);
        }

        return error;
    }

    private StepResult FileMutationStep()
    {
        uint cpxArenaBase = Arena;
        uint phase = memory.Read32(cpxArenaBase + (Phase)), p = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(0)) * 4))), record = (cpxArenaBase + CpxArena.MountBytes);
        CpxOperation op = (CpxOperation)memory.Read32(cpxArenaBase + (OperationAt));
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

        if (phase >= 200)
        {
            memory.Write32(cpxArenaBase + (Phase), phase - 200);
            StepResult commit = CommitStep();
            memory.Write32(cpxArenaBase + (Phase), memory.Read32(cpxArenaBase + (Phase)) + 200);
            // Advancing a file handle is permitted only after the final retirement barrier.
            if (commit == StepResult.Complete)
                memory.Write32(cpxArenaBase + (FileValue), op == CpxOperation.WriteInline ? memory.Read32(cpxArenaBase + (FileBytes)) : 0);
            return commit;
        }

        switch (phase)
        {
            case 100:
                if (op is CpxOperation.WriteInline or CpxOperation.ResizeInline)
                {
                    if (memory.Read8(record + 84) != 1)
                        return Reject(CpxError.WrongType);
                    if ((memory.Read32(record + 72) & 4) != 0)
                        return Reject(CpxError.Protected);
                    if (memory.Read8(record + 85) != 0)
                        return Reject(CpxError.Unsupported);
                    ulong size = ((((ulong)(memory.Read32(((uint)(record + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 8)) + 4)))));
                    if (((uint)(((ulong)(size)) >> 32)) != 0 || ((uint)((ulong)(size))) > 160 || memory.Read16(record + 88) != ((uint)((ulong)(size))) || !(((ulong)(((((ulong)(memory.Read32(((uint)(record + 16))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 16)) + 4))))))) == 0) || !(((ulong)(((((ulong)(memory.Read32(((uint)(record + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 24)) + 4))))))) == 0))
                        return Fail(CpxError.Corrupt);
                    if (op == CpxOperation.WriteInline && memory.Read32(cpxArenaBase + (FileBytes)) == 0)
                        return Done();
                    uint length = op == CpxOperation.ResizeInline ? memory.Read32(cpxArenaBase + (FileBytes)) : memory.Read32(cpxArenaBase + (FilePosition + 4)) + memory.Read32(cpxArenaBase + (FileBytes));
                    if (op == CpxOperation.WriteInline && length < ((uint)((ulong)(size))))
                        length = ((uint)((ulong)(size)));
                    // Clear both a newly exposed hole and a truncated tail. A later grow
                    // must never reveal bytes from the previous incarnation of the file.
                    if (length > ((uint)((ulong)(size))))
                        Clear(record + 96 + ((uint)((ulong)(size))), length - ((uint)((ulong)(size))));
                    else if (length < ((uint)((ulong)(size))))
                        Clear(record + 96 + length, 160 - length);
                    if (op == CpxOperation.WriteInline)
                        Copy(record + 96 + memory.Read32(cpxArenaBase + (FilePosition + 4)), ((cpxArenaBase + CpxArena.MountBytes) + 256), memory.Read32(cpxArenaBase + (FileBytes)));
                    {
                        uint cpxWideAddress = (uint)(record + 8);
                        ulong cpxWideValue = ((ulong)(length));
                        memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                        memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                    }

                    memory.Write16(record + 88, length);
                }
                else if (op == CpxOperation.SetProtection)
                    memory.Write32(record + 72, memory.Read32(((cpxArenaBase + CpxArena.MountBytes) + 256)));
                else
                    Copy(record + 56, ((cpxArenaBase + CpxArena.MountBytes) + 256), 8);
                // Admission precedes invalidation and every durable write.
                if ((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1))
                {
                    memory.Write32(cpxArenaBase + (Phase), 103);
                    return StepResult.Pending;
                }

                if (!Scalar64.Add(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)) + 4)))))), ((ulong)(1)), out _))
                    return Reject(CpxError.ResourceExhausted);
                memory.Write32(cpxArenaBase + (StagedCount), 0);
                memory.Write32(cpxArenaBase + (StagedLimit), 6);
                memory.Write32(cpxArenaBase + (Active), 1);
                Clear((memory.Read32(cpxArenaBase + (GeometryIndex))), 2048);
                Clear((memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(5)) * 4))), (memory.Read32(cpxArenaBase + (PageBytes))));
                return Issue(IoKind.Write, Commit(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)) + 4)))))) + 1), (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(5)) * 4))), (memory.Read32(cpxArenaBase + (PageBytes))), 101);
            case 101:
                return Issue(IoKind.Flush, default, 0, 0, 102);
            case 103:
                return BeginTx(6, 102);
            case 102:
                uint dest = p + 64 + (memory.Read32(cpxArenaBase + (TreeSlot)) - 1) * 256;
                Copy(dest, record, 256);
                return StagePage(p, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(TreeAddress)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(TreeAddress)) + 4)))))), 200);
            default:
                return Fail(CpxError.Corrupt);
        }
    }
}