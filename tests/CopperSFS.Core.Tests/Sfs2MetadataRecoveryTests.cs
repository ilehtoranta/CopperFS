using System.Numerics;
using System.Text;
using Amiga;
using CopperSFS.Bench;
using Xunit.Abstractions;
using static CopperSFS.AmigaSmartFileSystem2WriteCore;
using static CopperSFS.Tests.Sfs2WriteTests;

namespace CopperSFS.Tests;

public sealed class Sfs2MetadataRecoveryTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("node-growth")]
    [InlineData("admin-expansion")]
    [InlineData("comment-relocation")]
    public void MetadataGrowthAndObjectMovesRemainAtomic(string scenario)
    {
        var seed = new StudyDisk(Empty(), 512); uint target = 0; var name = "Trigger";
        if (scenario == "node-growth")
            for (var i = 0; i < 48; i++) CreateFile(ref seed, $"N{i:D3}");
        else if (scenario == "comment-relocation")
        {
            for (var i = 0; i < 4; i++)
            {
                var node = CreateFile(ref seed, new string('n', 80) + i);
                if (i == 0) target = node;
            }
            PutFile(ref seed, target, 0, StudyFixture.Pattern(1301), true);
        }
        else
        {
            var found = false;
            for (var i = 0; i < 250; i++)
            {
                var before = seed.Snapshot(); name = new string('n', 95) + i.ToString("D3");
                CreateFile(ref seed, name);
                if (U32(seed.Snapshot().AsSpan(8 * 512), 32) != 0)
                { seed = new StudyDisk(before, 512); found = true; break; }
            }
            Assert.True(found, "Fixture did not reach admin expansion.");
        }
        var image = seed.Snapshot(); var beforeManifest = Manifest(image);
        bool Mutate(ref StudyDisk disk)
        {
            var w = WorkFor(disk);
            if (scenario == "comment-relocation")
            {
                Encoding.ASCII.GetBytes(new string('c', 79)).CopyTo(disk.Ram, (int)disk.Payload.Raw);
                var r = new Request { Node = target, Buffer = disk.Payload, Length = 79 };
                return SetMetadata(ref disk, ref w, ref r, DosPacketAction.SetComment);
            }
            Encoding.ASCII.GetBytes(name).CopyTo(disk.Ram, (int)disk.NameBuffer.Raw);
            var create = new Request { Directory = 1, Name = disk.NameBuffer, Length = (uint)name.Length };
            return Create(ref disk, ref w, ref create, false);
        }
        var baseline = new StudyDisk(image, 512, crashModel: true); Assert.True(Mutate(ref baseline));
        var after = baseline.Snapshot(); var afterManifest = Manifest(after);
        if (scenario == "node-growth") Assert.NotEqual(1u, U32(after.AsSpan(6 * 512), 16));
        if (scenario == "admin-expansion") Assert.NotEqual(0u, U32(after.AsSpan(8 * 512), 32));
        if (scenario == "comment-relocation")
        {
            var oldEntry = Inspect(image).Entries.Single(e => e.Node == target);
            var newEntry = Inspect(after).Entries.Single(e => e.Node == target);
            Assert.Equal(oldEntry.Sha256, newEntry.Sha256); Assert.Equal(new string('c', 79), newEntry.Comment);
        }
        long checks = 0;
        void Validate(byte[] saved)
        {
            var disk = new StudyDisk(saved, 512); var w = WorkFor(disk); Assert.True(Recover(ref disk, ref w), w.Error.ToString());
            Assert.Contains(Manifest(disk.Snapshot()), new[] { beforeManifest, afterManifest });
            var stable = disk.Snapshot(); Assert.True(Recover(ref disk, ref w)); Assert.Equal(stable, disk.Snapshot()); checks++;
        }
        for (var cut = 1; cut <= baseline.Events; cut++)
        {
            var disk = new StudyDisk(image, 512, crashModel: true) { CutAfterEvent = cut };
            Assert.Throws<StudyPowerCut>(() => Mutate(ref disk)); foreach (var saved in disk.CrashImages()) Validate(saved);
            foreach (var persistent in new[] { false, true })
            {
                disk = new StudyDisk(image, 512, crashModel: true) { FailAtEvent = cut, PersistentFailure = persistent };
                Assert.False(Mutate(ref disk)); foreach (var saved in disk.CrashImages()) Validate(saved);
            }
        }
        output.WriteLine($"SFS2 metadata {scenario}: {checks} exhaustive image checks.");
    }
}
