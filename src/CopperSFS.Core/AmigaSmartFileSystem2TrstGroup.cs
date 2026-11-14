using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystem2WriteCore
{
    // Private mount-owned RAM only. Two linear arenas retain the acknowledged
    // view while a complete packet is attempted in its disposable counterpart.
    private const uint GroupMagic = 0x53324752, GroupHeader = 32;
    private static void SetTrstOverlay<T>(ref T p, APTR arena) where T : struct, IAmigaWritableFileSystemPlatform
    { p.RedoOverlay = arena; Sfs2MountContext.SetOverlay(ref p, p.Sfs2Context, arena); }

    public static bool InitializeTrstGroup<T>(ref T p, APTR group, uint bytes)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (p.BlockBytes < 512 || p.BlockBytes > 32768 || group.IsNull ||
            bytes < GroupHeader + 2 * (p.BlockBytes + 20) || !p.IsMapped(group, bytes)) return false;
        p.Clear(group, GroupHeader);
        p.WriteUInt32(group, 0, GroupMagic); p.WriteUInt32(group, 8, bytes);
        p.WriteUInt32(group, 12, ((bytes - GroupHeader) / 2) & ~3u);
        p.WriteUInt32(group, 16, group.Raw + GroupHeader);
        p.WriteUInt32(group, 20, p.BlockBytes); p.WriteUInt32(group, 24, p.BlockCount);
        return true;
    }

    public static bool TrstGroupPending<T>(ref T p, APTR group)
        where T : struct, IAmigaWritableFileSystemPlatform =>
        group.IsNotNull && p.ReadUInt32(group) == GroupMagic && p.ReadUInt32(group, 4) != 0;

    private static bool GroupValid<T>(ref T p, APTR group, out APTR active, out uint span)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        active = APTR.Null; span = 0;
        if (group.IsNull || !p.IsMapped(group, GroupHeader) || p.ReadUInt32(group) != GroupMagic ||
            p.ReadUInt32(group, 20) != p.BlockBytes || p.ReadUInt32(group, 24) != p.BlockCount) return false;
        var bytes = p.ReadUInt32(group, 8); span = p.ReadUInt32(group, 12);
        if (bytes < GroupHeader + 2 * (p.BlockBytes + 20) || !p.IsMapped(group, bytes) ||
            span != (((bytes - GroupHeader) / 2) & ~3u)) return false;
        active = APTR.FromPointer(p.ReadUInt32(group, 16));
        return active.Raw == group.Raw + GroupHeader || active.Raw == group.Raw + GroupHeader + span;
    }

    /// <summary>Accept one complete packet in volatile mount-owned RAM. All
    /// reads must observe RedoOverlay until FlushTrstGroup completes. Failure
    /// leaves earlier accepted packets intact. Only data free in both durable and acknowledged views can be written early.</summary>
    public static bool ExecuteTrstGroup<T>(ref T p, ref Work w, ref Request r,
        TrstOperation operation, APTR group, out uint actual)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        actual = 0; w.Error = DOS.Error.None;
        if (!GroupValid(ref p, group, out var active, out var span)) return Fail(ref w, DOS.Error.NoFreeStore);
        var state = p.ReadUInt32(group, 4); var bytes = p.BlockBytes;
        if (state > 1 || (state == 1 ? p.RedoOverlay != active : p.RedoOverlay.IsNotNull))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        var storage = p.ReadUInt32(group, 8);
        if (operation < TrstOperation.Write || operation > TrstOperation.SetDate ||
            w.Meta.IsNull || !p.IsMapped(w.Meta, bytes) || w.Data.IsNull || !p.IsMapped(w.Data, bytes) ||
            w.Bitmap.IsNull || !p.IsMapped(w.Bitmap, bytes) || w.Journal.IsNull || !p.IsMapped(w.Journal, bytes) ||
            Overlap(group, storage, w.Meta, bytes) || Overlap(group, storage, w.Data, bytes) ||
            Overlap(group, storage, w.Bitmap, bytes) || Overlap(group, storage, w.Journal, bytes) ||
            Overlap(group, storage, r.Buffer, operation == TrstOperation.SetDate ? 12 : r.Length) ||
            Overlap(group, storage, r.Name, r.Length)) return Fail(ref w, DOS.Error.NoFreeStore);
        if (p.WriteProtected()) return Fail(ref w, DOS.Error.DiskWriteProtected);
        if (p.Sfs2Context.IsNotNull && !Sfs2MountContext.Ready(ref p, p.Sfs2Context)) return Fail(ref w, DOS.Error.DiskNotValidated);
        if (state == 0 && p.Sfs2Context.IsNull && !Recover(ref p, ref w)) return false;
        var trial = APTR.FromPointer(active.Raw == group.Raw + GroupHeader ? active.Raw + span : group.Raw + GroupHeader);
        var capacity = (span - 16) / (bytes + 4);
        if (p.Sfs2Context.IsNotNull)
        {
            var cache = Sfs2MountContext.Cache(ref p, p.Sfs2Context);
            if (cache.IsNotNull && !Sfs2MetadataStore.Unpinned(ref p, cache)) return Fail(ref w, DOS.Error.DiskNotValidated);
            var epoch = Sfs2MountContext.NextAttempt(ref p, p.Sfs2Context);
            if (epoch == 0) return Fail(ref w, DOS.Error.DiskNotValidated);
            // The unused group header word certifies the inactive arena as
            // this accepted view's predecessor. Consume it before touching a
            // trial: a rejected packet must force a complete copy next time.
            var predecessor = p.ReadUInt32(group, 28) != 0;
            p.WriteUInt32(group, 28, 0);
            var initialized = state == 1 ? Sfs2MetadataStore.Clone(ref p, active, trial, span, epoch, predecessor) :
                Sfs2MetadataStore.Initialize(ref p, trial, span, bytes, epoch);
            if (!initialized) return Fail(ref w, DOS.Error.NoFreeStore);
            p.WriteUInt32(trial, 24, Sfs2MetadataStore.Trial);
        }
        else if (state == 1)
        {
            var count = p.ReadUInt32(active);
            if (count > capacity || p.ReadUInt32(active, 4) != capacity || p.ReadUInt32(active, 8) != bytes ||
                p.ReadUInt32(active, 12) != 0) return Fail(ref w, DOS.Error.DiskNotValidated);
            p.Copy(active, trial, 16 + count * (bytes + 4));
        }
        else
        {
            p.WriteUInt32(trial, 0, 0); p.WriteUInt32(trial, 4, capacity);
            p.WriteUInt32(trial, 8, bytes); p.WriteUInt32(trial, 12, 0);
        }
        var saved = r;
        SetTrstOverlay(ref p, trial);
        if (p.RedoOverlay != trial) return Fail(ref w, DOS.Error.ActionNotKnown);
        w.RedoStaging = true; w.GroupStaging = true;
        w.GroupBase = state == 1 ? active : APTR.Null;
        var ok = StageTrst(ref p, ref w, ref r, operation, out var written);
        w.RedoStaging = false; w.GroupStaging = false;
        w.GroupBase = APTR.Null;
        if (p.ReadUInt32(trial, 12) != 0) ok = Fail(ref w, DOS.Error.NoFreeStore);
        if (ok && p.Sfs2Context.IsNotNull && !Sfs2MetadataStore.Publish(ref p, trial)) ok = Fail(ref w, DOS.Error.DiskNotValidated);
        if (ok && !Sfs2MountContext.Accept(ref p, trial)) ok = Fail(ref w, DOS.Error.DiskNotValidated);
        if (!ok) { SetTrstOverlay(ref p, state == 1 ? active : APTR.Null); r = saved; return false; }
        if (p.Sfs2Context.IsNotNull) p.WriteUInt32(group, 28, state);
        p.WriteUInt32(group, 16, trial.Raw); p.WriteUInt32(group, 4, 1);
        actual = written; return true;
    }

    /// <summary>Commit the accepted view with the ordinary TRST protocol. A
    /// failed publication detaches the view and blocks reuse until recovery.</summary>
    public static bool FlushTrstGroup<T>(ref T p, ref Work w, APTR group)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!GroupValid(ref p, group, out var active, out var span)) return Fail(ref w, DOS.Error.NoFreeStore);
        var state = p.ReadUInt32(group, 4);
        if (state == 0) return true;
        if (state != 1 || p.RedoOverlay != active) return Fail(ref w, DOS.Error.DiskNotValidated);
        // Prepare roots from the accepted view before detaching it. No mutation
        // can enter during this single-owner commit.
        w.Error = DOS.Error.None;
        if (p.Sfs2Context.IsNotNull)
        {
            if (p.WriteProtected()) return Fail(ref w, DOS.Error.DiskWriteProtected);
            if (!Sfs2MountContext.Load(ref p, ref w) || !Sfs2MetadataStore.Unpinned(ref p, active)) return Fail(ref w, DOS.Error.DiskNotValidated);
        }
        else if (!Prepare(ref p, ref w)) return false;
        SetTrstOverlay(ref p, APTR.Null); p.WriteUInt32(group, 4, 2);
        Sfs2MountContext.CommitState(ref p, p.Sfs2Context, 2);
        var staged = new RedoPlatform<T> { Inner = p, Buffer = APTR.FromPointer(active.Raw + 16),
            Capacity = (span - 16) / (p.BlockBytes + 4), Count = p.ReadUInt32(active) };
        if (Sfs2MetadataStore.IsStore(ref p, active)) staged.OwnedArena = active;
        if (!CommitTrst(ref p, ref w, ref staged)) { Sfs2MountContext.Suspend(ref p, p.Sfs2Context); return false; }
        p.WriteUInt32(group, 4, 0); Sfs2MountContext.CommitState(ref p, p.Sfs2Context, 0); return true;
    }

    public static bool RecoverTrstGroup<T>(ref T p, ref Work w, APTR group)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!GroupValid(ref p, group, out _, out _)) return Fail(ref w, DOS.Error.NoFreeStore);
        if (p.ReadUInt32(group, 4) == 1) return FlushTrstGroup(ref p, ref w, group);
        if (p.ReadUInt32(group, 4) == 0 && p.Sfs2Context.IsNotNull && Sfs2MountContext.Ready(ref p, p.Sfs2Context)) return true;
        SetTrstOverlay(ref p, APTR.Null);
        var context = p.Sfs2Context;
        if (context.IsNotNull)
        {
            if (!Sfs2MountContext.Mount(ref p, ref w, context, p.ReadUInt32(context, 44))) return false;
        }
        else if (!Recover(ref p, ref w) || (p.ReadUInt32(group, 4) > 1 && !p.Flush())) return false;
        p.WriteUInt32(group, 4, 0); return true;
    }
}
