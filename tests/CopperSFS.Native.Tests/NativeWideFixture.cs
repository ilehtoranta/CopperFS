using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CopperSFS.Native.Tests;

// Sparse host storage for a normally allocated SFS2 file. Qualification inspects
// affected structures and data; it does not scan the entire 136 GiB device.
internal static class NativeWideFixture
{
    internal const uint Blocks = 0x11000000, First = 0x10000000, ExtentBlocks = 0x800001, MapBase = 128;
    internal const ulong OldSize = (ulong)ExtentBlocks * 512 - 3;
    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
    private static void U32(byte[] b, int at, uint v) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at), v);
    private static void U16(byte[] b, int at, ushort v) => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at), v);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, int inputBytes,
        IntPtr output, int outputBytes, out int returned, IntPtr overlapped);

    internal static void Create(string path)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        if (OperatingSystem.IsWindows() && !DeviceIoControl(file.SafeFileHandle, 0x900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        file.SetLength((long)Blocks * 512);
        var image = new Dictionary<uint, byte[]>();
        byte[] Header(uint n, uint id) { var b = new byte[512]; U32(b, 0, id); U32(b, 8, n); image[n] = b; return b; }
        foreach (var n in new[] { 0u, Blocks - 1 })
        {
            var b = Header(n, 0x53465302); U16(b, 12, 4); U32(b, 48, Blocks); U32(b, 52, 512);
            U32(b, 96, MapBase); U32(b, 100, 8); U32(b, 104, 2); U32(b, 108, 5); U32(b, 112, 6);
        }
        var root = Header(2, 0x4F424A43); U32(root, 28, 1); U32(root, 32, 15); U32(root, 40, 7);
        root[50] = 128; root[51] = (byte)'R'; U32(root, 484, 97);
        Header(4, 0x54524F4B);
        var tree = Header(5, 0x424E4443); U16(tree, 12, 1); tree[14] = 1; tree[15] = 16;
        U32(tree, 16, First); U32(tree, 24, 0x80000002); U32(tree, 28, ExtentBlocks);
        var nodes = Header(6, 0x4E444320); U32(nodes, 12, 1); U32(nodes, 16, 1); U32(nodes, 20, 2); U32(nodes, 30, 7);
        var obj = Header(7, 0x4F424A43); U32(obj, 12, 1); U32(obj, 28, 2); U32(obj, 32, 31); U32(obj, 36, First);
        U32(obj, 40, (uint)(OldSize >> 16)); U16(obj, 44, (ushort)(OldSize & 65535));
        "Wide"u8.CopyTo(obj.AsSpan(51));
        var admin = Header(8, 0x41444D43); admin[20] = 32; U32(admin, 24, 1); U32(admin, 28, 0x7F000000);
        var map0 = Header(MapBase, 0x42544D50);
        for (uint b = 33; b < 128; b++) map0[12 + b / 8] |= (byte)(128 >> (int)(b % 8));
        var map = Header(MapBase + (First + ExtentBlocks) / 4000, 0x42544D50);
        for (uint b = First + ExtentBlocks; b < First + ExtentBlocks + 2; b++) map[12 + b % 4000 / 8] |= (byte)(128 >> (int)(b % 8));
        foreach (var (number, b) in image)
        {
            uint sum = 2; for (var at = 0; at < 512; at += 4) sum = unchecked(sum + U32(b, at));
            U32(b, 4, unchecked(0u - sum)); file.Position = (long)number * 512; file.Write(b);
        }
        file.Flush(true);
    }

    internal static void Verify(string path)
    {
        using var file = File.OpenRead(path);
        byte[] Block(uint number)
        {
            var b = new byte[512]; file.Position = (long)number * 512; file.ReadExactly(b);
            uint sum = 0; for (var at = 0; at < 512; at += 4) sum = unchecked(sum + U32(b, at));
            Assert.Equal(0xFFFFFFFEu, sum); Assert.Equal(number, U32(b, 8)); return b;
        }
        Assert.Equal(0x54524F4Bu, U32(Block(4), 0));
        var obj = Block(7); Assert.Equal(OldSize, ((ulong)U32(obj, 40) << 16) | BinaryPrimitives.ReadUInt16BigEndian(obj.AsSpan(44)));
        Assert.Equal(ExtentBlocks, U32(Block(5), 28));
        Assert.Equal(97u, U32(Block(2), 484));
        var map = Block(MapBase + (First + ExtentBlocks) / 4000); var bit = (First + ExtentBlocks) % 4000;
        Assert.NotEqual(0, map[12 + bit / 8] & (128 >> (int)(bit % 8)));
        var bytes = new byte[512]; file.Position = (long)First * 512 + 0xFFFFFFFD; file.ReadExactly(bytes);
        for (var i = 0; i < bytes.Length; i++) Assert.Equal((byte)(i * 29 + 7), bytes[i]);
    }
}
