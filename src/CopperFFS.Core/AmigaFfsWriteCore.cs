using Amiga;
using System.Runtime.CompilerServices;

namespace CopperStart.Dos;

// DOS0–DOS5 writer. Serial handler ownership; caller supplies three filesystem
// blocks of scratch. No managed allocation or host filesystem operations.
// Metadata transactions invalidate the root before mutation. A failed device
// write leaves it invalid so a host checkpoint cannot publish a partial image.
public static partial class AmigaFfsWriteCore
{
    private const bool MountIndexEnabled = AmigaFfsMountIndex.Enabled;

    public struct Work
    {
        public APTR Meta, Data, Bitmap;
        public uint BitmapBlock, BitmapFirst, BitmapCount, BitmapDirty, NextKey;
        internal uint BitmapChecksumDelta;
        public uint DosType;
        internal uint CacheParent, CacheOther, CacheNew;
        // DOS4/5 record edits for Finish: CacheKey's record follows its
        // header (CacheFresh: it has none yet); CacheDrop's record goes.
        internal uint CacheKey, CacheDrop;
        internal bool CacheFresh;
        // An unindexed mount's summary supplies the free count and bitmap map.
        internal bool Summary;
        internal uint UnlinkedOwner;
        internal uint FreedFirst, FreedCount;
        // Optional initialized storage shared by work records of one serial mount.
        public APTR Index;
        // Owner must finish the file-write group before close, flush, handoff
        // or another operation on this volume.
        public bool GroupFileWrites;
        // Set only when WriteDataBlocks reaches a device which enforces the
        // full WriteProtected policy before accepting an existing-data write.
        public bool DeviceChecksDataProtection;
        // Fixed disks may retain short sequential appends inside an open
        // group. The next observing operation drains them before publication.
        public bool BufferSmallAppends;
        // Single-owner fixed mounts may complete successive namespace packets
        // with a shared invalid root. FinishFileWriteGroup publishes validity.
        public bool GroupMetadata;
        // The owner serves root reads from NamespaceRoot while a group is open.
        public bool ReadsGroupedRoot;
        // The owner also serves directory reads from NamespaceDirectory.
        public bool ReadsGroupedDirectories;
        internal bool RootLinkChanged;
        internal uint GroupHeader;
        internal APTR RootImage;
        internal APTR ActiveBitmap;
        internal uint RootKey;
        internal uint IndexedUsed, RestoreFile, RestoreSize;
        internal bool LayoutKnown, WritingIndexedFile, FreeCountKnown;
        internal bool CheckedTracking, TrackFrees;
        internal bool OpenProtectionChecked;
        // The caller's current DateStamp. When set, creates, deletes and
        // renames date the directories they change, new headers get their
        // creation time and the root records the disk-altered date.
        public bool StampTime;
        public uint NowDays, NowMinutes, NowTicks;
        public DOS.Error Error;
    }

    // Write the caller's time into a header or root's date (Tail+420..428)
    // and keep its checksum at offset 20 consistent for incremental sealing.
    private static void StampDate<T>(ref T p, ref Work w, APTR image, int at) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!w.StampTime || image.IsNull) return;
        var sum = unchecked(p.ReadUInt32(image, 20) + p.ReadUInt32(image, at) + p.ReadUInt32(image, at + 4) + p.ReadUInt32(image, at + 8) -
            w.NowDays - w.NowMinutes - w.NowTicks);
        p.WriteUInt32(image, at, w.NowDays); p.WriteUInt32(image, at + 4, w.NowMinutes); p.WriteUInt32(image, at + 8, w.NowTicks);
        p.WriteUInt32(image, 20, sum);
    }

    // Date a directory whose hash table this operation changed through a
    // sibling's link instead of its own header.
    private static bool StampDirectory<T>(ref T p, ref Work w, in AmigaFfsGeometry geometry, uint directory)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!w.StampTime) return true;
        if (MountIndexEnabled && directory == w.RootKey && w.RootImage.IsNotNull && w.DosType < 0x444F5304)
        {
            StampDate(ref p, ref w, w.RootImage, geometry.Tail + 420);
            w.RootLinkChanged = true;
            return true;
        }
        var current = InspectHeader(ref p, ref w, in geometry, directory);
        if (current.IsNull) return false;
        var target = EditDirectoryLinks(ref p, ref w, directory, current, true);
        if (target.IsNull) return false;
        StampDate(ref p, ref w, target, geometry.Tail + 420);
        return target != w.Meta || Put(ref p, ref w, directory, target, SealedHeader);
    }
    public struct Request
    {
        public uint Directory, Header, Position, Length;
        public APTR Name, Buffer;
    }
    private static bool Fail(ref Work w, DOS.Error error) { w.Error = error; return false; }
    private static bool Header<T>(ref T p, ref Work w, uint key) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var g = AmigaFfsGeometry.Read(ref p);
        var image = InspectHeader(ref p, ref w, in g, key);
        if (image.IsNull) return false;
        if (image != w.Meta) p.Copy(image, w.Meta, g.BlockBytes);
        return true;
    }

    // Borrowed only until the next block access. Writers that need a mutable
    // scratch image call Header; read-only preflight consumes the fields here.
    private static APTR InspectHeader<T>(ref T p, ref Work w, in AmigaFfsGeometry g, uint key)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (MountIndexEnabled && key == w.RootKey && w.LayoutKnown && w.RootImage.IsNotNull && w.Index.IsNotNull &&
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.Status) == AmigaFfsMountIndex.Updating)
            return w.RootImage;
        if (MountIndexEnabled && w.ReadsGroupedDirectories && w.LayoutKnown && w.GroupMetadata && key != 0 &&
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.PendingParent) == key &&
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.Status) == AmigaFfsMountIndex.Updating)
            return APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.ParentImage));
        if (MountIndexEnabled && w.GroupFileWrites && w.LayoutKnown && key != 0 &&
            (w.GroupHeader == key || HasFileWriteGroup(ref p, w.Index)) &&
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.PendingHeader) == key)
            return APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.HeaderImage));
        // Hash chains and link chains pass through hard and soft link headers.
        var image = AmigaDiskFileSystemCore.HeaderView(ref p, in g, key, w.Meta, APTR.Null, 0, true);
        if (image.IsNull) w.Error = DOS.Error.DiskNotValidated;
        return image;
    }
    private static void Seal<T>(ref T p, APTR buffer, int checksum) where T : struct, IAmigaWritableFileSystemPlatform
    {
        p.WriteUInt32(buffer, checksum, 0);
        // Keep the writer's unrolled sum local. Sharing the reader's
        // checksum loop adds an extra call on every verified read.
        var remaining = p.BlockBytes;
        var cursor = buffer;
        uint sum = 0;
        var end = APTR.FromPointer(cursor.Raw + (remaining & ~31u));
        while (cursor != end)
        {
            sum = unchecked(sum + p.ReadUInt32(cursor) + p.ReadUInt32(cursor, 4) +
                p.ReadUInt32(cursor, 8) + p.ReadUInt32(cursor, 12) +
                p.ReadUInt32(cursor, 16) + p.ReadUInt32(cursor, 20) +
                p.ReadUInt32(cursor, 24) + p.ReadUInt32(cursor, 28));
            cursor = APTR.FromPointer(cursor.Raw + 32);
        }
        remaining &= 31;
        while (remaining >= 4)
        {
            sum = unchecked(sum + p.ReadUInt32(cursor));
            cursor = APTR.FromPointer(cursor.Raw + 4); remaining -= 4;
        }
        p.WriteUInt32(buffer, checksum, unchecked(0u - sum));
    }
    // A link change can adjust the checksum of an already verified header
    // by the difference between its old and new pointer.
    private const int SealedHeader = -2;
    private static bool Put<T>(ref T p, ref Work w, uint key, APTR buffer, int checksum) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (w.Error != DOS.Error.None) return false;
        if (MountIndexEnabled && w.GroupHeader != 0 && key == w.GroupHeader)
        {
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.FreshHeaderImage, 0);
            var owned = APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.HeaderImage));
            if (buffer != owned) p.Copy(buffer, owned, p.BlockBytes);
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.PendingHeader, key);
            return true;
        }
        if (MountIndexEnabled && w.GroupHeader != 0 && checksum == 20 && buffer == w.Meta && p.ReadUInt32(buffer) == 16 &&
            p.ReadUInt32(buffer, 4) == key && p.ReadUInt32(w.Index, AmigaFfsMountIndex.TableImage) != 0)
        {
            if (p.ReadUInt32(w.Index, AmigaFfsMountIndex.FirstExtension) == key)
            {
                p.WriteUInt32(w.Index, AmigaFfsMountIndex.FreshTableImage, 0);
                p.Copy(buffer, APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.FirstExtensionImage)), p.BlockBytes);
                return true;
            }
            var previous = p.ReadUInt32(w.Index, AmigaFfsMountIndex.PendingTable);
            if (previous != key && previous != 0 && !PublishGroupTable(ref p, ref w)) return false;
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.FreshTableImage, 0);
            p.Copy(buffer, APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.TableImage)), p.BlockBytes);
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.PendingTable, key);
            return true;
        }
        if (checksum >= 0) Seal(ref p, buffer, checksum);
        // The validity writes already use the mount's private root image.
        // Bypass the duplicate retained copy. The platform invalidates the
        // old physical cache entry even if the device completes only a prefix.
        if (MountIndexEnabled && key == w.RootKey && buffer == w.RootImage && w.LayoutKnown)
            return p.WriteDataBlocks(key, 1, buffer) || Fail(ref w, DOS.Error.DiskNotValidated);
        // A write-through retained image avoids immediately rereading the same
        // root, pointer table or OFS chain page. Failed writes must invalidate
        // that image under the platform contract; durability is unchanged.
        var written =
#if COPPERFS_FFS_HANDLER
            checksum is 20 or SealedHeader ? p.WriteSealedClassicBlock(key, buffer) :
#endif
            p.SupportsRetainedBlockWrites ? p.WriteRetainedBlock(key, buffer) : p.WriteBlock(key, buffer);
        if (!written)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        // Link, unlink and directory-cache operations can edit the root between
        // the two validity writes. Keep the private image current in that case.
        if (MountIndexEnabled && key == w.RootKey && w.RootImage.IsNotNull && buffer != w.RootImage)
            p.Copy(buffer, w.RootImage, p.BlockBytes);
        return true;
    }
    private static bool Prepare<T>(ref T p, ref Work w, in AmigaFfsGeometry geometry, uint needed, bool scan = true, uint attributeHeader = 0,
        uint keepDirectory = 0) where T : struct, IAmigaWritableFileSystemPlatform
    {
        w.RestoreFile = 0; w.GroupHeader = 0; w.WritingIndexedFile = false; w.FreeCountKnown = false;
        w.CacheParent = w.CacheOther = w.CacheNew = 0;
        w.CacheKey = w.CacheDrop = 0; w.CacheFresh = false; w.Summary = false;
        w.RootLinkChanged = false;
        w.FreedCount = 0; w.CheckedTracking = false;
        w.Error = DOS.Error.None;
        if (geometry.BitmapBits == 0) return Fail(ref w, DOS.Error.DiskNotValidated);
        if (w.OpenProtectionChecked) w.OpenProtectionChecked = false;
        else if (p.WriteProtected()) return Fail(ref w, DOS.Error.DiskWriteProtected);
        w.RootKey = geometry.RootBlock;
        w.RootImage = APTR.Null;
        w.BitmapBlock = 0; w.BitmapDirty = 0; w.BitmapChecksumDelta = 0; w.NextKey = geometry.ReservedBlocks;
#if COPPERFS_NO_FFS_MOUNT_INDEX
        w.LayoutKnown = false;
        return PrepareUnindexed(ref p, ref w, in geometry, needed, scan);
#else
        var status = w.Index.IsNull ? 0u : p.ReadUInt32(w.Index, AmigaFfsMountIndex.Status);
        w.LayoutKnown = status == AmigaFfsMountIndex.Clean ||
            (w.GroupMetadata && status == AmigaFfsMountIndex.MetadataOpen) ||
            (w.GroupFileWrites && status == AmigaFfsMountIndex.GroupOpen);
        if (!w.LayoutKnown) return PrepareUnindexed(ref p, ref w, in geometry, needed, scan);

        w.RootImage = APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.RootImage));
        w.DosType = p.ReadUInt32(w.Index, AmigaFfsMountIndex.Type);
        w.IndexedUsed = p.ReadUInt32(w.Index, AmigaFfsMountIndex.Used);
        w.NextKey = p.ReadUInt32(w.Index, AmigaFfsMountIndex.NextCandidate);
        if (status == AmigaFfsMountIndex.MetadataOpen)
        {
            if (!PublishMetadataAttributes(ref p, w.Index, attributeHeader)) return Fail(ref w, DOS.Error.DiskNotValidated);
            var parent = p.ReadUInt32(w.Index, AmigaFfsMountIndex.PendingParent);
            if (parent != 0 && parent != keepDirectory)
            {
                // The native owner treats physical metadata writes between
                // packets as untracked. This publication belongs to the
                // existing group, just like PublishMetadataAttributes.
                p.WriteUInt32(w.Index, AmigaFfsMountIndex.Status, AmigaFfsMountIndex.Updating);
                if (!PublishParent(ref p, ref w))
                {
                    p.WriteUInt32(w.Index, AmigaFfsMountIndex.Status, AmigaFfsMountIndex.GroupFailed);
                    return false;
                }
                p.WriteUInt32(w.Index, AmigaFfsMountIndex.Status, AmigaFfsMountIndex.MetadataOpen);
            }
        }
        return geometry.BlockCount - w.IndexedUsed >= needed || Fail(ref w, DOS.Error.DiskFull);
