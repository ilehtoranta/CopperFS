#if COPPERFS_AUTOTRIM
using Amiga;

namespace CopperStart.Dos;

/// <summary>Volatile hints and bounded idle maintenance. The handler retains
/// exclusive ownership from final allocation validation through one UNMAP.</summary>
internal static class NativeAutomaticTrim
{
    private const int Control = 0, Queried = 4, Maximum = 8, Granularity = 12,
        Alignment = 16, Descriptors = 20, Generation = 24,
        DiscoveryCount = 44, Dropped = 48, Commands = 52, Blocks = 56;
    internal const int Eligible = 28;
    private static APTR Record(APTR state) => APTR.FromPointer(state.Raw + (uint)NativeAmigaFileSystem.AutoTrimRecordOffset);
    private static APTR Queue(APTR state) => APTR.FromPointer(state.Raw + (uint)NativeAmigaFileSystem.AutoTrimQueueOffset);
    private static APTR Batch(APTR state) => APTR.FromPointer(state.Raw + (uint)NativeAmigaFileSystem.AutoTrimBatchOffset);
    private static APTR Roots(APTR state) => APTR.FromPointer(state.Raw + (uint)NativeAmigaFileSystem.AutoTrimRootsOffset);

    internal static void Initialize(APTR state)
    {
        var record = Record(state);
        if (APTR.ReadUInt32(record, Control) != 0) return;
        var memory = NativeSfsTrim.AllocateControl(state, AmigaScsiUnmap.BatchControlBytes);
        APTR.WriteUInt32(record, Control, memory);
        if (memory == 0) Suspend(state);
    }

    internal static void Release(APTR state)
    {
        var record = Record(state); var memory = APTR.ReadUInt32(record, Control);
        if (memory != 0) global::Amiga.Exec.FreeMem(memory, AmigaScsiUnmap.BatchControlBytes);
        APTR.WriteUInt32(record, Control, 0); Reset(state);
    }

    internal static void Reset(APTR state)
    {
        var p = new NativeFileSystemPlatform(state);
        // Keep allocated staging across inhibit/remount; cached capabilities and
        // every validation record belong to the old media attachment.
        p.Clear(APTR.FromPointer(Record(state).Raw + 4), 60);
        p.Clear(Batch(state), AmigaAutomaticTrimBatch.Bytes);
        p.Clear(Roots(state), 32);
        AmigaFreedRangeQueue.Clear(ref p, Queue(state));
    }

    internal static bool Pending(APTR state) => APTR.ReadUInt32(state, NativeAmigaFileSystem.AutoTrimPendingOffset) == 1;
    internal static bool Ready(APTR state) => Pending(state) && APTR.ReadUInt32(Record(state), Eligible) != 0 &&
        APTR.ReadUInt32(state, NativeFileSystemStateCodec.InhibitedOffset) == 0;
    internal static void MakeEligible(APTR state)
    { if (Pending(state)) APTR.WriteUInt32(Record(state), Eligible, 1); }

