using Amiga;

namespace CopperSFS;

/// <summary>Caller-owned, single-owner mount certificate. No disk representation.
/// Detach before recovery/external access and establish again from checked media.</summary>
public static class Sfs2MountContext
{
    private const uint Magic = 0x53324D43;
    public const uint HeaderBytes = 64;
    public static uint StorageBytes(uint blockBytes) => HeaderBytes + blockBytes;
    public static APTR RootImage(APTR context) => APTR.FromPointer(context.Raw + HeaderBytes);
    public static APTR Cache<T>(ref T p, APTR context) where T : struct, IAmigaGuestMemory =>
        context.IsNull ? APTR.Null : APTR.FromPointer(p.ReadUInt32(context, 36));
    internal static APTR Overlay<T>(ref T p, APTR context) where T : struct, IAmigaGuestMemory =>
        context.IsNull ? APTR.Null : APTR.FromPointer(p.ReadUInt32(context, 40));
    internal static void SetOverlay<T>(ref T p, APTR context, APTR arena) where T : struct, IAmigaGuestMemory
    { if (context.IsNotNull) p.WriteUInt32(context, 40, arena.Raw); }
    public static bool Durable<T>(ref T p, APTR context) where T : struct, IAmigaFileSystemPlatform =>
        Ready(ref p, context) && p.ReadUInt32(context, 52) == 0 && Overlay(ref p, context).IsNull;
    internal static void CommitState<T>(ref T p, APTR context, uint state) where T : struct, IAmigaGuestMemory
    { if (context.IsNotNull) p.WriteUInt32(context, 52, state); }

    internal static uint NextAttempt<T>(ref T p, APTR context) where T : struct, IAmigaGuestMemory
    {
        var generation = unchecked(p.ReadUInt32(context, 48) + 1);
        if (generation == 0)
        {
            var epoch = p.ReadUInt32(context, 8);
            if (epoch == uint.MaxValue) return 0;
            p.WriteUInt32(context, 8, epoch + 1); generation = 1;
        }
        p.WriteUInt32(context, 48, generation); return generation;
    }

