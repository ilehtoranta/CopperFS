namespace CopperCPX;

/// <summary>Experimental CPX5 publication kernel. All state and buffers belong
/// to the caller. Metadata pairs preserve the selected version until a new
/// checkpoint is durable. The caller supplies an explicit mapping root; a page's
/// generation alone never authorizes its visibility. This kernel does not mount
/// or reinterpret CPX4 media.</summary>
public struct CpxShadowCommit<TMemory> where TMemory : struct, ICpxMemory
{
    public const uint Magic = 0x43505835, StateBytes = 160, DescriptorBytes = 32;
    private const uint ContextMagic = 0x43535035;
    private TMemory memory;
    public uint Arena;
    private const uint Signature = 0, Page = 4, SectorShift = 8, Phase = 12,
        Generation = 16, Identity = 24, Capacity = 32, Roots = 40,
        Survivor = 48, Workspace = 52, Descriptors = 56, Count = 60,
        Cursor = 64, RootCursor = 68, FirstNewRoot = 72, Waiting = 76,
        Token = 80, NextToken = 84, ErrorAt = 88, Status = 92, CancelAt = 96,
        Kind = 100, PendingSector = 104, PendingBuffer = 112, PendingBytes = 116,
        NextMappingRoot = 120, DurableMappingRootAt = 128, ChecksumTable = 136, ChecksumZeros = 140, ChecksumWord = 144, LogicalAddressShift = 148, OwnedImages = 152, ControlRoot = 156;

    public CpxShadowCommit(TMemory memory, uint arena) { this.memory = memory; Arena = arena; }
    private uint R(uint at) => memory.Read32(Arena + at);
    private void W(uint at, uint value) => memory.Write32(Arena + at, value);
    private ulong G(uint at) => ((ulong)memory.Read32(at) << 32) | memory.Read32(at + 4);
    private void Put(uint at, ulong value) { memory.Write32(at, (uint)(value >> 32)); memory.Write32(at + 4, (uint)value); }
    private uint RootBytes => (memory.Read32(Arena + (OwnedImages)) & 2) != 0 && memory.Read32(Arena + (Page)) < 1024 ? 1024u : memory.Read32(Arena + (Page));
    public CpxError Error => (CpxError)memory.Read32(Arena + (ErrorAt));
    public Wide DurableGeneration => (((ulong)memory.Read32((Arena + Generation)) << 32) | memory.Read32((Arena + Generation) + 4));
    public Wide DurableMappingRoot => (((ulong)memory.Read32((Arena + DurableMappingRootAt)) << 32) | memory.Read32((Arena + DurableMappingRootAt) + 4));
    public uint SurvivingCheckpoint => memory.Read32(Arena + (Survivor));

    public CpxError Initialize(uint sectorBytes, uint pageBytes, Wide capacitySectors,
        Wide volumeIdentity, Wide generation, Wide rootSector, uint survivor, uint workspace, Wide mappingRoot = default,
        uint checksumTable = 0, uint checksumZeros = 0, uint logicalAddressShift = 0)
    {
        if (Arena == 0 || (Arena & 3) != 0 || Arena > uint.MaxValue - StateBytes) return CpxError.InvalidArgument;
        if (memory.Read32(Arena + Signature) == ContextMagic && memory.Read32(Arena + (Waiting)) != 0) return CpxError.Busy;
        if (!CpxArena.ValidUnit(sectorBytes) || pageBytes < sectorBytes || pageBytes > 4096
            || (pageBytes & (pageBytes - 1)) != 0 || logicalAddressShift > 3 || survivor >= 3 || volumeIdentity.IsZero
            || !Buffer(workspace, pageBytes) || Overlap(Arena, StateBytes, workspace, pageBytes))
            return CpxError.InvalidArgument;
        ulong capacity = capacitySectors, roots = rootSector;
        uint sectors = pageBytes / sectorBytes;
        if (capacity < 3 * sectors || roots > capacity - 3 * sectors) return CpxError.InvalidArgument;
        uint next = memory.Read32(Arena + Signature) == ContextMagic ? memory.Read32(Arena + (NextToken)) : 0;
        for (uint at = 0; at < StateBytes; at += 4) memory.Write32(Arena + (at), 0);
        memory.Write32(Arena + (Signature), ContextMagic); memory.Write32(Arena + (Page), pageBytes); memory.Write32(Arena + (SectorShift), (uint)CpxArena.Shift(sectorBytes));
        Put(Arena + Generation, generation); Put(Arena + Identity, volumeIdentity);
        Put(Arena + Capacity, capacity); Put(Arena + Roots, roots);
        Put(Arena + DurableMappingRootAt, mappingRoot);
        memory.Write32(Arena + (ChecksumTable), checksumTable); memory.Write32(Arena + (ChecksumZeros), checksumZeros);
        memory.Write32(Arena + (LogicalAddressShift), logicalAddressShift);
        memory.Write32(Arena + (Survivor), survivor); memory.Write32(Arena + (Workspace), workspace); memory.Write32(Arena + (NextToken), next);
        return CpxError.None;
    }

