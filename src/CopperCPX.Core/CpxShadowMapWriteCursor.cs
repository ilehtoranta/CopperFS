namespace CopperCPX;

/// <summary>Builds a new CPX5 mapping root using a bounded postorder walk.
/// The I/O owner supplies authenticated geometry, validates completion tokens
/// and calls AcceptIo only after the borrowed frame buffer is quiescent.</summary>
public struct CpxShadowMapWriteCursor<TMemory> where TMemory : struct, ICpxMemory
{
    public const uint StateBytes = 1024;
    private const uint ContextMagic = 0x43534d35;
    private TMemory memory;
    private CpxShadowMapGeometry geometry;
    public uint Arena;
    private const uint Signature = 0, Status = 4, ErrorAt = 8, Phase = 12,
        Count = 16, Cursor = 20, Generation = 24, Identity = 32, Root = 40,
        ResultRoot = 48, Descriptors = 56, Buffers = 60, Depth = 64,
        IoSector = 72, IoBuffer = 80, IoKindAt = 84, Requested = 88, CancelAt = 92,
        ChecksumTable = 96, ChecksumZeros = 100, ChecksumWord = 104, Seeded = 108;
    private const uint Order = 128, Frames = 640, FrameBytes = 48;
    private const uint Source = 0, NodeIndex = 8, Target = 16, First = 24,
        End = 28, Next = 32, ParentSlot = 36, TargetCopy = 40;

    public CpxShadowMapWriteCursor(TMemory memory, uint arena, CpxShadowMapGeometry geometry)
    { this.memory = memory; Arena = arena; this.geometry = geometry; }
    private uint R(uint at) => memory.Read32(Arena + at);
    private void W(uint at, uint value) => memory.Write32(Arena + at, value);
    private ulong G(uint at) => ((ulong)memory.Read32(at) << 32) | memory.Read32(at + 4);
    private void Put(uint at, ulong value) { memory.Write32(at, (uint)(value >> 32)); memory.Write32(at + 4, (uint)value); }
    private uint Frame => Arena + Frames + memory.Read32(Arena + (Depth)) * FrameBytes;
    private uint Image => memory.Read32(Arena + (Buffers)) + memory.Read32(Arena + (Depth)) * geometry.PageBytes;
    private uint Descriptor(uint ordinal) => memory.Read32(Arena + (Descriptors)) + memory.Read32(Arena + Order + ordinal * 4) * 32;
    private ulong Pair(uint ordinal) => G(Descriptor(ordinal) + 8);
    public CpxError Error => (CpxError)memory.Read32(Arena + (ErrorAt));
    public Wide MappingRoot => G(Arena + ResultRoot);
    public IoKind RequestKind => (IoKind)memory.Read32(Arena + (IoKindAt));
    public Wide RequestSector => G(Arena + IoSector);
    public uint RequestBuffer => memory.Read32(Arena + (IoBuffer));
    public uint RequestBytes => geometry.PageBytes;

    // Descriptors have the publication kernel's 32-byte layout. Only their
    // selected-copy field at +20 is changed. All images and buffers are pinned.
    public CpxError Begin(Wide root, Wide generation, Wide volumeIdentity, uint descriptors, uint count, uint buffers,
        uint checksumTable = 0, uint checksumZeros = 0)
        => BeginCore(root, generation, volumeIdentity, descriptors, count, buffers, checksumTable, checksumZeros, false);

    // Only the owning filesystem uses this when promoting its authenticated
    // checkpoint catalogue. Map-only records have no image and retain +20's
    // flags; image records carry their old flags in +24. Public admission stays
    // limited to the ordinary 42-image transaction.
    internal CpxError BeginCatalogue(Wide generation, Wide volumeIdentity, uint descriptors, uint count, uint buffers,
        uint checksumTable, uint checksumZeros)
        => BeginCore(default, generation, volumeIdentity, descriptors, count, buffers, checksumTable, checksumZeros, true);

