using Amiga;

namespace CopperSFS;

/// <summary>Caller-owned, single-owner automatic maintenance scratch. Input ranges
/// are hints; output ranges expire on any intervening mount operation. No borrowed
/// cache pointers or authorization survive a yield to packet dispatch.</summary>
public static class AmigaAutomaticTrimBatch
{
    public const uint Bytes = 512, Capacity = 8;
    public const int Mount = 0, Generation = 4, Stage = 8, Inputs = 12,
        Outputs = 16, OutputBlocks = 20, Maximum = 24, Granularity = 28,
        Alignment = 32, Descriptors = 36, MapCount = 40,
        WalkBlock = 44, WalkIndex = 48, WalkOrdinal = 52, WalkGuard = 56, RemainderCount = 60,
        Input = 64, Output = 128, Maps = 192, Remainders = 384,
        PreparedRoots = 448, RootsValid = 468, MapSeen = 472, FfsRootMap = 476;
    // Up to sixteen bitmap pages can intersect eight ranges totalling 4096
    // blocks at the minimum supported filesystem block size. Each record is
    // logical index, physical key and its allocation-bitmap key. MapSeen
    // separately records exact pointer occurrences during FFS validation.
    public const uint MapCapacity = 16;

    public static void Begin<T>(ref T p, APTR batch, uint mount, uint generation,
        AmigaFileSystemDiscardLimits limits, uint descriptors) where T : struct, IAmigaGuestMemory
    {
        p.Clear(batch, Bytes);
        p.WriteUInt32(batch, Mount, mount); p.WriteUInt32(batch, Generation, generation);
        p.WriteUInt32(batch, Maximum, limits.MaximumBlocks);
        p.WriteUInt32(batch, Granularity, limits.Granularity);
        p.WriteUInt32(batch, Alignment, limits.Alignment);
        p.WriteUInt32(batch, Descriptors, descriptors < Capacity ? descriptors : Capacity);
    }

    public static bool Current<T>(ref T p, APTR batch, uint mount, uint generation)
        where T : struct, IAmigaGuestMemory => p.IsMapped(batch, Bytes) &&
        p.ReadUInt32(batch, Mount) == mount && p.ReadUInt32(batch, Generation) == generation;

    public static bool ValidInputs<T>(ref T p, APTR batch, uint lower, uint upper) where T : struct, IAmigaGuestMemory
    {
        var count = p.ReadUInt32(batch, Inputs);
        if (count > Capacity) return false;
        uint total = 0, end = lower;
        for (var i = 0u; i < count; i++)
        {
            var first = p.ReadUInt32(batch, Input + (int)i * 8);
            var length = p.ReadUInt32(batch, Input + (int)i * 8 + 4);
            if (first < end || first >= upper || length == 0 || length > upper - first || length > 4096 - total) return false;
            total += length; end = first + length;
        }
        return true;
    }

    public static void Sort<T>(ref T p, APTR batch) where T : struct, IAmigaGuestMemory
    {
        var count = p.ReadUInt32(batch, Inputs);
        for (var i = 1u; i < count; i++)
        {
            var first = p.ReadUInt32(batch, Input + (int)i * 8);
            var length = p.ReadUInt32(batch, Input + (int)i * 8 + 4);
            var j = i;
            while (j != 0 && p.ReadUInt32(batch, Input + (int)(j - 1) * 8) > first)
            {
                p.WriteUInt32(batch, Input + (int)j * 8, p.ReadUInt32(batch, Input + (int)(j - 1) * 8));
                p.WriteUInt32(batch, Input + (int)j * 8 + 4, p.ReadUInt32(batch, Input + (int)(j - 1) * 8 + 4));
                j--;
            }
            p.WriteUInt32(batch, Input + (int)j * 8, first);
            p.WriteUInt32(batch, Input + (int)j * 8 + 4, length);
        }
        uint used = 0;
        for (var i = 0u; i < count; i++)
        {
            var first = p.ReadUInt32(batch, Input + (int)i * 8);
            var length = p.ReadUInt32(batch, Input + (int)i * 8 + 4);
            if (length == 0 || first > uint.MaxValue - length) continue;
            if (used != 0)
            {
                var at = Input + (int)(used - 1) * 8;
                var start = p.ReadUInt32(batch, at); var end = start + p.ReadUInt32(batch, at + 4);
                if (first <= end)
                {
                    if (first + length > end) p.WriteUInt32(batch, at + 4, first + length - start);
                    continue;
                }
            }
            p.WriteUInt32(batch, Input + (int)used * 8, first);
            p.WriteUInt32(batch, Input + (int)used * 8 + 4, length); used++;
        }
        p.WriteUInt32(batch, Inputs, used);
    }

