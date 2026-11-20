namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // The four-byte gap between native maintenance and derived geometry.
    private const uint CheckpointPolicy = 4016;
    private bool AlternatingCheckpoints => (memory.Read32(Arena + CheckpointPolicy) & 1) != 0;

    /// <summary>Experimental retirement protocol. The caller must opt in while
    /// quiescent. Storage and on-disk records retain their existing bounds.</summary>
    public CpxError EnableAlternatingCheckpoints()
    {
        if (memory.Read32(Arena + Signature) != CpxArena.Magic)
            return CpxError.InvalidArgument;
        if (memory.Read32(Arena + Waiting) != 0 || memory.Read32(Arena + Finished) == 0 || memory.Read32(Arena + Active) != 0)
            return CpxError.Busy;
        memory.Write32(Arena + CheckpointPolicy, memory.Read32(Arena + CheckpointPolicy) | 1);
        return CpxError.None;
    }

    internal uint CpuPhaseBudget => (memory.Read32(Arena + CheckpointPolicy) & 2) != 0 ? 4u : 1u;

    public CpxError EnableCpuBatching()
    {
        if (memory.Read32(Arena + Signature) != CpxArena.Magic)
            return CpxError.InvalidArgument;
        if (memory.Read32(Arena + Waiting) != 0 || memory.Read32(Arena + Finished) == 0 || memory.Read32(Arena + Active) != 0)
            return CpxError.Busy;
        memory.Write32(Arena + CheckpointPolicy, memory.Read32(Arena + CheckpointPolicy) | 2);
        return CpxError.None;
    }

    private StepResult PublishAlternatingCheckpoint(uint buffer, ulong transaction)
    {
        ulong address = Anchor + ((transaction & 1) == 0 ? 2 * Q : Q);
        Header(buffer, 2, address, transaction);
        PutWide(buffer + 72, RW(Identity));
        PutWide(buffer + 80, transaction);
        Seal(buffer, 24);
        return Issue(IoKind.Write, address, buffer, P, 40);
    }

    private bool CleanAlternatingCheckpoints(ulong replay)
        => AlternatingCheckpoints && replay == 0
        && memory.Read32(Arena + ValidOne) != 0 && memory.Read32(Arena + ValidTwo) != 0
        // A classic writer can leave its newer copy in either slot. Repair
        // that transition before allowing the next parity-selected overwrite.
        && RW((RW(Generation) & 1) != 0 ? CheckpointOne : CheckpointTwo) == RW(Generation);
}