    private CpxError BeginCore(Wide root, Wide generation, Wide volumeIdentity, uint descriptors, uint count, uint buffers,
        uint checksumTable, uint checksumZeros, bool seeded)
    {
        if (!Buffer(Arena, StateBytes) || geometry.Height == 0 || !Buffer(descriptors, count * 32)
            || count == 0 || count > (seeded ? 128u : CpxArena.TransactionImages) || seeded && (!root.IsZero || !geometry.HasInodeCatalogue) || volumeIdentity.IsZero || (ulong)generation == ulong.MaxValue
            || !Buffer(buffers, geometry.Height * geometry.PageBytes)
            || Overlap(Arena, StateBytes, descriptors, count * 32)
            || Overlap(Arena, StateBytes, buffers, geometry.Height * geometry.PageBytes)
            || Overlap(descriptors, count * 32, buffers, geometry.Height * geometry.PageBytes))
            return CpxError.InvalidArgument;
        if (memory.Read32(Arena + (Signature)) == ContextMagic && (memory.Read32(Arena + (Requested)) != 0 || memory.Read32(Arena + (Status)) == 1)) return CpxError.Busy;
        ulong rootPair = geometry.NodePairSector(geometry.Height - 1, 0);
        if ((ulong)root != 0 && (ulong)root != rootPair && (ulong)root != rootPair + geometry.PageSectors) return CpxError.Corrupt;
        for (uint at = 0; at < StateBytes; at += 4) memory.Write32(Arena + (at), 0);
        memory.Write32(Arena + (Signature), ContextMagic); memory.Write32(Arena + (Status), 1); memory.Write32(Arena + (Count), count);
        memory.Write32(Arena + (Seeded), seeded ? 1u : 0u);
        memory.Write32(Arena + (Descriptors), descriptors); memory.Write32(Arena + (Buffers), buffers);
        memory.Write32(Arena + (ChecksumTable), checksumTable); memory.Write32(Arena + (ChecksumZeros), checksumZeros);
        Put(Arena + Root, root); Put(Arena + Generation, generation); Put(Arena + Identity, volumeIdentity);
        return CpxError.None;
    }

    public CpxError AcceptIo(bool success)
    {
        if (memory.Read32(Arena + (Requested)) == 0 || memory.Read32(Arena + (Status)) != 1) return CpxError.StaleCompletion;
        memory.Write32(Arena + (Requested), 0);
        if (!success) { Fail(CpxError.Io); return CpxError.Io; }
        if (memory.Read32(Arena + (CancelAt)) != 0) { Fail(CpxError.Cancelled); return CpxError.Cancelled; }
        return CpxError.None;
    }
    public void Cancel() { memory.Write32(Arena + (CancelAt), 1); if (memory.Read32(Arena + (Requested)) == 0) Fail(CpxError.Cancelled); }

