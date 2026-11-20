namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    private const uint RecoveryOwnership = 500, RecoveryBitmapReused = 508; // bit 0: owned attribute; bit 1: state image; bit 2: OWN1
    private StepResult RecoveryStep()
    {
        uint cpxArenaBase = Arena;
        uint p = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(5)) * 4))), phase = memory.Read32(cpxArenaBase + (Phase));
        ulong tx = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Candidate)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Candidate)) + 4))))));
        switch (phase)
        {
            case 0:
                // The two checkpoints are adjacent. Keep their independent CRC
                // and generation decisions while sharing one physical request.
                return Issue(IoKind.Read, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + (memory.Read32(cpxArenaBase + (GeometryQ))), (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(0)) * 4))), 2 * (memory.Read32(cpxArenaBase + (PageBytes))), 1);
            case 1:
                memory.Write32(cpxArenaBase + (ValidOne), CheckpointValid((memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(0)) * 4))), (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + (memory.Read32(cpxArenaBase + (GeometryQ)))) ? 1u : 0u);
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(CheckpointOne);
                ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)((memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(0)) * 4))) + 80))))) << 32 | ((ulong)(memory.Read32(((uint)((memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(0)) * 4))) + 80)) + 4)))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write32(cpxArenaBase + (Phase), 2);
                return StepResult.Pending;
            case 2:
                memory.Write32(cpxArenaBase + (ValidTwo), CheckpointValid((memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(1)) * 4))), (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + 2 * (memory.Read32(cpxArenaBase + (GeometryQ)))) ? 1u : 0u);
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(CheckpointTwo);
                ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)((memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(1)) * 4))) + 80))))) << 32 | ((ulong)(memory.Read32(((uint)((memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(1)) * 4))) + 80)) + 4)))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                if (memory.Read32(cpxArenaBase + (ValidOne)) == 0 && memory.Read32(cpxArenaBase + (ValidTwo)) == 0)
                    return Fail(CpxError.Corrupt);
                ulong a = memory.Read32(cpxArenaBase + (ValidOne)) != 0 ? ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(CheckpointOne)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(CheckpointOne)) + 4)))))) : default, b = memory.Read32(cpxArenaBase + (ValidTwo)) != 0 ? ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(CheckpointTwo)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(CheckpointTwo)) + 4)))))) : default;
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(Generation);
                ulong cpxWideValue = (a) >= (b) ? a : b;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

            {
                uint cpxWideAddress = cpxArenaBase + (uint)(Candidate);
                ulong cpxWideValue = default;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write32(cpxArenaBase + (Count), 0);
                return Issue(IoKind.Read, Commit(((ulong)(1))), p, (memory.Read32(cpxArenaBase + (PageBytes))), 3);
            case 3:
                if (!InspectCommit(p, ((ulong)(1))))
                    return Fail(CpxError.Corrupt);
                return Issue(IoKind.Read, Commit(((ulong)(2))), p, (memory.Read32(cpxArenaBase + (PageBytes))), 4);
            case 4:
                if (!InspectCommit(p, ((ulong)(2))))
                    return Fail(CpxError.Corrupt);
                tx = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Candidate)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Candidate)) + 4))))));
                if ((((ulong)(tx)) == 0))
                {
                    memory.Write32(cpxArenaBase + (Phase), 20);
                    return StepResult.Pending;
                }

                if (memory.Read32(cpxArenaBase + (ReadOnlyAt)) != 0)
                    return Fail(CpxError.ReadOnly);
                return Issue(IoKind.Read, Journal(tx), p, (memory.Read32(cpxArenaBase + (PageBytes))), 5);
            case 5:
                if (!Control(p, 8, Journal(tx)) || !IdentityMatches(p) || !(((ulong)(((((ulong)(memory.Read32(((uint)(p + 32))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 32)) + 4))))))) == ((ulong)(tx))) || !(((ulong)(((((ulong)(memory.Read32(((uint)(p + 80))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 80)) + 4))))))) == ((ulong)(tx))) || !(((ulong)(((((ulong)(memory.Read32(((uint)(p + 88))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 88)) + 4))))))) == ((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)) + 4))))))))) || memory.Read32(p + 96) != memory.Read32(cpxArenaBase + (Count)) || memory.Read32(p + 100) != memory.Read32(cpxArenaBase + (Digest)) || memory.Read16(p + 52) != 40)
                    return Fail(CpxError.Corrupt);
                Clear((memory.Read32(cpxArenaBase + (GeometryIndex))), 2048);
                memory.Write32(cpxArenaBase + (Cursor), 0);
                return Issue(IoKind.Read, Descriptor(tx, 0), p, (memory.Read32(cpxArenaBase + (PageBytes))), 6);
            case 6:
                uint page = memory.Read32(cpxArenaBase + (Cursor)), first = page * (((memory.Read32(cpxArenaBase + (PageBytes))) - 64) >> 5), take = first < memory.Read32(cpxArenaBase + (Count)) ? memory.Read32(cpxArenaBase + (Count)) - first : 0;
                if (take > (((memory.Read32(cpxArenaBase + (PageBytes))) - 64) >> 5))
                    take = ((memory.Read32(cpxArenaBase + (PageBytes))) - 64) >> 5;
                if (!Control(p, 9, Descriptor(tx, page)) || !(((ulong)(((((ulong)(memory.Read32(((uint)(p + 32))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 32)) + 4))))))) == ((ulong)(tx))) || memory.Read16(p + 48) != take || memory.Read16(p + 52) != take * 32)
                    return Fail(CpxError.Corrupt);
                Copy((memory.Read32(cpxArenaBase + (GeometryIndex))) + first * 32, p + 64, take * 32);
                if (page < 3)
                {
                    memory.Write32(cpxArenaBase + (Cursor), page + 1);
                    return Issue(IoKind.Read, Descriptor(tx, page + 1), p, (memory.Read32(cpxArenaBase + (PageBytes))), 6);
                }

                memory.Write32(cpxArenaBase + (Phase), 7);
                return StepResult.Pending;
            case 7:
                if (Crc((memory.Read32(cpxArenaBase + (GeometryIndex))), memory.Read32(cpxArenaBase + (Count)) * 32, false) != memory.Read32(cpxArenaBase + (Digest)))
                    return Fail(CpxError.Corrupt);
                memory.Write32(cpxArenaBase + (RecoveryOwnership), 0);
                memory.Write32(cpxArenaBase + (Cursor), 0);
                memory.Write32(cpxArenaBase + (Phase), 8);
                return StepResult.Pending;
            case 8:
                uint index = memory.Read32(cpxArenaBase + (Cursor)), d = (memory.Read32(cpxArenaBase + (GeometryIndex))) + index * 32;
                ulong home = ((((ulong)(memory.Read32(((uint)(d))))) << 32 | ((ulong)(memory.Read32(((uint)(d)) + 4)))));
                if (!TargetValid(home, memory.Read16(d + 24), ((((ulong)(memory.Read32(((uint)(d + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(d + 8)) + 4)))))) || memory.Read32(d + 16) != index || memory.Read16(d + 26) != 0 || memory.Read32(d + 28) != 0)
                    return Fail(CpxError.Corrupt);
                for (uint n = 0; n < index; n++)
                    if ((((ulong)(((((ulong)(memory.Read32(((uint)((memory.Read32(cpxArenaBase + (GeometryIndex))) + n * 32))))) << 32 | ((ulong)(memory.Read32(((uint)((memory.Read32(cpxArenaBase + (GeometryIndex))) + n * 32)) + 4))))))) == ((ulong)(home))))
                        return Fail(CpxError.Corrupt);
                return Issue(IoKind.Read, Image(tx, index), p, (memory.Read32(cpxArenaBase + (PageBytes))), 9);
            case 9:
                if (!ImageValid(p, tx, memory.Read32(cpxArenaBase + (Cursor))))
                    return Fail(CpxError.Corrupt);
                if (memory.Read16(p + 4) == 18 && memory.Read16(p + 64) == 2)
                    memory.Write32(cpxArenaBase + (RecoveryOwnership), memory.Read32(cpxArenaBase + (RecoveryOwnership)) | 1);
                if (memory.Read16(p + 4) == 3)
                    memory.Write32(cpxArenaBase + (RecoveryOwnership), memory.Read32(cpxArenaBase + (RecoveryOwnership)) | 2 | (memory.Read16(p + 52) == 136 ? 4u : 0u));
                if (memory.Read32(cpxArenaBase + (Cursor)) + 1 < memory.Read32(cpxArenaBase + (Count)))
                {
                    memory.Write32(cpxArenaBase + (Cursor), memory.Read32(cpxArenaBase + (Cursor)) + 1);
                    memory.Write32(cpxArenaBase + (Phase), 8);
                    return StepResult.Pending;
                }

                if ((memory.Read32(cpxArenaBase + (RecoveryOwnership)) & 1) != 0)
                {
                    if ((memory.Read32(cpxArenaBase + (RecoveryOwnership)) & 2) == 0)
                        return Issue(IoKind.Read, StatePageAddress, p, (memory.Read32(cpxArenaBase + (PageBytes))), 14);
                    if ((memory.Read32(cpxArenaBase + (RecoveryOwnership)) & 4) == 0)
                        return Fail(CpxError.Corrupt);
                }

                memory.Write32(cpxArenaBase + (Cursor), 0);
                memory.Write32(cpxArenaBase + (Phase), 10);
                return StepResult.Pending;
            case 10:
                return Issue(IoKind.Read, Image(tx, memory.Read32(cpxArenaBase + (Cursor))), p, (memory.Read32(cpxArenaBase + (PageBytes))), 11);
            case 11:
                if (!ImageValid(p, tx, memory.Read32(cpxArenaBase + (Cursor))))
                    return Fail(CpxError.Corrupt);
                return Issue(IoKind.Write, ((((ulong)(memory.Read32(((uint)((memory.Read32(cpxArenaBase + (GeometryIndex))) + memory.Read32(cpxArenaBase + (Cursor)) * 32))))) << 32 | ((ulong)(memory.Read32(((uint)((memory.Read32(cpxArenaBase + (GeometryIndex))) + memory.Read32(cpxArenaBase + (Cursor)) * 32)) + 4))))), p, (memory.Read32(cpxArenaBase + (PageBytes))), 12);
            case 12:
                if (memory.Read32(cpxArenaBase + (Cursor)) + 1 < memory.Read32(cpxArenaBase + (Count)))
                {
                    memory.Write32(cpxArenaBase + (Cursor), memory.Read32(cpxArenaBase + (Cursor)) + 1);
                    memory.Write32(cpxArenaBase + (Phase), 10);
                    return StepResult.Pending;
                }

                return Issue(IoKind.Flush, default, 0, 0, 13);
            case 13:
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(Generation);
                ulong cpxWideValue = tx;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write32(cpxArenaBase + (Phase), 20);
                return StepResult.Pending;
            case 14:
                if (!Control(p, 3, StatePageAddress) || !ValidStateLabel(p) || memory.Read16(p + 52) != 136 || (((((ulong)(memory.Read32(((uint)(p + 32))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 32)) + 4)))))) > (((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)) + 4))))))))
                    return Fail(CpxError.Corrupt);
                memory.Write32(cpxArenaBase + (Cursor), 0);
                memory.Write32(cpxArenaBase + (Phase), 10);
                return StepResult.Pending;
            case 20:
                if (CleanAlternatingCheckpoints(tx) || memory.Read32(cpxArenaBase + (ReadOnlyAt)) != 0 || ((((ulong)(tx)) == 0) && memory.Read32(cpxArenaBase + (ValidOne)) != 0 && (((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(CheckpointOne)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(CheckpointOne)) + 4)))))))) == ((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)) + 4)))))))))))
                {
                    memory.Write32(cpxArenaBase + (Phase), 22);
                    return StepResult.Pending;
                }

                MakeCheckpoint(p, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + (memory.Read32(cpxArenaBase + (GeometryQ))));
                return Issue(IoKind.Write, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + (memory.Read32(cpxArenaBase + (GeometryQ))), p, (memory.Read32(cpxArenaBase + (PageBytes))), 21);
            case 21:
                return Issue(IoKind.Flush, default, 0, 0, 22);
            case 22:
                if (CleanAlternatingCheckpoints(tx) || memory.Read32(cpxArenaBase + (ReadOnlyAt)) != 0 || ((((ulong)(tx)) == 0) && memory.Read32(cpxArenaBase + (ValidTwo)) != 0 && (((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(CheckpointTwo)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(CheckpointTwo)) + 4)))))))) == ((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)) + 4)))))))))))
                {
                    memory.Write32(cpxArenaBase + (Phase), 24);
                    return StepResult.Pending;
                }

                MakeCheckpoint(p, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + 2 * (memory.Read32(cpxArenaBase + (GeometryQ))));
                return Issue(IoKind.Write, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + 2 * (memory.Read32(cpxArenaBase + (GeometryQ))), p, (memory.Read32(cpxArenaBase + (PageBytes))), 23);
            case 23:
                return Issue(IoKind.Flush, default, 0, 0, 24);
            case 24:
                return Issue(IoKind.Read, CharsetAddress, p, (memory.Read32(cpxArenaBase + (PageBytes))), 25);
            case 25:
                if (!Control(p, 5, CharsetAddress) || memory.Read32(p + 64) != 1 || memory.Read16(p + 52) != 4)
                    return Fail(CpxError.Corrupt);
                return ReadStaged(StateAddress, p, 26);
            case 26:
                if (!Control(p, 3, StateAddress) || (((((ulong)(memory.Read32(((uint)(p + 64))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 64)) + 4)))))) < (((ulong)(2))) || (((ulong)(((((ulong)(memory.Read32(((uint)(p + 72))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 72)) + 4))))))) == 0) || !Allocatable(((((ulong)(memory.Read32(((uint)(p + 80))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 80)) + 4)))))) || !ValidStateLabel(p))
                    return Fail(CpxError.Corrupt);
                CacheStateLabel(p);
                return ReadStaged(Anchor + 8 * Q, Page(3), 27);
            case 27:
                uint reserve = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(3)) * 4)));
                if (!Control(reserve, 4, (((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + 8 * (memory.Read32(cpxArenaBase + (GeometryQ)))) || memory.Read16(reserve + 64) > ReserveCapacity || memory.Read16(reserve + 52) != ReservePayloadBytes)
                    return Fail(CpxError.Corrupt);
                // Journal replay no longer needs its descriptor index. Reuse
                // those 2 KiB as 128 address buckets for at most 64 reserve
                // entries, rather than comparing each entry to every prefix.
                uint reserveTable = (memory.Read32(cpxArenaBase + (GeometryIndex)));
                for (uint bucket = 0; bucket < 128; bucket++)
                    memory.Write32(reserveTable + bucket * 16 + 8, 0);
                memory.Write32(cpxArenaBase + (Count), memory.Read16(reserve + 64));
                memory.Write32(cpxArenaBase + (Cursor), 0);
                memory.Write32(cpxArenaBase + (Phase), 28);
                return StepResult.Pending;
            case 28:
                return RecoverReserveBatch(false);
            case 29:
                return RecoverReserveBatch(true);
            case 30:
                if (DirectInodes) return LoadObject(1, 32);
                return Call(CpxOperation.PruneObjects, 31);
            case 32:
                if (R(SourcePage) == 0 || memory.Read8(ObjectRecord + 84) != 2 || GetWide(ObjectRecord) != 1)
                    return Fail(CpxError.Corrupt);
                return Done();
            case 31:
                return Done();
            default:
                return Fail(CpxError.Corrupt);
        }
    }

    private ulong Bitmap(ulong at)
    {
        uint cpxArenaBase = Arena;
        ulong group = (((ulong)(((uint)(((ulong)(at)) >> 32)))) << 32 | ((ulong)(((uint)((ulong)(at))) & 0xffff0000)));
        return ((((ulong)(group)) == 0) ? ((ulong)(131072u >> (int)memory.Read32(cpxArenaBase + (UnitShift)))) : group) + (1 + ((((uint)((ulong)(at))) & 65535) >> (int)BitmapWindowShift)) * MetadataSpan;
    }

    private bool ControlBitmap(uint p, ulong at, ulong owner) => ValidPage(p, Bitmap(at)) && memory.Read16(p + 4) == 7 && (((ulong)(((((ulong)(memory.Read32(((uint)(p + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 24)) + 4))))))) == ((ulong)(owner))) && memory.Read32(p + 12) == ((((uint)((ulong)(at))) & 65535) >> (int)BitmapWindowShift) && memory.Read16(p + 52) == BitmapPayloadBytes;
    private bool CheckpointValid(uint p, ulong at) => Control(p, 2, at) && IdentityMatches(p) && (((ulong)(((((ulong)(memory.Read32(((uint)(p + 32))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 32)) + 4))))))) == ((ulong)(((((ulong)(memory.Read32(((uint)(p + 80))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 80)) + 4)))))))) && memory.Read16(p + 52) == 24;
    private void MakeCheckpoint(uint p, ulong at)
    {
        uint cpxArenaBase = Arena;
        Header(p, 2, at, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)) + 4)))))));
        {
            uint cpxWideAddress = (uint)(p + 72);
            ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Identity)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Identity)) + 4))))));
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        {
            uint cpxWideAddress = (uint)(p + 80);
            ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)) + 4))))));
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        Seal(p, 24);
    }

    private bool InspectCommit(uint p, ulong slot)
    {
        uint cpxArenaBase = Arena;
        // A torn/unpublished commit is ignored; a valid-CRC inconsistent commit is fatal.
        if (memory.Read32(p + 8) != Crc(p, (memory.Read32(cpxArenaBase + (PageBytes))), true))
            return true;
        if (memory.Read32(p) != CpxArena.Magic || memory.Read16(p + 6) != 4 || !(((ulong)(((((ulong)(memory.Read32(((uint)(p + 16))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 16)) + 4))))))) == ((ulong)(Commit(slot)))) || memory.Read16(p + 4) != 10 || !(((ulong)(((((ulong)(memory.Read32(((uint)(p + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 24)) + 4))))))) == 0) || !IdentityMatches(p) || memory.Read16(p + 52) != 40)
            return false;
        ulong tx = ((((ulong)(memory.Read32(((uint)(p + 80))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 80)) + 4)))));
        if (!(((ulong)(tx)) == ((ulong)(((((ulong)(memory.Read32(((uint)(p + 32))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 32)) + 4)))))))) || !(((ulong)(Commit(tx))) == ((ulong)(Commit(slot)))))
            return false;
        if ((tx) <= (((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)) + 4))))))))
            return true;
        if (!Scalar64.Add(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)) + 4)))))), ((ulong)(1)), out var next) || !(((ulong)(tx)) == ((ulong)(next))) || !(((ulong)(((((ulong)(memory.Read32(((uint)(p + 88))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 88)) + 4))))))) == ((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)) + 4))))))))) || !(((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Candidate)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Candidate)) + 4)))))))) == 0) || memory.Read32(p + 96) == 0 || memory.Read32(p + 96) > 64)
            return false;
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(Candidate);
            ulong cpxWideValue = tx;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(cpxArenaBase + (Count), memory.Read32(p + 96));
        memory.Write32(cpxArenaBase + (Digest), memory.Read32(p + 100));
        return true;
    }

    private bool ImageValid(uint p, ulong tx, uint index)
    {
        uint d = (memory.Read32(Arena + (GeometryIndex))) + index * 32;
        return ValidPage(p, ((((ulong)(memory.Read32(((uint)(d))))) << 32 | ((ulong)(memory.Read32(((uint)(d)) + 4)))))) && memory.Read16(p + 4) == memory.Read16(d + 24) && (((ulong)(((((ulong)(memory.Read32(((uint)(p + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 24)) + 4))))))) == ((ulong)(((((ulong)(memory.Read32(((uint)(d + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(d + 8)) + 4)))))))) && (((ulong)(((((ulong)(memory.Read32(((uint)(p + 32))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 32)) + 4))))))) == ((ulong)(tx))) && memory.Read32(p + 8) == memory.Read32(d + 20) && PayloadValid(p);
    }

    private bool PayloadValid(uint p)
    {
        uint type = memory.Read16(p + 4), count = memory.Read16(p + 48), payload;
        switch (type)
        {
            case 3:
                return ValidStateLabel(p);
            case 4:
                payload = ReservePayloadBytes;
                break;
            case 6:
                payload = GroupPayloadBytes;
                break;
            case 7:
                payload = BitmapPayloadBytes;
                break;
            case 23:
                if (!DirectInodes || count != 1 || memory.Read16(p + 50) != 0 || GetWide(p + 24) != GetWide(p + 64)) return false;
                payload = 256;
                break;
            case 24:
                return DirectInodes && count == 0 && memory.Read16(p + 52) == 0;
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
                return BlobHeadShape(p);
            case 22:
                return BlobTailShape(p);
            case 18:
                return AttributePayloadValid(p);
            default:
                return false;
        }

        return payload <= (memory.Read32(Arena + (PageBytes))) - 64 && memory.Read16(p + 52) == payload;
    }

    private bool TargetValid(ulong address, uint type, ulong owner)
    {
        uint cpxArenaBase = Arena;
        if (!PageAddress(address))
            return false;
        if (type == 3)
            return (((ulong)(owner)) == 0) && (((ulong)(address)) == ((ulong)(StateAddress)));
        if (type == 4)
            return (((ulong)(owner)) == 0) && (((ulong)(address)) == ((ulong)((((ulong)(memory.Read32(cpxArenaBase + (GeometryAnchor))))) + 8 * (memory.Read32(cpxArenaBase + (GeometryQ))))));
        if (type == 6 || type == 7)
        {
            if ((((uint)((ulong)(owner))) & 65535) != 0 || (owner) >= (((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Units)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Units)) + 4))))))))
                return false;
            Scalar64.Subtract(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Units)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Units)) + 4)))))), owner, out var remaining);
            if (!(((ulong)(owner)) == 0) && ((uint)(((ulong)(remaining)) >> 32)) == 0 && ((uint)((ulong)(remaining))) < 65536 && ((uint)((ulong)(remaining))) < (GroupMetadataPages + 1) * MetadataSpan)
                return false;
            ulong descriptor = (((ulong)(owner)) == 0) ? ((ulong)(131072u >> (int)memory.Read32(cpxArenaBase + (UnitShift)))) : owner;
            if (type == 6)
                return (((ulong)(address)) == ((ulong)(descriptor)));
            for (uint n = 1; n <= GroupMapCount; n++)
                if ((((ulong)(address)) == ((ulong)(descriptor + n * MetadataSpan))))
                    return true;
            return false;
        }

        if (SmallMetadata && type == 22) return owner != 0 && Allocatable(address);
        if (DirectInodes && type is 23 or 24) return owner != 0 && InodeAddress(owner, out var inode) && inode == address;
        return type >= 11 && type <= 19 && ((type is 11 or 12) ? (((ulong)(owner)) == 0) : !(((ulong)(owner)) == 0)) && Allocatable(address);
    }
}
