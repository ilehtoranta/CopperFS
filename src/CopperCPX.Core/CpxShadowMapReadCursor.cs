namespace CopperCPX;

/// <summary>Single-owner CPX5 map reader. The surrounding I/O owner validates
/// completion tokens before AcceptRead; this cursor never issues device I/O.
/// Reconstructing the value between steps preserves all state in caller storage.</summary>
public struct CpxShadowMapReadCursor<TMemory> where TMemory : struct, ICpxMemory
{
    public const uint StateBytes = 96;
    private const uint ContextMagic = 0x43535235;
    private TMemory memory;
    private CpxShadowMapGeometry geometry;
    public uint Arena;
    private const uint Signature = 0, Status = 4, ErrorAt = 8, Level = 12,
        Node = 16, Physical = 24, Pair = 32, Generation = 40, Identity = 48,
        Scratch = 56, HaveRead = 60, Requested = 64, Copy = 68, ChecksumTable = 72, ChecksumZeros = 76, ChecksumWord = 80, Authenticated = 84, Inode = 88;

    public CpxShadowMapReadCursor(TMemory memory, uint arena, CpxShadowMapGeometry geometry)
    { this.memory = memory; Arena = arena; this.geometry = geometry; }
    private uint R(uint at) => memory.Read32(Arena + at);
    private void W(uint at, uint value) => memory.Write32(Arena + at, value);
    private ulong G(uint at) => ((ulong)memory.Read32(at) << 32) | memory.Read32(at + 4);
    private void Put(uint at, ulong value) { memory.Write32(at, (uint)(value >> 32)); memory.Write32(at + 4, (uint)value); }
    public CpxError Error => (CpxError)memory.Read32(Arena + (ErrorAt));
    public uint SelectedCopy => memory.Read32(Arena + (Copy));
    public bool IsInode => memory.Read32(Arena + (Inode)) != 0;
    public Wide ReadSector => G(Arena + Physical);
    public uint ReadBuffer => memory.Read32(Arena + (Scratch));
    public uint ReadBytes => geometry.PageBytes;

    public CpxError Begin(Wide mappingRoot, Wide committedGeneration, Wide volumeIdentity, Wide pairSector, uint scratch,
        uint checksumTable = 0, uint checksumZeros = 0)
    {
        if (Arena == 0 || (Arena & 3) != 0 || Arena > uint.MaxValue - StateBytes || geometry.Height == 0
            || scratch == 0 || (scratch & 3) != 0 || scratch > uint.MaxValue - geometry.PageBytes
            || (ulong)Arena < (ulong)scratch + geometry.PageBytes && (ulong)scratch < (ulong)Arena + StateBytes)
            return CpxError.InvalidArgument;
        if (memory.Read32(Arena + (Signature)) == ContextMagic && memory.Read32(Arena + (Requested)) != 0) return CpxError.Busy;
        if (!geometry.ContainsPair(pairSector) || volumeIdentity.IsZero) return CpxError.InvalidArgument;
        ulong pair = pairSector;
        if (pair >= geometry.MapBaseSector && pair < geometry.MapEndSector) return CpxError.InvalidArgument;
        ulong root = mappingRoot, rootPair = geometry.NodePairSector(geometry.Height - 1, 0);
        if (root != 0 && root != rootPair && root != rootPair + geometry.PageSectors) return CpxError.Corrupt;
        for (uint at = 0; at < StateBytes; at += 4) memory.Write32(Arena + (at), 0);
        memory.Write32(Arena + (Signature), ContextMagic); memory.Write32(Arena + (Scratch), scratch);
        memory.Write32(Arena + (ChecksumTable), checksumTable); memory.Write32(Arena + (ChecksumZeros), checksumZeros);
        Put(Arena + Generation, committedGeneration); Put(Arena + Identity, volumeIdentity);
        Put(Arena + Physical, root); Put(Arena + Pair, geometry.PairIndex(pairSector));
        memory.Write32(Arena + (Level), geometry.Height - 1); memory.Write32(Arena + (Status), root == 0 ? 2u : 1u);
        return CpxError.None;
    }

    public CpxError AcceptRead(bool success)
    {
        if (memory.Read32(Arena + (Requested)) == 0 || memory.Read32(Arena + (Status)) != 1) return CpxError.StaleCompletion;
        memory.Write32(Arena + (Requested), 0);
        memory.Write32(Arena + (Authenticated), 0);
        if (!success) { Fail(CpxError.Io); return CpxError.Io; }
        memory.Write32(Arena + (HaveRead), 1); return CpxError.None;
    }

