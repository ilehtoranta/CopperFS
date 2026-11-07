using System.Buffers.Binary;
using System.Text;
using Amiga;
using CopperSFS;
using CopperSFS.Bench;
using CopperStart.Dos;
using CopperStart.Floppy.Native.Tests;

namespace CopperFS.Kickstart.Tests;

internal static class ReadMedia
{
    internal static readonly byte[] Payload = Enumerable.Range(0, 1048576)
        .Select(i => (byte)(i * 17 + (i >> 9) * 31 + (i >> 17) * 7 + 3)).ToArray();

    internal static byte[] Sfs2()
    {
        // Convert only the repository's original empty synthetic fixture, as in
        // Sfs2WriteTests. No external formatter, handler or media is imported.
        var image = StudyFixture.Empty(512, 16 * 1024 * 1024);
        for (var n = 0; n < image.Length / 512; n++)
        {
            var b = image.AsSpan(n * 512, 512); var id = U32(b, 0);
            if (id == 0) continue;
            if (id == 0x53465300) { Put(b, 0, 0x53465302); BinaryPrimitives.WriteUInt16BigEndian(b[12..], 4); }
            if (id == 0x4F424A43 && U32(b, 28) != 0)
            {
                var old = b.ToArray(); b[44..476].Clear(); old.AsSpan(44, 4).CopyTo(b[46..]); b[50] = old[48];
                var end = Array.IndexOf(old, (byte)0, 49); old.AsSpan(49, end - 49).CopyTo(b[51..]);
            }
            if (id == 0x424E4443) b[15] = 16;
            Seal(b);
        }
        var disk = new StudyDisk(image, 512);
        Encoding.ASCII.GetBytes("Payload").CopyTo(disk.Ram, (int)disk.NameBuffer.Raw);
        var work = Work(disk);
        var request = new AmigaSmartFileSystem2WriteCore.Request { Directory = 1, Name = disk.NameBuffer, Length = 7 };
        Assert.True(AmigaSmartFileSystem2WriteCore.Create(ref disk, ref work, ref request, false), work.Error.ToString());
        var node = request.Node; Payload.CopyTo(disk.Ram, (int)disk.Payload.Raw);
        request = new() { Node = node, Buffer = disk.Payload, Length = (uint)Payload.Length }; work = Work(disk);
        Assert.True(AmigaSmartFileSystem2WriteCore.WriteBatched(ref disk, ref work, ref request,
            disk.BatchBuffer, 65536, out var actual), work.Error.ToString());
        Assert.Equal((uint)Payload.Length, actual);
        var result = disk.Snapshot();
        // The bounded writer can leave multiple append extents. Relocate the
        // synthetic file to the same physical run as the FFS fixture, adjusting
        // its existing leaf, object and bitmap rather than timing fragmented I/O.
        var treeKey = U32(result, 108); var bitmapBase = U32(result, 96);
        var tree = result.AsSpan((int)treeKey * 512, 512);
        Assert.Equal(1, tree[14]);
        var entries = BinaryPrimitives.ReadUInt16BigEndian(tree[12..]);
        void Allocate(uint block, bool used)
        {
            var map = bitmapBase + block / 4000; var bit = block % 4000;
            var at = (int)map * 512 + 12 + (int)(bit / 8); var mask = (byte)(0x80 >> (int)(bit % 8));
            result[at] = used ? (byte)(result[at] & ~mask) : (byte)(result[at] | mask);
        }
        uint oldBlocks = 0;
        for (var i = 0; i < entries; i++)
        {
            var first = U32(tree, 16 + i * 16); var count = U32(tree, 28 + i * 16); oldBlocks += count;
            for (var at = 0u; at < count; at++) Allocate(first + at, false);
        }
        Assert.Equal((uint)Payload.Length / 512, oldBlocks);
        for (var at = 0u; at < oldBlocks; at++) Allocate(8192 + at, true);
        tree[16..].Clear(); BinaryPrimitives.WriteUInt16BigEndian(tree[12..], 1);
        Put(tree, 16, 8192); Put(tree, 24, node | 0x80000000); Put(tree, 28, oldBlocks); Seal(tree);
        var objectFound = false;
        for (var block = 0; block < result.Length / 512; block++)
        {
            var current = result.AsSpan(block * 512, 512);
            if (U32(current, 0) == 0x4F424A43 && U32(current, 28) == node)
            { Put(current, 36, 8192); Seal(current); objectFound = true; }
            if (U32(current, 0) == 0x42544D50) Seal(current);
        }
        Assert.True(objectFound); Payload.CopyTo(result, 8192 * 512);
        var verify = new StudyDisk(result, 512) { ReadOnly = true };
        Assert.Equal(1, AmigaSmartFileSystemReadOnlyCore.Read(ref verify, node, 0,
            verify.Payload, 1, verify.Work.Meta, verify.Work.Data, out _));
        var report = StudyFixture.Inspect(result, 512);
        Assert.Equal((ulong)Payload.Length, report.Entries.Single(e => e.Path == "/Payload").Size);
        return result;
    }

    private static AmigaSmartFileSystem2WriteCore.Work Work(StudyDisk d) => new()
    { Meta = d.Work.Meta, Data = d.Work.Data, Bitmap = d.Work.Bitmap, Journal = d.Work.Journal };
    private static uint U32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32BigEndian(b[at..]);
    private static void Put(Span<byte> b, int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(b[at..], value);
    private static void Seal(Span<byte> b)
    {
        Put(b, 4, 0); uint sum = 2;
        for (var i = 0; i < 512; i += 4) sum = unchecked(sum + U32(b, i));
        Put(b, 4, unchecked(0u - sum));
    }
}
