namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    private const uint RecoveryOwnership = 500, RecoveryBitmapReused = 508; // bit 0: owned attribute; bit 1: state image; bit 2: OWN1
    private StepResult RecoveryStep()
    {
        uint p = Page(5), phase = memory.Read32(Arena + (Phase));
        ulong tx = RW(Candidate);
        switch (phase)
        {
            case 0:
                // The two checkpoints are adjacent. Keep their independent CRC
                // and generation decisions while sharing one physical request.
                return Issue(IoKind.Read, Anchor + Q, Page(0), 2 * P, 1);
            case 1:
                memory.Write32(Arena + (ValidOne), CheckpointValid(Page(0), Anchor + Q) ? 1u : 0u);
                WW(CheckpointOne, GetWide(Page(0) + 80));
                memory.Write32(Arena + (Phase), 2);
                return StepResult.Pending;
            case 2:
                memory.Write32(Arena + (ValidTwo), CheckpointValid(Page(1), Anchor + 2 * Q) ? 1u : 0u);
                WW(CheckpointTwo, GetWide(Page(1) + 80));
                if (memory.Read32(Arena + (ValidOne)) == 0 && memory.Read32(Arena + (ValidTwo)) == 0)
                    return Fail(CpxError.Corrupt);
                ulong a = memory.Read32(Arena + (ValidOne)) != 0 ? RW(CheckpointOne) : default, b = memory.Read32(Arena + (ValidTwo)) != 0 ? RW(CheckpointTwo) : default;
                WW(Generation, (a) >= (b) ? a : b);
                WW(Candidate, default);
                memory.Write32(Arena + (Count), 0);
                return Issue(IoKind.Read, Commit(((ulong)(1))), p, P, 3);
            case 3:
                if (!InspectCommit(p, ((ulong)(1))))
                    return Fail(CpxError.Corrupt);
                return Issue(IoKind.Read, Commit(((ulong)(2))), p, P, 4);
            case 4:
                if (!InspectCommit(p, ((ulong)(2))))
                    return Fail(CpxError.Corrupt);
                tx = RW(Candidate);
                if ((((ulong)(tx)) == 0))
                {
                    memory.Write32(Arena + (Phase), 20);
                    return StepResult.Pending;
                }

                if (memory.Read32(Arena + (ReadOnlyAt)) != 0)
                    return Fail(CpxError.ReadOnly);
                return Issue(IoKind.Read, Journal(tx), p, P, 5);
            case 5:
                if (!Control(p, 8, Journal(tx)) || !IdentityMatches(p) || !(((ulong)(GetWide(p + 32))) == ((ulong)(tx))) || !(((ulong)(GetWide(p + 80))) == ((ulong)(tx))) || !(((ulong)(GetWide(p + 88))) == ((ulong)(RW(Generation)))) || memory.Read32(p + 96) != memory.Read32(Arena + (Count)) || memory.Read32(p + 100) != memory.Read32(Arena + (Digest)) || memory.Read16(p + 52) != 40)
                    return Fail(CpxError.Corrupt);
                Clear(Index, 2048);
                memory.Write32(Arena + (Cursor), 0);
                return Issue(IoKind.Read, Descriptor(tx, 0), p, P, 6);
            case 6:
                uint page = memory.Read32(Arena + (Cursor)), first = page * ((P - 64) >> 5), take = first < memory.Read32(Arena + (Count)) ? memory.Read32(Arena + (Count)) - first : 0;
                if (take > ((P - 64) >> 5))
                    take = (P - 64) >> 5;
                if (!Control(p, 9, Descriptor(tx, page)) || !(((ulong)(GetWide(p + 32))) == ((ulong)(tx))) || memory.Read16(p + 48) != take || memory.Read16(p + 52) != take * 32)
                    return Fail(CpxError.Corrupt);
                Copy(Index + first * 32, p + 64, take * 32);
                if (page < 3)
                {
                    memory.Write32(Arena + (Cursor), page + 1);
                    return Issue(IoKind.Read, Descriptor(tx, page + 1), p, P, 6);
                }

                memory.Write32(Arena + (Phase), 7);
                return StepResult.Pending;
            case 7:
                if (Crc(Index, memory.Read32(Arena + (Count)) * 32, false) != memory.Read32(Arena + (Digest)))
                    return Fail(CpxError.Corrupt);
                memory.Write32(Arena + (RecoveryOwnership), 0);
                memory.Write32(Arena + (Cursor), 0);
                memory.Write32(Arena + (Phase), 8);
                return StepResult.Pending;
            case 8:
                uint index = memory.Read32(Arena + (Cursor)), d = Index + index * 32;
                ulong home = GetWide(d);
                if (!TargetValid(home, memory.Read16(d + 24), GetWide(d + 8)) || memory.Read32(d + 16) != index || memory.Read16(d + 26) != 0 || memory.Read32(d + 28) != 0)
                    return Fail(CpxError.Corrupt);
                for (uint n = 0; n < index; n++)
                    if ((((ulong)(GetWide(Index + n * 32))) == ((ulong)(home))))
                        return Fail(CpxError.Corrupt);
                return Issue(IoKind.Read, Image(tx, index), p, P, 9);
            case 9:
                if (!ImageValid(p, tx, memory.Read32(Arena + (Cursor))))
                    return Fail(CpxError.Corrupt);
                if (memory.Read16(p + 4) == 18 && memory.Read16(p + 64) == 2)
                    memory.Write32(Arena + (RecoveryOwnership), memory.Read32(Arena + (RecoveryOwnership)) | 1);
                if (memory.Read16(p + 4) == 3)
                    memory.Write32(Arena + (RecoveryOwnership), memory.Read32(Arena + (RecoveryOwnership)) | 2 | (memory.Read16(p + 52) == 136 ? 4u : 0u));
                if (memory.Read32(Arena + (Cursor)) + 1 < memory.Read32(Arena + (Count)))
                {
                    memory.Write32(Arena + (Cursor), memory.Read32(Arena + (Cursor)) + 1);
                    memory.Write32(Arena + (Phase), 8);
                    return StepResult.Pending;
                }

                if ((memory.Read32(Arena + (RecoveryOwnership)) & 1) != 0)
                {
                    if ((memory.Read32(Arena + (RecoveryOwnership)) & 2) == 0)
                        return Issue(IoKind.Read, StatePageAddress, p, P, 14);
                    if ((memory.Read32(Arena + (RecoveryOwnership)) & 4) == 0)
                        return Fail(CpxError.Corrupt);
                }

                memory.Write32(Arena + (Cursor), 0);
                memory.Write32(Arena + (Phase), 10);
                return StepResult.Pending;
            case 10:
                return Issue(IoKind.Read, Image(tx, memory.Read32(Arena + (Cursor))), p, P, 11);
            case 11:
                if (!ImageValid(p, tx, memory.Read32(Arena + (Cursor))))
                    return Fail(CpxError.Corrupt);
                return Issue(IoKind.Write, GetWide(Index + memory.Read32(Arena + (Cursor)) * 32), p, P, 12);
            case 12:
                if (memory.Read32(Arena + (Cursor)) + 1 < memory.Read32(Arena + (Count)))
                {
                    memory.Write32(Arena + (Cursor), memory.Read32(Arena + (Cursor)) + 1);
                    memory.Write32(Arena + (Phase), 10);
                    return StepResult.Pending;
                }

                return Issue(IoKind.Flush, default, 0, 0, 13);
            case 13:
                WW(Generation, tx);
                memory.Write32(Arena + (Phase), 20);
                return StepResult.Pending;
            case 14:
                if (!Control(p, 3, StatePageAddress) || !ValidStateLabel(p) || memory.Read16(p + 52) != 136 || (GetWide(p + 32)) > (RW(Generation)))
                    return Fail(CpxError.Corrupt);
                memory.Write32(Arena + (Cursor), 0);
                memory.Write32(Arena + (Phase), 10);
                return StepResult.Pending;
            case 20:
                if (memory.Read32(Arena + (ReadOnlyAt)) != 0 || ((((ulong)(tx)) == 0) && memory.Read32(Arena + (ValidOne)) != 0 && (((ulong)(RW(CheckpointOne))) == ((ulong)(RW(Generation))))))
                {
                    memory.Write32(Arena + (Phase), 22);
                    return StepResult.Pending;
                }

                MakeCheckpoint(p, Anchor + Q);
                return Issue(IoKind.Write, Anchor + Q, p, P, 21);
            case 21:
                return Issue(IoKind.Flush, default, 0, 0, 22);
            case 22:
                if (memory.Read32(Arena + (ReadOnlyAt)) != 0 || ((((ulong)(tx)) == 0) && memory.Read32(Arena + (ValidTwo)) != 0 && (((ulong)(RW(CheckpointTwo))) == ((ulong)(RW(Generation))))))
                {
                    memory.Write32(Arena + (Phase), 24);
                    return StepResult.Pending;
                }

                MakeCheckpoint(p, Anchor + 2 * Q);
                return Issue(IoKind.Write, Anchor + 2 * Q, p, P, 23);
            case 23:
                return Issue(IoKind.Flush, default, 0, 0, 24);
            case 24:
                return Issue(IoKind.Read, Anchor + 4 * Q, p, P, 25);
            case 25:
                if (!Control(p, 5, Anchor + 4 * Q) || memory.Read32(p + 64) != 1 || memory.Read16(p + 52) != 4)
                    return Fail(CpxError.Corrupt);
                return Issue(IoKind.Read, Anchor + 3 * Q, p, P, 26);
            case 26:
                if (!Control(p, 3, Anchor + 3 * Q) || (GetWide(p + 64)) < (((ulong)(2))) || (((ulong)(GetWide(p + 72))) == 0) || !Allocatable(GetWide(p + 80)) || !ValidStateLabel(p))
                    return Fail(CpxError.Corrupt);
                CacheStateLabel(p);
                return Issue(IoKind.Read, Anchor + 8 * Q, Page(3), P, 27);
            case 27:
                uint reserve = Page(3);
                if (!Control(reserve, 4, Anchor + 8 * Q) || memory.Read16(reserve + 64) > 64 || memory.Read16(reserve + 52) != 520)
                    return Fail(CpxError.Corrupt);
                // Journal replay no longer needs its descriptor index. Reuse
                // those 2 KiB as 128 address buckets for at most 64 reserve
                // entries, rather than comparing each entry to every prefix.
                uint reserveTable = Index;
                for (uint bucket = 0; bucket < 128; bucket++)
                    memory.Write32(reserveTable + bucket * 16 + 8, 0);
                memory.Write32(Arena + (Count), memory.Read16(reserve + 64));
                memory.Write32(Arena + (Cursor), 0);
                memory.Write32(Arena + (Phase), 28);
                return StepResult.Pending;
            case 28:
                if (memory.Read32(Arena + (Cursor)) == memory.Read32(Arena + (Count)))
                {
                    memory.Write32(Arena + (Mounted), 1);
                    memory.Write32(Arena + (Poisoned), 0);
                    Clear(Index, 2048);
                    return memory.Read32(Arena + (ReadOnlyAt)) != 0 ? Done() : Call(CpxOperation.Reclaim, 30);
                }

                ulong address = GetWide(Page(3) + 72 + memory.Read32(Arena + (Cursor)) * 8);
                if (!Allocatable(address))
                    return Fail(CpxError.Corrupt);
                uint hash = ((((uint)((ulong)(address))) >> (int)(memory.Read32(Arena + (GeometryShift)) - memory.Read32(Arena + (UnitShift)))) ^ ((uint)(((ulong)(address)) >> 32))) & 127;
                uint table = Index;
                bool inserted = false;
                for (uint probe = 0; probe < 128; probe++)
                {
                    uint entry = table + hash * 16;
                    if (memory.Read32(entry + 8) == 0)
                    {
                        memory.Write32(entry, ((uint)(((ulong)(address)) >> 32)));
                        memory.Write32(entry + 4, ((uint)((ulong)(address))));
                        memory.Write32(entry + 8, 1);
                        inserted = true;
                        break;
                    }

                    if (memory.Read32(entry) == ((uint)(((ulong)(address)) >> 32)) && memory.Read32(entry + 4) == ((uint)((ulong)(address))))
                        return Fail(CpxError.Corrupt);
                    hash = (hash + 1) & 127;
                }

                if (!inserted)
                    return Fail(CpxError.Corrupt);
                bool same = memory.Read32(Arena + (Cursor)) != 0 && (((ulong)(Bitmap(address))) == ((ulong)(Bitmap(RW(Target)))));
                WW(Target, address);
                memory.Write32(Arena + (RecoveryBitmapReused), same ? 1u : 0u);
                if (same)
                {
                    memory.Write32(Arena + (Phase), 29);
                    return StepResult.Pending;
                }

                return Issue(IoKind.Read, Bitmap(address), p, P, 29);
            case 29:
                ulong at = RW(Target), group = (((ulong)(((uint)(((ulong)(at)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(at))) & 0xffff0000)));
                if (memory.Read32(Arena + (RecoveryBitmapReused)) == 0 && !ControlBitmap(p, at, group))
                    return Fail(CpxError.Corrupt);
                uint bit = ((uint)((ulong)(at))) & 4095;
                for (uint n = 0; n < Q; n++)
                    if ((memory.Read8(p + 64 + ((bit + n) >> 3)) & (0x80 >> ((int)(bit + n) & 7))) == 0)
                        return Fail(CpxError.Corrupt);
                memory.Write32(Arena + (Cursor), memory.Read32(Arena + (Cursor)) + 1);
                memory.Write32(Arena + (Phase), 28);
                return StepResult.Pending;
            case 30:
                return Call(CpxOperation.PruneObjects, 31);
            case 31:
                return Done();
            default:
                return Fail(CpxError.Corrupt);
        }
    }

    private ulong Bitmap(ulong at)
    {
        ulong group = (((ulong)(((uint)(((ulong)(at)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(at))) & 0xffff0000)));
        return ((((ulong)(group)) == 0) ? ((ulong)(131072u >> (int)memory.Read32(Arena + (UnitShift)))) : group) + (1 + ((((uint)((ulong)(at))) & 65535) >> 12)) * Q;
    }

    private bool ControlBitmap(uint p, ulong at, ulong owner) => ValidPage(p, Bitmap(at)) && memory.Read16(p + 4) == 7 && (((ulong)(GetWide(p + 24))) == ((ulong)(owner))) && memory.Read32(p + 12) == ((((uint)((ulong)(at))) & 65535) >> 12) && memory.Read16(p + 52) == 512;
    private bool CheckpointValid(uint p, ulong at) => Control(p, 2, at) && IdentityMatches(p) && (((ulong)(GetWide(p + 32))) == ((ulong)(GetWide(p + 80)))) && memory.Read16(p + 52) == 24;
    private void MakeCheckpoint(uint p, ulong at)
    {
        Header(p, 2, at, RW(Generation));
        PutWide(p + 72, RW(Identity));
        PutWide(p + 80, RW(Generation));
        Seal(p, 24);
    }

    private bool InspectCommit(uint p, ulong slot)
    {
        // A torn/unpublished commit is ignored; a valid-CRC inconsistent commit is fatal.
        if (memory.Read32(p + 8) != Crc(p, P, true))
            return true;
        if (memory.Read32(p) != CpxArena.Magic || memory.Read16(p + 6) != 4 || !(((ulong)(GetWide(p + 16))) == ((ulong)(Commit(slot)))) || memory.Read16(p + 4) != 10 || !(((ulong)(GetWide(p + 24))) == 0) || !IdentityMatches(p) || memory.Read16(p + 52) != 40)
            return false;
        ulong tx = GetWide(p + 80);
        if (!(((ulong)(tx)) == ((ulong)(GetWide(p + 32)))) || !(((ulong)(Commit(tx))) == ((ulong)(Commit(slot)))))
            return false;
        if ((tx) <= (RW(Generation)))
            return true;
        if (!Scalar64.Add(RW(Generation), ((ulong)(1)), out var next) || !(((ulong)(tx)) == ((ulong)(next))) || !(((ulong)(GetWide(p + 88))) == ((ulong)(RW(Generation)))) || !(((ulong)(RW(Candidate))) == 0) || memory.Read32(p + 96) == 0 || memory.Read32(p + 96) > 64)
            return false;
        WW(Candidate, tx);
        memory.Write32(Arena + (Count), memory.Read32(p + 96));
        memory.Write32(Arena + (Digest), memory.Read32(p + 100));
        return true;
    }

    private bool ImageValid(uint p, ulong tx, uint index)
    {
        uint d = Index + index * 32;
        return ValidPage(p, GetWide(d)) && memory.Read16(p + 4) == memory.Read16(d + 24) && (((ulong)(GetWide(p + 24))) == ((ulong)(GetWide(d + 8)))) && (((ulong)(GetWide(p + 32))) == ((ulong)(tx))) && memory.Read32(p + 8) == memory.Read32(d + 20) && PayloadValid(p);
    }

    private bool PayloadValid(uint p)
    {
        uint type = memory.Read16(p + 4), count = memory.Read16(p + 48), payload;
        switch (type)
        {
            case 3:
                return ValidStateLabel(p);
            case 4:
                payload = 520;
                break;
            case 6:
                payload = 80;
                break;
            case 7:
                payload = 512;
                break;
            case 11:
                payload = count * 256;
                break;
            case 12:
            case 16:
                payload = count * 16;
                break;
            case 13:
                payload = count * 128;
                break;
            case 14:
            case 15:
                payload = count * 24;
                break;
            case 17:
            case 19:
                if (memory.Read16(p + 64) == 0 || memory.Read16(p + 64) > 768 || memory.Read16(p + 66) != 0)
                    return false;
                payload = 4 + memory.Read16(p + 64);
                break;
            case 18:
                return AttributePayloadValid(p);
            default:
                return false;
        }

        return payload <= P - 64 && memory.Read16(p + 52) == payload;
    }

    private bool TargetValid(ulong address, uint type, ulong owner)
    {
        if (!PageAddress(address))
            return false;
        if (type == 3)
            return (((ulong)(owner)) == 0) && (((ulong)(address)) == ((ulong)(Anchor + 3 * Q)));
        if (type == 4)
            return (((ulong)(owner)) == 0) && (((ulong)(address)) == ((ulong)(Anchor + 8 * Q)));
        if (type == 6 || type == 7)
        {
            if ((((uint)((ulong)(owner))) & 65535) != 0 || (owner) >= (RW(Units)))
                return false;
            Scalar64.Subtract(RW(Units), owner, out var remaining);
            if (!(((ulong)(owner)) == 0) && ((uint)(((ulong)(remaining)) >> 32)) == 0 && ((uint)((ulong)(remaining))) < 65536 && ((uint)((ulong)(remaining))) < 18 * Q)
                return false;
            ulong descriptor = (((ulong)(owner)) == 0) ? ((ulong)(131072u >> (int)memory.Read32(Arena + (UnitShift)))) : owner;
            if (type == 6)
                return (((ulong)(address)) == ((ulong)(descriptor)));
            for (uint n = 1; n <= 16; n++)
                if ((((ulong)(address)) == ((ulong)(descriptor + n * Q))))
                    return true;
            return false;
        }

        return type >= 11 && type <= 19 && ((type is 11 or 12) ? (((ulong)(owner)) == 0) : !(((ulong)(owner)) == 0)) && Allocatable(address);
    }
}
