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

    internal static byte[] Ffs()
    {
        var fixture = DosDiskImage.Create(true, includeLargeFile: false, blockCount: 32768, payload: Payload, volumeName: "ReadBench");
        var image = fixture.Bytes;
        uint Read(uint block, int at) => BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan((int)block * 512 + at));
        void Allocate(uint block, bool used)
        {
            var bit = block - 2; var page = fixture.BitmapBlock + bit / 4064; var at = 4 + (int)(bit % 4064 / 32) * 4;
            var mask = 1u << (int)(bit % 32); var value = Read(page, at);
            fixture.Write(page, at, used ? value & ~mask : value | mask);
        }
        // Relocate just the payload to one physical run. Pointer-table blocks
        // remain separate, and the bitmap tracks both old and new ownership.
        var table = fixture.PayloadHeader;
        for (var index = 0u; index < Payload.Length / 512; index++)
        {
            if (index != 0 && index % 72 == 0) table = Read(table, 504);
            var at = 308 - (int)(index % 72) * 4; var old = Read(table, at); var fresh = 8192u + index;
            Allocate(old, false); Allocate(fresh, true); fixture.Write(table, at, fresh);
            if (index % 72 == 71 || index + 1 == Payload.Length / 512) fixture.Checksum(table);
        }
        fixture.Write(fixture.PayloadHeader, 16, 8192); fixture.Checksum(fixture.PayloadHeader);
        Payload.CopyTo(image, 8192 * 512);
        for (var page = 0u; page < fixture.BitmapBlocks; page++) fixture.Checksum(fixture.BitmapBlock + page, 0);
        var disk = new StudyDisk(image, 512, reservedBlocks: 2) { ReadOnly = true };
        Assert.Equal(Payload.Length, AmigaDiskFileSystemCore.Read(ref disk, fixture.PayloadHeader, 0x444F5301,
            0, disk.Payload, (uint)Payload.Length, disk.Work.Meta, disk.Work.Data, out var error));
        Assert.Equal(DOS.Error.None, error); Assert.Equal(Payload, disk.Ram.AsSpan((int)disk.Payload.Raw, Payload.Length).ToArray());
        return image;
    }

    internal static byte[] Sfs2(int volumeMiB = 16, bool fragmented = false)
    {
        // Convert only the repository's original empty synthetic fixture, as in
        // Sfs2WriteTests. No external formatter, handler or media is imported.
        var image = EmptySfs2Source(volumeMiB);
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
        PlacePayload(result, node, fragmented, true);
        var runBlocks = fragmented ? 128u : 2048u;
        var verify = new StudyDisk(result, 512) { ReadOnly = true };
        var cursor = new AmigaSmartFileSystemReadOnlyCore.ReadCursor();
        Assert.Equal(1, AmigaSmartFileSystemReadOnlyCore.Read(ref verify, ref cursor, node, 0,
            verify.Payload, 1, verify.Work.Meta, verify.Work.Data, out _));
        Assert.Equal(runBlocks, cursor.Count); Assert.Equal(fragmented ? 8448u : 0u, cursor.Next);
        var report = StudyFixture.Inspect(result, 512);
        Assert.Equal((ulong)Payload.Length, report.Entries.Single(e => e.Path == "/Payload").Size);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Payload)),
            report.Entries.Single(e => e.Path == "/Payload").Sha256);
        return result;
    }

    private static void PlacePayload(byte[] result, uint node, bool fragmented, bool sfs2)
    {
        // The bounded writer can leave multiple append extents. Relocate the
        // synthetic file to the same physical run as the FFS fixture, adjusting
        // its existing leaf, object and bitmap rather than timing fragmented I/O.
        var treeKey = U32(result, 108); var bitmapBase = U32(result, 96);
        var tree = result.AsSpan((int)treeKey * 512, 512);
        Assert.Equal(1, tree[14]);
        var stride = tree[15];
        Assert.Equal(sfs2 ? 16 : 14, (int)stride);
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
            var first = U32(tree, 16 + i * stride); var count = sfs2 ? U32(tree, 28 + i * stride) : BinaryPrimitives.ReadUInt16BigEndian(tree[(28 + i * stride)..]); oldBlocks += count;
            for (var at = 0u; at < count; at++) Allocate(first + at, false);
        }
        Assert.Equal((uint)Payload.Length / 512, oldBlocks);
        var runs = fragmented ? 16u : 1u; var runBlocks = oldBlocks / runs;
        tree[16..].Clear(); BinaryPrimitives.WriteUInt16BigEndian(tree[12..], (ushort)runs);
        for (var run = 0u; run < runs; run++)
        {
            var first = 8192 + run * 256; var at = 16 + (int)run * stride;
            for (var block = 0u; block < runBlocks; block++) Allocate(first + block, true);
            Put(tree, at, first); Put(tree, at + 4, run + 1 == runs ? 0u : first + 256);
            Put(tree, at + 8, run == 0 ? node | 0x80000000 : first - 256); if (sfs2) Put(tree, at + 12, runBlocks);
            else BinaryPrimitives.WriteUInt16BigEndian(tree[(at + 12)..], (ushort)runBlocks);
            Payload.AsSpan((int)(run * runBlocks * 512), (int)(runBlocks * 512)).CopyTo(result.AsSpan((int)first * 512));
        }
        Seal(tree, sfs2);
        var objectFound = false;
        for (var block = 0; block < result.Length / 512; block++)
        {
            var current = result.AsSpan(block * 512, 512);
            if (U32(current, 0) == 0x4F424A43 && U32(current, 28) == node)
            { Put(current, 36, 8192); Seal(current, sfs2); objectFound = true; }
            if (U32(current, 0) == 0x42544D50) Seal(current, sfs2);
        }
        Assert.True(objectFound);
    }

    private static byte[] EmptySfs2Source(int volumeMiB)
    {
        if (volumeMiB <= 32) return StudyFixture.Empty(512, checked(volumeMiB * 1024 * 1024));
        // Extend the repository's empty synthetic layout with additional admin
        // areas for its contiguous bitmap pages. Conversion seals every image
        // below; the independent checker verifies ownership and payload hashes.
        var image = new byte[checked(volumeMiB * 1024 * 1024)];
        StudyFixture.Empty(512, 8 * 1024 * 1024).AsSpan(0, 9 * 512).CopyTo(image);
        var blocks = image.Length / 512; var maps = (blocks + 3999) / 4000;
        var adminAreas = (8 + maps + 31) / 32;
        Assert.InRange(adminAreas, 1, (512 - 24) / 8);
        var root = image.AsSpan(0, 512);
        Put(root, 44, (uint)image.Length); Put(root, 48, (uint)blocks);
        root.CopyTo(image.AsSpan((blocks - 1) * 512)); Put(image.AsSpan((blocks - 1) * 512), 8, (uint)blocks - 1);
        Put(image.AsSpan(2 * 512), 484, (uint)(blocks - adminAreas * 32 - 2));
        var admin = image.AsSpan(8 * 512, 512); admin[24..].Clear();
        for (var area = 0; area < adminAreas; area++)
        {
            var first = 1 + area * 32; uint used = 0;
            for (var bit = 0; bit < 32; bit++)
                if (first + bit >= 2 && first + bit <= 8 + maps) used |= 0x80000000u >> bit;
            Put(admin, 24 + area * 8, (uint)first); Put(admin, 28 + area * 8, used);
        }
        for (var map = 0; map < maps; map++)
        { var page = image.AsSpan((9 + map) * 512, 512); Put(page, 0, 0x42544D50); Put(page, 8, (uint)(9 + map)); }
        for (var block = 1 + adminAreas * 32; block < blocks - 1; block++)
        { var bit = block % 4000; image[(9 + block / 4000) * 512 + 12 + bit / 8] |= (byte)(0x80 >> (bit % 8)); }
        return image;
    }

    internal static byte[] Sfs(int volumeMiB = 16, bool? fragmented = null)
    {
        // Build the classic comparison image from the repository's synthetic
        // empty fixture; both handler arms receive identical media bytes.
        var image = EmptySfs2Source(volumeMiB);
        if (volumeMiB > 32)
            for (var n = 0; n < image.Length; n += 512)
                if (U32(image, n) != 0) Seal(image.AsSpan(n, 512), false);
        var disk = new StudyDisk(image, 512, batchedWrites: true);
        var node = FsOps.Create(ref disk, "Payload");
        FsOps.Write(ref disk, node, 0, Payload);
        FsOps.Read(ref disk, node, 0, Payload.Length);
        Assert.Equal(Payload, disk.Ram.AsSpan((int)disk.Payload.Raw, Payload.Length).ToArray());
        var result = disk.Snapshot();
        if (fragmented.HasValue) PlacePayload(result, node, fragmented.Value, false);
        return result;
    }

    private static AmigaSmartFileSystem2WriteCore.Work Work(StudyDisk d) => new()
    { Meta = d.Work.Meta, Data = d.Work.Data, Bitmap = d.Work.Bitmap, Journal = d.Work.Journal };
    private static uint U32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32BigEndian(b[at..]);
    private static void Put(Span<byte> b, int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(b[at..], value);
    private static void Seal(Span<byte> b, bool sfs2 = true)
    {
        Put(b, 4, 0); uint sum = sfs2 ? 2u : 1u;
        for (var i = 0; i < 512; i += 4) sum = unchecked(sum + U32(b, i));
        Put(b, 4, unchecked(0u - sum));
    }
}
