using Amiga;

namespace CopperStart.Dos;

/// <summary>Allocation-free adapters for caller-owned native file records.</summary>
public static class NativeFileSystemReadCore
{
    // All formats share the validity word at 56, cleared by mount invalidation.
    // 88..95 remain the file's high size/position words; the wider SFS2 cursor
    // continues at 96 inside the existing 216-byte private object allocation.
    // FFS uses its otherwise unused word at 96 for the next pointer-table key.
    public static int ReadFfsAligned<T>(ref T p, APTR file, uint header, uint dosType, uint position,
        APTR destination, uint length, APTR metadata, APTR data, out DOS.Error error)
        where T : struct, IAmigaFileSystemPlatform
    {
        if (file.IsNull) return AmigaDiskFileSystemCore.Read(ref p, header, dosType, position,
            destination, length, metadata, data, out error);
        if (length != 0 && length < 512)
            return ReadFfsPartial(ref p,file,header,dosType,position,destination,length,metadata,data,out error);
        // The native record already owns a validated run. Whole 512-byte
        // reads within it need no temporary cursor, table frame or division.
        // Keep the general reader for scratch/private-record aliases and tails.
        if (length != 0 && length <= int.MaxValue && ((length | position) & 511) == 0 &&
            (destination.Raw & 3) == 0 && (dosType & 1) != 0 && p.BlockBytes == 512 &&
            header != 0 && p.ReadUInt32(file, 56) == header && p.ReadUInt32(file, 84) == dosType &&
            Disjoint(destination, length, file, 216) && Disjoint(destination, length, metadata, 512) &&
            Disjoint(destination, length, data, 512) && p.IsMapped(destination, length))
        {
            var size = p.ReadUInt32(file, 60);
            var index = position >> 9;
            var start = p.ReadUInt32(file, 72);
            var count = p.ReadUInt32(file, 80);
            if (position <= size && index >= start && index - start < count)
            {
                var take = length;
                if (take > size - position) take = size - position;
                if (take != 0 && (take & 511) == 0 && (take >> 9) <= count - (index - start))
                {
                    var blocks = take >> 9;
                    var first=p.ReadUInt32(file,76)+index-start;
                    var actual = length <= 4096 ? p.ReadDataBlocks(first,blocks,destination)
                        : p.ReadDataBlocks(first,blocks,destination,false);
                    error = DOS.Error.None;
                    if (actual == blocks) return (int)take;
                    // No cursor word can alias the destination here. On error
                    // only validity changes; retain the same completed prefix.
                    p.WriteUInt32(file, 56, 0);
                    error = DOS.Error.DiskNotValidated;
                    return actual == 0 || actual > blocks ? -1 : (int)(actual << 9);
                }
            }
        }
        return ReadFfs(ref p, file, header, dosType, position, destination, length, metadata, data, out error);
    }

