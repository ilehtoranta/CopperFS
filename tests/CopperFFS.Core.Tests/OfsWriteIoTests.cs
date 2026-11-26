using Amiga;
using CopperSFS;
using CopperStart.Dos;

namespace CopperStart.FileSystem.Tests;

public sealed partial class VolumeGeometryTests
{
    // Mark every other free block in [first, first + count) allocated so a
    // growing file gets no contiguous run.
    private static void Fragment(ref Memory p, AmigaVolumeGeometry g, uint first, uint count)
    {
        Assert.True(p.ReadBlock(g.RootBlock, Meta));
        for (var key = first; key < first + count; key += 2)
        {
            var bit = key - g.ReservedBlocks;
            var map = p.ReadUInt32(Meta, g.Tail + 316 + (int)(bit / g.BitmapBits) * 4);
            Assert.True(p.ReadBlock(map, Bitmap));
            var at = 4 + (int)(bit % g.BitmapBits / 32) * 4;
            p.WriteUInt32(Bitmap, at, p.ReadUInt32(Bitmap, at) & ~(1u << (int)(bit % 32)));
            uint sum = 0; for (var i = 4; i < g.BlockBytes; i += 4) sum = unchecked(sum + p.ReadUInt32(Bitmap, i));
            p.WriteUInt32(Bitmap, 0, unchecked(0u - sum));
            Assert.True(p.WriteBlock(map, Bitmap));
        }
    }

    // Growing an OFS file costs about one write per data block, also when the
    // free space is fragmented (no contiguous run): each block is linked to
    // its successor before it is written, and the pointer table is written
    // once per request instead of once per block.
    [Theory]
    [InlineData(0, false, 0u)] [InlineData(0, true, 0u)] [InlineData(0, false, 11u)] [InlineData(0, true, 11u)]
    [InlineData(1, false, 0u)] [InlineData(1, true, 0u)] [InlineData(1, false, 11u)] [InlineData(1, true, 11u)]
    public void OfsGrowthWritesAboutOneBlockPerDataBlock(int dos, bool fragmented, uint track)
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(1760, 512, 2, out var g));
        g.TrackBlocks = track;
        var p = new Memory(storage, g); VariantFormat(ref p, storage, 0x444F5300u + (uint)dos);
        var file = VariantCreate(ref p, storage, g.RootBlock, "Grow");
        OfsWrite(ref p, storage, ref file, 0, new byte[100]);
        if (fragmented) Fragment(ref p, g, g.ReservedBlocks, 400);
        var payload = dos == 0 ? 488 : 512; var blocks = 40;
        var data = Enumerable.Range(0, blocks * payload).Select(i => (byte)(i * 7)).ToArray();
        storage.Transfers.Clear();
        OfsWrite(ref p, storage, ref file, 100, data);
        var writes = storage.Transfers.Count(t => t.Command is 3 or 25);
        var reads = storage.Transfers.Count(t => t.Command is 2 or 24);
        var expected = new byte[100].Concat(data).ToArray();
        if (dos == 0) InspectOfs(storage, g, file.Header, expected);
        Assert.Equal(expected.Length, (int)AmigaDiskFileSystemCore.Read(ref p, file.Header, 0x444F5300u + (uint)dos,
            0, Output, (uint)expected.Length, Meta, Data, out _));
        Assert.Equal(expected, storage.Ram.AsSpan((int)Output.Raw, expected.Length).ToArray());
        // 40 new data blocks plus the old tail block, its pointer table,
        // the two root validity writes, a bitmap page and the header size.
        Assert.True(writes <= blocks + 8, $"{writes} writes, {reads} reads for {blocks} data blocks");
        Assert.True(reads <= 24, $"{writes} writes, {reads} reads for {blocks} data blocks");
    }
}

public sealed partial class VolumeGeometryTests
{
    // Overwriting whole FFS data blocks on a non-fixed disk (no multi-block
    // path) replaces every byte, so the old blocks are not read first.
    [Fact]
    public void FfsFullBlockOverwriteDoesNotReadTheOldBlocks()
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(1760, 512, 2, out var g));
        var p = new Memory(storage, g); VariantFormat(ref p, storage, 0x444F5301);
        var file = VariantCreate(ref p, storage, g.RootBlock, "Over");
        OfsWrite(ref p, storage, ref file, 0, new byte[40 * 512]);
        var data = Enumerable.Range(0, 40 * 512).Select(i => (byte)(i * 13 + 1)).ToArray();
        storage.Transfers.Clear();
        var w = Work(); data.CopyTo(storage.Ram, (int)Payload.Raw);
        file.Buffer = Payload; file.Position = 0; file.Length = (uint)data.Length;
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out var actual, false), w.Error.ToString());
        Assert.Equal((uint)data.Length, actual);
        var dataReads = storage.Transfers.Count(t => t.Command is 2 or 24 && t.Offset >= g.StartByte + 2 * 512ul && t.Offset < g.StartByte + 600 * 512ul);
        Assert.True(dataReads <= 4, $"{dataReads} reads in the data area");
        Assert.Equal(data.Length, (int)AmigaDiskFileSystemCore.Read(ref p, file.Header, 0x444F5301, 0, Output, (uint)data.Length, Meta, Data, out _));
        Assert.Equal(data, storage.Ram.AsSpan((int)Output.Raw, data.Length).ToArray());
    }
}

public sealed partial class VolumeGeometryTests
{
    // Relocating a fragmented OFS overwrite writes its contiguous replacement
    // through the mount's staging buffer, several sealed blocks per transfer,
    // instead of one write per block.
    [Fact]
    public void OfsRelocationWritesTheReplacementRunInBatches()
    {
        var (s, p, g, file, expected, old) = FragmentedFile(64, 8, true, gap: 249);
        var w = Work(); w.Index = APTR.FromPointer(0x180000);
        AmigaFfsMountIndex.Initialize(ref p, w.Index, 65536);
        // A metadata packet builds the mount index (and its staging buffer),
        // as the native mount does before the overwrite arrives.
        var touch = file; touch.Position = 0;
        Assert.True(AmigaFfsWriteCore.SetMetadata(ref p, ref w, ref touch, DosPacketAction.SetProtection), w.Error.ToString());
        file.Position = 0; s.Ram.AsSpan((int)Payload.Raw, expected.Length).Fill(0x5E);
        s.BulkTransfers = true; s.Transfers.Clear();
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out var actual, true), w.Error.ToString());
        Assert.Equal(file.Length, actual);
        Assert.True(p.ReadBlock(file.Header, Meta));
        Assert.NotEqual(old[0], p.ReadUInt32(Meta, 308)); // the file moved
        var writes = s.Transfers.Count(t => t.Command is 3 or 25);
        Assert.True(writes < 32, $"{writes} write transfers to relocate 64 blocks; status {p.ReadUInt32(w.Index, 0)} stage {p.ReadUInt32(w.Index, 48):X} bytes {p.ReadUInt32(w.Index, 52)}");
        var reader = new Memory(s, g);
        Assert.Equal(expected.Length, AmigaDiskFileSystemCore.Read(ref reader, file.Header, 0x444F5300, 0,
            Output, file.Length, Meta, Data, out var error));
        Assert.Equal(DOS.Error.None, error);
        Assert.True(s.Ram.AsSpan((int)Output.Raw, expected.Length).IndexOfAnyExcept((byte)0x5E) < 0);
    }
}
