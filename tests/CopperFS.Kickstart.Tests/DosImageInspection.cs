using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CopperFS.Kickstart.Tests;

// Independent clean-image walker. Deliberately uses no CopperFFS routines.
internal static class DosImageInspection
{
    internal sealed record Entry(string Path, uint Header, uint Size, string Sha256);
    internal sealed record Report(uint Blocks, int UsedBlocks, List<Entry> Files, List<uint> OmittedForwardLinks, List<uint> RestartedSequences);

    internal static Report Check(byte[] image, bool requireForwardLinks)
    {
        var blocks = (uint)image.Length / 512; var root = blocks / 2;
        uint Word(uint block, int offset) => BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(checked((int)block * 512 + offset), 4));
        void Seal(uint block)
        {
            uint sum = 0; for (var at = 0; at < 512; at += 4) sum = unchecked(sum + Word(block, at));
            Assert.Equal(0u, sum);
        }
        var owned = new HashSet<uint> { 0, 1 };
        void Own(uint block)
        { Assert.InRange(block, 2u, blocks - 1); Assert.True(owned.Add(block), $"Duplicate block {block}"); }
        var ffs = Word(0, 0) == 0x444F5301;
        Assert.True(ffs || Word(0, 0) == 0x444F5300);
        Own(root); Seal(root); Assert.Equal(uint.MaxValue, Word(root, 312));
        Assert.Equal(2u, Word(root, 0)); Assert.Equal(1u, Word(root, 508));
        var maps = new List<uint>();
        for (var page = 0u; page < (blocks - 2 + 4063) / 4064; page++)
        { var map = Word(root, 316 + (int)page * 4); Own(map); Seal(map); maps.Add(map); }
        var files = new List<Entry>();
        var omittedLinks = new List<uint>();
        var restartedSequences = new List<uint>();
        void Walk(uint directory, string prefix)
        {
            for (var slot = 0; slot < 72; slot++)
            for (var header = Word(directory, 24 + slot * 4); header != 0; header = Word(header, 496))
            {
                Own(header); Seal(header); Assert.Equal(2u, Word(header, 0)); Assert.Equal(header, Word(header, 4));
                Assert.Equal(directory, Word(header, 500));
                var nameLength = image[(int)header * 512 + 432]; Assert.InRange(nameLength, (byte)1, (byte)30);
                var path = prefix + "/" + Encoding.Latin1.GetString(image, (int)header * 512 + 433, nameLength);
                var type = Word(header, 508);
                if (type == 2) { Walk(header, path); continue; }
                Assert.Equal(0xFFFFFFFDu, type);
                var size = Word(header, 324); var payloadBytes = ffs ? 512u : 488u;
                var needed = (size + payloadBytes - 1) / payloadBytes;
                var data = new List<uint>(); var table = header;
                do
                {
                    Assert.Equal(Math.Min(72u, needed - (uint)data.Count), Word(table, 8));
                    var count = Word(table, 8);
                    for (var i = 0; i < count; i++) { var block = Word(table, 308 - i * 4); Own(block); data.Add(block); }
                    table = Word(table, 504);
                    if (table != 0) { Own(table); Seal(table); Assert.Equal(16u, Word(table, 0)); Assert.Equal(header, Word(table, 500)); }
                } while (table != 0);
                Assert.Equal(needed, (uint)data.Count);
                using var content = new MemoryStream();
                var sequenceBase = 0u;
                for (var i = 0; i < data.Count; i++)
                {
                    var block = data[i]; var count = Math.Min(payloadBytes, size - (uint)content.Length);
                    if (!ffs)
                    {
                        Seal(block); Assert.Equal(8u, Word(block, 0)); Assert.Equal(header, Word(block, 4));
                        if (!requireForwardLinks && Word(block, 8) != (uint)i + 1)
                        {
                            if (i != 0 && Word(block, 8) == 1 && Word(data[i - 1], 16) == 0) sequenceBase = (uint)i;
                            Assert.NotEqual(0u, sequenceBase);
                            Assert.Equal((uint)i + 1 - sequenceBase, Word(block, 8));
                            restartedSequences.Add(block);
                        }
                        else Assert.Equal((uint)i + 1, Word(block, 8));
                        Assert.Equal(count, Word(block, 12));
                        var expectedLink = i + 1 == data.Count ? 0 : data[i + 1];
                        var actualLink = Word(block, 16);
                        // FFS 40.1 leaves the previous EOF link zero on OFS
                        // append. Record that reference behavior explicitly;
                        // candidate output must still publish the complete link.
                        if (!requireForwardLinks && actualLink == 0 && expectedLink != 0)
                        { Assert.Equal(1u, Word(expectedLink, 8)); omittedLinks.Add(block); }
                        else Assert.Equal(expectedLink, actualLink);
                    }
                    content.Write(image, (int)block * 512 + (ffs ? 0 : 24), (int)count);
                }
                files.Add(new(path, header, size, Convert.ToHexString(SHA256.HashData(content.ToArray()))));
            }
        }
        Walk(root, "");
        for (var bit = 0u; bit < maps.Count * 4064; bit++)
        {
            var free = (Word(maps[(int)(bit / 4064)], 4 + (int)(bit % 4064 / 32) * 4) & 1u << (int)(bit % 32)) != 0;
            if (bit + 2 < blocks) Assert.True(!owned.Contains(bit + 2) == free, $"Block {bit + 2}: free {free}, owned {owned.Contains(bit + 2)}");
            else Assert.False(free);
        }
        Assert.Equal(90130u, files.Single(f => f.Path == "/AB/Renamed").Size);
        var expected = Enumerable.Range(0, 90130).Select(i =>
            i >= 35000 && i < 35777 ? (byte)((i - 35000) * 23 + 9) :
            i >= 487 && i < 1264 ? (byte)((i - 487) * 23 + 9) : (byte)(i * 17 + (i >> 9) * 31 + 3)).ToArray();
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), files.Single(f => f.Path == "/AB/Renamed").Sha256);
        return new(blocks, owned.Count, files, omittedLinks, restartedSequences);
    }
}