#endif
    }

    // ACTION_INFO's used count. A clean index or summary answers without I/O.
    // A volume too large for the index establishes the summary on the first
    // poll, so later polls stop rereading every bitmap page.
    public static bool CountUsed<T>(ref T p, ref Work w, out uint used) where T : struct, IAmigaWritableFileSystemPlatform
    {
        used = 0;
        if (MountIndexEnabled && w.Index.IsNotNull && p.ReadUInt32(w.Index, AmigaFfsMountIndex.Status) == 0)
        {
            var geometry = AmigaFfsGeometry.Read(ref p);
            if (geometry.BitmapBits != 0 && AmigaFfsMountIndex.Declines(ref p, w.Index, in geometry))
            {
                w.Error = DOS.Error.None; w.LayoutKnown = false; w.Summary = false; w.FreeCountKnown = false;
                w.RootKey = geometry.RootBlock; w.RootImage = APTR.Null;
                w.BitmapBlock = 0; w.BitmapDirty = 0; w.BitmapChecksumDelta = 0; w.NextKey = geometry.ReservedBlocks;
                if (PrepareUnindexed(ref p, ref w, in geometry, 0, true)) { used = w.IndexedUsed; return true; }
                w.Error = DOS.Error.None;
            }
        }
        return AmigaDiskFileSystemCore.CountUsed(ref p, w.Data, w.Bitmap, out used, w.Index);
    }

    // Only a cold or unindexed mount needs validation and bitmap traversal
    // scratch. Keep that work out of indexed mutation packets.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool PrepareUnindexed<T>(ref T p, ref Work w, in AmigaFfsGeometry geometry, uint needed, bool scan)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!LoadDosType(ref p, ref w)) return false;
        if (!Header(ref p, ref w, geometry.RootBlock) || p.ReadUInt32(w.Meta, geometry.Tail + 312) != uint.MaxValue)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if (MountIndexEnabled && w.Index.IsNotNull &&
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.Status) == AmigaFfsMountIndex.Summarized)
        {
            // The single mount owner published every bitmap change since the
            // summary was checked; any reset or failure would have discarded it.
            w.Summary = true; w.FreeCountKnown = true;
            w.IndexedUsed = p.ReadUInt32(w.Index, AmigaFfsMountIndex.Used);
            w.NextKey = p.ReadUInt32(w.Index, AmigaFfsMountIndex.NextCandidate);
            if (IsFree(ref p, ref w, geometry.RootBlock) || w.Error != DOS.Error.None) return Fail(ref w, DOS.Error.DiskNotValidated);
            return geometry.BlockCount - w.IndexedUsed >= needed || Fail(ref w, DOS.Error.DiskFull);
        }
        if (MountIndexEnabled && scan && w.Index.IsNotNull)
        {
            if (!AmigaFfsMountIndex.Build(ref p, w.Index, w.Meta, w.Bitmap, in geometry, w.DosType))
                return Fail(ref w, DOS.Error.DiskNotValidated);
            w.LayoutKnown = p.ReadUInt32(w.Index, AmigaFfsMountIndex.Status) == AmigaFfsMountIndex.Clean;
            if (w.LayoutKnown)
            {
                w.RootImage = APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.RootImage));
                w.IndexedUsed = p.ReadUInt32(w.Index, AmigaFfsMountIndex.Used);
                return geometry.BlockCount - w.IndexedUsed >= needed || Fail(ref w, DOS.Error.DiskFull);
            }
        }
        uint used = 0;
        if (scan && !AmigaDiskFileSystemCore.CountUsed(ref p, w.Data, w.Bitmap, out used))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if (IsFree(ref p, ref w, geometry.RootBlock)) return Fail(ref w, DOS.Error.DiskNotValidated);
        // Overwrites validate the touched pages on demand. Growth and other
        // metadata operations still inspect the whole map.
        if (!scan) return w.Error == DOS.Error.None;
        var bitmapBits = geometry.BitmapBits;
        var maps = AmigaGeometryMath.Divide(geometry.BlockCount - geometry.ReservedBlocks - 1, bitmapBits) + 1;
        var cursor = new AmigaDiskFileSystemCore.BitmapCursor();
        for (var index = 0u; index < maps; index++)
        {
            if (!AmigaDiskFileSystemCore.BitmapBlock(ref p, ref cursor, index, w.Meta, out var map) || IsFree(ref p, ref w, map))
                return Fail(ref w, DOS.Error.DiskNotValidated);
        }
        // Bitmap extension blocks are themselves allocated filesystem blocks.
        if (!Header(ref p, ref w, geometry.RootBlock)) return false;
        // A volume the index declined keeps the checked count and bitmap map
        // as a summary, so later packets need not repeat this scan.
        var summary = MountIndexEnabled && w.Index.IsNotNull &&
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.Status) == 0 ? AmigaFfsMountIndex.SummaryMap(w.Index) : APTR.Null;
        var capacity = summary.IsNull ? 0 : AmigaFfsMountIndex.SummaryCapacity(ref p, w.Index);
        if (summary.IsNotNull)
            p.Copy(APTR.FromPointer(w.Meta.Raw + (uint)geometry.Tail + 316), summary, AmigaFfsMountIndex.SummaryRootPointers * 4);
        var extension = p.ReadUInt32(w.Meta, geometry.Tail + 416);
        var extensions = 0u;
        for (var guard = 0u; extension != 0; guard++)
        {
            if (guard >= geometry.BlockCount || extension < geometry.ReservedBlocks ||
                extension >= geometry.BlockCount || IsFree(ref p, ref w, extension) ||
                !p.ReadBlock(extension, w.Data)) return Fail(ref w, DOS.Error.DiskNotValidated);
            if (extensions < capacity)
                p.WriteUInt32(summary, (int)(AmigaFfsMountIndex.SummaryRootPointers + 1 + extensions) * 4, extension);
            extensions++;
            extension = p.ReadUInt32(w.Data, (int)geometry.BlockBytes - 4);
        }
        if (w.Error != DOS.Error.None) return false;
        if (summary.IsNotNull && extensions <= capacity)
        {
            p.WriteUInt32(summary, (int)AmigaFfsMountIndex.SummaryRootPointers * 4, extensions);
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.Used, used);
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.NextCandidate, geometry.ReservedBlocks);
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.Status, AmigaFfsMountIndex.Summarized);
            w.Summary = true;
        }
        if (geometry.BlockCount - used < needed) return Fail(ref w, DOS.Error.DiskFull);
        w.IndexedUsed = used;
        w.FreeCountKnown = true;
        return true;
    }
    // These operations change names, attributes or add a previously free key.
    // The retained file's allocation and logical block order survive the commit.
    private static void KeepFile<T>(ref T p, ref Work w) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (MountIndexEnabled && w.LayoutKnown)
        {
            w.RestoreFile = p.ReadUInt32(w.Index, AmigaFfsMountIndex.Header);
            w.RestoreSize = p.ReadUInt32(w.Index, AmigaFfsMountIndex.FileBytes);
        }
    }

    // Growth needs a verified free count. When a workspace is available,
    // establish it before walking an existing file so that first walk can
    // also populate its logical block map.
    private static bool PrepareGrowthIndex<T>(ref T p, ref Work w, in AmigaFfsGeometry geometry)
        where T : struct, IAmigaWritableFileSystemPlatform =>
        !MountIndexEnabled || w.LayoutKnown || w.Index.IsNull || Prepare(ref p, ref w, in geometry, 0);

    private static bool CanGrow<T>(ref T p, ref Work w, in AmigaFfsGeometry geometry, uint blocks)
        where T : struct, IAmigaWritableFileSystemPlatform =>
        (MountIndexEnabled && w.LayoutKnown) || w.FreeCountKnown ? blocks <= geometry.BlockCount - w.IndexedUsed || Fail(ref w, DOS.Error.DiskFull) :
        Prepare(ref p, ref w, in geometry, blocks);

    private static bool Begin<T>(ref T p, ref Work w, in AmigaFfsGeometry geometry, uint valid = 0, bool keepRuns = false) where T : struct, IAmigaWritableFileSystemPlatform
    {
        // The summary's count is unpublished until this mutation's valid root.
        if (MountIndexEnabled && valid == 0 && w.Summary &&
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.Status) == AmigaFfsMountIndex.Summarized)
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.Status, AmigaFfsMountIndex.SummaryBusy);
        if (MountIndexEnabled && valid == 0 && w.LayoutKnown)
        {
            if (p.ReadUInt32(w.Index, AmigaFfsMountIndex.Status) is AmigaFfsMountIndex.Clean or AmigaFfsMountIndex.MetadataOpen)
            {
                p.WriteUInt32(w.Index, AmigaFfsMountIndex.PublishedHeader, 0);
                p.WriteUInt32(w.Index, AmigaFfsMountIndex.PublishedTable, 0);
                p.WriteUInt32(w.Index, AmigaFfsMountIndex.PublishedParent, 0);
                p.WriteUInt32(w.Index, AmigaFfsMountIndex.SealedAttributeHeader, 0);
                p.WriteUInt32(w.Index, AmigaFfsMountIndex.FreshHeaderImage, 0);
                p.WriteUInt32(w.Index, AmigaFfsMountIndex.FreshTableImage, 0);
            }
            if (!keepRuns) p.WriteUInt32(w.Index, AmigaFfsMountIndex.RunHeader, 0);
            if (!w.WritingIndexedFile || w.RestoreFile != p.ReadUInt32(w.Index, AmigaFfsMountIndex.Companion))
                p.WriteUInt32(w.Index, AmigaFfsMountIndex.ParkedHeader, 0);
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.ReadHeader, 0);
            var continuing = (w.GroupFileWrites && HasFileWriteGroup(ref p, w.Index)) ||
                (w.GroupMetadata && p.ReadUInt32(w.Index, AmigaFfsMountIndex.Status) == AmigaFfsMountIndex.MetadataOpen);
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.Status, AmigaFfsMountIndex.Updating);
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.Header, 0);
            if (continuing) return true;
        }
        var image = MountIndexEnabled ? w.RootImage : APTR.Null;
        if (!MountIndexEnabled || image.IsNull)
        {
            if (!Header(ref p, ref w, geometry.RootBlock)) return false;
            image = w.Meta;
        }
        // The valid flag vouches for every bitmap, header and data write of
        // the operation. A write-back device can reorder writes between
        // barriers, so make them durable before the root says so.
        if (valid != 0 && !p.Flush()) return Fail(ref w, DOS.Error.DiskNotValidated);
        // The root's disk-altered date (v_days) follows every published change.
        if (valid != 0) StampDate(ref p, ref w, image, geometry.Tail + 472);
        var sum = p.ReadUInt32(image, 20);
        var previous = p.ReadUInt32(image, geometry.Tail + 312);
        p.WriteUInt32(image, geometry.Tail + 312, valid);
        p.WriteUInt32(image, 20, unchecked(sum + previous - valid));
        return Put(ref p, ref w, geometry.RootBlock, image, -1);
    }
    private static bool Finish<T>(ref T p, ref Work w, in AmigaFfsGeometry geometry) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (w.DosType >= 0x444F5304 && !SyncCaches(ref p, ref w)) return false;
        var keepOpen = MountIndexEnabled && w.WritingIndexedFile && w.GroupFileWrites && w.LayoutKnown && w.RootImage.IsNotNull && w.RestoreFile != 0;
        var keepMetadata = MountIndexEnabled && w.GroupMetadata && !keepOpen && w.LayoutKnown && w.RootImage.IsNotNull && w.DosType < 0x444F5304;
        if (MountIndexEnabled && (keepOpen || keepMetadata) && p.ReadUInt32(w.Index, AmigaFfsMountIndex.BitmapImage) != 0)
        {
            if (w.BitmapDirty != 0)
            {
                SealBitmap(ref p, ref w);
                var image = w.ActiveBitmap;
                if (image == w.Bitmap)
                {
                    image = APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.BitmapImage));
                    if (w.Bitmap != image) p.Copy(w.Bitmap, image, p.BlockBytes);
                }
                p.WriteUInt32(w.Index, AmigaFfsMountIndex.PendingBitmapImage, image.Raw);
                p.WriteUInt32(w.Index, AmigaFfsMountIndex.PendingBitmap, w.BitmapBlock);
                w.BitmapDirty = 0;
            }
        }
        else if (!FlushBitmap(ref p, ref w)) return false;
        if (MountIndexEnabled && keepMetadata)
        {
            // A reader sharing this mount sees the owned root in HeaderView.
            // Other callers still need the changed hash link on the device.
            if (w.RootLinkChanged && !w.ReadsGroupedRoot &&
                !Put(ref p, ref w, w.RootKey, w.RootImage, -1)) return false;
        }
        else if ((!MountIndexEnabled || !keepOpen) &&
            ((MountIndexEnabled && !PublishGroupHeader(ref p, ref w)) || !Begin(ref p, ref w, in geometry, uint.MaxValue))) return false;
        if (MountIndexEnabled && w.Summary && w.FreeCountKnown &&
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.Status) == AmigaFfsMountIndex.SummaryBusy)
        {
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.Used, w.IndexedUsed);
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.NextCandidate, w.NextKey);
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.Status, AmigaFfsMountIndex.Summarized);
        }
        if (MountIndexEnabled && w.LayoutKnown && p.ReadUInt32(w.Index, AmigaFfsMountIndex.Status) == AmigaFfsMountIndex.Updating)
        {
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.Used, w.IndexedUsed);
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.NextCandidate, w.NextKey);
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.FileBytes, w.RestoreSize);
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.Header, w.RestoreFile);
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.Status, keepOpen ? AmigaFfsMountIndex.GroupOpen :
                keepMetadata ? AmigaFfsMountIndex.MetadataOpen : AmigaFfsMountIndex.Clean);
        }
        w.RestoreFile = 0;
        if (w.FreedCount != 0) { p.ObserveFreedRange(w.FreedFirst, w.FreedCount); w.FreedCount = 0; }
        return true;
    }
    private static void SealBitmap<T>(ref T p, ref Work w) where T : struct, IAmigaWritableFileSystemPlatform
    {
        // Include repeated transitions to a bit since the last verified image.
        p.WriteUInt32(w.ActiveBitmap, 0, unchecked(p.ReadUInt32(w.ActiveBitmap) + w.BitmapChecksumDelta));
        w.BitmapChecksumDelta = 0;
    }

    private static bool FlushBitmap<T>(ref T p, ref Work w) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (w.Error != DOS.Error.None) return false;
        if (w.BitmapDirty != 0)
        {
            SealBitmap(ref p, ref w);
            if (!PublishBitmap(ref p, ref w, w.BitmapBlock, w.ActiveBitmap)) return false;
            w.BitmapDirty = 0;
        }
        else if (MountIndexEnabled && w.LayoutKnown && (w.GroupFileWrites || w.GroupMetadata))
        {
            var key = p.ReadUInt32(w.Index, AmigaFfsMountIndex.PendingBitmap);
            if (key == 0) return true;
            var image = APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.PendingBitmapImage));
            if (!PublishBitmap(ref p, ref w, key, image)) return false;
        }
        if (MountIndexEnabled && w.LayoutKnown && (w.GroupFileWrites || w.GroupMetadata)) p.WriteUInt32(w.Index, AmigaFfsMountIndex.PendingBitmap, 0);
        return true;
    }

    private static bool PublishBitmap<T>(ref T p, ref Work w, uint key, APTR image) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!MountIndexEnabled || !w.LayoutKnown) return Put(ref p, ref w, key, image, -1);
        // Preflight may switch pages before Begin. An owned pending bitmap
        // write is part of the open transaction even on that path.
        var status = p.ReadUInt32(w.Index, AmigaFfsMountIndex.Status);
        var open = status is AmigaFfsMountIndex.GroupOpen or AmigaFfsMountIndex.MetadataOpen;
        if (open) p.WriteUInt32(w.Index, AmigaFfsMountIndex.Status, AmigaFfsMountIndex.Updating);
        var written = p.ReadUInt32(w.Index, AmigaFfsMountIndex.BitmapPages) != 0
            ? p.WriteDataBlocks(key, 1, image) : Put(ref p, ref w, key, image, -1);
        if (open) p.WriteUInt32(w.Index, AmigaFfsMountIndex.Status, written ? status : AmigaFfsMountIndex.GroupFailed);
        return written || Fail(ref w, DOS.Error.DiskNotValidated);
    }
    private static bool LoadBitmap<T>(ref T p, ref Work w, uint key) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var cursor = new AmigaDiskFileSystemCore.BitmapCursor();
        return LoadBitmap(ref p, ref w, key, ref cursor);
    }
    private static bool LoadBitmap<T>(ref T p, ref Work w, uint key, ref AmigaDiskFileSystemCore.BitmapCursor cursor)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        // The mount geometry is fixed for this work record. Retain only the
        // valid part of the loaded page so hits need no geometry reconstruction
        // or division, and padding beyond the volume never becomes a hit.
        // The clamped span cannot wrap, so unsigned subtraction also rejects lower keys.
        if (w.BitmapBlock != 0 && unchecked(key - w.BitmapFirst) < w.BitmapCount)
            return w.Error == DOS.Error.None;
        var blockCount = p.BlockCount;
        var reservedBlocks = p.ReservedBlocks;
        var bitmapBits = (p.BlockBytes - 4) * 8;
        if (bitmapBits == 0) return Fail(ref w, DOS.Error.DiskNotValidated);
        if (key < reservedBlocks || key >= blockCount) return Fail(ref w, DOS.Error.DiskNotValidated);
        var index = AmigaGeometryMath.Divide(key - reservedBlocks, bitmapBits);
        if (w.BitmapDirty != 0 && !FlushBitmap(ref p, ref w)) return false;
        uint block;
        if (MountIndexEnabled && w.LayoutKnown) block = AmigaFfsMountIndex.Map(ref p, w.Index, index);
        else if (MountIndexEnabled && w.Summary)
        {
            if (!SummaryBitmapBlock(ref p, ref w, index, out block)) return Fail(ref w, DOS.Error.DiskNotValidated);
        }
        else if (!AmigaDiskFileSystemCore.BitmapBlock(ref p, ref cursor, index, w.Data, out block))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        var pending = MountIndexEnabled && w.LayoutKnown && (w.GroupFileWrites || w.GroupMetadata) ? p.ReadUInt32(w.Index, AmigaFfsMountIndex.PendingBitmap) : 0;
        if (MountIndexEnabled && pending != 0 && pending != block && !FlushBitmap(ref p, ref w)) return false;
        var pages = MountIndexEnabled && w.LayoutKnown ? p.ReadUInt32(w.Index, AmigaFfsMountIndex.BitmapPages) : 0;
        w.ActiveBitmap = w.Bitmap;
        if (MountIndexEnabled && pages != 0) w.ActiveBitmap = APTR.FromPointer(pages + index * p.BlockBytes);
        else if (MountIndexEnabled && pending != 0 && pending == block)
        {
            var image = APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.PendingBitmapImage));
            if (w.Bitmap != image) p.Copy(image, w.Bitmap, p.BlockBytes);
        }
        else
        {
            if (!p.ReadBlock(block, w.Bitmap) || !AmigaDiskFileSystemCore.Checksum(ref p, w.Bitmap))
                return Fail(ref w, DOS.Error.DiskNotValidated);
        }
        var first = reservedBlocks + index * bitmapBits;
        var count = blockCount - first;
        w.BitmapBlock = block; w.BitmapFirst = first;
        w.BitmapCount = count > bitmapBits ? bitmapBits : count;
        return true;
    }
    // Bitmap page `index` from the summary: the root's pointers directly, a
    // later page with one read of its extension block (into Data) instead of
    // walking the chain from the root.
    private static bool SummaryBitmapBlock<T>(ref T p, ref Work w, uint index, out uint block)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var g = AmigaFfsGeometry.Read(ref p);
        var map = AmigaFfsMountIndex.SummaryMap(w.Index);
        const uint roots = AmigaFfsMountIndex.SummaryRootPointers;
        if (index < roots) block = p.ReadUInt32(map, (int)index * 4);
        else
        {
            block = 0;
            var slots = g.BlockBytes / 4 - 1;
            var extension = AmigaGeometryMath.Divide(index - roots, slots);
            if (extension >= p.ReadUInt32(map, (int)roots * 4)) return false;
            if (!p.ReadBlock(p.ReadUInt32(map, (int)(roots + 1 + extension) * 4), w.Data)) return false;
            block = p.ReadUInt32(w.Data, (int)(index - roots - extension * slots) * 4);
        }
        return block >= g.ReservedBlocks && block < g.BlockCount && block != g.RootBlock;
    }
    private static bool IsFree<T>(ref T p, ref Work w, uint key) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!LoadBitmap(ref p, ref w, key)) return false;
        var bit = key - w.BitmapFirst;
        return (p.ReadUInt32(w.ActiveBitmap, 4 + (int)(bit / 32) * 4) & (1u << (int)(bit % 32))) != 0;
    }
    private static void Bit<T>(ref T p, ref Work w, uint key, bool free) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!LoadBitmap(ref p, ref w, key)) return;
        var bit = key - w.BitmapFirst;
        var at = 4 + (int)(bit / 32) * 4; var mask = 1u << (int)(bit % 32);
        var value = p.ReadUInt32(w.ActiveBitmap, at);
        var updated = free ? value | mask : value & ~mask;
        w.BitmapChecksumDelta = unchecked(w.BitmapChecksumDelta + value - updated);
        p.WriteUInt32(w.ActiveBitmap, at, updated); w.BitmapDirty = 1;
        if (free != ((value & mask) != 0)) w.IndexedUsed = free ? w.IndexedUsed - 1 : w.IndexedUsed + 1;
        if (!free && key == w.NextKey) w.NextKey++;
        if (free && (value & mask) == 0) NoteFreed(ref p, ref w, key, 1);
    }

    private static void NoteFreed<T>(ref T p, ref Work w, uint first, uint count)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (first < w.NextKey) w.NextKey = first;
        if (!w.CheckedTracking) { w.TrackFrees = p.TracksFreedRanges; w.CheckedTracking = true; }
        if (!w.TrackFrees) return;
        if (w.FreedCount != 0 && first == w.FreedFirst + w.FreedCount) w.FreedCount += count;
        else if (w.FreedCount != 0 && w.FreedFirst - first == count)
        {
            // Unlink releases a file header after its data. Adjacent keys
            // need only one handoff to the mount's discard queue.
            w.FreedFirst = first;
            w.FreedCount += count;
        }
        else
        {
            if (w.FreedCount != 0) p.ObserveFreedRange(w.FreedFirst, w.FreedCount);
            w.FreedFirst = first; w.FreedCount = count;
        }
    }

    private static bool FreeRun<T>(ref T p, ref Work w, uint first, uint count) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var releasedFirst = first;
        while (count != 0)
        {
            if (!LoadBitmap(ref p, ref w, first)) return false;
            var bit = first - w.BitmapFirst;
            var at = 4 + (int)(bit >> 5) * 4;
            if ((bit & 31) == 0 && count >= 32)
            {
                var available = w.BitmapCount - bit;
                var words = (available < count ? available : count) >> 5;
                var released = 0u;
                while (released < words && p.ReadUInt32(w.ActiveBitmap, at + (int)released * 4) == 0)
                {
                    p.WriteUInt32(w.ActiveBitmap, at + (int)released * 4, uint.MaxValue);
                    released++;
                }
                if (released != 0)
                {
                    // Each zero word changed by -1 modulo 2^32.
                    w.BitmapChecksumDelta = unchecked(w.BitmapChecksumDelta + released);
                    var blocks = released << 5;
                    w.BitmapDirty = 1; w.IndexedUsed -= blocks;
                    first += blocks; count -= blocks;
                    continue;
                }
            }
            var take = 32u - (bit & 31);
            if (take > count) take = count;
            if (take > w.BitmapCount - bit) take = w.BitmapCount - bit;
            var mask = (uint.MaxValue >> (int)(32 - take)) << (int)(bit & 31);
            var old = p.ReadUInt32(w.ActiveBitmap, at);
            if ((old & mask) == 0)
            {
                p.WriteUInt32(w.ActiveBitmap, at, old | mask);
                w.BitmapDirty = 1; w.BitmapChecksumDelta = unchecked(w.BitmapChecksumDelta - mask);
                w.IndexedUsed -= take;
            }
            else
            {
                // An already-free bit interrupts this run of transitions.
                if (first != releasedFirst) NoteFreed(ref p, ref w, releasedFirst, first - releasedFirst);
                if ((old & mask) != mask)
                    for (var n = 0u; n < take; n++) Bit(ref p, ref w, first + n, true);
                releasedFirst = first + take;
            }
            count -= take; first += take;
        }
        if (first != releasedFirst) NoteFreed(ref p, ref w, releasedFirst, first - releasedFirst);
        return w.Error == DOS.Error.None;
    }
    private static uint Allocate<T>(ref T p, ref Work w, in AmigaFfsGeometry geometry) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (w.Error != DOS.Error.None) return 0;
        var key = w.NextKey;
        var cursor = new AmigaDiskFileSystemCore.BitmapCursor();
        while (key < geometry.BlockCount)
        {
            // Stay in the loaded page while scanning its words. Loading another
            // page validates it and flushes the previous page if it was dirty.
            if ((w.BitmapBlock == 0 || key - w.BitmapFirst >= w.BitmapCount) &&
                !LoadBitmap(ref p, ref w, key, ref cursor)) return 0;
            var bit = key - w.BitmapFirst;
            var at = 4 + (int)(bit / 32) * 4;
            var shift = (int)(bit % 32);
            var word = p.ReadUInt32(w.ActiveBitmap, at);
            var free = word & (uint.MaxValue << shift);
            if (free != 0)
            {
                // Find the first free bit in this word without probing each
                // occupied block (or dividing its block number) individually.
                var mask = 1u << shift;
                if ((word & mask) == 0)
                {
                    shift = (int)AmigaBitmapWordCore.FirstSet(free);
                    mask = 1u << shift;
                }
                var advance = (uint)shift - bit % 32;
                if (advance >= geometry.BlockCount - key) break;
                key += advance;
                if (key != geometry.RootBlock)
                {
                    p.WriteUInt32(w.ActiveBitmap, at, word & ~mask);
                    w.BitmapChecksumDelta = unchecked(w.BitmapChecksumDelta + mask);
                    w.BitmapDirty = 1; w.NextKey = key + 1; w.IndexedUsed++;
                    return key;
                }
                key++;
            }
            else
            {
                var advance = 32 - bit % 32;
                if (advance >= geometry.BlockCount - key) break;
                key += advance;
            }
        }
        Fail(ref w, DOS.Error.DiskFull); return 0;
    }
    // A run is only a request-local hint, not an on-disk reservation. It ends
    // before the next pointer table, where metadata may consume free space.
    // Keep NextKey independent: its first-free scan must still find earlier
    // holes after data has been placed near a file's tail.
    private static uint AllocateData<T>(ref T p, ref Work w, in AmigaFfsGeometry g,
        uint preferred, uint wanted, ref uint runFirst, ref uint runCount)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (w.Error != DOS.Error.None) return 0;
        if (runCount == 0 && preferred >= g.ReservedBlocks && preferred < g.BlockCount)
        {
            if (!LoadBitmap(ref p, ref w, preferred)) return 0;
            var available = w.BitmapCount - (preferred - w.BitmapFirst);
            if (wanted > available) wanted = available;
            // An adjacent prefix is useful even when it is shorter than the
            // write or crosses a track. This changes placement, not device I/O.
            runCount = FreePrefix(ref p, ref w, preferred, wanted, g.RootBlock);
            runFirst = preferred;
            if (runCount == 0)
            {
                // Only inspect the rest of this track (or a table-sized window
                // for unknown geometry), in the bitmap already loaded above.
                // Never load another bitmap merely to find a longer run.
                var trackBlocks = p.TrackBlocks;
                var window = trackBlocks > 1
                    ? trackBlocks - AmigaGeometryMath.Remainder(preferred, trackBlocks)
                    : g.TableEntries;
                if (window > available) window = available;
                for (var offset = 1u; offset < window;)
                {
                    var bit = preferred + offset - w.BitmapFirst;
                    var shift = (int)(bit & 31);
                    var free = p.ReadUInt32(w.ActiveBitmap, 4 + (int)(bit >> 5) * 4) >> shift;
                    if (free == 0)
                    {
                        offset += 32u - (uint)shift;
                        continue;
                    }
                    offset += AmigaBitmapWordCore.FirstSet(free);
                    if (offset >= window) break;
                    var remaining = window - offset;
                    var key = preferred + offset;
                    var count = FreePrefix(ref p, ref w, key, remaining < wanted ? remaining : wanted, g.RootBlock);
                    if (count > runCount) { runFirst = key; runCount = count; }
                    if (count == wanted) break;
                    offset += count + 1;
                }
            }
        }
        if (runCount == 0) return Allocate(ref p, ref w, in g);
        var result = runFirst;
        // Single-owner execution and the table boundary keep the hint live.
        // Recheck the consumed bit, including after a bitmap cache replacement.
        if (!IsFree(ref p, ref w, result))
        {
            if (w.Error == DOS.Error.None) Fail(ref w, DOS.Error.DiskNotValidated);
            return 0;
        }
        Bit(ref p, ref w, result, false);
        if (w.Error != DOS.Error.None) return 0;
        runFirst++; runCount--;
        return result;
    }

    // Read only the current verified bitmap page; a caller can claim this
    // prefix without loading another page or touching the root's bit.
    private static uint FreePrefix<T>(ref T p, ref Work w, uint first, uint wanted, uint root)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var bit = first - w.BitmapFirst;
        if (bit >= w.BitmapCount) return 0;
        if (wanted > w.BitmapCount - bit) wanted = w.BitmapCount - bit;
        if (root - first < wanted) wanted = root - first;
        var count = 0u;
        while (count < wanted)
        {
            var shift = (int)(bit & 31);
            var occupied = ~p.ReadUInt32(w.ActiveBitmap, 4 + (int)(bit >> 5) * 4) >> shift;
            var take = 32u - (uint)shift;
            if (take > wanted - count) take = wanted - count;
            if ((occupied & (uint.MaxValue >> (int)(32 - take))) != 0)
                return count + (uint)AmigaBitmapWordCore.FirstSet(occupied);
            count += take;
            bit += take;
        }
        return count;
    }
    private static bool ClaimRun<T>(ref T p, ref Work w, uint first, uint count) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (first - w.BitmapFirst >= w.BitmapCount || count > w.BitmapCount - (first - w.BitmapFirst))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        while (count != 0)
        {
            var bit = first - w.BitmapFirst;
            var shift = (int)(bit & 31);
            var take = 32u - (uint)shift;
            if (take > count) take = count;
            var mask = (uint.MaxValue >> (int)(32 - take)) << shift;
            var offset = 4 + (int)(bit >> 5) * 4;
            var word = p.ReadUInt32(w.ActiveBitmap, offset);
            if ((word & mask) != mask) return Fail(ref w, DOS.Error.DiskNotValidated);
            p.WriteUInt32(w.ActiveBitmap, offset, word & ~mask);
            w.BitmapChecksumDelta = unchecked(w.BitmapChecksumDelta + mask);
            if (first == w.NextKey) w.NextKey += take;
            w.BitmapDirty = 1; w.IndexedUsed += take; first += take; count -= take;
        }
        return true;
    }

    private static bool SearchFree<T>(ref T p, ref Work w, uint key, uint root)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var bit = key - w.BitmapFirst;
        return key != root && (p.ReadUInt32(w.ActiveBitmap, 4 + (int)(bit / 32) * 4) & (1u << (int)(bit % 32))) != 0;
    }
    private static bool LoadDosType<T>(ref T p, ref Work w) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!p.ReadBlock(0, w.Meta)) return Fail(ref w, DOS.Error.DiskNotValidated);
        w.DosType = p.ReadUInt32(w.Meta);
        return w.DosType is >= 0x444F5300 and <= 0x444F5305 || Fail(ref w, DOS.Error.ActionNotKnown);
    }
    private static uint Hash<T>(ref T p, APTR name, uint length, uint dosType) where T : struct, IAmigaFileSystemPlatform
    {
        var tableEntries = p.BlockBytes / 4 - 56;
        if (tableEntries == 0) return 0;
        return AmigaDiskFileSystemCore.NameBucket(ref p, name, length, dosType, tableEntries);
    }

    private static int StoredNameSlot<T>(ref T p, APTR header, in AmigaFfsGeometry geometry, uint dosType)
        where T : struct, IAmigaFileSystemPlatform
    {
        var length = p.ReadUInt8(header, geometry.Tail + 432);
        if (length > 30) return 0;
        return 24 + (int)Hash(ref p, APTR.FromPointer(header.Raw + geometry.BlockBytes - 79), length, dosType) * 4;
    }
    // Resolve the parent and retain a slice of the final component.
    public static bool Parent<T>(ref T p, ref Work w, ref Request r) where T : struct, IAmigaWritableFileSystemPlatform
        => ResolveParent(ref p, ref w, ref r, out _, true);

    private static bool ResolveParent<T>(ref T p, ref Work w, ref Request r, out APTR parent, bool copyParent = false)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        parent = APTR.Null;
        if (MountIndexEnabled && w.Index.IsNotNull &&
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.Status) is AmigaFfsMountIndex.Clean or AmigaFfsMountIndex.MetadataOpen)
            w.DosType = p.ReadUInt32(w.Index, AmigaFfsMountIndex.Type);
        else if (!LoadDosType(ref p, ref w)) return false;
        var geometry = AmigaFfsGeometry.Read(ref p);
        if (r.Length == 0 || r.Length > 255) return Fail(ref w, DOS.Error.InvalidComponentName);
        uint start = 0;
        for (var i = 0u; i < r.Length; i++) if (p.ReadUInt8(r.Name, (int)i) == ':' || p.ReadUInt8(r.Name, (int)i) == '/') start = i + 1;
        if (start != 0)
        {
            var parentLength = start;
            if (p.ReadUInt8(r.Name, (int)start - 1) == '/') parentLength--;
            r.Directory = AmigaDiskFileSystemCore.ResolveView(ref p, r.Directory, r.Name, parentLength, w.DosType, w.Meta,
                out var error, out parent, MountIndexEnabled && w.ReadsGroupedRoot ? w.Index : APTR.Null);
            if (r.Directory == 0) return Fail(ref w, error);
        }
        r.Name = APTR.FromPointer(r.Name.Raw + start); r.Length -= start;
        if (r.Length == 0 || r.Length > 30) return Fail(ref w, DOS.Error.InvalidComponentName);
        if (MountIndexEnabled && parent.IsNull && w.ReadsGroupedRoot && r.Directory == geometry.RootBlock)
            parent = AmigaFfsMountIndex.NamespaceRoot(ref p, w.Index);
        if (MountIndexEnabled && parent.IsNull && w.ReadsGroupedDirectories)
            parent = AmigaFfsMountIndex.NamespaceDirectory(ref p, w.Index, r.Directory);
        if (parent.IsNull) parent = InspectHeader(ref p, ref w, in geometry, r.Directory);
        if (parent.IsNull) return false;
        if (copyParent && parent != w.Meta) p.Copy(parent, w.Meta, geometry.BlockBytes);
        // Only the root (1) and real directories (2) hold entries; links do not.
        return unchecked((int)p.ReadUInt32(parent, geometry.Tail + 508)) is 1 or 2 || Fail(ref w, DOS.Error.ObjectWrongType);
    }

    private static bool FindDestination<T>(ref T p, ref Work w, ref Request r, out uint found, out APTR matched, out int slot)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        found = 0; matched = APTR.Null; slot = 0;
        if (!ResolveParent(ref p, ref w, ref r, out var parent)) return false;
        slot = 24 + (int)Hash(ref p, r.Name, r.Length, w.DosType) * 4;
        // This packet holds the checked directory. An empty bucket proves the
        // destination is available without reading and hashing its parent again.
        if (p.ReadUInt32(parent, slot) == 0) return true;
        // The destination name itself, even when it is a link.
        found = AmigaDiskFileSystemCore.ResolveForOpen(ref p, r.Directory, r.Name, r.Length,
            w.DosType, w.Meta, out var error, out matched, out _, MountIndexEnabled && w.ReadsGroupedRoot ? w.Index : APTR.Null,
            followFinal: false);
        return found != 0 || error == DOS.Error.ObjectNotFound || Fail(ref w, error);
    }
    private static bool Unlink<T>(ref T p, ref Work w, in AmigaFfsGeometry geometry,
        uint key, uint directory, int at, uint successor, APTR retainedDirectory = default)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var owner = directory;
        uint mark = 0, power = 1, run = 0; // Brent's cycle check over the chain
        for (var guard = 0u; guard < geometry.BlockCount; guard++)
        {
            if (guard != 0 && owner == mark) return Fail(ref w, DOS.Error.DiskNotValidated);
            if (guard != 0 && ++run == power) { mark = owner; power <<= 1; run = 0; }
            var image = APTR.Null;
            if (guard == 0) image = retainedDirectory;
            if (image.IsNull) image = InspectHeader(ref p, ref w, in geometry, owner);
            if (image.IsNull) return false;
            var current = p.ReadUInt32(image, at);
            if (current == 0) break;
            if (current == key)
            {
                w.UnlinkedOwner = owner;
                if (MountIndexEnabled && owner == w.RootKey && w.RootImage.IsNotNull && w.DosType < 0x444F5304)
                {
                    var sum = p.ReadUInt32(w.RootImage, 20);
                    p.WriteUInt32(w.RootImage, at, successor);
                    p.WriteUInt32(w.RootImage, 20, unchecked(sum + current - successor));
                    StampDate(ref p, ref w, w.RootImage, geometry.Tail + 420);
                    w.RootLinkChanged = true;
                    return true;
                }
                var target = EditDirectoryLinks(ref p, ref w, owner, image, owner == directory);
                if (target.IsNull) return false;
                p.WriteUInt32(target, at, successor);
                p.WriteUInt32(target, 20, unchecked(p.ReadUInt32(target, 20) + current - successor));
                // The directory itself changed; a sibling's link edit leaves its date to the caller.
                if (owner == directory) StampDate(ref p, ref w, target, geometry.Tail + 420);
                return target != w.Meta || Put(ref p, ref w, owner, target, SealedHeader);
            }
            owner = current; at = geometry.Tail + 496;
        }
        return Fail(ref w, DOS.Error.DiskNotValidated);
    }
    public static bool Create<T>(ref T p, ref Work w, ref Request r, bool directory) where T : struct, IAmigaWritableFileSystemPlatform =>
        Create(ref p, ref w, ref r, directory, false);

    public static bool Create<T>(ref T p, ref Work w, ref Request r, bool directory, bool openForWriting)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!FindDestination(ref p, ref w, ref r, out var existing, out _, out var slot)) return false;
        if (existing != 0) return Fail(ref w, DOS.Error.ObjectExists);
        return CreateMissingEntry(ref p, ref w, ref r, slot, directory, openForWriting);
    }

    // ACTION_MAKE_LINK. A hard link (type -4 to a file, 4 to a directory)
    // records its object in real_entry and joins the head of the object's
    // next_link chain, the layout Kickstart 3.1 FFS writes. A soft link
    // (type 3) stores its target text where a directory keeps its hash table.
    public static bool MakeLink<T>(ref T p, ref Work w, ref Request r, uint target, APTR softPath, uint softLength, bool soft)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var geometry = AmigaFfsGeometry.Read(ref p);
        if (!FindDestination(ref p, ref w, ref r, out var existing, out _, out var slot)) return false;
        if (existing != 0) return Fail(ref w, DOS.Error.ObjectExists);
        var linkType = 3; uint chain = 0;
        if (soft)
        {
            if (softLength == 0 || softLength >= geometry.BlockBytes - 224 || !p.IsMapped(softPath, softLength))
                return Fail(ref w, DOS.Error.BadNumber);
        }
        else
        {
            if (target == geometry.RootBlock) return Fail(ref w, DOS.Error.ObjectWrongType);
            var image = InspectHeader(ref p, ref w, in geometry, target);
            if (image.IsNull) return false;
            var type = unchecked((int)p.ReadUInt32(image, geometry.Tail + 508));
            if (type is not (2 or -3)) return Fail(ref w, DOS.Error.ObjectWrongType);
            linkType = type == 2 ? 4 : -4;
            chain = p.ReadUInt32(image, geometry.Tail + 472);
        }
        var link = new LinkRequest { Type = linkType, Target = target, Chain = chain, Path = softPath, PathLength = softLength };
        return CreateMissingEntry(ref p, ref w, ref r, slot, false, false, link);
    }

    internal struct LinkRequest
    {
        internal int Type;
        internal uint Target, Chain, PathLength;
        internal APTR Path;
    }

    // Same owner and same packet as the completed lookup. The parent and
    // final component are resolved and the requested name is absent.
    internal static bool CreateMissingEntry<T>(ref T p, ref Work w, ref Request r, int slot, bool directory, bool openForWriting = false)
        where T : struct, IAmigaWritableFileSystemPlatform =>
        CreateMissingEntry(ref p, ref w, ref r, slot, directory, openForWriting, default);

    private static bool CreateMissingEntry<T>(ref T p, ref Work w, ref Request r, int slot, bool directory, bool openForWriting,
        in LinkRequest link) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var geometry = AmigaFfsGeometry.Read(ref p);
        if (!Prepare(ref p, ref w, in geometry, 1, keepDirectory: MountIndexEnabled && w.ReadsGroupedDirectories ? r.Directory : 0)) return false;
        // The header, a new directory's own cache block and any growth of the
        // parent's cache must all fit before Begin invalidates the root.
        if (w.DosType >= 0x444F5304 && !CacheBudgetFits(ref p, ref w, r.Directory, (uint)(slot - 24) / 4,
            CacheRecordBytes(r.Length, 0), 0, 0, directory ? 2u : 1u)) return false;
        KeepFile(ref p, ref w);
        // The last closed file remains checked while its new sibling grows.
        // Park it before the empty file replaces the active table identity.
        var parked = MountIndexEnabled && openForWriting && !directory && w.RestoreFile != 0 && w.DosType < 0x444F5304 &&
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.Status) == AmigaFfsMountIndex.Clean
            ? AmigaFfsMountIndex.ParkFile(ref p, w.Index) : 0;
        // Begin expires clean readers. The creating packet can still adopt
        // this private parent: invalidating the root and claiming a free bit
        // cannot change the parent's directory links.
        var publishedParent = MountIndexEnabled && w.LayoutKnown && w.ReadsGroupedDirectories && r.Directory != geometry.RootBlock &&
            r.Directory != 0 && p.ReadUInt32(w.Index, AmigaFfsMountIndex.PublishedParent) == r.Directory;
        if (!Begin(ref p, ref w, in geometry)) return false;
        var key = Allocate(ref p, ref w, in geometry); if (key == 0) return false;
        w.CacheParent = r.Directory; if (directory) w.CacheNew = key;
        w.CacheKey = key; w.CacheFresh = true;
        var parent = MountIndexEnabled ? w.RootImage : APTR.Null;
        if (!MountIndexEnabled || r.Directory != geometry.RootBlock || parent.IsNull)
        {
            parent = MountIndexEnabled && publishedParent ? APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.ParentImage)) :
                InspectHeader(ref p, ref w, in geometry, r.Directory);
            if (parent.IsNull) return false;
        }
        var successor = p.ReadUInt32(parent, slot);
        var groupDirectory = MountIndexEnabled && w.ReadsGroupedDirectories && w.GroupMetadata && w.LayoutKnown && w.ReadsGroupedRoot &&
            w.DosType < 0x444F5304 && p.ReadUInt32(w.Index, AmigaFfsMountIndex.ParentImage) != 0;
        // An open-for-write packet can carry its new file straight into the
        // first write. Keep the directory link private until the new header
        // and allocation have both reached the device.
        if (MountIndexEnabled && openForWriting && !directory && w.RootImage.IsNotNull &&
            w.DosType < 0x444F5304 && p.ReadUInt32(w.Index, AmigaFfsMountIndex.HeaderImage) != 0 &&
            (r.Directory == w.RootKey || p.ReadUInt32(w.Index, AmigaFfsMountIndex.ParentImage) != 0) &&
            AmigaFfsMountIndex.FitsFile(ref p, w.Index, 0, geometry.TableEntries))
        {
            StartEmptyFileGroup(ref p, ref w, key);
        }
        if (MountIndexEnabled && r.Directory != w.RootKey && (w.GroupHeader != 0 || groupDirectory))
        {
            var image = APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.ParentImage));
            if (parent != image) p.Copy(parent, image, geometry.BlockBytes);
            parent = image;
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.PendingParent, r.Directory);
        }
        var header = MountIndexEnabled && w.GroupHeader == key ? APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.HeaderImage)) : w.Meta;
        // When the root link itself is private, a newly created directory can
        // receive its first children before either sector is published.
        var newDirectory = groupDirectory && directory && r.Directory == w.RootKey;
        if (MountIndexEnabled && newDirectory) header = APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.ParentImage));
        p.Clear(header, geometry.BlockBytes); p.WriteUInt32(header, 0, 2); p.WriteUInt32(header, 4, key);
        p.WriteUInt32(header, geometry.Tail + 496, successor); p.WriteUInt32(header, geometry.Tail + 500, r.Directory);
        // Kickstart 3.1 FFS leaves offset 12 zero in file and directory headers;
        // only the root stores its hash table size there.
        p.WriteUInt32(header, 12, 0);
        p.WriteUInt32(header, geometry.Tail + 508, directory ? 2u : 0xFFFFFFFD);
        p.WriteUInt8(header, geometry.Tail + 432, (byte)r.Length);
        for (var i = 0u; i < r.Length; i++) p.WriteUInt8(header, geometry.Tail + 433 + (int)i, p.ReadUInt8(r.Name, (int)i));
        if (link.Type != 0)
        {
            p.WriteUInt32(header, 12, 0);
            p.WriteUInt32(header, geometry.Tail + 508, unchecked((uint)link.Type));
            if (link.Type == 3)
                for (var i = 0u; i < link.PathLength; i++) p.WriteUInt8(header, 24 + (int)i, p.ReadUInt8(link.Path, (int)i));
            else
            {
                p.WriteUInt32(header, geometry.Tail + 468, link.Target);
                p.WriteUInt32(header, geometry.Tail + 472, link.Chain);
            }
        }
        StampDate(ref p, ref w, header, geometry.Tail + 420);
        if (MountIndexEnabled && w.GroupHeader == key)
        {
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.PendingHeader, key);
            if ((w.DosType & 1) != 0)
            {
                p.WriteUInt32(w.Index, AmigaFfsMountIndex.FreshHeaderImage, header.Raw);
                p.WriteUInt32(w.Index, AmigaFfsMountIndex.FreshHeaderKey, key);
                p.WriteUInt32(w.Index, AmigaFfsMountIndex.FreshHeaderPointerSum, 0);
            }
        }
        else if (MountIndexEnabled && newDirectory)
        {
            Seal(ref p, header, 20);
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.PendingParent, key);
        }
        else if (!Put(ref p, ref w, key, header, 20)) return false;
        if (link.Type is 4 or -4)
        {
            // The new link heads the object's next_link chain.
            if (!Header(ref p, ref w, link.Target)) return false;
            p.WriteUInt32(w.Meta, geometry.Tail + 472, key);
            if (!Put(ref p, ref w, link.Target, w.Meta, 20)) return false;
        }
        if (MountIndexEnabled && (w.GroupHeader != 0 || groupDirectory ||
            (r.Directory == geometry.RootBlock && w.RootImage.IsNotNull && w.DosType < 0x444F5304)))
        {
            // Finish writes this exact root after the bitmap. The previous root
            // on disk remains invalid until that write publishes the directory link.
            var sum = p.ReadUInt32(parent, 20);
            p.WriteUInt32(parent, slot, key);
            p.WriteUInt32(parent, 20, unchecked(sum + successor - key));
            StampDate(ref p, ref w, parent, geometry.Tail + 420);
            w.RootLinkChanged = parent == w.RootImage;
        }
        else
        {
            if (!Header(ref p, ref w, r.Directory)) return false;
            p.WriteUInt32(w.Meta, slot, key);
            p.WriteUInt32(w.Meta, 20, unchecked(p.ReadUInt32(w.Meta, 20) + successor - key));
            StampDate(ref p, ref w, w.Meta, geometry.Tail + 420);
            if (!Put(ref p, ref w, r.Directory, w.Meta, SealedHeader)) return false;
        }
        if (!Finish(ref p, ref w, in geometry)) return false;
        if (MountIndexEnabled && w.LayoutKnown && w.ReadsGroupedRoot && r.Directory == geometry.RootBlock && successor == 0)
            AmigaFfsMountIndex.RecordRootChild(ref p, w.Index, slot, key, r.Length);
        if (MountIndexEnabled && parked != 0 && w.GroupHeader == key)
        {
            AmigaFfsMountIndex.SwapDataKeys(ref p, w.Index);
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.Companion, key);
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.ParkedHeader, parked);
        }
        r.Header = key; return true;
    }

    private static void StartEmptyFileGroup<T>(ref T p, ref Work w, uint key) where T : struct, IAmigaWritableFileSystemPlatform
    {
        w.GroupFileWrites = true;
        w.GroupHeader = key;
        w.RestoreFile = key;
        w.RestoreSize = 0;
        w.WritingIndexedFile = true;
        p.WriteUInt32(AmigaFfsMountIndex.TableKeys(ref p, w.Index), 0, key);
        p.WriteUInt32(w.Index, AmigaFfsMountIndex.FileBlocks, 0);
        p.WriteUInt32(w.Index, AmigaFfsMountIndex.Tables, 1);
    }
    private static bool Allocated<T>(ref T p, ref Work w, in AmigaFfsGeometry geometry, uint key) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (key < geometry.ReservedBlocks || key >= geometry.BlockCount || key == geometry.RootBlock ||
            IsFree(ref p, ref w, key) || key == w.BitmapBlock || w.Error != DOS.Error.None ||
            (MountIndexEnabled && w.LayoutKnown && AmigaFfsMountIndex.Intersects(ref p, w.Index, key, 1)))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        return true;
    }
    // Index construction checks allocation and permanent ownership once per
    // physical run. A run can span bitmap words and verified bitmap pages.
    private static bool AllocatedRun<T>(ref T p, ref Work w, in AmigaFfsGeometry g, uint first, uint count)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (first < g.ReservedBlocks || first >= g.BlockCount || count > g.BlockCount - first ||
            (MountIndexEnabled && AmigaFfsMountIndex.Intersects(ref p, w.Index, first, count)))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        while (count != 0)
        {
            if (!LoadBitmap(ref p, ref w, first)) return false;
            var bit = first - w.BitmapFirst;
            var shift = bit & 31;
            var take = 32 - shift;
            if (take > count) take = count;
            var mask = (uint.MaxValue >> (int)(32 - take)) << (int)shift;
            if ((p.ReadUInt32(w.ActiveBitmap, 4 + (int)(bit >> 5) * 4) & mask) != 0)
                return Fail(ref w, DOS.Error.DiskNotValidated);
            first += take; count -= take;
        }
        return true;
    }
    // An overwrite checks only the table path and data pointers in [first, limit).
    // uint.MaxValue retains the entire-chain validation used for growth/release.
    private static bool Chain<T>(ref T p, ref Work w, uint header, bool release,
        uint first = 0, uint limit = uint.MaxValue) where T : struct, IAmigaWritableFileSystemPlatform
        => Chain(ref p, ref w, header, release, out _, first, limit);
    private static bool Chain<T>(ref T p, ref Work w, uint header, bool release,
        out uint lastData, uint first = 0, uint limit = uint.MaxValue) where T : struct, IAmigaWritableFileSystemPlatform
    {
        lastData = 0;
        if ((w.DosType & 1) == 0) return OfsChain(ref p, ref w, header, release, out lastData);
        var geometry = AmigaFfsGeometry.Read(ref p);
        var table = header;
        uint logical = 0, blocks = 0, size = 0;
        var keys = APTR.Null; var tables = APTR.Null;
        for (var guard = 0u; table != 0 && guard < geometry.BlockCount; guard++)
        {
            if (!Allocated(ref p, ref w, in geometry, table) || !p.ReadBlock(table, w.Meta) || !AmigaDiskFileSystemCore.Checksum(ref p, w.Meta) ||
                p.ReadUInt32(w.Meta, 4) != table || p.ReadUInt32(w.Meta, 8) > geometry.TableEntries ||
                p.ReadUInt32(w.Meta) != (table == header ? 2u : 16u) || p.ReadUInt32(w.Meta, geometry.Tail + 508) != 0xFFFFFFFD ||
                (table != header && p.ReadUInt32(w.Meta, geometry.Tail + 500) != header)) return Fail(ref w, DOS.Error.DiskNotValidated);
            var count = p.ReadUInt32(w.Meta, 8);
            if (table == header)
            {
                size = p.ReadUInt32(w.Meta, geometry.Tail + 324);
                blocks = size == 0 ? 0 : AmigaGeometryMath.Divide(size - 1, geometry.BlockBytes) + 1;
            }
            if (MountIndexEnabled && guard == 0 && w.LayoutKnown && !release && limit == uint.MaxValue &&
                AmigaFfsMountIndex.FitsFile(ref p, w.Index, blocks, geometry.TableEntries))
            {
                p.WriteUInt32(w.Index, AmigaFfsMountIndex.Header, 0);
                keys = AmigaFfsMountIndex.DataKeys(ref p, w.Index);
                tables = AmigaFfsMountIndex.TableKeys(ref p, w.Index);
            }
            // A full walk proves the whole pointer layout byte_size implies.
            // Callers then index any slot below the old block count directly,
            // so a short table or missing extension must not pass as success.
            var whole = limit == uint.MaxValue;
            if (whole || (MountIndexEnabled && keys.IsNotNull))
            {
                var remaining = blocks - logical;
                if (logical > blocks || count != (remaining < geometry.TableEntries ? remaining : geometry.TableEntries))
                    return Fail(ref w, DOS.Error.DiskNotValidated);
            }
            if (MountIndexEnabled && keys.IsNotNull) p.WriteUInt32(tables, (int)guard * 4, table);
            if (limit != uint.MaxValue && count < geometry.TableEntries && count < limit)
                return Fail(ref w, DOS.Error.DiskNotValidated);
            if (MountIndexEnabled && keys.IsNotNull)
            {
                uint start = 0, length = 0;
                for (var i = 0u; i < count; i++)
                {
                    var key = p.ReadUInt32(w.Meta, geometry.Tail + 308 - (int)i * 4);
                    if (length == 0 || key != start + length)
                    {
                        if (length != 0 && !AllocatedRun(ref p, ref w, in geometry, start, length)) return false;
                        start = key; length = 0;
                    }
                    p.WriteUInt32(keys, (int)(logical + i) * 4, key);
                    length++;
                }
                if (length != 0)
                {
                    if (!AllocatedRun(ref p, ref w, in geometry, start, length)) return false;
                    lastData = start + length - 1;
                }
            }
            else
            {
                for (var i = first; i < count && i < limit; i++)
                {
                    var key = p.ReadUInt32(w.Meta, geometry.Tail + 308 - (int)i * 4);
                    if (!Allocated(ref p, ref w, in geometry, key)) return false;
                    if (blocks != 0 && logical + i == blocks - 1) lastData = key;
                    if (release) Bit(ref p, ref w, key, true);
                }
            }
            var next = p.ReadUInt32(w.Meta, geometry.Tail + 504);
            if (table != header && release) Bit(ref p, ref w, table, true);
            if (limit <= geometry.TableEntries) return w.Error == DOS.Error.None;
            if (next == 0) return whole && w.Error == DOS.Error.None && logical + count == blocks &&
                (!MountIndexEnabled || keys.IsNull || AmigaFfsMountIndex.SealFile(ref p, w.Index, header, size, blocks, guard + 1)) ||
                Fail(ref w, DOS.Error.DiskNotValidated);
            if (logical + count >= blocks) return Fail(ref w, DOS.Error.DiskNotValidated);
            first = first > geometry.TableEntries ? first - geometry.TableEntries : 0;
            if (limit != uint.MaxValue) limit -= geometry.TableEntries;
            logical += geometry.TableEntries;
            table = next;
        }
        return Fail(ref w, DOS.Error.DiskNotValidated);
    }
    public static bool Remove<T>(ref T p, ref Work w, uint key, bool truncate) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var geometry = AmigaFfsGeometry.Read(ref p);
        if (key == geometry.RootBlock) return Fail(ref w, DOS.Error.ObjectInUse);
        if (!Prepare(ref p, ref w, in geometry, 0, attributeHeader: truncate ? key : 0)) return false;
        // The private header survives bitmap release and the invalid-root
        // write. It can carry either staged attributes or the last close's
        // sealed header. A borrowed physical cache view can expire sooner.
        var retained = APTR.Null;
        if (MountIndexEnabled && truncate && key != 0 && w.LayoutKnown &&
            ((w.GroupMetadata && p.ReadUInt32(w.Index, AmigaFfsMountIndex.PendingHeader) == key) ||
             p.ReadUInt32(w.Index, AmigaFfsMountIndex.PublishedHeader) == key))
            retained = APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.HeaderImage));
        var header = retained.IsNotNull ? retained : InspectHeader(ref p, ref w, in geometry, key);
        if (header.IsNull) return false;
        var type = unchecked((int)p.ReadUInt32(header, geometry.Tail + 508));
        w.CacheParent = p.ReadUInt32(header, geometry.Tail + 500);
        if (truncate) w.CacheKey = key; else w.CacheDrop = key;
        if ((p.ReadUInt32(header, geometry.Tail + 320) & (truncate ? 4u : 1u)) != 0) return Fail(ref w, truncate ? DOS.Error.WriteProtected : DOS.Error.DeleteProtected);
        var saved = new AmigaFfsMountIndex.SavedFile();
        if (MountIndexEnabled && type < 0 && w.LayoutKnown && w.DosType < 0x444F5304)
        {
            var size = p.ReadUInt32(header, geometry.Tail + 324);
            var payload = (w.DosType & 1) == 0 ? geometry.BlockBytes - 24 : geometry.BlockBytes;
            var blocks = size == 0 ? 0 : AmigaGeometryMath.Divide(size - 1, payload) + 1;
            saved = AmigaFfsMountIndex.BorrowRemovalSpace(ref p, w.Index, key, size, blocks, geometry.TableEntries);
        }
        w.UnlinkedOwner = 0;
        var ok = RemoveEntry(ref p, ref w, key, truncate, type, header, in geometry, in saved, retained);