    // Each record: logical page tag (u64), physical pair sector (u64), owned
    // image address (u32), currently selected copy (u32), reserved zero (u64).
    // The caller owns allocation/namespace validation and pins this write set.
    public CpxError Begin(uint descriptors, uint count, Wide mappingRoot)
        => BeginCore(descriptors, count, mappingRoot, false);

    private CpxError BeginCore(uint descriptors, uint count, Wide mappingRoot, bool allowEmpty)
    {
        if (memory.Read32(Arena + (Signature)) != ContextMagic) return CpxError.InvalidArgument;
        if (memory.Read32(Arena + (Waiting)) != 0 || memory.Read32(Arena + (Status)) == 1) return CpxError.Busy;
        if (memory.Read32(Arena + (Status)) == 3) return Error;
        if ((((ulong)memory.Read32((Arena + Generation)) << 32) | memory.Read32((Arena + Generation) + 4)) == ulong.MaxValue || count == 0 && !allowEmpty || count > CpxArena.TransactionImages
            || !Buffer(descriptors, count * DescriptorBytes)
            || Overlap(descriptors, count * DescriptorBytes, Arena, StateBytes)
            || Overlap(descriptors, count * DescriptorBytes, memory.Read32(Arena + (Workspace)), ((memory.Read32(Arena + (OwnedImages)) & 2) != 0 && memory.Read32(Arena + (Page)) < 1024 ? 1024u : memory.Read32(Arena + (Page)))))
            return CpxError.InvalidArgument;
        memory.Write32(Arena + (Descriptors), descriptors); memory.Write32(Arena + (Count), count); memory.Write32(Arena + (Cursor), 0); memory.Write32(Arena + (RootCursor), 0);
        memory.Write32(Arena + (OwnedImages), memory.Read32(Arena + (OwnedImages)) & 2);
        Put(Arena + NextMappingRoot, mappingRoot);
        memory.Write32(Arena + (Phase), 0); memory.Write32(Arena + (ErrorAt), 0); memory.Write32(Arena + (CancelAt), 0); memory.Write32(Arena + (Status), 1);
        return CpxError.None;
    }

    // The engine admits logical targets and headers before sealing its pinned
    // contiguous image array; the map writer admits its unique physical pairs
    // and all descriptor/buffer ranges. Those owned invariants make repeating
    // public untrusted-descriptor admission unnecessary here.
    internal CpxError BeginOwnedImages(uint descriptors, uint count, Wide mappingRoot)
    {
        CpxError error = BeginCore(descriptors, count, mappingRoot, true);
        if (error == CpxError.None) { memory.Write32(Arena + (OwnedImages), memory.Read32(Arena + (OwnedImages)) | 1); memory.Write32(Arena + (Phase), 1); }
        return error;
    }

