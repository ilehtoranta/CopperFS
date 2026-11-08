using Amiga;

namespace CopperSFS.Bench;

internal partial struct StudyDisk : IAmigaDiscardFileSystemPlatform
{
    public uint BlockCount => Geometry.BlockCount;
    public uint BlockBytes => Geometry.BlockBytes;
    public uint ReservedBlocks => Geometry.ReservedBlocks;
    public bool ReadMetadataBlock(uint block, uint id, APTR target) => AmigaFileSystemBlockIo.ReadMetadata(ref this, block, id, target);
    public APTR ReadMetadataView(uint block, uint id, APTR target) => ReadMetadataBlock(block, id, target) ? target : APTR.Null;
    public uint ReadDataBlocks(uint block, uint count, APTR target, bool readAhead) =>
        ReadDataBlocks(block, count, target);
    public uint ReadDataBlocks(uint block, uint count, APTR target) => AmigaFileSystemBlockIo.Read(ref this, block, count, target);
    public bool WriteDataBlocks(uint block, uint count, APTR source) => AmigaFileSystemBlockIo.Write(ref this, block, count, source);
    public bool QueryDiscard(APTR control, out AmigaFileSystemDiscardLimits limits)
    {
        limits = DiscardLimits;
        return DiscardSupported;
    }
    public bool DiscardBlocks(APTR control, uint block, uint count)
    {
        if (pending.Count != 0) throw new InvalidOperationException("Discard preceded durable checkpoint.");
        DiscardRequests.Add((block, count));
        var bitmapBase = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(device.AsSpan(bytes + 96));
        for (var i = block; i < block + count; i++)
        {
            CheckBlock(i);
            if (i < Geometry.ReservedBlocks || i == Geometry.BlockCount - 1) throw new InvalidOperationException("Reserved block discarded.");
            var bits = (Geometry.BlockBytes - 12) * 8;
            var map = bytes + (int)(bitmapBase + i / bits) * bytes;
            var word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(device.AsSpan(map + 12 + (int)((i % bits) / 32) * 4));
            if ((word & (1u << (int)(31 - i % 32))) == 0) throw new InvalidOperationException("Allocated block discarded.");
            device.AsSpan(bytes + (int)i * bytes, bytes).Fill(0xD7);
            Discarded++; Events++;
            if (Events == CutAfterEvent) throw new StudyPowerCut();
            if (Discarded == FailDiscardAt) return false;
        }
        return true;
    }
}
