using CopperCPX.Qualification;
using CopperCPX.Reference.Tests;

namespace CopperCPX.Tests;

/// <summary>Classic DOS contracts, independent of the record-pool implementation.</summary>
public class RecordLockPacketTests
{
    private static readonly (uint, uint) Ok = (uint.MaxValue, 0);
    internal static uint Open(DispatcherDriver d, string name = "file")
    {
        Assert.Equal(Ok, d.Packet(1004, DispatcherDriver.FileHandle >> 2, 0, d.Bstr(name)));
        return d.Ram.Read32(DispatcherDriver.FileHandle + 36);
    }
    internal static void Close(DispatcherDriver d, uint handle) => Assert.Equal(Ok, d.Packet(1007, handle));
    internal static (uint Result, uint Error) Lock(DispatcherDriver d, uint h, uint offset, uint length, uint mode = 1, uint ticks = 0)
        => d.Packet(2008, h, offset, length, mode, ticks);
    internal static void Unlock(DispatcherDriver d, uint h, uint offset, uint length)
        => Assert.Equal(Ok, d.Packet(2009, h, offset, length));

    [Theory]
    [InlineData(1u, 1u, false)] [InlineData(1u, 3u, false)]
    [InlineData(3u, 1u, false)] [InlineData(3u, 3u, true)]
    public void SharedAndExclusiveConflictsAreByFileAndIntersectingRange(uint first, uint second, bool compatible)
    {
        using var d = new DispatcherDriver(512);
        uint a = Open(d), b = Open(d), other = Open(d, "other");
        byte[] before = (byte[])d.Image.Clone();
        Assert.Equal(Ok, Lock(d, a, 100, 20, first));
        Assert.Equal(compatible ? Ok : (0u, 241u), Lock(d, b, 119, 1, second, uint.MaxValue));
        if (compatible) Unlock(d, b, 119, 1);
        // Adjacent half-open ranges do not overlap; unrelated files never conflict.
        Assert.Equal(Ok, Lock(d, b, 120, 20, second)); Unlock(d, b, 120, 20);
        Assert.Equal(Ok, Lock(d, b, 99, 1, second)); Unlock(d, b, 99, 1);
        Assert.Equal(Ok, Lock(d, other, 100, 20, second)); Unlock(d, other, 100, 20);
        Unlock(d, a, 100, 20); Close(d, a); Close(d, b); Close(d, other);
        Assert.Equal(before, d.Image); Assert.False(d.Dispatcher.HasOpenResources);
        new IndependentImage(d.Image);
    }

    [Fact]
    public void RecursiveSameHandleLocksRequireOneExactUnlockPerAcquisition()
    {
        using var d = new DispatcherDriver(512); uint a = Open(d), b = Open(d);
        Assert.Equal(Ok, Lock(d, a, 10, 30)); Assert.Equal(Ok, Lock(d, a, 10, 30));
        Assert.Equal((0u, 240u), d.Packet(2009, b, 10, 30));
        Assert.Equal((0u, 240u), d.Packet(2009, a, 10, 29));
        Assert.Equal((0u, 240u), d.Packet(2009, a, 11, 30));
        Unlock(d, a, 10, 30); Assert.Equal((0u, 241u), Lock(d, b, 10, 30));
        Unlock(d, a, 10, 30); Assert.Equal((0u, 240u), d.Packet(2009, a, 10, 30));
        Assert.Equal(Ok, Lock(d, b, 10, 30)); Close(d, b);
        Assert.Equal(Ok, Lock(d, a, 10, 30)); Close(d, a);
        Assert.False(d.Dispatcher.HasOpenResources);
    }

    [Fact]
    public void ZeroTimeoutAndInvalidModesDoNotParkOrConsumeSlots()
    {
        using var d = new DispatcherDriver(512); uint a = Open(d), b = Open(d);
        Assert.Equal(Ok, Lock(d, a, 0, 1));
        foreach (uint mode in new[] { 0u, 2u }) Assert.Equal((0u, 242u), Lock(d, b, 0, 1, mode));
        foreach (uint mode in new[] { 4u, uint.MaxValue }) Assert.Equal((0u, 115u), Lock(d, b, 0, 1, mode));
        Assert.Equal((0u, 211u), Lock(d, 123, 0, 1));
        Assert.Equal((0u, 211u), d.Packet(2009, 123, 0, 1));
        for (uint n = 1; n < 32; n++) Assert.Equal(Ok, Lock(d, a, n, 1));
        Assert.Equal((0u, 103u), Lock(d, a, 32, 1));
        Close(d, a); Close(d, b); Assert.False(d.Dispatcher.HasOpenResources);
    }