    /// <summary>Recover and fully check the volume before attaching caller-owned
    /// state. The supplied span also holds the bounded clean metadata cache.</summary>
    public static bool Mount<T>(ref T p, ref AmigaSmartFileSystem2WriteCore.Work work, APTR context, uint bytes)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (context.IsNull || bytes < StorageBytes(p.BlockBytes) + Sfs2MetadataStore.HeaderBytes + p.BlockBytes + 32 ||
            !p.IsMapped(context, bytes) || p.RedoOverlay.IsNotNull ||
            Overlaps(context, bytes, work.Meta, p.BlockBytes) || Overlaps(context, bytes, work.Data, p.BlockBytes) ||
            Overlaps(context, bytes, work.Bitmap, p.BlockBytes) || Overlaps(context, bytes, work.Journal, p.BlockBytes)) return false;
        var previousEpoch = p.ReadUInt32(context) == Magic ? p.ReadUInt32(context, 8) : 0;
        var retryBarrier = previousEpoch != 0 && p.ReadUInt32(context, 52) != 0;
        if (previousEpoch == uint.MaxValue) { Suspend(ref p, context); return false; }
        var epoch = previousEpoch + 1;
        Suspend(ref p, context); p.Sfs2Context = APTR.Null;
        if (!AmigaSmartFileSystem2WriteCore.Recover(ref p, ref work) ||
            // A failed final barrier can leave a clean marker in the device's
            // volatile view. Recovery must make that checked view durable before
            // restoring the mount certificate, even when no replay was needed.
            (retryBarrier && !p.Flush()) ||
            !Sfs2Layout.Mount(ref p, work.Meta) || !Sfs2Layout.Root(ref p, work.Meta) ||
            !Establish(ref p, context, work.Meta, epoch))
        { Suspend(ref p, context); p.Sfs2Context = context; return false; }
        var cache = APTR.FromPointer(context.Raw + StorageBytes(p.BlockBytes));
        if (!Sfs2MetadataStore.Initialize(ref p, cache, bytes - StorageBytes(p.BlockBytes), p.BlockBytes, epoch))
        { Suspend(ref p, context); p.Sfs2Context = context; return false; }
        p.WriteUInt32(cache, 24, Sfs2MetadataStore.CleanCache);
        p.WriteUInt32(context, 36, cache.Raw); p.WriteUInt32(context, 44, bytes);
        p.Sfs2Context = context;
        return p.Sfs2Context == context;
    }

    private static bool Overlaps(APTR first, uint bytes, APTR other, uint length) =>
        other.IsNotNull && (first.Raw <= other.Raw ? other.Raw - first.Raw < bytes : first.Raw - other.Raw < length);

    private static bool Establish<T>(ref T p, APTR context, APTR selectedRoot, uint epoch)
        where T : struct, IAmigaFileSystemPlatform
    {
        if (context.IsNull || !p.IsMapped(context, StorageBytes(p.BlockBytes)) ||
            selectedRoot.IsNull || !p.IsMapped(selectedRoot, p.BlockBytes) ||
            !ValidRoot(ref p, selectedRoot, p.ReadUInt32(selectedRoot, 8)) ||
            (context.Raw <= selectedRoot.Raw ? selectedRoot.Raw - context.Raw < StorageBytes(p.BlockBytes) :
                context.Raw - selectedRoot.Raw < p.BlockBytes) ||
            p.BlockCount > AmigaSmartFileSystem2WriteCore.MaxVolumeBlocks) return false;
        p.Clear(context, HeaderBytes);
        p.Copy(selectedRoot, RootImage(context), p.BlockBytes);
        p.WriteUInt32(context, 0, Magic); p.WriteUInt32(context, 4, 1);
        p.WriteUInt32(context, 8, epoch == 0 ? 1u : epoch);
        p.WriteUInt32(context, 16, p.BlockBytes); p.WriteUInt32(context, 20, p.BlockCount);
        p.WriteUInt32(context, 24, p.ReservedBlocks);
        var g = p.Geometry;
        p.WriteUInt32(context, 28, (uint)g.StartByte); p.WriteUInt32(context, 32, (uint)(g.StartByte >> 32));
        return true;
    }

    internal static bool Active<T>(ref T p, APTR context) where T : struct, IAmigaGuestMemory =>
        context.IsNotNull && p.ReadUInt32(context) == Magic && p.ReadUInt32(context, 4) == 1;

    public static bool Ready<T>(ref T p, APTR context) where T : struct, IAmigaFileSystemPlatform =>
        context.IsNotNull && p.IsMapped(context, StorageBytes(p.BlockBytes)) && p.ReadUInt32(context) == Magic && p.ReadUInt32(context, 4) == 1 &&
        p.ReadUInt32(context, 16) == p.BlockBytes && p.ReadUInt32(context, 20) == p.BlockCount &&
        p.ReadUInt32(context, 24) == p.ReservedBlocks &&
        p.ReadUInt32(context, 28) == (uint)p.StartByte && p.ReadUInt32(context, 32) == (uint)(p.StartByte >> 32);

    public static void Suspend<T>(ref T p, APTR context) where T : struct, IAmigaGuestMemory
    { if (context.IsNotNull) p.WriteUInt32(context, 4, 2); }

    internal static bool Volume<T>(ref T p, APTR context, out AmigaSmartFileSystemReadOnlyCore.Volume volume)
        where T : struct, IAmigaFileSystemPlatform
    {
        volume = default;
        if (!Ready(ref p, context)) return false;
        var root = RootImage(context);
        volume = new AmigaSmartFileSystemReadOnlyCore.Volume { Format = AmigaSmartFileSystemFormat.Sfs2,
            Root = p.ReadUInt32(root, 104), NodeTree = p.ReadUInt32(root, 112), ExtentTree = p.ReadUInt32(root, 108),
            CaseSensitive = (p.ReadUInt8(root, 20) & 128) != 0 };
        return true;
    }

    internal static bool Load<T>(ref T p, ref AmigaSmartFileSystem2WriteCore.Work work)
        where T : struct, IAmigaFileSystemPlatform
    {
        var context = p.Sfs2Context;
        if (!Ready(ref p, context)) return false;
        var root = RootImage(context);
        work.BitmapBase = p.ReadUInt32(root, 96); work.AdminContainer = p.ReadUInt32(root, 100);
        work.RootContainer = p.ReadUInt32(root, 104); work.ExtentRoot = p.ReadUInt32(root, 108);
        work.NodeRoot = p.ReadUInt32(root, 112); work.CaseSensitive = (p.ReadUInt8(root, 20) & 128) != 0;
        return true;
    }

    // Root addresses currently remain fixed during mutation. Nevertheless, accept
    // a changed root pair atomically with the packet, never during trial editing.
    internal static bool Accept<T>(ref T p, APTR arena) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var context = p.Sfs2Context;
        if (context.IsNull) return true;
        if (!Ready(ref p, context)) return false;
        var root = RootImage(context);
        if (!AmigaSmartFileSystemRedoArena.TryFind(ref p, arena, 0, out var first) ||
            !AmigaSmartFileSystemRedoArena.TryFind(ref p, arena, p.BlockCount - 1, out var last)) return false;
        var selected = root;
        if (first.IsNotNull)
        {
            if (!ValidRoot(ref p, first, 0)) return false;
            if ((short)(p.ReadUInt16(first, 14) - p.ReadUInt16(selected, 14)) >= 0) selected = first;
        }
        if (last.IsNotNull)
        {
            if (!ValidRoot(ref p, last, p.BlockCount - 1)) return false;
            if ((short)(p.ReadUInt16(last, 14) - p.ReadUInt16(selected, 14)) > 0) selected = last;
        }
        var generation = unchecked(p.ReadUInt32(context, 12) + 1);
        if (generation == 0)
        {
            var epoch = p.ReadUInt32(context, 8);
            if (epoch == uint.MaxValue) return false;
            p.WriteUInt32(context, 8, epoch + 1); generation = 1;
        }
        if (selected != root) p.Copy(selected, root, p.BlockBytes);
        p.WriteUInt32(context, 12, generation);
        p.WriteUInt32(context, 52, 1);
        return true;
    }

    private static bool ValidRoot<T>(ref T p, APTR image, uint key) where T : struct, IAmigaFileSystemPlatform =>
        (key == 0 || key == p.BlockCount - 1) && p.ReadUInt32(image) == 0x53465302 && p.ReadUInt32(image, 8) == key &&
        p.ReadUInt16(image, 12) == 4 && p.ReadUInt32(image, 48) == p.BlockCount && p.ReadUInt32(image, 52) == p.BlockBytes &&
        p.ReadUInt32(image, 96) != 0 && p.ReadUInt32(image, 100) != 0 && p.ReadUInt32(image, 104) != 0 &&
        p.ReadUInt32(image, 108) != 0 && p.ReadUInt32(image, 112) != 0 &&
        AmigaSmartFileSystemReadOnlyCore.Checksum(ref p, image, AmigaSmartFileSystemFormat.Sfs2);
}