    public static bool Contains<T>(ref T p, APTR batch, uint block) where T : struct, IAmigaGuestMemory
    {
        for (var i = 0u; i < p.ReadUInt32(batch, Inputs); i++)
        {
            var first = p.ReadUInt32(batch, Input + (int)i * 8);
            if (block >= first && block - first < p.ReadUInt32(batch, Input + (int)i * 8 + 4)) return true;
        }
        return false;
    }

    /// <summary>Scan only the requested words in a validated allocation page.
    /// Preserve a run across page boundaries. All-free words take one step.</summary>
    public static void ScanBitmap<T>(ref T p, APTR batch, APTR bitmap, int data,
        uint pageFirst, uint first, uint count, bool msb, ref uint run, ref uint length)
        where T : struct, IAmigaGuestMemory
    {
        var end = first + count;
        while (first < end)
        {
            var bit = first - pageFirst;
            var shift = bit & 31;
            var take = 32 - shift;
            if (take > end - first) take = end - first;
            var word = p.ReadUInt32(bitmap, data + (int)(bit >> 5) * 4);
            var mask = take == 32 ? uint.MaxValue : (1u << (int)take) - 1;
            var selected = msb ? word >> (int)(32 - shift - take) : word >> (int)shift;
            selected &= mask;
            if (selected == mask)
            {
                if (length == 0) run = first;
                length += take;
            }
            else if (selected == 0)
            {
                if (length != 0) { FreeRun(ref p, batch, run, length); length = 0; }
            }
            else
            {
                for (var i = 0u; i < take; i++)
                {
                    var free = (selected & (1u << (int)(msb ? take - 1 - i : i))) != 0;
                    if (free) { if (length == 0) run = first + i; length++; }
                    else if (length != 0) { FreeRun(ref p, batch, run, length); length = 0; }
                }
            }
            first += take;
        }
    }

    /// <summary>Append only the inward-aligned part of a proven free run. Excess
    /// fragmentation/budget is deliberately dropped: this is best-effort work.</summary>
    public static void FreeRun<T>(ref T p, APTR batch, uint first, uint count) where T : struct, IAmigaGuestMemory
    {
        var granularity = p.ReadUInt32(batch, Granularity);
        var alignment = p.ReadUInt32(batch, Alignment);
        var used = p.ReadUInt32(batch, Outputs);
        var maximum = p.ReadUInt32(batch, Maximum);
        var blocks = p.ReadUInt32(batch, OutputBlocks);
        if (granularity == 0 || alignment >= granularity) return;
        var remainder = first % granularity;
        var skip = remainder <= alignment ? alignment - remainder : granularity - (remainder - alignment);
        if (skip >= count) return;
        first += skip; count -= skip;
        count -= count % granularity;
        if (count == 0) return;
        if (blocks >= maximum || used >= p.ReadUInt32(batch, Descriptors) || used >= Capacity)
        { KeepHint(ref p, batch, first, count); return; }
        var take = count;
        if (take > maximum - blocks) take = maximum - blocks;
        take -= take % granularity;
        if (take < count) KeepHint(ref p, batch, first + take, count - take);
        if (take == 0) return;
        p.WriteUInt32(batch, Output + (int)used * 8, first);
        p.WriteUInt32(batch, Output + (int)used * 8 + 4, take);
        p.WriteUInt32(batch, Outputs, used + 1); p.WriteUInt32(batch, OutputBlocks, blocks + take);
    }

    private static void KeepHint<T>(ref T p, APTR batch, uint first, uint count) where T : struct, IAmigaGuestMemory
    {
        var used = p.ReadUInt32(batch, RemainderCount);
        if (used >= Capacity) return;
        p.WriteUInt32(batch, Remainders + (int)used * 8, first);
        p.WriteUInt32(batch, Remainders + (int)used * 8 + 4, count);
        p.WriteUInt32(batch, RemainderCount, used + 1);
    }
}
