namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    private const uint FormatOffset = 15600, FormatGroup = 15608, FormatMap = 15616, FormatGroupFree = 15620, FormatFree = 15624;
    /// <summary>Destructive formatting of caller-owned, quiescent media. A zero
    /// label length retains the byte-identical legacy unlabeled representation.</summary>
    public CpxError BeginFormat(uint sectorBytes, uint allocationBytes, Wide capacitySectors, Wide volumeId, uint epoch, uint latin1 = 0, uint length = 0)
    {
        uint cpxArenaBase = Arena;
        if ((((ulong)(volumeId)) == 0))
            return CpxError.InvalidArgument;
        if (memory.Read32(cpxArenaBase + (Signature)) == CpxArena.Magic && (memory.Read32(cpxArenaBase + (Waiting)) != 0 || memory.Read32(cpxArenaBase + (Finished)) == 0 || memory.Read32(cpxArenaBase + (Active)) != 0))
            return CpxError.Busy;
        if (memory.Read32(cpxArenaBase + (Signature)) == CpxArena.Magic && (memory.Read32(cpxArenaBase + (ReadOnlyAt)) != 0))
            return CpxError.ReadOnly;
        if (length != 0 && !ValidLabelInput(latin1, length))
            return CpxError.InvalidArgument;
        Copy((cpxArenaBase + 16000), latin1, length);
        memory.Write32(cpxArenaBase + (LabelLength), length);
        CpxError error = Initialize(sectorBytes, allocationBytes, capacitySectors, epoch);
        if (error != CpxError.None)
            return error;
        memory.Write32(cpxArenaBase + (BufferFormatEnabled), memory.Read32(cpxArenaBase + (BufferEnabled)));
        memory.Write32(cpxArenaBase + (BufferEnabled), 0);
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(Identity);
            ulong cpxWideValue = volumeId;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        return StartOperation(CpxOperation.Format);
    }

    private ulong FormatDescriptor
    {
        get
        {
            uint cpxArenaBase = Arena;
            return (((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(FormatGroup)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(FormatGroup)) + 4)))))))) == 0) ? ((ulong)(131072u >> (int)memory.Read32(cpxArenaBase + (UnitShift)))) : ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(FormatGroup)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(FormatGroup)) + 4))))));
        }
    }

    private StepResult FormatStep()
    {
        uint cpxArenaBase = Arena;
        uint p = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(0)) * 4))), descriptor = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(1)) * 4)));
        ulong root = ((ulong)(2097152u >> (int)memory.Read32(cpxArenaBase + (UnitShift)))), group = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(FormatGroup)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(FormatGroup)) + 4))))));
        switch (memory.Read32(cpxArenaBase + (Phase)))
        {
            // Invalidate every possible prior primary/backup page position.
            // Until this barrier only anchor bytes are changed, so an old valid
            // backup still describes an untouched old filesystem.
            case 0:
                Clear(p, (memory.Read32(cpxArenaBase + (PageBytes))));
                memory.Write32(cpxArenaBase + (FormatOffset), 0);
                memory.Write32(cpxArenaBase + (Phase), 1);
                return StepResult.Pending;
            case 1:
                return Issue(IoKind.Write, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + (memory.Read32(cpxArenaBase + (FormatOffset)) >> (int)memory.Read32(cpxArenaBase + (UnitShift))), p, (memory.Read32(cpxArenaBase + (PageBytes))), 2);
            case 2:
                return Issue(IoKind.Write, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Units)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Units)) + 4)))))) - ((4096 - memory.Read32(cpxArenaBase + (FormatOffset))) >> (int)memory.Read32(cpxArenaBase + (UnitShift))), p, (memory.Read32(cpxArenaBase + (PageBytes))), 3);
            case 3:
                memory.Write32(cpxArenaBase + (FormatOffset), memory.Read32(cpxArenaBase + (FormatOffset)) + (memory.Read32(cpxArenaBase + (PageBytes))));
                if (memory.Read32(cpxArenaBase + (FormatOffset)) < 4096)
                {
                    memory.Write32(cpxArenaBase + (Phase), 1);
                    return StepResult.Pending;
                }

                return Issue(IoKind.Flush, default, 0, 0, 4);
            case 4:
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(FormatGroup);
                ulong cpxWideValue = default;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

            {
                uint cpxWideAddress = cpxArenaBase + (uint)(FormatFree);
                ulong cpxWideValue = default;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write32(cpxArenaBase + (Phase), 5);
                return StepResult.Pending;
            case 5:
                if ((group) >= (((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Units)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Units)) + 4))))))))
                {
                    memory.Write32(cpxArenaBase + (Phase), 20);
                    return StepResult.Pending;
                }

                Scalar64.Subtract(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Units)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Units)) + 4)))))), group, out var remaining);
                if (!(((ulong)(group)) == 0) && ((uint)(((ulong)(remaining)) >> 32)) == 0 && ((uint)((ulong)(remaining))) < 18 * (memory.Read32(cpxArenaBase + (GeometryQ))))
                {
                    memory.Write32(cpxArenaBase + (Phase), 20);
                    return StepResult.Pending;
                }

                Header(descriptor, 6, FormatDescriptor, default);
            {
                uint cpxWideAddress = (uint)(descriptor + 24);
                ulong cpxWideValue = group;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write32(descriptor + 64, ((uint)(((ulong)(remaining)) >> 32)) != 0 || ((uint)((ulong)(remaining))) >= 65536 ? 65536u : ((uint)((ulong)(remaining))));
                memory.Write32(cpxArenaBase + (FormatMap), 0);
                memory.Write32(cpxArenaBase + (FormatGroupFree), 0);
                memory.Write32(cpxArenaBase + (Phase), 6);
                return StepResult.Pending;
            case 6:
                ulong mapAddress = FormatDescriptor + (memory.Read32(cpxArenaBase + (FormatMap)) + 1) * (memory.Read32(cpxArenaBase + (GeometryQ)));
                Header(p, 7, mapAddress, default);
            {
                uint cpxWideAddress = (uint)(p + 24);
                ulong cpxWideValue = group;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write32(p + 12, memory.Read32(cpxArenaBase + (FormatMap)));
                memory.Write32(cpxArenaBase + (Phase), 7);
                return StepResult.Pending;
            case 7:
                uint free = 0;
                ulong start = group + memory.Read32(cpxArenaBase + (FormatMap)) * 4096;
                for (uint bit = 0; bit < 4096; bit++)
                {
                    ulong unit = start + bit;
                    bool used = !DataUnit(unit) || ((unit) >= (root) && (unit) < (root + 33 * (memory.Read32(cpxArenaBase + (GeometryQ)))));
                    if (used)
                    {
                        uint at = p + 64 + (bit >> 3);
                        memory.Write8(at, (byte)(memory.Read8(at) | (0x80u >> (int)(bit & 7))));
                    }
                    else
                        free++;
                }

                memory.Write32(descriptor + 80 + memory.Read32(cpxArenaBase + (FormatMap)) * 4, free);
                memory.Write32(cpxArenaBase + (FormatGroupFree), memory.Read32(cpxArenaBase + (FormatGroupFree)) + free);
                memory.Write32(cpxArenaBase + (Phase), 8);
                return StepResult.Pending;
            case 8:
                Seal(p, 512);
                return Issue(IoKind.Write, FormatDescriptor + (memory.Read32(cpxArenaBase + (FormatMap)) + 1) * (memory.Read32(cpxArenaBase + (GeometryQ))), p, (memory.Read32(cpxArenaBase + (PageBytes))), 9);
            case 9:
                memory.Write32(cpxArenaBase + (FormatMap), memory.Read32(cpxArenaBase + (FormatMap)) + 1);
                if (memory.Read32(cpxArenaBase + (FormatMap)) < 16)
                {
                    memory.Write32(cpxArenaBase + (Phase), 6);
                    return StepResult.Pending;
                }

                memory.Write32(descriptor + 68, memory.Read32(cpxArenaBase + (FormatGroupFree)));
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(FormatFree);
                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(FormatFree)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(FormatFree)) + 4)))))) + memory.Read32(cpxArenaBase + (FormatGroupFree));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write32(cpxArenaBase + (Phase), 10);
                return StepResult.Pending;
            case 10:
                Seal(descriptor, 80);
                return Issue(IoKind.Write, FormatDescriptor, descriptor, (memory.Read32(cpxArenaBase + (PageBytes))), 11);
            case 11:
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(FormatGroup);
                ulong cpxWideValue = group + 65536;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write32(cpxArenaBase + (Phase), 5);
                return StepResult.Pending;
            case 20:
                Header(p, 11, root, default);
                memory.Write16(p + 48, 1);
            {
                uint cpxWideAddress = (uint)(p + 64);
                ulong cpxWideValue = ((ulong)(1));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write32(p + 64 + 76, 1);
                memory.Write32(p + 64 + 80, 1);
                memory.Write8(p + 64 + 84, 2);
                Seal(p, 256);
                return Issue(IoKind.Write, root, p, (memory.Read32(cpxArenaBase + (PageBytes))), 21);
            case 21:
                Header(p, 3, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + 3 * (memory.Read32(cpxArenaBase + (GeometryQ))), default);
            {
                uint cpxWideAddress = (uint)(p + 64);
                ulong cpxWideValue = ((ulong)(2));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

            {
                uint cpxWideAddress = (uint)(p + 72);
                ulong cpxWideValue = ((ulong)(1));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

            {
                uint cpxWideAddress = (uint)(p + 80);
                ulong cpxWideValue = root;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

            {
                uint cpxWideAddress = (uint)(p + 96);
                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(FormatFree)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(FormatFree)) + 4))))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                if (memory.Read32(cpxArenaBase + (LabelLength)) != 0)
                    EncodeStateLabel(p);
                Seal(p, memory.Read32(cpxArenaBase + (LabelLength)) == 0 ? 48u : 128u);
                return Issue(IoKind.Write, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + 3 * (memory.Read32(cpxArenaBase + (GeometryQ))), p, (memory.Read32(cpxArenaBase + (PageBytes))), 22);
            case 22:
                Header(p, 4, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + 8 * (memory.Read32(cpxArenaBase + (GeometryQ))), default);
                memory.Write16(p + 64, 32);
                for (uint n = 0; n < 32; n++)
                {
                    uint cpxWideAddress = (uint)(p + 72 + n * 8);
                    ulong cpxWideValue = root + (n + 1) * (memory.Read32(cpxArenaBase + (GeometryQ)));
                    memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                    memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                }

                Seal(p, 520);
                return Issue(IoKind.Write, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + 8 * (memory.Read32(cpxArenaBase + (GeometryQ))), p, (memory.Read32(cpxArenaBase + (PageBytes))), 23);
            case 23:
                Header(p, 5, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + 4 * (memory.Read32(cpxArenaBase + (GeometryQ))), default);
                memory.Write32(p + 64, 1);
                Seal(p, 4);
                return Issue(IoKind.Write, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + 4 * (memory.Read32(cpxArenaBase + (GeometryQ))), p, (memory.Read32(cpxArenaBase + (PageBytes))), 24);
            case 24:
                Clear(p, (memory.Read32(cpxArenaBase + (PageBytes))));
                return Issue(IoKind.Write, Commit(((ulong)(1))), p, (memory.Read32(cpxArenaBase + (PageBytes))), 25);
            case 25:
                return Issue(IoKind.Write, Commit(((ulong)(2))), p, (memory.Read32(cpxArenaBase + (PageBytes))), 26);
            case 26:
                MakeCheckpoint(p, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + (memory.Read32(cpxArenaBase + (GeometryQ))));
                return Issue(IoKind.Write, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + (memory.Read32(cpxArenaBase + (GeometryQ))), p, (memory.Read32(cpxArenaBase + (PageBytes))), 27);
            case 27:
                MakeCheckpoint(p, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + 2 * (memory.Read32(cpxArenaBase + (GeometryQ))));
                return Issue(IoKind.Write, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + 2 * (memory.Read32(cpxArenaBase + (GeometryQ))), p, (memory.Read32(cpxArenaBase + (PageBytes))), 28);
            case 28:
                return Issue(IoKind.Flush, default, 0, 0, 29);
            case 29:
                FormatAnchor(p, (((((ulong)(memory.Read32(cpxArenaBase + (((uint)(GeometryBackup)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(GeometryBackup)) + 4))))))));
                return Issue(IoKind.Write, (((((ulong)(memory.Read32(cpxArenaBase + (((uint)(GeometryBackup)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(GeometryBackup)) + 4))))))), p, (memory.Read32(cpxArenaBase + (PageBytes))), 30);
            case 30:
                return Issue(IoKind.Flush, default, 0, 0, 31);
            case 31:
                FormatAnchor(p, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))));
                return Issue(IoKind.Write, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))), p, (memory.Read32(cpxArenaBase + (PageBytes))), 32);
            case 32:
                return Issue(IoKind.Flush, default, 0, 0, 33);
            case 33:
                if (memory.Read32(cpxArenaBase + (LabelLength)) != 0)
                    CacheInputLabel();
                memory.Write32(cpxArenaBase + (Mounted), 1);
                memory.Write32(cpxArenaBase + (BufferEnabled), memory.Read32(cpxArenaBase + (BufferFormatEnabled)));
                memory.Write32(cpxArenaBase + (BufferFormatEnabled), 0);
                return Done();
            default:
                return Fail(CpxError.Corrupt);
        }
    }

    private void FormatAnchor(uint p, ulong at)
    {
        uint cpxArenaBase = Arena;
        Header(p, 1, at, default);
        {
            uint cpxWideAddress = (uint)(p + 72);
            ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Identity)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Identity)) + 4))))));
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(p + 80, 1u << (int)memory.Read32(cpxArenaBase + (UnitShift)));
        memory.Write32(p + 84, (memory.Read32(cpxArenaBase + (PageBytes))));
        {
            uint cpxWideAddress = (uint)(p + 88);
            ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Units)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Units)) + 4))))));
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        {
            uint cpxWideAddress = (uint)(p + 96);
            ulong cpxWideValue = ((ulong)(1));
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(p + 104, 1u << (int)memory.Read32(cpxArenaBase + (SectorShift)));
        memory.Write32(p + 108, 1);
        {
            uint cpxWideAddress = (uint)(p + 112);
            ulong cpxWideValue = (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + 8 * (memory.Read32(cpxArenaBase + (GeometryQ)));
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        {
            uint cpxWideAddress = (uint)(p + 120);
            ulong cpxWideValue = (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + 3 * (memory.Read32(cpxArenaBase + (GeometryQ)));
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        Seal(p, 64);
    }
}