    // Only the owning engine can supply bytes previously authenticated for the
    // same selected physical page and durable root. Device completions always
    // take the ordinary checksum path above.
    internal CpxError AcceptCachedRead()
    {
        CpxError error = AcceptRead(true);
        if (error == CpxError.None) memory.Write32(Arena + (Authenticated), 1);
        return error;
    }

    public StepResult Step()
    {
        if (memory.Read32(Arena + (Signature)) != ContextMagic) return StepResult.Failed;
        if (memory.Read32(Arena + (Status)) == 3) return StepResult.Failed;
        if (memory.Read32(Arena + (Status)) == 2) return StepResult.Complete;
        if (memory.Read32(Arena + (HaveRead)) == 0) { memory.Write32(Arena + (Requested), 1); return StepResult.NeedIo; }
        uint buffer = memory.Read32(Arena + (Scratch)), level = memory.Read32(Arena + (Level));
        ulong node = G(Arena + Node), pair = geometry.NodePairSector(level, node);
        uint entries = geometry.Entries(level, node), payload = level == 0 && !geometry.HasInodeCatalogue ? (entries + 7) >> 3 : (entries + 3) >> 2;
        if (entries == 0 || memory.Read32(buffer) != CpxShadowCommit<TMemory>.Magic || memory.Read16(buffer + 6) != 5
            || memory.Read16(buffer + 4) != (level == 0 ? 20u : 21u)
            || memory.Read32(buffer + 12) != (level == 0 && geometry.HasInodeCatalogue ? 2u : 0u)
            || G(buffer + 16) != (0x8000000000000000ul | pair) || G(buffer + 24) != G(Arena + Identity)
            || G(buffer + 32) == 0 || G(buffer + 32) > G(Arena + Generation)
            || memory.Read16(buffer + 48) != entries || memory.Read16(buffer + 50) != level
            || memory.Read16(buffer + 52) != payload || memory.Read32(Arena + (Authenticated)) == 0 && memory.Read32(buffer + 8) != Crc(buffer))
            return Fail(CpxError.Corrupt);
        ulong index = G(Arena + Pair);
        if (level == 0)
        {
            uint bit = (uint)(index & (geometry.LeafEntries - 1));
            if (bit >= entries) return Fail(CpxError.Corrupt);
            if (geometry.HasInodeCatalogue)
            {
                uint value = ((uint)memory.Read8(buffer + 64 + (bit >> 2)) >> (int)(6 - (bit & 3) * 2)) & 3u;
                memory.Write32(Arena + (Copy), value & 1); memory.Write32(Arena + (Inode), value >> 1);
            }
            else memory.Write32(Arena + (Copy), ((uint)memory.Read8(buffer + 64 + (bit >> 3)) >> (int)(7 - (bit & 7))) & 1u);
            memory.Write32(Arena + (Status), 2); return StepResult.Complete;
        }
        uint shift = geometry.LeafShift + (level - 1) * geometry.BranchShift;
        uint child = (uint)((index >> (int)shift) & (geometry.BranchEntries - 1));
        if (child >= entries) return Fail(CpxError.Corrupt);
        uint selected = ((uint)memory.Read8(buffer + 64 + (child >> 2)) >> (int)(6 - (child & 3) * 2)) & 3;
        if (selected == 3) return Fail(CpxError.Corrupt);
        if (selected == 0) { memory.Write32(Arena + (Copy), 0); memory.Write32(Arena + (Status), 2); return StepResult.Complete; }
        node = (node << (int)geometry.BranchShift) + child;
        Put(Arena + Node, node); memory.Write32(Arena + (Level), level - 1);
        Put(Arena + Physical, (ulong)geometry.NodePairSector(level - 1, node) + (selected - 1) * geometry.PageSectors);
        memory.Write32(Arena + (HaveRead), 0); return StepResult.Pending;
    }

    private uint Crc(uint buffer)
        => CpxShadowChecksums<TMemory>.Compute(ref memory, buffer, geometry.PageBytes, memory.Read32(Arena + (ChecksumTable)), memory.Read32(Arena + (ChecksumZeros)), Arena + ChecksumWord);
    private StepResult Fail(CpxError error) { memory.Write32(Arena + (ErrorAt), (uint)error); memory.Write32(Arena + (Status), 3); return StepResult.Failed; }
}
