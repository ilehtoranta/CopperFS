using Amiga;
using CopperSFS;
using CopperStart.Dos;

namespace CopperStart.FileSystem.Tests;

public sealed partial class VolumeGeometryTests
{
    [Theory]
    [InlineData(512u)] [InlineData(1024u)]
    public void ExistingFfsRunsUseBulkWritesAndRetainFailureInvalidation(uint bytes)
    {
        var (s, p, g, file) = AllocationFixture(false, bytes, blocks: 20000);
        s.BulkTransfers = true;
        var originalData = new byte[(g.TableEntries + 4) * bytes];
        OfsWrite(ref p, s, ref file, 0, originalData);
        var saved = s.Sectors.ToDictionary(x => x.Key, x => x.Value.ToArray());
        var payload = Enumerable.Range(0, originalData.Length - 13).Select(i => (byte)(i * 13 + 5)).ToArray();
        payload.CopyTo(s.Ram, (int)Payload.Raw);
        file.Position = 0; file.Buffer = Payload; file.Length = (uint)payload.Length;
        var w = Work(); s.WriteCalls = 0; s.Transfers.Clear();
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref w, ref file, out var actual, true), w.Error.ToString());
        Assert.Equal(file.Length, actual);
        Assert.Contains(s.Transfers, t => t.Command is 3 or 25 && t.Bytes >= bytes * 2);
        payload.CopyTo(originalData, 0); InspectAllocation(s, g, file.Header, false, originalData);
        for (var fail = 1; fail <= s.WriteCalls; fail++)
        {
            var broken = new Storage { BulkTransfers = true, FailWriteCall = fail };
            foreach (var pair in saved) broken.Sectors[pair.Key] = pair.Value.ToArray();
            payload.CopyTo(broken.Ram, (int)Payload.Raw);
            var disk = new Memory(broken, g); var request = file; w = Work();
            Assert.False(AmigaFfsWriteCore.Write(ref disk, ref w, ref request, out _, true));
            Assert.True(AmigaDiskFileSystemCore.Header(ref disk, g.RootBlock, Meta));
            Assert.Equal(fail == 1 ? uint.MaxValue : 0u, disk.ReadUInt32(Meta, g.Tail + 312));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OverwritingWithoutGrowthDoesNotRewriteTheFileHeader(bool ofs)
    {
        var (s, p, g, file) = AllocationFixture(ofs);
        var data = Enumerable.Range(0, 1300).Select(i => (byte)i).ToArray();
        OfsWrite(ref p, s, ref file, 0, data);
        s.Transfers.Clear();
        var patch = new byte[] { 21, 43, 65 };
        OfsWrite(ref p, s, ref file, 17, patch);
        Assert.DoesNotContain(s.Transfers, t => t.Command is 3 or 25 && t.Offset == file.Header * (ulong)g.BlockBytes);
        patch.CopyTo(data, 17); InspectAllocation(s, g, file.Header, ofs, data);
    }

    [Theory]
    [InlineData(false, 512u, 4000u)]
    [InlineData(false, 1024u, 8100u)]
    [InlineData(false, 4096u, 100u)]
    [InlineData(true, 512u, 4000u)]
    [InlineData(true, 1024u, 8100u)]
    [InlineData(true, 4096u, 100u)]
    public void BatchedGrowthPreservesTablesAndInvalidatesOnEveryFailedWrite(bool ofs, uint bytes, uint first)
    {
        var (s, p, g, file) = AllocationFixture(ofs, bytes, blocks: 20000, first: first);
        s.BulkTransfers = true;
        var dataBytes = bytes - (ofs ? 24u : 0u);
        var prefix = Enumerable.Range(0, (int)dataBytes - 5).Select(i => (byte)(i * 17 + 3)).ToArray();
        OfsWrite(ref p, s, ref file, 0, prefix);
        var original = s.Sectors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        var blocks = bytes == 4096 ? 34u : g.TableEntries + 2;
        var payload = Enumerable.Range(0, (int)(blocks * dataBytes + 17)).Select(i => (byte)(i * 13 + 7)).ToArray();
        var expected = prefix.Concat(payload).ToArray();
        payload.CopyTo(s.Ram, (int)Payload.Raw);
        file.Position = (uint)prefix.Length; file.Buffer = Payload; file.Length = (uint)payload.Length;
        s.WriteCalls = 0; s.Transfers.Clear();
        var work = Work();
        Assert.True(AmigaFfsWriteCore.Write(ref p, ref work, ref file, out var actual, true), work.Error.ToString());
        Assert.Equal(file.Length, actual);
        var writes = s.WriteCalls;
        if (!ofs) Assert.Contains(s.Transfers, t => t.Command is 3 or 25 && t.Bytes >= bytes * 2);
        var keys = InspectAllocation(s, g, file.Header, ofs, expected);
        if (ofs)
        {
            // One write per new data block, except the end of a run which must
            // be linked when the next bitmap/table range becomes available.
            var dataWrites = s.Transfers.Count(t => t.Command is 3 or 25 && keys.Contains((uint)(t.Offset / bytes)));
            Assert.InRange(dataWrites, keys.Length, keys.Length + 8);
        }
        for (var fail = 1; fail <= writes; fail++)
        {
            var broken = new Storage { BulkTransfers = true, FailWriteCall = fail };
            foreach (var pair in original) broken.Sectors[pair.Key] = pair.Value.ToArray();
            payload.CopyTo(broken.Ram, (int)Payload.Raw);
            var disk = new Memory(broken, g); work = Work(); var request = file;
            Assert.False(AmigaFfsWriteCore.Write(ref disk, ref work, ref request, out _, true));
            Assert.Equal(DOS.Error.DiskNotValidated, work.Error);
            Assert.True(AmigaDiskFileSystemCore.Header(ref disk, g.RootBlock, Meta));
            Assert.Equal(fail == 1 ? uint.MaxValue : 0u, disk.ReadUInt32(Meta, g.Tail + 312));
        }
    }
}