    private static int ReadFfsPartial<T>(ref T p, APTR file, uint header, uint dosType, uint position,
        APTR destination, uint length, APTR metadata, APTR data, out DOS.Error error)
        where T : struct, IAmigaFileSystemPlatform
    {
        // A retained run also covers partial packets within a single sector.
        // Consume its logical view immediately; misses and aliases keep the
        // general reader's scratch, cursor writeback and failure behavior.
        var within=position & 511;
        if (length <= 512-within && (dosType & 1)!=0 && p.BlockBytes==512 && header!=0 &&
            p.ReadUInt32(file,56)==header && p.ReadUInt32(file,84)==dosType &&
            Disjoint(destination,length,file,216) && Disjoint(destination,length,metadata,512) &&
            Disjoint(destination,length,data,512) && p.IsMapped(destination,length))
        {
            var index=position>>9; var start=p.ReadUInt32(file,72); var count=p.ReadUInt32(file,80);
            var size=p.ReadUInt32(file,60);
            if(position<size && index>=start && index-start<count)
            {
                var take=length; if(take>size-position) take=size-position;
                var image=p.ReadRetainedBlock(p.ReadUInt32(file,76)+index-start);
                if(image.IsNotNull && Disjoint(destination,take,image,512))
                {
                    p.Copy(APTR.FromPointer(image.Raw+within),destination,take);
                    error=DOS.Error.None; return (int)take;
                }
            }
        }
        return ReadFfs(ref p,file,header,dosType,position,destination,length,metadata,data,out error);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static bool Disjoint(APTR a, uint length, APTR b, uint bytes) =>
        a.Raw <= b.Raw ? b.Raw - a.Raw >= length : a.Raw - b.Raw >= bytes;

    public static int ReadFfs<T>(ref T p, APTR file, uint header, uint dosType, uint position,
        APTR destination, uint length, APTR metadata, APTR data, out DOS.Error error)
        where T : struct, IAmigaFileSystemPlatform
    {
        if (file.IsNull) return AmigaDiskFileSystemCore.Read(ref p, header, dosType, position,
            destination, length, metadata, data, out error);
        var cursor = new AmigaDiskFileSystemCore.ReadCursor
        {
            Header = p.ReadUInt32(file, 56), Size = p.ReadUInt32(file, 60), Table = p.ReadUInt32(file, 64),
            TableIndex = p.ReadUInt32(file, 68), RunStart = p.ReadUInt32(file, 72),
            RunBlock = p.ReadUInt32(file, 76), RunCount = p.ReadUInt32(file, 80), DosType = p.ReadUInt32(file, 84),
            NextTable = p.ReadUInt32(file, 96)
        };
        var actual = AmigaDiskFileSystemCore.Read(ref p, ref cursor, header, dosType, position,
            destination, length, metadata, data, out error, out var changed);
        // Retain the old writeback if the caller aliases the private object:
        // copying data there may have overwritten its cached cursor words.
        if (!changed && (destination.Raw <= file.Raw
            ? file.Raw - destination.Raw >= length
            : destination.Raw - file.Raw >= 216)) return actual;
        p.WriteUInt32(file, 56, cursor.Header); p.WriteUInt32(file, 60, cursor.Size);
        p.WriteUInt32(file, 64, cursor.Table); p.WriteUInt32(file, 68, cursor.TableIndex);
        p.WriteUInt32(file, 72, cursor.RunStart); p.WriteUInt32(file, 76, cursor.RunBlock);
        p.WriteUInt32(file, 80, cursor.RunCount); p.WriteUInt32(file, 84, cursor.DosType);
        p.WriteUInt32(file, 96, cursor.NextTable);
        return actual;
    }

    public static int ReadSfs2<T>(ref T p, APTR file, uint node, ulong position,
        APTR destination, uint length, APTR metadata, APTR data, out DOS.Error error)
        where T : struct, IAmigaFileSystemPlatform
    {
        if (file.IsNull) return ReadSfs2WithoutFile(ref p, node, position,
            destination, length, metadata, data, out error);

        // A whole packet inside the validated extent consumes no traversal state.
        // Use the normal block reader so overlays, read-ahead and MaxTransfer apply.
        if (length >= 512 && length <= int.MaxValue && ((length | (uint)position) & 511) == 0 &&
            (destination.Raw & 3) == 0 && p.BlockBytes == 512 && node != 0 && p.ReadUInt32(file, 56) == node &&
            Sfs2ExtentContains(ref p, file, position, length, out var diskBlock) &&
            Disjoint(destination, length, file, 216) &&
            Sfs2ReadBuffers(ref p, destination, length, metadata, data))
        {
            var blocks = length >> 9;
            var read = length <= 4096 ? p.ReadDataBlocks(diskBlock, blocks, destination)
                : p.ReadDataBlocks(diskBlock, blocks, destination, false);
            if (read == blocks) { error = DOS.Error.None; return (int)length; }
            p.WriteUInt32(file, 56, 0);
            error = DOS.Error.DiskNotValidated;
            return read == 0 || read > blocks ? -1 : (int)(read << 9);
        }

        return ReadSfs2UnalignedOrMiss(ref p, file, node, position, destination, length, metadata, data, out error);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int ReadSfs2WithoutFile<T>(ref T p, uint node, ulong position,
        APTR destination, uint length, APTR metadata, APTR data, out DOS.Error error)
        where T : struct, IAmigaFileSystemPlatform =>
        AmigaSmartFileSystemReadOnlyCore.Read(ref p, node, position, destination, length, metadata, data, out error);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int ReadSfs2UnalignedOrMiss<T>(ref T p, APTR file, uint node, ulong position,
        APTR destination, uint length, APTR metadata, APTR data, out DOS.Error error)
        where T : struct, IAmigaFileSystemPlatform
    {
        // A sub-sector packet inside the saved extent does not advance the
        // traversal cursor. Keep EOF tails, sector crossings and private
        // buffer aliases on the reader that restores the full cursor.
        var within = (uint)position & 511;
        if (length != 0 && length < 512 && length <= 512 - within && p.BlockBytes == 512 &&
            node != 0 && p.ReadUInt32(file, 56) == node &&
            Sfs2ExtentContains(ref p, file, position, length, out var block) &&
            Disjoint(destination, length, file, 216) &&
            Sfs2ReadBuffers(ref p, destination, length, metadata, data))
        {
            var image = p.ReadRetainedBlock(block);
            if (image.IsNull || !Disjoint(destination, length, image, 512))
            {
                if (!p.ReadBlock(block, data))
                {
                    p.WriteUInt32(file, 56, 0);
                    error = DOS.Error.DiskNotValidated;
                    return -1;
                }
                image = data;
            }
            p.Copy(APTR.FromPointer(image.Raw + within), destination, length);
            error = DOS.Error.None;
            return (int)length;
        }
        return ReadSfs2Cursor(ref p, file, node, position, destination, length, metadata, data, out error);
    }

    private static bool Sfs2ReadBuffers<T>(ref T p, APTR destination, uint length, APTR metadata, APTR data)
        where T : struct, IAmigaFileSystemPlatform
    {
        // Native metadata and data scratch are consecutive sectors. Check their
        // union for overlap, then try one memory range for both mapped buffers.
        if (metadata.Raw <= uint.MaxValue - 1024 && data.Raw == metadata.Raw + 512)
        {
            if (!Disjoint(destination, length, metadata, 1024)) return false;
            if (destination.Raw <= data.Raw)
            {
                if (p.IsMapped(destination, data.Raw + 512 - destination.Raw)) return true;
            }
            else if (length <= uint.MaxValue - destination.Raw &&
                p.IsMapped(data, destination.Raw - data.Raw + length)) return true;
        }
        else if (!Disjoint(destination, length, metadata, 512) || !Disjoint(destination, length, data, 512))
            return false;
        return p.IsMapped(destination, length) && p.IsMapped(data, 512);
    }

    private static bool Sfs2ExtentContains<T>(ref T p, APTR file, ulong position, uint length, out uint diskBlock)
        where T : struct, IAmigaFileSystemPlatform
    {
        diskBlock = 0;
        var high = (uint)(position >> 32);
        if (high >= 512) return false; // The logical block key is 32 bits.
        var low = (uint)position;
        var end = unchecked(low + length);
        var endHigh = end < low ? high + 1 : high;
        var sizeHigh = p.ReadUInt32(file, 68);
        if (endHigh > sizeHigh || (endHigh == sizeHigh && end > p.ReadUInt32(file, 64))) return false;
        var logical = (high << 23) | (low >> 9);
        var start = p.ReadUInt32(file, 84);
        var count = p.ReadUInt32(file, 96);
        var offset = logical - start;
        // The strict offset checks below cover the starting sector even for
        // a sub-sector read. Whole-sector runs must also fit through the end.
        var blocks = length >> 9;
        if (logical < start || offset >= count || blocks > count - offset) return false;
        var extent = p.ReadUInt32(file, 80);
        var limit = p.BlockCount - 1; // The backup root is outside the data area.
        diskBlock = unchecked(extent + offset);
        return diskBlock >= extent && diskBlock < limit && blocks <= limit - diskBlock;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static int ReadSfs2Cursor<T>(ref T p, APTR file, uint node, ulong position,
        APTR destination, uint length, APTR metadata, APTR data, out DOS.Error error,
        AmigaSmartFileSystemReadOnlyCore.ReadStart start = default)
        where T : struct, IAmigaFileSystemPlatform
    {
        var cursor = new AmigaSmartFileSystemReadOnlyCore.ReadCursor
        {
            Node = p.ReadUInt32(file, 56), Format = (AmigaSmartFileSystemFormat)p.ReadUInt32(file, 60),
            Size = ((ulong)p.ReadUInt32(file, 68) << 32) | p.ReadUInt32(file, 64),
            First = p.ReadUInt32(file, 72), Tree = p.ReadUInt32(file, 76), Extent = p.ReadUInt32(file, 80),
            Start = p.ReadUInt32(file, 84), Count = p.ReadUInt32(file, 96),
            Next = p.ReadUInt32(file, 100), Previous = p.ReadUInt32(file, 104)
        };
        var actual = AmigaSmartFileSystemReadOnlyCore.Read(ref p, ref cursor, node, position,
            destination, length, metadata, data, start, out error);
        p.WriteUInt32(file, 56, cursor.Node); p.WriteUInt32(file, 60, (uint)cursor.Format);
        p.WriteUInt32(file, 64, (uint)cursor.Size); p.WriteUInt32(file, 68, (uint)(cursor.Size >> 32));
        p.WriteUInt32(file, 72, cursor.First); p.WriteUInt32(file, 76, cursor.Tree);
        p.WriteUInt32(file, 80, cursor.Extent); p.WriteUInt32(file, 84, cursor.Start);
        p.WriteUInt32(file, 96, cursor.Count); p.WriteUInt32(file, 100, cursor.Next);
        p.WriteUInt32(file, 104, cursor.Previous);
        return actual;
    }
}
