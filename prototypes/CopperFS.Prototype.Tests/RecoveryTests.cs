using CopperFS.Prototype;
using static CopperFS.Prototype.Layout;
using static CopperFS.Prototype.Tests.DesignTests;

namespace CopperFS.Prototype.Tests;

public class RecoveryTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static byte[] Baseline(int allocation)
    {
        var d = Fresh(allocation); var v = new PrototypeVolume<MemoryDevice>(d, Workspace(allocation));
        v.Put("old"u8, "payload"u8); v.Create("append"u8, out _);
        // Reuse both slots before injection, leaving authentic stale commit records.
        v.Create("temporary"u8, out _); v.Delete("temporary"u8);
        return d.Bytes;
    }
    private static void Mutate(CrashDevice d, int allocation, string operation)
    {
        var v = new PrototypeVolume<CrashDevice>(d, Workspace(allocation));
        MutationResult result;
        if (operation == "create") result = v.Create("new"u8, out _);
        else if (operation == "rename") result = v.Rename("old"u8, "renamed"u8);
        else if (operation == "delete") result = v.Delete("old"u8);
        else { v.TryGetInfo("append"u8, out var info); result = v.Append(info.Id, 0, Pattern(7101), out _); }
        Assert.Equal(MutationResult.Success, result);
    }
    private static void Verify(byte[] crash, int allocation, string operation)
    {
        var d = new MemoryDevice(crash); var v = new PrototypeVolume<MemoryDevice>(d, Workspace(allocation));
        v.Check(); var oracle = new IndependentImage(d.Bytes);
        bool old = oracle.Names.ContainsKey((1, "old")), renamed = oracle.Names.ContainsKey((1, "renamed"));
        if (operation == "rename") Assert.NotEqual(old, renamed);
        if (old) Assert.Equal("payload"u8.ToArray(), oracle.Read(oracle.Names[(1, "old")]));
        if (renamed) Assert.Equal("payload"u8.ToArray(), oracle.Read(oracle.Names[(1, "renamed")]));
        var data = oracle.Read(oracle.Names[(1, "append")]);
        Assert.True(data.Length == 0 || data.AsSpan().SequenceEqual(Pattern(7101)));
        // Recovery is idempotent and both checkpoint copies are retired before slot reuse.
        byte[] stable = (byte[])d.Bytes.Clone(); var second = new PrototypeVolume<MemoryDevice>(d, Workspace(allocation));
        Assert.Equal(stable, d.Bytes); Assert.Equal(v.Generation, second.Generation);
    }
    [Theory]
    [InlineData(512,"create")] [InlineData(512,"rename")] [InlineData(512,"delete")] [InlineData(512,"append")]
    [InlineData(4096,"create")] [InlineData(4096,"append")]
    public void EverySectorAndBarrierWithFivePersistenceSchedules(int allocation, string operation)
    {
        byte[] baseline = Baseline(allocation); var complete = new CrashDevice(baseline); Mutate(complete, allocation, operation);
        output.WriteLine($"{complete.Events.Count} sector/barrier cuts x 5 persistence schedules = {complete.Events.Count * 5} crash recoveries.");
        for (int cut = 1; cut <= complete.Events.Count; cut++)
            for (int persistence = 0; persistence < 5; persistence++)
            {
                var fault = new CrashDevice(baseline) { Cut = cut, Persistence = persistence };
                Assert.Throws<PowerLoss>(() => Mutate(fault, allocation, operation));
                Verify(fault.Durable, allocation, operation);
            }
    }
    private static byte[] CommittedNotRetired()
    {
        byte[] baseline = Baseline(512); var run = new CrashDevice(baseline); Mutate(run, 512, "rename");
        // Third barrier completes the commit publication: invalidate, stage, commit.
        int barriers = 0, cut = 0;
        for (int i = 0; i < run.Events.Count; i++) if (run.Events[i] == "after flush" && ++barriers == 3) { cut = i + 1; break; }
        var fault = new CrashDevice(baseline) { Cut = cut }; Assert.Throws<PowerLoss>(() => Mutate(fault, 512, "rename")); return fault.Durable;
    }
    [Fact] public void ReplayCanCrashAtEveryWriteAndBarrier()
    {
        byte[] committed = CommittedNotRetired(); var run = new CrashDevice(committed);
        var recovered = new PrototypeVolume<CrashDevice>(run, Workspace()); Assert.True(recovered.TryGetInfo("renamed"u8, out _));
        output.WriteLine($"{run.Events.Count} replay cuts x 5 persistence schedules = {run.Events.Count * 5} interrupted recoveries.");
        for (int cut = 1; cut <= run.Events.Count; cut++) for (int mode = 0; mode < 5; mode++)
        {
            var fault = new CrashDevice(committed) { Cut = cut, Persistence = mode };
            try { var unused = new PrototypeVolume<CrashDevice>(fault, Workspace()); Assert.Fail("Expected injected crash."); } catch (PowerLoss) { }
            Verify(fault.Durable, 512, "rename");
            var final = new PrototypeVolume<MemoryDevice>(new MemoryDevice(fault.Durable), Workspace()); Assert.True(final.TryGetInfo("renamed"u8, out _));
        }
    }
    [Theory] [InlineData("image")] [InlineData("descriptor")] [InlineData("header")]
    public void BadCommittedLogIsRejectedBeforeAnyHomeWrite(string corrupt)
    {
        byte[] bytes = CommittedNotRetired(); var d = new MemoryDevice(bytes);
        ulong tx = U64(bytes, 65536 + 1024 + 80) + 1; var l = new Layout(512, 512, (ulong)bytes.Length / 512, 12345);
        ulong at = corrupt == "image" ? l.Image(tx, 1) : corrupt == "descriptor" ? l.Descriptor(tx, 0) : l.Journal(tx);
        d.Bytes[checked((int)(at * 512)) + 100] ^= 0x80;
        try { var unused = new PrototypeVolume<MemoryDevice>(d, Workspace()); Assert.Fail("Accepted corrupt committed log."); } catch (InvalidDataException) { }
        Assert.Equal(0, d.Writes); Assert.Equal(0, d.Flushes);
    }
    [Fact] public void ReadOnlyMountDoesNotReplayAndLostAnchorHasBackup()
    {
        var d = new MemoryDevice(CommittedNotRetired());
        try { var unused = new PrototypeVolume<MemoryDevice>(d, Workspace(), true); Assert.Fail("Replayed read-only."); } catch (InvalidOperationException) { }
        Assert.Equal(0, d.Writes);
        d.Bytes.AsSpan(65536, 1024).Clear(); var recovered = new PrototypeVolume<MemoryDevice>(d, Workspace()); Assert.True(recovered.TryGetInfo("renamed"u8, out _));
    }
    [Theory] [InlineData("target")] [InlineData("duplicate")] [InlineData("owner")]
    public void RechecksummedInvalidDescriptorsStillFailBeforeHomeWrites(string damage)
    {
        var d = new MemoryDevice(CommittedNotRetired()); var l = new Layout(512, 512, (ulong)d.Bytes.Length / 512, 12345);
        ulong tx = U64(d.Bytes, 65536 + 1024 + 80) + 1;
        Span<byte> descriptor = d.Bytes.AsSpan((int)(l.Descriptor(tx, 0) * 512), 1024);
        if (damage == "target") Put64(descriptor, 64, l.Anchor);
        else if (damage == "owner") Put64(descriptor, 72, 999);
        else { descriptor.Slice(64, 32).CopyTo(descriptor.Slice(96, 32)); Put32(descriptor, 96 + 16, 1); }
        uint digest = Crc(descriptor.Slice(64, U16(descriptor, 48) * 32)); Seal(descriptor);
        Span<byte> header = d.Bytes.AsSpan((int)(l.Journal(tx) * 512), 1024), commit = d.Bytes.AsSpan((int)(l.Commit(tx) * 512), 1024);
        Put32(header, 100, digest); Put32(commit, 100, digest); Seal(header); Seal(commit);
        try { var unused = new PrototypeVolume<MemoryDevice>(d, Workspace()); Assert.Fail("Accepted invalid descriptor."); } catch (InvalidDataException) { }
        Assert.Equal(0, d.Writes);
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void RepairOneCheckpointAndRefuseLosingBoth(int damaged)
    {
        var d = new MemoryDevice(Baseline(512));
        if (damaged != 1) d.Bytes.AsSpan(65536 + 1024, 1024).Clear();
        if (damaged != 0) d.Bytes.AsSpan(65536 + 2048, 1024).Clear();
        if (damaged == 2)
        { try { var unused = new PrototypeVolume<MemoryDevice>(d, Workspace()); Assert.Fail("Accepted lost checkpoints."); } catch (InvalidDataException) { } Assert.Equal(0, d.Writes); }
        else
        {
            var v = new PrototypeVolume<MemoryDevice>(d, Workspace()); Assert.True(v.TryGetInfo("old"u8, out _));
            Assert.Equal(U64(d.Bytes, 65536 + 1024 + 80), U64(d.Bytes, 65536 + 2048 + 80)); new IndependentImage(d.Bytes);
        }
    }
    [Fact] public void IoFailurePoisonsMountUntilRemounted()
    {
        var d = new CrashDevice(Baseline(512)) { Cut = 1 }; var v = new PrototypeVolume<CrashDevice>(d, Workspace());
        try { v.Create("crash"u8, out _); Assert.Fail("No crash."); } catch (PowerLoss) { }
        try { v.TryGetInfo("old"u8, out _); Assert.Fail("Poisoned mount remained usable."); } catch (InvalidOperationException) { }
    }
    [Fact] public void Exactly42ImagesRecoverAnd43rdImageNeverPublishes()
    {
        var d = Fresh(); var v = new PrototypeVolume<MemoryDevice>(d, Workspace());
        for (int i = 0; i < 90; i++) Assert.Equal(MutationResult.Success, v.Create(System.Text.Encoding.ASCII.GetBytes($"n{i}"), out _));
        var oracle = new IndependentImage(d.Bytes); ulong[] targets = oracle.Metadata.Take(43).ToArray(); Assert.Equal(43, targets.Length);
        byte[] baseline = (byte[])d.Bytes.Clone(); v.RestageForQualification(targets.AsSpan(0, 42)); Assert.Equal(42, v.LastTransactionPages);
        ulong generation = v.Generation;
        try { v.RestageForQualification(targets); Assert.Fail("Accepted 43 images."); } catch (InvalidOperationException) { }
        Assert.Equal(generation, v.Generation); new IndependentImage(d.Bytes);
        var trace = new CrashDevice(baseline); Restage(trace, targets.AsSpan(0, 42).ToArray());
        output.WriteLine($"{trace.Events.Count} 42-image cuts x 5 persistence schedules = {trace.Events.Count * 5} crash recoveries.");
        for (int cut = 1; cut <= trace.Events.Count; cut++) for (int mode = 0; mode < 5; mode++)
        {
            var fault = new CrashDevice(baseline) { Cut = cut, Persistence = mode };
            Assert.Throws<PowerLoss>(() => Restage(fault, targets.AsSpan(0, 42).ToArray()));
            var finalDevice = new MemoryDevice(fault.Durable); var final = new PrototypeVolume<MemoryDevice>(finalDevice, Workspace());
            Assert.True(final.Generation == generation - 1 || final.Generation == generation); new IndependentImage(finalDevice.Bytes);
        }
        static void Restage(CrashDevice device, ulong[] pages)
        { var volume = new PrototypeVolume<CrashDevice>(device, Workspace()); volume.RestageForQualification(pages); }
    }
}
