using Amiga;

namespace CopperSFS;

/// <summary>Allocation-free planning of original SFS LONGCOMPRESSION records.</summary>
public static class AmigaSmartFileSystemTrstCodec
{
    // A mount-owned proof, never disk input: clear bits certify identical
    // 16-byte regions of a 512-byte before/final pair. The resulting wire
    // codes are exactly those of the full planner, including 64-word splits.
    internal static uint PlanSparse512<T>(ref T p, APTR oldImage, APTR finalImage,
        uint changed, APTR plan, out uint length) where T : struct, IAmigaGuestMemory
    {
        uint word = 0, runs = 0; length = 0;
        var kind = SparseKind(ref p, oldImage, finalImage, word, changed);
        while (word < 128)
        {
            var current = kind; var count = 1u; word++;
            while (word < 128 && count < 64)
            {
                if (current == 1 && (changed & (1u << (int)(word >> 2))) == 0)
                {
                    var take = 4 - (word & 3);
                    if (take > 64 - count) take = 64 - count;
                    word += take; count += take; continue;
                }
                kind = SparseKind(ref p, oldImage, finalImage, word, changed);
                if (kind != current) break;
                word++; count++;
            }
            p.WriteUInt8(plan, (int)runs++, (byte)((current << 6) | (count - 1)));
            length += current == 3 ? 1 + count * 4 : 1;
            if (word < 128 && count == 64) kind = SparseKind(ref p, oldImage, finalImage, word, changed);
        }
        return runs;
    }

    private static uint SparseKind<T>(ref T p, APTR oldImage, APTR finalImage, uint word, uint changed)
        where T : struct, IAmigaGuestMemory
    {
        if ((changed & (1u << (int)(word >> 2))) == 0) return 1;
        var value = p.ReadUInt32(APTR.FromPointer(finalImage.Raw + word * 4));
        var old = p.ReadUInt32(APTR.FromPointer(oldImage.Raw + word * 4));
        return value == old ? 1u : value == 0 ? 0u : value == uint.MaxValue ? 2u : 3u;
    }

    /// <summary>
    /// Classifies each old/final longword once. Writes one code per run to plan
    /// (at most blockBytes / 4 bytes) and returns the run count and payload size.
    /// Inputs and plan must be mapped, disjoint buffers; blockBytes is a supported
    /// SFS block size. Literal bytes remain in finalImage until journal emission.
    /// </summary>
    public static uint Plan<T>(ref T p, APTR oldImage, APTR finalImage, uint blockBytes,
        APTR plan, out uint length) where T : struct, IAmigaGuestMemory
    {
        var oldAt = oldImage.Raw; var finalAt = finalImage.Raw;
        var remaining = blockBytes >> 2;
        uint runs = 0;
        length = 0;
        if (remaining == 0) return 0;
        var value = p.ReadUInt32(APTR.FromPointer(finalAt));
        var old = p.ReadUInt32(APTR.FromPointer(oldAt));
        while (remaining != 0)
        {
            var kind = value == old ? 1u : value == 0 ? 0u : value == uint.MaxValue ? 2u : 3u;
            var limit = remaining < 64 ? remaining : 64;
            var count = 1u;
            // Dispatch once per run. Most metadata words are unchanged; that
            // loop needs one comparison instead of reclassifying every word.
            // The first word of the following run stays in value/old, so each
            // input word is still read exactly once, including alternating runs.
            if (kind == 1)
            {
                // Four guarded pairs share one loop-control comparison.
                // Advance the run count and pointers once per complete batch.
                // A mismatch carries the exact first word of the next run.
                while (limit - count >= 4)
                {
                    {
                        var nextValue = p.ReadUInt32(APTR.FromPointer(finalAt + 4));
                        var nextOld = p.ReadUInt32(APTR.FromPointer(oldAt + 4));
                        if (nextValue != nextOld)
                        { oldAt += 4; finalAt += 4; value = nextValue; old = nextOld; goto UnchangedDone; }
                    }
                    {
                        var nextValue = p.ReadUInt32(APTR.FromPointer(finalAt + 8));
                        var nextOld = p.ReadUInt32(APTR.FromPointer(oldAt + 8));
                        if (nextValue != nextOld)
                        { count++; oldAt += 8; finalAt += 8; value = nextValue; old = nextOld; goto UnchangedDone; }
                    }
                    {
                        var nextValue = p.ReadUInt32(APTR.FromPointer(finalAt + 12));
                        var nextOld = p.ReadUInt32(APTR.FromPointer(oldAt + 12));
                        if (nextValue != nextOld)
                        { count += 2; oldAt += 12; finalAt += 12; value = nextValue; old = nextOld; goto UnchangedDone; }
                    }
                    {
                        var nextValue = p.ReadUInt32(APTR.FromPointer(finalAt + 16));
                        var nextOld = p.ReadUInt32(APTR.FromPointer(oldAt + 16));
                        if (nextValue != nextOld)
                        { count += 3; oldAt += 16; finalAt += 16; value = nextValue; old = nextOld; goto UnchangedDone; }
                    }
                    oldAt += 16; finalAt += 16; count += 4;
                }
                while (count < limit)
                {
                    oldAt += 4; finalAt += 4;
                    var nextValue = p.ReadUInt32(APTR.FromPointer(finalAt));
                    var nextOld = p.ReadUInt32(APTR.FromPointer(oldAt));
                    if (nextValue != nextOld) { value = nextValue; old = nextOld; break; }
                    count++;
                }
            UnchangedDone:;
            }
            else if (kind == 0)
            {
                while (count < limit)
                {
                    oldAt += 4; finalAt += 4;
                    var nextValue = p.ReadUInt32(APTR.FromPointer(finalAt));
                    var nextOld = p.ReadUInt32(APTR.FromPointer(oldAt));
                    if (nextValue != 0 || nextOld == 0) { value = nextValue; old = nextOld; break; }
                    count++;
                }
            }
            else if (kind == 2)
            {
                while (count < limit)
                {
                    oldAt += 4; finalAt += 4;
                    var nextValue = p.ReadUInt32(APTR.FromPointer(finalAt));
                    var nextOld = p.ReadUInt32(APTR.FromPointer(oldAt));
                    if (nextValue != uint.MaxValue || nextOld == uint.MaxValue)
                    { value = nextValue; old = nextOld; break; }
                    count++;
                }
            }
            else
            {
                while (count < limit)
                {
                    oldAt += 4; finalAt += 4;
                    var nextValue = p.ReadUInt32(APTR.FromPointer(finalAt));
                    var nextOld = p.ReadUInt32(APTR.FromPointer(oldAt));
                    if (nextValue == nextOld || nextValue == 0 || nextValue == uint.MaxValue)
                    { value = nextValue; old = nextOld; break; }
                    count++;
                }
            }
            p.WriteUInt8(plan, (int)runs++, (byte)((kind << 6) | (count - 1)));
            length += kind == 3 ? 1 + count * 4 : 1;
            remaining -= count;
            if (remaining != 0 && count == limit)
            {
                oldAt += 4; finalAt += 4;
                value = p.ReadUInt32(APTR.FromPointer(finalAt));
                old = p.ReadUInt32(APTR.FromPointer(oldAt));
            }
        }
        return runs;
    }
}