#if !COPPERFS_NO_FFS_MOUNT_INDEX
        AmigaFfsMountIndex.RestoreRemovalSpace(ref p, w.Index, in saved, ok);
#endif
        if (MountIndexEnabled && ok && !truncate && saved.Key != 0)
        {
            // The checked disjoint file kept its header and tail sector in
            // private RAM. Unlink can change a hash predecessor in that file.
            if (saved.SealedHeader != w.UnlinkedOwner)
                p.WriteUInt32(w.Index, AmigaFfsMountIndex.PublishedHeader, saved.SealedHeader);
            if (saved.SealedTable != w.UnlinkedOwner)
                p.WriteUInt32(w.Index, AmigaFfsMountIndex.PublishedTable, saved.SealedTable);
        }
        return ok;
    }

    private static bool RemoveEntry<T>(ref T p, ref Work w, uint key, bool truncate, int type, APTR header,
        in AmigaFfsGeometry geometry, in AmigaFfsMountIndex.SavedFile saved, APTR retained)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var slot = 0;
        uint successor = 0;
        if (!truncate)
        {
            slot = StoredNameSlot(ref p, header, in geometry, w.DosType);
            if (slot == 0) return Fail(ref w, DOS.Error.DiskNotValidated);
            if (MountIndexEnabled && w.LayoutKnown && w.CacheParent == geometry.RootBlock)
                AmigaFfsMountIndex.RecordRootChild(ref p, w.Index, slot, 0, 0);
            successor = p.ReadUInt32(header, geometry.Tail + 496);
        }
        // A link header owns no data, and a soft link stores its path where a
        // directory keeps its hash table.
        if (type is 3 or 4 or -4)
            return truncate ? Fail(ref w, DOS.Error.ObjectWrongType) :
                RemoveLinked(ref p, ref w, key, type, header, in geometry, slot, successor);
        var linked = truncate ? 0 : p.ReadUInt32(header, geometry.Tail + 472);
        if (type > 0)
        {
            if (truncate) return Fail(ref w, DOS.Error.ObjectWrongType);
            for (var at = 24; at < geometry.Tail + 312; at += 4) if (p.ReadUInt32(header, at) != 0) return Fail(ref w, DOS.Error.DirectoryNotEmpty);
        }
        else if (!(MountIndexEnabled && w.LayoutKnown &&
            AmigaFfsMountIndex.Matches(ref p, w.Index, key, p.ReadUInt32(header, geometry.Tail + 324))) &&
            !Chain(ref p, ref w, key, false)) return false;
        // An object with hard links survives under its first link's name.
        if (linked != 0) return PromoteFirstLink(ref p, ref w, key, type, linked, in geometry, slot, successor);
        if (MountIndexEnabled && !AmigaFfsMountIndex.RemovalIsDisjoint(ref p, w.Index, in saved))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if (MountIndexEnabled && type > 0 && w.LayoutKnown) p.WriteUInt32(w.Index, AmigaFfsMountIndex.PrefixDirectory, 0);
        var indexedRelease = MountIndexEnabled && type < 0 && w.LayoutKnown && p.ReadUInt32(w.Index, AmigaFfsMountIndex.Header) == key;
        if (MountIndexEnabled && truncate && w.GroupFileWrites && indexedRelease && w.DosType < 0x444F5304 &&
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.FileBytes) == 0)
        {
            if (retained.IsNull && !Header(ref p, ref w, key)) return false;
            var empty = w.Meta;
            if (retained.IsNotNull) empty = retained;
            var occupied = p.ReadUInt32(empty, 8) | p.ReadUInt32(empty, 16) |
                p.ReadUInt32(empty, geometry.Tail + 324) | p.ReadUInt32(empty, geometry.Tail + 504);
            for (var at = 24; occupied == 0 && at < geometry.Tail + 312; at += 4)
                occupied = p.ReadUInt32(empty, at);
            if (occupied == 0) return true;
        }
        // Begin expires physical-run identities. Capture a complete mapping
        // while the preflight still owns it, then apply it to the bitmap.
        var contiguous = MountIndexEnabled && indexedRelease && p.ReadUInt32(w.Index, AmigaFfsMountIndex.RunHeader) == key &&
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.RunLogical) == 0 &&
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.RunLength) == p.ReadUInt32(w.Index, AmigaFfsMountIndex.FileBlocks)
            ? p.ReadUInt32(w.Index, AmigaFfsMountIndex.RunPhysical) : 0;
        uint bodyBlocks = 0;
        var body = MountIndexEnabled && indexedRelease ? AmigaFfsMountIndex.ContiguousBody(ref p, w.Index, key, out bodyBlocks) : 0;
        // Keep the checked parent through the invalid-root write. Releasing
        // allocation bits cannot change its directory links.
        var retainedDirectory = APTR.Null;
        if (MountIndexEnabled && !truncate && w.LayoutKnown && w.ReadsGroupedDirectories && w.CacheParent != 0 &&
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.PublishedParent) == w.CacheParent)
            retainedDirectory = APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.ParentImage));
        if (!Begin(ref p, ref w, in geometry)) return false;
        if (type > 0 && w.DosType >= 0x444F5304 && !ReleaseCache(ref p, ref w, key)) return false;
        if (MountIndexEnabled && body != 0)
        {
            if (!FreeRun(ref p, ref w, body, bodyBlocks)) return false;
        }
        else if (MountIndexEnabled && indexedRelease)
        {
            var keys = AmigaFfsMountIndex.DataKeys(ref p, w.Index);
            var tables = AmigaFfsMountIndex.TableKeys(ref p, w.Index);
            var blocks = p.ReadUInt32(w.Index, AmigaFfsMountIndex.FileBlocks);
            if (contiguous != 0 && !FreeRun(ref p, ref w, contiguous, blocks)) return false;
            // The verified file index spans pointer tables. Free consecutive
            // payloads together before the scattered extension blocks: mixing
            // their physical addresses breaks runs and exhausts discard hints.
            for (uint index = contiguous == 0 ? 0 : blocks; index < blocks;)
            {
                var first = p.ReadUInt32(keys, (int)index * 4);
                var run = 1u;
                while (run < blocks - index && p.ReadUInt32(keys, (int)(index + run) * 4) == first + run) run++;
                if (!FreeRun(ref p, ref w, first, run)) return false;
                index += run;
            }
            var tableCount = p.ReadUInt32(w.Index, AmigaFfsMountIndex.Tables);
            for (uint table = 1; table < tableCount; table++)
            {
                Bit(ref p, ref w, p.ReadUInt32(tables, (int)table * 4), true);
                if (w.Error != DOS.Error.None) return false;
            }
        }
        else if (type < 0 && !Chain(ref p, ref w, key, true)) return false;
        if (truncate)
        {
            if (retained.IsNull && !Header(ref p, ref w, key)) return false;
            // The old chain has been checked and released. Let the first write
            // replace this empty header before publishing the retained bitmap.
            if (MountIndexEnabled && w.GroupFileWrites && indexedRelease && saved.Key == 0 && w.RootImage.IsNotNull &&
                w.DosType < 0x444F5304 && p.ReadUInt32(w.Index, AmigaFfsMountIndex.HeaderImage) != 0)
                StartEmptyFileGroup(ref p, ref w, key);
            var image = w.Meta;
            if (retained.IsNotNull)
            {
                // The group owns the retained header through publication,
                // so truncation can edit it in place.
                if (w.GroupHeader == key) image = retained;
                else p.Copy(retained, image, geometry.BlockBytes);
                p.WriteUInt32(w.Index, AmigaFfsMountIndex.PendingHeader, 0);
            }
            p.Clear(APTR.FromPointer(image.Raw + 24), geometry.TableEntries * 4);
            p.WriteUInt32(image, 8, 0);
            p.WriteUInt32(image, 16, 0);
            p.WriteUInt32(image, geometry.Tail + 324, 0);
            p.WriteUInt32(image, geometry.Tail + 504, 0);
            if (!Put(ref p, ref w, key, image, 20)) return false;
        }
        else
        {
            if (!Unlink(ref p, ref w, in geometry, key, w.CacheParent, slot, successor, retainedDirectory)) return false;
            if (w.UnlinkedOwner != w.CacheParent && !StampDirectory(ref p, ref w, in geometry, w.CacheParent)) return false;
            Bit(ref p, ref w, key, true);
        }
        return Finish(ref p, ref w, in geometry);
    }
    // Delete one link header. A hard link is also spliced out of its
    // object's next_link chain so no header keeps pointing at a free block.
    private static bool RemoveLinked<T>(ref T p, ref Work w, uint key, int type, APTR header,
        in AmigaFfsGeometry geometry, int slot, uint successor) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var target = p.ReadUInt32(header, geometry.Tail + 468);
        var following = p.ReadUInt32(header, geometry.Tail + 472);
        if (MountIndexEnabled && type == 4 && w.LayoutKnown) p.WriteUInt32(w.Index, AmigaFfsMountIndex.PrefixDirectory, 0);
        if (!Begin(ref p, ref w, in geometry)) return false;
        if (type != 3)
        {
            var owner = target;
            for (var guard = 0u; owner != 0 && guard < geometry.BlockCount; guard++)
            {
                var image = InspectHeader(ref p, ref w, in geometry, owner);
                if (image.IsNull) return false;
                var current = p.ReadUInt32(image, geometry.Tail + 472);
                if (current == key)
                {
                    if (image != w.Meta) p.Copy(image, w.Meta, geometry.BlockBytes);
                    p.WriteUInt32(w.Meta, geometry.Tail + 472, following);
                    if (!Put(ref p, ref w, owner, w.Meta, 20)) return false;
                    break;
                }
                owner = current;
            }
        }
        if (!Unlink(ref p, ref w, in geometry, key, w.CacheParent, slot, successor)) return false;
        if (w.UnlinkedOwner != w.CacheParent && !StampDirectory(ref p, ref w, in geometry, w.CacheParent)) return false;
        Bit(ref p, ref w, key, true);
        return w.Error == DOS.Error.None && Finish(ref p, ref w, in geometry);
    }

    // Kickstart FFS keeps the object's own header (and so its data and
    // extension blocks) and moves it into its first link's directory slot
    // under the link's name, then frees the link header.
    private static bool PromoteFirstLink<T>(ref T p, ref Work w, uint key, int type, uint link,
        in AmigaFfsGeometry geometry, int slot, uint successor) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var image = InspectHeader(ref p, ref w, in geometry, link);
        if (image.IsNull) return false;
        if (unchecked((int)p.ReadUInt32(image, geometry.Tail + 508)) != (type > 0 ? 4 : -4) ||
            p.ReadUInt32(image, geometry.Tail + 468) != key) return Fail(ref w, DOS.Error.DiskNotValidated);
        var linkSlot = StoredNameSlot(ref p, image, in geometry, w.DosType);
        if (linkSlot == 0) return Fail(ref w, DOS.Error.DiskNotValidated);
        var linkParent = p.ReadUInt32(image, geometry.Tail + 500);
        // Data stays untouched until the final bitmap release below.
        p.Copy(image, w.Data, geometry.BlockBytes);
        // The object keeps its key under the link's name and parent; the
        // link header itself goes.
        w.CacheOther = linkParent; w.CacheKey = key; w.CacheDrop = link;
        if (MountIndexEnabled && w.LayoutKnown)
        {
            if (type > 0) p.WriteUInt32(w.Index, AmigaFfsMountIndex.PrefixDirectory, 0);
            if (linkParent == geometry.RootBlock) AmigaFfsMountIndex.RecordRootChild(ref p, w.Index, linkSlot, 0, 0);
        }
        if (!Begin(ref p, ref w, in geometry)) return false;
        if (!Unlink(ref p, ref w, in geometry, key, w.CacheParent, slot, successor)) return false;
        if (w.UnlinkedOwner != w.CacheParent && !StampDirectory(ref p, ref w, in geometry, w.CacheParent)) return false;
        // Unlinking the object can change the link's own hash successor.
        image = InspectHeader(ref p, ref w, in geometry, link);
        if (image.IsNull) return false;
        var linkSuccessor = p.ReadUInt32(image, geometry.Tail + 496);
        if (!Unlink(ref p, ref w, in geometry, link, linkParent, linkSlot, key)) return false;
        if (w.UnlinkedOwner != linkParent && !StampDirectory(ref p, ref w, in geometry, linkParent)) return false;
        if (!Header(ref p, ref w, key)) return false;
        p.Copy(APTR.FromPointer(w.Data.Raw + (uint)(geometry.Tail + 432)), APTR.FromPointer(w.Meta.Raw + (uint)(geometry.Tail + 432)), 32);
        p.WriteUInt32(w.Meta, geometry.Tail + 472, p.ReadUInt32(w.Data, geometry.Tail + 472));
        p.WriteUInt32(w.Meta, geometry.Tail + 496, linkSuccessor);
        p.WriteUInt32(w.Meta, geometry.Tail + 500, linkParent);
        if (!Put(ref p, ref w, key, w.Meta, 20)) return false;
        Bit(ref p, ref w, link, true);
        return w.Error == DOS.Error.None && Finish(ref p, ref w, in geometry);
    }

    public static bool Rename<T>(ref T p, ref Work w, ref Request r) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var geometry = AmigaFfsGeometry.Read(ref p);
        if (r.Header == geometry.RootBlock) return Fail(ref w, DOS.Error.ObjectInUse);
        if (!FindDestination(ref p, ref w, ref r, out var found, out var matched, out var slot)) return false;
        if (found == r.Header)
        {
            var equal = p.ReadUInt8(matched, geometry.Tail + 432) == r.Length;
            for (var i = 0u; equal && i < r.Length; i++)
                equal = p.ReadUInt8(matched, geometry.Tail + 433 + (int)i) == p.ReadUInt8(r.Name, (int)i);
            if (equal) return true;
        }
        if (found != 0 && found != r.Header) return Fail(ref w, DOS.Error.ObjectExists);
        if (!Prepare(ref p, ref w, in geometry, 0)) return false;
        var source = InspectHeader(ref p, ref w, in geometry, r.Header);
        if (source.IsNull) return false;
        var movesDirectory = p.ReadUInt32(source, geometry.Tail + 508) == 2;
        w.CacheParent = p.ReadUInt32(source, geometry.Tail + 500); w.CacheOther = r.Directory; w.CacheKey = r.Header;
        if ((p.ReadUInt32(source, geometry.Tail + 320) & 1) != 0) return Fail(ref w, DOS.Error.DeleteProtected);
        var sourceSlot = StoredNameSlot(ref p, source, in geometry, w.DosType);
        if (sourceSlot == 0) return Fail(ref w, DOS.Error.DiskNotValidated);
        if (MountIndexEnabled && w.LayoutKnown && w.CacheParent == geometry.RootBlock)
            AmigaFfsMountIndex.RecordRootChild(ref p, w.Index, sourceSlot, 0, 0);
        var oldSuccessor = p.ReadUInt32(source, geometry.Tail + 496);
        var oldSlot = MountIndexEnabled && w.LayoutKnown && w.CacheParent == r.Directory && r.Directory != geometry.RootBlock &&
            w.DosType < 0x444F5304 ? sourceSlot : 0;
        uint successor = 0;
        var privateSource = MountIndexEnabled && w.LayoutKnown && p.ReadUInt32(w.Index, AmigaFfsMountIndex.PublishedHeader) == r.Header &&
            source.Raw == p.ReadUInt32(w.Index, AmigaFfsMountIndex.HeaderImage);
        var ancestor = r.Directory;
        for (var guard = 0u; ancestor != geometry.RootBlock && guard < geometry.BlockCount; guard++)
        {
            if (ancestor == r.Header) return Fail(ref w, DOS.Error.ObjectInUse);
            var ancestorImage = InspectHeader(ref p, ref w, in geometry, ancestor);
            if (ancestorImage.IsNull) return false;
            if (MountIndexEnabled && ancestor == r.Directory && oldSlot != 0)
            {
                if (p.ReadUInt32(ancestorImage, oldSlot) != r.Header) oldSlot = 0;
                else successor = slot == oldSlot ? oldSuccessor : p.ReadUInt32(ancestorImage, slot);
            }
            ancestor = p.ReadUInt32(ancestorImage, geometry.Tail + 500);
            if (guard + 1 == geometry.BlockCount) return Fail(ref w, DOS.Error.DiskNotValidated);
        }
        // The destination cache gains the renamed record. A same-directory
        // rename also drops its old record; a move frees space in the source
        // only after the destination is checked, so count none of it.
        if (w.DosType >= 0x444F5304)
        {
            if (!Header(ref p, ref w, r.Header)) return false;
            var bytes = CacheRecordBytes(r.Length, p.ReadUInt8(w.Meta, geometry.Tail + 328));
            if (!CacheBudgetFits(ref p, ref w, r.Directory, (uint)(slot - 24) / 4, bytes,
                w.CacheParent == r.Directory ? r.Header : 0, 0, 0)) return false;
        }
        KeepFile(ref p, ref w);
        // A same-directory rename can keep editing the checked parent after
        // Begin withdraws its read proof. Neither the invalid-root write nor
        // the renamed child's write can change this directory sector.
        var retainedDirectory = APTR.Null;
        if (MountIndexEnabled && oldSlot != 0 && w.ReadsGroupedDirectories &&
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.PublishedParent) == r.Directory)
            retainedDirectory = APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.ParentImage));
        if (MountIndexEnabled && w.LayoutKnown && movesDirectory) p.WriteUInt32(w.Index, AmigaFfsMountIndex.PrefixDirectory, 0);
        w.UnlinkedOwner = 0;
        if (!Begin(ref p, ref w, in geometry)) return false;
        // The destination directory also owns the source bucket head. Patch
        // both links in its final sector write after the renamed header succeeds.
        // A non-head source still needs its predecessor checked and rewritten.
        if (!MountIndexEnabled || oldSlot == 0)
        {
            if (!Unlink(ref p, ref w, in geometry, r.Header, w.CacheParent, sourceSlot, oldSuccessor)) return false;
            var parent = InspectHeader(ref p, ref w, in geometry, r.Directory);
            if (parent.IsNull) return false;
            successor = p.ReadUInt32(parent, slot);
        }
        var renamed = w.Meta;
        if (MountIndexEnabled && oldSlot != 0 && privateSource) renamed = source;
        else if (!Header(ref p, ref w, r.Header)) return false;
        // Only the 32-byte stored name and the parent/hash links change.
        // Include any old padding bytes when replacing an original's name.
        var name = APTR.FromPointer(renamed.Raw + (uint)(geometry.Tail + 432));
        var checksum = unchecked(p.ReadUInt32(renamed, 20) + oldSuccessor - successor + w.CacheParent - r.Directory);
        for (var at = 0; at < 32; at += 4) checksum = unchecked(checksum + p.ReadUInt32(name, at));
        p.Clear(name, 32);
        p.WriteUInt8(name, 0, (byte)r.Length);
        for (var i = 0u; i < r.Length; i++) p.WriteUInt8(name, 1 + (int)i, p.ReadUInt8(r.Name, (int)i));
        for (var at = 0; at < 32; at += 4) checksum = unchecked(checksum - p.ReadUInt32(name, at));
        p.WriteUInt32(renamed, geometry.Tail + 496, successor);
        p.WriteUInt32(renamed, geometry.Tail + 500, r.Directory);
        p.WriteUInt32(renamed, 20, checksum);
        if (!Put(ref p, ref w, r.Header, renamed, SealedHeader)) return false;
        if (MountIndexEnabled && r.Directory == w.RootKey && w.RootImage.IsNotNull && w.DosType < 0x444F5304)
        {
            var sum = p.ReadUInt32(w.RootImage, 20);
            p.WriteUInt32(w.RootImage, slot, r.Header);
            p.WriteUInt32(w.RootImage, 20, unchecked(sum + successor - r.Header));
            StampDate(ref p, ref w, w.RootImage, geometry.Tail + 420);
            w.RootLinkChanged = true;
        }
        else if (slot != oldSlot)
        {
            var current = retainedDirectory;
            if (current.IsNull) current = InspectHeader(ref p, ref w, in geometry, r.Directory);
            if (current.IsNull) return false;
            var target = EditDirectoryLinks(ref p, ref w, r.Directory, current, true);
            if (target.IsNull) return false;
            var sum = p.ReadUInt32(target, 20);
            if (MountIndexEnabled && oldSlot != 0)
            {
                sum = unchecked(sum + p.ReadUInt32(target, oldSlot) - oldSuccessor);
                p.WriteUInt32(target, oldSlot, oldSuccessor);
            }
            sum = unchecked(sum + p.ReadUInt32(target, slot) - r.Header);
            p.WriteUInt32(target, slot, r.Header);
            p.WriteUInt32(target, 20, sum);
            StampDate(ref p, ref w, target, geometry.Tail + 420);
            if (target == w.Meta && !Put(ref p, ref w, r.Directory, target, SealedHeader)) return false;
        }
        else if (!StampDirectory(ref p, ref w, in geometry, r.Directory)) return false;
        // Kickstart dates both directories of a move, not the renamed object.
        if (w.CacheParent != r.Directory && w.UnlinkedOwner != w.CacheParent &&
            !StampDirectory(ref p, ref w, in geometry, w.CacheParent)) return false;
        return Finish(ref p, ref w, in geometry);
    }

    // The borrowed header can reside in a replaceable physical cache slot.
    // Preserve it before publishing a different parent can recycle that slot.
    private static APTR EditDirectoryLinks<T>(ref T p, ref Work w, uint key, APTR current, bool directory)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var image = MountIndexEnabled && directory && w.ReadsGroupedDirectories && w.GroupMetadata && w.LayoutKnown &&
            key != w.RootKey && w.DosType < 0x444F5304
            ? APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.ParentImage)) : APTR.Null;
        if (!MountIndexEnabled || image.IsNull)
        {
            if (current != w.Meta) p.Copy(current, w.Meta, p.BlockBytes);
            return w.Meta;
        }
        var previous = p.ReadUInt32(w.Index, AmigaFfsMountIndex.PendingParent);
        if (previous != 0 && previous != key)
        {
            if (current != w.Meta) p.Copy(current, w.Meta, p.BlockBytes);
            if (!PublishParent(ref p, ref w)) return APTR.Null;
            p.Copy(w.Meta, image, p.BlockBytes);
        }
        else if (current != image) p.Copy(current, image, p.BlockBytes);
        p.WriteUInt32(w.Index, AmigaFfsMountIndex.PendingParent, key);
        return image;
    }
    // ACTION_RENAME_DISK: the root's 32-byte name field. Like the other root
    // changes, it is published with the valid root at Finish.
    public static bool Relabel<T>(ref T p, ref Work w, APTR name, uint length) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var geometry = AmigaFfsGeometry.Read(ref p);
        if (length == 0 || length > 30 || !p.IsMapped(name, length)) return Fail(ref w, DOS.Error.InvalidComponentName);
        for (var i = 0u; i < length; i++)
            if (p.ReadUInt8(name, (int)i) is (byte)':' or (byte)'/') return Fail(ref w, DOS.Error.InvalidComponentName);
        if (!Prepare(ref p, ref w, in geometry, 0) || !Begin(ref p, ref w, in geometry)) return false;
        var image = MountIndexEnabled && w.RootImage.IsNotNull ? w.RootImage : APTR.Null;
        if (image.IsNull)
        {
            if (!Header(ref p, ref w, geometry.RootBlock)) return false;
            image = w.Meta;
        }
        var field = APTR.FromPointer(image.Raw + (uint)(geometry.Tail + 432));
        var checksum = p.ReadUInt32(image, 20);
        for (var at = 0; at < 32; at += 4) checksum = unchecked(checksum + p.ReadUInt32(field, at));
        p.Clear(field, 32);
        p.WriteUInt8(field, 0, (byte)length);
        for (var i = 0u; i < length; i++) p.WriteUInt8(field, 1 + (int)i, p.ReadUInt8(name, (int)i));
        for (var at = 0; at < 32; at += 4) checksum = unchecked(checksum - p.ReadUInt32(field, at));
        p.WriteUInt32(image, 20, checksum);
        if (image == w.Meta) { if (!Put(ref p, ref w, geometry.RootBlock, image, SealedHeader)) return false; }
        else w.RootLinkChanged = true;
        return Finish(ref p, ref w, in geometry);
    }

    public static bool SetMetadata<T>(ref T p, ref Work w, ref Request r,
        DosPacketAction action) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var geometry = AmigaFfsGeometry.Read(ref p);
        if (action != DosPacketAction.SetProtection && action != DosPacketAction.SetComment && action != DosPacketAction.SetDate &&
            action != DosPacketAction.SetOwner)
            return Fail(ref w, DOS.Error.ActionNotKnown);
        // The volume root has a different layout: these file-header fields
        // hold bitmap pointers there, rather than protection/comment data.
        if (r.Header == geometry.RootBlock) return Fail(ref w, DOS.Error.ActionNotKnown);
        if (action == DosPacketAction.SetComment && r.Length > 79)
            return Fail(ref w, DOS.Error.CommentTooBig);
        if (MountIndexEnabled && w.GroupMetadata && r.Header != 0 && w.Index.IsNotNull &&
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.Status) == AmigaFfsMountIndex.MetadataOpen &&
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.SealedAttributeHeader) == r.Header &&
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.PendingHeader) == r.Header)
        {
            if (p.WriteProtected()) return Fail(ref w, DOS.Error.DiskWriteProtected);
            // The existing group already owns the invalid root and bitmap.
            var pending = APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.HeaderImage));
            EditAttribute(ref p, pending, geometry.Tail, ref r, action);
            w.Error = DOS.Error.None;
            return true;
        }
        if (!Prepare(ref p, ref w, in geometry, 0, attributeHeader: r.Header)) return false;
        // A longer comment can grow the parent's DOS4/5 cache chain.
        if (action == DosPacketAction.SetComment && w.DosType >= 0x444F5304)
        {
            if (!Header(ref p, ref w, r.Header)) return false;
            var parent = p.ReadUInt32(w.Meta, geometry.Tail + 500);
            var bytes = CacheRecordBytes(p.ReadUInt8(w.Meta, geometry.Tail + 432), r.Length);
            if (!CacheBudgetFits(ref p, ref w, parent, uint.MaxValue, 0, r.Header, bytes, 0)) return false;
        }
        var image = w.Meta;
        var staged = MountIndexEnabled && w.GroupMetadata && w.LayoutKnown && w.RootImage.IsNotNull && w.DosType < 0x444F5304 &&
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.HeaderImage) != 0;
        if (MountIndexEnabled && staged)
        {
            image = APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.HeaderImage));
            if (p.ReadUInt32(w.Index, AmigaFfsMountIndex.PendingHeader) != r.Header)
            {
                var current = InspectHeader(ref p, ref w, in geometry, r.Header);
                if (current.IsNull) return false;
                p.Copy(current, image, geometry.BlockBytes);
            }
            else Seal(ref p, image, 20);
        }
        else if (!Header(ref p, ref w, r.Header)) return false;
        KeepFile(ref p, ref w);
        if (!Begin(ref p, ref w, in geometry) ||
            ((!MountIndexEnabled || w.RootImage.IsNull) && !Header(ref p, ref w, r.Header))) return false;
        w.CacheParent = p.ReadUInt32(image, geometry.Tail + 500); w.CacheKey = r.Header;
        EditAttribute(ref p, image, geometry.Tail, ref r, action);
        if (MountIndexEnabled && staged)
        {
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.PendingHeader, r.Header);
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.SealedAttributeHeader, r.Header);
        }
        else if (!Put(ref p, ref w, r.Header, image, SealedHeader)) return false;
        return Finish(ref p, ref w, in geometry);
    }

    private static void EditAttribute<T>(ref T p, APTR image, int tail, ref Request r, DosPacketAction action)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        // Keep the verified image available to later namespace lookups until
        // the group publishes. Only the attribute words and their sum change.
        var checksum = p.ReadUInt32(image, 20);
        if (action is DosPacketAction.SetProtection or DosPacketAction.SetOwner)
        {
            // Protection bits, or the owner longword (UID << 16 | GID).
            var at = tail + (action == DosPacketAction.SetOwner ? 316 : 320);
            checksum = unchecked(checksum + p.ReadUInt32(image, at) - r.Position);
            p.WriteUInt32(image, at, r.Position);
        }
        else if (action == DosPacketAction.SetDate)
            for (var offset = 0; offset < 12; offset += 4)
            {
                var at = tail + 420 + offset;
                var value = p.ReadUInt32(r.Buffer, offset);
                checksum = unchecked(checksum + p.ReadUInt32(image, at) - value);
                p.WriteUInt32(image, at, value);
            }
        else
        {
            var comment = APTR.FromPointer(image.Raw + (uint)(tail + 328));
            for (var offset = 0; offset < 80; offset += 4)
            {
                checksum = unchecked(checksum + p.ReadUInt32(comment, offset));
                p.WriteUInt32(comment, offset, 0);
            }
            p.WriteUInt8(comment, 0, (byte)r.Length);
            for (var index = 0u; index < r.Length; index++)
                p.WriteUInt8(comment, 1 + (int)index, p.ReadUInt8(r.Buffer, (int)index));
            // The length byte and payload occupy this rounded prefix. The
            // padding bytes are already clear and add nothing to the sum.
            var prefix = ((int)r.Length + 4) & ~3;
            for (var offset = 0; offset < prefix; offset += 4)
                checksum = unchecked(checksum - p.ReadUInt32(comment, offset));
        }
        p.WriteUInt32(image, 20, checksum);
    }

    // A directory reader can observe a completed attribute change without
    // making the root valid between consecutive namespace packets.
    internal static bool PublishMetadataAttributes<T>(ref T p, APTR index, uint keep)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var key = p.ReadUInt32(index, AmigaFfsMountIndex.PendingHeader);
        if (key == 0 || key == keep) return true;
        var image = APTR.FromPointer(p.ReadUInt32(index, AmigaFfsMountIndex.HeaderImage));
        p.WriteUInt32(index, AmigaFfsMountIndex.Status, AmigaFfsMountIndex.Updating);
        if (p.ReadUInt32(index, AmigaFfsMountIndex.SealedAttributeHeader) != key) Seal(ref p, image, 20);
        if (!p.WriteSealedClassicBlock(key, image))
        {
            p.WriteUInt32(index, AmigaFfsMountIndex.Status, AmigaFfsMountIndex.GroupFailed);
            return false;
        }
        p.WriteUInt32(index, AmigaFfsMountIndex.PendingHeader, 0);
        p.WriteUInt32(index, AmigaFfsMountIndex.SealedAttributeHeader, 0);
        p.WriteUInt32(index, AmigaFfsMountIndex.Status, AmigaFfsMountIndex.MetadataOpen);
        return true;
    }

    public static bool Write<T>(ref T p, ref Work w, ref Request r, out uint actual) where T : struct, IAmigaWritableFileSystemPlatform
        => Write(ref p, ref w, ref r, out actual, false);

    // Opt in only when the mount has positively identified a fixed hard disk.
    public static bool Write<T>(ref T p, ref Work w, ref Request r, out uint actual, bool fixedHardDisk) where T : struct, IAmigaWritableFileSystemPlatform
    {
        actual = 0;
        if (MountIndexEnabled && HasFileWriteGroup(ref p, w.Index))
        {
            if (w.BufferSmallAppends && w.GroupFileWrites && fixedHardDisk &&
                TryBufferAppend(ref p, w.Index, ref r, out w.Error))
            {
                actual = w.Error == DOS.Error.None ? r.Length : 0;
                return w.Error == DOS.Error.None;
            }
            uint joined = 0;
            var pending = p.ReadUInt32(w.Index, AmigaFfsMountIndex.PendingAppend);
            if (pending != 0)
            {
                if (w.BufferSmallAppends && w.GroupFileWrites && fixedHardDisk &&
                    !CompleteBufferedSector(ref p, ref w, ref r, pending, out joined)) return false;
                if (!DrainBufferedAppend(ref p, ref w)) return false;
            }
            if (joined != 0)
            {
                var rest = r;
                rest.Position += joined;
                rest.Buffer = APTR.FromPointer(r.Buffer.Raw + joined);
                rest.Length -= joined;
                var ok = WriteThrough(ref p, ref w, ref rest, out var written, fixedHardDisk);
                actual = joined + written;
                if (!ok) p.WriteUInt32(w.Index, AmigaFfsMountIndex.Status, AmigaFfsMountIndex.GroupFailed);
                return ok;
            }
        }
        return WriteThrough(ref p, ref w, ref r, out actual, fixedHardDisk);
    }

    private static bool WriteThrough<T>(ref T p, ref Work w, ref Request r, out uint actual, bool fixedHardDisk)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var geometry = AmigaFfsGeometry.Read(ref p);
        actual = 0;
        var blockBytes = geometry.BlockBytes; var tableEntries = geometry.TableEntries;
        if (blockBytes == 0 || tableEntries == 0) return Fail(ref w, DOS.Error.DiskNotValidated);
        if (r.Length > int.MaxValue || !p.IsMapped(r.Buffer, r.Length)) return Fail(ref w, DOS.Error.BadNumber);
        if (MountIndexEnabled && HasFileWriteGroup(ref p, w.Index))
        {
            var sizeInGroup = p.ReadUInt32(w.Index, AmigaFfsMountIndex.FileBytes);
            var sameFile = w.GroupFileWrites && fixedHardDisk && r.Header == p.ReadUInt32(w.Index, AmigaFfsMountIndex.Header);
            if (sameFile && r.Position < sizeInGroup &&
                TryWriteGroupRun(ref p, w.Index, ref r, blockBytes, sizeInGroup, w.DeviceChecksDataProtection, out w.Error))
            {
                if (w.Error != DOS.Error.None) return false;
                actual = r.Length;
                return true;
            }
            if (sameFile && FitsGroupSlice(ref p, w.Index, ref r, sizeInGroup, blockBytes))
                return OverwriteGroupSlice(ref p, w.Index, w.Data, ref r, w.DeviceChecksDataProtection, out actual, out w.Error);
            if (sameFile && r.Length <= uint.MaxValue - r.Position && r.Position + r.Length > sizeInGroup)
            {
                var type = p.ReadUInt32(w.Index, AmigaFfsMountIndex.Type);
                var payload = (type & 1) != 0 ? blockBytes : blockBytes - 24;
                var lastByte = r.Position + r.Length - 1;
                var blocks = (payload == 512 ? lastByte >> 9 : AmigaGeometryMath.Divide(lastByte, payload)) + 1;
                sameFile = AmigaFfsMountIndex.FitsFile(ref p, w.Index, blocks, tableEntries);
                if (sameFile && (type & 1) != 0 && type < 0x444F5304 && r.Position == sizeInGroup &&
                    p.ReadUInt32(w.Index, AmigaFfsMountIndex.HeaderImage) != 0)
                {
                    // The owning writer already verified the header, pointer
                    // tables and allocation of this exact prefix. Only the
                    // additional blocks and tables need admission.
                    var existing = p.ReadUInt32(w.Index, AmigaFfsMountIndex.FileBlocks);
                    var oldTables = p.ReadUInt32(w.Index, AmigaFfsMountIndex.Tables);
                    var newTail = AppendTailTable(blocks, tableEntries);
                    var tablesNeeded = newTail + 1;
                    if (!Prepare(ref p, ref w, in geometry, blocks - existing + tablesNeeded - oldTables)) return false;
                    w.RestoreFile = r.Header;
                    w.GroupHeader = r.Header;
                    w.RestoreSize = r.Position + r.Length;
                    w.WritingIndexedFile = true;
                    var keys = AmigaFfsMountIndex.DataKeys(ref p, w.Index);
                    var tables = AmigaFfsMountIndex.TableKeys(ref p, w.Index);
                    var last = existing == 0 ? 0 : p.ReadUInt32(keys, (int)(existing - 1) * 4);
                    return AppendFfsBlocks(ref p, ref w, ref r, in geometry, keys, tables, existing, blocks,
                        existing == 0 ? 0 : oldTables - 1, newTail, last, out actual);
                }
            }
            if (!sameFile && !FinishFileWriteGroup(ref p, ref w)) return false;
        }
