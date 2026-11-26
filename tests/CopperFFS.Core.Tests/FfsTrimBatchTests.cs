using Amiga;
using CopperSFS;
using CopperStart.Dos;

namespace CopperStart.FileSystem.Tests;

public sealed partial class VolumeGeometryTests
{
    private sealed partial class Storage
    {
        public uint Descriptors = 8;
        public int FailCommandAt;
        public readonly List<(uint Block, uint Count)[]> Commands = [];
    }

    private partial struct Memory : IAmigaBatchDiscardFileSystemPlatform
    {
        public uint DiscardDescriptors(APTR control) => storage.Descriptors;

        // One command per call. Each descriptor goes through DiscardBlocks, which
        // asserts every block is free on disk. Validate the whole vector first,
        // as the SCSI codec does, so a bad descriptor submits nothing.
        public bool DiscardRanges(APTR control, APTR ranges, uint count)
        {
            Assert.InRange(count, 1u, storage.Descriptors);
            var vector = new (uint Block, uint Count)[count];
            ulong total = 0;
            for (var i = 0; i < count; i++)
            {
                vector[i] = (ReadUInt32(ranges, i * 8), ReadUInt32(ranges, i * 8 + 4));
                Assert.True(vector[i].Count != 0 && vector[i].Count <= storage.Limits.MaximumBlocks);
                total += vector[i].Count;
            }
            Assert.True(total <= storage.Limits.MaximumBlocks, $"vector of {total} blocks");
            storage.Commands.Add(vector);
            if (storage.Commands.Count == storage.FailCommandAt) return false;
            foreach (var (block, blocks) in vector) Assert.True(DiscardBlocks(control, block, blocks));
            return true;
        }
    }