    [Fact]
    public void LastRecordSlotAndExhaustionPreserveExistingLocksAndSlotsAreReusable()
    {
        using var d = new DispatcherDriver(512); uint a = Open(d), b = Open(d);
        for (uint n = 0; n < 32; n++) Assert.Equal(Ok, Lock(d, a, n * 4, 2));
        Assert.Equal((0u, 103u), Lock(d, a, 1000, 1));
        Assert.Equal((0u, 241u), Lock(d, b, 124, 2));
        Unlock(d, a, 124, 2); Assert.Equal(Ok, Lock(d, b, 124, 2));
        Assert.Equal((0u, 103u), Lock(d, a, 1000, 1));
        Close(d, a); Close(d, b);
        a = Open(d);
        for (uint n = 0; n < 32; n++) Assert.Equal(Ok, Lock(d, a, n, 1));
        Close(d, a); Assert.False(d.Dispatcher.HasOpenResources);
    }

    [Fact]
    public void RecordLocksAreCooperativeAndDoNotBlockOrdinaryIo()
    {
        using var d = new DispatcherDriver(512); uint a = Open(d), b = Open(d);
        Assert.Equal(Ok, Lock(d, a, 0, 10)); d.Ram.Write8(90000, 73);
        Assert.Equal((1u, 0u), d.Packet(87, b, 90000, 1));
        Assert.Equal((1u, 0u), d.Packet(82, a, 91000, 1)); Assert.Equal(73, d.Ram.Read8(91000));
        Assert.Equal((0u, 241u), Lock(d, b, 0, 1));
        Close(d, a); Assert.Equal(Ok, Lock(d, b, 0, 1)); Close(d, b);
        new IndependentImage(d.Image);
    }

    [Fact]
    public void HardLinkAliasesShareRecordConflictIdentity()
    {
        using var d = new DispatcherDriver(512); uint a = Open(d);
        uint fileLock = d.Packet(8, 0, d.Bstr("file"), 0xfffffffe).Result; Assert.NotEqual(0u, fileLock);
        Assert.Equal(Ok, d.Packet(1021, 0, d.Bstr("alias"), fileLock, 0)); Assert.Equal(Ok, d.Packet(15, fileLock));
        uint b = Open(d, "alias"); Assert.Equal(Ok, Lock(d, a, 0, 1));
        Assert.Equal((0u, 241u), Lock(d, b, 0, 1)); Close(d, a);
        Assert.Equal(Ok, Lock(d, b, 0, 1)); Close(d, b); new IndependentImage(d.Image);
    }

    private static (uint Result, uint Error) WidePacket(DispatcherDriver d, uint action, uint handle, ulong offset, ulong length, uint mode = 1)
    {
        d.Ram.Write32(92000, (uint)(offset >> 32)); d.Ram.Write32(92004, (uint)offset);
        d.Ram.Write32(92008, (uint)(length >> 32)); d.Ram.Write32(92012, (uint)length);
        return d.Packet(action, handle, 92000, 92008, mode);
    }

    [Fact]
    public void WideRangesUseBothHalvesAndCarryAcrossFourGiB()
    {
        using var d = new DispatcherDriver(512); uint a = Open(d), b = Open(d);
        const ulong start = 0xfffffff8, length = 16;
        Assert.Equal(Ok, WidePacket(d, 26402, a, start, length));
        Assert.Equal((0u, 241u), WidePacket(d, 26402, b, 0x100000000, 1));
        Assert.Equal(Ok, WidePacket(d, 26402, b, 0x100000008, 1));
        Assert.Equal(Ok, WidePacket(d, 26403, b, 0x100000008, 1));
        Assert.Equal(Ok, Lock(d, b, 0, 1)); Unlock(d, b, 0, 1);
        Assert.Equal((0u, 240u), d.Packet(2009, a, (uint)start, (uint)length + 1));
        Assert.Equal(Ok, WidePacket(d, 26403, a, start, length));
        Assert.Equal(Ok, WidePacket(d, 26402, a, 1ul << 48, (1ul << 32) + 2));
        Assert.Equal((0u, 241u), WidePacket(d, 26402, b, (1ul << 48) + (1ul << 32), 1));
        Assert.Equal(Ok, WidePacket(d, 26403, a, 1ul << 48, (1ul << 32) + 2));
        Close(d, a); Close(d, b);
    }

    [Fact]
    public void InvalidWidePointersAndOverflowNeverConsumeRecordCapacity()
    {
        using var d = new DispatcherDriver(512); uint a = Open(d);
        foreach (uint pointer in new[] { 0u, 92001u, 0xfffffffcu })
        {
            Assert.Equal((0u, 115u), d.Packet(26402, a, pointer, 92008, 1));
            Assert.Equal((0u, 115u), d.Packet(26403, a, 92000, pointer));
        }
        Assert.Equal((0u, 115u), WidePacket(d, 26402, a, ulong.MaxValue - 2, 4));
        for (uint n = 0; n < 32; n++) Assert.Equal(Ok, Lock(d, a, n, 1));
        Close(d, a);
    }

    [Fact]
    public void ZeroLengthLocksDoNotConflictButRetainExactReleaseOwnership()
    {
        using var d = new DispatcherDriver(512); uint a = Open(d), b = Open(d);
        Assert.Equal(Ok, Lock(d, a, 10, 0)); Assert.Equal(Ok, Lock(d, b, 0, 20));
        Unlock(d, a, 10, 0); Unlock(d, b, 0, 20); Close(d, a); Close(d, b);
    }
}
