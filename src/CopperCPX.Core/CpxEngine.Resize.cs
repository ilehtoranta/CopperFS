namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    private const uint ResizeId = 14200, ResizeLength = 14208, ResizeOld = 14216, ResizePhysical = 14224, ResizeScan = 14232;
    private uint ResizeObject => Arena + 14300;
    private uint ResizeExtent => Arena + 14556;

    public CpxError BeginResize(Wide id, Wide size)
    {
        CpxError error = StartOperation(CpxOperation.Resize);
        if (error != CpxError.None)
            return error;
        WW(ResizeId, id);
        WW(ResizeLength, size);
        return error;
    }

    private StepResult ResizeStep()
    {
        uint obj = ResizeObject, p = Page(3), unit = 1u << (int)memory.Read32(Arena + (UnitShift));
        ulong length = RW(ResizeLength), old = RW(ResizeOld);
        switch (memory.Read32(Arena + (Phase)))
        {
            case 0:
                uint cached = BufferedWrites ? CachedObject(RW(ResizeId)) : 0;
                if (cached != 0 && memory.Read8(cached + 84) == 1 && (memory.Read32(cached + 72) & 4) == 0 && (((ulong)(length)) == ((ulong)(GetWide(cached + 8)))))
                    return Done();
                return Call(CpxOperation.Reclaim, 1);
            case 1:
                return LoadObject(RW(ResizeId), 2);
            case 2:
                if (memory.Read32(Arena + (SourcePage)) == 0)
                    return Reject(CpxError.NotFound);
                Copy(obj, ObjectRecord, 256);
                if (memory.Read8(obj + 84) != 1)
                    return Reject(CpxError.WrongType);
                if ((memory.Read32(obj + 72) & 4) != 0)
                    return Reject(CpxError.Protected);
                WW(ResizeOld, GetWide(obj + 8));
                if ((((ulong)(length)) == ((ulong)(GetWide(obj + 8)))))
                    return Done();
                if (memory.Read8(obj + 85) == 0)
                {
                    if (((uint)(((ulong)(GetWide(obj + 8))) >> 32)) != 0 || ((uint)((ulong)(GetWide(obj + 8)))) > 160 || memory.Read16(obj + 88) != ((uint)((ulong)(GetWide(obj + 8)))))
                        return Fail(CpxError.Corrupt);
                    if (((uint)(((ulong)(length)) >> 32)) == 0 && ((uint)((ulong)(length))) <= 160)
                    {
                        uint from = ((uint)((ulong)(length))) < ((uint)((ulong)(GetWide(obj + 8)))) ? ((uint)((ulong)(length))) : ((uint)((ulong)(GetWide(obj + 8))));
                        Clear(obj + 96 + from, 160 - from);
                        PutWide(obj + 8, length);
                        memory.Write16(obj + 88, ((uint)((ulong)(length))));
                        return BeginTx(6, 10);
                    }

                    return EnsureReserve(30, 40);
                }

                if (memory.Read8(obj + 85) > 2)
                    return Fail(CpxError.Corrupt);
                if ((length) < (GetWide(obj + 8)))
                    return BeginTx(6, 20);
                if ((((uint)((ulong)(GetWide(obj + 8)))) & (unit - 1)) == 0)
                    return BeginTx(6, 9);
                ulong logical = Scalar64.Right(GetWide(obj + 8), (int)memory.Read32(Arena + (UnitShift)));
                if (memory.Read8(obj + 85) == 1)
                {
                    uint count = memory.Read16(obj + 90);
                    if (count > 6)
                        return Fail(CpxError.Corrupt);
                    Clear(ResizeExtent, 24);
                    for (uint n = 0; n < count; n++)
                        if ((GetWide(obj + 96 + n * 24)) <= (logical))
                            Copy(ResizeExtent, obj + 96 + n * 24, 24);
                    memory.Write32(Arena + (Phase), 4);
                    return StepResult.Pending;
                }

                StartSeek(CpxTreeKind.Extent, GetWide(obj + 24), RW(ResizeId), logical, default, CpxSeek.Floor);
                return Call(CpxOperation.ReadTree, 3);
            case 3:
                Clear(ResizeExtent, 24);
                if (TreeFound)
                    Copy(ResizeExtent, TreeRecord, 24);
                memory.Write32(Arena + (Phase), 4);
                return StepResult.Pending;
            case 4:
                uint countAt = memory.Read32(ResizeExtent + 16);
                ulong logicalAt = Scalar64.Right(old, (int)memory.Read32(Arena + (UnitShift)));
                if (countAt == 0 || (logicalAt) >= (GetWide(ResizeExtent) + countAt) || memory.Read32(ResizeExtent + 20) == 1)
                    return BeginTx(6, 9);
                if (countAt > 65536 || memory.Read32(ResizeExtent + 20) != 0 || !Scalar64.Subtract(logicalAt, GetWide(ResizeExtent), out var delta) || !Scalar64.Add(GetWide(ResizeExtent + 8), delta, out var physical) || !DataUnit(physical))
                    return Fail(CpxError.Corrupt);
                WW(ResizePhysical, physical);
                return Issue(IoKind.Read, physical, Page(4), unit, 5);
            case 5:
                Clear(Page(4) + (((uint)((ulong)(old))) & (unit - 1)), unit - (((uint)((ulong)(old))) & (unit - 1)));
                return BeginTx(6, 6);
            case 6:
                return Issue(IoKind.Write, RW(ResizePhysical), Page(4), unit, 9);
            case 9:
                PutWide(obj + 8, length);
                memory.Write32(Arena + (Phase), 10);
                return StepResult.Pending;
            case 10:
                return SaveObject(obj, 11);
            case 11:
                return Call(CpxOperation.CommitTransaction, 99);
            case 20:
                return ReadStaged(Anchor + 3 * Q, p, 21);
            case 21:
                if (!Control(p, 3, Anchor + 3 * Q) || !ReadableGeneration(p) || memory.Read16(obj + 86) != 0)
                    return Fail(CpxError.Corrupt);
                PutWide(obj + 8, length);
                memory.Write16(obj + 86, 2);
                PutWide(obj + 40, GetWide(p + 88));
                PutWide(p + 88, RW(ResizeId));
                return StagePage(p, Anchor + 3 * Q, 22);
            case 22:
                return SaveObject(obj, 23);
            case 23:
                return Call(CpxOperation.CommitTransaction, 24);
            case 24:
                return Call(CpxOperation.Reclaim, 99);
            case 40:
                Clear(Page(4), unit);
                Copy(Page(4), obj + 96, ((uint)((ulong)(old))));
                Clear(obj + 96, 160);
                memory.Write16(obj + 88, 0);
                memory.Write8(obj + 85, 1);
                if ((((ulong)(old)) == 0))
                    return BeginTx(6, 9);
                WW(ResizeScan, ((ulong)(2097152u >> (int)memory.Read32(Arena + (UnitShift)))));
                memory.Write32(Arena + (Phase), 41);
                return StepResult.Pending;
            case 41:
                ulong at = RW(ResizeScan), group = (((ulong)(((uint)(((ulong)(at)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(at))) & 0xffff0000)));
                Scalar64.Subtract(RW(Units), group, out var remaining);
                if ((at) >= (Backup) || !(((ulong)(group)) == 0) && ((uint)(((ulong)(remaining)) >> 32)) == 0 && ((uint)((ulong)(remaining))) < 18 * Q)
                    return Reject(CpxError.DiskFull);
                return ReadStaged(Bitmap(at), p, 42);
            case 42:
                if (!ControlBitmap(p, RW(ResizeScan), (((ulong)(((uint)(((ulong)(RW(ResizeScan))) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(RW(ResizeScan)))) & 0xffff0000)))) || !ReadableGeneration(p))
                    return Fail(CpxError.Corrupt);
                memory.Write32(Arena + (Phase), 43);
                return StepResult.Pending;
            case 43:
                ulong window = (((ulong)(((uint)(((ulong)(RW(ResizeScan))) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(RW(ResizeScan)))) & ~4095u)));
                for (uint bit = ((uint)((ulong)(RW(ResizeScan)))) & 4095; bit < 4096; bit++)
                {
                    if ((memory.Read8(p + 64 + (bit >> 3)) & (0x80 >> (int)(bit & 7))) != 0 || !DataUnit(window + bit))
                        continue;
                    WW(ResizePhysical, window + bit);
                    PutWide(obj + 16, ((ulong)(1)));
                    memory.Write16(obj + 90, 1);
                    PutWide(obj + 104, window + bit);
                    memory.Write32(obj + 112, 1);
                    return BeginTx(42, 44);
                }

                WW(ResizeScan, window + 4096);
                memory.Write32(Arena + (Phase), 41);
                return StepResult.Pending;
            case 44:
                return ChangeAllocation(RW(ResizePhysical), 1, true, 6);
            case 99:
                return Done();
            default:
                return Fail(CpxError.Corrupt);
        }
    }
}
