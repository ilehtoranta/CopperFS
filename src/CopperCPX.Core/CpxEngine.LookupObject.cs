namespace CopperCPX;

public partial struct CpxEngine<TMemory> where TMemory : struct, ICpxMemory
{
    // Lookup and recovery never execute together. The entry key also borrows
    // the metadata-publication target, unused by lookup and object reads.
    private const uint LookupObjectMode = 508, LookupObjectEntryAt = PreparedTarget;
    internal Wide LookupObjectEntry => new(memory.Read32(Arena + LookupObjectEntryAt), memory.Read32(Arena + LookupObjectEntryAt + 4));

    internal CpxError BeginLookupObject(Wide parent, uint name, uint length)
    {
        CpxError error = BeginLookup(parent, name, length);
        if (error == CpxError.None) memory.Write32(Arena + LookupObjectMode, 1);
        return error;
    }

    private StepResult FinishLookup()
    {
        uint context = Arena;
        if (memory.Read32(context + LookupObjectMode) == 0) return Done();
        memory.Write32(context + LookupObjectMode, 0);
        if (memory.Read32(context + LookupFoundAt) == 0) return Done();
        PutWide(context + LookupObjectEntryAt, GetWide(TreeRecord + 8));
        ulong id = GetWide(context + LookupTarget);
        // Retire only the lookup half. Its bounded owner continues through
        // ordinary object validation, including cancellation before each phase.
        memory.Write32(context + Finished, 1);
        if (TryBeginCachedObjectRead(id)) return StepResult.Pending;
        memory.Write32(context + Finished, 0);
        memory.Write32(context + OperationAt, (uint)CpxOperation.ReadObject);
        memory.Write32(context + Phase, 0);
        memory.Write32(context + CallDepth, 0);
        memory.Write32(context + SourcePage, 0);
        memory.Write32(context + IoReady, 0);
        memory.Write32(context + NsPublished, 0);
        PutWide(context + Target, id); PutWide(context + ObjectKey, id);
        return StepResult.Pending;
    }
}
