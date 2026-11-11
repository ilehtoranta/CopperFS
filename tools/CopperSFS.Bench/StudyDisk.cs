using System.Buffers.Binary;
using Amiga;

namespace CopperSFS.Bench;

internal sealed class StudyPowerCut : Exception;
internal sealed record IoCounts(long BlockReads, long BlockWrites, long Flushes,
    long DeviceReads, long DeviceReadBytes, long DeviceWriteBytes, long CacheHits,
    long DeviceWrites, long RangeReads, long RangeWrites, uint MaxRangeBlocks);

// In-memory device, not a storage speed emulator. The optional read-through LRU
// cache is experimental host-adapter code, not a change to the filesystem core.
internal partial struct StudyDisk : IAmigaBlockRangeFileSystemPlatform
{
    private readonly byte[] device;
    private readonly int bytes;
    private readonly bool crashModel;
    private readonly Dictionary<uint, List<byte[]>> pending;
    private readonly CacheLine[] cache;
    private readonly int transferBytes;
    private long clock, reads, writes, flushes, deviceReads, deviceReadBytes, hits;
    private long deviceWrites, rangeReads, rangeWrites;
    private uint maxRangeBlocks;
    internal int Events, CutAfterEvent, TearAtWrite, TearBytes, FailAtEvent, FailAtRead;
    internal bool BatchedWrites, ScalarRanges, PersistentFailure, ReadOnly;
    internal bool RelocateFragmented = true;
    public APTR RedoOverlay { get; set; }
    internal bool DiscardSupported = true;
    internal APTR TrimCertificate;
    internal int Discarded, FailDiscardAt;
    internal AmigaFileSystemDiscardLimits DiscardLimits = new() { MaximumBlocks = 32, Granularity = 1 };
    internal List<(uint Block, uint Count)> DiscardRequests = new();
    internal bool ReserveOverwrites = true;
    internal uint BatchBytes = 65536;
    internal readonly byte[] Ram;
    public AmigaVolumeGeometry Geometry { get; }
    private sealed class CacheLine(int length)
    {
        internal int Start = -1;
        internal long Used;
        internal readonly byte[] Data = new byte[length];
    }
    internal StudyDisk(byte[] image, int blockBytes, bool crashModel = false, int cacheBytes = 0, int readAheadBytes = 0,
        bool batchedWrites = false, uint maxTransfer = 0x7FFFFFFF, uint reservedBlocks = 1)
    {
        bytes = blockBytes; this.crashModel = crashModel;
        device = new byte[image.Length + 2 * bytes];
        device.AsSpan(0, bytes).Fill(0xA5); device.AsSpan(device.Length - bytes, bytes).Fill(0xA5);
        image.CopyTo(device, bytes);
        AmigaVolumeGeometry.Create((uint)(image.Length / bytes), (uint)bytes, reservedBlocks, out var geometry);
        geometry.MaxTransfer = maxTransfer; Geometry = geometry;
        BatchedWrites = batchedWrites;
        Ram = new byte[6 * bytes + 1024 * 1024 + 512 + 65536 + AmigaSmartFileSystemWriteCore.OverwriteReservationBytes];
        pending = new();
        transferBytes = readAheadBytes == 0 ? bytes : readAheadBytes;
        if (cacheBytes < 0 || transferBytes < bytes || transferBytes % bytes != 0 || cacheBytes % transferBytes != 0 ||
            (crashModel && cacheBytes != 0)) throw new ArgumentOutOfRangeException(nameof(cacheBytes));
        cache = new CacheLine[cacheBytes / transferBytes];
        for (var i = 0; i < cache.Length; i++) cache[i] = new CacheLine(transferBytes);
    }
    internal readonly AmigaSmartFileSystemWriteCore.Work Work => new()
    {
        Reservations = ReserveOverwrites ? APTR.FromPointer((uint)(Ram.Length - AmigaSmartFileSystemWriteCore.OverwriteReservationBytes)) : APTR.Null,
        Meta = APTR.FromPointer((uint)bytes), Data = APTR.FromPointer((uint)(2 * bytes)),
        Bitmap = APTR.FromPointer((uint)(3 * bytes)), Journal = APTR.FromPointer((uint)(4 * bytes))
    };
    internal readonly APTR NameBuffer => APTR.FromPointer((uint)(5 * bytes));
    internal readonly APTR Payload => APTR.FromPointer((uint)(6 * bytes));
    internal readonly APTR BatchBuffer => APTR.FromPointer((uint)(6 * bytes + 1024 * 1024 + 512));
    internal readonly IoCounts Counts => new(reads, writes, flushes, deviceReads, deviceReadBytes, writes * bytes, hits,
        deviceWrites, rangeReads, rangeWrites, maxRangeBlocks);
    private readonly bool Failed(long count, int at) => at > 0 && (PersistentFailure ? count >= at : count == at);
    public readonly bool WriteProtected() => ReadOnly;
    private readonly void CheckBlock(uint block)
    {
        if (block >= Geometry.BlockCount) throw new InvalidOperationException($"Out-of-range block {block}");
    }
    private readonly void VisibleBlock(uint block, Span<byte> output)
    {
        if (pending.TryGetValue(block, out var versions)) versions[^1].CopyTo(output);
        else device.AsSpan(bytes + checked((int)block * bytes), bytes).CopyTo(output);
    }
    public bool ReadBlock(uint block, APTR destination)
    {
        if (RedoOverlay.IsNotNull)
        {
            if (!AmigaSmartFileSystemRedoArena.TryFind(ref this, RedoOverlay, block, out var view)) return false;
            if (view.IsNotNull) { Copy(view, destination, Geometry.BlockBytes); return true; }
        }
        CheckBlock(block); reads++;
        if (Failed(reads, FailAtRead)) return false;
        if (!IsMapped(destination, (uint)bytes)) return false;
        var output = Ram.AsSpan((int)destination.Raw, bytes);
        if (cache.Length == 0)
        {
            deviceReads++; deviceReadBytes += bytes;
            VisibleBlock(block, output);
            return true;
        }
        var offset = checked((int)block * bytes);
        var start = offset / transferBytes * transferBytes;
        CacheLine? found = null;
        foreach (var line in cache) if (line.Start == start) { found = line; break; }
        if (found is null)
        {
            found = cache[0];
            foreach (var line in cache) if (line.Used < found.Used) found = line;
            var length = Math.Min(transferBytes, device.Length - 2 * bytes - start);
            device.AsSpan(bytes + start, length).CopyTo(found.Data);
            deviceReads++; deviceReadBytes += length;
            found.Start = start;
        }
        else hits++;
        found.Used = ++clock;
        found.Data.AsSpan(offset - start, bytes).CopyTo(output);
        return true;
    }
    public bool WriteBlock(uint block, APTR source)
    {
        deviceWrites++;
        return WriteOne(block, source);
    }
    private bool WriteOne(uint block, APTR source)
    {
        if (TrimCertificate.IsNotNull) WriteUInt32(TrimCertificate, 0, 0);
        if (RedoOverlay.IsNotNull) return AmigaSmartFileSystemRedoArena.Store(ref this, RedoOverlay, block, source);
        CheckBlock(block);
        if (block < Geometry.ReservedBlocks || block >= Geometry.BlockCount - 1)
            throw new InvalidOperationException($"Write to reserved block {block}");
        if (!IsMapped(source, (uint)bytes)) return false;
        writes++; Events++;
        if (Failed(Events, FailAtEvent)) return false;
        var value = Ram.AsSpan((int)source.Raw, bytes);
        var offset = checked((int)block * bytes);
        if (writes == TearAtWrite)
        {
            value[..TearBytes].CopyTo(device.AsSpan(bytes + offset));
            throw new StudyPowerCut();
        }
        if (crashModel)
        {
            if (!pending.TryGetValue(block, out var versions)) pending.Add(block, versions = new());
            versions.Add(value.ToArray());
        }
        else value.CopyTo(device.AsSpan(bytes + offset));
        foreach (var line in cache)
            if (line.Start >= 0 && offset >= line.Start && offset - line.Start < transferBytes)
                value.CopyTo(line.Data.AsSpan(offset - line.Start));
        if (Events == CutAfterEvent) throw new StudyPowerCut();
        return true;
    }
    public bool Flush()
    {
        if (RedoOverlay.IsNotNull) return true;
        flushes++; Events++;
        if (Failed(Events, FailAtEvent)) return false;
        foreach (var (block, versions) in pending) versions[^1].CopyTo(device, bytes + (int)block * bytes);
        pending.Clear();
        if (Events == CutAfterEvent) throw new StudyPowerCut();
        return true;
    }
    private void CheckRange(uint first, uint count, APTR buffer)
    {
        if (count == 0 || (ulong)first + count > Geometry.BlockCount ||
            (ulong)count * (uint)bytes > Geometry.MaxTransfer || !IsMapped(buffer, count * (uint)bytes))
            throw new InvalidOperationException("Invalid range or MaxTransfer exceeded");
        maxRangeBlocks = Math.Max(maxRangeBlocks, count);
    }
    public bool ReadBlocks(uint first, uint count, APTR destination)
    {
        CheckRange(first, count, destination); rangeReads++;
        if (ScalarRanges)
        {
            for (uint i = 0; i < count; i++)
                if (!ReadBlock(first + i, APTR.FromPointer(destination.Raw + i * (uint)bytes))) return false;
            return true;
        }
        deviceReads++; deviceReadBytes += count * bytes;
        for (uint i = 0; i < count; i++)
        {
            if (Failed(++reads, FailAtRead)) return false;
            VisibleBlock(first + i, Ram.AsSpan((int)destination.Raw + (int)i * bytes, bytes));
        }
        return true;
    }
    public bool WriteBlocks(uint first, uint count, APTR source)
    {
        CheckRange(first, count, source); rangeWrites++;
        if (!ScalarRanges) deviceWrites++;
        for (uint i = 0; i < count; i++)
        {
            var pointer = APTR.FromPointer(source.Raw + i * (uint)bytes);
            // Every individual sector remains a cut/failure point. No test
            // assumes that one range request is an atomic device write.
            if (!(ScalarRanges ? WriteBlock(first + i, pointer) : WriteOne(first + i, pointer))) return false;
        }
        return true;
    }
    internal readonly IEnumerable<byte[]> SampledCrashImages()
    {
        var blocks = pending.OrderBy(p => p.Key).ToArray();
        // Deliberately bounded sampling for full-size batches: none, all,
        // alternating sectors, first/second halves, and four seeded subsets.
        for (var sample = 0; sample < 10; sample++)
        {
            var image = Snapshot(); var random = new Random(104729 + sample);
            for (var i = 0; i < blocks.Length; i++)
            {
                var (block, versions) = blocks[i];
                var choice = sample switch
                {
                    0 => 0, 1 => versions.Count, 2 => i % 2 == 0 ? versions.Count : 0,
                    3 => i % 2 != 0 ? versions.Count : 0, 4 => i < blocks.Length / 2 ? versions.Count : 0,
                    5 => i >= blocks.Length / 2 ? versions.Count : 0, _ => random.Next(versions.Count + 1)
                };
                if (choice > 0) versions[choice - 1].CopyTo(image, (int)block * bytes);
            }
            yield return image;
        }
    }
    internal readonly byte[] Snapshot()
    {
        if (device.AsSpan(0, bytes).ContainsAnyExcept((byte)0xA5) ||
            device.AsSpan(device.Length - bytes, bytes).ContainsAnyExcept((byte)0xA5))
            throw new InvalidOperationException("Partition guard overwritten");
        return device.AsSpan(bytes, device.Length - 2 * bytes).ToArray();
    }
    internal readonly IEnumerable<byte[]> CrashImages()
    {
        var blocks = pending.ToArray();
        long combinations = 1;
        foreach (var pair in blocks) combinations = checked(combinations * (pair.Value.Count + 1));
        if (combinations > 65536) throw new InvalidOperationException($"Crash state limit exceeded: {combinations}");
        for (long selection = 0; selection < combinations; selection++)
        {
            var image = Snapshot(); var digits = selection;
            foreach (var (block, versions) in blocks)
            {
                var choice = (int)(digits % (versions.Count + 1)); digits /= versions.Count + 1;
                if (choice != 0) versions[choice - 1].CopyTo(image, (int)block * bytes);
            }
            yield return image;
        }
    }
    // Lets additional tests explicitly report exhaustive versus sampled spaces
    // without weakening CrashImages' guard or overflowing on large data ranges.
    internal readonly bool CrashSpaceFits(int maximum)
    {
        long combinations = 1;
        foreach (var versions in pending.Values)
        {
            if (combinations > maximum / (versions.Count + 1)) return false;
            combinations *= versions.Count + 1;
        }
        return true;
    }
    public readonly byte ReadUInt8(APTR a, int offset = 0) => Ram[(int)a.Raw + offset];
    public readonly ushort ReadUInt16(APTR a, int offset = 0) => BinaryPrimitives.ReadUInt16BigEndian(Ram.AsSpan((int)a.Raw + offset));
    public readonly uint ReadUInt32(APTR a, int offset = 0) => BinaryPrimitives.ReadUInt32BigEndian(Ram.AsSpan((int)a.Raw + offset));
    public readonly void WriteUInt8(APTR a, int offset, byte value) => Ram[(int)a.Raw + offset] = value;
    public readonly void WriteUInt16(APTR a, int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(Ram.AsSpan((int)a.Raw + offset), value);
    public readonly void WriteUInt32(APTR a, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(Ram.AsSpan((int)a.Raw + offset), value);
    public readonly bool IsMapped(APTR a, uint length) => a.Raw <= Ram.Length && length <= Ram.Length - a.Raw;
    public readonly void Clear(APTR a, uint length) => Ram.AsSpan((int)a.Raw, (int)length).Clear();
    public readonly void Copy(APTR from, APTR to, uint length) => Ram.AsSpan((int)from.Raw, (int)length).CopyTo(Ram.AsSpan((int)to.Raw));
}