    internal CpxError UseOwnedControlRoot(uint control)
    {
        uint payload = memory.Read16(control + 52), bytes = payload == 960 && memory.Read32(Arena + (Page)) < 1024 ? 1024u : memory.Read32(Arena + (Page));
        uint sectors = bytes >> (int)memory.Read32(Arena + (SectorShift));
        if (!Buffer(control, bytes) || !Buffer(memory.Read32(Arena + (Workspace)), bytes) || Overlap(control, bytes, Arena, StateBytes) ||
            Overlap(control, bytes, memory.Read32(Arena + (Workspace)), bytes) || Overlap(memory.Read32(Arena + (Workspace)), bytes, Arena, StateBytes) || memory.Read32(control) != Magic ||
            memory.Read16(control + 6) != 5 || payload is not (432 or 960) ||
            (((ulong)memory.Read32((Arena + Capacity)) << 32) | memory.Read32((Arena + Capacity) + 4)) < 3 * sectors || (((ulong)memory.Read32((Arena + Roots)) << 32) | memory.Read32((Arena + Roots) + 4)) > (((ulong)memory.Read32((Arena + Capacity)) << 32) | memory.Read32((Arena + Capacity) + 4)) - 3 * sectors ||
            memory.Read16(control + 56) is not (48 or 128 or 136) || memory.Read16(control + 58) != 32 ||
            memory.Read16(control + 232) > 32) return CpxError.Corrupt;
        memory.Write32(Arena + (OwnedImages), payload == 960 ? 2u : 0u);
        memory.Write32(Arena + (ControlRoot), control); return CpxError.None;
    }

    public CpxIo Request => new() { Kind = (IoKind)memory.Read32(Arena + (Kind)), Sector = (((ulong)memory.Read32((Arena + PendingSector)) << 32) | memory.Read32((Arena + PendingSector) + 4)),
        Buffer = memory.Read32(Arena + (PendingBuffer)), Bytes = memory.Read32(Arena + (PendingBytes)), Token = memory.Read32(Arena + (Token)) };

    public CpxError CompleteIo(uint token, uint actual, bool success)
    {
        if (memory.Read32(Arena + (Waiting)) == 0 || token != memory.Read32(Arena + (Token))) return CpxError.StaleCompletion;
        memory.Write32(Arena + (Waiting), 0);
        if (!success || (IoKind)memory.Read32(Arena + (Kind)) == IoKind.Write && actual != memory.Read32(Arena + (PendingBytes))) Fail(CpxError.Io);
        else if (memory.Read32(Arena + (CancelAt)) != 0) Fail(CpxError.Cancelled);
        return Error;
    }

    public void Cancel() { memory.Write32(Arena + (CancelAt), 1); if (memory.Read32(Arena + (Waiting)) == 0) Fail(CpxError.Cancelled); }

