namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    private const uint CleanupId = 13400, CleanupKeep = 13408, CleanupTake = 13416, CleanupRoot = 13424, CleanupUnlink = 13432, CleanupTree = 13436;
    private uint CleanupObject => Arena + 13500;
    private uint CleanupExtent => Arena + 13756;

    private StepResult ReclaimStep()
    {
        uint p = Page(3), obj = CleanupObject, extent = CleanupExtent;
        switch (memory.Read32(Arena + (Phase)))
        {
            case 0:
                return ReadStaged(Anchor + 3 * Q, p, 1);
            case 1:
                if (!Control(p, 3, Anchor + 3 * Q) || !ReadableGeneration(p))
                    return Fail(CpxError.Corrupt);
                WW(CleanupId, GetWide(p + 88));
                if ((((ulong)(RW(CleanupId))) == 0))
                    return Done();
                return LoadObject(RW(CleanupId), 2);
            case 2:
                if (memory.Read32(Arena + (SourcePage)) == 0)
                    return Fail(CpxError.Corrupt);
                Copy(obj, ObjectRecord, 256);
                uint flag = memory.Read16(obj + 86), links = memory.Read32(obj + 76);
                if (flag is < 1 or > 2 || flag == 1 && links != 0 || flag == 2 && (links == 0 || memory.Read8(obj + 84) != 1))
                    return Fail(CpxError.Corrupt);
                memory.Write32(Arena + (CleanupUnlink), flag == 1 ? 1u : 0u);
                ulong size = GetWide(obj + 8), keep = Scalar64.Right(size, (int)memory.Read32(Arena + (UnitShift)));
                if ((((uint)((ulong)(size))) & ((1u << (int)memory.Read32(Arena + (UnitShift))) - 1)) != 0)
                    keep += 1;
                WW(CleanupKeep, flag == 1 ? default : keep);
                byte storage = memory.Read8(obj + 85);
                if (memory.Read8(obj + 84) != 1 || storage == 0)
                {
                    memory.Write32(Arena + (Phase), 20);
                    return StepResult.Pending;
                }

                if (storage == 1)
                {
                    uint count = memory.Read16(obj + 90);
                    if (count > 6)
                        return Fail(CpxError.Corrupt);
                    if (count == 0)
                    {
                        memory.Write32(Arena + (Phase), 20);
                        return StepResult.Pending;
                    }

                    Copy(extent, obj + 96 + (count - 1) * 24, 24);
                    memory.Write32(Arena + (Phase), 4);
                    return StepResult.Pending;
                }

                if (storage != 2)
                    return Fail(CpxError.Corrupt);
                StartSeek(CpxTreeKind.Extent, GetWide(obj + 24), RW(CleanupId), (((ulong)(uint.MaxValue)) << 32 | ((ulong)(uint.MaxValue))), default, CpxSeek.Floor);
                return Call(CpxOperation.ReadTree, 3);
            case 3:
                if (!TreeFound)
                {
                    memory.Write32(Arena + (Phase), 20);
                    return StepResult.Pending;
                }

                Copy(extent, TreeRecord, 24);
                memory.Write32(Arena + (Phase), 4);
                return StepResult.Pending;
            case 4:
                uint length = memory.Read32(extent + 16);
                ulong physical = GetWide(extent + 8), logical = GetWide(extent);
                if (length == 0 || length > 65536 || !Scalar64.Add(logical, ((ulong)(length)), out var end) || !Scalar64.Add(physical, ((ulong)(length - 1)), out var last) || ((uint)(((ulong)(physical)) >> 32)) != ((uint)(((ulong)(last)) >> 32)) || (((uint)((ulong)(physical))) >> 16) != (((uint)((ulong)(last))) >> 16) || memory.Read32(extent + 20) > 1)
                    return Fail(CpxError.Corrupt);
                if ((end) <= (RW(CleanupKeep)))
                {
                    memory.Write32(Arena + (Phase), 20);
                    return StepResult.Pending;
                }

                uint take = (((uint)((ulong)(last))) & 4095) + 1;
                if (take > length)
                    take = length;
                if ((logical) < (RW(CleanupKeep)))
                {
                    Scalar64.Subtract(end, RW(CleanupKeep), out var excess);
                    if (((uint)(((ulong)(excess)) >> 32)) == 0 && take > ((uint)((ulong)(excess))))
                        take = ((uint)((ulong)(excess)));
                }

                memory.Write32(Arena + (CleanupTake), take);
                return BeginTx(9, 5);
            case 5:
                return ChangeAllocation(GetWide(extent + 8) + memory.Read32(extent + 16) - memory.Read32(Arena + (CleanupTake)), memory.Read32(Arena + (CleanupTake)), false, 6);
            case 6:
                uint left = memory.Read32(extent + 16) - memory.Read32(Arena + (CleanupTake));
                if (!AdjustWide(obj + 16, memory.Read32(Arena + (CleanupTake)), false))
                    return Fail(CpxError.Corrupt);
                if (memory.Read8(obj + 85) == 1)
                {
                    uint count = memory.Read16(obj + 90);
                    if (left == 0)
                    {
                        Clear(obj + 96 + (count - 1) * 24, 24);
                        memory.Write16(obj + 90, count - 1);
                    }
                    else
                        memory.Write32(obj + 96 + (count - 1) * 24 + 16, left);
                    return SaveObject(obj, 8);
                }

                memory.Write32(extent + 16, left);
                return MutateTree(CpxTreeKind.Extent, GetWide(obj + 24), RW(CleanupId), extent, left == 0 ? 2u : 1u, 7);
            case 7:
                return SaveObject(obj, 8);
            case 8:
                return Call(CpxOperation.CommitTransaction, 0);
            case 20:
                ulong tree = GetWide(obj + (memory.Read8(obj + 84) == 2 ? 32u : 24u));
                WW(CleanupRoot, tree);
                if ((((ulong)(tree)) == 0))
                {
                    memory.Write32(Arena + (Phase), 30);
                    return StepResult.Pending;
                }

                memory.Write32(Arena + (CleanupTree), memory.Read8(obj + 84) == 2 ? 1u : 2u);
                return BeginTx(27, 21);
            case 21:
                return PruneTree((CpxTreeKind)memory.Read32(Arena + (CleanupTree)), RW(CleanupRoot), RW(CleanupId), 22);
            case 22:
                if (memory.Read32(Arena + (PruneChanged)) == 0)
                {
                    if (memory.Read32(Arena + (CleanupUnlink)) != 0)
                        return Fail(CpxError.Corrupt);
                    return Call(CpxOperation.AbortTransaction, 30);
                }

                PutWide(obj + (memory.Read32(Arena + (CleanupTree)) == 1 ? 32u : 24u), RW(PruneRoot));
                return SaveObject(obj, 23);
            case 23:
                return Call(CpxOperation.CommitTransaction, 0);
            case 30:
                return BeginTx(9, 31);
            case 31:
                if (memory.Read32(Arena + (CleanupUnlink)) != 0 && !(((ulong)(GetWide(obj + 48))) == 0))
                    return ReleaseMetadata(GetWide(obj + 48), 32);
                memory.Write32(Arena + (Phase), 32);
                return StepResult.Pending;
            case 32:
                if (memory.Read32(Arena + (CleanupUnlink)) != 0 && memory.Read8(obj + 84) == 3 && memory.Read8(obj + 85) == 3)
                    return ReleaseMetadata(GetWide(obj + 96), 33);
                memory.Write32(Arena + (Phase), 33);
                return StepResult.Pending;
            case 33:
                return ReadStaged(Anchor + 3 * Q, p, 34);
            case 34:
                if (!Control(p, 3, Anchor + 3 * Q) || !(((ulong)(GetWide(p + 88))) == ((ulong)(RW(CleanupId)))))
                    return Fail(CpxError.Corrupt);
                WW(CleanupRoot, GetWide(p + 80));
                PutWide(p + 88, GetWide(obj + 40));
                return StagePage(p, Anchor + 3 * Q, 35);
            case 35:
                if (memory.Read32(Arena + (CleanupUnlink)) != 0)
                    return MutateTree(CpxTreeKind.Object, RW(CleanupRoot), default, obj, 2, 36);
                // A fully reclaimed extent tree must not strand an empty file
                // in a storage mode that requires a nonexistent tree to write.
                if ((((ulong)(GetWide(obj + 16))) == 0) && (((ulong)(GetWide(obj + 24))) == 0))
                {
                    ulong finalSize = GetWide(obj + 8);
                    bool inline = ((uint)(((ulong)(finalSize)) >> 32)) == 0 && ((uint)((ulong)(finalSize))) <= 160;
                    Clear(obj + 96, 160);
                    memory.Write8(obj + 85, inline ? (byte)0 : (byte)1);
                    memory.Write16(obj + 90, 0);
                    memory.Write16(obj + 88, inline ? ((uint)((ulong)(finalSize))) : 0);
                }

                PutWide(obj + 40, default);
                memory.Write16(obj + 86, 0);
                return SaveObject(obj, 36);
            case 36:
                return Call(CpxOperation.CommitTransaction, 37);
            case 37:
                if (memory.Read32(Arena + (CleanupUnlink)) != 0)
                    return Call(CpxOperation.PruneObjects, 0);
                memory.Write32(Arena + (Phase), 0);
                return StepResult.Pending;
            default:
                return Fail(CpxError.Corrupt);
        }
    }
}
