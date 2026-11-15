using CopperFS.Prototype;
using static CopperFS.Prototype.Tests.DesignTests;

namespace CopperFS.Prototype.Tests;

public class AppendTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static MemoryDevice UnwrittenTail(int allocation, int extentCount, out ulong id, out int length)
    {
        var d = new MemoryDevice();
        // Unwritten storage must not depend on new/discarded sectors reading as zero.
        d.Bytes.AsSpan().Fill(0xa5);
        PrototypeVolume<MemoryDevice>.Format(d, allocation, Workspace(allocation), 12345);
        var v = new PrototypeVolume<MemoryDevice>(d, Workspace(allocation));
        Assert.Equal(MutationResult.Success, v.Create("file"u8, out id));
        for (int i = 0; i < extentCount; i++)
            Assert.Equal(MutationResult.Success, v.Preallocate(id, (ulong)(i * 4 * allocation), i == extentCount - 1 ? 3u : 1u, out _));
        if (extentCount > 1) Assert.Equal(MutationResult.Success, v.Write(id, 0, "keep"u8, out _));
        length = extentCount == 1 ? 103 : (extentCount - 1) * 4 * allocation + allocation + 103;
        Assert.Equal(MutationResult.Success, v.Truncate(id, (ulong)length));
        Assert.True(v.TryGetInfo("file"u8, out var info));
        Assert.Equal((ulong)extentCount, info.ExtentCount);
        v.Check(); new IndependentImage(d.Bytes);
        return d;
    }

    [Theory]
    [InlineData(512, 1, 0)] [InlineData(1024, 1, 27)]
    [InlineData(2048, 6, 0)] [InlineData(4096, 6, 27)]
    [InlineData(512, 9, 27)] [InlineData(4096, 9, 0)]
    public void AppendConvertsRetainedUnwrittenTailAndPreservesZeros(int allocation, int extentCount, int gap)
    {
        var d = UnwrittenTail(allocation, extentCount, out ulong id, out int length);
        var v = new PrototypeVolume<MemoryDevice>(d, Workspace(allocation));
        byte[] before = new IndependentImage(d.Bytes).Read(id), payload = "hello"u8.ToArray();
        int offset = length + gap;
        byte[] expected = new byte[offset + payload.Length]; before.CopyTo(expected, 0); payload.CopyTo(expected, offset);

        Assert.Equal(MutationResult.Success, v.Append(id, (ulong)offset, payload, out int written));
        Assert.Equal(payload.Length, written);
        byte[] actual = new byte[expected.Length]; Assert.Equal(actual.Length, v.Read(id, 0, actual)); Assert.Equal(expected, actual);
        Assert.Equal(expected, new IndependentImage(d.Bytes).Read(id)); v.Check();
        Assert.True(v.TryGetInfo("file"u8, out var info));
        // Splitting the last of six inline descriptors must also promote the extent tree.
        Assert.Equal((ulong)(extentCount + (extentCount > 1 ? 1 : 0)), info.ExtentCount);

        var reopened = new PrototypeVolume<MemoryDevice>(d, Workspace(allocation));
        Assert.Equal(actual.Length, reopened.Read(id, 0, actual)); Assert.Equal(expected, actual);
        // A later extension must not expose the old nonzero bytes after the append.
        int grown = (offset / allocation + 1) * allocation;
        Assert.Equal(MutationResult.Success, reopened.Truncate(id, (ulong)grown));
        Array.Resize(ref expected, grown);
        Assert.Equal(expected, new IndependentImage(d.Bytes).Read(id)); reopened.Check();
    }

    [Theory]
    [InlineData(512, 1, 0)] [InlineData(4096, 1, 27)]
    [InlineData(512, 6, 27)] [InlineData(512, 9, 0)]
    public void UnwrittenTailAppendSurvivesEverySectorAndBarrier(int allocation, int extentCount, int gap)
    {
        var initial = UnwrittenTail(allocation, extentCount, out ulong id, out int length);
        byte[] before = new IndependentImage(initial.Bytes).Read(id), payload = Pattern(23);
        int offset = length + gap;
        byte[] expected = new byte[offset + payload.Length]; before.CopyTo(expected, 0); payload.CopyTo(expected, offset);
        var trace = new CrashDevice(initial.Bytes); Append(trace);
        Assert.Equal(expected, new IndependentImage(trace.Durable).Read(id));
        output.WriteLine($"{trace.Events.Count} unwritten append cuts x 5 persistence schedules = {trace.Events.Count * 5} crash recoveries.");

        for (int cut = 1; cut <= trace.Events.Count; cut++) for (int mode = 0; mode < 5; mode++)
        {
            var fault = new CrashDevice(initial.Bytes) { Cut = cut, Persistence = mode };
            Assert.Throws<PowerLoss>(() => Append(fault));
            var d = new MemoryDevice(fault.Durable);
            var recovered = new PrototypeVolume<MemoryDevice>(d, Workspace(allocation));
            recovered.Check(); byte[] actual = new IndependentImage(d.Bytes).Read(id);
            Assert.True(actual.AsSpan().SequenceEqual(before) || actual.AsSpan().SequenceEqual(expected), $"cut={cut}, persistence={mode}");
            int writes = d.Writes;
            var again = new PrototypeVolume<MemoryDevice>(d, Workspace(allocation));
            Assert.Equal(recovered.Generation, again.Generation); Assert.Equal(writes, d.Writes);
            // Reuse the journal and any remaining tail after recovery.
            Assert.Equal(MutationResult.Success, again.Append(id, (ulong)actual.Length, "next"u8, out int appended));
            Assert.Equal(4, appended);
            byte[] next = new byte[actual.Length + 4]; actual.CopyTo(next, 0); "next"u8.CopyTo(next.AsSpan(actual.Length));
            Assert.Equal(next, new IndependentImage(d.Bytes).Read(id));
        }
        void Append(CrashDevice device)
        {
            var volume = new PrototypeVolume<CrashDevice>(device, Workspace(allocation));
            Assert.Equal(MutationResult.Success, volume.Append(id, (ulong)offset, payload, out int written));
            Assert.Equal(payload.Length, written);
        }
    }
}