    // Fragment free space into many small runs: a file every other block group.
    // Returns the headers of the kept (odd) files.
    private static uint[] Fragment(ref Memory p, Storage storage, AmigaVolumeGeometry g, int files)
    {
        var w = Work(); var headers = new uint[files * 2];
        for (var i = 0; i < files * 2; i++)
        {
            System.Text.Encoding.ASCII.GetBytes($"F{i:D4}").CopyTo(storage.Ram, (int)Name.Raw);
            var r = new AmigaFfsWriteCore.Request { Directory = g.RootBlock, Name = Name, Length = 5 };
            Assert.True(AmigaFfsWriteCore.Create(ref p, ref w, ref r, false), w.Error.ToString());
            storage.Ram.AsSpan((int)Payload.Raw, 1500).Fill((byte)i);
            r.Buffer = Payload; r.Length = 1500;
            Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref r, out _), w.Error.ToString());
            headers[i] = r.Header;
        }
        for (var i = 0; i < files * 2; i += 2)
            Assert.True(AmigaFfsWriteCore.Remove(ref p, ref w, headers[i], false), w.Error.ToString());
        return headers.Where((_, i) => i % 2 == 1).ToArray();
    }

    private static APTR Ranges(Storage storage) => APTR.FromPointer((uint)storage.Ram.Length - 1024);

    // Manual TRIM over fragmented free space used to send one UNMAP per free
    // run. With an eight-descriptor device it now packs eight runs per
    // command, with exactly the descriptors the per-run path and a dry run
    // report, and every surviving file still reads back.
    [Theory]
    [InlineData(false, 8u)] [InlineData(true, 8u)] [InlineData(false, 3u)] [InlineData(true, 1u)]
    public void BatchedManualTrimPacksFreeRunsIntoFewerCommands(bool ofs, uint descriptors)
    {
        AmigaSmartFileSystemWriteCore.TrimResult Run(bool batched, uint flags, out Storage storage)
        {
            // A 4096-block budget, as the SCSI codec caps it, so the large free
            // tail adds only a few single-descriptor commands.
            storage = new Storage { Descriptors = descriptors, Limits = new() { MaximumBlocks = 4096, Granularity = 1 } };
            Assert.True(AmigaVolumeGeometry.Create(9003, 512, 2, out var g));
            var p = new Memory(storage, g); Format(ref p, g); if (ofs) SetDosType(ref p, 0x444F5300);
            var kept = Fragment(ref p, storage, g, 40);
            var w = Work(); storage.Discards.Clear(); storage.Discarded = 0;
            Assert.True(batched
                ? AmigaFfsWriteCore.TrimWindowBatched(ref p, ref w, APTR.Null, Ranges(storage), flags, out var result, 0, uint.MaxValue)
                : AmigaFfsWriteCore.TrimWindow(ref p, ref w, APTR.Null, flags, out result, 0, uint.MaxValue), w.Error.ToString());
            for (var k = 0; k < kept.Length; k++)
            {
                Assert.Equal(1500, AmigaDiskFileSystemCore.Read(ref p, kept[k], ofs ? 0x444F5300u : 0x444F5301u, 0, Output, 1500, Meta, Data, out _));
                var fill = (byte)(k * 2 + 1);
                Assert.All(storage.Ram.AsSpan((int)Output.Raw, 1500).ToArray(), b => Assert.Equal(fill, b));
            }
            return result;
        }
        var direct = Run(false, 0, out var single);
        var dry = Run(true, AmigaSmartFileSystemTrimPacket.DryRun, out var dryStorage);
        var batch = Run(true, 0, out var packed);
        Assert.True(direct.Ranges > 40, $"{direct.Ranges} runs");
        Assert.Empty(single.Commands); Assert.Empty(dryStorage.Discards);
        Assert.Equal(direct.Blocks, batch.Blocks); Assert.Equal(direct.Ranges, batch.Ranges);
        Assert.Equal(dry.Blocks, batch.Blocks); Assert.Equal(dry.Ranges, batch.Ranges);
        Assert.Equal(single.Discards, packed.Discards);
        if (descriptors < 2) { Assert.Empty(packed.Commands); return; }
        Assert.Equal(packed.Discards, packed.Commands.SelectMany(c => c).ToList());
        Assert.All(packed.Commands, c => Assert.InRange(c.Length, 1, (int)descriptors));
        Assert.True(packed.Commands.Count <= (int)(direct.Ranges + descriptors - 1) / (int)descriptors + 3,
            $"{packed.Commands.Count} commands for {direct.Ranges} runs");
    }

    // A large free run splits into MaximumBlocks descriptors; no command may
    // exceed that block budget even when slots remain.
    [Fact]
    public void BatchedManualTrimKeepsEachCommandWithinTheBlockBudget()
    {
        var storage = new Storage { Limits = new() { MaximumBlocks = 100, Granularity = 4, Alignment = 1 } };
        Assert.True(AmigaVolumeGeometry.Create(9003, 512, 2, out var g));
        var p = new Memory(storage, g); Format(ref p, g); Fragment(ref p, storage, g, 10);
        var w = Work(); storage.Discards.Clear();
        Assert.True(AmigaFfsWriteCore.TrimWindowBatched(ref p, ref w, APTR.Null, Ranges(storage), 0, out var result, 0, uint.MaxValue), w.Error.ToString());
        Assert.NotEmpty(storage.Commands);
        foreach (var command in storage.Commands)
        {
            Assert.True(command.Sum(r => (long)r.Count) <= 100);
            Assert.All(command, r => { Assert.Equal(1u, r.Block % 4); Assert.Equal(0u, r.Count % 4); });
        }
        Assert.Equal((uint)storage.Commands.Sum(c => c.Length), result.Ranges);
        Assert.Equal((uint)storage.Discards.Sum(r => (long)r.Count), result.Blocks);
    }

    // A failed command can have deallocated any part of its vector, so none of
    // it is counted; earlier commands are. A retry scans fresh allocation.
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void BatchedManualTrimFailureExcludesTheWholeFailedVector(int fail)
    {
        var storage = new Storage { FailCommandAt = fail };
        Assert.True(AmigaVolumeGeometry.Create(9003, 512, 2, out var g));
        var p = new Memory(storage, g); Format(ref p, g); Fragment(ref p, storage, g, 40);
        var w = Work(); storage.Discards.Clear();
        Assert.False(AmigaFfsWriteCore.TrimWindowBatched(ref p, ref w, APTR.Null, Ranges(storage), 0, out var result, 0, uint.MaxValue));
        Assert.Equal(DOS.Error.SeekError, w.Error);
        Assert.Equal(fail, storage.Commands.Count);
        var completed = storage.Commands.Take(fail - 1).SelectMany(c => c).ToList();
        Assert.Equal((uint)completed.Count, result.Ranges);
        Assert.Equal((uint)completed.Sum(r => (long)r.Count), result.Blocks);
        storage.FailCommandAt = 0;
        Assert.True(AmigaFfsWriteCore.TrimWindowBatched(ref p, ref w, APTR.Null, Ranges(storage), 0, out _, 0, uint.MaxValue), w.Error.ToString());
    }
}