    internal static void BeforeOperation(APTR state)
    {
        if (!Pending(state)) return;
        APTR.WriteUInt32(Roots(state), 28, 0);
        if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRecoveryPendingOffset) != 0)
        { Suspend(state); return; }
        var record = Record(state); var batch = Batch(state);
        APTR.WriteUInt32(record, Generation, unchecked(APTR.ReadUInt32(record, Generation) + 1));
        APTR.WriteUInt32(record, Dropped, APTR.ReadUInt32(record, Dropped) + APTR.ReadUInt32(batch, AmigaAutomaticTrimBatch.Inputs));
        APTR.WriteUInt32(batch, AmigaAutomaticTrimBatch.Inputs, 0);
        APTR.WriteUInt32(batch, AmigaAutomaticTrimBatch.Stage, 0);
        APTR.WriteUInt32(batch, AmigaAutomaticTrimBatch.Outputs, 0);
    }

    internal static void Suspend(APTR state)
    {
        var p = new NativeFileSystemPlatform(state);
        AmigaFreedRangeQueue.Clear(ref p, Queue(state));
        p.Clear(Batch(state), AmigaAutomaticTrimBatch.Bytes);
        p.Clear(Roots(state), 32);
        APTR.WriteUInt32(state, NativeAmigaFileSystem.AutoTrimPendingOffset, 2);
        APTR.WriteUInt32(Record(state), Eligible, 0);
        APTR.WriteUInt32(Record(state), Queried, 2);
    }

    // A failed transport may have reset/replaced the attachment. Stop trusting
    // capabilities immediately, but retain the submitted range vector until
    // the caller invalidates cache entries after a possibly partial UNMAP.
    internal static void TransportFailed(APTR state)
    {
        var record = Record(state);
        APTR.WriteUInt32(record, Queried, 2);
        APTR.WriteUInt32(record, Eligible, 0);
        APTR.WriteUInt32(record, Generation, unchecked(APTR.ReadUInt32(record, Generation) + 1));
        APTR.WriteUInt32(state, NativeAmigaFileSystem.AutoTrimPendingOffset, 2);
        APTR.WriteUInt32(Roots(state), 28, 0);
        APTR.WriteUInt32(Batch(state), AmigaAutomaticTrimBatch.Stage, 0);
    }

    // Only successful normal commits or this turn's clean-marker validation may
    // populate these scalars. They expire before the next foreground operation.
    internal static void RememberRoots(APTR state, uint bitmap, uint root, uint admin, uint node, uint extent)
    {
        if (!Pending(state) || root == 0 || NativeAmigaFileSystem.HasBufferedWrites(state) ||
            APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRecoveryPendingOffset) != 0) return;
        var cache = Roots(state);
        APTR.WriteUInt32(cache, 0, APTR.ReadUInt32(Record(state), Generation));
        APTR.WriteUInt32(cache, 4, state.Raw);
        APTR.WriteUInt32(cache, 8, bitmap); APTR.WriteUInt32(cache, 12, root);
        APTR.WriteUInt32(cache, 16, admin); APTR.WriteUInt32(cache, 20, node); APTR.WriteUInt32(cache, 24, extent);
        APTR.WriteUInt32(cache, 28, 1);
    }

    internal static void Service(APTR state)
    {
        if (!Ready(state) || APTR.ReadUInt32(state, NativeFileSystemStateCodec.InhibitedOffset) != 0) return;
        if (APTR.ReadUInt32(state, NativeFileSystemStateCodec.SmartRecoveryPendingOffset) != 0)
        { Suspend(state); return; }
        // New foreground work may have arrived after the eligibility timer.
        // Its ordinary write timer owns commit; TRIM must not force it early.
        if (NativeAmigaFileSystem.HasBufferedWrites(state))
        { APTR.WriteUInt32(Record(state), Eligible, 0); return; }
        var p = new NativeFileSystemPlatform(state);
        var record = Record(state); var batch = Batch(state);
#if COPPERFS_FFS_HANDLER
        if (APTR.ReadUInt32(record, Control) == 0) Initialize(state);
#endif
        var control = APTR.FromPointer(APTR.ReadUInt32(record, Control));
        if (control.IsNull) { Suspend(state); return; }
        if (APTR.ReadUInt32(record, Queried) == 0)
        {
            if (!NativeAmigaFileSystem.BufferFitsMask(control.Raw, AmigaScsiUnmap.BatchControlBytes, p.Geometry.Mask))
            { Suspend(state); return; }
            APTR.WriteUInt32(record, DiscoveryCount, APTR.ReadUInt32(record, DiscoveryCount) + 1);
            if (!p.QueryDiscard(control, out var discovered) || !discovered.Valid || discovered.Granularity > 4096)
            { Suspend(state); return; }
            APTR.WriteUInt32(record, Maximum, discovered.MaximumBlocks < 4096 ? discovered.MaximumBlocks : 4096);
            APTR.WriteUInt32(record, Granularity, discovered.Granularity);
            APTR.WriteUInt32(record, Alignment, discovered.Alignment);
            APTR.WriteUInt32(record, Descriptors, AmigaScsiUnmap.MaximumDescriptors(ref p, control));
            APTR.WriteUInt32(record, Queried, 1);
        }
        var generation = APTR.ReadUInt32(record, Generation);
        if (APTR.ReadUInt32(batch, AmigaAutomaticTrimBatch.Inputs) == 0)
        {
            var limits = new AmigaFileSystemDiscardLimits
            {
                MaximumBlocks = APTR.ReadUInt32(record, Maximum),
                Granularity = APTR.ReadUInt32(record, Granularity), Alignment = APTR.ReadUInt32(record, Alignment)
            };
            AmigaAutomaticTrimBatch.Begin(ref p, batch, state.Raw, generation, limits, APTR.ReadUInt32(record, Descriptors));
            uint used = 0, budget = 4096;
            while (used < 8 && budget >= limits.Granularity &&
                AmigaFreedRangeQueue.Take(ref p, Queue(state), budget, limits.Granularity - 1, out var first, out var count))
            {
                p.WriteUInt32(batch, AmigaAutomaticTrimBatch.Input + (int)used * 8, first);
                p.WriteUInt32(batch, AmigaAutomaticTrimBatch.Input + (int)used * 8 + 4, count);
                used++; budget -= count;
            }
            p.WriteUInt32(batch, AmigaAutomaticTrimBatch.Inputs, used);
            AmigaAutomaticTrimBatch.Sort(ref p, batch);
            var roots = Roots(state);
            if (p.ReadUInt32(roots, 28) != 0 && p.ReadUInt32(roots, 0) == generation && p.ReadUInt32(roots, 4) == state.Raw)
            {
                p.Copy(APTR.FromPointer(roots.Raw + 8), APTR.FromPointer(batch.Raw + AmigaAutomaticTrimBatch.PreparedRoots), 20);
                p.WriteUInt32(batch, AmigaAutomaticTrimBatch.RootsValid, 1);
            }
        }
        if (p.ReadUInt32(batch, AmigaAutomaticTrimBatch.Inputs) != 0)
        {
            if (!AmigaAutomaticTrimBatch.Current(ref p, batch, state.Raw, generation) ||
                !NativeSfsTrim.CollectAutomatic(state, batch, generation, out var complete))
            { Suspend(state); return; }
            if (!complete) return;
            var count = p.ReadUInt32(batch, AmigaAutomaticTrimBatch.Outputs);
            if (count != 0)
            {
                p.WriteUInt32(record, Commands, p.ReadUInt32(record, Commands) + 1);
                if (!p.DiscardRanges(control, APTR.FromPointer(batch.Raw + AmigaAutomaticTrimBatch.Output), count))
                { Suspend(state); return; }
                p.WriteUInt32(record, Blocks, p.ReadUInt32(record, Blocks) + p.ReadUInt32(batch, AmigaAutomaticTrimBatch.OutputBlocks));
            }
        }
        // Keep only hints for the next command. Layout/commit proof may survive
        // an idle turn in this generation, but allocation is scanned again.
        // BeforeOperation drops this entire context if a packet interleaves.
        var remaining = p.ReadUInt32(batch, AmigaAutomaticTrimBatch.RemainderCount);
        if (remaining != 0)
        {
            p.Copy(APTR.FromPointer(batch.Raw + AmigaAutomaticTrimBatch.Remainders),
                APTR.FromPointer(batch.Raw + AmigaAutomaticTrimBatch.Input), remaining * 8);
            p.WriteUInt32(batch, AmigaAutomaticTrimBatch.Inputs, remaining);
            p.WriteUInt32(batch, AmigaAutomaticTrimBatch.RemainderCount, 0);
            p.WriteUInt32(batch, AmigaAutomaticTrimBatch.Outputs, 0);
            p.WriteUInt32(batch, AmigaAutomaticTrimBatch.OutputBlocks, 0);
            return;
        }
        p.WriteUInt32(batch, AmigaAutomaticTrimBatch.Inputs, 0);
        var pending = AmigaFreedRangeQueue.Count(ref p, Queue(state)) != 0;
        APTR.WriteUInt32(state, NativeAmigaFileSystem.AutoTrimPendingOffset, pending ? 1u : 0u);
        if (!pending) { p.WriteUInt32(record, Eligible, 0); p.WriteUInt32(Roots(state), 28, 0); }
    }
}
#endif
