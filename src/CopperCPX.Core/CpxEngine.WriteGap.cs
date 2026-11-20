namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    private const uint WriteGapPrepared = 15520;
    // Before publishing a sparse extension, erase the old EOF unit's hidden
    // suffix. The size remains unchanged until the write's first chunk retires.
    // A crash here cannot expose the suffix and retains the original prefix.
    private StepResult WriteGapStep()
    {
        uint cpxArenaBase = Arena;
        uint obj = (cpxArenaBase + 14300), extent = (cpxArenaBase + 14556), unit = 1u << (int)memory.Read32(cpxArenaBase + (UnitShift));
        ulong old = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(OldFileSize)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(OldFileSize)) + 4)))))), logical = Scalar64.Right(old, (int)memory.Read32(cpxArenaBase + (UnitShift)));
        switch (memory.Read32(cpxArenaBase + (Phase)))
        {
            case 0:
                Clear(extent, 24);
                if (memory.Read8(obj + 85) == 2)
                {
                    StartSeek(CpxTreeKind.Extent, ((((ulong)(memory.Read32(((uint)(obj + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(obj + 24)) + 4))))), ((((ulong)(memory.Read32(((uint)(obj))))) << 32 | ((ulong)(memory.Read32(((uint)(obj)) + 4))))), logical, default, CpxSeek.Floor);
                    return Call(CpxOperation.ReadTree, 1);
                }

                uint count = memory.Read16(obj + 90);
                if (memory.Read8(obj + 85) != 1 || count > 6)
                    return Fail(CpxError.Corrupt);
                for (uint n = 0; n < count; n++)
                    if ((((((ulong)(memory.Read32(((uint)(obj + 96 + n * 24))))) << 32 | ((ulong)(memory.Read32(((uint)(obj + 96 + n * 24)) + 4)))))) <= (logical))
                        Copy(extent, obj + 96 + n * 24, 24);
                memory.Write32(cpxArenaBase + (Phase), 2);
                return StepResult.Pending;
            case 1:
                if ((memory.Read32(cpxArenaBase + (SeekFound)) != 0))
                    Copy(extent, ((cpxArenaBase + CpxArena.MountBytes) + 512), 24);
                memory.Write32(cpxArenaBase + (Phase), 2);
                return StepResult.Pending;
            case 2:
                uint length = memory.Read32(extent + 16), flags = memory.Read32(extent + 20);
                if (length == 0)
                    return Done();
                if (length > 65536 || flags > 1 || !Scalar64.Add(((((ulong)(memory.Read32(((uint)(extent))))) << 32 | ((ulong)(memory.Read32(((uint)(extent)) + 4))))), ((ulong)(length)), out var end) || !Scalar64.Subtract(logical, ((((ulong)(memory.Read32(((uint)(extent))))) << 32 | ((ulong)(memory.Read32(((uint)(extent)) + 4))))), out var delta))
                    return Fail(CpxError.Corrupt);
                if ((logical) >= (end) || flags == 1)
                    return Done();
                if (!Scalar64.Add(((((ulong)(memory.Read32(((uint)(extent + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(extent + 8)) + 4))))), delta, out var physical) || !DataUnit(physical))
                    return Fail(CpxError.Corrupt);
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(ResizePhysical);
                ulong cpxWideValue = physical;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                return Issue(IoKind.Read, physical, (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(4)) * 4))), unit, 3);
            case 3:
                uint tail = ((uint)((ulong)(old))) & (unit - 1);
                Clear((memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(4)) * 4))) + tail, unit - tail);
                return Issue(IoKind.Write, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizePhysical)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizePhysical)) + 4)))))), (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(4)) * 4))), unit, 4);
            case 4:
                return Issue(IoKind.Flush, default, 0, 0, 5);
            case 5:
                return Done();
            default:
                return Fail(CpxError.Corrupt);
        }
    }
}