#if !COPPERFS_NO_FFS_MOUNT_INDEX
        w.GroupFileWrites &= fixedHardDisk;
#endif
        // A write does not edit directory links. Its group can carry the
        // preceding namespace update through the same publication boundary.
        var keepDirectory = MountIndexEnabled && w.GroupMetadata && w.Index.IsNotNull
            ? p.ReadUInt32(w.Index, AmigaFfsMountIndex.PendingParent) : 0;
        if (!Prepare(ref p, ref w, in geometry, 0, false, keepDirectory: keepDirectory)) return false;
        if ((w.DosType & 1) == 0) return WriteOfs(ref p, ref w, ref r, in geometry, out actual, fixedHardDisk);
        // The first writer's sealed header survives a sibling close in its
        // own mount page. Only the unchanged, parked mapping can use it.
        var parked = MountIndexEnabled && w.LayoutKnown && p.ReadUInt32(w.Index, AmigaFfsMountIndex.Status) == AmigaFfsMountIndex.Clean &&
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.ParkedHeader) == r.Header && r.Header != 0;
        var header = MountIndexEnabled && parked && p.ReadUInt32(w.Index, AmigaFfsMountIndex.PublishedPreviousHeader) == r.Header
            ? APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.PreviousHeaderImage))
            : InspectHeader(ref p, ref w, in geometry, r.Header);
        if (header.IsNull) return false;
        if (p.ReadUInt32(header, geometry.Tail + 508) != 0xFFFFFFFD) return Fail(ref w, DOS.Error.ObjectWrongType);
        if ((p.ReadUInt32(header, geometry.Tail + 320) & 4) != 0) return Fail(ref w, DOS.Error.WriteProtected);
        var size = p.ReadUInt32(header, geometry.Tail + 324);
        if (r.Position > size) return Fail(ref w, DOS.Error.SeekError);
        if (r.Length > uint.MaxValue - r.Position) return Fail(ref w, DOS.Error.DiskFull);
        if (r.Length == 0) return true;
        var end = r.Position + r.Length; var newSize = end > size ? end : size;
        var oldBlocks = size == 0 ? 0 : AmigaGeometryMath.Divide(size - 1, blockBytes) + 1;
        if (oldBlocks == 0 && (p.ReadUInt32(header, 8) != 0 || p.ReadUInt32(header, geometry.Tail + 504) != 0))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        var first = 0u; var limit = uint.MaxValue;
        if (MountIndexEnabled && end > size && !PrepareGrowthIndex(ref p, ref w, in geometry)) return false;
        var resumed = MountIndexEnabled && parked && AmigaFfsMountIndex.ResumeFile(ref p, w.Index, r.Header, size);
        if (end <= size)
        {
            first = AmigaGeometryMath.Divide(r.Position, blockBytes);
            limit = AmigaGeometryMath.Divide(end - 1, blockBytes) + 1;
        }
        uint lastData = 0;
        if (MountIndexEnabled && w.LayoutKnown && AmigaFfsMountIndex.Matches(ref p, w.Index, r.Header, size))
        {
            if (oldBlocks != 0) lastData = p.ReadUInt32(AmigaFfsMountIndex.DataKeys(ref p, w.Index), (int)(oldBlocks - 1) * 4);
        }
        else
        {
            var whole = MountIndexEnabled && w.LayoutKnown && first == 0 && AmigaFfsMountIndex.FitsFile(ref p, w.Index, oldBlocks, tableEntries);
            if (!Chain(ref p, ref w, r.Header, false, out lastData, first, whole ? uint.MaxValue : limit)) return false;
        }
        var newBlocks = oldBlocks;
        uint oldExt = 0, newExt = 0;
        if (end > size)
        {
            newBlocks = AmigaGeometryMath.Divide(end - 1, blockBytes) + 1;
            oldExt = AppendTailTable(oldBlocks, tableEntries);
            newExt = AppendTailTable(newBlocks, tableEntries);
            if (!CanGrow(ref p, ref w, in geometry, newBlocks - oldBlocks + newExt - oldExt)) return false;
        }
        var fileKeys = APTR.Null;
        var fileTables = APTR.Null;
        if (MountIndexEnabled && w.LayoutKnown && AmigaFfsMountIndex.FitsFile(ref p, w.Index, newBlocks, geometry.TableEntries) &&
            (oldBlocks == 0 || AmigaFfsMountIndex.Matches(ref p, w.Index, r.Header, size)))
        {
            fileKeys = AmigaFfsMountIndex.DataKeys(ref p, w.Index);
            fileTables = AmigaFfsMountIndex.TableKeys(ref p, w.Index);
            p.WriteUInt32(fileTables, 0, r.Header);
            w.RestoreFile = r.Header; w.RestoreSize = newSize; w.WritingIndexedFile = true;
            if (w.GroupFileWrites && w.DosType < 0x444F5304 && p.ReadUInt32(w.Index, AmigaFfsMountIndex.HeaderImage) != 0)
                w.GroupHeader = r.Header;
        }
        if (MountIndexEnabled && w.BufferSmallAppends && TryStartBufferedAppend(ref p, ref w, ref r, in geometry, size, out actual))
            return w.Error == DOS.Error.None;
        if (MountIndexEnabled && fileKeys.IsNotNull && w.GroupHeader != 0 && w.GroupFileWrites && w.DosType < 0x444F5304 && r.Position == size)
            return AppendFfsBlocks(ref p, ref w, ref r, in geometry, fileKeys, fileTables, oldBlocks, newBlocks,
                oldExt, newExt, lastData, out actual, resumed);
        // A single verified physical run cannot benefit from relocation.
        // Its data fits one logical transfer even across pointer-table boundaries.
        if (MountIndexEnabled && fileKeys.IsNotNull && fixedHardDisk && end <= size &&
            ((r.Position | r.Length) & (blockBytes - 1)) == 0)
        {
            var count = limit - first;
            var key = VerifiedRun(ref p, w.Index, r.Header, fileKeys, first, count);
            if (key != 0)
            {
                if (!Begin(ref p, ref w, in geometry, keepRuns: true)) return false;
                if (!p.WriteDataBlocks(key, count, r.Buffer)) return Fail(ref w, DOS.Error.DiskNotValidated);
                actual = r.Length;
                if (w.GroupFileWrites && w.RootImage.IsNotNull)
                {
                    p.WriteUInt32(w.Index, AmigaFfsMountIndex.Header, r.Header);
                    p.WriteUInt32(w.Index, AmigaFfsMountIndex.Status, AmigaFfsMountIndex.GroupOpen);
                    return true;
                }
                return Finish(ref p, ref w, in geometry);
            }
        }
        if (!Begin(ref p, ref w, in geometry)) return false;
        var sizePublished = false;
        var table = r.Header; var tableIndex = 0u;
        uint runFirst = 0, runCount = 0;
        // A failed fit is final for this table. Never probe again per sector.
        var probeAt = fixedHardDisk && end <= size && ((r.Position | r.Length) & (blockBytes - 1)) == 0
            ? first : uint.MaxValue;
        while (actual < r.Length)
        {
            var position = r.Position + actual; var index = AmigaGeometryMath.DivRem(position, blockBytes, out var withinRemainder);
            var targetTable = AmigaGeometryMath.DivRem(index, tableEntries, out var slotRemainder);
            var slot = slotRemainder;
            // Walk/extend pointer tables sequentially, retaining only their key.
            if (MountIndexEnabled && fileTables.IsNotNull && targetTable != tableIndex && oldBlocks != 0 &&
                targetTable <= (oldBlocks - 1) / geometry.TableEntries)
            {
                table = p.ReadUInt32(fileTables, (int)targetTable * 4);
                tableIndex = targetTable; runCount = 0;
            }
            while (tableIndex < targetTable)
            {
                runCount = 0;
                if (!ReadWriteTable(ref p, ref w, table)) return Fail(ref w, DOS.Error.DiskNotValidated);
                var next = p.ReadUInt32(w.Meta, geometry.Tail + 504);
                if (next == 0)
                {
                    // Only growth past the existing blocks may add a table.
                    if ((tableIndex + 1) * tableEntries < oldBlocks) return Fail(ref w, DOS.Error.DiskNotValidated);
                    next = Allocate(ref p, ref w, in geometry); if (next == 0) return false;
                    p.Clear(w.Meta, geometry.BlockBytes); p.WriteUInt32(w.Meta, 0, 16); p.WriteUInt32(w.Meta, 4, next);
                    p.WriteUInt32(w.Meta, 12, geometry.TableEntries); p.WriteUInt32(w.Meta, geometry.Tail + 500, r.Header); p.WriteUInt32(w.Meta, geometry.Tail + 508, 0xFFFFFFFD);
                    if (!Put(ref p, ref w, next, w.Meta, 20) || !ReadWriteTable(ref p, ref w, table))
                        return Fail(ref w, DOS.Error.DiskNotValidated);
                    p.WriteUInt32(w.Meta, geometry.Tail + 504, next);
                    if (!Put(ref p, ref w, table, w.Meta, 20)) return false;
                }
                table = next; tableIndex++;
                if (MountIndexEnabled && fileTables.IsNotNull) p.WriteUInt32(fileTables, (int)tableIndex * 4, table);
            }
            if (!ReadWriteTable(ref p, ref w, table)) return Fail(ref w, DOS.Error.DiskNotValidated);
            var at = geometry.Tail + 308 - (int)slot * 4; var key = p.ReadUInt32(w.Meta, at);
            // Chain proved every existing slot; never let a stale or zero key
            // reach the boot block, reserved area or root as file data.
            if (index < oldBlocks && (key < geometry.ReservedBlocks || key >= geometry.BlockCount || key == geometry.RootBlock))
                return Fail(ref w, DOS.Error.DiskNotValidated);
            if (index == probeAt)
            {
                var countInTable = tableEntries - slot;
                if (countInTable > limit - index) countInTable = limit - index;
                probeAt += countInTable;
                if (!RelocateOverwrite(ref p, ref w, in geometry, ref r, table, slot, countInTable, ref actual, out var moved)) return false;
                if (moved)
                {
                    w.RestoreFile = 0; fileKeys = APTR.Null; fileTables = APTR.Null;
                    if (actual == r.Length) return Finish(ref p, ref w, in geometry);
                    continue;
                }
            }
            if (index >= oldBlocks)
            {
                var wanted = newBlocks - index;
                var slots = tableEntries - slot;
                if (wanted > slots) wanted = slots;
                key = AllocateData(ref p, ref w, in geometry, (lastData == 0 ? r.Header : lastData) + 1,
                    wanted, ref runFirst, ref runCount);
                if (key == 0) return false;
                if (MountIndexEnabled && fileKeys.IsNotNull) p.WriteUInt32(fileKeys, (int)index * 4, key);
                // Consume the already-proved, request-local contiguous hint in
                // one transfer. Stay within this table/map, allocate every bit,
                // and write data before publishing the complete pointer prefix.
                // The root remains invalid until the ordinary Finish path.
                var full = AmigaGeometryMath.Divide(r.Length - actual, blockBytes);
                if (fixedHardDisk && withinRemainder == 0 && full >= 2 && runCount != 0)
                {
                    var blocks = runCount + 1;
                    if (blocks > full) blocks = full;
                    if (!ClaimRun(ref p, ref w, key + 1, blocks - 1)) return false;
                    if (!p.WriteDataBlocks(key, blocks, APTR.FromPointer(r.Buffer.Raw + actual)))
                        return Fail(ref w, DOS.Error.DiskNotValidated);
                    for (var i = 0u; i < blocks; i++)
                    {
                        p.WriteUInt32(w.Meta, at - (int)i * 4, key + i);
                        if (MountIndexEnabled && fileKeys.IsNotNull) p.WriteUInt32(fileKeys, (int)(index + i) * 4, key + i);
                    }
                    p.WriteUInt32(w.Meta, 8, slot + blocks);
                    if (index == 0) p.WriteUInt32(w.Meta, 16, key);
                    if (table == r.Header && blocks * blockBytes == r.Length - actual)
                        sizePublished = SetFinalSize(ref p, ref w, geometry.Tail, newSize);
                    if (!Put(ref p, ref w, table, w.Meta, 20)) return false;
                    runFirst += blocks - 1; runCount -= blocks - 1;
                    lastData = key + blocks - 1;
                    actual += blocks * blockBytes;
                    continue;
                }
                lastData = key;
                p.Clear(w.Data, geometry.BlockBytes);
            }
            else
            {
                // Chain already validated every touched pointer and allocation bit.
                // Full existing FFS blocks need neither a scratch read nor a copy.
                // Keep the run inside this pointer table and the existing file.
                if (fixedHardDisk && r.Length - actual >= blockBytes * 2 && withinRemainder == 0)
                {
                    var full = AmigaGeometryMath.Divide(r.Length - actual, blockBytes);
                    if (full > oldBlocks - index) full = oldBlocks - index;
                    if (full > tableEntries - slot) full = tableEntries - slot;
                    var blocks = 1u;
                    while (blocks < full && p.ReadUInt32(w.Meta, at - (int)blocks * 4) == key + blocks) blocks++;
                    if (full >= 2 && blocks >= 2)
                    {
                        if (!p.WriteDataBlocks(key, blocks, APTR.FromPointer(r.Buffer.Raw + actual)))
                            return Fail(ref w, DOS.Error.DiskNotValidated);
                        actual += blocks * blockBytes;
                        continue;
                    }
                }
                if (!p.ReadBlock(key, w.Data)) return Fail(ref w, DOS.Error.DiskNotValidated);
            }
            var within = withinRemainder;
            var count = blockBytes - within;
            if (count > r.Length - actual) count = r.Length - actual;
            // Keep geometry and request fields out of the per-byte live set.
            var source = APTR.FromPointer(r.Buffer.Raw + actual);
            var destination = APTR.FromPointer(w.Data.Raw + within);
            p.Copy(source, destination, count);
            if (!Put(ref p, ref w, key, w.Data, -1)) return false;
            if (index >= oldBlocks)
            {
                p.WriteUInt32(w.Meta, at, key); p.WriteUInt32(w.Meta, 8, slot + 1);
                if (index == 0) p.WriteUInt32(w.Meta, 16, key);
                if (table == r.Header && count == r.Length - actual)
                    sizePublished = SetFinalSize(ref p, ref w, geometry.Tail, newSize);
                if (!Put(ref p, ref w, table, w.Meta, 20)) return false;
            }
            actual += count;
        }
        if (MountIndexEnabled && w.RestoreFile != 0)
        {
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.FileBlocks, newBlocks);
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.Tables, (newBlocks - 1) / geometry.TableEntries + 1);
        }
        if (newSize == size || sizePublished) return Finish(ref p, ref w, in geometry);
        if (!Header(ref p, ref w, r.Header)) return false;
        w.CacheParent = p.ReadUInt32(w.Meta, geometry.Tail + 500);
        p.WriteUInt32(w.Meta, geometry.Tail + 324, newSize);
        return Put(ref p, ref w, r.Header, w.Meta, 20) && Finish(ref p, ref w, in geometry);
    }

    private static uint VerifiedRun<T>(ref T p, APTR index, uint header, APTR keys, uint first, uint count)
        where T : struct, IAmigaFileSystemPlatform
    {
        if (p.ReadUInt32(index, AmigaFfsMountIndex.RunHeader) == header)
        {
            var offset = first - p.ReadUInt32(index, AmigaFfsMountIndex.RunLogical);
            var length = p.ReadUInt32(index, AmigaFfsMountIndex.RunLength);
            if (offset < length && count <= length - offset)
                return p.ReadUInt32(index, AmigaFfsMountIndex.RunPhysical) + offset;
            // In a strictly increasing prefix, adjacent endpoints prove that
            // every intermediate key is consecutive, even across table pages.
            var ascending = p.ReadUInt32(index, AmigaFfsMountIndex.AscendingBlocks);
            if (first < ascending && count <= ascending - first)
            {
                var start = p.ReadUInt32(keys, (int)first * 4);
                var last = p.ReadUInt32(keys, (int)(first + count - 1) * 4);
                return last - start == count - 1 ? start : 0;
            }
        }
        var key = p.ReadUInt32(keys, (int)first * 4);
        for (var i = 1u; i < count; i++)
            if (p.ReadUInt32(keys, (int)(first + i) * 4) != key + i) return 0;
        AmigaFfsMountIndex.RememberRun(ref p, index, header, first, key, count);
        return key;
    }

    // An open group owns the validated mapping and its invalid on-disk root.
    // A contained run can be submitted without constructing mutation scratch.
    // The caller has already matched the request to the open group and read
    // the group's materialized length.
    internal static bool TryWriteGroupRun<T>(ref T p, APTR index, ref Request r, uint blockBytes, uint size,
        bool deviceChecksProtection, out DOS.Error error) where T : struct, IAmigaWritableFileSystemPlatform
    {
        error = DOS.Error.None;
        if (r.Length == 0) return false;
        if ((p.ReadUInt32(index, AmigaFfsMountIndex.Type) & 1) == 0)
            return TryOverwriteOfsRun(ref p, index, ref r, blockBytes, size, deviceChecksProtection, out error);
        if (((r.Position | r.Length) & (blockBytes - 1)) != 0 ||
            r.Position > size || r.Length > size - r.Position) return false;
        if (p.ReadUInt32(index, AmigaFfsMountIndex.PendingAppend) != 0) return false;
        var count = blockBytes == 512 ? r.Length >> 9 : AmigaGeometryMath.Divide(r.Length, blockBytes);
        var first = blockBytes == 512 ? r.Position >> 9 : AmigaGeometryMath.Divide(r.Position, blockBytes);
        var key = VerifiedRun(ref p, index, r.Header, AmigaFfsMountIndex.DataKeys(ref p, index), first, count);
        if (key == 0) return false;
        if (r.Length > int.MaxValue || !p.IsMapped(r.Buffer, r.Length)) error = DOS.Error.BadNumber;
        else if (!deviceChecksProtection && p.WriteProtected()) error = DOS.Error.DiskWriteProtected;
        else if (!p.WriteDataBlocks(key, count, r.Buffer))
        {
            p.WriteUInt32(index, AmigaFfsMountIndex.Status, AmigaFfsMountIndex.GroupFailed);
            error = deviceChecksProtection && p.WriteProtected() ? DOS.Error.DiskWriteProtected : DOS.Error.DiskNotValidated;
        }
        return true;
    }

    public static bool HasFileWriteGroup<T>(ref T p, APTR index) where T : struct, IAmigaFileSystemPlatform
        => MountIndexEnabled && index.IsNotNull && p.ReadUInt32(index, AmigaFfsMountIndex.Status) == AmigaFfsMountIndex.GroupOpen;

    // A short overwrite cannot relocate or change the file length. The open
    // group already verified both the logical keys and their allocation bits.
    internal static bool FitsGroupSlice<T>(ref T p, APTR index, ref Request r, uint size, uint blockBytes)
        where T : struct, IAmigaWritableFileSystemPlatform =>
        r.Length != 0 && r.Length <= blockBytes * 2 && r.Position < size && r.Length <= size - r.Position &&
        p.ReadUInt32(index, AmigaFfsMountIndex.Type) < 0x444F5304;

    internal static bool OverwriteGroupSlice<T>(ref T p, APTR index, APTR scratch, ref Request r, bool deviceChecksProtection,
        out uint actual, out DOS.Error error)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        actual = 0;
        error = DOS.Error.None;
        if (!deviceChecksProtection && p.WriteProtected()) { error = DOS.Error.DiskWriteProtected; return false; }
        var ofs = (p.ReadUInt32(index, AmigaFfsMountIndex.Type) & 1) == 0;
        var prefix = ofs ? 24u : 0u;
        var payload = p.BlockBytes - prefix;
        var within = r.Position & 511;
        var logical = payload == 512 ? r.Position >> 9 : AmigaGeometryMath.DivRem(r.Position, payload, out within);
        var keys = AmigaFfsMountIndex.DataKeys(ref p, index);
        p.WriteUInt32(index, AmigaFfsMountIndex.Status, AmigaFfsMountIndex.Updating);
        var staged = false;
        var crossesSector = r.Length > payload - within;
        if (crossesSector)
        {
            // Short packets can straddle up to four OFS payloads. Only batch
            // verified physical neighbours, using the mount's transient stage.
            var count = 2u;
            var beyondFirst = r.Length - (payload - within);
            while (beyondFirst > payload) { count++; beyondFirst -= payload; }
            var bytes = p.BlockBytes;
            var stage = APTR.FromPointer(p.ReadUInt32(index, AmigaFfsMountIndex.Stage));
            var span = count * bytes;
            if (span <= p.ReadUInt32(index, AmigaFfsMountIndex.StageBytes) &&
                stage.Raw - r.Buffer.Raw >= r.Length && r.Buffer.Raw - stage.Raw >= span)
            {
                var start = VerifiedRun(ref p, index, r.Header, keys, logical, count);
                if (start != 0)
                {
                    staged = true;
                    if (PatchGroupRun(ref p, ref r, stage, start, count, bytes, within, prefix, beyondFirst))
                        actual = r.Length;
                }
            }
        }
        while (!staged && actual < r.Length)
        {
            var key = p.ReadUInt32(keys, (int)logical * 4);
            var take = payload - within;
            if (take > r.Length - actual) take = r.Length - actual;
            var source = APTR.FromPointer(r.Buffer.Raw + actual);
            var streamCold = false;
            if (ofs || take < payload)
            {
                // A scattered overwrite only needs the sector being patched.
                // Avoid a track-sized refill on a miss during read-modify-write.
#if COPPERFS_FFS_HANDLER
                var retained = p.ReadRetainedBlock(key);
                // A scattered multi-sector OFS patch is already doing
                // read-modify-write in scratch. Avoid admitting a cold
                // payload into the metadata LRU solely to copy it again.
                streamCold = ofs && crossesSector && retained.IsNull;
                if (retained.IsNotNull) p.Copy(retained, scratch, p.BlockBytes);
                else
#endif
                if (p.ReadDataBlocks(key, 1, scratch, false) != 1) break;
                if (ofs) PatchOfsPayload(ref p, source, scratch, prefix + within, take, p.BlockBytes);
                else p.Copy(source, APTR.FromPointer(scratch.Raw + within), take);
                source = scratch;
            }
            var written = streamCold ? p.WriteDataBlocks(key, 1, source) :
                p.SupportsRetainedBlockWrites ? p.WriteRetainedBlock(key, source) : p.WriteBlock(key, source);
            if (!written) break;
            actual += take;
            logical++;
            within = 0;
        }
        if (actual == r.Length)
        {
            p.WriteUInt32(index, AmigaFfsMountIndex.Status, AmigaFfsMountIndex.GroupOpen);
            return true;
        }
        p.WriteUInt32(index, AmigaFfsMountIndex.Status, AmigaFfsMountIndex.GroupFailed);
        error = deviceChecksProtection && p.WriteProtected() ? DOS.Error.DiskWriteProtected : DOS.Error.DiskNotValidated;
        return false;
    }

    private static bool PatchGroupRun<T>(ref T p, ref Request r, APTR stage, uint start,
        uint count, uint bytes, uint within, uint prefix, uint lastBytes)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        // Preserve the edges of FFS sectors and all existing OFS headers.
        var firstRead = prefix != 0 || within != 0 ? 0u : count - 1;
        var endRead = prefix != 0 || lastBytes < bytes ? count : 1u;
        if (firstRead < endRead)
        {
            var target = APTR.FromPointer(stage.Raw + firstRead * bytes);
            if (p.ReadDataBlocks(start + firstRead, endRead - firstRead, target, false) != endRead - firstRead)
                return false;
        }
        if (prefix == 0)
            p.Copy(r.Buffer, APTR.FromPointer(stage.Raw + within), r.Length);
        else
        {
            var copied = 0u;
            for (var i = 0u; i < count; i++)
            {
                var sector = APTR.FromPointer(stage.Raw + i * bytes);
                var take = bytes - prefix - within;
                if (take > r.Length - copied) take = r.Length - copied;
                var source = APTR.FromPointer(r.Buffer.Raw + copied);
                if (take == bytes - prefix) CopyAndSealOfs(ref p, source, sector, take);
                else PatchOfsPayload(ref p, source, sector, prefix + within, take, bytes);
                copied += take;
                within = 0;
            }
        }
        return p.WriteDataBlocks(start, count, stage);
    }

    private static bool ReadWriteTable<T>(ref T p, ref Work w, uint table) where T : struct, IAmigaWritableFileSystemPlatform
    {
#if COPPERFS_NO_FFS_MOUNT_INDEX
        return p.ReadBlock(table, w.Meta);
#else
        if (w.GroupHeader != 0 && table != 0 && p.ReadUInt32(w.Index, AmigaFfsMountIndex.FirstExtension) == table)
        {
            p.Copy(APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.FirstExtensionImage)), w.Meta, p.BlockBytes);
            return true;
        }
        if (w.GroupHeader != 0 && table != 0 && p.ReadUInt32(w.Index, AmigaFfsMountIndex.PendingTable) == table)
        {
            p.Copy(APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.TableImage)), w.Meta, p.BlockBytes);
            return true;
        }
        if (w.GroupHeader != table || table == 0 || p.ReadUInt32(w.Index, AmigaFfsMountIndex.PendingHeader) != table)
            return p.ReadBlock(table, w.Meta);
        p.Copy(APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.HeaderImage)), w.Meta, p.BlockBytes);
        return true;
