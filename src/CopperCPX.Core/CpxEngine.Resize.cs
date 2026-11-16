namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    private const uint ResizeId = 14200, ResizeLength = 14208, ResizeOld = 14216, ResizePhysical = 14224, ResizeScan = 14232;
    private uint ResizeObject => Arena + 14300;
    private uint ResizeExtent => Arena + 14556;

    public CpxError BeginResize(Wide id, Wide size)
    {
        uint cpxArenaBase = Arena;
        CpxError error = StartOperation(CpxOperation.Resize);
        if (error != CpxError.None)
            return error;
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(ResizeId);
            ulong cpxWideValue = id;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        {
            uint cpxWideAddress = cpxArenaBase + (uint)(ResizeLength);
            ulong cpxWideValue = size;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        return error;
    }

    private StepResult ResizeStep()
    {
        uint cpxArenaBase = Arena;
        uint obj = (cpxArenaBase + 14300), p = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(3)) * 4))), unit = 1u << (int)memory.Read32(cpxArenaBase + (UnitShift));
        ulong length = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizeLength)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizeLength)) + 4)))))), old = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizeOld)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizeOld)) + 4))))));
        switch (memory.Read32(cpxArenaBase + (Phase)))
        {
            case 0:
                uint cached = (memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) ? CachedObject(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizeId)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizeId)) + 4))))))) : 0;
                if (cached != 0 && memory.Read8(cached + 84) == 1 && (memory.Read32(cached + 72) & 4) == 0 && (((ulong)(length)) == ((ulong)(((((ulong)(memory.Read32(((uint)(cached + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(cached + 8)) + 4)))))))))
                    return Done();
                if (TryFastShrink(cached, length)) return Done();
                return Call(CpxOperation.Reclaim, 1);
            case 1:
                return LoadObject(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizeId)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizeId)) + 4)))))), 2);
            case 2:
                if (memory.Read32(cpxArenaBase + (SourcePage)) == 0)
                    return Reject(CpxError.NotFound);
                Copy(obj, (cpxArenaBase + CpxArena.MountBytes), 256);
                if (memory.Read8(obj + 84) != 1)
                    return Reject(CpxError.WrongType);
                if ((memory.Read32(obj + 72) & 4) != 0)
                    return Reject(CpxError.Protected);
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(ResizeOld);
                ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)(obj + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(obj + 8)) + 4)))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                if ((((ulong)(length)) == ((ulong)(((((ulong)(memory.Read32(((uint)(obj + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(obj + 8)) + 4)))))))))
                    return Done();
                if (memory.Read8(obj + 85) == 0)
                {
                    if (((uint)(((ulong)(((((ulong)(memory.Read32(((uint)(obj + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(obj + 8)) + 4))))))) >> 32)) != 0 || ((uint)((ulong)(((((ulong)(memory.Read32(((uint)(obj + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(obj + 8)) + 4)))))))) > 160 || memory.Read16(obj + 88) != ((uint)((ulong)(((((ulong)(memory.Read32(((uint)(obj + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(obj + 8)) + 4)))))))))
                        return Fail(CpxError.Corrupt);
                    if (((uint)(((ulong)(length)) >> 32)) == 0 && ((uint)((ulong)(length))) <= 160)
                    {
                        uint from = ((uint)((ulong)(length))) < ((uint)((ulong)(((((ulong)(memory.Read32(((uint)(obj + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(obj + 8)) + 4)))))))) ? ((uint)((ulong)(length))) : ((uint)((ulong)(((((ulong)(memory.Read32(((uint)(obj + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(obj + 8)) + 4))))))));
                        Clear(obj + 96 + from, 160 - from);
                        {
                            uint cpxWideAddress = (uint)(obj + 8);
                            ulong cpxWideValue = length;
                            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                        }

                        memory.Write16(obj + 88, ((uint)((ulong)(length))));
                        return BeginTx(6, 10);
                    }

                    return EnsureReserve(30, 40);
                }

                if (memory.Read8(obj + 85) > 2)
                    return Fail(CpxError.Corrupt);
                if ((length) < (((((ulong)(memory.Read32(((uint)(obj + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(obj + 8)) + 4)))))))
                    return BeginTx(6, 20);
                if ((((uint)((ulong)(((((ulong)(memory.Read32(((uint)(obj + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(obj + 8)) + 4)))))))) & (unit - 1)) == 0)
                    return BeginTx(6, 9);
                ulong logical = Scalar64.Right(((((ulong)(memory.Read32(((uint)(obj + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(obj + 8)) + 4))))), (int)memory.Read32(cpxArenaBase + (UnitShift)));
                if (memory.Read8(obj + 85) == 1)
                {
                    uint count = memory.Read16(obj + 90);
                    if (count > 6)
                        return Fail(CpxError.Corrupt);
                    Clear((cpxArenaBase + 14556), 24);
                    for (uint n = 0; n < count; n++)
                        if ((((((ulong)(memory.Read32(((uint)(obj + 96 + n * 24))))) << 32 | ((ulong)(memory.Read32(((uint)(obj + 96 + n * 24)) + 4)))))) <= (logical))
                            Copy((cpxArenaBase + 14556), obj + 96 + n * 24, 24);
                    memory.Write32(cpxArenaBase + (Phase), 4);
                    return StepResult.Pending;
                }

                StartSeek(CpxTreeKind.Extent, ((((ulong)(memory.Read32(((uint)(obj + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(obj + 24)) + 4))))), ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizeId)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizeId)) + 4)))))), logical, default, CpxSeek.Floor);
                return Call(CpxOperation.ReadTree, 3);
            case 3:
                Clear((cpxArenaBase + 14556), 24);
                if ((memory.Read32(cpxArenaBase + (SeekFound)) != 0))
                    Copy((cpxArenaBase + 14556), ((cpxArenaBase + CpxArena.MountBytes) + 512), 24);
                memory.Write32(cpxArenaBase + (Phase), 4);
                return StepResult.Pending;
            case 4:
                uint countAt = memory.Read32((cpxArenaBase + 14556) + 16);
                ulong logicalAt = Scalar64.Right(old, (int)memory.Read32(cpxArenaBase + (UnitShift)));
                if (countAt == 0 || (logicalAt) >= (((((ulong)(memory.Read32(((uint)((cpxArenaBase + 14556)))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 14556))) + 4))))) + countAt) || memory.Read32((cpxArenaBase + 14556) + 20) == 1)
                    return BeginTx(6, 9);
                if (countAt > 65536 || memory.Read32((cpxArenaBase + 14556) + 20) != 0 || !Scalar64.Subtract(logicalAt, ((((ulong)(memory.Read32(((uint)((cpxArenaBase + 14556)))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 14556))) + 4))))), out var delta) || !Scalar64.Add(((((ulong)(memory.Read32(((uint)((cpxArenaBase + 14556) + 8))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 14556) + 8)) + 4))))), delta, out var physical) || !DataUnit(physical))
                    return Fail(CpxError.Corrupt);
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(ResizePhysical);
                ulong cpxWideValue = physical;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                return Issue(IoKind.Read, physical, (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(4)) * 4))), unit, 5);
            case 5:
                Clear((memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(4)) * 4))) + (((uint)((ulong)(old))) & (unit - 1)), unit - (((uint)((ulong)(old))) & (unit - 1)));
                return BeginTx(6, 6);
            case 6:
                return Issue(IoKind.Write, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizePhysical)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizePhysical)) + 4)))))), (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(4)) * 4))), unit, 9);
            case 9:
            {
                uint cpxWideAddress = (uint)(obj + 8);
                ulong cpxWideValue = length;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write32(cpxArenaBase + (Phase), 10);
                return StepResult.Pending;
            case 10:
                return SaveObject(obj, 11);
            case 11:
                return Call(CpxOperation.CommitTransaction, 99);
            case 20:
                return ReadStaged(StateAddress, p, 21);
            case 21:
                if (!Control(p, 3, StateAddress) || !ReadableGeneration(p) || memory.Read16(obj + 86) != 0)
                    return Fail(CpxError.Corrupt);
            {
                uint cpxWideAddress = (uint)(obj + 8);
                ulong cpxWideValue = length;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write16(obj + 86, 2);
            {
                uint cpxWideAddress = (uint)(obj + 40);
                ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)(p + 88))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 88)) + 4)))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

            {
                uint cpxWideAddress = (uint)(p + 88);
                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizeId)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizeId)) + 4))))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                return StagePage(p, StateAddress, 22);
            case 22:
                return SaveObject(obj, 23);
            case 23:
                return Call(CpxOperation.CommitTransaction, 24);
            case 24:
                return Call(CpxOperation.Reclaim, 99);
            case 40:
                Clear((memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(4)) * 4))), unit);
                Copy((memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(4)) * 4))), obj + 96, ((uint)((ulong)(old))));
                Clear(obj + 96, 160);
                memory.Write16(obj + 88, 0);
                memory.Write8(obj + 85, 1);
                if ((((ulong)(old)) == 0))
                    return BeginTx(6, 9);
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(ResizeScan);
                ulong cpxWideValue = ((ulong)(2097152u >> (int)memory.Read32(cpxArenaBase + (UnitShift))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write32(cpxArenaBase + (Phase), 41);
                return StepResult.Pending;
            case 41:
                ulong at = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizeScan)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizeScan)) + 4)))))), group = (((ulong)(((uint)(((ulong)(at)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(at))) & 0xffff0000)));
                Scalar64.Subtract(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Units)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Units)) + 4)))))), group, out var remaining);
                if ((at) >= ((((((ulong)(memory.Read32(cpxArenaBase + (((uint)(GeometryBackup)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(GeometryBackup)) + 4)))))))) || !(((ulong)(group)) == 0) && ((uint)(((ulong)(remaining)) >> 32)) == 0 && ((uint)((ulong)(remaining))) < (GroupMetadataPages + 1) * MetadataSpan)
                    return Reject(CpxError.DiskFull);
                return ReadStaged(Bitmap(at), p, 42);
            case 42:
                if (!ControlBitmap(p, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizeScan)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizeScan)) + 4)))))), (((ulong)(((uint)(((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizeScan)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizeScan)) + 4)))))))) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizeScan)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizeScan)) + 4))))))))) & 0xffff0000)))) || !ReadableGeneration(p))
                    return Fail(CpxError.Corrupt);
                memory.Write32(cpxArenaBase + (Phase), 43);
                return StepResult.Pending;
            case 43:
                ulong window = (((ulong)(((uint)(((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizeScan)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizeScan)) + 4)))))))) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizeScan)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizeScan)) + 4))))))))) & ~BitmapWindowMask)));
                for (uint bit = ((uint)((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizeScan)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizeScan)) + 4))))))))) & BitmapWindowMask; bit < BitmapWindowUnits; bit++)
                {
                    if ((memory.Read8(p + 64 + (bit >> 3)) & (0x80 >> (int)(bit & 7))) != 0 || !DataUnit(window + bit))
                        continue;
                    {
                        uint cpxWideAddress = cpxArenaBase + (uint)(ResizePhysical);
                        ulong cpxWideValue = window + bit;
                        memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                        memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                    }

                    {
                        uint cpxWideAddress = (uint)(obj + 16);
                        ulong cpxWideValue = ((ulong)(1));
                        memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                        memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                    }

                    memory.Write16(obj + 90, 1);
                    {
                        uint cpxWideAddress = (uint)(obj + 104);
                        ulong cpxWideValue = window + bit;
                        memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                        memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                    }

                    memory.Write32(obj + 112, 1);
                    return BeginTx(42, 44);
                }

            {
                uint cpxWideAddress = cpxArenaBase + (uint)(ResizeScan);
                ulong cpxWideValue = window + BitmapWindowUnits;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write32(cpxArenaBase + (Phase), 41);
                return StepResult.Pending;
            case 44:
                return ChangeAllocation(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizePhysical)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ResizePhysical)) + 4)))))), 1, true, 6);
            case 99:
                return Done();
            default:
                return Fail(CpxError.Corrupt);
        }
    }
}