    public StepResult Step()
    {
        if (memory.Read32(Arena + (Signature)) != ContextMagic) return StepResult.Failed;
        if (memory.Read32(Arena + (Waiting)) != 0) return StepResult.NeedIo;
        if (memory.Read32(Arena + (Status)) == 3) return StepResult.Failed;
        if (memory.Read32(Arena + (Status)) != 1) return StepResult.Complete;
        if (memory.Read32(Arena + (CancelAt)) != 0) return Fail(CpxError.Cancelled);
        uint cursor = memory.Read32(Arena + (Cursor)), page = memory.Read32(Arena + (Page)), sectors = page >> (int)memory.Read32(Arena + (SectorShift));
        ulong transaction = (((ulong)memory.Read32((Arena + Generation)) << 32) | memory.Read32((Arena + Generation) + 4)) + 1;
        uint descriptor = memory.Read32(Arena + (Descriptors)) + cursor * DescriptorBytes;
        switch (memory.Read32(Arena + (Phase)))
        {
            case 0:
                if (!DescriptorValid(descriptor, cursor, transaction)) return Fail(CpxError.Corrupt);
                memory.Write32(Arena + (Cursor), cursor + 1);
                if (cursor + 1 == memory.Read32(Arena + (Count))) { memory.Write32(Arena + (Cursor), 0); memory.Write32(Arena + (Phase), 1); }
                return StepResult.Pending;
            case 1:
                if (memory.Read32(Arena + (Count)) == 0) return Issue(IoKind.Flush, 0, 0, 0, 3);
                return Issue(IoKind.Write, (((ulong)memory.Read32((descriptor + 8)) << 32) | memory.Read32((descriptor + 8) + 4)) + (1 - memory.Read32(descriptor + 20)) * sectors,
                    memory.Read32(descriptor + 16), page, 2);
            case 2:
                memory.Write32(Arena + (Cursor), cursor + 1);
                if (cursor + 1 < memory.Read32(Arena + (Count))) { memory.Write32(Arena + (Phase), 1); return StepResult.Pending; }
                return Issue(IoKind.Flush, 0, 0, 0, 3);
            case 3:
                memory.Write32(Arena + (RootCursor), 0); memory.Write32(Arena + (FirstNewRoot), uint.MaxValue); memory.Write32(Arena + (Phase), 4);
                return StepResult.Pending;
            case 4:
                uint root = memory.Read32(Arena + (RootCursor));
                if (root == memory.Read32(Arena + (Survivor))) root++;
                if (root == 3) return Issue(IoKind.Flush, 0, 0, 0, 5);
                ulong address = (((ulong)memory.Read32((Arena + Roots)) << 32) | memory.Read32((Arena + Roots) + 4)) + root * (((memory.Read32(Arena + (OwnedImages)) & 2) != 0 && memory.Read32(Arena + (Page)) < 1024 ? 1024u : memory.Read32(Arena + (Page))) >> (int)memory.Read32(Arena + (SectorShift)));
                MakeCheckpoint(memory.Read32(Arena + (Workspace)), address, transaction);
                if (memory.Read32(Arena + (FirstNewRoot)) == uint.MaxValue) memory.Write32(Arena + (FirstNewRoot), root);
                memory.Write32(Arena + (RootCursor), root + 1);
                return Issue(IoKind.Write, address, memory.Read32(Arena + (Workspace)), ((memory.Read32(Arena + (OwnedImages)) & 2) != 0 && memory.Read32(Arena + (Page)) < 1024 ? 1024u : memory.Read32(Arena + (Page))), 4);
            case 5:
                Put(Arena + Generation, transaction); Put(Arena + DurableMappingRootAt, (((ulong)memory.Read32((Arena + NextMappingRoot)) << 32) | memory.Read32((Arena + NextMappingRoot) + 4)));
                memory.Write32(Arena + (Survivor), memory.Read32(Arena + (FirstNewRoot))); memory.Write32(Arena + (Status), 2);
                return StepResult.Complete;
            default: return Fail(CpxError.Corrupt);
        }
    }

    private bool DescriptorValid(uint descriptor, uint index, ulong transaction)
    {
        uint page = memory.Read32(Arena + (Page)), sectors = page >> (int)memory.Read32(Arena + (SectorShift));
        uint image = memory.Read32(descriptor + 16), selected = memory.Read32(descriptor + 20);
        ulong pair = (((ulong)memory.Read32((descriptor + 8)) << 32) | memory.Read32((descriptor + 8) + 4)), capacity = (((ulong)memory.Read32((Arena + Capacity)) << 32) | memory.Read32((Arena + Capacity) + 4)), roots = (((ulong)memory.Read32((Arena + Roots)) << 32) | memory.Read32((Arena + Roots) + 4));
        if (selected > 1 || (((ulong)memory.Read32((descriptor + 24)) << 32) | memory.Read32((descriptor + 24) + 4)) != 0 || (pair & (2 * sectors - 1)) != 0
            || capacity < 2 * sectors || pair > capacity - 2 * sectors
            || pair < roots + 3 * (((memory.Read32(Arena + (OwnedImages)) & 2) != 0 && memory.Read32(Arena + (Page)) < 1024 ? 1024u : memory.Read32(Arena + (Page))) >> (int)memory.Read32(Arena + (SectorShift))) && roots < pair + 2 * sectors
            || !Buffer(image, page) || Overlap(image, page, Arena, StateBytes)
            || Overlap(image, page, memory.Read32(Arena + (Workspace)), ((memory.Read32(Arena + (OwnedImages)) & 2) != 0 && memory.Read32(Arena + (Page)) < 1024 ? 1024u : memory.Read32(Arena + (Page)))) || Overlap(image, page, memory.Read32(Arena + (Descriptors)), memory.Read32(Arena + (Count)) * DescriptorBytes)
            || memory.Read32(image) != Magic || memory.Read16(image + 6) != 5
            || (((ulong)memory.Read32((image + 16)) << 32) | memory.Read32((image + 16) + 4)) != (((ulong)memory.Read32((descriptor)) << 32) | memory.Read32((descriptor) + 4)) || (((ulong)memory.Read32((image + 32)) << 32) | memory.Read32((image + 32) + 4)) != transaction
            || (memory.Read32(Arena + (OwnedImages)) & 1) == 0 && memory.Read32(image + 8) != Crc(image, page)) return false;
        for (uint n = 0; n < index; n++)
        {
            uint previous = memory.Read32(Arena + (Descriptors)) + n * DescriptorBytes;
            if ((((ulong)memory.Read32((previous)) << 32) | memory.Read32((previous) + 4)) == (((ulong)memory.Read32((descriptor)) << 32) | memory.Read32((descriptor) + 4)) || (((ulong)memory.Read32((previous + 8)) << 32) | memory.Read32((previous + 8) + 4)) == pair
                || Overlap(image, page, memory.Read32(previous + 16), page)) return false;
        }
        return true;
    }

