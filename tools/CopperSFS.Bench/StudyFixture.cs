using System.Buffers.Binary;
using System.Text;
using CopperSFS.Check;

namespace CopperSFS.Bench;

// Original synthetic fixture, using the layout documented in docs/provenance.md.
// This is deliberately not a formatter or independent SFS interoperability evidence.
internal static class StudyFixture
{
    internal static byte[] Empty(int bytes, int capacity)
    {
        if (bytes is not (512 or 1024 or 2048 or 4096) || capacity % bytes != 0)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        var blocks = capacity / bytes;
        var mapBits = (bytes - 12) * 8;
        var mapCount = (blocks + mapBits - 1) / mapBits;
        if (blocks < 128 || 9 + mapCount > 33)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        var image = new byte[capacity];
        Span<byte> Block(int number) => image.AsSpan(number * bytes, bytes);
        void Header(int number, string id)
        {
            Encoding.Latin1.GetBytes(id).CopyTo(Block(number));
            U32(Block(number), 8, (uint)number);
        }
        foreach (var number in new[] { 0, blocks - 1 })
        {
            Header(number, "SFS\0");
            var root = Block(number);
            U16(root, 12, 3); U16(root, 14, 1);
            U32(root, 44, (uint)capacity); U32(root, 48, (uint)blocks); U32(root, 52, (uint)bytes);
            U32(root, 96, 9); U32(root, 100, 8); U32(root, 104, 2); U32(root, 108, 5); U32(root, 112, 6);
            Seal(root);
        }
        Header(2, "OBJC");
        var obj = Block(2);
        U32(obj, 28, 1); U32(obj, 32, 15); U32(obj, 36, 3); U32(obj, 40, 7);
        obj[48] = 128; Encoding.Latin1.GetBytes("BLOCKSTUDY").CopyTo(obj[49..]);
        U32(obj, bytes - 28, (uint)(blocks - 34));
        Header(3, "HTAB"); U32(Block(3), 12, 1);
        Header(4, "TROK");
        Header(5, "BNDC"); Block(5)[14] = 1; Block(5)[15] = 14;
        Header(6, "NDC "); U32(Block(6), 12, 1); U32(Block(6), 16, 1); U32(Block(6), 20, 2);
        Header(7, "OBJC"); U32(Block(7), 12, 1);
        Header(8, "ADMC"); Block(8)[20] = 2; U32(Block(8), 24, 1);
        uint adminUsed = 0;
        foreach (var number in Enumerable.Range(2, 7).Concat(Enumerable.Range(9, mapCount)))
            adminUsed |= 0x80000000u >> (number - 1);
        U32(Block(8), 28, adminUsed);
        for (var map = 0; map < mapCount; map++) Header(9 + map, "BTMP");
        for (var number = 33; number < blocks - 1; number++)
        {
            var bit = number % mapBits;
            Block(9 + number / mapBits)[12 + bit / 8] |= (byte)(0x80 >> (bit % 8));
        }
        foreach (var number in Enumerable.Range(2, 7).Concat(Enumerable.Range(9, mapCount))) Seal(Block(number));
        Inspect(image, bytes);
        return image;
    }

    internal static ImageReport Inspect(byte[] image, int bytes)
    {
        using var stream = new MemoryStream(image, writable: false);
        return SfsImageChecker.Check(stream, bytes);
    }
    internal static byte[] Pattern(int length, int seed = 7)
    {
        var result = new byte[length];
        var random = new Random(seed);
        random.NextBytes(result);
        return result;
    }
    internal static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
    private static void U16(Span<byte> data, int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(data[offset..], value);
    private static void U32(Span<byte> data, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(data[offset..], value);
    private static void Seal(Span<byte> data)
    {
        U32(data, 4, 0); uint sum = 1;
        for (var offset = 0; offset < data.Length; offset += 4) sum = unchecked(sum + U32(data, offset));
        U32(data, 4, unchecked(0u - sum));
    }
}
