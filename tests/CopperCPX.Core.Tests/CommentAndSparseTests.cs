using CopperCPX.Reference.Tests;
using CopperFS.Cpx4.Reference;
using System.Text;

namespace CopperCPX.Tests;

public class CommentAndSparseTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory][InlineData(0)][InlineData(79)][InlineData(80)]
    public void CommentInputIsOwnedBoundedAndUsesExistingUtf8AttributeEncoding(int length)
    {
        var disk = EngineTests.Fresh(512); var v = new Cpx4Volume<Disk>(disk, new byte[36864]); v.Create("file"u8, out ulong id);
        var ram = new Ram(new byte[100000]); var e = FileMutationTests.Mount(disk, ram);
        ram.Bytes.AsSpan(90000, length).Fill(0xe9);
        var admitted = e.BeginComment(Wide.From((uint)id), 90000, (uint)length);
        if (length > 79) { Assert.Equal(CpxError.InvalidArgument, admitted); return; }
        Assert.Equal(CpxError.None, admitted); ram.Bytes.AsSpan(90000, length).Fill(0x7f);
        EngineTests.Drive(ref e, ram, disk);
        v = new(disk, new byte[36864]); byte[] comment = new byte[158]; Assert.Equal(length * 2, v.ReadCommentUtf8(id, comment));
        Assert.Equal(Encoding.UTF8.GetBytes(new string('é', length)), comment[..(length * 2)]); new IndependentImage(disk.Bytes);
        e.BeginComment(Wide.From((uint)id), 0, 0); EngineTests.Drive(ref e, ram, disk);
        v = new(disk, new byte[36864]); Assert.Equal(0, v.ReadCommentUtf8(id, comment)); new IndependentImage(disk.Bytes); v.Check();
    }
    [Theory][InlineData(false, 700u)][InlineData(false, 4097u)][InlineData(true, 4097u)]
    public void SparseWritesClearStalePartialUnitTailsAndPreserveOriginalPrefix(bool tree, uint offset)
    {
        var disk = EngineTests.Fresh(512); var v = new Cpx4Volume<Disk>(disk, new byte[36864]); v.Create("file"u8, out ulong id);
        if (tree) for (uint n = 0; n < 7; n++) v.Write(id, n * 1024, new byte[512], out _);
        v.Write(id, 0, Enumerable.Repeat((byte)0x52, 1024).ToArray(), out _); v.Truncate(id, 600);
        var before = new IndependentImage(disk.Bytes); byte[] obj = before.Objects[id];
        // The old tail still contains nonzero data after shrink. Extending
        // directly through Write must not expose it in the newly created gap.
        var ram = new Ram(new byte[100000]); var e = FileMutationTests.Mount(disk, ram); ram.Bytes[90000] = 0x7c;
        e.BeginMappedWrite(Wide.From((uint)id), Wide.From(offset), 90000, 1); EngineTests.Drive(ref e, ram, disk);
        Assert.Equal(1u, e.TransferredBytes); v = new(disk, new byte[36864]); byte[] bytes = new byte[offset + 1];
        Assert.Equal(bytes.Length, v.Read(id, 0, bytes)); Assert.All(bytes[..600], b => Assert.Equal(0x52, b));
        Assert.All(bytes[600..^1], b => Assert.Equal(0, b)); Assert.Equal(0x7c, bytes[^1]); new IndependentImage(disk.Bytes); v.Check();
    }
    [Fact] public void EmptyAndNonemptyInlineFilesAcceptPairedSparseOffsetsAboveFourGiB()
    {
        foreach (bool empty in new[] { false, true })
        {
            var disk = EngineTests.Fresh(512); var v = new Cpx4Volume<Disk>(disk, new byte[36864]); v.Create("wide"u8, out ulong id);
            if (!empty) v.Write(id, 0, "prefix"u8, out _);
            var ram = new Ram(new byte[100000]); var e = FileMutationTests.Mount(disk, ram); ram.Bytes[90000] = 0x65;
            e.BeginMappedWrite(Wide.From((uint)id), new(1, 13), 90000, 1); EngineTests.Drive(ref e, ram, disk);
            v = new(disk, new byte[36864]); byte[] actual = new byte[14]; Assert.Equal(14, v.Read(id, 0x100000000, actual));
            Assert.All(actual[..13], b => Assert.Equal(0, b)); Assert.Equal(0x65, actual[13]); new IndependentImage(disk.Bytes); v.Check();
        }
    }
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)]
    public void CommentCreationReplacementRemovalAndSparseExtensionRecoverAtEveryBoundary(int mode)
    {
        var disk = EngineTests.Fresh(512); var v = new Cpx4Volume<Disk>(disk, new byte[36864]); v.Create("file"u8, out ulong id);
        if (mode is 1 or 2) v.SetComment(id, "old"u8);
        if (mode == 3) { v.Write(id, 0, Enumerable.Repeat((byte)0x52, 1024).ToArray(), out _); v.Truncate(id, 600); }
        void Mutate(IBlockDevice device)
        {
            var ram = new Ram(new byte[100000]); var e = FileMutationTests.Mount(device, ram); "new"u8.CopyTo(ram.Bytes.AsSpan(90000));
            if (mode == 3) e.BeginMappedWrite(Wide.From((uint)id), Wide.From(4097), 90000, 3);
            else e.BeginComment(Wide.From((uint)id), 90000, mode == 2 ? 0u : 3u);
            EngineTests.Drive(ref e, ram, device);
        }
        var trace = new CrashDevice(disk.Bytes); Mutate(trace);
        for (int cut = 1; cut <= trace.Events.Count; cut++) for (int persistence = 0; persistence < 5; persistence++)
        {
            var crash = new CrashDevice(disk.Bytes) { Cut = cut, Persistence = persistence }; Assert.Throws<PowerLoss>(() => Mutate(crash));
            var final = new MemoryDevice(crash.Durable); FileMutationTests.Mount(final, new Ram(new byte[100000]));
            new IndependentImage(final.Bytes); var check = new Cpx4Volume<MemoryDevice>(final, new byte[36864]); check.Check();
            if (mode == 3)
            {
                byte[] data = new byte[4100]; int length = check.Read(id, 0, data); Assert.Contains(length, new[] { 600, 4100 });
                Assert.All(data[..600], b => Assert.Equal(0x52, b));
                if (length == 4100) { Assert.All(data[600..4097], b => Assert.Equal(0, b)); Assert.Equal("new"u8.ToArray(), data[4097..]); }
            }
            else
            {
                byte[] data = new byte[158]; int n = check.ReadCommentUtf8(id, data); string actual = Encoding.UTF8.GetString(data, 0, n);
                Assert.Contains(actual, mode == 0 ? new[] { "", "new" } : mode == 1 ? new[] { "old", "new" } : new[] { "old", "" });
            }
        }
        output.WriteLine($"Comment/sparse mode {mode}: {trace.Events.Count} sector/barrier cuts × 5 persistence schedules.");
    }
}
