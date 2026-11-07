using System.Buffers.Binary;
using System.Text;

namespace CopperStart.Floppy.Native.Tests;

public sealed class DosDiskImageLayoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HdFillerOwnsLowerHalfAndLeavesUpperHalfAvailable(bool ffs)
    {
        var disk = DosDiskImage.Create(ffs, highDensity: true);
        disk.FillBeforeBlock(1800);
        disk.AddDataFile("UpperProbe", []);
        uint Word(uint block, int at) => BinaryPrimitives.ReadUInt32BigEndian(disk.Bytes.AsSpan((int)block * 512 + at));
        uint Header(string name) => Enumerable.Range(2, (int)disk.BlockCount - 2).Select(n => (uint)n).Single(block =>
            Word(block, 0) == 2 && disk.Bytes[block * 512 + 432] == name.Length &&
            Encoding.ASCII.GetString(disk.Bytes, (int)block * 512 + 433, name.Length) == name);
        void Checksum(uint block) {
            uint sum = 0; for (var at = 0; at < 512; at += 4) sum = unchecked(sum + Word(block, at));
            Assert.Equal(0u, sum);
        }
        var header = Header("LowerHalf"); var upper = Header("UpperProbe");
        Assert.InRange(upper, 1800u, 1802u);
        var owned = new HashSet<uint>(); var data = new List<uint>();
        for (var table = header; table != 0; table = Word(table, 504)) {
            Assert.True(owned.Add(table)); Checksum(table);
            var count = Word(table, 8); Assert.InRange(count, 1u, 72u);
            for (var i = 0; i < count; i++) {
                var block = Word(table, 308 - (int)i * 4);
                Assert.True(owned.Add(block)); data.Add(block);
                Assert.InRange(block, 22u, upper - 1);
                Assert.NotEqual(disk.RootBlock, block); Assert.NotEqual(disk.BitmapBlock, block);
            }
        }
        Assert.Equal((uint)data.Count * (ffs ? 512u : 488u), Word(header, 324));
        for (var i = 0; i < data.Count; i++) {
            if (ffs) continue;
            Checksum(data[i]); Assert.Equal(8u, Word(data[i], 0));
            Assert.Equal(header, Word(data[i], 4)); Assert.Equal((uint)i + 1, Word(data[i], 8));
            Assert.Equal(488u, Word(data[i], 12));
            Assert.Equal(i + 1 == data.Count ? 0 : data[i + 1], Word(data[i], 16));
        }
        foreach (var block in owned.Append(upper)) {
            var bit = block - 2;
            Assert.Equal(0u, Word(disk.BitmapBlock, 4 + (int)(bit / 32) * 4) & (1u << (int)(bit % 32)));
        }
        Checksum(disk.RootBlock); Checksum(disk.BitmapBlock); Checksum(upper);
    }
}
