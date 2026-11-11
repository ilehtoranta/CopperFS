namespace CopperStart.Dos;

// Traversal owns only filesystem dimensions. Device offsets, transfer limits
// and DMA flags stay in AmigaVolumeGeometry at the platform I/O boundary.
internal readonly struct AmigaFfsGeometry
{
    public readonly uint BlockCount, BlockBytes, ReservedBlocks, RootBlock;

    private AmigaFfsGeometry(uint blocks, uint bytes, uint reserved)
    {
        BlockCount = blocks; BlockBytes = bytes; ReservedBlocks = reserved;
        RootBlock = (blocks - 1) / 2 + reserved / 2 +
            ((blocks - 1) % 2 + reserved % 2) / 2;
    }

    public static AmigaFfsGeometry Read<T>(ref T p) where T : struct, IAmigaFileSystemPlatform
        => new(p.BlockCount, p.BlockBytes, p.ReservedBlocks);

    public uint TableEntries => BlockBytes / 4 - 56;
    public uint BitmapBits => (BlockBytes - 4) * 8;
    public int Tail => (int)BlockBytes - 512;
}
