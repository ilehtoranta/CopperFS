using CopperFS.Prototype;
using static CopperFS.Prototype.Layout;
using static CopperFS.Prototype.Tests.DesignTests;

namespace CopperFS.Prototype.Tests;

public class GeometryTests
{
    [Fact] public void UnitAddressAndLogicalOffsetCarryPast32BitsWithoutWrapping()
    {
        var d = Fresh(); var v = new PrototypeVolume<MemoryDevice>(d, Workspace()); v.Put("wide"u8, Pattern(512));
        byte[] anchor = d.Bytes.AsSpan(65536, 1024).ToArray(); ulong units = (1UL << 32) + 8192, physical = (1UL << 32) + 100, logical = (1UL << 32) + 7;
        Put64(anchor, 88, units); Seal(anchor); anchor.CopyTo(d.Bytes, 65536);
        var objectPage = d.Bytes.AsSpan(2097152, 1024); var obj = objectPage.Slice(64 + 256, 256);
        Put64(obj, 8, (logical + 1) * 512); Put64(obj, 96, logical); Put64(obj, 104, physical); Seal(objectPage);
        var sparse = new SparseDevice(d.Bytes, units, physical, Pattern(512));
        var mounted = new PrototypeVolume<SparseDevice>(sparse, Workspace(), true); byte[] actual = new byte[512];
        Assert.Equal(512, mounted.Read("wide"u8, logical * 512, actual)); Assert.Equal(Pattern(512), actual); Assert.Equal(physical, sparse.HighestRead);
        Assert.Equal(512, mounted.Read("wide"u8, (logical - 1) * 512, actual)); Assert.All(actual, b => Assert.Equal(0, b));
    }
    private sealed class SparseDevice(byte[] low, ulong units, ulong payloadAddress, byte[] payload) : IBlockDevice
    {
        public int SectorSize => 512;
        public ulong SectorCount => units;
        internal ulong HighestRead;
        public void Read(ulong at, Span<byte> bytes)
        {
            if (at > units || (ulong)bytes.Length / 512 > units - at) throw new IOException();
            bytes.Clear();
            if (at == payloadAddress) { payload.CopyTo(bytes); HighestRead = at; }
            else if (at * 512 < (ulong)low.Length && (ulong)bytes.Length <= (ulong)low.Length - at * 512) low.AsSpan((int)(at * 512), bytes.Length).CopyTo(bytes);
        }
        public void Write(ulong at, ReadOnlySpan<byte> bytes) => throw new InvalidOperationException("Read-only fixture.");
        public void Flush() => throw new InvalidOperationException("Read-only fixture.");
    }
    [Fact] public void InvalidGeometryAndConflictingAnchorsFailClosed()
    {
        var d = Fresh(); Put64(d.Bytes, d.Bytes.Length - 1024 + 72, 999); Seal(d.Bytes.AsSpan(d.Bytes.Length - 1024));
        try { var unused = new PrototypeVolume<MemoryDevice>(d, Workspace()); Assert.Fail("Accepted conflicting anchors."); } catch (InvalidDataException) { }
        Assert.Throws<ArgumentException>(() => PrototypeVolume<MemoryDevice>.Format(new MemoryDevice(megabytes: 2), 512, Workspace(), 1));
        Assert.Throws<ArgumentException>(() => new Layout(4096, 512, ulong.MaxValue, 1).Validate());
    }
    [Theory] [InlineData(512)] [InlineData(4096)]
    public void FinalPartialGroupAndBackupAreAlwaysReserved(int allocation)
    {
        var d = Fresh(allocation, megabytes: 33); var v = new PrototypeVolume<MemoryDevice>(d, Workspace(allocation));
        Assert.Equal(MutationResult.Success, v.Put("file"u8, Pattern(20000))); v.Check(); new IndependentImage(d.Bytes);
    }
}