    private void MakeCheckpoint(uint buffer, ulong address, ulong transaction)
    {
        uint rootBytes = ((memory.Read32(Arena + (OwnedImages)) & 2) != 0 && memory.Read32(Arena + (Page)) < 1024 ? 1024u : memory.Read32(Arena + (Page)));
        uint control = memory.Read32(Arena + (ControlRoot));
        bool extended = (memory.Read32(Arena + (OwnedImages)) & 2) != 0;
        uint copiedEnd = control == 0 ? 96u : extended ? 1024u : 496u;
        // Every payload byte is either copied from the admitted owned template
        // or cleared. The header is rebuilt, including every reserved field.
        for (uint at = 0; at < 96; at += 4) memory.Write32(buffer + at, 0);
        if (control != 0) memory.Copy(buffer + 96, control + 96, copiedEnd - 96);
        for (uint at = copiedEnd; at < rootBytes; at += 4) memory.Write32(buffer + at, 0);
        memory.Write32(buffer, Magic); memory.Write16(buffer + 4, 2); memory.Write16(buffer + 6, 5);
        Put(buffer + 16, address >> (int)memory.Read32(Arena + (LogicalAddressShift))); Put(buffer + 32, transaction); memory.Write16(buffer + 52, 32);
        Put(buffer + 72, (((ulong)memory.Read32((Arena + Identity)) << 32) | memory.Read32((Arena + Identity) + 4))); Put(buffer + 80, transaction);
        Put(buffer + 88, (((ulong)memory.Read32((Arena + NextMappingRoot)) << 32) | memory.Read32((Arena + NextMappingRoot) + 4)));
        if (control != 0)
        {
            memory.Write16(buffer + 52, extended ? 960u : 432u); memory.Write16(buffer + 56, memory.Read16(control + 56)); memory.Write16(buffer + 58, 32);
        }
        memory.Write32(buffer + 8, Crc(buffer, rootBytes));
    }

    private uint Crc(uint buffer, uint bytes)
        => CpxShadowChecksums<TMemory>.Compute(ref memory, buffer, bytes, memory.Read32(Arena + (ChecksumTable)), memory.Read32(Arena + (ChecksumZeros)), Arena + ChecksumWord);

    private StepResult Issue(IoKind kind, ulong sector, uint buffer, uint bytes, uint next)
    {
        uint token = memory.Read32(Arena + (NextToken)) + 1;
        if (token == 0) return Fail(CpxError.ResourceExhausted);
        memory.Write32(Arena + (NextToken), token); memory.Write32(Arena + (Token), token); memory.Write32(Arena + (Kind), (uint)kind);
        Put(Arena + PendingSector, sector); memory.Write32(Arena + (PendingBuffer), buffer); memory.Write32(Arena + (PendingBytes), bytes);
        memory.Write32(Arena + (Phase), next); memory.Write32(Arena + (Waiting), 1); return StepResult.NeedIo;
    }
    private StepResult Fail(CpxError error) { memory.Write32(Arena + (ErrorAt), (uint)error); memory.Write32(Arena + (Status), 3); return StepResult.Failed; }
    private static bool Buffer(uint address, uint bytes) => address != 0 && (address & 3) == 0 && address <= uint.MaxValue - bytes;
    private static bool Overlap(uint a, uint lengthA, uint b, uint lengthB) => (ulong)a < (ulong)b + lengthB && (ulong)b < (ulong)a + lengthA;
}
