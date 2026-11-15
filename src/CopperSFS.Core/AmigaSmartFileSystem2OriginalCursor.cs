using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystem2WriteCore
{
    public const uint OriginalDataCursorBytes = 48;

    private static void RememberOriginalDataCursor<T>(ref T p, APTR cursor, APTR group,
        APTR active, uint node, ulong size, uint logical, uint physical, uint count, uint first)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var context = p.Sfs2Context; p.WriteUInt32(cursor, 0, 0);
        p.WriteUInt32(cursor, 4, context.Raw);
        p.WriteUInt32(cursor, 8, p.ReadUInt32(context, 8));
        p.WriteUInt32(cursor, 12, p.ReadUInt32(context, 12));
        p.WriteUInt32(cursor, 16, group.Raw); p.WriteUInt32(cursor, 20, active.Raw);
        p.WriteUInt32(cursor, 24, (uint)size); p.WriteUInt32(cursor, 28, (uint)(size >> 32));
        p.WriteUInt32(cursor, 32, logical); p.WriteUInt32(cursor, 36, physical); p.WriteUInt32(cursor, 40, count);
        p.WriteUInt32(cursor, 44, first);
        p.WriteUInt32(cursor, 0, node);
    }

    // A successful nonempty write checked the file type and write permission,
    // cleared archive, and published these size/first scalars. The group already
    // checked mount readiness before consulting this optional descriptor.
    private static bool OriginalFileInformation<T>(ref T p, APTR cursor, APTR group, APTR active,
        uint node, out FileInformation info) where T : struct, IAmigaWritableFileSystemPlatform
    {
        info = default; var context = p.Sfs2Context;
        if (cursor.IsNull || p.ReadUInt32(cursor) != node || p.RedoOverlay != active ||
            p.ReadUInt32(cursor, 4) != context.Raw || p.ReadUInt32(cursor, 8) != p.ReadUInt32(context, 8) ||
            p.ReadUInt32(cursor, 12) != p.ReadUInt32(context, 12) || p.ReadUInt32(cursor, 16) != group.Raw ||
            p.ReadUInt32(cursor, 20) != active.Raw) return false;
        info.Size = ((ulong)p.ReadUInt32(cursor, 28) << 32) | p.ReadUInt32(cursor, 24);
        info.First = p.ReadUInt32(cursor, 44);
        if (p.ReadUInt32(cursor, 40) == 0)
        {
            // Zero count selects a file description, never an extent proof.
            info.ObjectBlock = p.ReadUInt32(cursor, 32); info.ObjectOffset = p.ReadUInt32(cursor, 36);
        }
        // Only writable/archive-clear bits are consumed from this scalar. The
        // metadata updater always reads the actual protection from its record.
        info.Protection = 15; return true;
    }

    /// <summary>Continue an opt-in whole-sector overwrite inside a previously
    /// checked extent. The caller owns and initially clears the cursor storage.
    /// A miss accepts no bytes and requires ExecuteTrstGroup. All access remains
    /// serialized; mount epochs and metadata acceptance expire the certificate.
    /// No metadata or allocation may change through this entry.</summary>
    public static bool TryContinueOriginalDataWrite<T>(ref T p, APTR group, APTR cursor,
        ref Request r, out bool handled, out DOS.Error error)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        handled = false; error = DOS.Error.None;
        if (!p.UsesOriginalSfs2DataWrites || cursor.IsNull || group.IsNull || r.Node == 0 ||
            p.BlockBytes != 512 || r.Length == 0 || r.Length > int.MaxValue ||
            ((r.Position | r.Length) & 511) != 0 || r.Position >> 32 >= 512 ||
            !p.IsMapped(cursor, OriginalDataCursorBytes)) return true;
        if (p.ReadUInt32(cursor) != r.Node) return true;
        var size = ((ulong)p.ReadUInt32(cursor, 28) << 32) | p.ReadUInt32(cursor, 24);
        if (r.Position > size || r.Length > size - r.Position) return true;
        var context = p.Sfs2Context;
        if (!Sfs2MountContext.Ready(ref p, context) ||
            p.ReadUInt32(cursor, 4) != context.Raw || p.ReadUInt32(cursor, 8) != p.ReadUInt32(context, 8) ||
            p.ReadUInt32(cursor, 12) != p.ReadUInt32(context, 12) || p.ReadUInt32(cursor, 16) != group.Raw ||
            !p.IsMapped(group, GroupHeader) || p.ReadUInt32(group) != GroupMagic || p.ReadUInt32(group, 4) != 1 ||
            p.ReadUInt32(group, 20) != 512 || p.ReadUInt32(group, 24) != p.BlockCount) return true;
        var active = APTR.FromPointer(p.ReadUInt32(cursor, 20));
        if (p.RedoOverlay != active || p.ReadUInt32(group, 16) != active.Raw ||
            !Sfs2MetadataStore.Unpinned(ref p, active) || p.ReadUInt32(active, 24) != 0) return true;
        var cache = Sfs2MountContext.Cache(ref p, context);
        if (cache.IsNotNull && !Sfs2MetadataStore.Unpinned(ref p, cache)) return true;
        var logical = (uint)(r.Position >> 9); var start = p.ReadUInt32(cursor, 32);
        var offset = logical - start; var count = r.Length >> 9; var available = p.ReadUInt32(cursor, 40);
        if (logical < start || offset >= available || count > available - offset) return true;
        if (!p.IsMapped(r.Buffer, r.Length) || Overlap(group, p.ReadUInt32(group, 8), r.Buffer, r.Length) ||
            Overlap(cursor, OriginalDataCursorBytes, r.Buffer, r.Length) ||
            Overlap(context, p.ReadUInt32(context, 44), r.Buffer, r.Length)) return true;
        var first = p.ReadUInt32(cursor, 36); var block = first + offset;
        var limit = p.BlockCount - 1;
        if (block < first || block < p.ReservedBlocks || block >= limit || count > limit - block) return true;
        if (p.WriteProtected()) { error = DOS.Error.DiskWriteProtected; return false; }
        if (!p.WriteExistingFileDataBlocks(block, count, r.Buffer))
        { error = DOS.Error.DiskNotValidated; return false; }
        // The acknowledged metadata view is identical. Keep its revision and
        // predecessor certificate, but require the ordinary flush barrier.
        Sfs2MountContext.CommitState(ref p, context, 1);
        handled = true; return true;
    }
}
