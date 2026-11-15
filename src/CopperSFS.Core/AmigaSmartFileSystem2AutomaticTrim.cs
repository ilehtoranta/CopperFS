using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystem2WriteCore
{
    /// <summary>Automatic maintenance only. The owner must have finished normal
    /// commit and exclude all operations through discard completion. This checks
    /// a clean marker, never replays a journal or commits a logical overlay.
    /// Output is volatile authorization for this exclusive call only.</summary>
    public static bool CollectAutomaticTrim<T>(ref T p, ref Work w, APTR batch, uint mount, uint generation)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        w.Error = DOS.Error.None;
        if (p.Sfs2Context.IsNotNull && !Sfs2MountContext.Durable(ref p, p.Sfs2Context)) return Fail(ref w, DOS.Error.DiskNotValidated);
        if (!AmigaAutomaticTrimBatch.Current(ref p, batch, mount, generation) ||
            p.ReadUInt32(batch, AmigaAutomaticTrimBatch.Inputs) > AmigaAutomaticTrimBatch.Capacity)
            return Fail(ref w, DOS.Error.BadNumber);
        if (p.WriteProtected()) return Fail(ref w, DOS.Error.DiskWriteProtected);
        if (p.RedoOverlay.IsNotNull) return Fail(ref w, DOS.Error.DiskNotValidated);
        var blocks = p.BlockCount; var bytes = p.BlockBytes; var reserved = p.ReservedBlocks;
        if (bytes < 512 || bytes > uint.MaxValue / 8 || blocks <= reserved + 1)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if (!AmigaAutomaticTrimBatch.ValidInputs(ref p, batch, reserved, blocks - 1)) return Fail(ref w, DOS.Error.BadNumber);
        var prepared = p.ReadUInt32(batch, AmigaAutomaticTrimBatch.RootsValid) == 1;
        if (prepared)
        {
            w.BitmapBase = p.ReadUInt32(batch, AmigaAutomaticTrimBatch.PreparedRoots);
            w.RootContainer = p.ReadUInt32(batch, AmigaAutomaticTrimBatch.PreparedRoots + 4);
            w.AdminContainer = p.ReadUInt32(batch, AmigaAutomaticTrimBatch.PreparedRoots + 8);
            w.NodeRoot = p.ReadUInt32(batch, AmigaAutomaticTrimBatch.PreparedRoots + 12);
            w.ExtentRoot = p.ReadUInt32(batch, AmigaAutomaticTrimBatch.PreparedRoots + 16);
        }
        else
        {
            if (!Sfs2Layout.Root(ref p, w.Meta)) return Fail(ref w, DOS.Error.DiskNotValidated);
            w.BitmapBase = p.ReadUInt32(w.Meta, 96); w.AdminContainer = p.ReadUInt32(w.Meta, 100);
            w.RootContainer = p.ReadUInt32(w.Meta, 104); w.ExtentRoot = p.ReadUInt32(w.Meta, 108);
            w.NodeRoot = p.ReadUInt32(w.Meta, 112);
        }
        var bits = (bytes - 12) * 8;
        var maps = (blocks - 1) / bits + 1;
        if (w.BitmapBase < reserved || maps > blocks - 1 ||
            w.BitmapBase >= blocks - maps || w.RootContainer < reserved ||
            w.RootContainer >= blocks - 3 || w.AdminContainer < reserved ||
            w.AdminContainer >= blocks - 1 || w.NodeRoot < reserved ||
            w.NodeRoot >= blocks - 1 || w.ExtentRoot < reserved || w.ExtentRoot >= blocks - 1 ||
            p.HasAcknowledgedCheckpoint(w.RootContainer))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        // A low-level flush is not a commit notification: the clean marker and
        // absence of an acknowledged overlay are independent requirements.
        if (p.ReadUInt32(batch, AmigaAutomaticTrimBatch.Stage) != 2 &&
            (!p.Flush() || !Read(ref p, ref w, w.RootContainer + 2, TransactionOk, w.Meta) || p.ReadUInt32(w.Meta, 12) != 0))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if (!prepared && !AmigaSmartFileSystemReadOnlyCore.AutomaticSfs2RootObject(ref p, w.RootContainer, w.Meta))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        p.WriteUInt32(batch, AmigaAutomaticTrimBatch.PreparedRoots, w.BitmapBase);
        p.WriteUInt32(batch, AmigaAutomaticTrimBatch.PreparedRoots + 4, w.RootContainer);
        p.WriteUInt32(batch, AmigaAutomaticTrimBatch.PreparedRoots + 8, w.AdminContainer);
        p.WriteUInt32(batch, AmigaAutomaticTrimBatch.PreparedRoots + 12, w.NodeRoot);
        p.WriteUInt32(batch, AmigaAutomaticTrimBatch.PreparedRoots + 16, w.ExtentRoot);
        p.WriteUInt32(batch, AmigaAutomaticTrimBatch.RootsValid, 1);
        uint loaded = uint.MaxValue;
        p.WriteUInt32(batch, AmigaAutomaticTrimBatch.Outputs, 0);
        p.WriteUInt32(batch, AmigaAutomaticTrimBatch.OutputBlocks, 0);
        for (var i = 0u; i < p.ReadUInt32(batch, AmigaAutomaticTrimBatch.Inputs); i++)
        {
            var first = p.ReadUInt32(batch, AmigaAutomaticTrimBatch.Input + (int)i * 8);
            var count = p.ReadUInt32(batch, AmigaAutomaticTrimBatch.Input + (int)i * 8 + 4);
            if (count == 0 || first < reserved || first >= blocks - 1 || count > blocks - 1 - first)
                return Fail(ref w, DOS.Error.BadNumber);
            uint run = 0, length = 0;
            var end = first + count;
            while (first < end)
            {
                var map = first / bits;
                var pageFirst = map * bits;
                var pageCount = blocks - pageFirst < bits ? blocks - pageFirst : bits;
                if (loaded != map)
                {
                    if (!Read(ref p, ref w, w.BitmapBase + map, BitmapId, w.Bitmap)) return false;
                    if (!TrimCheckAllocated(ref p, ref w, pageFirst, pageCount, w.RootContainer) ||
                        !TrimCheckAllocated(ref p, ref w, pageFirst, pageCount, w.RootContainer + 2) ||
                        !TrimCheckAllocated(ref p, ref w, pageFirst, pageCount, w.AdminContainer) ||
                        !TrimCheckAllocated(ref p, ref w, pageFirst, pageCount, w.NodeRoot) ||
                        !TrimCheckAllocated(ref p, ref w, pageFirst, pageCount, w.ExtentRoot))
                        return false;
                    var from = pageFirst > w.BitmapBase ? pageFirst : w.BitmapBase;
                    var until = pageFirst + pageCount < w.BitmapBase + maps ? pageFirst + pageCount : w.BitmapBase + maps;
                    for (var key = from; key < until; key++)
                        if (TrimFree(ref p, w.Bitmap, key - pageFirst)) return Fail(ref w, DOS.Error.DiskNotValidated);
                    loaded = map;
                }
                var take = pageFirst + pageCount - first;
                if (take > end - first) take = end - first;
                AmigaAutomaticTrimBatch.ScanBitmap(ref p, batch, w.Bitmap, 12, pageFirst, first, take, true, ref run, ref length);
                first += take;
            }
            if (length != 0) AmigaAutomaticTrimBatch.FreeRun(ref p, batch, run, length);
        }
        p.WriteUInt32(batch, AmigaAutomaticTrimBatch.Stage, 2);
        return true;
    }
}

public static partial class AmigaSmartFileSystemReadOnlyCore
{
    // Reuse the exact clean-mount root-object checks without selecting both
    // volume root copies a second time merely to recover the same scalars.
    internal static bool AutomaticSfs2RootObject<T>(ref T p, uint block, APTR scratch)
        where T : struct, IAmigaFileSystemPlatform
    {
        var volume = new Volume { Format = AmigaSmartFileSystemFormat.Sfs2, Root = block };
        var root = HeaderView(ref p, block, Objects, scratch, volume.Format);
        return root.IsNotNull && p.ReadUInt32(root, 12) == 0 &&
            Scan(ref p, volume, root, 1, out var at, out _) && (p.ReadUInt8(root, at + 26) & 128) != 0;
    }
}
