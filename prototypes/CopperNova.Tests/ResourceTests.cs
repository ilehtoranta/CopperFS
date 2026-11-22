using CopperCPX;
using CopperNova.Prototype;
using CopperNova.Reference;
using static CopperNova.Tests.FilesystemTests;

namespace CopperNova.Tests;

public class ResourceTests
{
    [Fact]
    public void LiveHandlesAndLocksGrowAndReleaseWithoutAcceptingStaleTokens()
    {
        var h = new Harness(NovaImage.FormatPacked());
        h.Mount();
        uint writer = h.Open("held");
        h.Write(writer, new byte[] { 17, 33 });
        h.Close(writer);

        uint[] handles = Enumerable.Range(0, 100).Select(_ => h.Open("held", 1005)).ToArray();
        h.Bstr(Harness.NameAt, "held");
        uint[] locks = Enumerable.Range(0, 100).Select(_ => h.Packet(8, 0, Harness.NameAt >> 2, 0xfffffffe)).ToArray();
        Assert.Equal(100, locks.Distinct().Count());
        Assert.DoesNotContain(0u, locks);
        Assert.Equal(0u, h.Packet(16, 0, Harness.NameAt >> 2));
        Assert.Equal(202u, h.Engine.DosError);
        Assert.Equal(0u, h.Packet(31, 1));
        Assert.Equal(202u, h.Engine.DosError);

        uint staleHandle = handles[0];
        h.Close(staleHandle);
        uint replacement = h.Open("held", 1005);
        Assert.NotEqual(staleHandle, replacement);
        Assert.Equal(0u, h.Packet(1007, staleHandle));
        Assert.Equal(211u, h.Engine.DosError);
        // Copying file data into either live record must be rejected.
        Assert.Equal(uint.MaxValue, h.Packet(82, replacement, locks[99] << 2, 1));
        Assert.Equal(115u, h.Engine.DosError);
        Assert.Equal(2u, h.Packet(82, replacement, Harness.Data, 2));
        Assert.Equal((byte)17, h.Memory.Read8(Harness.Data));
        h.Close(replacement);

        foreach (uint handle in handles.Skip(1).Reverse()) h.Close(handle);
        h.Bstr(Harness.NameAt, "held");
        Assert.Equal(0u, h.Packet(16, 0, Harness.NameAt >> 2));
        Assert.Equal(202u, h.Engine.DosError);
        foreach (uint locked in locks.Reverse()) Assert.Equal(uint.MaxValue, h.Packet(15, locked));
        Assert.Equal(0u, h.Packet(15, locks[0]));
        Assert.Equal(211u, h.Engine.DosError);
        Assert.False(h.Engine.HasResources);
        h.InhibitAndRemount();
    }

    [Fact]
    public void ResourceFailureLeavesFilesAndNamespaceIntact()
    {
        var h = new Harness(NovaImage.FormatPacked());
        h.Mount();
        uint writer = h.Open("keep");
        h.Write(writer, [42]);
        h.Close(writer);
        h.Flush();
        byte[] before = (byte[])h.Disk.Clone();
        h.BeforeIo = io => Assert.Equal(IoKind.Read, io.Kind);
        h.FailResourceAllocation = true;
        Assert.Equal(0u, h.OpenRaw("keep", 1006));
        Assert.Equal(103u, h.Engine.DosError);
        Assert.Equal(0u, h.OpenRaw("missing", 1006));
        Assert.Equal(103u, h.Engine.DosError);
        Assert.Equal(0u, h.Packet(22, 0, Harness.NameAt >> 2));
        Assert.Equal(103u, h.Engine.DosError);
        Assert.Equal(0u, h.Packet(8, 0, Harness.NameAt >> 2, 0xfffffffe));
        Assert.Equal(103u, h.Engine.DosError);
        Assert.False(h.Engine.HasResources);
        Assert.Equal(before, h.Disk);
        h.FailResourceAllocation = false;
        uint handle = h.Open("keep", 1005);
        Assert.Equal(1u, h.Packet(82, handle, Harness.Data, 1));
        Assert.Equal((byte)42, h.Memory.Read8(Harness.Data));
        h.Close(handle);
        Assert.Equal(0u, h.OpenRaw("missing", 1005));
        Assert.Equal(205u, h.Engine.DosError);
    }
}
