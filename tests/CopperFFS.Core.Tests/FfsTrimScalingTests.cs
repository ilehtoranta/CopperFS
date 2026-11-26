using Amiga;
using CopperSFS;
using CopperStart.Dos;

namespace CopperStart.FileSystem.Tests;

public sealed partial class VolumeGeometryTests
{
    // Manual TRIM without a mount index checks that no two bitmap or
    // extension pointers alias. That check compared every pointer with every
    // earlier one; it is now sort-based, so quadrupling the volume costs
    // roughly four times the work, not sixteen. The discard window is kept
    // tiny so the measurement is the layout proof and allocation preflight.
    [Fact]
    public void ManualTrimLayoutProofScalesLinearly()
    {
        long Cost(uint blocks)
        {
            var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(blocks, 512, 2, out var g));
            var p = new Memory(storage, g); VariantFormat(ref p, storage, 0x444F5301);
            var w = Work(); storage.LongReads = 0;
            Assert.True(AmigaFfsWriteCore.TrimWindow(ref p, ref w, APTR.Null, AmigaSmartFileSystemTrimPacket.DryRun,
                out var result, g.ReservedBlocks + 8, 64), w.Error.ToString());
            Assert.Equal(64u, result.Blocks);
            return storage.LongReads;
        }
        var small = Cost(1_000_000); var large = Cost(4_000_000);
        Assert.True(large <= small * 6, $"1M blocks: {small} word reads, 4M blocks: {large}");
    }

    // Aliased bitmap pointers are still refused after the sort-based proof:
    // a duplicate between the root's pointers and an extension block's, and
    // an extension block that is also listed as a bitmap page.
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ManualTrimRefusesAliasedBitmapPointers(bool extensionAlias)
    {
        var storage = new Storage(); Assert.True(AmigaVolumeGeometry.Create(1_000_000, 512, 2, out var g));
        var p = new Memory(storage, g); VariantFormat(ref p, storage, 0x444F5301);
        Assert.True(p.ReadBlock(g.RootBlock, Meta));
        var extension = p.ReadUInt32(Meta, g.Tail + 416); Assert.NotEqual(0u, extension);
        Assert.True(p.ReadBlock(extension, Data));
        // Point the extension's tenth bitmap entry at the root's third page,
        // or the root's third pointer at the extension block itself.
        if (extensionAlias)
        {
            p.WriteUInt32(Meta, g.Tail + 316 + 8, extension);
            Reseal(ref p, g.RootBlock);
        }
        else
        {
            p.WriteUInt32(Data, 36, p.ReadUInt32(Meta, g.Tail + 316 + 8));
            Assert.True(p.WriteBlock(extension, Data));
        }
        var w = Work();
        Assert.False(AmigaFfsWriteCore.TrimWindow(ref p, ref w, APTR.Null, AmigaSmartFileSystemTrimPacket.DryRun,
            out _, g.ReservedBlocks + 8, 64));
        Assert.Equal(DOS.Error.DiskNotValidated, w.Error);
    }
}
