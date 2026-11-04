using CopperSFS.Bench;
using System.Text.Json;
using Amiga;
using Xunit.Abstractions;
using static CopperSFS.Tests.ReliabilityFixture;

namespace CopperSFS.Tests;

public sealed class NodeFullnessTests(ITestOutputHelper output)
{
    private static AmigaSmartFileSystemWriteCore.Work Work() => new()
    {
        Meta = APTR.FromPointer(512), Data = APTR.FromPointer(1024),
        Bitmap = APTR.FromPointer(1536), Journal = APTR.FromPointer(2048)
    };

    private static uint Create(ref ReliabilityDisk disk, string name, uint parent = 1, bool directory = false)
    {
        var work = Work();
        var request = new AmigaSmartFileSystemWriteCore.Request
            { Directory = parent, Name = disk.Name(name), Length = (uint)name.Length };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref request, directory),
            $"Create {name}: {work.Error}");
        return request.Node;
    }

    [Fact]
    public void FillingNodeLeafMustSetItsParentFullBit()
    {
        var disk = new ReliabilityDisk(Image());
        for (var i = 0; i < 94; i++) Create(ref disk, $"F{i:D3}");
        var image = disk.Snapshot(); Assert.Equal(98, Inspect(image).Entries.Count);
        var root = Block(image, 6); Assert.Equal(49u, U32(root, 16));
        var pointer = U32(root, 24); var leaf = Block(image, (int)(pointer >> 4));
        for (var offset = 20; offset + 10 <= 512; offset += 10) Assert.NotEqual(0u, U32(leaf, offset));
        output.WriteLine($"Full second leaf at block {pointer >> 4}, parent pointer=0x{pointer:X8}");
        Assert.Equal(1u, pointer & 1);
        U32(root, 24, pointer & ~1u); Seal(root);
        Assert.Throws<InvalidDataException>(() => Inspect(image));
    }

    [Fact]
    public void FreeingNodeLeafSlotMustClearItsParentFullBit()
    {
        var disk = new ReliabilityDisk(Image());
        for (var i = 0; i < 46; i++) Create(ref disk, $"F{i:D3}");
        var work = Work(); Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, 5), work.Error.ToString());
        var image = disk.Snapshot(); Inspect(image);
        var pointer = U32(Block(image, 6), 20); var leaf = Block(image, (int)(pointer >> 4));
        Assert.Equal(0u, U32(leaf, 20 + 4 * 10));
        output.WriteLine($"First leaf has a free slot, parent pointer=0x{pointer:X8}");
        Assert.Equal(0u, pointer & 1);
        U32(Block(image, 6), 20, pointer | 1); Seal(Block(image, 6));
        Assert.Throws<InvalidDataException>(() => Inspect(image));
    }


    // Research regressions above originate at 4a902a1; exercise current CSUN/TRST
    // transaction paths here, including persistence of partial parent updates.
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void FullnessChangesRecoverAtomicallyAtEveryWriteAndFlush(bool remove, bool redo)
    {
        var seed = new StudyDisk(Image(), 512);
        for (var i = 0; i < (remove ? 94 : 93); i++) FsOps.Create(ref seed, $"F{i:D3}");
        var image = seed.Snapshot(); var before = Inspect(image).Entries.Count;
        bool Mutate(ref StudyDisk disk)
        {
            var work = disk.Work;
            System.Text.Encoding.ASCII.GetBytes("Last").CopyTo(disk.Ram, (int)disk.NameBuffer.Raw);
            var request = new AmigaSmartFileSystemWriteCore.Request
                { Directory = 1, Node = 5, Name = disk.NameBuffer, Length = 4 };
            if (redo) return AmigaSmartFileSystemWriteCore.ExecuteTrst(ref disk, ref work, ref request,
                remove ? AmigaSmartFileSystemWriteCore.TrstOperation.Remove : AmigaSmartFileSystemWriteCore.TrstOperation.CreateFile,
                disk.BatchBuffer, 65536, out _);
            return remove ? AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, 5)
                : AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref request, false);
        }
        var baseline = new StudyDisk(image, 512, crashModel: true);
        Assert.True(Mutate(ref baseline)); var after = Inspect(baseline.Snapshot()).Entries.Count;
        Assert.Equal(before + (remove ? -1 : 1), after);
        for (var cut = 1; cut <= baseline.Events; cut++)
        {
            foreach (var fail in new[] { false, true })
            {
                var disk = new StudyDisk(image, 512, crashModel: true)
                    { CutAfterEvent = fail ? 0 : cut, FailAtEvent = fail ? cut : 0 };
                if (fail) Assert.False(Mutate(ref disk));
                else Assert.Throws<StudyPowerCut>(() => Mutate(ref disk));
                foreach (var saved in disk.CrashImages())
                {
                    var recovered = new StudyDisk(saved, 512); FsOps.Recover(ref recovered);
                    var stable = recovered.Snapshot();
                    Assert.Contains(Inspect(stable).Entries.Count, new[] { before, after });
                    FsOps.Recover(ref recovered); Assert.Equal(stable, recovered.Snapshot());
                }
            }
        }
    }
}
