using CopperCPX.Reference.Tests;
using CopperFS.Cpx4.Reference;
using System.Text;

namespace CopperCPX.Tests;

public class NativeLinkTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory][InlineData(20, false)][InlineData(160, false)][InlineData(161, false)][InlineData(768, false)][InlineData(384, true)]
    public void SoftLinksOwnInlineOrExternalUtf8AndDeleteReclaimsThem(int length, bool latin)
    {
        var disk = EngineTests.Fresh(512); var ram = new Ram(new byte[100000]); var e = FileMutationTests.Mount(disk, ram);
        "link"u8.CopyTo(ram.Bytes.AsSpan(90000)); ram.Bytes.AsSpan(91000, length).Fill(latin ? (byte)0xe9 : (byte)'x');
        Assert.Equal(CpxError.None, e.BeginSymlink(Wide.From(1), 90000, 4, 91000, (uint)length));
        ram.Bytes.AsSpan(91000, length).Clear(); EngineTests.Drive(ref e, ram, disk); Wide id = e.MutatedObject;
        e.BeginReadSymlink(id, 92000, 768); EngineTests.Drive(ref e, ram, disk);
        byte[] expected = Encoding.UTF8.GetBytes(new string(latin ? 'é' : 'x', length));
        Assert.Equal((uint)expected.Length, e.TransferredBytes); Assert.Equal(expected, ram.Bytes.AsSpan(92000, expected.Length).ToArray());
        var reference = new Cpx4Volume<Disk>(disk, new byte[36864]); byte[] data = new byte[768];
        Assert.Equal(expected.Length, reference.ReadSymlinkUtf8(id.Low, data)); Assert.Equal(expected, data[..expected.Length]);
        new IndependentImage(disk.Bytes); reference.Check();
        e.BeginDelete(Wide.From(1), 90000, 4); EngineTests.Drive(ref e, ram, disk);
        Assert.Empty(new IndependentImage(disk.Bytes).Names); new Cpx4Volume<Disk>(disk, new byte[36864]).Check();
    }
    [Fact] public void OverlongEncodedTargetAndEmbeddedNulFailBeforeDiskMutation()
    {
        foreach (bool nul in new[] { false, true })
        {
            var disk = EngineTests.Fresh(512); var ram = new Ram(new byte[100000]); var e = FileMutationTests.Mount(disk, ram);
            byte[] before = (byte[])disk.Bytes.Clone(); "link"u8.CopyTo(ram.Bytes.AsSpan(90000)); ram.Bytes.AsSpan(91000, 385).Fill(0xe9);
            if (nul) ram.Bytes[91001] = 0;
            e.BeginSymlink(Wide.From(1), 90000, 4, 91000, 385);
            FileMutationTests.DriveFailure(ref e, ram, disk, CpxError.InvalidArgument); Assert.Equal(before, disk.Bytes);
        }
    }
    [Fact] public void HardLinksShareObjectOwnershipUntilTheLastNameIsDeleted()
    {
        var disk = EngineTests.Fresh(512); var reference = new Cpx4Volume<Disk>(disk, new byte[36864]); reference.Put("file"u8, "content"u8);
        var ram = new Ram(new byte[100000]); var e = FileMutationTests.Mount(disk, ram);
        byte[] name = Encoding.Latin1.GetBytes(new string('é', 79)); name.CopyTo(ram.Bytes, 91000); "file"u8.CopyTo(ram.Bytes.AsSpan(90000));
        Assert.Equal(CpxError.None, e.BeginHardLink(Wide.From(2), Wide.From(1), 91000, (uint)name.Length)); EngineTests.Drive(ref e, ram, disk);
        var decoded = new IndependentImage(disk.Bytes); Assert.Equal(2u, IndependentImage.I(decoded.Objects[2], 76)); Assert.Equal(2, decoded.Names.Count);
        e.BeginDelete(Wide.From(1), 90000, 4); EngineTests.Drive(ref e, ram, disk);
        reference = new(disk, new byte[36864]); Assert.True(reference.TryGetInfo(name, out var info)); byte[] data = new byte[7]; Assert.Equal(7, reference.Read(info.Id, 0, data)); Assert.Equal("content"u8.ToArray(), data);
        new IndependentImage(disk.Bytes); reference.Check();
        e.BeginDelete(Wide.From(1), 91000, (uint)name.Length); EngineTests.Drive(ref e, ram, disk);
        Assert.Single(new IndependentImage(disk.Bytes).Objects); new Cpx4Volume<Disk>(disk, new byte[36864]).Check();
    }
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)]
    public void LinkPublicationAndOwnershipRecoverAtEverySectorAndBarrier(int mode)
    {
        var original = EngineTests.Fresh(512); var reference = new Cpx4Volume<Disk>(original, new byte[36864]); reference.Put("file"u8, "content"u8);
        void Mutate(IBlockDevice disk)
        {
            var ram = new Ram(new byte[100000]); var e = FileMutationTests.Mount(disk, ram); "link"u8.CopyTo(ram.Bytes.AsSpan(90000)); ram.Bytes.AsSpan(91000, 200).Fill((byte)'x');
            Assert.Equal(CpxError.None, mode == 0 ? e.BeginHardLink(Wide.From(2), Wide.From(1), 90000, 4) : e.BeginSymlink(Wide.From(1), 90000, 4, 91000, mode == 1 ? 20u : 200u));
            EngineTests.Drive(ref e, ram, disk);
        }
        var trace = new CrashDevice(original.Bytes); Mutate(trace);
        for (int cut = 1; cut <= trace.Events.Count; cut++) for (int persistence = 0; persistence < 5; persistence++)
        {
            var crash = new CrashDevice(original.Bytes) { Cut = cut, Persistence = persistence }; Assert.Throws<PowerLoss>(() => Mutate(crash));
            var final = new MemoryDevice(crash.Durable); FileMutationTests.Mount(final, new Ram(new byte[100000]));
            var decoded = new IndependentImage(final.Bytes); Assert.Equal("content"u8.ToArray(), decoded.Read(decoded.Names[(1, "file")]));
            var check = new Cpx4Volume<MemoryDevice>(final, new byte[36864]); check.Check();
            if (mode != 0 && check.TryGetInfo("link"u8, out var info))
            { byte[] data = new byte[200]; int length = check.ReadSymlinkUtf8(info.Id, data); Assert.Equal(mode == 1 ? 20 : 200, length); Assert.All(data[..length], b => Assert.Equal((byte)'x', b)); }
        }
        output.WriteLine($"Link mode {mode}: {trace.Events.Count} cuts × 5 schedules.");
    }
}
