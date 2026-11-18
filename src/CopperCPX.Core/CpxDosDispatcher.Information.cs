namespace CopperCPX;
public partial struct CpxDosDispatcher<TMemory>
    where TMemory : struct, ICpxMemory
{
    // All offsets are in the existing mount/transport budget. UNMAP ends at
    // 2768 and the DMA page starts at 4096. No buffer grows with directory size.
    private uint InfoEntry => arena + 3072;
    private uint InfoObject => arena + 3200;
    private uint InfoFib => arena + 3456;

    private const uint EntryId = 104, EnumSerial = 112, EnumSlot = 116, EnumNew = 120, FibAddress = 124, InfoMode = 128, InfoRoot = 132, InfoHigh = 140, InfoLow = 148, NameSource = 156, NameBytes = 160, NameScan = 164, NameCount = 168, NameAlias = 172, NameComment = 176;
    private uint U16(uint p) => (uint)(memory.Read8(p) << 8) | memory.Read8(p + 1);
    private void CopyInfo(uint to, uint from, uint bytes)
    {
        for (uint n = 0; n < bytes; n++)
            memory.Write8(to + n, memory.Read8(from + n));
    }

    private bool Buffer(uint address, uint bytes) => address != 0 && (address & 1) == 0 && address <= uint.MaxValue - bytes;
    private uint LockContext(uint p)
    {
        uint cpxArenaBase = arena;
        for (uint n = 0; n < 32; n++)
            if (p == (cpxArenaBase + CpxArena.Scratch + 6 * engine.MetadataPageBytes + CpxArena.IndexBytes) + 2048 + n * 48 + 8)
                return (cpxArenaBase + CpxArena.Scratch + 6 * engine.MetadataPageBytes + CpxArena.IndexBytes) + 15360 + n * 16;
        return 0;
    }

    private void Context(uint parent, uint entry)
    {
        uint cpxArenaBase = arena;
        Put((cpxArenaBase + 512) + ParentId, G(parent));
        Put((cpxArenaBase + 512) + EntryId, G(entry));
    }

    private void ReleaseEnumerations(uint owner, uint fib, bool byFib)
    {
        uint cpxArenaBase = arena;
        uint replacement = 0;
        if (!byFib && owner != 0)
        {
            Wide id = G((owner << 2) + 20);
            for (uint n = 0; n < 32; n++)
            {
                uint p = (cpxArenaBase + CpxArena.Scratch + 6 * engine.MetadataPageBytes + CpxArena.IndexBytes) + 2048 + n * 48 + 8;
                if (p != (owner << 2) && memory.Read32(p - 8) == 1 && Wide.Equal(G(p + 20), id))
                {
                    replacement = p >> 2;
                    break;
                }
            }
        }

        for (uint n = 0; n < 16; n++)
        {
            uint p = (cpxArenaBase + CpxArena.Scratch + 6 * engine.MetadataPageBytes + CpxArena.IndexBytes) + 3584 + n * 64 + 8;
            if (memory.Read32(p - 8) == 1 && (!byFib || memory.Read32(p + 40) == 0) && memory.Read32(p + (byFib ? 32u : 36u)) == (byFib ? fib : owner))
            {
                if (!byFib && memory.Read32(p + 40) == 1033 && replacement != 0)
                {
                    memory.Write32(p + 36, replacement);
                    continue;
                }

                // This scan already validates the exact active slot and owner.
                // Retire directly, preserving generation; do not rescan the pool.
                memory.Write32(p - 8, 0);
            }
        }
    }

    private StepResult BeginNext()
    {
        uint cpxArenaBase = arena;
        if (!LockId(Arg(0), out var id))
            return Finish(0, 211);
        if (Arg(1) > 0x3fffffff || !Buffer(Arg(1) << 2, 260))
            return Finish(0, 115);
        uint fib = Arg(1) << 2, key = memory.Read32(fib), slot = 0;
        if (key == uint.MaxValue)
            return Finish(0, 232);
        if (key == 0)
        {
            if (memory.Read32(cpxArenaBase + 512 + (EnumSerial)) >= uint.MaxValue - 1 || pools.Take(ResourceKind.Enumeration, out slot, out _) != CpxError.None)
                return Finish(0, 103);
            memory.Write32(cpxArenaBase + 512 + (EnumSerial), memory.Read32(cpxArenaBase + 512 + (EnumSerial)) + 1);
            Put(slot, id);
            memory.Write32(slot + 28, memory.Read32(cpxArenaBase + 512 + (EnumSerial)));
            memory.Write32(slot + 32, fib);
            memory.Write32(slot + 36, Arg(0));
            memory.Write32(cpxArenaBase + 512 + (EnumNew), 1);
        }
        else
        {
            for (uint n = 0; n < 16; n++)
            {
                uint p = (cpxArenaBase + CpxArena.Scratch + 6 * engine.MetadataPageBytes + CpxArena.IndexBytes) + 3584 + n * 64 + 8;
                if (memory.Read32(p - 8) == 1 && memory.Read32(p + 40) == 0 && memory.Read32(p + 28) == key && memory.Read32(p + 32) == fib && memory.Read32(p + 36) == Arg(0) && Wide.Equal(G(p), id))
                    slot = p;
            }

            if (slot == 0)
                return Finish(0, 211);
        }

        memory.Write32(cpxArenaBase + 512 + (EnumSlot), slot);
        memory.Write32(cpxArenaBase + 512 + (FibAddress), fib);
        memory.Write32(cpxArenaBase + 512 + (InfoMode), 1);
        Put((cpxArenaBase + 512) + Object, id);
        return Start(engine.BeginOperation(CpxOperation.ReadObject, id), 40);
    }

    private StepResult Examine()
    {
        uint cpxArenaBase = arena;
        if (Arg(1) > 0x3fffffff || !Buffer(Arg(1) << 2, 260))
            return Finish(0, 115);
        memory.Write32(cpxArenaBase + 512 + (FibAddress), Arg(1) << 2);
        memory.Write32(cpxArenaBase + 512 + (InfoMode), 0);
        PrepareFib();
        if (Wide.Equal(G((cpxArenaBase + 512) + Object), Wide.From(1)))
        {
            CopyInfo((cpxArenaBase + 3456) + 8, engine.VolumeLabelAddress, 32);
            return ReadComment();
        }

        if (G((cpxArenaBase + 512) + ParentId).IsZero && memory.Read8((cpxArenaBase + 3200) + 84) == 2)
            Put((cpxArenaBase + 512) + ParentId, G((cpxArenaBase + 3200) + 40));
        if (G((cpxArenaBase + 512) + ParentId).IsZero)
            return Finish(0, 205);
        return Start(engine.BeginOperation(CpxOperation.ReadObject, G((cpxArenaBase + 512) + ParentId)), 41);
    }

    private void PrepareFib()
    {
        uint cpxArenaBase = arena;
        CopyInfo((cpxArenaBase + 3200), engine.ObjectRecord, 256);
        for (uint n = 0; n < 260; n += 4)
            memory.Write32((cpxArenaBase + 3456) + n, 0);
        uint kind = memory.Read8((cpxArenaBase + 3200) + 84), type = Wide.Equal(G((cpxArenaBase + 3200)), Wide.From(1)) ? 1u : kind == 2 ? 2u : kind == 3 ? 3u : 0xfffffffdu;
        Wide size = kind == 2 ? default : G((cpxArenaBase + 3200) + 8), allocated = G((cpxArenaBase + 3200) + 16);
        memory.Write32((cpxArenaBase + 3456) + 4, type);
        memory.Write32((cpxArenaBase + 3456) + 120, type);
        memory.Write32((cpxArenaBase + 3456) + 116, memory.Read32((cpxArenaBase + 3200) + 72));
        memory.Write32((cpxArenaBase + 3456) + 124, size.High != 0 || size.Low > int.MaxValue ? 0 : size.Low);
        memory.Write32((cpxArenaBase + 3456) + 128, allocated.High != 0 ? uint.MaxValue : allocated.Low);
        memory.Write32((cpxArenaBase + 3456) + 132, memory.Read32((cpxArenaBase + 3200) + 56));
        memory.Write32((cpxArenaBase + 3456) + 136, U16((cpxArenaBase + 3200) + 60));
        memory.Write32((cpxArenaBase + 3456) + 140, U16((cpxArenaBase + 3200) + 62));
        Put((cpxArenaBase + 3456) + 228, size);
        Put((cpxArenaBase + 3456) + 236, allocated);
    }

    private StepResult FindEntry(bool first, uint next)
    {
        uint cpxArenaBase = arena;
        return Start(engine.BeginTreeRead(CpxTreeKind.Directory, G((cpxArenaBase + 512) + InfoRoot), memory.Read32(cpxArenaBase + 512 + (InfoMode)) == 1 ? G((cpxArenaBase + 512) + Object) : G((cpxArenaBase + 512) + ParentId), G((cpxArenaBase + 512) + InfoHigh), G((cpxArenaBase + 512) + InfoLow), first ? CpxSeek.Next : CpxSeek.After), next);
    }

    private StepResult EntryName()
    {
        uint cpxArenaBase = arena;
        uint length = U16((cpxArenaBase + 3072) + 32);
        Wide page = G((cpxArenaBase + 3072) + 24);
        if (length == 0 || length > 768 || G((cpxArenaBase + 3072) + 8).IsZero)
            return Finish(0, 213);
        if (!page.IsZero)
            return Start(engine.BeginOperation(CpxOperation.ReadMetadata, page), 44);
        if (length > 80)
            return Finish(0, 213);
        return DecodeName((cpxArenaBase + 3072) + 48, length, false);
    }

    private StepResult DecodeName(uint source, uint bytes, bool comment)
    {
        uint cpxArenaBase = arena;
        memory.Write32(cpxArenaBase + 512 + (NameSource), source);
        memory.Write32(cpxArenaBase + 512 + (NameBytes), bytes);
        memory.Write32(cpxArenaBase + 512 + (NameScan), 0);
        memory.Write32(cpxArenaBase + 512 + (NameCount), 0);
        memory.Write32(cpxArenaBase + 512 + (NameAlias), 0);
        memory.Write32(cpxArenaBase + 512 + (NameComment), comment ? 1u : 0u);
        memory.Write32(cpxArenaBase + 512 + (Phase), 60);
        return StepResult.Pending;
    }

    private StepResult DecodeStep()
    {
        uint cpxArenaBase = arena;
        uint at = memory.Read32(cpxArenaBase + 512 + (NameScan)), count = memory.Read32(cpxArenaBase + 512 + (NameCount)), source = memory.Read32(cpxArenaBase + 512 + (NameSource)), limit = memory.Read32(cpxArenaBase + 512 + (NameComment)) == 0 ? 107u : 79u;
        for (uint work = 0; work < 64 && at < memory.Read32(cpxArenaBase + 512 + (NameBytes)); work++)
        {
            uint c = memory.Read8(source + at++), extra = 0, minimum = 0;
            if (c >= 128)
            {
                if (c is >= 194 and <= 223)
                {
                    c &= 31;
                    extra = 1;
                    minimum = 128;
                }
                else if (c is >= 224 and <= 239)
                {
                    c &= 15;
                    extra = 2;
                    minimum = 2048;
                }
                else if (c is >= 240 and <= 244)
                {
                    c &= 7;
                    extra = 3;
                    minimum = 65536;
                }
                else
                    return Finish(0, 213);
                if (extra > memory.Read32(cpxArenaBase + 512 + (NameBytes)) - at)
                    return Finish(0, 213);
                for (uint n = 0; n < extra; n++)
                {
                    uint b = memory.Read8(source + at++);
                    if ((b & 192) != 128)
                        return Finish(0, 213);
                    c = (c << 6) | (b & 63);
                }

                if (c < minimum || c > 0x10ffff || c is >= 0xd800 and <= 0xdfff)
                    return Finish(0, 213);
            }

            if (c == 0 || memory.Read32(cpxArenaBase + 512 + (NameComment)) == 0 && (c == 47 || c == 58))
                return Finish(0, 213);
            if (c > 255 || count >= limit)
                memory.Write32(cpxArenaBase + 512 + (NameAlias), 1);
            else
                memory.Write8((cpxArenaBase + 3456) + (memory.Read32(cpxArenaBase + 512 + (NameComment)) == 0 ? 9u : 145u) + count, (byte)c);
            count++;
        }

        memory.Write32(cpxArenaBase + 512 + (NameScan), at);
        memory.Write32(cpxArenaBase + 512 + (NameCount), count);
        if (at < memory.Read32(cpxArenaBase + 512 + (NameBytes)))
            return StepResult.Pending;
        if (memory.Read32(cpxArenaBase + 512 + (NameComment)) != 0)
        {
            if (memory.Read32(cpxArenaBase + 512 + (NameAlias)) != 0)
                return Finish(0, 213);
            memory.Write8((cpxArenaBase + 3456) + 144, (byte)count);
            memory.Write32(cpxArenaBase + 512 + (Phase), 61);
            return StepResult.Pending;
        }

        if (memory.Read32(cpxArenaBase + 512 + (NameAlias)) != 0)
        {
            for (uint n = 0; n < 108; n++)
                memory.Write8((cpxArenaBase + 3456) + 8 + n, 0);
            memory.Write8((cpxArenaBase + 3456) + 8, 19);
            memory.Write8((cpxArenaBase + 3456) + 9, 95);
            memory.Write8((cpxArenaBase + 3456) + 10, 95);
            memory.Write8((cpxArenaBase + 3456) + 11, 117);
            Wide id = G((cpxArenaBase + 3072) + 8);
            for (uint n = 0; n < 16; n++)
            {
                uint digit = Wide.Right(id, (int)((15 - n) * 4)).Low & 15;
                memory.Write8((cpxArenaBase + 3456) + 12 + n, (byte)(digit < 10 ? 48 + digit : 87 + digit));
            }
        }
        else
            memory.Write8((cpxArenaBase + 3456) + 8, (byte)count);
        return memory.Read32(cpxArenaBase + 512 + (Action)) == 26406 ? PrependNewLinkName() : ReadComment();
    }

    private StepResult ReadComment()
    {
        uint cpxArenaBase = arena;
        Wide page = G((cpxArenaBase + 3200) + 48);
        if (!page.IsZero)
            return Start(engine.BeginOperation(CpxOperation.ReadMetadata, page), 46);
        memory.Write32(cpxArenaBase + 512 + (Phase), 61);
        return StepResult.Pending;
    }

    private StepResult PublishFib()
    {
        uint cpxArenaBase = arena;
        if (memory.Read32(cpxArenaBase + 512 + (Action)) == 1033)
            return PrepareAllRecord();
        if (memory.Read32(cpxArenaBase + 512 + (InfoMode)) == 1)
        {
            uint p = memory.Read32(cpxArenaBase + 512 + (EnumSlot));
            Put(p + 8, G((cpxArenaBase + 3072)));
            Put(p + 16, G((cpxArenaBase + 3072) + 8));
            memory.Write32(p + 24, 1);
            memory.Write32((cpxArenaBase + 3456), memory.Read32(p + 28));
            memory.Write32(cpxArenaBase + 512 + (EnumNew), 0);
        }
        else
            ReleaseEnumerations(0, memory.Read32(cpxArenaBase + 512 + (FibAddress)), true);
        CopyInfo(memory.Read32(cpxArenaBase + 512 + (FibAddress)), (cpxArenaBase + 3456), 260);
        return Finish(uint.MaxValue);
    }

    private StepResult InformationStep()
    {
        uint cpxArenaBase = arena;
        uint phase = memory.Read32(cpxArenaBase + 512 + (Phase));
        if (phase >= 62)
            return ExAllStep();
        if (phase == 60)
            return DecodeStep();
        if (phase == 61)
            return PublishFib();
        StepResult step = engine.Step();
        if (step is StepResult.Pending or StepResult.NeedIo)
            return step;
        if (step == StepResult.Failed)
            return Finish(0, ToDosError(engine.Error));
        uint page = engine.MetadataBuffer;
        switch (phase)
        {
            case 40:
            case 41:
                if (!engine.ObjectFound)
                    return Finish(0, 205);
                if (memory.Read8(engine.ObjectRecord + 84) != 2)
                    return Finish(0, 212);
                Put((cpxArenaBase + 512) + InfoRoot, G(engine.ObjectRecord + 32));
                uint slot = memory.Read32(cpxArenaBase + 512 + (EnumSlot));
                bool first = phase == 41 || memory.Read32(slot + 24) == 0;
                Put((cpxArenaBase + 512) + InfoHigh, first ? default : G(slot + 8));
                Put((cpxArenaBase + 512) + InfoLow, first ? default : G(slot + 16));
                return FindEntry(first, phase == 40 ? 43u : 42u);
            case 42:
                if (!engine.TreeFound)
                    return Finish(0, 205);
                Put((cpxArenaBase + 512) + InfoHigh, G(engine.TreeRecord));
                Put((cpxArenaBase + 512) + InfoLow, G(engine.TreeRecord + 8));
                if (!Wide.Equal(G(engine.TreeRecord + 16), G((cpxArenaBase + 512) + Object)) || !G((cpxArenaBase + 512) + EntryId).IsZero && !Wide.Equal(G(engine.TreeRecord + 8), G((cpxArenaBase + 512) + EntryId)))
                    return FindEntry(false, 42);
                CopyInfo((cpxArenaBase + 3072), engine.TreeRecord, 128);
                return EntryName();
            case 43:
                if (!engine.TreeFound)
                {
                    if (memory.Read32(cpxArenaBase + 512 + (Action)) == 1033)
                        return Finish(0, 232);
                    uint p = memory.Read32(cpxArenaBase + 512 + (EnumSlot));
                    pools.Release(ResourceKind.Enumeration, p, memory.Read32(p - 4));
                    memory.Write32(cpxArenaBase + 512 + (EnumNew), 0);
                    memory.Write32(memory.Read32(cpxArenaBase + 512 + (FibAddress)), uint.MaxValue);
                    return Finish(0, 232);
                }

                CopyInfo((cpxArenaBase + 3072), engine.TreeRecord, 128);
                return Start(engine.BeginOperation(CpxOperation.ReadObject, G((cpxArenaBase + 3072) + 16)), 45);
            case 44:
                uint length = U16((cpxArenaBase + 3072) + 32);
                if (U16(page + 4) != 17 || !Wide.Equal(G(page + 24), G((cpxArenaBase + 3072) + 8)) || U16(page + 64) != length || U16(page + 66) != 0 || U16(page + 52) != length + 4)
                    return Finish(0, 213);
                return DecodeName(page + 68, length, false);
            case 45:
                if (!engine.ObjectFound)
                    return Finish(0, 213);
                PrepareFib();
                return EntryName();
            case 46:
                if (!engine.ValidAttribute(page, G((cpxArenaBase + 3200))))
                    return Finish(0, 213);
                memory.Write32((cpxArenaBase + 3456) + 224, engine.AttributeOwnerInfo(page));
                return DecodeName(engine.AttributeCommentData(page), engine.AttributeCommentBytes(page), true);
            case 52:
                return PublishInformation();
            default:
                return Finish(0, 209);
        }
    }

    private StepResult BeginInformation()
    {
        uint cpxArenaBase = arena;
        uint action = memory.Read32(cpxArenaBase + 512 + (Action)), output = action == 25 ? Arg(0) : Arg(1);
        if (action == 26 && !LockId(Arg(0), out _))
            return Finish(0, 211);
        if (action == 26407)
        {
            uint attribute = Arg(0), bytes = attribute is 2 or 6 or 7 ? 8u : 4u;
            if (attribute == 5 || attribute > 7)
                return Finish(0, 115);
            if (Arg(2) > int.MaxValue || Arg(2) < bytes)
                return Finish(0, 120);
            if (!Buffer(output, bytes))
                return Finish(0, 115);
            if (attribute < 6)
            {
                if (attribute == 2)
                    Put(output, new(uint.MaxValue, uint.MaxValue));
                else
                    memory.Write32(output, attribute == 0 ? 255u : attribute == 1 ? 30u : attribute == 3 ? 0u : 31u);
                return Finish(uint.MaxValue);
            }
        }
        else
        {
            if (output > 0x3fffffff || !Buffer(output << 2, 36))
                return Finish(0, 115);
            output <<= 2;
        }

        memory.Write32(cpxArenaBase + 512 + (FibAddress), output);
        return Start(engine.BeginOperation(CpxOperation.ReadMetadata, engine.StatePageAddress), 52);
    }

    private StepResult PublishInformation()
    {
        uint cpxArenaBase = arena;
        uint p = engine.MetadataBuffer, output = memory.Read32(cpxArenaBase + 512 + (FibAddress));
        Wide total = engine.AllocationUnits, free = G(p + 96);
        if (U16(p + 4) != 3 || !G(p + 24).IsZero || !Wide.Subtract(total, free, out var used))
            return Finish(0, 213);
        if (memory.Read32(cpxArenaBase + 512 + (Action)) == 26407)
        {
            Put(output, Arg(0) == 6 ? total : used);
            return Finish(uint.MaxValue);
        }

        for (uint n = 0; n < 36; n += 4)
            memory.Write32(output + n, 0);
        memory.Write32(output + 8, (engine.IsReadOnly || memory.Read32(cpxArenaBase + 512 + (SoftwareProtected)) != 0) ? 80u : 82u);
        memory.Write32(output + 12, total.High != 0 || total.Low > int.MaxValue ? int.MaxValue : total.Low);
        memory.Write32(output + 16, used.High != 0 || used.Low > int.MaxValue ? int.MaxValue : used.Low);
        memory.Write32(output + 20, engine.AllocationUnitBytes);
        memory.Write32(output + 24, CpxArena.Magic);
        memory.Write32(output + 28, memory.Read32(cpxArenaBase + 512 + (Volume)) >> 2);
        memory.Write32(output + 32, HasOpenResources ? uint.MaxValue : 0);
        return Finish(uint.MaxValue);
    }
}