namespace CopperCPX;
public partial struct CpxDosDispatcher<TMemory>
    where TMemory : struct, ICpxMemory
{
    // Read-only operation union. The read engines use 8192..9535, reset the
    // namespace publication word at 10696, and use symlink scalars at 15524.
    // Input is snapshotted before lookup; the answer grows backwards so a
    // parent walk needs neither recursion nor an array of directory names.
    private uint NewPath => arena + 9600; // 1024 bytes including NUL
    private uint NewAnswer => arena + 10800; // 4096 bytes including NUL

    private const uint AnswerEnd = 4095;
    // Offsets relative to dispatcher State (arena + 512), starting at 14912.
    private const uint AnswerAt = 14400, PieceSource = 14404, PieceLeft = 14408, PieceNext = 14412, ObjectIndex = 14416, DirectoryId = 14424, DirectoryIndex = 14432, CursorHash = 14440, CursorEntry = 14448, BestEntry = 14456, BestParent = 14464, AncestorId = 14472, AncestorParent = 14480, RemainingLinks = 14488, HardTarget = 14492;
    private const uint ReadInput = 140, CopyPiece = 141, AddTarget = 142, AddPrefix = 143, ReadIndex = 144, ScanObject = 145, ScanEntry = 146, ReadAncestor = 147, ReadParent = 148, FindChild = 149, PublishAnswer = 150, CopyAnswer = 151, NextAncestor = 152;
    private StepResult BeginNewReadLink()
    {
        uint cpxArenaBase = arena;
        if (!LockId(Arg(0), out var id))
            return Finish(0, 211);
        if (Arg(1) == 0)
            return Finish(0, 115);
        Put((cpxArenaBase + 512) + Object, id);
        memory.Write32(cpxArenaBase + 512 + (LinkScan), 0);
        memory.Write32(cpxArenaBase + 512 + (PathAt), 0);
        memory.Write32(cpxArenaBase + 512 + (HardTarget), 0);
        memory.Write32(cpxArenaBase + 512 + (AnswerAt), AnswerEnd);
        memory.Write32(cpxArenaBase + 512 + (Phase), ReadInput);
        return StepResult.Pending;
    }

    private StepResult ReadNewLinkInput()
    {
        uint cpxArenaBase = arena;
        uint at = memory.Read32(cpxArenaBase + 512 + (LinkScan)), input = Arg(1);
        for (uint n = 0; n < 64; n++, at++)
        {
            if (input > uint.MaxValue - at)
                return Finish(0, 115);
            byte c = memory.Read8(input + at);
            if (c == 0)
            {
                memory.Write32(cpxArenaBase + 512 + (PathLength), at);
                memory.Write32(cpxArenaBase + 512 + (LinkScan), 0);
                memory.Write32(cpxArenaBase + 512 + (Phase), 10);
                return StepResult.Pending;
            }

            if (at == 1023)
                return Finish(0, 120);
            memory.Write8((cpxArenaBase + 9600) + at, c);
            if (c == 58)
            {
                Put((cpxArenaBase + 512) + Object, Wide.From(1));
                memory.Write32(cpxArenaBase + 512 + (PathAt), at + 1);
            }
        }

        memory.Write32(cpxArenaBase + 512 + (LinkScan), at);
        return StepResult.Pending;
    }

    private StepResult NewLinkComponent()
    {
        uint cpxArenaBase = arena;
        uint start = memory.Read32(cpxArenaBase + 512 + (PathAt)), length = memory.Read32(cpxArenaBase + 512 + (PathLength)), end = start + memory.Read32(cpxArenaBase + 512 + (LinkScan));
        if (start == length)
            return Finish(0, 212);
        for (uint n = 0; n < 64 && end < length && memory.Read8((cpxArenaBase + 9600) + end) != 47; n++)
            end++;
        if (end - start > 255)
            return Finish(0, 210);
        if (end < length && memory.Read8((cpxArenaBase + 9600) + end) != 47)
        {
            memory.Write32(cpxArenaBase + 512 + (LinkScan), end - start);
            return StepResult.Pending;
        }

        memory.Write32(cpxArenaBase + 512 + (LinkScan), 0);
        if (end == start)
            return Start(engine.BeginOperation(CpxOperation.ReadObject, G((cpxArenaBase + 512) + Object)), 14);
        Put((cpxArenaBase + 512) + ParentId, G((cpxArenaBase + 512) + Object));
        memory.Write32(cpxArenaBase + 512 + (PathEnd), end);
        return Start(engine.BeginLookup(G((cpxArenaBase + 512) + Object), (cpxArenaBase + 9600) + start, end - start), 11);
    }

    private StepResult BeginCanonicalScan()
    {
        uint cpxArenaBase = arena;
        memory.Write32(cpxArenaBase + 512 + (HardTarget), 1);
        memory.Write32(cpxArenaBase + 512 + (RemainingLinks), memory.Read32(engine.ObjectRecord + 76));
        Put((cpxArenaBase + 512) + BestEntry, default);
        Put((cpxArenaBase + 512) + DirectoryId, default);
        return Start(engine.BeginOperation(CpxOperation.ReadMetadata, engine.StatePageAddress), ReadIndex);
    }

    private StepResult NextDirectory(bool first)
    {
        uint cpxArenaBase = arena;
        return Start(engine.BeginTreeRead(CpxTreeKind.Object, G((cpxArenaBase + 512) + ObjectIndex), default, G((cpxArenaBase + 512) + DirectoryId), default, first ? CpxSeek.Next : CpxSeek.After), ScanObject);
    }

    private StepResult NextEntry(bool first, uint phase)
    {
        uint cpxArenaBase = arena;
        if (first)
        {
            Put((cpxArenaBase + 512) + CursorHash, default);
            Put((cpxArenaBase + 512) + CursorEntry, default);
        }

        return Start(engine.BeginTreeRead(CpxTreeKind.Directory, G((cpxArenaBase + 512) + DirectoryIndex), G((cpxArenaBase + 512) + DirectoryId), G((cpxArenaBase + 512) + CursorHash), G((cpxArenaBase + 512) + CursorEntry), first ? CpxSeek.Next : CpxSeek.After), phase);
    }

    private StepResult ScanLinkEntry(uint phase)
    {
        uint cpxArenaBase = arena;
        if (!engine.TreeFound)
            return phase == FindChild ? Finish(0, 213) : NextDirectory(false);
        uint rec = engine.TreeRecord;
        Wide hash = G(rec), entry = G(rec + 8);
        int order = Wide.Compare(hash, G((cpxArenaBase + 512) + CursorHash));
        if (entry.IsZero || order < 0 || order == 0 && Wide.Compare(entry, G((cpxArenaBase + 512) + CursorEntry)) <= 0)
            return Finish(0, 213);
        Put((cpxArenaBase + 512) + CursorHash, hash);
        Put((cpxArenaBase + 512) + CursorEntry, entry);
        if (phase == FindChild)
        {
            if (!Wide.Equal(G(rec + 16), G((cpxArenaBase + 512) + AncestorId)))
                return NextEntry(false, phase);
            CopyInfo((cpxArenaBase + 3072), rec, 128);
            return EntryName();
        }

        if (!Wide.Equal(G(rec + 16), G((cpxArenaBase + 512) + Object)))
            return NextEntry(false, phase);
        Wide best = G((cpxArenaBase + 512) + BestEntry);
        if (best.IsZero || Wide.Compare(entry, best) < 0)
        {
            Put((cpxArenaBase + 512) + BestEntry, entry);
            Put((cpxArenaBase + 512) + BestParent, G((cpxArenaBase + 512) + DirectoryId));
            CopyInfo((cpxArenaBase + 3072), rec, 128);
        }

        memory.Write32(cpxArenaBase + 512 + (RemainingLinks), memory.Read32(cpxArenaBase + 512 + (RemainingLinks)) - 1);
        if (memory.Read32(cpxArenaBase + 512 + (RemainingLinks)) != 0)
            return NextEntry(false, phase);
        if (Wide.Equal(G((cpxArenaBase + 512) + BestEntry), G((cpxArenaBase + 512) + EntryId)))
            return Finish(0, 212);
        return BeginNewLinkTail(false);
    }

    private StepResult PrependPiece(uint source, uint length, uint next)
    {
        uint cpxArenaBase = arena;
        if (length > memory.Read32(cpxArenaBase + 512 + (AnswerAt)))
            return Finish(0xfffffffe, 120);
        memory.Write32(cpxArenaBase + 512 + (PieceSource), source);
        memory.Write32(cpxArenaBase + 512 + (PieceLeft), length);
        memory.Write32(cpxArenaBase + 512 + (PieceNext), next);
        memory.Write32(cpxArenaBase + 512 + (Phase), length == 0 ? next : CopyPiece);
        return StepResult.Pending;
    }

    private StepResult BeginNewLinkTail(bool soft)
    {
        uint cpxArenaBase = arena;
        uint suffix = memory.Read32(cpxArenaBase + 512 + (LinkSuffix));
        if (soft && suffix < memory.Read32(cpxArenaBase + 512 + (PathLength)) && memory.Read32(cpxArenaBase + 512 + (LinkDecoded)) != 0 && memory.Read8((cpxArenaBase + 3072) + memory.Read32(cpxArenaBase + 512 + (LinkDecoded)) - 1)is 47 or 58)
            suffix++;
        return PrependPiece((cpxArenaBase + 9600) + suffix, memory.Read32(cpxArenaBase + 512 + (PathLength)) - suffix, AddTarget);
    }

    private bool PrependSeparator(byte separator)
    {
        uint cpxArenaBase = arena;
        uint at = memory.Read32(cpxArenaBase + 512 + (AnswerAt));
        if (at == 0)
            return false;
        memory.Write8((cpxArenaBase + 10800) + at - 1, separator);
        memory.Write32(cpxArenaBase + 512 + (AnswerAt), at - 1);
        return true;
    }

    private StepResult PrependVolume(bool colon)
    {
        if (colon && !PrependSeparator(58))
            return Finish(0xfffffffe, 120);
        return PrependPiece(engine.VolumeLabelAddress + 1, memory.Read8(engine.VolumeLabelAddress), PublishAnswer);
    }

    private StepResult WalkNewLinkParent(Wide id)
    {
        if (Wide.Equal(id, Wide.From(1)))
            return PrependVolume(true);
        if (id.IsZero)
            return Finish(0, 213);
        Put((arena + 512) + AncestorId, id);
        return Start(engine.BeginOperation(CpxOperation.ReadObject, id), ReadAncestor);
    }

    private StepResult PrependNewLinkName()
    {
        uint cpxArenaBase = arena;
        // HardTarget changes from 1 to 0 after the canonical filename. Every
        // subsequent component comes from the link's actual directory ancestry.
        bool file = memory.Read32(cpxArenaBase + 512 + (HardTarget)) != 0;
        if (!file && !PrependSeparator(47))
            return Finish(0xfffffffe, 120);
        memory.Write32(cpxArenaBase + 512 + (HardTarget), 0);
        return PrependPiece((cpxArenaBase + 3456) + 9, memory.Read8((cpxArenaBase + 3456) + 8), NextAncestor);
    }

    private StepResult NewReadLinkStep()
    {
        uint cpxArenaBase = arena;
        uint phase = memory.Read32(cpxArenaBase + 512 + (Phase));
        switch (phase)
        {
            case ReadInput:
                return ReadNewLinkInput();
            case CopyPiece:
                uint left = memory.Read32(cpxArenaBase + 512 + (PieceLeft)), count = left > 64 ? 64 : left, pos = memory.Read32(cpxArenaBase + 512 + (AnswerAt)) - count;
                CopyInfo((cpxArenaBase + 10800) + pos, memory.Read32(cpxArenaBase + 512 + (PieceSource)) + left - count, count);
                memory.Write32(cpxArenaBase + 512 + (AnswerAt), pos);
                memory.Write32(cpxArenaBase + 512 + (PieceLeft), left - count);
                if (count == left)
                    memory.Write32(cpxArenaBase + 512 + (Phase), memory.Read32(cpxArenaBase + 512 + (PieceNext)));
                return StepResult.Pending;
            case AddTarget:
                if (memory.Read32(cpxArenaBase + 512 + (HardTarget)) != 0)
                {
                    Put((cpxArenaBase + 512) + AncestorParent, G((cpxArenaBase + 512) + BestParent));
                    return EntryName();
                }

                return PrependPiece((cpxArenaBase + 3072), memory.Read32(cpxArenaBase + 512 + (LinkDecoded)), AddPrefix);
            case AddPrefix:
                if (memory.Read32(cpxArenaBase + 512 + (LinkAbsolute)) == 0)
                    return WalkNewLinkParent(G((cpxArenaBase + 512) + ParentId));
                if (memory.Read32(cpxArenaBase + 512 + (LinkAbsolute)) == 1)
                    return PrependVolume(false);
                memory.Write32(cpxArenaBase + 512 + (Phase), PublishAnswer);
                return StepResult.Pending;
            case NextAncestor:
                return WalkNewLinkParent(G((cpxArenaBase + 512) + AncestorParent));
            case PublishAnswer:
                uint required = AnswerEnd - memory.Read32(cpxArenaBase + 512 + (AnswerAt)) + 1, output = Arg(2);
                if (output == 0)
                    return Finish(required);
                if (Arg(3) > int.MaxValue || Arg(3) < required)
                    return Finish(0xfffffffe, 120);
                if (output > uint.MaxValue - (required - 1))
                    return Finish(0, 115);
                memory.Write32(cpxArenaBase + 512 + (LinkScan), 0);
                memory.Write32(cpxArenaBase + 512 + (Phase), CopyAnswer);
                return StepResult.Pending;
            case CopyAnswer:
                uint at = memory.Read32(cpxArenaBase + 512 + (LinkScan)), length = AnswerEnd - memory.Read32(cpxArenaBase + 512 + (AnswerAt)), remaining = length - at, take = remaining > 64 ? 64 : remaining;
                CopyInfo(Arg(2) + at, (cpxArenaBase + 10800) + memory.Read32(cpxArenaBase + 512 + (AnswerAt)) + at, take);
                memory.Write32(cpxArenaBase + 512 + (LinkScan), at + take);
                if (take < remaining)
                    return StepResult.Pending;
                memory.Write8(Arg(2) + length, 0);
                return Finish(length);
        }

        StepResult step = engine.Step();
        if (step is StepResult.Pending or StepResult.NeedIo)
            return step;
        if (step == StepResult.Failed)
            return Finish(0, ToDosError(engine.Error));
        switch (phase)
        {
            case ReadIndex:
                if (U16(engine.MetadataBuffer + 4) != 3 || !G(engine.MetadataBuffer + 24).IsZero)
                    return Finish(0, 213);
                Put((cpxArenaBase + 512) + ObjectIndex, G(engine.MetadataBuffer + 80));
                return NextDirectory(true);
            case ScanObject:
                if (!engine.TreeFound || Wide.Compare(G(engine.TreeRecord), G((cpxArenaBase + 512) + DirectoryId)) <= 0)
                    return Finish(0, 213);
                Put((cpxArenaBase + 512) + DirectoryId, G(engine.TreeRecord));
                if (memory.Read8(engine.TreeRecord + 84) != 2 || memory.Read32(engine.TreeRecord + 76) == 0)
                    return NextDirectory(false);
                Put((cpxArenaBase + 512) + DirectoryIndex, G(engine.TreeRecord + 32));
                return NextEntry(true, ScanEntry);
            case ScanEntry:
            case FindChild:
                return ScanLinkEntry(phase);
            case ReadAncestor:
                if (!engine.ObjectFound || memory.Read8(engine.ObjectRecord + 84) != 2)
                    return Finish(0, 213);
                Wide parent = G(engine.ObjectRecord + 40);
                if (parent.IsZero || Wide.Equal(parent, G((cpxArenaBase + 512) + AncestorId)))
                    return Finish(0, 213);
                Put((cpxArenaBase + 512) + AncestorParent, parent);
                return Start(engine.BeginOperation(CpxOperation.ReadObject, parent), ReadParent);
            case ReadParent:
                if (!engine.ObjectFound || memory.Read8(engine.ObjectRecord + 84) != 2)
                    return Finish(0, 213);
                Put((cpxArenaBase + 512) + DirectoryId, G((cpxArenaBase + 512) + AncestorParent));
                Put((cpxArenaBase + 512) + DirectoryIndex, G(engine.ObjectRecord + 32));
                return NextEntry(true, FindChild);
            default:
                return Finish(0, 213);
        }
    }
}