    public StepResult Step()
    {
        if (memory.Read32(Arena + (Signature)) != ContextMagic || memory.Read32(Arena + (Status)) == 3) return StepResult.Failed;
        if (memory.Read32(Arena + (Status)) == 2) return StepResult.Complete;
        if (memory.Read32(Arena + (Requested)) != 0) return StepResult.NeedIo;
        if (memory.Read32(Arena + (CancelAt)) != 0) return Fail(CpxError.Cancelled);
        uint frame = (Arena + Frames + memory.Read32(Arena + (Depth)) * FrameBytes), image = (memory.Read32(Arena + (Buffers)) + memory.Read32(Arena + (Depth)) * geometry.PageBytes), level = geometry.Height - 1 - memory.Read32(Arena + (Depth));
        switch (memory.Read32(Arena + (Phase)))
        {
            case 0:
                uint cursor = memory.Read32(Arena + (Cursor)), descriptor = memory.Read32(Arena + (Descriptors)) + cursor * 32;
                ulong pair = G(descriptor + 8); uint sourceImage = memory.Read32(descriptor + 16);
                if (!geometry.ContainsPair(pair) || pair >= geometry.MapBaseSector && pair < geometry.MapEndSector
                    || (memory.Read32(Arena + (Seeded)) == 0 || sourceImage != 0) && (!Buffer(sourceImage, geometry.PageBytes)
                    || Overlap(sourceImage, geometry.PageBytes, Arena, StateBytes)
                    || Overlap(sourceImage, geometry.PageBytes, memory.Read32(Arena + (Buffers)), geometry.Height * geometry.PageBytes)
                    || Overlap(sourceImage, geometry.PageBytes, memory.Read32(Arena + (Descriptors)), memory.Read32(Arena + (Count)) * 32))
                    || memory.Read32(Arena + (Seeded)) != 0 && (memory.Read32(descriptor + 20) > 3 || G(descriptor + 24) > 3)) return Fail(CpxError.Corrupt);
                for (uint previous = 0; previous < cursor; previous++)
                    if (G(memory.Read32(Arena + (Descriptors)) + previous * 32 + 8) == pair) return Fail(CpxError.Corrupt);
                memory.Write32(Arena + Order + cursor * 4, cursor); memory.Write32(Arena + (Cursor), cursor + 1);
                if (cursor + 1 == memory.Read32(Arena + (Count))) { memory.Write32(Arena + (Cursor), 1); memory.Write32(Arena + (Phase), 1); }
                return StepResult.Pending;
            case 1:
                uint at = memory.Read32(Arena + (Cursor));
                if (at < memory.Read32(Arena + (Count)))
                {
                    uint item = memory.Read32(Arena + Order + at * 4), next = at;
                    ulong key = G(memory.Read32(Arena + (Descriptors)) + item * 32 + 8);
                    while (next != 0 && Pair(next - 1) > key)
                    { memory.Write32(Arena + Order + next * 4, memory.Read32(Arena + Order + (next - 1) * 4)); next--; }
                    memory.Write32(Arena + Order + next * 4, item); memory.Write32(Arena + (Cursor), at + 1);
                    return StepResult.Pending;
                }
                Put(frame + Source, G(Arena + Root)); Put(frame + NodeIndex, 0);
                memory.Write32(frame + First, 0); memory.Write32(frame + End, memory.Read32(Arena + (Count))); memory.Write32(Arena + (Phase), 2);
                return StepResult.Pending;
            case 2:
                ulong source = G(frame + Source);
                if (source != 0) return Issue(IoKind.Read, source, image, 3);
                ClearNode(image, level, G(frame + NodeIndex));
                memory.Write32(frame + TargetCopy, 0);
                Put(frame + Target, geometry.NodePairSector(level, G(frame + NodeIndex)));
                memory.Write32(frame + Next, memory.Read32(frame + First)); memory.Write32(Arena + (Phase), 4);
                return StepResult.Pending;
            case 3:
                if (!ValidNode(image, level, G(frame + NodeIndex))) return Fail(CpxError.Corrupt);
                ulong baseSector = geometry.NodePairSector(level, G(frame + NodeIndex));
                uint copy = G(frame + Source) == baseSector ? 1u : 0u;
                memory.Write32(frame + TargetCopy, copy); Put(frame + Target, baseSector + copy * geometry.PageSectors);
                Put(image + 32, G(Arena + Generation) + 1);
                memory.Write32(frame + Next, memory.Read32(frame + First)); memory.Write32(Arena + (Phase), 4);
                return StepResult.Pending;
            case 4:
                uint ordinal = memory.Read32(frame + Next), end = memory.Read32(frame + End);
                if (ordinal == end)
                {
                    memory.Write32(image + 8, Crc(image));
                    return Issue(IoKind.Write, G(frame + Target), image, 5);
                }
                ulong index = geometry.PairIndex(Pair(ordinal));
                if (level == 0)
                {
                    if (geometry.HasInodeCatalogue)
                    {
                        uint element = (uint)(index & (geometry.LeafEntries - 1)), byteAt = image + 64 + (element >> 2), shiftAt = 6 - (element & 3) * 2;
                        uint oldByte = memory.Read8(byteAt), previous = (oldByte >> (int)shiftAt) & 3u;
                        uint descriptorAt = Descriptor(ordinal), sourceAt = memory.Read32(descriptorAt + 16);
                        if (memory.Read32(Arena + (Seeded)) != 0) previous = (uint)G(descriptorAt + 24);
                        uint entry;
                        if (sourceAt == 0) entry = memory.Read32(descriptorAt + 20);
                        else
                        {
                            uint inode = memory.Read16(sourceAt + 4) == 23 && G(sourceAt + 24) != 0 ? 2u : 0u;
                            entry = inode | (1u - (previous & 1));
                            memory.Write32(descriptorAt + 20, previous & 1);
                        }
                        memory.Write8(byteAt, (byte)((oldByte & ~(3u << (int)shiftAt)) | entry << (int)shiftAt));
                        memory.Write32(frame + Next, ordinal + 1); return StepResult.Pending;
                    }
                    uint bit = (uint)(index & (geometry.LeafEntries - 1)), location = image + 64 + (bit >> 3), mask = 0x80u >> (int)(bit & 7);
                    uint value = memory.Read8(location), oldCopy = (value & mask) == 0 ? 0u : 1u;
                    memory.Write32(Descriptor(ordinal) + 20, oldCopy);
                    memory.Write8(location, (byte)(value ^ mask));
                    memory.Write32(frame + Next, ordinal + 1); return StepResult.Pending;
                }
                uint shift = geometry.LeafShift + (level - 1) * geometry.BranchShift;
                uint child = (uint)((index >> (int)shift) & (geometry.BranchEntries - 1));
                uint limit = ordinal + 1;
                while (limit < end && ((geometry.PairIndex(Pair(limit)) >> (int)shift) & (geometry.BranchEntries - 1)) == child) limit++;
                uint selected = ((uint)memory.Read8(image + 64 + (child >> 2)) >> (int)(6 - (child & 3) * 2)) & 3;
                if (selected == 3) return Fail(CpxError.Corrupt);
                ulong childNode = (G(frame + NodeIndex) << (int)geometry.BranchShift) + child;
                uint childFrame = frame + FrameBytes;
                memory.Write32(frame + Next, limit);
                Put(childFrame + Source, selected == 0 ? 0 : (ulong)geometry.NodePairSector(level - 1, childNode) + (selected - 1) * geometry.PageSectors);
                Put(childFrame + NodeIndex, childNode);
                memory.Write32(childFrame + First, ordinal); memory.Write32(childFrame + End, limit); memory.Write32(childFrame + ParentSlot, child);
                memory.Write32(Arena + (Depth), memory.Read32(Arena + (Depth)) + 1); memory.Write32(Arena + (Phase), 2); return StepResult.Pending;
            case 5:
                if (memory.Read32(Arena + (Depth)) == 0) { Put(Arena + ResultRoot, G(frame + Target)); memory.Write32(Arena + (Status), 2); return StepResult.Complete; }
                uint parentImage = image - geometry.PageBytes, slot = memory.Read32(frame + ParentSlot);
                uint byteAddress = parentImage + 64 + (slot >> 2), bitShift = 6 - (slot & 3) * 2;
                uint old = memory.Read8(byteAddress), selection = memory.Read32(frame + TargetCopy) + 1;
                memory.Write8(byteAddress, (byte)((old & ~(3u << (int)bitShift)) | selection << (int)bitShift));
                memory.Write32(Arena + (Depth), memory.Read32(Arena + (Depth)) - 1); memory.Write32(Arena + (Phase), 4); return StepResult.Pending;
            default: return Fail(CpxError.Corrupt);
        }
    }

