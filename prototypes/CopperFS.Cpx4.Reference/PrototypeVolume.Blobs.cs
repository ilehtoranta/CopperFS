using static CopperFS.Cpx4.Reference.Layout;

namespace CopperFS.Cpx4.Reference;
public ref partial struct Cpx4Volume<TDevice> where TDevice : IBlockDevice
{
    private ulong ReadBlob(ulong head, PageType type, ulong owner, int length, scoped Span<byte> output, bool verifyOnly = false)
    {
        if (!layout.AllocatablePage(head) || length is < 1 or > 768 || !verifyOnly && output.Length < length)
            throw new InvalidDataException("Invalid external string address or length.");
        Load(head, B(4));
        if (!VolumePage(B(4), type, head, owner) || U16(B(4), 64) != length || U16(B(4), 66) != 0)
            throw new InvalidDataException("Invalid external string head.");
        int first = Math.Min(length, layout.PageBytes - 68);
        ulong tail = U64(B(4), 56), transaction = U64(B(4), 32);
        if (!verifyOnly) B(4).Slice(68, first).CopyTo(output);
        if (first == length)
        {
            if (tail != 0) throw new InvalidDataException("Unexpected external string continuation.");
            return 0;
        }
        if (!layout.SmallMetadata || layout.PageBytes != 512 || tail == head || !layout.AllocatablePage(tail))
            throw new InvalidDataException("Missing or invalid external string continuation.");
        Load(tail, B(4));
        if (!VolumePage(B(4), PageType.BlobTail, tail, owner) || U64(B(4), 56) != head || U64(B(4), 32) != transaction ||
            U16(B(4), 64) != length - first || U16(B(4), 66) != first)
            throw new InvalidDataException("Invalid external string continuation identity or payload.");
        if (!verifyOnly) B(4).Slice(68, length - first).CopyTo(output[first..]);
        return tail;
    }

    private ulong MarkBlob(ulong head, PageType type, ulong owner, int length, ulong window)
    {
        ulong tail = ReadBlob(head, type, owner, length, Span<byte>.Empty, true);
        MarkOwned(head, layout.MetadataSpan, window);
        if (tail == 0) return 1;
        MarkOwned(tail, layout.MetadataSpan, window); return 2;
    }
}
