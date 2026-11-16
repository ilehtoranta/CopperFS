namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    private const uint CleanupId = 13400, CleanupKeep = 13408, CleanupTake = 13416, CleanupRoot = 13424, CleanupUnlink = 13432, CleanupTree = 13436;
    private uint CleanupObject => Arena + 13500;
    private uint CleanupExtent => Arena + 13756;

    private StepResult ReclaimStep()
    {
        uint cpxArenaBase = Arena;
        uint p = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(3)) * 4))), obj = (cpxArenaBase + 13500), extent = (cpxArenaBase + 13756);
        switch (memory.Read32(cpxArenaBase + (Phase)))
        {
            case 0:
                return ReadStaged(StateAddress, p, 1);
            case 1:
                if (!Control(p, 3, StateAddress) || !ReadableGeneration(p))
                    return Fail(CpxError.Corrupt);
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(CleanupId);
                ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)(p + 88))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 88)) + 4)))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                if ((((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(CleanupId)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(CleanupId)) + 4)))))))) == 0))
                    return Done();
                return LoadObject(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(CleanupId)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(CleanupId)) + 4)))))), 2);
            case 2:
                if (memory.Read32(cpxArenaBase + (SourcePage)) == 0)
                    return Fail(CpxError.Corrupt);
                Copy(obj, (cpxArenaBase + CpxArena.MountBytes), 256);
                uint flag = memory.Read16(obj + 86), links = memory.Read32(obj + 76);
                if (flag is < 1 or > 2 || flag == 1 && links != 0 || flag == 2 && (links == 0 || memory.Read8(obj + 84) != 1))
                    return Fail(CpxError.Corrupt);
                memory.Write32(cpxArenaBase + (CleanupUnlink), flag == 1 ? 1u : 0u);
                ulong size = ((((ulong)(memory.Read32(((uint)(obj + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(obj + 8)) + 4))))), keep = Scalar64.Right(size, (int)memory.Read32(cpxArenaBase + (UnitShift)));
                if ((((uint)((ulong)(size))) & ((1u << (int)memory.Read32(cpxArenaBase + (UnitShift))) - 1)) != 0)
                    keep += 1;
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(CleanupKeep);
                ulong cpxWideValue = flag == 1 ? default : keep;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                byte storage = memory.Read8(obj + 85);
                if (memory.Read8(obj + 84) != 1 || storage == 0)
                {
                    memory.Write32(cpxArenaBase + (Phase), 20);
                    return StepResult.Pending;
                }

                if (storage == 1)
                {
                    uint count = memory.Read16(obj + 90);
                    if (count > 6)
                        return Fail(CpxError.Corrupt);
                    if (count == 0)
                    {
                        memory.Write32(cpxArenaBase + (Phase), 20);
                        return StepResult.Pending;
                    }

                    Copy(extent, obj + 96 + (count - 1) * 24, 24);
                    memory.Write32(cpxArenaBase + (Phase), 4);
                    return StepResult.Pending;
                }

                if (storage != 2)
                    return Fail(CpxError.Corrupt);
                StartSeek(CpxTreeKind.Extent, ((((ulong)(memory.Read32(((uint)(obj + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(obj + 24)) + 4))))), ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(CleanupId)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(CleanupId)) + 4)))))), (((ulong)(uint.MaxValue)) << 32 | ((ulong)(uint.MaxValue))), default, CpxSeek.Floor);
                return Call(CpxOperation.ReadTree, 3);
            case 3:
                if (!(memory.Read32(cpxArenaBase + (SeekFound)) != 0))
                {
                    memory.Write32(cpxArenaBase + (Phase), 20);
                    return StepResult.Pending;
                }

                Copy(extent, ((cpxArenaBase + CpxArena.MountBytes) + 512), 24);
                memory.Write32(cpxArenaBase + (Phase), 4);
                return StepResult.Pending;
            case 4:
                uint length = memory.Read32(extent + 16);
                ulong physical = ((((ulong)(memory.Read32(((uint)(extent + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(extent + 8)) + 4))))), logical = ((((ulong)(memory.Read32(((uint)(extent))))) << 32 | ((ulong)(memory.Read32(((uint)(extent)) + 4)))));
                if (length == 0 || length > 65536 || !Scalar64.Add(logical, ((ulong)(length)), out var end) || !Scalar64.Add(physical, ((ulong)(length - 1)), out var last) || ((uint)(((ulong)(physical)) >> 32)) != ((uint)(((ulong)(last)) >> 32)) || (((uint)((ulong)(physical))) >> 16) != (((uint)((ulong)(last))) >> 16) || memory.Read32(extent + 20) > 1)
                    return Fail(CpxError.Corrupt);
                if ((end) <= (((((ulong)(memory.Read32(cpxArenaBase + (((uint)(CleanupKeep)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(CleanupKeep)) + 4))))))))
                {
                    memory.Write32(cpxArenaBase + (Phase), 20);
                    return StepResult.Pending;
                }

                uint take = (((uint)((ulong)(last))) & 4095) + 1;
                if (take > length)
                    take = length;
                if ((logical) < (((((ulong)(memory.Read32(cpxArenaBase + (((uint)(CleanupKeep)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(CleanupKeep)) + 4))))))))
                {
                    Scalar64.Subtract(end, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(CleanupKeep)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(CleanupKeep)) + 4)))))), out var excess);
                    if (((uint)(((ulong)(excess)) >> 32)) == 0 && take > ((uint)((ulong)(excess))))
                        take = ((uint)((ulong)(excess)));
                }

                memory.Write32(cpxArenaBase + (CleanupTake), take);
                return BeginTx(9, 5);
            case 5:
                return ChangeAllocation(((((ulong)(memory.Read32(((uint)(extent + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(extent + 8)) + 4))))) + memory.Read32(extent + 16) - memory.Read32(cpxArenaBase + (CleanupTake)), memory.Read32(cpxArenaBase + (CleanupTake)), false, 6);
            case 6:
                uint left = memory.Read32(extent + 16) - memory.Read32(cpxArenaBase + (CleanupTake));
                if (!AdjustWide(obj + 16, memory.Read32(cpxArenaBase + (CleanupTake)), false))
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
                return MutateTree(CpxTreeKind.Extent, ((((ulong)(memory.Read32(((uint)(obj + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(obj + 24)) + 4))))), ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(CleanupId)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(CleanupId)) + 4)))))), extent, left == 0 ? 2u : 1u, 7);
            case 7:
                return SaveObject(obj, 8);
            case 8:
                return Call(CpxOperation.CommitTransaction, 0);
            case 20:
                ulong tree = ((((ulong)(memory.Read32(((uint)(obj + (memory.Read8(obj + 84) == 2 ? 32u : 24u)))))) << 32 | ((ulong)(memory.Read32(((uint)(obj + (memory.Read8(obj + 84) == 2 ? 32u : 24u))) + 4)))));
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(CleanupRoot);
                ulong cpxWideValue = tree;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                if ((((ulong)(tree)) == 0))
                {
                    memory.Write32(cpxArenaBase + (Phase), 30);
                    return StepResult.Pending;
                }

                memory.Write32(cpxArenaBase + (CleanupTree), memory.Read8(obj + 84) == 2 ? 1u : 2u);
                return BeginTx(27, 21);
            case 21:
                return PruneTree((CpxTreeKind)memory.Read32(cpxArenaBase + (CleanupTree)), ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(CleanupRoot)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(CleanupRoot)) + 4)))))), ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(CleanupId)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(CleanupId)) + 4)))))), 22);
            case 22:
                if (memory.Read32(cpxArenaBase + (PruneChanged)) == 0)
                {
                    if (memory.Read32(cpxArenaBase + (CleanupUnlink)) != 0)
                        return Fail(CpxError.Corrupt);
                    return Call(CpxOperation.AbortTransaction, 30);
                }

            {
                uint cpxWideAddress = (uint)(obj + (memory.Read32(cpxArenaBase + (CleanupTree)) == 1 ? 32u : 24u));
                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(PruneRoot)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(PruneRoot)) + 4))))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                return SaveObject(obj, 23);
            case 23:
                return Call(CpxOperation.CommitTransaction, 0);
            case 30:
                return BeginTx(9, 31);
            case 31:
                if (memory.Read32(cpxArenaBase + (CleanupUnlink)) != 0 && !(((ulong)(((((ulong)(memory.Read32(((uint)(obj + 48))))) << 32 | ((ulong)(memory.Read32(((uint)(obj + 48)) + 4))))))) == 0))
                    return ReleaseMetadata(((((ulong)(memory.Read32(((uint)(obj + 48))))) << 32 | ((ulong)(memory.Read32(((uint)(obj + 48)) + 4))))), 32);
                memory.Write32(cpxArenaBase + (Phase), 32);
                return StepResult.Pending;
            case 32:
                if (memory.Read32(cpxArenaBase + (CleanupUnlink)) != 0 && memory.Read8(obj + 84) == 3 && memory.Read8(obj + 85) == 3)
                    return ReleaseMetadata(((((ulong)(memory.Read32(((uint)(obj + 96))))) << 32 | ((ulong)(memory.Read32(((uint)(obj + 96)) + 4))))), 33);
                memory.Write32(cpxArenaBase + (Phase), 33);
                return StepResult.Pending;
            case 33:
                return ReadStaged(StateAddress, p, 34);
            case 34:
                if (!Control(p, 3, StateAddress) || !(((ulong)(((((ulong)(memory.Read32(((uint)(p + 88))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 88)) + 4))))))) == ((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(CleanupId)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(CleanupId)) + 4))))))))))
                    return Fail(CpxError.Corrupt);
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(CleanupRoot);
                ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)(p + 80))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 80)) + 4)))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

            {
                uint cpxWideAddress = (uint)(p + 88);
                ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)(obj + 40))))) << 32 | ((ulong)(memory.Read32(((uint)(obj + 40)) + 4)))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                return StagePage(p, StateAddress, 35);
            case 35:
                if (memory.Read32(cpxArenaBase + (CleanupUnlink)) != 0)
                    return MutateTree(CpxTreeKind.Object, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(CleanupRoot)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(CleanupRoot)) + 4)))))), default, obj, 2, 36);
                // A fully reclaimed extent tree must not strand an empty file
                // in a storage mode that requires a nonexistent tree to write.
                if ((((ulong)(((((ulong)(memory.Read32(((uint)(obj + 16))))) << 32 | ((ulong)(memory.Read32(((uint)(obj + 16)) + 4))))))) == 0) && (((ulong)(((((ulong)(memory.Read32(((uint)(obj + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(obj + 24)) + 4))))))) == 0))
                {
                    ulong finalSize = ((((ulong)(memory.Read32(((uint)(obj + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(obj + 8)) + 4)))));
                    bool inline = ((uint)(((ulong)(finalSize)) >> 32)) == 0 && ((uint)((ulong)(finalSize))) <= 160;
                    Clear(obj + 96, 160);
                    memory.Write8(obj + 85, inline ? (byte)0 : (byte)1);
                    memory.Write16(obj + 90, 0);
                    memory.Write16(obj + 88, inline ? ((uint)((ulong)(finalSize))) : 0);
                }

            {
                uint cpxWideAddress = (uint)(obj + 40);
                ulong cpxWideValue = default;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write16(obj + 86, 0);
                return SaveObject(obj, 36);
            case 36:
                return Call(CpxOperation.CommitTransaction, 37);
            case 37:
                if (memory.Read32(cpxArenaBase + (CleanupUnlink)) != 0)
                    return Call(CpxOperation.PruneObjects, 0);
                memory.Write32(cpxArenaBase + (Phase), 0);
                return StepResult.Pending;
            default:
                return Fail(CpxError.Corrupt);
        }
    }
}