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
        NextMappingRoot = 120, DurableMappingRootAt = 128, ChecksumTable = 136, ChecksumZeros = 140, ChecksumWord = 144, LogicalAddressShift = 148, OwnedImages = 152;

    public CpxShadowCommit(TMemory memory, uint arena) { this.memory = memory; Arena = arena; }
    private uint R(uint at) => memory.Read32(Arena + at);
    private void W(uint at, uint value) => memory.Write32(Arena + at, value);
    private ulong G(uint at) => ((ulong)memory.Read32(at) << 32) | memory.Read32(at + 4);
    private void Put(uint at, ulong value) { memory.Write32(at, (uint)(value >> 32)); memory.Write32(at + 4, (uint)value); }
    public CpxError Error => (CpxError)R(ErrorAt);
    public Wide DurableGeneration => G(Arena + Generation);
    public Wide DurableMappingRoot => G(Arena + DurableMappingRootAt);
    public uint SurvivingCheckpoint => R(Survivor);

    public CpxError Initialize(uint sectorBytes, uint pageBytes, Wide capacitySectors,
        Wide volumeIdentity, Wide generation, Wide rootSector, uint survivor, uint workspace, Wide mappingRoot = default,
        uint checksumTable = 0, uint checksumZeros = 0, uint logicalAddressShift = 0)
    {
        if (Arena == 0 || (Arena & 3) != 0 || Arena > uint.MaxValue - StateBytes) return CpxError.InvalidArgument;
        if (memory.Read32(Arena + Signature) == ContextMagic && R(Waiting) != 0) return CpxError.Busy;
        if (!CpxArena.ValidUnit(sectorBytes) || pageBytes < sectorBytes || pageBytes > 4096
            || (pageBytes & (pageBytes - 1)) != 0 || logicalAddressShift > 3 || survivor >= 3 || volumeIdentity.IsZero
            || !Buffer(workspace, pageBytes) || Overlap(Arena, StateBytes, workspace, pageBytes))
            return CpxError.InvalidArgument;
        ulong capacity = capacitySectors, roots = rootSector;
        uint sectors = pageBytes / sectorBytes;
        if (capacity < 3 * sectors || roots > capacity - 3 * sectors) return CpxError.InvalidArgument;
        uint next = memory.Read32(Arena + Signature) == ContextMagic ? R(NextToken) : 0;
        for (uint at = 0; at < StateBytes; at += 4) W(at, 0);
        W(Signature, ContextMagic); W(Page, pageBytes); W(SectorShift, (uint)CpxArena.Shift(sectorBytes));
        Put(Arena + Generation, generation); Put(Arena + Identity, volumeIdentity);
        Put(Arena + Capacity, capacity); Put(Arena + Roots, roots);
        Put(Arena + DurableMappingRootAt, mappingRoot);
        W(ChecksumTable, checksumTable); W(ChecksumZeros, checksumZeros);
        W(LogicalAddressShift, logicalAddressShift);
        W(Survivor, survivor); W(Workspace, workspace); W(NextToken, next);
        return CpxError.None;
    }

    // Each record: logical page tag (u64), physical pair sector (u64), owned
    // image address (u32), currently selected copy (u32), reserved zero (u64).
    // The caller owns allocation/namespace validation and pins this write set.
    public CpxError Begin(uint descriptors, uint count, Wide mappingRoot)
    {
        if (R(Signature) != ContextMagic) return CpxError.InvalidArgument;
        if (R(Waiting) != 0 || R(Status) == 1) return CpxError.Busy;
        if (R(Status) == 3) return Error;
        if (G(Arena + Generation) == ulong.MaxValue || count == 0 || count > CpxArena.TransactionImages
            || !Buffer(descriptors, count * DescriptorBytes)
            || Overlap(descriptors, count * DescriptorBytes, Arena, StateBytes)
            || Overlap(descriptors, count * DescriptorBytes, R(Workspace), R(Page)))
            return CpxError.InvalidArgument;
        W(Descriptors, descriptors); W(Count, count); W(Cursor, 0); W(RootCursor, 0);
        W(OwnedImages, 0);
        Put(Arena + NextMappingRoot, mappingRoot);
        W(Phase, 0); W(ErrorAt, 0); W(CancelAt, 0); W(Status, 1);
        return CpxError.None;
    }

    // The engine admits logical targets and headers before sealing its pinned
    // contiguous image array; the map writer admits its unique physical pairs
    // and all descriptor/buffer ranges. Those owned invariants make repeating
    // public untrusted-descriptor admission unnecessary here.
    internal CpxError BeginOwnedImages(uint descriptors, uint count, Wide mappingRoot)
    {
        CpxError error = Begin(descriptors, count, mappingRoot);
        if (error == CpxError.None) { W(OwnedImages, 1); W(Phase, 1); }
        return error;
    }

    public CpxIo Request => new() { Kind = (IoKind)R(Kind), Sector = G(Arena + PendingSector),
        Buffer = R(PendingBuffer), Bytes = R(PendingBytes), Token = R(Token) };

    public CpxError CompleteIo(uint token, uint actual, bool success)
    {
        if (R(Waiting) == 0 || token != R(Token)) return CpxError.StaleCompletion;
        W(Waiting, 0);
        if (!success || (IoKind)R(Kind) == IoKind.Write && actual != R(PendingBytes)) Fail(CpxError.Io);
        else if (R(CancelAt) != 0) Fail(CpxError.Cancelled);
        return Error;
    }

    public void Cancel() { W(CancelAt, 1); if (R(Waiting) == 0) Fail(CpxError.Cancelled); }

    public StepResult Step()
    {
        if (R(Signature) != ContextMagic) return StepResult.Failed;
        if (R(Waiting) != 0) return StepResult.NeedIo;
        if (R(Status) == 3) return StepResult.Failed;
        if (R(Status) != 1) return StepResult.Complete;
        if (R(CancelAt) != 0) return Fail(CpxError.Cancelled);
        uint cursor = R(Cursor), page = R(Page), sectors = page >> (int)R(SectorShift);
        ulong transaction = G(Arena + Generation) + 1;
        uint descriptor = R(Descriptors) + cursor * DescriptorBytes;
        switch (R(Phase))
        {
            case 0:
                if (!DescriptorValid(descriptor, cursor, transaction)) return Fail(CpxError.Corrupt);
                W(Cursor, cursor + 1);
                if (cursor + 1 == R(Count)) { W(Cursor, 0); W(Phase, 1); }
                return StepResult.Pending;
            case 1:
                return Issue(IoKind.Write, G(descriptor + 8) + (1 - memory.Read32(descriptor + 20)) * sectors,
                    memory.Read32(descriptor + 16), page, 2);
            case 2:
                W(Cursor, cursor + 1);
                if (cursor + 1 < R(Count)) { W(Phase, 1); return StepResult.Pending; }
                return Issue(IoKind.Flush, 0, 0, 0, 3);
            case 3:
                W(RootCursor, 0); W(FirstNewRoot, uint.MaxValue); W(Phase, 4);
                return StepResult.Pending;
            case 4:
                uint root = R(RootCursor);
                if (root == R(Survivor)) root++;
                if (root == 3) return Issue(IoKind.Flush, 0, 0, 0, 5);
                ulong address = G(Arena + Roots) + root * sectors;
                MakeCheckpoint(R(Workspace), address, transaction);
                if (R(FirstNewRoot) == uint.MaxValue) W(FirstNewRoot, root);
                W(RootCursor, root + 1);
                return Issue(IoKind.Write, address, R(Workspace), page, 4);
            case 5:
                Put(Arena + Generation, transaction); Put(Arena + DurableMappingRootAt, G(Arena + NextMappingRoot));
                W(Survivor, R(FirstNewRoot)); W(Status, 2);
                return StepResult.Complete;
            default: return Fail(CpxError.Corrupt);
        }
    }

    private bool DescriptorValid(uint descriptor, uint index, ulong transaction)
    {
        uint page = R(Page), sectors = page >> (int)R(SectorShift);
        uint image = memory.Read32(descriptor + 16), selected = memory.Read32(descriptor + 20);
        ulong pair = G(descriptor + 8), capacity = G(Arena + Capacity), roots = G(Arena + Roots);
        if (selected > 1 || G(descriptor + 24) != 0 || (pair & (2 * sectors - 1)) != 0
            || capacity < 2 * sectors || pair > capacity - 2 * sectors
            || pair < roots + 3 * sectors && roots < pair + 2 * sectors
            || !Buffer(image, page) || Overlap(image, page, Arena, StateBytes)
            || Overlap(image, page, R(Workspace), page) || Overlap(image, page, R(Descriptors), R(Count) * DescriptorBytes)
            || memory.Read32(image) != Magic || memory.Read16(image + 6) != 5
            || G(image + 16) != G(descriptor) || G(image + 32) != transaction
            || R(OwnedImages) == 0 && memory.Read32(image + 8) != Crc(image, page)) return false;
        for (uint n = 0; n < index; n++)
        {
            uint previous = R(Descriptors) + n * DescriptorBytes;
            if (G(previous) == G(descriptor) || G(previous + 8) == pair
                || Overlap(image, page, memory.Read32(previous + 16), page)) return false;
        }
        return true;
    }

    private void MakeCheckpoint(uint buffer, ulong address, ulong transaction)
    {
        for (uint at = 0; at < R(Page); at += 4) memory.Write32(buffer + at, 0);
        memory.Write32(buffer, Magic); memory.Write16(buffer + 4, 2); memory.Write16(buffer + 6, 5);
        Put(buffer + 16, address >> (int)R(LogicalAddressShift)); Put(buffer + 32, transaction); memory.Write16(buffer + 52, 32);
        Put(buffer + 72, G(Arena + Identity)); Put(buffer + 80, transaction);
        Put(buffer + 88, G(Arena + NextMappingRoot));
        memory.Write32(buffer + 8, Crc(buffer, R(Page)));
    }

    private uint Crc(uint buffer, uint bytes)
        => CpxShadowChecksums<TMemory>.Compute(ref memory, buffer, bytes, R(ChecksumTable), R(ChecksumZeros), Arena + ChecksumWord);

    private StepResult Issue(IoKind kind, ulong sector, uint buffer, uint bytes, uint next)
    {
        uint token = R(NextToken) + 1;
        if (token == 0) return Fail(CpxError.ResourceExhausted);
        W(NextToken, token); W(Token, token); W(Kind, (uint)kind);
        Put(Arena + PendingSector, sector); W(PendingBuffer, buffer); W(PendingBytes, bytes);
        W(Phase, next); W(Waiting, 1); return StepResult.NeedIo;
    }
    private StepResult Fail(CpxError error) { W(ErrorAt, (uint)error); W(Status, 3); return StepResult.Failed; }
    private static bool Buffer(uint address, uint bytes) => address != 0 && (address & 3) == 0 && address <= uint.MaxValue - bytes;
    private static bool Overlap(uint a, uint lengthA, uint b, uint lengthB) => (ulong)a < (ulong)b + lengthB && (ulong)b < (ulong)a + lengthA;
}
