using CopperSFS.Bench;
using Xunit.Abstractions;

namespace CopperSFS.Tests;

public sealed class BlockSizeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(4096)]
    public void MutationsAndRecoveryWorkAcrossBlockSizes(int bytes)
    {
        var results = RecoveryStudy.Run(bytes, output.WriteLine);
        Assert.Equal(RecoveryStudy.Scenarios.Length, results.Count);
        Assert.All(results, row =>
        {
            Assert.True(row.MutationImages >= row.MutationCutPoints);
            Assert.True(row.RecoveryImages >= row.RecoveryCutPoints);
            Assert.True(row.MutationCutPoints > 0 && row.RecoveryCutPoints > 0);
        });
    }

    [Theory]
    [InlineData(512, 512)]
    [InlineData(1024, 1024)]
    [InlineData(2048, 2048)]
    [InlineData(4096, 4096)]
    [InlineData(512, 32768)]
    [InlineData(1024, 32768)]
    [InlineData(2048, 32768)]
    [InlineData(4096, 32768)]
    public void AdapterCachesPreserveReadsAcrossWritesAndFlushes(int bytes, int transferBytes)
    {
        var image = StudyFixture.Empty(bytes, 1024 * 1024);
        var disk = new StudyDisk(image, bytes, cacheBytes: 65536, readAheadBytes: transferBytes);
        var node = FsOps.Create(ref disk, "Check");
        var data = StudyFixture.Pattern(65539);
        FsOps.Write(ref disk, node, 0, data);
        FsOps.Read(ref disk, node, 0, data.Length);
        Assert.True(disk.Ram.AsSpan((int)disk.Payload.Raw, data.Length).SequenceEqual(data));
        var patch = StudyFixture.Pattern(6001, 17);
        FsOps.Write(ref disk, node, 30001, patch);
        patch.CopyTo(data, 30001);
        FsOps.Read(ref disk, node, 0, data.Length);
        Assert.True(disk.Ram.AsSpan((int)disk.Payload.Raw, data.Length).SequenceEqual(data));
        var report = StudyFixture.Inspect(disk.Snapshot(), bytes);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)),
            report.Entries.Single(e => e.Path == "/Check").Sha256);
    }
}
