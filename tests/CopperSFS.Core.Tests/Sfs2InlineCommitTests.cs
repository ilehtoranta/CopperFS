using System.Reflection;
using Amiga;
using CopperSFS.Bench;
using static CopperSFS.AmigaSmartFileSystem2WriteCore;
using static CopperSFS.Tests.Sfs2WriteTests;

namespace CopperSFS.Tests;

public sealed class Sfs2InlineCommitTests
{
    [Theory]
    [InlineData(120, 4, false, 512u, true)] // Exactly full page.
    [InlineData(120, 4, true, 512u, false)] // Next record on next page.
    [InlineData(119, 4, true, 512u, false)] // Split header at 508.
    [InlineData(119, 5, true, 512u, false)] // Split header at 510.
    [InlineData(128, 1, false, 512u, false)] // Split literal.
    [InlineData(120, 4, false, 256u, false)] // Multi-sector block.
    public void InlineOrPromotedCommitSurvivesEveryCutAndFailure(int literals, int pieces,
        bool second, uint sectorBytes, bool inline)
    {
        var initial = Empty(); var marker = U32(initial, 104) + 2;
        var targets = second ? new uint[] { 40, 41 } : [40u];
        var images = second ? new[] { Payload(literals, pieces), Enumerable.Repeat((byte)255, 512).ToArray() }
            : [Payload(literals, pieces)];
        StudyDisk New() => new(initial, 512, crashModel: true, sectorBytes: sectorBytes)
            { WrittenBlocks = [], FlushWriteCounts = [] };
        void Check(byte[] saved, bool final = false)
        {
            var disk = new StudyDisk(saved, 512); var work = WorkFor(disk);
            Assert.True(Recover(ref disk, ref work), work.Error.ToString());
            var result = disk.Snapshot();
            var old = targets.All(t => result.AsSpan((int)t * 512, 512).SequenceEqual(initial.AsSpan((int)t * 512, 512)));
            var complete = targets.Select((t, i) => result.AsSpan((int)t * 512, 512).SequenceEqual(images[i])).All(v => v);
            Assert.True(complete || !final && old);
            Assert.Equal(0x54524F4Bu, U32(result, (int)marker * 512));
        }
        var baseline = New(); Assert.True(Commit(ref baseline, targets, images)); Check(baseline.Snapshot(), true);
        var markers = baseline.WrittenBlocks!.Where(b => b.Block == marker).ToArray();
        Assert.Equal(2, markers.Length);
        Assert.Equal(inline ? 0x54525354u : 0x54524641u, U32(markers[0].Data, 0));
        Assert.Equal(4, baseline.Counts.Flushes);
        if (inline) Assert.Equal(new long[] { 0, 1, 1 + targets.Length, 2 + targets.Length }, baseline.FlushWriteCounts);
        var pages = baseline.WrittenBlocks!.Where(b => U32(b.Data, 0) == 0x54525354).ToArray();
        Assert.Equal(second || literals == 128 ? 2 : 1, pages.Length);
        foreach (var page in pages)
        {
            Assert.Equal(page.Block, U32(page.Data, 8));
            Assert.Equal(0xFFFFFFFEu, Enumerable.Range(0, 128).Aggregate(0u, (sum, i) => unchecked(sum + U32(page.Data, i * 4))));
        }
        if (!inline) Assert.DoesNotContain(pages, p => p.Block == marker || targets.Contains(p.Block));
        for (var cut = 1; cut <= baseline.Events; cut++)
        {
            var disk = New(); disk.CutAfterEvent = cut;
            Assert.Throws<StudyPowerCut>(() => Commit(ref disk, targets, images));
            foreach (var saved in disk.CrashImages()) Check(saved);
        }
        for (var failure = 1; failure <= baseline.Events; failure++)
        {
            var disk = New(); disk.FailAtEvent = failure;
            Assert.False(Commit(ref disk, targets, images));
            foreach (var saved in disk.CrashImages()) Check(saved);
        }
    }

