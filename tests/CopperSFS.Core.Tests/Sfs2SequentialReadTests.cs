using Amiga;

namespace CopperSFS.Tests;

public sealed partial class Sfs2ReadOnlyTests
{
    [Fact]
    public void RootChecksumIncludesEveryWordAcrossUnrolledTilesAndTheFinalWord()
    {
        var image = Image(); var first = image[0].ToArray(); var last = image[127].ToArray();
        for (var at = 0; at < 512; at += 4)
        {
            image[0] = first.ToArray(); image[127] = last.ToArray();
            image[0][at] ^= 1; image[127][at] ^= 1;
            var disk = new Disk(image);
            Assert.False(AmigaSmartFileSystemReadOnlyCore.Root(ref disk, Meta, out _), $"Checksum skipped word {at / 4}");
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PartialReadsConsumeRetainedImagesAndPreserveAliasedDestinations(bool alias)
    {
        var image = Image();
        var disk = new Disk(image) { RetainedKey = 40, RetainedImage = APTR.FromPointer(12000) };
        image[40].CopyTo(disk.Ram, 12000);
        var target = APTR.FromPointer(alias ? 12001u : 4096u);
        var cursor = new AmigaSmartFileSystemReadOnlyCore.ReadCursor();
        Assert.Equal(64, AmigaSmartFileSystemReadOnlyCore.Read(ref disk, ref cursor, 8, 3,
            target, 64, Meta, Data, out var error));
        Assert.Equal(DOS.Error.None, error);
        Assert.Equal(image[40].AsSpan(3, 64).ToArray(), disk.Ram.AsSpan((int)target.Raw, 64).ToArray());
        Assert.Equal(alias ? 1u : 0u, disk.RetainedBlockReads);
    }

    [Theory]
    [InlineData(512)] [InlineData(1024)] [InlineData(4096)]
    public void SequentialCursorReusesValidatedExtentAcrossPacketsAndSharedScratch(int bytes)
    {
        var image = Image(bytes: bytes); var disk = new Disk(image, blockBytes: (uint)bytes);
        var meta = APTR.FromPointer(4096); var data = APTR.FromPointer(8192); var target = APTR.FromPointer(16384);
        var cursor = new AmigaSmartFileSystemReadOnlyCore.ReadCursor();
        Assert.Equal(bytes, AmigaSmartFileSystemReadOnlyCore.Read(ref disk, ref cursor, 8, 0,
            target, (uint)bytes, meta, data, out var error));
        Assert.Equal(DOS.Error.None, error);
        disk.Clear(meta, (uint)bytes); disk.Clear(data, (uint)bytes);
        var before = disk.Reads;
        Assert.Equal(bytes + 277, AmigaSmartFileSystemReadOnlyCore.Read(ref disk, ref cursor, 8, (ulong)bytes,
            target, (uint)(bytes * 2), meta, data, out error));
        Assert.Equal(DOS.Error.None, error); Assert.Equal(before + 2, disk.Reads);
        Assert.Equal(new[] { (40u, 1u), (41u, 1u) }, disk.DataReads);
        Assert.Equal(Enumerable.Range(bytes, bytes + 277).Select(i => (byte)(i * 17 + 3)),
            disk.Ram.AsSpan(16384, bytes + 277).ToArray());
        var other = new AmigaSmartFileSystemReadOnlyCore.ReadCursor();
        Assert.Equal(1, AmigaSmartFileSystemReadOnlyCore.Read(ref disk, ref other, 9, 0, target, 1, meta, data, out _));
        before = disk.Reads;
        Assert.Equal(bytes * 2, AmigaSmartFileSystemReadOnlyCore.Read(ref disk, ref cursor, 8, 0,
            target, (uint)(bytes * 2), meta, data, out _));
        Assert.Equal(before + 2, disk.Reads); Assert.Equal((40u, 2u), disk.DataReads[^1]);
    }

    [Theory]
    [InlineData(0u)] [InlineData(1u)] [InlineData(2u)] [InlineData(3u)]
    public void FragmentedCursorPreservesEdgesBackwardSeeksAndExtentBacklinkChecks(uint alignment)
    {
        var image = Image(); var tree = image[6];
        U32(tree, 20, 60); U32(tree, 28, 2); U16(tree, 12, 3);
        U32(tree, 48, 60); U32(tree, 56, 40); U32(tree, 60, 1); Seal(tree, true);
        image[60] = image[42].ToArray(); var disk = new Disk(image);
        var cursor = new AmigaSmartFileSystemReadOnlyCore.ReadCursor();
        foreach (var position in new ulong[] { 0, 512, 1024, 3, 1023, 1301 })
        {
            var expected = (int)Math.Min(1301 - position, 1100ul);
            disk.Ram.AsSpan(4096, 1400).Fill(0xA7);
            Assert.Equal(expected, AmigaSmartFileSystemReadOnlyCore.Read(ref disk, ref cursor, 8, position,
                APTR.FromPointer(Target.Raw + alignment), 1100, Meta, Data, out var error));
            Assert.Equal(DOS.Error.None, error);
            Assert.Equal(Enumerable.Range((int)position, expected).Select(i => (byte)(i * 17 + 3)),
                disk.Ram.AsSpan(4096 + (int)alignment, expected).ToArray());
            Assert.All(disk.Ram.AsSpan(4096, (int)alignment).ToArray(), b => Assert.Equal(0xA7, b));
            Assert.All(disk.Ram.AsSpan(4096 + (int)alignment + expected, 8).ToArray(), b => Assert.Equal(0xA7, b));
        }
        cursor.Node = 0; U32(tree, 56, 50); Seal(tree, true);
        Assert.Equal(1024, AmigaSmartFileSystemReadOnlyCore.Read(ref disk, ref cursor, 8, 0,
            Target, 1301, Meta, Data, out var failed));
        Assert.Equal(DOS.Error.DiskNotValidated, failed); Assert.Equal(0u, cursor.Node);
    }

    [Fact]
    public void BulkFailureReturnsCompletedPrefixAndInvalidatedCursorReloadsSizeAndMapping()
    {
        var image = Image(); var disk = new Disk(image) { BadBlock = 41 };
        var cursor = new AmigaSmartFileSystemReadOnlyCore.ReadCursor();
        disk.Ram.AsSpan(4096, 1301).Fill(0xA7);
        Assert.Equal(512, AmigaSmartFileSystemReadOnlyCore.Read(ref disk, ref cursor, 8, 0,
            Target, 1301, Meta, Data, out var error));
        Assert.Equal(DOS.Error.DiskNotValidated, error); Assert.Equal(0u, cursor.Node);
        Assert.Equal((40u, 2u), disk.DataReads.Single());
        Assert.All(disk.Ram.AsSpan(4608, 789).ToArray(), b => Assert.Equal(0xA7, b));
        disk.BadBlock = uint.MaxValue;
        Assert.Equal(512, AmigaSmartFileSystemReadOnlyCore.Read(ref disk, ref cursor, 8, 0,
            Target, 512, Meta, Data, out _));
        U32(image[10], 36, 50); U32(image[10], 40, 0); U16(image[10], 44, 17); Seal(image[10], true);
        U32(image[6], 40, 0x80000008); Seal(image[6], true);
        image[50] = Enumerable.Repeat((byte)0xD3, 512).ToArray();
        cursor.Node = 0;
        Assert.Equal(17, AmigaSmartFileSystemReadOnlyCore.Read(ref disk, ref cursor, 8, 0,
            Target, 512, Meta, Data, out error));
        Assert.Equal(DOS.Error.None, error);
        Assert.All(disk.Ram.AsSpan(4096, 17).ToArray(), b => Assert.Equal(0xD3, b));
    }
}
