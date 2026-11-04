using CopperSFS;
using CopperStart.Dos;

namespace CopperStart.FileSystem.Tests;

public sealed partial class VolumeGeometryTests
{
    [Theory]
    [InlineData(0UL, 0u, 0UL, false)]
    [InlineData(0UL, 0x7FFFFFu, 0xFFFFFE00UL, false)]
    [InlineData(0UL, 0x800000u, 0x100000000UL, true)]
    [InlineData(0xFFFFFE00UL, 0u, 0xFFFFFE00UL, false)]
    [InlineData(0xFFFFFE00UL, 1u, 0x100000000UL, true)]
    [InlineData(0xFFFFFDFFUL, 1u, 0xFFFFFFFFUL, true)]
    public void SingleSectorTransfersPreserveOffsetsAndTd64Boundary(ulong start, uint block, ulong expected, bool extended)
    {
        foreach (var write in new[] { false, true })
        {
            var storage = new Storage();
            Assert.True(AmigaVolumeGeometry.Create(0x1000000, 512, 2, out var geometry));
            geometry.StartByte = start; geometry.MaxTransfer = 512;
            var platform = new Memory(storage, geometry);
            Assert.True(AmigaFileSystemDeviceCore.Transfer(ref platform, geometry, write, block, 1, Payload));
            var transfer = Assert.Single(storage.Transfers);
            Assert.Equal((ushort)(extended ? write ? 25 : 24 : write ? 3 : 2), transfer.Command);
            Assert.Equal(expected, transfer.Offset); Assert.Equal(512u, transfer.Bytes);
            storage.Transfers.Clear();
            Assert.False(AmigaFileSystemDeviceCore.Transfer(ref platform, geometry, write, geometry.BlockCount, 1, Payload));
            Assert.Empty(storage.Transfers);
            geometry.MaxTransfer = 511;
            Assert.False(AmigaFileSystemDeviceCore.Transfer(ref platform, geometry, write, block, 1, Payload));
            Assert.Empty(storage.Transfers);
        }
    }
}
