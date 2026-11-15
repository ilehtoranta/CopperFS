namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    private const uint FormatOffset = 15600, FormatGroup = 15608, FormatMap = 15616, FormatGroupFree = 15620, FormatFree = 15624;
    /// <summary>Destructive formatting of caller-owned, quiescent media. A zero
    /// label length retains the byte-identical legacy unlabeled representation.</summary>
    public CpxError BeginFormat(uint sectorBytes, uint allocationBytes, Wide capacitySectors, Wide volumeId, uint epoch, uint latin1 = 0, uint length = 0)
    {
        if ((((ulong)(volumeId)) == 0))
            return CpxError.InvalidArgument;
        if (memory.Read32(Arena + (Signature)) == CpxArena.Magic && (memory.Read32(Arena + (Waiting)) != 0 || memory.Read32(Arena + (Finished)) == 0 || memory.Read32(Arena + (Active)) != 0))
            return CpxError.Busy;
        if (memory.Read32(Arena + (Signature)) == CpxArena.Magic && IsReadOnly)
            return CpxError.ReadOnly;
        if (length != 0 && !ValidLabelInput(latin1, length))
            return CpxError.InvalidArgument;
        Copy(LabelInput, latin1, length);
        memory.Write32(Arena + (LabelLength), length);
        CpxError error = Initialize(sectorBytes, allocationBytes, capacitySectors, epoch);
        if (error != CpxError.None)
            return error;
        memory.Write32(Arena + (BufferFormatEnabled), memory.Read32(Arena + (BufferEnabled)));
        memory.Write32(Arena + (BufferEnabled), 0);
        WW(Identity, volumeId);
        return StartOperation(CpxOperation.Format);
    }

    private ulong FormatDescriptor => (((ulong)(RW(FormatGroup))) == 0) ? ((ulong)(131072u >> (int)memory.Read32(Arena + (UnitShift)))) : RW(FormatGroup);

    private StepResult FormatStep()
    {
        uint p = Page(0), descriptor = Page(1);
        ulong root = ((ulong)(2097152u >> (int)memory.Read32(Arena + (UnitShift)))), group = RW(FormatGroup);
        switch (memory.Read32(Arena + (Phase)))
        {
            // Invalidate every possible prior primary/backup page position.
            // Until this barrier only anchor bytes are changed, so an old valid
            // backup still describes an untouched old filesystem.
            case 0:
                Clear(p, P);
                memory.Write32(Arena + (FormatOffset), 0);
                memory.Write32(Arena + (Phase), 1);
                return StepResult.Pending;
            case 1:
                return Issue(IoKind.Write, Anchor + (memory.Read32(Arena + (FormatOffset)) >> (int)memory.Read32(Arena + (UnitShift))), p, P, 2);
            case 2:
                return Issue(IoKind.Write, RW(Units) - ((4096 - memory.Read32(Arena + (FormatOffset))) >> (int)memory.Read32(Arena + (UnitShift))), p, P, 3);
            case 3:
                memory.Write32(Arena + (FormatOffset), memory.Read32(Arena + (FormatOffset)) + P);
                if (memory.Read32(Arena + (FormatOffset)) < 4096)
                {
                    memory.Write32(Arena + (Phase), 1);
                    return StepResult.Pending;
                }

                return Issue(IoKind.Flush, default, 0, 0, 4);
            case 4:
                WW(FormatGroup, default);
                WW(FormatFree, default);
                memory.Write32(Arena + (Phase), 5);
                return StepResult.Pending;
            case 5:
                if ((group) >= (RW(Units)))
                {
                    memory.Write32(Arena + (Phase), 20);
                    return StepResult.Pending;
                }

                Scalar64.Subtract(RW(Units), group, out var remaining);
                if (!(((ulong)(group)) == 0) && ((uint)(((ulong)(remaining)) >> 32)) == 0 && ((uint)((ulong)(remaining))) < 18 * Q)
                {
                    memory.Write32(Arena + (Phase), 20);
                    return StepResult.Pending;
                }

                Header(descriptor, 6, FormatDescriptor, default);
                PutWide(descriptor + 24, group);
                memory.Write32(descriptor + 64, ((uint)(((ulong)(remaining)) >> 32)) != 0 || ((uint)((ulong)(remaining))) >= 65536 ? 65536u : ((uint)((ulong)(remaining))));
                memory.Write32(Arena + (FormatMap), 0);
                memory.Write32(Arena + (FormatGroupFree), 0);
                memory.Write32(Arena + (Phase), 6);
                return StepResult.Pending;
            case 6:
                ulong mapAddress = FormatDescriptor + (memory.Read32(Arena + (FormatMap)) + 1) * Q;
                Header(p, 7, mapAddress, default);
                PutWide(p + 24, group);
                memory.Write32(p + 12, memory.Read32(Arena + (FormatMap)));
                memory.Write32(Arena + (Phase), 7);
                return StepResult.Pending;
            case 7:
                uint free = 0;
                ulong start = group + memory.Read32(Arena + (FormatMap)) * 4096;
                for (uint bit = 0; bit < 4096; bit++)
                {
                    ulong unit = start + bit;
                    bool used = !DataUnit(unit) || ((unit) >= (root) && (unit) < (root + 33 * Q));
                    if (used)
                    {
                        uint at = p + 64 + (bit >> 3);
                        memory.Write8(at, (byte)(memory.Read8(at) | (0x80u >> (int)(bit & 7))));
                    }
                    else
                        free++;
                }

                memory.Write32(descriptor + 80 + memory.Read32(Arena + (FormatMap)) * 4, free);
                memory.Write32(Arena + (FormatGroupFree), memory.Read32(Arena + (FormatGroupFree)) + free);
                memory.Write32(Arena + (Phase), 8);
                return StepResult.Pending;
            case 8:
                Seal(p, 512);
                return Issue(IoKind.Write, FormatDescriptor + (memory.Read32(Arena + (FormatMap)) + 1) * Q, p, P, 9);
            case 9:
                memory.Write32(Arena + (FormatMap), memory.Read32(Arena + (FormatMap)) + 1);
                if (memory.Read32(Arena + (FormatMap)) < 16)
                {
                    memory.Write32(Arena + (Phase), 6);
                    return StepResult.Pending;
                }

                memory.Write32(descriptor + 68, memory.Read32(Arena + (FormatGroupFree)));
                WW(FormatFree, RW(FormatFree) + memory.Read32(Arena + (FormatGroupFree)));
                memory.Write32(Arena + (Phase), 10);
                return StepResult.Pending;
            case 10:
                Seal(descriptor, 80);
                return Issue(IoKind.Write, FormatDescriptor, descriptor, P, 11);
            case 11:
                WW(FormatGroup, group + 65536);
                memory.Write32(Arena + (Phase), 5);
                return StepResult.Pending;
            case 20:
                Header(p, 11, root, default);
                memory.Write16(p + 48, 1);
                PutWide(p + 64, ((ulong)(1)));
                memory.Write32(p + 64 + 76, 1);
                memory.Write32(p + 64 + 80, 1);
                memory.Write8(p + 64 + 84, 2);
                Seal(p, 256);
                return Issue(IoKind.Write, root, p, P, 21);
            case 21:
                Header(p, 3, Anchor + 3 * Q, default);
                PutWide(p + 64, ((ulong)(2)));
                PutWide(p + 72, ((ulong)(1)));
                PutWide(p + 80, root);
                PutWide(p + 96, RW(FormatFree));
                if (memory.Read32(Arena + (LabelLength)) != 0)
                    EncodeStateLabel(p);
                Seal(p, memory.Read32(Arena + (LabelLength)) == 0 ? 48u : 128u);
                return Issue(IoKind.Write, Anchor + 3 * Q, p, P, 22);
            case 22:
                Header(p, 4, Anchor + 8 * Q, default);
                memory.Write16(p + 64, 32);
                for (uint n = 0; n < 32; n++)
                    PutWide(p + 72 + n * 8, root + (n + 1) * Q);
                Seal(p, 520);
                return Issue(IoKind.Write, Anchor + 8 * Q, p, P, 23);
            case 23:
                Header(p, 5, Anchor + 4 * Q, default);
                memory.Write32(p + 64, 1);
                Seal(p, 4);
                return Issue(IoKind.Write, Anchor + 4 * Q, p, P, 24);
            case 24:
                Clear(p, P);
                return Issue(IoKind.Write, Commit(((ulong)(1))), p, P, 25);
            case 25:
                return Issue(IoKind.Write, Commit(((ulong)(2))), p, P, 26);
            case 26:
                MakeCheckpoint(p, Anchor + Q);
                return Issue(IoKind.Write, Anchor + Q, p, P, 27);
            case 27:
                MakeCheckpoint(p, Anchor + 2 * Q);
                return Issue(IoKind.Write, Anchor + 2 * Q, p, P, 28);
            case 28:
                return Issue(IoKind.Flush, default, 0, 0, 29);
            case 29:
                FormatAnchor(p, Backup);
                return Issue(IoKind.Write, Backup, p, P, 30);
            case 30:
                return Issue(IoKind.Flush, default, 0, 0, 31);
            case 31:
                FormatAnchor(p, Anchor);
                return Issue(IoKind.Write, Anchor, p, P, 32);
            case 32:
                return Issue(IoKind.Flush, default, 0, 0, 33);
            case 33:
                if (memory.Read32(Arena + (LabelLength)) != 0)
                    CacheInputLabel();
                memory.Write32(Arena + (Mounted), 1);
                memory.Write32(Arena + (BufferEnabled), memory.Read32(Arena + (BufferFormatEnabled)));
                memory.Write32(Arena + (BufferFormatEnabled), 0);
                return Done();
            default:
                return Fail(CpxError.Corrupt);
        }
    }

    private void FormatAnchor(uint p, ulong at)
    {
        Header(p, 1, at, default);
        PutWide(p + 72, RW(Identity));
        memory.Write32(p + 80, 1u << (int)memory.Read32(Arena + (UnitShift)));
        memory.Write32(p + 84, P);
        PutWide(p + 88, RW(Units));
        PutWide(p + 96, ((ulong)(1)));
        memory.Write32(p + 104, 1u << (int)memory.Read32(Arena + (SectorShift)));
        memory.Write32(p + 108, 1);
        PutWide(p + 112, Anchor + 8 * Q);
        PutWide(p + 120, Anchor + 3 * Q);
        Seal(p, 64);
    }
}