    private void ClearNode(uint image, uint level, ulong index)
    {
        for (uint at = 0; at < geometry.PageBytes; at += 4) memory.Write32(image + at, 0);
        memory.Write32(image, CpxShadowCommit<TMemory>.Magic); memory.Write16(image + 4, level == 0 ? 20u : 21u); memory.Write16(image + 6, 5);
        memory.Write32(image + 12, level == 0 && geometry.HasInodeCatalogue ? 2u : 0u);
        Put(image + 16, 0x8000000000000000ul | (ulong)geometry.NodePairSector(level, index));
        Put(image + 24, G(Arena + Identity)); Put(image + 32, G(Arena + Generation) + 1);
        uint entries = geometry.Entries(level, index);
        memory.Write16(image + 48, entries); memory.Write16(image + 50, level);
        memory.Write16(image + 52, level == 0 && !geometry.HasInodeCatalogue ? (entries + 7) >> 3 : (entries + 3) >> 2);
    }
    private bool ValidNode(uint image, uint level, ulong index)
    {
        uint entries = geometry.Entries(level, index);
        return entries != 0 && memory.Read32(image) == CpxShadowCommit<TMemory>.Magic && memory.Read16(image + 6) == 5
            && memory.Read16(image + 4) == (level == 0 ? 20u : 21u)
            && memory.Read32(image + 12) == (level == 0 && geometry.HasInodeCatalogue ? 2u : 0u)
            && G(image + 16) == (0x8000000000000000ul | (ulong)geometry.NodePairSector(level, index)) && G(image + 24) == G(Arena + Identity)
            && G(image + 32) != 0 && G(image + 32) <= G(Arena + Generation)
            && memory.Read16(image + 48) == entries && memory.Read16(image + 50) == level
            && memory.Read16(image + 52) == (level == 0 && !geometry.HasInodeCatalogue ? (entries + 7) >> 3 : (entries + 3) >> 2)
            && memory.Read32(image + 8) == Crc(image);
    }
    private uint Crc(uint buffer)
        => CpxShadowChecksums<TMemory>.Compute(ref memory, buffer, geometry.PageBytes, memory.Read32(Arena + (ChecksumTable)), memory.Read32(Arena + (ChecksumZeros)), Arena + ChecksumWord);
    private StepResult Issue(IoKind kind, ulong sector, uint buffer, uint next)
    { memory.Write32(Arena + (IoKindAt), (uint)kind); Put(Arena + IoSector, sector); memory.Write32(Arena + (IoBuffer), buffer); memory.Write32(Arena + (Phase), next); memory.Write32(Arena + (Requested), 1); return StepResult.NeedIo; }
    private StepResult Fail(CpxError error) { memory.Write32(Arena + (ErrorAt), (uint)error); memory.Write32(Arena + (Status), 3); return StepResult.Failed; }
    private static bool Buffer(uint at, uint bytes) => at != 0 && (at & 3) == 0 && at <= uint.MaxValue - bytes;
    private static bool Overlap(uint a, uint lengthA, uint b, uint lengthB) => (ulong)a < (ulong)b + lengthB && (ulong)b < (ulong)a + lengthA;
}
