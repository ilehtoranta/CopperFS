namespace CopperCPX;
public partial struct CpxEngine<TMemory>
    where TMemory : struct, ICpxMemory
{
    private const uint LookupHashValue = 452, LookupHashed = 504;
    private uint LegacyName => ObjectRecord + 1024;
    public bool LookupFound => memory.Read32(Arena + (LookupFoundAt)) != 0;
    public Wide LookupId => RW(LookupTarget);

    public CpxError BeginLookup(Wide parent, uint name, uint length)
    {
        if (length == 0 || length > 255 || name > uint.MaxValue - length)
            return CpxError.InvalidArgument;
        for (uint n = 0; n < length; n++)
            if (memory.Read8(name + n)is 0 or 47 or 58)
                return CpxError.InvalidArgument;
        CpxError error = StartOperation(CpxOperation.Lookup, parent);
        if (error != CpxError.None)
            return error;
        memory.Write32(Arena + (NameLength), length);
        memory.Write32(Arena + (LookupFoundAt), 0);
        WW(LookupTarget, default);
        for (uint n = 0; n < length; n++)
            memory.Write8(LegacyName + n, memory.Read8(name + n));
        return CpxError.None;
    }

    private static uint FoldLatin(uint c) => c is >= 65 and <= 90 or >= 192 and <= 214 or >= 216 and <= 222 ? c + 32 : c;
    private bool IsAlias(out ulong id)
    {
        id = default;
        if (memory.Read32(Arena + (NameLength)) != 19 || memory.Read8(LegacyName) != 95 || memory.Read8(LegacyName + 1) != 95 || FoldLatin(memory.Read8(LegacyName + 2)) != 117)
            return false;
        for (uint n = 3; n < 19; n++)
        {
            uint c = FoldLatin(memory.Read8(LegacyName + n)), digit = c is >= 48 and <= 57 ? c - 48 : c is >= 97 and <= 102 ? c - 87 : 16;
            if (digit == 16)
                return false;
            Scalar64.Left(id, 4, out id);
            id |= digit;
        }

        return true;
    }

    private bool SameLegacy(uint utf8, uint length)
    {
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
            if (legacy >= memory.Read32(Arena + (NameLength)) || FoldLatin(c) != FoldLatin(memory.Read8(LegacyName + legacy)))
                same = false;
            legacy++;
        }

        return same && legacy == memory.Read32(Arena + (NameLength));
    }

    private StepResult LookupStep()
    {
        uint phase = memory.Read32(Arena + (Phase));
        if (phase == 0 && TryCachedLookup())
            return Done();
        if (phase < 100)
        {
            StepResult lookup = ObjectStep();
            if (lookup != StepResult.Complete)
                return lookup;
            memory.Write32(Arena + (Finished), 0);
            if (memory.Read32(Arena + (SourcePage)) == 0)
                return Reject(CpxError.NotFound);
            if (memory.Read8(ObjectRecord + 84) != 2)
                return Reject(CpxError.WrongType);
            WW(DirectoryRoot, GetWide(ObjectRecord + 32));
            WW(NameCursorHigh, default);
            WW(NameCursorLow, default);
            bool alias = IsAlias(out _);
            memory.Write32(Arena + (LookupHashed), alias ? 0u : 1u);
            memory.Write32(Arena + (DataProgress), 0);
            memory.Write32(Arena + (LookupHashValue), uint.MaxValue);
            memory.Write32(Arena + (Phase), alias ? 100u : 104u);
            return StepResult.Pending;
        }

        if (phase >= 200)
        {
            memory.Write32(Arena + (Phase), phase - 200);
            StepResult seek = SeekStep();
            memory.Write32(Arena + (Phase), memory.Read32(Arena + (Phase)) + 200);
            if (seek == StepResult.Complete)
            {
                memory.Write32(Arena + (Finished), 0);
                memory.Write32(Arena + (Phase), 102);
                return StepResult.Pending;
            }

            return seek;
        }

        switch (phase)
        {
            case 100:
            case 101:
                StartSeek(CpxTreeKind.Directory, RW(DirectoryRoot), RW(ObjectKey), RW(NameCursorHigh), RW(NameCursorLow), phase == 100 ? CpxSeek.Next : CpxSeek.After);
                memory.Write32(Arena + (Phase), 200);
                return StepResult.Pending;
            case 102:
                if (!TreeFound)
                    return Done();
                if (memory.Read32(Arena + (LookupHashed)) != 0 && !(((ulong)(GetWide(TreeRecord))) == ((ulong)((((ulong)(~memory.Read32(Arena + (LookupHashValue)))) << 32 | ((ulong)(0)))))))
                    return Done();
                WW(NameCursorHigh, GetWide(TreeRecord));
                WW(NameCursorLow, GetWide(TreeRecord + 8));
                if (IsAlias(out var alias))
                {
                    if ((((ulong)(alias)) == ((ulong)(GetWide(TreeRecord + 8)))))
                    {
                        WW(LookupTarget, GetWide(TreeRecord + 16));
                        memory.Write32(Arena + (LookupFoundAt), 1);
                        return Done();
                    }

                    memory.Write32(Arena + (Phase), 101);
                    return StepResult.Pending;
                }

                uint length = memory.Read16(TreeRecord + 32);
                ulong external = GetWide(TreeRecord + 24);
                if (length == 0 || length > 768)
                    return Fail(CpxError.Corrupt);
                if ((((ulong)(external)) == 0))
                {
                    if (length > 80)
                        return Fail(CpxError.Corrupt);
                    if (SameLegacy(TreeRecord + 48, length))
                    {
                        WW(LookupTarget, GetWide(TreeRecord + 16));
                        memory.Write32(Arena + (LookupFoundAt), 1);
                        return Done();
                    }

                    memory.Write32(Arena + (Phase), 101);
                    return StepResult.Pending;
                }

                if (!Allocatable(external))
                    return Fail(CpxError.Corrupt);
                return ReadStaged(external, Page(4), 103);
            case 103:
                uint p = Page(4), len = memory.Read16(TreeRecord + 32);
                if (!ValidPage(p, GetWide(TreeRecord + 24)) || !ReadableGeneration(p) || memory.Read16(p + 4) != 17 || !(((ulong)(GetWide(p + 24))) == ((ulong)(GetWide(TreeRecord + 8)))) || memory.Read16(p + 64) != len || memory.Read16(p + 66) != 0 || memory.Read16(p + 52) != len + 4)
                    return Fail(CpxError.Corrupt);
                if (SameLegacy(p + 68, len))
                {
                    WW(LookupTarget, GetWide(TreeRecord + 16));
                    memory.Write32(Arena + (LookupFoundAt), 1);
                    return Done();
                }

                memory.Write32(Arena + (Phase), 101);
                return StepResult.Pending;
            case 104:
                uint scan = memory.Read32(Arena + (DataProgress)), end = scan + 64, hash = memory.Read32(Arena + (LookupHashValue));
                if (end > memory.Read32(Arena + (NameLength)))
                    end = memory.Read32(Arena + (NameLength));
                for (; scan < end; scan++)
                {
                    uint c = FoldLatin(memory.Read8(LegacyName + scan));
                    if (c < 128)
                        hash = HashByte(hash, c);
                    else
                    {
                        hash = HashByte(hash, 0xc0 | (c >> 6));
                        hash = HashByte(hash, 0x80 | (c & 63));
                    }
                }

                memory.Write32(Arena + (DataProgress), scan);
                memory.Write32(Arena + (LookupHashValue), hash);
                if (scan == memory.Read32(Arena + (NameLength)))
                {
                    WW(NameCursorHigh, (((ulong)(~hash)) << 32 | ((ulong)(0))));
                    memory.Write32(Arena + (Phase), 100);
                }

                return StepResult.Pending;
            default:
                return Fail(CpxError.Corrupt);
        }
    }

    private bool TryCachedLookup()
    {
        if (!BufferedWrites || IsAlias(out _))
            return false;
        ulong parent = RW(ObjectKey);
        uint record = CachedObject(parent);
        if (record == 0 || memory.Read8(record + 84) != 2)
            return false;
        ulong root = GetWide(record + 32);
        uint page = (((ulong)(root)) == 0) ? 0 : CachedMetadata(root);
        if (!(((ulong)(root)) == 0) && (page == 0 || memory.Read16(page + 4) != 13 || memory.Read16(page + 50) != 0 || !(((ulong)(GetWide(page + 24))) == ((ulong)(parent))) || !(((ulong)(GetWide(page + 40))) == 0)))
            return false;
        uint count = page == 0 ? 0 : memory.Read16(page + 48);
        if (count > (P - 64) >> 7 || page != 0 && memory.Read16(page + 52) != count * 128)
            return false;
        uint hash = uint.MaxValue;
        for (uint n = 0; n < memory.Read32(Arena + (NameLength)); n++)
        {
            uint c = FoldLatin(memory.Read8(LegacyName + n));
            if (c < 128)
                hash = HashByte(hash, c);
            else
            {
                hash = HashByte(hash, 0xc0 | (c >> 6));
                hash = HashByte(hash, 0x80 | (c & 63));
            }
        }

        uint found = 0;
        for (uint n = 0; n < count; n++)
        {
            uint entry = page + 64 + n * 128;
            if (n != 0)
            {
                int order = Scalar64.Compare(GetWide(entry - 128), GetWide(entry));
                if (order > 0 || order == 0 && (GetWide(entry - 120)) >= (GetWide(entry + 8)))
                    return false;
            }

            if (!(((ulong)(GetWide(entry))) == ((ulong)((((ulong)(~hash)) << 32 | ((ulong)(0)))))))
                continue;
            uint length = memory.Read16(entry + 32);
            if (length == 0 || length > 80 || !(((ulong)(GetWide(entry + 24))) == 0))
                return false;
            if (SameLegacy(entry + 48, length))
                found = entry;
        }

        Copy(ObjectRecord, record, 256);
        memory.Write32(Arena + (SourcePage), 1);
        if (found != 0)
        {
            Copy(TreeRecord, found, 128);
            WW(LookupTarget, GetWide(found + 16));
            memory.Write32(Arena + (LookupFoundAt), 1);
        }

        return true;
    }
}
