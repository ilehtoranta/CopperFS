namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    private const uint NsParent = 10600, NsDestination = 10608, NsLength = 10616, NsNewLength = 10620, NsDirectory = 10624, NsReplace = 10628, NsReplacing = 10632, NsResult = 10640, NsEntryId = 10648, NsRoot = 10656, NsAncestor = 10664, NsHops = 10672, NsNameScan = 10680, NsEncodedLength = 10684, NsHash = 10688, NsNameReturn = 10692, NsPublished = 10696;
    private uint NsParentRecord => Arena + 10800;
    private uint NsDestinationRecord => Arena + 11056;
    private uint NsObject => Arena + 11312;
    private uint NsVictim => Arena + 11568;
    private uint NsEntry => Arena + 11824;
    private uint NsOldEntry => Arena + 11952;
    private uint NsPrevious => Arena + 12080;
    private uint NsName => Arena + 12208;
    private uint NsNewName => Arena + 12464;
    private uint NsEncoded => Arena + 12720;

    public Wide MutatedObject
    {
        get
        {
            uint cpxArenaBase = Arena;
            return ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsResult)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsResult)) + 4))))));
        }
    }

    public bool NamespacePublished => memory.Read32(Arena + (NsPublished)) != 0;

    private bool ValidComponent(uint name, uint length, bool creating)
    {
        if (name == 0 || length == 0 || length > 255 || name > uint.MaxValue - length)
            return false;
        if (length <= 2 && memory.Read8(name) == 46 && (length == 1 || memory.Read8(name + 1) == 46))
            return false;
        for (uint n = 0; n < length; n++)
            if (memory.Read8(name + n)is 0 or 47 or 58)
                return false;
        if (creating && length == 19 && memory.Read8(name) == 95 && memory.Read8(name + 1) == 95 && FoldLatin(memory.Read8(name + 2)) == 117)
        {
            bool alias = true;
            for (uint n = 3; n < 19; n++)
            {
                uint c = FoldLatin(memory.Read8(name + n));
                if (!(c is >= 48 and <= 57 or >= 97 and <= 102))
                    alias = false;
            }

            if (alias)
                return false;
        }

        return true;
    }

    public CpxError BeginCreate(Wide parent, uint name, uint length, bool directory = false)
    {
        uint cpxArenaBase = Arena;
        if (!ValidComponent(name, length, true))
            return CpxError.InvalidArgument;
        CpxError error = StartOperation(CpxOperation.Create);
        if (error != CpxError.None)
            return error;
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(NsParent);
            ulong cpxWideValue = parent;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(cpxArenaBase + (NsLength), length);
        memory.Write32(cpxArenaBase + (NsDirectory), directory ? 1u : 0u);
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(NsResult);
            ulong cpxWideValue = default;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        Copy((cpxArenaBase + 12208), name, length);
        if ((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1))
            memory.Write32(cpxArenaBase + (Phase), 600);
        return error;
    }

    public CpxError BeginDelete(Wide parent, uint name, uint length)
    {
        uint cpxArenaBase = Arena;
        if (!ValidComponent(name, length, false))
            return CpxError.InvalidArgument;
        CpxError error = StartOperation(CpxOperation.Delete);
        if (error != CpxError.None)
            return error;
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(NsParent);
            ulong cpxWideValue = parent;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(cpxArenaBase + (NsLength), length);
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(NsResult);
            ulong cpxWideValue = default;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        Copy((cpxArenaBase + 12208), name, length);
        return error;
    }

    public CpxError BeginRename(Wide parent, uint name, uint length, Wide destination, uint newName, uint newLength, bool replace = false)
    {
        uint cpxArenaBase = Arena;
        if (!ValidComponent(name, length, false) || !ValidComponent(newName, newLength, true))
            return CpxError.InvalidArgument;
        CpxError error = StartOperation(CpxOperation.Rename);
        if (error != CpxError.None)
            return error;
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(NsParent);
            ulong cpxWideValue = parent;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        {
            uint cpxWideAddress = cpxArenaBase + (uint)(NsDestination);
            ulong cpxWideValue = destination;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(cpxArenaBase + (NsLength), length);
        memory.Write32(cpxArenaBase + (NsNewLength), newLength);
        memory.Write32(cpxArenaBase + (NsReplace), replace ? 1u : 0u);
        memory.Write32(cpxArenaBase + (NsReplacing), 0);
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(NsResult);
            ulong cpxWideValue = default;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        Copy((cpxArenaBase + 12208), name, length);
        Copy((cpxArenaBase + 12464), newName, newLength);
        if ((memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) && !replace && (((ulong)(parent)) == ((ulong)(destination))))
            memory.Write32(cpxArenaBase + (Phase), 610);
        return error;
    }

    private StepResult LookupName(ulong parent, uint name, uint length, uint next)
    {
        uint cpxArenaBase = Arena;
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(ObjectKey);
            ulong cpxWideValue = parent;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        memory.Write32(cpxArenaBase + (NameLength), length);
        memory.Write32(cpxArenaBase + (LookupFoundAt), 0);
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(LookupTarget);
            ulong cpxWideValue = default;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        Copy(((cpxArenaBase + CpxArena.MountBytes) + 1024), name, length);
        return Call(CpxOperation.Lookup, next);
    }

    private StepResult LoadObject(ulong id, uint next)
    {
        {
            uint cpxWideAddress = Arena + (uint)(ObjectKey);
            ulong cpxWideValue = id;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        return Call(CpxOperation.ReadObject, next);
    }

    private StepResult BuildEntry(uint next)
    {
        uint cpxArenaBase = Arena;
        memory.Write32(cpxArenaBase + (NsNameScan), 0);
        memory.Write32(cpxArenaBase + (NsEncodedLength), 0);
        memory.Write32(cpxArenaBase + (NsHash), uint.MaxValue);
        memory.Write32(cpxArenaBase + (NsNameReturn), next);
        memory.Write32(cpxArenaBase + (Phase), 80);
        return StepResult.Pending;
    }

    private uint HashByte(uint crc, uint value) => (crc >> 8) ^ memory.Read32((memory.Read32(Arena + (GeometryCrc))) + ((crc ^ value) & 255) * 4);
    private bool AdjustWide(uint address, uint count, bool add)
    {
        ulong result;
        bool ok = add ? Scalar64.Add(((((ulong)(memory.Read32(((uint)(address))))) << 32 | ((ulong)(memory.Read32(((uint)(address)) + 4))))), ((ulong)(count)), out result) : Scalar64.Subtract(((((ulong)(memory.Read32(((uint)(address))))) << 32 | ((ulong)(memory.Read32(((uint)(address)) + 4))))), ((ulong)(count)), out result);
        if (ok)
        {
            uint cpxWideAddress = (uint)(address);
            ulong cpxWideValue = result;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        return ok;
    }

    private StepResult NamespaceStep()
    {
        uint cpxArenaBase = Arena;
        uint phase = memory.Read32(cpxArenaBase + (Phase)), p = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(3)) * 4)));
        CpxOperation op = (CpxOperation)memory.Read32(cpxArenaBase + (OperationAt));
        if (phase is 600 or 601)
            return FastCreateStep();
        if (phase == 610)
        {
            if (TryFastRename())
                return Done();
            memory.Write32(cpxArenaBase + (Phase), 0);
            return StepResult.Pending;
        }

        switch (phase)
        {
            case 0:
                if (op == CpxOperation.Create && memory.Read32(cpxArenaBase + (NsDirectory)) == 3)
                    return Call(CpxOperation.EncodeSymlink, 100);
                return Call(CpxOperation.Reclaim, 1);
            case 1:
                return LookupName(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsParent)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsParent)) + 4)))))), (cpxArenaBase + 12208), memory.Read32(cpxArenaBase + (NsLength)), 2);
            case 2:
                Copy((cpxArenaBase + 10800), (cpxArenaBase + CpxArena.MountBytes), 256);
                if ((memory.Read32((cpxArenaBase + 10800) + 72) & 4) != 0)
                    return Reject(CpxError.Protected);
                if (op == CpxOperation.Create)
                {
                    if ((memory.Read32(cpxArenaBase + (LookupFoundAt)) != 0))
                        return Reject(CpxError.AlreadyExists);
                    return EnsureReserve(32, 10);
                }

                if (!(memory.Read32(cpxArenaBase + (LookupFoundAt)) != 0))
                    return Reject(CpxError.NotFound);
                Copy((cpxArenaBase + 11952), ((cpxArenaBase + CpxArena.MountBytes) + 512), 128);
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(NsResult);
                ulong cpxWideValue = LookupId;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                return LoadObject(LookupId, 3);
            case 3:
                if (memory.Read32(cpxArenaBase + (SourcePage)) == 0)
                    return Fail(CpxError.Corrupt);
                Copy((cpxArenaBase + 11312), (cpxArenaBase + CpxArena.MountBytes), 256);
                if ((memory.Read32((cpxArenaBase + 11312) + 72) & 1) != 0)
                    return Reject(CpxError.Protected);
                if (op == CpxOperation.Delete)
                {
                    if (memory.Read8((cpxArenaBase + 11312) + 84) == 2 && !(((ulong)(((((ulong)(memory.Read32(((uint)((cpxArenaBase + 11312) + 8))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 11312) + 8)) + 4))))))) == 0))
                        return Reject(CpxError.DirectoryNotEmpty);
                    return BeginTx(7, 30);
                }

                return LookupName(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsDestination)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsDestination)) + 4)))))), (cpxArenaBase + 12464), memory.Read32(cpxArenaBase + (NsNewLength)), 4);
            case 4:
                Copy((cpxArenaBase + 11056), (cpxArenaBase + CpxArena.MountBytes), 256);
                if ((memory.Read32((cpxArenaBase + 11056) + 72) & 4) != 0)
                    return Reject(CpxError.Protected);
                if ((memory.Read32(cpxArenaBase + (LookupFoundAt)) != 0) && !(((ulong)(((((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 512) + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 512) + 8)) + 4))))))) == ((ulong)(((((ulong)(memory.Read32(((uint)((cpxArenaBase + 11952) + 8))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 11952) + 8)) + 4)))))))))
                {
                    if (memory.Read32(cpxArenaBase + (NsReplace)) == 0)
                        return Reject(CpxError.AlreadyExists);
                    if ((((ulong)(LookupId)) == ((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsResult)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsResult)) + 4))))))))))
                        return Done();
                    Copy((cpxArenaBase + 12080), ((cpxArenaBase + CpxArena.MountBytes) + 512), 128);
                    memory.Write32(cpxArenaBase + (NsReplacing), 1);
                    return LoadObject(LookupId, 5);
                }

                memory.Write32(cpxArenaBase + (Phase), 6);
                return StepResult.Pending;
            case 5:
                if (memory.Read32(cpxArenaBase + (SourcePage)) == 0)
                    return Fail(CpxError.Corrupt);
                Copy((cpxArenaBase + 11568), (cpxArenaBase + CpxArena.MountBytes), 256);
                if ((memory.Read32((cpxArenaBase + 11568) + 72) & 1) != 0)
                    return Reject(CpxError.Protected);
                if (memory.Read8((cpxArenaBase + 11568) + 84) != memory.Read8((cpxArenaBase + 11312) + 84))
                    return Reject(CpxError.WrongType);
                if (memory.Read8((cpxArenaBase + 11568) + 84) == 2 && !(((ulong)(((((ulong)(memory.Read32(((uint)((cpxArenaBase + 11568) + 8))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 11568) + 8)) + 4))))))) == 0))
                    return Reject(CpxError.DirectoryNotEmpty);
                memory.Write32(cpxArenaBase + (Phase), 6);
                return StepResult.Pending;
            case 6:
                if (memory.Read8((cpxArenaBase + 11312) + 84) != 2)
                    return EnsureReserve(10, 40);
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(NsAncestor);
                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsDestination)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsDestination)) + 4))))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

            {
                uint cpxWideAddress = cpxArenaBase + (uint)(NsHops);
                ulong cpxWideValue = default;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write32(cpxArenaBase + (Phase), 7);
                return StepResult.Pending;
            case 7:
                if ((((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsAncestor)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsAncestor)) + 4)))))))) == ((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsResult)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsResult)) + 4))))))))))
                    return Reject(CpxError.InvalidArgument);
                if ((((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsAncestor)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsAncestor)) + 4)))))))) == 0))
                    return EnsureReserve(10, 40);
                return LoadObject(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsAncestor)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsAncestor)) + 4)))))), 8);
            case 8:
                if (memory.Read32(cpxArenaBase + (SourcePage)) == 0 || memory.Read8((cpxArenaBase + CpxArena.MountBytes) + 84) != 2)
                    return Fail(CpxError.Corrupt);
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(NsAncestor);
                ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)((cpxArenaBase + CpxArena.MountBytes) + 40))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + CpxArena.MountBytes) + 40)) + 4)))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

            {
                uint cpxWideAddress = cpxArenaBase + (uint)(NsHops);
                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsHops)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsHops)) + 4)))))) + 1;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                return ReadStaged(StateAddress, p, 9);
            case 9:
                if (!Control(p, 3, StateAddress) || (((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsHops)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsHops)) + 4))))))) >= (((((ulong)(memory.Read32(((uint)(p + 64))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 64)) + 4)))))))
                    return Fail(CpxError.Corrupt);
                memory.Write32(cpxArenaBase + (Phase), 7);
                return StepResult.Pending;
            case 10:
                return BeginTx(39, 11);
            case 11:
                return ReadStaged(StateAddress, p, 12);
            case 12:
                if (!Control(p, 3, StateAddress) || !ReadableGeneration(p))
                    return Fail(CpxError.Corrupt);
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(NsResult);
                ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)(p + 64))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 64)) + 4)))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

            {
                uint cpxWideAddress = cpxArenaBase + (uint)(NsEntryId);
                ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)(p + 72))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 72)) + 4)))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                if (!AdjustWide(p + 64, 1, true) || !AdjustWide(p + 72, 1, true) || !AdjustWide(p + 104, 1, true))
                    return Reject(CpxError.ResourceExhausted);
                Clear((cpxArenaBase + 11312), 256);
            {
                uint cpxWideAddress = (uint)((cpxArenaBase + 11312));
                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsResult)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsResult)) + 4))))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write32((cpxArenaBase + 11312) + 76, 1);
                memory.Write32((cpxArenaBase + 11312) + 80, 1);
                memory.Write8((cpxArenaBase + 11312) + 84, (byte)(memory.Read32(cpxArenaBase + (NsDirectory)) == 3 ? 3 : memory.Read32(cpxArenaBase + (NsDirectory)) != 0 ? 2 : 1));
                if (memory.Read32(cpxArenaBase + (NsDirectory)) == 1)
                {
                    uint cpxWideAddress = (uint)((cpxArenaBase + 11312) + 40);
                    ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsParent)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsParent)) + 4))))));
                    memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                    memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                }

                return StagePage(p, StateAddress, 13);
            case 13:
                if (memory.Read32(cpxArenaBase + (NsDirectory)) == 3)
                {
                    {
                        uint cpxWideAddress = (uint)((cpxArenaBase + 11312) + 8);
                        ulong cpxWideValue = ((ulong)(memory.Read32(cpxArenaBase + (LinkEncoded))));
                        memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                        memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                    }

                    if (memory.Read32(cpxArenaBase + (LinkEncoded)) > 160)
                        return TakeMetadata(102);
                    memory.Write16((cpxArenaBase + 11312) + 88, memory.Read32(cpxArenaBase + (LinkEncoded)));
                    Copy((cpxArenaBase + 11312) + 96, (cpxArenaBase + 15552), memory.Read32(cpxArenaBase + (LinkEncoded)));
                }

                return BuildEntry(14);
            case 14:
                return MutateTree(CpxTreeKind.Directory, ((((ulong)(memory.Read32(((uint)((cpxArenaBase + 10800) + 32))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 10800) + 32)) + 4))))), ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsParent)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsParent)) + 4)))))), (cpxArenaBase + 11824), 0, 15);
            case 15:
            {
                uint cpxWideAddress = (uint)((cpxArenaBase + 10800) + 32);
                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(MutationRoot)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(MutationRoot)) + 4))))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                if (!AdjustWide((cpxArenaBase + 10800) + 8, 1, true))
                    return Fail(CpxError.Corrupt);
                return SaveObject((cpxArenaBase + 10800), 16);
            case 16:
                return ReadStaged(StateAddress, p, 17);
            case 17:
                if (!Control(p, 3, StateAddress))
                    return Fail(CpxError.Corrupt);
                return MutateTree(CpxTreeKind.Object, ((((ulong)(memory.Read32(((uint)(p + 80))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 80)) + 4))))), default, (cpxArenaBase + 11312), 0, 18);
            case 18:
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(NsRoot);
                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(MutationRoot)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(MutationRoot)) + 4))))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                return ReadStaged(StateAddress, p, 19);
            case 19:
                if (!Control(p, 3, StateAddress))
                    return Fail(CpxError.Corrupt);
            {
                uint cpxWideAddress = (uint)(p + 80);
                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsRoot)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsRoot)) + 4))))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                return StagePage(p, StateAddress, 20);
            case 20:
                return Call(CpxOperation.CommitTransaction, 99);
            case 30:
                return MutateTree(CpxTreeKind.Directory, ((((ulong)(memory.Read32(((uint)((cpxArenaBase + 10800) + 32))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 10800) + 32)) + 4))))), ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsParent)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsParent)) + 4)))))), (cpxArenaBase + 11952), 2, 31);
            case 31:
                if (!AdjustWide((cpxArenaBase + 10800) + 8, 1, false))
                    return Fail(CpxError.Corrupt);
                return SaveObject((cpxArenaBase + 10800), 32);
            case 32:
                if (!(((ulong)(((((ulong)(memory.Read32(((uint)((cpxArenaBase + 11952) + 24))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 11952) + 24)) + 4))))))) == 0))
                    return ReleaseMetadata(((((ulong)(memory.Read32(((uint)((cpxArenaBase + 11952) + 24))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 11952) + 24)) + 4))))), 33);
                memory.Write32(cpxArenaBase + (Phase), 33);
                return StepResult.Pending;
            case 33:
                Copy((cpxArenaBase + 11568), (cpxArenaBase + 11312), 256);
                memory.Write32(cpxArenaBase + (Phase), 60);
                return StepResult.Pending;
            case 40:
                return BeginTx(30, 41);
            case 41:
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(NsEntryId);
                ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)((cpxArenaBase + 11952) + 8))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 11952) + 8)) + 4)))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                return BuildEntry(42);
            case 42:
                return MutateTree(CpxTreeKind.Directory, ((((ulong)(memory.Read32(((uint)((cpxArenaBase + 10800) + 32))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 10800) + 32)) + 4))))), ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsParent)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsParent)) + 4)))))), (cpxArenaBase + 11952), 2, 43);
            case 43:
                if (memory.Read32(cpxArenaBase + (NsReplacing)) != 0)
                    return MutateTree(CpxTreeKind.Directory, ((((ulong)(memory.Read32(((uint)((cpxArenaBase + 11056) + 32))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 11056) + 32)) + 4))))), ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsDestination)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsDestination)) + 4)))))), (cpxArenaBase + 12080), 2, 44);
                memory.Write32(cpxArenaBase + (Phase), 44);
                return StepResult.Pending;
            case 44:
                return MutateTree(CpxTreeKind.Directory, ((((ulong)(memory.Read32(((uint)((cpxArenaBase + 11056) + 32))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 11056) + 32)) + 4))))), ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsDestination)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsDestination)) + 4)))))), (cpxArenaBase + 11824), 0, 45);
            case 45:
            {
                uint cpxWideAddress = (uint)((cpxArenaBase + 11056) + 32);
                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(MutationRoot)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(MutationRoot)) + 4))))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                if (!(((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsParent)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsParent)) + 4)))))))) == ((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsDestination)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsDestination)) + 4))))))))))
                {
                    if (!AdjustWide((cpxArenaBase + 11056) + 8, 1, true))
                        return Fail(CpxError.Corrupt);
                }

                if (memory.Read32(cpxArenaBase + (NsReplacing)) != 0 && !AdjustWide((cpxArenaBase + 11056) + 8, 1, false))
                    return Fail(CpxError.Corrupt);
                return SaveObject((cpxArenaBase + 11056), 46);
            case 46:
                if ((((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsParent)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsParent)) + 4)))))))) == ((ulong)(((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsDestination)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsDestination)) + 4))))))))))
                {
                    memory.Write32(cpxArenaBase + (Phase), 48);
                    return StepResult.Pending;
                }

                if (!AdjustWide((cpxArenaBase + 10800) + 8, 1, false))
                    return Fail(CpxError.Corrupt);
                return SaveObject((cpxArenaBase + 10800), 47);
            case 47:
                if (memory.Read8((cpxArenaBase + 11312) + 84) == 2)
                {
                    {
                        uint cpxWideAddress = (uint)((cpxArenaBase + 11312) + 40);
                        ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsDestination)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsDestination)) + 4))))));
                        memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                        memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                    }

                    return SaveObject((cpxArenaBase + 11312), 48);
                }

                memory.Write32(cpxArenaBase + (Phase), 48);
                return StepResult.Pending;
            case 48:
                if (!(((ulong)(((((ulong)(memory.Read32(((uint)((cpxArenaBase + 11952) + 24))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 11952) + 24)) + 4))))))) == 0))
                    return ReleaseMetadata(((((ulong)(memory.Read32(((uint)((cpxArenaBase + 11952) + 24))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 11952) + 24)) + 4))))), 49);
                memory.Write32(cpxArenaBase + (Phase), 49);
                return StepResult.Pending;
            case 49:
                if (memory.Read32(cpxArenaBase + (NsReplacing)) == 0)
                    return Call(CpxOperation.CommitTransaction, 99);
                if (!(((ulong)(((((ulong)(memory.Read32(((uint)((cpxArenaBase + 12080) + 24))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 12080) + 24)) + 4))))))) == 0))
                    return ReleaseMetadata(((((ulong)(memory.Read32(((uint)((cpxArenaBase + 12080) + 24))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 12080) + 24)) + 4))))), 60);
                memory.Write32(cpxArenaBase + (Phase), 60);
                return StepResult.Pending;
            case 60:
                return ReadStaged(StateAddress, p, 61);
            case 61:
                if (!Control(p, 3, StateAddress) || memory.Read32((cpxArenaBase + 11568) + 76) == 0 || memory.Read16((cpxArenaBase + 11568) + 86) != 0)
                    return Fail(CpxError.Corrupt);
                uint links = memory.Read32((cpxArenaBase + 11568) + 76) - 1;
                memory.Write32((cpxArenaBase + 11568) + 76, links);
                if (links == 0)
                {
                    memory.Write16((cpxArenaBase + 11568) + 86, 1);
                    {
                        uint cpxWideAddress = (uint)((cpxArenaBase + 11568) + 40);
                        ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)(p + 88))))) << 32 | ((ulong)(memory.Read32(((uint)(p + 88)) + 4)))));
                        memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                        memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                    }

                    {
                        uint cpxWideAddress = (uint)(p + 88);
                        ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)((cpxArenaBase + 11568)))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + 11568))) + 4)))));
                        memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                        memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                    }
                }

                if (!AdjustWide(p + 104, 1, false))
                    return Fail(CpxError.Corrupt);
                return StagePage(p, StateAddress, 62);
            case 62:
                return SaveObject((cpxArenaBase + 11568), 63);
            case 63:
                return Call(CpxOperation.CommitTransaction, 64);
            case 64:
                memory.Write32(cpxArenaBase + (NsPublished), 1);
                return Call(CpxOperation.Reclaim, 99);
            case 80:
                uint name = op == CpxOperation.Rename ? (cpxArenaBase + 12464) : (cpxArenaBase + 12208), length = op == CpxOperation.Rename ? memory.Read32(cpxArenaBase + (NsNewLength)) : memory.Read32(cpxArenaBase + (NsLength));
                uint scan = memory.Read32(cpxArenaBase + (NsNameScan)), end = scan + 64, encoded = memory.Read32(cpxArenaBase + (NsEncodedLength)), hash = memory.Read32(cpxArenaBase + (NsHash));
                if (end > length)
                    end = length;
                for (; scan < end; scan++)
                {
                    uint c = memory.Read8(name + scan), fold = FoldLatin(c);
                    if (c < 128)
                        memory.Write8((cpxArenaBase + 12720) + encoded++, (byte)c);
                    else
                    {
                        memory.Write8((cpxArenaBase + 12720) + encoded++, (byte)(0xc0 | (c >> 6)));
                        memory.Write8((cpxArenaBase + 12720) + encoded++, (byte)(0x80 | (c & 63)));
                    }

                    if (fold < 128)
                        hash = ((((uint)(hash)) >> 8) ^ memory.Read32((memory.Read32(cpxArenaBase + (GeometryCrc))) + ((((uint)(hash)) ^ ((uint)(fold))) & 255) * 4));
                    else
                    {
                        hash = ((((uint)(hash)) >> 8) ^ memory.Read32((memory.Read32(cpxArenaBase + (GeometryCrc))) + ((((uint)(hash)) ^ ((uint)(0xc0 | (fold >> 6)))) & 255) * 4));
                        hash = ((((uint)(hash)) >> 8) ^ memory.Read32((memory.Read32(cpxArenaBase + (GeometryCrc))) + ((((uint)(hash)) ^ ((uint)(0x80 | (fold & 63)))) & 255) * 4));
                    }
                }

                memory.Write32(cpxArenaBase + (NsNameScan), scan);
                memory.Write32(cpxArenaBase + (NsEncodedLength), encoded);
                memory.Write32(cpxArenaBase + (NsHash), hash);
                if (scan < length)
                    return StepResult.Pending;
                Clear((cpxArenaBase + 11824), 128);
            {
                uint cpxWideAddress = (uint)((cpxArenaBase + 11824));
                ulong cpxWideValue = (((ulong)(~hash)) << 32 | ((ulong)(0)));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

            {
                uint cpxWideAddress = (uint)((cpxArenaBase + 11824) + 8);
                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsEntryId)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsEntryId)) + 4))))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

            {
                uint cpxWideAddress = (uint)((cpxArenaBase + 11824) + 16);
                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsResult)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsResult)) + 4))))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write16((cpxArenaBase + 11824) + 32, encoded);
                memory.Write16((cpxArenaBase + 11824) + 36, length);
                if (encoded <= 80)
                {
                    Copy((cpxArenaBase + 11824) + 48, (cpxArenaBase + 12720), encoded);
                    memory.Write32(cpxArenaBase + (Phase), memory.Read32(cpxArenaBase + (NsNameReturn)));
                    return StepResult.Pending;
                }

                return TakeMetadata(81);
            case 81:
                uint page = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(4)) * 4)));
                ulong address = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(MetadataResult)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(MetadataResult)) + 4))))));
            {
                uint cpxWideAddress = (uint)((cpxArenaBase + 11824) + 24);
                ulong cpxWideValue = address;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                Header(page, 17, address, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)) + 4)))))) + 1);
            {
                uint cpxWideAddress = (uint)(page + 24);
                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsEntryId)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsEntryId)) + 4))))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write16(page + 64, memory.Read32(cpxArenaBase + (NsEncodedLength)));
                Copy(page + 68, (cpxArenaBase + 12720), memory.Read32(cpxArenaBase + (NsEncodedLength)));
                memory.Write16(page + 52, memory.Read32(cpxArenaBase + (NsEncodedLength)) + 4);
                return StagePage(page, address, memory.Read32(cpxArenaBase + (NsNameReturn)));
            case 99:
                memory.Write32(cpxArenaBase + (NsPublished), 1);
                return Done();
            case 100:
                return Call(CpxOperation.Reclaim, 1);
            case 102:
                ulong linkPage = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(MetadataResult)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(MetadataResult)) + 4))))));
                uint link = (memory.Read32(cpxArenaBase + (GeometryPages + ((uint)(4)) * 4)));
                memory.Write8((cpxArenaBase + 11312) + 85, 3);
            {
                uint cpxWideAddress = (uint)((cpxArenaBase + 11312) + 96);
                ulong cpxWideValue = linkPage;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                Header(link, 19, linkPage, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(Generation)) + 4)))))) + 1);
            {
                uint cpxWideAddress = (uint)(link + 24);
                ulong cpxWideValue = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsResult)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NsResult)) + 4))))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                memory.Write16(link + 64, memory.Read32(cpxArenaBase + (LinkEncoded)));
                Copy(link + 68, (cpxArenaBase + 15552), memory.Read32(cpxArenaBase + (LinkEncoded)));
                memory.Write16(link + 52, 4 + memory.Read32(cpxArenaBase + (LinkEncoded)));
                return StagePage(link, linkPage, 103);
            case 103:
                return BuildEntry(14);
            default:
                return Fail(CpxError.Corrupt);
        }
    }
}