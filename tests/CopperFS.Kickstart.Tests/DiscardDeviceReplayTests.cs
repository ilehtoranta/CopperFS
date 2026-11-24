using System.Text.Json;
using CopperSFS.Check;

namespace CopperFS.Kickstart.Tests;

public sealed class DiscardDeviceReplayTheoryAttribute : TheoryAttribute
{
    public DiscardDeviceReplayTheoryAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("COPPERSHARP_KICKSTART31_ROM")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COPPERFS_DISCARD_DEVICE_HANDLER_ROOT")))
            Skip = "Supply Kickstart and COPPERFS_DISCARD_DEVICE_HANDLER_ROOT with freshly built ffs-auto/sfs-auto/sfs2-auto handlers.";
    }
}

public sealed partial class OriginalHandlerCorrectnessTests
{
    [DiscardDeviceReplayTheory]
    [InlineData("ffs")] [InlineData("sfs")] [InlineData("sfs2")]
    public async Task NativeDiscardAdmissionStillTrimsSupportedHardfiles(string profile)
    {
        var handlers = Environment.GetEnvironmentVariable("COPPERFS_DISCARD_DEVICE_HANDLER_ROOT")!;
        var handler = Path.Combine(handlers, profile + "-auto", profile == "ffs" ? "CopperFFS" : "CopperSFS");
        // A requested replay with a missing payload is a failure, not a skip.
        Assert.True(File.Exists(handler), $"Missing rebuilt handler: {handler}");
        var root = Path.Combine(handlers, "replay", profile);
        var image = profile == "ffs" ? ReadMedia.Ffs() : profile == "sfs" ? ReadMedia.Sfs() : ReadMedia.Sfs2();
        var trace = new DiscardTrace();
        var entry = "TrimQualificationProbe::" + (profile == "ffs" ? "Ffs" : profile == "sfs" ? "Sfs" : "Sfs2") + "Execute";
        var result = await Run(root, profile, "execute", handler, image, true, entry, storage: trace);
        using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "execute", "result.json")));
        Assert.Equal(uint.MaxValue, receipt.RootElement.GetProperty("Stage").GetUInt32());
        Assert.Equal(0u, receipt.RootElement.GetProperty("IoErr").GetUInt32());
        Assert.True(trace.DiscardCount > 0, "Expected actual UNMAP on the opted-in hardfile provider.");
        // The guest probe streams and checks the complete allocated payload
        // after discard. These clean ReadMedia fixtures need no metadata writes.
        // They deliberately contain extra reserved/unowned ranges, so the
        // shared-mutation fixture's strict ownership checker does not apply.
        Assert.Equal(0, trace.Writes);
        Assert.Equal(image.Length, result.Length);
        var discarded = new bool[image.Length / 512];
        foreach (var operation in trace.Operations.Where(op => op.StartsWith("D:")))
        {
            var fields = operation.Split(':');
            var start = int.Parse(fields[1]); var bytes = int.Parse(fields[2]);
            Assert.True(start >= 0 && bytes > 0 && start <= image.Length - bytes);
            Assert.Equal(0, start % 512); Assert.Equal(0, bytes % 512);
            Array.Fill(discarded, true, start / 512, bytes / 512);
        }
        for (var block = 0; block < discarded.Length; block++)
            if (!discarded[block])
                Assert.True(image.AsSpan(block * 512, 512).SequenceEqual(result.AsSpan(block * 512, 512)),
                    $"Non-discarded block {block} changed.");
        if (profile == "sfs2") SfsImageChecker.Check(new MemoryStream(result), reservedBlocks: 1);
    }
}
