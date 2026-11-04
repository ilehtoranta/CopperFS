using Amiga;

namespace CopperSFS;

/// <summary>Allocation-free planning of original SFS LONGCOMPRESSION records.</summary>
public static class AmigaSmartFileSystemTrstCodec
{
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
        uint runs = 0, count = 0, kind = 4;
        length = 0;
        while (remaining != 0)
        {
            var value = p.ReadUInt32(APTR.FromPointer(finalAt));
            var old = p.ReadUInt32(APTR.FromPointer(oldAt));
            var next = value == old ? 1u : value == 0 ? 0u : value == uint.MaxValue ? 2u : 3u;
            if (next != kind || count == 64)
            {
                if (count != 0)
                {
                    p.WriteUInt8(plan, (int)runs++, (byte)((kind << 6) | (count - 1)));
                    length += kind == 3 ? 1 + count * 4 : 1;
                }
                kind = next; count = 0;
            }
            count++; oldAt += 4; finalAt += 4; remaining--;
        }
        if (count != 0)
        {
            p.WriteUInt8(plan, (int)runs++, (byte)((kind << 6) | (count - 1)));
            length += kind == 3 ? 1 + count * 4 : 1;
        }
        return runs;
    }
}