#endif
    }

    private static bool PublishGroupTable<T>(ref T p, ref Work w, bool retainPrivate = false,
        int pending = AmigaFfsMountIndex.PendingTable) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var key = p.ReadUInt32(w.Index, pending);
        if (key == 0) return true;
        var image = APTR.FromPointer(p.ReadUInt32(w.Index, pending + 4));
        SealFileImage(ref p, w.Index, key, image, false);
        var written = retainPrivate ? p.WriteDataBlocks(key, 1, image) :
#if COPPERFS_FFS_HANDLER
            p.WriteSealedClassicBlock(key, image);
#else
            p.SupportsRetainedBlockWrites ? p.WriteRetainedBlock(key, image) : p.WriteBlock(key, image);
#endif
        if (!written)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        p.WriteUInt32(w.Index, pending, 0);
        return true;
    }

    private static bool PublishFileTables<T>(ref T p, ref Work w, bool retainPrivate)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var first = p.ReadUInt32(w.Index, AmigaFfsMountIndex.FirstExtension);
        if (first == 0) return PublishGroupTable(ref p, ref w, retainPrivate);
        var tail = p.ReadUInt32(w.Index, AmigaFfsMountIndex.PendingTable);
        var child = APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.TableImage));
        var parent = APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.FirstExtensionImage));
        if (tail != 0 && first == tail + 1 && parent.Raw - child.Raw == p.BlockBytes &&
            p.ReadUInt32(parent, (int)p.BlockBytes - 8) == tail)
        {
            SealFileImage(ref p, w.Index, tail, child, false);
            SealFileImage(ref p, w.Index, first, parent, false);
            if (!p.WriteDataBlocks(tail, 2, child)) return Fail(ref w, DOS.Error.DiskNotValidated);
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.PendingTable, 0);
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.FirstExtension, 0);
            return true;
        }
        return PublishGroupTable(ref p, ref w, retainPrivate) &&
            PublishGroupTable(ref p, ref w, false, AmigaFfsMountIndex.FirstExtension);
    }

    private static bool PublishGroupHeader<T>(ref T p, ref Work w, bool retainPrivate = false, bool retainParent = false) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!w.LayoutKnown) return true;
        var previousTable = p.ReadUInt32(w.Index, AmigaFfsMountIndex.PreviousTable);
        if (previousTable != 0 && !PublishAdjacentSibling(ref p, ref w, previousTable)) return false;
        if (!PublishPreviousImage(ref p, ref w, AmigaFfsMountIndex.PreviousTable) ||
            !PublishPreviousImage(ref p, ref w, AmigaFfsMountIndex.PreviousHeader)) return false;
        if (!PublishFileTables(ref p, ref w, retainPrivate)) return false;
        var key = p.ReadUInt32(w.Index, AmigaFfsMountIndex.PendingHeader);
        w.GroupHeader = 0;
        if (key != 0)
        {
            var image = APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.HeaderImage));
            if (retainPrivate)
            {
                SealFileImage(ref p, w.Index, key, image, true);
                if (!p.WriteDataBlocks(key, 1, image)) return Fail(ref w, DOS.Error.DiskNotValidated);
            }
            else if (!Put(ref p, ref w, key, image, 20)) return false;
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.PendingHeader, 0);
        }
        return PublishParent(ref p, ref w, retainParent);
    }

    private static bool PublishAdjacentSibling<T>(ref T p, ref Work w, uint previousTable)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        // The two files are independent. Their data and shared bitmap have
        // been written. The sibling may reference only published extensions.
        if (p.ReadUInt32(w.Index, AmigaFfsMountIndex.PendingHeader) != previousTable + 1 ||
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.PendingTable) != 0 ||
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.FirstExtension) != 0 ||
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.ParkedHeader) == 0 ||
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.Type) is not (0x444F5301 or 0x444F5303)) return true;
        var table = APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.PreviousTableImage));
        var sibling = APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.HeaderImage));
        if (sibling.Raw - table.Raw != p.BlockBytes) return true;
        SealFileImage(ref p, w.Index, previousTable, table, false);
        SealFileImage(ref p, w.Index, previousTable + 1, sibling, true);
        if (!p.WriteDataBlocks(previousTable, 2, table)) return Fail(ref w, DOS.Error.DiskNotValidated);
        p.WriteUInt32(w.Index, AmigaFfsMountIndex.PublishedPreviousTable, previousTable);
        p.WriteUInt32(w.Index, AmigaFfsMountIndex.PreviousTable, 0);
        p.WriteUInt32(w.Index, AmigaFfsMountIndex.PendingHeader, 0);
        return true;
    }

    private static bool PublishParent<T>(ref T p, ref Work w, bool retainPrivate = false) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var parent = p.ReadUInt32(w.Index, AmigaFfsMountIndex.PendingParent);
        if (parent != 0)
        {
            var image = APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.ParentImage));
            if (retainPrivate)
            {
                // ParentImage already carries the checksum delta for its link.
                // Keep it available for lookup after the root has been published.
                if (!p.WriteDataBlocks(parent, 1, image)) return Fail(ref w, DOS.Error.DiskNotValidated);
            }
            else if (!Put(ref p, ref w, parent, image, SealedHeader)) return false;
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.PendingParent, 0);
        }
        return true;
    }

    private static bool PublishPreviousImage<T>(ref T p, ref Work w, int field) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var key = p.ReadUInt32(w.Index, field);
        if (key == 0) return true;
        var image = APTR.FromPointer(p.ReadUInt32(w.Index, field + 4));
        if (p.ReadUInt32(w.Index, AmigaFfsMountIndex.ParkedHeader) != 0 &&
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.Type) is 0x444F5301 or 0x444F5303)
        {
            SealFileImage(ref p, w.Index, key, image, field == AmigaFfsMountIndex.PreviousHeader);
            if (!p.WriteDataBlocks(key, 1, image)) return Fail(ref w, DOS.Error.DiskNotValidated);
            var published = field == AmigaFfsMountIndex.PreviousHeader
                ? AmigaFfsMountIndex.PublishedPreviousHeader : AmigaFfsMountIndex.PublishedPreviousTable;
            p.WriteUInt32(w.Index, published, key);
        }
        else if (!Put(ref p, ref w, key, image, 20)) return false;
        p.WriteUInt32(w.Index, field, 0);
        return true;
    }

    // An empty bucket in the owned root proves that opening this new sibling
    // cannot observe the previous file. Keep both metadata images until the
    // shared bitmap is published. Any close still publishes the entire group.
    public static bool TryCreateGroupedSibling<T>(ref T p, ref Work w, ref Request r)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!HasFileWriteGroup(ref p, w.Index) || r.Length == 0 || r.Length > 30 ||
            r.Directory != AmigaFfsGeometry.Read(ref p).RootBlock ||
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.PreviousHeaderImage) == 0 ||
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.PreviousHeader) != 0 ||
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.PreviousTable) != 0 ||
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.PendingParent) != 0) return false;
        var type = p.ReadUInt32(w.Index, AmigaFfsMountIndex.Type);
        if (type >= 0x444F5304 || !p.IsMapped(r.Name, r.Length)) return false;
        for (var i = 0u; i < r.Length; i++)
            if (p.ReadUInt8(r.Name, (int)i) is (byte)'/' or (byte)':') return false;
        var root = APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.RootImage));
        var slot = 24 + (int)Hash(ref p, r.Name, r.Length, type) * 4;
        if (p.ReadUInt32(root, slot) != 0) return false;
        w.Error = DOS.Error.None;
        if (!DrainBufferedAppend(ref p, ref w)) return true;
        if (p.ReadUInt32(w.Index, AmigaFfsMountIndex.FirstExtension) != 0)
        {
            if (p.ReadUInt32(w.Index, AmigaFfsMountIndex.PendingTable) != 0)
            {
                p.WriteUInt32(w.Index, AmigaFfsMountIndex.Status, AmigaFfsMountIndex.Updating);
                if (!PublishGroupTable(ref p, ref w))
                {
                    p.WriteUInt32(w.Index, AmigaFfsMountIndex.Status, AmigaFfsMountIndex.GroupFailed);
                    return true;
                }
                p.WriteUInt32(w.Index, AmigaFfsMountIndex.Status, AmigaFfsMountIndex.GroupOpen);
            }
            SaveGroupImage(ref p, w.Index, AmigaFfsMountIndex.FirstExtension, AmigaFfsMountIndex.PendingTable);
        }
        var parked = AmigaFfsMountIndex.ParkFile(ref p, w.Index);
        SaveGroupImage(ref p, w.Index, AmigaFfsMountIndex.PendingHeader, AmigaFfsMountIndex.PreviousHeader);
        SaveGroupImage(ref p, w.Index, AmigaFfsMountIndex.PendingTable, AmigaFfsMountIndex.PreviousTable);
        w.GroupFileWrites = true;
        if (CreateMissingEntry(ref p, ref w, ref r, slot, false, true))
        {
            if (parked != 0) AmigaFfsMountIndex.SwapDataKeys(ref p, w.Index);
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.Companion, r.Header);
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.ParkedHeader, parked);
        }
        else if (HasFileWriteGroup(ref p, w.Index))
        {
            SaveGroupImage(ref p, w.Index, AmigaFfsMountIndex.PreviousTable, AmigaFfsMountIndex.PendingTable);
            SaveGroupImage(ref p, w.Index, AmigaFfsMountIndex.PreviousHeader, AmigaFfsMountIndex.PendingHeader);
        }
        return true;
    }

    private static void SaveGroupImage<T>(ref T p, APTR index, int pending, int previous)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var key = p.ReadUInt32(index, pending);
        if (key == 0) return;
        p.WriteUInt32(index, pending, 0);
        MoveGroupImage(ref p, index, pending, previous, key);
    }

    private static void MoveGroupImage<T>(ref T p, APTR index, int source, int destination, uint key)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var spare = p.ReadUInt32(index, destination + 4);
        p.WriteUInt32(index, destination + 4, p.ReadUInt32(index, source + 4));
        p.WriteUInt32(index, source + 4, spare);
        p.WriteUInt32(index, destination, key);
    }

    public static bool FinishFileWriteGroup<T>(ref T p, ref Work w) where T : struct, IAmigaWritableFileSystemPlatform
    {
#if COPPERFS_NO_FFS_MOUNT_INDEX
        return true;
#else
        if (w.Index.IsNull) return true;
        var status = p.ReadUInt32(w.Index, AmigaFfsMountIndex.Status);
        if (status == AmigaFfsMountIndex.GroupFailed || status == AmigaFfsMountIndex.Updating)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if (status != AmigaFfsMountIndex.GroupOpen && status != AmigaFfsMountIndex.MetadataOpen) return true;
        if (status == AmigaFfsMountIndex.GroupOpen && !DrainBufferedAppend(ref p, ref w)) return false;
        p.WriteUInt32(w.Index, AmigaFfsMountIndex.Status, AmigaFfsMountIndex.Updating);
        var g = AmigaFfsGeometry.Read(ref p);
        w.Error = DOS.Error.None;
        w.RootImage = APTR.FromPointer(p.ReadUInt32(w.Index, AmigaFfsMountIndex.RootImage));
        w.RootKey = g.RootBlock;
        w.LayoutKnown = true;
        w.BitmapDirty = 0;
        var grouped = w.GroupFileWrites;
        w.GroupFileWrites = true;
        var bitmapPublished = FlushBitmap(ref p, ref w);
        w.GroupFileWrites = grouped;
        // Completed packets wrote their data. The retained
        // root and file mapping belong to this attachment. Publish validity last.
        // These images stay in mount-owned RAM after close. A reader can
        // borrow them until the next mutation starts. Streaming the sectors
        // avoids copying the same sealed bytes to the metadata LRU.
        var retainPrivate = w.ReadsGroupedRoot && status == AmigaFfsMountIndex.GroupOpen;
        if (p.ReadUInt32(w.Index, AmigaFfsMountIndex.FirstExtension) != 0 &&
            p.ReadUInt32(w.Index, AmigaFfsMountIndex.PendingTable) == 0)
            SaveGroupImage(ref p, w.Index, AmigaFfsMountIndex.FirstExtension, AmigaFfsMountIndex.PendingTable);
        var header = p.ReadUInt32(w.Index, AmigaFfsMountIndex.PendingHeader);
        var table = p.ReadUInt32(w.Index, AmigaFfsMountIndex.PendingTable);
        var parent = p.ReadUInt32(w.Index, AmigaFfsMountIndex.PendingParent);
        if (!bitmapPublished || !PublishGroupHeader(ref p, ref w, retainPrivate, w.ReadsGroupedDirectories) ||
            !Begin(ref p, ref w, in g, uint.MaxValue))
        {
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.Status, AmigaFfsMountIndex.GroupFailed);
            return false;
        }
        if (retainPrivate)
        {
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.PublishedHeader, header != parent ? header : 0);
            p.WriteUInt32(w.Index, AmigaFfsMountIndex.PublishedTable, table != parent ? table : 0);
        }
        if (w.ReadsGroupedDirectories) p.WriteUInt32(w.Index, AmigaFfsMountIndex.PublishedParent, parent);
        p.WriteUInt32(w.Index, AmigaFfsMountIndex.Status, AmigaFfsMountIndex.Clean);
        return true;
