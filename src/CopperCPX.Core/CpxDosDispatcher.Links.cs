namespace CopperCPX;
public partial struct CpxDosDispatcher<TMemory>
    where TMemory : struct, ICpxMemory
{
    // Union with examination scratch; link packets never run FIB phases.
    private uint LinkText => arena + 3072;

    private const uint LinkScan = 204, LinkBytes = 208, LinkDecoded = 212, LinkPrefix = 216, LinkSuffix = 220, LinkAbsolute = 224, LinkTotal = 228, LinkTarget = 232;
    private StepResult BeginLinkPacket()
    {
        uint cpxArenaBase = arena;
        bool create = memory.Read32(cpxArenaBase + 512 + (Action)) == 1021;
        if (create && Arg(3) > 1)
            return Finish(0, 115);
        if (create && Arg(3) == 0)
        {
            if (Arg(2) == 0 || !LockId(Arg(2), out var id))
                return Finish(0, 211);
            Put((cpxArenaBase + 512) + LinkTarget, id);
            return StartPath(Arg(0), Arg(1));
        }

        uint input = Arg(create ? 2u : 1u), limit = create ? 768u : 255u;
        if (input == 0 || input > uint.MaxValue - limit)
            return Finish(0, 115);
        if (!create)
        {
            if (!LockId(Arg(0), out var id))
                return Finish(0, 211);
            if (Arg(0) != 0)
            {
                uint context = LockContext(Arg(0) << 2);
                Context(context, context + 8);
            }

            if (Arg(2) == 0 || Arg(3) > int.MaxValue || Arg(2) > uint.MaxValue - Arg(3))
                return Finish(0, 115);
            Put((cpxArenaBase + 512) + Object, id);
            memory.Write32(cpxArenaBase + 512 + (PathAt), 0);
        }

        memory.Write32(cpxArenaBase + 512 + (LinkScan), 0);
        memory.Write32(cpxArenaBase + 512 + (Phase), 90);
        return StepResult.Pending;
    }

    private StepResult LinkStep()
    {
        uint cpxArenaBase = arena;
        uint phase = memory.Read32(cpxArenaBase + 512 + (Phase));
        if (phase == 90)
        {
            bool create = memory.Read32(cpxArenaBase + 512 + (Action)) == 1021;
            uint at = memory.Read32(cpxArenaBase + 512 + (LinkScan)), limit = create ? 768u : 255u, input = Arg(create ? 2u : 1u), target = create ? (cpxArenaBase + 3072) : (cpxArenaBase + 1024);
            for (uint work = 0; work < 64; work++, at++)
            {
                byte c = memory.Read8(input + at);
                if (c == 0)
                {
                    if (create)
                    {
                        if (at == 0)
                            return Finish(0, 210);
                        memory.Write32(cpxArenaBase + 512 + (LinkBytes), at);
                        return StartPath(Arg(0), Arg(1));
                    }

                    memory.Write32(cpxArenaBase + 512 + (PathLength), at);
                    memory.Write32(cpxArenaBase + 512 + (Phase), 10);
                    return StepResult.Pending;
                }

                if (at == limit)
                    return Finish(0, 120);
                memory.Write8(target + at, c);
                if (!create && c == 58)
                {
                    Put((cpxArenaBase + 512) + Object, Wide.From(1));
                    memory.Write32(cpxArenaBase + 512 + (PathAt), at + 1);
                }
            }

            memory.Write32(cpxArenaBase + 512 + (LinkScan), at);
            return StepResult.Pending;
        }

        if (phase == 92)
        {
            StepResult step = engine.Step();
            if (step is StepResult.Pending or StepResult.NeedIo)
                return step;
            if (step == StepResult.Failed)
                return Finish(0, ToDosError(engine.Error));
            memory.Write32(cpxArenaBase + 512 + (LinkBytes), engine.TransferredBytes);
            memory.Write32(cpxArenaBase + 512 + (LinkScan), 0);
            memory.Write32(cpxArenaBase + 512 + (LinkDecoded), 0);
            memory.Write32(cpxArenaBase + 512 + (LinkAbsolute), 0);
            memory.Write32(cpxArenaBase + 512 + (Phase), 93);
            return StepResult.Pending;
        }

        if (phase == 93)
        {
            uint at = memory.Read32(cpxArenaBase + 512 + (LinkScan)), decoded = memory.Read32(cpxArenaBase + 512 + (LinkDecoded));
            for (uint work = 0; work < 64 && at < memory.Read32(cpxArenaBase + 512 + (LinkBytes)); work++)
            {
                uint c = memory.Read8((cpxArenaBase + 3072) + at++);
                if (c >= 128)
                {
                    if (c is not (194 or 195) || at == memory.Read32(cpxArenaBase + 512 + (LinkBytes)))
                        return Finish(0, 210);
                    uint tail = memory.Read8((cpxArenaBase + 3072) + at++);
                    if (tail < 128 || tail > 191)
                        return Finish(0, 213);
                    c = ((c & 3) << 6) | (tail & 63);
                }

                if (c == 0)
                    return Finish(0, 213);
                if (c == 58)
                    memory.Write32(cpxArenaBase + 512 + (LinkAbsolute), decoded == 0 ? 1u : 2u);
                memory.Write8((cpxArenaBase + 3072) + decoded++, (byte)c);
            }

            memory.Write32(cpxArenaBase + 512 + (LinkScan), at);
            memory.Write32(cpxArenaBase + 512 + (LinkDecoded), decoded);
            if (at < memory.Read32(cpxArenaBase + 512 + (LinkBytes)))
                return StepResult.Pending;
            if (memory.Read32(cpxArenaBase + 512 + (Action)) == 26406)
                return BeginNewLinkTail(true);
            uint prefix = memory.Read32(cpxArenaBase + 512 + (LinkAbsolute)) == 0 ? memory.Read32(cpxArenaBase + 512 + (LinkPrefix)) : memory.Read32(cpxArenaBase + 512 + (LinkAbsolute)) == 1 ? 10u : 0u, suffix = memory.Read32(cpxArenaBase + 512 + (LinkSuffix));
            if (suffix < memory.Read32(cpxArenaBase + 512 + (PathLength)) && decoded != 0 && memory.Read8((cpxArenaBase + 3072) + decoded - 1)is 47 or 58)
                suffix++;
            memory.Write32(cpxArenaBase + 512 + (LinkPrefix), prefix);
            memory.Write32(cpxArenaBase + 512 + (LinkSuffix), suffix);
            uint total = prefix + decoded + memory.Read32(cpxArenaBase + 512 + (PathLength)) - suffix;
            if (total >= Arg(3))
                return Finish(0xfffffffe, 120);
            memory.Write32(cpxArenaBase + 512 + (LinkTotal), total);
            memory.Write32(cpxArenaBase + 512 + (LinkScan), 0);
            memory.Write32(cpxArenaBase + 512 + (Phase), 94);
            return StepResult.Pending;
        }

        if (phase == 94)
        {
            uint at = memory.Read32(cpxArenaBase + 512 + (LinkScan)), prefix = memory.Read32(cpxArenaBase + 512 + (LinkPrefix)), decoded = memory.Read32(cpxArenaBase + 512 + (LinkDecoded)), output = Arg(2);
            for (uint work = 0; work < 64 && at < memory.Read32(cpxArenaBase + 512 + (LinkTotal)); work++, at++)
            {
                byte c;
                if (at < prefix)
                    c = memory.Read32(cpxArenaBase + 512 + (LinkAbsolute)) == 1 ? VolumeNameByte(at) : memory.Read8((cpxArenaBase + 1024) + at);
                else if (at < prefix + decoded)
                    c = memory.Read8((cpxArenaBase + 3072) + at - prefix);
                else
                    c = memory.Read8((cpxArenaBase + 1024) + memory.Read32(cpxArenaBase + 512 + (LinkSuffix)) + at - prefix - decoded);
                memory.Write8(output + at, c);
            }

            memory.Write32(cpxArenaBase + 512 + (LinkScan), at);
            if (at < memory.Read32(cpxArenaBase + 512 + (LinkTotal)))
                return StepResult.Pending;
            memory.Write8(output + at, 0);
            return Finish(at);
        }

        return Finish(0, 209);
    }

    private static byte VolumeNameByte(uint at) => at switch
    {
        0 => 67,
        1 => 111,
        2 or 3 => 112,
        4 => 101,
        5 => 114,
        6 => 67,
        7 => 80,
        8 => 88,
        _ => 52
    };
    private StepResult CreateLink()
    {
        uint cpxArenaBase = arena;
        if (engine.LookupFound)
            return Finish(0, 203);
        uint at = memory.Read32(cpxArenaBase + 512 + (PathAt)), length = memory.Read32(cpxArenaBase + 512 + (PathLength)) - at;
        Wide parent = G((cpxArenaBase + 512) + ParentId);
        return Start(Arg(3) == 0 ? engine.BeginHardLink(G((cpxArenaBase + 512) + LinkTarget), parent, (cpxArenaBase + 1024) + at, length) : engine.BeginSymlink(parent, (cpxArenaBase + 1024) + at, length, (cpxArenaBase + 3072), memory.Read32(cpxArenaBase + 512 + (LinkBytes))), 31);
    }

    private StepResult PathObject()
    {
        uint cpxArenaBase = arena;
        if (!engine.ObjectFound)
            return Finish(0, 205);
        uint kind = memory.Read8(engine.ObjectRecord + 84);
        if (kind == 3)
        {
            if (memory.Read32(cpxArenaBase + 512 + (Action)) == 4097 && memory.Read32(cpxArenaBase + 512 + (PathAt)) == memory.Read32(cpxArenaBase + 512 + (PathLength)))
                return Resolved();
            if (memory.Read32(cpxArenaBase + 512 + (Action))is not (1024 or 26406))
                return Finish(0, 233);
            return Start(engine.BeginReadSymlink(G((cpxArenaBase + 512) + Object), (cpxArenaBase + 3072), 768), 92);
        }

        if (memory.Read32(cpxArenaBase + 512 + (Action)) == 26406 && kind == 1 && memory.Read32(engine.ObjectRecord + 76) > 1)
            return BeginCanonicalScan();
        if (memory.Read32(cpxArenaBase + 512 + (PathAt)) == memory.Read32(cpxArenaBase + 512 + (PathLength)))
            return memory.Read32(cpxArenaBase + 512 + (Action)) == 26406 ? Finish(0, 212) : memory.Read32(cpxArenaBase + 512 + (Action)) == 1024 ? Finish(0, 205) : Resolved();
        if (kind != 2)
            return Finish(0, 212);
        memory.Write32(cpxArenaBase + 512 + (Phase), 10);
        return StepResult.Pending;
    }
}