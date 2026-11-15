using Amiga;

namespace CopperSFS;

/// <summary>Caller-owned, single-owner, volatile hints. Overflow drops hints;
/// it must never force a commit or alter a filesystem operation's result.</summary>
public static class AmigaFreedRangeQueue
{
    public const uint Capacity = 128, Bytes = 4 + Capacity * 8;

    public static uint Count<T>(ref T p, APTR queue) where T : struct, IAmigaGuestMemory =>
        queue.IsNull ? 0 : p.ReadUInt32(queue);

    public static void Clear<T>(ref T p, APTR queue) where T : struct, IAmigaGuestMemory
    { if (queue.IsNotNull) p.WriteUInt32(queue, 0, 0); }

    public static bool Add<T>(ref T p, APTR queue, uint first, uint count) where T : struct, IAmigaGuestMemory
    {
        if (queue.IsNull || count == 0 || first > uint.MaxValue - count) return false;
        var used = Count(ref p, queue);
        if (used > Capacity) { Clear(ref p, queue); return false; }
        var end = first + count;
        // Append-order frees usually extend the last run.
        if (used != 0)
        {
            var at = 4 + (int)(used - 1) * 8;
            var start = p.ReadUInt32(queue, at); var length = p.ReadUInt32(queue, at + 4);
            if (start > uint.MaxValue - length) { Clear(ref p, queue); return false; }
            var finish = start + length;
            if (first <= finish && start <= end)
            {
                if (start < first) first = start;
                if (finish > end) end = finish;
                p.WriteUInt32(queue, at, first);
                p.WriteUInt32(queue, at + 4, end - first);
                return true;
            }
        }
        if (used == Capacity) return false;
        p.WriteUInt32(queue, 4 + (int)used * 8, first);
        p.WriteUInt32(queue, 8 + (int)used * 8, end - first);
        p.WriteUInt32(queue, 0, used + 1);
        return true;
    }

    /// <summary>Remove a bounded chunk. Overlap preserves alignment opportunities
    /// across chunk boundaries. The receiver still revalidates every hinted block.</summary>
    public static bool Take<T>(ref T p, APTR queue, uint maximum, uint overlap,
        out uint first, out uint count) where T : struct, IAmigaGuestMemory
    {
        first = count = 0; var used = Count(ref p, queue);
        if (used == 0 || used > Capacity || maximum == 0 || overlap >= maximum) return false;
        first = p.ReadUInt32(queue, 4); count = p.ReadUInt32(queue, 8);
        if (count == 0 || first > uint.MaxValue - count) { Clear(ref p, queue); return false; }
        if (count > maximum)
        {
            var step = maximum - overlap;
            p.WriteUInt32(queue, 4, first + step); p.WriteUInt32(queue, 8, count - step);
            count = maximum;
        }
        else
        {
            used--;
            p.WriteUInt32(queue, 4, p.ReadUInt32(queue, 4 + (int)used * 8));
            p.WriteUInt32(queue, 8, p.ReadUInt32(queue, 8 + (int)used * 8));
            p.WriteUInt32(queue, 0, used);
        }
        return true;
    }
}