#endif
    }

    // The header is also the first pointer table. Seal the final pointer and
    // length together once its data has succeeded.
    private static bool SetFinalSize<T>(ref T p, ref Work w, int tail, uint size)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        w.CacheParent = p.ReadUInt32(w.Meta, tail + 500); w.CacheKey = p.ReadUInt32(w.Meta, 4);
        p.WriteUInt32(w.Meta, tail + 324, size);
        return true;
    }

    // Limit the first hard-disk policy to a single bitmap page per table
    // segment. Reservation and release then each cost just one bitmap write.
    private static bool RelocateOverwrite<T>(ref T p, ref Work w, in AmigaFfsGeometry g,
        ref Request r, uint table, uint slot, uint count, ref uint actual, out bool moved)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        moved = false;
        if (count < 8) return true;
        var at = g.Tail + 308 - (int)slot * 4;
        var first = p.ReadUInt32(w.Meta, at);
        var last = first;
        var breaks = 0u;
        for (var i = 1u; i < count; i++)
        {
            var key = p.ReadUInt32(w.Meta, at - (int)i * 4);
            if ((w.DosType & 1) == 0)
            {
                if ((key > last ? key - last : last - key) >= 128) breaks++;
            }
            else if (key != last + 1) breaks++;
            last = key;
        }
        // Charge both ends for potentially severing an existing run. At a
        // table boundary the reader already starts a separate transfer.
        var entries = p.ReadUInt32(w.Meta, 8);
        var cuts = slot != 0 && p.ReadUInt32(w.Meta, at + 4) + 1 == first ? 1u : 0u;
        if (slot + count < entries && p.ReadUInt32(w.Meta, at - (int)count * 4) == last + 1) cuts++;
        // The 32-block/four-run benchmark amortized publication on the
        // overwrite itself. Keep the higher read-savings margin below it.
        var requiredBreaks = count >= 32 ? 3u : 4u;
        if ((w.DosType & 1) == 0) requiredBreaks = 7;
        if (breaks < requiredBreaks + cuts) return true;
        if (!LoadBitmap(ref p, ref w, first)) return false;
        for (var i = 1u; i < count; i++)
            if (unchecked(p.ReadUInt32(w.Meta, at - (int)i * 4) - w.BitmapFirst) >= w.BitmapCount) return true;

        // Try just past the last old block, then the beginning of the same
        // page. The second window can find space before a crowded file.
        var offset = last - w.BitmapFirst + 1;
        var destination = OverwriteRun(ref p, ref w, g.RootBlock, offset, w.BitmapCount, count);
        if (destination == 0) destination = OverwriteRun(ref p, ref w, g.RootBlock, 0, w.BitmapCount, count);
        if (destination == 0) return true;

        // Validate source ownership within this table before releasing any
        // pointers. Data is scratch until the new pointer image is assembled.
        p.Clear(w.Data, g.BlockBytes);
        var next = p.ReadUInt32(w.Meta, g.Tail + 504);
        for (var i = 0u; i < entries; i++)
        {
            var key = p.ReadUInt32(w.Meta, g.Tail + 308 - (int)i * 4);
            var bit = unchecked(key - w.BitmapFirst);
            if (bit >= w.BitmapCount) continue;
            var word = (int)(bit / 32) * 4;
            var mask = 1u << (int)(bit % 32);
            var seen = p.ReadUInt32(w.Data, word);
            if ((seen & mask) != 0 || key == table || key == r.Header || key == next ||
                key == w.BitmapBlock || key == g.RootBlock || SearchFree(ref p, ref w, key, g.RootBlock))
                return Fail(ref w, DOS.Error.DiskNotValidated);
            p.WriteUInt32(w.Data, word, seen | mask);
        }

        // Keep the invalid marker durable while the two sets of blocks coexist.
        if (!FlushBitmap(ref p, ref w) || (MountIndexEnabled && !PublishGroupHeader(ref p, ref w)) || !p.Flush())
            return Fail(ref w, DOS.Error.DiskNotValidated);
        w.GroupHeader = 0;
        for (var i = 0u; i < count; i++) Bit(ref p, ref w, destination + i, false);
        if (!FlushBitmap(ref p, ref w)) return false;
        if ((w.DosType & 1) == 0)
        {
            if (!WriteReplacementOfsData(ref p, ref w, ref r, destination, count)) return false;
        }
        else if (!p.WriteDataBlocks(destination, count, APTR.FromPointer(r.Buffer.Raw + actual)))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if (!p.Flush())
            return Fail(ref w, DOS.Error.DiskNotValidated);
        // Keep the old table in Meta until the replacement table is on disk.
        p.Copy(w.Meta, w.Data, g.BlockBytes);
        for (var i = 0u; i < count; i++) p.WriteUInt32(w.Data, at - (int)i * 4, destination + i);
        if (table == r.Header && slot == 0) p.WriteUInt32(w.Data, 16, destination);
        if (!Put(ref p, ref w, table, w.Data, 20)) return false;
        actual += (w.DosType & 1) == 0 ? r.Length : count * g.BlockBytes;
        if (!p.Flush()) return Fail(ref w, DOS.Error.DiskNotValidated);
        for (var i = 0u; i < count; i++) Bit(ref p, ref w, p.ReadUInt32(w.Meta, at - (int)i * 4), true);
        moved = true;
        return FlushBitmap(ref p, ref w) && (p.Flush() || Fail(ref w, DOS.Error.DiskNotValidated));
    }

    private static uint OverwriteRun<T>(ref T p, ref Work w, uint root, uint offset, uint limit, uint count)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var window = limit - offset;
        if (window > count + 128) window = count + 128;
        uint length = 0;
        for (var i = 0u; i < window; i++)
        {
            var key = w.BitmapFirst + offset + i;
            if (key == w.BitmapBlock || !SearchFree(ref p, ref w, key, root)) length = 0;
            else if (++length == count) return key - (count - 1);
        }
        return 0;
    }
}