    [Fact]
    public void UnpublishedDataIsDurableBeforeInlinePublication()
    {
        var seed = new StudyDisk(Empty(), 512); var node = CreateFile(ref seed, "Data");
        PutFile(ref seed, node, 0, StudyFixture.Pattern(1024, 723));
        var image = seed.Snapshot(); var disk = new StudyDisk(image, 512, crashModel: true)
            { WrittenBlocks = [], FlushWriteCounts = [] };
        var work = WorkFor(disk);
        Assert.True(Sfs2MountContext.Mount(ref disk, ref work, APTR.FromPointer(disk.Payload.Raw + 900000), 16384));
        Assert.True(InitializeTrstGroup(ref disk, disk.BatchBuffer, 65536));
        disk.Ram.AsSpan((int)disk.Payload.Raw, 1024).Fill(77);
        var request = new Request { Node = node, Position = 1024, Buffer = disk.Payload, Length = 1024 };
        Assert.True(ExecuteTrstGroup(ref disk, ref work, ref request, TrstOperation.Write, disk.BatchBuffer, out _), work.Error.ToString());
        var dataWrites = disk.WrittenBlocks!.Count; Assert.True(dataWrites > 0);
        Assert.Empty(disk.FlushWriteCounts!);
        Assert.True(FlushTrstGroup(ref disk, ref work, disk.BatchBuffer));
        Assert.Equal(dataWrites, disk.FlushWriteCounts![0]);
        var marker = U32(image, 104) + 2;
        Assert.Equal(marker, disk.WrittenBlocks![dataWrites].Block);
        Assert.Equal(0x54525354u, U32(disk.WrittenBlocks[dataWrites].Data, 0));
        Assert.Equal(dataWrites + 1, disk.FlushWriteCounts[1]);
        Inspect(disk.Snapshot());
    }

    private static byte[] Payload(int literals, int pieces)
    {
        var data = new byte[512]; var word = 0;
        for (var piece = 0; piece < pieces; piece++)
        {
            var take = literals / pieces + (piece < literals % pieces ? 1 : 0);
            for (var i = 0; i < take; i++, word++) U32(data, word * 4, (uint)(word + 1));
            word += (128 - literals) / pieces + (piece < (128 - literals) % pieces ? 1 : 0);
        }
        Assert.Equal(128, word); return data;
    }

    // Exact framing independent of filesystem allocation decisions. Public API
    // group tests exercise real metadata, accepted rollback and all barriers.
    private static bool Commit(ref StudyDisk disk, uint[] targets, byte[][] images)
    {
        var arena = disk.BatchBuffer;
        Assert.True(Sfs2MetadataStore.Initialize(ref disk, arena, 16384, 512, 1));
        var work = WorkFor(disk); var initial = disk.Snapshot();
        work.RootContainer = U32(initial, 104); work.BitmapBase = U32(initial, 96);
        for (var i = 0; i < targets.Length; i++)
        {
            images[i].CopyTo(disk.Ram, (int)work.Data.Raw);
            Assert.True(Sfs2MetadataStore.Store(ref disk, arena, targets[i], work.Data));
        }
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var core = typeof(AmigaSmartFileSystem2WriteCore);
        var type = core.GetNestedType("RedoPlatform`1", BindingFlags.NonPublic)!.MakeGenericType(typeof(StudyDisk));
        var staged = Activator.CreateInstance(type)!;
        type.GetField("Inner", fields)!.SetValue(staged, disk);
        type.GetField("OwnedArena", fields)!.SetValue(staged, arena);
        type.GetField("Count", fields)!.SetValue(staged, (uint)targets.Length);
        object[] args = [disk, work, staged];
        try { return (bool)core.GetMethod("CommitTrst", BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(StudyDisk)).Invoke(null, args)!; }
        catch (TargetInvocationException e) when (e.InnerException is StudyPowerCut) { throw e.InnerException; }
        finally { disk = (StudyDisk)args[0]; }
    }
}
