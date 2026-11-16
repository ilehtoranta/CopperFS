namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    private const uint LookupHashValue = 452, LookupHashed = 504;
    private uint LegacyName => (Arena + CpxArena.MountBytes) + 1024;
    public bool LookupFound => memory.Read32(Arena + (LookupFoundAt)) != 0;

    public Wide LookupId
    {
        get
        {
            uint cpxArenaBase = Arena;
            return ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(LookupTarget)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(LookupTarget)) + 4))))));
        }
    }

    public CpxError BeginLookup(Wide parent, uint name, uint length)
    {
        uint cpxArenaBase = Arena;
        if (length == 0 || length > 255 || name > uint.MaxValue - length)
            return CpxError.InvalidArgument;
        for (uint n = 0; n < length; n++)
            if (memory.Read8(name + n)is 0 or 47 or 58)
                return CpxError.InvalidArgument;
        CpxError error = StartOperation(CpxOperation.Lookup, parent);
        if (error != CpxError.None)
            return error;
        memory.Write32(cpxArenaBase + (NameLength), length);
        memory.Write32(cpxArenaBase + (LookupFoundAt), 0);
        {
            uint cpxWideAddress = cpxArenaBase + (uint)(LookupTarget);
            ulong cpxWideValue = default;
            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
        }

        for (uint n = 0; n < length; n++)
            memory.Write8(((cpxArenaBase + CpxArena.MountBytes) + 1024) + n, memory.Read8(name + n));
        return CpxError.None;
    }

    private static uint FoldLatin(uint c) => c is >= 65 and <= 90 or >= 192 and <= 214 or >= 216 and <= 222 ? c + 32 : c;
    private bool IsAlias(out ulong id)
    {
        uint cpxArenaBase = Arena;
        id = default;
        if (memory.Read32(cpxArenaBase + (NameLength)) != 19 || memory.Read8(((cpxArenaBase + CpxArena.MountBytes) + 1024)) != 95 || memory.Read8(((cpxArenaBase + CpxArena.MountBytes) + 1024) + 1) != 95 || FoldLatin(memory.Read8(((cpxArenaBase + CpxArena.MountBytes) + 1024) + 2)) != 117)
            return false;
        for (uint n = 3; n < 19; n++)
        {
            uint c = FoldLatin(memory.Read8(((cpxArenaBase + CpxArena.MountBytes) + 1024) + n)), digit = c is >= 48 and <= 57 ? c - 48 : c is >= 97 and <= 102 ? c - 87 : 16;
            if (digit == 16)
                return false;
            Scalar64.Left(id, 4, out id);
            id |= digit;
        }

        return true;
    }

    private bool SameLegacy(uint utf8, uint length)
    {
        uint cpxArenaBase = Arena;
        uint index = 0, legacy = 0;
        bool same = true;
        while (index < length)
        {
            uint c = memory.Read8(utf8 + index++), continuations = 0, minimum = 0;
            if (c >= 128)
            {
                if (c is >= 194 and <= 223)
                {
                    c &= 31;
                    continuations = 1;
                    minimum = 128;
                }
                else if (c is >= 224 and <= 239)
                {
                    c &= 15;
                    continuations = 2;
                    minimum = 2048;
                }
                else if (c is >= 240 and <= 244)
                {
                    c &= 7;
                    continuations = 3;
                    minimum = 65536;
                }
                else
                    return false;
                if (continuations > length - index)
                    return false;
                for (uint n = 0; n < continuations; n++)
                {
                    uint next = memory.Read8(utf8 + index++);
                    if ((next & 192) != 128)
                        return false;
                    c = (c << 6) | (next & 63);
                }

                if (c < minimum || c > 0x10ffff || c is >= 0xd800 and <= 0xdfff)
                    return false;
            }

            if (c is 0 or 47 or 58)
                return false;
            if (legacy >= memory.Read32(cpxArenaBase + (NameLength)) || FoldLatin(c) != FoldLatin(memory.Read8(((cpxArenaBase + CpxArena.MountBytes) + 1024) + legacy)))
                same = false;
            legacy++;
        }

        return same && legacy == memory.Read32(cpxArenaBase + (NameLength));
    }

    private StepResult LookupStep()
    {
        uint cpxArenaBase = Arena;
        uint phase = memory.Read32(cpxArenaBase + (Phase));
        if (phase == 0 && TryCachedLookup())
            return FinishLookup();
        if (phase < 100)
        {
            StepResult lookup = ObjectStep();
            if (lookup != StepResult.Complete)
                return lookup;
            memory.Write32(cpxArenaBase + (Finished), 0);
            if (memory.Read32(cpxArenaBase + (SourcePage)) == 0)
                return Reject(CpxError.NotFound);
            if (memory.Read8((cpxArenaBase + CpxArena.MountBytes) + 84) != 2)
                return Reject(CpxError.WrongType);
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(DirectoryRoot);
                ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)((cpxArenaBase + CpxArena.MountBytes) + 32))))) << 32 | ((ulong)(memory.Read32(((uint)((cpxArenaBase + CpxArena.MountBytes) + 32)) + 4)))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

            {
                uint cpxWideAddress = cpxArenaBase + (uint)(NameCursorHigh);
                ulong cpxWideValue = default;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

            {
                uint cpxWideAddress = cpxArenaBase + (uint)(NameCursorLow);
                ulong cpxWideValue = default;
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

            bool alias = IsAlias(out _);
            memory.Write32(cpxArenaBase + (LookupHashed), alias ? 0u : 1u);
            memory.Write32(cpxArenaBase + (DataProgress), 0);
            memory.Write32(cpxArenaBase + (LookupHashValue), uint.MaxValue);
            memory.Write32(cpxArenaBase + (Phase), alias ? 100u : 104u);
            return StepResult.Pending;
        }

        if (phase >= 200)
        {
            memory.Write32(cpxArenaBase + (Phase), phase - 200);
            StepResult seek = SeekStep();
            memory.Write32(cpxArenaBase + (Phase), memory.Read32(cpxArenaBase + (Phase)) + 200);
            if (seek == StepResult.Complete)
            {
                memory.Write32(cpxArenaBase + (Finished), 0);
                memory.Write32(cpxArenaBase + (Phase), 102);
                return StepResult.Pending;
            }

            return seek;
        }

        switch (phase)
        {
            case 100:
            case 101:
                StartSeek(CpxTreeKind.Directory, ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(DirectoryRoot)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(DirectoryRoot)) + 4)))))), ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ObjectKey)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ObjectKey)) + 4)))))), ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NameCursorHigh)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NameCursorHigh)) + 4)))))), ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(NameCursorLow)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(NameCursorLow)) + 4)))))), phase == 100 ? CpxSeek.Next : CpxSeek.After);
                memory.Write32(cpxArenaBase + (Phase), 200);
                return StepResult.Pending;
            case 102:
                if (!(memory.Read32(cpxArenaBase + (SeekFound)) != 0))
                    return FinishLookup();
                if (memory.Read32(cpxArenaBase + (LookupHashed)) != 0 && !(((ulong)(((((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 512)))))) << 32 | ((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 512))) + 4))))))) == ((ulong)((((ulong)(~memory.Read32(cpxArenaBase + (LookupHashValue)))) << 32 | ((ulong)(0)))))))
                    return FinishLookup();
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(NameCursorHigh);
                ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 512)))))) << 32 | ((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 512))) + 4)))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

            {
                uint cpxWideAddress = cpxArenaBase + (uint)(NameCursorLow);
                ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 512) + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 512) + 8)) + 4)))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

                if (IsAlias(out var alias))
                {
                    if ((((ulong)(alias)) == ((ulong)(((((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 512) + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 512) + 8)) + 4)))))))))
                    {
                        {
                            uint cpxWideAddress = cpxArenaBase + (uint)(LookupTarget);
                            ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 512) + 16))))) << 32 | ((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 512) + 16)) + 4)))));
                            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                        }

                        memory.Write32(cpxArenaBase + (LookupFoundAt), 1);
                        return FinishLookup();
                    }

                    memory.Write32(cpxArenaBase + (Phase), 101);
                    return StepResult.Pending;
                }

                uint length = memory.Read16(((cpxArenaBase + CpxArena.MountBytes) + 512) + 32);
                ulong external = ((((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 512) + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 512) + 24)) + 4)))));
                if (length == 0 || length > 768)
                    return Fail(CpxError.Corrupt);
                if ((((ulong)(external)) == 0))
                {
                    if (length > 80)
                        return Fail(CpxError.Corrupt);
                    if (SameLegacy(((cpxArenaBase + CpxArena.MountBytes) + 512) + 48, length))
                    {
                        {
                            uint cpxWideAddress = cpxArenaBase + (uint)(LookupTarget);
                            ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 512) + 16))))) << 32 | ((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 512) + 16)) + 4)))));
                            memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                            memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                        }

                        memory.Write32(cpxArenaBase + (LookupFoundAt), 1);
                        return FinishLookup();
                    }

                    memory.Write32(cpxArenaBase + (Phase), 101);
                    return StepResult.Pending;
                }

                if (!Allocatable(external))
                    return Fail(CpxError.Corrupt);
                return BeginBlob(external, 17, GetWide(Arena + CpxArena.MountBytes + 512 + 8), length, NsEncoded, 1, 103);
            case 103:
                uint len = memory.Read16(Arena + CpxArena.MountBytes + 512 + 32);
                if (SameLegacy(NsEncoded, len))
                {
                    {
                        uint cpxWideAddress = cpxArenaBase + (uint)(LookupTarget);
                        ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 512) + 16))))) << 32 | ((ulong)(memory.Read32(((uint)(((cpxArenaBase + CpxArena.MountBytes) + 512) + 16)) + 4)))));
                        memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                        memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                    }

                    memory.Write32(cpxArenaBase + (LookupFoundAt), 1);
                    return FinishLookup();
                }

                memory.Write32(cpxArenaBase + (Phase), 101);
                return StepResult.Pending;
            case 104:
                uint scan = memory.Read32(cpxArenaBase + (DataProgress)), end = scan + 64, hash = memory.Read32(cpxArenaBase + (LookupHashValue));
                if (end > memory.Read32(cpxArenaBase + (NameLength)))
                    end = memory.Read32(cpxArenaBase + (NameLength));
                for (; scan < end; scan++)
                {
                    uint c = FoldLatin(memory.Read8(((cpxArenaBase + CpxArena.MountBytes) + 1024) + scan));
                    if (c < 128)
                        hash = ((((uint)(hash)) >> 8) ^ memory.Read32((memory.Read32(cpxArenaBase + (GeometryCrc))) + ((((uint)(hash)) ^ ((uint)(c))) & 255) * 4));
                    else
                    {
                        hash = ((((uint)(hash)) >> 8) ^ memory.Read32((memory.Read32(cpxArenaBase + (GeometryCrc))) + ((((uint)(hash)) ^ ((uint)(0xc0 | (c >> 6)))) & 255) * 4));
                        hash = ((((uint)(hash)) >> 8) ^ memory.Read32((memory.Read32(cpxArenaBase + (GeometryCrc))) + ((((uint)(hash)) ^ ((uint)(0x80 | (c & 63)))) & 255) * 4));
                    }
                }

                memory.Write32(cpxArenaBase + (DataProgress), scan);
                memory.Write32(cpxArenaBase + (LookupHashValue), hash);
                if (scan == memory.Read32(cpxArenaBase + (NameLength)))
                {
                    {
                        uint cpxWideAddress = cpxArenaBase + (uint)(NameCursorHigh);
                        ulong cpxWideValue = (((ulong)(~hash)) << 32 | ((ulong)(0)));
                        memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                        memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
                    }

                    memory.Write32(cpxArenaBase + (Phase), 100);
                }

                return StepResult.Pending;
            default:
                return Fail(CpxError.Corrupt);
        }
    }

    private bool TryCachedLookup()
    {
        uint cpxArenaBase = Arena;
        if (!(memory.Read32(cpxArenaBase + (BufferEnabled)) == 1) || IsAlias(out _))
            return false;
        ulong parent = ((((ulong)(memory.Read32(cpxArenaBase + (((uint)(ObjectKey)))))) << 32 | ((ulong)(memory.Read32(cpxArenaBase + (((uint)(ObjectKey)) + 4))))));
        uint record = CachedObject(parent);
        if (record == 0 || memory.Read8(record + 84) != 2)
            return false;
        ulong root = ((((ulong)(memory.Read32(((uint)(record + 32))))) << 32 | ((ulong)(memory.Read32(((uint)(record + 32)) + 4)))));
        uint page = (((ulong)(root)) == 0) ? 0 : CachedMetadata(root);
        if (!(((ulong)(root)) == 0) && (page == 0 || memory.Read16(page + 4) != 13 || memory.Read16(page + 50) != 0 || !(((ulong)(((((ulong)(memory.Read32(((uint)(page + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(page + 24)) + 4))))))) == ((ulong)(parent))) || !(((ulong)(((((ulong)(memory.Read32(((uint)(page + 40))))) << 32 | ((ulong)(memory.Read32(((uint)(page + 40)) + 4))))))) == 0)))
            return false;
        uint count = page == 0 ? 0 : memory.Read16(page + 48);
        if (count > ((memory.Read32(cpxArenaBase + (PageBytes))) - 64) >> 7 || page != 0 && memory.Read16(page + 52) != count * 128)
            return false;
        uint hash = uint.MaxValue;
        for (uint n = 0; n < memory.Read32(cpxArenaBase + (NameLength)); n++)
        {
            uint c = FoldLatin(memory.Read8(((cpxArenaBase + CpxArena.MountBytes) + 1024) + n));
            if (c < 128)
                hash = ((((uint)(hash)) >> 8) ^ memory.Read32((memory.Read32(cpxArenaBase + (GeometryCrc))) + ((((uint)(hash)) ^ ((uint)(c))) & 255) * 4));
            else
            {
                hash = ((((uint)(hash)) >> 8) ^ memory.Read32((memory.Read32(cpxArenaBase + (GeometryCrc))) + ((((uint)(hash)) ^ ((uint)(0xc0 | (c >> 6)))) & 255) * 4));
                hash = ((((uint)(hash)) >> 8) ^ memory.Read32((memory.Read32(cpxArenaBase + (GeometryCrc))) + ((((uint)(hash)) ^ ((uint)(0x80 | (c & 63)))) & 255) * 4));
            }
        }

        uint found = 0;
        for (uint n = 0; n < count; n++)
        {
            uint entry = page + 64 + n * 128;
            if (n != 0)
            {
                int order = Scalar64.Compare(((((ulong)(memory.Read32(((uint)(entry - 128))))) << 32 | ((ulong)(memory.Read32(((uint)(entry - 128)) + 4))))), ((((ulong)(memory.Read32(((uint)(entry))))) << 32 | ((ulong)(memory.Read32(((uint)(entry)) + 4))))));
                if (order > 0 || order == 0 && (((((ulong)(memory.Read32(((uint)(entry - 120))))) << 32 | ((ulong)(memory.Read32(((uint)(entry - 120)) + 4)))))) >= (((((ulong)(memory.Read32(((uint)(entry + 8))))) << 32 | ((ulong)(memory.Read32(((uint)(entry + 8)) + 4)))))))
                    return false;
            }

            if (!(((ulong)(((((ulong)(memory.Read32(((uint)(entry))))) << 32 | ((ulong)(memory.Read32(((uint)(entry)) + 4))))))) == ((ulong)((((ulong)(~hash)) << 32 | ((ulong)(0)))))))
                continue;
            uint length = memory.Read16(entry + 32);
            if (length == 0 || length > 80 || !(((ulong)(((((ulong)(memory.Read32(((uint)(entry + 24))))) << 32 | ((ulong)(memory.Read32(((uint)(entry + 24)) + 4))))))) == 0))
                return false;
            if (SameLegacy(entry + 48, length))
                found = entry;
        }

        Copy((cpxArenaBase + CpxArena.MountBytes), record, 256);
        memory.Write32(cpxArenaBase + (SourcePage), 1);
        if (found != 0)
        {
            Copy(((cpxArenaBase + CpxArena.MountBytes) + 512), found, 128);
            {
                uint cpxWideAddress = cpxArenaBase + (uint)(LookupTarget);
                ulong cpxWideValue = ((((ulong)(memory.Read32(((uint)(found + 16))))) << 32 | ((ulong)(memory.Read32(((uint)(found + 16)) + 4)))));
                memory.Write32(cpxWideAddress, (uint)(cpxWideValue >> 32));
                memory.Write32(cpxWideAddress + 4, (uint)cpxWideValue);
            }

            memory.Write32(cpxArenaBase + (LookupFoundAt), 1);
        }

        return true;
    }
}
